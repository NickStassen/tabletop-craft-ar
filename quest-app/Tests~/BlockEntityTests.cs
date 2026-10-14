// Block entities (BlockEntityCatalog, BlockEntityModels, SignText, BlockEntityExtras, block_entities data): the vanilla
// renderers' transforms, layer and texture choices per state, baked model bounds against the real layers (with
// --entity-models), sign text layout with the pack's font (with --jar), banner layers, beacon beam sections, the
// block-entity scan, and the protocol fixture. Run from Program.Main.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class BlockEntityTests
{
    static string P(Vector3 v) => $"({v.x:F4},{v.y:F4},{v.z:F4})";

    /// <summary>Images of (0,0,0), (1,0,0), (0,1,0), (0,0,1).</summary>
    public static string Probe(M4 m) => $"{P(m.Point(new Vector3(0, 0, 0)))} {P(m.Point(new Vector3(1, 0, 0)))} {P(m.Point(new Vector3(0, 1, 0)))} {P(m.Point(new Vector3(0, 0, 1)))}".Replace("-0.0000", "0.0000");

    /// <summary>--be-probe: the transforms in the format of a scratch harness that prints the client's own (not shipped).</summary>
    public static void PrintProbes()
    {
        foreach (var d in new[] { "north", "east", "south", "west" }) Console.WriteLine($"chest {d.ToUpper()} {Probe(BlockEntityCatalog.ChestTransform(d))}");
        foreach (var d in new[] { "up", "down", "north", "east", "south", "west" }) Console.WriteLine($"shulker {d.ToUpper()} {Probe(BlockEntityCatalog.ShulkerTransform(d))}");
        foreach (var d in new[] { "north", "east" }) Console.WriteLine($"skull wall {d.ToUpper()} {Probe(BlockEntityCatalog.SkullTransform(true, d, 0))}");
        foreach (var r in new[] { 0, 3 }) Console.WriteLine($"skull floor {r} {Probe(BlockEntityCatalog.SkullTransform(false, "north", r))}");
        foreach (var d in new[] { "north", "east" }) Console.WriteLine($"banner wall {d.ToUpper()} {Probe(BlockEntityCatalog.BannerTransform(true, d, 0))}");
        foreach (var r in new[] { 0, 5 }) Console.WriteLine($"banner floor {r} {Probe(BlockEntityCatalog.BannerTransform(false, "north", r))}");
        foreach (var d in new[] { "north", "east" }) Console.WriteLine($"pot {d.ToUpper()} {Probe(BlockEntityCatalog.PotTransform(d))}");
        foreach (var d in new[] { "north", "east" }) Console.WriteLine($"statue {d.ToUpper()} {Probe(BlockEntityCatalog.StatueTransform(d))}");
        foreach (var d in new[] { "north", "east" })
        {
            Console.WriteLine($"sign wall front {d.ToUpper()} {Probe(BlockEntityCatalog.SignTextTransform($"minecraft:oak_wall_sign[facing={d},waterlogged=false]", true))}");
            Console.WriteLine($"sign wall back {d.ToUpper()} {Probe(BlockEntityCatalog.SignTextTransform($"minecraft:oak_wall_sign[facing={d},waterlogged=false]", false))}");
        }
        Console.WriteLine($"sign floor front 5 {Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_sign[rotation=5,waterlogged=false]", true))}");
        Console.WriteLine($"sign floor back 5 {Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_sign[rotation=5,waterlogged=false]", false))}");
        Console.WriteLine($"hanging wall front EAST {Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_wall_hanging_sign[facing=east,waterlogged=false]", true))}");
        Console.WriteLine($"hanging wall back EAST {Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_wall_hanging_sign[facing=east,waterlogged=false]", false))}");
        Console.WriteLine($"hanging floor front 6 {Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_hanging_sign[attached=false,rotation=6,waterlogged=false]", true))}");
    }

    static bool Near(Vector3 a, float x, float y, float z, float eps = 2e-3f) => Math.Abs(a.x - x) < eps && Math.Abs(a.y - y) < eps && Math.Abs(a.z - z) < eps;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string jar)
    {
        Kinds(check);
        Transforms(check);
        Parts(check);
        Protocol(check, fixture);
        Banner(check);
        Beacon(check);
        Scan(check);
        if (EntityModels.Loaded) Models(check);
        else Console.WriteLine("SKIP block entity models: pass --entity-models (generated from your own client jar)");
        if (jar != null) { Font(check, jar); Baker(check, jar); }
        else Console.WriteLine("SKIP sign font: pass --jar");
    }

    static void Kinds(Action<bool, string> check)
    {
        var want = new (string path, BlockEntityKind kind)[]
        {
            ("chest", BlockEntityKind.Chest), ("trapped_chest", BlockEntityKind.Chest), ("ender_chest", BlockEntityKind.Chest),
            ("waxed_oxidized_copper_chest", BlockEntityKind.Chest), ("shulker_box", BlockEntityKind.ShulkerBox), ("light_blue_shulker_box", BlockEntityKind.ShulkerBox),
            ("skeleton_wall_skull", BlockEntityKind.Skull), ("player_head", BlockEntityKind.Skull), ("dragon_wall_head", BlockEntityKind.Skull),
            ("piston_head", BlockEntityKind.None), ("red_wall_banner", BlockEntityKind.Banner), ("oak_hanging_sign", BlockEntityKind.HangingSign),
            ("oak_wall_hanging_sign", BlockEntityKind.HangingSign), ("cherry_wall_sign", BlockEntityKind.Sign), ("bamboo_shelf", BlockEntityKind.Shelf),
            ("soul_campfire", BlockEntityKind.Campfire), ("exposed_copper_golem_statue", BlockEntityKind.CopperGolemStatue), ("stone", BlockEntityKind.None),
            ("bell", BlockEntityKind.Bell), ("end_gateway", BlockEntityKind.EndGateway), ("trial_spawner", BlockEntityKind.TrialSpawner),
        };
        var bad = want.Where(w => BlockEntityCatalog.KindOf(w.path) != w.kind).Select(w => $"{w.path}={BlockEntityCatalog.KindOf(w.path)}").ToList();
        check(bad.Count == 0, $"block entities: kind by block id ({want.Length} ids{(bad.Count > 0 ? ", wrong: " + string.Join(" ", bad) : "")})");
        check(BlockEntityCatalog.CodeDrawn("chest") && BlockEntityCatalog.CodeDrawn("white_banner") && BlockEntityCatalog.CodeDrawn("end_portal") &&
              !BlockEntityCatalog.CodeDrawn("oak_sign") && !BlockEntityCatalog.CodeDrawn("lectern") && !BlockEntityCatalog.CodeDrawn("campfire"),
              "block entities: chests, banners, portals are code-drawn; signs, lecterns, campfires keep their block models");
    }

    static void Transforms(Action<bool, string> check)
    {
        // Expected images from the 26.3 client's own transformations (vanilla renderers, checked with a scratch probe).
        check(Probe(BlockEntityCatalog.ChestTransform("east")) == "(0.0000,0.0000,1.0000) (0.0000,0.0000,0.0000) (0.0000,1.0000,1.0000) (1.0000,0.0000,1.0000)",
              "chest facing east: ChestRenderer turns -270 degrees about the block centre");
        check(Probe(BlockEntityCatalog.ShulkerTransform("north")) == "(0.5000,0.5000,-0.4995) (-0.4995,0.5000,-0.4995) (0.5000,0.5000,0.5000) (0.5000,1.4995,-0.4995)",
              "shulker box facing north: Direction.getRotation, 0.9995 scale, Y/Z flip");
        check(Probe(BlockEntityCatalog.SkullTransform(true, "east", 0)) == "(0.2500,0.2500,0.5000) (0.2500,0.2500,-0.5000) (0.2500,-0.7500,0.5000) (-0.7500,0.2500,0.5000)",
              "wall skull facing east: 0.25 off the wall, turned to face away from it");
        check(Probe(BlockEntityCatalog.SkullTransform(false, "north", 3)) == "(0.5000,0.0000,0.5000) (0.1173,0.0000,-0.4239) (0.5000,-1.0000,0.5000) (-0.4239,0.0000,0.8827)",
              "floor skull rotation 3: 67.5 degrees");
        check(Probe(BlockEntityCatalog.BannerTransform(false, "north", 5)) == "(0.5000,0.0000,0.5000) (0.2449,0.0000,1.1159) (0.5000,-0.6667,0.5000) (1.1159,0.0000,0.7551)",
              "standing banner rotation 5: 2/3 scale, Y and Z flipped");
        check(Probe(BlockEntityCatalog.PotTransform("east")) == "(1.0000,0.0000,0.0000) (1.0000,0.0000,1.0000) (1.0000,1.0000,0.0000) (0.0000,0.0000,0.0000)",
              "decorated pot facing east");
        check(Probe(BlockEntityCatalog.StatueTransform("east")) == "(0.5000,0.0000,0.5000) (0.5000,0.0000,1.5000) (0.5000,1.0000,0.5000) (-0.5000,0.0000,0.5000)",
              "copper golem statue facing east");
        check(Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_wall_sign[facing=north,waterlogged=false]", true)) ==
              "(0.5000,0.5208,0.8908) (0.4896,0.5208,0.8908) (0.5000,0.5104,0.8908) (0.5000,0.5208,0.8804)", "wall sign front text: on the board, 1/96 per font pixel");
        check(Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_sign[rotation=5,waterlogged=false]", false)) ==
              "(0.5431,0.8333,0.5179) (0.5471,0.8333,0.5082) (0.5431,0.8229,0.5179) (0.5527,0.8333,0.5218)", "standing sign back text, rotation 5");
        check(Probe(BlockEntityCatalog.SignTextTransform("minecraft:oak_wall_hanging_sign[facing=east,waterlogged=false]", true)) ==
              "(0.5730,0.3050,0.5000) (0.5730,0.3050,0.4859) (0.5730,0.2909,0.5000) (0.5871,0.3050,0.5000)", "wall hanging sign front text");
        foreach (var d in new[] { "north", "south", "east", "west", "up", "down" })
            if (BlockEntityCatalog.ShulkerTransform(d).Det <= 0) { check(false, "shulker transform keeps orientation " + d); return; }
        check(BlockEntityModels.ToBlock(BlockEntityCatalog.BannerTransform(true, "west", 0)).Det < 0 && BlockEntityModels.ToBlock(BlockEntityCatalog.SkullTransform(false, "", 7)).Det < 0,
              "figure-space meshes: block transforms are reflections (Unity's Z mirror makes them proper again)");
        var cf = BlockEntityCatalog.CampfireItemTransform("north", 1);
        var c0 = cf.Point(new Vector3(0, 0, 0));
        check(Near(c0, 0.1875f, 0.44921875f, 0.8125f), $"campfire slot 1 lies on the logs ({P(c0)})");
        var sh = BlockEntityCatalog.ShelfItemTransform("north", 0).Point(new Vector3(0, 0, 0));
        check(Near(sh, 0.5f + 0.3125f, 0.5f, 0.75f), $"shelf slot 0 facing north: left end seen from the front, a quarter in from the back ({P(sh)})");
        var sp = BlockEntityCatalog.SpawnerMobTransform(0, 0.6f, 1.95f);
        check(Math.Abs(sp.Dir(new Vector3(0, 1, 0)).y - 0.53125f / 1.95f * (float)Math.Cos(Math.PI / 6)) < 1e-4f, "spawner mob: 0.53125 / largest side, tilted 30 degrees");
    }

    static readonly List<BEPart> Tmp = new List<BEPart>();

    static List<BEPart> PartsOf(string state, BlockEntityData d = null) { BlockEntityCatalog.Parts(state, d, Tmp); return new List<BEPart>(Tmp); }

    static void Parts(Action<bool, string> check)
    {
        var l = PartsOf("minecraft:chest[facing=west,type=left,waterlogged=false]");
        check(l.Count == 1 && l[0].Model == "double_chest_left" && l[0].Texture == "entity/chest/normal_left", "double chest left half: double_chest_left with normal_left");
        l = PartsOf("minecraft:trapped_chest[facing=west,type=right,waterlogged=false]");
        check(l[0].Model == "double_chest_right" && l[0].Texture == "entity/chest/trapped_right", "trapped chest right half");
        check(PartsOf("minecraft:ender_chest[facing=north,waterlogged=false]")[0].Texture == "entity/chest/ender", "ender chest");
        check(PartsOf("minecraft:waxed_weathered_copper_chest[facing=north,type=single,waterlogged=false]")[0].Texture == "entity/chest/copper_weathered", "waxed weathered copper chest");
        check(PartsOf("minecraft:light_blue_shulker_box[facing=up]")[0].Texture == "entity/shulker/shulker_light_blue" &&
              PartsOf("minecraft:shulker_box[facing=up]")[0].Texture == "entity/shulker/shulker", "shulker boxes: dyed and plain textures");
        var head = PartsOf("minecraft:player_wall_head[facing=south,powered=false]", new BlockEntityData { Kind = "skull", Skin = "http://textures.minecraft.net/texture/ab", SkinUuid = "u" });
        check(head[0].Model == "player_head" && head[0].Texture.StartsWith(BlockEntityCatalog.SkinPrefix + "http://textures.minecraft.net/texture/ab|u"), "player head: its profile's skin");
        check(PartsOf("minecraft:dragon_head[powered=false,rotation=0]")[0].Pose == "dragon" && PartsOf("minecraft:zombie_head[powered=false,rotation=0]")[0].Model == "zombie_head" &&
              PartsOf("minecraft:wither_skeleton_wall_skull[facing=north,powered=false]")[0].Texture == "entity/skeleton/wither_skeleton", "mob heads: layer, texture, dragon jaw pose");
        var banner = PartsOf("minecraft:orange_wall_banner[facing=east]", new BlockEntityData { Kind = "banner", Patterns = new List<BannerLayer> { new BannerLayer("minecraft:cross", "black") } });
        check(banner.Count == 2 && banner[0].Model == "wall_banner" && banner[1].Model == "wall_banner#flag" && banner[1].Texture == "banner:orange|minecraft:cross=black" && banner[1].Pose == "flag",
              "wall banner: pole/bar plus the flag with its base colour and patterns");
        var pot = PartsOf("minecraft:decorated_pot[cracked=false,facing=north,waterlogged=false]", new BlockEntityData { Kind = "decorated_pot", Sherds = new[] { "minecraft:brick", "minecraft:arms_up_pottery_sherd", null, "minecraft:brick" } });
        check(pot.Count == 5 && pot[2].Only == "left" && pot[2].Texture == "entity/decorated_pot/arms_up_pottery_pattern" && pot[4].Texture == "entity/decorated_pot/decorated_pot_side" &&
              pot[3].Texture == "entity/decorated_pot/decorated_pot_side", "decorated pot: base plus four sides, sherd patterns (back, left, right, front)");
        check(PartsOf("minecraft:oxidized_copper_golem_statue[copper_golem_pose=star,facing=north,waterlogged=false]")[0].Model == "copper_golem_star" &&
              PartsOf("minecraft:oxidized_copper_golem_statue[copper_golem_pose=star,facing=north,waterlogged=false]")[0].Texture == "entity/copper_golem/copper_golem_oxidized",
              "copper golem statue: pose layer and weathered texture");
        check(PartsOf("minecraft:lectern[facing=north,has_book=true,powered=false]").Count == 1 && PartsOf("minecraft:lectern[facing=north,has_book=false,powered=false]").Count == 0,
              "lectern: the book only with has_book=true");
        check(PartsOf("minecraft:conduit[waterlogged=true]").Count == 1 && PartsOf("minecraft:conduit[waterlogged=true]", new BlockEntityData { Kind = "conduit", Active = true }).Count == 0,
              "conduit: the shell while inactive (active parts are animated)");
        check(PartsOf("minecraft:oak_sign[rotation=0,waterlogged=false]").Count == 0 && PartsOf("minecraft:stone").Count == 0, "signs and plain blocks: no model parts");
    }

    static void Protocol(Action<bool, string> check, Func<string, string> fixture)
    {
        var m = Json.Parse(File.ReadAllText(fixture("block_entities.json"))) as Dictionary<string, object>;
        var store = new BlockEntityStore();
        store.All[new Vector3Int(100, 64, -35)] = new BlockEntityData { Kind = "sign" };
        var changed = new List<Vector3Int>();
        store.Apply(m, changed);
        var sign = store.Get(new Vector3Int(105, 64, -35));
        var glow = store.Get(new Vector3Int(106, 64, -35));
        check(store.All.Count == 12 && changed.Count == 13 && store.Get(new Vector3Int(100, 64, -35)) == null, "block_entities fixture: 12 entries applied, 1 removed");
        check(sign.Front.Lines[0] == "Welcome" && sign.Front.Lines[3] == "" && !sign.Front.Glowing && glow.Front.Glowing && glow.Front.Color == "light_blue" && glow.Back.Lines[0] == "Closed",
              "block_entities: sign sides (lines, colour, glowing)");
        var banner = store.Get(new Vector3Int(107, 65, -35));
        check(banner.Patterns.Count == 2 && banner.Patterns[1].Pattern == "minecraft:creeper" && banner.Patterns[1].Color == "black", "block_entities: banner patterns");
        var head = store.Get(new Vector3Int(108, 64, -35));
        check(head.Skin.StartsWith("http://textures.minecraft.net/") && head.SkinName == "Steve" && head.SkinUuid.Length == 36, "block_entities: player head profile");
        check(store.Get(new Vector3Int(109, 64, -35)).Sherds[3] == "minecraft:skull_pottery_sherd" && store.Get(new Vector3Int(110, 64, -35)).Entity == "minecraft:zombie" &&
              Math.Abs(store.Get(new Vector3Int(110, 64, -35)).EntityHeight - 1.95f) < 1e-4f && store.Get(new Vector3Int(112, 64, -35)).Tier == 4 &&
              store.Get(new Vector3Int(113, 64, -35)).Items[0] == "minecraft:beef" && store.Get(new Vector3Int(113, 64, -35)).Items[1] == null &&
              store.Get(new Vector3Int(114, 64, -35)).Items.Length == 3 && store.Get(new Vector3Int(115, 64, -35)).Item == "minecraft:diamond" && store.Get(new Vector3Int(116, 64, -35)).Active,
              "block_entities: sherds, spawner mob and size, beacon tier, campfire/shelf/vault items, conduit");
        store.Prune(new Vector3Int(100, 0, -40), new Vector3Int(10, 256, 10));
        check(store.All.Count == 5 && store.Get(new Vector3Int(110, 64, -35)) == null, "block_entities: entries outside a new region are dropped");
    }

    static void Banner(Action<bool, string> check)
    {
        var solid = Enumerable.Repeat(new Color32(100, 50, 25, 255), 4).ToArray();
        var full = Enumerable.Repeat(new Color32(255, 255, 255, 255), 4).ToArray();
        var half = new[] { new Color32(255, 255, 255, 0), new Color32(255, 255, 255, 128), new Color32(0, 0, 0, 0), new Color32(255, 255, 255, 255) };
        var o = BannerImage.Compose(solid, 2, 2, new List<(Color32[], Color32)> { (full, new Color32(200, 0, 0, 255)), (half, new Color32(0, 0, 255, 255)) });
        check(o[0].r == 200 && o[0].b == 0 && o[3].r == 0 && o[3].b == 255 && o[1].r > 90 && o[1].r < 110 && o[1].b > 120 && o[1].b < 135 && o[2].r == 200,
              $"banner flag: base dye over the solid texture, patterns tinted and alpha-blended in order ({o[1].r},{o[1].b})");
        var layers = new List<BannerLayer>();
        BannerImage.ParseKey(BlockEntityCatalog.BannerKey("cyan", new BlockEntityData { Patterns = new List<BannerLayer> { new BannerLayer("minecraft:border", "light_gray") } }), out string bc, layers);
        check(bc == "cyan" && layers.Count == 1 && layers[0].Color == "light_gray" && BannerImage.PatternTexture("minecraft:border") == "entity/banner/border", "banner key round trip, pattern textures");
    }

    static WorldModel Column(params string[] states)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(1, states.Length, 1) };
        var pal = states.Distinct().ToList();
        w.Palette.AddRange(pal);
        w.Grid = states.Select(s => (ushort)pal.IndexOf(s)).ToArray();
        return w;
    }

    static void Beacon(Action<bool, string> check)
    {
        Color32 Dye(string n) => n == "white" ? new Color32(250, 255, 254, 255) : n == "red" ? new Color32(176, 46, 38, 255) : n == "blue" ? new Color32(60, 68, 170, 255) : new Color32(255, 255, 255, 255);
        var w = Column("minecraft:beacon", "minecraft:air", "minecraft:red_stained_glass", "minecraft:blue_stained_glass_pane[east=false,north=false,south=false,waterlogged=false,west=false]",
                       "minecraft:blue_stained_glass", "minecraft:glass", "minecraft:air");
        var s = BeaconBeam.Sections(w, 0, 0, 0, Dye);
        check(s.Count == 4 && s[0].Y == 0 && s[0].Height == 2 && s[0].Color.r == 250 && s[1].Y == 2 && s[1].Height == 1 && s[1].Color.r == 176 &&
              s[2].Y == 3 && s[2].Height == 1 && s[2].Color.r == (176 + 60) / 2 && s[3].Y == 4 && s[3].Height == 3 && s[3].Color.r == ((176 + 60) / 2 + 60) / 2,
              $"beacon beam: white from the beacon, the first glass replaces, the next averages, others extend ({string.Join(" ", s.Select(x => $"{x.Y}+{x.Height}:{x.Color.r}"))})");
        check(BeaconBeam.Sections(Column("minecraft:beacon", "minecraft:red_stained_glass", "minecraft:stone", "minecraft:air"), 0, 0, 0, Dye).Count == 0 &&
              BeaconBeam.Sections(Column("minecraft:beacon", "minecraft:bedrock"), 0, 0, 0, Dye).Count == 1, "beacon beam: an opaque block stops it (bedrock doesn't)");
    }

    static void Scan(Action<bool, string> check)
    {
        var pal = new List<string> { "minecraft:air", "minecraft:chest[facing=north,type=single,waterlogged=false]", "minecraft:stone", "minecraft:lectern[facing=north,has_book=false,powered=false]",
                                     "minecraft:lectern[facing=north,has_book=true,powered=false]", "minecraft:oak_sign[rotation=0,waterlogged=false]" };
        var mask = BlockEntityScan.Mask(pal);
        check(!mask[0] && mask[1] && !mask[2] && !mask[3] && mask[4] && mask[5], "scan mask: chests, signs, lecterns with a book");
        var dims = new Vector3Int(20, 18, 17);
        var grid = new ushort[dims.x * dims.y * dims.z];
        int Idx(int x, int y, int z) => (y * dims.z + z) * dims.x + x;
        grid[Idx(1, 2, 3)] = 1; grid[Idx(17, 2, 3)] = 5; grid[Idx(5, 17, 16)] = 4; grid[Idx(6, 6, 6)] = 3; grid[Idx(7, 7, 7)] = 2;
        var all = new List<Vector3Int>();
        BlockEntityScan.Cells(grid, dims, mask, null, all);
        var some = new List<Vector3Int>();
        BlockEntityScan.Cells(grid, dims, mask, new[] { new Vector3Int(1, 0, 0), new Vector3Int(0, 1, 1) }, some);
        check(all.Count == 3 && all.Contains(new Vector3Int(17, 2, 3)) && some.Count == 2 && some.Contains(new Vector3Int(5, 17, 16)) && !some.Contains(new Vector3Int(1, 2, 3)),
              "scan: whole grid and listed (edge) sections");
    }

    static FigureMesh Bake(BEPart p) => BlockEntityModels.Bake(EntityModels.Get(p.Model), BlockEntityCatalog.Pose(p.Pose), p.Only);

    static (Vector3 min, Vector3 max) BlockBounds(string state, int part = 0, BlockEntityData d = null)
    {
        var p = PartsOf(state, d)[part];
        var fm = Bake(p);
        BlockEntityModels.Bounds(fm, BlockEntityModels.ToBlock(p.M), out var min, out var max);
        return (min, max);
    }

    static void Models(Action<bool, string> check)
    {
        var missing = new List<string>();
        foreach (var layer in new[] { "chest", "double_chest_left", "double_chest_right", "shulker_box", "skeleton_skull", "wither_skeleton_skull", "player_head", "zombie_head",
                                      "creeper_head", "dragon_skull", "piglin_head", "standing_banner", "standing_banner#flag", "wall_banner", "wall_banner#flag", "bell",
                                      "decorated_pot_base", "decorated_pot_sides", "conduit#shell", "conduit#cage", "conduit#eye", "conduit#wind", "copper_golem",
                                      "copper_golem_running", "copper_golem_sitting", "copper_golem_star", "book" })
            if (EntityModels.Get(layer) == null) missing.Add(layer);
        if (missing.Contains("chest") && missing.Contains("book")) { Console.WriteLine("SKIP block entity models: this entity_models.json predates 0.4.0 (regenerate it with DumpEntityModels)"); return; }
        check(missing.Count == 0, "entity_models.json has the block-entity layers" + (missing.Count > 0 ? " (missing " + string.Join(" ", missing) + ")" : ""));
        if (missing.Count > 0) return;
        var b = BlockBounds("minecraft:chest[facing=north,type=single,waterlogged=false]");
        check(Near(b.min, 1 / 16f, 0, 0) && Near(b.max, 15 / 16f, 14 / 16f, 15 / 16f), $"chest: 14x14x14 px in the middle of the block, lid closed, the latch on the north side ({P(b.min)}..{P(b.max)})");
        var bl = BlockBounds("minecraft:chest[facing=north,type=left,waterlogged=false]");
        var br = BlockBounds("minecraft:chest[facing=north,type=right,waterlogged=false]");
        check(Near(bl.min, 1 / 16f, 0, 0) && Near(bl.max, 1, 14 / 16f, 15 / 16f) && Near(br.min, 0, 0, 0) && Near(br.max, 15 / 16f, 14 / 16f, 15 / 16f),
              $"double chest facing north: the left half reaches east (its partner is clockwise of the facing), the right half west ({P(bl.min)}..{P(bl.max)})");
        var sb = BlockBounds("minecraft:red_shulker_box[facing=up]");
        check(Near(sb.min, 0, 0, 0, 1e-3f) && Near(sb.max, 1, 1, 1, 1e-3f), $"shulker box fills its block ({P(sb.min)}..{P(sb.max)})");
        var sk = BlockBounds("minecraft:skeleton_skull[powered=false,rotation=0]");
        var sw = BlockBounds("minecraft:skeleton_wall_skull[facing=north,powered=false]");
        check(Near(sk.min, 0.25f, 0, 0.25f) && Near(sk.max, 0.75f, 0.5f, 0.75f) && Near(sw.min, 0.25f, 0.25f, 0.5f) && Near(sw.max, 0.75f, 0.75f, 1),
              $"skulls: 8 px cube on the floor, on the south wall when facing north ({P(sw.min)}..{P(sw.max)})");
        var dh = BlockBounds("minecraft:dragon_head[powered=false,rotation=0]");
        check(dh.max.y - dh.min.y > 0.5f && dh.max.z - dh.min.z > 0.9f, $"dragon head: bigger than a skull, snout and jaw ({P(dh.min)}..{P(dh.max)})");
        var bp = BlockBounds("minecraft:white_banner[rotation=0]", 0);
        var bf = BlockBounds("minecraft:white_banner[rotation=0]", 1);
        check(Math.Abs(bp.max.y - 44 / 16f * 2 / 3) < 0.01f && bp.min.y > -0.01f && bf.min.y > 0.1f && bf.max.y < bp.max.y + 0.01f && bf.max.x - bf.min.x > 0.8f,
              $"standing banner: 1.83 blocks of pole and bar, the flag below the bar ({P(bf.min)}..{P(bf.max)})");
        var wf = BlockBounds("minecraft:white_wall_banner[facing=north]", 1);
        check(wf.min.z > 0.75f && wf.min.y < -0.4f, $"wall banner facing north hangs on the south wall and reaches below the block ({P(wf.min)}..{P(wf.max)})");
        var bell = BlockBounds("minecraft:bell[attachment=floor,facing=north,powered=false]");
        check(Near(bell.min, 4 / 16f, 4 / 16f, 4 / 16f) && Near(bell.max, 12 / 16f, 13 / 16f, 12 / 16f), $"bell body under its frame ({P(bell.min)}..{P(bell.max)})");
        var pot = BlockBounds("minecraft:decorated_pot[cracked=false,facing=north,waterlogged=false]", 0);
        var front = BlockBounds("minecraft:decorated_pot[cracked=false,facing=north,waterlogged=false]", 4);
        check(pot.min.y > -0.01f && pot.max.y > 1.1f && Math.Abs(front.max.z - front.min.z) < 0.01f && front.max.y - front.min.y > 0.9f,
              $"decorated pot: base with its neck above the block, flat sides ({P(pot.max)}; front {P(front.min)}..{P(front.max)})");
        var st = BlockBounds("minecraft:copper_golem_statue[copper_golem_pose=standing,facing=north,waterlogged=false]");
        check(st.min.y > -0.02f && st.max.y > 0.8f && st.max.y < 1.6f, $"copper golem statue stands on the block, upright ({P(st.min)}..{P(st.max)})");
        var bk = BlockBounds("minecraft:lectern[facing=north,has_book=true,powered=false]");
        check(bk.min.y > 0.7f && bk.max.y < 1.3f, $"lectern book on the slope ({P(bk.min)}..{P(bk.max)})");
        int verts = Bake(PartsOf("minecraft:chest[facing=north,type=single,waterlogged=false]")[0]).VertexCount;
        check(verts == 3 * 24, $"chest mesh: three boxes, 72 vertices ({verts})");
    }

    /// <summary>With BlockEntityRenderer the terrain's baker leaves code-drawn block entities out; icons keep their boxes.</summary>
    public static void Baker(Action<bool, string> check, string jar)
    {
        using var assets = new ZipAssets(jar);
        var with = new BlockModels(assets);
        var without = new BlockModels(assets) { EntityBoxes = false };
        int Q(BlockModels m, string s) => m.Prepare(s, new FaceKey[6])?.Quads.Length ?? -1;
        string chest = "minecraft:chest[facing=north,type=single,waterlogged=false]", bell = "minecraft:bell[attachment=floor,facing=north,powered=false]",
               sign = "minecraft:oak_sign[rotation=0,waterlogged=false]", portal = "minecraft:end_portal", shulker = "minecraft:shulker_box[facing=up]";
        check(Q(with, chest) > 0 && Q(without, chest) == 0 && Q(without, portal) == 0 && Q(without, shulker) == 0,
              $"terrain baker: no placeholder boxes for code-drawn block entities (chest {Q(with, chest)} -> {Q(without, chest)} quads)");
        check(Q(without, bell) > 0 && Q(without, bell) < Q(with, bell) && Q(without, sign) == Q(with, sign) && Q(without, sign) > 0,
              $"terrain baker: the bell keeps its frame (body drawn by code), signs keep their 26.x models ({Q(with, bell)} -> {Q(without, bell)})");
        BlockRules.ResetShapes();
    }

    /// <summary>--measure-blocks DIR: terrain faces with and without the placeholder boxes, and the block entities
    /// BlockEntityRenderer would draw (instances, instanced groups and draw calls, triangles) per saved snapshot.</summary>
    public static void Measure(string dir, string jar)
    {
        using var assets = new ZipAssets(jar);
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var w = new WorldModel();
            w.ApplySnapshot(ChunkSnapshot.From(Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>));
            long[] faces = new long[2];
            for (int pass = 0; pass < 2; pass++)
            {
                BlockRules.ResetShapes();
                var pf = new PackFaces(assets, pass == 0);
                pf.Prewarm(w);
                var mesh = new MeshData();
                var n = w.SectionCount;
                for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
                {
                    mesh.Clear();
                    GreedyMesher.Build(w, new Vector3Int(x, y, z), pf, mesh);
                    GreedyMesher.BuildSpecials(w, new Vector3Int(x, y, z), pf, mesh);
                    faces[pass] += mesh.Verts.Count / 4;
                }
            }
            BlockRules.ResetShapes();
            var mask = BlockEntityScan.Mask(w.Palette);
            var cells = new List<Vector3Int>();
            BlockEntityScan.Cells(w.Grid, w.Dims, mask, null, cells);
            var byKind = new Dictionary<BlockEntityKind, int>();
            var groups = new Dictionary<string, int>();
            var tris = new Dictionary<string, int>();
            long boxFaces = 0;
            foreach (var c in cells)
            {
                string st = w.State(w.Get(c.x, c.y, c.z));
                var k = BlockEntityCatalog.KindOfState(st);
                byKind.TryGetValue(k, out int nk); byKind[k] = nk + 1;
                foreach (var p in PartsOf(st))
                {
                    string key = p.MeshKey + "@" + p.Texture;
                    groups.TryGetValue(key, out int ng); groups[key] = ng + 1;
                    if (!tris.ContainsKey(p.MeshKey)) { var fm = EntityModels.Loaded ? Bake(p) : null; tris[p.MeshKey] = fm != null ? fm.Tris.Count / 3 : 12; }
                }
            }
            long beTris = 0; int calls = 0;
            foreach (var g in groups) { calls += (g.Value + BlockEntityRenderer_Chunk - 1) / BlockEntityRenderer_Chunk; beTris += (long)g.Value * tris[g.Key.Substring(0, g.Key.IndexOf('@'))]; }
            boxFaces = faces[0] - faces[1];
            Console.WriteLine($"MEASURE_BLOCKS {Path.GetFileName(f)} dims={w.Dims.x}x{w.Dims.y}x{w.Dims.z} terrain_faces_with_boxes={faces[0]} without={faces[1]} (placeholder faces {boxFaces}) " +
                              $"block_entities={cells.Count} [{string.Join(" ", byKind.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "=" + kv.Value))}] " +
                              $"instanced_groups={groups.Count} draw_calls={calls} block_entity_tris={beTris} models={(EntityModels.Loaded ? "entity_models.json" : "fallback boxes")}");
        }
    }

    const int BlockEntityRenderer_Chunk = 256;   // BlockEntityRenderer.Chunk (instances per draw call)

    static (Color32[] px, int w, int h)? Png(ZipArchive z, string path)
    {
        var e = z.GetEntry(path + (path.EndsWith(".png") ? "" : ".png"));
        if (e == null) return null;
        using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
        var px = PngDecoder.Decode(ms.ToArray(), out int w, out int h);
        return px == null ? ((Color32[], int, int)?)null : (px, w, h);
    }

    static void Font(Action<bool, string> check, string jar)
    {
        using var z = ZipFile.OpenRead(jar);
        Dictionary<string, object> ReadJson(string p) { var e = z.GetEntry(p); if (e == null) return null; using var r = new StreamReader(e.Open()); return Json.Parse(r.ReadToEnd()) as Dictionary<string, object>; }
        var font = BitmapFont.Load(ReadJson, p => Png(z, p));
        check(font != null && font.GlyphCount > 300 && font.AtlasW > 0, $"sign font from the pack: {font?.GlyphCount} glyphs, atlas {font?.AtlasW}x{font?.AtlasH}");
        if (font == null) return;
        check(font.Advance(' ') == 4 && font.Advance('i') < font.Advance('a') && font.Advance('W') >= font.Advance('a') && font.Width("ab") == font.Advance('a') + font.Advance('b'),
              $"font metrics: space 4, glyph widths measured from the sheet (i {font.Advance('i')}, a {font.Advance('a')}, W {font.Advance('W')})");
        check(font.TryGlyph('é', out _) && font.TryGlyph('Ж', out _), "font: accented and Cyrillic glyphs from the pack's other sheets");
        string longLine = "The quick brown fox jumps over the lazy dog";
        string fit = SignTextLayout.Fit(font, longLine, 90);
        check(font.Width(fit) <= 90 && fit.Length < longLine.Length && longLine.StartsWith(fit) && !fit.EndsWith(" "), $"sign lines wrap at the last space within 90 px (\"{fit}\")");
        var quads = new List<TextQuad>();
        var side = new SignSideText { Lines = new[] { "Hi", "", "", "" }, Color = "black" };
        SignTextLayout.Layout(font, side, new Color32(0, 0, 0, 255), 10, 90, quads);
        float w = font.Width("Hi");
        check(quads.Count == 2 && quads[0].X0 == -(int)w / 2 && quads[0].Y0 == -20 && quads[0].Color.r == 0 && !quads[0].FullBright,
              $"sign text: centred, first line 2 lines above the middle, black dimmed by 0.4 and lit ({quads.FirstOrDefault().X0},{quads.FirstOrDefault().Y0})");
        quads.Clear();
        side.Glowing = true; side.Color = "red";
        SignTextLayout.Layout(font, side, new Color32(255, 0, 0, 255), 10, 90, quads);
        check(quads.Count == 18 && quads.Count(q => q.Color.r == 255) == 2 && quads.Count(q => q.Color.r == 102) == 16 && quads.All(q => q.FullBright) &&
              quads.Where(q => q.Color.r == 102).All(q => q.Z < 0),
              "glowing sign text: full colour at full brightness over an 8-way outline in the 0.4 colour, behind it");
        quads.Clear();
        side.Color = "black";
        SignTextLayout.Layout(font, side, new Color32(0, 0, 0, 255), 10, 90, quads);
        check(quads.Count(q => q.Color.r == 0xF0 && q.Color.g == 0xEB) == 16, "black glowing text: cream outline");
        check(!EntityModels.TextDye("red", out int red) || red == 0xFF0000, "sign text colours from entity_models.json (files from 0.4.0's tool; older ones fall back to the dye colours)");
    }
}
