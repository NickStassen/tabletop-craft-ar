package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.util.List;
import java.util.Map;

/**
 * [talk] Builders and parsers for chat, commands and server status (PROTOCOL.md 4.26-4.28), pure and unit-tested
 * against tools/fixtures. Envelope fields are added by {@link Session#send}.
 */
public final class ControlMessages {
    public static final String CAP_CHAT = "chat_v1", CAP_COMMAND = "command_v1", CAP_STATUS = "status_v1";
    /** chat.kind values (PROTOCOL.md 4.26). */
    public static final List<String> CHAT_KINDS = List.of("chat", "join", "quit", "death", "advancement", "broadcast", "say", "me", "operator", "whisper");
    /** Most lines one command_result carries, and the longest line (characters). */
    public static final int MAX_LINES = 200, MAX_LINE_CHARS = 1000;
    public static final int MAX_SUGGESTIONS = 50;

    private ControlMessages() {}

    /** Who said something: a player (name, uuid, entity id), the console ({"name":"Server"}) or a headset session. */
    public record From(String name, String uuid, Integer id, String session) {
        public static From server() { return new From("Server", null, null, null); }
        JsonObject json() {
            JsonObject o = new JsonObject();
            o.addProperty("name", name);
            if (uuid != null) o.addProperty("uuid", uuid);
            if (id != null) o.addProperty("id", id);
            if (session != null) o.addProperty("session", session);
            return o;
        }
    }

    /** chat (S->C). from may be null (a broadcast); line is the whole line as Java players saw it (operator only). */
    public static JsonObject chat(String kind, From from, String text, String line) {
        if (!CHAT_KINDS.contains(kind)) throw new IllegalArgumentException("unknown chat kind " + kind);
        JsonObject o = Protocol.body("chat");
        o.addProperty("kind", kind);
        if (from != null) o.add("from", from.json());
        o.addProperty("text", text);
        if (line != null) o.addProperty("line", line);
        return o;
    }

    /** chat_send (C->S): the text, unchecked (ChatText.message cleans it). */
    public static String chatSendText(JsonObject m) {
        String t = str(m, "text");
        if (t == null) throw new IllegalArgumentException("chat_send needs text");
        return t;
    }

    /** A command from the headset: the line and an optional pointer (world block coordinates). */
    public record CommandRequest(String line, String atWorld, int[] atPos) {
        public boolean hasAt() { return atPos != null; }
    }

    /** command (C->S). Throws IllegalArgumentException (bad_message) when malformed or over maxLen characters. */
    public static CommandRequest command(JsonObject m, int maxLen) {
        String line = commandLine(m, "command", maxLen);
        String w = null;
        int[] pos = null;
        if (m.has("at") && m.get("at").isJsonObject()) {
            JsonObject at = m.getAsJsonObject("at");
            w = str(at, "world");
            JsonArray p = at.has("pos") && at.get("pos").isJsonArray() ? at.getAsJsonArray("pos") : null;
            if (p == null || p.size() != 3) throw new IllegalArgumentException("command at needs pos [x, y, z]");
            pos = new int[3];
            for (int i = 0; i < 3; i++) {
                JsonElement e = p.get(i);
                if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("command at pos must be integers");
                double d = e.getAsDouble();
                if (d != Math.rint(d) || Math.abs(d) > 30_000_000) throw new IllegalArgumentException("command at pos must be integer block coordinates");
                pos[i] = (int) d;
            }
        }
        return new CommandRequest(line, w, pos);
    }

    /** command_complete (C->S): the partial line (may be empty or end with a space). */
    public static String completeLine(JsonObject m, int maxLen) {
        String l = str(m, "line");
        if (l == null) throw new IllegalArgumentException("command_complete needs line");
        if (l.length() > maxLen) throw new IllegalArgumentException("command_complete line longer than " + maxLen + " characters");
        for (int i = 0; i < l.length(); i++) if (Character.isISOControl(l.charAt(i))) throw new IllegalArgumentException("command_complete line has control characters");
        return l;
    }

