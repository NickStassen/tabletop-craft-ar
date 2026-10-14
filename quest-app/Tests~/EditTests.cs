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
        Placement(check);
        TwoBlockEdits(check);
        Picks(check);
        Deltas(check, fixture);
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

        // Navigation shows a window of the region (NavState: fractional bounds) and clips the rest: never target past it.
        var nav = World(12, 6, 12, (x, y, z) => y == 0 || x == 11 || (x == 9 && y == 1) ? 1 : 0, "minecraft:air", "minecraft:stone");
        Vector3 lo = new Vector3(2.5f, 0, 2.5f), hi = new Vector3(9.5f, 6, 9.5f);
        check(!BlockRaycast.Cast(nav, new Vector3(1.5f, 5f, 5.5f), new Vector3(0, -1, 0), 20, lo, hi).Hit, "window: a column outside the visible box can't be hit");
        h = BlockRaycast.Cast(nav, new Vector3(5.5f, 5f, 5.5f), new Vector3(0, -1, 0), 20, lo, hi);
        check(h.Hit && Eq(h.Block, V(5, 0, 5)) && h.AdjacentInView, "window: inside it works as before");
        h = BlockRaycast.Cast(nav, new Vector3(-3f, 0.5f, 5.5f), new Vector3(1, 0, 0), 30, lo, hi);
        check(h.Hit && Eq(h.Block, V(2, 0, 5)) && h.Face == 0 && Near(h.Point.x, 2.5f) && !h.AdjacentInView,
              $"window: entering through its cut side hits the half-shown cell; the cell before it is hidden (got {h.Block}, point x {h.Point.x})");
        h = BlockRaycast.Cast(nav, new Vector3(4.5f, 2.5f, 5.5f), new Vector3(1, 0, 0), 30, lo, hi);
        check(!h.Hit, "window: a wall in the hidden margin (x = 11) isn't hit");
        h = BlockRaycast.Cast(nav, new Vector3(4.5f, 1.5f, 5.5f), new Vector3(1, 0, 0), 30, lo, hi);
        check(h.Hit && Eq(h.Block, V(9, 1, 5)) && Eq(h.Adjacent, V(8, 1, 5)) && h.AdjacentInView, "window: a half-shown block at its edge can be hit and built on");
        check(!BlockRaycast.Cast(nav, new Vector3(4.5f, 1.5f, 5.5f), new Vector3(1, 0, 0), 30, new Vector3(2, 0, 2), new Vector3(9, 6, 9)).Hit,
              "window: with a whole-block edge the next cell (x = 9) is outside");

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
        check(BlockEditRules.BreakResult("minecraft:oak_slab[type=bottom,waterlogged=true]") == BlockEditRules.WaterSource &&
              BlockEditRules.BreakResult("minecraft:kelp[age=3]") == BlockEditRules.WaterSource &&
              BlockEditRules.BreakResult("minecraft:oak_slab[type=bottom,waterlogged=false]") == "minecraft:air" &&
              BlockEditRules.BreakResult("minecraft:grass_block[snowy=false]") == "minecraft:air", "breaking a block that holds water leaves water");
        check(BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:water[level=0]") == "minecraft:oak_slab[type=bottom,waterlogged=true]" &&
              BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:water[level=3]") == "minecraft:oak_slab[type=bottom,waterlogged=false]" &&
              BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:air") == "minecraft:oak_slab[type=bottom,waterlogged=false]" &&
              BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:seagrass") == "minecraft:oak_slab[type=bottom,waterlogged=true]" &&
              BlockEditRules.PlaceResult("minecraft:stone", "minecraft:water[level=0]") == "minecraft:stone", "placing into a water source waterlogs");
        check(BlockEditRules.PlaceResult("minecraft:oak_slab[type=bottom,waterlogged=true]", "minecraft:air") == "minecraft:oak_slab[type=bottom,waterlogged=false]" &&
              BlockEditRules.PlaceResult("minecraft:tube_coral_fan[waterlogged=true]", "minecraft:water[level=8]") == "minecraft:tube_coral_fan[waterlogged=false]",
              "a waterlogged pick placed on land (or in falling water) is dry: no water source comes with it");
        check(BlockEditRules.Label("minecraft:oak_stairs[facing=east]") == "oak stairs", "block label");

        // Replaceable cells (vanilla canBeReplaced, mirrored by the plugin).
        check(BlockPlacement.Replaceable("minecraft:short_grass", "minecraft:stone") && BlockPlacement.Replaceable("minecraft:snow[layers=1]", "minecraft:stone") &&
              !BlockPlacement.Replaceable("minecraft:snow[layers=2]", "minecraft:stone") && BlockPlacement.Replaceable("minecraft:vine[east=false,north=true,south=false,up=false,west=false]", null) &&
              BlockPlacement.Replaceable("minecraft:sculk_vein[down=true,east=false,north=false,south=false,up=false,waterlogged=false,west=false]", "minecraft:stone") &&
              BlockPlacement.Replaceable("minecraft:fire[age=0,east=false,north=false,south=false,up=false,west=false]", null) &&
              BlockPlacement.Replaceable("minecraft:water[level=0]", "minecraft:stone") && BlockPlacement.Replaceable("minecraft:air", "minecraft:stone") &&
              !BlockPlacement.Replaceable("minecraft:stone", "minecraft:dirt") && !BlockPlacement.Replaceable("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:oak_slab[type=bottom,waterlogged=false]"),
              "replaceable: grass, one snow layer, vines, sculk vein, fire, fluids and air; not two snow layers, solid blocks or slabs");
        check(!BlockPlacement.Replaceable("minecraft:short_grass", "minecraft:short_grass") && BlockPlacement.Replaceable("minecraft:short_grass", "minecraft:fern"),
              "replaceable: a block doesn't replace itself (vanilla: the item of the block in the cell)");

        // The look (Direction.orderedByNearest, Player.getDirection, yaw).
        var north = Look.From(new Vector3(0, -0.5f, -1));
        check(north.Horizontal == Dir.North && Near(Math.Abs(north.Yaw), 180) && north.Nearest().SequenceEqual(new[] { Dir.North, Dir.Down, Dir.West, Dir.East, Dir.Up, Dir.South }),
              $"look north: horizontal north, yaw 180, nearest {string.Join(",", north.Nearest())}");
        var down = Look.From(new Vector3(0.1f, -1, -0.3f));
        check(down.NearestOne == Dir.Down && down.Horizontal == Dir.North && down.Nearest()[1] == Dir.North && down.Nearest()[2] == Dir.East, "look down (a bit north-east): nearest down, then north, then east");
        check(Look.From(new Vector3(1, 0, 0.2f)).Horizontal == Dir.East && Near(Look.From(new Vector3(1, 0, 0)).Yaw, -90) && Look.From(new Vector3(0, 0, 1)).Horizontal == Dir.South &&
              Look.From(new Vector3(-1, 0.1f, 0)).Horizontal == Dir.West, "look: east is yaw -90, south 0, west 90");
        check(BlockPlacement.Segment(0) == 0 && BlockPlacement.Segment(360) == 0 && BlockPlacement.Segment(-90) == 12 && BlockPlacement.Segment(90) == 4 && BlockPlacement.Segment(11.25f) == 1 && BlockPlacement.Segment(-11.25f) == 0,
              "rotation segments round like Java (Math.round)");
        check(BlockPlacement.WallVariant("minecraft:torch", null) == "minecraft:wall_torch" && BlockPlacement.WallVariant("minecraft:soul_torch", null) == "minecraft:soul_wall_torch" &&
              BlockPlacement.WallVariant("minecraft:oak_sign[rotation=0,waterlogged=false]", null) == "minecraft:oak_wall_sign" &&
              BlockPlacement.WallVariant("minecraft:oak_hanging_sign[attached=false,rotation=0,waterlogged=false]", null) == "minecraft:oak_wall_hanging_sign" &&
              BlockPlacement.WallVariant("minecraft:player_head[powered=false,rotation=0]", null) == "minecraft:player_wall_head" &&
              BlockPlacement.WallVariant("minecraft:dead_tube_coral_fan[waterlogged=true]", null) == "minecraft:dead_tube_coral_wall_fan" &&
              BlockPlacement.WallVariant("minecraft:stone", null) == null && BlockPlacement.WallVariant("minecraft:wall_torch[facing=east]", null) == null &&
              BlockPlacement.WallVariant("minecraft:torch", id => id != "minecraft:wall_torch") == null, "wall forms by name, checked against the registry");
        check(BlockPlacement.Rebuild("minecraft:oak_sign[rotation=4,waterlogged=false]", "minecraft:oak_wall_sign", new[] { "rotation" }, "facing", "east") == "minecraft:oak_wall_sign[facing=east,waterlogged=false]" &&
              BlockPlacement.Rebuild("minecraft:torch", "minecraft:wall_torch", null, "facing", "west") == "minecraft:wall_torch[facing=west]",
              "wall state: standing properties minus rotation, plus facing, sorted like getAsString");
    }

    /// <summary>A placement test world: 10x8x10 at origin (100,60,-40), stone floor at y=0, block A = stone at (3,1,3),
    /// a stone ceiling block at (5,4,5), and whatever else the case puts in.</summary>
    static WorldModel PlaceWorld(params (int x, int y, int z, string s)[] extra)
    {
        var w = World(10, 8, 10, (x, y, z) => y == 0 || (x == 3 && y == 1 && z == 3) || (x == 5 && y == 4 && z == 5) ? 1 : 0, "minecraft:air", "minecraft:stone");
        foreach (var e in extra)
        {
            int id = w.Palette.IndexOf(e.s);
            if (id < 0) { id = w.Palette.Count; w.Palette.Add(e.s); }
            w.Grid[w.Index(e.x, e.y, e.z)] = (ushort)id;
        }
        w.Origin = V(100, 60, -40);
        return w;
    }

    static BlockHit HitOn(WorldModel w, Vector3Int block, int face, Vector3 point)
    {
        var adj = block + BlockRaycast.Normal(face);
        return new BlockHit { Hit = true, Block = block, Face = face, Adjacent = adj, AdjacentInView = w.InBounds(adj.x, adj.y, adj.z), Point = point };
    }

    static void Placement(Action<bool, string> check)
    {
        var ed = new BlockEditor { ServerAllows = true };
        var lookN = new Vector3(0, -0.5f, -1);           // looking north, a bit down
        var lookDown = new Vector3(0.1f, -1, -0.3f);      // looking down (a bit north)
        var A = V(3, 1, 3); var ceil = V(5, 4, 5);
        var top = new Vector3(3.5f, 2, 3.5f);             // A's top face
        var eastLow = new Vector3(4, 1.3f, 3.5f); var eastHigh = new Vector3(4, 1.8f, 3.5f);   // A's east face, lower / upper half
        var under = new Vector3(5.5f, 4, 5.5f);           // the ceiling block's underside
        string Plan(string sel, WorldModel w, Vector3Int block, int face, Vector3 point, Vector3 look)
        {
            ed.Selected = sel;
            var p = ed.Plan(w, HitOn(w, block, face, point), look, 0);
            return p.Ok ? p.State : "refused: " + p.Why;
        }
        var w0 = PlaceWorld();
        string On(string sel, int face, Vector3 point, Vector3 look) => Plan(sel, w0, face == Dir.Down ? ceil : A, face, point, look);
        void Is(string got, string want, string what) => check(got == want, what + (got == want ? "" : $" (got {got})"));

        Is(On("minecraft:oak_log[axis=y]", Dir.East, eastLow, lookN), "minecraft:oak_log[axis=x]", "log: axis of the clicked face (east -> x)");
        Is(On("minecraft:oak_log[axis=y]", Dir.Up, top, lookN), "minecraft:oak_log[axis=y]", "log: on top -> y");
        Is(On("minecraft:furnace[facing=north,lit=false]", Dir.Up, top, lookN), "minecraft:furnace[facing=south,lit=false]", "furnace faces the user");
        Is(On("minecraft:anvil[facing=north]", Dir.Up, top, lookN), "minecraft:anvil[facing=east]", "anvil: clockwise of the look (AnvilBlock)");
        Is(On("minecraft:campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]", Dir.Up, top, new Vector3(1, -0.5f, 0)), "minecraft:campfire[facing=east,lit=true,signal_fire=false,waterlogged=false]", "campfire faces the way the user looks");

        // Torches, signs, banners, heads, coral fans: standing on top, the wall form on a side (StandingAndWallBlockItem).
        Is(On("minecraft:torch", Dir.Up, top, lookN), "minecraft:torch", "torch on top stands");
        Is(On("minecraft:torch", Dir.East, eastLow, lookN), "minecraft:wall_torch[facing=east]", "torch on a side is a wall torch facing out");
        Is(On("minecraft:torch", Dir.Down, under, lookN), "refused: can't place: nothing to attach the torch to", "torch under a block with nothing around: refused");
        Is(On("minecraft:redstone_torch[lit=true]", Dir.East, eastLow, lookN), "minecraft:redstone_wall_torch[facing=east,lit=true]", "redstone torch on a side");
        Is(On("minecraft:oak_sign[rotation=0,waterlogged=false]", Dir.Up, top, lookN), "minecraft:oak_sign[rotation=0,waterlogged=false]", "sign on top faces the user (rotation from the yaw + 180)");
        Is(On("minecraft:oak_sign[rotation=0,waterlogged=false]", Dir.Up, top, new Vector3(1, -0.5f, 0)), "minecraft:oak_sign[rotation=4,waterlogged=false]", "sign placed looking east: rotation 4 (faces west)");
        Is(On("minecraft:oak_sign[rotation=0,waterlogged=false]", Dir.East, eastLow, lookN), "minecraft:oak_wall_sign[facing=east,waterlogged=false]", "sign on a side is a wall sign");
        Is(On("minecraft:white_banner[rotation=0]", Dir.East, eastLow, lookN), "minecraft:white_wall_banner[facing=east]", "banner on a side");
        Is(On("minecraft:skeleton_skull[powered=false,rotation=0]", Dir.Up, top, lookN), "minecraft:skeleton_skull[powered=false,rotation=8]", "skull on top: rotation from the yaw (SkullBlock)");
        Is(On("minecraft:skeleton_skull[powered=false,rotation=0]", Dir.East, eastLow, lookN), "minecraft:skeleton_wall_skull[facing=east,powered=false]", "skull on a side");
        Is(On("minecraft:tube_coral_fan[waterlogged=true]", Dir.East, eastLow, lookN), "minecraft:tube_coral_wall_fan[facing=east,waterlogged=false]", "coral fan on a side (dry in air)");
        Is(On("minecraft:oak_hanging_sign[attached=false,rotation=0,waterlogged=false]", Dir.Down, under, lookN), "minecraft:oak_hanging_sign[attached=false,rotation=0,waterlogged=false]", "hanging sign under a full block: not attached, 4-way");
        Is(On("minecraft:oak_hanging_sign[attached=false,rotation=0,waterlogged=false]", Dir.East, eastLow, lookN), "minecraft:oak_wall_hanging_sign[facing=south,waterlogged=false]", "hanging sign on a side: along the wall");

        // Buttons, levers, grindstones (face), bells (attachment).
        Is(On("minecraft:oak_button[face=wall,facing=north,powered=false]", Dir.Up, top, lookN), "minecraft:oak_button[face=floor,facing=north,powered=false]", "button on top: floor, facing the look");
        Is(On("minecraft:oak_button[face=wall,facing=north,powered=false]", Dir.East, eastLow, lookN), "minecraft:oak_button[face=wall,facing=east,powered=false]", "button on a side: wall, facing out");
        Is(On("minecraft:lever[face=wall,facing=north,powered=false]", Dir.Down, under, lookN), "minecraft:lever[face=ceiling,facing=north,powered=false]", "lever under a block: ceiling");
        Is(On("minecraft:grindstone[face=wall,facing=north]", Dir.Down, under, lookN), "minecraft:grindstone[face=ceiling,facing=north]", "grindstone under a block: ceiling");
        Is(On("minecraft:bell[attachment=floor,facing=north,powered=false]", Dir.Up, top, lookN), "minecraft:bell[attachment=floor,facing=north,powered=false]", "bell on top: floor");
        Is(On("minecraft:bell[attachment=floor,facing=north,powered=false]", Dir.East, eastLow, lookN), "minecraft:bell[attachment=single_wall,facing=west,powered=false]", "bell on a side: single wall, facing the wall");
        var wBell = PlaceWorld((5, 1, 3, "minecraft:stone"));
        Is(Plan("minecraft:bell[attachment=floor,facing=north,powered=false]", wBell, A, Dir.East, eastLow, lookN), "minecraft:bell[attachment=double_wall,facing=west,powered=false]", "bell between two blocks: double wall");
        Is(On("minecraft:bell[attachment=floor,facing=north,powered=false]", Dir.Down, under, lookN), "minecraft:bell[attachment=ceiling,facing=north,powered=false]", "bell under a block: ceiling");

        // Top halves: slabs, stairs, trapdoors.
        const string slab = "minecraft:oak_slab[type=bottom,waterlogged=false]";
        Is(On(slab, Dir.Up, top, lookN), slab, "slab on top: bottom");
        Is(On(slab, Dir.Down, under, lookN), "minecraft:oak_slab[type=top,waterlogged=false]", "slab under a block: top");
        Is(On(slab, Dir.East, eastHigh, lookN), "minecraft:oak_slab[type=top,waterlogged=false]", "slab on the upper half of a side: top");
        Is(On(slab, Dir.East, eastLow, lookN), slab, "slab on the lower half of a side: bottom");
        const string stairs = "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]";
        Is(On(stairs, Dir.Up, top, lookN), "minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]", "stairs face the way the user looks");
        Is(On(stairs, Dir.East, eastHigh, lookN), "minecraft:oak_stairs[facing=north,half=top,shape=straight,waterlogged=false]", "stairs on the upper half of a side: upside down");
        Is(On(stairs, Dir.Down, under, lookN), "minecraft:oak_stairs[facing=north,half=top,shape=straight,waterlogged=false]", "stairs under a block: upside down");
        const string trap = "minecraft:oak_trapdoor[facing=north,half=bottom,open=false,powered=false,waterlogged=false]";
        Is(On(trap, Dir.East, eastHigh, lookN), "minecraft:oak_trapdoor[facing=east,half=top,open=false,powered=false,waterlogged=false]", "trapdoor on a side: facing out, top from the hit point");
        Is(On(trap, Dir.East, eastLow, lookN), "minecraft:oak_trapdoor[facing=east,half=bottom,open=false,powered=false,waterlogged=false]", "trapdoor on the lower half of a side: bottom");
        Is(On(trap, Dir.Up, top, lookN), "minecraft:oak_trapdoor[facing=south,half=bottom,open=false,powered=false,waterlogged=false]", "trapdoor on top: bottom, facing the user");
        Is(On(trap, Dir.Down, under, lookN), "minecraft:oak_trapdoor[facing=south,half=top,open=false,powered=false,waterlogged=false]", "trapdoor under a block: top");

        // Facing the clicked face, hoppers, observers, pistons.
        Is(On("minecraft:end_rod[facing=up]", Dir.East, eastLow, lookN), "minecraft:end_rod[facing=east]", "end rod points out of the clicked face");
        var wRod = PlaceWorld((6, 1, 6, "minecraft:end_rod[facing=up]"));
        Is(Plan("minecraft:end_rod[facing=up]", wRod, V(6, 1, 6), Dir.Up, new Vector3(6.5f, 2, 6.5f), lookN), "minecraft:end_rod[facing=down]", "end rod on an end rod pointing the same way: flipped (EndRodBlock)");
        Is(On("minecraft:lightning_rod[facing=up,powered=false,waterlogged=false]", Dir.East, eastLow, lookN), "minecraft:lightning_rod[facing=east,powered=false,waterlogged=false]", "lightning rod points out of the clicked face");
        Is(On("minecraft:amethyst_cluster[facing=up,waterlogged=false]", Dir.Down, under, lookN), "minecraft:amethyst_cluster[facing=down,waterlogged=false]", "amethyst cluster under a block points down");
        Is(On("minecraft:shulker_box[facing=up]", Dir.East, eastLow, lookN), "minecraft:shulker_box[facing=east]", "shulker box opens away from the clicked face");
        Is(On("minecraft:hopper[enabled=true,facing=down]", Dir.Up, top, lookN), "minecraft:hopper[enabled=true,facing=down]", "hopper on top: down");
        Is(On("minecraft:hopper[enabled=true,facing=down]", Dir.East, eastLow, lookN), "minecraft:hopper[enabled=true,facing=west]", "hopper on a side: into the clicked block");
        Is(On("minecraft:observer[facing=south,powered=false]", Dir.Up, top, lookDown), "minecraft:observer[facing=down,powered=false]", "observer looking down: its face looks down (away from the user)");
        Is(On("minecraft:observer[facing=south,powered=false]", Dir.Up, top, lookN), "minecraft:observer[facing=north,powered=false]", "observer looking north: face north");
        Is(On("minecraft:piston[extended=false,facing=north]", Dir.Up, top, lookDown), "minecraft:piston[extended=false,facing=up]", "piston looking down: faces up (the user)");
        Is(On("minecraft:dispenser[facing=north,triggered=false]", Dir.Up, top, lookN), "minecraft:dispenser[facing=south,triggered=false]", "dispenser faces the user");
        Is(On("minecraft:barrel[facing=north,open=false]", Dir.Up, top, lookDown), "minecraft:barrel[facing=up,open=false]", "barrel looking down opens up");
        Is(On("minecraft:crafter[crafting=false,orientation=north_up,triggered=false]", Dir.Up, top, lookDown), "minecraft:crafter[crafting=false,orientation=up_north,triggered=false]", "crafter looking down: front up, top the look");

        // Doors, beds, tall plants: the lower half / foot, with room for the other half.
        const string door = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]";
        ed.Selected = door;
        var pDoor = ed.Plan(w0, HitOn(w0, V(1, 0, 1), Dir.Up, new Vector3(1.8f, 1, 1.5f)), lookN, 0);
        check(pDoor.Ok && pDoor.State == "minecraft:oak_door[facing=north,half=lower,hinge=right,open=false,powered=false]" && pDoor.HasPartner && Eq(pDoor.PartnerCell, V(1, 2, 1)) &&
              pDoor.PartnerState == "minecraft:oak_door[facing=north,half=upper,hinge=right,open=false,powered=false]", $"door: lower half, upper half above, hinge right from the hit point ({pDoor.State})");
        Is(Plan(door, w0, V(1, 0, 1), Dir.Up, new Vector3(1.2f, 1, 1.5f), lookN), door, "door clicked on its left side: hinge left");
        var wDoorWall = PlaceWorld((0, 1, 1, "minecraft:stone"), (0, 2, 1, "minecraft:stone"));
        Is(Plan(door, wDoorWall, V(1, 0, 1), Dir.Up, new Vector3(1.8f, 1, 1.5f), lookN), door, "door next to a wall on its left: hinge on the wall side (left)");
        var wLow = PlaceWorld((1, 2, 1, "minecraft:stone"));
        Is(Plan(door, wLow, V(1, 0, 1), Dir.Up, new Vector3(1.5f, 1, 1.5f), lookN), "refused: can't place: no room for the oak door", "door without room above: refused");
        var lookE = new Vector3(1, -0.4f, 0.1f);
        ed.Selected = "minecraft:red_bed[facing=north,occupied=false,part=foot]";
        var pBed = ed.Plan(w0, HitOn(w0, V(1, 0, 6), Dir.Up, new Vector3(1.5f, 1, 6.5f)), lookE, 0);
        check(pBed.Ok && pBed.State == "minecraft:red_bed[facing=east,occupied=false,part=foot]" && Eq(pBed.PartnerCell, V(2, 1, 6)) && pBed.PartnerState == "minecraft:red_bed[facing=east,occupied=false,part=head]",
              $"bed: foot where clicked, head one block the way the user looks ({pBed.State} + {pBed.PartnerState} at {pBed.PartnerCell})");
        var wBed = PlaceWorld((2, 1, 6, "minecraft:stone"));
        Is(Plan("minecraft:red_bed[facing=north,occupied=false,part=foot]", wBed, V(1, 0, 6), Dir.Up, new Vector3(1.5f, 1, 6.5f), lookE), "refused: can't place: no room for the red bed", "bed without room for its head: refused");
        ed.Selected = "minecraft:tall_grass[half=lower]";
        var pTall = ed.Plan(w0, HitOn(w0, V(7, 0, 7), Dir.Up, new Vector3(7.5f, 1, 7.5f)), lookN, 0);
        check(pTall.Ok && pTall.State == "minecraft:tall_grass[half=lower]" && Eq(pTall.PartnerCell, V(7, 2, 7)) && pTall.PartnerState == "minecraft:tall_grass[half=upper]", "tall grass: both halves");
        Is(Plan("minecraft:small_dripleaf[facing=north,half=lower,waterlogged=false]", w0, V(7, 0, 7), Dir.Up, new Vector3(7.5f, 1, 7.5f), lookN),
           "minecraft:small_dripleaf[facing=south,half=lower,waterlogged=false]", "small dripleaf faces the user");
        var wTallGrass = PlaceWorld((7, 2, 7, "minecraft:stone"));
        Is(Plan("minecraft:tall_grass[half=lower]", wTallGrass, V(7, 0, 7), Dir.Up, new Vector3(7.5f, 1, 7.5f), lookN), "refused: can't place: no room for the tall grass", "tall grass without room above: refused");

        // Lanterns, vines, lichen, cocoa, ladders, leaves, rails.
        Is(On("minecraft:lantern[hanging=false,waterlogged=false]", Dir.Down, under, lookN), "minecraft:lantern[hanging=true,waterlogged=false]", "lantern under a block hangs");
        Is(On("minecraft:lantern[hanging=false,waterlogged=false]", Dir.Up, top, lookN), "minecraft:lantern[hanging=false,waterlogged=false]", "lantern on top stands");
        Is(On("minecraft:vine[east=false,north=false,south=false,up=false,west=false]", Dir.East, eastLow, lookN), "minecraft:vine[east=false,north=false,south=false,up=false,west=true]", "vine on a side clings to it");
        Is(On("minecraft:glow_lichen[down=false,east=false,north=false,south=false,up=false,waterlogged=false,west=false]", Dir.Up, top, lookN),
           "minecraft:glow_lichen[down=true,east=false,north=false,south=false,up=false,waterlogged=false,west=false]", "glow lichen on top lies on it");
        Is(On("minecraft:cocoa[age=0,facing=north]", Dir.East, eastLow, lookN), "minecraft:cocoa[age=0,facing=west]", "cocoa on a log's side faces the log");
        Is(On("minecraft:ladder[facing=north,waterlogged=false]", Dir.East, eastLow, lookN), "minecraft:ladder[facing=east,waterlogged=false]", "ladder on a side faces out");
        Is(On("minecraft:oak_leaves[distance=7,persistent=false,waterlogged=false]", Dir.Up, top, lookN), "minecraft:oak_leaves[distance=7,persistent=true,waterlogged=false]", "placed leaves are persistent");
        Is(On("minecraft:rail[shape=north_south,waterlogged=false]", Dir.Up, top, new Vector3(1, -0.5f, 0.2f)), "minecraft:rail[shape=east_west,waterlogged=false]", "rail along the look");

        // Replaceable cells: aim at short grass -> into its cell; place next to grass / one snow layer, not two layers.
        var wGrass = PlaceWorld((6, 1, 6, "minecraft:short_grass"), (4, 1, 3, "minecraft:snow[layers=1]"), (2, 1, 3, "minecraft:snow[layers=2]"));
        ed.Selected = "minecraft:dirt";
        var pg = ed.Plan(wGrass, HitOn(wGrass, V(6, 1, 6), Dir.Up, new Vector3(6.5f, 1.4f, 6.5f)), lookN, 0);
        check(pg.Ok && Eq(pg.Cell, V(6, 1, 6)), $"aiming at short grass places into its cell (got {pg.Cell})");
        var ps = ed.Plan(wGrass, HitOn(wGrass, A, Dir.East, eastLow), lookN, 0);
        check(ps.Ok && Eq(ps.Cell, V(4, 1, 3)), "placing next to a single snow layer replaces it");
        var ps2 = ed.Plan(wGrass, HitOn(wGrass, A, Dir.West, new Vector3(3, 1.3f, 3.5f)), lookN, 0);
        check(!ps2.Ok && ps2.Why == "can't place: snow is in the way", $"two snow layers are not replaced ({ps2.Why})");
        check(BlockRaycast.CanPlaceInto(wGrass, V(6, 1, 6), "minecraft:dirt") && !BlockRaycast.CanPlaceInto(wGrass, V(6, 1, 6), "minecraft:short_grass") && !BlockRaycast.CanPlaceInto(wGrass, A, "minecraft:dirt"),
              "CanPlaceInto: grass yes (not by itself), stone no");
        var pgg = ed.Plan(wGrass, BlockRaycast.Cast(wGrass, new Vector3(6.5f, 6, 6.5f), new Vector3(0, -1, 0), 10), new Vector3(0, -1, 0), 0);
        check(pgg.Ok && Eq(pgg.Cell, V(6, 1, 6)), "the ray stops at the short grass (its outline) and the block replaces it");
    }

    static void TwoBlockEdits(Action<bool, string> check)
    {
        const string lower = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]";
        const string upper = "minecraft:oak_door[facing=north,half=upper,hinge=left,open=false,powered=false]";
        var w = PlaceWorld((1, 1, 1, lower), (1, 2, 1, upper), (6, 1, 6, "minecraft:red_bed[facing=east,occupied=false,part=foot]"), (7, 1, 6, "minecraft:red_bed[facing=east,occupied=false,part=head]"));
        var ed = new BlockEditor { ServerAllows = true };
        var msg = ed.Break(w, HitOn(w, V(1, 2, 1), Dir.North, new Vector3(1.5f, 2.5f, 1)), 0);
        check(msg != null && w.Get(1, 2, 1) == 0 && w.Get(1, 1, 1) == 0 && ed.Pending.Count == 2, "breaking a door's top half removes both halves (optimistically)");
        ed.OnError(w, Parse("{\"type\":\"error\",\"code\":\"edit_denied\",\"message\":\"no\",\"pos\":[101,62,-39]}"));
        check(w.State(w.Get(1, 1, 1)) == lower && w.State(w.Get(1, 2, 1)) == upper && ed.Pending.Count == 0, "a refused door break puts both halves back");
        ed.Break(w, HitOn(w, V(7, 1, 6), Dir.Up, new Vector3(7.5f, 1.56f, 6.5f)), 0);
        check(w.Get(6, 1, 6) == 0 && w.Get(7, 1, 6) == 0, "breaking a bed's head removes the foot too");
        // A refused door place reverts both guessed halves.
        var w2 = PlaceWorld((9, 7, 9, lower), (9, 6, 9, upper));
        var ed2 = new BlockEditor { ServerAllows = true, Selected = lower };
        msg = ed2.Place(w2, HitOn(w2, V(1, 0, 1), Dir.Up, new Vector3(1.2f, 1, 1.5f)), new Vector3(0, -0.5f, -1), 0);
        check(msg != null && msg.S("state") == lower && w2.State(w2.Get(1, 1, 1)) == lower && w2.State(w2.Get(1, 2, 1)) == upper && ed2.Pending.Count == 2, "door placed: both halves shown at once");
        ed2.OnError(w2, Parse("{\"type\":\"error\",\"code\":\"edit_denied\",\"message\":\"no\",\"pos\":[101,61,-39]}"));
        check(w2.Get(1, 1, 1) == 0 && w2.Get(1, 2, 1) == 0 && ed2.Pending.Count == 0, "a refused door place takes both halves away");
    }

    static void Picks(Action<bool, string> check)
    {
        var reg = BlockRegistry.From(Parse("{\"type\":\"block_registry\",\"count\":6,\"categories\":[{\"id\":\"building\",\"label\":\"B\",\"blocks\":[" +
            "\"minecraft:torch\",\"minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]\",\"minecraft:wheat[age=0]\"," +
            "\"minecraft:oak_leaves[distance=7,persistent=false,waterlogged=false]\",\"minecraft:cauldron\",\"minecraft:stone\"]}]," +
            "\"items\":{},\"variants\":{\"minecraft:wall_torch\":\"minecraft:torch\",\"minecraft:water_cauldron\":\"minecraft:cauldron\"}}"));
        var w = PlaceWorld((1, 1, 1, "minecraft:wall_torch[facing=east]"), (2, 1, 1, "minecraft:oak_door[facing=east,half=upper,hinge=right,open=true,powered=true]"),
                           (4, 1, 1, "minecraft:wheat[age=7]"), (5, 1, 1, "minecraft:oak_leaves[distance=2,persistent=false,waterlogged=true]"), (6, 1, 1, "minecraft:water_cauldron[level=2]"));
        var ed = new BlockEditor { ServerAllows = true, Registry = reg };
        string PickAt(int x) { ed.Pick(w, HitOn(w, V(x, 1, 1), Dir.Up, new Vector3(x + 0.5f, 2, 1.5f))); return ed.Selected; }
        check(PickAt(1) == "minecraft:torch", "pick a wall torch -> torch (the item it was placed with)");
        check(PickAt(2) == "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]", "pick a door's open top half -> the door's default state");
        check(PickAt(4) == "minecraft:wheat[age=0]", "pick grown wheat -> age 0");
        check(PickAt(5) == "minecraft:oak_leaves[distance=7,persistent=false,waterlogged=false]", "pick waterlogged leaves -> default (dry; placing makes them persistent)");
        check(PickAt(6) == "minecraft:cauldron", "pick a water cauldron -> cauldron");
        var bare = new BlockEditor { ServerAllows = true };
        check(bare.Base("minecraft:oak_door[facing=east,half=upper,hinge=right,open=true,powered=true]") == "minecraft:oak_door[facing=east,half=lower,hinge=right,open=true,powered=false]" &&
              bare.Base("minecraft:red_bed[facing=east,occupied=false,part=head]") == "minecraft:red_bed[facing=east,occupied=false,part=foot]" &&
              bare.Base("minecraft:wheat[age=7]") == "minecraft:wheat[age=0]" && bare.Base("minecraft:oak_slab[type=top,waterlogged=true]") == "minecraft:oak_slab[type=top,waterlogged=false]",
              "pick without a registry: lower half, foot, age 0, unpowered, dry");
        // A wall torch still selected (an old hotbar entry) is placed as the torch would be.
        ed.Selected = "minecraft:wall_torch[facing=north]";
        var p = ed.Plan(w, HitOn(w, V(3, 1, 3), Dir.Up, new Vector3(3.5f, 2, 3.5f)), new Vector3(0, -0.5f, -1), 0);
        check(p.Ok && p.State == "minecraft:torch", $"a selected wall torch stands on top ({p.State})");
        p = ed.Plan(w, HitOn(w, V(3, 1, 3), Dir.East, new Vector3(4, 1.5f, 3.5f)), new Vector3(0, -0.5f, -1), 0);
        check(p.Ok && p.State == "minecraft:wall_torch[facing=east]", $"...and goes on a wall as a wall torch ({p.State})");
    }

    /// <summary>Optimistic edits across sectioned deltas (gliding, resizing) and sends the server hasn't answered.</summary>
    static void Deltas(Action<bool, string> check, Func<string, string> fixture)
    {
        Dictionary<string, object> Load(string name) => Parse(System.IO.File.ReadAllText(fixture(name)));
        var w = new WorldModel();
        w.ApplySnapshot(SectionedSnapshot.Assemble(new[] { Load("chunk_snapshot_sections.json") }, null, null));
        var ed = new BlockEditor { ServerAllows = true };
        ed.OnSnapshot(w, null);
        // Guesses at (20,5,3) (old section 1, air: place stone) and (5,9,3) (old section 0, leaves the region).
        ed.Pending.Apply(w, V(20, 5, 3), 1, 0);
        ed.Pending.Apply(w, V(5, 9, 3), 1, 0);
        var listed = new List<Vector3Int>();
        var d = SectionedSnapshot.Assemble(new[] { Load("chunk_snapshot_delta.json") }, w, listed);   // origin +16 x, section 1 listed
        w.ApplySnapshot(d);
        ed.OnSnapshot(w, new HashSet<Vector3Int>(listed));
        check(ed.Pending.Count == 1 && ed.Pending.IsPending(w, V(4, 5, 3)) && w.Get(4, 5, 3) == 1, "delta: a guess in a copied section moves with the origin and stays pending");
        ed.OnError(w, Parse("{\"type\":\"error\",\"code\":\"edit_denied\",\"message\":\"no\",\"pos\":[20,65,3]}"));
        check(w.Get(4, 5, 3) == 0 && ed.Pending.Count == 0, "optimistic edit, then delta, then error: the moved guess is reverted (no ghost block)");
        ed.Pending.Apply(w, V(4, 6, 3), 1, 10);
        w.SnapshotId = 50; ed.OnSnapshot(w, null);
        check(ed.Pending.Count == 0, "a full snapshot clears the guesses");

        // A delta that lists the section with the guess: the server's data wins, the entry is dropped (no revert later).
        var w2 = new WorldModel();
        w2.ApplySnapshot(SectionedSnapshot.Assemble(new[] { Load("chunk_snapshot_sections.json") }, null, null));
        var ed2 = new BlockEditor { ServerAllows = true };
        ed2.OnSnapshot(w2, null);
        ed2.Pending.Apply(w2, V(20, 5, 3), 1, 0);
        ed2.Pending.Apply(w2, V(5, 10, 3), 1, 0);
        var same = Load("chunk_snapshot_delta.json");
        same["origin"] = new List<object> { 0.0, 60.0, 0.0 };
        same["base"] = new Dictionary<string, object> { ["snapshot_id"] = 8.0, ["shift"] = new List<object> { 0.0, 0.0, 0.0 } };
        var listed2 = new List<Vector3Int>();
        w2.ApplySnapshot(SectionedSnapshot.Assemble(new[] { same }, w2, listed2));
        ed2.OnSnapshot(w2, new HashSet<Vector3Int>(listed2));
        check(ed2.Pending.Count == 1 && ed2.Pending.IsPending(w2, V(5, 10, 3)) && w2.Get(5, 10, 3) == 1 && w2.Get(20, 5, 3) == 0,
              "delta listing a guessed cell's section: the server's value replaces the guess; the other guess stays");
        check(ed2.Pending.Expire(w2, 3.5) == 1 && w2.Get(5, 10, 3) == 0, "a carried guess still expires after 3 s of silence");

        // Hold-repeat with a block new to the palette: nothing is shown, so the cell counts as taken for a second.
        var w3 = PlaceWorld();
        w3.SnapshotId = 1;
        var ed3 = new BlockEditor { ServerAllows = true, Selected = "minecraft:gold_block" };
        var hit = HitOn(w3, V(3, 1, 3), Dir.Up, new Vector3(3.5f, 2, 3.5f));
        var down = new Vector3(0, -1, 0);
        check(ed3.Place(w3, hit, down, 0) != null && w3.Get(3, 2, 3) == 0, "a new block is sent, not shown");
        var again = ed3.Plan(w3, hit, down, 0.35);
        check(!again.Ok && again.Waiting && ed3.Place(w3, hit, down, 0.35) == null && ed3.Result == "waiting for the server", "repeat within a second: not sent again (no warning)");
        check(ed3.Plan(w3, hit, down, 1.05).Ok, "after a second it may be sent again");
        ed3.Place(w3, hit, down, 2);
        var gold = Parse("{\"snapshot_id\":1,\"palette_add\":[\"minecraft:gold_block\"],\"changes\":[[3,2,3,2]]}");
        ed3.OnBlockUpdate(w3, gold); w3.ApplyBlockUpdate(gold);
        check(ed3.Pending.UnansweredCount == 0 && w3.State(w3.Get(3, 2, 3)) == "minecraft:gold_block", "the block_update answers the send");
        ed3.Selected = "minecraft:stone";
        check(ed3.Place(w3, HitOn(w3, V(6, 0, 6), Dir.Up, new Vector3(6.5f, 1, 6.5f)), down, 3) != null && ed3.Pending.UnansweredCount == 0 &&
              ed3.Break(w3, HitOn(w3, V(6, 1, 6), Dir.Up, new Vector3(6.5f, 2, 6.5f)), 3.1) != null,
              "a block shown at once can be broken right away (only unshown sends wait)");
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
        check(Eq(hitSea.Block, V(3, 0, 3)) && msg.S("state") == "minecraft:oak_slab[type=bottom,waterlogged=true]" && w.State(w.Get(3, 1, 3)) == "minecraft:oak_slab[type=bottom,waterlogged=true]",
              "aiming through water at the seabed places into the water (waterlogged, sent and shown)");

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
        var asm = new SnapshotAssembler();   // the app's snapshot path (sections_v1 parts, deltas)
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
                        case "chunk_snapshot":
                            asm.Offer(m, w, new HashSet<Vector3Int>());
                            if (!asm.Busy) break;   // more parts to come
                            var res = asm.Wait();
                            if (res.Error != null) { log.Add("snapshot error " + res.Error); break; }
                            w.ApplySnapshot(res.Snapshot); ed.OnSnapshot(w, res.Delta ? res.ListedKeys : null);
                            break;
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
        // A block that isn't in the palette (ruined portals bring gold blocks): no guess, it arrives with palette_add.
        string fresh = new[] { "minecraft:gold_block", "minecraft:emerald_block", "minecraft:lapis_block", "minecraft:purpur_block" }.First(b => !w.Palette.Contains(b));
        ed.Selected = fresh;
        h = Down(); top = h.Adjacent;
        link.Send(ed.Place(w, h, down, Now())); step++;
        bool guessed = ed.Pending.Count > 0;
        check(Wait(() => w.State(w.Get(top.x, top.y, top.z)) == fresh, "new block placed") && !guessed, $"live edit: place {fresh} (new palette entry, not guessed) at {w.Origin + top}");
        h = Down();
        link.Send(ed.Break(w, h, Now())); step++;
        check(Wait(() => ed.Pending.Count == 0 && w.Get(top.x, top.y, top.z) == w.Palette.IndexOf(BlockEditRules.BreakResult(fresh)), "new block broken"), $"live edit: break {fresh}");
        // A guess the server refuses (command blocks are on the deny list): the error's pos reverts it.
        ed.Pending.Apply(w, top, (ushort)w.Palette.IndexOf(fresh), Now());
        link.Send(Protocol.BlockEdit("place", w.Origin + top, "minecraft:command_block[conditional=false,facing=up]", "up"));
        check(Wait(() => ed.Pending.Count == 0, "denied") && w.State(w.Get(top.x, top.y, top.z)) == "minecraft:air", "live edit: denied place is reverted from the error's pos");
        foreach (var l in log) Console.WriteLine("live edit: " + l);
        link.Dispose();
    }
}
