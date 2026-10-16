// [mesh] Section mesher profiling and output identity.
//   dotnet run -c Release -- --profile-mesh DIR [--jar JAR] [--hash OUT.txt] [--reps N]
// Meshes every section of every saved snapshot in DIR (chunk_snapshot JSON, one message or sectioned parts) the way a
// TabletopRenderer worker does (GreedyMesher.Build + BuildSpecials + SectionMesh.From) on one thread and prints the
// time per stage and per section kind. With --jar the faces and models come from the client jar (PackFaces, as on the
// headset with pack.zip), else from a cached flat-colour table (like the atlas without a pack: one lookup per state).
// --hash writes one line per section with a hash of the mesher output (vertices, UVs, colours, the three index lists
// and the light codes, in order), so two builds of the mesher can be compared section by section (identical output).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class MeshTests
{
    /// <summary>Flat colours cached per state, as ResourcePackAtlas.Face answers without a pack (one dictionary lookup).
    /// The colour is a stable hash of the state: BlockRules.FlatColor uses string.GetHashCode for unknown blocks, which
    /// .NET randomises per process, so its output can't be compared between two runs.</summary>
    public sealed class CachedFlat : IFaceSource
    {
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, FaceKey> _keys = new System.Collections.Concurrent.ConcurrentDictionary<string, FaceKey>();
        public FaceKey Face(string s, int d)
        {
            if (!_keys.TryGetValue(s, out var k))
            {
                uint h = 2166136261;
                foreach (char ch in BlockRules.Path(s)) { h ^= ch; h *= 16777619; }
                var c = s.Contains("water") ? new Color32(52, 92, 220, 170) : new Color32((byte)(80 + (h & 0x7F)), (byte)(80 + ((h >> 8) & 0x7F)), (byte)(80 + ((h >> 16) & 0x7F)), 255);
                _keys[s] = k = new FaceKey { Layer = -1, Color = c };
            }
            return k;
        }
        public void Prewarm(WorldModel w) { foreach (var s in w.Palette) Face(s, 0); Face(FluidRules.WaterState, 0); Face(FluidRules.LavaState, 0); }
    }

    public static ChunkSnapshot Load(string path)
    {
        var j = Json.Parse(System.IO.File.ReadAllText(path));
        if (j is List<object> parts) return SectionedSnapshot.Assemble(parts.Cast<Dictionary<string, object>>().ToList(), null, null);
        return ChunkSnapshot.From((Dictionary<string, object>)j);
    }

    /// <summary>FNV-1a over the exact bits of a mesher result, in order.</summary>
    public static ulong Hash(MeshData m)
    {
        ulong h = 1469598103934665603UL;
        void Add(uint v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } }
        uint F(float f) => BitConverter.SingleToUInt32Bits(f);
        Add((uint)m.Verts.Count);
        foreach (var v in m.Verts) { Add(F(v.x)); Add(F(v.y)); Add(F(v.z)); }
        foreach (var v in m.Uvs) { Add(F(v.x)); Add(F(v.y)); Add(F(v.z)); }
        foreach (var c in m.Colors) Add((uint)(c.r | c.g << 8 | c.b << 16 | c.a << 24));
        Add((uint)m.Opaque.Count); foreach (var i in m.Opaque) Add((uint)i);
        Add((uint)m.Transparent.Count); foreach (var i in m.Transparent) Add((uint)i);
        Add((uint)m.WaterBack.Count); foreach (var i in m.WaterBack) Add((uint)i);
        Add((uint)m.Light.Count); foreach (var l in m.Light) Add(l);   // [living] light codes
        return h;
    }

    public static void Profile(string dir, string jar, string hashOut, int reps, bool noLight = false)
    {
        using var assets = jar != null ? new ZipAssets(jar) : null;
        var hashes = hashOut != null ? new System.IO.StreamWriter(hashOut) : null;
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var snap = Load(f);
            if (noLight) { snap.Light = null; snap.Biomes = null; }
            var w = new WorldModel();
            w.ApplySnapshot(snap);
            w.DirtySections.Clear();
            BlockRules.ResetShapes();
            IFaceSource faces;
            if (assets != null) { var pf = new PackFaces(assets); pf.Prewarm(w); pf.Face(FluidRules.WaterState, 0); pf.Face(FluidRules.LavaState, 0); faces = pf; }
            else { var cf = new CachedFlat(); cf.Prewarm(w); faces = cf; }
            var n = w.SectionCount;
            var md = new MeshData();
            // Warm up the JIT and the caches (tints, shapes) once, then time `reps` passes.
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            { var k = new Vector3Int(x, y, z); GreedyMesher.Build(w, k, faces, md); GreedyMesher.BuildSpecials(w, k, faces, md); SectionMesh.From(md, k); }
            double tBuild = 0, tSpecial = 0, tFrom = 0, worst = 0;
            long aBuild = 0, aSpecial = 0, aFrom = 0;
            long quads = 0, verts = 0; int empty = 0, emptyMs = 0;
            var kinds = new Dictionary<string, (int n, double ms)>();
            var sw = new Stopwatch();
            for (int r = 0; r < reps; r++)
                for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
                {
                    var k = new Vector3Int(x, y, z);
                    long a0 = GC.GetAllocatedBytesForCurrentThread();   // [smooth] garbage per stage (the headset's GC pauses)
                    long t0 = Stopwatch.GetTimestamp();
                    GreedyMesher.Build(w, k, faces, md);
                    long t1 = Stopwatch.GetTimestamp();
                    long a1 = GC.GetAllocatedBytesForCurrentThread();
                    GreedyMesher.BuildSpecials(w, k, faces, md);
                    long t2 = Stopwatch.GetTimestamp();
                    long a2 = GC.GetAllocatedBytesForCurrentThread();
                    var sm = SectionMesh.From(md, k);
                    long t3 = Stopwatch.GetTimestamp();
                    long a3 = GC.GetAllocatedBytesForCurrentThread();
                    aBuild += a1 - a0; aSpecial += a2 - a1; aFrom += a3 - a2;
                    double b = Ms(t1 - t0), s = Ms(t2 - t1), fr = Ms(t3 - t2);
                    tBuild += b; tSpecial += s; tFrom += fr; worst = Math.Max(worst, b + s + fr);
                    if (r == 0)
                    {
                        quads += sm?.Quads ?? 0; verts += md.Verts.Count;
                        string kind = Kind(w, k);
                        kinds.TryGetValue(kind, out var kv); kinds[kind] = (kv.n + 1, kv.ms + b + s + fr);
                        if (sm == null) empty++;
                        hashes?.WriteLine($"{System.IO.Path.GetFileName(f)} {x},{y},{z} {md.Verts.Count} {Hash(md):x16}");
                    }
                }
            int secs = n.x * n.y * n.z;
            double total = tBuild + tSpecial + tFrom;
            // The fluid pass alone (BuildSpecials runs it last), timed separately.
            double tFluid = 0;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                md.Clear(); long t0 = Stopwatch.GetTimestamp();
                FluidMesher.Build(w, new Vector3Int(x, y, z), faces, md);
                tFluid += Ms(Stopwatch.GetTimestamp() - t0);
            }
            Console.WriteLine($"PROFILE {System.IO.Path.GetFileName(f)} {w.Dims.x}x{w.Dims.y}x{w.Dims.z} pack={(assets != null ? "yes" : "no")} light={(w.Light != null ? "yes" : "no")} " +
                              $"sections={secs} empty_out={empty} quads={quads} verts={verts} palette={w.Palette.Count}");
            Console.WriteLine($"  per section (mean of {reps}x{secs}): total={total / reps / secs:F3} ms  build={tBuild / reps / secs:F3}  specials+fluid={tSpecial / reps / secs:F3} " +
                              $"(fluid alone {tFluid / secs:F3})  from={tFrom / reps / secs:F3}  worst={worst:F2} ms");
            Console.WriteLine($"  allocated per section: build={aBuild / reps / secs / 1024.0:F1} KB  specials+fluid={aSpecial / reps / secs / 1024.0:F1} KB  from={aFrom / reps / secs / 1024.0:F1} KB");
            foreach (var kv in kinds.OrderByDescending(k => k.Value.ms))
                Console.WriteLine($"  kind {kv.Key,-12} n={kv.Value.n,5}  ms/section={kv.Value.ms / kv.Value.n:F3}  share={kv.Value.ms / (total / reps) * 100:F0}%");
        }
        hashes?.Dispose();
        BlockRules.ResetShapes();
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>What a section holds: air (all air), solid (all opaque cubes), surface (both), or other.</summary>
    static string Kind(WorldModel w, Vector3Int s)
    {
        int S = WorldModel.Section, air = 0, cube = 0, other = 0;
        for (int y = s.y * S; y < Math.Min(w.Dims.y, s.y * S + S); y++)
            for (int z = s.z * S; z < Math.Min(w.Dims.z, s.z * S + S); z++)
                for (int x = s.x * S; x < Math.Min(w.Dims.x, s.x * S + S); x++)
                {
                    var sh = w.ShapeOf(w.Grid[w.Index(x, y, z)]);
                    if (sh == BlockShape.Air) air++; else if (sh == BlockShape.Cube) cube++; else other++;
                }
        if (cube == 0 && other == 0) return "air";
        if (air == 0 && other == 0) return "solid";
        if (other == 0) return "air+cube";
        return "mixed";
    }

    // ---------- unit tests (part of the default suite) ----------

    sealed class Keys : IFaceSource
    {
        public FaceKey Face(string s, int d) => new FaceKey { Layer = s.Contains("grass") ? 3 : s.Contains("stone") ? 1 : 2, Color = BlockRules.FlatColor(s), Tint = s.Contains("grass_block") ? TintKind.Grass : TintKind.None };
    }

    static WorldModel World(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    public static void Run(Action<bool, string> check)
    {
        BlockRules.ResetShapes();
        // ShadeCache: the cached cells light every face exactly like the world (random shapes and light, a region that
        // isn't a multiple of 16, faces at every section border and the region edges, cubic and inner faces, floors).
        var rnd = new Random(7);
        string[] pal = { "minecraft:air", "minecraft:stone", "minecraft:glass", "minecraft:oak_leaves", "minecraft:short_grass", "minecraft:water", "minecraft:oak_slab[type=bottom]" };
        var w = World(37, 21, 29, (x, y, z) => rnd.Next(10) < 4 ? 0 : rnd.Next(1, pal.Length), pal);
        w.Light = new byte[w.Grid.Length];
        for (int i = 0; i < w.Light.Length; i++) w.Light[i] = (byte)(rnd.Next(16) << 4 | rnd.Next(16));
        var cache = new ShadeCache();
        int faces = 0, same = 0;
        bool smooth = BlockShading.SmoothLighting;
        foreach (bool sm in new[] { true, false })
        {
            BlockShading.SmoothLighting = sm;
            for (int sx = 0; sx < 3; sx++) for (int sy = 0; sy < 2; sy++) for (int sz = 0; sz < 2; sz++)
            {
                cache.Bind(w, new Vector3Int(sx * 16, sy * 16, sz * 16));
                for (int n = 0; n < 300; n++)
                {
                    int x = Math.Min(w.Dims.x - 1, sx * 16 + rnd.Next(16)), y = Math.Min(w.Dims.y - 1, sy * 16 + rnd.Next(16)), z = Math.Min(w.Dims.z - 1, sz * 16 + rnd.Next(16));
                    int dir = rnd.Next(6), floor = rnd.Next(4) == 0 ? rnd.Next(16) : 0; bool cubic = rnd.Next(4) != 0;
                    var key = new FaceKey { Color = new Color32(200, 180, 160, 255), Shade = (byte)rnd.Next(3) };
                    var a = BlockShading.FaceCorners(cache, w, key, x, y, z, dir, cubic, floor);
                    var b = BlockShading.FaceCorners(w, key, x, y, z, dir, cubic, floor);
                    faces++;
                    if (Eq(a, b)) same++;
                }
            }
        }
        BlockShading.SmoothLighting = smooth;
        check(faces > 0 && same == faces, $"mesh: ShadeCache lights {same}/{faces} random faces exactly like the world");
        cache.Bind(w, new Vector3Int(0, 0, 0));
        var far = BlockShading.FaceCorners(cache, w, new FaceKey { Color = new Color32(255, 255, 255, 255) }, 30, 5, 5, 1);   // outside the bound section: world path
        check(Eq(far, BlockShading.FaceCorners(w, new FaceKey { Color = new Color32(255, 255, 255, 255) }, 30, 5, 5, 1)), "mesh: a face outside the bound section is lit from the world");

        // Greedy pass: a section without cubes returns nothing (the plants and water are BuildSpecials'); a section with a
        // palette id past the palette meshes it as air (as State() does); a partial edge section stops at the region.
        var md = new MeshData();
        var plants = World(16, 16, 16, (x, y, z) => y == 0 ? 1 : y == 1 ? 2 : 0, "minecraft:air", "minecraft:water", "minecraft:short_grass");
        GreedyMesher.Build(plants, new Vector3Int(0, 0, 0), new Keys(), md);
        check(md.Verts.Count == 0, "mesh: a section without cubes has no greedy faces");
        var odd = World(20, 5, 3, (x, y, z) => y == 0 ? 1 : x == 7 && y == 1 ? 9 : 0, "minecraft:air", "minecraft:stone");
        GreedyMesher.Build(odd, new Vector3Int(1, 0, 0), new Keys(), md);
        int q = md.Verts.Count / 4;
        float maxX = 0; foreach (var v in md.Verts) maxX = Math.Max(maxX, v.x);
        check(q == 5 && maxX == 20, $"mesh: a 4-wide edge section of a stone floor: 5 quads up to the region edge, none toward the stone next door (got {q}, max x {maxX})");
        GreedyMesher.Build(odd, new Vector3Int(0, 0, 0), new Keys(), md);
        check(md.Verts.Count / 4 == 5, $"mesh: an unknown palette id meshes as air (got {md.Verts.Count / 4} quads)");
        bool inner = GreedyMesher.LeavesInner;
        var leaves = World(2, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:oak_leaves");
        GreedyMesher.LeavesInner = true; GreedyMesher.Build(leaves, new Vector3Int(0, 0, 0), new Keys(), md); int with = md.Verts.Count / 4;
        GreedyMesher.LeavesInner = false; GreedyMesher.Build(leaves, new Vector3Int(0, 0, 0), new Keys(), md); int without = md.Verts.Count / 4;
        GreedyMesher.LeavesInner = inner;
        check(with == 8 && without == 6, $"mesh: leaves_inner draws the two faces between two leaves (8 merged quads) or culls them like glass (6) (got {with}, {without})");

        // Job size: about JobFrames of work at the measured cost, between the minimum and MaxSectionsPerJob.
        check(MeshScope.SectionsPerJob(0, 14f, 4) == 4 && MeshScope.SectionsPerJob(1f, 0, 4) == 4, "mesh: unknown cost -> the minimum job");
        check(MeshScope.SectionsPerJob(0.6f, 13.9f, 4) == 35 && MeshScope.SectionsPerJob(0.1f, 13.9f, 4) == MeshScope.MaxSectionsPerJob && MeshScope.SectionsPerJob(20f, 13.9f, 4) == 4,
              $"mesh: 1.5 frames of work per job (0.6 ms -> {MeshScope.SectionsPerJob(0.6f, 13.9f, 4)}, cheap -> 64, slow -> 4)");
        check(MeshScope.SectionsPerJob(0.6f, 13.9f, 80) == 80, "mesh: mesh_sections_per_job above the maximum wins");
        var meter = new MeshWorkMeter();
        meter.Frame(); bool zero = meter.MsPerSection == 0;
        long tick = System.Diagnostics.Stopwatch.Frequency / 1000;   // 1 ms
        meter.Add(tick); meter.Add(3 * tick); meter.Frame();
        float first = meter.MsPerSection;
        meter.Add(4 * tick); meter.Frame();
        check(zero && Math.Abs(first - 2f) < 1e-3f && Math.Abs(meter.MsPerSection - (0.8f * 2f + 0.2f * 4f)) < 1e-3f, $"mesh: worker time per section, EMA ({first:F2}, {meter.MsPerSection:F2} ms)");
        float skirtWas = MeshScope.SkirtFactor;
        var act = new SectionBox { Min = new Vector3Int(0, 0, 0), Max = new Vector3Int(10, 4, 10) };
        float cap1 = MeshScope.MeshSpeedCap(1000, act, new Vector3(1, 0, 0));
        MeshScope.SkirtFactor = 2f; float cap2 = MeshScope.MeshSpeedCap(1000, act, new Vector3(1, 0, 0)); MeshScope.SkirtFactor = skirtWas;
        check(Math.Abs(cap2 - 2 * cap1) < 1e-3f, $"mesh: the skirt lets a glide go skirt_speed times past the mesh cap ({cap1:F0} -> {cap2:F0} blocks/s)");

        // Batches off screen wait for their columns ahead; batches in view don't.
        var lay = new BatchLayout(new Vector3Int(0, 0, 0), new Vector3Int(160, 64, 160), 2);
        var vis = new SectionBox { Min = new Vector3Int(2, 0, 2), Max = new Vector3Int(8, 4, 8) };
        check(!BatchRules.OffScreen(lay, lay.KeyOf(new Vector3Int(2, 0, 2)), vis) && !BatchRules.OffScreen(lay, lay.KeyOf(new Vector3Int(7, 0, 7)), vis)
              && BatchRules.OffScreen(lay, lay.KeyOf(new Vector3Int(8, 0, 4)), vis) && BatchRules.OffScreen(lay, lay.KeyOf(new Vector3Int(0, 0, 4)), vis)
              && !BatchRules.OffScreen(lay, lay.KeyOf(new Vector3Int(0, 0, 0)), SectionBox.All), "mesh: batch off screen (x/z outside the visible box; never with no box)");

        SkirtTests(check);
        BlockRules.ResetShapes();
    }

    static bool Eq(Quad4 a, Quad4 b) => E(a.C0, b.C0) && E(a.C1, b.C1) && E(a.C2, b.C2) && E(a.C3, b.C3) && a.L0 == b.L0 && a.L1 == b.L1 && a.L2 == b.L2 && a.L3 == b.L3;
    static bool E(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    static void SkirtTests(Action<bool, string> check)
    {
        var faces = new Keys();
        var md = new MeshData();
        // A flat stone floor at y = 5 (top face at 6), grass plants on it, under open sky: one merged top face per tile, a
        // hair below the real surface, no sides (same heights, nothing toward the region edge).
        var flat = World(32, 20, 32, (x, y, z) => y <= 5 ? 1 : y == 6 && (x + z) % 3 == 0 ? 2 : 0, "minecraft:air", "minecraft:stone", "minecraft:short_grass");
        flat.Light = new byte[flat.Grid.Length]; for (int i = 0; i < flat.Light.Length; i++) flat.Light[i] = 15 << 4;
        SurfaceSkirt.Build(flat, 1, 0, 0, 20, faces, md, out int lo, out int hi);
        float y0 = md.Verts.Count > 0 ? md.Verts[0].y : -1;
        check(md.Verts.Count == 4 && lo == 5 && hi == 5 && Math.Abs(y0 - (6 - SurfaceSkirt.Inset)) < 1e-5f && md.Uvs[0].z == 1 && md.Light.Count == 4 && md.Light[0] == LightCode.Of(15, 0),
              $"skirt: a flat floor under plants is one top face just below the surface, stone texture, sky-lit (got {md.Verts.Count / 4} quads at y {y0})");
        var sm = SectionMesh.From(md, new Vector3Int(1, 0, 0));
        check(sm != null && sm.Verts[0].Ny == 127 && sm.Opaque == 6, "skirt: the tile's top face points up in Unity space, opaque submesh");
        // A pillar 3 blocks higher: its top and 4 step sides (from the floor's surface up to its own).
        var pillar = World(16, 20, 16, (x, y, z) => y <= 5 || (x == 8 && z == 8 && y <= 8) ? 1 : 0, "minecraft:air", "minecraft:stone");
        SurfaceSkirt.Build(pillar, 0, 0, 0, 20, faces, md, out lo, out hi);
        int sides = 0; float top = 0, bottom = 99;
        for (int i = 0; i < md.Verts.Count; i += 4)
        {
            var a = md.Verts[i]; var c = md.Verts[i + 2];
            if (Math.Abs(a.y - c.y) > 0.5f) { sides++; top = Math.Max(top, Math.Max(a.y, c.y)); bottom = Math.Min(bottom, Math.Min(a.y, c.y)); }
        }
        check(sides == 4 && lo == 5 && hi == 8 && Math.Abs(bottom - 6) < 1e-5f && Math.Abs(top - (9 - SurfaceSkirt.Inset)) < 1e-5f,
              $"skirt: a pillar gets 4 step sides from the floor's surface to its own (got {sides} sides, y {bottom}..{top}, tops {lo}..{hi})");
        // The window top cuts the pillar: the surface is what shows below it; a window above nothing gives no faces.
        SurfaceSkirt.Build(pillar, 0, 0, 0, 7, faces, md, out lo, out hi);
        check(lo == 5 && hi == 6 && md.Verts.Count > 0, $"skirt: below a window top at 7 the pillar's top is its block at 6 (got {lo}..{hi})");
        SurfaceSkirt.Build(pillar, 0, 0, 10, 20, faces, md, out lo, out hi);
        check(lo == -1 && md.Verts.Count == 0, "skirt: nothing in the window's height range, no faces");
        SurfaceSkirt.SurfaceRange(pillar, 0, 0, 0, 20, out int rlo, out int rhi);
        check(rlo == 5 && rhi == 8, "skirt: SurfaceRange matches the built tile");
        // Biome tint and light come from the cell above the top block (BlockShading.Flat).
        var grass = World(16, 8, 16, (x, y, z) => y <= 2 ? 1 : 0, "minecraft:air", "minecraft:grass_block[snowy=false]");
        grass.Light = new byte[grass.Grid.Length]; for (int i = 0; i < grass.Light.Length; i++) grass.Light[i] = (byte)(8 << 4 | 3);
        SurfaceSkirt.Build(grass, 0, 0, 0, 8, faces, md, out _, out _);
        var want = BlockShading.Flat(grass, faces.Face("minecraft:grass_block[snowy=false]", 3), new Vector3Int(0, 3, 0), out byte code);
        check(md.Colors.Count == 4 && E(md.Colors[0], want) && md.Light[0] == code && md.Uvs[0].z == 3, "skirt: top colour = tint x light of the cell above, with its light code");

        // Planner: a tile per box column with a pending section and nothing of it in the window on screen; [smooth] dropped
        // as soon as any of its window sections is on screen, and not planned again while that holds (no toggling).
        var p = new SkirtPlanner(); var pend = new SkirtPlanner.Pending(); var build = new List<Vector2Int>();
        var box = new SectionBox { Min = new Vector3Int(0, 0, 0), Max = new Vector3Int(4, 4, 4) };
        var pending = new HashSet<Vector3Int> { new Vector3Int(1, 2, 1), new Vector3Int(3, 0, 2), new Vector3Int(9, 1, 1) };
        var shown = new HashSet<Vector3Int>();
        void Frame(int max) { pend.Reset(box, new Vector3Int(10, 6, 10), 1, 3); foreach (var s in pending) pend.Mark(s); foreach (var s in shown) pend.MarkShown(s); p.Frame(pend, max, build); }
        Frame(8);
        check(build.Count == 2 && build.Contains(new Vector2Int(1, 1)) && build.Contains(new Vector2Int(3, 2)), $"skirt plan: a tile per box column with a pending section ({build.Count})");
        Frame(8);
        check(build.Count == 0 && p.Tiles.Count == 2, "skirt plan: tiles being built aren't asked for again");
        p.Built(new Vector2Int(1, 1), 30, 33);   // surface in section 1, things standing on it in section 2
        p.Built(new Vector2Int(3, 2), -1, -1);   // no surface below the window top
        Frame(8);
        check(p.Tiles.Count == 2 && p.Dropped.Count == 0, "skirt plan: built tiles stay while their column is pending and nothing of it is on screen");
        shown.Add(new Vector3Int(1, 0, 1));   // below the window's rows (1..2)
        Frame(8);
        check(p.Tiles.ContainsKey(new Vector2Int(1, 1)) && p.Dropped.Count == 0, "skirt plan: a section on screen below the window doesn't drop the tile");
        shown.Add(new Vector3Int(1, 1, 1));
        Frame(8);
        check(!p.Tiles.ContainsKey(new Vector2Int(1, 1)) && p.Dropped.Count == 1 && build.Count == 0, "skirt plan: a tile goes as soon as any section of its column in the window is on screen");
        Frame(8);
        check(build.Count == 0 && !p.Tiles.ContainsKey(new Vector2Int(1, 1)), "skirt plan: ...and doesn't come back while its column is still pending (no flicker)");
        pending.Remove(new Vector3Int(3, 0, 2));
        Frame(8);
        check(!p.Tiles.ContainsKey(new Vector2Int(3, 2)) && p.Dropped.Count == 1, "skirt plan: a tile goes when nothing of its column is pending");
        shown.Clear(); pending.Add(new Vector3Int(3, 0, 2));
        Frame(8);
        check(build.Count == 2 && p.Tiles.Count == 2, "skirt plan: columns that are wholly pending again get tiles again");
        p.Shift(new Vector3Int(1, 0, 0), new Vector3Int(10, 6, 10));
        check(p.Tiles.ContainsKey(new Vector2Int(2, 2)) && p.Tiles.ContainsKey(new Vector2Int(0, 1)) && p.Dropped.Count == 0, "skirt plan: a shift re-keys tiles (new key = old - shift)");
        p.Shift(new Vector3Int(1, 0, 0), new Vector3Int(10, 6, 10));
        check(p.Dropped.Count == 1 && p.Tiles.Count == 1, "skirt plan: tiles that leave the region go");
        box = new SectionBox { Min = new Vector3Int(5, 0, 5), Max = new Vector3Int(8, 4, 8) };
        Frame(8);
        check(p.Tiles.Count == 0 && p.Dropped.Count == 1, "skirt plan: tiles outside the box go");
        Frame(0);
        check(build.Count == 0, "skirt plan: no builds while a job is running (maxBuild 0)");
    }
}
