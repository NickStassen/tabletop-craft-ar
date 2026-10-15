// Which model, textures and render rules each entity type uses: vanilla's renderer choices (model layer per type,
// variant and age; texture paths of the 26.x pack; overlays; second layers like sheep wool) as a table the Quest can
// apply to an EntityInfo. Texture paths are relative to assets/minecraft/textures/ without ".png". Engine-free.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>[living] Orb: an experience orb's billboard; Billboard: a camera-facing sprite (dragon fireball);
    /// Lightning: the bolt mesh (LightningBolt.cs); Display: block, item and text displays (DisplayTransform).</summary>
    public enum EntityKind { Model, Item, Block, Frame, Painting, Box, Orb, Billboard, Lightning, Display }

    /// <summary>How the model root sits in the entity's frame (the game renderer's transforms before the model).
    /// Origin: the renderer only flips the model (scale -1, -1, 1), so its origin is the entity position (LeashKnotRenderer).
    /// Projectile: ArrowRenderer, rotY(yRot - 90) * rotZ(xRot), no flip, no lift. Trident: ThrownTridentRenderer,
    /// rotY(yRot - 90) * rotZ(xRot + 90). Crystal: EndCrystalRenderer, scale 2, translate (0, -0.5, 0), no flip, no yaw.</summary>
    /// [living] Dragon: EnderDragonRenderer, rotY(-yaw), 1 block forward, then the living flip and lift. Fangs:
    /// EvokerFangsRenderer, rotY(90 - yaw), flip and lift. Spit: LlamaSpitRenderer, 0.15 up then like a projectile.
    /// Bullet: ShulkerBulletRenderer, 0.15 up, spinning, flipped at half size. Cushion: CushionRenderer, rotY(180 -
    /// facing), rotX(180), 0.25 down. Skull: WitherSkullRenderer, the flip only (the head part turns with yaw and pitch).
    public enum RootKind { Living, Boat, Minecart, Origin, Projectile, Trident, Crystal, Dragon, Fangs, Spit, Bullet, Cushion, Skull }

    public sealed class EntityLook
    {
        public EntityKind Kind = EntityKind.Box;
        public string Model;                       // model layer in entity_models.json (EntityModels)
        public string Texture;                     // base texture
        public List<string> Overlays;              // drawn over the base in the same texture (villager type, horse markings)
        public string ExtraModel, ExtraTexture;    // a second, inflated layer (sheep wool, stray/drowned/bogged outer, slime)
        public Color32 Tint = White, ExtraTint = White;
        public bool ExtraTranslucent;              // slime outer layer
        public RootKind Root = RootKind.Living;
        public float Scale = 1;
        public bool ArmsForward;                   // zombies hold their arms out
        public bool HideArms;                      // illagers: crossed "arms" part instead of right_arm/left_arm
        public bool ArmorStand;                    // posed by StandPart, not animated
        public string Item;                        // Item/Block/Frame: what to show; minecart contents
        public string Contents;                    // minecart display block (item id)
        public Color32 BoxColor = White;           // fallback box
        // [living] gear and collars (more layers on the same parts), parts not drawn, block models on the figure
        public List<GearLayer> Layers;
        public HashSet<string> Hidden;
        public List<BlockAttach> Blocks;
        public int Orb = -1;                       // experience orb icon (LivingLooks.OrbIcon)
        public string State;                       // block display: its block state
        public float RootLift;                     // pixels added to RootOffset's height (a renderer's extra translate)
        public bool NameShown;                     // name label: CustomNameVisible

        /// <summary>Texture cache key: base + overlays + tint.</summary>
        public string TextureKey => Texture + (Overlays == null ? "" : "+" + string.Join("+", Overlays));

        public static readonly Color32 White = new Color32(255, 255, 255, 255);
    }

    public static class EntityCatalog
    {
        /// <summary>The model root's offset from the entity position in model pixels: living entities stand on y 0 (model
        /// origin 1.501 blocks up), boats and minecarts sit 6 px up, a leash knot's and a projectile's origin is the
        /// entity position (a knot spans 0.375-0.875 of its fence post), an end crystal's is 8 px (scaled: 1 block) down.</summary>
        public static Vector3 RootOffset(RootKind k)
        {
            switch (k)
            {
                case RootKind.Living: case RootKind.Fangs: return new Vector3(0, 24.016f, 0);
                case RootKind.Boat: case RootKind.Minecart: return new Vector3(0, 6, 0);
                case RootKind.Crystal: return new Vector3(0, -8, 0);
                case RootKind.Dragon: return new Vector3(0, 24.016f, -16);             // [living] 1 block forward
                case RootKind.Spit: return new Vector3(0, 2.4f, 0);                    // 0.15 up
                case RootKind.Bullet: return new Vector3(0, 4.8f, 0);                  // 0.15 up inside the half-size root
                case RootKind.Cushion: return new Vector3(0, 4, 0);                    // 0.25 down after the flip
                default: return new Vector3(0, 0, 0);
            }
        }

        // Figure space is the game's model space point-reflected (EntityModels.Pivot), seen through the renderer's Z
        // mirror. A game rotation rotY(a) becomes a Unity rotY(-a), rotZ(b) stays rotZ(b), and a renderer without the
        // living (-1, -1, 1) flip leaves an extra rotZ(180) on the figure meshes.

        /// <summary>The model root's rotation, applied after RootOffset: localRotation = rotY(yaw) * rotZ(roll), in
        /// degrees (Unity / figure space). pitch: the entity's pitch (minecarts tilt, projectiles point along it).</summary>
        public static void RootRotation(RootKind k, float pitch, out float yaw, out float roll)
        {
            yaw = 0; roll = 0;
            switch (k)
            {
                case RootKind.Boat: yaw = 90; break;                                  // AbstractBoatRenderer: turned 90 degrees
                case RootKind.Minecart: roll = -pitch; break;
                case RootKind.Projectile: case RootKind.Spit: yaw = -90; roll = pitch + 180; break;   // rotY(yRot - 90) * rotZ(xRot), unflipped
                case RootKind.Trident: yaw = -90; roll = pitch + 270; break;          // rotY(yRot - 90) * rotZ(xRot + 90), unflipped
                case RootKind.Crystal: roll = 180; break;                             // unflipped, no yaw
                case RootKind.Cushion: yaw = 180; break;                              // [living] rotX(180) = the flip turned 180
            }
        }

        /// <summary>The figure root's turn about Y (degrees) for an entity yaw. Mobs and vehicles: 180 + yaw, like the
        /// player figures. Projectiles keep the game's projectile convention (yRot = atan2(dx, dz), the mob one mirrored):
        /// 180 - yaw. Leash knots and end crystals ignore the yaw.</summary>
        public static float RootTurn(RootKind k, float yaw)
        {
            switch (k)
            {
                case RootKind.Projectile: case RootKind.Trident: case RootKind.Spit: return 180 - yaw;
                case RootKind.Origin: case RootKind.Crystal: case RootKind.Bullet: case RootKind.Skull: return 0;
                case RootKind.Dragon: return yaw;                                    // [living] rotY(-yaw): the living turn + 180
                case RootKind.Fangs: return yaw - 90;                                // rotY(90 - yaw)
                case RootKind.Cushion: return 180 + Mathf.Round(yaw / 90f) * 90f;    // its facing, a horizontal direction
                default: return 180 + yaw;
            }
        }

        /// <summary>
        /// Armour stands (ArmorStandArmorModel / ArmorStandModel.setupAnim): a part's rotation in degrees (game numbers,
        /// which figure space keeps) and whether it is drawn. The pose replaces the default rotations of head, body,
        /// arms and legs; the body and shoulder sticks follow the body; the base plate turns against the stand's yaw so
        /// it stays square to the world; arms only with "arms", the base plate only with "base_plate"; the humanoid hat
        /// never. False for parts the stand doesn't drive.
        /// </summary>
        public static bool StandPart(EntityInfo e, string part, float yawDeg, out Vector3 deg, out bool visible)
        {
            deg = new Vector3(0, 0, 0); visible = true;
            var p = e.StandPose ?? EntityInfo.DefaultStandPose;
            int i;
            switch (part)
            {
                case "head": i = 0; break;
                case "body": case "right_body_stick": case "left_body_stick": case "shoulder_stick": i = 1; break;
                case "left_arm": i = 2; visible = e.Arms; break;
                case "right_arm": i = 3; visible = e.Arms; break;
                case "left_leg": i = 4; break;
                case "right_leg": i = 5; break;
                case "base_plate": deg = new Vector3(0, -yawDeg, 0); visible = e.BasePlate; return true;
                case "hat": visible = false; return true;
                default: return false;
            }
            deg = new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
            return true;
        }

        static readonly HashSet<string> Woods =new HashSet<string> { "oak", "spruce", "birch", "jungle", "acacia", "cherry", "dark_oak", "pale_oak", "mangrove", "poplar" };
        // Thrown things drawn as their item sprite.
        static readonly Dictionary<string, string> ThrownItems = new Dictionary<string, string>
        {
            ["snowball"] = "minecraft:snowball", ["egg"] = "minecraft:egg", ["ender_pearl"] = "minecraft:ender_pearl",
            ["experience_bottle"] = "minecraft:experience_bottle", ["splash_potion"] = "minecraft:splash_potion",
            ["lingering_potion"] = "minecraft:lingering_potion", ["eye_of_ender"] = "minecraft:ender_eye",
            ["fireball"] = "minecraft:fire_charge", ["small_fireball"] = "minecraft:fire_charge", ["firework_rocket"] = "minecraft:firework_rocket",
            ["wind_charge"] = "minecraft:wind_charge", ["breeze_wind_charge"] = "minecraft:wind_charge", ["fishing_bobber"] = "minecraft:string",
        };
        static readonly Dictionary<string, string> MinecartBlocks = new Dictionary<string, string>
        {
            ["chest_minecart"] = "minecraft:chest", ["furnace_minecart"] = "minecraft:furnace", ["hopper_minecart"] = "minecraft:hopper",
            ["tnt_minecart"] = "minecraft:tnt", ["spawner_minecart"] = "minecraft:spawner", ["command_block_minecart"] = "minecraft:command_block",
        };

        /// <summary>The look of an entity (always non-null; unknown types get a coloured box of their size). [living] The
        /// types this file doesn't know (dragon, wither, orbs, displays...) and the gear, collars, chests and blocks on
        /// known ones come from LivingLooks.</summary>
        public static EntityLook Resolve(EntityInfo e)
        {
            var special = LivingLooks.Resolve(e);
            var look = special ?? ResolveVanilla(e);
            LivingLooks.Dress(e, look);
            return look;
        }

        static EntityLook ResolveVanilla(EntityInfo e)
        {
            string t = e.Path ?? "";
            string v = e.Variant;
            bool baby = e.Baby;
            string B(string s) => baby ? s + "_baby" : s;           // texture/model name with the baby suffix
            var L = new EntityLook { Kind = EntityKind.Model, BoxColor = BoxColor(t) };

            // Vehicles. 26.x has one entity type per boat wood.
            if (t.EndsWith("_chest_boat") || t == "bamboo_chest_raft")
            {
                string w = t == "bamboo_chest_raft" ? "bamboo" : t.Substring(0, t.Length - 11);
                return Woods.Contains(w) || w == "bamboo" ? Set(L, "chest_boat/" + w, "entity/chest_boat/" + w, RootKind.Boat) : Box(L);
            }
            if (t.EndsWith("_boat") || t == "bamboo_raft")
            {
                string w = t == "bamboo_raft" ? "bamboo" : t.Substring(0, t.Length - 5);
                return Woods.Contains(w) || w == "bamboo" ? Set(L, "boat/" + w, "entity/boat/" + w, RootKind.Boat) : Box(L);
            }
            if (t == "minecart" || MinecartBlocks.ContainsKey(t))
            {
                Set(L, t, "entity/minecart/minecart", RootKind.Minecart);
                MinecartBlocks.TryGetValue(t, out L.Contents);
                return L;
            }

            switch (t)
            {
                // Items and blocks.
                case "item": L.Kind = EntityKind.Item; L.Item = e.Item; return L;
                case "item_display": L.Kind = EntityKind.Item; L.Item = e.Item; return L;
                case "item_frame": case "glow_item_frame": L.Kind = EntityKind.Frame; L.Item = e.Item; L.Texture = t == "item_frame" ? "block/item_frame" : "block/glow_item_frame"; return L;
                case "painting": L.Kind = EntityKind.Painting; L.Texture = "painting/" + (v ?? "kebab"); return L;
                case "falling_block": case "block_display": L.Kind = EntityKind.Block; L.Item = BlockItem(e.Block); return L;
                case "tnt": L.Kind = EntityKind.Block; L.Item = "minecraft:tnt"; return L;

                // Farm animals (26.x temperate/warm/cold variants have their own models).
                case "cow":
                    v = v ?? "temperate";
                    return Set(L, B(v == "cold" ? "cold_cow" : v == "warm" ? "warm_cow" : "cow"), B("entity/cow/cow_" + v));
                case "mooshroom": return Set(L, B("mooshroom"), B("entity/cow/mooshroom_" + (v ?? "red")));
                case "pig":
                    v = v ?? "temperate";
                    return Set(L, baby ? "pig_baby" : v == "cold" ? "cold_pig" : "pig", B("entity/pig/pig_" + v));
                case "chicken":
                    v = v ?? "temperate";
                    return Set(L, baby ? "chicken_baby" : v == "cold" ? "cold_chicken" : "chicken", B("entity/chicken/chicken_" + v));
                case "sheep":
                    Set(L, B("sheep"), B("entity/sheep/sheep"));
                    if (!e.Sheared) { L.ExtraModel = B("sheep") + "#wool"; L.ExtraTexture = B("entity/sheep/sheep_wool"); L.ExtraTint = WoolColor(e.Color); }
                    return L;
                case "horse":
                    Set(L, B("horse"), B("entity/horse/horse_" + HorseColor(v)));
                    if (e.Markings != null) L.Overlays = new List<string> { B("entity/horse/horse_markings_" + e.Markings.Replace("_", "")) };
                    return L;
                case "donkey": return Set(L, B("donkey"), B("entity/horse/donkey"));
                case "mule": return Set(L, B("mule"), B("entity/horse/mule"));
                case "skeleton_horse": return Set(L, B("skeleton_horse"), B("entity/horse/horse_skeleton"));
                case "zombie_horse": return Set(L, B("zombie_horse"), B("entity/horse/horse_zombie"));
                case "camel": return Set(L, B("camel"), B("entity/camel/camel"));
                case "camel_husk": return Set(L, "camel", "entity/camel/camel_husk");
                case "llama": return Set(L, B("llama"), B("entity/llama/llama_" + (v ?? "creamy")));
                case "trader_llama": return Set(L, B("trader_llama"), B("entity/llama/llama_" + (v ?? "creamy")));
                case "wolf":
                    string wolf = v == null || v == "pale" ? "wolf" : "wolf_" + v;
                    return Set(L, B("wolf"), B("entity/wolf/" + wolf + (e.Angry ? "_angry" : e.Tamed ? "_tame" : "")));
                case "cat": return Set(L, B("cat"), B("entity/cat/cat_" + (v ?? "tabby")));
                case "ocelot": return Set(L, B("ocelot"), B("entity/cat/ocelot"));
                case "fox": return Set(L, B("fox"), B("entity/fox/fox" + (v == "snow" ? "_snow" : "") + (e.Pose == "sleeping" ? "_sleep" : "")));
                case "rabbit": return Set(L, B("rabbit"), B("entity/rabbit/rabbit_" + RabbitType(v)));
                case "goat": return Set(L, B("goat"), B("entity/goat/goat"));
                case "polar_bear": return Set(L, B("polar_bear"), B("entity/bear/polarbear"));
                case "panda":
                    string gene = v ?? "normal";
                    return Set(L, B("panda"), baby ? (gene == "normal" ? "entity/panda/panda_baby" : "entity/panda/" + gene + "_panda_baby")
                                                   : (gene == "normal" ? "entity/panda/panda" : "entity/panda/panda_" + gene));
                case "bee": return Set(L, B("bee"), B("entity/bee/bee" + (e.Angry ? "_angry" : "")));
                case "parrot": return Set(L, "parrot", "entity/parrot/parrot_" + ParrotVariant(v));
                case "bat": return Set(L, "bat", "entity/bat/bat");
                case "frog": return Set(L, "frog", "entity/frog/frog_" + (v ?? "temperate"));
                case "tadpole": return Set(L, "tadpole", "entity/tadpole/tadpole");
                case "armadillo": return Set(L, B("armadillo"), B("entity/armadillo/armadillo"));
                case "sniffer": return Set(L, B("sniffer"), baby ? "entity/sniffer/snifflet" : "entity/sniffer/sniffer");
                case "strider": return Set(L, B("strider"), B("entity/strider/strider"));
                case "hoglin": return Set(L, B("hoglin"), B("entity/hoglin/hoglin"));
                case "zoglin": return Set(L, B("zoglin"), B("entity/hoglin/zoglin"));
                case "allay": return Set(L, "allay", "entity/allay/allay");
                case "vex": return Set(L, "vex", "entity/illager/vex");
                case "happy_ghast": return Set(L, B("happy_ghast"), B("entity/ghast/happy_ghast"));
                case "ghast": return Set(L, "ghast", "entity/ghast/ghast");

                // Water.
                case "axolotl": return Set(L, B("axolotl"), B("entity/axolotl/axolotl_" + (v ?? "lucy")));
                case "cod": return Set(L, "cod", "entity/fish/cod");
                case "salmon": return Set(L, v == "small" ? "salmon_small" : v == "large" ? "salmon_large" : "salmon", "entity/fish/salmon");
                case "tropical_fish":
                    bool large = v != null && Array.IndexOf(new[] { "flopper", "stripey", "glitter", "blockfish", "betty", "clayfish" }, v) >= 0;
                    Set(L, large ? "tropical_fish_large" : "tropical_fish_small", large ? "entity/fish/tropical_b" : "entity/fish/tropical_a");
                    L.Tint = DyeColor(e.Color, EntityLook.White);
                    return L;
                case "pufferfish": return Set(L, v == "big" ? "pufferfish_big" : v == "medium" ? "pufferfish_medium" : "pufferfish_small", "entity/fish/pufferfish");
                case "squid": return Set(L, B("squid"), B("entity/squid/squid"));
                case "glow_squid": return Set(L, B("glow_squid"), B("entity/squid/glow_squid"));
                case "dolphin": return Set(L, B("dolphin"), B("entity/dolphin/dolphin"));
                case "turtle": return Set(L, B("turtle"), B("entity/turtle/turtle"));
                case "guardian": return Set(L, "guardian", "entity/guardian/guardian");
                case "elder_guardian": return Set(L, "elder_guardian", "entity/guardian/guardian_elder");
                case "nautilus": return Set(L, B("nautilus"), B("entity/nautilus/nautilus"));
                case "zombie_nautilus": return Set(L, "zombie_nautilus", "entity/nautilus/zombie_nautilus");

                // Humanoids.
                case "zombie": Set(L, B("zombie"), B("entity/zombie/zombie")); L.ArmsForward = true; return L;
                case "husk": Set(L, B("husk"), B("entity/zombie/husk")); L.ArmsForward = true; return L;
                case "drowned":
                    Set(L, B("drowned"), B("entity/zombie/drowned")); L.ArmsForward = true;
                    L.ExtraModel = B("drowned") + "#outer"; L.ExtraTexture = B("entity/zombie/drowned_outer_layer");
                    return L;
                case "zombie_villager":
                    Set(L, B("zombie_villager"), B("entity/zombie_villager/zombie_villager")); L.ArmsForward = true;
                    L.Overlays = VillagerOverlays("entity/zombie_villager/", v, e.Profession, baby);
                    return L;
                case "skeleton": return Set(L, "skeleton", "entity/skeleton/skeleton");
                case "stray": Set(L, "stray", "entity/skeleton/stray"); L.ExtraModel = "stray#outer"; L.ExtraTexture = "entity/skeleton/stray_overlay"; return L;
                case "bogged": Set(L, "bogged", "entity/skeleton/bogged"); L.ExtraModel = "bogged#outer"; L.ExtraTexture = "entity/skeleton/bogged_overlay"; return L;
                case "wither_skeleton": return Set(L, "wither_skeleton", "entity/skeleton/wither_skeleton");
                case "parched": return Set(L, "parched", "entity/skeleton/parched");
                case "villager":
                    Set(L, B("villager"), B("entity/villager/villager"));
                    L.Overlays = VillagerOverlays("entity/villager/", v, e.Profession, baby);
                    return L;
                case "wandering_trader": return Set(L, "wandering_trader", "entity/wandering_trader/wandering_trader");
                case "witch": return Set(L, "witch", "entity/witch/witch");
                case "pillager": case "vindicator": case "evoker": case "illusioner":
                    Set(L, t, "entity/illager/" + t); L.HideArms = true; return L;
                case "ravager": return Set(L, "ravager", "entity/illager/ravager");
                case "piglin": return Set(L, B("piglin"), B("entity/piglin/piglin"));
                case "piglin_brute": return Set(L, "piglin_brute", "entity/piglin/piglin_brute");
                case "zombified_piglin": return Set(L, B("zombified_piglin"), B("entity/piglin/zombified_piglin"));
                case "enderman": return Set(L, "enderman", "entity/enderman/enderman");
                case "mannequin": return Set(L, "player", "entity/player/wide/steve");
                case "iron_golem": return Set(L, "iron_golem", "entity/iron_golem/iron_golem");
                case "snow_golem": return Set(L, "snow_golem", "entity/snow_golem/snow_golem");
                case "copper_golem": return Set(L, "copper_golem", "entity/copper_golem/copper_golem");
                case "creaking": return Set(L, "creaking", "entity/creaking/creaking");
                case "blaze": return Set(L, "blaze", "entity/blaze/blaze");
                case "breeze": return Set(L, "breeze", "entity/breeze/breeze");
                case "warden": return Set(L, "warden", "entity/warden/warden");

                // Others.
                case "creeper": return Set(L, "creeper", "entity/creeper/creeper");
                case "spider": return Set(L, "spider", "entity/spider/spider");
                case "cave_spider": return Set(L, "cave_spider", "entity/spider/cave_spider");
                case "silverfish": return Set(L, "silverfish", "entity/silverfish/silverfish");
                case "endermite": return Set(L, "endermite", "entity/endermite/endermite");
                case "slime":
                    Set(L, "slime", "entity/slime/slime");
                    L.ExtraModel = "slime#outer"; L.ExtraTexture = "entity/slime/slime"; L.ExtraTranslucent = true;
                    L.Scale = Math.Max(0.5f, e.Width / 0.52f);
                    return L;
                case "magma_cube": Set(L, "magma_cube", "entity/slime/magmacube"); L.Scale = Math.Max(0.5f, e.Width / 0.52f); return L;
                case "phantom": return Set(L, "phantom", "entity/phantom/phantom");
                case "shulker": return Set(L, "shulker", e.Color == null ? "entity/shulker/shulker" : "entity/shulker/shulker_" + e.Color);
                case "armor_stand": Set(L, e.Small ? "armor_stand_small" : "armor_stand", "entity/armorstand/armorstand"); L.ArmorStand = true; return L;
                case "arrow": case "spectral_arrow":
                    return Set(L, "arrow", t == "arrow" ? "entity/projectiles/arrow" : "entity/projectiles/arrow_spectral", RootKind.Projectile);
                case "trident": return Set(L, "trident", "entity/trident/trident", RootKind.Trident);
                case "end_crystal": Set(L, "end_crystal", "entity/end_crystal/end_crystal", RootKind.Crystal); L.Scale = 2; return L;
                case "leash_knot": return Set(L, "leash_knot", "entity/lead_knot/lead_knot", RootKind.Origin);
            }
            if (ThrownItems.TryGetValue(t, out var thrown)) { L.Kind = EntityKind.Item; L.Item = thrown; return L; }
            return Box(L);
        }

        static EntityLook Set(EntityLook l, string model, string texture, RootKind root = RootKind.Living)
        {
            l.Kind = EntityKind.Model; l.Model = model; l.Texture = texture; l.Root = root;
            return l;
        }

        static EntityLook Box(EntityLook l) { l.Kind = EntityKind.Box; l.Model = null; return l; }

        /// <summary>A block state's item id ("minecraft:oak_log[axis=y]" -> "minecraft:oak_log").</summary>
        public static string BlockItem(string state)
        {
            if (string.IsNullOrEmpty(state)) return null;
            int b = state.IndexOf('[');
            return b >= 0 ? state.Substring(0, b) : state;
        }

        static string HorseColor(string v) => v == null ? "brown" : v == "dark_brown" ? "darkbrown" : v;

        static string RabbitType(string v)
        {
            switch (v)
            {
                case "black_and_white": return "white_splotched";
                case "salt_and_pepper": return "salt";
                case "the_killer_bunny": return "caerbannog";
                case null: return "brown";
                default: return v;
            }
        }

        static string ParrotVariant(string v)
        {
            switch (v)
            {
                case "blue": return "blue";
                case "green": return "green";
                case "cyan": return "yellow_blue";
                case "gray": return "grey";
                default: return "red_blue";
            }
        }

        static List<string> VillagerOverlays(string root, string type, string profession, bool baby)
        {
            var l = new List<string> { root + (baby ? "baby/" : "type/") + (type ?? "plains") };
            if (!baby && profession != null) l.Add(root + "profession/" + profession);
            return l;
        }

        /// <summary>A dye's texture colour (the game's DyeColor.getTextureDiffuseColor, read into entity_models.json by the
        /// generator), or fallback when there is no such dye or no model data.</summary>
        public static Color32 DyeColor(string name, Color32 fallback)
        {
            if (!EntityModels.Dye(name, out int c)) return fallback;
            return new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);
        }

        /// <summary>Sheep wool colour: the dye's colour at 75 % brightness, white only slightly greyed (as Sheep.getColor
        /// does); plain wool when the dye colours aren't loaded.</summary>
        public static Color32 WoolColor(string name)
        {
            var c = DyeColor(name ?? "white", EntityLook.White);
            float k = name == null || name == "white" ? 0.93f : 0.75f;
            return new Color32((byte)(c.r * k), (byte)(c.g * k), (byte)(c.b * k), 255);
        }

        /// <summary>A stable, muted colour per type for the fallback box.</summary>
        public static Color32 BoxColor(string type)
        {
            int h = 17;
            foreach (char ch in type ?? "") h = h * 31 + ch;
            return new Color32((byte)(110 + (h & 0x7F)), (byte)(110 + ((h >> 7) & 0x7F)), (byte)(110 + ((h >> 14) & 0x7F)), 255);
        }

        /// <summary>The armour texture for an equipped item key and slot ("minecraft:iron_chestplate", "chest"), or null.
        /// dyeable: leather, tinted like the item's sprite (its default dye comes from the pack's items/*.json).</summary>
        public static string ArmorTexture(string item, string slot, out bool dyeable)
        {
            dyeable = false;
            if (item == null) return null;
            string p = item.StartsWith("minecraft:") ? item.Substring(10) : item;
            if (p == "turtle_helmet") return slot == "head" ? "entity/equipment/humanoid/turtle_scute" : null;
            string[] kinds = { "_helmet", "_chestplate", "_leggings", "_boots" };
            string[] slots = { "head", "chest", "legs", "feet" };
            for (int i = 0; i < 4; i++)
            {
                if (!p.EndsWith(kinds[i]) || slots[i] != slot) continue;
                string mat = p.Substring(0, p.Length - kinds[i].Length);
                if (mat == "golden") mat = "gold";
                dyeable = mat == "leather";
                return "entity/equipment/" + (slot == "legs" ? "humanoid_leggings/" : "humanoid/") + mat;
            }
            return null;
        }
    }
}
