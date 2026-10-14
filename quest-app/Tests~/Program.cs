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
        var e32 = AreaMath.Estimate(32, 96, 32); var e128 = AreaMath.Estimate(128, 384, 128);
        Check(e32.Quads == 3584 && e32.Vertices == 4 * e32.Quads && e32.Triangles == 2 * e32.Quads, $"estimate 32x96x32 = {e32}");
        Check(AreaMath.Estimate(64, 96, 64).Quads == 4 * e32.Quads, "estimate scales with columns");
        Check(AreaMath.Estimate(32, 384, 32).Quads > e32.Quads * 3, "estimate grows with depth (caves)");
        Check(!AreaMath.OverBudget(e128, AreaMath.DefaultQuadBudget, false), $"128x384x128 {e128} within default budget");
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

        LightAndBiomeTests();

        int measIdx = Array.IndexOf(args, "--measure");
        if (measIdx >= 0)
        {
            // --no-light: drop the snapshot's light/biomes (old look); --flat-light: light without smooth lighting/AO.
            BlockShading.SmoothLighting = Array.IndexOf(args, "--flat-light") < 0;
            Measure(args[measIdx + 1], Array.IndexOf(args, "--no-light") >= 0);
            return 0;
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

    static void LightAndBiomeTests()
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
        string jar = Environment.GetEnvironmentVariable("TABLETOP_CLIENT_JAR")
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
        long baseQuads = AreaMath.Estimate(128, 96, 128).Quads;
        AreaMath.LightFactor = AreaMath.LightFactorFor(true, true);
        long litQuads = AreaMath.Estimate(128, 96, 128).Quads;
        AreaMath.LightFactor = AreaMath.LightFactorFor(false, true);
        Check(litQuads == (long)Math.Ceiling(baseQuads * 1.6) && AreaMath.Estimate(128, 96, 128).Quads == baseQuads && AreaMath.LightFactorFor(true, false) == 1.25,
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

    static void Measure(string dir, bool noLight)
    {
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var w = new WorldModel();
            var snap = ChunkSnapshot.From(Json.Parse(System.IO.File.ReadAllText(f)) as Dictionary<string, object>);
            if (noLight) { snap.Light = null; snap.Biomes = null; }
            IFaceSource faces = new FlatTinted();
            w.ApplySnapshot(snap);
            Console.WriteLine($"MEASURE {System.IO.Path.GetFileName(f)} light={(w.Light != null ? "yes" : "no")} biomes={(w.Biomes != null ? string.Join(",", w.Biomes.Palette) : "no")}");
            var mesh = new MeshData();
            long quads = 0, specials = 0, maxSection = 0;
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
                quads += q; specials += s - q; maxSection = Math.Max(maxSection, s);
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
            }
            var est = AreaMath.Estimate(w.Dims.x, w.Dims.y, w.Dims.z);
            Console.WriteLine($"MEASURE {w.Dims.x}x{w.Dims.y}x{w.Dims.z} sections={n.x * n.y * n.z} quads={quads} specials={specials} verts={(quads + specials) * 4} " +
                              $"tris={(quads + specials) * 2} max_section_quads={maxSection} mesh_ms={sw.Elapsed.TotalMilliseconds:F0} worst_section_ms={worst:F1} estimate={est.Quads}");
        }
    }
}
