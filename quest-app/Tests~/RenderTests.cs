// Engine-free tests for the 0.3.1 look-like-vanilla rendering work: vanilla's per-position randomness (BlockRandom:
// Mth.getSeed, the java.util.Random LCG, weighted variant picks, model offsets) against golden values computed with
// the real 26.3 classes, and with a client jar (--jar) the baked random variants and the greedy cubes' per-block UV
// tables (RandomUv).
using System;
using System.Collections.Generic;
using System.Linq;
using TabletopCraft.World;
using UnityEngine;

static class RenderTests
{
    /// <summary>Golden values from the 26.3 client classes (Mth.getSeed, RandomSource.createThreadLocalInstance =
    /// SingleThreadedRandomSource): the seed of (x, y, z), nextInt(4/16/5) after setSeed(seed), the multipart part seed
    /// (nextLong) and nextInt(2/4/5) after reseeding with it, and the XYZ model offset of (x, 0, z).</summary>
    readonly struct G
    {
        public readonly int X, Y, Z, V4, V16, V5, M2, M4, M5; public readonly long Seed, PartSeed; public readonly float Dx, Dy, Dz;
        public G(int x, int y, int z, long seed, int v4, int v16, int v5, long partSeed, int m2, int m4, int m5, float dx, float dy, float dz)
        { X = x; Y = y; Z = z; Seed = seed; V4 = v4; V16 = v16; V5 = v5; PartSeed = partSeed; M2 = m2; M4 = m4; M5 = m5; Dx = dx; Dy = dy; Dz = dz; }
    }

    static readonly G[] Gold =
    {
        new G(0, 0, 0, 0L, 2, 11, 0, -4962768465676381896L, 0, 1, 3, -0.250000000f, -0.200000003f, -0.250000000f),
        new G(1, 64, -1, 51075559105168L, 2, 9, 0, -7656025847503286258L, 0, 1, 2, 0.050000012f, -0.186666667f, -0.183333337f),
        new G(-37, 70, 123, 127394479219720L, 0, 2, 1, 3149645818009156871L, 0, 1, 2, 0.050000012f, -0.039999999f, -0.183333337f),
        new G(1000, -12, -999, -89076087406270L, 2, 10, 0, -6563781731044010084L, 1, 3, 3, 0.050000012f, -0.039999999f, 0.116666675f),
        new G(12345, 100, -54321, -94259506441571L, 1, 5, 0, 6437769178289409417L, 0, 1, 2, -0.183333337f, -0.200000003f, -0.049999997f),
        new G(-30000000, 319, 29999999, -20892113470306L, 0, 1, 0, 1896228282024270181L, 1, 2, 0, -0.250000000f, -0.013333333f, 0.150000006f),
    };

    public static void Run(Action<bool, string> check, string jar)
    {
        Randomness(check);
        UvCodes(check);
        Pistons(check, jar);
        Blocks(check, jar);
        if (jar != null) Pack(check, jar);
        else Console.WriteLine("SKIP render pack tests: set MC_CLIENT_JAR or pass --jar PATH");
    }

