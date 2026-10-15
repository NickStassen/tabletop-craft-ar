package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.function.Predicate;

/**
 * [hands] What the headset's left trigger does to a block (PROTOCOL.md "Using blocks and items", cap interact_v1): the
 * curated Paper-API answer to vanilla's right click, on block-state strings so it is pure and unit-tested. The Quest
 * mirrors the empty-hand classification ({@link #kind}) in World/UseRules.cs to colour its outline; the server stays
 * authoritative. InteractService applies the {@link Effect} with Bukkit on the main thread.
 * <ul>
 *   <li><b>Empty hand</b> ({@link #emptyHand}): doors, trapdoors and fence gates open and close (iron ones only with
 *       {@code interact.iron-doors}); levers flip, buttons press and release after 20 or 30 ticks; note blocks tune,
 *       repeaters cycle their delay, comparators their mode, daylight detectors invert; bells ring; cakes lose a bite;
 *       a full composter empties; a jukebox ejects its disc; lit candles, candle cakes and campfires go out. Chests,
 *       barrels, shulker boxes and other containers, and signs, are {@link Kind#CONTAINER} / {@link Kind#SIGN}: a hook
 *       for the container view and the sign editor of a later version (refused for now).</li>
 *   <li><b>An item on a face</b> ({@link #useItem}): bone meal, flint and steel / fire charge (TNT, candles, campfires,
 *       fire), buckets (water, lava, powder snow, and the empty bucket that picks a source up), axes (strip, scrape,
 *       unwax), honeycomb (wax), hoes (till), shovels (path, douse), shears (carve a pumpkin, stop a vine growing), spawn
 *       eggs (a mob on the face, or a spawner's type) and the {@code tabletop:lightning} wand.</li>
 * </ul>
 */
public final class InteractRules {
    private InteractRules() {}

    /** The virtual tool that strikes lightning (item_registry "use"). */
    public static final String LIGHTNING = "tabletop:lightning";
    public static final String AIR = "minecraft:air";

    /** Server switches (config.yml interact). lightning: "real", "effect" (harmless) or "off". */
    public record Flags(boolean ironDoors, boolean fire, boolean tnt, boolean fluids, String lightning) {
        public static final Flags ALL = new Flags(true, true, true, true, "real");
        public boolean lightningOn() { return !"off".equals(lightning); }
    }

    /** What an empty hand does to a block, by family. NONE: nothing (the client places a block there instead). */
    public enum Kind { NONE, DOOR, TRAPDOOR, GATE, LEVER, BUTTON, NOTE_BLOCK, REPEATER, COMPARATOR, DAYLIGHT, BELL, CAKE, CANDLE_CAKE,
                       COMPOSTER, JUKEBOX, CANDLE, CAMPFIRE, CONTAINER, SIGN }

    /** Something InteractService does besides the block changes. */
    public enum Extra { NONE, NEIGHBOURS_BEHIND, RELEASE_LATER, REFRESH, NOTE, BELL, EJECT, DROP, PRIME_TNT, BONE_MEAL, SPAWN, SET_SPAWNER, LIGHTNING }

    /** A block change at the clicked block plus an offset (the clicked block itself is 0,0,0). */
    public record Change(int dx, int dy, int dz, String state) {}

    /**
     * What happens: a refusal ({@code denied}), or block changes, a sound key ({@code block.wooden_door.open}) with its
     * pitch, and one extra with its argument (a drop's item id, a spawn egg's entity type, a release delay in ticks)
     * at {@code at} (offset of the cell it happens in).
     */
    public record Effect(String denied, List<Change> changes, String sound, float pitch, Extra extra, String arg, int count, int[] at) {
        static Effect deny(String why) { return new Effect(why, List.of(), null, 1f, Extra.NONE, null, 0, null); }
        static Effect of(String sound, Change... c) { return new Effect(null, List.of(c), sound, 1f, Extra.NONE, null, 0, null); }
        Effect with(Extra e, String arg, int count, int[] at) { return new Effect(denied, changes, sound, pitch, e, arg, count, at); }
        Effect pitch(float p) { return new Effect(denied, changes, sound, p, extra, arg, count, at); }
        public boolean ok() { return denied == null; }
    }

    /** A cell next to the clicked one: its state and Paper's replaceable flag. */
    public record Cell(String state, boolean replaceable) {}

