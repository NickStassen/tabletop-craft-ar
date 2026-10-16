package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.java_websocket.WebSocket;
import org.java_websocket.handshake.ClientHandshake;
import org.java_websocket.server.WebSocketServer;

import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.util.Collection;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.logging.Level;

/**
 * Websocket endpoint (ARCHITECTURE.md 3.6). Runs on Java-WebSocket's threads: it only parses and validates.
 * Poses go into a latest-value slot; everything that touches the world is queued for the main thread.
 */
public final class LinkServer extends WebSocketServer {
    private final TabletopPlugin plugin;
    private final String token;
    private final String path;
    private final Map<WebSocket, Session> sessions = new ConcurrentHashMap<>();

    public LinkServer(TabletopPlugin plugin, String host, int port, String path, String token) {
        super(new InetSocketAddress(host, port));
        this.plugin = plugin;
        this.token = token == null ? "" : token;
        this.path = path == null || path.isEmpty() ? "/tabletop" : path;
        setReuseAddr(true);
        setConnectionLostTimeout(0); // idle handling is done by the plugin (close 4004)
    }

    public Collection<Session> sessions() { return sessions.values(); }

    @Override public void onOpen(WebSocket conn, ClientHandshake hs) {
        String desc = hs.getResourceDescriptor();
        String p = desc == null ? "/" : desc.split("\\?", 2)[0];
        if (!p.equals(path)) {
            plugin.getLogger().warning("Rejecting connection to path " + p + " (expected " + path + ")");
            conn.close(1008, "wrong path, use " + path);
            return;
        }
        sessions.put(conn, new Session(conn));
        plugin.getLogger().info("Link connected: " + conn.getRemoteSocketAddress());
    }

    @Override public void onClose(WebSocket conn, int code, String reason, boolean remote) {
        Session s = sessions.remove(conn);
        if (s == null) return;
        s.state = Session.State.CLOSED;
        plugin.getLogger().info("Link closed: " + s.client + " " + conn.getRemoteSocketAddress() + " code=" + code + " " + reason);
        plugin.enqueue(() -> plugin.onSessionClosed(s));
    }

    @Override public void onMessage(WebSocket conn, ByteBuffer bytes) {
        Session s = sessions.get(conn);
        if (s != null) s.send(Protocol.error("unsupported", "binary frames are not supported in v1", null, false));
    }

