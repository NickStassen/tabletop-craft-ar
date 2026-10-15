package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.*;

/** [operator] The container view's rules (ContainerRules) and the bookmark store (Bookmarks). */
class ContainerRulesTest {
    @Test void whichBlocksOpenAndSort() {
        for (String id : new String[] { "minecraft:chest", "minecraft:trapped_chest", "minecraft:barrel", "minecraft:red_shulker_box", "minecraft:shulker_box",
                                        "minecraft:hopper", "minecraft:dispenser", "minecraft:dropper", "minecraft:furnace", "minecraft:smoker",
                                        "minecraft:blast_furnace", "minecraft:brewing_stand", "minecraft:crafter", "minecraft:copper_chest" })
            assertTrue(ContainerRules.container(id), id);
        assertFalse(ContainerRules.container("minecraft:ender_chest"));
        assertFalse(ContainerRules.container("minecraft:stone"));
        assertTrue(ContainerRules.sortable("minecraft:chest[facing=north,type=left,waterlogged=false]"));
        assertTrue(ContainerRules.sortable("minecraft:lime_shulker_box"));
        assertFalse(ContainerRules.sortable("minecraft:furnace"));
        assertFalse(ContainerRules.sortable("minecraft:brewing_stand"));
        assertFalse(ContainerRules.sortable("minecraft:crafter"));
    }

    @Test void titlesAreVanillas() {
        assertEquals("Large Chest", ContainerRules.title("minecraft:chest", true));
        assertEquals("Chest", ContainerRules.title("minecraft:chest", false));
        assertEquals("Chest", ContainerRules.title("minecraft:trapped_chest", false));
        assertEquals("Shulker Box", ContainerRules.title("minecraft:blue_shulker_box", false));
        assertEquals("Item Hopper", ContainerRules.title("minecraft:hopper", false));
        assertEquals("Blast Furnace", ContainerRules.title("minecraft:blast_furnace", false));
        assertEquals("Brewing Stand", ContainerRules.title("minecraft:brewing_stand", false));
    }

    @Test void slotRules() {
        assertNull(ContainerRules.placeProblem("minecraft:chest", 3, "minecraft:diamond", false));
        assertNotNull(ContainerRules.placeProblem("minecraft:shulker_box", 0, "minecraft:red_shulker_box", false));
        assertNull(ContainerRules.placeProblem("minecraft:chest", 0, "minecraft:red_shulker_box", false));
        assertNull(ContainerRules.placeProblem("minecraft:furnace", 0, "minecraft:raw_iron", false));
        assertEquals("raw iron isn't fuel", ContainerRules.placeProblem("minecraft:furnace", 1, "minecraft:raw_iron", false));
        assertNull(ContainerRules.placeProblem("minecraft:smoker", 1, "minecraft:coal", true));
        assertTrue(ContainerRules.placeProblem("minecraft:blast_furnace", 2, "minecraft:iron_ingot", false).contains("output"));
        assertNull(ContainerRules.placeProblem("minecraft:brewing_stand", 0, "minecraft:potion", false));
        assertNotNull(ContainerRules.placeProblem("minecraft:brewing_stand", 1, "minecraft:stone", false));
        assertNull(ContainerRules.placeProblem("minecraft:brewing_stand", 3, "minecraft:nether_wart", false));
        assertNull(ContainerRules.placeProblem("minecraft:brewing_stand", 4, "minecraft:blaze_powder", false));
        assertNotNull(ContainerRules.placeProblem("minecraft:brewing_stand", 4, "minecraft:coal", true));
    }

    static ContainerRules.Stack<String> st(String item, String meta, int count, int max) { return new ContainerRules.Stack<>(item, meta, count, max, item + meta); }

    @Test void sortMergesAndOrders() {
        List<ContainerRules.Stack<String>> in = new ArrayList<>();
        in.add(st("minecraft:torch", "", 40, 64));
        in.add(null);
        in.add(st("minecraft:diamond", "", 3, 64));
        in.add(st("minecraft:torch", "", 40, 64));
        in.add(st("minecraft:ender_pearl", "", 10, 16));
        in.add(st("minecraft:ender_pearl", "", 10, 16));
        in.add(st("minecraft:diamond_sword", "{damage:5}", 1, 1));
        in.add(st("minecraft:diamond_sword", "", 1, 1));
        in.add(st("minecraft:torch", "{name}", 2, 64));
        var out = ContainerRules.sort(in);
        List<String> got = new ArrayList<>();
        for (var s : out) got.add(s.item().substring(10) + s.meta() + "x" + s.count());
        assertEquals(List.of("diamondx3", "diamond_swordx1", "diamond_sword{damage:5}x1", "ender_pearlx16", "ender_pearlx4", "torchx64", "torchx16", "torch{name}x2"), got);
        assertEquals("minecraft:diamond_sword{damage:5}", out.get(2).handle(), "the stack's own handle (its components) is kept");
        assertTrue(ContainerRules.sort(List.of()).isEmpty());
    }

    @Test void bookmarksAreUniqueSortedAndCapped() {
        Bookmarks b = new Bookmarks(3);
        assertNull(b.add(new OperatorMessages.Mark("village", "world", 1, 64, 2, "A")));
        assertNull(b.add(new OperatorMessages.Mark("Base", "world", 5, 70, 5, "A")));
        assertNull(b.add(new OperatorMessages.Mark("Village", "world_nether", 9, 40, 9, "B")));   // same name: moved
        assertEquals(2, b.size());
        assertEquals("world_nether", b.find("VILLAGE").world());
        assertEquals(List.of("Base", "Village"), b.list().stream().map(OperatorMessages.Mark::name).toList());
        assertNull(b.add(new OperatorMessages.Mark("Cave", "world", 0, -30, 0, null)));
        assertNotNull(b.add(new OperatorMessages.Mark("Too many", "world", 0, 0, 0, null)));
        assertNull(b.add(new OperatorMessages.Mark("cave", "world", 1, 1, 1, null)), "replacing never hits the cap");
        assertTrue(b.remove("BASE"));
        assertFalse(b.remove("Base"));
        List<Map<String, Object>> maps = b.toMaps();
        Bookmarks c = new Bookmarks(10);
        List<Object> raw = new ArrayList<>(maps);
        raw.add(Map.of("name", "bad"));
        raw.add("junk");
        assertEquals(2, c.load(raw));
        assertEquals(b.list(), c.list());
    }
}
