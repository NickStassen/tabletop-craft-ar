// [sound] Which sound the game would play: each block's sound group (the server's sound_groups, else a guess from the
// block id and sounds.json), the sounds of block state changes (doors, levers, buttons, pistons, axes, hoes...), mob
// sounds and vanilla's volume and pitch formulas. Engine-free (Tests~); the hand-written ids are checked against the
// user's sounds.json by `Tests~ --sounds sounds.zip`.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    /// <summary>A block's SoundType: the five events, volume and pitch.</summary>
    public struct BlockSoundGroup
    {
        public string Break, Step, Place, Hit, Fall;
        public float Volume, Pitch;
        public bool Silent => Break == null;
        public static BlockSoundGroup Family(string f, float volume = 1f, float pitch = 1f) => new BlockSoundGroup
        { Break = "block." + f + ".break", Step = "block." + f + ".step", Place = "block." + f + ".place", Hit = "block." + f + ".hit", Fall = "block." + f + ".fall", Volume = volume, Pitch = pitch };
    }

    /// <summary>A sound to play: event id, vanilla volume (over 1 = heard further) and pitch.</summary>
    public struct SoundCue
    {
        public string Event;
        public float Volume, Pitch;
        public SoundCue(string e, float v, float p) { Event = e; Volume = v; Pitch = p; }
        public override string ToString() => $"{Event} v{Volume:F2} p{Pitch:F2}";
    }

    /// <summary>Block id -> sound group: the server's table (sound_groups, PROTOCOL.md "Sound"), else a guess.</summary>
    public sealed class BlockSounds
    {
        public const string Type = "sound_groups";
        readonly Dictionary<string, BlockSoundGroup> _byBlock = new Dictionary<string, BlockSoundGroup>();
        readonly Dictionary<string, BlockSoundGroup> _guessed = new Dictionary<string, BlockSoundGroup>();
        public int Count => _byBlock.Count;
        public int Groups { get; private set; }

        public static BlockSounds From(Dictionary<string, object> m)
        {
            var b = new BlockSounds();
            foreach (var o in m?.A("groups") ?? new List<object>())
            {
                if (!(o is Dictionary<string, object> g)) continue;
                var grp = new BlockSoundGroup
                {
                    Break = SoundDefs.Key(g.S("break")), Step = SoundDefs.Key(g.S("step")), Place = SoundDefs.Key(g.S("place")),
                    Hit = SoundDefs.Key(g.S("hit")), Fall = SoundDefs.Key(g.S("fall")), Volume = (float)g.D("volume", 1), Pitch = (float)g.D("pitch", 1),
                };
                b.Groups++;
                foreach (var id in g.A("blocks") ?? new List<object>()) if (id is string s) b._byBlock[s] = grp;
            }
            return b;
        }

        /// <summary>The group of a block state; null/fluids/air are silent.</summary>
        public BlockSoundGroup Of(string state, SoundDefs defs)
        {
            if (state == null) return default;
            string id = BlockRules.Name(state);
            if (!id.Contains(":")) id = "minecraft:" + id;
            if (_byBlock.TryGetValue(id, out var g)) return g;
            lock (_guessed)
            {
                if (!_guessed.TryGetValue(id, out g)) _guessed[id] = g = Guess(id, defs);
                return g;
            }
        }

        static readonly string[] Oxidation = { "waxed_", "exposed_", "weathered_", "oxidized_" };

        /// <summary>
        /// Without the server's table (an older server): the block's own family when sounds.json has block.&lt;id&gt;.break,
        /// else by words in the id (planks and logs sound like wood, glass like glass...), else stone.
        /// </summary>
        public static BlockSoundGroup Guess(string id, SoundDefs defs)
        {
            string p = BlockRules.Path(id);
            if (p == "air" || p == "cave_air" || p == "void_air" || p == "water" || p == "lava" || p == "bubble_column") return default;
            bool Has(string f) => defs == null || defs.Has("block." + f + ".break");
            if (defs != null && defs.Has("block." + p + ".break") && defs.Has("block." + p + ".step")) return BlockSoundGroup.Family(p);
            string bare = p;
            foreach (var o in Oxidation) if (bare.StartsWith(o)) bare = bare.Substring(o.Length);
            string f = Family(bare);
            return BlockSoundGroup.Family(Has(f) ? f : "stone");
        }

        static bool Any(string p, params string[] words) { foreach (var w in words) if (p.Contains(w)) return true; return false; }

        static string Family(string p)
        {
            if (p.Contains("copper")) return "copper";
            if (p.StartsWith("deepslate_brick")) return "deepslate_bricks";
            if (p.StartsWith("deepslate_tile")) return "deepslate_tiles";
            if (p.StartsWith("polished_deepslate")) return "polished_deepslate";
            if (p.Contains("deepslate")) return "deepslate";
            if (p.Contains("nether_brick")) return "nether_bricks";
            if (p.Contains("netherrack")) return "netherrack";
            if (p.Contains("netherite")) return "netherite_block";
            if (p.Contains("basalt")) return "basalt";
            if (p.StartsWith("tuff_brick")) return "tuff_bricks";
            if (p.StartsWith("polished_tuff")) return "polished_tuff";
            if (p.Contains("tuff")) return "tuff";
            if (p.StartsWith("mud_brick")) return "mud_bricks";
            if (p == "packed_mud") return "packed_mud";
            if (p == "mud") return "mud";
            if (p.Contains("amethyst")) return "amethyst_block";
            if (p.Contains("dripstone")) return "dripstone_block";
            if (p.Contains("soul_sand")) return "soul_sand";
            if (p.Contains("soul_soil")) return "soul_soil";
            if (p.Contains("nylium")) return "nylium";
            if (p.Contains("wart_block")) return "wart_block";
            if (p.StartsWith("crimson_") || p.StartsWith("warped_")) return p.EndsWith("stem") || p.EndsWith("hyphae") ? "stem" : "nether_wood";
            if (p.StartsWith("cherry_")) return p.EndsWith("leaves") ? "cherry_leaves" : p.EndsWith("sapling") ? "cherry_sapling" : "cherry_wood";
            if (p.StartsWith("bamboo_")) return "bamboo_wood";
            if (p.Contains("moss_carpet")) return "moss_carpet";
            if (p.Contains("moss")) return "moss";
            if (Any(p, "carpet", "wool")) return "wool";
            if (Any(p, "planks", "_log", "_wood", "bookshelf", "chest", "barrel", "crafting_table", "note_block", "jukebox", "_sign", "_fence",
                    "_door", "_trapdoor", "_stairs", "_slab", "_button", "_pressure_plate", "lectern", "loom", "composter", "beehive", "campfire"))
                return Any(p, "stone", "brick", "quartz", "sandstone", "prismarine", "purpur", "blackstone", "iron", "andesite", "diorite", "granite", "cobble") ? "stone" : "wood";
            if (Any(p, "glass", "ice", "glowstone", "sea_lantern", "beacon", "redstone_lamp")) return "glass";
            if (p.Contains("concrete_powder") || (p.EndsWith("sand") && !p.Contains("sandstone"))) return "sand";
            if (Any(p, "gravel", "dirt", "farmland", "clay")) return "gravel";
            if (p == "powder_snow") return "powder_snow";
            if (p.Contains("snow")) return "snow";
            if (Any(p, "leaves", "grass", "fern", "sapling", "tulip", "dandelion", "poppy", "orchid", "allium", "bluet", "daisy", "cornflower",
                    "lily", "rose", "mycelium", "podzol", "hay_block", "tnt", "sugar_cane", "bush", "flower", "kelp", "seagrass", "dried_kelp"))
                return "grass";
            if (Any(p, "wheat", "carrots", "potatoes", "beetroots", "melon_stem", "pumpkin_stem", "torchflower_crop", "pitcher_crop")) return "crop";
            if (Any(p, "iron", "gold", "diamond_block", "emerald_block", "lapis_block", "rail", "hopper", "cauldron", "bell")) return "metal";
            if (p.Contains("slime")) return "slime_block";
            if (p.Contains("honey")) return "honey_block";
            if (p.Contains("coral")) return "coral_block";
            if (p.Contains("lantern")) return "lantern";
            if (p == "chain" || p.EndsWith("_chain")) return "chain";
            if (p.Contains("candle")) return "candle";
            if (p.Contains("vine")) return "vine";
            if (p.Contains("sculk")) return "sculk";
            if (p.Contains("froglight")) return "froglight";
            if (p.Contains("scaffolding")) return "scaffolding";
            if (p == "ladder") return "ladder";
            if (p.Contains("anvil")) return "anvil";
            return "stone";
        }
    }

    public static class SoundRules
    {
        static float R(Random rng) => (float)rng.NextDouble();

        // ---------- vanilla's block sound formulas (LevelRenderer level event 2001, MultiPlayerGameMode, Entity) ----------
        public static SoundCue Break(BlockSoundGroup g) => new SoundCue(g.Break, (g.Volume + 1f) / 2f, g.Pitch * 0.8f);
        public static SoundCue Place(BlockSoundGroup g) => new SoundCue(g.Place, (g.Volume + 1f) / 2f, g.Pitch * 0.8f);
        public static SoundCue Hit(BlockSoundGroup g) => new SoundCue(g.Hit, (g.Volume + 1f) / 8f, g.Pitch * 0.5f);
        public static SoundCue Step(BlockSoundGroup g) => new SoundCue(g.Step, g.Volume * 0.15f, g.Pitch);
        public static SoundCue Fall(BlockSoundGroup g) => new SoundCue(g.Fall, g.Volume * 0.5f, g.Pitch * 0.75f);

        /// <summary>Door, trapdoor, fence gate and button sound family (BlockSetType), as the server's InteractRules.</summary>
        public static string SetFamily(string path)
        {
            if (path.StartsWith("iron_")) return "iron";
            if (path.Contains("copper")) return "copper";
            if (path.StartsWith("cherry_")) return "cherry_wood";
            if (path.StartsWith("bamboo_")) return "bamboo_wood";
            if (path.StartsWith("crimson_") || path.StartsWith("warped_")) return "nether_wood";
            if (path == "stone_button" || path == "polished_blackstone_button" || path == "stone_pressure_plate" || path == "polished_blackstone_pressure_plate") return "stone";
            if (path == "light_weighted_pressure_plate" || path == "heavy_weighted_pressure_plate") return "metal";
            return "wooden";
        }

        static string Prop(string s, string k) => BlockEditRules.Prop(s, k);
        static bool On(string s, string k) => Prop(s, k) == "true";

        /// <summary>
        /// The sound of a block changing from old to new in place (a block_update), or null. Covers what the game plays
        /// when a player or redstone uses a block (doors, trapdoors, gates, levers, buttons, pressure plates, pistons,
        /// comparators, candles, campfires, composters) and tools (stripping, scraping, waxing, tilling, paths, carving).
        /// Breaking and placing come from actions instead: a plain block_update (/fill, physics) makes no sound.
        /// </summary>
        public static SoundCue? Transition(string oldState, string newState, Random rng)
        {
            if (oldState == null || newState == null || oldState == newState) return null;
            string op = BlockRules.Path(oldState), np = BlockRules.Path(newState);
            if (op == np)
            {
                if (np.EndsWith("_door"))
                {
                    if (Prop(newState, "half") != "lower" || On(oldState, "open") == On(newState, "open")) return null;
                    return new SoundCue("block." + SetFamily(np) + "_door." + (On(newState, "open") ? "open" : "close"), 1f, R(rng) * 0.1f + 0.9f);
                }
                if (np.EndsWith("_trapdoor") && On(oldState, "open") != On(newState, "open"))
                    return new SoundCue("block." + SetFamily(np) + "_trapdoor." + (On(newState, "open") ? "open" : "close"), 1f, R(rng) * 0.1f + 0.9f);
                if (np.EndsWith("fence_gate") && On(oldState, "open") != On(newState, "open"))
                {
                    string f = SetFamily(np);
                    return new SoundCue("block." + (f == "wooden" ? "" : f + "_") + "fence_gate." + (On(newState, "open") ? "open" : "close"), 1f, R(rng) * 0.1f + 0.9f);
                }
                if (np == "lever" && On(oldState, "powered") != On(newState, "powered"))
                    return new SoundCue("block.lever.click", 0.3f, On(newState, "powered") ? 0.6f : 0.5f);
                if (np.EndsWith("_button") && On(oldState, "powered") != On(newState, "powered"))
                    return new SoundCue("block." + SetFamily(np) + "_button.click_" + (On(newState, "powered") ? "on" : "off"), 1f, 1f);
                if (np.EndsWith("_pressure_plate"))
                {
                    bool was = On(oldState, "powered") || (Prop(oldState, "power") ?? "0") != "0", now = On(newState, "powered") || (Prop(newState, "power") ?? "0") != "0";
                    if (was != now) return new SoundCue("block." + SetFamily(np) + "_pressure_plate.click_" + (now ? "on" : "off"), 1f, 1f);
                    return null;
                }
                if ((np == "piston" || np == "sticky_piston") && On(oldState, "extended") != On(newState, "extended"))
                    return On(newState, "extended") ? new SoundCue("block.piston.extend", 0.5f, R(rng) * 0.25f + 0.6f) : new SoundCue("block.piston.contract", 0.5f, R(rng) * 0.15f + 0.6f);
                if (np == "comparator" && Prop(oldState, "mode") != Prop(newState, "mode"))
                    return new SoundCue("block.comparator.click", 0.3f, Prop(newState, "mode") == "subtract" ? 0.55f : 0.5f);
                if ((np.EndsWith("candle") || np.EndsWith("candle_cake")) && On(oldState, "lit") != On(newState, "lit"))
                    return On(newState, "lit") ? new SoundCue("item.flintandsteel.use", 1f, R(rng) * 0.4f + 0.8f) : new SoundCue("block.candle.extinguish", 1f, 1f);
                if (np.EndsWith("campfire") && On(oldState, "lit") != On(newState, "lit"))
                    return On(newState, "lit") ? new SoundCue("item.flintandsteel.use", 1f, R(rng) * 0.4f + 0.8f) : new SoundCue("entity.generic.extinguish_fire", 1f, 1f);
                if (np == "composter")
                {
                    int a = int.TryParse(Prop(oldState, "level"), out var x) ? x : 0, b = int.TryParse(Prop(newState, "level"), out var y) ? y : 0;
                    if (b == 0 && a == 8) return new SoundCue("block.composter.empty", 1f, 1f);
                    if (b == 8 && a == 7) return new SoundCue("block.composter.ready", 1f, 1f);
                    if (b == a + 1) return new SoundCue(b == 7 ? "block.composter.fill_success" : "block.composter.fill", 1f, 1f);
                }
                return null;
            }
            // Tools: a different block in the same place.
            if (np == "stripped_" + op) return new SoundCue("item.axe.strip", 1f, 1f);
            if (op.Contains("copper") && np.Contains("copper"))
            {
                bool ow = op.StartsWith("waxed_"), nw = np.StartsWith("waxed_");
                string ob = ow ? op.Substring(6) : op, nb = nw ? np.Substring(6) : np;
                if (!ow && nw && ob == nb) return new SoundCue("item.honeycomb.wax_on", 1f, 1f);
                if (ow && !nw && ob == nb) return new SoundCue("item.axe.wax_off", 1f, 1f);
                if (ow == nw && Stage(ob) > Stage(nb) && Bare(ob) == Bare(nb)) return new SoundCue("item.axe.scrape", 1f, 1f);
                return null;   // weathering one stage further is silent
            }
            if (np == "farmland" && (op == "grass_block" || op == "dirt" || op == "dirt_path")) return new SoundCue("item.hoe.till", 1f, 1f);
            if (np == "dirt" && (op == "coarse_dirt" || op == "rooted_dirt")) return new SoundCue("item.hoe.till", 1f, 1f);
            if (np == "dirt_path" && (op == "grass_block" || op == "dirt" || op == "podzol" || op == "coarse_dirt" || op == "mycelium" || op == "rooted_dirt"))
                return new SoundCue("item.shovel.flatten", 1f, 1f);
            if (op == "pumpkin" && np == "carved_pumpkin") return new SoundCue("block.pumpkin.carve", 1f, 1f);
            return null;
        }

        static int Stage(string copper) => copper.StartsWith("exposed_") ? 1 : copper.StartsWith("weathered_") ? 2 : copper.StartsWith("oxidized_") ? 3 : 0;
        static string Bare(string copper)
        {
            foreach (var o in new[] { "exposed_", "weathered_", "oxidized_" }) if (copper.StartsWith(o)) return copper.Substring(o.Length);
            return copper;
        }

        // ---------- mobs (Mob#playAmbientSound, LivingEntity#playHurtSound / die, Mob#getVoicePitch) ----------
        static readonly Dictionary<string, string> MobAlias = new Dictionary<string, string>
        {
            ["mooshroom"] = "cow", ["trader_llama"] = "llama", ["cave_spider"] = "spider", ["giant"] = "zombie", ["illusioner"] = "illusioner",
        };

        /// <summary>The mob's own event (entity.&lt;type&gt;.&lt;what&gt;), its alias's, or for hurt/death the generic one; null if none.</summary>
        public static string Mob(string type, string what, SoundDefs defs)
        {
            string p = BlockRules.Path(type ?? "");
            string k = "entity." + p + "." + what;
            if (defs.Has(k)) return k;
            if (MobAlias.TryGetValue(p, out var a) && defs.Has("entity." + a + "." + what)) return "entity." + a + "." + what;
            if (what == "hurt" || what == "death") return defs.Has("entity.generic." + what) ? "entity.generic." + what : null;
            return null;
        }

        /// <summary>A mob's ambient sound right now (cats stray when untamed, angry wolves growl), or null.</summary>
        public static string Ambient(EntityInfo e, SoundDefs defs)
        {
            string p = e.Path ?? "";
            if (p == "cat" && !e.Tamed && defs.Has("entity.cat.stray_ambient")) return "entity.cat.stray_ambient";
            if (p == "wolf" && e.Angry && defs.Has("entity.wolf.growl")) return "entity.wolf.growl";
            return Mob(e.Type, "ambient", defs);
        }

        /// <summary>LivingEntity#getSoundVolume (louder for the big ones).</summary>
        public static float MobVolume(string type)
        {
            string p = BlockRules.Path(type ?? "");
            return p == "ghast" || p == "ender_dragon" || p == "happy_ghast" ? 5f : p == "warden" ? 4f : 1f;
        }

        /// <summary>Mob#getVoicePitch: a little random around 1, babies around 1.5.</summary>
        public static float VoicePitch(bool baby, Random rng) => (R(rng) - R(rng)) * 0.2f + (baby ? 1.5f : 1f);

        /// <summary>The game's ambient sound clock: each tick a mob plays when rng(1000) &lt; its counter, then waits 80 ticks.</summary>
        public const int AmbientInterval = 80;

        /// <summary>Living entities that walk and make sounds (not items, boats, minecarts, projectiles, displays...).</summary>
        public static bool Living(EntityInfo e) => e.HasBodyYaw || e.Eye > 0;
    }
}
