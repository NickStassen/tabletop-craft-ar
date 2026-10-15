// Block editing from the headset, engine-free part (tested in Tests~): the selected block, the block_edit messages
// (PROTOCOL.md 4.20), where a placed block goes and its state (BlockPlacement.cs), and optimistic local updates that
// reconcile with the server.
//
// Optimistic updates: an edit is applied to WorldModel right away (so the section re-meshes without waiting a round
// trip) only when the resulting state is already in the session palette. The palette mirrors the server's session
// palette index for index (block_update.palette_add appends), so the client never adds entries itself; an edit whose
// result is a new state just waits for the server's block_update (and its cell counts as taken for a second, so a held
// trigger doesn't send it twice). Each optimistic cell remembers the value it had before (the server's last word on
// it). Then:
//   - a block_update that touches the cell drops the pending entry (the server's value wins);
//   - an error for the edit (it carries "pos") reverts the cell to its old value;
//   - after ExpirySeconds without either, the cell reverts too. The server diffs the region at least once a second
//     and sends every change, so silence means the world still holds the old value there (e.g. sand that fell
//     straight away, or a send that was dropped while reconnecting);
//   - a full chunk_snapshot replaces everything; a sectioned delta (gliding, resizing) keeps the guesses in the
//     sections it copied from the old grid, moved with the origin, and drops those in the sections it sent.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class BlockEditRules
    {
        public const string DefaultBlock = "minecraft:stone";
        /// <summary>What breaking a block that holds water leaves (Paper's BlockData#getAsString for a water source).</summary>
        public const string WaterSource = "minecraft:water[level=0]";
        public const string Air = "minecraft:air";

        /// <summary>Value of a block-state property ("east" for "facing" in "minecraft:furnace[facing=east,lit=false]"), or null.</summary>
        public static string Prop(string state, string key)
        {
            int i = Find(state, key);
            if (i < 0) return null;
            int s = i + key.Length + 1, e = s;
            while (e < state.Length && state[e] != ',' && state[e] != ']') e++;
            return state.Substring(s, e - s);
        }

        /// <summary>The state with one existing property changed (unchanged when it has no such property).</summary>
        public static string WithProp(string state, string key, string value)
        {
            int i = Find(state, key);
            if (i < 0) return state;
            int s = i + key.Length + 1, e = s;
            while (e < state.Length && state[e] != ',' && state[e] != ']') e++;
            return state.Substring(0, s) + value + state.Substring(e);
        }

        static int Find(string state, string key)
        {
            if (state == null) return -1;
            int open = state.IndexOf('[');
            if (open < 0) return -1;
            for (int j = state.IndexOf(key + "=", open, StringComparison.Ordinal); j > 0; j = state.IndexOf(key + "=", j + 1, StringComparison.Ordinal))
                if (state[j - 1] == '[' || state[j - 1] == ',') return j;
            return -1;
        }

        /// <summary>Breaking a block that holds water (waterlogged, kelp, seagrass) leaves a water source, as in vanilla.</summary>
        public static string BreakResult(string old) =>
            FluidRules.Kind(old) == FluidKind.Water && BlockRules.Path(old) != "water" ? WaterSource : Air;

        /// <summary>The cell holds a water source (a still water block, or a block that holds water): vanilla's
        /// getFluidState(pos).is(Fluids.WATER), which decides "waterlogged" for a block placed there.</summary>
        public static bool HoldsWaterSource(string old) =>
            old != null && FluidRules.Kind(old) == FluidKind.Water && (BlockRules.Path(old) != "water" || FluidRules.IntProp(old, "level") == 0);

        /// <summary>A waterloggable block is waterlogged exactly when it goes into a water source, as in vanilla (the
        /// server does the same); a waterlogged state picked from the seabed doesn't carry its water onto land.</summary>
        public static string PlaceResult(string state, string old) =>
            Prop(state, "waterlogged") == null ? state : WithProp(state, "waterlogged", HoldsWaterSource(old) ? "true" : "false");

        /// <summary>Short block name for labels: "minecraft:oak_stairs[...]" -> "oak stairs".</summary>
        public static string Label(string state) => BlockRules.Path(state ?? Air).Replace('_', ' ');
    }

    /// <summary>Optimistic edits waiting for the server (see the file header).</summary>
    public sealed class PendingEdits
    {
        public const double ExpirySeconds = 3.0;
        /// <summary>A sent edit counts as unanswered this long (or until a block_update or error for its cell).</summary>
        public const double UnansweredSeconds = 1.0;
        struct Entry { public ushort Before; public double Expires; }
        readonly Dictionary<int, Entry> _cells = new Dictionary<int, Entry>();
        readonly Dictionary<Vector3Int, double> _sent = new Dictionary<Vector3Int, double>();   // world pos -> until
        readonly List<int> _tmp = new List<int>();
        readonly List<Vector3Int> _tmpPos = new List<Vector3Int>();
        // The grid the indices refer to (snapshot id, origin, dims), so a delta can move them.
        int _snapshot = -1;
        Vector3Int _origin, _dims;

        public int Count => _cells.Count;
        /// <summary>Bumped whenever a cell is changed here (guess or revert): the app re-slices what depends on the grid.</summary>
        public int Changes { get; private set; }
        public bool IsPending(WorldModel w, Vector3Int c) => w.SnapshotId == _snapshot && w.InBounds(c.x, c.y, c.z) && _cells.ContainsKey(w.Index(c.x, c.y, c.z));

        /// <summary>Sets the cell to palette index <paramref name="id"/> now and remembers its old value.</summary>
        public void Apply(WorldModel w, Vector3Int c, ushort id, double now)
        {
            Sync(w);
            int i = w.Index(c.x, c.y, c.z);
            // A second edit on a pending cell keeps the first old value: that is still what the server last said.
            ushort before = _cells.TryGetValue(i, out var e) ? e.Before : w.Grid[i];
            _cells[i] = new Entry { Before = before, Expires = now + ExpirySeconds };
            w.SetLocal(c.x, c.y, c.z, id);
            Changes++;
        }

        /// <summary>An edit of this cell (world position) went out; until the server answers (or UnansweredSeconds pass)
        /// the cell counts as taken.</summary>
        public void MarkSent(Vector3Int world, double now) => _sent[world] = now + UnansweredSeconds;
        public bool Unanswered(Vector3Int world, double now) => _sent.TryGetValue(world, out var until) && now < until;
        public int UnansweredCount => _sent.Count;

        /// <summary>Call for every block_update before WorldModel.ApplyBlockUpdate: the server's values replace the guesses.</summary>
        public void OnBlockUpdate(WorldModel w, Dictionary<string, object> m)
        {
            if ((_cells.Count == 0 && _sent.Count == 0) || m.I("snapshot_id", -2) != w.SnapshotId) return;
            foreach (var o in m.A("changes") ?? new List<object>())
            {
                var a = (List<object>)o;
                int x = (int)(double)a[0], y = (int)(double)a[1], z = (int)(double)a[2];
                _sent.Remove(w.Origin + new Vector3Int(x, y, z));
                if (w.InBounds(x, y, z) && w.SnapshotId == _snapshot) _cells.Remove(w.Index(x, y, z));
            }
        }

        /// <summary>The server refused the edit at this cell: put the old value back. False if nothing was pending there.</summary>
        public bool Revert(WorldModel w, Vector3Int c)
        {
            Sync(w);
            _sent.Remove(w.Origin + c);
            if (!w.InBounds(c.x, c.y, c.z)) return false;
            int i = w.Index(c.x, c.y, c.z);
            if (!_cells.TryGetValue(i, out var e)) return false;
            _cells.Remove(i);
            w.SetLocal(c.x, c.y, c.z, e.Before);
            Changes++;
            return true;
        }

        /// <summary>Reverts every pending cell (the server doesn't know block_edit). Returns how many.</summary>
        public int RevertAll(WorldModel w)
        {
            Sync(w);
            _sent.Clear();
            _tmp.Clear(); _tmp.AddRange(_cells.Keys);
            foreach (int i in _tmp) Revert(w, Cell(_dims, i));
            return _tmp.Count;
        }

        /// <summary>Reverts cells the server stayed silent about for ExpirySeconds, and forgets old sends. Returns how many reverted.</summary>
        public int Expire(WorldModel w, double now)
        {
            if (_sent.Count > 0)
            {
                _tmpPos.Clear();
                foreach (var kv in _sent) if (now >= kv.Value) _tmpPos.Add(kv.Key);
                foreach (var p in _tmpPos) _sent.Remove(p);
            }
            if (_cells.Count == 0) return 0;
            Sync(w);
            _tmp.Clear();
            foreach (var kv in _cells) if (now >= kv.Value.Expires) _tmp.Add(kv.Key);
            foreach (int i in _tmp) Revert(w, Cell(_dims, i));
            return _tmp.Count;
        }

        public void Clear() { _cells.Clear(); _sent.Clear(); }

        /// <summary>
        /// A new snapshot was applied to <paramref name="w"/>. Full snapshot (<paramref name="listed"/> null): the old
        /// guesses no longer apply. Sectioned delta: the sections it didn't list were copied from the old grid, guesses
        /// included, so their entries move with the origin and stay pending (a later error still reverts them); entries
        /// in listed sections (the server's own data) or outside the new region are dropped.
        /// </summary>
        public void OnSnapshot(WorldModel w, ICollection<Vector3Int> listed)
        {
            if (listed == null || _snapshot < 0 || _cells.Count == 0)
            {
                _cells.Clear();
                Rebase(w);
                return;
            }
            var moved = new List<KeyValuePair<int, Entry>>(_cells.Count);
            int S = WorldModel.Section;
            foreach (var kv in _cells)
            {
                var c = _origin + Cell(_dims, kv.Key) - w.Origin;
                if (!w.InBounds(c.x, c.y, c.z) || listed.Contains(new Vector3Int(c.x / S, c.y / S, c.z / S))) continue;
                moved.Add(new KeyValuePair<int, Entry>(w.Index(c.x, c.y, c.z), kv.Value));
            }
            _cells.Clear();
            foreach (var kv in moved) _cells[kv.Key] = kv.Value;
            Rebase(w);
        }

        void Rebase(WorldModel w) { _snapshot = w.SnapshotId; _origin = w.Origin; _dims = w.Dims; }

        /// <summary>A snapshot replaced the grid without OnSnapshot: old guesses no longer apply.</summary>
        void Sync(WorldModel w) { if (_snapshot != w.SnapshotId || _dims != w.Dims) { _cells.Clear(); Rebase(w); } }

        static Vector3Int Cell(Vector3Int dims, int i) => new Vector3Int(i % dims.x, i / (dims.x * dims.z), (i / dims.x) % dims.z);
    }

    /// <summary>What the left trigger would do now: the cell, the oriented state, the second half, or why not.</summary>
    public struct PlacePlan
    {
        public bool Ok;
        /// <summary>Refused only because an edit of that cell is still on its way (no warning for that).</summary>
        public bool Waiting;
        public Vector3Int Cell;            // region-local cell the block goes into
        public string State;               // what block_edit sends and the cell shows (oriented, waterlogged from the cell)
        public bool HasPartner;
        public Vector3Int PartnerCell;     // the other half of a door, bed or tall plant
        public string PartnerState;
        public string Why;                 // refusal, for the status line and toast
    }

    /// <summary>The selected block, block_edit messages and their optimistic local effect. XR/BlockEditMode.cs drives it.</summary>
    public sealed class BlockEditor
    {
        public string Selected = BlockEditRules.DefaultBlock;
        public readonly PendingEdits Pending = new PendingEdits();
        /// <summary>welcome.caps holds edit_v1 (the server allows this headset to edit).</summary>
        public bool ServerAllows;
        /// <summary>Last thing that happened, for the status text.</summary>
        public string Result = "";
        public int Sent;
        /// <summary>[menu] The server's block_registry (PROTOCOL.md 4.24): only its blocks and variants can be placed, a
        /// pick takes the block's default state, wall forms come from its variants. Null = no registry yet.</summary>
        public BlockRegistry Registry;
        /// <summary>[menu] Placement check; defaults to the registry's. Null with no registry: everything goes.</summary>
        public Func<string, bool> Placeable;

        bool CanPlace(string state) => Placeable != null ? Placeable(state) : Registry == null || Registry.IsPlaceable(state);
        bool Known(string id) => Registry == null || Registry.Find(id) != null;

        /// <summary>block_edit op "break" for the hit block, or null (Result says why).</summary>
        public Dictionary<string, object> Break(WorldModel w, BlockHit h, double now)
        {
            if (!Ready(w, h)) return null;
            var world = w.Origin + h.Block;
            if (Pending.Unanswered(world, now)) { Result = "waiting for the server"; return null; }
            string old = w.State(w.Get(h.Block.x, h.Block.y, h.Block.z));
            bool shown = Optimistic(w, h.Block, BlockEditRules.BreakResult(old), now);
            // A door, bed or tall plant goes as a whole (the server removes the other half too).
            if (BlockPlacement.Partner(old, out var off, out var other))
            {
                var pc = h.Block + off;
                string there = w.InBounds(pc.x, pc.y, pc.z) ? w.State(w.Get(pc.x, pc.y, pc.z)) : null;
                if (there != null && BlockRules.Name(there) == BlockRules.Name(other)) Optimistic(w, pc, BlockEditRules.BreakResult(there), now);
            }
            if (!shown) Pending.MarkSent(world, now);   // not shown yet: don't send it again while the trigger is held
            Sent++;
            Result = "broke " + BlockEditRules.Label(old);
            return Protocol.BlockEdit("break", world, null, BlockRaycast.FaceNames[h.Face]);
        }

        /// <summary>
        /// Where and what the left trigger would place now. Vanilla rules (BlockPlacement): a replaceable hit block
        /// (short grass, a snow layer, vines) is replaced, else the block goes in front of the hit face; the state
        /// follows the face, hit point and look; doors, beds and tall plants need room for their second half.
        /// </summary>
        public PlacePlan Plan(WorldModel w, BlockHit h, Vector3 rayDir, double now)
        {
            var plan = new PlacePlan();
            if (w == null || !w.Loaded || !h.Hit) { plan.Why = ""; return plan; }
            string sel = Selected;
            if (InteractProtocol.IsItem(sel)) { plan.Why = ""; return plan; }   // [hands] an item is used, not placed
            if (!CanPlace(sel)) { plan.Why = "can't place " + BlockEditRules.Label(sel); return plan; }   // [menu]
            sel = Base(sel);
            string hitState = w.State(w.Get(h.Block.x, h.Block.y, h.Block.z));
            bool replace = BlockPlacement.Replaceable(hitState, sel);
            var c = replace ? h.Block : h.Adjacent;
            plan.Cell = c;
            if (!replace && !h.AdjacentInView) { plan.Why = "can't place outside the area"; return plan; }
            string old = w.State(w.Get(c.x, c.y, c.z));
            if (!BlockRaycast.CanPlaceInto(w, c, sel)) { plan.Why = "can't place: " + BlockEditRules.Label(old) + " is in the way"; return plan; }
            if (Pending.Unanswered(w.Origin + c, now)) { plan.Why = "waiting for the server"; plan.Waiting = true; return plan; }
            var ctx = new PlaceContext { World = w, Pos = c, Clicked = h.Block, Face = h.Face, ReplaceClicked = replace, Hit = h.Point, Look = Look.From(rayDir) };
            string state = BlockPlacement.StateFor(sel, ctx, Known, out string why);
            if (state == null) { plan.Why = "can't place: " + why; return plan; }
            plan.State = BlockEditRules.PlaceResult(state, old);
            if (BlockPlacement.Partner(state, out var off, out var other))
            {
                var pc = c + off;
                string there = w.InBounds(pc.x, pc.y, pc.z) ? w.State(w.Get(pc.x, pc.y, pc.z)) : null;
                if (there == null || !BlockPlacement.Replaceable(there, sel)) { plan.Why = "can't place: no room for the " + BlockEditRules.Label(state); return plan; }
                plan.HasPartner = true; plan.PartnerCell = pc; plan.PartnerState = BlockEditRules.PlaceResult(other, there);
            }
            plan.Ok = true;
            return plan;
        }

        /// <summary>The target readout by the controller: "oak stairs @ 105,64,-35" (world coordinates), or "" on a miss.</summary>
        public static string Describe(WorldModel w, BlockHit h)
        {
            if (w == null || !w.Loaded || !h.Hit || !w.InBounds(h.Block.x, h.Block.y, h.Block.z)) return "";
            var p = w.Origin + h.Block;
            return BlockEditRules.Label(w.State(w.Get(h.Block.x, h.Block.y, h.Block.z))) + " @ " + p.x + "," + p.y + "," + p.z;
        }

        /// <summary>A tiny world holding what the plan places (and its second half) for the preview ghost; its cell
        /// (0,0,0) is region-local <paramref name="corner"/>. Null when the plan places nothing.</summary>
        public static WorldModel PreviewWorld(PlacePlan p, out Vector3Int corner)
        {
            corner = p.Cell;
            if (p.State == null) return null;
            var lo = p.Cell; var hi = p.Cell;
            if (p.HasPartner)
            {
                lo = new Vector3Int(Math.Min(lo.x, p.PartnerCell.x), Math.Min(lo.y, p.PartnerCell.y), Math.Min(lo.z, p.PartnerCell.z));
                hi = new Vector3Int(Math.Max(hi.x, p.PartnerCell.x), Math.Max(hi.y, p.PartnerCell.y), Math.Max(hi.z, p.PartnerCell.z));
            }
            corner = lo;
            var w = new WorldModel { SnapshotId = 0, World = "preview", Dims = hi - lo + new Vector3Int(1, 1, 1) };
            w.Palette.Add(BlockEditRules.Air); w.Palette.Add(p.State);
            w.Grid = new ushort[w.Dims.x * w.Dims.y * w.Dims.z];
            var c = p.Cell - lo;
            w.Grid[w.Index(c.x, c.y, c.z)] = 1;
            if (p.HasPartner)
            {
                w.Palette.Add(p.PartnerState);
                var pc = p.PartnerCell - lo;
                w.Grid[w.Index(pc.x, pc.y, pc.z)] = 2;
            }
            return w;
        }

        /// <summary>block_edit op "place" from Plan, applied optimistically; null when refused (Result says why).</summary>
        public Dictionary<string, object> Place(WorldModel w, BlockHit h, Vector3 rayDir, double now)
        {
            if (!Ready(w, h)) return null;
            var p = Plan(w, h, rayDir, now);
            if (!p.Ok) { Result = p.Why; return null; }
            bool shown = Optimistic(w, p.Cell, p.State, now);
            if (p.HasPartner) Optimistic(w, p.PartnerCell, p.PartnerState, now);
            var world = w.Origin + p.Cell;
            if (!shown) Pending.MarkSent(world, now);   // a block new to the palette: the cell looks empty until the server answers
            Sent++;
            Result = "placed " + BlockEditRules.Label(p.State);
            return Protocol.BlockEdit("place", world, p.State, BlockRaycast.FaceNames[h.Face]);
        }

        /// <summary>
        /// [hands] interact (PROTOCOL.md "Using blocks and items") for the hit block: the empty hand (item null) or an item
        /// used on the hit face, with the hit point inside the block and the ray direction. Nothing changes locally: the
        /// result comes back as block_update (or entities, actions), a refusal as an error with op "interact".
        /// </summary>
        public Dictionary<string, object> Interact(WorldModel w, BlockHit h, string item, bool sneak, Vector3 rayDir)
        {
            if (w == null || !w.Loaded || !h.Hit) return null;
            string label = BlockEditRules.Label(w.State(w.Get(h.Block.x, h.Block.y, h.Block.z)));
            Sent++;
            Result = item == null ? "used " + label : "used " + BlockRules.Path(item).Replace('_', ' ') + " on " + label;
            return InteractProtocol.Interact(w.Origin + h.Block, BlockRaycast.FaceNames[h.Face], h.Point - (Vector3)h.Block, item, sneak, rayDir);
        }

        /// <summary>Pick block (middle click): the hit block becomes the selection, as its item would place it: the
        /// registry's default state (a wall torch picks the torch, a door's top half the door, crops at age 0).</summary>
        public bool Pick(WorldModel w, BlockHit h)
        {
            if (w == null || !w.Loaded || !h.Hit) return false;
            string picked = w.State(w.Get(h.Block.x, h.Block.y, h.Block.z));
            if (!CanPlace(picked)) { Result = "can't place " + BlockEditRules.Label(picked); return false; }   // [menu]
            Selected = Base(picked);
            Result = "picked " + BlockEditRules.Label(Selected);
            return true;
        }

        /// <summary>
        /// The state a block's item places before orientation: the registry's default state of the block, or of the
        /// block a variant belongs to (wall torch -> torch, water cauldron -> cauldron). Without a registry, only the
        /// properties that make a different block part are reset (lower half, foot, age 0, unpowered, dry).
        /// </summary>
        public string Base(string state)
        {
            if (string.IsNullOrEmpty(state)) return state;
            var e = Registry?.Find(state);
            if (e != null) return e.State;
            string s = state;
            if (BlockEditRules.Prop(s, "half") == "upper") s = BlockEditRules.WithProp(s, "half", "lower");
            if (BlockEditRules.Prop(s, "part") == "head") s = BlockEditRules.WithProp(s, "part", "foot");
            if (BlockEditRules.Prop(s, "age") != null) s = BlockEditRules.WithProp(s, "age", "0");
            if (BlockEditRules.Prop(s, "powered") == "true") s = BlockEditRules.WithProp(s, "powered", "false");
            if (BlockEditRules.Prop(s, "waterlogged") == "true") s = BlockEditRules.WithProp(s, "waterlogged", "false");
            return s;
        }

        bool Ready(WorldModel w, BlockHit h)
        {
            if (!ServerAllows) { Result = "editing is off on the server (edit.enabled)"; return false; }
            if (w == null || !w.Loaded || !h.Hit) return false;
            return true;
        }

        /// <summary>Shows the result at once when it's in the palette (else it waits for block_update.palette_add).</summary>
        bool Optimistic(WorldModel w, Vector3Int c, string result, double now)
        {
            if (!w.InBounds(c.x, c.y, c.z)) return false;
            int id = w.Palette.IndexOf(result);
            if (id < 0) return false;
            Pending.Apply(w, c, (ushort)id, now);
            return true;
        }

        /// <summary>A full snapshot replaced the grid: forget the guesses.</summary>
        public void OnSnapshot() => Pending.Clear();
        /// <summary>A snapshot was applied; <paramref name="listed"/> = a delta's sent section keys, null for a full one.</summary>
        public void OnSnapshot(WorldModel w, ICollection<Vector3Int> listed) => Pending.OnSnapshot(w, listed);
        public void OnBlockUpdate(WorldModel w, Dictionary<string, object> m) => Pending.OnBlockUpdate(w, m);
        public void Tick(WorldModel w, double now) { if (w != null && w.Loaded) Pending.Expire(w, now); }

        /// <summary>Server errors: one for a block_edit carries "pos" (world) and undoes that cell's optimistic change.</summary>
        public void OnError(WorldModel w, Dictionary<string, object> m)
        {
            if (w == null || !w.Loaded) return;
            string code = m.S("code");
            if (code == "unknown_type" && (m.S("message") ?? "").Contains("block_edit"))
            {
                ServerAllows = false;
                Pending.RevertAll(w);
                Result = "the server has no block editing (update the plugin)";
                return;
            }
            if (m.S("op") == "interact" || m.S("op") == "entity_op") { Result = m.S("message") ?? ""; return; }   // [hands] nothing to undo (the app toasts it)
            if (!(m.A("pos") is List<object> p) || p.Count != 3) return;
            var c = new Vector3Int((int)(double)p[0] - w.Origin.x, (int)(double)p[1] - w.Origin.y, (int)(double)p[2] - w.Origin.z);
            string guess = w.InBounds(c.x, c.y, c.z) ? w.State(w.Get(c.x, c.y, c.z)) : null;
            Pending.Revert(w, c);
            string was = w.InBounds(c.x, c.y, c.z) ? w.State(w.Get(c.x, c.y, c.z)) : null;
            // The other half of a refused door, bed or tall plant (guessed with it) goes back too.
            if (guess != null && BlockPlacement.Partner(guess, out var off, out _)) Pending.Revert(w, c + off);
            if (was != null && BlockPlacement.Partner(was, out off, out _)) Pending.Revert(w, c + off);
            Result = code + ": " + m.S("message");
        }
    }
}