    private static String commandLine(JsonObject m, String type, int maxLen) {
        String l = str(m, "line");
        if (l == null || CommandPolicy.strip(l).isEmpty()) throw new IllegalArgumentException(type + " needs a line");
        if (l.length() > maxLen) throw new IllegalArgumentException(type + " line longer than " + maxLen + " characters");
        for (int i = 0; i < l.length(); i++) if (Character.isISOControl(l.charAt(i))) throw new IllegalArgumentException(type + " line has control characters (one command per message)");
        return l;
    }

    /**
     * The line the server dispatches: with a pointer, vanilla's {@code execute in <dimension> positioned <x+.5> <y>
     * <z+.5> run <line>} so {@code ~ ~ ~}, {@code @p} and {@code @e[distance=..5]} are relative to the block the
     * headset points at.
     */
    public static String dispatchLine(String line, String dimensionKey, int[] at) {
        String l = CommandPolicy.strip(line);
        if (at == null) return l;
        return "execute in " + dimensionKey + " positioned " + (at[0] + 0.5) + " " + at[1] + " " + (at[2] + 0.5) + " run " + l;
    }

    /** command_result (S->C). ok: the command ran and none of its feedback was an error. */
    public static JsonObject commandResult(long refSeq, boolean ok, double ms, List<String> lines, boolean asChat) {
        JsonObject o = Protocol.body("command_result");
        o.addProperty("ref_seq", refSeq);
        o.addProperty("ok", ok);
        o.addProperty("ms", Math.round(ms * 100) / 100.0);
        o.add("lines", strings(lines));
        if (asChat) o.addProperty("as_chat", true);
        return o;
    }

    /** command_output (S->C): feedback that arrived after the result (async plugin commands), within 5 s. */
    public static JsonObject commandOutput(long refSeq, List<String> lines) {
        JsonObject o = Protocol.body("command_output");
        o.addProperty("ref_seq", refSeq);
        o.add("lines", strings(lines));
        return o;
    }

    /**
     * command_suggestions (S->C). Bukkit's tabComplete answers whole replacement tokens for the last word, so start
     * is where that word begins in the line as sent (after its last space; a leading '/' counts). Suggestions for the
     * command name come back as "/name"; the slash is dropped unless the line has one.
     */
    public static JsonObject suggestions(long refSeq, String line, List<String> raw) {
        int start = line.lastIndexOf(' ') + 1;
        boolean root = start == 0;
        boolean slash = line.startsWith("/");
        if (root && slash) start = 1;
        JsonArray items = new JsonArray();
        java.util.LinkedHashSet<String> seen = new java.util.LinkedHashSet<>();
        for (String s : raw) {
            if (s == null || s.isEmpty()) continue;
            String t = root && s.startsWith("/") ? s.substring(1) : s;
            if (seen.add(t) && seen.size() <= MAX_SUGGESTIONS) items.add(t);
        }
        JsonObject o = Protocol.body("command_suggestions");
        o.addProperty("ref_seq", refSeq);
        o.addProperty("start", start);
        o.add("items", items);
        return o;
    }

    /** server_status (S->C), every status.every-ticks to status_v1 sessions. */
    public record WorldStatus(String name, String env, int chunks, int entities, int players, long time, long day, String weather) {}
    public record TickStatus(double rate, boolean frozen, boolean stepping, boolean sprinting) {}

    public static JsonObject serverStatus(double[] tps, double mspt, long usedMb, long maxMb, int online, int maxPlayers, TickStatus tick, List<WorldStatus> worlds) {
        JsonObject o = Protocol.body("server_status");
        JsonArray t = new JsonArray();
        for (double d : tps) t.add(Math.round(Math.min(d, Math.max(20.0, tick.rate())) * 100) / 100.0);   // Paper reports a hair over the rate
        o.add("tps", t);
        o.addProperty("mspt", Math.round(mspt * 100) / 100.0);
        JsonObject mem = new JsonObject();
        mem.addProperty("used_mb", usedMb);
        mem.addProperty("max_mb", maxMb);
        o.add("memory", mem);
        JsonObject pl = new JsonObject();
        pl.addProperty("online", online);
        pl.addProperty("max", maxPlayers);
        o.add("players", pl);
        o.add("tick", tick(tick));
        JsonArray ws = new JsonArray();
        for (WorldStatus w : worlds) {
            JsonObject j = new JsonObject();
            j.addProperty("name", w.name());
            j.addProperty("env", w.env());
            j.addProperty("chunks", w.chunks());
            j.addProperty("entities", w.entities());
            j.addProperty("players", w.players());
            j.addProperty("time", w.time());
            j.addProperty("day", w.day());
            j.addProperty("weather", w.weather());
            ws.add(j);
        }
        o.add("worlds", ws);
        return o;
    }

