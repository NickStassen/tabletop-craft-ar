package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import static dev.tabletopcraft.ar.InteractRules.*;
import static org.junit.jupiter.api.Assertions.*;

/** [hands] The curated right-click table (PROTOCOL.md "Using blocks and items") on state strings. */
class InteractRulesTest {
    static final Flags ALL = Flags.ALL, NOTHING = new Flags(false, false, false, false, "off");
    static final Set<String> BLOCKS = Set.of("minecraft:stripped_oak_log", "minecraft:stripped_crimson_hyphae", "minecraft:copper_block",
        "minecraft:exposed_copper", "minecraft:weathered_copper", "minecraft:oxidized_copper", "minecraft:waxed_copper_block",
        "minecraft:waxed_exposed_copper", "minecraft:waxed_oxidized_copper", "minecraft:cut_copper_stairs", "minecraft:exposed_cut_copper_stairs",
        "minecraft:weathered_cut_copper_stairs", "minecraft:lightning_rod", "minecraft:waxed_lightning_rod", "minecraft:stripped_bamboo_block");

    /** A world around the clicked block: listed offsets, air elsewhere. */
    static Around world(String clicked, Object... cells) {
        Map<String, Cell> m = new HashMap<>();
        m.put("0,0,0", new Cell(clicked, false));
        for (int i = 0; i < cells.length; i += 2) m.put((String) cells[i], new Cell((String) cells[i + 1], cells[i + 1].toString().contains("short_grass")));
        return (dx, dy, dz) -> m.getOrDefault(dx + "," + dy + "," + dz, new Cell(AIR, true));
    }

    static Effect item(String state, String item, String face, Around a) { return useItem(state, item, face, null, ALL, a, BLOCKS::contains, false); }
    static Effect item(String state, String item) { return item(state, item, "up", world(state)); }

    static String only(Effect e) {
        assertTrue(e.ok(), () -> "refused: " + e.denied());
        assertEquals(1, e.changes().size(), "one change");
        return e.changes().get(0).state();
    }

    @Test void emptyHandFamilies() {
        Object[][] table = {
            { "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]", Kind.DOOR },
            { "minecraft:iron_door[facing=north,half=lower,hinge=left,open=false,powered=false]", Kind.DOOR },
            { "minecraft:spruce_trapdoor[facing=north,half=bottom,open=false,powered=false,waterlogged=false]", Kind.TRAPDOOR },
            { "minecraft:oak_fence_gate[facing=north,in_wall=false,open=false,powered=false]", Kind.GATE },
            { "minecraft:lever[face=wall,facing=east,powered=false]", Kind.LEVER },
            { "minecraft:stone_button[face=floor,facing=north,powered=false]", Kind.BUTTON },
            { "minecraft:note_block[instrument=harp,note=0,powered=false]", Kind.NOTE_BLOCK },
            { "minecraft:repeater[delay=1,facing=north,locked=false,powered=false]", Kind.REPEATER },
            { "minecraft:comparator[facing=north,mode=compare,powered=false]", Kind.COMPARATOR },
            { "minecraft:daylight_detector[inverted=false,power=0]", Kind.DAYLIGHT },
            { "minecraft:bell[attachment=floor,facing=north,powered=false]", Kind.BELL },
            { "minecraft:cake[bites=0]", Kind.CAKE },
            { "minecraft:red_candle_cake[lit=false]", Kind.CANDLE_CAKE },
            { "minecraft:composter[level=8]", Kind.COMPOSTER },
            { "minecraft:composter[level=3]", Kind.NONE },
            { "minecraft:jukebox[has_record=true]", Kind.JUKEBOX },
            { "minecraft:jukebox[has_record=false]", Kind.NONE },
            { "minecraft:candle[candles=2,lit=true,waterlogged=false]", Kind.CANDLE },
            { "minecraft:blue_candle[candles=1,lit=false,waterlogged=false]", Kind.NONE },
            { "minecraft:soul_campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]", Kind.CAMPFIRE },
            { "minecraft:campfire[facing=north,lit=false,signal_fire=false,waterlogged=false]", Kind.NONE },
            { "minecraft:chest[facing=north,type=single,waterlogged=false]", Kind.CONTAINER },
            { "minecraft:barrel[facing=up,open=false]", Kind.CONTAINER },
            { "minecraft:red_shulker_box[facing=up]", Kind.CONTAINER },
            { "minecraft:oak_wall_sign[facing=north,waterlogged=false]", Kind.SIGN },
            { "minecraft:cherry_hanging_sign[attached=false,rotation=0,waterlogged=false]", Kind.SIGN },
            { "minecraft:stone", Kind.NONE },
            { "minecraft:crafting_table", Kind.NONE },
        };
        for (Object[] row : table) assertEquals(row[1], kind((String) row[0], ALL), (String) row[0]);
        assertEquals(Kind.NONE, kind("minecraft:iron_door[facing=north,half=lower,hinge=left,open=false,powered=false]", NOTHING));
        assertEquals(Kind.NONE, kind("minecraft:iron_trapdoor[facing=north,half=bottom,open=false,powered=false,waterlogged=false]", NOTHING));
        assertTrue(emptyHand("minecraft:iron_door[facing=north,half=lower,hinge=left,open=false,powered=false]", "north", null, NOTHING).denied().contains("interact.iron-doors"));
    }

