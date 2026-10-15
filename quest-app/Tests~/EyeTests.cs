// Eye-in-fluid tests: medium detection at the eye (EyeMediumRules), the hysteresis/fade filter (EyeMediumFilter), and
// the water's inside faces (FluidMesher -> MeshData.WaterBack).
using System;
using System.Linq;
using TabletopCraft.World;
using UnityEngine;

static class EyeTests
{
    sealed class Tagged : IFaceSource
    {
        public FaceKey Face(string s, int d) => new FaceKey { Layer = s == FluidRules.WaterState || s == FluidRules.WaterFlowState || s == FluidRules.OverlayState ? 1 : s == FluidRules.LavaState || s == FluidRules.LavaFlowState ? 2 : 0, Color = BlockRules.FlatColor(s) };
    }

    static WorldModel World(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static EyeMedium At(WorldModel w, float x, float y, float z) => EyeMediumRules.At(w, new Vector3(x, y, z));

    public static void Run(Action<bool, string> check)
    {
        const string water = "minecraft:water[level=0]";
        float src = 8f / 9f;

        // Two-high water column: the lower cell is full (water above), the upper one ends at 1 + 8/9.
        var col = World(1, 2, 1, (x, y, z) => 1, "minecraft:air", water);
        check(At(col, 0.5f, 0.95f, 0.5f) == EyeMedium.Water && At(col, 0.5f, 1.5f, 0.5f) == EyeMedium.Water && At(col, 0.5f, 1.95f, 0.5f) == EyeMedium.Air,
              "eye: lower cell full height under more water, surface cell ends at 1+8/9");
        check(At(col, 0.5f, 1f + src, 0.5f) == EyeMedium.Air && At(col, 0.5f, 1f + src - 1e-4f, 0.5f) == EyeMedium.Water,
              "eye: exactly at the surface is air (vanilla <), just below is water");
        var one = World(1, 1, 1, (x, y, z) => 1, "minecraft:air", water);
        check(At(one, 0.5f, 0.85f, 0.5f) == EyeMedium.Water && At(one, 0.5f, 0.95f, 0.5f) == EyeMedium.Air, "eye: a lone source cell is 8/9 high");

        // Flowing water is lower (amount/9); falling water counts as a full source amount.
        var flow = World(3, 1, 1, (x, y, z) => x + 1, "minecraft:air", "minecraft:water[level=4]", "minecraft:water[level=8]", "minecraft:water[level=7]");
        check(At(flow, 0.5f, 0.4f, 0.5f) == EyeMedium.Water && At(flow, 0.5f, 0.5f, 0.5f) == EyeMedium.Air &&
              At(flow, 1.5f, 0.85f, 0.5f) == EyeMedium.Water && At(flow, 2.5f, 0.1f, 0.5f) == EyeMedium.Water && At(flow, 2.5f, 0.12f, 0.5f) == EyeMedium.Air,
              "eye: flowing level 4 is 4/9 high, falling 8/9, level 7 1/9");

        // Waterlogged blocks and plants that always sit in water.
        var plants = World(4, 1, 1, (x, y, z) => x + 1, "minecraft:air", "minecraft:kelp[age=3]", "minecraft:seagrass", "minecraft:bubble_column[drag=true]",
                           "minecraft:oak_fence[east=false,north=false,south=false,waterlogged=true,west=false]");
        check(Enumerable.Range(0, 4).All(i => At(plants, i + 0.5f, 0.5f, 0.5f) == EyeMedium.Water) && At(plants, 0.5f, 0.95f, 0.5f) == EyeMedium.Air,
              "eye: kelp, seagrass, bubble column, waterlogged fence are water up to 8/9");

        // Waterlogged stairs: a solid cube while no pack model exists (BlockRules), water once they mesh as a model.
        string stairs = "minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=true]";
        var st = World(1, 1, 1, (x, y, z) => 1, "minecraft:air", stairs);
        bool cubeSolid = At(st, 0.5f, 0.5f, 0.5f) == EyeMedium.Solid;
        BlockRules.SetShape(stairs, BlockShape.Model);   // what BlockModels.Prepare does with a pack
        check(cubeSolid && At(st, 0.5f, 0.5f, 0.5f) == EyeMedium.Water && At(st, 0.5f, 0.95f, 0.5f) == EyeMedium.Air,
              "eye: waterlogged stairs are solid as a cube placeholder, water up to 8/9 as a model");

        // Slabs: the solid half is solid, the other half is the fluid (or air).
        var slabs = World(2, 1, 1, (x, y, z) => x + 1, "minecraft:air", "minecraft:oak_slab[type=bottom,waterlogged=true]", "minecraft:oak_slab[type=top,waterlogged=true]");
        check(At(slabs, 0.5f, 0.25f, 0.5f) == EyeMedium.Solid && At(slabs, 0.5f, 0.75f, 0.5f) == EyeMedium.Water && At(slabs, 0.5f, 0.95f, 0.5f) == EyeMedium.Air &&
              At(slabs, 1.5f, 0.75f, 0.5f) == EyeMedium.Solid && At(slabs, 1.5f, 0.25f, 0.5f) == EyeMedium.Water,
              "eye: waterlogged slabs: solid half solid, other half water");

        // Lava, powder snow, opaque blocks, see-through blocks, ice over water.
        var misc = World(6, 2, 1, (x, y, z) => y == 1 ? (x == 5 ? 6 : 0) : x + 1, "minecraft:air", "minecraft:lava[level=0]", "minecraft:powder_snow",
                         "minecraft:stone", "minecraft:glass", water, "minecraft:ice");
        check(At(misc, 0.5f, 0.5f, 0.5f) == EyeMedium.Lava && At(misc, 1.5f, 0.99f, 0.5f) == EyeMedium.PowderSnow && At(misc, 2.5f, 0.5f, 0.5f) == EyeMedium.Solid &&
              At(misc, 3.5f, 0.5f, 0.5f) == EyeMedium.Air, "eye: lava, powder snow (whole cell), stone solid, glass air");
        check(At(misc, 4.5f, 0.5f, 0.5f) == EyeMedium.Water && At(misc, 4.5f, 0.95f, 0.5f) == EyeMedium.Air && At(misc, 4.5f, 1.5f, 0.5f) == EyeMedium.Air,
              "eye: under ice the water ends at 8/9 (thin air gap), inside the ice is air");

        // Outside the region is air, even right next to the water or under the region.
        var tank = World(4, 4, 4, (x, y, z) => 1, "minecraft:air", water);
        check(At(tank, -0.01f, 2f, 2f) == EyeMedium.Air && At(tank, 2f, -0.5f, 2f) == EyeMedium.Air && At(tank, 2f, 5f, 2f) == EyeMedium.Air &&
              At(tank, 2f, 3.95f, 2f) == EyeMedium.Air && At(tank, 2f, 3.5f, 2f) == EyeMedium.Water && At(new WorldModel(), 0, 0, 0) == EyeMedium.Air,
              "eye: outside the region is air; the region's top cut through water is a surface at 8/9");

        // Filter: enter after a short dwell, ignore one-frame blips, no flicker while hovering at the surface.
        var f = new EyeMediumFilter();
        const float bs = 0.01f, dt = 0.02f;   // 1 cm blocks: 4 mm margin = 0.4 block
        var under = new Vector3(2f, 3.5f, 2f);
        f.Update(tank, under, bs, dt);
        bool waited = f.Medium == EyeMedium.Air;
        f.Update(tank, under, bs, dt); f.Update(tank, under, bs, dt);
        check(waited && f.Medium == EyeMedium.Water, "filter: water after the dwell time, not on the first frame");
        int switches = 0;
        for (int i = 0; i < 100; i++)
        {
            f.Update(tank, new Vector3(2f, 3f + src + (i % 2 == 0 ? 0.2f : -0.2f), 2f), bs, dt);
            if (f.Changed) switches++;
        }
        check(switches == 0 && f.Medium == EyeMedium.Water, "filter: hovering +-0.2 block around the surface never flickers");
        for (int i = 0; i < 5; i++) f.Update(tank, new Vector3(2f, 3f + src + 0.6f, 2f), bs, dt);
        check(f.Medium == EyeMedium.Air, "filter: leaves water once past the margin (0.4 block) for the dwell time");
        var g = new EyeMediumFilter();
        for (int i = 0; i < 10; i++) g.Update(tank, i == 5 ? under : new Vector3(2f, 6f, 2f), bs, dt);
        check(g.Medium == EyeMedium.Air && g.Look.TintAlpha == 0, "filter: a one-frame dip doesn't switch");

        // Fade: ~0.2 s in and out, no colour shift while fading in; vanilla water vision 24 -> 96 blocks over 30 s.
        var h = new EyeMediumFilter();
        h.Update(tank, under, bs, 0.05f);   // dwell reached: switches to water, fade starts
        float a0 = h.Look.TintAlpha;
        h.Update(tank, under, bs, 0.1f);
        float aMid = h.Look.TintAlpha;
        h.Update(tank, under, bs, 0.1f);
        float aEnd = h.Look.TintAlpha;
        var expect = EyeMediumRules.Look(EyeMedium.Water, EyeMediumRules.WaterColor(tank, under), EyeMediumRules.WaterVision(h.VisionTicks));
        check(a0 < 0.1f && aMid > 0.1f && aMid < 0.4f && Math.Abs(aEnd - 0.45f) < 1e-4 && Math.Abs(h.Look.Fog.B - expect.Fog.B) < 1e-4,
              $"fade in over 0.2 s (alpha {a0:F2} -> {aMid:F2} -> {aEnd:F2})");
        check(Math.Abs(h.Look.End - 48f) < 0.1f && EyeMediumRules.WaterVision(0) == 0 && Math.Abs(EyeMediumRules.WaterVision(100) - 0.6f) < 1e-6 &&
              EyeMediumRules.WaterVision(600) == 1, $"water vision: 48 blocks on entering (got {h.Look.End:F1}), vanilla ramp from half");
        for (int i = 0; i < 31 * 20; i++) h.Update(tank, under, bs, 0.05f);
        check(Math.Abs(h.Look.End - 96f) < 1e-3 && h.Look.Start == 0f && h.Look.Strength == 0.7f, $"water vision: 96 blocks after 30 s (got {h.Look.End:F1})");
        var c = EyeMediumRules.WaterColor(tank, under);
        check(Math.Abs(c.R - 63 / 255f) < 1e-6 && Math.Abs(c.B - 228 / 255f) < 1e-6 && Math.Abs(h.Look.Fog.B - c.B) < 1e-4,
              "water fog: default water colour without biomes, full brightness once adjusted");
        for (int i = 0; i < 3; i++) h.Update(tank, new Vector3(2f, 6f, 2f), bs, 0.05f);
        float outMid = h.Look.TintAlpha;
        for (int i = 0; i < 5; i++) h.Update(tank, new Vector3(2f, 6f, 2f), bs, 0.05f);
        check(h.Medium == EyeMedium.Air && outMid > 0 && outMid < 0.45f && h.Look.TintAlpha == 0 && h.Look.Strength == 0, "fade out to nothing after leaving");
        var lava = EyeMediumRules.Look(EyeMedium.Lava, default, 0);
        var solid = EyeMediumRules.Look(EyeMedium.Solid, default, 0);
        check(lava.End == 1f && lava.Strength == 1f && lava.TintAlpha >= 0.8f && solid.TintAlpha < 1f && solid.End <= 2f && EyeMediumRules.Look(EyeMedium.Air, c, 1).Visible == false,
              "looks: lava ~1 block, strong tint; solid dim, not black; air off");

        // Water's inside faces: every water face has a reversed copy in WaterBack (own vertices), lava has none, and the
        // copies never go to the transparent list (from outside nothing is drawn twice).
        var m = new MeshData();
        GreedyMesher.BuildSpecials(one, new Vector3Int(0, 0, 0), new Tagged(), m);
        int front = m.Transparent.Count / 6;
        bool inward = true;
        for (int i = 0; i < m.WaterBack.Count; i += 3)
        {
            Vector3 p0 = m.Verts[m.WaterBack[i]], p1 = m.Verts[m.WaterBack[i + 1]], p2 = m.Verts[m.WaterBack[i + 2]];
            Vector3 a = p1 - p0, b = p2 - p0;
            var n = new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
            var toCentre = new Vector3(0.5f, 0.45f, 0.5f) - (p0 + p1 + p2) * (1f / 3f);
            if (n.x * toCentre.x + n.y * toCentre.y + n.z * toCentre.z <= 0) inward = false;
        }
        bool disjoint = !m.WaterBack.Intersect(m.Transparent).Any();
        check(front == 6 && m.WaterBack.Count == 36 && m.Verts.Count == 48 && inward && disjoint,
              $"inside faces: 6 water faces -> 6 reversed copies with own vertices, facing into the water ({front} outside faces)");
        var surface = Enumerable.Range(0, m.WaterBack.Count / 6).Select(q => m.WaterBack[q * 6]).Where(v => m.Verts[v].y == src).Count();
        check(surface == 1, "inside faces: the surface (8/9) is visible from below");
        m.Clear();
        GreedyMesher.BuildSpecials(World(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:lava[level=0]"), new Vector3Int(0, 0, 0), new Tagged(), m);
        check(m.WaterBack.Count == 0 && m.Opaque.Count == 36, "inside faces: none for lava");
        var ocean = World(16, 5, 16, (x, y, z) => y < 4 ? 1 : 0, "minecraft:air", water);
        m.Clear();
        GreedyMesher.BuildSpecials(ocean, new Vector3Int(0, 0, 0), new Tagged(), m);
        check(m.WaterBack.Count == m.Transparent.Count, $"inside faces: the merged ocean's {m.Transparent.Count / 6} quads all have inside copies");

        // Nav's window caps: cells holding water are cut as water (no holes around kelp), and water cuts get inside copies.
        string[] pal = { "minecraft:air", "minecraft:stone", water, "minecraft:kelp_plant", "minecraft:poppy", "minecraft:lava[level=0]", stairs };
        check(CapMesher.CapState(pal[3], BlockRules.Shape(pal[3])) == FluidRules.WaterState && CapMesher.CapState(pal[2], BlockShape.Fluid) == FluidRules.WaterState &&
              CapMesher.CapState(pal[6], BlockShape.Model) == FluidRules.WaterState && CapMesher.CapState(pal[1], BlockShape.Cube) == pal[1] &&
              CapMesher.CapState(pal[4], BlockShape.Cross) == null && CapMesher.CapState(pal[0], BlockShape.Air) == null &&
              CapMesher.CapState(pal[5], BlockShape.Fluid) == FluidRules.LavaState &&
              CapMesher.CapState("minecraft:oak_leaves[waterlogged=true]", BlockShape.TransparentCube) == "minecraft:oak_leaves[waterlogged=true]",
              "caps: kelp/waterlogged models/bubble columns cut as water, cubes as themselves, plants and air not at all");
        var pool = World(4, 3, 4, (x, y, z) => y == 0 ? 1 : x == 1 && z == 0 ? 3 : 2, pal);   // stone floor, water with a kelp at x=1
        var capKeys = new FaceKey[pal.Length * 6]; var capTable = new bool[pal.Length];
        for (int i = 0; i < pal.Length; i++)
        {
            string cut = CapMesher.CapState(pal[i], BlockRules.Shape(pal[i]));
            capTable[i] = cut != null;
            for (int fc = 0; capTable[i] && fc < 6; fc++)
                capKeys[i * 6 + fc] = new FaceKey { Layer = cut == FluidRules.WaterState ? 1 : 0, Color = BlockRules.FlatColor(cut), Transparent = cut == FluidRules.WaterState,
                                                    Tint = cut == FluidRules.WaterState ? TintKind.Water : TintKind.None };
        }
        var cm = new MeshData();
        CapMesher.Build(pool, 4, 0, capKeys, capTable, cm);   // -Z side at z=0: stone row + 4x2 water incl. the kelp cell
        int waterCaps = cm.Transparent.Count / 6;
        check(waterCaps == 1 && cm.WaterBack.Count == 6 && cm.Opaque.Count == 6, $"caps: the water cut merges over the kelp cell into 1 quad with an inside copy ({waterCaps} water quads)");
    }
}