    /** The clicked block's surroundings (offsets from it), for items that act on the face cell or need air above. */
    public interface Around { Cell at(int dx, int dy, int dz); }

    // ---------- names ----------

    static String path(String state) {
        String id = BlockEdit.blockId(state);
        return id.startsWith("minecraft:") ? id.substring(10) : id;
    }

    static String itemPath(String item) {
        String id = item.toLowerCase(Locale.ROOT).trim();
        return id.startsWith("minecraft:") ? id.substring(10) : id;
    }

    static boolean isAir(String state) {
        String p = path(state);
        return p.equals("air") || p.equals("cave_air") || p.equals("void_air");
    }

    /** Containers (the container view's hook). */
    static final Set<String> CONTAINERS = Set.of("chest", "trapped_chest", "barrel", "hopper", "dispenser", "dropper", "furnace",
        "blast_furnace", "smoker", "brewing_stand", "crafter");

    static boolean container(String p) {
        return CONTAINERS.contains(p) || p.endsWith("shulker_box") || p.endsWith("copper_chest");
    }

    static boolean sign(String p) { return p.endsWith("_sign"); }

    /** The empty-hand family of a block (pure, shared with the Quest's UseRules). */
    public static Kind kind(String state, Flags f) {
        String p = path(state);
        if (p.endsWith("_door")) return p.equals("iron_door") && !f.ironDoors() ? Kind.NONE : Kind.DOOR;
        if (p.endsWith("_trapdoor")) return p.equals("iron_trapdoor") && !f.ironDoors() ? Kind.NONE : Kind.TRAPDOOR;
        if (p.endsWith("_fence_gate")) return Kind.GATE;
        if (p.equals("lever")) return Kind.LEVER;
        if (p.endsWith("_button")) return Kind.BUTTON;
        if (container(p)) return Kind.CONTAINER;
        if (sign(p)) return Kind.SIGN;
        switch (p) {
            case "note_block": return Kind.NOTE_BLOCK;
            case "repeater": return Kind.REPEATER;
            case "comparator": return Kind.COMPARATOR;
            case "daylight_detector": return Kind.DAYLIGHT;
            case "bell": return Kind.BELL;
            case "cake": return Kind.CAKE;
            case "composter": return "8".equals(EditRules.prop(state, "level")) ? Kind.COMPOSTER : Kind.NONE;
            case "jukebox": return "true".equals(EditRules.prop(state, "has_record")) ? Kind.JUKEBOX : Kind.NONE;
            case "campfire": case "soul_campfire": return "true".equals(EditRules.prop(state, "lit")) ? Kind.CAMPFIRE : Kind.NONE;
            default: break;
        }
        if (p.endsWith("candle_cake")) return Kind.CANDLE_CAKE;
        if (p.endsWith("candle")) return "true".equals(EditRules.prop(state, "lit")) ? Kind.CANDLE : Kind.NONE;
        return Kind.NONE;
    }

    /** Sound family of a door, trapdoor, fence gate or button: wooden, cherry_wood, bamboo_wood, nether_wood, copper, iron, stone. */
    static String soundFamily(String p) {
        if (p.startsWith("iron_")) return "iron";
        if (p.contains("copper")) return "copper";
        if (p.startsWith("cherry_")) return "cherry_wood";
        if (p.startsWith("bamboo_")) return "bamboo_wood";
        if (p.startsWith("crimson_") || p.startsWith("warped_")) return "nether_wood";
        if (p.equals("stone_button") || p.equals("polished_blackstone_button")) return "stone";
        return "wooden";
    }

    /** Ticks a pressed button stays down (vanilla: 20 for stone, 30 for wood). */
    public static int buttonTicks(String state) { return soundFamily(path(state)).equals("stone") ? 20 : 30; }

    // ---------- directions ----------

    static final List<String> HORIZONTAL = List.of("north", "south", "west", "east");

    static String opposite(String d) {
        return switch (d) {
            case "north" -> "south"; case "south" -> "north"; case "west" -> "east"; case "east" -> "west";
            case "up" -> "down"; case "down" -> "up"; default -> d;
        };
    }