    @Test void doorsOpenBothHalvesWithTheirOwnSound() {
        Effect e = emptyHand("minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]", "south", null, ALL);
        assertEquals(List.of(new Change(0, 0, 0, "minecraft:oak_door[facing=north,half=lower,hinge=left,open=true,powered=false]"),
                             new Change(0, 1, 0, "minecraft:oak_door[facing=north,half=upper,hinge=left,open=true,powered=false]")), e.changes());
        assertEquals("block.wooden_door.open", e.sound());
        Effect up = emptyHand("minecraft:oak_door[facing=north,half=upper,hinge=left,open=true,powered=false]", "south", null, ALL);
        assertEquals(new Change(0, -1, 0, "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]"), up.changes().get(1));
        assertEquals("block.wooden_door.close", up.sound());
        String[][] sounds = { { "cherry_door", "block.cherry_wood_door.open" }, { "crimson_door", "block.nether_wood_door.open" },
            { "bamboo_door", "block.bamboo_wood_door.open" }, { "waxed_exposed_copper_door", "block.copper_door.open" }, { "iron_door", "block.iron_door.open" } };
        for (String[] s : sounds)
            assertEquals(s[1], emptyHand("minecraft:" + s[0] + "[facing=north,half=lower,hinge=left,open=false,powered=false]", "up", null, ALL).sound(), s[0]);
        assertEquals("block.iron_trapdoor.open", emptyHand("minecraft:iron_trapdoor[facing=north,half=bottom,open=false,powered=false,waterlogged=false]", "up", null, ALL).sound());
        assertEquals("block.cherry_wood_fence_gate.close", emptyHand("minecraft:cherry_fence_gate[facing=north,in_wall=false,open=true,powered=false]", "up", null, ALL).sound());
        assertEquals("block.fence_gate.open", emptyHand("minecraft:oak_fence_gate[facing=north,in_wall=false,open=false,powered=false]", "up", null, ALL).sound());
    }

    @Test void gatesSwingAwayFromTheHeadset() {
        String gate = "minecraft:oak_fence_gate[facing=north,in_wall=false,open=false,powered=false]";
        // Looking south (from the north side) at a gate facing north: it turns to open southwards, away from us.
        assertEquals("minecraft:oak_fence_gate[facing=south,in_wall=false,open=true,powered=false]", only(emptyHand(gate, "north", new double[] { 0.1, -0.3, 0.9 }, ALL)));
        assertEquals("minecraft:oak_fence_gate[facing=north,in_wall=false,open=true,powered=false]", only(emptyHand(gate, "south", new double[] { 0, 0, -1 }, ALL)));
        // Without a direction, a side face says where we look from.
        assertEquals("minecraft:oak_fence_gate[facing=south,in_wall=false,open=true,powered=false]", only(emptyHand(gate, "north", null, ALL)));
    }