    @Override public void onMessage(WebSocket conn, String text) {
        Session s = sessions.get(conn);
        if (s == null) return;
        s.lastInboundMs = System.currentTimeMillis();
        if (text.length() > Protocol.MAX_INBOUND_CHARS) {
            s.send(Protocol.error("bad_message", "frame larger than 64 KiB", null, false));
            return;
        }
        JsonObject m;
        try { m = JsonParser.parseString(text).getAsJsonObject(); }
        catch (Exception e) { s.send(Protocol.error("bad_message", "not a JSON object", null, false)); return; }
        Long refSeq = m.has("seq") && m.get("seq").isJsonPrimitive() ? m.get("seq").getAsLong() : null;
        try {
            int v = m.has("v") ? m.get("v").getAsInt() : -1;
            String type = m.has("type") ? m.get("type").getAsString() : "";
            if (v != Protocol.VERSION && !type.equals("hello")) {
                fatal(s, "version_mismatch", "server speaks v" + Protocol.VERSION, refSeq, Protocol.CLOSE_VERSION);
                return;
            }
            if (s.state == Session.State.AWAIT_HELLO && !type.equals("hello")) {
                s.send(Protocol.error("bad_message", "send hello first", refSeq, false));
                return;
            }
            // [setup] waiting for /tabletop pair: nothing but keep-alive, goodbye and a fresh code
            if (s.state == Session.State.PAIRING && !(type.equals("ping") || type.equals("pong") || type.equals("bye") || type.equals("pair_request"))) {
                s.send(Protocol.error("bad_message", "pair this headset first (/tabletop pair <code> on the server)", refSeq, false));
                return;
            }
            switch (type) {
                case "hello" -> onHello(s, m, refSeq);
                case "subscribe" -> {
                    String world = m.has("world") ? m.get("world").getAsString() : null;
                    var o = m.getAsJsonArray("origin");
                    var d = m.getAsJsonArray("dims");
                    if (o == null || o.size() != 3 || d == null || d.size() != 3) throw new IllegalArgumentException("subscribe needs origin[3] and dims[3]");
                    int[] origin = { o.get(0).getAsInt(), o.get(1).getAsInt(), o.get(2).getAsInt() };
                    int[] dims = { d.get(0).getAsInt(), d.get(1).getAsInt(), d.get(2).getAsInt() };
                    boolean follow = m.has("follow") && !m.get("follow").isJsonNull();
                    plugin.enqueue(() -> plugin.onSubscribe(s, world, origin, dims, refSeq, follow));
                }
                case "pose" -> {
                    Pose head = Pose.parse(m.get("head"));
                    if (head == null) throw new IllegalArgumentException("pose.head required");
                    // Optional snapshot_id: the snapshot whose origin the tabletop frame refers to (PROTOCOL.md 4.7).
                    int snap = m.has("snapshot_id") && m.get("snapshot_id").isJsonPrimitive() ? m.get("snapshot_id").getAsInt() : -1;
                    plugin.offerPose(s, head, Pose.parse(m.get("left")), Pose.parse(m.get("right")), refSeq == null ? -1 : refSeq, snap);
                }
                case "region_request" -> {
                    RegionRequest q = RegionRequest.parse(m);
                    int[] margin = RegionRequest.viewMargin(m);
                    plugin.enqueue(() -> plugin.onRegionRequest(s, q, margin, refSeq));
                }
                case "region_query" -> {
                    String player = m.has("player") && !m.get("player").isJsonNull() ? m.get("player").getAsString() : null;
                    plugin.enqueue(() -> plugin.onRegionQuery(s, player, refSeq));
                }
                case "avatar_config" -> {
                    AvatarConfig cfg = AvatarConfig.parse(m);
                    plugin.enqueue(() -> plugin.onAvatarConfig(s, cfg, refSeq));
                }
                case "block_edit" -> {   // PROTOCOL.md 4.20; rate-limited here so a flood never reaches the main thread
                    BlockEdit e = BlockEdit.parse(m);
                    Long group = PowerMessages.group(m);   // [power] one trigger hold = one undo group
                    EditLimiter lim = s.editLimiter;
                    if (s.canEdit && lim != null && !lim.tryAcquire(System.nanoTime())) {
                        s.send(e.error("rate_limited", "more than " + plugin.editRate() + " edits per second", refSeq));
                        return;
                    }
                    plugin.enqueue(() -> plugin.power.journalled(s, group, "edit", e.x(), e.y(), e.z(), 1, 1, () -> plugin.onBlockEdit(s, e, refSeq)));
                }
                // [power] undo/redo, box edits, brushes (power_v1): parsed and rate-limited here, run as budgeted jobs
                case "undo", "redo" -> {
                    if (!plugin.power.admit(s)) { s.send(PowerMessages.error("rate_limited", "too many " + type + "s per second", refSeq, type, null, null)); return; }
                    boolean redo = type.equals("redo");
                    plugin.enqueue(() -> plugin.power.onUndo(s, redo, refSeq));
                }
                case "box_edit" -> {
                    PowerMessages.BoxEdit e = PowerMessages.boxEdit(m);
                    if (!plugin.power.admit(s)) { s.send(e.error("rate_limited", "too many box edits per second", refSeq)); return; }
                    plugin.enqueue(() -> plugin.power.onBoxEdit(s, e, refSeq));
                }
                case "brush" -> {
                    PowerMessages.Brush b = PowerMessages.brush(m, Math.max(0, plugin.power.brushMaxRadius));
                    plugin.enqueue(() -> plugin.power.onBrush(s, b, refSeq));
                }
                // [talk] chat, commands, status (PROTOCOL.md 4.26-4.28): parsed and rate-limited here, run on the main thread
                case "chat_send" -> {
                    String said = ControlMessages.chatSendText(m);
                    if (!limited(s, s.chatLimiter, "chat messages", refSeq)) plugin.enqueue(() -> plugin.control.onChatSend(s, said, refSeq));
                }
                case "command" -> {
                    ControlMessages.CommandRequest r = ControlMessages.command(m, plugin.control.commandMaxLen);
                    if (!limited(s, s.commandLimiter, "commands", refSeq)) plugin.enqueue(() -> plugin.control.onCommand(s, r, refSeq));
                }
                case "command_complete" -> {
                    String line = ControlMessages.completeLine(m, plugin.control.commandMaxLen);
                    if (!limited(s, s.completeLimiter, "completions", refSeq)) plugin.enqueue(() -> plugin.control.onComplete(s, line, refSeq));
                }
                case "world_query" -> {
                    String world = m.has("world") && m.get("world").isJsonPrimitive() ? m.get("world").getAsString() : null;
                    if (!limited(s, s.completeLimiter, "queries", refSeq)) plugin.enqueue(() -> plugin.control.onWorldQuery(s, world, refSeq));
                }
                case "players_query" -> { if (!limited(s, s.completeLimiter, "queries", refSeq)) plugin.enqueue(() -> plugin.control.onPlayersQuery(s, refSeq)); }
                // [hands] using blocks, items and mobs (interact_v1): parsed and rate-limited here, applied on the main thread
                case "interact" -> {
                    InteractMessages.Interact in = InteractMessages.interact(m);
                    Long group = PowerMessages.group(m);   // [power] journalled for undo like block_edit
                    if (s.canInteract && s.interactLimiter != null && !s.interactLimiter.tryAcquire(System.nanoTime())) {
                        s.send(in.error("rate_limited", "more than " + (int) plugin.interact.rate + " uses per second (interact.max-per-second)", refSeq));
                        return;
                    }
                    int[] win = PowerService.window(in.item());
                    plugin.enqueue(() -> plugin.power.journalled(s, group, "interact", in.x(), in.y(), in.z(), win[0], win[1], () -> plugin.interact.onInteract(s, in, refSeq)));
                }
                case "entity_op" -> {
                    InteractMessages.EntityOp op = InteractMessages.entityOp(m);
                    // Holding sends up to 10 moves a second: its own bucket, so it never starves the other actions.
                    EditLimiter lim = op.op().equals("hold") || op.op().equals("drop") ? s.holdLimiter : s.interactLimiter;
                    if (s.canInteract && lim != null && !lim.tryAcquire(System.nanoTime())) {
                        s.send(op.error("rate_limited", "too many mob actions per second (interact.max-per-second)", refSeq));
                        return;
                    }
                    plugin.enqueue(() -> plugin.interact.onEntityOp(s, op, refSeq));
                }
                // [sound] asset delivery (PROTOCOL.md "Asset delivery"): checked here, read and paced on the asset worker
                case "asset_get" -> plugin.assets.onGet(s, m, refSeq);
                // [operator] bookmarks (marks_v1), container view and editing, sign editing (interact_v1): parsed and
                // rate-limited here (bookmarks share the query bucket, containers and signs the interact bucket), run on the main thread
                case "bookmark" -> {
                    OperatorMessages.BookmarkOp b = OperatorMessages.bookmark(m);
                    if (!limited(s, s.completeLimiter, "bookmark changes", refSeq)) plugin.enqueue(() -> plugin.operator.onBookmark(s, b, refSeq));
                }
                case "container_query" -> {
                    int[] p = OperatorMessages.containerPos(m, "container_query");
                    if (!limited(s, s.canInteract ? s.interactLimiter : null, "container views", refSeq)) plugin.enqueue(() -> plugin.operator.onContainerQuery(s, p, refSeq));
                }
                case "container_edit" -> {
                    OperatorMessages.ContainerEdit e = OperatorMessages.containerEdit(m);
                    Long group = PowerMessages.group(m);   // [power] journalled for undo (PowerService.journalledTiles)
                    if (!limited(s, s.canInteract ? s.interactLimiter : null, "container edits", refSeq))
                        plugin.enqueue(() -> plugin.power.journalledTiles(s, group, "container", e.pos(), () -> plugin.operator.onContainerEdit(s, e, refSeq)));
                }
                case "container_close" -> {
                    int[] p = OperatorMessages.containerPos(m, "container_close");
                    plugin.enqueue(() -> plugin.operator.onContainerClose(s, p));
                }
                case "sign_edit" -> {
                    OperatorMessages.SignEdit e = OperatorMessages.signEdit(m);
                    Long group = PowerMessages.group(m);   // [power] journalled for undo (PowerService.journalledTiles)
                    if (!limited(s, s.canInteract ? s.interactLimiter : null, "sign edits", refSeq))
                        plugin.enqueue(() -> plugin.power.journalledTiles(s, group, "sign", e.pos(), () -> plugin.operator.onSignEdit(s, e, refSeq)));
                }
                // [safety] a report about a player to this server's operators (PROTOCOL.md "Chat safety"): checked and
                // rate-limited here, logged and announced on the main thread, written to reports.log off it
                case "report" -> {
                    ReportService rs = plugin.reports;
                    if (!s.canReport) { s.send(ReportService.error("report_denied", rs.enabled() ? "ask for report_v1 in hello.caps" : "reports are off on this server (reports.enabled)", refSeq)); return; }
                    ReportService.Report r;
                    try { r = ReportService.parse(m); }
                    catch (IllegalArgumentException e) { s.send(ReportService.error("bad_message", e.getMessage(), refSeq)); return; }
                    if (!rs.admit(s, System.currentTimeMillis())) { s.send(ReportService.error("rate_limited", "more than " + rs.perMinute + " reports a minute (reports.max-per-minute)", refSeq)); return; }
                    plugin.enqueue(() -> rs.onReport(s, r, refSeq));
                }
                case "ping" -> {
                    JsonObject p = Protocol.body("pong");
                    if (m.has("id")) p.add("id", m.get("id"));
                    if (m.has("t")) p.add("echo_t", m.get("t"));
                    s.send(p);
                }
                case "pong" -> { /* server-initiated pings are not used in v0.1 */ }
                case "pair_request" -> onPairRequest(s, m, refSeq);   // [setup] PROTOCOL.md "Pairing"
                case "bye" -> s.conn.close(1000, "bye");
                case "error" -> {
                    plugin.getLogger().warning("Client error from " + s.client + ": " + m);
                    // The client couldn't apply a snapshot (e.g. a delta against a snapshot it doesn't have): resend in full.
                    if (m.has("code") && "bad_snapshot".equals(m.get("code").getAsString())) plugin.enqueue(() -> plugin.onBadSnapshot(s));
                }
                default -> s.send(Protocol.error("unknown_type", "unknown type: " + type, refSeq, false));
            }
        } catch (RuntimeException e) {
            s.send(Protocol.error("bad_message", String.valueOf(e.getMessage()), refSeq, false));
        }
    }

