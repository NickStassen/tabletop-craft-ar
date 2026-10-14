package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.*;

/** block_registry (PROTOCOL.md 4.24): filtering, categories, the message, and which states a block_edit may place. */
class BlockRegistryTest {
    private static BlockRegistry.Candidate c(String path, String props, String item, String home, String... tags) {
        String id = "minecraft:" + path;
        return new BlockRegistry.Candidate(id, id + props, item == null ? null : "minecraft:" + item, home == null ? null : "minecraft:" + home, Set.of(tags), true);
    }

    /** The same blocks as tools/fixtures/block_registry.json, in server registry order. */
    static List<BlockRegistry.Candidate> sample() {
        List<BlockRegistry.Candidate> l = new ArrayList<>();
        l.add(c("stone", "", "stone", "stone", "base_stone_overworld"));
        l.add(c("oak_log", "[axis=y]", "oak_log", "oak_log", "logs"));
        l.add(c("oak_stairs", "[facing=north,half=bottom,shape=straight,waterlogged=false]", "oak_stairs", "oak_stairs", "stairs"));
        l.add(c("diamond_block", "", "diamond_block", "diamond_block"));
        l.add(c("white_wool", "", "white_wool", "white_wool", "wool"));
        l.add(c("glass", "", "glass", "glass"));
        l.add(c("grass_block", "[snowy=false]", "grass_block", "grass_block"));
        l.add(c("poppy", "", "poppy", "poppy", "flowers", "small_flowers"));
        l.add(c("wheat", "[age=0]", "wheat_seeds", "wheat", "crops"));
        l.add(c("torch", "", "torch", "torch"));
        l.add(c("chest", "[facing=north,type=single,waterlogged=false]", "chest", "chest"));
        l.add(c("redstone_wire", "[east=none,north=none,power=0,south=none,west=none]", "redstone", "redstone_wire"));
        l.add(c("rail", "[shape=north_south,waterlogged=false]", "rail", "rail", "rails"));
        l.add(c("command_block", "[conditional=false,facing=north]", "command_block", "command_block"));   // edit.deny
        l.add(c("barrier", "[waterlogged=false]", "barrier", "barrier"));                                   // technical
        l.add(c("structure_void", "", "structure_void", "structure_void"));                                 // technical
        l.add(new BlockRegistry.Candidate("minecraft:future_block", "minecraft:future_block", "minecraft:future_block", "minecraft:future_block", Set.of(), false));
        l.add(c("wall_torch", "[facing=north]", "torch", "torch"));                                          // a variant of torch
        l.add(c("command_block_wall", "", "command_block", "command_block"));                               // variant of a denied block
        return l;
    }

    static BlockRegistry build() { return BlockRegistry.build(sample(), Set.of("minecraft:command_block")); }

    @Test void filtersTechnicalDeniedDisabledAndSortsFamilies() {
        BlockRegistry r = build();
        List<String> ids = r.entries().stream().map(BlockRegistry.Entry::id).toList();
        assertEquals(List.of("minecraft:diamond_block", "minecraft:oak_log", "minecraft:oak_stairs", "minecraft:stone",
            "minecraft:white_wool", "minecraft:glass", "minecraft:grass_block", "minecraft:poppy", "minecraft:wheat",
            "minecraft:chest", "minecraft:torch", "minecraft:rail", "minecraft:redstone_wire"), ids);
        assertEquals("minecraft:oak_log[axis=y]", r.entry("minecraft:oak_log").state());
        assertEquals("minecraft:wheat_seeds", r.entry("minecraft:wheat").item(), "item id when it differs from the block id");
        assertNull(r.entry("minecraft:stone").item());
        assertEquals(java.util.Map.of("minecraft:wall_torch", "minecraft:torch"), r.variants());
    }

    @Test void categoriesFollowTheCreativeTabs() {
        BlockRegistry r = build();
        assertEquals("building", r.entry("minecraft:stone").category());
        assertEquals("building", r.entry("minecraft:oak_stairs").category());
        assertEquals("building", r.entry("minecraft:diamond_block").category());
        assertEquals("colored", r.entry("minecraft:white_wool").category());
        assertEquals("colored", r.entry("minecraft:glass").category());
        assertEquals("natural", r.entry("minecraft:grass_block").category());
        assertEquals("natural", r.entry("minecraft:wheat").category());
        assertEquals("functional", r.entry("minecraft:torch").category());
        assertEquals("functional", r.entry("minecraft:chest").category());
        assertEquals("redstone", r.entry("minecraft:redstone_wire").category());
        assertEquals("redstone", r.entry("minecraft:rail").category());

        Set<String> none = Set.of();
        assertEquals("colored", BlockRegistry.categorize("minecraft:light_blue_concrete_powder", none));
        assertEquals("colored", BlockRegistry.categorize("minecraft:magenta_glazed_terracotta", none));
        assertEquals("colored", BlockRegistry.categorize("minecraft:black_wool_stairs", none));
        assertEquals("natural", BlockRegistry.categorize("minecraft:red_sand", Set.of("sand")));
        assertEquals("natural", BlockRegistry.categorize("minecraft:red_mushroom", none));
        assertEquals("natural", BlockRegistry.categorize("minecraft:deepslate_diamond_ore", none));
        assertEquals("natural", BlockRegistry.categorize("minecraft:raw_iron_block", none));
        assertEquals("natural", BlockRegistry.categorize("minecraft:jack_o_lantern", none));
        assertEquals("functional", BlockRegistry.categorize("minecraft:soul_lantern", none));
        assertEquals("functional", BlockRegistry.categorize("minecraft:oak_hanging_sign", Set.of("all_signs")));
        assertEquals("functional", BlockRegistry.categorize("minecraft:zombie_head", none));
        assertEquals("redstone", BlockRegistry.categorize("minecraft:trapped_chest", none));
        assertEquals("redstone", BlockRegistry.categorize("minecraft:oxidized_copper_bulb", none));
        assertEquals("redstone", BlockRegistry.categorize("minecraft:oak_button", Set.of("buttons")));
        assertEquals("building", BlockRegistry.categorize("minecraft:red_sandstone", none));
        assertEquals("building", BlockRegistry.categorize("minecraft:sea_lantern", none));
        assertEquals("building", BlockRegistry.categorize("minecraft:waxed_weathered_cut_copper", none));
        assertEquals("building", BlockRegistry.categorize("minecraft:crimson_stem", Set.of("logs")));
        assertEquals("other", BlockRegistry.categorize("minecraft:something_new", none));
    }

