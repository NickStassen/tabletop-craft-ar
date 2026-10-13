package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import org.java_websocket.WebSocket;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicInteger;

/** Per-connection state: AWAIT_HELLO -> READY -> CLOSED, subscription, palette and seq counter. */
public final class Session {
    public enum State { AWAIT_HELLO, READY, CLOSED }

    public final WebSocket conn;
    public final String id = UUID.randomUUID().toString().substring(0, 8);
    public final long openedMs = System.currentTimeMillis();
    public volatile State state = State.AWAIT_HELLO;
    public volatile long lastInboundMs = openedMs;
    public volatile String client = "?";
    private final AtomicInteger seq = new AtomicInteger();

    // Main-thread-only fields below.
    public RegionSpec region;
    public int snapshotId;
    public int[] lastGrid;                 // global palette ids, as last sent
    public final List<String> palette = new ArrayList<>();
    private final Map<Integer, Integer> globalToLocal = new HashMap<>();
    public final Set<UUID> skinSent = new HashSet<>();
    public boolean lastPlayersNonEmpty;
    /** True while following the server's shared region (default); false after a private subscribe. */
    public volatile boolean shared = true;

    public Session(WebSocket conn) { this.conn = conn; }

    /** Adds envelope seq + t and sends. Thread-safe (Java-WebSocket send is thread-safe). */
    public void send(JsonObject body) {
        if (!conn.isOpen()) return;
        body.addProperty("seq", seq.getAndIncrement() & 0xffffffffL);
        body.addProperty("t", System.currentTimeMillis());
        conn.send(Protocol.GSON.toJson(body));
    }

    /** Like send, returns the UTF-8 byte count sent. */
    public int sendCounted(JsonObject body) {
        if (!conn.isOpen()) return 0;
        body.addProperty("seq", seq.getAndIncrement() & 0xffffffffL);
        body.addProperty("t", System.currentTimeMillis());
        byte[] b = Protocol.GSON.toJson(body).getBytes(java.nio.charset.StandardCharsets.UTF_8);
        conn.send(b.length <= Protocol.MAX_OUTBOUND_BYTES ? new String(b, java.nio.charset.StandardCharsets.UTF_8) : Protocol.GSON.toJson(Protocol.error("region_too_large", "snapshot " + b.length + " bytes exceeds 4 MiB", null, false)));
        return b.length;
    }

    public void resetPalette() { palette.clear(); globalToLocal.clear(); }

    /** Returns the session palette index for a global id, appending to palette (and to added) if new. */
    public int localIndex(int globalId, Palette global, List<String> added) {
        Integer l = globalToLocal.get(globalId);
        if (l != null) return l;
        int idx = palette.size();
        String s = global.state(globalId);
        palette.add(s);
        globalToLocal.put(globalId, idx);
        if (added != null) added.add(s);
        return idx;
    }
}