    private void onHello(Session s, JsonObject m, Long refSeq) {
        if (s.state != Session.State.AWAIT_HELLO) { s.send(Protocol.error("bad_message", "duplicate hello", refSeq, false)); return; }
        boolean speaks = false;
        if (m.has("protocols") && m.get("protocols").isJsonArray())
            for (var e : m.getAsJsonArray("protocols")) if (e.getAsInt() == Protocol.VERSION) speaks = true;
        if (!speaks) { fatal(s, "version_mismatch", "server speaks protocols [" + Protocol.VERSION + "]", refSeq, Protocol.CLOSE_VERSION); return; }
        String t = m.has("token") && !m.get("token").isJsonNull() ? m.get("token").getAsString() : "";
        boolean tokenOk = !token.isEmpty() && tokenMatches(token, t);
        // [setup] Pairing (PROTOCOL.md "Pairing"): a paired headset's device token counts like the shared token.
        PairingService pairing = plugin.pairing;
        s.pairCap = pairing.enabled() && hasCap(m, PairingService.CAP);
        s.deviceName = PairingService.cleanName(str(m, "device_name"));
        String dt = str(m, "device_token");
        PairingService.Device d = dt == null || dt.isEmpty() ? null : pairing.check(dt);
        s.deviceTokenRejected = dt != null && !dt.isEmpty() && d == null;
        if (d != null) { s.deviceId = d.id(); s.devicePlayer = d.player(); }
        s.trusted = tokenOk || d != null;
        boolean closed = !token.isEmpty() || pairing.required();
        if (closed && !s.trusted) {
            if (s.pairCap) { s.hello = m; s.state = Session.State.PAIRING; sendCode(s, true, refSeq); return; }
            fatal(s, "auth_failed", token.isEmpty() ? "this server pairs its headsets: update the app to v1.1.0 or newer" : "bad or missing token", refSeq, Protocol.CLOSE_AUTH);
            return;
        }
        finishHello(s, m);
    }