    @Test void leversAndButtonsUpdateTheBlockTheyHangOn() {
        Effect wall = emptyHand("minecraft:lever[face=wall,facing=east,powered=false]", "east", null, ALL);
        assertEquals("minecraft:lever[face=wall,facing=east,powered=true]", only(wall));
        assertEquals(Extra.NEIGHBOURS_BEHIND, wall.extra());
        assertArrayEquals(new int[] { -1, 0, 0 }, wall.at());   // a lever facing east hangs on the block west of it
        assertEquals(0.6f, wall.pitch());
        assertArrayEquals(new int[] { 0, -1, 0 }, emptyHand("minecraft:lever[face=floor,facing=north,powered=true]", "up", null, ALL).at());
        assertArrayEquals(new int[] { 0, 1, 0 }, emptyHand("minecraft:lever[face=ceiling,facing=north,powered=false]", "down", null, ALL).at());
        Effect stone = emptyHand("minecraft:stone_button[face=wall,facing=south,powered=false]", "south", null, ALL);
        assertEquals("minecraft:stone_button[face=wall,facing=south,powered=true]", only(stone));
        assertEquals(Extra.RELEASE_LATER, stone.extra());
        assertEquals(20, stone.count());
        assertArrayEquals(new int[] { 0, 0, -1 }, stone.at());
        assertEquals("block.stone_button.click_on", stone.sound());
        assertEquals(30, emptyHand("minecraft:oak_button[face=floor,facing=north,powered=false]", "up", null, ALL).count());
        assertEquals("block.nether_wood_button.click_on", emptyHand("minecraft:warped_button[face=floor,facing=north,powered=false]", "up", null, ALL).sound());
        Effect down = emptyHand("minecraft:oak_button[face=floor,facing=north,powered=true]", "up", null, ALL);
        assertTrue(down.ok() && down.changes().isEmpty(), "a pressed button stays pressed");
    }

    @Test void redstoneComponentsCycle() {
        assertEquals("minecraft:note_block[instrument=harp,note=0,powered=false]", only(emptyHand("minecraft:note_block[instrument=harp,note=24,powered=false]", "up", null, ALL)));
        assertEquals(Extra.NOTE, emptyHand("minecraft:note_block[instrument=harp,note=3,powered=false]", "up", null, ALL).extra());
        assertEquals("minecraft:repeater[delay=1,facing=north,locked=false,powered=false]", only(emptyHand("minecraft:repeater[delay=4,facing=north,locked=false,powered=false]", "up", null, ALL)));
        Effect c = emptyHand("minecraft:comparator[facing=north,mode=compare,powered=false]", "up", null, ALL);
        assertEquals("minecraft:comparator[facing=north,mode=subtract,powered=false]", only(c));
        assertEquals(Extra.REFRESH, c.extra());
        assertEquals("minecraft:daylight_detector[inverted=true,power=0]", only(emptyHand("minecraft:daylight_detector[inverted=false,power=0]", "up", null, ALL)));
    }

    @Test void cakesCandlesCompostersJukeboxes() {
        assertEquals("minecraft:cake[bites=3]", only(emptyHand("minecraft:cake[bites=2]", "up", null, ALL)));
        assertEquals(AIR, only(emptyHand("minecraft:cake[bites=6]", "up", null, ALL)));
        assertEquals("minecraft:red_candle_cake[lit=false]", only(emptyHand("minecraft:red_candle_cake[lit=true]", "up", null, ALL)));
        Effect eat = emptyHand("minecraft:red_candle_cake[lit=false]", "up", null, ALL);
        assertEquals("minecraft:cake[bites=1]", only(eat));
        assertEquals(Extra.DROP, eat.extra());
        assertEquals("minecraft:red_candle", eat.arg());
        assertEquals("minecraft:candle", emptyHand("minecraft:candle_cake[lit=false]", "up", null, ALL).arg());
        Effect comp = emptyHand("minecraft:composter[level=8]", "up", null, ALL);
        assertEquals("minecraft:composter[level=0]", only(comp));
        assertEquals("minecraft:bone_meal", comp.arg());
        assertArrayEquals(new int[] { 0, 1, 0 }, comp.at());
        assertEquals(Extra.EJECT, emptyHand("minecraft:jukebox[has_record=true]", "up", null, ALL).extra());
        assertEquals("minecraft:candle[candles=2,lit=false,waterlogged=false]", only(emptyHand("minecraft:candle[candles=2,lit=true,waterlogged=false]", "up", null, ALL)));
        assertEquals("minecraft:campfire[facing=north,lit=false,signal_fire=false,waterlogged=false]",
            only(emptyHand("minecraft:campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]", "up", null, ALL)));
        assertEquals(Extra.BELL, emptyHand("minecraft:bell[attachment=floor,facing=north,powered=false]", "east", null, ALL).extra());
    }

