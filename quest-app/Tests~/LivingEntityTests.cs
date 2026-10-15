// [living] Engine-free tests for the entity side of the living world: the entities_living fixture through the store,
// the gear/collar/chest/pattern catalog (against the client jar's equipment assets and textures when given), the new
// model roots against the vanilla renderers' PoseStacks, leads and fishing lines against LeashFeatureRenderer and
// FishingHookRenderer, the lightning bolt (java.util.Random port), rain and snow placement, display entity orientation
// (DisplayRenderer) and experience orbs.
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static partial class LivingTests
{
    static partial void More(Func<string, string> fixture, string jar)
    {
        Store(fixture);
        Gear();
        Roots();
        Leads();
        Bolt();
        Rain();
        Displays();
        Orbs();
        if (jar != null) GearInPack(jar);
        else Console.WriteLine("SKIP living pack tests: set MC_CLIENT_JAR or pass --jar PATH");
        if (EntityModels.Loaded) GearModels();
    }

    static EntityInfo Info(string type, Action<EntityInfo> more = null)
    {
        var e = new EntityInfo { Type = "minecraft:" + type, Width = 1, Height = 1 };
        more?.Invoke(e);
        return e;
    }

    static void Store(Func<string, string> fixture)
    {
        var msg = Json.Parse(System.IO.File.ReadAllText(fixture("entities_living.json"))) as Dictionary<string, object>;
        var store = new EntityStore();
        store.Apply(msg, null);
        var e = store.All;
        Check(e[301].Leash == 7 && Near(e[301].Eye, 0.68f) && e[303].Leash == 302 && e[302].Leash == -1, "entities_living: leads (wolf to player 7, horse to knot 302) and eye heights");
        Check(e[304].Chested && e[305].Xp == 17 && e[306].Owner == 7 && e[307].Carried == "minecraft:grass_block[snowy=false]", "chests, orb value, bobber owner, carried block");
        Check(Near(e[308].Peek, 0.5f) && e[308].Attach == "north" && e[313].Sheared && e[314].NameVisible && !e[301].NameVisible, "shulker lid, sheared snow golem, visible name");
        var td = e[309].Display; var id = e[310].Display; var bd = e[311].Display;
        Check(td.Billboard == "center" && td.Text == "Welcome\nto the farm" && td.Background == 0x40000000 && td.Shadow && td.Align == "center" && td.LineWidth == 200,
              "text display: billboard, text, background, shadow, alignment");
        Check(id.ItemTransform == "fixed" && id.BlockLight == 15 && id.SkyLight == 15 && Near(id.Left[2], 0.3827f) && Near(id.Scale.x, 2f) && Near(id.Translation.y, 0.5f),
              "item display: transform mode, brightness, rotation, scale, translation");
        Check(Near(bd.Scale.y, 0.5f) && bd.Text == null && bd.BlockLight == -1 && e[311].Block.StartsWith("minecraft:oak_stairs"), "block display: scale, no text, world light, its block");
        int looks = e[301].LooksVersion;
        store.Apply(Json.Parse("{\"full\":false,\"entities\":[{\"id\":301,\"leash\":null},{\"id\":308,\"peek\":0.2}]}") as Dictionary<string, object>, null);
        Check(e[301].Leash == -1 && Near(e[308].Peek, 0.2f) && e[301].LooksVersion == looks, "a lead let go and a lid moving don't rebuild the figure");
    }

    static GearLayer Layer(EntityLook l, string model) => l.Layers?.FirstOrDefault(g => g.Model == model);

    static void Gear()
    {
        var horse = EntityCatalog.Resolve(Info("horse", e => e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:golden_horse_armor", ["saddle"] = "minecraft:saddle" }));
        Check(Layer(horse, "horse_armor")?.Texture == "entity/equipment/horse_body/gold" && Layer(horse, "horse#saddle")?.Texture == "entity/equipment/horse_saddle/saddle",
              "horse: golden armour (equipment asset gold) and saddle");
        var leather = EntityCatalog.Resolve(Info("horse", e => e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:leather_horse_armor" }));
        Check(leather.Layers.Count == 2 && leather.Layers[0].Tint.r == 0xA0 && leather.Layers[1].Texture.EndsWith("leather_overlay"), "leather horse armour: undyed #A06540 and the overlay");
        var zombieHorse = EntityCatalog.Resolve(Info("zombie_horse", e => e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:diamond_horse_armor" }));
        Check(Layer(zombieHorse, "undead_horse_armor")?.Texture == "entity/equipment/horse_body/diamond", "undead horses wear their own armour model");
        var donkey = EntityCatalog.Resolve(Info("donkey")); var chested = EntityCatalog.Resolve(Info("mule", e => e.Chested = true));
        Check(donkey.Hidden != null && donkey.Hidden.Contains("left_chest") && donkey.Hidden.Contains("right_chest") && chested.Hidden == null, "donkey and mule chests only when chested");
        var llama = EntityCatalog.Resolve(Info("llama", e => { e.Variant = "white"; e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:light_blue_carpet" }; }));
        var trader = EntityCatalog.Resolve(Info("trader_llama", e => e.Variant = "brown"));
        var traderBaby = EntityCatalog.Resolve(Info("trader_llama", e => { e.Variant = "brown"; e.Baby = true; }));
        Check(Layer(llama, "llama#decor")?.Texture == "entity/equipment/llama_body/light_blue" && Layer(trader, "llama#decor")?.Texture == "entity/equipment/llama_body/trader_llama"
              && Layer(traderBaby, "llama_baby#decor")?.Texture == "entity/equipment/llama_body/trader_llama_baby", "llama carpets; trader llamas wear their blanket");
        var wolf = EntityCatalog.Resolve(Info("wolf", e => { e.Tamed = true; e.Color = "red"; e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:wolf_armor" }; }));
        var collar = wolf.Layers?.FirstOrDefault(g => g.Texture == "entity/wolf/wolf_collar");
        Check(Layer(wolf, "wolf_armor")?.Texture == "entity/equipment/wolf_body/armadillo_scute" && collar != null && collar.Model == wolf.Model && collar.Over,
              "wolf: armadillo scute armour, collar on the wolf's own model drawn over it");
        var cat = EntityCatalog.Resolve(Info("cat", e => { e.Tamed = true; e.Color = "blue"; e.Baby = true; }));
        Check(Layer(cat, "cat_baby#collar")?.Texture == "entity/cat/cat_collar_baby" && EntityCatalog.Resolve(Info("cat", e => e.Color = "blue")).Layers == null, "cat collar only when tamed");
        var ghast = EntityCatalog.Resolve(Info("happy_ghast", e => e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:orange_harness" }));
        Check(Layer(ghast, "happy_ghast_harness")?.Texture == "entity/equipment/happy_ghast_body/orange_harness", "happy ghast harness");
        var nautilus = EntityCatalog.Resolve(Info("nautilus", e => e.Equipment = new Dictionary<string, string> { ["body"] = "minecraft:iron_nautilus_armor", ["saddle"] = "minecraft:saddle" }));
        Check(Layer(nautilus, "nautilus_armor")?.Texture == "entity/equipment/nautilus_body/iron" && Layer(nautilus, "nautilus#saddle") != null, "nautilus armour and saddle");
        var pig = EntityCatalog.Resolve(Info("pig", e => e.Equipment = new Dictionary<string, string> { ["saddle"] = "minecraft:saddle" }));
        var piglet = EntityCatalog.Resolve(Info("pig", e => { e.Baby = true; e.Equipment = new Dictionary<string, string> { ["saddle"] = "minecraft:saddle" }; }));
        Check(Layer(pig, "pig#saddle")?.Texture == "entity/equipment/pig_saddle/saddle" && piglet.Layers == null, "pig saddle (not on piglets)");
        var fish = EntityCatalog.Resolve(Info("tropical_fish", e => { e.Variant = "glitter"; e.Color = "white"; e.Markings = "lime"; }));
        Check(Layer(fish, "tropical_fish_large#pattern")?.Texture == "entity/fish/tropical_b_pattern_3" && LivingLooks.PatternNumber("spotty", out bool lg) == 6 && !lg,
              "tropical fish pattern layer by pattern (glitter = large 3, spotty = small 6)");
        var golem = EntityCatalog.Resolve(Info("snow_golem")); var bald = EntityCatalog.Resolve(Info("snow_golem", e => e.Sheared = true));
        Check(golem.Blocks?.Single().State == "minecraft:carved_pumpkin[facing=north]" && golem.Blocks[0].Part == "head" && bald.Blocks == null, "snow golem pumpkin until sheared");
        var moo = EntityCatalog.Resolve(Info("mooshroom", e => e.Variant = "brown")); var calf = EntityCatalog.Resolve(Info("mooshroom", e => e.Baby = true));
        Check(moo.Blocks?.Count == 3 && moo.Blocks.All(b => b.State == "minecraft:brown_mushroom") && moo.Blocks.Count(b => b.Part == "head") == 1 && calf.Blocks == null,
              "mooshroom: two mushrooms on the back, one on the head; none on calves");
        var ender = EntityCatalog.Resolve(Info("enderman", e => e.Carried = "minecraft:tnt[unstable=false]"));
        Check(ender.Blocks?.Single().State == "minecraft:tnt[unstable=false]" && ender.Blocks[0].Part == null, "enderman carries its block");
        Check(!EntityCatalog.Resolve(Info("villager", e => e.Name = "Bob")).NameShown && EntityCatalog.Resolve(Info("villager", e => { e.Name = "Bob"; e.NameVisible = true; })).NameShown,
              "name labels only with CustomNameVisible");
        Check(LivingLooks.Asset("minecraft:golden_nautilus_armor") == "gold" && LivingLooks.Asset("minecraft:wolf_armor") == "armadillo_scute" && LivingLooks.Asset("minecraft:white_carpet") == "white_carpet"
              && LivingLooks.Asset("minecraft:iron_sword") == null, "items to equipment assets");
        // New entity types
        var kinds = new (string type, EntityKind kind, string model, RootKind root)[]
        {
            ("ender_dragon", EntityKind.Model, "ender_dragon", RootKind.Dragon), ("wither", EntityKind.Model, "wither", RootKind.Living),
            ("giant", EntityKind.Model, "giant", RootKind.Living), ("evoker_fangs", EntityKind.Model, "evoker_fangs", RootKind.Fangs),
            ("shulker_bullet", EntityKind.Model, "shulker_bullet", RootKind.Bullet), ("wither_skull", EntityKind.Model, "wither_skull", RootKind.Skull),
            ("llama_spit", EntityKind.Model, "llama_spit", RootKind.Spit), ("cushion", EntityKind.Model, "cushion", RootKind.Cushion),
            ("sulfur_cube", EntityKind.Model, "sulfur_cube#inner", RootKind.Living), ("experience_orb", EntityKind.Orb, null, RootKind.Living),
            ("dragon_fireball", EntityKind.Billboard, null, RootKind.Living), ("lightning_bolt", EntityKind.Lightning, null, RootKind.Living),
            ("item_display", EntityKind.Display, null, RootKind.Living), ("block_display", EntityKind.Display, null, RootKind.Living),
            ("text_display", EntityKind.Display, null, RootKind.Living), ("ominous_item_spawner", EntityKind.Item, null, RootKind.Living),
        };
        var wrong = kinds.Where(k => { var l = EntityCatalog.Resolve(Info(k.type)); return l.Kind != k.kind || (k.model != null && (l.Model != k.model || l.Root != k.root)); })
                         .Select(k => k.type).ToList();
        Check(wrong.Count == 0, "new entity types resolve to their models, roots and kinds" + (wrong.Count > 0 ? ": " + string.Join(", ", wrong) : ""));
        Check(EntityCatalog.Resolve(Info("wither")).Scale == 2 && EntityCatalog.Resolve(Info("shulker_bullet")).Scale == 0.5f && EntityCatalog.Resolve(Info("dragon_fireball")).Scale == 2,
              "wither at scale 2, shulker bullet at 0.5, dragon fireball 2");
    }

    /// <summary>Every texture the gear rules name exists in the pack, and the rule equals what the pack's equipment JSON
    /// says for each asset and layer type the catalog uses.</summary>
    static void GearInPack(string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        bool Has(string t) => zip.GetEntry("assets/minecraft/textures/" + t + ".png") != null;
        var missing = new List<string>();
        var mismatch = new List<string>();
        var layerTypes = new[] { "horse_body", "wolf_body", "llama_body", "happy_ghast_body", "nautilus_body", "pig_saddle", "strider_saddle", "horse_saddle", "donkey_saddle",
                                 "mule_saddle", "skeleton_horse_saddle", "zombie_horse_saddle", "camel_saddle", "camel_husk_saddle", "nautilus_saddle" };
        int checkedLayers = 0;
        foreach (var entry in zip.Entries.Where(en => en.FullName.StartsWith("assets/minecraft/equipment/") && en.FullName.EndsWith(".json")))
        {
            string asset = entry.Name.Substring(0, entry.Name.Length - 5);
            using var r = new System.IO.StreamReader(entry.Open());
            var layers = (Json.Parse(r.ReadToEnd()) as Dictionary<string, object>)?.O("layers");
            if (layers == null) continue;
            foreach (var lt in layerTypes)
            {
                if (layers.A(lt) is not List<object> list) continue;
                checkedLayers++;
                // The JSON's drawable layers: dyeable ones only when they have an undyed colour.
                var want = list.OfType<Dictionary<string, object>>().Where(l => l.O("dyeable") == null || l.O("dyeable").ContainsKey("color_when_undyed"))
                               .Select(l => "entity/equipment/" + lt + "/" + l.S("texture").Replace("minecraft:", "")).ToList();
                var got = LivingLooks.Textures(asset, lt).Select(t => t.texture).ToList();
                if (!want.SequenceEqual(got)) mismatch.Add($"{asset}/{lt}: json {string.Join("+", want)} rule {string.Join("+", got)}");
                foreach (var t in got) if (!Has(t)) missing.Add(t);
            }
        }
        Check(checkedLayers > 40 && mismatch.Count == 0, $"equipment rules match the pack's equipment JSON for {checkedLayers} asset layers" + (mismatch.Count > 0 ? ": " + string.Join("; ", mismatch.Take(6)) : ""));
        foreach (var e in GearSamples())
        {
            var l = EntityCatalog.Resolve(e);
            if (l.Texture != null && l.Kind == EntityKind.Model && !Has(l.Texture)) missing.Add(l.Texture);
            if (l.ExtraTexture != null && !Has(l.ExtraTexture)) missing.Add(l.ExtraTexture);
            foreach (var g in l.Layers ?? new List<GearLayer>()) if (!Has(g.Texture)) missing.Add(g.Texture);
        }
        foreach (var t in new[] { "entity/experience/experience_orb", "entity/enderdragon/dragon_fireball" }) if (!Has(t)) missing.Add(t);
        Check(missing.Count == 0, "every living texture is in the pack" + (missing.Count > 0 ? ": " + string.Join(", ", missing.Distinct().Take(8)) : ""));
    }

    static IEnumerable<EntityInfo> GearSamples()
    {
        Dictionary<string, string> Eq(string body, string saddle = null)
        {
            var d = new Dictionary<string, string>();
            if (body != null) d["body"] = body;
            if (saddle != null) d["saddle"] = saddle;
            return d;
        }
        foreach (var m in new[] { "leather", "iron", "golden", "diamond", "copper", "netherite" }) yield return Info("horse", e => e.Equipment = Eq("minecraft:" + m + "_horse_armor", "minecraft:saddle"));
        foreach (var m in new[] { "copper", "iron", "golden", "diamond", "netherite" }) yield return Info("nautilus", e => e.Equipment = Eq("minecraft:" + m + "_nautilus_armor", "minecraft:saddle"));
        foreach (var t in new[] { "zombie_horse", "skeleton_horse", "donkey", "mule", "camel", "camel_husk", "pig", "strider" }) yield return Info(t, e => e.Equipment = Eq(null, "minecraft:saddle"));
        foreach (var c in new[] { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" })
        {
            yield return Info("llama", e => e.Equipment = Eq("minecraft:" + c + "_carpet"));
            yield return Info("happy_ghast", e => e.Equipment = Eq("minecraft:" + c + "_harness"));
        }
        yield return Info("trader_llama"); yield return Info("trader_llama", e => e.Baby = true);
        yield return Info("wolf", e => { e.Tamed = true; e.Color = "red"; e.Equipment = Eq("minecraft:wolf_armor"); });
        yield return Info("wolf", e => { e.Tamed = true; e.Color = "red"; e.Baby = true; });
        yield return Info("cat", e => { e.Tamed = true; e.Color = "red"; }); yield return Info("cat", e => { e.Tamed = true; e.Color = "red"; e.Baby = true; });
        foreach (var p in new[] { "kob", "sunstreak", "snooper", "dasher", "brinely", "spotty", "flopper", "stripey", "glitter", "blockfish", "betty", "clayfish" })
            yield return Info("tropical_fish", e => { e.Variant = p; e.Color = "white"; e.Markings = "red"; });
        foreach (var t in new[] { "ender_dragon", "wither", "giant", "evoker_fangs", "shulker_bullet", "wither_skull", "llama_spit", "cushion", "sulfur_cube" }) yield return Info(t);
        yield return Info("sulfur_cube", e => e.Baby = true);
        foreach (var c in new[] { "white", "red", "black" }) yield return Info("cushion", e => e.Color = c);
    }

    /// <summary>Every model layer the living catalog names is in entity_models.json (the generator's LAYERS).</summary>
    static void GearModels()
    {
        var missing = new List<string>();
        foreach (var e in GearSamples())
        {
            var l = EntityCatalog.Resolve(e);
            if (l.Kind == EntityKind.Model && EntityModels.Get(l.Model) == null) missing.Add(l.Model);
            if (l.ExtraModel != null && EntityModels.Get(l.ExtraModel) == null) missing.Add(l.ExtraModel);
            foreach (var g in l.Layers ?? new List<GearLayer>()) if (EntityModels.Get(g.Model) == null) missing.Add(g.Model);
        }
        Check(missing.Count == 0, "every living model layer is in entity_models.json" + (missing.Count > 0 ? ": " + string.Join(", ", missing.Distinct()) : ""));
        var giant = EntityModels.Get("giant");
        Check(giant != null && Near(giant.Root.Sx, 6f), "the giant layer carries its scale 6 (GiantMobRenderer draws it unscaled)");
    }

    /// <summary>The new roots seen through the renderer's Z mirror equal the vanilla renderers' PoseStacks.</summary>
    static void Roots()
    {
        float[] M(params float[][] ms) { var r = EntityScene.I(); foreach (var m in ms) r = EntityScene.Mul(r, m); return r; }
        var flipLift = M(EntityScene.S(-1, -1, 1), EntityScene.T(0, -1.501f, 0));
        float[] Vanilla(RootKind k, float yaw, float pitch) => k switch
        {
            RootKind.Dragon => M(EntityScene.RY(-yaw), EntityScene.T(0, 0, 1), flipLift),                                   // EnderDragonRenderer
            RootKind.Fangs => M(EntityScene.RY(90 - yaw), flipLift),                                                        // EvokerFangsRenderer
            RootKind.Spit => M(EntityScene.T(0, 0.15f, 0), EntityScene.RY(yaw - 90), EntityScene.RZ(pitch)),               // LlamaSpitRenderer
            RootKind.Cushion => M(EntityScene.RY(180 - Mathf.Round(yaw / 90f) * 90f), EntityScene.RX(180), EntityScene.T(0, -0.25f, 0)),   // CushionRenderer
            RootKind.Skull => EntityScene.S(-1, -1, 1),                                                                     // WitherSkullRenderer
            _ => M(EntityScene.T(0, 0.15f, 0), EntityScene.RX(180), EntityScene.S(-0.5f, -0.5f, 0.5f)),                    // ShulkerBulletRenderer, age 0: rotX(cos 0 * 180)
        };
        var pts = new[] { new Vector3(4, 0, 0), new Vector3(-11, 2.5f, 0), new Vector3(0, -2, 1.5f), new Vector3(3, 7, -5), new Vector3(0, 24, 0) };
        var worst = new Dictionary<RootKind, float>();
        foreach (var k in new[] { RootKind.Dragon, RootKind.Fangs, RootKind.Spit, RootKind.Cushion, RootKind.Skull, RootKind.Bullet })
            foreach (float yaw in new[] { 0f, 37f, 90f, -135f, 180f })
                foreach (float pitch in new[] { 0f, 30f, -60f })
                {
                    float scale = k == RootKind.Bullet ? 0.5f : 1f;
                    var root = EntityScene.ModelRoot(k, pitch);
                    if (k == RootKind.Bullet) root = M(root, BulletSpinAtZero());
                    var fig = M(EntityScene.RY(EntityCatalog.RootTurn(k, yaw)), EntityScene.S(scale / 16, scale / 16, scale / 16), root);
                    var van = M(EntityScene.S(1, 1, -1), Vanilla(k, yaw, pitch), EntityScene.S(1 / 16f, 1 / 16f, 1 / 16f));
                    foreach (var p in pts)
                    {
                        var fp = EntityScene.Apply(fig, new Vector3(-p.x, -p.y, -p.z));
                        var vp = EntityScene.Apply(van, p);
                        float d = Math.Max(Math.Abs(fp.x - vp.x), Math.Max(Math.Abs(fp.y - vp.y), Math.Abs(fp.z - vp.z)));
                        worst[k] = Math.Max(worst.TryGetValue(k, out var w) ? w : 0, d);
                    }
                }
        Check(worst.Values.All(w => w < 1e-4f), "dragon, fangs, llama spit, cushion, wither skull and shulker bullet roots match their renderers: " +
                                                    string.Join(", ", worst.Select(kv => $"{kv.Key} {kv.Value:G2}")));
        // SulfurCubeRenderer.scale runs after LivingEntityRenderer's flip: x0.5, then 0.98 - 1/16 up (baby: x1, 1.24 - 1/16).
        float cubeWorst = 0;
        foreach (bool baby in new[] { false, true })
        {
            var look = EntityCatalog.Resolve(Info("sulfur_cube", e => e.Baby = baby));
            float k = baby ? 1f : 0.5f, up = (baby ? 1.24f : 0.98f) - 1f / 16f;
            var fig = M(EntityScene.RY(EntityCatalog.RootTurn(look.Root, 0)), EntityScene.S(look.Scale / 16, look.Scale / 16, look.Scale / 16),
                        EntityScene.T(0, look.RootLift, 0), EntityScene.ModelRoot(look.Root, 0));
            var van = M(EntityScene.S(1, 1, -1), EntityScene.RY(180), EntityScene.S(-1, -1, 1), EntityScene.S(k, k, k), EntityScene.T(0, up, 0), EntityScene.T(0, -1.501f, 0),
                        EntityScene.S(1 / 16f, 1 / 16f, 1 / 16f));
            foreach (var p in pts)
            {
                var fp = EntityScene.Apply(fig, new Vector3(-p.x, -p.y, -p.z)); var vp = EntityScene.Apply(van, p);
                cubeWorst = Math.Max(cubeWorst, Math.Max(Math.Abs(fp.x - vp.x), Math.Max(Math.Abs(fp.y - vp.y), Math.Abs(fp.z - vp.z))));
            }
        }
        var bottom = EntityScene.Apply(M(EntityScene.S(0.5f / 16, 0.5f / 16, 0.5f / 16), EntityScene.T(0, EntityCatalog.Resolve(Info("sulfur_cube")).RootLift, 0),
                                         EntityScene.ModelRoot(RootKind.Living, 0)), new Vector3(0, -9, 0));   // the cube's lowest face (game y +9)
        Check(cubeWorst < 1e-4f && Math.Abs(bottom.y) < 0.02f, $"sulfur cube: scale and lift of SulfurCubeRenderer (worst {cubeWorst:G2}), resting on the ground ({bottom.y:F3})");
        LivingLooks.FangsPose(0, out float y0, out float up0, out float lo0);
        LivingLooks.FangsPose(0.5f, out float y5, out float up5, out float lo5);
        Check(Near(y0, 24f) && Near(up0, 0.65f * Mathf.PI) && Near(lo0, 1.35f * Mathf.PI) && y5 < 14f && Near(up5, Mathf.PI) && Near(lo5, Mathf.PI),
              $"evoker fangs: open and underground at the start, out ({24 - y5:F1} px) and shut half way");
    }

    /// <summary>EntityFigure's bullet spin at age 0 (rotY(0) rotX(180) rotZ(0) in the game frame), as a figure matrix:
    /// game rotX(a) seen through the Z mirror is a Unity rotX(-a).</summary>
    static float[] BulletSpinAtZero() => EntityScene.RX(-180);

    static void Leads()
    {
        // LeashFeatureRenderer: 2 ribbons of 25 vertex pairs; pair i at t = i/24 with sag; colours alternate 0.7 / 1.0.
        var verts = new List<Vector3>(); var cols = new List<Color32>(); var tris = new List<int>();
        var s = new Vector3(10, 64, 10); var e = new Vector3(13, 66, 6);
        Leash.Rope(s, e, true, null, verts, cols, tris);
        Check(verts.Count == 100 && tris.Count == 2 * 24 * 6, $"a lead is 2 ribbons of 24 quads ({verts.Count} vertices)");
        float dx = 3, dy = 2, dz = -4, k = 0.025f / Mathf.Sqrt(dx * dx + dz * dz), ox = dz * k, oz = dx * k;
        float worst = 0;
        for (int i = 0; i <= 24; i++)
        {
            float t = i / 24f, y = dy * t * t;   // the holder is higher: y = dy t^2
            var want0 = s + new Vector3(dx * t - ox, y + 0.05f, dz * t + oz);
            var want1 = s + new Vector3(dx * t + ox, y, dz * t - oz);
            worst = Math.Max(worst, Math.Max((verts[2 * i] - want0).magnitude, (verts[2 * i + 1] - want1).magnitude));
        }
        Check(worst < 1e-4f, $"rope vertices follow LeashFeatureRenderer.addVertexPair (worst {worst:G2})");
        Check(cols[0].r == (byte)(0.5f * 0.7f * 255 + 0.5f) && cols[2].r == (byte)(0.5f * 255 + 0.5f) && cols[50].r == cols[2].r, "dark and light brown pairs alternate, the back ribbon offset by one");
        verts.Clear(); cols.Clear(); tris.Clear();
        Leash.Rope(e, s, true, null, verts, cols, tris);
        float mid = verts[24].y - e.y, lower = -2f - (-2f) * 0.5f * 0.5f;   // holder lower: y = dy - dy (1 - t)^2
        Check(Near(mid, lower + 0.05f, 1e-3f), $"a rope down to a lower holder hangs as vanilla's ({mid:F3})");
        var off = Leash.Offset("minecraft:wolf", 0.68f, 0.6f, 0.85f);
        Check(Near(off.y, 0.6f * 0.68f) && Near(off.z, 0.24f) && Near(Leash.Offset("minecraft:oak_boat", 0, 1.375f, 0.5625f).y, 0.495f)
              && Near(Leash.Offset("minecraft:cow", 1.3f, 0.9f, 1.4f).y, 1.3f), "leash offsets: wolf 0.6 eye, boats 0.88 height, default the eye");
        var turned = Leash.Turn(new Vector3(0, 1, 1), 90);
        Check(Near(turned.x, -1f) && Near(turned.z, 0f, 1e-5f), "an offset in front of a mob facing west (yaw 90) points west");
        Check(Near(Leash.Hold("minecraft:leash_knot", s, 0, 0.5f, 0).y, 64.2f) && Near(Leash.Hold("minecraft:cow", s, 1.3f, 1.4f, 0).y, 64 + 0.91f), "knot holds 0.2 up, mobs at 0.7 eye height");
        var hand = Leash.PlayerHold(new Vector3(0, 64, 0), 0, false);
        Check(Near(hand.x, -0.22f) && Near(hand.y, 64.8f) && Near(hand.z, 0.07f), "a player facing south holds the lead in the right hand (west, -X), 0.8 up");
        var q = Leash.QuadLeashedOffsets("minecraft:oak_boat", 1.375f, 0.5625f);
        Check(q.Length == 4 && Near(q[0].x, -0.382f * 1.375f) && Near(q[0].z, 0.64f * 1.375f) && Near(q[1].z, -0.64f * 1.375f) && Leash.QuadHolder("minecraft:happy_ghast") && Leash.QuadLeashed("minecraft:oak_boat"),
              "a boat under a happy ghast hangs from 4 corners");
        var line = Leash.FishingLine(new Vector3(0, 62, 0), new Vector3(2, 66, 1));
        Check(line.Length == 17 && Near(line[0].y, 62.25f) && Near(line[16].y, 66f) && Near(line[8].y, 62.25f + 3.75f * (0.25f + 0.5f) * 0.5f), "fishing line: 16 segments sagging (t^2 + t) / 2 from 0.25 above the bobber");
        var rod = Leash.RodHand(new Vector3(0, 64, 0), 0, false);
        Check(Near(rod.x, -0.35f) && Near(rod.z, 0.8f) && Near(rod.y, 64 + 1.62f - 0.45f), "rod hand (FishingHookRenderer.getPlayerHandPos)");
    }

    static void Bolt()
    {
        var r = new JavaRandom(42);
        Check(r.NextInt(11) == 7 && r.NextInt(31) == 10 && r.NextInt(256) == 174 && r.NextInt(16) == 0 && r.NextLong() == 5694868678511409995L, "JavaRandom matches java.util.Random(42)");
        var r2 = new JavaRandom(-7);
        Check(r2.NextInt(11) == 8 && r2.NextInt(11) == 5 && r2.NextInt(1000) == 590, "JavaRandom matches java.util.Random(-7)");
        var q = LightningBolt.Quads(12345);
        Check(q.Count == 4 * (8 + 3 + 3) * 4 * 4, $"bolt: 4 layers of 8 main and 2 x 3 branch segments, 4 quads each ({q.Count / 4} quads)");
        // The main bolt's lowest segment of the thinnest layer ends at the strike point (0, 0, 0).
        var bottom = q.Take(8 * 4 * 4).Where(v => v.y == 0).ToList();
        Check(bottom.Count > 0 && bottom.All(v => Math.Abs(v.x) <= 0.1f + 1e-4f && Math.Abs(v.z) <= 0.1f + 1e-4f), "the main bolt reaches the ground at the entity");
        Check(q.Max(v => v.y) == 128f && q.Min(v => v.y) >= 0f, "128 blocks tall, 8 segments of 16");
        Check(LightningBolt.Quads(12345).SequenceEqual(q) && !LightningBolt.Quads(54321).SequenceEqual(q), "the shape depends on the seed only");
    }

    static void Rain()
    {
        var ref2345 = new SimplexNoise2(2345);
        double worst = 0;
        for (int i = 0; i < 200; i++) { double x = i * 1.37 - 50, y = i * 0.91 + 12; worst = Math.Max(worst, Math.Abs(ref2345.Get(x, y) - SwampNoise.Get(x, y))); }
        Check(worst < 1e-5, "SimplexNoise2(2345) is the game's biome info noise (SwampNoise)");
        Check(!Precipitation.HasPrecipitation("minecraft:desert") && !Precipitation.HasPrecipitation("minecraft:nether_wastes") && Precipitation.HasPrecipitation("minecraft:plains"),
              "no precipitation in deserts or the Nether");
        Check(Precipitation.At("minecraft:plains", null, 0, 70, 0) == Precip.Rain && Precipitation.At("minecraft:snowy_plains", null, 0, 70, 0) == Precip.Snow
              && Precipitation.At("minecraft:savanna", null, 0, 70, 0) == Precip.None, "rain on plains, snow on snowy plains, nothing on savannas");
        // Windswept hills (0.2) turn to snow 40 blocks above sea level + 17 (0.05 / 40 per block), give or take the noise.
        Check(Precipitation.At("minecraft:windswept_hills", null, 100, 90, 100) == Precip.Rain && Precipitation.At("minecraft:windswept_hills", null, 100, 200, 100) == Precip.Snow,
              "mountains: rain below, snow above the height-adjusted 0.15");
        Check(Near(Precipitation.Temperature(0.8f, 0, 70, 0), 0.8f) && Precipitation.Temperature(0.8f, 0, 120, 0) < 0.8f, "temperature drops only above sea level + 17");
        // Columns: grass block with a poppy on it; drops land on the grass (y 2), a roof stops them (y 4).
        var w = World(2, 6, 1, (x, y, z) => y <= 1 ? 1 : (y == 2 && x == 0 ? 2 : (x == 1 && y == 3 ? 3 : 0)), (x, y, z) => 0xF0,
                      "minecraft:air", "minecraft:grass_block[snowy=false]", "minecraft:poppy", "minecraft:stone");
        Check(Precipitation.Ground(w, 0, 0) == 2 && Precipitation.Ground(w, 1, 0) == 4, "drops pass flowers and stop on the highest solid block");
        var drops = Precipitation.Layout(w, 0, 0, 2, 1, 50, 7);
        Check(drops.Count == 50 && drops.All(d => d.Kind == Precip.Rain && d.Top == 6 && (d.Ground == 2 || d.Ground == 4) && d.Speed > 10) && Precipitation.Layout(w, 0, 0, 2, 1, 50, 7).Select(d => d.X).SequenceEqual(drops.Select(d => d.X)),
              "drop layout: deterministic, rain without biome data, from the region top to the ground");
    }

    static void Displays()
    {
        // DisplayRenderer.calculateOrientation: rotationYXZ(-yaw, pitch, 0) as a quaternion, against the matrix product.
        float worst = 0;
        foreach (float yaw in new[] { 0f, 30f, -120f })
            foreach (float pitch in new[] { 0f, 25f, -60f })
            {
                var q = LivingLooks.Orientation("fixed", yaw, pitch, 0, 0);
                var m = QuatMatrix(q);
                var want = EntityScene.Mul(EntityScene.RY(-yaw), EntityScene.RX(pitch));
                for (int i = 0; i < 16; i++) worst = Math.Max(worst, Math.Abs(m[i] - want[i]));
            }
        Check(worst < 1e-5f, $"fixed displays: rotationYXZ(-yaw, pitch, 0) (worst {worst:G2})");
        var c = QuatMatrix(LivingLooks.Orientation("center", 10, 20, 70, -30));
        var cw = EntityScene.Mul(EntityScene.RY(-(70 - 180)), EntityScene.RX(30));
        var v = QuatMatrix(LivingLooks.Orientation("vertical", 10, 20, 70, -30));
        var vw = EntityScene.Mul(EntityScene.RY(-(70 - 180)), EntityScene.RX(20));
        var h = QuatMatrix(LivingLooks.Orientation("horizontal", 10, 20, 70, -30));
        var hw = EntityScene.Mul(EntityScene.RY(-10), EntityScene.RX(30));
        Check(Enumerable.Range(0, 16).All(i => Math.Abs(c[i] - cw[i]) < 1e-5f && Math.Abs(v[i] - vw[i]) < 1e-5f && Math.Abs(h[i] - hw[i]) < 1e-5f),
              "center, vertical and horizontal billboards take the camera's yaw (- 180) and pitch (negated)");
        // A camera south of a center-billboard display looking north: the display turns to face it (its +Z toward the camera).
        LivingLooks.YawPitch(new Vector3(0, 0, -1), out float camYaw, out float camPitch);
        var face = QuatMatrix(LivingLooks.Orientation("center", 0, 0, camYaw, camPitch));
        var front = EntityScene.Apply(face, new Vector3(0, 0, 1));
        Check(Near(camYaw, 180f) || Near(camYaw, -180f), $"looking north is yaw 180 ({camYaw})");
        Check(front.z > 0.99f, $"a centre billboard turns its front (+Z) towards a camera looking north (front {front.z:F2})");
        var mirrored = LivingLooks.Mirror(new[] { 0.1f, 0.2f, 0.3f, 0.927f });
        Check(mirrored[0] == -0.1f && mirrored[1] == -0.2f && mirrored[2] == 0.3f && mirrored[3] == 0.927f, "game rotations through the Z mirror: (-x, -y, z, w)");
    }

    static float[] QuatMatrix(float[] q)
    {
        float x = q[0], y = q[1], z = q[2], w = q[3];
        return new[]
        {
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), 0,
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), 0,
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), 0,
            0, 0, 0, 1,
        };
    }

    static void Orbs()
    {
        Check(LivingLooks.OrbIcon(1) == 0 && LivingLooks.OrbIcon(3) == 1 && LivingLooks.OrbIcon(16) == 2 && LivingLooks.OrbIcon(17) == 3 && LivingLooks.OrbIcon(2476) == 9 && LivingLooks.OrbIcon(9000) == 10,
              "ExperienceOrb.getIcon thresholds");
        LivingLooks.OrbUv(5, out float u0, out float v0, out float u1, out float v1);
        Check(Near(u0, 0.25f) && Near(u1, 0.5f) && Near(v1, 0.75f) && Near(v0, 0.5f), "icon 5: second row, second column of the 64 x 64 sheet");
        var c0 = LivingLooks.OrbColor(0);
        Check(c0.r == 127 && c0.g == 255 && c0.a == 128 && c0.b == (byte)(int)((Mathf.Sin(4.1887903f) + 1f) * 0.1f * 255f), "orb colour at age 0");
        Check(LivingLooks.OrbBlockLight(10) == 15 && LivingLooks.OrbBlockLight(3) == 10, "orbs glow 7 levels above the block light");
    }
}
