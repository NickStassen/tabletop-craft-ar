package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.format.NamedTextColor;
import org.bukkit.Bukkit;
import org.bukkit.OfflinePlayer;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.entity.Player;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ThreadLocalRandom;
import java.util.regex.Pattern;

/**
 * [safety] Reports from headsets (cap {@code report_v1}, PROTOCOL.md "Chat safety: mute, block, report"; config.yml
 * section {@code reports}). Muting and blocking are the headset's own: it hides what a player says. A report goes to
 * this server's operators, who are the ones who can act on it (kick, ban, whitelist): it is appended to
 * {@code plugins/TabletopCraftAR/reports.log} with the reporting headset, the reason, the headset's note and the
 * reported player's recent chat, logged to the console and shown to online players with {@code tabletop.reports}. The
 * reported player isn't told. Recent chat is kept in memory per player (the newest {@code reports.context-lines}
 * within {@code reports.context-minutes}) only while reports are on, and written only into a report.
 */
final class ReportService {
    static final String CAP = "report_v1";
    static final int MAX_DETAILS = 200;
    /** The reasons headsets send; any other lower-case word is kept as given (a newer app's reason). */
    static final List<String> REASONS = List.of("harassment", "hate", "sexual", "threat", "spam", "cheating", "other");
    static final Pattern REASON = Pattern.compile("[a-z_]{1,24}");
    static final String LOG_NAME = "reports.log";
    /** Players whose chat is remembered at once (the least recently heard are dropped first). */
    static final int MAX_PLAYERS = 500;
    private static final DateTimeFormatter STAMP = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss xx").withZone(ZoneId.systemDefault());
    private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("HH:mm:ss").withZone(ZoneId.systemDefault());

    /** A report as the headset sent it (details already cleaned, null when there are none). */
    record Report(String uuid, String name, String reason, String details, boolean blocked) {}

    private final TabletopPlugin plugin;
    // Config, read on websocket threads (volatile).
    volatile boolean enabled = true;
    volatile int perMinute = 3, contextLines = 20;
    volatile long contextMs = 30 * 60_000L, maxLogBytes = 1024 * 1024L;
    final ChatContext context = new ChatContext();
    long reports;

    ReportService(TabletopPlugin plugin) { this.plugin = plugin; }

    void configure(FileConfiguration c) {
        enabled = c.getBoolean("reports.enabled", true);
        perMinute = Math.max(1, c.getInt("reports.max-per-minute", 3));
        contextLines = Math.max(0, Math.min(200, c.getInt("reports.context-lines", 20)));
        contextMs = Math.max(1, c.getInt("reports.context-minutes", 30)) * 60_000L;
        maxLogBytes = Math.max(16, c.getLong("reports.max-log-kb", 1024)) * 1024L;
        if (!enabled) context.clear();
    }

    boolean enabled() { return enabled; }

    /** A player said something (chat, say, me, or a whisper to the headsets): remembered for a report's context. Any thread. */
    void heard(Player p, String kind, String text) {
        if (!enabled || contextLines == 0 || p == null || text == null || text.isBlank()) return;
        context.heard(p.getUniqueId().toString(), p.getName(), kind, text, System.currentTimeMillis(), contextLines);
    }

    /** At most perMinute reports a minute per session (websocket thread). */
    boolean admit(Session s, long nowMs) {
        synchronized (s.reportTimes) { return admit(s.reportTimes, nowMs, perMinute); }
    }

