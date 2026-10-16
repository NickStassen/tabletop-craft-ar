package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayDeque;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/** [safety] Reports to the server's operators (PROTOCOL.md "Chat safety: mute, block, report"): messages, context, log. */
class ReportTest {
    @TempDir Path dir;

    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("../tools/fixtures/" + name + ".json"))).getAsJsonObject();
    }

    private static void same(JsonObject fx, JsonObject built, String... keys) {
        for (String k : keys) assertEquals(fx.get(k), built.get(k), k);
    }

    @Test void reportFixtureParses() throws Exception {
        ReportService.Report r = ReportService.parse(fixture("report"));
        assertEquals("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", r.uuid());
        assertEquals("Alex", r.name());
        assertEquals("harassment", r.reason());
        assertEquals("kept insulting me in chat", r.details());
        assertTrue(r.blocked());
        assertTrue(ReportService.REASONS.contains(r.reason()));
        assertTrue(fixture("hello_report").getAsJsonArray("caps").toString().contains(ReportService.CAP));
        assertTrue(fixture("welcome_report").getAsJsonArray("caps").toString().contains(ReportService.CAP));
    }

    @Test void ackAndErrorMatchTheFixtures() throws Exception {
        same(fixture("report_ack"), ReportService.ack(70, "5e1a9c03", 1), "type", "ref_seq", "id", "operators");
        same(fixture("error_report_denied"), ReportService.error("report_denied", "reports are off on this server (reports.enabled)", 71L),
            "type", "code", "message", "ref_seq", "fatal", "op");
    }

    @Test void malformedReportsAreBadMessages() {
        for (String bad : new String[] { "{}", "{\"player\":{}}", "{\"player\":{\"uuid\":\"nope\"},\"reason\":\"spam\"}",
                                         "{\"player\":{\"uuid\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\"}}",
                                         "{\"player\":{\"uuid\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\"},\"reason\":\"Spam!\"}",
                                         "{\"player\":{\"uuid\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\"},\"reason\":\"spam\",\"details\":\"" + "x".repeat(201) + "\"}" })
            assertThrows(IllegalArgumentException.class, () -> ReportService.parse(JsonParser.parseString(bad).getAsJsonObject()), bad);
        // optional fields: no name, no details, blank details, an unknown (newer) reason word; control characters are cleaned
        ReportService.Report r = ReportService.parse(JsonParser.parseString(
            "{\"player\":{\"uuid\":\"0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0\"},\"reason\":\"doxxing\",\"details\":\"  \"}").getAsJsonObject());
        assertEquals("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", r.uuid(), "uuids are normalised");
        assertNull(r.name());
        assertNull(r.details());
        assertFalse(r.blocked());
        assertEquals("doxxing", r.reason());
        ReportService.Report c = ReportService.parse(JsonParser.parseString(
            "{\"player\":{\"uuid\":\"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0\",\"name\":\"Al\\nex\"},\"reason\":\"spam\",\"details\":\"line one\\nline two\\u0007\"}").getAsJsonObject());
        assertEquals("line one line two", c.details(), "one line in the log, no control characters");
        assertEquals("Al_ex", c.name());
    }

    @Test void contextKeepsTheNewestLinesPerPlayerWithinTheWindow() {
        ReportService.ChatContext ctx = new ReportService.ChatContext();
        long t = 1_000_000;
        for (int i = 0; i < 25; i++) ctx.heard("a", "Alex", "chat", "line " + i, t + i * 1000L, 20);
        ctx.heard("b", "Steve", "say", "hello", t, 20);
        ctx.heard("a", "Alex", "whisper", "psst", t + 30_000, 20);
        List<ReportService.ChatContext.Line> l = ctx.recent("a", t + 30_000, 60_000);
        assertEquals(20, l.size(), "at most context-lines per player");
        assertEquals("<Alex> line 6", l.get(0).line());
        assertEquals("Alex whispers to the headsets: psst", l.get(19).line());
        assertEquals(List.of(), ctx.recent("nobody", t, 60_000));
        assertEquals(1, ctx.recent("a", t + 30_000, 5_000).size(), "older lines than the window are left out");
        assertEquals("[Steve] hello", ctx.recent("b", t, 1).get(0).line());
        assertEquals("* Alex waves", new ReportService.ChatContext.Line(0, "me", "Alex", "waves").line());
        for (int i = 0; i < ReportService.MAX_PLAYERS + 10; i++) ctx.heard("p" + i, "P" + i, "chat", "x", t, 20);
        assertEquals(ReportService.MAX_PLAYERS, ctx.players(), "the players heard least recently are dropped");
    }

    @Test void formatHasEverythingTheOperatorNeeds() throws Exception {
        ReportService.Report r = ReportService.parse(fixture("report"));
        String block = ReportService.format("5e1a9c03", 1_760_370_009_000L, "headset \"Quest 3\" (device a1b2c3d4) session 6f1c2a3b as Nick", r, "Alex", "online",
            List.of(new ReportService.ChatContext.Line(1_760_370_000_000L, "chat", "Alex", "you are terrible")));
        assertTrue(block.startsWith("=== report 5e1a9c03"));
        for (String part : new String[] { "Quest 3", "a1b2c3d4", "Alex 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 (online)", "reason:  harassment",
                                          "note:    kept insulting me in chat", "blocked on the headset: yes", "<Alex> you are terrible" })
            assertTrue(block.contains(part), part);
        String none = ReportService.format("1", 0, "x", new ReportService.Report("u", null, "spam", null, false), "?", "never seen on this server", List.of());
        assertTrue(none.contains("none heard") && !none.contains("note:"));
    }

    @Test void appendRotatesPastTheLimit() throws Exception {
        Path f = dir.resolve("plugin").resolve(ReportService.LOG_NAME);
        ReportService.append(f, "a".repeat(600) + "\n", 1024);
        ReportService.append(f, "b".repeat(300) + "\n", 1024);
        assertEquals(902, Files.size(f));
        ReportService.append(f, "c".repeat(300) + "\n", 1024);
        assertEquals(301, Files.size(f), "the full log moved aside");
        assertEquals(902, Files.size(f.resolveSibling(ReportService.LOG_NAME + ".1")));
    }

    @Test void slidingWindowAdmitsPerMinute() {
        ArrayDeque<Long> times = new ArrayDeque<>();
        assertTrue(ReportService.admit(times, 0, 3));
        assertTrue(ReportService.admit(times, 1000, 3));
        assertTrue(ReportService.admit(times, 2000, 3));
        assertFalse(ReportService.admit(times, 30_000, 3));
        assertTrue(ReportService.admit(times, 60_000, 3), "the first one left the window");
        assertFalse(ReportService.admit(times, 60_500, 3));
    }
}