    /**
     * The horizontal direction the headset looks along: from the ray direction (world axes) when sent, else the
     * opposite of a side face (a ray that hits the north face looks south). Null when neither says.
     */
    static String look(double[] dir, String face) {
        if (dir != null && dir.length == 3 && (Math.abs(dir[0]) > 1e-6 || Math.abs(dir[2]) > 1e-6))
            return Math.abs(dir[0]) >= Math.abs(dir[2]) ? (dir[0] > 0 ? "east" : "west") : (dir[2] > 0 ? "south" : "north");
        return face != null && HORIZONTAL.contains(face) ? opposite(face) : null;
    }

    static int[] offset(String face) {
        int[] s = EditRules.step(face);
        return s == null ? new int[] { 0, 1, 0 } : s;
    }

    // ---------- empty hand ----------

    /** The empty-hand interaction with the block {@code state}, clicked on {@code face} with the ray direction {@code dir}. */
    public static Effect emptyHand(String state, String face, double[] dir, Flags f) {
        String p = path(state), label = EditRules.label(state);
        Kind k = kind(state, f);
        switch (k) {
            case DOOR -> {
                boolean open = !"true".equals(EditRules.prop(state, "open"));
                String next = EditRules.withProp(state, "open", String.valueOf(open));
                boolean lower = !"upper".equals(EditRules.prop(state, "half"));
                Change other = new Change(0, lower ? 1 : -1, 0, EditRules.withProp(next, "half", lower ? "upper" : "lower"));
                return new Effect(null, List.of(new Change(0, 0, 0, next), other), "block." + soundFamily(p) + "_door." + (open ? "open" : "close"),
                    1f, Extra.NONE, null, 0, null);
            }
            case TRAPDOOR -> {
                boolean open = !"true".equals(EditRules.prop(state, "open"));
                return Effect.of("block." + soundFamily(p) + "_trapdoor." + (open ? "open" : "close"), new Change(0, 0, 0, EditRules.withProp(state, "open", String.valueOf(open))));
            }
            case GATE -> {
                boolean open = !"true".equals(EditRules.prop(state, "open"));
                String next = EditRules.withProp(state, "open", String.valueOf(open));
                // Vanilla FenceGateBlock: opening from the side it faces swings it away from you.
                String look = look(dir, face);
                if (open && look != null && opposite(look).equals(EditRules.prop(state, "facing"))) next = EditRules.withProp(next, "facing", look);
                String fam = soundFamily(p);
                return Effect.of("block." + (fam.equals("wooden") ? "" : fam + "_") + "fence_gate." + (open ? "open" : "close"), new Change(0, 0, 0, next));
            }
            case LEVER -> {
                boolean on = !"true".equals(EditRules.prop(state, "powered"));
                return Effect.of("block.lever.click", new Change(0, 0, 0, EditRules.withProp(state, "powered", String.valueOf(on))))
                    .pitch(on ? 0.6f : 0.5f).with(Extra.NEIGHBOURS_BEHIND, null, 0, behind(state));
            }
            case BUTTON -> {
                if ("true".equals(EditRules.prop(state, "powered"))) return new Effect(null, List.of(), null, 1f, Extra.NONE, null, 0, null);   // already down
                return Effect.of("block." + soundFamily(p) + "_button.click_on", new Change(0, 0, 0, EditRules.withProp(state, "powered", "true")))
                    .with(Extra.RELEASE_LATER, null, buttonTicks(state), behind(state));
            }
            case NOTE_BLOCK -> {
                int note = parse(EditRules.prop(state, "note"), 0);
                return Effect.of(null, new Change(0, 0, 0, EditRules.withProp(state, "note", String.valueOf((note + 1) % 25)))).with(Extra.NOTE, null, 0, null);
            }
            case REPEATER -> {
                int d = parse(EditRules.prop(state, "delay"), 1);
                return Effect.of(null, new Change(0, 0, 0, EditRules.withProp(state, "delay", String.valueOf(d % 4 + 1))));
            }
            case COMPARATOR -> {
                boolean subtract = !"subtract".equals(EditRules.prop(state, "mode"));
                return Effect.of("block.comparator.click", new Change(0, 0, 0, EditRules.withProp(state, "mode", subtract ? "subtract" : "compare")))
                    .pitch(subtract ? 0.55f : 0.5f).with(Extra.REFRESH, null, 0, null);
            }
            case DAYLIGHT -> {
                boolean inv = !"true".equals(EditRules.prop(state, "inverted"));
                return Effect.of(null, new Change(0, 0, 0, EditRules.withProp(state, "inverted", String.valueOf(inv))));
            }
            case BELL -> { return new Effect(null, List.of(), null, 1f, Extra.BELL, face, 0, null); }
            case CAKE -> {
                int bites = parse(EditRules.prop(state, "bites"), 0);
                return Effect.of("entity.generic.eat", new Change(0, 0, 0, bites >= 6 ? AIR : EditRules.withProp(state, "bites", String.valueOf(bites + 1))));
            }
            case CANDLE_CAKE -> {
                if ("true".equals(EditRules.prop(state, "lit"))) return Effect.of("block.candle.extinguish", new Change(0, 0, 0, EditRules.withProp(state, "lit", "false")));
                // Eating a candle cake drops its candle and leaves a cake with one bite taken.
                String candle = "minecraft:" + p.substring(0, p.length() - "_cake".length());
                return Effect.of("entity.generic.eat", new Change(0, 0, 0, "minecraft:cake[bites=1]")).with(Extra.DROP, candle, 1, new int[] { 0, 0, 0 });
            }
            case COMPOSTER -> {
                return Effect.of("block.composter.empty", new Change(0, 0, 0, EditRules.withProp(state, "level", "0")))
                    .with(Extra.DROP, "minecraft:bone_meal", 1, new int[] { 0, 1, 0 });
            }
            case JUKEBOX -> { return new Effect(null, List.of(), null, 1f, Extra.EJECT, null, 0, null); }
            case CANDLE -> { return Effect.of("block.candle.extinguish", new Change(0, 0, 0, EditRules.withProp(state, "lit", "false"))); }
            case CAMPFIRE -> { return Effect.of("block.fire.extinguish", new Change(0, 0, 0, EditRules.withProp(state, "lit", "false"))); }
            case CONTAINER -> { return Effect.deny(label + ": containers open in the container view, which isn't in this version (hold the left grip to place against it)"); }
            case SIGN -> { return Effect.deny(label + ": signs are edited in the sign editor, which isn't in this version"); }
            default -> {
                if ((p.equals("iron_door") || p.equals("iron_trapdoor")) && !f.ironDoors()) return Effect.deny(label + " only opens with redstone (interact.iron-doors)");
                return Effect.deny("nothing to use on " + label);
            }
        }
    }