    // ---------- the report (main thread) ----------
    void onReport(Session s, Report r, Long refSeq) {
        if (s.state != Session.State.READY) return;
        long now = System.currentTimeMillis();
        String id = String.format("%08x", ThreadLocalRandom.current().nextInt());
        UUID uuid = UUID.fromString(r.uuid());
        Player online = Bukkit.getPlayer(uuid);
        String known = online != null ? online.getName() : null;
        if (known == null) {
            OfflinePlayer op = Bukkit.getOfflinePlayer(uuid);   // the user cache only: no lookup
            if (op.hasPlayedBefore()) known = op.getName();
        }
        String name = known != null ? known : r.name() != null ? r.name() : "?";
        String reporter = reporter(s);
        String block = format(id, now, reporter, r, name, online != null ? "online" : known != null ? "offline" : "never seen on this server",
            context.recent(r.uuid(), now, contextMs));
        reports++;
        plugin.getLogger().warning("REPORT " + id + " from " + reporter + ": " + name + " (" + r.uuid() + ") for " + r.reason()
            + (r.details() != null ? ": " + r.details() : "") + "; details in plugins/" + plugin.getName() + "/" + LOG_NAME);
        Component note = Component.text("[Tabletop] Report " + id + ": " + reporter + " reported " + name + " for " + r.reason()
            + ". Details: plugins/" + plugin.getName() + "/" + LOG_NAME, NamedTextColor.GOLD);
        int ops = 0;
        for (Player p : Bukkit.getOnlinePlayers()) if (p.hasPermission("tabletop.reports") && !p.getUniqueId().equals(uuid)) { p.sendMessage(note); ops++; }
        int told = ops;
        Path file = plugin.getDataFolder().toPath().resolve(LOG_NAME);
        long max = maxLogBytes;
        Bukkit.getScheduler().runTaskAsynchronously(plugin, () -> {
            try {
                append(file, block, max);
                plugin.send(s, ack(refSeq == null ? -1 : refSeq, id, told));
            } catch (IOException e) {
                plugin.getLogger().warning("REPORT " + id + " could not be written to " + file + ": " + e.getMessage());
                plugin.send(s, error("internal", "the server could not save the report (" + e.getMessage() + "); its operators were told", refSeq));
            }
        });
    }

    /** Who reported: the headset's name, its paired device id, the session and the chat name (never its address). */
    static String reporter(Session s) {
        return "headset \"" + s.deviceName + "\"" + (s.deviceId != null ? " (device " + s.deviceId + ")" : " (not paired)")
            + " session " + s.id + " as " + s.operatorName;
    }

    // ---------- pure parts (unit-tested) ----------

    /** report (C->S). Throws IllegalArgumentException (bad_message) when malformed. */
    static Report parse(JsonObject m) {
        if (!m.has("player") || !m.get("player").isJsonObject()) throw new IllegalArgumentException("report needs player {uuid, name}");
        JsonObject p = m.getAsJsonObject("player");
        String uuid = str(p, "uuid");
        if (uuid == null) throw new IllegalArgumentException("report player needs a uuid");
        try { uuid = UUID.fromString(uuid).toString(); }
        catch (IllegalArgumentException e) { throw new IllegalArgumentException("report player uuid is not a UUID"); }
        String name = str(p, "name");
        if (name != null) {
            name = ChatText.message(name, 32);
            if (name != null) name = name.replace(' ', '_');
        }
        String reason = str(m, "reason");
        if (reason == null || !REASON.matcher(reason).matches()) throw new IllegalArgumentException("report reason must be a lower-case word such as " + String.join(", ", REASONS));
        String details = str(m, "details");
        if (details != null) {
            if (details.length() > MAX_DETAILS * 2) throw new IllegalArgumentException("report details are longer than " + MAX_DETAILS + " characters");
            String clean = ChatText.message(details, MAX_DETAILS);
            if (clean == null && !details.isBlank()) throw new IllegalArgumentException("report details are longer than " + MAX_DETAILS + " characters");
            details = clean;
        }
        boolean blocked = m.has("blocked") && m.get("blocked").isJsonPrimitive() && m.get("blocked").getAsBoolean();
        return new Report(uuid, name, reason, details, blocked);
    }

    /** report_ack (S->C): the report's id and how many online operators were shown it (0: it waits in reports.log). */
    static JsonObject ack(long refSeq, String id, int operators) {
        JsonObject o = Protocol.body("report_ack");
        o.addProperty("ref_seq", refSeq);
        o.addProperty("id", id);
        o.addProperty("operators", operators);
        return o;
    }

    /** An error about a report (op "report"). */
    static JsonObject error(String code, String message, Long refSeq) {
        JsonObject o = Protocol.error(code, message, refSeq, false);
        o.addProperty("op", "report");
        return o;
    }