    static void Randomness(Action<bool, string> check)
    {
        var bad = new List<string>();
        var xyz = new BakedModel { OffsetKind = 2 };
        foreach (var g in Gold)
        {
            long seed = BlockRandom.Seed(g.X, g.Y, g.Z);
            var r = new BlockRandom.JavaRandom(seed);
            int v4 = r.NextInt(4); r.SetSeed(seed); int v16 = r.NextInt(16); r.SetSeed(seed); int v5 = r.NextInt(5);
            r.SetSeed(seed); long part = r.NextLong();
            r.SetSeed(part); int m2 = r.NextInt(2); r.SetSeed(part); int m4 = r.NextInt(4); r.SetSeed(part); int m5 = r.NextInt(5);
            var off = BlockRandom.Offset(xyz, g.X, g.Z);
            if (seed != g.Seed || v4 != g.V4 || v16 != g.V16 || v5 != g.V5 || part != g.PartSeed || m2 != g.M2 || m4 != g.M4 || m5 != g.M5 ||
                Math.Abs(off.x - g.Dx) > 1e-6f || Math.Abs(off.y - g.Dy) > 1e-6f || Math.Abs(off.z - g.Dz) > 1e-6f)
                bad.Add($"({g.X},{g.Y},{g.Z}) seed {seed} picks {v4}/{v16}/{v5} part {m2}/{m4}/{m5} off {off.x},{off.y},{off.z}");
        }
        check(bad.Count == 0, $"block randomness matches the 26.3 classes at {Gold.Length} positions (Mth.getSeed, Random LCG, weighted and multipart picks, XYZ offsets)" +
                              (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));
        // Picks through a model: a 4-entry list picks nextInt(4); a multipart model with totals 2 and 4 combines its parts.
        var plain = new BakedModel { RandomTotals = new[] { 4 }, Variants = new BakedModel[4] };
        var multi = new BakedModel { RandomTotals = new[] { 2, 4 }, RandomMultipart = true, Variants = new BakedModel[8] };
        check(Gold.All(g => BlockRandom.Pick(plain, g.X, g.Y, g.Z) == g.V4 && BlockRandom.Pick(multi, g.X, g.Y, g.Z) == g.M2 * 4 + g.M4),
              "variant picks: plain lists draw nextInt(total), multipart parts share one reseeded long (fire: 2 x 4 combinations)");
        // XZ offsets ignore y (both halves of a tall flower move together) and stay within +-0.25 (dripstone 0.125).
        var xz = new BakedModel { OffsetKind = 1 }; var drip = new BakedModel { OffsetKind = 1, MaxHorizontalOffset = 0.125f };
        bool ok = true;
        for (int x = -20; x < 20; x++) for (int z = -20; z < 20; z++)
        {
            var o = BlockRandom.Offset(xz, x, z); var d = BlockRandom.Offset(drip, x, z);
            ok &= o.y == 0 && Math.Abs(o.x) <= 0.25f && Math.Abs(o.z) <= 0.25f && Math.Abs(d.x) <= 0.125f + 1e-6f && Math.Abs(d.z) <= 0.125f + 1e-6f;
        }
        check(ok && BlockRandom.Offset(new BakedModel(), 5, 7).x == 0, "model offsets: XZ never moves up or down, within 0.25 (speleothems 0.125); no offset type = none");
    }

    /// <summary>RandomUv codes: Relative(a, b) maps a's UVs onto b's, and Apply does in C# what the shader does.</summary>
    static void UvCodes(Action<bool, string> check)
    {
        bool ok = true;
        for (byte a = 0; a < 8; a++)
            for (byte b = 0; b < 8; b++)
            {
                byte r = RandomUv.Relative(a, b);
                RandomUv.Matrix(a, out int a00, out int a01, out int a10, out int a11);
                RandomUv.Matrix(b, out int b00, out int b01, out int b10, out int b11);
                RandomUv.Matrix(r, out int r00, out int r01, out int r10, out int r11);
                // R * A == B
                ok &= r00 * a00 + r01 * a10 == b00 && r00 * a01 + r01 * a11 == b01 && r10 * a00 + r11 * a10 == b10 && r10 * a01 + r11 * a11 == b11;
            }
        float u = 0.2f, v = 0.7f;
        RandomUv.Apply(2, ref u, ref v);   // negate u: mirrored
        float u2 = 0.2f, v2 = 0.7f;
        RandomUv.Apply(1 | 2, ref u2, ref v2);   // swap, then negate u: a quarter turn
        check(ok && Math.Abs(u - 0.8f) < 1e-6f && Math.Abs(v - 0.7f) < 1e-6f && Math.Abs(u2 - 0.3f) < 1e-6f && Math.Abs(v2 - 0.2f) < 1e-6f,
              "random UV codes: Relative composes with UvMap for all 64 pairs; Apply mirrors and turns the in-block coordinate");
    }

