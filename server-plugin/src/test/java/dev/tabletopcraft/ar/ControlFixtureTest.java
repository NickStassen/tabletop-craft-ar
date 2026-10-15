package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.*;

/**
 * [talk] The chat, command and status fixtures (PROTOCOL.md 4.26-4.28) against the plugin's builders and parsers, like
 * FixtureTest does for the older messages (kept apart so the files don't collide with other features' fixture tests).
 */
class ControlFixtureTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static void same(JsonObject fx, JsonObject built, String... keys) {
        for (String k : keys) assertEquals(fx.get(k), built.get(k), k);
    }

    @Test void chatFixturesMatchTheBuilder() throws Exception {
        JsonObject c = fixture("chat");
        same(c, ControlMessages.chat("chat", new ControlMessages.From("Alex", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", 7, null), "hi giant", null),
            "type", "kind", "from", "text", "line");
        JsonObject d = fixture("chat_death");
        same(d, ControlMessages.chat("death", new ControlMessages.From("Alex", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", 7, null), "Alex was slain by Zombie", null),
            "type", "kind", "from", "text");
        JsonObject o = fixture("chat_operator");
        String line = ChatText.plain(ChatText.format(ChatText.DEFAULT_FORMAT, "Nick", "hello from the table"));
        same(o, ControlMessages.chat("operator", new ControlMessages.From("Nick", null, null, "6f1c"), "hello from the table", line),
            "type", "kind", "from", "text", "line");
        for (JsonObject f : new JsonObject[] { c, d, o }) assertTrue(ControlMessages.CHAT_KINDS.contains(f.get("kind").getAsString()));
        assertThrows(IllegalArgumentException.class, () -> ControlMessages.chat("shout", null, "x", null));
        assertEquals("hello from the table", ControlMessages.chatSendText(fixture("chat_send")));
        assertThrows(IllegalArgumentException.class, () -> ControlMessages.chatSendText(new JsonObject()));
    }

    @Test void commandFixtureParsesAndWrapsWithThePointer() throws Exception {
        ControlMessages.CommandRequest r = ControlMessages.command(fixture("command"), 256);
        assertEquals("summon minecraft:lightning_bolt ~ ~1 ~", r.line());
        assertEquals("world", r.atWorld());
        assertArrayEquals(new int[] { 105, 64, -35 }, r.atPos());
        assertEquals("execute in minecraft:overworld positioned 105.5 64 -34.5 run summon minecraft:lightning_bolt ~ ~1 ~",
            ControlMessages.dispatchLine(r.line(), "minecraft:overworld", r.atPos()));
        assertEquals("time set day", ControlMessages.dispatchLine("/time set day", "minecraft:overworld", null));
    }

    @Test void malformedCommandsAreBadMessages() {
        for (String bad : new String[] { "{}", "{\"line\":\"  \"}", "{\"line\":\"/\"}", "{\"line\":\"time set day\\nstop\"}", "{\"line\":\"" + "x".repeat(257) + "\"}",
                                         "{\"line\":\"time\",\"at\":{\"pos\":[1,2]}}", "{\"line\":\"time\",\"at\":{\"pos\":[1,2.5,3]}}",
                                         "{\"line\":\"time\",\"at\":{\"pos\":[1,\"2\",3]}}" })
            assertThrows(IllegalArgumentException.class, () -> ControlMessages.command(JsonParser.parseString(bad).getAsJsonObject(), 256), bad);
        assertEquals("gamerule adv", ControlMessages.completeLine(JsonParser.parseString("{\"line\":\"gamerule adv\"}").getAsJsonObject(), 256));
        assertEquals("", ControlMessages.completeLine(JsonParser.parseString("{\"line\":\"\"}").getAsJsonObject(), 256));
        assertThrows(IllegalArgumentException.class, () -> ControlMessages.completeLine(new JsonObject(), 256));
    }

    @Test void resultOutputAndSuggestionsMatchTheBuilders() throws Exception {
        same(fixture("command_result"), ControlMessages.commandResult(80, true, 0.4249, List.of("Summoned new Lightning Bolt"), false),
            "type", "ref_seq", "ok", "ms", "lines", "as_chat");
        assertTrue(ControlMessages.commandResult(1, true, 0, List.of(), true).get("as_chat").getAsBoolean());
        same(fixture("command_output"), ControlMessages.commandOutput(81, List.of("Backup finished in 1.2 s")), "type", "ref_seq", "lines");
        JsonObject fx = fixture("command_suggestions");
        String line = fixture("command_complete").get("line").getAsString();
        same(fx, ControlMessages.suggestions(82, line, List.of("advance_time", "advance_weather")), "type", "ref_seq", "start", "items");
    }

    @Test void suggestionStartAndRootSlash() {
        // Bukkit answers "/name" for the command name; the slash only stays out unless the line had one.
        JsonObject a = ControlMessages.suggestions(1, "ti", List.of("/time", "/tick", "/time"));
        assertEquals(0, a.get("start").getAsInt());
        assertEquals(Protocol.GSON.toJsonTree(List.of("time", "tick")), a.get("items"));
        JsonObject b = ControlMessages.suggestions(2, "/ti", List.of("/time"));
        assertEquals(1, b.get("start").getAsInt());
        assertEquals("time", b.getAsJsonArray("items").get(0).getAsString());
        JsonObject c = ControlMessages.suggestions(3, "time set ", List.of("day", "noon"));
        assertEquals(9, c.get("start").getAsInt());
        java.util.ArrayList<String> many = new java.util.ArrayList<>();
        for (int i = 0; i < 80; i++) many.add("s" + i);
        assertEquals(ControlMessages.MAX_SUGGESTIONS, ControlMessages.suggestions(4, "x ", many).getAsJsonArray("items").size());
    }

    @Test void statusFixtureMatchesTheBuilder() throws Exception {
        JsonObject built = ControlMessages.serverStatus(new double[] { 20.000001, 19.98, 20.03 }, 3.2149, 812, 2048, 1, 20,
            new ControlMessages.TickStatus(20, false, false, false),
            List.of(new ControlMessages.WorldStatus("world", "normal", 441, 118, 1, 6000, 12, "clear"),
                    new ControlMessages.WorldStatus("world_nether", "nether", 0, 0, 0, 6000, 12, "clear")));
        same(fixture("server_status"), built, "type", "tps", "mspt", "memory", "players", "tick", "worlds");
        assertEquals("thunder", ControlMessages.weather(true, true));
        assertEquals("rain", ControlMessages.weather(true, false));
        assertEquals("clear", ControlMessages.weather(false, false));
    }

    @Test void worldAndPlayersFixturesMatchTheBuilders() throws Exception {
        Map<String, String> rules = new LinkedHashMap<>();
        rules.put("advance_time", "true"); rules.put("advance_weather", "true"); rules.put("keep_inventory", "false");
        rules.put("max_entity_cramming", "24"); rules.put("mob_griefing", "true"); rules.put("random_tick_speed", "3");
        JsonObject w = ControlMessages.worldInfo(84L, "world", "normal", 6000, 12, "rain", "normal", new int[] { 0, 64, 0 },
            new ControlMessages.TickStatus(20, true, false, false), rules);
        same(fixture("world_info"), w, "type", "ref_seq", "world", "env", "time", "day", "weather", "difficulty", "spawn", "tick", "gamerules");
        JsonObject p = ControlMessages.playersInfo(85L, List.of(
            new ControlMessages.PlayerRow("Alex", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", 7, "world", new double[] { 112.5, 64, -30.25 }, "survival", 18.5, 20, 17, 23, false),
            new ControlMessages.PlayerRow("Steve", "1a2b3c4d-5e6f-4a1b-9c2d-3e4f5a6b7c8d", 9, "world_nether", new double[] { -8.3, 70, 4.75 }, "creative", 20, 20, 20, 41, true)));
        same(fixture("players_info"), p, "type", "ref_seq", "players");
    }

    @Test void welcomeAndErrorFixtures() throws Exception {
        JsonObject w = fixture("welcome_control");
        assertEquals(w.get("chat"), ControlMessages.welcomeChat(true, "Nick", 256));
        assertEquals(w.get("commands"), ControlMessages.welcomeCommands(List.of("time set day", "weather clear", "tick freeze", "tick unfreeze"), 256));
        var caps = w.getAsJsonArray("caps").toString();
        for (String cap : new String[] { ControlMessages.CAP_CHAT, ControlMessages.CAP_COMMAND, ControlMessages.CAP_STATUS }) assertTrue(caps.contains(cap), cap);
        assertEquals("Nick", ChatText.name(fixture("hello_control").get("operator_name").getAsString(), "Tabletop"));
        JsonObject e = fixture("error_command_denied");
        same(e, Protocol.error("command_denied", "/stop is in commands.deny", 83L, false), "type", "code", "message", "ref_seq", "fatal");
        assertEquals(e.get("message").getAsString(), new CommandPolicy(List.of(), CommandPolicy.DEFAULT_DENY, l -> l).check("stop").reason());
    }
}
