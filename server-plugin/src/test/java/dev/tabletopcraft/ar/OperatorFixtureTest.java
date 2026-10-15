package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/** [operator] The bookmark, worlds, container and sign fixtures against the plugin's parsers and builders. */
class OperatorFixtureTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static JsonObject json(String s) { return JsonParser.parseString(s).getAsJsonObject(); }

    private static void same(JsonObject fx, JsonObject built, String... keys) {
        for (String k : keys) assertEquals(fx.get(k), built.get(k), k);
    }

    @Test void bookmarkFixturesParse() throws Exception {
        OperatorMessages.BookmarkOp add = OperatorMessages.bookmark(fixture("bookmark"));
        assertEquals("add", add.op());
        assertEquals("Village", add.name());
        assertEquals("world", add.world());
        assertArrayEquals(new int[] { 120, 64, -300 }, add.pos());
        OperatorMessages.BookmarkOp rm = OperatorMessages.bookmark(fixture("bookmark_remove"));
        assertEquals("remove", rm.op());
        assertNull(rm.world());
        same(fixture("error_mark_denied"), add.error("mark_denied", "at most 200 bookmarks (marks.max): remove one first", 70L),
            "type", "code", "message", "ref_seq", "fatal", "op", "name");
    }

    @Test void bookmarksMessageMatchesTheFixture() throws Exception {
        Bookmarks b = new Bookmarks(200);
        assertNull(b.add(new OperatorMessages.Mark("Village", "world", 120, 64, -300, "Nick")));
        assertNull(b.add(new OperatorMessages.Mark("Nether hub", "world_nether", 8, 70, -4, "Nick")));
        same(fixture("bookmarks"), OperatorMessages.bookmarks(b.list(), b.max), "type", "max", "marks");
    }

    @Test void malformedBookmarksAreBadMessages() {
        for (String bad : new String[] { "{}", "{\"op\":\"rename\",\"name\":\"x\"}", "{\"op\":\"add\",\"name\":\"\"}", "{\"op\":\"add\",\"name\":\"x\"}",
                                         "{\"op\":\"add\",\"name\":\"x\",\"world\":\"world\"}", "{\"op\":\"add\",\"name\":\"x\",\"world\":\"world\",\"pos\":[1,2.5,3]}",
                                         "{\"op\":\"remove\",\"name\":\"\\u00a7\\u00a7\"}" })
            assertThrows(IllegalArgumentException.class, () -> OperatorMessages.bookmark(json(bad)), bad);
        assertEquals("Base camp", OperatorMessages.bookmark(json("{\"op\":\"remove\",\"name\":\"  Base\\n\\u00a7ccamp \"}")).name());
    }

    @Test void worldsAndWelcomeMarksMatchTheFixtures() throws Exception {
        JsonObject wel = fixture("welcome_operator");
        var worlds = List.of(new OperatorMessages.WorldEntry("world", "normal", -64, 320, new int[] { 0, 64, 0 }),
            new OperatorMessages.WorldEntry("world_nether", "nether", 0, 256, new int[] { 0, 70, 0 }),
            new OperatorMessages.WorldEntry("world_the_end", "the_end", 0, 256, new int[] { 100, 49, 0 }));
        assertEquals(wel.get("worlds"), OperatorMessages.worlds(worlds));
        assertEquals(wel.get("marks"), OperatorMessages.welcomeMarks(200, true));
        assertEquals(fixture("region_info_worlds").get("worlds"), OperatorMessages.worlds(worlds));
        assertTrue(wel.getAsJsonArray("caps").contains(new com.google.gson.JsonPrimitive(OperatorMessages.CAP_MARKS)));
        assertEquals("edit", wel.getAsJsonObject("interact").get("containers").getAsString());
        assertTrue(wel.getAsJsonObject("interact").get("signs").getAsBoolean());
    }

    @Test void jumpRequestIsAnOrdinaryRegionRequest() throws Exception {
        JsonObject j = fixture("region_request_jump");
        RegionRequest q = RegionRequest.parse(j);
        assertEquals("world_nether", q.world());
        assertEquals(List.of(-40, 24, -60, 160, 96, 160), List.of(q.ox(), q.oy(), q.oz(), q.dx(), q.dy(), q.dz()));
        assertArrayEquals(new int[] { 32, 16, 32 }, RegionRequest.viewMargin(j));
    }

    @Test void containerFixturesParseAndBuild() throws Exception {
        assertArrayEquals(new int[] { 1, 64, 2 }, OperatorMessages.containerPos(fixture("container_query"), "container_query"));
        assertArrayEquals(new int[] { 1, 64, 2 }, OperatorMessages.containerPos(fixture("container_close"), "container_close"));
        JsonObject fx = fixture("container");
        JsonObject built = OperatorMessages.container(92L, new int[] { 1, 64, 2 }, "minecraft:chest", "Large Chest", 54,
            List.of(new OperatorMessages.Slot(0, "minecraft:diamond", 3), new OperatorMessages.Slot(5, "minecraft:torch", 64),
                    new OperatorMessages.Slot(30, "minecraft:oak_planks", 16)), new int[] { 2, 64, 2 }, "edit");
        same(fx, built, "type", "ref_seq", "pos", "block", "title", "size", "other", "edit", "items");
        JsonObject f = fixture("container_furnace");
        JsonObject fb = OperatorMessages.container(null, new int[] { 4, 64, 2 }, "minecraft:furnace", "Furnace", 3,
            List.of(new OperatorMessages.Slot(0, "minecraft:raw_iron", 12), new OperatorMessages.Slot(1, "minecraft:coal", 5),
                    new OperatorMessages.Slot(2, "minecraft:iron_ingot", 4)), null, "view");
        same(f, fb, "type", "pos", "block", "title", "size", "edit", "items");
        assertFalse(fb.has("other"));
        assertFalse(fb.has("ref_seq"));
        same(fixture("container_closed"), OperatorMessages.containerClosed(new int[] { 1, 64, 2 }, "the container is gone"), "type", "pos", "closed", "reason");
        same(fixture("error_container_denied"), OperatorMessages.error("interact_denied", "a shulker box can't go inside a shulker box", 94L, "container_edit", new int[] { 1, 64, 2 }),
            "type", "code", "message", "ref_seq", "fatal", "op", "pos");
    }

    @Test void containerEditsParse() throws Exception {
        OperatorMessages.ContainerEdit take = OperatorMessages.containerEdit(fixture("container_edit"));
        assertEquals("set", take.op());
        assertEquals(5, take.slot());
        assertEquals("minecraft:torch", take.item());
        assertEquals(32, take.count());
        assertTrue(take.checkWas());
        assertEquals("minecraft:torch", take.wasItem());
        assertEquals(64, take.wasCount());
        assertFalse(take.clears());
        OperatorMessages.ContainerEdit put = OperatorMessages.containerEdit(fixture("container_edit_put"));
        assertTrue(put.checkWas());
        assertNull(put.wasItem());
        assertEquals(0, put.wasCount());
        OperatorMessages.ContainerEdit sort = OperatorMessages.containerEdit(fixture("container_edit_sort"));
        assertEquals("sort", sort.op());
        assertTrue(OperatorMessages.containerEdit(json("{\"pos\":[1,2,3],\"slot\":0,\"item\":null,\"count\":0}")).clears());
        assertTrue(OperatorMessages.containerEdit(json("{\"pos\":[1,2,3],\"slot\":0}")).clears());
        for (String bad : new String[] { "{\"pos\":[1,2,3]}", "{\"pos\":[1,2,3],\"slot\":-1}", "{\"pos\":[1,2,3],\"slot\":0,\"item\":\"diamond\"}",
                                         "{\"pos\":[1,2,3],\"slot\":0,\"item\":\"minecraft:diamond\",\"count\":500}", "{\"pos\":[1,2,3],\"op\":\"shuffle\"}",
                                         "{\"pos\":[1,2,3],\"slot\":0.5}", "{\"pos\":[1,2,3],\"slot\":0,\"was\":[\"minecraft:dirt\"]}" })
            assertThrows(IllegalArgumentException.class, () -> OperatorMessages.containerEdit(json(bad)), bad);
    }

    @Test void signEditsParse() throws Exception {
        OperatorMessages.SignEdit e = OperatorMessages.signEdit(fixture("sign_edit"));
        assertArrayEquals(new int[] { 106, 64, -35 }, e.pos());
        assertEquals("front", e.side());
        assertEquals(List.of("", "Shop", "open 9-5", ""), e.lines());
        assertEquals("light_blue", e.color());
        assertEquals(Boolean.TRUE, e.glowing());
        OperatorMessages.SignEdit plain = OperatorMessages.signEdit(json("{\"pos\":[1,2,3],\"side\":\"back\",\"lines\":[\"a\\u00a7cb\",null,\"tab\\there\",\"x\\u0007\"]}"));
        assertEquals(List.of("ab", "", "tab here", "x"), plain.lines());
        assertNull(plain.color());
        assertNull(plain.glowing());
        assertEquals(90, OperatorMessages.cleanSignLine("x".repeat(200)).length());
        for (String bad : new String[] { "{\"pos\":[1,2,3],\"side\":\"top\",\"lines\":[\"\",\"\",\"\",\"\"]}", "{\"pos\":[1,2,3],\"side\":\"front\",\"lines\":[\"\"]}",
                                         "{\"pos\":[1,2,3],\"side\":\"front\",\"lines\":[\"\",\"\",\"\",\"\"],\"color\":\"teal\"}",
                                         "{\"pos\":[1,2,3],\"side\":\"front\",\"lines\":[1,\"\",\"\",\"\"]}" })
            assertThrows(IllegalArgumentException.class, () -> OperatorMessages.signEdit(json(bad)), bad);
    }
}
