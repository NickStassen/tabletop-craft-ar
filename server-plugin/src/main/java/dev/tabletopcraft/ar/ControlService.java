package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import io.papermc.paper.event.player.AsyncChatEvent;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.format.NamedTextColor;
import net.kyori.adventure.text.format.TextDecoration;
import org.bukkit.Bukkit;
import org.bukkit.GameRule;
import org.bukkit.Location;
import org.bukkit.Registry;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.command.ConsoleCommandSender;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.PlayerDeathEvent;
import org.bukkit.event.player.PlayerAdvancementDoneEvent;
import org.bukkit.event.player.PlayerCommandPreprocessEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerQuitEvent;
import org.bukkit.event.server.BroadcastMessageEvent;
import org.bukkit.event.server.ServerCommandEvent;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.ConcurrentLinkedQueue;

/**
 * [talk] Chat, commands and server status for the headset (PROTOCOL.md 4.26-4.28, caps chat_v1, command_v1,
 * status_v1; config.yml sections chat, commands, status).
 * <ul>
 * <li><b>Chat in:</b> MONITOR handlers turn game chat, joins, quits, deaths, advancements, plugin broadcasts and
 *     /say, /me into {@code chat} messages. {@link AsyncChatEvent} runs off the main thread: it only reads the
 *     player's name, uuid and entity id and flattens the message; the JSON goes out on the outbound executor.</li>
 * <li><b>Chat out:</b> {@code chat_send} is broadcast as config {@code chat.format} with the sanitised operator name
 *     (a system message: no signing), echoed to every chat_v1 session as kind {@code operator}, and shown for
 *     {@code chat.bubble-seconds} above the giant's head.</li>
 * <li><b>Commands:</b> {@link CommandPolicy} on the websocket thread, then the main thread runs the line through
 *     {@code Server#createCommandSender} (operator level), so its feedback is captured; what arrives later (async
 *     plugin commands) within {@link #LATE_MS} goes out as {@code command_output}. Ops with {@code tabletop.notify}
 *     see a grey "[Tabletop: /line]" (the sender doesn't inform admins itself). say/me become chat.</li>
 * <li><b>Status:</b> {@code server_status} every {@code status.every-ticks}; {@code world_query} and
 *     {@code players_query} on request.</li>
 * </ul>
 */
final class ControlService implements Listener {
    static final long LATE_MS = 5000;
    static final double SLOW_COMMAND_MS = 50;

    private final TabletopPlugin plugin;
    // Config, read on websocket threads (volatile).
    volatile boolean chatEnabled = true, chatSend = true;
    volatile String chatFormat = ChatText.DEFAULT_FORMAT, meFormat = ChatText.DEFAULT_ME_FORMAT, defaultName = "Tabletop";
    volatile int chatMaxLen = 256, bubbleSeconds = 5;
    volatile double chatRate = 2, chatBurst = 5;
    volatile boolean commandsEnabled = true, requireToken = true, tokenSet, pairingOn = true, notifyOps = true;
    volatile int commandMaxLen = 256;
    volatile double commandRate = 2, commandBurst = 5, completeRate = 10;
    volatile CommandPolicy policy = new CommandPolicy(List.of(), CommandPolicy.DEFAULT_DENY, l -> l);
    volatile List<String> quick = List.of();
    volatile boolean statusEnabled = true;
    volatile int statusEveryTicks = 40;

    private boolean ownBroadcast;   // main thread: our own Bukkit.broadcast is running (its BroadcastMessageEvent isn't mirrored)
    private final ConcurrentLinkedQueue<Late> late = new ConcurrentLinkedQueue<>();
    long commandsRun, chatsSent, statusSent, statusBuilds, statusNanos;

    ControlService(TabletopPlugin plugin) { this.plugin = plugin; }