    static void Pack(Action<bool, string> check, string jar)
    {
        using var assets = new ZipAssets(jar);
        var baker = new BlockModelBaker(assets);
        BakedModel B(string s) => baker.Bake(s);

        // Weighted variants are baked per combination and picked per position like vanilla.
        var lily = B("minecraft:lily_pad");
        var fire = B("minecraft:fire[age=0,east=false,north=false,south=false,up=false,west=false]");
        var fireSides = B("minecraft:fire[age=0,east=true,north=true,south=false,up=false,west=false]");
        var chorus = B("minecraft:chorus_plant[down=false,east=false,north=true,south=false,up=false,west=false]");
        var poppy = B("minecraft:poppy");
        check(lily.Variants?.Length == 4 && !lily.RandomMultipart && fire.Variants?.Length == 8 && fire.RandomMultipart && fireSides.Variants?.Length == 4 &&
              chorus.Variants?.Length == 5 && poppy.Variants == null,
              $"random variants baked: lily pad 4, fire on the ground floor x sides 2x4={fire.Variants?.Length}, fire on two walls 4, chorus plant 5 (weights 2+1+1+1), poppy none");
        // The lily pad's variants are its model turned 0/90/180/270 degrees; the pick follows the seed (V4 above).
        var turned = Enumerable.Range(0, 4).Select(k => Corner(lily.Variants[k])).ToList();
        check(turned.Distinct().Count() == 4 && Gold.All(g => lily.Pick(g.X, g.Y, g.Z) == lily.Variants[g.V4]),
              "lily pad: four turned variants, picked by nextInt(4) of the block's seed");
        check(poppy.OffsetKind == 1 && B("minecraft:short_grass").OffsetKind == 2 && B("minecraft:small_dripleaf[facing=north,half=lower,waterlogged=false]").MaxVerticalOffset == 0.1f &&
              B("minecraft:pointed_dripstone[thickness=tip,vertical_direction=up,waterlogged=false]").MaxHorizontalOffset == 0.125f && B("minecraft:stone").OffsetKind == 0,
              "offset types: poppy XZ, short grass XYZ, small dripleaf 0.1 down, pointed dripstone 0.125 sideways, stone none");

        // Meshing: a poppy moves by its offset, the lily pad turns per block.
        BlockRules.ResetShapes();
        RandomUv.Reset();
        var pf = new PackFaces(assets);
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(1, 1, 1), Origin = new Vector3Int(1000, -12, -999) };
        w.Palette.AddRange(new[] { "minecraft:air", "minecraft:poppy" });
        w.Grid = new ushort[] { 1 };
        pf.Prewarm(w);
        var m = new MeshData();
        GreedyMesher.BuildSpecials(w, new Vector3Int(0, 0, 0), pf, m);
        var plain = new MeshData();
        BlockModelMesher.Emit(plain, new WorldModel { SnapshotId = 1, Dims = new Vector3Int(1, 1, 1), Grid = new ushort[] { 1 }, Palette = { "minecraft:air", "minecraft:poppy" } },
                              0, 0, 0, "minecraft:poppy", new BakedModel { Quads = poppy.Quads }, null);
        var g4 = Gold[3];   // (1000, -12, -999): XZ offset 0.05, 0.1167
        bool moved = m.Verts.Count == plain.Verts.Count && m.Verts.Count > 0 &&
                     Enumerable.Range(0, m.Verts.Count).All(i => Math.Abs(m.Verts[i].x - plain.Verts[i].x - g4.Dx) < 1e-5f && Math.Abs(m.Verts[i].z - plain.Verts[i].z - g4.Dz) < 1e-5f && m.Verts[i].y == plain.Verts[i].y);
        check(moved, $"mesh: a poppy at (1000, -12, -999) is offset by ({g4.Dx}, 0, {g4.Dz}) like vanilla");

