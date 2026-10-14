// Engine-free tests for the Quest scripts: JSON, RLE, the greedy mesher, the pose frame conversion, and
// (with --live ws://...) a real session against the Paper plugin using the same LinkClient the headset uses.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class Program
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

    /// <summary>Flat colours plus the biome tint kinds a resource pack gives grass, leaves, plants and water (for --measure).</summary>
    sealed class FlatTinted : IFaceSource
    {
        public FaceKey Face(string s, int d)
        {
            var k = new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) };
            string p = BlockRules.Path(s);
            bool tinted = p.EndsWith("leaves") || p == "water" || p == "vine" || p == "short_grass" || p == "tall_grass" || p.EndsWith("fern") ||
                          p == "sugar_cane" || (p == "grass_block" && d == 3);
            if (tinted) k.Tint = BiomeColors.BlockTint(s, out _);
            return k;
        }
    }

    static WorldModel Box(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static string Fixture(string n)
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        { var p = System.IO.Path.Combine(d.FullName, "tools", "fixtures", n); if (System.IO.File.Exists(p)) return p; }
        throw new System.IO.FileNotFoundException(n);
    }

    static int Main(string[] args)
    {
        // JSON round trip
        var o = Json.Parse("{\"a\":[1,2.5,-3e2],\"b\":\"x\\\"y\\u00e9\",\"c\":null,\"d\":true}") as Dictionary<string, object>;
        Check(o.A("a").Count == 3 && (double)o.A("a")[2] == -300 && o.S("b") == "x\"yé" && o["c"] == null && o.B("d"), "json parse");
        Check(Json.Write(new Dictionary<string, object> { ["k"] = new List<object> { 1, 0.5, "s" } }) == "{\"k\":[1,0.5,\"s\"]}", "json write");

        // RLE (pairs index,run)
        var g = ChunkSnapshot.DecodeRle(new List<object> { 1.0, 2.0, 0.0, 1.0 }, 3, 2);
        Check(g.SequenceEqual(new ushort[] { 1, 1, 0 }), "rle decode");
        try { ChunkSnapshot.DecodeRle(new List<object> { 0.0, 2.0 }, 3, 1); Check(false, "rle short total rejected"); } catch (FormatException) { Check(true, "rle short total rejected"); }

        // Mesher: a solid 4x4x4 stone cube = 6 merged quads.
        var m = new MeshData();
        var cube = Box(4, 4, 4, (x, y, z) => 1, "minecraft:air", "minecraft:stone");
        GreedyMesher.Build(cube, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count == 24 && m.Opaque.Count == 36, $"solid cube -> 6 quads (got {m.Verts.Count / 4})");
        // Checkerboard of two block types on a floor: no merging across different keys.
        var chk = Box(4, 1, 4, (x, y, z) => 1 + ((x + z) & 1), "minecraft:air", "minecraft:stone", "minecraft:dirt");
        GreedyMesher.Build(chk, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 16 + 16 + 8 + 8, $"checkerboard floor quads (got {m.Verts.Count / 4})");
        // Hidden faces: two cubes touching produce 10 faces merged into 6 quads (1x2 rectangles).
        var two = Box(2, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:stone");
        GreedyMesher.Build(two, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 6, $"two touching cubes -> 6 quads (got {m.Verts.Count / 4})");
        // Glass next to glass shares no face; air pocket inside stone is skipped (faces inward).
        var glass = Box(2, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:glass");
        GreedyMesher.Build(glass, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 6 && m.Transparent.Count == 36, "glass pair -> 6 transparent quads");
        // Cross placeholder
        var flower = Box(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:poppy");
        GreedyMesher.Build(flower, new Vector3Int(0, 0, 0), new Flat(), m);
        int before = m.Verts.Count;
        GreedyMesher.BuildSpecials(flower, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(before == 0 && m.Verts.Count == 8, "poppy -> cross quads only");

        // TabletopRenderer meshes on worker threads, which fill BlockRules' shape cache on first use. A plain
        // Dictionary there threw NullReferenceException in the first meshing batch on the headset.
        var fresh = Enumerable.Range(0, 20000).Select(i => "minecraft:race_test_" + i).ToArray();
        bool raced = false;
        try { System.Threading.Tasks.Parallel.ForEach(fresh, st => BlockRules.Shape(st)); } catch (Exception) { raced = true; }
        Check(!raced && fresh.All(st => BlockRules.Shape(st) == BlockShape.Cube), "BlockRules.Shape safe under parallel first use");

        // Pose conversion (PROTOCOL.md section 3): Unity identity looks +Z (Unity forward) -> tabletop -Z (north).
        var tp = TabletopPose.FromUnityLocal(new Vector3(0.1f, 0.5f, 0.2f), new Quaternion(0.1f, 0.2f, 0.3f, 0.927f));
        Check(tp.p.x == 0.1f && tp.p.z == -0.2f && tp.q.x == -0.1f && tp.q.y == -0.2f && tp.q.z == 0.3f, "unity -> tabletop frame mapping");

        // Block update with palette_add, and stale snapshot drop
        var wm = Box(2, 2, 2, (x, y, z) => 0, "minecraft:air");
        var upd = Json.Parse("{\"snapshot_id\":1,\"palette_add\":[\"minecraft:torch\"],\"changes\":[[1,1,1,1]]}") as Dictionary<string, object>;
        Check(wm.ApplyBlockUpdate(upd) && wm.State(wm.Get(1, 1, 1)) == "minecraft:torch", "block_update palette_add");
        Check(!wm.ApplyBlockUpdate(Json.Parse("{\"snapshot_id\":9,\"changes\":[]}") as Dictionary<string, object>), "stale block_update dropped");

        // Area setup maths
        Check(Math.Abs(AreaMath.FitBlockSize(1.2f, 0.8f, 64, 64, 1f) - 0.0125f) < 1e-6, "fit: min(tableW/dx, tableD/dz)");
        Check(Math.Abs(AreaMath.FitBlockSize(1.2f, 0.8f, 128, 32, 1f) - 1.2f / 128) < 1e-6, "fit: width-limited");
        Check(AreaMath.FitBlockSize(0.5f, 0.5f, 100000, 100000) == AreaMath.MinBlockM && AreaMath.FitBlockSize(10, 10, 1, 1) == AreaMath.MaxBlockM, "fit: clamped");
        Check(AreaMath.FitBlockSize(0, 1, 16, 16) == 0.01f, "fit: no table -> default");
        var e32 = AreaMath.Estimate(32, 96, 32); var e128 = AreaMath.Estimate(128, 128, 128);
        Check(e32.Quads == 5632 && e32.Vertices == 4 * e32.Quads && e32.Triangles == 2 * e32.Quads, $"estimate 32x96x32 = {e32}");
        Check(AreaMath.Estimate(64, 96, 64).Quads == 4 * e32.Quads, "estimate scales with columns");
        Check(AreaMath.Estimate(32, 384, 32).Quads > e32.Quads * 3, "estimate grows with depth (caves)");
        Check(!AreaMath.OverBudget(e128, AreaMath.DefaultQuadBudget, false) && !AreaMath.OverBudget(AreaMath.Estimate(64, 384, 64), AreaMath.DefaultQuadBudget, false),
              $"128x128x128 {e128} and 64x384x64 within default budget");
        Check(AreaMath.OverBudget(AreaMath.Estimate(128, 384, 128), AreaMath.DefaultQuadBudget, false), $"128x384x128 {AreaMath.Estimate(128, 384, 128)} needs the override (measured ~4x 64x384x64)");
        Check(AreaMath.OverBudget(AreaMath.Estimate(512, 384, 512), AreaMath.DefaultQuadBudget, false), "512x384x512 over budget");
        Check(!AreaMath.OverBudget(AreaMath.Estimate(512, 384, 512), AreaMath.DefaultQuadBudget, true), "override lifts soft cap");
        Check(AreaMath.ClampToCaps(300, 384, 300, 128, 384, 128, 2097152) == (128, 128, 128), "clamp to server caps");

        // Area setup state + protocol builders vs fixtures
        var st = new AreaSetupState();
        st.Begin("world", new Vector3Int(0, -64, 0), new Vector3Int(32, 64, 32));
        Check(st.Center.x == 16 && st.Center.z == 16 && st.Mode == CenterMode.CurrentOrigin, "area: centre on current origin");
        st.Resize(0, 100); st.Resize(2, -100); st.Resize(1, 1);
        Check(st.Size.x == 128 && st.Size.z == 8 && st.Size.y == 80, $"area: resize clamps to caps/min (got {st.Size})");
        var info = RegionInfo.From(Json.Parse(System.IO.File.ReadAllText(Fixture("region_info.json"))) as Dictionary<string, object>);
        Check(info.Players.Count == 1 && info.Limits.MaxX == 128 && info.Dims.x == 32, "region_info fixture parses");
        st.SetInfo(info); st.NextPlayer(1);
        Check(st.Mode == CenterMode.Player && st.Center.x == 12 && st.Center.z == -41, $"area: centre on player (got {st.Center})");
        var rq = st.BuildRequest();
        var fx = Json.Parse(System.IO.File.ReadAllText(Fixture("region_request.json"))) as Dictionary<string, object>;
        Check(rq.S("type") == "region_request" && rq.ContainsKey("center") && rq.ContainsKey("size") && rq.ContainsKey("y_origin") && fx.ContainsKey("center") && fx.ContainsKey("y_origin"), "region_request matches fixture shape");
        st.Size = new Vector3Int(128, 384, 128); st.Limits = new RegionLimits { MaxX = 512, MaxY = 384, MaxZ = 512, MaxBlocks = long.MaxValue }; st.Resize(0, 48); st.Resize(2, 48);
        Check(st.OverBudget && st.Blocker() != null, $"area: soft cap warns ({st.Estimate})");
        st.OverrideBudget = true;
        Check(st.Blocker() == null, "area: override allows confirm");
        var err = RegionLimits.From(Json.Parse(System.IO.File.ReadAllText(Fixture("error_region_too_large.json"))) as Dictionary<string, object>, default);
        Check(err.MaxX == 128 && err.MaxBlocks == 2097152, "region_too_large limits parse");
        Check(Protocol.RegionQuery("Bob").S("player") == "Bob" && !Protocol.RegionQuery().ContainsKey("player"), "region_query builder");

        PlayerTests.Run(Check, Fixture);
        WaterTests.Run(Check, args);   // fluids, waterlogging, texture animation (WaterTests.cs)

        // Block models from a real client jar / resource pack (never committed; MC_CLIENT_JAR or --jar PATH).
        string jar = JarPath(args);
        if (jar != null) ModelTests(jar);
        else Console.WriteLine("SKIP block model tests: set MC_CLIENT_JAR or pass --jar PATH (a client jar or resource pack zip)");

        int sceneIdx = Array.IndexOf(args, "--scene");
        if (sceneIdx >= 0 && jar != null) SceneDump.Run(jar, args[sceneIdx + 1], args);
        if (args.Contains("--list-shapes") && jar != null) SceneDump.ListShapes(jar);

        LightAndBiomeTests(jar);   // lighting, AO, biome colours (colour maps from the jar when given)

        int measIdx = Array.IndexOf(args, "--measure");
        if (measIdx >= 0)
        {
            // --no-light: drop the snapshot's light/biomes (old look); --flat-light: light without smooth lighting/AO.
            BlockShading.SmoothLighting = Array.IndexOf(args, "--flat-light") < 0;
            Measure(args[measIdx + 1], jar, Array.IndexOf(args, "--no-light") >= 0);
            return fails == 0 ? 0 : 1;
        }

        int liveIdx = Array.IndexOf(args, "--live");
        if (liveIdx >= 0) Live(args[liveIdx + 1]);
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }

    static bool Near(float a, float b, float eps = 1e-3f) => Math.Abs(a - b) <= eps;
    static bool Near(Color32 c, int r, int g, int b, int eps = 1) => Math.Abs(c.r - r) <= eps && Math.Abs(c.g - g) <= eps && Math.Abs(c.b - b) <= eps;
    static string Hex(Color32 c) => $"{c.r:X2}{c.g:X2}{c.b:X2}";

    /// <summary>A world with explicit light: f(x, y, z) -> packed sky &lt;&lt; 4 | block.</summary>
    static WorldModel WithLight(WorldModel w, Func<int, int, int, int> f)
    {
        w.Light = new byte[w.Grid.Length];
        for (int y = 0; y < w.Dims.y; y++) for (int z = 0; z < w.Dims.z; z++) for (int x = 0; x < w.Dims.x; x++) w.Light[w.Index(x, y, z)] = (byte)f(x, y, z);
        return w;
    }

    /// <summary>Stone floor at y=0 with stone wall cells at the given (x, y, z); air elsewhere lit by open sky.</summary>
    static WorldModel Floor(int dx, int dy, int dz, params (int x, int y, int z)[] walls)
    {
        var w = Box(dx, dy, dz, (x, y, z) => y == 0 || walls.Contains((x, y, z)) ? 1 : 0, "minecraft:air", "minecraft:stone");
        return WithLight(w, (x, y, z) => w.Grid[w.Index(x, y, z)] == 1 ? 0 : 0xF0);
    }

    /// <summary>Light and biome tints through the real pack models (grass block cube faces, short grass cross, stairs).</summary>
    static void PackLightTests(string jar)
    {
        using var assets = new ZipAssets(jar);
        BlockRules.ResetShapes();
        var w = Box(3, 3, 3, (x, y, z) => y == 0 ? 1 : (x == 1 && y == 1 && z == 1) ? 2 : (x == 0 && y == 1 && z == 0) ? 3 : 0,
                    "minecraft:air", "minecraft:grass_block[snowy=false]", "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]", "minecraft:short_grass");
        WithLight(w, (x, y, z) => y == 0 || (x == 1 && y == 1 && z == 1) ? 0 : 0xF0);
        w.Tints = new BiomeTints(new BiomeGrid { QOrigin = new Vector3Int(0, 0, 0), QDims = new Vector3Int(1, 1, 1), Palette = new[] { "minecraft:cherry_grove" }, Cells = new ushort[1] },
                                 new Vector3Int(0, 0, 0), w.Dims);
        var faces = new PackFaces(assets);
        faces.Prewarm(w);
        var m = new MeshData();
        GreedyMesher.Build(w, new Vector3Int(0, 0, 0), faces, m);
        int greedyVerts = m.Verts.Count;
        bool cherryTop = false;
        for (int i = 0; i < greedyVerts; i += 4)
            if (m.Verts[i].y == 1 && m.Verts[i + 2].y == 1 && (int)m.Uvs[i].z == assets.LayerOf("block/grass_block_top") && Near(m.Colors[i], 0xB6, 0xDB, 0x61, 0)) cherryTop = true;
        Check(cherryTop, "pack: grass_block top (model cube face, tintindex) takes the biome grass colour");
        GreedyMesher.BuildSpecials(w, new Vector3Int(0, 0, 0), faces, m);
        int grassLayer = assets.LayerOf("block/short_grass");
        bool crossTinted = false, stairsShaded = false, stairsLit = false;
        for (int i = greedyVerts; i < m.Verts.Count; i += 4)
        {
            int layer = (int)m.Uvs[i].z;
            if (layer == grassLayer && Near(m.Colors[i], 0xB6, 0xDB, 0x61, 0)) crossTinted = true;
            if (layer == assets.LayerOf("block/oak_planks"))
            {
                for (int k = 0; k < 4; k++) { if (m.Colors[i + k].r < 250) stairsShaded = true; if (m.Colors[i + k].r > 200) stairsLit = true; }
            }
        }
        Check(crossTinted, "pack: short_grass (rotated cross faces) takes the biome grass colour");
        Check(stairsShaded && stairsLit, "pack: oak_stairs model faces get smooth light and AO (some corners darker than others)");
        BlockRules.ResetShapes();
    }

    static void LightAndBiomeTests(string jarArg)
    {
        // Lightmap: 26.x lightmap.fsh with the overworld at noon and brightness 0.5.
        var full = BlockShading.Lightmap(15, 0); var dark = BlockShading.Lightmap(0, 0);
        Check(Near(full.x, 1) && Near(full.y, 1) && Near(full.z, 1), $"lightmap: full daylight is white ({full})");
        Check(dark.x > 0.05f && dark.x < 0.15f && Near(dark.x, dark.z), $"lightmap: level 0 is dark grey, not black ({dark.x:F3})");
        float prev = -1; bool mono = true;
        for (int s = 0; s <= 15; s++) { float l = BlockShading.Lightmap(s, 0).x; if (l < prev) mono = false; prev = l; }
        Check(mono, "lightmap: brightness rises with sky light");
        var torch = BlockShading.Lightmap(0, 10);
        Check(torch.x > torch.z + 0.05f, $"lightmap: block light is warm (r {torch.x:F2} > b {torch.z:F2})");
        Check(Near(BlockShading.Curve(0.5f), 0.2f) && Near(BlockShading.Curve(1f), 1f) && BlockShading.Curve(0) == 0, "brightness curve: level / (4 - 3 level)");
        var l7 = BlockShading.Lightmap(7, 0); var l8 = BlockShading.Lightmap(8, 0); var l75 = BlockShading.Lightmap(7.5f, 0);
        Check(Near(l75.x, (l7.x + l8.x) / 2), "lightmap: smooth (fractional) levels interpolate between levels");

        // AO on the top face of the middle floor block (1,0,1). Corners: c0 (z-,x-) c1 (z+,x-) c2 (z+,x+) c3 (z-,x+).
        var grey = new FaceKey { Layer = -1, Color = new Color32(200, 200, 200, 255) };
        var q = BlockShading.FaceCorners(Floor(3, 4, 3), grey, 1, 0, 1, 3);
        Check(Near(q.C0, 200, 200, 200) && Near(q.C2, 200, 200, 200), $"AO: open floor in daylight is unshaded ({Hex(q.C0)})");
        q = BlockShading.FaceCorners(Floor(3, 4, 3, (2, 1, 1)), grey, 1, 0, 1, 3);
        Check(Near(q.C0, 200, 200, 200) && Near(q.C1, 200, 200, 200) && Near(q.C2, 160, 160, 160) && Near(q.C3, 160, 160, 160),
              $"AO: wall on +x darkens the two corners next to it to 0.8 ({Hex(q.C0)} {Hex(q.C1)} {Hex(q.C2)} {Hex(q.C3)})");
        q = BlockShading.FaceCorners(Floor(3, 4, 3, (2, 1, 2)), grey, 1, 0, 1, 3);
        Check(Near(q.C2, 160, 160, 160) && Near(q.C0, 200, 200, 200) && Near(q.C1, 200, 200, 200), $"AO: diagonal block shades only its corner ({Hex(q.C2)})");
        q = BlockShading.FaceCorners(Floor(3, 4, 3, (2, 1, 1), (1, 1, 2)), grey, 1, 0, 1, 3);
        Check(Near(q.C2, 120, 120, 120), $"AO: inner corner of 1-high walls = 0.6 (vanilla reads the corner cell above open sides) ({Hex(q.C2)})");
        q = BlockShading.FaceCorners(Floor(3, 4, 3, (2, 1, 1), (1, 1, 2), (2, 2, 1), (1, 2, 2)), grey, 1, 0, 1, 3);
        Check(Near(q.C2, 80, 80, 80), $"AO: inner corner of 2-high walls = 0.4 (corner hidden, side reused) ({Hex(q.C2)})");

        // Smooth light: centre air cell block light 15, its neighbours 13 -> each corner averages (13+13+13+15)/4.
        var cave = WithLight(Box(3, 3, 3, (x, y, z) => y == 0 ? 1 : 0, "minecraft:air", "minecraft:stone"), (x, y, z) => y == 0 ? 0 : x == 1 && y == 1 && z == 1 ? 15 : 13);
        q = BlockShading.FaceCorners(cave, grey, 1, 0, 1, 3);
        var want = BlockShading.Lightmap(0, 13.5f);
        Check(Near(q.C0, (int)(200 * want.x + 0.5f), (int)(200 * want.y + 0.5f), (int)(200 * want.z + 0.5f)), $"smooth light: corner = lightmap(avg of 4 cells) ({Hex(q.C0)})");
        // Dark centre (block light 2): vanilla smoothBlend leaves a zero-light wall cell at 0 -> darker corner.
        var dim = WithLight(Box(3, 3, 3, (x, y, z) => y == 0 || (x == 2 && y == 1 && z == 1) ? 1 : 0, "minecraft:air", "minecraft:stone"), (x, y, z) => y == 0 || (x == 2 && y == 1 && z == 1) ? 0 : 2);
        q = BlockShading.FaceCorners(dim, grey, 1, 0, 1, 3);
        var wantDim = BlockShading.Lightmap(0, 1.5f);
        Check(Near(q.C2, (int)(200 * wantDim.x * 0.8f + 0.5f), (int)(200 * wantDim.y * 0.8f + 0.5f), (int)(200 * wantDim.z * 0.8f + 0.5f)), $"smooth light: dark centre keeps zero neighbours ({Hex(q.C2)})");
        var noLight = Box(3, 3, 3, (x, y, z) => y == 0 ? 1 : 0, "minecraft:air", "minecraft:stone");
        q = BlockShading.FaceCorners(noLight, grey, 1, 0, 1, 3);
        Check(Near(q.C0, 200, 200, 200) && Near(q.C2, 200, 200, 200), "no light data (old server): colours = key colour, no AO");
        var flatTorch = BlockShading.Flat(cave, grey, new Vector3Int(1, 1, 1));
        Check(Near(flatTorch, (int)(200 * BlockShading.Lightmap(0, 15).x + 0.5f), (int)(200 * BlockShading.Lightmap(0, 15).y + 0.5f), (int)(200 * BlockShading.Lightmap(0, 15).z + 0.5f)),
              "flat shading (cross plants) uses the block's own cell");
        var slabTop = BlockShading.Quad(Floor(3, 4, 3, (2, 1, 1)), grey, new Vector3Int(1, 0, 1), 3, new Vector3(1, 1, 1), new Vector3(1, 1, 2), new Vector3(2, 1, 2), new Vector3(2, 1, 1));
        Check(Near(slabTop.C0, 200, 200, 200) && Near(slabTop.C2, 160, 160, 160), "Quad() on a full face matches FaceCorners");
        var halfway = BlockShading.Quad(Floor(3, 4, 3, (2, 1, 1)), grey, new Vector3Int(1, 0, 1), 3, new Vector3(1.5f, 1, 1), new Vector3(1.5f, 1, 2), new Vector3(2, 1, 2), new Vector3(2, 1, 1));
        Check(Near(halfway.C0, 180, 180, 180), $"Quad() interpolates partial faces bilinearly ({Hex(halfway.C0)})");

        // Greedy merging with light: uniform light still merges a 4x4 slab into 6 quads; one dimmer air cell splits it.
        var m = new MeshData();
        var slab = Floor(4, 2, 4);
        GreedyMesher.Build(slab, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 6, $"greedy+light: uniform daylight still merges (got {m.Verts.Count / 4} quads)");
        slab.Light[slab.Index(1, 1, 1)] = 0xA0; // sky 10 above (1,0,1)
        GreedyMesher.Build(slab, new Vector3Int(0, 0, 0), new Flat(), m);
        int quads = m.Verts.Count / 4;
        bool consistent = true;
        for (int i = 0; i < m.Verts.Count; i += 4)
        {
            if (m.Verts[i].y != 1 || m.Verts[i + 1].y != 1 || m.Verts[i + 2].y != 1) continue; // top faces only
            float minX = Math.Min(m.Verts[i].x, m.Verts[i + 2].x), maxX = Math.Max(m.Verts[i].x, m.Verts[i + 2].x);
            float minZ = Math.Min(m.Verts[i].z, m.Verts[i + 2].z), maxZ = Math.Max(m.Verts[i].z, m.Verts[i + 2].z);
            for (int bx = (int)minX; bx < maxX; bx++)
                for (int bz = (int)minZ; bz < maxZ; bz++)
                {
                    // The merged quad's colour at each covered face centre must equal that face's own corners there.
                    var own = BlockShading.FaceCorners(slab, new Flat().Face("minecraft:stone", 3), bx, 0, bz, 3);
                    float fu = (bz + 0.5f - minZ) / (maxZ - minZ), fv = (bx + 0.5f - minX) / (maxX - minX);
                    Color32 c0 = m.Colors[i], c1 = m.Colors[i + 1], c2 = m.Colors[i + 2], c3 = m.Colors[i + 3];
                    float got = c0.g * (1 - fu) * (1 - fv) + c1.g * fu * (1 - fv) + c2.g * fu * fv + c3.g * (1 - fu) * fv;
                    float exp = (own.C0.g + own.C1.g + own.C2.g + own.C3.g) / 4f;
                    if (Math.Abs(got - exp) > 1.01f) consistent = false;
                }
        }
        Check(quads > 6 && quads <= 6 + 16 && consistent, $"greedy+light: a dimmer cell splits merges ({quads} quads), merged quads keep per-face colours");
        GreedyMesher.Build(noLight, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Colors.All(c => c.r == BlockRules.FlatColor("minecraft:stone").r), "greedy without light data: flat colours as before");

        // Swamp noise = vanilla Biome.BIOME_INFO_NOISE (reference values from the 26.3 client classes).
        var refs = new (int x, int z, float v)[] { (0, 0, 0f), (1, 1, 0.09764258f), (10, -7, 0.4662441f), (-100, 250, 0.07138351f), (123, 456, -0.11129549f), (-2000, -3000, -0.55597997f), (777, -31, 0.7262097f) };
        Check(refs.All(r => Math.Abs(SwampNoise.Get(r.x * 0.0225, r.z * 0.0225) - r.v) < 1e-6), "swamp noise matches vanilla");
        var swamp = BiomeColors.Info("minecraft:swamp", null);
        Check(BiomeColors.ColorOf(swamp, TintKind.Grass, 123, 456) == 0x4C763C && BiomeColors.ColorOf(swamp, TintKind.Grass, 10, -7) == 0x6A7039, "swamp grass: two colours by noise");
        Check(BiomeColors.ColorOf(swamp, TintKind.Water, 0, 0) == 0x617B64 && BiomeColors.ColorOf(swamp, TintKind.Foliage, 0, 0) == 0x6A7039, "swamp water + foliage overrides");
        Check(BiomeColors.ColorOf(BiomeColors.Info("minecraft:cherry_grove", null), TintKind.Grass, 0, 0) == 0xB6DB61 &&
              BiomeColors.ColorOf(BiomeColors.Info("minecraft:badlands", null), TintKind.Foliage, 0, 0) == 0x9E814D &&
              BiomeColors.ColorOf(BiomeColors.Info("minecraft:warm_ocean", null), TintKind.Water, 0, 0) == 0x43D5EE, "special biomes: cherry, badlands, warm ocean");
        var custom = BiomeColors.Info("mymod:odd", new[] { 0.95, 0.9 });
        Check(custom.Temperature == 0.95f && custom.Downfall == 0.9f && BiomeColors.Info("minecraft:plains", new[] { 0.1, 0.1 }).Temperature == 0.8f,
              "unknown biome uses the server's climate; vanilla ids use the built-in table");

        // Block tint rules (atlas knowledge from vanilla BlockColors).
        Check(BiomeColors.BlockTint("minecraft:grass_block[snowy=false]", out _) == TintKind.Grass && BiomeColors.BlockTint("minecraft:oak_leaves[distance=1]", out _) == TintKind.Foliage &&
              BiomeColors.BlockTint("minecraft:water[level=0]", out _) == TintKind.Water && BiomeColors.BlockTint("minecraft:leaf_litter", out _) == TintKind.DryFoliage &&
              BiomeColors.BlockTint("minecraft:spruce_leaves", out var sp) == TintKind.None && sp.HasValue && sp.Value.g == 0x99, "block tint kinds (grass, foliage, water, dry foliage, fixed spruce)");

        // Blending: badlands (fixed grass 0x90814D) for x < 8, cherry grove (0xB6DB61) for x >= 8, 5x5 window.
        var g = new BiomeGrid { QOrigin = new Vector3Int(0, 0, 0), QDims = new Vector3Int(4, 1, 4), Palette = new[] { "minecraft:badlands", "minecraft:cherry_grove" }, Cells = new ushort[16] };
        for (int qz = 0; qz < 4; qz++) for (int qx = 0; qx < 4; qx++) g.Cells[qz * 4 + qx] = (ushort)(qx < 2 ? 0 : 1);
        var tints = new BiomeTints(g, new Vector3Int(0, 0, 0), new Vector3Int(16, 4, 16));
        Check(tints.Tint(TintKind.Grass, 1, 0, 5) == 0x90814D && tints.Tint(TintKind.Grass, 14, 0, 5) == 0xB6DB61, "biome blend: pure colour away from borders");
        Check(tints.Tint(TintKind.Grass, 8, 0, 5) == 0xA6B759, $"biome blend: 5x5 average at the border ({tints.Tint(TintKind.Grass, 8, 0, 5):X6})");
        Check(tints.Tint(TintKind.Grass, 8, 3, 5) == 0xA6B759 && tints.Tint(TintKind.Water, 1, 0, 5) == 0x3F76E4 && tints.Tint(TintKind.Water, 8, 0, 5) == 0x519DEA,
              $"biome blend: cached per layer; water blends too ({tints.Tint(TintKind.Water, 8, 0, 5):X6})");

        // Colour maps against the real client jar (skipped if it isn't there).
        string jar = jarArg ?? Environment.GetEnvironmentVariable("TABLETOP_CLIENT_JAR")
                     ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft", "versions", "26.3", "26.3.jar");
        Check(BiomeColors.Lookup(null, 0.8f, 0.4f) == -1, "colormap missing -> -1 (fixed fallback tint)");
        if (System.IO.File.Exists(jar))
        {
            BiomeColors.SetColormaps(Png.FromZip(jar, "assets/minecraft/textures/colormap/grass.png"), Png.FromZip(jar, "assets/minecraft/textures/colormap/foliage.png"),
                                     Png.FromZip(jar, "assets/minecraft/textures/colormap/dry_foliage.png"));
            int G(string b) => BiomeColors.ColorOf(BiomeColors.Info("minecraft:" + b, null), TintKind.Grass, 0, 0);
            int F(string b) => BiomeColors.ColorOf(BiomeColors.Info("minecraft:" + b, null), TintKind.Foliage, 0, 0);
            // Reference values: vanilla ColorMapColorUtil on the same jar's colour maps.
            Check(G("plains") == 0x91BD59 && F("plains") == 0x77AB2F && BiomeColors.ColorOf(BiomeColors.Info("minecraft:plains", null), TintKind.DryFoliage, 0, 0) == 0xA37546,
                  $"colormap (26.3 jar): plains grass {G("plains"):X6} foliage {F("plains"):X6}");
            Check(G("forest") == 0x79C05A && G("jungle") == 0x59C93C && F("jungle") == 0x30BB0B && G("taiga") == 0x86B783 && G("snowy_plains") == 0x80B497 &&
                  G("desert") == 0xBFB755 && G("birch_forest") == 0x88BB66 && G("meadow") == 0x83BB6D && G("sparse_jungle") == 0x64C73F, "colormap (26.3 jar): forest, jungle, taiga, snowy, desert, birch, meadow");
            Check(G("dark_forest") == 0x507A32, $"dark forest grass modifier ({G("dark_forest"):X6})");
            PackLightTests(jar);
        }
        else Console.WriteLine("SKIP colormap tests: no client jar at " + jar + " (set TABLETOP_CLIENT_JAR)");

        // Protocol: optional light + biomes in chunk_snapshot, light in block_update.
        var ex = ChunkSnapshot.From(Json.Parse(System.IO.File.ReadAllText(Fixture("chunk_snapshot_extras.json"))) as Dictionary<string, object>);
        Check(ex.Light != null && ex.Light.Length == 32 && ex.Light[(1 * 4 + 3) * 4 + 2] == 254 && ex.Light[0] == 0 && ex.Light[16] == 240, "chunk_snapshot light parses (rle_v1)");
        Check(ex.Biomes != null && ex.Biomes.Palette[ex.Biomes.At(102, 62, -42)] == "minecraft:plains" && ex.Biomes.Palette[ex.Biomes.At(102, 62, -39)] == "minecraft:swamp" &&
              ex.Biomes.Climate[1][1] == 0.9, "chunk_snapshot biomes parse (4x4x4, world-aligned, climate)");
        var plain = ChunkSnapshot.From(Json.Parse(System.IO.File.ReadAllText(Fixture("chunk_snapshot.json"))) as Dictionary<string, object>);
        Check(plain.Light == null && plain.Biomes == null, "v1 snapshot (light/biomes null) still parses");
        var wl = new WorldModel();
        wl.ApplySnapshot(ex);
        wl.DirtySections.Clear();
        Check(wl.Tints != null && wl.ApplyBlockUpdate(Json.Parse(System.IO.File.ReadAllText(Fixture("block_update_light.json"))) as Dictionary<string, object>) &&
              wl.Light[wl.Index(1, 1, 1)] == 254 && wl.Light[wl.Index(1, 1, 2)] == 253 && wl.State(wl.Get(1, 1, 1)) == "minecraft:torch" && wl.DirtySections.Count == 1,
              "block_update light applies and marks the section dirty");
        wl.DirtySections.Clear();
        Check(wl.ApplyBlockUpdate(Json.Parse("{\"snapshot_id\":7,\"changes\":[],\"light\":[[0,1,0,17]]}") as Dictionary<string, object>) && wl.Light[wl.Index(0, 1, 0)] == 17 && wl.DirtySections.Count == 1,
              "light-only block_update");
        Check(Protocol.Hello("").A("caps").Contains("light_v1") && Protocol.Hello("").A("caps").Contains("biomes_v1"), "hello asks for light_v1 + biomes_v1");
        // Model quads (BlockModelMesher) take their colours from BlockShading.Model.
        var floorWall = Floor(3, 4, 3, (2, 1, 1));
        var topQuad = new BakedQuad { P0 = new Vector3(0, 1, 0), P1 = new Vector3(0, 1, 1), P2 = new Vector3(1, 1, 1), P3 = new Vector3(1, 1, 0), Color = grey.Color, Dir = 3 };
        var mq = BlockShading.Model(floorWall, 1, 0, 1, topQuad);
        Check(Near(mq.C0, 200, 200, 200) && Near(mq.C1, 200, 200, 200) && Near(mq.C2, 160, 160, 160) && Near(mq.C3, 160, 160, 160), "model quad, axis-aligned: smooth light + AO like a cube face");
        var rotQuad = topQuad; rotQuad.Dir = -1;
        var lm15 = BlockShading.Lightmap(0, 15);
        Check(Near(BlockShading.Model(cave, 1, 1, 1, rotQuad).C0, (int)(200 * lm15.x + 0.5f), (int)(200 * lm15.y + 0.5f), (int)(200 * lm15.z + 0.5f)), "model quad, rotated: the block's own light, no AO");
        var cherry = new BiomeGrid { QOrigin = new Vector3Int(0, 0, 0), QDims = new Vector3Int(1, 1, 1), Palette = new[] { "minecraft:cherry_grove" }, Cells = new ushort[1] };
        var tintedWorld = Box(3, 3, 3, (x, y, z) => y == 0 ? 1 : 0, "minecraft:air", "minecraft:stone");
        tintedWorld.Tints = new BiomeTints(cherry, new Vector3Int(0, 0, 0), tintedWorld.Dims);
        var tintedQuad = rotQuad; tintedQuad.Tint = TintKind.Grass; tintedQuad.Color = new Color32(124, 189, 107, 255);
        Check(Near(BlockShading.Model(tintedWorld, 1, 1, 1, tintedQuad).C0, 0xB6, 0xDB, 0x61, 0), "model quad, tinted: biome grass colour (cherry grove), no light data");

        // Fluids: vanilla light (max of the cell and the one above, no AO), per-biome water, merging by colour.
        const string src = "minecraft:water[level=0]";
        var pond = WithLight(Box(4, 3, 4, (x, y, z) => y == 0 ? 1 : y == 1 ? 2 : 0, "minecraft:air", "minecraft:stone", src), (x, y, z) => y == 0 ? 0 : y == 1 ? 0xE0 : 0xF0);
        var fm = new MeshData();
        FluidMesher.Build(pond, new Vector3Int(0, 0, 0), new Flat(), fm);
        var flatWater = BlockRules.FlatColor(FluidRules.WaterState);
        int TopQuads(MeshData md) { int t = 0; for (int i = 0; i < md.Verts.Count; i += 4) if (md.Verts[i].y > 1.5f && md.Verts[i + 1].y > 1.5f && md.Verts[i + 2].y > 1.5f) t++; return t; }
        Check(TopQuads(fm) == 1 && fm.Colors.All(c => c.r == flatWater.r && c.b == flatWater.b && c.a == flatWater.a), $"fluid light: open water lit by the sky above merges into one top quad ({TopQuads(fm)})");
        pond.Light[pond.Index(1, 2, 1)] = 0x80;   // shade above one cell (sky 8 < water's 14)
        FluidMesher.Build(pond, new Vector3Int(0, 0, 0), new Flat(), fm);
        var l14 = BlockShading.Lightmap(14, 0);
        Check(TopQuads(fm) > 1 && fm.Colors.Any(c => Near(c, (int)(flatWater.r * l14.x + 0.5f), (int)(flatWater.g * l14.y + 0.5f), (int)(flatWater.b * l14.z + 0.5f)) && c.a == flatWater.a),
              $"fluid light: a shaded cell takes max(own 14, above 8) and splits the merge ({TopQuads(fm)} top quads)");
        var swampWorld = Box(2, 2, 2, (x, y, z) => 0, "minecraft:air");
        swampWorld.Tints = new BiomeTints(new BiomeGrid { QOrigin = new Vector3Int(0, 0, 0), QDims = new Vector3Int(1, 1, 1), Palette = new[] { "minecraft:swamp" }, Cells = new ushort[1] }, new Vector3Int(0, 0, 0), swampWorld.Dims);
        var wc = FluidMesher.WaterColor(swampWorld, 1, 1, 1, new Color32(63, 118, 228, 200));
        Check(wc.r == 0x61 && wc.g == 0x7B && wc.b == 0x64 && wc.a == 200 && FluidMesher.WaterColor(noLight, 1, 1, 1, new Color32(63, 118, 228, 200)).b == 228,
              "WaterColor: swamp water colour with the atlas alpha; atlas tint without biomes");

        long baseQuads = AreaMath.Estimate(128, 96, 128).Quads;
        AreaMath.LightFactor = AreaMath.LightFactorFor(true, true);
        long litQuads = AreaMath.Estimate(128, 96, 128).Quads;
        AreaMath.LightFactor = AreaMath.LightFactorFor(false, true);
        Check(litQuads == (long)Math.Ceiling(baseQuads * 1.35) && AreaMath.Estimate(128, 96, 128).Quads == baseQuads && AreaMath.LightFactorFor(true, false) == 1.15,
              $"area estimate scales with server light ({baseQuads} -> {litQuads} faces)");
        var corner = Box(32, 32, 32, (x, y, z) => 0, "minecraft:air");
        corner.DirtySections.Clear();
        corner.ApplyBlockUpdate(Json.Parse("{\"snapshot_id\":1,\"changes\":[[15,15,15,0]]}") as Dictionary<string, object>);
        Check(corner.DirtySections.Count == 8, $"a change at a section corner re-meshes all 8 sections around it (AO) (got {corner.DirtySections.Count})");
    }

    static void Live(string url)
    {
        var link = new LinkClient(url, "");
        link.Start();
        var world = new WorldModel();
        var area = new AreaSetupState(); bool requested = false, changed = false; string session = null; Vector3Int resized = default;
        int updates = 0, playerMsgs = 0, posesSent = 0; long rtt = -1; string avatarNote = null; List<object> serverCaps = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var mesh = new MeshData();
        while (DateTime.UtcNow < deadline)
        {
            while (link.Inbound.TryDequeue(out var m))
            {
                switch (m.S("type"))
                {
                    case "welcome": serverCaps = m.A("caps"); session = m.S("session"); area.Limits = RegionLimits.From(m.O("limits"), RegionLimits.Default); link.Send(Protocol.AvatarConfig(true, 0.01f, "", true, "Tabletop Giant")); link.Send(Protocol.Ping(1)); break;
                    case "chunk_snapshot":
                        world.ApplySnapshot(ChunkSnapshot.From(m));
                        if (!requested) { requested = true; area.Begin(world.World, world.Origin, world.Dims); link.Send(Protocol.RegionQuery()); }
                        else if (changed) resized = world.Dims;
                        break;
                    case "region_info":
                        area.SetInfo(RegionInfo.From(m));
                        area.Resize(0, 2); area.Resize(2, 2);   // +16 x/z via the same state the headset uses
                        link.Send(area.BuildRequest());
                        break;
                    case "region_changed": changed = m.S("by_session") == session; break;
                    case "block_update": if (world.ApplyBlockUpdate(m)) updates++; break;
                    case "player_positions": playerMsgs++; break;
                    case "pong": rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)m.D("echo_t"); break;
                    case "avatar_state": avatarNote = $"enabled={m.B("enabled")} scale={m.D("scale")}"; break;
                    case "error": Console.WriteLine("server error " + m.S("code") + " " + m.S("message")); fails++; break;
                }
            }
            if (link.Connected)
            {
                double t = posesSent / 30.0;
                var head = TabletopPose.FromUnityLocal(new Vector3(0.16f + 0.1f * (float)Math.Cos(t), 0.45f, -(0.16f + 0.1f * (float)Math.Sin(t))), new Quaternion(0, 0, 0, 1));
                link.Send(Protocol.Pose(posesSent, head, head, head));
                posesSent++;
            }
            Thread.Sleep(33);
        }
        Check(world.Loaded, $"live: snapshot received dims={world.Dims} palette={world.Palette.Count}");
        if (serverCaps != null && serverCaps.Contains(Protocol.CapLight))
            Check(world.Light != null && world.Biomes != null, $"live: light + biomes ({world.Biomes?.Palette.Length} biomes: {string.Join(",", world.Biomes?.Palette ?? new string[0])})");
        else Console.WriteLine("live: server did not grant light_v1 (old plugin?)");
        int quads = 0;
        var n = world.SectionCount;
        for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++) { GreedyMesher.Build(world, new Vector3Int(x, y, z), new Flat(), mesh); quads += mesh.Verts.Count / 4; }
        Check(quads > 0, $"live: meshed {n.x * n.y * n.z} sections into {quads} quads");
        Check(changed && resized.x == area.Size.x && resized.z == area.Size.z, $"live: area setup resize -> region_changed + snapshot {resized}");
        Check(rtt >= 0, $"live: ping/pong rtt {rtt} ms");
        Check(avatarNote != null, $"live: avatar_state {avatarNote}");
        Console.WriteLine($"live: poses sent {posesSent}, player msgs {playerMsgs}, block updates {updates}");
        link.Dispose();
    }

    /// <summary>Face counts and meshing time per saved snapshot: "flat" = flat-colour keys and placeholder shapes (no
    /// pack), "pack" = the jar's block models and one texture layer per texture, as on the headset with pack.zip.
    /// Snapshots with light/biomes are meshed with them (BlockShading) unless noLight.</summary>
    static void Measure(string dir, string jar, bool noLight)
    {
        using var assets = jar != null ? new ZipAssets(jar) : null;
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var w = new WorldModel();
            var snap = ChunkSnapshot.From(Json.Parse(System.IO.File.ReadAllText(f)) as Dictionary<string, object>);
            if (noLight) { snap.Light = null; snap.Biomes = null; }
            w.ApplySnapshot(snap);
            Console.WriteLine($"MEASURE {System.IO.Path.GetFileName(f)} light={(w.Light == null ? "no" : BlockShading.SmoothLighting ? "smooth" : "flat")} biomes={(w.Biomes != null ? string.Join(",", w.Biomes.Palette) : "no")}");
            foreach (bool pack in assets != null ? new[] { false, true } : new[] { false })
            {
                BlockRules.ResetShapes();
                IFaceSource faces = new FlatTinted();
                var prep = System.Diagnostics.Stopwatch.StartNew();
                if (pack) { var pf = new PackFaces(assets); pf.Prewarm(w); faces = pf; }
                double prepMs = prep.Elapsed.TotalMilliseconds;
                var mesh = new MeshData();
                long quads = 0, specials = 0, maxSection = 0, tris = 0;
                var n = w.SectionCount;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double worst = 0;
                for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
                {
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    GreedyMesher.Build(w, new Vector3Int(x, y, z), faces, mesh);
                    long q = mesh.Verts.Count / 4;
                    GreedyMesher.BuildSpecials(w, new Vector3Int(x, y, z), faces, mesh);
                    long s = mesh.Verts.Count / 4;
                    quads += q; specials += s - q; maxSection = Math.Max(maxSection, s); tris += (mesh.Opaque.Count + mesh.Transparent.Count) / 3;
                    worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
                }
                var est = AreaMath.Estimate(w.Dims.x, w.Dims.y, w.Dims.z);
                Console.WriteLine($"MEASURE {(pack ? "pack" : "flat")} {w.Dims.x}x{w.Dims.y}x{w.Dims.z} sections={n.x * n.y * n.z} quads={quads} specials={specials} total={quads + specials} " +
                                  $"verts={(quads + specials) * 4} tris={tris} max_section_quads={maxSection} mesh_ms={sw.Elapsed.TotalMilliseconds:F0} " +
                                  $"worst_section_ms={worst:F1} prewarm_ms={prepMs:F0} estimate={est.Quads}");
                if (pack) Console.WriteLine("  top model blocks: " + string.Join(", ", TopSpecials(w, faces).Take(8)));
            }
        }
        BlockRules.ResetShapes();
    }

    /// <summary>Which states produce the most non-greedy faces ("name xN=faces").</summary>
    static IEnumerable<string> TopSpecials(WorldModel w, IFaceSource faces)
    {
        var count = new Dictionary<string, (int n, long q)>();
        var one = new MeshData();
        var models = faces as IModelSource;
        for (int y = 0; y < w.Dims.y; y++) for (int z = 0; z < w.Dims.z; z++) for (int x = 0; x < w.Dims.x; x++)
        {
            string st = w.State(w.Get(x, y, z));
            if (BlockRules.Shape(st) != BlockShape.Model) continue;
            one.Clear();
            BlockModelMesher.Emit(one, w, x, y, z, st, models?.Model(st), models);
            string name = BlockRules.Path(st);
            count.TryGetValue(name, out var c);
            count[name] = (c.n + 1, c.q + one.Verts.Count / 4);
        }
        return count.OrderByDescending(kv => kv.Value.q).Select(kv => $"{kv.Key} x{kv.Value.n}={kv.Value.q}");
    }

    static string JarPath(string[] args)
    {
        int i = Array.IndexOf(args, "--jar");
        string p = i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable("MC_CLIENT_JAR");
        return !string.IsNullOrEmpty(p) && System.IO.File.Exists(p) ? p : null;
    }

    static IEnumerable<Vector3> Corners(BakedQuad q) { yield return q.P0; yield return q.P1; yield return q.P2; yield return q.P3; }

    static (Vector3 min, Vector3 max) Bounds(BakedModel m, Func<BakedQuad, bool> where = null)
    {
        var min = new Vector3(1e9f, 1e9f, 1e9f); var max = new Vector3(-1e9f, -1e9f, -1e9f);
        foreach (var q in m.Quads)
            if (where == null || where(q))
                foreach (var p in Corners(q)) for (int a = 0; a < 3; a++) { min[a] = Math.Min(min[a], p[a]); max[a] = Math.Max(max[a], p[a]); }
        return (min, max);
    }

    static bool Near(float a, float b) => Math.Abs(a - b) < 1e-3f;

    static void ModelTests(string jar)
    {
        using var assets = new ZipAssets(jar);
        var baker = new BlockModelBaker(assets);
        BakedModel B(string s) => baker.Bake(s);
        Console.WriteLine("block models from " + System.IO.Path.GetFileName(jar));

        // Full cubes stay greedy: faces per direction with the right texture, tint and UV rotation.
        var stone = B("minecraft:stone");
        Check(stone.FullCube && stone.Opaque && stone.Quads.Length == 6 && stone.CubeFaces.All(f => f.Layer >= 0 && f.Layer == stone.CubeFaces[0].Layer && f.UvMap == 0), "model: stone is an opaque full cube with default UVs");
        var grass = B("minecraft:grass_block[snowy=false]");
        Check(grass.FullCube && grass.Opaque && grass.CubeFaces[3].Color.r == ZipAssets.Tint.r && grass.CubeFaces[2].Layer == assets.LayerOf("block/dirt") &&
              grass.CubeFaces[4].Layer == assets.LayerOf("block/grass_block_side") && grass.CubeFaces[4].Color.r == 255, "model: grass_block = tinted top, dirt bottom, plain side");
        var logY = B("minecraft:oak_log[axis=y]"); var logX = B("minecraft:oak_log[axis=x]");
        int logTop = assets.LayerOf("block/oak_log_top");
        Check(logY.CubeFaces[3].Layer == logTop && logY.CubeFaces[0].Layer != logTop && logX.CubeFaces[0].Layer == logTop && logX.CubeFaces[1].Layer == logTop && logX.CubeFaces[3].Layer != logTop,
              "model: oak_log axis=x has its end faces west/east (variant x/y rotation)");
        Check(logY.CubeFaces.All(f => f.UvMap == 0) && logX.CubeFaces[3].UvMap != 0 && logX.CubeFaces[4].UvMap != 0, $"model: sideways log bark is rotated (UvMap up={logX.CubeFaces[3].UvMap} north={logX.CubeFaces[4].UvMap})");

        // Stairs: 2 elements = 11 faces; facing rotates the step, half=top flips it.
        var stairs = B("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]");
        var (smin, smax) = Bounds(stairs);
        var (tmin, tmax) = Bounds(stairs, q => q.Dir == 3 && Near(q.P0.y, 1));
        Check(!stairs.FullCube && stairs.Quads.Length == 11 && Near(smin.x, 0) && Near(smin.y, 0) && Near(smax.y, 1) && Near(smax.z, 1) && Near(tmin.x, 0.5f) && Near(tmax.x, 1),
              $"model: oak_stairs facing=east = 11 faces, step on the east half (x {tmin.x}..{tmax.x})");
        var stairsN = B("minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]");
        (tmin, tmax) = Bounds(stairsN, q => q.Dir == 3 && Near(q.P0.y, 1));
        Check(Near(tmin.z, 0) && Near(tmax.z, 0.5f) && Near(tmin.x, 0) && Near(tmax.x, 1), $"model: oak_stairs facing=north (y=270) step on the north half (z {tmin.z}..{tmax.z})");
        var stairsTop = B("minecraft:oak_stairs[facing=east,half=top,shape=straight,waterlogged=false]");
        (tmin, tmax) = Bounds(stairsTop, q => q.Dir == 2 && Near(q.P0.y, 0));
        Check(stairsTop.Quads.Length == 11 && Near(tmin.x, 0.5f) && Near(tmax.x, 1), "model: oak_stairs half=top (x=180) step underneath on the east half");
        Check(stairs.Quads.Where(q => q.Dir == 2).All(q => q.Cull == 2) && stairsN.Quads.Count(q => q.Cull == 4) == 2, "model: cullfaces follow the rotation");
        var inner = B("minecraft:oak_stairs[facing=east,half=bottom,shape=inner_left,waterlogged=false]");
        Check(inner.Quads.Length > 11, $"model: inner stairs ({inner.Quads.Length} faces)");

        // Multipart: fences, walls, redstone (OR), panes.
        var post = B("minecraft:oak_fence[east=false,north=false,south=false,waterlogged=false,west=false]");
        var fenceN = B("minecraft:oak_fence[east=false,north=true,south=false,waterlogged=false,west=false]");
        var (fmin, fmax) = Bounds(fenceN);
        Check(post.Quads.Length == 6 && fenceN.Quads.Length == 16 && Near(fmin.z, 0) && Near(fmax.z, 10 / 16f) && fmin.x > 6 / 16f - 1e-3f && fmax.x < 10 / 16f + 1e-3f,
              $"model: oak_fence north=true = post + north arm ({fenceN.Quads.Length} faces, z {fmin.z}..{fmax.z})");
        (fmin, fmax) = Bounds(B("minecraft:oak_fence[east=true,north=false,south=false,waterlogged=false,west=false]"));
        Check(Near(fmax.x, 1) && Near(fmin.x, 6 / 16f) && Near(fmin.z, 6 / 16f), "model: oak_fence east=true arm (part y=90)");
        string Wall(string e, string n, string s, string up, string we) => $"minecraft:cobblestone_wall[east={e},north={n},south={s},up={up},waterlogged=false,west={we}]";
        int wpost = B(Wall("none", "none", "none", "true", "none")).Quads.Length, wlow = B(Wall("low", "none", "none", "false", "none")).Quads.Length, wtall = B(Wall("none", "none", "tall", "false", "none")).Quads.Length;
        var wall = B(Wall("low", "none", "tall", "true", "none"));
        var (wmin, wmax) = Bounds(wall);
        Check(wpost > 0 && wall.Quads.Length == wpost + wlow + wtall && Near(wmax.x, 1) && Near(wmax.z, 1) && Near(wmax.y, 1) && Near(wmin.x, 4 / 16f), $"model: wall = post + low east + tall south ({wall.Quads.Length} faces)");
        string Wire(string e, string n, string s, string we) => $"minecraft:redstone_wire[east={e},north={n},power=0,south={s},west={we}]";
        int dot = B(Wire("none", "none", "none", "none")).Quads.Length, eo = B(Wire("side", "none", "none", "none")).Quads.Length, wo = B(Wire("none", "none", "none", "side")).Quads.Length;
        int no = B(Wire("none", "side", "none", "none")).Quads.Length, ew = B(Wire("side", "none", "none", "side")).Quads.Length, en = B(Wire("side", "side", "none", "none")).Quads.Length;
        int up = B(Wire("up", "side", "none", "none")).Quads.Length;
        Check(dot > 0 && eo > 0 && ew == eo + wo && en == dot + eo + no && up > en, $"model: redstone multipart OR/| (dot {dot}, line {ew}, corner {en}, climbing {up})");
        var lit = B("minecraft:redstone_wire[east=side,north=none,power=15,south=none,west=side]");
        Check(lit.Quads.Any(q => q.Color.r == 255 && q.Color.g < 80 && q.Color.b == 0) && lit.Quads.All(q => !q.Tinted) && B(Wire("none", "none", "none", "none")).Quads.Any(q => q.Color.r == 76),
              "model: redstone colour follows power, not the biome tint");
        var pane = B("minecraft:glass_pane[east=true,north=false,south=false,waterlogged=false,west=true]");
        Check(pane.Quads.Length > 0 && pane.Quads.All(q => q.Translucent), "model: glass_pane is translucent (26.x force_translucent sprite)");
        Check(B("minecraft:red_stained_glass_pane[east=true,north=false,south=false,waterlogged=false,west=true]").Quads.All(q => q.Translucent), "model: stained glass pane is translucent");

        // Rotated elements: wall torch leans away from its wall; cross plants; 26.x x/y/z rotations.
        var torch = B("minecraft:wall_torch[facing=north]");
        var top = torch.Quads.SelectMany(Corners).OrderByDescending(p => p.y).First();
        var bottom = torch.Quads.SelectMany(Corners).OrderBy(p => p.y).First();
        Check(torch.Quads.Length == 6 && torch.Quads.Any(q => q.Dir < 0) && top.z < bottom.z - 0.1f && bottom.z > 0.85f, $"model: wall_torch facing=north leans north off the south wall (top z {top.z:F2}, bottom z {bottom.z:F2})");
        var poppy = B("minecraft:poppy");
        var (pmin, pmax) = Bounds(poppy);
        Check(poppy.Quads.Length == 4 && poppy.Quads.All(q => q.Dir < 0 && q.Cull < 0 && !q.Translucent) && Near(pmin.y, 0) && Near(pmax.y, 1) && pmin.x < 0.06f && pmax.x > 0.94f,
              $"model: poppy cross = 4 diagonal faces across the block ({pmin.x:F3}..{pmax.x:F3})");
        var hanging = B("minecraft:oak_hanging_sign[attached=false,rotation=3,waterlogged=false]");
        Check(hanging.Quads.Length > 0 && hanging.Quads.Any(q => q.Dir < 0), $"model: hanging sign with 26.x x/y/z element rotation ({hanging.Quads.Length} faces)");

        // Heights and block entities.
        var snow3 = B("minecraft:snow[layers=3]");
        var (nmin, nmax) = Bounds(snow3);
        Check(!snow3.FullCube && snow3.Quads.Length == 6 && Near(nmax.y, 6 / 16f) && Near(nmin.y, 0), $"model: snow layers=3 is 6/16 high, inner face skipped ({snow3.Quads.Length} faces)");
        Check(B("minecraft:snow[layers=8]").FullCube, "model: snow layers=8 is a full cube");
        var chest = B("minecraft:chest[facing=north,type=single,waterlogged=false]");
        var (cmin, cmax) = Bounds(chest);
        Check(chest.Quads.Length == 6 && Near(cmin.x, 1 / 16f) && Near(cmax.y, 14 / 16f) && chest.Quads.All(q => (int)q.T0.z == assets.LayerOf("block/oak_planks")), "block entity: chest box from its particle texture");
        (cmin, cmax) = Bounds(B("minecraft:chest[facing=east,type=left,waterlogged=false]"));
        Check(Near(cmax.z, 1) && Near(cmin.z, 1 / 16f), $"block entity: double chest half reaches its partner (east/left: z {cmin.z:F3}..{cmax.z:F3})");
        var banner = B("minecraft:red_banner[rotation=4]");
        var (bmin, bmax) = Bounds(banner);
        Check(banner.Quads.Any(q => (int)q.T0.z == assets.LayerOf("block/red_wool")) && bmax.y > 1.5f, "block entity: banner = pole + red wool flag");
        var skull = B("minecraft:skeleton_skull[powered=false,rotation=0]");
        Check(skull.Quads.Length == 6 && skull.Quads.All(q => (int)q.T0.z < 0), "block entity: skull = flat-coloured box");
        Check(B("minecraft:bell[attachment=floor,facing=north,powered=false]").Quads.Any(q => (int)q.T0.z == assets.LayerOf("block/bell_bottom")), "block entity: bell gets a body under its frame");
        Check(B("minecraft:red_bed[facing=north,occupied=false,part=head]").Quads.Length > 0 && B("minecraft:oak_sign[rotation=5,waterlogged=false]").Quads.Length > 0, "model: 26.x bed and sign models bake");
        Check(B("minecraft:red_shulker_box[facing=up]").FullCube, "block entity: shulker box is a full cube");
        Check(B("minecraft:powder_snow").FullCube && B("minecraft:powder_snow").Opaque && B("minecraft:end_gateway").Opaque && !B("minecraft:spawner").FullCube,
              "model: opaque shells with inner faces stay cubes (powder snow), see-through cages don't (spawner)");

        // Classification + meshing, the way the renderer prewarms the palette on the main thread.
        BlockRules.ResetShapes();
        var pf = new PackFaces(assets);
        string[] st = { "minecraft:air", "minecraft:stone", "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]", "minecraft:glass",
                        "minecraft:oak_leaves[distance=7,persistent=true,waterlogged=false]", "minecraft:copper_grate[waterlogged=false]", "minecraft:water[level=0]",
                        "minecraft:poppy", "minecraft:snow[layers=3]", "minecraft:glass_pane[east=true,north=false,south=false,waterlogged=false,west=true]" };
        foreach (var s in st) pf.Face(s, 0);
        Check(BlockRules.Shape(st[1]) == BlockShape.Cube && BlockRules.Shape(st[2]) == BlockShape.Model && BlockRules.Shape(st[3]) == BlockShape.TransparentCube &&
              BlockRules.Shape(st[4]) == BlockShape.TransparentCube && BlockRules.Shape(st[5]) == BlockShape.TransparentCube && BlockRules.Shape(st[6]) == BlockShape.Fluid &&
              BlockRules.Shape(st[7]) == BlockShape.Model && BlockRules.Shape(st[0]) == BlockShape.Air, "classify with a pack: stone cube; stairs, poppy models; glass, leaves, grate see-through cubes; water fluid");
        var m = new MeshData();
        var w1 = Box(1, 2, 1, (x, y, z) => y == 0 ? 1 : 2, st);
        pf.Prewarm(w1);
        GreedyMesher.Build(w1, new Vector3Int(0, 0, 0), pf, m);
        int greedy = m.Verts.Count / 4;
        GreedyMesher.BuildSpecials(w1, new Vector3Int(0, 0, 0), pf, m);
        Check(greedy == 6 && m.Verts.Count / 4 - greedy == 10 && m.Transparent.Count == 0, $"mesh: stairs on stone = 6 cube faces + 10 stair faces, bottom culled (got {greedy} + {m.Verts.Count / 4 - greedy})");
        var w2 = Box(2, 2, 1, (x, y, z) => y == 0 ? 1 : 8, st);
        m.Clear();
        GreedyMesher.BuildSpecials(w2, new Vector3Int(0, 0, 0), pf, m);
        Check(m.Verts.Count / 4 == 8, $"mesh: two snow layers on stone cull their bottoms and touching sides (got {m.Verts.Count / 4} faces)");
        var w3 = Box(3, 1, 1, (x, y, z) => 9, st);
        m.Clear();
        GreedyMesher.BuildSpecials(w3, new Vector3Int(0, 0, 0), pf, m);
        int paneAlone = pf.Model(st[9]).Quads.Length;
        Check(m.Transparent.Count > 0 && m.Opaque.Count == 0 && m.Verts.Count / 4 < 3 * paneAlone, $"mesh: joined glass panes drop the faces between them ({m.Verts.Count / 4} < {3 * paneAlone})");
        // Greedy faces in vanilla orientation: on the east face of a grass block v grows with y, u with -z.
        var w4 = Box(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:grass_block[snowy=false]");
        pf.Prewarm(w4);
        m.Clear();
        GreedyMesher.Build(w4, new Vector3Int(0, 0, 0), pf, m);
        bool oriented = false;
        for (int i = 0; i < m.Verts.Count; i += 4)
            if (Enumerable.Range(i, 4).All(k => m.Verts[k].x == 1))
                oriented = Enumerable.Range(i, 4).All(k => Near(m.Uvs[k].y - m.Uvs[i].y, m.Verts[k].y - m.Verts[i].y) && Near(m.Uvs[k].x - m.Uvs[i].x, -(m.Verts[k].z - m.Verts[i].z)));
        Check(oriented, "mesh: greedy east face of grass is upright (fringe on top)");
        BlockRules.ResetShapes();
    }
}
