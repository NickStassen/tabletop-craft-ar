// Engine-free tests for entities and actions (PROTOCOL.md 4.21-4.22): the entity store against the fixtures, the
// player_positions extras, actions parsing, the generated box models (winding, UVs, feet on the ground), animation,
// and, with a client jar (--jar / MC_CLIENT_JAR), that every catalog model and texture and every item's sprite or cube
// resolves in the real pack.
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class EntityTests
{
    /// <summary>models: the user's generated entity_models.json (--entity-models PATH or TABLETOP_ENTITY_MODELS); the model
    /// tests skip without it, as the jar tests do without a jar.</summary>
    public static void Run(Action<bool, string> check, Func<string, string> fixture, string jar, string models)
    {
        Store(check, fixture);
        Actions(check, fixture);
        Timing(check);
        NoData(check);
        if (models != null && EntityModels.LoadFile(models))
        {
            Console.WriteLine("entity models: " + EntityModels.Status);
            Models(check);
            Animation(check);
        }
        else Console.WriteLine("SKIP entity model tests: generate entity_models.json with tools/entity_models/DumpEntityModels.java and pass --entity-models PATH or set TABLETOP_ENTITY_MODELS"
                               + (models != null ? " (" + EntityModels.Status + ")" : ""));
        if (jar != null) Pack(check, jar);
        else Console.WriteLine("SKIP entity pack tests: set MC_CLIENT_JAR or pass --jar PATH");
    }

    static Dictionary<string, object> Load(Func<string, string> fixture, string name) =>
        Json.Parse(System.IO.File.ReadAllText(fixture(name))) as Dictionary<string, object>;

    static void Store(Action<bool, string> check, Func<string, string> fixture)
    {
        var s = new EntityStore();
        var removed = new List<int>();
        s.Apply(Load(fixture, "entities.json"), removed);
        var boat = s.Get(101); var pig = s.Get(102); var zombie = s.Get(104); var item = s.Get(105); var frame = s.Get(106);
        check(s.All.Count == 6 && boat.Path == "oak_boat" && boat.Passengers.SequenceEqual(new[] { 7, 102 }) && pig.Vehicle == 101 && pig.Baby && pig.Variant == "temperate",
              "entities fixture: boat with passengers, baby pig riding it");
        check(Math.Abs(pig.BodyYaw - 90) < 1e-4 && Math.Abs(pig.Yaw - 80) < 1e-4 && Math.Abs(boat.BodyYaw - 90) < 1e-4 && !boat.HasBodyYaw,
              "entities: body yaw for living entities, yaw doubles as body yaw otherwise");
        check(item.Item == "minecraft:diamond" && item.Count == 3 && frame.Facing == "north" && frame.Rotation == 2 && zombie.Hand("head") == "minecraft:leather_helmet",
              "entities: item, frame facing/rotation, equipment");
        int zombieLooks = zombie.LooksVersion, boatLooks = boat.LooksVersion;
        s.Apply(Load(fixture, "entities_delta.json"), removed);
        check(removed.SequenceEqual(new[] { 105 }) && s.Get(105) == null && Math.Abs(boat.Pos.x - 113f) < 1e-4 && Math.Abs(boat.Yaw - 95) < 1e-4,
              "entities delta: moved, turned, removed");
        check(zombie.Hand("head") == null && zombie.Hand("main_hand") == "minecraft:iron_sword" && zombie.LooksVersion > zombieLooks && boat.LooksVersion == boatLooks,
              "entities delta: equipment replaced whole (looks change), moves don't rebuild");
        // A delta for an unknown id is ignored; a full list drops what it doesn't name and clears unlisted fields.
        s.Apply(Json.Parse("{\"full\":false,\"entities\":[{\"id\":999,\"pos\":[0,0,0]}]}") as Dictionary<string, object>, removed);
        check(s.Get(999) == null, "entities: delta for an unknown id ignored");
        removed.Clear();
        s.Apply(Json.Parse("{\"full\":true,\"entities\":[{\"id\":102,\"type\":\"minecraft:pig\",\"pos\":[1,2,3],\"yaw\":0,\"pitch\":0}]}") as Dictionary<string, object>, removed);
        check(s.All.Count == 1 && !s.Get(102).Baby && s.Get(102).Vehicle == -1 && removed.Count == 4, $"entities full list replaces the set (removed {removed.Count})");

        var rider = PlayerInfo.From((Dictionary<string, object>)Load(fixture, "player_positions_riding.json").A("players")[0]);
        check(rider.Id == 7 && rider.Vehicle == 101 && rider.Pose == "riding" && rider.Equipment["off_hand"] == "minecraft:torch" && rider.Equipment.Count == 6,
              "player_positions extras: id, vehicle, pose, equipment");
        var plain = PlayerInfo.From((Dictionary<string, object>)Load(fixture, "player_positions.json").A("players")[0]);
        check(plain.Vehicle == -1 && plain.Pose == null && plain.Equipment == null && plain.Id == -1, "player_positions without extras: no vehicle, standing, empty hands");
    }

    static void Actions(Action<bool, string> check, Func<string, string> fixture)
    {
        var list = new List<GameAction>();
        GameAction.Parse(Load(fixture, "actions.json"), list);
        check(list.Count == 6 && list[0].Kind == ActionKind.Swing && list[0].Id == 7 && !list[0].OffHand, "actions: swing");
        check(list[1].Kind == ActionKind.Dig && list[1].Stage == 4 && list[1].Pos.x == 105 && list[1].Pos.z == -36, "actions: dig stage at a block");
        check(list[2].Kind == ActionKind.Break && list[2].Block == "minecraft:stone" && list[3].Kind == ActionKind.Place && list[4].Kind == ActionKind.Hurt && list[4].Crit && list[4].Id == 104,
              "actions: break, place, hurt with crit");
    }

    // ---- model geometry ----

    struct M { public float[] a; }   // 4x4 row-major

    static M Mul(M x, M y)
    {
        var r = new float[16];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) for (int k = 0; k < 4; k++) r[i * 4 + j] += x.a[i * 4 + k] * y.a[k * 4 + j];
        return new M { a = r };
    }
    static M Id() => new M { a = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 } };
    static M T(float x, float y, float z) { var m = Id(); m.a[3] = x; m.a[7] = y; m.a[11] = z; return m; }
    static M S(float x, float y, float z) { var m = Id(); m.a[0] = x; m.a[5] = y; m.a[10] = z; return m; }
    static M RX(float r) { var m = Id(); float c = (float)Math.Cos(r), s = (float)Math.Sin(r); m.a[5] = c; m.a[6] = -s; m.a[9] = s; m.a[10] = c; return m; }
    static M RY(float r) { var m = Id(); float c = (float)Math.Cos(r), s = (float)Math.Sin(r); m.a[0] = c; m.a[2] = s; m.a[8] = -s; m.a[10] = c; return m; }
    static M RZ(float r) { var m = Id(); float c = (float)Math.Cos(r), s = (float)Math.Sin(r); m.a[0] = c; m.a[1] = -s; m.a[4] = s; m.a[5] = c; return m; }

    /// <summary>Figure-space bounds of a whole model (root at (0, 24, 0) like a living entity), as EntityFigure places parts.</summary>
    static (float minY, float maxY, float minX, float maxX) Bounds(EModel m)
    {
        float minY = 1e9f, maxY = -1e9f, minX = 1e9f, maxX = -1e9f;
        void Walk(EModelPart p, M parent)
        {
            EntityModels.Pivot(p, out float px, out float py, out float pz);
            var w = Mul(parent, Mul(T(px, py, pz), Mul(Mul(RZ(p.Rz), Mul(RY(p.Ry), RX(p.Rx))), S(p.Sx, p.Sy, p.Sz))));
            var fm = new FigureMesh();
            EntityModels.BuildPart(m, p, fm);
            for (int i = 0; i < fm.VertexCount; i++)
            {
                float x = fm.Pos[3 * i], y = fm.Pos[3 * i + 1], z = fm.Pos[3 * i + 2];
                float wx = w.a[0] * x + w.a[1] * y + w.a[2] * z + w.a[3], wy = w.a[4] * x + w.a[5] * y + w.a[6] * z + w.a[7];
                minY = Math.Min(minY, wy); maxY = Math.Max(maxY, wy); minX = Math.Min(minX, wx); maxX = Math.Max(maxX, wx);
            }
            foreach (var c in p.Children) Walk(c, w);
        }
        Walk(m.Root, T(0, 24, 0));
        return (minY, maxY, minX, maxX);
    }

    static void Models(Action<bool, string> check)
    {
        var names = EntityModels.Names.ToList();
        int bad = 0, faces = 0, cubes = 0, models = 0;
        string firstBad = null;
        foreach (var n in names)
        {
            var m = EntityModels.Get(n);
            if (m == null) { bad++; firstBad = firstBad ?? n + " (null)"; continue; }
            models++;
            cubes += m.CubeCount;
            foreach (var p in m.Parts)
            {
                var fm = new FigureMesh();
                EntityModels.BuildPart(m, p, fm);
                faces += fm.VertexCount / 4;
                // A few vanilla cubes reach a little past their texture (llama_baby chests): only flag nonsense.
                for (int k = 0; k < fm.Uv.Count; k++) if (float.IsNaN(fm.Uv[k]) || fm.Uv[k] < -0.5f || fm.Uv[k] > 1.5f) { bad++; firstBad = firstBad ?? $"{n}/{p.Name} uv {fm.Uv[k]}"; break; }
                for (int t = 0; t < fm.Tris.Count; t += 3)
                {
                    int a = fm.Tris[t], b = fm.Tris[t + 1], c = fm.Tris[t + 2];
                    float ux = fm.Pos[3 * b] - fm.Pos[3 * a], uy = fm.Pos[3 * b + 1] - fm.Pos[3 * a + 1], uz = fm.Pos[3 * b + 2] - fm.Pos[3 * a + 2];
                    float vx = fm.Pos[3 * c] - fm.Pos[3 * a], vy = fm.Pos[3 * c + 1] - fm.Pos[3 * a + 1], vz = fm.Pos[3 * c + 2] - fm.Pos[3 * a + 2];
                    float cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                    float area = cx * cx + cy * cy + cz * cz;
                    float dot = cx * fm.Normal[3 * a] + cy * fm.Normal[3 * a + 1] + cz * fm.Normal[3 * a + 2];
                    if (area > 1e-6f && dot <= 0) { bad++; firstBad = firstBad ?? $"{n}/{p.Name} winding"; break; }
                }
            }
        }
        check(bad == 0 && names.Count > 150, $"entity models: {names.Count} names ({models} parsed), {cubes} cubes, {faces} faces, all wound outward, UVs sane{(firstBad == null ? "" : " FIRST BAD " + firstBad)}");

        // Cow head: pivot (0, 4, -8) -> figure (0, -4, 8); its front (north) face uses skin (6..14, 6..14) of 64x64.
        var cow = EntityModels.Get("cow");
        var head = cow.Find("head");
        EntityModels.Pivot(head, out float hx, out float hy, out float hz);
        var hm = new FigureMesh();
        EntityModels.BuildPart(cow, head, hm);
        var front = Enumerable.Range(0, 24).Where(i => hm.Normal[3 * i + 2] > 0.5f).ToList();   // figure +Z = the game's front
        float u0 = front.Min(i => hm.Uv[2 * i]) * 64, u1 = front.Max(i => hm.Uv[2 * i]) * 64, v0 = (1 - front.Max(i => hm.Uv[2 * i + 1])) * 64;
        check(hx == 0 && hy == -4 && hz == 8 && front.Count == 4 && u0 == 6 && u1 == 14 && v0 == 6, $"cow head pivot and front face UVs (u {u0}..{u1}, v {v0})");

        // Feet on the ground (y = 0 with the root at 24) and plausible heights, root scale included (villager 0.9375).
        foreach (var (name, height) in new[] { ("cow", 1.4f), ("pig", 0.9f), ("sheep", 1.3f), ("zombie", 1.95f), ("villager", 1.95f), ("chicken", 0.7f),
                                                ("horse", 1.6f), ("creeper", 1.7f), ("skeleton", 1.99f), ("wolf", 0.85f), ("iron_golem", 2.7f), ("cow_baby", 0.7f) })
        {
            var (minY, maxY, _, _) = Bounds(EntityModels.Get(name));
            float h = (maxY - minY) / 16f;
            check(Math.Abs(minY) < 0.6f && h > height * 0.6f && h < height * 1.5f, $"{name}: feet at y {minY:F2} px, model {h:F2} blocks tall (bbox {height})");
        }
        // Mirrored cubes: the cow's left legs mirror the right legs' skin (same UV area) across x.
        var lm = new FigureMesh(); var rm = new FigureMesh();
        EntityModels.BuildPart(cow, cow.Find("left_front_leg"), lm); EntityModels.BuildPart(cow, cow.Find("right_front_leg"), rm);
        check(lm.Uv.OrderBy(x => x).SequenceEqual(rm.Uv.OrderBy(x => x)) && cow.Find("left_front_leg").Cubes[0].Mirror, "mirrored leg shares the skin area");
        check(EntityModels.Get("boat/spruce") != null && EntityModels.Get("boat/spruce").CubeCount == EntityModels.Get("boat/oak").CubeCount && EntityModels.Get("nope") == null,
              "aliases resolve (boat/spruce = boat/oak); unknown model -> null");
    }

    static void Animation(Action<bool, string> check)
    {
        var rig = EntityRig.For(EntityModels.Get("cow"));
        var rot = new float[rig.Roles.Length * 3];
        var a = new EntityAnimInput { WalkPos = 0, WalkSpeed = 1, HeadYaw = 30, HeadPitch = 10 };
        EntityAnimation.Pose(rig, a, rot);
        int head = EntityModels.Get("cow").Find("head").Index, rh = EntityModels.Get("cow").Find("right_hind_leg").Index, lh = EntityModels.Get("cow").Find("left_hind_leg").Index;
        check(Math.Abs(rot[3 * head + 1] - 30 * Math.PI / 180) < 1e-4 && Math.Abs(rot[3 * head] - 10 * Math.PI / 180) < 1e-4, "cow head turns with the head yaw/pitch");
        check(Math.Abs(rot[3 * rh] - 1.4f) < 1e-4 && Math.Abs(rot[3 * lh] + 1.4f) < 1e-4, "cow hind legs swing opposite at full speed (1.4 rad)");
        var zrig = EntityRig.For(EntityModels.Get("zombie"));
        var zrot = new float[zrig.Roles.Length * 3];
        EntityAnimation.Pose(zrig, new EntityAnimInput { ArmsForward = true }, zrot);
        int ra = EntityModels.Get("zombie").Find("right_arm").Index;
        check(zrig.Humanoid && Math.Abs(zrot[3 * ra] + Math.PI / 2) < 0.1, "zombie arms held forward");
        EntityAnimation.Pose(zrig, new EntityAnimInput { Riding = true }, zrot);
        int rl = EntityModels.Get("zombie").Find("right_leg").Index;
        check(Math.Abs(zrot[3 * rl] + 1.4137f) < 1e-3, "riding pose: legs forward");
        EntityAnimation.Pose(zrig, new EntityAnimInput { AttackTime = 0.5f }, zrot);
        check(zrot[3 * ra] < -0.7f && zrot[3 * ra] > -0.8f, $"attack swing at half way: right arm -0.76 rad like HumanoidModel ({zrot[3 * ra]:F2} rad)");
    }

    /// <summary>Animation timing that needs no model data.</summary>
    static void Timing(Action<bool, string> check)
    {
        var swing = new SwingState();
        float first = swing.Step(0.05f);
        swing.Start(false);
        float mid = 0; for (int i = 0; i < 3; i++) mid = swing.Step(0.05f);
        float end = 0; for (int i = 0; i < 4; i++) end = swing.Step(0.05f);
        check(first == 0 && Math.Abs(mid - 0.5f) < 1e-3 && end == 0, $"arm swing lasts 6 ticks (half {mid}, after {end})");
        var w = new WalkState();
        for (int i = 0; i < 60; i++) w.Step(1 / 60f, 4.3f / 60f, 0);
        check(Math.Abs(w.Speed - 0.86f) < 0.03f, $"walk speed from movement ({w.Speed:F2})");
    }

    /// <summary>Without entity_models.json: no models (entities become boxes), the catalog still resolves, dyes fall back;
    /// a minimal file loads, bumps the generation and resolves aliases; junk is refused with a status.</summary>
    static void NoData(Action<bool, string> check)
    {
        EntityModels.Unload();
        int g0 = EntityModels.Generation;
        var cow = EntityCatalog.Resolve(new EntityInfo { Type = "minecraft:cow", Variant = "warm", Width = 0.9f, Height = 1.4f });
        var sheep = EntityCatalog.Resolve(new EntityInfo { Type = "minecraft:sheep", Color = "red" });
        check(!EntityModels.Loaded && EntityModels.Get("cow") == null && cow.Kind == EntityKind.Model && cow.Model == "warm_cow" && cow.Texture == "entity/cow/cow_warm",
              "no model data: Get() is null, the catalog still names model and texture (the figure draws a box)");
        check(sheep.ExtraTint.r == sheep.ExtraTint.g && EntityCatalog.DyeColor("red", EntityLook.White).r == 255, "no model data: dye colours fall back (plain wool)");
        check(!EntityModels.LoadJson("{\"models\":{}}") && EntityModels.Status.Contains("format") && !EntityModels.LoadJson("not json") && !EntityModels.Loaded,
              $"junk refused ({EntityModels.Status})");
        string mini = "{\"format\":1,\"minecraft\":\"test\",\"dyes\":{\"red\":11546150},\"models\":{\"box\":{\"t\":[16,16],\"r\":[\"\",[0,0,0,0,0,0],[],[[\"body\",[0,16,0,0,0,0],[[0,0,-2,-4,-2,4,8,4]],[]]]]},\"box2\":\"box\"}}";
        check(EntityModels.LoadJson(mini) && EntityModels.Generation > g0 && EntityModels.Get("box2")?.CubeCount == 1 && EntityCatalog.DyeColor("red", EntityLook.White).r == 0xB0,
              $"minimal file loads: aliases resolve, dyes from the file ({EntityModels.Status})");
        EntityModels.Unload();
    }

    // ---- against the real pack ----

    static void Pack(Action<bool, string> check, string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        bool Has(string texture) => zip.GetEntry(ItemModels.TexturePath(texture)) != null;
        Dictionary<string, object> Read(string path)
        {
            var e = zip.GetEntry(path);
            if (e == null) return null;
            using var r = new System.IO.StreamReader(e.Open());
            return Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
        }

        // Every catalog look: model in the data, textures (and overlays, second layers) in the pack.
        var samples = new List<EntityInfo>();
        void Add(string type, string variant = null, bool baby = false, Action<EntityInfo> more = null)
        {
            var e = new EntityInfo { Type = "minecraft:" + type, Variant = variant, Baby = baby, Width = 1, Height = 1 };
            more?.Invoke(e);
            samples.Add(e);
        }
        foreach (var w in new[] { "oak", "spruce", "birch", "jungle", "acacia", "cherry", "dark_oak", "pale_oak", "mangrove" }) { Add(w + "_boat"); Add(w + "_chest_boat"); }
        Add("bamboo_raft"); Add("bamboo_chest_raft");
        foreach (var t in new[] { "minecart", "chest_minecart", "furnace_minecart", "hopper_minecart", "tnt_minecart", "spawner_minecart", "command_block_minecart" }) Add(t);
        foreach (var v in new[] { "temperate", "warm", "cold" }) { Add("cow", v); Add("cow", v, true); Add("pig", v); Add("pig", v, true); Add("chicken", v); Add("chicken", v, true); Add("frog", v); }
        Add("mooshroom", "red"); Add("mooshroom", "brown", true);
        Add("sheep", null, false, e => e.Color = "red"); Add("sheep", null, true); Add("sheep", null, false, e => e.Sheared = true);
        foreach (var c in new[] { "white", "creamy", "chestnut", "brown", "black", "gray", "dark_brown" }) Add("horse", c, false, e => e.Markings = "white_dots");
        Add("horse", "gray", true, e => e.Markings = "black_dots");
        foreach (var t in new[] { "donkey", "mule", "skeleton_horse", "zombie_horse", "camel", "goat", "polar_bear", "armadillo", "sniffer", "strider", "hoglin", "zoglin", "squid", "glow_squid", "dolphin", "turtle", "nautilus", "zombie", "husk", "drowned", "piglin", "zombified_piglin", "bee" })
        { Add(t); Add(t, null, true); }
        foreach (var v in new[] { "creamy", "white", "brown", "gray" }) { Add("llama", v); Add("trader_llama", v, true); }
        foreach (var v in new[] { "pale", "spotted", "snowy", "black", "ashen", "rusty", "woods", "chestnut", "striped" }) { Add("wolf", v); Add("wolf", v, true, e => e.Tamed = true); Add("wolf", v, false, e => e.Angry = true); }
        foreach (var v in new[] { "tabby", "black", "red", "siamese", "british_shorthair", "calico", "persian", "ragdoll", "white", "jellie", "all_black" }) { Add("cat", v); Add("cat", v, true); }
        Add("ocelot"); Add("fox", "red"); Add("fox", "snow", true, e => e.Pose = "sleeping");
        foreach (var v in new[] { "brown", "white", "black", "black_and_white", "gold", "salt_and_pepper", "the_killer_bunny" }) { Add("rabbit", v); Add("rabbit", v, true); }
        foreach (var v in new[] { "normal", "lazy", "worried", "playful", "brown", "weak", "aggressive" }) { Add("panda", v); Add("panda", v, true); }
        foreach (var v in new[] { "red", "blue", "green", "cyan", "gray" }) Add("parrot", v);
        foreach (var v in new[] { "lucy", "wild", "gold", "cyan", "blue" }) { Add("axolotl", v); Add("axolotl", v, true); }
        foreach (var v in new[] { "small", "medium", "large" }) Add("salmon", v);
        foreach (var v in new[] { "small", "medium", "big" }) Add("pufferfish", v);
        Add("tropical_fish", "kob", false, e => e.Color = "orange"); Add("tropical_fish", "betty");
        Add("villager", "plains", false, e => e.Profession = "farmer"); Add("villager", "taiga", true); Add("villager", "snow");
        Add("zombie_villager", "desert", false, e => e.Profession = "librarian"); Add("zombie_villager", "swamp", true);
        Add("shulker"); Add("shulker", null, false, e => e.Color = "lime"); Add("armor_stand"); Add("armor_stand", null, false, e => e.Small = true);
        foreach (var t in new[] { "bat", "tadpole", "allay", "vex", "happy_ghast", "ghast", "cod", "guardian", "elder_guardian", "zombie_nautilus", "skeleton", "stray", "bogged", "wither_skeleton", "parched",
                                  "wandering_trader", "witch", "pillager", "vindicator", "evoker", "illusioner", "ravager", "piglin_brute", "enderman", "iron_golem", "snow_golem", "copper_golem",
                                  "creaking", "blaze", "breeze", "warden", "creeper", "spider", "cave_spider", "silverfish", "endermite", "slime", "magma_cube", "phantom", "arrow", "trident",
                                  "end_crystal", "leash_knot", "mannequin" })
            Add(t);
        var missing = new List<string>();
        foreach (var e in samples)
        {
            var l = EntityCatalog.Resolve(e);
            string what = $"{e.Path}{(e.Variant != null ? "/" + e.Variant : "")}{(e.Baby ? " baby" : "")}";
            if (l.Kind != EntityKind.Model) { missing.Add(what + " not a model"); continue; }
            if (EntityModels.Loaded && EntityModels.Get(l.Model) == null) missing.Add(what + " model " + l.Model);
            if (!Has(l.Texture)) missing.Add(what + " texture " + l.Texture);
            if (l.Overlays != null) foreach (var o in l.Overlays) if (!Has(o)) missing.Add(what + " overlay " + o);
            if (l.ExtraModel != null && ((EntityModels.Loaded && EntityModels.Get(l.ExtraModel) == null) || !Has(l.ExtraTexture))) missing.Add(what + " extra " + l.ExtraModel + " " + l.ExtraTexture);
        }
        check(missing.Count == 0, $"entity catalog: {samples.Count} looks resolve to models and pack textures" + (missing.Count > 0 ? ": MISSING " + string.Join("; ", missing.Take(12)) : ""));

        // Items.
        var diamond = ItemModels.Resolve("minecraft:diamond", Read);
        check(diamond.Kind == ItemVisualKind.Sprite && diamond.Sprite == "minecraft:item/diamond" && Has(diamond.Sprite), $"item: diamond -> sprite {diamond.Sprite}");
        var stone = ItemModels.Resolve("minecraft:stone", Read);
        check(stone.Kind == ItemVisualKind.Cube && stone.Faces.All(f => f == "minecraft:block/stone"), $"item: stone -> cube of block/stone ({string.Join(",", stone.Faces)})");
        var grass = ItemModels.Resolve("minecraft:grass_block", Read);
        check(grass.Kind == ItemVisualKind.Cube && grass.Faces[1] == "minecraft:block/grass_block_top" && grass.Tinted[1] && grass.Faces[0] == "minecraft:block/dirt", "item: grass block top tinted, dirt bottom");
        var log = ItemModels.Resolve("minecraft:oak_log", Read);
        check(log.Kind == ItemVisualKind.Cube && log.Faces[1] == "minecraft:block/oak_log_top" && log.Faces[2] == "minecraft:block/oak_log", "item: oak log top and bark");
        var sword = ItemModels.Resolve("minecraft:diamond_sword", Read);
        check(sword.Kind == ItemVisualKind.Sprite && sword.Handheld && sword.ThirdPersonRight.Set && sword.ThirdPersonRight.Rz == 55 && sword.ThirdPersonRight.Sx == 0.85f,
              $"item: diamond sword is a handheld sprite, held at z 55 deg, scale 0.85 ({sword.ThirdPersonRight.Rz}, {sword.ThirdPersonRight.Sx})");
        check(diamond.Ground.Set && diamond.Ground.Sx == 0.5f && diamond.Ground.Ty == 2 && stone.Ground.Sx == 0.25f && stone.ThirdPersonRight.Rx == 75,
              "item display: generated items lie at half size, blocks at a quarter, held blocks tilted 75 deg");
        var torch = ItemModels.Resolve("minecraft:torch", Read);
        check(torch.Kind == ItemVisualKind.Sprite && torch.Sprite == "minecraft:block/torch", $"item: torch -> sprite {torch.Sprite}");
        var helmet = ItemModels.Resolve("minecraft:leather_helmet", Read);
        check(helmet.Kind == ItemVisualKind.Sprite && helmet.SpriteTint.r == 0xA0 && helmet.Overlay != null, $"item: leather helmet tinted {helmet.SpriteTint.r:X2}{helmet.SpriteTint.g:X2}{helmet.SpriteTint.b:X2} with overlay");
        var chest = ItemModels.Resolve("minecraft:chest", Read);
        check(chest.Kind == ItemVisualKind.Cube && Has(chest.Faces[0]), $"item: chest (special model) -> cube of {chest.Faces[0]}");
        var compass = ItemModels.Resolve("minecraft:compass", Read);
        check(compass.Kind == ItemVisualKind.Sprite && Has(compass.Sprite), $"item: compass (range dispatch) -> {compass.Sprite}");

        // Every item in the pack.
        int sprites = 0, cubes = 0, none = 0, missingTex = 0;
        var noneNames = new List<string>(); var missingNames = new List<string>();
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith("assets/minecraft/items/") || !e.FullName.EndsWith(".json")) continue;
            string id = "minecraft:" + e.Name.Substring(0, e.Name.Length - 5);
            if (id == "minecraft:air") continue;   // the missing-texture model
            var v = ItemModels.Resolve(id, Read);
            if (v.Kind == ItemVisualKind.Sprite) { sprites++; if (!Has(v.Sprite)) { missingTex++; missingNames.Add(id + ":" + v.Sprite); } }
            else if (v.Kind == ItemVisualKind.Cube) { cubes++; if (v.Faces.Any(f => !Has(f))) { missingTex++; missingNames.Add(id + ":" + v.Faces.First(f => !Has(f))); } }
            else { none++; noneNames.Add(id); }
        }
        check(missingTex == 0 && none < 10 && sprites > 600 && cubes > 600, $"all pack items: {sprites} sprites, {cubes} cubes, {none} none ({string.Join(",", noneNames.Take(6))}), {missingTex} missing textures {string.Join(",", missingNames.Take(4))}");

        // Armour textures for each material.
        var armor = new List<string>();
        foreach (var mat in new[] { "leather", "chainmail", "iron", "golden", "diamond", "netherite", "copper" })
            foreach (var (k, slot) in new[] { ("helmet", "head"), ("chestplate", "chest"), ("leggings", "legs"), ("boots", "feet") })
            {
                var t = EntityCatalog.ArmorTexture($"minecraft:{mat}_{k}", slot, out _);
                if (t == null || !Has(t)) armor.Add(mat + "_" + k);
            }
        check(armor.Count == 0 && EntityCatalog.ArmorTexture("minecraft:diamond_sword", "chest", out _) == null, "armour textures for every material and slot" + (armor.Count > 0 ? ": MISSING " + string.Join(",", armor) : ""));
        bool cracks = Enumerable.Range(0, 10).All(i => Has("block/destroy_stage_" + i));
        check(cracks && Has("block/item_frame") && Has("painting/kebab"), "dig crack stages, item frame and painting textures exist");
    }
}