    /** [setup] The rest of hello, once the session may have the world: caps, limits, READY. Also after /tabletop pair. */
    void finishHello(Session s, JsonObject m) {
        s.hello = null;
        s.client = m.has("client") ? m.get("client").getAsString() : "unknown";
        if (m.has("caps") && m.get("caps").isJsonArray()) {
            java.util.Set<String> caps = new java.util.HashSet<>();
            for (var e : m.getAsJsonArray("caps")) if (e.isJsonPrimitive()) caps.add(e.getAsString());
            s.caps = java.util.Set.copyOf(caps);
        }
        boolean wantsPower = false;   // [power] granted after the loop: it needs edit_v1, which may come later in the list
        if (m.has("caps") && m.get("caps").isJsonArray())
            for (var e : m.getAsJsonArray("caps")) {
                if (!e.isJsonPrimitive()) continue;
                String c = e.getAsString();
                if (c.equals(Protocol.CAP_LIGHT)) s.wantsLight = plugin.lightEnabled();
                else if (c.equals(Protocol.CAP_BIOMES)) s.wantsBiomes = plugin.biomesEnabled();
                else if (c.equals(Protocol.CAP_ENTITIES)) s.wantsEntities = plugin.entitiesEnabled();
                else if (c.equals(Protocol.CAP_ACTIONS)) s.wantsActions = plugin.actionsEnabled();
                else if (c.equals(Protocol.CAP_BLOCK_ENTITIES)) s.wantsBlockEntities = plugin.blockEntitiesEnabled();   // [blocks]
                else if (c.equals(Protocol.CAP_EDIT) && plugin.editAllowed(s)) { s.editLimiter = new EditLimiter(plugin.editRate()); s.canEdit = true; }
                // [talk] PROTOCOL.md 4.26-4.28
                else if (c.equals(ControlMessages.CAP_CHAT) && plugin.control.chatAllowed()) s.wantsChat = true;
                else if (c.equals(ControlMessages.CAP_COMMAND) && plugin.control.commandsAllowed(s)) s.canCommand = true;
                else if (c.equals(ControlMessages.CAP_STATUS) && plugin.control.statusAllowed()) s.wantsStatus = true;
                else if (c.equals(InteractMessages.CAP) && plugin.interact.allowed(s)) plugin.interact.grant(s);   // [hands]
                else if (c.equals(PowerMessages.CAP)) wantsPower = true;
                else if (c.equals(Protocol.CAP_WORLD)) s.wantsWorld = plugin.worldStateEnabled();   // [living] world_state
                else if (c.equals(Protocol.CAP_SOUND)) s.wantsSound = plugin.soundEnabled();        // [sound]
                else if (c.equals(AssetService.CAP)) plugin.assets.grant(s);   // [sound] only with auth.token set, from the LAN
                else if (c.equals(OperatorMessages.CAP_MARKS) && plugin.operator.marksAllowed(s)) s.wantsMarks = true;   // [operator]
                else if (c.equals(ReportService.CAP) && plugin.reports.enabled()) s.canReport = true;   // [safety]
            }
        if (wantsPower && s.canEdit && plugin.power.allowed(s)) plugin.power.grant(s);   // [power]
        ControlService cs = plugin.control;   // [talk] rate limits (token buckets) and the chat name
        s.chatLimiter = new EditLimiter(cs.chatRate, cs.chatBurst);
        s.commandLimiter = new EditLimiter(cs.commandRate, cs.commandBurst);
        s.completeLimiter = new EditLimiter(cs.completeRate, cs.completeRate);
        s.operatorName = ChatText.name(m.has("operator_name") && m.get("operator_name").isJsonPrimitive() ? m.get("operator_name").getAsString() : null,
            s.devicePlayer != null ? s.devicePlayer : cs.defaultName);   // [setup] a paired headset's player
        s.state = Session.State.READY;
        plugin.getLogger().info("Hello from " + s.client + " (session " + s.id + ") @ " + s.conn.getRemoteSocketAddress());
        plugin.enqueue(() -> plugin.onReady(s));
    }