    /** One report as it goes into reports.log. */
    static String format(String id, long nowMs, String reporter, Report r, String name, String seen, List<ChatContext.Line> lines) {
        StringBuilder b = new StringBuilder();
        b.append("=== report ").append(id).append("  ").append(STAMP.format(Instant.ofEpochMilli(nowMs))).append('\n');
        b.append("from:    ").append(reporter).append('\n');
        b.append("player:  ").append(name).append(' ').append(r.uuid()).append(" (").append(seen).append(")\n");
        b.append("reason:  ").append(r.reason()).append('\n');
        if (r.details() != null) b.append("note:    ").append(r.details()).append('\n');
        b.append("blocked on the headset: ").append(r.blocked() ? "yes" : "no").append('\n');
        if (lines.isEmpty()) b.append("recent chat from ").append(name).append(": none heard\n");
        else {
            b.append("recent chat from ").append(name).append(" (oldest first):\n");
            for (ChatContext.Line l : lines) b.append("  ").append(TIME.format(Instant.ofEpochMilli(l.ms()))).append(' ').append(l.line()).append('\n');
        }
        return b.append('\n').toString();
    }

    private static final Object FILE_LOCK = new Object();

    /** Appends a report; a log past maxBytes is first moved to reports.log.1 (replacing an older one). */
    static void append(Path file, String block, long maxBytes) throws IOException {
        synchronized (FILE_LOCK) {
            Files.createDirectories(file.getParent());
            if (Files.exists(file) && Files.size(file) + block.length() > maxBytes)
                Files.move(file, file.resolveSibling(file.getFileName() + ".1"), StandardCopyOption.REPLACE_EXISTING);
            Files.writeString(file, block, StandardCharsets.UTF_8, StandardOpenOption.CREATE, StandardOpenOption.APPEND);
        }
    }

    /** Sliding window: true (and counted) when fewer than perMinute of the times are within the last minute. */
    static boolean admit(Deque<Long> times, long nowMs, int perMinute) {
        while (!times.isEmpty() && nowMs - times.peekFirst() >= 60_000) times.pollFirst();
        if (times.size() >= perMinute) return false;
        times.addLast(nowMs);
        return true;
    }

    private static String str(JsonObject m, String k) { return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null; }

    /** The newest lines each player said, in memory only. Thread-safe (AsyncChatEvent runs off the main thread). */
    static final class ChatContext {
        record Line(long ms, String kind, String name, String text) {
            /** As the game shows it: "&lt;Alex&gt; hi", "[Alex] hi" (say), "* Alex waves" (me), "Alex whispers: hi". */
            String line() {
                return switch (kind) {
                    case "say" -> "[" + name + "] " + text;
                    case "me" -> "* " + name + " " + text;
                    case "whisper" -> name + " whispers to the headsets: " + text;
                    default -> "<" + name + "> " + text;
                };
            }
        }

        private final LinkedHashMap<String, Deque<Line>> byPlayer = new LinkedHashMap<>(16, 0.75f, true) {
            @Override protected boolean removeEldestEntry(Map.Entry<String, Deque<Line>> e) { return size() > MAX_PLAYERS; }
        };

        synchronized void heard(String uuid, String name, String kind, String text, long nowMs, int maxLines) {
            Deque<Line> d = byPlayer.computeIfAbsent(uuid, k -> new ArrayDeque<>());
            d.addLast(new Line(nowMs, kind, name, text.length() > 300 ? text.substring(0, 300) + "..." : text));
            while (d.size() > maxLines) d.pollFirst();
        }

        /** The lines of the last maxAgeMs, oldest first. */
        synchronized List<Line> recent(String uuid, long nowMs, long maxAgeMs) {
            Deque<Line> d = byPlayer.get(uuid);
            List<Line> l = new ArrayList<>();
            if (d != null) for (Line x : d) if (nowMs - x.ms() <= maxAgeMs) l.add(x);
            return l;
        }

        synchronized void clear() { byPlayer.clear(); }

        synchronized int players() { return byPlayer.size(); }
    }
}
