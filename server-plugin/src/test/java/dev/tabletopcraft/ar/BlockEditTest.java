package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/** block_edit parsing, error replies and the per-session rate limit (PROTOCOL.md 4.17). */
class BlockEditTest {
    private static JsonObject j(String s) { return JsonParser.parseString(s).getAsJsonObject(); }

    @Test void parsesPlaceAndBreak() {
        BlockEdit p = BlockEdit.parse(j("{\"op\":\"place\",\"pos\":[1,-64,-3],\"state\":\"minecraft:oak_log[axis=x]\",\"face\":\"east\"}"));
        assertEquals(new BlockEdit(BlockEdit.Op.PLACE, 1, -64, -3, "minecraft:oak_log[axis=x]", "east"), p);
        BlockEdit b = BlockEdit.parse(j("{\"op\":\"break\",\"pos\":[1,2,3],\"state\":\"ignored\"}"));
        assertEquals(BlockEdit.Op.BREAK, b.op());
        assertNull(b.state());
        assertNull(b.face());
        assertEquals(2.0, BlockEdit.parse(j("{\"op\":\"break\",\"pos\":[1.0,2.0,3.0]}")).y(), 0);
    }

    @Test void rejectsMalformed() {
        for (String bad : new String[] {
            "{\"pos\":[1,2,3]}",                                         // no op
            "{\"op\":\"explode\",\"pos\":[1,2,3]}",                      // unknown op
            "{\"op\":\"break\"}",                                        // no pos
            "{\"op\":\"break\",\"pos\":[1,2]}",                          // short pos
            "{\"op\":\"break\",\"pos\":[1,2.5,3]}",                      // not a block coordinate
            "{\"op\":\"break\",\"pos\":[1,\"2\",3]}",                    // not a number
            "{\"op\":\"break\",\"pos\":[1,2,3e9]}",                      // outside the world
            "{\"op\":\"place\",\"pos\":[1,2,3]}",                        // place without state
            "{\"op\":\"place\",\"pos\":[1,2,3],\"state\":\" \"}",        // blank state
            "{\"op\":\"place\",\"pos\":[1,2,3],\"state\":\"minecraft:stone\",\"face\":\"sideways\"}",
        }) assertThrows(IllegalArgumentException.class, () -> BlockEdit.parse(j(bad)), bad);
        String longState = "minecraft:stone[" + "a".repeat(600) + "]";
        assertThrows(IllegalArgumentException.class, () -> BlockEdit.parse(j("{\"op\":\"place\",\"pos\":[0,0,0],\"state\":\"" + longState + "\"}")));
    }

    @Test void blockIdForDenyList() {
        assertEquals("minecraft:command_block", BlockEdit.blockId("minecraft:command_block[conditional=false,facing=up]"));
        assertEquals("minecraft:stone", BlockEdit.blockId("stone"));
        assertEquals("mymod:thing", BlockEdit.blockId("MyMod:Thing"));
    }

    @Test void errorCarriesOpAndPos() {
        JsonObject e = new BlockEdit(BlockEdit.Op.PLACE, 105, 64, -35, "minecraft:stone", "up").error("edit_denied", "occupied", 42L);
        assertEquals("error", e.get("type").getAsString());
        assertEquals("edit_denied", e.get("code").getAsString());
        assertEquals("place", e.get("op").getAsString());
        assertEquals(Protocol.ints(105, 64, -35), e.get("pos"));
        assertEquals(42, e.get("ref_seq").getAsLong());
        assertFalse(e.get("fatal").getAsBoolean());
    }

    @Test void limiterAllowsOneSecondBurstThenRefills() {
        EditLimiter l = new EditLimiter(20);
        long t = 1_000_000_000L;
        int ok = 0;
        for (int i = 0; i < 40; i++) if (l.tryAcquire(t)) ok++;
        assertEquals(20, ok, "a burst gets one second's worth");
        assertFalse(l.tryAcquire(t + 10_000_000L), "10 ms later: not yet a whole token");
        assertTrue(l.tryAcquire(t + 50_000_000L), "50 ms later: one token back at 20/s");
        // Steady 20/s is fine forever.
        long now = t + 50_000_000L;
        for (int i = 0; i < 200; i++) { now += 50_000_000L; assertTrue(l.tryAcquire(now)); }
        // The bucket never holds more than a second's worth after a long pause.
        now += 60_000_000_000L;
        ok = 0;
        for (int i = 0; i < 40; i++) if (l.tryAcquire(now)) ok++;
        assertEquals(20, ok);
    }
}
