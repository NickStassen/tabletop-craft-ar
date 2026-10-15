// [living] The rest of vanilla's entity looks (PROTOCOL.md "Living entities"), as a table like EntityCatalog: the
// entity types it didn't know (ender dragon, wither, giant, evoker fangs, shulker bullet, wither skull, llama spit,
// dragon fireball, cushion, sulfur cube, ominous item spawner, experience orbs, lightning, display entities) and what is
// worn or carried: saddles, horse/wolf/nautilus armour, llama carpets, the happy ghast harness, chests on donkeys, mules
// and llamas, wolf and cat collars, tropical fish patterns, the snow golem's pumpkin, the mooshroom's mushrooms and the
// enderman's block. Textures follow the 26.x equipment assets (assets/minecraft/equipment/<asset>.json:
// entity/equipment/<layer type>/<texture>.png), model layers come from entity_models.json (DumpEntityModels LAYERS).
// Engine-free (Tests~ checks the rules against the client jar).
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>A layer drawn over an entity model on the same parts (gear, collars, patterns), as EntityLook.ExtraModel.</summary>
    public sealed class GearLayer
    {
        public string Model, Texture;
        public List<string> Overlays;
        public Color32 Tint = EntityLook.White;
        public bool Translucent;
        /// <summary>The layer reuses the model's own geometry (wolf collar): drawn after the body so it wins equal depths.</summary>
        public bool Over;
    }

    /// <summary>One step of a game PoseStack in model space: T translate (blocks), X/Y/Z rotate (degrees), S scale.</summary>
    public struct AttachOp
    {
        public char Kind;
        public Vector3 V;
        public static AttachOp T(float x, float y, float z) => new AttachOp { Kind = 'T', V = new Vector3(x, y, z) };
        public static AttachOp S(float x, float y, float z) => new AttachOp { Kind = 'S', V = new Vector3(x, y, z) };
        public static AttachOp X(float deg) => new AttachOp { Kind = 'X', V = new Vector3(deg, 0, 0) };
        public static AttachOp Y(float deg) => new AttachOp { Kind = 'Y', V = new Vector3(deg, 0, 0) };
    }

    /// <summary>A block model drawn on the figure (snow golem pumpkin, mooshroom mushrooms, enderman block): under a part
    /// (null = the model root), through the layer's PoseStack ops, the block's [0, 1] model at the end.</summary>
    public sealed class BlockAttach
    {
        public string Part, State;
        public AttachOp[] Ops;
    }

    public static class LivingLooks
    {
        static string P(string id) => id != null && id.StartsWith("minecraft:") ? id.Substring(10) : id;

        /// <summary>A look for the types EntityCatalog doesn't know, or null.</summary>
        public static EntityLook Resolve(EntityInfo e)
        {
            string t = e.Path ?? "";
            var L = new EntityLook { Kind = EntityKind.Model, BoxColor = EntityCatalog.BoxColor(t) };
            switch (t)
            {
                case "experience_orb": L.Kind = EntityKind.Orb; L.Texture = "entity/experience/experience_orb"; L.Orb = OrbIcon(e.Xp); return L;
                case "dragon_fireball": L.Kind = EntityKind.Billboard; L.Texture = "entity/enderdragon/dragon_fireball"; L.Scale = 2; return L;
                case "lightning_bolt": L.Kind = EntityKind.Lightning; return L;
                case "item_display": case "block_display": case "text_display":
                    L.Kind = EntityKind.Display; L.Item = e.Item; L.State = e.Block; return L;
                case "ominous_item_spawner": L.Kind = EntityKind.Item; L.Item = e.Item; return L;
                case "ender_dragon": return Set(L, "ender_dragon", "entity/enderdragon/dragon", RootKind.Dragon);
                case "wither": Set(L, "wither", "entity/wither/wither"); L.Scale = 2; return L;   // WitherBossRenderer.scale: 2
                case "giant": Set(L, "giant", "entity/zombie/zombie"); L.ArmsForward = true; return L;
                case "evoker_fangs": return Set(L, "evoker_fangs", "entity/illager/evoker_fangs", RootKind.Fangs);
                case "shulker_bullet": Set(L, "shulker_bullet", "entity/shulker/spark", RootKind.Bullet); L.Scale = 0.5f; return L;
                case "wither_skull": return Set(L, "wither_skull", "entity/wither/wither", RootKind.Skull);
                case "llama_spit": return Set(L, "llama_spit", "entity/llama/llama_spit", RootKind.Spit);
                case "cushion": return Set(L, "cushion", "entity/cushion/" + (e.Color ?? "white") + "_cushion", RootKind.Cushion);
                case "sulfur_cube":
                    // AbstractCubeMobRenderer like the slime: the opaque inner cube, the outer shell translucent over it.
                    string small = e.Baby ? "_small" : "";
                    Set(L, "sulfur_cube" + small + "#inner", "entity/sulfur_cube/sulfur_cube_inner" + small);
                    L.ExtraModel = "sulfur_cube" + small; L.ExtraTexture = "entity/sulfur_cube/sulfur_cube_outer" + small; L.ExtraTranslucent = true;
                    return L;
            }
            return null;
        }

        static EntityLook Set(EntityLook l, string model, string texture, RootKind root = RootKind.Living)
        {
            l.Kind = EntityKind.Model; l.Model = model; l.Texture = texture; l.Root = root;
            return l;
        }

        /// <summary>Gear, collars, chests, patterns, carried blocks and the name label rule on a resolved look.</summary>
        public static void Dress(EntityInfo e, EntityLook l)
        {
            l.NameShown = e.NameVisible && !string.IsNullOrEmpty(e.Name);
            if (l.Kind != EntityKind.Model || l.Model == null) return;
            string t = e.Path ?? "";
            string body = e.Hand("body"), saddle = e.Hand("saddle");
            switch (t)
            {
                case "horse":
                    Armor(l, body, "horse_armor", "horse_body");
                    Saddle(l, saddle, t);
                    break;
                case "zombie_horse": case "skeleton_horse":
                    Armor(l, body, "undead_horse_armor", "horse_body");
                    Saddle(l, saddle, t);
                    break;
                case "donkey": case "mule":
                    Saddle(l, saddle, t);
                    Chests(l, e);
                    break;
                case "camel": case "camel_husk": case "pig": case "strider":
                    if (!e.Baby) Saddle(l, saddle, t);
                    break;
                case "llama": case "trader_llama":
                    Chests(l, e);
                    string decor = e.Baby ? "llama_baby#decor" : "llama#decor";
                    if (Asset(body) is string carpet) Add(l, decor, Textures(carpet, "llama_body"));
                    else if (t == "trader_llama") Add(l, decor, Textures(e.Baby ? "trader_llama_baby" : "trader_llama", "llama_body"));
                    break;
                case "wolf":
                    Armor(l, body, "wolf_armor", "wolf_body");
                    if (e.Tamed && e.Color != null)
                        (l.Layers ??= new List<GearLayer>()).Add(new GearLayer { Model = l.Model, Texture = e.Baby ? "entity/wolf/wolf_collar_baby" : "entity/wolf/wolf_collar",
                                                                                  Tint = EntityCatalog.DyeColor(e.Color, EntityLook.White), Over = true });
                    break;
                case "cat":
                    if (e.Tamed && e.Color != null)
                        (l.Layers ??= new List<GearLayer>()).Add(new GearLayer { Model = e.Baby ? "cat_baby#collar" : "cat#collar",
                                                                                  Texture = e.Baby ? "entity/cat/cat_collar_baby" : "entity/cat/cat_collar",
                                                                                  Tint = EntityCatalog.DyeColor(e.Color, EntityLook.White) });
                    break;
                case "happy_ghast":
                    Armor(l, body, e.Baby ? "happy_ghast_baby_harness" : "happy_ghast_harness", "happy_ghast_body");
                    break;
                case "nautilus": case "zombie_nautilus":
                    Armor(l, body, "nautilus_armor", "nautilus_body");
                    if (!e.Baby) Saddle(l, saddle, "nautilus");
                    break;
                case "tropical_fish":
                    int n = PatternNumber(e.Variant, out bool large);
                    if (n > 0)
                        (l.Layers ??= new List<GearLayer>()).Add(new GearLayer { Model = large ? "tropical_fish_large#pattern" : "tropical_fish_small#pattern",
                                                                                  Texture = (large ? "entity/fish/tropical_b_pattern_" : "entity/fish/tropical_a_pattern_") + n,
                                                                                  Tint = EntityCatalog.DyeColor(e.Markings, EntityLook.White) });
                    break;
                case "snow_golem":
                    if (!e.Sheared) Block(l, "head", "minecraft:carved_pumpkin[facing=north]", SnowGolemPumpkin);
                    break;
                case "mooshroom":
                    if (!e.Baby)
                    {
                        string m = e.Variant == "brown" ? "minecraft:brown_mushroom" : "minecraft:red_mushroom";
                        Block(l, null, m, MushroomBack1); Block(l, null, m, MushroomBack2); Block(l, "head", m, MushroomHead);
                    }
                    break;
                case "enderman":
                    if (!string.IsNullOrEmpty(e.Carried)) Block(l, null, e.Carried, EndermanBlock);
                    break;
            }
        }

        // SnowGolemHeadLayer, MushroomCowMushroomLayer and CarriedBlockLayer (26.3), in game model space.
        public static readonly AttachOp[] SnowGolemPumpkin = { AttachOp.T(0, -0.34375f, 0), AttachOp.Y(180), AttachOp.S(0.625f, -0.625f, -0.625f), AttachOp.T(-0.5f, -0.5f, -0.5f) };
        public static readonly AttachOp[] MushroomBack1 = { AttachOp.T(0.2f, -0.35f, 0.5f), AttachOp.Y(-48), AttachOp.S(-1, -1, 1), AttachOp.T(-0.5f, -0.5f, -0.5f) };
        public static readonly AttachOp[] MushroomBack2 = { AttachOp.T(0.2f, -0.35f, 0.5f), AttachOp.Y(42), AttachOp.T(0.1f, 0, -0.6f), AttachOp.Y(-48), AttachOp.S(-1, -1, 1), AttachOp.T(-0.5f, -0.5f, -0.5f) };
        public static readonly AttachOp[] MushroomHead = { AttachOp.T(0, -0.7f, -0.2f), AttachOp.Y(-78), AttachOp.S(-1, -1, 1), AttachOp.T(-0.5f, -0.5f, -0.5f) };
        public static readonly AttachOp[] EndermanBlock = { AttachOp.T(0, 0.6875f, -0.75f), AttachOp.X(20), AttachOp.Y(45), AttachOp.T(0.25f, 0.1875f, 0.25f), AttachOp.S(-0.5f, -0.5f, 0.5f), AttachOp.Y(90) };

        static void Block(EntityLook l, string part, string state, AttachOp[] ops) => (l.Blocks ??= new List<BlockAttach>()).Add(new BlockAttach { Part = part, State = state, Ops = ops });

        static void Chests(EntityLook l, EntityInfo e)
        {
            if (e.Chested) return;
            (l.Hidden ??= new HashSet<string>()).Add("left_chest");
            l.Hidden.Add("right_chest");
        }

        static void Armor(EntityLook l, string item, string model, string layerType)
        {
            if (Asset(item) is string asset) Add(l, model, Textures(asset, layerType));
        }

        static void Saddle(EntityLook l, string item, string mob)
        {
            if (Asset(item) is string asset) Add(l, mob + "#saddle", Textures(asset, mob + "_saddle"));
        }

        static void Add(EntityLook l, string model, List<(string texture, Color32? tint)> textures)
        {
            foreach (var (tex, tint) in textures)
                (l.Layers ??= new List<GearLayer>()).Add(new GearLayer { Model = model, Texture = tex, Tint = tint ?? EntityLook.White });
        }

        /// <summary>The equipment asset an item wears as (its equippable asset_id in 26.x): horse, nautilus and wolf
        /// armour by material, carpets, harnesses and the saddle; null for anything else.</summary>
        public static string Asset(string item)
        {
            string p = P(item);
            if (string.IsNullOrEmpty(p)) return null;
            if (p == "saddle") return "saddle";
            if (p == "wolf_armor") return "armadillo_scute";
            if (p.EndsWith("_carpet") || p.EndsWith("_harness")) return p;
            foreach (var suffix in new[] { "_horse_armor", "_nautilus_armor" })
                if (p.EndsWith(suffix)) { string m = p.Substring(0, p.Length - suffix.Length); return m == "golden" ? "gold" : m; }
            return null;
        }

        /// <summary>
        /// An equipment asset's textures for one layer type, as its assets/minecraft/equipment/&lt;asset&gt;.json lists them:
        /// entity/equipment/&lt;layer type&gt;/&lt;texture&gt; with the tint dyeable layers get undyed (leather's
        /// color_when_undyed #A06540; a dyeable layer without one, like the wolf armour's overlay, isn't drawn undyed).
        /// Carpets use their colour's name on llamas.
        /// </summary>
        public static List<(string texture, Color32? tint)> Textures(string asset, string layerType)
        {
            var r = new List<(string, Color32?)>();
            string dir = "entity/equipment/" + layerType + "/";
            if (asset.EndsWith("_carpet")) { r.Add((dir + asset.Substring(0, asset.Length - 7), null)); return r; }
            if (asset == "leather") { r.Add((dir + "leather", new Color32(0xA0, 0x65, 0x40, 255))); r.Add((dir + "leather_overlay", null)); return r; }
            r.Add((dir + asset, null));
            return r;
        }

        /// <summary>TropicalFish.Pattern: 1-6 within its size (texture tropical_a/b_pattern_N), 0 if unknown.</summary>
        public static int PatternNumber(string pattern, out bool large)
        {
            string[] small = { "kob", "sunstreak", "snooper", "dasher", "brinely", "spotty" };
            string[] big = { "flopper", "stripey", "glitter", "blockfish", "betty", "clayfish" };
            int i = Array.IndexOf(small, pattern);
            large = i < 0;
            if (i < 0) i = Array.IndexOf(big, pattern);
            return i < 0 ? 0 : i + 1;
        }

        // ---------- experience orbs (ExperienceOrbRenderer, ExperienceOrb.getIcon) ----------

        static readonly int[] OrbSteps = { 2477, 1237, 617, 307, 149, 73, 37, 17, 7, 3 };

        /// <summary>ExperienceOrb.getIcon: 10 for 2477+ down to 0 for 1-2 points.</summary>
        public static int OrbIcon(int value)
        {
            for (int i = 0; i < OrbSteps.Length; i++) if (value >= OrbSteps[i]) return 10 - i;
            return 0;
        }

        /// <summary>The icon's 16 px cell in entity/experience/experience_orb.png (64 x 64, 4 per row), as texture UVs
        /// (Unity: v up, so the top row is v 0.75..1).</summary>
        public static void OrbUv(int icon, out float u0, out float v0, out float u1, out float v1)
        {
            u0 = (icon % 4 * 16) / 64f; u1 = (icon % 4 * 16 + 16) / 64f;
            float t0 = (icon / 4 * 16) / 64f, t1 = (icon / 4 * 16 + 16) / 64f;   // game v, down from the top
            v0 = 1 - t1; v1 = 1 - t0;
        }

        /// <summary>The orb's pulsing colour: red (sin(age/2) + 1) / 2, full green, blue (sin(age/2 + 4.19) + 1) / 10, alpha 128.</summary>
        public static Color32 OrbColor(float ageTicks)
        {
            float t = ageTicks / 2f;
            int r = (int)((Mathf.Sin(t) + 1f) * 0.5f * 255f), b = (int)((Mathf.Sin(t + 4.1887903f) + 1f) * 0.1f * 255f);
            return new Color32((byte)r, 255, (byte)b, 128);
        }

        /// <summary>ExperienceOrbRenderer.getBlockLightLevel: an orb glows 7 levels above the block light around it.</summary>
        public static int OrbBlockLight(int block) => Math.Min(15, block + 7);

        // ---------- display entities (DisplayRenderer) ----------

        /// <summary>DisplayRenderer.calculateOrientation as a game-frame quaternion (x, y, z, w): rotationYXZ(-yaw, pitch, 0)
        /// with the entity's angles (fixed), the camera's yaw (vertical), its pitch (horizontal) or both (center); the camera
        /// angles enter as transformYRot (yaw - 180) and transformXRot (-pitch).</summary>
        public static float[] Orientation(string billboard, float yaw, float pitch, float camYaw, float camPitch)
        {
            float y = yaw, x = pitch;
            if (billboard == "vertical" || billboard == "center") y = camYaw - 180f;
            if (billboard == "horizontal" || billboard == "center") x = -camPitch;
            return Mul(AxisAngle(0, 1, 0, -y), AxisAngle(1, 0, 0, x));
        }

        /// <summary>The game's yaw and pitch of a look direction (game frame): yaw = atan2(-x, z), pitch = -asin(y).</summary>
        public static void YawPitch(Vector3 dir, out float yaw, out float pitch)
        {
            var d = dir.normalized;
            yaw = Mathf.Atan2(-d.x, d.z) * Mathf.Rad2Deg;
            pitch = -(float)Math.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
        }

        public static float[] AxisAngle(float x, float y, float z, float deg)
        {
            float h = deg * Mathf.Deg2Rad * 0.5f, s = Mathf.Sin(h);
            return new[] { x * s, y * s, z * s, Mathf.Cos(h) };
        }

        public static float[] Mul(float[] a, float[] b) => new[]
        {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
        };

        /// <summary>A game-frame rotation seen through the Z mirror (PROTOCOL.md 3): (x, y, z, w) -> (-x, -y, z, w).</summary>
        public static float[] Mirror(float[] q) => new[] { -q[0], -q[1], q[2], q[3] };
    }
}
