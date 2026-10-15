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
            long quads = 0, verts = 0; int empty = 0, emptyMs = 0;
            var kinds = new Dictionary<string, (int n, double ms)>();
            var sw = new Stopwatch();
            for (int r = 0; r < reps; r++)
                for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
                {
                    var k = new Vector3Int(x, y, z);
                    long t0 = Stopwatch.GetTimestamp();
                    GreedyMesher.Build(w, k, faces, md);
                    long t1 = Stopwatch.GetTimestamp();
                    GreedyMesher.BuildSpecials(w, k, faces, md);
                    long t2 = Stopwatch.GetTimestamp();
                    var sm = SectionMesh.From(md, k);
                    long t3 = Stopwatch.GetTimestamp();
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
}