    /**
     * The block a lever or button is attached to, as an offset: vanilla also updates that block's neighbours, so
     * redstone behind a wall sees the new power (face floor = below, ceiling = above, wall = behind its facing).
     */
    static int[] behind(String state) {
        String face = EditRules.prop(state, "face"), facing = EditRules.prop(state, "facing");
        if ("floor".equals(face)) return new int[] { 0, -1, 0 };
        if ("ceiling".equals(face)) return new int[] { 0, 1, 0 };
        int[] s = EditRules.step(facing == null ? "north" : opposite(facing));
        return s == null ? new int[] { 0, -1, 0 } : s;
    }

    static int parse(String v, int def) {
        try { return v == null ? def : Integer.parseInt(v); } catch (NumberFormatException e) { return def; }
    }

    // ---------- items ----------

    /** Weathering stages of copper blocks, oldest last. */
    static final List<String> STAGES = List.of("", "exposed_", "weathered_", "oxidized_");

    /** A copper block's parts: waxed, stage (0..3) and the base name ("copper_block", "cut_copper_stairs", "lightning_rod"). */
    record Copper(boolean waxed, int stage, String base) {
        String path() {
            String s = STAGES.get(stage);
            String b = base.equals("copper_block") && stage > 0 ? "copper" : base;
            return (waxed ? "waxed_" : "") + s + b;
        }
    }

    /** Parses a copper block's path; null for anything that doesn't weather. */
    static Copper copper(String p) {
        boolean waxed = p.startsWith("waxed_");
        String rest = waxed ? p.substring(6) : p;
        int stage = 0;
        for (int i = 3; i >= 1; i--) if (rest.startsWith(STAGES.get(i))) { stage = i; rest = rest.substring(STAGES.get(i).length()); break; }
        if (rest.equals("copper")) rest = stage > 0 || waxed ? "copper_block" : rest;
        if (!(rest.contains("copper") || rest.equals("lightning_rod")) || rest.endsWith("_ore") || rest.startsWith("raw_")) return null;
        return new Copper(waxed, stage, rest);
    }

