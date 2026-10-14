package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.*;

/** PROTOCOL.md 4.22: one tick of actions, collapsed and filtered per region, against tools/fixtures/actions.json. */
class ActionBatchTest {
    static final RegionSpec REGION = new RegionSpec("world", 100, 60, -40, 32, 64, 32);

    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    @Test void tickMatchesFixture() throws Exception {
        ActionBatch b = new ActionBatch();
        b.swing("world", 112.9, 62.98, -30.25, 7, false, true);
        b.dig("world", 105, 63, -36, 2, 7);
        b.swing("world", 112.9, 62.98, -30.25, 7, false, true);    // swings every tick while digging: one per tick
        b.dig("world", 105, 63, -36, 4, 7);                         // latest stage wins
        b.breakBlock("world", 106, 63, -36, "minecraft:stone", 7);
        b.place("world", 104, 64, -36, "minecraft:oak_planks", 7);
        b.hurt("world", 108.7, 64, -28, 104, true, false);
        b.hurt("world", 108.7, 64, -28, 104, false, false);         // a second hit the same tick keeps the crit
        b.swing("world", 109.0, 64, -29, 104, false, false);
        JsonObject m = b.message(REGION);
        assertEquals("actions", m.get("type").getAsString());
        JsonArray want = fixture("actions").getAsJsonArray("actions");
        JsonArray got = m.getAsJsonArray("actions");
        // Re-inserted repeats move to the end; compare as sets of entries plus the dig and hurt values.
        assertEquals(want.size(), got.size(), got.toString());
        for (var e : want) assertTrue(got.contains(e), "missing " + e + " in " + got);
    }

    @Test void outsideTheRegionIsNotSent() {
        ActionBatch b = new ActionBatch();
        b.breakBlock("world", 99, 63, -36, "minecraft:dirt", null);       // just west of the region
        b.dig("world", 100, 124, -36, 3, null);                            // above it
        b.swing("world", 90.5, 64, -30, 7, false, true);                   // a player 9.5 blocks out: still listed
        b.swing("world", 90.5, 64, -30, 55, false, false);                 // a mob that far out: not
        b.hurt("world_nether", 110, 64, -30, 8, false, false);
        assertEquals(1, b.forRegion(REGION).size());
        assertNull(new ActionBatch().message(REGION));
    }

    @Test void breakClearsDigAndCapHolds() {
        ActionBatch b = new ActionBatch();
        b.dig("world", 105, 63, -36, 8, 7);
        b.breakBlock("world", 105, 63, -36, "minecraft:stone", 7);
        JsonArray a = b.forRegion(REGION);
        assertEquals(1, a.size());
        assertEquals("break", a.get(0).getAsJsonObject().get("kind").getAsString());
        b.clear();
        for (int i = 0; i < ActionBatch.MAX + 10; i++) b.dig("world", 100 + i % 32, 61, -40 + i / 32, i % 10, null);
        assertEquals(ActionBatch.MAX, b.size());
        assertEquals(10, b.dropped());
        b.dig("world", 100, 61, -40, -1, null);                             // an update to a kept entry still fits
        assertEquals(ActionBatch.MAX, b.size());
    }
}
