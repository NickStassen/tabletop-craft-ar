// Fluids/waterlogging (FluidMesher) tests, and --measure-fluids DIR for saved snapshots.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TabletopCraft.World;
using UnityEngine;

static class WaterTests
{
    const float Src = 8f / 9f;   // vanilla source height

    // Fluid faces get layer 1 (water) / 2 (lava), everything else 0, so the tests can tell quads apart.
    sealed class Tagged : IFaceSource
    {
        public FaceKey Face(string s, int d) => new FaceKey { Layer = s == FluidRules.WaterState ? 1 : s == FluidRules.LavaState ? 2 : 0, Color = BlockRules.FlatColor(s) };
    }

    static WorldModel World(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static MeshData Mesh(WorldModel w)
    {
        var m = new MeshData();
        GreedyMesher.Build(w, new Vector3Int(0, 0, 0), new Tagged(), m);
        GreedyMesher.BuildSpecials(w, new Vector3Int(0, 0, 0), new Tagged(), m);
        return m;
    }

    static List<Vector3[]> Quads(MeshData m, int layer)
    {
        var r = new List<Vector3[]>();
        for (int q = 0; q < m.Verts.Count / 4; q++)
            if ((int)m.Uvs[q * 4].z == layer) r.Add(new[] { m.Verts[q * 4], m.Verts[q * 4 + 1], m.Verts[q * 4 + 2], m.Verts[q * 4 + 3] });
        return r;
    }

    static bool All(Vector3[] q, Func<Vector3, bool> f) => q.All(f);

    public static void Run(Action<bool, string> check, string[] args)
    {
        int mf = Array.IndexOf(args, "--measure-fluids");
        if (mf >= 0 && mf + 1 < args.Length) MeasureFluids(args[mf + 1]);
        const string water = "minecraft:water[level=0]";

        // Water next to kelp (always waterlogged): one water volume over both cells, nothing between them.
        var m = Mesh(World(2, 1, 1, (x, y, z) => x + 1, "minecraft:air", water, "minecraft:kelp[age=3]"));
        var wq = Quads(m, 1);
        check(wq.Count == 6 && !wq.Any(q => All(q, v => v.x == 1)), $"water + kelp: 6 water quads, none between the cells (got {wq.Count})");
        check(Quads(m, 0).Count == 2, "water + kelp: kelp cross still drawn");
        var layers = m.Transparent.Select(i => (int)m.Uvs[i].z).ToList();
        check(layers.Count == 2 * 12 + 6 * 6 && layers.Take(24).All(l => l == 0) && layers.Skip(24).All(l => l == 1),
              "water + kelp: cross then water in the transparent submesh (water blends over the plant)");

        // A lone kelp in open water: only the outer surface of the water (no faces around the kelp cell).
        m = Mesh(World(3, 3, 3, (x, y, z) => x == 1 && y == 1 && z == 1 ? 2 : 1, "minecraft:air", water, "minecraft:kelp_plant"));
        wq = Quads(m, 1);
        bool inner = wq.Any(q => All(q, v => v.x >= 1 && v.x <= 2 && v.y >= 1 && v.y <= 2 && v.z >= 1 && v.z <= 2));
        check(wq.Count == 10 && !inner && Quads(m, 0).Count == 2, $"kelp in 3x3x3 water: cross + 10 merged outer water quads, no inner faces (got {wq.Count})");
        // Winding: (p1-p0)x(p2-p0) points out of the volume, as GreedyMesher emits (the renderer flips after the Z mirror).
        bool outward = wq.All(q =>
        {
            Vector3 a = q[1] - q[0], b = q[2] - q[0];
            var nrm = new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            var c = (q[0] + q[1] + q[2] + q[3]) * 0.25f - new Vector3(1.5f, 1.5f, 1.5f);
            return nrm.x * c.x + nrm.y * c.y + nrm.z * c.z > 0;
        });
        check(outward, "water quads wind counter-clockwise seen from outside");

        // Seagrass, tall seagrass and bubble columns hold water too; waterlogged=true on any block does.
        check(FluidRules.Kind("minecraft:seagrass") == FluidKind.Water && FluidRules.Kind("minecraft:tall_seagrass[half=upper]") == FluidKind.Water &&
              FluidRules.Kind("minecraft:bubble_column[drag=true]") == FluidKind.Water && FluidRules.Kind("minecraft:oak_fence[east=false,north=false,south=false,waterlogged=true,west=false]") == FluidKind.Water &&
              FluidRules.Kind("minecraft:oak_fence[east=false,north=false,south=false,waterlogged=false,west=false]") == FluidKind.None && FluidRules.Kind("minecraft:lava[level=0]") == FluidKind.Lava,
              "fluid rules: kelp/seagrass/bubble_column/waterlogged=true hold water, waterlogged=false doesn't");
        check(FluidRules.Amount(FluidRules.Info("minecraft:water[level=0]")) == 8 && FluidRules.Amount(FluidRules.Info("minecraft:water[level=3]")) == 5 &&
              FluidRules.Amount(FluidRules.Info("minecraft:water[level=9]")) == 8, "fluid rules: source 8, level 3 -> 5, falling -> 8");

        // Surface: a two-high column has a full lower cell and a top at 1 + 8/9, no face between the cells.
        m = Mesh(World(1, 2, 1, (x, y, z) => 1, "minecraft:air", water));
        wq = Quads(m, 1);
        float top = 1f + Src;
        bool lowered = wq.Any(q => All(q, v => v.y == top));
        bool between = wq.Any(q => All(q, v => v.y == 1f));
        float highest = wq.Max(q => q.Max(v => v.y));
        check(lowered && !between && highest == top && wq.Count == 10, $"water surface: top lowered to 1+8/9, none between cells (got {wq.Count} quads, top {highest})");

        // Flowing water slopes toward open air (vanilla corner averaging).
        m = Mesh(World(3, 1, 1, (x, y, z) => x == 2 ? 0 : x + 1, "minecraft:air", water, "minecraft:water[level=1]"));
        var flowTop = Quads(m, 1).FirstOrDefault(q => All(q, v => v.x >= 1 && v.x <= 2 && v.y > 0.1f));
        bool slopes = flowTop != null && flowTop.Where(v => v.x == 1).All(v => v.y > 0.85f && v.y < Src) && flowTop.Where(v => v.x == 2).All(v => v.y > 0.3f && v.y < 0.45f);
        check(slopes, "flowing water[level=1] next to air: top slopes from ~0.88 to ~0.39");

        // Waterlogged blocks hold water. A bottom slab: lowered top and sides, the bottom is the slab's own face.
        m = Mesh(World(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:oak_slab[type=bottom,waterlogged=true]"));
        wq = Quads(m, 1);
        check(wq.Count == 5 && wq.Any(q => All(q, v => v.y == Src)) && !wq.Any(q => All(q, v => v.y == 0)) && Quads(m, 0).Count == 6,
              $"waterlogged bottom slab: slab box + 5 water quads, top 8/9, no bottom (got {wq.Count})");

        // Waterlogged stairs next to water: no face between them; the stairs cell is filled with water unless the
        // stairs still mesh as an opaque cube (then the cube hides it; real stair models come from the shapes work).
        string stairs = "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=true]";
        m = Mesh(World(2, 1, 1, (x, y, z) => x + 1, "minecraft:air", water, stairs));
        wq = Quads(m, 1);
        bool cube = BlockRules.Shape(stairs) == BlockShape.Cube;
        var inStairs = wq.Where(q => All(q, v => v.x >= 1)).ToList();
        bool stairsOk = cube ? inStairs.Count == 0 : inStairs.Count > 0 && !inStairs.Any(q => All(q, v => v.x == 2)) && !inStairs.Any(q => All(q, v => v.y == 0));
        check(FluidRules.Kind(stairs) == FluidKind.Water && !wq.Any(q => All(q, v => v.x == 1)) && stairsOk,
              $"waterlogged stairs: holds water, no face toward it, {(cube ? "hidden inside the cube placeholder" : "water geometry in the cell, back and bottom culled")} ({inStairs.Count} quads in the cell)");

        // Full faces that hide a fluid face (vanilla face occlusion): straight stairs hide their back and bottom/top.
        check(FluidRules.FullFaces(stairs, BlockShape.Air) == (1 << 1 | 1 << 2) && FluidRules.FullFaces("minecraft:oak_stairs[facing=north,half=top,shape=outer_left]", BlockShape.Air) == 1 << 3 &&
              FluidRules.FullFaces("minecraft:stone_slab[type=top]", BlockShape.Slab) == 1 << 3 && FluidRules.FullFaces("minecraft:stone", BlockShape.Cube) == 0x3F,
              "full faces: stairs back + half, slab half, cube all");
        m = Mesh(World(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:oak_slab[type=top,waterlogged=true]"));
        check(Quads(m, 1).Count == 5 && !Quads(m, 1).Any(q => All(q, v => v.y == Src)), "waterlogged top slab: its top hides the water surface, 4 sides + bottom");

        // Water against an opaque block: the block face is drawn (seen through water), the water face isn't.
        m = Mesh(World(2, 1, 1, (x, y, z) => x + 1, "minecraft:air", water, "minecraft:stone"));
        check(Quads(m, 1).Count == 5 && Quads(m, 0).Count == 6, $"water next to stone: 5 water quads, stone keeps 6 faces (got {Quads(m, 1).Count}/{Quads(m, 0).Count})");

        // Lava: opaque submesh, lowered top, nothing transparent.
        m = Mesh(World(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:lava[level=0]"));
        check(Quads(m, 2).Count == 6 && m.Transparent.Count == 0 && m.Opaque.Count == 36, "lava: 6 opaque quads, no transparent ones");

        // A flat ocean merges: 16x4x16 of water under air is 1 top + 1 bottom + 2 per side quads.
        m = Mesh(World(16, 5, 16, (x, y, z) => y < 4 ? 1 : 0, "minecraft:air", water));
        check(Quads(m, 1).Count == 10, $"16x4x16 ocean: greedy-merged to 10 quads (got {Quads(m, 1).Count})");

    }

    /// <summary>--measure-fluids DIR: fluid quads and FluidMesher time per saved chunk_snapshot (tools/measure_regions.py).</summary>
    static void MeasureFluids(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var w = new WorldModel();
            w.ApplySnapshot(TabletopCraft.Net.ChunkSnapshot.From(TabletopCraft.Net.Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>));
            var m = new MeshData();
            var faces = new Tagged();
            var n = w.SectionCount;
            long total = 0, water = 0, fluidSections = 0;
            double fluidMs = 0, worst = 0;
            var sw = new System.Diagnostics.Stopwatch();
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                var s = new Vector3Int(x, y, z);
                GreedyMesher.Build(w, s, faces, m);
                GreedyMesher.BuildSpecials(w, s, faces, m);
                total += m.Verts.Count / 4;
                int wq = Quads(m, 1).Count;
                water += wq;
                m.Clear();
                sw.Restart();
                FluidMesher.Build(w, s, faces, m);
                double ms = sw.Elapsed.TotalMilliseconds;
                fluidMs += ms; worst = Math.Max(worst, ms);
                if (wq > 0) fluidSections++;
                m.Clear();
            }
            Console.WriteLine($"FLUIDS {Path.GetFileNameWithoutExtension(f)} sections={n.x * n.y * n.z} with_water={fluidSections} total_quads={total} water_quads={water} " +
                              $"fluid_mesh_ms={fluidMs:F0} worst_section_ms={worst:F2}");
        }
    }
}
