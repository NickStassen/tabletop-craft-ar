package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * The blocks the headset's block menu offers (block_registry, PROTOCOL.md 4.24), and the rule a block_edit "place"
 * must pass: its block is a registry block, or a variant of one (a wall torch is placed with the torch item).
 *
 * <p>A block is placeable when it has an item form whose block is itself (Bukkit {@code ItemType#getBlockType}), isn't
 * air, isn't operator/technical ({@link #TECHNICAL}, config.yml {@code edit.deny}) and is enabled in the world. Order:
 * the server's item registry order, which is vanilla's and groups families (all oak blocks together). Paper exposes no
 * creative tabs ({@code ItemType#getCreativeCategory} is deprecated and returns one value for everything), so the
 * category comes from block tags and names ({@link #categorize}): the first matching rule of redstone, colored,
 * functional, natural, building wins, and anything left is "other".
 *
 * <p>Building it ({@link #build}) is pure and unit-tested; {@link RegistryScan} reads the candidates from Paper.
 */
public final class BlockRegistry {
    /** Menu categories in tab order (id, label). Vanilla's creative tabs for blocks, plus a catch-all. */
    public static final List<String[]> CATEGORIES = List.of(
        new String[] { "building", "Building Blocks" }, new String[] { "colored", "Colored Blocks" },
        new String[] { "natural", "Natural Blocks" }, new String[] { "functional", "Functional Blocks" },
        new String[] { "redstone", "Redstone Blocks" }, new String[] { "other", "Other" });

    /** Blocks with an item form that are never offered: invisible/technical ones and the legacy petrified slab. */
    public static final Set<String> TECHNICAL = Set.of("minecraft:air", "minecraft:cave_air", "minecraft:void_air",
        "minecraft:barrier", "minecraft:light", "minecraft:structure_void", "minecraft:petrified_oak_slab");

    /** Block tags the categories use (Bukkit Tag.REGISTRY_BLOCKS, minecraft namespace). */
    public static final List<String> TAGS = List.of("logs", "planks", "stairs", "slabs", "walls", "fences", "fence_gates",
        "doors", "trapdoors", "base_stone_overworld", "base_stone_nether", "leaves", "saplings", "flowers", "small_flowers",
        "crops", "dirt", "sand", "ice", "nylium", "corals", "coral_blocks", "wall_corals", "wart_blocks", "cave_vines",
        "wool", "wool_carpets", "terracotta", "candles", "beds", "banners", "shulker_boxes", "rails", "buttons",
        "pressure_plates", "all_signs", "anvil", "cauldrons", "campfires", "flower_pots");

    /**
     * One block as Paper reports it, before filtering.
     *
     * @param id      block id, "minecraft:oak_log"
     * @param state   default state, {@code BlockType#createBlockData().getAsString()}
     * @param item    id of its item form, or null without one
     * @param home    block id of that item's block (== id for the block's own item; a wall torch's is the torch's)
     * @param tags    which of {@link #TAGS} the block is in
     * @param enabled enabled by the world's feature flags
     */
    public record Candidate(String id, String state, String item, String home, Set<String> tags, boolean enabled) {}

    /** A menu entry: default state, category id, and the item id when it differs from the block id (else null). */
    public record Entry(String id, String state, String category, String item) {}

    private final List<Entry> entries;
    private final Set<String> ids;
    private final Map<String, String> variants;   // block id -> registry block id (wall torch -> torch)
    private final JsonObject message;

    private BlockRegistry(List<Entry> entries, Map<String, String> variants) {
        this.entries = List.copyOf(entries);
        Set<String> s = new HashSet<>();
        for (Entry e : entries) s.add(e.id());
        this.ids = Set.copyOf(s);
        this.variants = Collections.unmodifiableMap(new LinkedHashMap<>(variants));
        this.message = toJson();
    }

    /** An empty registry (before the server could be read): nothing is placeable. */
    public static BlockRegistry empty() { return new BlockRegistry(List.of(), Map.of()); }

    /** Filters candidates (in registry order) into entries and variants. deny holds block ids (config edit.deny). */
    public static BlockRegistry build(List<Candidate> candidates, Set<String> deny) {
        List<Entry> entries = new ArrayList<>();
        Set<String> seen = new HashSet<>();
        for (Candidate c : candidates) {
            if (c.item() == null || !c.id().equals(c.home()) || !c.enabled()) continue;
            if (TECHNICAL.contains(c.id()) || deny.contains(c.id()) || !seen.add(c.id())) continue;
            String item = c.item().equals(c.id()) ? null : c.item();
            entries.add(new Entry(c.id(), c.state(), categorize(c.id(), c.tags()), item));
        }
        // Paper doesn't expose vanilla's registry order: group families instead (sortKey), like the creative tabs do.
        List<String> tabs = CATEGORIES.stream().map(t -> t[0]).toList();
        entries.sort(java.util.Comparator.comparingInt((Entry e) -> tabs.indexOf(e.category())).thenComparing(e -> sortKey(e.id(), e.category())));
        Map<String, String> variants = new java.util.TreeMap<>();
        for (Candidate c : candidates)
            if (c.item() != null && c.home() != null && !c.id().equals(c.home()) && seen.contains(c.home())
                && !deny.contains(c.id()) && !TECHNICAL.contains(c.id()) && !seen.contains(c.id()))
                variants.put(c.id(), c.home());
        return new BlockRegistry(entries, variants);
    }

    public List<Entry> entries() { return entries; }
    public Map<String, String> variants() { return variants; }
    public int size() { return entries.size(); }

    /** True when a block_edit may place this block id: a registry block or a variant of one. */
    public boolean placeable(String blockId) { return ids.contains(blockId) || variants.containsKey(blockId); }

    /**
     * Why a "place" of this state is refused (bad_state), or null when it may be placed. The state must parse as a
     * block (the caller checked that) and name a registry block or variant: fire, water, portals, piston heads, the
     * operator blocks and everything else without an item form can't be placed from the headset.
     */
    public String refusePlace(String state) {
        String id = BlockEdit.blockId(state);
        if (placeable(id)) return null;
        return id + " is not a placeable block (not in block_registry)";
    }

    /** The block_registry message body (a copy: Session.send adds the envelope to it). */
    public JsonObject message() { return message.deepCopy(); }

    private JsonObject toJson() {
        JsonObject o = Protocol.body("block_registry");
        o.addProperty("count", entries.size());
        JsonArray cats = new JsonArray();
        for (String[] c : CATEGORIES) {
            JsonObject co = new JsonObject();
            co.addProperty("id", c[0]);
            co.addProperty("label", c[1]);
            JsonArray blocks = new JsonArray();
            for (Entry e : entries) if (e.category().equals(c[0])) blocks.add(e.state());
            co.add("blocks", blocks);
            cats.add(co);
        }
        o.add("categories", cats);
        JsonObject items = new JsonObject();
        for (Entry e : entries) if (e.item() != null) items.addProperty(e.id(), e.item());
        o.add("items", items);
        JsonObject vars = new JsonObject();
        variants.forEach(vars::addProperty);
        o.add("variants", vars);
        return o;
    }

    // ---------- categories (PROTOCOL.md 4.24 lists the rules) ----------

    static final List<String> DYES = List.of("light_blue", "light_gray", "white", "orange", "magenta", "yellow", "lime",
        "pink", "gray", "cyan", "purple", "blue", "brown", "green", "red", "black");
    private static final List<String> DYED_KINDS = List.of("wool", "carpet", "terracotta", "concrete", "stained_glass",
        "shulker_box", "bed", "candle", "banner");
    private static final Set<String> REDSTONE = Set.of("redstone_wire", "redstone_torch", "redstone_block", "repeater",
        "comparator", "target", "lever", "tripwire_hook", "tripwire", "daylight_detector", "piston", "sticky_piston",
        "slime_block", "honey_block", "dispenser", "dropper", "crafter", "hopper", "observer", "note_block", "redstone_lamp",
        "tnt", "sculk_sensor", "calibrated_sculk_sensor", "trapped_chest", "lightning_rod");
    private static final Set<String> COLORED = Set.of("glass", "glass_pane", "tinted_glass", "terracotta", "shulker_box", "candle");
    private static final Set<String> FUNCTIONAL = Set.of("crafting_table", "furnace", "blast_furnace", "smoker",
        "cartography_table", "fletching_table", "smithing_table", "loom", "stonecutter", "grindstone", "enchanting_table",
        "brewing_stand", "beacon", "conduit", "chest", "ender_chest", "barrel", "bookshelf", "chiseled_bookshelf", "lectern",
        "decorated_pot", "scaffolding", "ladder", "bell", "jukebox", "respawn_anchor", "lodestone", "beehive", "composter",
        "end_rod", "end_portal_frame", "dragon_egg", "spawner", "trial_spawner", "vault", "suspicious_sand",
        "suspicious_gravel", "heavy_core", "torch", "soul_torch", "cake", "creaking_heart");
    private static final Set<String> NATURAL = Set.of("grass_block", "podzol", "mycelium", "dirt_path", "farmland", "mud",
        "muddy_mangrove_roots", "mangrove_roots", "clay", "gravel", "snow", "snow_block", "powder_snow", "calcite",
        "dripstone_block", "pointed_dripstone", "netherrack", "soul_sand", "soul_soil", "basalt", "magma_block", "glowstone",
        "shroomlight", "obsidian", "crying_obsidian", "bedrock", "end_stone", "chorus_plant", "chorus_flower", "cactus",
        "cactus_flower", "sugar_cane", "bamboo", "kelp", "dried_kelp_block", "seagrass", "sea_pickle", "sponge", "wet_sponge",
        "vine", "lily_pad", "cobweb", "short_grass", "tall_grass", "fern", "large_fern", "dead_bush", "bush", "firefly_bush",
        "short_dry_grass", "tall_dry_grass", "leaf_litter", "melon", "pumpkin", "carved_pumpkin", "jack_o_lantern", "hay_block",
        "turtle_egg", "sniffer_egg", "frogspawn", "bee_nest", "honeycomb_block", "spore_blossom", "hanging_roots", "glow_lichen",
        "big_dripleaf", "small_dripleaf", "azalea", "flowering_azalea", "cocoa", "sweet_berry_bush", "budding_amethyst",
        "amethyst_cluster", "bone_block", "nether_wart", "twisting_vines", "weeping_vines", "nether_sprouts",
        "brown_mushroom_block", "red_mushroom_block", "mushroom_stem", "moss_block", "moss_carpet", "pale_moss_block",
        "pale_moss_carpet", "pale_hanging_moss", "resin_clump", "sculk", "sculk_vein", "sculk_catalyst", "sculk_shrieker",
        "ochre_froglight", "verdant_froglight", "pearlescent_froglight", "ancient_debris", "dried_ghast", "sulfur_spike");
    private static final List<String> BUILDING_PARTS = List.of("brick", "stone", "planks", "_wood", "hyphae", "_stem",
        "_log", "copper", "quartz", "purpur", "prismarine", "deepslate", "tuff", "granite", "diorite", "andesite", "basalt",
        "_block", "bars", "chain", "grate", "tiles", "pillar", "packed_mud", "smooth_", "polished_", "chiseled_", "cut_",
        "sea_lantern", "bamboo_mosaic", "resin", "sandstone", "cobble", "cinnabar", "sulfur");

    /** Category id of a block from its tags and name (first matching rule wins; see the class comment). */
    public static String categorize(String id, Set<String> tags) {
        String p = path(id);
        // Redstone: rails, buttons, pressure plates and the redstone components (copper bulbs and rods too).
        if (has(tags, "rails", "buttons", "pressure_plates") || REDSTONE.contains(p) || p.endsWith("copper_bulb") || p.endsWith("_lightning_rod"))
            return "redstone";
        // Colored: the 16 dye colours of wool, carpets, terracotta, concrete, glass, shulker boxes, beds, candles,
        // banners (and their stairs/slabs), plus plain glass, terracotta, shulker boxes and candles.
        if (has(tags, "wool", "wool_carpets", "terracotta", "candles", "beds", "banners", "shulker_boxes") || COLORED.contains(p) || dyed(p)
            || p.endsWith("_bed"))
            return "colored";
        // Functional: workstations, storage, lights, signs, heads, pots, anvils, cauldrons, campfires...
        if (has(tags, "all_signs", "anvil", "cauldrons", "campfires", "flower_pots") || FUNCTIONAL.contains(p)
            || p.startsWith("infested_") || p.endsWith("_head") || p.endsWith("_skull") || p.endsWith("_shelf")
            || p.endsWith("_chest") || p.contains("golem_statue") || p.endsWith("_torch")
            || (p.endsWith("lantern") && !p.equals("jack_o_lantern") && !p.equals("sea_lantern")))
            return "functional";
        // Natural: soil, plants, leaves, saplings, flowers, crops, ores, raw ore blocks, coral, ice, nether/end ground.
        if (has(tags, "leaves", "saplings", "flowers", "small_flowers", "crops", "dirt", "sand", "ice", "nylium", "corals",
                "coral_blocks", "wall_corals", "wart_blocks", "cave_vines") || NATURAL.contains(p)
            || p.endsWith("_ore") || (p.startsWith("raw_") && p.endsWith("_block")) || p.endsWith("_bud")
            || p.endsWith("_mushroom") || p.endsWith("_fungus") || p.endsWith("_roots") || p.endsWith("_propagule")
            || (p.startsWith("dead_") && p.contains("coral")) || p.endsWith("_shrub"))
            return "natural";
        // Building: logs, planks, stairs, slabs, walls, fences, doors, trapdoors, stone and its families, metal blocks.
        if (has(tags, "logs", "planks", "stairs", "slabs", "walls", "fences", "fence_gates", "doors", "trapdoors",
                "base_stone_overworld", "base_stone_nether"))
            return "building";
        for (String part : BUILDING_PARTS) if (p.contains(part)) return "building";
        return "other";
    }

    // ---------- order inside a category ----------

    /** Vanilla's dye order in the creative tabs. */
    static final List<String> DYE_ORDER = List.of("white", "light_gray", "gray", "black", "brown", "red", "orange", "yellow",
        "lime", "green", "cyan", "light_blue", "blue", "purple", "magenta", "pink");
    private static final List<String> COLORED_KIND_ORDER = List.of("wool", "wool_stairs", "wool_slab", "carpet", "terracotta",
        "glazed_terracotta", "concrete", "concrete_stairs", "concrete_slab", "concrete_powder", "stained_glass",
        "stained_glass_pane", "shulker_box", "bed", "candle", "banner");
    /** Shape suffixes in display order (a family's full block first). Longest match wins ("_hanging_sign" over "_sign"). */
    private static final List<String> SHAPES = List.of("_stairs", "_slab", "_wall", "_fence", "_fence_gate", "_door",
        "_trapdoor", "_pressure_plate", "_button", "_sign", "_hanging_sign", "_shelf", "_bars", "_chain", "_grate",
        "_bulb", "_lantern", "_torch", "_chest", "_golem_statue", "_lightning_rod");
    /** Leading words that make a variant of the same family, with their order weight (copper: waxed > oxidized ...). */
    private static final Map<String, Integer> MODS = Map.ofEntries(Map.entry("stripped", 1), Map.entry("dead", 1),
        Map.entry("mossy", 2), Map.entry("cracked", 3), Map.entry("chiseled", 4), Map.entry("polished", 5),
        Map.entry("smooth", 6), Map.entry("cut", 7), Map.entry("infested", 9), Map.entry("exposed", 10),
        Map.entry("weathered", 20), Map.entry("oxidized", 30), Map.entry("waxed", 40));
    /** Trailing words naming a block form of a family, with their order (logs, wood, planks, plain, bricks...). */
    private static final Map<String, Integer> FORMS = Map.ofEntries(Map.entry("log", 0), Map.entry("stem", 0),
        Map.entry("wood", 1), Map.entry("hyphae", 1), Map.entry("planks", 2), Map.entry("block", 3), Map.entry("bricks", 4),
        Map.entry("brick", 4), Map.entry("tiles", 5), Map.entry("pillar", 6));

    /**
     * Sort key inside a category: families together (oak logs, planks, stairs...; all copper by weathering), colored
     * blocks by kind then vanilla's dye order, families alphabetically. E.g. "stone", "stone_stairs", "stone_bricks",
     * "mossy_stone_bricks" share the family "stone".
     */
    static String sortKey(String id, String category) {
        String p = path(id);
        if (category.equals("colored")) {
            int color = -1;
            String kind = p;
            for (int i = 0; i < DYE_ORDER.size(); i++) {
                String d = DYE_ORDER.get(i);
                if (p.startsWith(d + "_") && (color < 0 || d.length() > DYE_ORDER.get(color).length())) { color = i; kind = p.substring(d.length() + 1); }
            }
            if (kind.equals("glass") || kind.equals("glass_pane")) kind = "stained_" + kind;
            if (kind.equals("tinted_glass")) { kind = "stained_glass"; color = 99; }
            int k = COLORED_KIND_ORDER.indexOf(kind);
            return String.format("%02d %s %03d %s", k < 0 ? 50 : k, k < 0 ? kind : "", color + 1, p);
        }
        String shape = "";
        for (String sh : SHAPES) if ((p.endsWith(sh) || p.equals(sh.substring(1))) && sh.length() > shape.length()) shape = sh;
        int shapeRank = shape.isEmpty() ? 0 : SHAPES.indexOf(shape) + 1;
        String base = shape.isEmpty() ? p : p.equals(shape.substring(1)) ? "" : p.substring(0, p.length() - shape.length());
        List<String> words = new ArrayList<>(List.of(base.split("_")));
        words.removeIf(String::isEmpty);
        int mod = 0, form = 3;
        while (words.size() > 1 && MODS.containsKey(words.get(0))) mod += MODS.get(words.remove(0));
        if (words.size() > 2 && words.get(0).equals("deepslate") && words.get(words.size() - 1).equals("ore")) { words.remove(0); mod += 1; }
        if (words.size() > 1 && FORMS.containsKey(words.get(words.size() - 1))) form = FORMS.get(words.remove(words.size() - 1));
        String family = String.join("_", words);
        if (family.equals("bricks")) family = "brick";
        if (family.isEmpty() || MODS.containsKey(family)) { mod += MODS.getOrDefault(family, 0); family = shape.isEmpty() ? p : shape.substring(1); }
        return String.format("%s %d %03d %02d %s", family, form, mod, shapeRank, p);
    }

    private static boolean dyed(String p) {
        for (String d : DYES)
            if (p.startsWith(d + "_")) {
                String rest = p.substring(d.length() + 1);
                for (String k : DYED_KINDS) if (rest.contains(k)) return true;
                return false;
            }
        return false;
    }

    private static boolean has(Set<String> tags, String... any) {
        for (String t : any) if (tags.contains(t)) return true;
        return false;
    }

    /** "minecraft:oak_log" -> "oak_log". */
    static String path(String id) {
        int c = id.indexOf(':');
        return (c < 0 ? id : id.substring(c + 1)).toLowerCase(Locale.ROOT);
    }

    /** Counts per category, for the log line. */
    public Map<String, Integer> counts() {
        Map<String, Integer> m = new LinkedHashMap<>();
        for (String[] c : CATEGORIES) m.put(c[0], 0);
        for (Entry e : entries) m.merge(e.category(), 1, Integer::sum);
        return m;
    }

    /** Lookup by block id (tests, /tabletop blocks). */
    public Entry entry(String id) {
        for (Entry e : entries) if (e.id().equals(id)) return e;
        return null;
    }
}
