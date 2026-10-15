package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

import static org.junit.jupiter.api.Assertions.*;

/** [hands] The interact, item_registry, entity_op and entity_info fixtures against the plugin's parsers and builders. */
class InteractFixtureTest {
    static final String U = "3c1e5f7a-2b4d-4e6f-8a9b-0c1d2e3f4a5b";

    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static void same(JsonObject fx, JsonObject built, String... keys) {
        for (String k : keys) assertEquals(fx.get(k), built.get(k), k);
    }

    private static List<String> strings(JsonArray a) {
        List<String> l = new ArrayList<>();
        a.forEach(e -> l.add(e.getAsString()));
        return l;
    }

    @Test void interactFixturesParse() throws Exception {
        InteractMessages.Interact door = InteractMessages.interact(fixture("interact"));
        assertEquals(List.of(105, 64, -35), List.of(door.x(), door.y(), door.z()));
        assertEquals("north", door.face());
        assertNull(door.item(), "empty hand");
        assertFalse(door.sneak());
        assertArrayEquals(new double[] { 0.5, 0.7, 0.0 }, door.hit());
        assertArrayEquals(new double[] { 0.1, -0.5, 0.86 }, door.dir());
        InteractMessages.Interact meal = InteractMessages.interact(fixture("interact_item"));
        assertEquals("minecraft:bone_meal", meal.item());
        assertNull(meal.hit());
        JsonObject err = fixture("error_interact_denied");
        same(err, meal.error("interact_denied", "the axe does nothing to stone", 91L), "type", "code", "message", "ref_seq", "fatal", "op", "pos");
    }

    @Test void malformedInteractsAreBadMessages() {
        for (String bad : new String[] { "{}", "{\"pos\":[1,2,3]}", "{\"pos\":[1,2,3],\"face\":\"sideways\"}", "{\"pos\":[1,2.5,3],\"face\":\"up\"}",
                                         "{\"pos\":[1,2,3],\"face\":\"up\",\"hit\":[0.5,2,0.5]}", "{\"pos\":[1,2,3],\"face\":\"up\",\"item\":\"bone meal\"}",
                                         "{\"pos\":[1,2,3],\"face\":\"up\",\"item\":\"\"}", "{\"pos\":[1,2,3],\"face\":\"up\",\"dir\":[1,2]}" })
            assertThrows(IllegalArgumentException.class, () -> InteractMessages.interact(JsonParser.parseString(bad).getAsJsonObject()), bad);
        assertEquals("tabletop:lightning", InteractMessages.interact(JsonParser.parseString("{\"pos\":[1,2,3],\"face\":\"up\",\"item\":\"tabletop:lightning\"}").getAsJsonObject()).item());
    }

    @Test void entityOpFixturesParse() throws Exception {
        InteractMessages.EntityOp hit = InteractMessages.entityOp(fixture("entity_op"));
        assertEquals(UUID.fromString(U), hit.uuid());
        assertEquals("attack", hit.op());
        assertArrayEquals(new double[] { 0.1, -0.4, 0.9 }, hit.dir());
        InteractMessages.EntityOp move = InteractMessages.entityOp(fixture("entity_op_move"));
        assertArrayEquals(new double[] { 110.5, 64.0, -30.5 }, move.to());
        assertEquals(90f, move.yaw());
        assertEquals("minecraft:shears", InteractMessages.entityOp(fixture("entity_op_use")).item());
        assertEquals("Rex", InteractMessages.entityOp(fixture("entity_op_name")).text());
        JsonObject err = fixture("error_not_found");
        same(err, InteractMessages.entityOp(fixture("entity_op_use")).error("not_found", "no such entity (it may have died or left)", 97L),
            "type", "code", "message", "ref_seq", "fatal", "op", "entity_op", "uuid");
    }

    @Test void malformedEntityOpsAreBadMessages() {
        String u = "\"uuid\":\"" + U + "\"";
        for (String bad : new String[] { "{\"op\":\"kill\"}", "{\"uuid\":\"pig\",\"op\":\"kill\"}", "{" + u + ",\"op\":\"explode\"}", "{" + u + "}",
                                         "{" + u + ",\"op\":\"move\"}", "{" + u + ",\"op\":\"hold\",\"to\":[1,2]}", "{" + u + ",\"op\":\"name\"}",
                                         "{" + u + ",\"op\":\"use\",\"item\":\"Shears\"}", "{" + u + ",\"op\":\"move\",\"to\":[1,2,4e9]}" })
            assertThrows(IllegalArgumentException.class, () -> InteractMessages.entityOp(JsonParser.parseString(bad).getAsJsonObject()), bad);
        for (String op : InteractMessages.OPS) {
            String extra = switch (op) { case "move", "hold" -> ",\"to\":[1,64,2]"; case "name" -> ",\"text\":\"\""; default -> ""; };
            assertEquals(op, InteractMessages.entityOp(JsonParser.parseString("{" + u + ",\"op\":\"" + op + "\"" + extra + "}").getAsJsonObject()).op());
        }
    }

    @Test void namesAreCleaned() {
        assertEquals("Rex the Dog", InteractMessages.cleanName("  Rex§c  the\tDog\n"));
        assertEquals("", InteractMessages.cleanName("   "));
        assertEquals(64, InteractMessages.cleanName("x".repeat(100)).length());
    }

    @Test void itemRegistryMatchesTheBuilder() throws Exception {
        JsonObject fx = fixture("item_registry");
        JsonArray cats = fx.getAsJsonArray("categories");
        List<String> use = strings(cats.get(0).getAsJsonObject().getAsJsonArray("items"));
        List<String> eggs = strings(cats.get(1).getAsJsonObject().getAsJsonArray("items"));
        assertEquals(InteractRules.usable(InteractRules.Flags.ALL, true), use, "the fixture lists every usable item in menu order");
        same(fx, InteractMessages.itemRegistry(use, eggs), "type", "count", "categories");
        for (String egg : eggs) assertNotNull(InteractRules.eggType(egg), egg);
    }

    @Test void entityInfoMatchesTheBuilder() throws Exception {
        JsonObject built = InteractMessages.entityInfo(99L, new InteractMessages.Info(U, 104, "minecraft:wolf", "Rex", new double[] { 108.7, 64.0, -28.0 },
            new double[] { 14.0, 20.0 }, "Alex", -2400, true, true, false, false, false, false, false, null, 0));
        same(fixture("entity_info"), built, "type", "ref_seq", "uuid", "id", "entity_type", "name", "pos", "health", "owner", "age", "baby", "ai", "frozen",
            "glowing", "held", "leashed", "player", "vehicle", "passengers");
    }

    @Test void welcomeAndHelloCarryTheCap() throws Exception {
        JsonObject w = fixture("welcome_interact");
        assertTrue(strings(w.getAsJsonArray("caps")).contains(InteractMessages.CAP));
        assertEquals(w.get("interact"), InteractMessages.welcomeInteract(InteractRules.Flags.ALL, true, false, 5, 60));
        assertTrue(strings(fixture("hello_interact").getAsJsonArray("caps")).contains(InteractMessages.CAP));
    }
}