    /** Replaces the block id of a state, keeping its properties. */
    static String rename(String state, String newPath) {
        int b = state.indexOf('[');
        return "minecraft:" + newPath + (b < 0 ? "" : state.substring(b));
    }

    static boolean strippable(String p) {
        return !p.startsWith("stripped_") && (p.endsWith("_log") || p.endsWith("_wood") || p.endsWith("_stem") || p.endsWith("_hyphae") || p.equals("bamboo_block"));
    }

    /** Growing plant heads whose growth shears stop (vanilla GrowingPlantHeadBlock: age to its maximum, 25). */
    static final Set<String> PLANT_HEADS = Set.of("kelp", "weeping_vines", "twisting_vines", "cave_vines");

    /** The entity type a spawn egg makes ("minecraft:pig_spawn_egg" -> "minecraft:pig"), or null. */
    public static String eggType(String item) {
        String p = itemPath(item);
        return p.endsWith("_spawn_egg") ? "minecraft:" + p.substring(0, p.length() - "_spawn_egg".length()) : null;
    }

    /**
     * An item used on {@code face} of the block {@code state}.
     *
     * @param around    the clicked block's surroundings (the face cell, the cell above)
     * @param exists    whether a block id exists on this server (stripped / waxed / scraped results)
     * @param ultraWarm water evaporates (the Nether)
     */
    public static Effect useItem(String state, String item, String face, double[] dir, Flags f, Around around, Predicate<String> exists, boolean ultraWarm) {
        String ip = itemPath(item), p = path(state), label = EditRules.label(state);
        int[] o = offset(face);
        boolean sideOrTop = !"down".equals(face);
        if (item.equals(LIGHTNING)) {
            if (!f.lightningOn()) return Effect.deny("lightning is off on this server (interact.lightning)");
            return new Effect(null, List.of(), null, 1f, Extra.LIGHTNING, f.lightning(), 0, o);
        }
        String egg = eggType(item);
        if (egg != null) {
            if (p.equals("spawner")) return new Effect(null, List.of(), "block.spawner.place", 1f, Extra.SET_SPAWNER, egg, 0, new int[] { 0, 0, 0 });
            return new Effect(null, List.of(), null, 1f, Extra.SPAWN, egg, 1, o);
        }
        switch (ip) {
            case "bone_meal": return new Effect(null, List.of(), "item.bone_meal.use", 1f, Extra.BONE_MEAL, face, 0, new int[] { 0, 0, 0 });
            case "flint_and_steel": case "fire_charge": return fire(state, ip, o, f, around);
            case "bucket": return emptyBucket(state, f);
            case "water_bucket": case "lava_bucket": case "powder_snow_bucket": return fullBucket(state, ip, o, f, around, ultraWarm);
            case "honeycomb": {
                Copper c = copper(p);
                if (c == null || c.waxed()) return Effect.deny("honeycomb does nothing to " + label);
                String next = new Copper(true, c.stage(), c.base()).path();
                if (!exists.test("minecraft:" + next)) return Effect.deny("honeycomb does nothing to " + label);
                return Effect.of("item.honeycomb.wax_on", new Change(0, 0, 0, rename(state, next)));
            }
            case "shears": {
                if (p.equals("pumpkin")) {
                    String facing = HORIZONTAL.contains(face) ? face : opposite(look(dir, face) == null ? "south" : look(dir, face));
                    return Effect.of("block.pumpkin.carve", new Change(0, 0, 0, "minecraft:carved_pumpkin[facing=" + facing + "]"))
                        .with(Extra.DROP, "minecraft:pumpkin_seeds", 4, o);
                }
                if (PLANT_HEADS.contains(p) && parse(EditRules.prop(state, "age"), 25) < 25)
                    return Effect.of("block.growing_plant.crop", new Change(0, 0, 0, EditRules.withProp(state, "age", "25")));
                return Effect.deny("shears do nothing to " + label + " (use them on a sheep, a pumpkin or a growing vine)");
            }
            default: break;
        }
        if (ip.endsWith("_axe")) {
            if (strippable(p) && exists.test("minecraft:stripped_" + p)) return Effect.of("item.axe.strip", new Change(0, 0, 0, rename(state, "stripped_" + p)));
            Copper c = copper(p);
            if (c != null && !c.waxed() && c.stage() > 0) {
                String next = new Copper(false, c.stage() - 1, c.base()).path();
                if (exists.test("minecraft:" + next)) return Effect.of("item.axe.scrape", new Change(0, 0, 0, rename(state, next)));
            }
            if (c != null && c.waxed()) {
                String next = new Copper(false, c.stage(), c.base()).path();
                if (exists.test("minecraft:" + next)) return Effect.of("item.axe.wax_off", new Change(0, 0, 0, rename(state, next)));
            }
            return Effect.deny("the axe does nothing to " + label);
        }
        if (ip.endsWith("_hoe")) {
            boolean airAbove = sideOrTop && isAir(around.at(0, 1, 0).state());
            switch (p) {
                case "grass_block", "dirt", "dirt_path" -> {
                    if (!airAbove) return Effect.deny("tilling needs air above the " + label);
                    return Effect.of("item.hoe.till", new Change(0, 0, 0, "minecraft:farmland[moisture=0]"));
                }
                case "coarse_dirt" -> {
                    if (!airAbove) return Effect.deny("tilling needs air above the " + label);
                    return Effect.of("item.hoe.till", new Change(0, 0, 0, "minecraft:dirt"));
                }
                case "rooted_dirt" -> {
                    return Effect.of("item.hoe.till", new Change(0, 0, 0, "minecraft:dirt")).with(Extra.DROP, "minecraft:hanging_roots", 1, o);
                }
                default -> { return Effect.deny("the hoe does nothing to " + label); }
            }
        }
        if (ip.endsWith("_shovel")) {
            if ((p.equals("campfire") || p.equals("soul_campfire")) && "true".equals(EditRules.prop(state, "lit")))
                return Effect.of("block.fire.extinguish", new Change(0, 0, 0, EditRules.withProp(state, "lit", "false")));
            if (Set.of("grass_block", "dirt", "podzol", "coarse_dirt", "mycelium", "rooted_dirt").contains(p)) {
                if (!(sideOrTop && isAir(around.at(0, 1, 0).state()))) return Effect.deny("a path needs air above the " + label);
                return Effect.of("item.shovel.flatten", new Change(0, 0, 0, "minecraft:dirt_path"));
            }
            return Effect.deny("the shovel does nothing to " + label);
        }
        if (ip.equals("name_tag") || ip.equals("lead")) return Effect.deny("use the " + ip.replace('_', ' ') + " on a mob");
        return Effect.deny("nothing happens when " + ip.replace('_', ' ') + " is used on " + label);
    }