    void configure(FileConfiguration c) {
        chatEnabled = c.getBoolean("chat.enabled", true);
        chatSend = c.getBoolean("chat.send", true);
        chatFormat = c.getString("chat.format", ChatText.DEFAULT_FORMAT);
        meFormat = c.getString("chat.me-format", ChatText.DEFAULT_ME_FORMAT);
        defaultName = ChatText.name(c.getString("chat.default-name", "Tabletop"), "Tabletop");
        chatMaxLen = Math.max(1, Math.min(256, c.getInt("chat.max-length", 256)));
        chatRate = Math.max(0.1, c.getDouble("chat.max-per-second", 2));
        chatBurst = Math.max(1, c.getDouble("chat.burst", 5));
        bubbleSeconds = Math.max(0, c.getInt("chat.bubble-seconds", 5));
        commandsEnabled = c.getBoolean("commands.enabled", true);
        requireToken = c.getBoolean("commands.require-token", true);
        tokenSet = !c.getString("auth.token", "").isEmpty();
        pairingOn = c.getBoolean("auth.pairing", true);   // [setup]
        notifyOps = c.getBoolean("commands.notify-ops", true);
        commandMaxLen = Math.max(1, Math.min(32000, c.getInt("commands.max-length", 256)));
        commandRate = Math.max(0.1, c.getDouble("commands.max-per-second", 2));
        commandBurst = Math.max(1, c.getDouble("commands.burst", 5));
        completeRate = Math.max(1, c.getDouble("commands.completions-per-second", 10));
        List<String> deny = c.isList("commands.deny") ? c.getStringList("commands.deny") : CommandPolicy.DEFAULT_DENY;
        policy = new CommandPolicy(c.getStringList("commands.allow"), deny, ControlService::resolveCommand);
        quick = List.copyOf(c.getStringList("commands.quick"));
        statusEnabled = c.getBoolean("status.enabled", true);
        statusEveryTicks = Math.max(1, c.getInt("status.every-ticks", 40));
    }

    /** Alias resolver for CommandPolicy: the command's name, or null when there is no such command. */
    static String resolveCommand(String label) {
        Command cmd = Bukkit.getCommandMap().getCommand(label);
        return cmd == null ? null : cmd.getName();
    }

    boolean chatAllowed() { return chatEnabled; }
    /** command_v1: commands.enabled and, with commands.require-token (the default), an auth.token is set. */
    boolean commandsAllowed() { return commandsEnabled && (!requireToken || tokenSet || pairingOn); }
    /** [setup] command_v1 for this session: with commands.require-token only for trusted sessions (auth.token or paired). */
    boolean commandsAllowed(Session s) { return commandsEnabled && (!requireToken || s.trusted); }
    String commandsOffReason() {
        return !commandsEnabled ? "commands are off on this server (commands.enabled)" : "commands need a paired headset (/tabletop pair <code>) or auth.token (commands.require-token)";
    }
    boolean statusAllowed() { return statusEnabled; }

    /** welcome additions for the granted caps (PROTOCOL.md 4.26-4.27). */
    void welcome(Session s, JsonObject w) {
        if (s.wantsChat) w.add("chat", ControlMessages.welcomeChat(chatSend, s.operatorName, chatMaxLen));
        if (s.canCommand) w.add("commands", ControlMessages.welcomeCommands(quick, commandMaxLen));
    }

    // ---------- chat in (any thread) ----------
    /** Sends one chat message to every chat_v1 session (thread-safe: the outbound executor serialises). */
    void toChatSessions(JsonObject msg) {
        for (Session s : plugin.allSessions()) if (s.state == Session.State.READY && s.wantsChat) plugin.send(s, msg.deepCopy());
    }

    private boolean anyChat() {
        for (Session s : plugin.allSessions()) if (s.state == Session.State.READY && s.wantsChat) return true;
        return false;
    }

    private static ControlMessages.From from(Player p) {
        return new ControlMessages.From(p.getName(), p.getUniqueId().toString(), p.getEntityId(), null);
    }