    @Test void containersAndSignsAreHooksAndPlainBlocksDoNothing() {
        assertTrue(emptyHand("minecraft:chest[facing=north,type=single,waterlogged=false]", "up", null, ALL).denied().contains("container view"));
        assertTrue(emptyHand("minecraft:oak_sign[rotation=0,waterlogged=false]", "up", null, ALL).denied().contains("sign editor"));
        assertEquals("nothing to use on stone", emptyHand("minecraft:stone", "up", null, ALL).denied());
        assertEquals("nothing to use on composter", emptyHand("minecraft:composter[level=4]", "up", null, ALL).denied());
    }

    @Test void axesStripScrapeAndUnwax() {
        assertEquals("minecraft:stripped_oak_log[axis=x]", only(item("minecraft:oak_log[axis=x]", "minecraft:iron_axe")));
        assertEquals("minecraft:stripped_crimson_hyphae[axis=y]", only(item("minecraft:crimson_hyphae[axis=y]", "minecraft:netherite_axe")));
        assertEquals("minecraft:stripped_bamboo_block[axis=z]", only(item("minecraft:bamboo_block[axis=z]", "minecraft:wooden_axe")));
        assertFalse(item("minecraft:stripped_oak_log[axis=x]", "minecraft:iron_axe").ok());
        assertEquals("minecraft:exposed_cut_copper_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]",
            only(item("minecraft:weathered_cut_copper_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]", "minecraft:iron_axe")));
        assertEquals("minecraft:copper_block", only(item("minecraft:exposed_copper", "minecraft:iron_axe")));
        assertEquals("item.axe.scrape", item("minecraft:exposed_copper", "minecraft:iron_axe").sound());
        Effect unwax = item("minecraft:waxed_oxidized_copper", "minecraft:iron_axe");
        assertEquals("minecraft:oxidized_copper", only(unwax));
        assertEquals("item.axe.wax_off", unwax.sound());
        assertEquals("minecraft:copper_block", only(item("minecraft:waxed_copper_block", "minecraft:iron_axe")));
        assertEquals("the axe does nothing to stone", item("minecraft:stone", "minecraft:iron_axe").denied());
        assertFalse(item("minecraft:copper_block", "minecraft:iron_axe").ok(), "fresh unwaxed copper has nothing to scrape");
    }

    @Test void honeycombWaxesCopper() {
        assertEquals("minecraft:waxed_exposed_copper", only(item("minecraft:exposed_copper", "minecraft:honeycomb")));
        assertEquals("minecraft:waxed_copper_block", only(item("minecraft:copper_block", "minecraft:honeycomb")));
        assertEquals("minecraft:waxed_lightning_rod[facing=up,powered=false,waterlogged=false]",
            only(item("minecraft:lightning_rod[facing=up,powered=false,waterlogged=false]", "minecraft:honeycomb")));
        assertFalse(item("minecraft:waxed_copper_block", "minecraft:honeycomb").ok());
        assertFalse(item("minecraft:copper_ore", "minecraft:honeycomb").ok());
        assertFalse(item("minecraft:raw_copper_block", "minecraft:honeycomb").ok());
    }

    @Test void hoesAndShovelsNeedAirAbove() {
        assertEquals("minecraft:farmland[moisture=0]", only(item("minecraft:grass_block[snowy=false]", "minecraft:iron_hoe")));
        assertEquals("minecraft:farmland[moisture=0]", only(item("minecraft:dirt_path", "minecraft:diamond_hoe")));
        assertEquals("item.hoe.till", item("minecraft:dirt", "minecraft:iron_hoe").sound());
        assertFalse(item("minecraft:dirt", "minecraft:iron_hoe", "up", world("minecraft:dirt", "0,1,0", "minecraft:stone")).ok());
        assertFalse(item("minecraft:dirt", "minecraft:iron_hoe", "down", world("minecraft:dirt")).ok(), "not from below");
        assertEquals("minecraft:dirt", only(item("minecraft:coarse_dirt", "minecraft:iron_hoe")));
        Effect rooted = item("minecraft:rooted_dirt", "minecraft:iron_hoe", "east", world("minecraft:rooted_dirt", "0,1,0", "minecraft:stone"));
        assertEquals("minecraft:dirt", only(rooted));
        assertEquals("minecraft:hanging_roots", rooted.arg());
        assertArrayEquals(new int[] { 1, 0, 0 }, rooted.at());
        assertEquals("minecraft:dirt_path", only(item("minecraft:podzol[snowy=false]", "minecraft:iron_shovel")));
        assertFalse(item("minecraft:grass_block[snowy=false]", "minecraft:iron_shovel", "up", world("minecraft:grass_block[snowy=false]", "0,1,0", "minecraft:oak_log[axis=y]")).ok());
        assertEquals("minecraft:campfire[facing=north,lit=false,signal_fire=false,waterlogged=false]",
            only(item("minecraft:campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]", "minecraft:iron_shovel")));
    }