    /** Flint and steel or a fire charge: prime TNT, light a candle, candle cake or campfire, else fire in the face cell. */
    static Effect fire(String state, String ip, int[] o, Flags f, Around around) {
        String p = path(state), label = EditRules.label(state);
        String use = ip.equals("fire_charge") ? "item.firecharge.use" : "item.flintandsteel.use";
        if (p.equals("tnt")) {
            if (!f.tnt()) return Effect.deny("TNT is off on this server (interact.tnt)");
            return Effect.of("entity.tnt.primed", new Change(0, 0, 0, AIR)).with(Extra.PRIME_TNT, null, 80, new int[] { 0, 0, 0 });
        }
        boolean lightable = p.endsWith("candle") || p.endsWith("candle_cake") || p.equals("campfire") || p.equals("soul_campfire");
        if (lightable && "false".equals(EditRules.prop(state, "lit"))) {
            if ("true".equals(EditRules.prop(state, "waterlogged"))) return Effect.deny(label + " is under water");
            return Effect.of(use, new Change(0, 0, 0, EditRules.withProp(state, "lit", "true")));
        }
        if (!f.fire()) return Effect.deny("fire is off on this server (interact.fire)");
        Cell c = around.at(o[0], o[1], o[2]);
        if (!isAir(c.state())) return Effect.deny("no room for fire: " + EditRules.label(c.state()) + " is in the way");
        String below = path(around.at(o[0], o[1] - 1, o[2]).state());
        String fireState = below.equals("soul_sand") || below.equals("soul_soil") ? "minecraft:soul_fire" : "minecraft:fire";
        return Effect.of(use, new Change(o[0], o[1], o[2], fireState));
    }

