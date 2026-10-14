// Block editing from the headset, engine-free part (tested in Tests~): the selected block, the block_edit messages
// (PROTOCOL.md 4.20), placement orientation, and optimistic local updates that reconcile with the server.
//
// Optimistic updates: an edit is applied to WorldModel right away (so the section re-meshes without waiting a round
// trip) only when the resulting state is already in the session palette. The palette mirrors the server's session
// palette index for index (block_update.palette_add appends), so the client never adds entries itself; an edit whose
// result is a new state just waits for the server's block_update. Each optimistic cell remembers the value it had
// before (the server's last word on it). Then:
//   - a block_update that touches the cell drops the pending entry (the server's value wins);
//   - an error for the edit (it carries "pos") reverts the cell to its old value;
//   - after ExpirySeconds without either, the cell reverts too. The server diffs the region at least once a second
//     and sends every change, so silence means the world still holds the old value there (e.g. sand that fell
//     straight away, or a send that was dropped while reconnecting);
//   - a new chunk_snapshot replaces everything.
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
            int open = state.IndexOf('[');
            if (open < 0) return -1;
            for (int j = state.IndexOf(key + "=", open, StringComparison.Ordinal); j > 0; j = state.IndexOf(key + "=", j + 1, StringComparison.Ordinal))
                if (state[j - 1] == '[' || state[j - 1] == ',') return j;
            return -1;
        }

        static readonly string[] Horizontal = { "west", "east", "north", "south" };

        /// <summary>
        /// Simple placement orientation (the picked state is otherwise copied as is): blocks with an "axis" (logs,
        /// pillars, chains) line up with the clicked face; blocks with a horizontal "facing" turn toward the user, except
        /// stairs, doors, fence gates, beds and campfires, which face the way the user looks (vanilla's
        /// getStateForPlacement for both groups). Vertical facings (hoppers, end rods pointing up) are kept.
        /// </summary>
        public static string Orient(string state, int face, Vector3 rayDir)
        {
            string axis = Prop(state, "axis");
            if (axis != null && BlockRules.Path(state) != "nether_portal") state = WithProp(state, "axis", face < 2 ? "x" : face < 4 ? "y" : "z");
            string facing = Prop(state, "facing");
            if (facing != null && Array.IndexOf(Horizontal, facing) >= 0 && (Math.Abs(rayDir.x) > 1e-6f || Math.Abs(rayDir.z) > 1e-6f))
            {
                // Look direction on the ground plane (block space: +X east, +Z south).
                string look = Math.Abs(rayDir.x) >= Math.Abs(rayDir.z) ? (rayDir.x > 0 ? "east" : "west") : (rayDir.z > 0 ? "south" : "north");
                state = WithProp(state, "facing", FacesLook(state) ? look : Opposite(look));
            }
            return state;
        }

        static bool FacesLook(string state)
        {
            string p = BlockRules.Path(state);
            return p.EndsWith("_stairs") || p.EndsWith("_door") || p.EndsWith("_fence_gate") || p.EndsWith("_bed") || p.EndsWith("campfire");
        }

        static string Opposite(string d) => d == "east" ? "west" : d == "west" ? "east" : d == "north" ? "south" : "north";

        /// <summary>Breaking a block that holds water (waterlogged, kelp, seagrass) leaves a water source, as in vanilla.</summary>
        public static string BreakResult(string old) =>
            FluidRules.Kind(old) == FluidKind.Water && BlockRules.Path(old) != "water" ? WaterSource : Air;

        /// <summary>Placing a waterloggable block into a water source waterlogs it, as in vanilla (the server does the same).</summary>
        public static string PlaceResult(string state, string old) =>
            BlockRules.Path(old) == "water" && FluidRules.IntProp(old, "level") == 0 && Prop(state, "waterlogged") == "false"
                ? WithProp(state, "waterlogged", "true") : state;

        /// <summary>Short block name for labels: "minecraft:oak_stairs[...]" -> "oak stairs".</summary>
        public static string Label(string state) => BlockRules.Path(state).Replace('_', ' ');
    }

    /// <summary>Optimistic edits waiting for the server (see the file header).</summary>
    public sealed class PendingEdits
    {
        public const double ExpirySeconds = 3.0;
        struct Entry { public ushort Before; public double Expires; }
        readonly Dictionary<int, Entry> _cells = new Dictionary<int, Entry>();
        readonly List<int> _tmp = new List<int>();
        int _snapshot = -1;

        public int Count => _cells.Count;
        /// <summary>Bumped whenever a cell is changed here (guess or revert): the app re-slices what depends on the grid.</summary>
        public int Changes { get; private set; }
        public bool IsPending(WorldModel w, Vector3Int c) => w.InBounds(c.x, c.y, c.z) && _cells.ContainsKey(w.Index(c.x, c.y, c.z));

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

        /// <summary>Call for every block_update before WorldModel.ApplyBlockUpdate: the server's values replace the guesses.</summary>
        public void OnBlockUpdate(WorldModel w, Dictionary<string, object> m)
        {
            if (_cells.Count == 0 || m.I("snapshot_id", -2) != w.SnapshotId) return;
            foreach (var o in m.A("changes") ?? new List<object>())
            {
                var a = (List<object>)o;
                int x = (int)(double)a[0], y = (int)(double)a[1], z = (int)(double)a[2];
                if (w.InBounds(x, y, z)) _cells.Remove(w.Index(x, y, z));
            }
        }

        /// <summary>The server refused the edit at this cell: put the old value back. False if nothing was pending there.</summary>
        public bool Revert(WorldModel w, Vector3Int c)
        {
            Sync(w);
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
            _tmp.Clear(); _tmp.AddRange(_cells.Keys);
            foreach (int i in _tmp) Revert(w, Cell(w, i));
            return _tmp.Count;
        }

        /// <summary>Reverts cells the server stayed silent about for ExpirySeconds. Returns how many.</summary>
        public int Expire(WorldModel w, double now)
        {
            if (_cells.Count == 0) return 0;
            Sync(w);
            _tmp.Clear();
            foreach (var kv in _cells) if (now >= kv.Value.Expires) _tmp.Add(kv.Key);
            foreach (int i in _tmp) Revert(w, Cell(w, i));
            return _tmp.Count;
        }

        public void Clear() => _cells.Clear();

        /// <summary>A new snapshot replaced the grid: old guesses no longer apply.</summary>
        void Sync(WorldModel w) { if (_snapshot != w.SnapshotId) { _cells.Clear(); _snapshot = w.SnapshotId; } }

        static Vector3Int Cell(WorldModel w, int i) => new Vector3Int(i % w.Dims.x, i / (w.Dims.x * w.Dims.z), (i / w.Dims.x) % w.Dims.z);
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
        /// <summary>[menu] The server's block_registry check (PROTOCOL.md 4.24): only these states can be placed. Null = no
        /// registry yet (an older server), everything goes.</summary>
        public Func<string, bool> Placeable;

        /// <summary>block_edit op "break" for the hit block, or null (Result says why).</summary>
        public Dictionary<string, object> Break(WorldModel w, BlockHit h, double now)
        {
            if (!Ready(w, h)) return null;
            string old = w.State(w.Get(h.Block.x, h.Block.y, h.Block.z));
            Optimistic(w, h.Block, BlockEditRules.BreakResult(old), now);
            Sent++;
            Result = "broke " + BlockEditRules.Label(old);
            return Protocol.BlockEdit("break", w.Origin + h.Block, null, BlockRaycast.FaceNames[h.Face]);
        }

        /// <summary>block_edit op "place": the selected block (oriented) into the cell in front of the hit face, or null.</summary>
        public Dictionary<string, object> Place(WorldModel w, BlockHit h, Vector3 rayDir, double now)
        {
            if (!Ready(w, h)) return null;
            var c = h.Adjacent;
            if (!h.AdjacentInView) { Result = "can't place outside the area"; return null; }
            if (!BlockRaycast.CanPlaceInto(w, c)) { Result = "can't place: cell is occupied"; return null; }
            if (Placeable != null && !Placeable(Selected)) { Result = "can't place " + BlockEditRules.Label(Selected); return null; }   // [menu]
            string state = BlockEditRules.Orient(Selected, h.Face, rayDir);
            Optimistic(w, c, BlockEditRules.PlaceResult(state, w.State(w.Get(c.x, c.y, c.z))), now);
            Sent++;
            Result = "placed " + BlockEditRules.Label(state);
            return Protocol.BlockEdit("place", w.Origin + c, state, BlockRaycast.FaceNames[h.Face]);
        }

        /// <summary>Pick block (middle click): the hit block's state becomes the selection.</summary>
        public bool Pick(WorldModel w, BlockHit h)
        {
            if (w == null || !w.Loaded || !h.Hit) return false;
            string picked = w.State(w.Get(h.Block.x, h.Block.y, h.Block.z));
            if (Placeable != null && !Placeable(picked)) { Result = "can't place " + BlockEditRules.Label(picked); return false; }   // [menu]
            Selected = picked;
            Result = "picked " + BlockEditRules.Label(Selected);
            return true;
        }

        bool Ready(WorldModel w, BlockHit h)
        {
            if (!ServerAllows) { Result = "editing is off on the server (edit.enabled)"; return false; }
            if (w == null || !w.Loaded || !h.Hit) return false;
            return true;
        }

        void Optimistic(WorldModel w, Vector3Int c, string result, double now)
        {
            int id = w.Palette.IndexOf(result);
            if (id >= 0) Pending.Apply(w, c, (ushort)id, now);   // else: a new state, wait for block_update.palette_add
        }

        public void OnSnapshot() => Pending.Clear();
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
            if (!(m.A("pos") is List<object> p) || p.Count != 3) return;
            var c = new Vector3Int((int)(double)p[0] - w.Origin.x, (int)(double)p[1] - w.Origin.y, (int)(double)p[2] - w.Origin.z);
            Pending.Revert(w, c);
            Result = code + ": " + m.S("message");
        }
    }
}