        // Greedy cubes with random rotations get per-side UV tables; sides that look the same in every variant don't.
        var stone = Face6(pf, "minecraft:stone"); var dirt = Face6(pf, "minecraft:dirt"); var grassF = Face6(pf, "minecraft:grass_block[snowy=false]");
        var nether = Face6(pf, "minecraft:netherrack"); var plainCube = Face6(pf, "minecraft:cobblestone");
        check(stone.All(f => f.Random != 0) && dirt[3].Random != 0 && dirt[2].Random != 0 && dirt[0].Random == 0 && dirt[4].Random == 0 &&
              grassF[3].Random != 0 && grassF[0].Random == 0 && nether.All(f => f.Random != 0) && plainCube.All(f => f.Random == 0) &&
              RandomUv.Table(nether[3].Random).Length == 16 && RandomUv.Table(dirt[3].Random).Length == 4,
              $"random UV tables: stone all sides (mirrored), dirt and grass top/bottom only (turned), netherrack 16 variants, cobblestone none ({RandomUv.Count} tables)");
        // Each table entry turns variant 0's UVs into that variant's UVs (checked on the baked faces).
        var bad = new List<string>();
        foreach (var s in new[] { "minecraft:stone", "minecraft:dirt", "minecraft:netherrack", "minecraft:grass_block[snowy=false]", "minecraft:deepslate[axis=x]", "minecraft:sand" })
        {
            Face6(pf, s);
            var bm = pf.Model(s);
            for (int d = 0; d < 6; d++)
            {
                var codes = RandomUv.Table(bm.CubeFaces[d].Random);
                for (int k = 0; k < bm.Variants.Length; k++)
                {
                    byte code = codes == null ? (byte)0 : codes[k];
                    if (!SameUv(bm.Variants[0], bm.Variants[k], d, code)) bad.Add($"{s} dir {d} variant {k}");
                }
            }
        }
        check(bad.Count == 0, "random UV tables reproduce every variant's baked face UVs" + (bad.Count > 0 ? ": " + string.Join(", ", bad.Take(6)) : ""));
        // The greedy mesher still merges a floor of stone into one quad per side (the rotation is per block in the shader),
        // and encodes the table in uv.z = layer + 4096 * table.
        var floor = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(4, 1, 4), Origin = new Vector3Int(0, 0, 0) };
        floor.Palette.AddRange(new[] { "minecraft:air", "minecraft:stone" });
        floor.Grid = Enumerable.Repeat((ushort)1, 16).ToArray();
        pf.Prewarm(floor);
        m.Clear();
        GreedyMesher.Build(floor, new Vector3Int(0, 0, 0), pf, m);
        int stoneLayer = assets.LayerOf("block/stone");
        check(m.Verts.Count / 4 == 6 && m.Uvs.All(t => (int)t.z % RandomUv.LayerStride == stoneLayer && stone.Any(f => f.Random == (int)t.z / RandomUv.LayerStride && f.Random != 0)),
              $"mesh: a 4x4 stone floor still merges into 6 quads, each carrying its random table in uv.z (got {m.Verts.Count / 4})");
        var packed = RandomUv.Packed();
        var dt = RandomUv.Table(dirt[3].Random);
        int lo = (int)packed[dirt[3].Random * 4 + 1];
        check(packed[dirt[3].Random * 4] == dt.Length && Enumerable.Range(0, dt.Length).All(k => ((lo >> (3 * k)) & 7) == dt[k]), "random UV tables pack into the shader's float4 layout");
        BlockRules.ResetShapes();
    }

    /// <summary>Moving pistons (PistonGhosts): the block_updates 26.3 sends for a push, a retraction and a sticky pull,
    /// and what each moving_piston cell draws meanwhile.</summary>
    static void Pistons(Action<bool, string> check, string jar)
    {
        string[] pal =
        {
            "minecraft:air", "minecraft:piston[extended=false,facing=east]", "minecraft:stone", "minecraft:dirt", "minecraft:piston[extended=true,facing=east]",
            "minecraft:moving_piston[facing=east,type=normal]", "minecraft:piston_head[facing=east,short=false,type=normal]",
            "minecraft:sticky_piston[extended=true,facing=east]", "minecraft:moving_piston[facing=east,type=sticky]", "minecraft:piston_head[facing=east,short=false,type=sticky]",
            "minecraft:sticky_piston[extended=false,facing=east]",
        };
        WorldModel Row(params int[] ids)
        {
            var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(ids.Length, 1, 1) };
            w.Palette.AddRange(pal);
            w.Grid = ids.Select(i => (ushort)i).ToArray();
            return w;
        }
        bool Update(WorldModel w, params int[][] changes) =>
            w.ApplyBlockUpdate(TabletopCraft.Net.Json.Parse("{\"snapshot_id\":1,\"changes\":[" + string.Join(",", changes.Select(c => "[" + c[0] + ",0,0," + c[1] + "]")) + "]}") as Dictionary<string, object>);
        string Shown(WorldModel w, int x) => w.MovingShown.TryGetValue(x, out var s) ? s : null;

        // Push: base at x 0 facing east, stone and dirt in front.
        var push = Row(1, 2, 3, 0, 0);
        Update(push, new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 5 }, new[] { 3, 5 });
        bool pushed = Shown(push, 1) == pal[6] && Shown(push, 2) == pal[2] && Shown(push, 3) == pal[3] && Shown(push, 0) == null;
        Update(push, new[] { 1, 6 }, new[] { 2, 2 }, new[] { 3, 3 });
        check(pushed && push.MovingShown.IsEmpty, "piston push: the head cell draws the piston head, each pushed block its new cell, until the final states");
        // Retract (normal): the base cell moves (it keeps its extended look), the head cell goes straight to air.
        var pull = Row(4, 6, 0);
        Update(pull, new[] { 0, 5 }, new[] { 1, 0 });
        bool retracted = Shown(pull, 0) == pal[4] && Shown(pull, 1) == null;
        Update(pull, new[] { 0, 1 });
        // Sticky pull: base and head cell move, the stone two cells out is pulled into the head cell.
        var sticky = Row(7, 9, 2, 0);
        Update(sticky, new[] { 0, 8 }, new[] { 1, 8 }, new[] { 2, 0 });
        bool pulled = Shown(sticky, 0) == pal[7] && Shown(sticky, 1) == pal[2];
        check(retracted && pull.MovingShown.IsEmpty && pulled, "piston retract: the base keeps its extended look; a sticky piston's pulled block shows in the head cell");
        // A world without pistons never records anything (cheap path).
        check(PistonGhosts.Begin(new WorldModel { Palette = { "minecraft:air", "minecraft:stone" } }) == null, "no moving_piston in the palette: block updates skip the piston bookkeeping");

        if (jar == null) return;
        using var assets = new ZipAssets(jar);
        BlockRules.ResetShapes();
        var pf = new PackFaces(assets);
        var w2 = Row(1, 2, 3, 0, 0);
        pf.Prewarm(w2);
        Update(w2, new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 5 }, new[] { 3, 5 });
        pf.Prewarm(w2); foreach (var s in w2.MovingShown.Values) pf.Face(s, 0);   // as TabletopRenderer.Prewarm
        var m = new MeshData();
        GreedyMesher.BuildSpecials(w2, new Vector3Int(0, 0, 0), pf, m);
        int In(float lo, float hi) => Enumerable.Range(0, m.Verts.Count / 4).Count(q => Enumerable.Range(0, 4).All(i => m.Verts[q * 4 + i].x >= lo - 1e-4f && m.Verts[q * 4 + i].x <= hi + 1e-4f));
        int head = In(1, 2), stoneQuads = In(2, 3), dirtQuads = In(3, 4);
        check(head > 0 && stoneQuads >= 5 && dirtQuads >= 5, $"piston push meshes the head and both blocks in their moving cells (quads {head}/{stoneQuads}/{dirtQuads})");
        BlockRules.ResetShapes();
    }

    /// <summary>Small block fixes: light_emission faces, emissive blocks, "ambientocclusion": false, bare texture slots,
    /// faces between leaves.</summary>
    public static void Blocks(Action<bool, string> check, string jar)
    {
        if (jar == null) return;
        using var assets = new ZipAssets(jar);
        var baker = new BlockModelBaker(assets);
        BakedModel B(string s) => baker.Bake(s);
        var eye = B("minecraft:open_eyeblossom");
        var heavy = B("minecraft:heavy_core[waterlogged=false]");
        check(eye.Quads.Count(q => q.Emission == 15) == 4 && eye.Quads.Count(q => q.Emission == 0) == 4 && B("minecraft:firefly_bush").Quads.Any(q => q.Emission == 15),
              "light_emission: open eyeblossom's and the firefly bush's emissive layer faces carry 15");
        check(heavy.Quads.Length == 6 && heavy.Quads.All(q => (int)q.T0.z == assets.LayerOf("block/heavy_core")), "heavy core: bare \"texture\": \"all\" resolves to its slot (heavy_core texture, not a flat colour)");
        check(B("minecraft:magma_block").Shade == BakedModel.ShadeFullBright && B("minecraft:magma_block").CubeFaces.All(f => f.Shade == BakedModel.ShadeFullBright) &&
              B("minecraft:sculk_sensor[power=0,sculk_sensor_phase=active,waterlogged=false]").Shade == BakedModel.ShadeFullBright &&
              B("minecraft:sculk_sensor[power=0,sculk_sensor_phase=inactive,waterlogged=false]").Shade != BakedModel.ShadeFullBright &&
              B("minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]").Shade == BakedModel.ShadeFlat &&
              B("minecraft:rail[shape=north_south,waterlogged=false]").Shade == BakedModel.ShadeFlat && B("minecraft:stone").Shade == BakedModel.ShadeSmooth,
              "shading: magma and active sculk sensors full bright, doors and rails flat (ambientocclusion false), stone smooth");

        // Lit in the dark: a stone box with no light at all around a one-block air pocket, an open eyeblossom standing
        // on a magma block in it. Magma's top face is full bright, the stone walls of the pocket dark, the eyeblossom's
        // emissive faces glow and its plain faces stay dark.
        BlockRules.ResetShapes();
        var pf = new PackFaces(assets);
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(5, 5, 5) };
        w.Palette.AddRange(new[] { "minecraft:air", "minecraft:magma_block", "minecraft:stone", "minecraft:open_eyeblossom" });
        w.Grid = new ushort[125];
        for (int y = 0; y < 5; y++) for (int z = 0; z < 5; z++) for (int x = 0; x < 5; x++)
            w.Grid[w.Index(x, y, z)] = (ushort)(x == 2 && z == 2 && y == 2 ? 3 : x == 2 && z == 2 && y == 1 ? 1 : 2);
        w.Light = new byte[125];
        pf.Prewarm(w);
        var m = new MeshData();
        GreedyMesher.Build(w, new Vector3Int(0, 0, 0), pf, m);
        int magmaLayer = assets.LayerOf("block/magma"), stoneLayer = assets.LayerOf("block/stone");
        var full = BlockShading.Lightmap(15, 15); var dark = BlockShading.Lightmap(0, 0);
        bool Inside(int q) => Enumerable.Range(0, 4).All(i => m.Verts[q * 4 + i].x > 0.5f && m.Verts[q * 4 + i].x < 4.5f && m.Verts[q * 4 + i].y > 0.5f && m.Verts[q * 4 + i].y < 4.5f && m.Verts[q * 4 + i].z > 0.5f && m.Verts[q * 4 + i].z < 4.5f);
        int magmaTop = -1, stoneMax = -1;
        for (int q = 0; q < m.Verts.Count / 4; q++)
        {
            if (!Inside(q)) continue;   // the box's outside faces are on the region border: open sky
            int layer = (int)m.Uvs[q * 4].z % RandomUv.LayerStride;
            if (layer == magmaLayer) magmaTop = m.Colors[q * 4].g;
            if (layer == stoneLayer) for (int i = 0; i < 4; i++) stoneMax = Math.Max(stoneMax, m.Colors[q * 4 + i].g);
        }
        bool magmaBright = magmaTop >= full.y * 255 * 0.99f, stoneDark = stoneMax >= 0 && stoneMax <= dark.y * 255 * 1.05f + 1;
        m.Clear();
        GreedyMesher.BuildSpecials(w, new Vector3Int(0, 0, 0), pf, m);
        int eLayer = assets.LayerOf("block/open_eyeblossom_emissive"), pLayer = assets.LayerOf("block/open_eyeblossom");
        var quads = Enumerable.Range(0, m.Verts.Count / 4).ToList();
        bool glow = quads.Any(q => (int)m.Uvs[q * 4].z == eLayer) && quads.Where(q => (int)m.Uvs[q * 4].z == eLayer).All(q => m.Colors[q * 4].g >= full.y * 255 * 0.99f) &&
                    quads.Where(q => (int)m.Uvs[q * 4].z == pLayer).All(q => m.Colors[q * 4].g <= dark.y * 255 * 1.05f + 1);
        check(magmaBright && stoneDark && glow, $"lighting in the dark: magma's top full bright ({magmaTop}), the pocket's stone walls dark ({stoneMax}), the eyeblossom's emissive faces glow ({glow})");

        // Leaves: faces between two identical leaves are drawn (vanilla cutoutLeaves); glass still joins up.
        var leaves = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(2, 1, 1) };
        leaves.Palette.AddRange(new[] { "minecraft:air", "minecraft:oak_leaves[distance=1,persistent=false,waterlogged=false]", "minecraft:glass" });
        leaves.Grid = new ushort[] { 1, 1 };
        pf.Prewarm(leaves);
        m.Clear();
        GreedyMesher.Build(leaves, new Vector3Int(0, 0, 0), pf, m);
        int inner = m.Verts.Count / 4;
        GreedyMesher.LeavesInner = false;
        GreedyMesher.Build(leaves, new Vector3Int(0, 0, 0), pf, m);
        int culled = m.Verts.Count / 4;
        GreedyMesher.LeavesInner = true;
        leaves.Grid = new ushort[] { 2, 2 };
        GreedyMesher.Build(leaves, new Vector3Int(0, 0, 0), pf, m);
        check(inner == 8 && culled == 6 && m.Verts.Count / 4 == 6, $"leaves: two oak leaves draw the 2 faces between them (8 faces, {culled} with leaves_inner off); two glass blocks join (6)");
        BlockRules.ResetShapes();
    }

    static FaceKey[] Face6(PackFaces pf, string s) => Enumerable.Range(0, 6).Select(d => pf.Face(s, d)).ToArray();

    /// <summary>A model's first quad's first corner, rounded (tells turned variants apart).</summary>
    static string Corner(BakedModel m)
    {
        var q = m.Quads[0];
        return $"{Math.Round(q.P0.x, 3)},{Math.Round(q.P0.y, 3)},{Math.Round(q.P0.z, 3)}:{Math.Round(q.T0.x, 3)},{Math.Round(q.T0.y, 3)}";
    }

    /// <summary>On side d, does variant k's face show variant 0's texture moved by code (compared at a point inside the face)?</summary>
    static bool SameUv(BakedModel v0, BakedModel vk, int d, byte code)
    {
        var a = WholeFace(v0, d); var b = WholeFace(vk, d);
        if (a == null || b == null) return false;
        // A point inside the face: weights on a's corners; find the same point on b by matching corner positions.
        float[] wts = { 0.55f, 0.25f, 0.15f, 0.05f };
        var pa = new[] { a.Value.P0, a.Value.P1, a.Value.P2, a.Value.P3 }; var ta = new[] { a.Value.T0, a.Value.T1, a.Value.T2, a.Value.T3 };
        var pb = new[] { b.Value.P0, b.Value.P1, b.Value.P2, b.Value.P3 }; var tb = new[] { b.Value.T0, b.Value.T1, b.Value.T2, b.Value.T3 };
        float ua = 0, va = 0, ub = 0, vb = 0;
        for (int i = 0; i < 4; i++)
        {
            int j = Enumerable.Range(0, 4).FirstOrDefault(n => Math.Abs(pb[n].x - pa[i].x) < 1e-4f && Math.Abs(pb[n].y - pa[i].y) < 1e-4f && Math.Abs(pb[n].z - pa[i].z) < 1e-4f);
            ua += wts[i] * ta[i].x; va += wts[i] * ta[i].y; ub += wts[i] * tb[j].x; vb += wts[i] * tb[j].y;
        }
        RandomUv.Apply(code, ref ua, ref va);
        float Frac(float x) => x - (float)Math.Floor(x);
        return Math.Abs(Frac(ua) - Frac(ub)) < 1e-3f && Math.Abs(Frac(va) - Frac(vb)) < 1e-3f;
    }

    static BakedQuad? WholeFace(BakedModel m, int d)
    {
        foreach (var q in m.Quads) if (q.Dir == d && q.Cull == d) return q;
        return null;
    }
}