    private static ControlMessages.From from(CommandSender s) {
        if (s instanceof Player p) return from(p);
        if (s instanceof ConsoleCommandSender) return ControlMessages.From.server();
        return new ControlMessages.From(s.getName(), null, null, null);
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onChat(AsyncChatEvent e) {
        if (!chatEnabled) return;
        String text = ChatText.plain(e.message());
        plugin.reports.heard(e.getPlayer(), "chat", text);   // [safety] a report's context
        if (anyChat()) toChatSessions(ControlMessages.chat("chat", from(e.getPlayer()), text, null));
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onJoin(PlayerJoinEvent e) { system("join", e.getPlayer(), e.joinMessage()); }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onQuit(PlayerQuitEvent e) { system("quit", e.getPlayer(), e.quitMessage()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onDeath(PlayerDeathEvent e) { system("death", e.getPlayer(), e.deathMessage()); }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onAdvancement(PlayerAdvancementDoneEvent e) { system("advancement", e.getPlayer(), e.message()); }

    /** Plugin broadcasts (Bukkit#broadcast). Our own chat_send broadcast isn't mirrored: it's echoed as "operator". */
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onBroadcast(BroadcastMessageEvent e) {
        if (ownBroadcast && Bukkit.isPrimaryThread()) return;
        if (!chatEnabled || !anyChat()) return;
        String text = ChatText.plain(e.message());
        if (!text.isBlank()) toChatSessions(ControlMessages.chat("broadcast", null, text, null));
    }

    /** /say and /me by a player who may run them (they fire no chat event). Best effort: selectors aren't expanded. */
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onPlayerCommand(PlayerCommandPreprocessEvent e) { mirrorSayMe(e.getPlayer(), e.getMessage()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onServerCommand(ServerCommandEvent e) { mirrorSayMe(e.getSender(), e.getCommand()); }

    private void mirrorSayMe(CommandSender sender, String line) {
        if (!chatEnabled) return;
        String root = CommandPolicy.rootLabel(line);
        if (!root.equals("say") && !root.equals("me")) return;
        if (sender instanceof Player p && !p.hasPermission("minecraft.command." + root)) return;
        String rest = rest(line);
        if (rest.isBlank()) return;
        if (sender instanceof Player p) plugin.reports.heard(p, root, rest);   // [safety]
        if (anyChat()) toChatSessions(ControlMessages.chat(root, from(sender), rest, null));
    }

    private static String rest(String line) {
        String s = CommandPolicy.strip(line);
        int sp = s.indexOf(' ');
        return sp < 0 ? "" : s.substring(sp + 1).trim();
    }

    private void system(String kind, Player p, Component msg) {
        if (!chatEnabled || msg == null || !anyChat()) return;
        String text = ChatText.plain(msg);
        if (!text.isBlank()) toChatSessions(ControlMessages.chat(kind, from(p), text, null));
    }

    /** /tabletop tell: a Java player (or the console) whispers to the headsets. */
    int tell(CommandSender sender, String text) {
        String t = ChatText.message(text, chatMaxLen);
        if (t == null) return -1;
        int n = 0;
        JsonObject msg = ControlMessages.chat("whisper", from(sender), t, null);
        if (sender instanceof Player p) plugin.reports.heard(p, "whisper", t);   // [safety]
        for (Session s : plugin.allSessions()) if (s.state == Session.State.READY && s.wantsChat) { plugin.send(s, msg.deepCopy()); n++; }
        plugin.getLogger().info(String.format("TELL %s -> %d headsets: %s", sender.getName(), n, t));
        return n;
    }

    // ---------- chat out (main thread) ----------
    void onChatSend(Session s, String raw, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.wantsChat || !chatSend) {
            plugin.send(s, Protocol.error("chat_denied", !s.wantsChat ? "ask for chat_v1 in hello.caps (chat.enabled)" : "sending chat is off on this server (chat.send)", refSeq, false));
            return;
        }
        String text = ChatText.message(raw, chatMaxLen);
        if (text == null) { plugin.send(s, Protocol.error("bad_message", "chat_send text is empty or longer than " + chatMaxLen + " characters", refSeq, false)); return; }
        say(s, text, false);
    }

    /** Broadcasts the operator's line, echoes it to the headsets and shows the bubble over the giant. */
    private void say(Session s, String text, boolean me) {
        Component line = ChatText.format(me ? meFormat : chatFormat, s.operatorName, text);
        ownBroadcast = true;
        try { Bukkit.broadcast(line); } finally { ownBroadcast = false; }
        String plain = ChatText.plain(line);
        chatsSent++;
        plugin.getLogger().info("CHAT session=" + s.id + " " + plain);
        toChatSessions(ControlMessages.chat("operator", new ControlMessages.From(s.operatorName, null, null, s.id), text, plain));
        if (bubbleSeconds > 0) plugin.giantSays(me ? "* " + text : text, bubbleSeconds);
    }

    // ---------- commands (main thread) ----------
    /** Feedback of one command: lines until the result is sent, then late output for LATE_MS. */
    private final class Capture implements java.util.function.Consumer<Component> {
        final Session s; final long refSeq;
        final List<String> lines = new ArrayList<>();
        boolean error, done;
        long until;
        Capture(Session s, long refSeq) { this.s = s; this.refSeq = refSeq; }

        @Override public synchronized void accept(Component c) {
            List<String> ls = ChatText.lines(ChatText.plain(c), ControlMessages.MAX_LINE_CHARS);
            if (!done) {
                if (ChatText.isError(c)) error = true;
                for (String l : ls) if (lines.size() < ControlMessages.MAX_LINES) lines.add(l);
                return;
            }
            if (System.currentTimeMillis() <= until) late.add(new Late(s, refSeq, ls));
        }

        synchronized List<String> finish() { done = true; until = System.currentTimeMillis() + LATE_MS; return List.copyOf(lines); }
    }

    private record Late(Session s, long refSeq, List<String> lines) {}

    void onCommand(Session s, ControlMessages.CommandRequest r, Long refSeq) {
        if (s.state != Session.State.READY) return;
        long ref = refSeq == null ? -1 : refSeq;
        if (!s.canCommand) { plugin.send(s, Protocol.error("command_denied", commandsAllowed(s) ? "ask for command_v1 in hello.caps" : commandsOffReason(), refSeq, false)); return; }
        CommandPolicy.Decision d = policy.check(r.line());
        if (!d.allowed()) {
            plugin.getLogger().info("CMD_DENIED session=" + s.id + " /" + CommandPolicy.strip(r.line()) + ": " + d.reason());
            plugin.send(s, Protocol.error("command_denied", d.reason(), refSeq, false));
            return;
        }
        String root = CommandPolicy.rootLabel(r.line());
        // say/me through the server's sender would read "[FeedbackForwardingSender] hi": send them as the giant's chat.
        if (root.equals("say") || root.equals("me")) {
            String text = ChatText.message(rest(r.line()), chatMaxLen);
            if (text == null) { plugin.send(s, Protocol.error("bad_message", "/" + root + " needs a message (at most " + chatMaxLen + " characters)", refSeq, false)); return; }
            say(s, text, root.equals("me"));
            plugin.send(s, ControlMessages.commandResult(ref, true, 0, List.of("(sent as chat)"), true));
            return;
        }
        String line = CommandPolicy.strip(r.line());
        if (r.hasAt()) {
            World w = Bukkit.getWorld(r.atWorld() != null ? r.atWorld() : s.region != null ? s.region.world() : plugin.defaultRegion().world());
            if (w == null) { plugin.send(s, Protocol.error("world_not_found", "no world " + r.atWorld(), refSeq, false)); return; }
            line = ControlMessages.dispatchLine(line, w.getKey().asString(), r.atPos());
        }
        Capture cap = new Capture(s, ref);
        CommandSender sender = Bukkit.createCommandSender(cap);
        long t0 = System.nanoTime();
        boolean found;
        try { found = Bukkit.dispatchCommand(sender, line); }
        catch (RuntimeException e) {
            // A syntax error from this sender comes out of dispatchCommand as a CommandException wrapping brigadier's
            // CommandSyntaxException ("Unknown or incomplete command...<--[HERE]"): report it like the game would.
            String syntax = syntaxError(e);
            found = syntax != null;
            cap.accept(Component.text(syntax != null ? syntax : "error: " + rootCause(e), NamedTextColor.RED));
            if (syntax == null) plugin.getLogger().log(java.util.logging.Level.WARNING, "CMD session=" + s.id + " /" + line + " threw", e);
        }
        double ms = (System.nanoTime() - t0) / 1e6;
        List<String> lines;
        boolean error;
        synchronized (cap) { error = cap.error; lines = cap.finish(); }
        if (!found && lines.isEmpty()) { lines = List.of("Unknown command: /" + root); error = true; }
        boolean ok = found && !error;
        commandsRun++;
        String log = String.format("CMD session=%s /%s %s (%.2f ms)%s", s.id, line, ok ? "ok" : "failed", ms, lines.isEmpty() ? "" : ": " + lines.get(0));
        if (ms > SLOW_COMMAND_MS) plugin.getLogger().warning(log + " [slow]"); else plugin.getLogger().info(log);
        plugin.send(s, ControlMessages.commandResult(ref, ok, ms, lines, false));
        if (notifyOps) {
            Component note = Component.text("[Tabletop: /" + CommandPolicy.strip(r.line()) + "]", NamedTextColor.GRAY, TextDecoration.ITALIC);
            for (Player p : Bukkit.getOnlinePlayers()) if (p.hasPermission("tabletop.notify")) p.sendMessage(note);
        }
    }

    /** The message of a brigadier CommandSyntaxException in the cause chain (no compile-time brigadier dependency), or null. */
    static String syntaxError(Throwable e) {
        for (Throwable t = e; t != null; t = t.getCause() == t ? null : t.getCause())
            if (t.getClass().getSimpleName().equals("CommandSyntaxException")) return t.getMessage();
        return null;
    }

    static String rootCause(Throwable e) {
        Throwable r = e;
        while (r.getCause() != null && r.getCause() != r) r = r.getCause();
        return r.getClass().getSimpleName() + (r.getMessage() == null ? "" : ": " + r.getMessage());
    }

    void onComplete(Session s, String line, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.canCommand) { plugin.send(s, Protocol.error("command_denied", commandsAllowed(s) ? "ask for command_v1 in hello.caps" : commandsOffReason(), refSeq, false)); return; }
        String bare = line.startsWith("/") ? line.substring(1) : line;
        List<String> raw;
        try { raw = Bukkit.getCommandMap().tabComplete(Bukkit.createCommandSender(c -> {}), bare); }
        catch (RuntimeException e) { raw = null; }
        plugin.send(s, ControlMessages.suggestions(refSeq == null ? -1 : refSeq, line, raw == null ? List.of() : raw));
    }

    // ---------- status, world, players (main thread) ----------
    void tick(long tickCount) {
        flushLate();
        if (!statusEnabled || tickCount % statusEveryTicks != 0) return;
        List<Session> to = new ArrayList<>();
        for (Session s : plugin.allSessions()) if (s.state == Session.State.READY && s.wantsStatus) to.add(s);
        if (to.isEmpty()) return;
        long t0 = System.nanoTime();
        JsonObject st = serverStatus();
        statusNanos += System.nanoTime() - t0;
        statusBuilds++;
        for (Session s : to) plugin.send(s, st.deepCopy());
        statusSent += to.size();
    }

    private void flushLate() {
        Late l;
        Session cs = null; long cr = 0; List<String> lines = null;
        while ((l = late.poll()) != null) {
            if (lines != null && (l.s() != cs || l.refSeq() != cr)) { sendLate(cs, cr, lines); lines = null; }
            if (lines == null) { cs = l.s(); cr = l.refSeq(); lines = new ArrayList<>(); }
            for (String x : l.lines()) if (lines.size() < ControlMessages.MAX_LINES) lines.add(x);
        }
        if (lines != null) sendLate(cs, cr, lines);
    }

    private void sendLate(Session s, long refSeq, List<String> lines) {
        if (s.state == Session.State.READY) plugin.send(s, ControlMessages.commandOutput(refSeq, lines));
    }

    static ControlMessages.TickStatus tickStatus() {
        var tm = Bukkit.getServerTickManager();
        return new ControlMessages.TickStatus(tm.getTickRate(), tm.isFrozen(), tm.isStepping(), tm.isSprinting());
    }

    static String env(World w) { return w.getEnvironment().name().toLowerCase(Locale.ROOT); }

    JsonObject serverStatus() {
        Runtime rt = Runtime.getRuntime();
        List<ControlMessages.WorldStatus> ws = new ArrayList<>();
        for (World w : Bukkit.getWorlds())
            ws.add(new ControlMessages.WorldStatus(w.getName(), env(w), w.getChunkCount(), w.getEntityCount(), w.getPlayerCount(), w.getTime(),
                w.getFullTime() / 24000, ControlMessages.weather(w.hasStorm(), w.isThundering())));
        return ControlMessages.serverStatus(Bukkit.getTPS(), Bukkit.getAverageTickTime(), (rt.totalMemory() - rt.freeMemory()) >> 20, rt.maxMemory() >> 20,
            Bukkit.getOnlinePlayers().size(), Bukkit.getMaxPlayers(), tickStatus(), ws);
    }

    void onWorldQuery(Session s, String world, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.wantsStatus) { plugin.send(s, Protocol.error("unsupported", "ask for status_v1 in hello.caps (status.enabled)", refSeq, false)); return; }
        String wn = world != null ? world : s.region != null ? s.region.world() : plugin.defaultRegion().world();
        World w = Bukkit.getWorld(wn);
        if (w == null) { plugin.send(s, Protocol.error("world_not_found", "no world " + wn, refSeq, false)); return; }
        plugin.send(s, ControlMessages.worldInfo(refSeq, w.getName(), env(w), w.getTime(), w.getFullTime() / 24000,
            ControlMessages.weather(w.hasStorm(), w.isThundering()), w.getDifficulty().name().toLowerCase(Locale.ROOT),
            spawn(w), tickStatus(), gameRules(w)));
    }

    /**
     * Every game rule of the world by the name the gamerule command takes (26.x: snake_case registry keys such as
     * advance_time; a datapack's rules keep their namespace), never hardcoded on the headset.
     */
    static Map<String, String> gameRules(World w) {
        Map<String, String> rules = new TreeMap<>();
        for (GameRule<?> rule : Registry.GAME_RULE) {
            try {
                Object v = w.getGameRuleValue(rule);
                if (v == null) continue;
                var k = rule.getKey();
                rules.put(k.getNamespace().equals("minecraft") ? k.getKey() : k.asString(), String.valueOf(v));
            } catch (RuntimeException ignored) { /* a rule this world's feature flags don't enable */ }
        }
        return rules;
    }

    private static int[] spawn(World w) {
        Location sp = w.getSpawnLocation();
        return new int[] { sp.getBlockX(), sp.getBlockY(), sp.getBlockZ() };
    }

    void onPlayersQuery(Session s, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.wantsStatus) { plugin.send(s, Protocol.error("unsupported", "ask for status_v1 in hello.caps (status.enabled)", refSeq, false)); return; }
        List<ControlMessages.PlayerRow> rows = new ArrayList<>();
        for (Player p : Bukkit.getOnlinePlayers()) {
            Location l = p.getLocation();
            AttributeInstance mh = p.getAttribute(Attribute.MAX_HEALTH);
            rows.add(new ControlMessages.PlayerRow(p.getName(), p.getUniqueId().toString(), p.getEntityId(), l.getWorld().getName(),
                new double[] { l.getX(), l.getY(), l.getZ() }, p.getGameMode().name().toLowerCase(Locale.ROOT), p.getHealth(),
                mh != null ? mh.getValue() : 20, p.getFoodLevel(), p.getPing(), p.isOp()));
        }
        rows.sort((a, b) -> a.name().compareToIgnoreCase(b.name()));
        plugin.send(s, ControlMessages.playersInfo(refSeq, rows));
    }

    String stats() {
        return String.format(Locale.ROOT, "chat sent %d, commands run %d, status messages %d (%.3f ms main thread each)", chatsSent, commandsRun,
            statusSent, statusBuilds == 0 ? 0 : statusNanos / 1e6 / statusBuilds);
    }

    Collection<String> denyList() { return policy.denyList(); }
}