    /** Families stay together (logs, wood, planks, shapes; copper by weathering); colored blocks go by kind, then dye. */
    @Test void sortKeysGroupFamilies() {
        List<String> building = new ArrayList<>(List.of("oak_planks", "stripped_oak_log", "oak_stairs", "oak_log", "oak_wood",
            "oak_slab", "stone_bricks", "stone", "mossy_stone_bricks", "stone_brick_stairs", "stone_stairs", "bricks", "brick_stairs",
            "waxed_copper_block", "copper_block", "exposed_cut_copper", "cut_copper", "exposed_copper"));
        building.sort(java.util.Comparator.comparing(p -> BlockRegistry.sortKey("minecraft:" + p, "building")));
        assertEquals(List.of("bricks", "brick_stairs", "copper_block", "cut_copper", "exposed_copper", "exposed_cut_copper",
            "waxed_copper_block", "oak_log", "stripped_oak_log", "oak_wood", "oak_planks", "oak_stairs", "oak_slab",
            "stone", "stone_stairs", "stone_bricks", "stone_brick_stairs", "mossy_stone_bricks"), building);
        List<String> colored = new ArrayList<>(List.of("red_wool", "white_concrete", "glass", "white_wool", "light_gray_wool", "blue_stained_glass", "tinted_glass"));
        colored.sort(java.util.Comparator.comparing(p -> BlockRegistry.sortKey("minecraft:" + p, "colored")));
        assertEquals(List.of("white_wool", "light_gray_wool", "red_wool", "white_concrete", "glass", "blue_stained_glass", "tinted_glass"), colored);
        List<String> redstone = new ArrayList<>(List.of("waxed_exposed_lightning_rod", "lightning_rod", "exposed_lightning_rod"));
        redstone.sort(java.util.Comparator.comparing(p -> BlockRegistry.sortKey("minecraft:" + p, "redstone")));
        assertEquals(List.of("lightning_rod", "exposed_lightning_rod", "waxed_exposed_lightning_rod"), redstone);
    }

    /** A placed state must be a registry block (or a variant of one): everything else is bad_state. */
    @Test void placementAcceptsRegistryStatesAndRejectsOthers() {
        BlockRegistry r = build();
        assertNull(r.refusePlace("minecraft:stone"));
        assertNull(r.refusePlace("minecraft:oak_log[axis=x]"), "any state of a registry block");
        assertNull(r.refusePlace("minecraft:wall_torch[facing=east]"), "a variant (picked from a wall) is placeable");
        assertNull(r.refusePlace("minecraft:redstone_wire[east=side,north=none,power=0,south=none,west=side]"));
        assertNotNull(r.refusePlace("minecraft:fire[age=0,east=false,north=false,south=false,up=false,west=false]"), "no item form");
        assertNotNull(r.refusePlace("minecraft:water[level=0]"));
        assertNotNull(r.refusePlace("minecraft:command_block[conditional=false,facing=up]"), "edit.deny");
        assertNotNull(r.refusePlace("minecraft:command_block_wall"), "variant of a denied block");
        assertNotNull(r.refusePlace("minecraft:barrier"), "technical");
        assertNotNull(r.refusePlace("minecraft:future_block"), "disabled by feature flags");
        assertNotNull(r.refusePlace("minecraft:air"));
        assertTrue(r.refusePlace("minecraft:fire").contains("block_registry"));
        assertNotNull(BlockRegistry.empty().refusePlace("minecraft:stone"), "nothing is placeable before the registry is read");
    }

    @Test void messageMatchesTheGoldenFixture() throws Exception {
        JsonObject fixture = JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", "block_registry.json"))).getAsJsonObject();
        JsonObject m = build().message();
        fixture.remove("seq"); fixture.remove("t");
        assertEquals(fixture, m);
        assertEquals(13, m.get("count").getAsInt());
        var cats = m.getAsJsonArray("categories");
        assertEquals(BlockRegistry.CATEGORIES.size(), cats.size());
        assertEquals("other", cats.get(cats.size() - 1).getAsJsonObject().get("id").getAsString());
        assertNotSame(build().message(), build().message());
    }
}