    @Test void shearsCarveAndTrim() {
        Effect side = item("minecraft:pumpkin", "minecraft:shears", "east", world("minecraft:pumpkin"));
        assertEquals("minecraft:carved_pumpkin[facing=east]", only(side));
        assertEquals("minecraft:pumpkin_seeds", side.arg());
        assertEquals(4, side.count());
        Effect top = useItem("minecraft:pumpkin", "minecraft:shears", "up", new double[] { 0, -1, -0.2 }, ALL, world("minecraft:pumpkin"), BLOCKS::contains, false);
        assertEquals("minecraft:carved_pumpkin[facing=south]", only(top), "from above it faces the headset");
        assertEquals("minecraft:kelp[age=25]", only(item("minecraft:kelp[age=3]", "minecraft:shears")));
        assertFalse(item("minecraft:kelp[age=25]", "minecraft:shears").ok());
        assertFalse(item("minecraft:vine[east=false,north=true,south=false,up=false,west=false]", "minecraft:shears").ok());
    }

    @Test void fireTntCandlesAndCampfires() {
        Effect tnt = item("minecraft:tnt[unstable=false]", "minecraft:flint_and_steel");
        assertEquals(AIR, only(tnt));
        assertEquals(Extra.PRIME_TNT, tnt.extra());
        assertEquals(80, tnt.count());
        assertTrue(useItem("minecraft:tnt[unstable=false]", "minecraft:flint_and_steel", "up", null, new Flags(true, true, false, true, "real"),
            world("minecraft:tnt[unstable=false]"), BLOCKS::contains, false).denied().contains("interact.tnt"));
        assertEquals("minecraft:candle[candles=1,lit=true,waterlogged=false]", only(item("minecraft:candle[candles=1,lit=false,waterlogged=false]", "minecraft:fire_charge")));
        assertFalse(item("minecraft:candle[candles=1,lit=false,waterlogged=true]", "minecraft:flint_and_steel").ok(), "not under water");
        assertEquals("minecraft:campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]",
            only(item("minecraft:campfire[facing=north,lit=false,signal_fire=false,waterlogged=false]", "minecraft:flint_and_steel")));
        Effect fire = item("minecraft:netherrack", "minecraft:flint_and_steel");
        assertEquals(new Change(0, 1, 0, "minecraft:fire"), fire.changes().get(0));
        assertEquals("item.flintandsteel.use", fire.sound());
        assertEquals(new Change(0, 1, 0, "minecraft:soul_fire"), item("minecraft:soul_soil", "minecraft:flint_and_steel").changes().get(0));
        assertEquals(new Change(-1, 0, 0, "minecraft:fire"), item("minecraft:oak_planks", "minecraft:flint_and_steel", "west", world("minecraft:oak_planks")).changes().get(0));
        assertFalse(item("minecraft:stone", "minecraft:flint_and_steel", "up", world("minecraft:stone", "0,1,0", "minecraft:stone")).ok());
        assertTrue(useItem("minecraft:stone", "minecraft:flint_and_steel", "up", null, NOTHING, world("minecraft:stone"), BLOCKS::contains, false).denied().contains("interact.fire"));
    }