    /** [setup] pair_request: a fresh code for a headset without a credential (READY on an open server, or PAIRING). */
    private void onPairRequest(Session s, JsonObject m, Long refSeq) {
        if (!s.pairCap) { s.send(pairError("unsupported", plugin.pairing.enabled() ? "ask for pair_v1 in hello.caps" : "pairing is off on this server (auth.pairing)", refSeq, false)); return; }
        if (s.deviceId != null) { s.send(pairError("pair_denied", "this headset is already paired with this server", refSeq, false)); return; }
        if (m.has("device_name") && m.get("device_name").isJsonPrimitive()) s.deviceName = PairingService.cleanName(m.get("device_name").getAsString());
        sendCode(s, s.state == Session.State.PAIRING, refSeq);
    }

    /** [setup] Ask PairingService for a code and show it on the headset (never on the server: typing it proves you see the headset). */
    private void sendCode(Session s, boolean required, Long refSeq) {
        String addr = s.conn.getRemoteSocketAddress() instanceof InetSocketAddress isa ? isa.getAddress().getHostAddress() : "?";
        long now = System.currentTimeMillis();
        PairingService.Pending p = plugin.pairing.request(s, s.deviceName, addr, now);
        if (p == null) {
            String why = plugin.pairing.refusal(addr, now);
            plugin.getLogger().info("PAIR refused for " + addr + ": " + why);
            if (required) { s.send(pairError("pair_denied", why, refSeq, true)); s.conn.close(Protocol.CLOSE_AUTH, "pair_denied"); }
            else s.send(pairError("pair_denied", why, refSeq, false));
            return;
        }
        s.send(PairingService.codeMessage(p, required, s.deviceTokenRejected));
        plugin.enqueue(() -> plugin.announcePairing(s.deviceName, addr));
    }

