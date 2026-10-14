// Engine-free tests for block editing from the headset: the DDA ray traversal and face selection (BlockRaycast.cs), the
// Unity <-> block-space Z mirror, placement rules, optimistic edits and their reconciliation (BlockEdits.cs), the
// block_edit builder against the golden fixtures, and (--live-edit ws://...) real edits against the Paper plugin.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class EditTests
{
    static WorldModel World(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, World = "world", Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;
    static bool Eq(Vector3Int a, Vector3Int b) => a.x == b.x && a.y == b.y && a.z == b.z;
    static Vector3Int V(int x, int y, int z) => new Vector3Int(x, y, z);
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;
    /// <summary>Through JSON text, as the server sees it (built messages hold boxed ints, parsed ones doubles).</summary>
    static Dictionary<string, object> RT(Dictionary<string, object> m) => Parse(Json.Write(m));

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Raycast(check);
        Mirror(check);
        Rules(check);
        Editor(check, fixture);
    }

    static void Raycast(Action<bool, string> check)
    {
        // Stone floor at y=0, water in the two layers above it at x >= 6.
        var w = World(8, 6, 8, (x, y, z) => y == 0 ? 1 : y <= 2 && x >= 6 ? 2 : 0, "minecraft:air", "minecraft:stone", "minecraft:water[level=0]");
        var h = BlockRaycast.Cast(w, new Vector3(4.5f, 5.5f, 4.5f), new Vector3(0, -1, 0), 20);
        check(h.Hit && Eq(h.Block, V(4, 0, 4)) && h.Face == 3 && Eq(h.Adjacent, V(4, 1, 4)) && Near(h.Distance, 4.5f) && Near(h.Point.y, 1),
              $"ray straight down hits the floor's top face (got {h.Block} face {h.Face} d {h.Distance})");
        h = BlockRaycast.Cast(w, new Vector3(4.5f, 20f, 4.5f), new Vector3(0, -3, 0), 30);
        check(h.Hit && Eq(h.Block, V(4, 0, 4)) && h.Face == 3 && Near(h.Distance, 19), $"ray from above the region enters through its top (d {h.Distance})");
        check(!BlockRaycast.Cast(w, new Vector3(4.5f, 20f, 4.5f), new Vector3(0, -1, 0), 10).Hit, "ray shorter than the distance misses");
        h = BlockRaycast.Cast(w, new Vector3(6.5f, 5.5f, 3.5f), new Vector3(0, -1, 0), 20);
        check(h.Hit && Eq(h.Block, V(6, 0, 3)) && h.Face == 3 && BlockRaycast.CanPlaceInto(w, h.Adjacent), "water is skipped: aim at the seabed, place into the water");
        // A column at x=6 (stone in the water): a horizontal ray hits its west face.
        w.Grid[w.Index(6, 2, 4)] = 1;
        h = BlockRaycast.Cast(w, new Vector3(1.5f, 2.5f, 4.5f), new Vector3(1, 0, 0), 20);
        check(h.Hit && Eq(h.Block, V(6, 2, 4)) && h.Face == 0 && Eq(h.Adjacent, V(5, 2, 4)) && Near(h.Point.x, 6), $"horizontal ray hits the west face (got {h.Block} face {h.Face})");
        // Entering through the region's side straight into the floor: the cell in front is outside the region.
        h = BlockRaycast.Cast(w, new Vector3(-3f, 0.5f, 4.5f), new Vector3(1, 0, 0), 20);
        check(h.Hit && Eq(h.Block, V(0, 0, 4)) && h.Face == 0 && Eq(h.Adjacent, V(-1, 0, 4)) && !BlockRaycast.CanPlaceInto(w, h.Adjacent),
              "ray entering through the region's west side hits the edge block; no placing outside");
        check(!BlockRaycast.Cast(w, new Vector3(4.5f, 3.5f, 4.5f), new Vector3(0.1f, 1, 0), 50).Hit, "ray pointing up into air misses");
        check(!BlockRaycast.Cast(w, new Vector3(4.5f, 9f, 4.5f), new Vector3(1, 0, 0), 50).Hit, "ray parallel to the region above it misses");
        check(!BlockRaycast.Cast(w, new Vector3(4.5f, 3f, 4.5f), new Vector3(0, 0, 0), 50).Hit, "zero direction misses");
        // Starting inside a block (the controller poked into the miniature): that block, face back along the main axis.
        h = BlockRaycast.Cast(w, new Vector3(4.5f, 0.5f, 4.5f), new Vector3(0.2f, -1, 0), 10);
        check(h.Hit && Eq(h.Block, V(4, 0, 4)) && h.Face == 3 && h.Distance == 0, $"ray starting inside a block targets it (face {h.Face})");
        // Plants and models are targeted by their cell.
        var plant = World(3, 3, 3, (x, y, z) => y == 0 ? 1 : x == 1 && y == 1 && z == 1 ? 2 : 0, "minecraft:air", "minecraft:grass_block[snowy=false]", "minecraft:short_grass");
        h = BlockRaycast.Cast(plant, new Vector3(1.5f, 2.9f, 1.5f), new Vector3(0, -1, 0), 10);
        check(h.Hit && Eq(h.Block, V(1, 1, 1)), "short grass is targetable by its cell");

        // Property test against a fine ray march: the hit cell is solid, the hit point lies on the stated face, and no
        // solid cell comes earlier along the ray; a miss means the march finds nothing either.
        var rnd = new System.Random(1234);
        var rw = World(12, 10, 12, (x, y, z) => { int r = rnd.Next(100); return r < 12 ? 1 : r < 18 ? 2 : r < 22 ? 3 : 0; },
                       "minecraft:air", "minecraft:stone", "minecraft:water[level=0]", "minecraft:glass");
        int bad = 0, hits = 0, corner = 0;
        for (int i = 0; i < 3000; i++)
        {
            var o = new Vector3((float)(rnd.NextDouble() * 20 - 4), (float)(rnd.NextDouble() * 18 - 4), (float)(rnd.NextDouble() * 20 - 4));
            // Half aimed at a random point inside the region (more hits), half in a random direction.
            var d = (i & 1) == 0
                ? new Vector3((float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 2 - 1))
                : new Vector3((float)(rnd.NextDouble() * 12), (float)(rnd.NextDouble() * 10), (float)(rnd.NextDouble() * 12)) - o;
            float len = MathF.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            if (len < 0.1f) continue;
            d = d * (1 / len);
            var r = BlockRaycast.Cast(rw, o, d, 40);
            float tm = March(rw, o, d, 40);
            if (r.Hit)
            {
                hits++;
                bool solid = BlockRaycast.Targetable(rw.ShapeOf(rw.Grid[rw.Index(r.Block.x, r.Block.y, r.Block.z)]));
                int ax = r.Face / 2; float plane = r.Block[ax] + (r.Face & 1);
                bool onFace = Near(r.Point[ax], plane, 2e-3f) || r.Distance == 0;
                for (int a = 0; a < 3; a++) if (a != ax && (r.Point[a] < r.Block[a] - 2e-3f || r.Point[a] > r.Block[a] + 1 + 2e-3f)) onFace = false;
                bool notLater = tm < 0 ? true : tm >= r.Distance - 2e-3f;
                if (tm < 0) corner++;   // the march stepped over a clipped corner: fine, the DDA is exact
                if (!solid || !onFace || !notLater) bad++;
            }
            else if (tm >= 0) bad++;
        }
        check(bad == 0 && hits > 1000 && corner < hits / 50, $"DDA agrees with a fine ray march on 3000 random rays ({hits} hits, {bad} bad, {corner} corner-only)");
    }

    /// <summary>First targetable cell along the ray by sampling every 1/500 block; -1 if none.</summary>
    static float March(WorldModel w, Vector3 o, Vector3 d, float max)
    {
        for (float t = 0; t <= max; t += 0.002f)
        {
            var p = o + d * t;
            int x = (int)MathF.Floor(p.x), y = (int)MathF.Floor(p.y), z = (int)MathF.Floor(p.z);
            if (w.InBounds(x, y, z) && BlockRaycast.Targetable(w.ShapeOf(w.Grid[w.Index(x, y, z)]))) return t;
        }
        return -1;
    }

    static void Mirror(Action<bool, string> check)
    {
        // TabletopRenderer draws block-space (x,y,z) at Blocks-local (x, y, -z). One block at (2,1,5); a ray that starts
        // south of it in block space travels Unity +Z, which is block-space -Z (north), and hits its south face.
        var w = World(6, 4, 8, (x, y, z) => x == 2 && y == 1 && z == 5 ? 1 : 0, "minecraft:air", "minecraft:stone");
        var unityOrigin = new Vector3(2.5f, 1.5f, -9.5f);
        var unityDir = new Vector3(0, 0, 1);
        var h = BlockRaycast.Cast(w, BlockRaycast.FromBlocksLocal(unityOrigin), BlockRaycast.FromBlocksLocal(unityDir), 20);
        check(h.Hit && Eq(h.Block, V(2, 1, 5)) && h.Face == 5 && Eq(h.Adjacent, V(2, 1, 6)), $"Unity +Z ray = block-space north: hits the south face (got {h.Block} face {h.Face})");
        var back = BlockRaycast.ToBlocksLocal(h.Point);
        check(Near(back.z, -6f) && Near(back.x, 2.5f), $"hit point maps back to Blocks-local on the rendered face (z {back.z})");
        // The rendered block centre (vertex convention) maps into that cell.
        var c = BlockRaycast.FromBlocksLocal(new Vector3(2.5f, 1.5f, -5.5f));
        check((int)MathF.Floor(c.x) == 2 && (int)MathF.Floor(c.y) == 1 && (int)MathF.Floor(c.z) == 5, "rendered block centre (2.5, 1.5, -5.5) is cell (2,1,5)");
        // Same frame as the pose protocol: tabletop p = [ux, uy, -uz] metres, block = p / block_size.
        float bs = 0.01f;
        var u = new Vector3(0.123f, 0.045f, -0.067f);
        var p = TabletopPose.FromUnityLocal(u, new Quaternion(0, 0, 0, 1)).p;
        var b = BlockRaycast.FromBlocksLocal(u * (1 / bs));
        check(Near(p.x / bs, b.x, 1e-3f) && Near(p.y / bs, b.y, 1e-3f) && Near(p.z / bs, b.z, 1e-3f), "block space = pose tabletop frame / block size");
    }

    static void Rules(Action<bool, string> check)
    {
        const string furnace = "minecraft:furnace[facing=east,lit=false]";
        check(BlockEditRules.Prop(furnace, "facing") == "east" && BlockEditRules.Prop(furnace, "lit") == "false" && BlockEditRules.Prop("minecraft:stone", "facing") == null &&
              BlockEditRules.Prop("minecraft:x[surfacing=up,facing=down]", "facing") == "down", "state property lookup");
        check(BlockEditRules.WithProp(furnace, "facing", "north") == "minecraft:furnace[facing=north,lit=false]" &&
              BlockEditRules.WithProp(furnace, "lit", "true") == "minecraft:furnace[facing=east,lit=true]" &&
              BlockEditRules.WithProp("minecraft:stone", "facing", "up") == "minecraft:stone", "state property replace");
        var north = new Vector3(0, -0.3f, -1); var east = new Vector3(1, -0.5f, 0.2f);
        check(BlockEditRules.Orient("minecraft:oak_log[axis=y]", 1, north) == "minecraft:oak_log[axis=x]" &&
              BlockEditRules.Orient("minecraft:oak_log[axis=y]", 3, north) == "minecraft:oak_log[axis=y]" &&
              BlockEditRules.Orient("minecraft:oak_log[axis=x]", 4, north) == "minecraft:oak_log[axis=z]", "logs line up with the clicked face");
        check(BlockEditRules.Orient("minecraft:furnace[facing=north,lit=false]", 3, north) == "minecraft:furnace[facing=south,lit=false]" &&
              BlockEditRules.Orient("minecraft:furnace[facing=north,lit=false]", 3, east) == "minecraft:furnace[facing=west,lit=false]", "furnaces face the user");
        check(BlockEditRules.Orient("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]", 3, north) ==
              "minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]", "stairs face the way the user looks");
        check(BlockEditRules.Orient("minecraft:hopper[enabled=true,facing=down]", 1, east) == "minecraft:hopper[enabled=true,facing=down]" &&
              BlockEditRules.Orient("minecraft:stone", 2, east) == "minecraft:stone", "vertical facings and plain blocks are kept");
        check(BlockEditRules.BreakResult("minecraft:oak_slab[type=bottom,waterlogged=true]") == BlockEditRules.WaterSource &&
              BlockEditRules.BreakResult("minecraft:kelp[age=3]") == BlockEditRules.WaterSource &&
              BlockEditRules.BreakResult("minecraft:oak_slab[type=bottom,waterlogged=false]") == "minecraft:air" &&
              BlockEditRules.BreakResult("minecraft:grass_block[snowy=false]") == "minecraft:air", "breaking a block that holds water leaves water");
        check(BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:water[level=0]") == "minecraft:oak_slab[type=bottom,waterlogged=true]" &&
              BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:water[level=3]") == "minecraft:oak_slab[type=bottom,waterlogged=false]" &&
              BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:air") == "minecraft:oak_slab[type=bottom,waterlogged=false]" &&
              BlockEditRules.PlaceResult("minecraft:stone", "minecraft:water[level=0]") == "minecraft:stone", "placing into a water source waterlogs");
        check(BlockEditRules.Label("minecraft:oak_stairs[facing=east]") == "oak stairs", "block label");
    }

    static void Editor(Action<bool, string> check, Func<string, string> fixture)
    {
        // Region at origin (100,60,-40); stone floor, a waterlogged slab at (3,1,3).
        var w = World(8, 8, 8, (x, y, z) => y == 0 ? 1 : x == 3 && y == 1 && z == 3 ? 3 : 0,
                      "minecraft:air", "minecraft:stone", "minecraft:dirt", "minecraft:oak_slab[type=bottom,waterlogged=true]", BlockEditRules.WaterSource);
        w.Origin = V(100, 60, -40);
        var ed = new BlockEditor();
        var down = new Vector3(0, -1, 0);
        var hit = BlockRaycast.Cast(w, new Vector3(1.5f, 6f, 1.5f), down, 20);
        check(ed.Break(w, hit, 0) == null && ed.Result.Contains("off"), "no edits until the server grants edit_v1");
        ed.ServerAllows = true;
        w.DirtySections.Clear();
        var msg = ed.Break(w, hit, 10);
        var fxBreak = Parse(System.IO.File.ReadAllText(fixture("block_edit_break.json")));
        var pos = Json.Ints(RT(msg).A("pos"));
        check(msg.S("type") == "block_edit" && msg.S("op") == "break" && pos.SequenceEqual(new[] { 101, 60, -39 }) && msg.S("face") == "up" && !msg.ContainsKey("state") &&
              msg.Keys.OrderBy(k => k).SequenceEqual(fxBreak.Keys.Where(k => k != "v" && k != "seq" && k != "t").OrderBy(k => k)), "break message: world pos, face, same fields as the fixture");
        check(w.Get(1, 0, 1) == 0 && ed.Pending.Count == 1 && w.DirtySections.Count > 0, "break is applied optimistically and re-meshes");
        var err = Parse("{\"type\":\"error\",\"code\":\"edit_denied\",\"message\":\"no\",\"pos\":[101,60,-39]}");
        ed.OnError(w, err);
        check(w.Get(1, 0, 1) == 1 && ed.Pending.Count == 0, "server error with pos reverts the optimistic break");

        ed.Break(w, hit, 10);
        var upd = Parse("{\"snapshot_id\":1,\"palette_add\":[],\"changes\":[[1,0,1,0]]}");
        ed.OnBlockUpdate(w, upd); w.ApplyBlockUpdate(upd);
        ed.OnError(w, err);   // a late error after the server's own update must not undo it
        check(w.Get(1, 0, 1) == 0 && ed.Pending.Count == 0, "block_update confirms the cell; a later error doesn't revert it");
        var stale = Parse("{\"snapshot_id\":0,\"changes\":[[2,0,2,0]]}");
        var hit2 = BlockRaycast.Cast(w, new Vector3(2.5f, 6f, 2.5f), down, 20);
        ed.Break(w, hit2, 20);
        ed.OnBlockUpdate(w, stale);
        check(ed.Pending.Count == 1, "a stale block_update doesn't confirm anything");
        check(ed.Pending.Expire(w, 22.9) == 0 && w.Get(2, 0, 2) == 0, "pending edit kept before expiry");
        check(ed.Pending.Expire(w, 23.1) == 1 && w.Get(2, 0, 2) == 1, "server silence for 3 s reverts to the old value");

        // Place: the selected block goes into the cell in front of the hit face.
        ed.Selected = "minecraft:dirt";
        var hit3 = BlockRaycast.Cast(w, new Vector3(4.5f, 6f, 4.5f), down, 20);
        msg = ed.Place(w, hit3, down, 30);
        pos = Json.Ints(RT(msg).A("pos"));
        check(msg.S("op") == "place" && msg.S("state") == "minecraft:dirt" && pos.SequenceEqual(new[] { 104, 61, -36 }) && w.Get(4, 1, 4) == 2, "place goes on top of the hit face, optimistically");
        int before = w.Palette.Count;
        ed.Selected = "minecraft:gold_block";
        var hit4 = BlockRaycast.Cast(w, new Vector3(5.5f, 6f, 5.5f), down, 20);
        msg = ed.Place(w, hit4, down, 30);
        check(msg != null && msg.S("state") == "minecraft:gold_block" && w.Get(5, 1, 5) == 0 && w.Palette.Count == before && ed.Pending.Count == 1,
              "a state not in the palette is sent but not guessed (the palette stays in sync with the server)");
        var side = BlockRaycast.Cast(w, new Vector3(-3f, 0.5f, 4.5f), new Vector3(1, 0, 0), 20);
        check(ed.Place(w, side, new Vector3(1, 0, 0), 30) == null && ed.Result.Contains("outside"), "no placing outside the area");

        // Waterlogged slab: breaking it leaves water (in the palette here, so it's shown at once).
        var hitSlab = BlockRaycast.Cast(w, new Vector3(3.5f, 6f, 3.5f), down, 20);
        ed.Break(w, hitSlab, 40);
        check(Eq(hitSlab.Block, V(3, 1, 3)) && w.State(w.Get(3, 1, 3)) == BlockEditRules.WaterSource, "breaking a waterlogged slab shows water");
        var hitSea = BlockRaycast.Cast(w, new Vector3(3.5f, 6f, 3.5f), down, 20);
        ed.Selected = "minecraft:oak_slab[type=bottom,waterlogged=false]";
        msg = ed.Place(w, hitSea, down, 40);
        check(Eq(hitSea.Block, V(3, 0, 3)) && msg.S("state") == "minecraft:oak_slab[type=bottom,waterlogged=false]" && w.State(w.Get(3, 1, 3)) == "minecraft:oak_slab[type=bottom,waterlogged=true]",
              "aiming through water at the seabed places into the water (waterlogged)");

        check(ed.Pick(w, BlockRaycast.Cast(w, new Vector3(4.5f, 6f, 4.5f), down, 20)) && ed.Selected == "minecraft:dirt", "B picks the targeted block");

        // Snapshot replaces the guesses.
        w.SnapshotId = 2; ed.OnSnapshot();
        check(ed.Pending.Count == 0, "a new snapshot clears pending edits");

        // An old server answers unknown_type: undo everything and stop editing.
        w.SnapshotId = 1;
        ed.Break(w, BlockRaycast.Cast(w, new Vector3(6.5f, 6f, 6.5f), down, 20), 50);
        ed.OnError(w, Parse("{\"type\":\"error\",\"code\":\"unknown_type\",\"message\":\"unknown type: block_edit\",\"ref_seq\":9}"));
        check(!ed.ServerAllows && ed.Pending.Count == 0 && w.Get(6, 0, 6) == 1, "unknown_type for block_edit reverts and disables editing");

        // Golden fixtures: the builder makes the place fixture, and the denial fixture reverts its cell.
        var fx = Parse(System.IO.File.ReadAllText(fixture("block_edit.json")));
        var built = Protocol.BlockEdit("place", V(105, 64, -35), "minecraft:oak_log[axis=x]", "east");
        check(fx.S("type") == built.S("type") && fx.S("op") == built.S("op") && fx.S("state") == built.S("state") && fx.S("face") == built.S("face") &&
              Json.Ints(fx.A("pos")).SequenceEqual(Json.Ints(Parse(Json.Write(built)).A("pos"))), "block_edit builder matches tools/fixtures/block_edit.json");
        var ed2 = new BlockEditor { ServerAllows = true };
        ed2.Pending.Apply(w, V(5, 4, 5), 2, 0);
        ed2.OnError(w, Parse(System.IO.File.ReadAllText(fixture("error_edit_denied.json"))));
        check(w.Get(5, 4, 5) == 0 && ed2.Pending.Count == 0 && ed2.Result.StartsWith("edit_denied"), "error_edit_denied fixture undoes the edit at its pos");
        var hello = Protocol.Hello("");
        check(hello.A("caps").Contains(Protocol.CapEdit), "hello asks for edit_v1");
    }

    /// <summary>Real edits through the same code the headset uses: raycast down the region's middle column, break the
    /// surface block, put it back, place and break a gold block, and an edit the server refuses (reverted).</summary>
    public static void Live(string url, Action<bool, string> check)
    {
        var link = new LinkClient(url, "");
        link.Start();
        var w = new WorldModel();
        var ed = new BlockEditor();
        var down = new Vector3(0, -1, 0);
        int step = 0; string original = null; Vector3Int cell = default, top = default;
        var t0 = DateTime.UtcNow; var phaseStart = t0; var log = new List<string>();
        double Now() => (DateTime.UtcNow - t0).TotalSeconds;
        bool Wait(Func<bool> done, string what)
        {
            var end = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < end)
            {
                while (link.Inbound.TryDequeue(out var m))
                    switch (m.S("type"))
                    {
                        case "welcome": ed.ServerAllows = m.A("caps")?.Contains(Protocol.CapEdit) == true; break;
                        case "chunk_snapshot": w.ApplySnapshot(ChunkSnapshot.From(m)); ed.OnSnapshot(); break;
                        case "block_update": ed.OnBlockUpdate(w, m); w.ApplyBlockUpdate(m); break;
                        case "error": log.Add($"error {m.S("code")} {m.S("message")}"); ed.OnError(w, m); break;
                    }
                ed.Tick(w, Now());
                if (done()) { log.Add($"{what}: {(DateTime.UtcNow - phaseStart).TotalMilliseconds:F0} ms"); phaseStart = DateTime.UtcNow; return true; }
                Thread.Sleep(5);
            }
            log.Add(what + ": timeout");
            return false;
        }
        BlockHit Down() => BlockRaycast.Cast(w, new Vector3(w.Dims.x / 2 + 0.5f, w.Dims.y + 1, w.Dims.z / 2 + 0.5f), down, w.Dims.y + 4);

        check(Wait(() => w.Loaded && link.Connected, "snapshot"), "live edit: snapshot");
        check(ed.ServerAllows, "live edit: server granted edit_v1");
        var h = Down();
        if (!h.Hit || !ed.ServerAllows) { link.Dispose(); return; }
        cell = h.Block; original = w.State(w.Get(cell.x, cell.y, cell.z));
        phaseStart = DateTime.UtcNow;
        link.Send(ed.Break(w, h, Now())); step++;
        bool optimistic = ed.Pending.Count == 1 && w.State(w.Get(cell.x, cell.y, cell.z)) == BlockEditRules.BreakResult(original);
        check(Wait(() => ed.Pending.Count == 0 && w.State(w.Get(cell.x, cell.y, cell.z)) == BlockEditRules.BreakResult(original), "break confirmed"),
              $"live edit: break {original} at {w.Origin + cell} -> block_update {w.State(w.Get(cell.x, cell.y, cell.z))} (optimistic {optimistic})");
        // Put it back on the block below, from the same ray.
        ed.Selected = original;
        h = Down();
        link.Send(ed.Place(w, h, down, Now())); step++;
        check(Wait(() => ed.Pending.Count == 0 && w.State(w.Get(cell.x, cell.y, cell.z)) == original, "restore confirmed") && Eq(h.Adjacent, cell),
              $"live edit: place {original} back -> {w.State(w.Get(cell.x, cell.y, cell.z))}");
        // Gold isn't in the palette: no guess, it arrives with palette_add.
        ed.Selected = "minecraft:gold_block";
        h = Down(); top = h.Adjacent;
        link.Send(ed.Place(w, h, down, Now())); step++;
        check(Wait(() => w.State(w.Get(top.x, top.y, top.z)) == "minecraft:gold_block", "gold placed"), $"live edit: place gold_block (new palette entry) at {w.Origin + top}");
        h = Down();
        link.Send(ed.Break(w, h, Now())); step++;
        check(Wait(() => ed.Pending.Count == 0 && w.Get(top.x, top.y, top.z) == w.Palette.IndexOf(BlockEditRules.BreakResult("minecraft:gold_block")), "gold broken"), "live edit: break gold_block");
        // A guess the server refuses (command blocks are on the deny list): the error's pos reverts it.
        ed.Pending.Apply(w, top, (ushort)w.Palette.IndexOf("minecraft:gold_block"), Now());
        link.Send(Protocol.BlockEdit("place", w.Origin + top, "minecraft:command_block[conditional=false,facing=up]", "up"));
        check(Wait(() => ed.Pending.Count == 0, "denied") && w.State(w.Get(top.x, top.y, top.z)) == "minecraft:air", "live edit: denied place is reverted from the error's pos");
        foreach (var l in log) Console.WriteLine("live edit: " + l);
        link.Dispose();
    }
}