    static JsonObject tick(TickStatus tick) {
        JsonObject k = new JsonObject();
        k.addProperty("rate", Math.round(tick.rate() * 100) / 100.0);
        k.addProperty("frozen", tick.frozen());
        k.addProperty("stepping", tick.stepping());
        k.addProperty("sprinting", tick.sprinting());
        return k;
    }

    /** "clear", "rain" or "thunder". */
    public static String weather(boolean storm, boolean thunder) { return thunder ? "thunder" : storm ? "rain" : "clear"; }

    /**
     * world_info (S->C), the reply to world_query. gamerules: name (as the server's gamerule command takes it) to
     * value: booleans as JSON booleans, numbers as numbers, anything else as a string.
     */
    public static JsonObject worldInfo(Long refSeq, String world, String env, long time, long day, String weather, String difficulty,
                                       int[] spawn, TickStatus tick, Map<String, String> gamerules) {
        JsonObject o = Protocol.body("world_info");
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        o.addProperty("world", world);
        o.addProperty("env", env);
        o.addProperty("time", time);
        o.addProperty("day", day);
        o.addProperty("weather", weather);
        o.addProperty("difficulty", difficulty);
        o.add("spawn", Protocol.ints(spawn));
        o.add("tick", tick(tick));
        JsonObject g = new JsonObject();
        for (var e : gamerules.entrySet()) {
            String v = e.getValue();
            if ("true".equals(v) || "false".equals(v)) g.addProperty(e.getKey(), Boolean.parseBoolean(v));
            else {
                try { g.addProperty(e.getKey(), Long.parseLong(v)); }
                catch (NumberFormatException x) {
                    try { g.addProperty(e.getKey(), Double.parseDouble(v)); } catch (NumberFormatException y) { g.addProperty(e.getKey(), v); }
                }
            }
        }
        o.add("gamerules", g);
        return o;
    }

    /** One players_info entry. */
    public record PlayerRow(String name, String uuid, int id, String world, double[] pos, String gamemode, double health, double maxHealth,
                            int food, int ping, boolean op) {}

    public static JsonObject playersInfo(Long refSeq, List<PlayerRow> players) {
        JsonObject o = Protocol.body("players_info");
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        JsonArray a = new JsonArray();
        for (PlayerRow p : players) {
            JsonObject j = new JsonObject();
            j.addProperty("name", p.name());
            j.addProperty("uuid", p.uuid());
            j.addProperty("id", p.id());
            j.addProperty("world", p.world());
            j.add("pos", Protocol.doubles(p.pos()));
            j.addProperty("gamemode", p.gamemode());
            j.addProperty("health", Math.round(p.health() * 10) / 10.0);
            j.addProperty("max_health", Math.round(p.maxHealth() * 10) / 10.0);
            j.addProperty("food", p.food());
            j.addProperty("ping", p.ping());
            j.addProperty("op", p.op());
            a.add(j);
        }
        o.add("players", a);
        return o;
    }

    /** welcome.chat (sessions with chat_v1). */
    public static JsonObject welcomeChat(boolean send, String name, int maxLen) {
        JsonObject o = new JsonObject();
        o.addProperty("send", send);
        o.addProperty("name", name);
        o.addProperty("max_len", maxLen);
        return o;
    }

    /** welcome.commands (sessions with command_v1). */
    public static JsonObject welcomeCommands(List<String> quick, int maxLen) {
        JsonObject o = new JsonObject();
        o.add("quick", strings(quick));
        o.addProperty("max_len", maxLen);
        return o;
    }

    static JsonArray strings(List<String> l) {
        JsonArray a = new JsonArray();
        for (String s : l) a.add(s);
        return a;
    }

    private static String str(JsonObject m, String k) {
        return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null;
    }
}