    static JsonObject pairError(String code, String message, Long refSeq, boolean fatal) {
        JsonObject o = Protocol.error(code, message, refSeq, fatal);
        o.addProperty("op", "pair_request");
        return o;
    }

    private static boolean hasCap(JsonObject m, String cap) {
        if (!m.has("caps") || !m.get("caps").isJsonArray()) return false;
        for (var e : m.getAsJsonArray("caps")) if (e.isJsonPrimitive() && e.getAsString().equals(cap)) return true;
        return false;
    }

    private static String str(JsonObject m, String k) { return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null; }

    /** [talk] True (and an error rate_limited sent) when the session's bucket is empty. */
    private boolean limited(Session s, EditLimiter lim, String what, Long refSeq) {
        if (lim == null || lim.tryAcquire(System.nanoTime())) return false;
        s.send(Protocol.error("rate_limited", "too many " + what + " per second", refSeq, false));
        return true;
    }

    /** Constant-time token check: both sides hashed (no length leak), then MessageDigest.isEqual. */
    static boolean tokenMatches(String expected, String given) {
        try {
            var sha = java.security.MessageDigest.getInstance("SHA-256");
            byte[] a = sha.digest(expected.getBytes(java.nio.charset.StandardCharsets.UTF_8));
            byte[] b = sha.digest((given == null ? "" : given).getBytes(java.nio.charset.StandardCharsets.UTF_8));
            return java.security.MessageDigest.isEqual(a, b);
        } catch (java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }   // every JVM has SHA-256
    }

    private void fatal(Session s, String code, String msg, Long refSeq, int closeCode) {
        s.send(Protocol.error(code, msg, refSeq, true));
        s.conn.close(closeCode, code);
    }

    @Override public void onError(WebSocket conn, Exception ex) { plugin.getLogger().log(Level.WARNING, "Websocket error", ex); }

    @Override public void onStart() {
        plugin.getLogger().info("Link listening on ws://" + getAddress().getHostString() + ":" + getPort() + path);
        // ws://0.0.0.0 isn't something to type into the headset: list the addresses it can actually use.
        var urls = LanBeacon.headsetUrls(getAddress().getHostString(), getPort(), path, LanBeacon.localNics());
        for (String u : urls) plugin.getLogger().info("Headset URL: " + u);
        if (urls.isEmpty()) plugin.getLogger().info("Headset URL: ws://<this PC's LAN IPv4>:" + getPort() + path + " (no private IPv4 found)");
    }
}
