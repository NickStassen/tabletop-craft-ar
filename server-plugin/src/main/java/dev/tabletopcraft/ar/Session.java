package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import org.java_websocket.WebSocket;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicInteger;

/** Per-connection state: AWAIT_HELLO -> READY -> CLOSED, subscription, palette and seq counter. */
public final class Session {
    /** [nav] region_requests this session sent; the server applies only the newest one's read (TabletopPlugin). Main thread. */
    long regionRequests;
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
    // The session palette and its map are used only on the outbound executor thread (message order).
    public final List<String> palette = new ArrayList<>();
    private final Map<Integer, Integer> globalToLocal = new HashMap<>();
    public final Map<UUID, String> skinSent = new HashMap<>();   // skin URL last sent per player ("" for none)
    public boolean lastPlayersNonEmpty;
    /** True while following the server's shared region (default); false after a private subscribe. */
    public volatile boolean shared = true;
    /** hello.caps (e.g. "sections_v1"). Written once before READY. */
    public volatile Set<String> caps = Set.of();
    /** Region that lastGrid describes (what the client has), for delta snapshots. Main thread. */
    public RegionSpec gridRegion;
    /** [nav] System.nanoTime() when the oldest data in lastGrid was captured (RegionData.capturedNs). Main thread. */
    public long lastGridNs;
    /** Recent snapshot id -> region, so a pose is mapped with the origin the client used (pose.snapshot_id). Main thread. */
    private final Map<Integer, RegionSpec> recentRegions = new java.util.LinkedHashMap<>() {
        @Override protected boolean removeEldestEntry(Map.Entry<Integer, RegionSpec> e) { return size() > 8; }
    };
    public long lastFullResendMs;

    public void rememberRegion(int snapshotId, RegionSpec r) { recentRegions.put(snapshotId, r); }

    /** The region a pose with this snapshot_id refers to; the current region if unknown or absent (&lt; 0). */
    public RegionSpec regionFor(int snapshotId) {
        RegionSpec r = snapshotId < 0 ? null : recentRegions.get(snapshotId);
        return r != null ? r : region;
    }
    /** Opted into light_v1 / biomes_v1 in hello.caps (and enabled in config); PROTOCOL.md 4.4.1. */
    public volatile boolean wantsLight, wantsBiomes;
    public byte[] lastLight;               // packed light, as last sent (main thread), null without light_v1
    // Entities (PROTOCOL.md 4.21, entities_v1): what this client holds, for deltas (main thread), and bytes sent.
    public volatile boolean wantsEntities;
    public final Map<Integer, EntityState> entitiesSent = new HashMap<>();
    public RegionSpec entitiesRegion;      // region the held set belongs to; a change triggers a full list
    public final java.util.concurrent.atomic.AtomicLong entityBytes = new java.util.concurrent.atomic.AtomicLong();
    // Actions (PROTOCOL.md 4.22, actions_v1): swings, digging, breaks, places, damage inside the region.
    public volatile boolean wantsActions;
    public final java.util.concurrent.atomic.AtomicLong actionBytes = new java.util.concurrent.atomic.AtomicLong();
    public BiomeGrid lastBiomes;           // biomes of the last snapshot (for a resend after bad_snapshot), null without biomes_v1
    // [living] world_state (world_v1): time of day and weather; what this client got last and when (main thread).
    public volatile boolean wantsWorld;
    public WorldState worldStateSent;
    public long worldStateTick;
    /** Granted edit_v1 in welcome.caps (block_edit, PROTOCOL.md 4.20); its rate limit, set together with it. */
    public volatile boolean canEdit;
    // [blocks] Block entity data (PROTOCOL.md 4.29, block_entities_v1): what this client holds per world chunk (position
    // key -> entry JSON) and the region it was sent for (main thread), and bytes sent.
    public volatile boolean wantsBlockEntities;
    public final Map<Long, Map<Long, String>> blockEntitiesSent = new HashMap<>();
    public RegionSpec blockEntitiesRegion;
    public final java.util.concurrent.atomic.AtomicLong blockEntityBytes = new java.util.concurrent.atomic.AtomicLong();
    public volatile EditLimiter editLimiter;
    // [talk] Chat, commands and status (PROTOCOL.md 4.26-4.28), set at hello. operatorName: the sanitised name Java
    // players see in chat (hello.operator_name, else config chat.default-name).
    public volatile boolean wantsChat, canCommand, wantsStatus;
    public volatile String operatorName = "Tabletop";
    public volatile EditLimiter chatLimiter, commandLimiter, completeLimiter;
    // [hands] Using blocks, items and mobs (interact_v1, InteractService): granted at hello with its rate limits (interact
    // and entity_op share one bucket; holds have their own; spawn eggs are counted per minute).
    public volatile boolean canInteract;
    public volatile EditLimiter interactLimiter, holdLimiter, spawnLimiter;
    // [sound] sound_v1: sound_groups once after the registries, sound actions in "actions" (PROTOCOL.md "Sound").
    public volatile boolean wantsSound;
    // [sound] assets_v1: the operator's own pack.zip, entity_models.json and sounds.zip offered and served in binary
    // frames (AssetService, PROTOCOL.md "Asset delivery"); its pacing and in-flight count (asset worker thread).
    public volatile boolean canAssets, assetsOffered;
    final java.util.concurrent.atomic.AtomicInteger assetsInFlight = new java.util.concurrent.atomic.AtomicInteger();
    long assetsNextNs;
    final Map<String, Long> assetsStartNs = new java.util.concurrent.ConcurrentHashMap<>();
    // [operator] Shared bookmarks (marks_v1, OperatorService), granted at hello.
    public volatile boolean wantsMarks;

    /** RegionReader extras this session needs in its snapshots and diffs. */
    public int readExtras() { return (wantsLight ? RegionReader.LIGHT : 0) | (wantsBiomes ? RegionReader.BIOMES : 0); }

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
        String json = Protocol.GSON.toJson(body);
        int n = json.getBytes(java.nio.charset.StandardCharsets.UTF_8).length;
        if (n <= Protocol.MAX_OUTBOUND_BYTES) conn.send(json);
        // Too big for one frame: tell the client (with a proper envelope). Clients with "sections_v1" never get here.
        else send(Protocol.error("region_too_large", "snapshot " + n + " bytes exceeds the 4 MiB frame limit (smaller region, or a client with sections_v1)", null, false));
        return n;
    }

    /** Palette size, readable from any thread (the palette itself belongs to the outbound executor thread). */
    public volatile int paletteSize;

    public void resetPalette() { palette.clear(); globalToLocal.clear(); paletteSize = 0; }

    /** Returns the session palette index for a global id, appending to palette (and to added) if new. */
    public int localIndex(int globalId, Palette global, List<String> added) {
        Integer l = globalToLocal.get(globalId);
        if (l != null) return l;
        int idx = palette.size();
        String s = global.state(globalId);
        palette.add(s);
        paletteSize = palette.size();
        globalToLocal.put(globalId, idx);
        if (added != null) added.add(s);
        return idx;
    }
}
