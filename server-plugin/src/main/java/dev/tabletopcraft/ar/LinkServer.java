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
                    plugin.offerPose(s, head, Pose.parse(m.get("left")), Pose.parse(m.get("right")), refSeq == null ? -1 : refSeq);
                }
                case "region_request" -> {
                    RegionRequest q = RegionRequest.parse(m);
                    plugin.enqueue(() -> plugin.onRegionRequest(s, q, refSeq));
                }
                case "region_query" -> {
                    String player = m.has("player") && !m.get("player").isJsonNull() ? m.get("player").getAsString() : null;
                    plugin.enqueue(() -> plugin.onRegionQuery(s, player, refSeq));
                }
                case "avatar_config" -> {
                    AvatarConfig cfg = AvatarConfig.parse(m);
                    plugin.enqueue(() -> plugin.onAvatarConfig(s, cfg, refSeq));
                }
                case "ping" -> {
                    JsonObject p = Protocol.body("pong");
                    if (m.has("id")) p.add("id", m.get("id"));
                    if (m.has("t")) p.add("echo_t", m.get("t"));
                    s.send(p);
                }
                case "pong" -> { /* server-initiated pings are not used in v0.1 */ }
                case "bye" -> s.conn.close(1000, "bye");
                case "error" -> plugin.getLogger().warning("Client error from " + s.client + ": " + m);
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
        if (!token.isEmpty() && !token.equals(t)) { fatal(s, "auth_failed", "bad or missing token", refSeq, Protocol.CLOSE_AUTH); return; }
        s.client = m.has("client") ? m.get("client").getAsString() : "unknown";
        if (m.has("caps") && m.get("caps").isJsonArray())
            for (var e : m.getAsJsonArray("caps")) {
                if (!e.isJsonPrimitive()) continue;
                String c = e.getAsString();
                if (c.equals(Protocol.CAP_LIGHT)) s.wantsLight = plugin.lightEnabled();
                else if (c.equals(Protocol.CAP_BIOMES)) s.wantsBiomes = plugin.biomesEnabled();
            }
        s.state = Session.State.READY;
        plugin.getLogger().info("Hello from " + s.client + " (session " + s.id + ") @ " + s.conn.getRemoteSocketAddress());
        plugin.enqueue(() -> plugin.onReady(s));
    }

    private void fatal(Session s, String code, String msg, Long refSeq, int closeCode) {
        s.send(Protocol.error(code, msg, refSeq, true));
        s.conn.close(closeCode, code);
    }

    @Override public void onError(WebSocket conn, Exception ex) { plugin.getLogger().log(Level.WARNING, "Websocket error", ex); }

    @Override public void onStart() { plugin.getLogger().info("Link listening on ws://" + getAddress().getHostString() + ":" + getPort() + path); }
}
