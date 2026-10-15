package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.*;

/** block_entities (PROTOCOL.md 4.29): entries from plain values match the golden fixture; per-chunk deltas. */
class BlockEntityJsonTest {
    private static BlockEntityJson.Side side(String color, boolean glowing, String... lines) { return new BlockEntityJson.Side(List.of(lines), color, glowing); }

    /** The fixture's entities, built the way BlockEntityStreamer builds them from the world. */
    static List<JsonObject> fixtureEntities() {
        List<JsonObject> l = new ArrayList<>();
        l.add(BlockEntityJson.sign(105, 64, -35, side("black", false, "Welcome", "to the", "village", ""), side("black", false, "", "", "", "")));
        l.add(BlockEntityJson.sign(106, 64, -35, side("light_blue", true, "", "Shop", "", ""), side("red", false, "Closed", "", "", "")));
        l.add(BlockEntityJson.banner(107, 65, -35, List.of(new String[] { "minecraft:stripe_bottom", "red" }, new String[] { "minecraft:creeper", "black" })));
        l.add(BlockEntityJson.skull(108, 64, -35, "http://textures.minecraft.net/texture/0123456789abcdef", "Steve", "00000000-0000-4000-8000-000000000001"));
        l.add(BlockEntityJson.pot(109, 64, -35, List.of("minecraft:brick", "minecraft:arms_up_pottery_sherd", "minecraft:brick", "minecraft:skull_pottery_sherd")));
        l.add(BlockEntityJson.spawner("spawner", 110, 64, -35, "minecraft:zombie", new double[] { 0.6, 1.95 }, false));
        l.add(BlockEntityJson.spawner("trial_spawner", 111, 64, -35, "minecraft:breeze", new double[] { 0.6, 1.77 }, false));
        l.add(BlockEntityJson.beacon(112, 64, -35, 4));
        List<String> campfire = new ArrayList<>(); campfire.add("minecraft:beef"); campfire.add(null); campfire.add(null); campfire.add("minecraft:porkchop");
        l.add(BlockEntityJson.items("campfire", 113, 64, -35, campfire));
        List<String> shelf = new ArrayList<>(); shelf.add(null); shelf.add("minecraft:apple"); shelf.add(null);
        l.add(BlockEntityJson.items("shelf", 114, 64, -35, shelf));
        l.add(BlockEntityJson.vault(115, 64, -35, "minecraft:diamond"));
        l.add(BlockEntityJson.conduit(116, 64, -35, true));
        return l;
    }

    @Test void fixtureMatchesBuilders() throws Exception {
        JsonObject f = JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", "block_entities.json"))).getAsJsonObject();
        JsonObject m = BlockEntityJson.message(fixtureEntities(), List.<int[]>of(new int[] { 100, 64, -35 }));
        assertEquals(f.get("type").getAsString(), m.get("type").getAsString());
        assertEquals(f.getAsJsonArray("entities"), m.getAsJsonArray("entities"));
        assertEquals(f.getAsJsonArray("removed"), m.getAsJsonArray("removed"));
    }

    @Test void defaultsAreNotSent() {
        assertNull(BlockEntityJson.sign(0, 0, 0, side("black", false, "", "", "", ""), side("black", false, "", "", "", "")), "empty sign");
        assertNull(BlockEntityJson.banner(0, 0, 0, List.of()), "plain banner (base colour is in the block id)");
        assertNull(BlockEntityJson.pot(0, 0, 0, List.of("minecraft:brick", "minecraft:brick", "minecraft:brick", "minecraft:brick")), "plain pot");
        assertNull(BlockEntityJson.conduit(0, 0, 0, false), "inactive conduit");
        assertNull(BlockEntityJson.skull(0, 0, 0, null, null, null), "head without a profile");
        assertNull(BlockEntityJson.spawner("spawner", 0, 0, 0, null, null, false), "empty spawner");
        List<String> none = new ArrayList<>(); none.add(null); none.add(null);
        assertNull(BlockEntityJson.items("shelf", 0, 0, 0, none), "empty shelf");
        assertNotNull(BlockEntityJson.beacon(0, 0, 0, 0), "beacons always say their tier");
    }

    @Test void positionKeysRoundTrip() {
        for (int[] p : new int[][] { { 0, 0, 0 }, { -1, -64, -1 }, { 29_999_999, 319, -29_999_999 }, { -123456, 70, 987654 } }) {
            long k = BlockEntityJson.key(p[0], p[1], p[2]);
            assertArrayEquals(p, new int[] { BlockEntityJson.x(k), BlockEntityJson.y(k), BlockEntityJson.z(k) });
        }
    }

    @Test void chunkDeltasAddChangeAndRemove() {
        long a = BlockEntityJson.key(1, 64, 1), b = BlockEntityJson.key(2, 64, 1), out = BlockEntityJson.key(3, 300, 1);
        Map<Long, String> now = new HashMap<>(Map.of(a, "{\"a\":1}", b, "{\"b\":1}", out, "{\"o\":1}"));
        Map<Long, String> held = new HashMap<>();
        List<String> add = new ArrayList<>(); List<int[]> removed = new ArrayList<>();
        BlockEntityJson.diff(now, held, k -> BlockEntityJson.y(k) < 256, add, removed);
        assertEquals(2, add.size(), "both in-region entries are new; the one above the region is left out");
        assertEquals(0, removed.size());
        add.clear();
        BlockEntityJson.diff(now, held, k -> BlockEntityJson.y(k) < 256, add, removed);
        assertTrue(add.isEmpty() && removed.isEmpty(), "nothing changed: nothing sent");
        now.put(a, "{\"a\":2}");
        now.remove(b);
        BlockEntityJson.diff(now, held, k -> BlockEntityJson.y(k) < 256, add, removed);
        assertEquals(List.of("{\"a\":2}"), add, "a changed entry is resent whole");
        assertEquals(1, removed.size());
        assertArrayEquals(new int[] { 2, 64, 1 }, removed.get(0), "a gone entry is removed");
        assertEquals(1, held.size());
    }

    @Test void pruneForgetsWhatTheClientDropsWithTheRegion() {
        long in = BlockEntityJson.key(5, 64, 5), edge = BlockEntityJson.key(20, 64, 5);
        Map<Long, Map<Long, String>> held = new HashMap<>();
        held.put(BlockEntityJson.chunkKey(0, 0), new HashMap<>(Map.of(in, "x")));
        held.put(BlockEntityJson.chunkKey(1, 0), new HashMap<>(Map.of(edge, "y")));
        held.put(BlockEntityJson.chunkKey(9, 9), new HashMap<>(Map.of(BlockEntityJson.key(150, 64, 150), "z")));
        RegionSpec r = new RegionSpec("world", 0, 0, 0, 18, 128, 16);
        BlockEntityJson.prune(held, k -> r.containsWorld(BlockEntityJson.x(k), BlockEntityJson.y(k), BlockEntityJson.z(k)), ck -> (ck >> 32) <= 1 && (int) ck == 0);
        assertEquals(2, held.size(), "the far chunk is forgotten");
        assertTrue(held.get(BlockEntityJson.chunkKey(1, 0)).isEmpty(), "an entry outside the region in a kept chunk is forgotten");
        assertEquals(1, held.get(BlockEntityJson.chunkKey(0, 0)).size());
        JsonArray pos = BlockEntityJson.entry(1, 2, 3, "sign").getAsJsonArray("pos");
        assertEquals(3, pos.size());
    }
}