    /** The empty bucket picks up the source it hits: water, lava, powder snow, or a waterlogged block's water. */
    static Effect emptyBucket(String state, Flags f) {
        if (!f.fluids()) return Effect.deny("buckets are off on this server (interact.fluids)");
        String p = path(state);
        if ((p.equals("water") || p.equals("lava")) && "0".equals(EditRules.prop(state, "level")))
            return Effect.of(p.equals("lava") ? "item.bucket.fill_lava" : "item.bucket.fill", new Change(0, 0, 0, AIR));
        if (p.equals("powder_snow")) return Effect.of("item.bucket.fill_powder_snow", new Change(0, 0, 0, AIR));
        if ("true".equals(EditRules.prop(state, "waterlogged")))
            return Effect.of("item.bucket.fill", new Change(0, 0, 0, EditRules.withProp(state, "waterlogged", "false")));
        return Effect.deny("the bucket finds no source to pick up at " + EditRules.label(state));
    }

    /** A full bucket: water waterlogs the clicked block when it can, else it (lava, powder snow) goes into the face cell. */
    static Effect fullBucket(String state, String ip, int[] o, Flags f, Around around, boolean ultraWarm) {
        if (!f.fluids()) return Effect.deny("buckets are off on this server (interact.fluids)");
        boolean water = ip.equals("water_bucket");
        if (water && ultraWarm) return Effect.deny("water evaporates in this dimension");
        if (water && "false".equals(EditRules.prop(state, "waterlogged")))
            return Effect.of("item.bucket.empty", new Change(0, 0, 0, EditRules.withProp(state, "waterlogged", "true")));
        Cell c = around.at(o[0], o[1], o[2]);
        String cp = path(c.state());
        boolean free = isAir(c.state()) || c.replaceable() || cp.equals("water") || cp.equals("lava");
        if (!free) return Effect.deny("no room: " + EditRules.label(c.state()) + " is in the way");
        String placed = switch (ip) {
            case "water_bucket" -> "minecraft:water[level=0]";
            case "lava_bucket" -> "minecraft:lava[level=0]";
            default -> "minecraft:powder_snow";
        };
        if (BlockEdit.blockId(c.state()).equals(BlockEdit.blockId(placed)) && "0".equals(EditRules.prop(c.state(), "level")))
            return Effect.deny("there is a source there already");
        if (ip.equals("powder_snow_bucket") && (cp.equals("water") || cp.equals("lava"))) return Effect.deny("powder snow can't go into " + cp);
        String sound = switch (ip) { case "water_bucket" -> "item.bucket.empty"; case "lava_bucket" -> "item.bucket.empty_lava"; default -> "item.bucket.empty_powder_snow"; };
        return Effect.of(sound, new Change(o[0], o[1], o[2], placed));
    }

    // ---------- the item registry ----------

    /** One item the headset may use, and the switch that has to be on for it ("" = always). */
    public record Usable(String item, String family) {}

    /** Every usable item in menu order (PROTOCOL.md item_registry "use"): one per tool class, foods to breed, the wand. */
    public static final List<Usable> USABLE = List.of(
        new Usable("minecraft:flint_and_steel", "fire"), new Usable("minecraft:fire_charge", "fire"), new Usable("minecraft:bone_meal", ""),
        new Usable("minecraft:bucket", "fluids"), new Usable("minecraft:water_bucket", "fluids"), new Usable("minecraft:lava_bucket", "fluids"),
        new Usable("minecraft:powder_snow_bucket", "fluids"), new Usable("minecraft:iron_axe", ""), new Usable("minecraft:honeycomb", ""),
        new Usable("minecraft:iron_hoe", ""), new Usable("minecraft:iron_shovel", ""), new Usable("minecraft:shears", ""),
        new Usable("minecraft:name_tag", "entities"), new Usable("minecraft:lead", "entities"), new Usable("minecraft:wheat", "entities"),
        new Usable("minecraft:carrot", "entities"), new Usable("minecraft:wheat_seeds", "entities"), new Usable(LIGHTNING, "lightning"));

    /** The usable items whose family is switched on (fire also covers TNT: flint and steel lights it). */
    public static List<String> usable(Flags f, boolean entities) {
        List<String> out = new ArrayList<>();
        for (Usable u : USABLE) {
            boolean on = switch (u.family()) {
                case "fire" -> f.fire() || f.tnt();
                case "fluids" -> f.fluids();
                case "entities" -> entities;
                case "lightning" -> f.lightningOn();
                default -> true;
            };
            if (on) out.add(u.item());
        }
        return out;
    }
}
