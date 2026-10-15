package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/** [power] The undo, box_edit, brush, history and box_progress fixtures against the plugin's parsers and builders. */
class PowerFixtureTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static JsonObject json(String s) { return JsonParser.parseString(s).getAsJsonObject(); }

    private static void same(JsonObject fx, JsonObject built, String... keys) {
        for (String k : keys) assertEquals(fx.get(k), built.get(k), k);
    }

    private static List<String> strings(JsonArray a) {
        List<String> l = new ArrayList<>();
        a.forEach(e -> l.add(e.getAsString()));
        return l;
    }

    @Test void groupsRideOnEditsAndUses() throws Exception {
        assertEquals(7L, PowerMessages.group(fixture("block_edit_group")));
        assertEquals(8L, PowerMessages.group(fixture("interact_group")));
        assertNull(PowerMessages.group(fixture("block_edit")), "no group: the edit is its own group");
        // the rest of the message still parses as before
        assertEquals(BlockEdit.Op.PLACE, BlockEdit.parse(fixture("block_edit_group")).op());
        assertEquals("north", InteractMessages.interact(fixture("interact_group")).face());
        for (String bad : new String[] { "{\"group\":-1}", "{\"group\":1.5}", "{\"group\":\"seven\"}", "{\"group\":5000000000}" })
            assertThrows(IllegalArgumentException.class, () -> PowerMessages.group(json(bad)), bad);
        assertNull(PowerMessages.group(json("{\"group\":null}")));
    }

    @Test void boxEditFixturesParse() throws Exception {
        PowerMessages.BoxEdit fill = PowerMessages.boxEdit(fixture("box_edit_fill"));
        assertEquals("fill", fill.op());
        assertEquals("minecraft:stone_bricks", fill.state());
        assertEquals(List.of(101, 60, -30), List.of(fill.minX(), fill.minY(), fill.minZ()), "corners in any order");
        assertEquals(List.of(110, 64, -21), List.of(fill.maxX(), fill.maxY(), fill.maxZ()));
        assertEquals(10 * 5 * 10, fill.volume());
        assertEquals(9L, fill.group());
        PowerMessages.BoxEdit rep = PowerMessages.boxEdit(fixture("box_edit_replace"));
        assertEquals("minecraft:stone_bricks", rep.filter());
        assertEquals("minecraft:glass", rep.state());
        PowerMessages.BoxEdit clone = PowerMessages.boxEdit(fixture("box_edit_clone"));
        assertArrayEquals(new int[] { 112, 60, -30 }, clone.dest());
        assertEquals(1, clone.rotate());
        assertNull(clone.state());
        assertEquals(4 * 3 * 2, clone.volume());
        JsonObject err = fixture("error_box_denied");
        same(err, fill.error("edit_denied", "the box is 40000 blocks, more than 32768 (edit.box-max-blocks)", 130L),
            "type", "code", "message", "ref_seq", "fatal", "op", "box_op", "group");
    }

    @Test void malformedBoxEditsAreBadMessages() {
        for (String bad : new String[] {
            "{}", "{\"op\":\"melt\",\"from\":[0,0,0],\"to\":[1,1,1]}", "{\"op\":\"fill\",\"from\":[0,0,0],\"to\":[1,1,1]}",
            "{\"op\":\"fill\",\"from\":[0,0],\"to\":[1,1,1],\"state\":\"minecraft:stone\"}",
            "{\"op\":\"fill\",\"from\":[0,0.5,0],\"to\":[1,1,1],\"state\":\"minecraft:stone\"}",
            "{\"op\":\"replace\",\"from\":[0,0,0],\"to\":[1,1,1],\"state\":\"minecraft:stone\"}",
            "{\"op\":\"clone\",\"from\":[0,0,0],\"to\":[1,1,1]}", "{\"op\":\"clone\",\"from\":[0,0,0],\"to\":[1,1,1],\"dest\":[5,0,0],\"rotate\":4}",
            "{\"op\":\"fill\",\"from\":[0,0,0],\"to\":[1,1,1],\"state\":\"stone; drop table\"}" })
            assertThrows(IllegalArgumentException.class, () -> PowerMessages.boxEdit(json(bad)), bad);
        PowerMessages.BoxEdit clear = PowerMessages.boxEdit(json("{\"op\":\"clear\",\"from\":[0,0,0],\"to\":[3,3,3],\"state\":\"minecraft:stone\"}"));
        assertNull(clear.state(), "clear ignores a state");
        assertEquals(0, PowerMessages.boxEdit(json("{\"op\":\"clone\",\"from\":[0,0,0],\"to\":[1,1,1],\"dest\":[5,0,0]}")).rotate());
    }

    @Test void brushFixturesParse() throws Exception {
        PowerMessages.Brush paint = PowerMessages.brush(fixture("brush"), 8);
        assertEquals("paint", paint.op());
        assertEquals("sphere", paint.shape());
        assertEquals(3, paint.radius());
        assertEquals(7, paint.height(), "a sphere is 2r+1 high");
        assertEquals("minecraft:moss_block", paint.state());
        PowerMessages.Brush rep = PowerMessages.brush(fixture("brush_replace"), 8);
        assertEquals("cylinder", rep.shape());
        assertEquals(1, rep.height());
        assertEquals("minecraft:grass_block", rep.filter());
        assertThrows(IllegalArgumentException.class, () -> PowerMessages.brush(fixture("brush"), 2), "radius over the server's maximum");
        for (String bad : new String[] { "{\"op\":\"paint\",\"center\":[0,0,0],\"radius\":2}", "{\"op\":\"erase\",\"center\":[0,0,0]}",
                                         "{\"op\":\"erase\",\"shape\":\"cube\",\"center\":[0,0,0],\"radius\":2}",
                                         "{\"op\":\"replace\",\"center\":[0,0,0],\"radius\":2,\"state\":\"minecraft:stone\"}",
                                         "{\"op\":\"erase\",\"shape\":\"cylinder\",\"center\":[0,0,0],\"radius\":2,\"height\":40}" })
            assertThrows(IllegalArgumentException.class, () -> PowerMessages.brush(json(bad), 8), bad);
        PowerMessages.Brush erase = PowerMessages.brush(json("{\"op\":\"erase\",\"center\":[0,0,0],\"radius\":2,\"state\":\"minecraft:stone\"}"), 8);
        assertNull(erase.state(), "erase places nothing");
    }

    @Test void historyAndProgressBuilders() throws Exception {
        JsonObject h = fixture("history");
        same(h, PowerMessages.history(3, 1, 45, 2, 10000), "type", "undo", "redo", "undo_blocks", "redo_blocks", "depth");
        JsonObject run = fixture("box_progress");
        JsonObject built = PowerMessages.boxProgress(new PowerMessages.Progress("fill", 9L, 130L, "write", 12000, 32768, 11800, 0, 0, false, List.of(), 512.3, 40));
        same(run, built, "type", "op", "group", "ref_seq", "phase", "processed", "total", "changed", "skipped", "outside", "finished");
        assertFalse(built.has("ms"), "timing only when finished");
        JsonObject done = fixture("box_progress_done");
        List<int[]> skipped = new ArrayList<>();
        skipped.add(new int[] { 106, 64, -35 });
        skipped.add(new int[] { 107, 64, -35 });
        JsonObject b2 = PowerMessages.boxProgress(new PowerMessages.Progress("undo", 7L, 120L, "done", 14, 14, 12, 2, 0, true, skipped, 3.24, 1));
        same(done, b2, "type", "op", "group", "ref_seq", "phase", "processed", "total", "changed", "skipped", "outside", "finished", "skipped_pos", "ms", "ticks");
        List<int[]> many = new ArrayList<>();
        for (int i = 0; i < 40; i++) many.add(new int[] { i, 0, 0 });
        assertEquals(PowerMessages.MAX_SKIPPED_POS, PowerMessages.boxProgress(new PowerMessages.Progress("undo", null, null, "done", 40, 40, 0, 40, 0, true, many, 1, 1))
            .getAsJsonArray("skipped_pos").size());
    }

    @Test void welcomeAndHello() throws Exception {
        JsonObject w = fixture("welcome_power");
        assertTrue(strings(w.getAsJsonArray("caps")).containsAll(List.of(Protocol.CAP_EDIT, PowerMessages.CAP)));
        JsonObject lim = new JsonObject();
        PowerMessages.limits(lim, 10000, 32768, 8, 20000);
        same(w.getAsJsonObject("limits"), lim, "undo_depth", "box_max_blocks", "brush_max_radius", "brush_blocks_per_second");
        assertTrue(strings(fixture("hello_power").getAsJsonArray("caps")).contains(PowerMessages.CAP));
        assertNull(PowerMessages.group(fixture("undo")));
        assertEquals("redo", fixture("redo").get("type").getAsString());
    }
}
