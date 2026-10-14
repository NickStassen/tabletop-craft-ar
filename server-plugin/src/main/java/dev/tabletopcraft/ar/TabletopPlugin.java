package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.destroystokyo.paper.profile.PlayerProfile;
import org.bukkit.HeightMap;
import org.bukkit.profile.PlayerTextures;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.scheduler.BukkitTask;

import java.util.*;
import java.util.Map;
import java.util.HashMap;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class TabletopPlugin extends JavaPlugin {
    private RegionCaps caps = RegionCaps.DEFAULT;

    private LinkServer link;
    private ExecutorService outbound;
    private final RegionReader reader = new RegionReader(this);
    private final java.util.Set<RegionSpec> diffInFlight = new java.util.HashSet<>();
    private final GiantAvatar avatar = new GiantAvatar();
    private final ConcurrentLinkedQueue<Runnable> queue = new ConcurrentLinkedQueue<>();
    private record PoseSet(Session s, Pose head, Pose left, Pose right, long seq) {}
    private final AtomicReference<PoseSet> latestPose = new AtomicReference<>();
    private PoseSet lastPose;                              // re-stepped while the smoothed giant catches up
    private final Map<String, PlayerProfile> skinCache = new HashMap<>(); // lower-case name -> resolved profile
    private final Map<String, String> skinHandBlocks = new HashMap<>();   // lower-case name -> block closest to the skin's hand
    private final Set<String> skinLookups = new HashSet<>();
    private BukkitTask tickTask;
    private Map<String, Set<Long>> ticketed = Map.of();   // world name -> chunk keys holding our plugin ticket
    private Set<Long> giantChunks = Set.of();             // chunks of the giant's last applied pose (ticketed)
    private String giantWorld;
    private final Set<Long> giantLoading = java.util.concurrent.ConcurrentHashMap.newKeySet();
    private final Map<String, Set<Long>> giantPending = new HashMap<>(); // loaded for the giant's next move, ticketed until it moves
    private boolean dirty;
    private long tickCount;
    private Session avatarOwner;
    private long avatarOwnerGoneMs = -1;
    // Light/biome extras (PROTOCOL.md 4.4.1): config switches, and a re-diff a few ticks after block changes so
    // light the engine propagates after the change event reaches clients quickly (the safety diff catches the rest).
    private volatile boolean lightOn = true, biomesOn = true;
    private long lightRecheckTick = -1;
    static final int LIGHT_RECHECK_TICKS = 4, MAX_LIGHT_CHANGES = 16384;

    @Override public void onEnable() {
        saveDefaultConfig();
        getServer().getPluginManager().registerEvents(new BlockListener(this), this);
        start();
    }

    @Override public void onDisable() { stop(); }

    private void start() {
        var c = getConfig();
        caps = new RegionCaps(c.getInt("max_region.x", 128), c.getInt("max_region.y", 384), c.getInt("max_region.z", 128),
            c.getLong("max_region.blocks", 1L << 21), c.getLong("max_region.bytes", 4L * 1024 * 1024));
        avatar.blockSizeM = c.getDouble("avatar.block-size-m", 0.01);
        avatar.maxScale = c.getDouble("avatar.max-scale", 16.0);
        avatar.userHeightM = c.getDouble("avatar.user-height-m", 1.75);
        avatar.skinPlayer = defaultSkinPlayer();
        avatar.interpolationTicks = c.getInt("avatar.interpolation-ticks", 2);
        avatar.placement = GiantPlacement.Mode.parse(c.getString("avatar.placement", "floating"));
        avatar.headSizeM = c.getDouble("avatar.head-size-m", 0.25);
        avatar.handSizeM = c.getDouble("avatar.hand-size-m", 0.1);
        avatar.maxHeadBlocks = c.getDouble("avatar.max-head-blocks", 64);
        avatar.handBlockSetting = c.getString("avatar.hand-block", "auto");
        avatar.smoothing = c.getDouble("avatar.smoothing", 0.5);
        lightOn = c.getBoolean("stream.light", true);
        biomesOn = c.getBoolean("stream.biomes", true);
        outbound = Executors.newSingleThreadExecutor(r -> { Thread t = new Thread(r, "tabletop-outbound"); t.setDaemon(true); return t; });
        link = new LinkServer(this, c.getString("bind.host", "0.0.0.0"), c.getInt("bind.port", 8765), c.getString("bind.path", "/tabletop"), c.getString("auth.token", ""));
        link.start();
        tickTask = Bukkit.getScheduler().runTaskTimer(this, this::tick, 1, 1);
    }

    private void stop() {
        if (tickTask != null) tickTask.cancel();
        avatar.remove();
        lastPose = null;
        giantPending.clear();
        releaseTickets();
        if (link != null) {
            for (Session s : link.sessions()) s.conn.close(Protocol.CLOSE_SHUTDOWN, "plugin disabled");
            try { link.stop(1000); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
            link = null;
        }
        if (outbound != null) { outbound.shutdown(); try { outbound.awaitTermination(2, TimeUnit.SECONDS); } catch (InterruptedException ignored) {} }
    }

    // ---------- thread hand-off ----------
    void enqueue(Runnable r) { queue.add(r); }
    void offerPose(Session s, Pose h, Pose l, Pose r, long seq) { latestPose.set(new PoseSet(s, h, l, r, seq)); }
    boolean lightEnabled() { return lightOn; }
    boolean biomesEnabled() { return biomesOn; }
    private void send(Session s, JsonObject body) { if (outbound != null && !outbound.isShutdown()) outbound.execute(() -> s.send(body)); }

    void markDirty(String world, int x, int y, int z) {
        if (link == null) return;
        for (Session s : link.sessions()) if (s.region != null && s.region.world().equals(world) && s.region.containsWorld(x, y, z)) { dirty = true; return; }
    }

    RegionSpec defaultRegion() {
        var c = getConfig();
        return new RegionSpec(c.getString("region.world", "world"), c.getInt("region.origin.x"), c.getInt("region.origin.y", 60), c.getInt("region.origin.z"),
            c.getInt("region.dims.x", 32), c.getInt("region.dims.y", 64), c.getInt("region.dims.z", 32));
    }

    // ---------- main tick ----------
    private void tick() {
        tickCount++;
        Runnable r;
        while ((r = queue.poll()) != null) {
            try { r.run(); } catch (RuntimeException e) { getLogger().warning("command failed: " + e); }
        }
        applyPose();
        int safety = Math.max(1, getConfig().getInt("stream.safety-diff-ticks", 20));
        if (dirty || tickCount % safety == 0 || tickCount == lightRecheckTick) {
            if (dirty) lightRecheckTick = tickCount + LIGHT_RECHECK_TICKS;
            dirty = false;
            diffAll();
        }
        if (tickCount % Math.max(1, getConfig().getInt("stream.players-every-ticks", 2)) == 0) sendPlayers();
        if (tickCount % 20 == 0) timeouts();
        if (tickCount % 10 == 0) updateTickets();   // follow the giant
        if (tickCount % 10 == 0 && getConfig().getBoolean("debug.avatar-state", false) && avatar.spawned()) broadcast(avatar.state(true));
    }

    private Collection<Session> ready() {
        List<Session> l = new ArrayList<>();
        if (link != null) for (Session s : link.sessions()) if (s.state == Session.State.READY && s.region != null) l.add(s);
        return l;
    }

    private void broadcast(JsonObject body) { for (Session s : ready()) send(s, body.deepCopy()); }

    void onReady(Session s) {
        JsonObject w = Protocol.body("welcome");
        w.addProperty("protocol", Protocol.VERSION);
        w.addProperty("server", "tabletop-plugin");
        w.addProperty("server_version", getPluginMeta().getVersion());
        w.addProperty("mc_version", Bukkit.getMinecraftVersion());
        w.addProperty("session", s.id);
        RegionSpec r = defaultRegion();
        w.addProperty("world", r.world());
        JsonObject reg = new JsonObject();
        reg.add("origin", Protocol.ints(r.ox(), r.oy(), r.oz()));
        reg.add("dims", Protocol.ints(r.sx(), r.sy(), r.sz()));
        w.add("region", reg);
        JsonObject lim = new JsonObject();
        caps.addTo(lim);
        lim.addProperty("pose_hz_max", 60);
        lim.addProperty("player_hz", 20 / Math.max(1, getConfig().getInt("stream.players-every-ticks", 2)));
        w.add("limits", lim);
        w.addProperty("avatar_supported", true);
        com.google.gson.JsonArray caps = new com.google.gson.JsonArray(); // optional features granted to this session
        if (s.wantsLight) caps.add(Protocol.CAP_LIGHT);
        if (s.wantsBiomes) caps.add(Protocol.CAP_BIOMES);
        w.add("caps", caps);
        send(s, w);
        subscribe(s, r, null);
    }

    void onSubscribe(Session s, String world, int[] origin, int[] dims, Long refSeq, boolean follow) {
        if (s.state != Session.State.READY) return;
        String wn = world == null ? defaultRegion().world() : world;
        World w = Bukkit.getWorld(wn);
        if (w == null) { send(s, Protocol.error("world_not_found", "no world " + wn, refSeq, false)); return; }
        String why = caps.check(dims[0], dims[1], dims[2]);
        if (why != null) { send(s, tooLarge(why, refSeq)); return; }
        s.shared = false;
        int y0 = Math.max(origin[1], w.getMinHeight());
        int y1 = Math.min(origin[1] + dims[1], w.getMaxHeight());
        if (y1 <= y0) { send(s, Protocol.error("region_too_large", "region is outside the build height", refSeq, false)); return; }
        if (follow) send(s, Protocol.error("unsupported", "follow is not implemented in v0.1", refSeq, false));
        subscribe(s, new RegionSpec(wn, origin[0], y0, origin[2], dims[0], y1 - y0, dims[2]), refSeq);
    }

    private void subscribe(Session s, RegionSpec r, Long refSeq) {
        s.region = r;
        s.lastGrid = null; // pause diffs until the new snapshot is captured
        updateTickets();
        reader.readAsync(r, s.readExtras(), d -> { if (r.equals(s.region) && s.state == Session.State.READY) sendSnapshot(s, d); });
    }

    private void sendSnapshot(Session s, RegionData d) {
        long t0 = System.nanoTime();
        long readNs = (long) ((reader.lastSnapMs + reader.lastWalkMs) * 1e6);
        int[] grid = d.grid();
        byte[] light = s.wantsLight ? d.light() : null;
        BiomeGrid biomes = s.wantsBiomes ? d.biomes() : null;
        s.lastGrid = grid;
        s.lastLight = light;
        s.snapshotId++;
        s.resetPalette();
        Palette pal = reader.palette();
        int air = pal.id("minecraft:air");
        boolean hasAir = false;
        for (int g : grid) if (g == air) { hasAir = true; break; }
        if (hasAir) s.localIndex(air, pal, null); // index 0 = air when present
        int[] local = new int[grid.length];
        for (int i = 0; i < grid.length; i++) local[i] = s.localIndex(grid[i], pal, null);
        List<String> palette = List.copyOf(s.palette);
        RegionSpec r = s.region;
        int id = s.snapshotId;
        // Encode + serialize off the main thread; the outbound executor keeps message order.
        long remapNs = System.nanoTime() - t0;
        if (outbound != null) outbound.execute(() -> {
            long e0 = System.nanoTime();
            int[] data = BlockCodec.encode(local);
            long encNs = System.nanoTime() - e0;
            JsonObject msg = Protocol.chunkSnapshot(id, r, palette, data);
            long x0 = System.nanoTime();
            Protocol.addExtras(msg, light, biomes);
            long extrasNs = System.nanoTime() - x0;
            JsonObject timing = new JsonObject();
            timing.addProperty("read_ms", Math.round(readNs / 1e4) / 100.0);
            timing.addProperty("remap_ms", Math.round(remapNs / 1e4) / 100.0);
            timing.addProperty("encode_ms", Math.round(encNs / 1e4) / 100.0);
            if (light != null || biomes != null) timing.addProperty("extras_encode_ms", Math.round(extrasNs / 1e4) / 100.0);
            msg.add("server_timing", timing); // additive extension, ignored by older clients
            long j0 = System.nanoTime();
            int bytes = s.sendCounted(msg);
            JsonObject lj = msg.has("light") && msg.get("light").isJsonObject() ? msg.getAsJsonObject("light") : null;
            JsonObject bj = msg.has("biomes") && msg.get("biomes").isJsonObject() ? msg.getAsJsonObject("biomes") : null;
            getLogger().info(String.format("SNAPSHOT session=%s dims=%dx%dx%d blocks=%d palette=%d runs=%d bytes=%d read_ms=%.2f remap_ms=%.2f encode_ms=%.2f json_send_ms=%.2f light_runs=%d biomes=%d biome_runs=%d extras_ms=%.2f",
                s.id, r.sx(), r.sy(), r.sz(), r.volume(), palette.size(), data.length / 2, bytes, readNs / 1e6, remapNs / 1e6, encNs / 1e6, (System.nanoTime() - j0) / 1e6,
                lj == null ? -1 : lj.getAsJsonArray("data").size() / 2, bj == null ? -1 : bj.getAsJsonArray("palette").size(),
                bj == null ? -1 : bj.getAsJsonArray("data").size() / 2, extrasNs / 1e6));
        });
    }

    private void diffAll() {
        java.util.Set<RegionSpec> regions = new java.util.HashSet<>();
        for (Session s : ready()) if (s.lastGrid != null) regions.add(s.region);
        for (RegionSpec r : regions) {
            if (!diffInFlight.add(r)) continue;
            Map<Session, Integer> gen = new HashMap<>();
            int extras = 0; // read light/biomes if any session on this region wants them (a re-snapshot may need biomes)
            for (Session s : ready()) if (r.equals(s.region) && s.lastGrid != null) { gen.put(s, s.snapshotId); extras |= s.readExtras(); }
            try {
                reader.readAsync(r, extras, d -> { diffInFlight.remove(r); applyDiff(r, d, gen); });
            } catch (IllegalStateException e) { diffInFlight.remove(r); }
        }
    }

    private void applyDiff(RegionSpec r, RegionData d, Map<Session, Integer> gen) {
        int[] g = d.grid();
        for (var e : gen.entrySet()) {
            Session s = e.getKey();
            // Drop results that started before a newer snapshot or a region change.
            if (s.state != Session.State.READY || !r.equals(s.region) || s.snapshotId != e.getValue() || s.lastGrid == null) continue;
            int[] old = s.lastGrid;
            List<int[]> changed = new ArrayList<>();
            for (int i = 0; i < g.length; i++) if (g[i] != old[i]) changed.add(new int[] { i, g[i] });
            s.lastGrid = g;
            List<int[]> light = lightChanges(s, r, d.light());
            if (changed.isEmpty() && light.isEmpty()) continue;
            if (changed.size() > 4096 || light.size() > MAX_LIGHT_CHANGES) { sendSnapshot(s, d); continue; }
            List<String> added = new ArrayList<>();
            List<Protocol.Change> out = new ArrayList<>();
            for (int[] c : changed) {
                int i = c[0];
                int lx = i % r.sx(), lz = (i / r.sx()) % r.sz(), ly = i / (r.sx() * r.sz());
                out.add(new Protocol.Change(lx, ly, lz, s.localIndex(c[1], reader.palette(), added)));
            }
            send(s, Protocol.blockUpdate(s.snapshotId, added, out, light));
        }
    }

    /** Cells whose packed light differs from what the session last got, as [x, y, z, value]; updates lastLight. */
    private static List<int[]> lightChanges(Session s, RegionSpec r, byte[] now) {
        if (!s.wantsLight || now == null) return List.of();
        byte[] old = s.lastLight;
        s.lastLight = now;
        if (old == null || old.length != now.length) return List.of();
        return BlockCodec.lightDiff(old, now, r);
    }

    private void sendPlayers() {
        int margin = getConfig().getInt("stream.players-margin", 16);
        for (Session s : ready()) {
            RegionSpec r = s.region;
            World w = Bukkit.getWorld(r.world());
            if (w == null) continue;
            com.google.gson.JsonArray arr = new com.google.gson.JsonArray();
            for (Player p : w.getPlayers()) {
                Location l = p.getLocation();
                if (l.getX() < r.ox() - margin || l.getX() > r.ox() + r.sx() + margin || l.getZ() < r.oz() - margin || l.getZ() > r.oz() + r.sz() + margin) continue;
                JsonObject e = new JsonObject();
                e.addProperty("uuid", p.getUniqueId().toString());
                e.addProperty("name", p.getName());
                e.add("pos", Protocol.doubles(l.getX(), l.getY(), l.getZ()));
                e.addProperty("yaw", Math.round(l.getYaw() * 10) / 10.0);
                e.addProperty("pitch", Math.round(l.getPitch() * 10) / 10.0);
                e.addProperty("sneaking", p.isSneaking());
                if (s.skinSent.add(p.getUniqueId())) {
                    PlayerProfile prof = p.getPlayerProfile();
                    var textures = prof.getTextures();
                    var skin = textures.getSkin();
                    e.addProperty("skin", skin == null ? null : skin.toString());
                    // Additive (PROTOCOL.md 4.6): arm width from the textures payload's metadata.
                    if (skin != null) e.addProperty("skin_model", SkinLookup.model(textures.getSkinModel() == PlayerTextures.SkinModel.SLIM));
                }
                arr.add(e);
            }
            if (arr.isEmpty() && !s.lastPlayersNonEmpty) continue;
            s.lastPlayersNonEmpty = !arr.isEmpty();
            JsonObject o = Protocol.body("player_positions");
            o.add("players", arr);
            send(s, o);
        }
    }

    private void timeouts() {
        long now = System.currentTimeMillis();
        if (link != null) for (Session s : link.sessions()) {
            if (s.state == Session.State.AWAIT_HELLO && now - s.openedMs > 5000) s.conn.close(Protocol.CLOSE_HELLO_TIMEOUT, "hello timeout");
            else if (s.state == Session.State.READY && now - s.lastInboundMs > 15000) s.conn.close(Protocol.CLOSE_IDLE, "idle timeout");
        }
        if (avatarOwnerGoneMs > 0 && now - avatarOwnerGoneMs > getConfig().getLong("avatar.despawn-after-disconnect-ms", 10000)) {
            avatarOwnerGoneMs = -1;
            if (avatar.spawned()) { avatar.remove(); avatar.enabled = false; getLogger().info("Avatar despawned (owner disconnected)"); }
        }
    }

    void onSessionClosed(Session s) {
        if (s == avatarOwner) avatarOwnerGoneMs = System.currentTimeMillis();
        updateTickets();
    }

    // ---------- area setup (PROTOCOL.md 4.13-4.15) ----------
    private JsonObject tooLarge(String why, Long refSeq) {
        JsonObject e = Protocol.error("region_too_large", why, refSeq, false);
        caps.addTo(e);
        return e;
    }

    private JsonObject regionChanged(RegionSpec r, Session by, Long refSeq) {
        JsonObject o = Protocol.body("region_changed");
        o.addProperty("world", r.world());
        o.add("origin", Protocol.ints(r.ox(), r.oy(), r.oz()));
        o.add("dims", Protocol.ints(r.sx(), r.sy(), r.sz()));
        o.addProperty("by_session", by == null ? "server" : by.id);
        if (refSeq != null && by != null) o.addProperty("ref_seq", refSeq);
        return o;
    }

    /** Shared-region change requested by a headset: validate, check byte cap, then last-writer-wins broadcast. */
    void onRegionRequest(Session s, RegionRequest q, Long refSeq) {
        if (s.state != Session.State.READY) return;
        String wn = q.world() == null ? defaultRegion().world() : q.world();
        World w = Bukkit.getWorld(wn);
        if (w == null) { send(s, Protocol.error("world_not_found", "no world " + wn, refSeq, false)); return; }
        String why = caps.check(q.dx(), q.dy(), q.dz());
        if (why != null) { getLogger().info("REGION_REJECT " + why); send(s, tooLarge(why, refSeq)); return; }
        int y0 = Math.max(q.oy(), w.getMinHeight()), y1 = Math.min(q.oy() + q.dy(), w.getMaxHeight());
        if (y1 <= y0) { send(s, tooLarge("region is outside the build height " + w.getMinHeight() + ".." + w.getMaxHeight(), refSeq)); return; }
        RegionSpec r = new RegionSpec(wn, q.ox(), y0, q.oz(), q.dx(), y1 - y0, q.dz());
        long t0 = System.nanoTime();
        int extras = s.readExtras(); // every shared session gets this read: include what any of them wants
        for (Session o : ready()) if (o.shared) extras |= o.readExtras();
        reader.readAsync(r, extras, d -> {
            if (s.state != Session.State.READY) return;
            int[] grid = d.grid();
            // Byte cap: estimate from the global-palette RLE (same run structure as each session's encoding),
            // plus the light/biome fields (their size is estimated off-thread by the reader).
            int[] data = BlockCodec.encode(grid);
            java.util.Set<Integer> ids = new java.util.HashSet<>();
            for (int k = 0; k < data.length; k += 2) ids.add(data[k]);
            List<String> pal = new ArrayList<>();
            for (int id : ids) pal.add(reader.palette().state(id));
            long est = RegionRequest.estimateSnapshotBytes(data, pal) + d.extraBytes();
            if (est > caps.maxBytes()) {
                String msg = String.format("snapshot would be about %d bytes, over max %d (try a smaller region)", est, caps.maxBytes());
                getLogger().info("REGION_REJECT " + msg);
                send(s, tooLarge(msg, refSeq));
                return;
            }
            getLogger().info(String.format("REGION_SET by=%s %s origin=%d,%d,%d dims=%dx%dx%d est_bytes=%d total_ms=%.1f chunk_load_ms=%.1f snapshot_main_ms=%.2f walk_async_ms=%.1f",
                s.id, wn, r.ox(), r.oy(), r.oz(), r.sx(), r.sy(), r.sz(), est, (System.nanoTime() - t0) / 1e6, reader.lastLoadMs, reader.lastSnapMs, reader.lastWalkMs));
            getConfig().set("region.world", wn);
            getConfig().set("region.origin.x", r.ox()); getConfig().set("region.origin.y", r.oy()); getConfig().set("region.origin.z", r.oz());
            getConfig().set("region.dims.x", r.sx()); getConfig().set("region.dims.y", r.sy()); getConfig().set("region.dims.z", r.sz());
            saveConfig();
            s.shared = true;
            for (Session o : ready()) {
                if (!o.shared) continue;
                send(o, regionChanged(r, s, o == s ? refSeq : null));
                o.region = r;
                sendSnapshot(o, d);
            }
            updateTickets();
        });
    }

    void onRegionQuery(Session s, String playerName, Long refSeq) {
        if (s.state != Session.State.READY) return;
        JsonObject o = Protocol.body("region_info");
        RegionSpec r = s.region != null ? s.region : defaultRegion();
        JsonObject reg = new JsonObject();
        reg.addProperty("world", r.world());
        reg.add("origin", Protocol.ints(r.ox(), r.oy(), r.oz()));
        reg.add("dims", Protocol.ints(r.sx(), r.sy(), r.sz()));
        o.add("region", reg);
        com.google.gson.JsonArray arr = new com.google.gson.JsonArray();
        for (Player p : Bukkit.getOnlinePlayers()) {
            if (playerName != null && !p.getName().equalsIgnoreCase(playerName)) continue;
            Location l = p.getLocation();
            JsonObject e = new JsonObject();
            e.addProperty("name", p.getName());
            e.addProperty("uuid", p.getUniqueId().toString());
            e.addProperty("world", l.getWorld().getName());
            e.add("pos", Protocol.doubles(l.getX(), l.getY(), l.getZ()));
            e.addProperty("yaw", Math.round(l.getYaw() * 10) / 10.0);
            arr.add(e);
        }
        if (playerName != null && arr.isEmpty()) { send(s, Protocol.error("bad_message", "no online player named " + playerName, refSeq, false)); return; }
        o.add("players", arr);
        JsonObject lim = new JsonObject();
        caps.addTo(lim);
        o.add("limits", lim);
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        send(s, o);
    }

    // ---------- avatar ----------
    void onAvatarConfig(Session s, AvatarConfig c, Long refSeq) {
        // Block size / max_scale changes (the headset sends them while scaling) only rescale the spawned giant;
        // new entities are needed only when its appearance changes.
        boolean looks = (c.skinPlayer() != null && !(c.skinPlayer().isBlank() ? defaultSkinPlayer() : c.skinPlayer()).equals(avatar.skinPlayer))
            || (c.model() != null && !c.model().equals(avatar.model))
            || (c.showHands() != null && c.showHands() != avatar.showHands)
            || (c.nameTagPresent() && !java.util.Objects.equals(c.nameTag(), avatar.nameTag));
        // A blank skin_player falls back to the plugin default (config.yml avatar.skin-player); missing keeps the current one.
        if (c.skinPlayer() != null) avatar.skinPlayer = c.skinPlayer().isBlank() ? defaultSkinPlayer() : c.skinPlayer();
        if (c.model() != null) avatar.model = c.model();
        if (c.blockSizeM() != null) avatar.blockSizeM = c.blockSizeM();
        if (c.maxScale() != null) avatar.maxScale = c.maxScale();
        if (c.showHands() != null) avatar.showHands = c.showHands();
        if (c.nameTagPresent()) avatar.nameTag = c.nameTag();
        if (c.skinUrl() != null) send(s, Protocol.error("unsupported", "skin_url is not implemented in v0.1; use skin_player", refSeq, false));
        if (c.enabled() != null) avatar.enabled = c.enabled();
        avatarOwner = s; avatarOwnerGoneMs = -1;
        if (!avatar.enabled) avatar.remove();
        else if (looks || !avatar.spawned()) spawnAvatarAtAnchor();
        else avatar.rescale();
        send(s, avatar.state(getConfig().getBoolean("debug.avatar-state", false)));
    }

    private String defaultSkinPlayer() { return getConfig().getString("avatar.skin-player", ""); }

    private void spawnAvatarAtAnchor() {
        RegionSpec r = defaultRegion();
        World w = Bukkit.getWorld(r.world());
        if (w == null) return;
        // Stand on the ground in the middle of the region until the first pose arrives (region chunks are ticketed).
        int cx = r.ox() + r.sx() / 2, cz = r.oz() + r.sz() / 2;
        double y = w.isChunkLoaded(cx >> 4, cz >> 4) ? w.getHighestBlockYAt(cx, cz, HeightMap.MOTION_BLOCKING_NO_LEAVES) + 1 : r.oy();
        String key = avatar.skinPlayer.toLowerCase(Locale.ROOT);
        avatar.resolvedSkin = skinCache.get(key);
        avatar.skinHandBlock = skinHandBlocks.get(key);
        avatar.spawn(w, new Location(w, cx + 0.5, y, cz + 0.5));
        resolveSkin(avatar.skinPlayer);
        getLogger().info(String.format("Avatar spawned: scale %.2f (%s), placement %s, skin %s", avatar.scale(),
            avatar.note == null ? "no clamp" : avatar.note, avatar.placement.id(), avatar.skinPlayer.isBlank() ? "default" : avatar.skinPlayer));
    }

    // ---------- giant skin ----------
    /**
     * Resolves skin_player to a signed textures property off the main thread, then gives the giant a static
     * profile. Until then (and if this fails) it keeps the name-only profile, which Java clients resolve
     * themselves. Paper's own lookup follows the server's online-mode setting (an offline server turns the name
     * into an offline UUID without textures), so ask with online=true and, if that finds nothing, look the UUID up
     * on the Mojang API directly.
     */
    private void resolveSkin(String name) {
        if (name == null || name.isBlank()) { avatar.skinNote = null; return; }
        String key = name.toLowerCase(Locale.ROOT);
        if (skinCache.containsKey(key)) { avatar.skinNote = null; return; }
        if (!skinLookups.add(key)) return;
        avatar.skinNote = "resolving skin " + name;
        PlayerProfile profile = Bukkit.createProfile(name);
        Bukkit.getScheduler().runTaskAsynchronously(this, () -> {
            PlayerProfile done = null;
            String why = null;
            try {
                if (profile.complete(true, true) && profile.hasTextures()) done = profile;
                else {
                    java.util.UUID id = SkinLookup.uuidForName(name);
                    if (id == null) why = "no Java account named " + name;
                    else {
                        PlayerProfile byId = Bukkit.createProfile(id, name);
                        if (byId.complete(true, true) && byId.hasTextures()) done = byId;
                        else why = "no textures for " + name;
                    }
                }
            } catch (Exception e) { why = e.getClass().getSimpleName() + ": " + e.getMessage(); }
            // The floating hands take the skin's hand colour (closest plain block); a failure keeps the default.
            String hand = null;
            if (done != null) {
                try {
                    hand = SkinLookup.handBlock(done.getTextures().getSkin(), done.getTextures().getSkinModel() == PlayerTextures.SkinModel.SLIM);
                } catch (Exception e) { getLogger().warning("SKIN hand colour for " + name + ": " + e); }
            }
            PlayerProfile result = done;
            String reason = why, handBlock = hand;
            enqueue(() -> onSkinResolved(name, key, result, reason, handBlock));
        });
    }

    private void onSkinResolved(String name, String key, PlayerProfile p, String why, String handBlock) {
        skinLookups.remove(key);
        boolean current = avatar.skinPlayer.equalsIgnoreCase(name);
        if (p != null) {
            skinCache.put(key, p);
            if (handBlock != null) skinHandBlocks.put(key, handBlock);
            getLogger().info("SKIN resolved " + name + " -> " + p.getId() + " (" + (p.getTextures().getSkinModel() == PlayerTextures.SkinModel.SLIM ? "slim" : "wide") + "), hand block " + handBlock);
            if (current) { avatar.resolvedSkin = p; avatar.skinHandBlock = handBlock; avatar.skinNote = null; avatar.refreshSkin(); }
        } else {
            getLogger().warning("SKIN lookup failed for " + name + ": " + why);
            if (current) avatar.skinNote = "server skin lookup failed (" + why + "); Java clients may still resolve " + name;
        }
        if (!current || avatarOwner == null || avatarOwner.state != Session.State.READY) return;
        if (p == null) send(avatarOwner, Protocol.error("skin_failed", "skin_player " + name + ": " + why, null, false));
        send(avatarOwner, avatar.state(getConfig().getBoolean("debug.avatar-state", false)));
    }

    private void applyPose() {
        PoseSet ps = latestPose.getAndSet(null);
        if (ps == null) {
            // No new pose this tick: keep easing toward the last one so the giant settles where the user is.
            if (lastPose == null || !avatar.settling()) return;
            ps = lastPose;
        } else lastPose = ps;
        if (ps.s().region == null) return;
        if (!avatar.enabled && getConfig().getBoolean("avatar.spawn-on-first-pose", true) && !avatar.spawned()) {
            avatar.enabled = true; avatarOwner = ps.s(); spawnAvatarAtAnchor();
            send(ps.s(), avatar.state(getConfig().getBoolean("debug.avatar-state", false)));
        }
        if (!avatar.spawned()) return;
        RegionSpec r = ps.s().region;
        World w = Bukkit.getWorld(r.world());
        if (w == null) return;
        GiantPlacement.Placement target = GiantPlacement.place(avatar.placement, ps.head(), ps.left(), ps.right(), r,
            avatar.blockSizeM, avatar.scale(), avatar.userHeightM, (x, z) -> groundFeetY(w, x, z), w.getMinHeight());
        if (target == null) {
            // Ground mode, column not loaded (the region's chunks are ticketed, so this is rare): load it, hold.
            double[] hp = ps.head().toWorld(r.ox(), r.oy(), r.oz(), avatar.blockSizeM);
            int bx = (int) Math.floor(GiantPlacement.clamp(hp[0], r.ox(), r.ox() + r.sx(), GiantPlacement.EDGE_INSET));
            int bz = (int) Math.floor(GiantPlacement.clamp(hp[2], r.oz(), r.oz() + r.sz(), GiantPlacement.EDGE_INSET));
            loadAsync(w, chunkKey(bx >> 4, bz >> 4));
            return;
        }
        GiantAvatar.Step step = avatar.step(target, ps.head());
        // Never move the giant into an unloaded chunk: that deletes its non-persistent entities on the spot. Load
        // the target chunks asynchronously (no main-thread stall), hold the giant meanwhile, then ticket and move.
        Set<Long> targets = new HashSet<>();
        for (double[] p : new double[][] { step.eye(), step.left(), step.right() })
            if (p != null) targets.add(chunkKey((int) Math.floor(p[0]) >> 4, (int) Math.floor(p[2]) >> 4));
        boolean loaded = true;
        for (long k : targets) if (!w.isChunkLoaded((int) k, (int) (k >> 32))) { loaded = false; loadAsync(w, k); }
        if (!loaded) return;
        if (!targets.equals(giantChunks) || !w.getName().equals(giantWorld) || !giantPending.isEmpty()) {
            giantChunks = targets; giantWorld = w.getName(); giantPending.clear(); updateTickets();
        }
        avatar.commit(w, step, ps.head(), ps.left(), ps.right(), ps.seq());
        if (getConfig().getBoolean("debug.log-poses", false))
            getLogger().info(String.format("pose seq=%d eye=%.2f,%.2f,%.2f yaw=%.1f ground=%.1f", ps.seq(), step.eye()[0], step.eye()[1], step.eye()[2], step.yaw(), step.groundY()));
    }

    private void loadAsync(World w, long k) {
        if (!giantLoading.add(k)) return;
        String wn = w.getName();
        w.getChunkAtAsync((int) k, (int) (k >> 32)).whenComplete((c, err) -> {
            giantLoading.remove(k);
            if (c == null) return;
            // Ticket it right away: with no player nearby a chunk loaded this way unloads again before the next
            // tick, and the giant would wait for it forever. The ticket is cheap here because it's loaded.
            Runnable hold = () -> { giantPending.computeIfAbsent(wn, x -> new HashSet<>()).add(k); updateTickets(); };
            if (Bukkit.isPrimaryThread()) hold.run(); else enqueue(hold);
        });
    }

    /** Feet Y on the ground in column (x, z), ignoring leaves so the giant wades through forests; NaN if not loaded. */
    private static double groundFeetY(World w, int x, int z) {
        if (!w.isChunkLoaded(x >> 4, z >> 4)) return Double.NaN;
        return w.getHighestBlockYAt(x, z, HeightMap.MOTION_BLOCKING_NO_LEAVES) + 1;
    }

    // ---------- chunk tickets ----------
    /**
     * Keeps the regions' chunks loaded, plus the chunks the giant is in: its entities are non-persistent, so when
     * it walks out of the region (the head maps there when you step back from the table) into a chunk no player
     * keeps loaded, the chunk unloads and deletes them (26.x has no spawn chunks). applyPose only moves it into
     * loaded chunks. Plugin tickets aren't counted, so this diffs the wanted set against what's ticketed.
     */
    private void updateTickets() {
        Map<String, Set<Long>> want = new HashMap<>();
        Set<RegionSpec> regions = new HashSet<>();
        regions.add(defaultRegion());
        for (Session s : ready()) regions.add(s.region);
        for (RegionSpec r : regions) {
            Set<Long> keys = want.computeIfAbsent(r.world(), k -> new HashSet<>());
            for (int cx = r.ox() >> 4; cx <= (r.ox() + r.sx() - 1) >> 4; cx++)
                for (int cz = r.oz() >> 4; cz <= (r.oz() + r.sz() - 1) >> 4; cz++) keys.add(chunkKey(cx, cz));
        }
        if (giantWorld != null && avatar.spawned()) want.computeIfAbsent(giantWorld, k -> new HashSet<>()).addAll(giantChunks);
        if (avatar.spawned()) giantPending.forEach((name, keys) -> want.computeIfAbsent(name, k -> new HashSet<>()).addAll(keys));
        if (want.equals(ticketed)) return;
        Set<String> worlds = new HashSet<>(want.keySet());
        worlds.addAll(ticketed.keySet());
        for (String name : worlds) {
            World w = Bukkit.getWorld(name);
            if (w == null) continue;
            Set<Long> had = ticketed.getOrDefault(name, Set.of()), keep = want.getOrDefault(name, Set.of());
            for (long k : had) if (!keep.contains(k)) w.removePluginChunkTicket((int) k, (int) (k >> 32), this);
            for (long k : keep) if (!had.contains(k)) w.addPluginChunkTicket((int) k, (int) (k >> 32), this);
        }
        ticketed = want;
    }

    private static long chunkKey(int cx, int cz) { return (cx & 0xffffffffL) | ((long) cz << 32); }

    private void releaseTickets() {
        for (World w : Bukkit.getWorlds()) w.removePluginChunkTickets(this);
        ticketed = Map.of();
    }

    // ---------- commands ----------
    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (args.length == 0) return false;
        switch (args[0].toLowerCase(Locale.ROOT)) {
            case "status" -> {
                RegionSpec r = defaultRegion();
                sender.sendMessage(String.format("[tabletop] default region %s origin %d %d %d dims %d %d %d; clients %d; avatar %s scale %.2f placement %s; poses applied %d",
                    r.world(), r.ox(), r.oy(), r.oz(), r.sx(), r.sy(), r.sz(), ready().size(), avatar.spawned() ? "spawned" : "none", avatar.scale(), avatar.placement.id(), avatar.posesApplied));
            }
            case "setorigin" -> {
                int x, y, z; String world;
                if (args.length >= 4) {
                    try { x = Integer.parseInt(args[1]); y = Integer.parseInt(args[2]); z = Integer.parseInt(args[3]); }
                    catch (NumberFormatException e) { sender.sendMessage("[tabletop] usage: /tabletop setorigin [x y z]"); return true; }
                    world = sender instanceof Player p ? p.getWorld().getName() : defaultRegion().world();
                } else if (sender instanceof Player p) {
                    Location l = p.getLocation(); x = l.getBlockX(); y = l.getBlockY(); z = l.getBlockZ(); world = l.getWorld().getName();
                } else { sender.sendMessage("[tabletop] console must pass x y z"); return true; }
                getConfig().set("region.world", world);
                getConfig().set("region.origin.x", x); getConfig().set("region.origin.y", y); getConfig().set("region.origin.z", z);
                saveConfig();
                resubscribeAll();
                sender.sendMessage(String.format("[tabletop] origin set to %s %d %d %d", world, x, y, z));
            }
            case "setsize" -> {
                if (args.length < 4) { sender.sendMessage("[tabletop] usage: /tabletop setsize <dx> <dy> <dz>"); return true; }
                try {
                    int dx = Integer.parseInt(args[1]), dy = Integer.parseInt(args[2]), dz = Integer.parseInt(args[3]);
                    if (caps.check(dx, dy, dz) != null) throw new NumberFormatException();
                    getConfig().set("region.dims.x", dx); getConfig().set("region.dims.y", dy); getConfig().set("region.dims.z", dz);
                } catch (NumberFormatException e) { sender.sendMessage("[tabletop] dims must be within " + caps.maxX() + " x " + caps.maxY() + " x " + caps.maxZ() + " and " + caps.maxBlocks() + " blocks"); return true; }
                saveConfig();
                resubscribeAll();
                sender.sendMessage("[tabletop] size updated");
            }
            case "avatar" -> {
                String sub = args.length > 1 ? args[1].toLowerCase(Locale.ROOT) : "status";
                switch (sub) {
                    case "spawn" -> { avatar.enabled = true; spawnAvatarAtAnchor(); sender.sendMessage("[tabletop] avatar spawned, scale " + avatar.scale()); }
                    case "remove" -> { avatar.enabled = false; avatar.remove(); sender.sendMessage("[tabletop] avatar removed"); }
                    case "placement" -> {
                        if (args.length < 3 || !List.of("floating", "ground", "exact").contains(args[2].toLowerCase(Locale.ROOT))) { sender.sendMessage("[tabletop] usage: /tabletop avatar placement <floating|ground|exact>"); return true; }
                        avatar.placement = GiantPlacement.Mode.parse(args[2]);
                        getConfig().set("avatar.placement", avatar.placement.id()); saveConfig();
                        // Floating is a head and hands, ground/exact a Mannequin: switching between them needs new entities.
                        if (avatar.spawned() && avatar.floating() == avatar.placement.hasBody()) spawnAvatarAtAnchor();
                        sender.sendMessage("[tabletop] giant placement: " + avatar.placement.id());
                    }
                    case "skin" -> {
                        if (args.length < 3) { sender.sendMessage("[tabletop] usage: /tabletop avatar skin <java account name>"); return true; }
                        avatar.skinPlayer = args[2];
                        if (avatar.spawned()) spawnAvatarAtAnchor(); else resolveSkin(args[2]);
                        sender.sendMessage("[tabletop] giant skin: " + args[2]);
                    }
                    case "blocksize" -> {
                        if (args.length < 3) { sender.sendMessage("[tabletop] usage: /tabletop avatar blocksize <metres per block>"); return true; }
                        try { double b = Double.parseDouble(args[2]); if (b < 0.002 || b > 0.1) throw new NumberFormatException(); avatar.blockSizeM = b; getConfig().set("avatar.block-size-m", b); saveConfig(); }
                        catch (NumberFormatException e) { sender.sendMessage("[tabletop] block size must be 0.002..0.1"); return true; }
                        if (avatar.spawned()) spawnAvatarAtAnchor();
                        sender.sendMessage("[tabletop] block size set");
                    }
                    default -> {
                        String json = Protocol.GSON.toJson(avatar.state(true));
                        sender.sendMessage("[tabletop] " + json);
                        getLogger().info("AVATAR_STATE " + json);
                        for (Entity e : avatar.entities()) {
                            Location l = e.getLocation();
                            getLogger().info(String.format("AVATAR_ENTITY %s id=%d %.2f %.2f %.2f yaw=%.1f", e.getType(), e.getEntityId(), l.getX(), l.getY(), l.getZ(), l.getYaw()));
                        }
                    }
                }
            }
            case "reload" -> { reloadConfig(); stop(); start(); sender.sendMessage("[tabletop] reloaded"); }
            default -> { return false; }
        }
        return true;
    }

    private void resubscribeAll() {
        RegionSpec r = defaultRegion();
        for (Session s : ready()) if (s.shared) { send(s, regionChanged(r, null, null)); subscribe(s, r, null); }
        updateTickets();
    }

    @Override
    public List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        if (args.length == 1) return List.of("status", "setorigin", "setsize", "avatar", "reload");
        if (args.length == 2 && args[0].equalsIgnoreCase("avatar")) return List.of("status", "spawn", "remove", "blocksize", "placement", "skin");
        if (args.length == 3 && args[0].equalsIgnoreCase("avatar") && args[1].equalsIgnoreCase("placement")) return List.of("floating", "ground", "exact");
        return List.of();
    }
}