    @Test void bucketsPickUpAndPour() {
        assertEquals(AIR, only(item("minecraft:water[level=0]", "minecraft:bucket")));
        assertEquals("item.bucket.fill_lava", item("minecraft:lava[level=0]", "minecraft:bucket").sound());
        assertFalse(item("minecraft:water[level=3]", "minecraft:bucket").ok(), "flowing water is no source");
        assertEquals(AIR, only(item("minecraft:powder_snow", "minecraft:bucket")));
        assertEquals("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]",
            only(item("minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=true]", "minecraft:bucket")));
        assertFalse(item("minecraft:stone", "minecraft:bucket").ok());
        assertEquals("minecraft:oak_slab[type=bottom,waterlogged=true]", only(item("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:water_bucket")));
        assertEquals(new Change(0, 1, 0, "minecraft:water[level=0]"), item("minecraft:stone", "minecraft:water_bucket").changes().get(0));
        assertEquals(new Change(0, 1, 0, "minecraft:lava[level=0]"), item("minecraft:oak_slab[type=bottom,waterlogged=false]", "minecraft:lava_bucket").changes().get(0));
        assertEquals(new Change(0, 0, 1, "minecraft:powder_snow"), item("minecraft:stone", "minecraft:powder_snow_bucket", "south", world("minecraft:stone")).changes().get(0));
        assertEquals(new Change(0, 1, 0, "minecraft:water[level=0]"),
            item("minecraft:dirt", "minecraft:water_bucket", "up", world("minecraft:dirt", "0,1,0", "minecraft:short_grass")).changes().get(0), "grass is washed away");
        assertFalse(item("minecraft:stone", "minecraft:water_bucket", "up", world("minecraft:stone", "0,1,0", "minecraft:stone")).ok());
        assertFalse(item("minecraft:stone", "minecraft:water_bucket", "up", world("minecraft:stone", "0,1,0", "minecraft:water[level=0]")).ok(), "a source there already");
        assertTrue(useItem("minecraft:stone", "minecraft:water_bucket", "up", null, ALL, world("minecraft:stone"), BLOCKS::contains, true).denied().contains("evaporates"));
        assertTrue(useItem("minecraft:stone", "minecraft:bucket", "up", null, NOTHING, world("minecraft:stone"), BLOCKS::contains, false).denied().contains("interact.fluids"));
    }

    @Test void eggsBoneMealLightningAndMobItems() {
        Effect egg = item("minecraft:stone", "minecraft:pig_spawn_egg");
        assertEquals(Extra.SPAWN, egg.extra());
        assertEquals("minecraft:pig", egg.arg());
        assertArrayEquals(new int[] { 0, 1, 0 }, egg.at());
        assertEquals(Extra.SET_SPAWNER, item("minecraft:spawner", "minecraft:zombie_spawn_egg").extra());
        assertEquals("minecraft:mooshroom", eggType("minecraft:mooshroom_spawn_egg"));
        assertNull(eggType("minecraft:egg"));
        assertEquals(Extra.BONE_MEAL, item("minecraft:oak_sapling[stage=0]", "minecraft:bone_meal").extra());
        Effect bolt = item("minecraft:stone", LIGHTNING, "north", world("minecraft:stone"));
        assertEquals(Extra.LIGHTNING, bolt.extra());
        assertEquals("real", bolt.arg());
        assertArrayEquals(new int[] { 0, 0, -1 }, bolt.at());
        assertEquals("effect", useItem("minecraft:stone", LIGHTNING, "up", null, new Flags(true, true, true, true, "effect"), world("minecraft:stone"), BLOCKS::contains, false).arg());
        assertFalse(useItem("minecraft:stone", LIGHTNING, "up", null, NOTHING, world("minecraft:stone"), BLOCKS::contains, false).ok());
        assertEquals("use the name tag on a mob", item("minecraft:stone", "minecraft:name_tag").denied());
        assertFalse(item("minecraft:grass_block[snowy=false]", "minecraft:wheat").ok());
    }

    @Test void usableItemsFollowTheSwitches() {
        List<String> all = usable(ALL, true);
        assertEquals(USABLE.size(), all.size());
        assertTrue(all.contains(LIGHTNING) && all.contains("minecraft:bucket") && all.contains("minecraft:name_tag"));
        List<String> none = usable(NOTHING, false);
        assertFalse(none.contains("minecraft:flint_and_steel") || none.contains("minecraft:bucket") || none.contains("minecraft:lead") || none.contains(LIGHTNING));
        assertTrue(none.contains("minecraft:bone_meal") && none.contains("minecraft:iron_axe") && none.contains("minecraft:shears"));
        assertTrue(usable(new Flags(true, false, true, false, "off"), false).contains("minecraft:flint_and_steel"), "TNT alone still needs flint and steel");
    }
}
