package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.destroystokyo.paper.profile.PlayerProfile;
import org.bukkit.HeightMap;
import org.bukkit.profile.PlayerTextures;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.data.BlockData;
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

    private volatile LinkServer link;   // [talk] volatile: read by the async chat handler
    private ExecutorService outbound;
    private final RegionReader reader = new RegionReader(this);
    private final java.util.Set<RegionSpec> diffInFlight = new java.util.HashSet<>();
    private final GiantAvatar avatar = new GiantAvatar();
    private EditWorld editWorld;                          // applies block_edit (onEnable: needs the logger)
    private final ConcurrentLinkedQueue<Runnable> queue = new ConcurrentLinkedQueue<>();
    private record PoseSet(Session s, Pose head, Pose left, Pose right, long seq, int snapshotId) {}
    /** Server capabilities in welcome.caps (PROTOCOL.md 4.17-4.18). */
    static final List<String> SERVER_CAPS = List.of("view_margin", SectionCodec.ENCODING, "pose_snapshot_id");
    private int[] sharedMargin = new int[3];              // view_margin of the shared region (config region.view_margin)
    private long configSaveAt = -1;                       // debounced saveConfig (region changes while panning)
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
    // [nav] Incremental region moves (RegionMerge): while one is read, diffs of the shared region are held back (their
    // result would change the base the move copies from) and the region is diffed again once the move is sent.
    private int movesInFlight;
    private boolean rediffAfterMove;
    private final List<RegionSpec> moveTargets = new ArrayList<>();   // regions being read for a region_request
    private long moveStartedMs;
    private long lastDirtyNs = Long.MIN_VALUE;            // last markDirty inside a session's region (System.nanoTime)
    private long sharedEstBytes = -1;                     // max_bytes estimate of the shared region (sharedEstRegion)
    private RegionSpec sharedEstRegion;
    private long tickCount;
    private Session avatarOwner;
    private long avatarOwnerGoneMs = -1;
    // Light/biome extras (PROTOCOL.md 4.4.1): config switches, and a re-diff a few ticks after block changes so
    // light the engine propagates after the change event reaches clients quickly (the safety diff catches the rest).
    private volatile boolean lightOn = true, biomesOn = true;
    private long lightRecheckTick = -1;
    static final int LIGHT_RECHECK_TICKS = 4, MAX_LIGHT_CHANGES = 16384;
    private final EntityStreamer entities = new EntityStreamer(this);   // entities_v1 (PROTOCOL.md 4.21)
    private final ActionListener actions = new ActionListener(this);    // actions_v1 (PROTOCOL.md 4.22)
    private final BlockEntityStreamer blockEntities = new BlockEntityStreamer(this);   // [blocks] block_entities_v1 (PROTOCOL.md 4.29)
    private final WorldStateStreamer worldState = new WorldStateStreamer(this);   // [living] world_v1: time and weather
    // Block editing from the headset (block_edit, PROTOCOL.md 4.20), config section "edit". Read on websocket threads.
    private volatile boolean editEnabled = true, editRequireToken, editTokenSet, editPhysics = true;
    private volatile int editRate = 20;
    private volatile Set<String> editDeny = Set.of();
    // [menu] Placeable blocks for the headset's block menu (block_registry, PROTOCOL.md 4.24), built on start/reload.
    private volatile BlockRegistry blockRegistry = BlockRegistry.empty();
    // [talk] chat, commands and server status for the headset (PROTOCOL.md 4.26-4.28)
    final ControlService control = new ControlService(this);
    // [hands] using blocks, items and mobs from the headset (interact_v1, config section interact)
    final InteractService interact = new InteractService(this);
    // [power] undo/redo, box edits and brushes from the headset (power_v1, config section edit)
    final PowerService power = new PowerService(this);
    // [operator] worlds and spawn, shared bookmarks, container view and editing, sign editing
    final OperatorService operator = new OperatorService(this);
    // [comfort] LAN discovery (PROTOCOL.md 7, config.yml discovery): answers the headset's UDP probe on its own thread.
    private LanBeacon beacon;
    private volatile int onlinePlayers;
    // [sound] Block sound groups for sound_v1 headsets (built on start/reload) and opt-in asset delivery (config assets).
    private volatile SoundGroups soundGroups = SoundGroups.empty();
    private volatile boolean soundEnabled = true;
    AssetService assets;   // onEnable (needs the logger)

    @Override public void onEnable() {
        saveDefaultConfig();
        editWorld = new EditWorld(getLogger(), avatar::entities);
        assets = new AssetService(getLogger(), this::send, this::allSessions);   // [sound]
        getServer().getPluginManager().registerEvents(new BlockListener(this), this);
        getServer().getPluginManager().registerEvents(actions, this);
        getServer().getPluginManager().registerEvents(blockEntities, this);   // [blocks] sign edits, interactions
        getServer().getPluginManager().registerEvents(control, this);   // [talk]
        interact.init(editWorld);                                        // [hands]
        power.init(editWorld);                                           // [power]
        getServer().getPluginManager().registerEvents(interact, this);   // [hands] unfreeze-all for mobs in chunks loading later
        start();
    }

    @Override public void onDisable() { stop(); reader.shutdown(); }

    private void start() {
        var c = getConfig();
        // [nav] A config.yml written with the 0.3.0 caps (288 / 16777216) can't hold a 256-wide window with its glide
        // margin: raise it to the new defaults (an operator's own values are left alone).
        RegionCaps old = RegionCaps.OLD_DEFAULT;
        if (c.getInt("max_region.x", -1) == old.maxX() && c.getInt("max_region.z", -1) == old.maxZ() && c.getLong("max_region.blocks", -1) == old.maxBlocks()) {
            c.set("max_region.x", RegionCaps.DEFAULT.maxX()); c.set("max_region.z", RegionCaps.DEFAULT.maxZ()); c.set("max_region.blocks", RegionCaps.DEFAULT.maxBlocks());
            saveConfig();
            getLogger().info(String.format("config: max_region raised from the 0.3.0 defaults to %dx%dx%d, %d blocks (room for the glide margin)",
                RegionCaps.DEFAULT.maxX(), c.getInt("max_region.y", 384), RegionCaps.DEFAULT.maxZ(), RegionCaps.DEFAULT.maxBlocks()));
        }
        RegionCaps d0 = RegionCaps.DEFAULT;
        caps = new RegionCaps(c.getInt("max_region.x", d0.maxX()), c.getInt("max_region.y", d0.maxY()), c.getInt("max_region.z", d0.maxZ()),
            c.getLong("max_region.blocks", d0.maxBlocks()), c.getLong("max_region.bytes", d0.maxBytes()),
            Math.max(1, c.getInt("max_region.view_width", d0.maxViewWidth())));   // [area] PROTOCOL.md 4.25
        avatar.blockSizeM = c.getDouble("avatar.block-size-m", 0.01);
        avatar.maxScale = c.getDouble("avatar.max-scale", 16.0);
        avatar.userHeightM = c.getDouble("avatar.user-height-m", 1.75);
        avatar.skinPlayer = defaultSkinPlayer();
        avatar.interpolationTicks = c.getInt("avatar.interpolation-ticks", 2);
        RegionSpec d = defaultRegion();
        sharedMargin = RegionRequest.clampMargin(new int[] { c.getInt("region.view_margin.x", 0), c.getInt("region.view_margin.y", 0), c.getInt("region.view_margin.z", 0) }, d.sx(), d.sy(), d.sz());
        avatar.placement = GiantPlacement.Mode.parse(c.getString("avatar.placement", "floating"));
        avatar.headSizeM = c.getDouble("avatar.head-size-m", 0.25);
        avatar.handSizeM = c.getDouble("avatar.hand-size-m", 0.1);
        avatar.maxHeadBlocks = c.getDouble("avatar.max-head-blocks", 64);
        avatar.handBlockSetting = c.getString("avatar.hand-block", "auto");
        avatar.smoothing = c.getDouble("avatar.smoothing", 0.5);
        reader.snapshotBudgetMs = Math.max(0, c.getDouble("stream.snapshot-budget-ms", 1.5));   // [area] spread big reads over ticks
        lightOn = c.getBoolean("stream.light", true);
        biomesOn = c.getBoolean("stream.biomes", true);
        entities.configure(c.getBoolean("entities.enabled", true), c.getInt("entities.max", 256), c.getInt("entities.margin", 2));
        actions.enabled = c.getBoolean("actions.enabled", true);
        blockEntities.configure(c.getBoolean("block-entities.enabled", true), c.getDouble("block-entities.budget-ms", 0.3), c.getInt("block-entities.rescan-ticks", 40));   // [blocks]
        worldState.enabled = c.getBoolean("world-state.enabled", true);   // [living]
        editEnabled = c.getBoolean("edit.enabled", true);
        editRequireToken = c.getBoolean("edit.require-token", false);
        editTokenSet = !c.getString("auth.token", "").isEmpty();
        editRate = Math.max(1, c.getInt("edit.max-per-second", 20));
        editPhysics = c.getBoolean("edit.physics", true);
        Set<String> deny = new HashSet<>();
        for (String id : c.getStringList("edit.deny")) deny.add(BlockEdit.blockId(id));
        editDeny = Set.copyOf(deny);
        buildBlockRegistry();   // [menu]
        control.configure(c);   // [talk]
        interact.configure(c);  // [hands]
        power.configure(c);     // [power]
        operator.configure(c);  // [operator]
        World iw = Bukkit.getWorld(defaultRegion().world());
        interact.buildItemRegistry(iw != null ? iw : Bukkit.getWorlds().isEmpty() ? null : Bukkit.getWorlds().get(0));
        soundEnabled = c.getBoolean("sound.enabled", true);   // [sound]
        buildSoundGroups();
        assets.configure(c.getBoolean("assets.serve", false), c.getString("assets.dir", ""), !c.getString("auth.token", "").isEmpty(),
            c.getDouble("assets.max-mb-per-second", 8));
        outbound = Executors.newSingleThreadExecutor(r -> { Thread t = new Thread(r, "tabletop-outbound"); t.setDaemon(true); return t; });
        link = new LinkServer(this, c.getString("bind.host", "0.0.0.0"), c.getInt("bind.port", 8765), c.getString("bind.path", "/tabletop"), c.getString("auth.token", ""));
        link.start();
        startDiscovery();
        tickTask = Bukkit.getScheduler().runTaskTimer(this, this::tick, 1, 1);
    }

    /** [comfort] The discovery responder (LanBeacon). Its reply reads only values fixed here and a volatile player count. */
    private void startDiscovery() {
        var c = getConfig();
        if (!c.getBoolean("discovery.enabled", true)) return;
        int udp = c.getInt("discovery.port", 8765), wsPort = c.getInt("bind.port", 8765);
        String path = c.getString("bind.path", "/tabletop"), version = getPluginMeta().getVersion(), mc = Bukkit.getMinecraftVersion();
        String name = c.getString("discovery.name", "");
        if (name == null || name.isBlank())
            name = net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer.plainText().serialize(Bukkit.motd());
        boolean token = !c.getString("auth.token", "").isEmpty();
        String shown = name;
        onlinePlayers = Bukkit.getOnlinePlayers().size();
        try {
            beacon = LanBeacon.start(getLogger(), udp, () -> LanBeacon.reply(shown, wsPort, path, token, version, mc, onlinePlayers));
            getLogger().info("LAN discovery on UDP " + udp + " as \"" + shown + "\" (headset: menu > Info > Server > Find servers)");
        } catch (java.net.SocketException e) {
            getLogger().warning("LAN discovery off: UDP " + udp + " unavailable (" + e.getMessage() + "); set discovery.port or discovery.enabled in config.yml");
        }
    }

    private void stop() {
        if (tickTask != null) tickTask.cancel();
        assets.stop();   // [sound]
        interact.shutdown();   // [hands] held mobs get their gravity and AI back
        power.shutdown();      // [power] running box edits stop (their sessions close)
        operator.shutdown();   // [operator] container lids close
        if (beacon != null) { beacon.stop(); beacon = null; }
        if (configSaveAt >= 0) { configSaveAt = -1; saveConfig(); }
        movesInFlight = 0; moveTargets.clear();   // [nav]
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
    void offerPose(Session s, Pose h, Pose l, Pose r, long seq, int snapshotId) { latestPose.set(new PoseSet(s, h, l, r, seq, snapshotId)); }
    boolean lightEnabled() { return lightOn; }
    boolean biomesEnabled() { return biomesOn; }
    boolean entitiesEnabled() { return entities.enabled; }
    boolean actionsEnabled() { return actions.enabled; }
    boolean blockEntitiesEnabled() { return blockEntities.enabled; }   // [blocks]
    boolean worldStateEnabled() { return worldState.enabled; }
    /** [living] world_state goes out in order with the snapshots (the outbound thread). */
    void sendWorldState(Session s, JsonObject body) { send(s, body); }
    void send(Session s, JsonObject body) { if (outbound != null && !outbound.isShutdown()) outbound.execute(() -> s.send(body)); }
    /** Like send, adding the UTF-8 size to a counter (entities bytes/s). */
    void sendCounting(Session s, JsonObject body, java.util.concurrent.atomic.AtomicLong bytes, java.util.concurrent.atomic.AtomicLong total) {
        if (outbound != null && !outbound.isShutdown()) outbound.execute(() -> { int n = s.sendCounted(body); bytes.addAndGet(n); total.addAndGet(n); });
    }

    /** [blocks] Like sendCounting, building the body on the outbound thread too (big block_entities messages). */
    void sendCountingLater(Session s, java.util.function.Supplier<JsonObject> body, java.util.concurrent.atomic.AtomicLong bytes, java.util.concurrent.atomic.AtomicLong total) {
        if (outbound != null && !outbound.isShutdown()) outbound.execute(() -> { int n = s.sendCounted(body.get()); bytes.addAndGet(n); total.addAndGet(n); });
    }

    /** [talk] Every connected session, any state (thread-safe view). */
    Collection<Session> allSessions() { LinkServer l = link; return l == null ? List.of() : l.sessions(); }

    /** [talk] The operator's chat line as a speech bubble over the giant's head, for Java players. */
    void giantSays(String text, int seconds) { if (avatar.spawned()) avatar.say(text, seconds); }

    void markDirty(String world, int x, int y, int z) {
        if (link == null) return;
        blockEntities.touch(world, x, z);   // [blocks] its chunk's block entities are read again next tick
        for (Session s : link.sessions()) if (s.region != null && s.region.world().equals(world) && s.region.containsWorld(x, y, z)) { dirty = true; lastDirtyNs = System.nanoTime(); return; }
        // [nav] A region being read for a move: note the change so the region is diffed again once the move is sent.
        for (RegionSpec m : moveTargets) if (m.world().equals(world) && m.containsWorld(x, y, z)) { lastDirtyNs = System.nanoTime(); return; }
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
        power.tick();   // [power] box edits, undo, redo and brush strokes within edit.box-budget-ms; history
        applyPose();
        int safety = Math.max(1, getConfig().getInt("stream.safety-diff-ticks", 20));
        // Big regions take longer to re-read: stretch the safety-net period (events still trigger a diff right away).
        long biggest = 0;
        for (Session s : ready()) biggest = Math.max(biggest, s.region.volume());
        safety *= (int) Math.max(1, biggest / 4_000_000L);
        if (dirty || tickCount % safety == 0 || tickCount == lightRecheckTick) {
            if (dirty) lightRecheckTick = tickCount + LIGHT_RECHECK_TICKS;
            dirty = false;
            diffAll();
        }
        if (tickCount % Math.max(1, getConfig().getInt("stream.players-every-ticks", 2)) == 0) { sendPlayers(); entities.tick(ready()); }
        actions.flush(ready());   // swings, digging, breaks, damage of this tick (PROTOCOL.md 4.22)
        blockEntities.tick(ready());   // [blocks] sign text, banner patterns, heads... (PROTOCOL.md 4.29), budgeted
        worldState.tick(ready());   // [living] time of day and weather, on change and about once a second
        control.tick(tickCount);  // [talk] late command output, server_status
        interact.tick();          // [hands] held mobs that got no update for 3 s are let go
        operator.tick(tickCount); // [operator] open container views: changes re-sent, gone ones closed
        avatar.tickBubble();      // [talk] the giant's speech bubble follows the head and expires
        if (tickCount % 20 == 0) { timeouts(); onlinePlayers = Bukkit.getOnlinePlayers().size(); }
        // [nav] A move read that never finished (a chunk load failed) must not hold diffs back forever.
        if (movesInFlight > 0 && System.currentTimeMillis() - moveStartedMs > 15000) {
            getLogger().warning("region move read did not finish; diffs resume");
            movesInFlight = 0; moveTargets.clear(); rediffAfterMove = false; dirty = true;
        }
        if (tickCount % 600 == 0) { String rs = reader.takeStats(); if (rs != null) getLogger().info("READS " + rs); }   // [area] main-thread cost of reads, per 30 s
        if (tickCount % 10 == 0) updateTickets();   // follow the giant
        // A headset gliding across the world changes the region every second or so: save the config at most every 5 s.
        if (configSaveAt >= 0 && System.currentTimeMillis() >= configSaveAt) { configSaveAt = -1; saveConfig(); }
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
        reg.add("view_margin", Protocol.ints(sharedMargin));
        w.add("region", reg);
        JsonObject lim = new JsonObject();
        caps.addTo(lim);
        addBuildHeight(lim, r.world());
        power.limits(s, lim);   // [power] undo_depth, box_max_blocks, brush limits
        lim.addProperty("pose_hz_max", 60);
        lim.addProperty("player_hz", 20 / Math.max(1, getConfig().getInt("stream.players-every-ticks", 2)));
        w.add("limits", lim);
        w.addProperty("avatar_supported", true);
        // Caps in effect for this session: the optional features it asked for and was granted (light_v1, biomes_v1,
        // sections_v1, edit_v1), plus what this server always does (view_margin, pose_snapshot_id). PROTOCOL.md 4.5.1, 4.17-4.18, 4.20.
        com.google.gson.JsonArray sc = new com.google.gson.JsonArray();
        if (s.wantsLight) sc.add(Protocol.CAP_LIGHT);
        if (s.wantsBiomes) sc.add(Protocol.CAP_BIOMES);
        if (sectionsFor(s)) sc.add(SectionCodec.ENCODING);
        if (s.wantsEntities) sc.add(Protocol.CAP_ENTITIES);   // entities (PROTOCOL.md 4.21)
        if (s.wantsActions) sc.add(Protocol.CAP_ACTIONS);     // actions (PROTOCOL.md 4.22)
        if (s.wantsBlockEntities) sc.add(Protocol.CAP_BLOCK_ENTITIES);   // [blocks] block entity data (PROTOCOL.md 4.29)
        if (s.wantsWorld) sc.add(Protocol.CAP_WORLD);          // [living] world_state
        sc.add("view_margin");
        sc.add("pose_snapshot_id");
        if (s.canEdit) sc.add(Protocol.CAP_EDIT);   // block editing (PROTOCOL.md 4.20)
        // [talk] chat, commands, status (PROTOCOL.md 4.26-4.28)
        if (s.wantsChat) sc.add(ControlMessages.CAP_CHAT);
        if (s.canCommand) sc.add(ControlMessages.CAP_COMMAND);
        if (s.wantsStatus) sc.add(ControlMessages.CAP_STATUS);
        if (s.canInteract) sc.add(InteractMessages.CAP);   // [hands]
        if (s.wantsSound) sc.add(Protocol.CAP_SOUND);      // [sound]
        if (s.canAssets) sc.add(AssetService.CAP);
        if (power.granted(s)) sc.add(PowerMessages.CAP);   // [power]
        if (s.wantsMarks) sc.add(OperatorMessages.CAP_MARKS);   // [operator]
        w.add("caps", sc);
        control.welcome(s, w);
        interact.welcome(s, w);   // [hands] welcome.interact: the switches
        operator.welcome(s, w);   // [operator] welcome.worlds, welcome.marks, welcome.interact containers/signs
        send(s, w);
        send(s, blockRegistry.message());   // [menu] every placeable block, once per connection (PROTOCOL.md 4.24)
        if (s.canInteract) send(s, interact.itemRegistryMessage());   // [hands] usable items and spawn eggs
        if (s.wantsSound) send(s, soundGroups.message());   // [sound] each block's break/step/place/hit/fall sounds
        if (s.wantsMarks) send(s, operator.bookmarksMessage());       // [operator] the shared bookmarks
        subscribe(s, r, null);
        assets.offer(s);   // [sound] the operator's own files, when asset delivery is on and hashed (else when it is)
    }

    /** [sound] Reads every block's sound group (main thread, start/reload) and logs the message size. */
    private void buildSoundGroups() {
        long t0 = System.nanoTime();
        try { soundGroups = SoundGroups.scan(); }
        catch (RuntimeException e) { getLogger().warning("sound groups: " + e); soundGroups = SoundGroups.empty(); return; }
        int bytes = Protocol.GSON.toJson(soundGroups.message()).getBytes(java.nio.charset.StandardCharsets.UTF_8).length;
        getLogger().info(String.format("SOUND_GROUPS %d blocks in %d groups, %d bytes, %.1f ms", soundGroups.blocks(), soundGroups.size(), bytes,
            (System.nanoTime() - t0) / 1e6));
    }

    boolean soundEnabled() { return soundEnabled; }

    /** [sound] A sound for sound_v1 headsets at a position (InteractService: the sound it plays for a headset's use). */
    void soundAt(Location l, String sound, float volume, float pitch) {
        if (sound != null) actions.sound(l, new SoundRules.Play(SoundGroups.key(sound), volume, pitch), false);
    }

    /** [menu] Reads the placeable blocks (item registry order, categories from tags and names) and logs the message size. */
    private void buildBlockRegistry() {
        long t0 = System.nanoTime();
        World rw = Bukkit.getWorld(defaultRegion().world());
        if (rw == null && !Bukkit.getWorlds().isEmpty()) rw = Bukkit.getWorlds().get(0);
        try {
            blockRegistry = BlockRegistry.build(RegistryScan.candidates(rw), editDeny);
        } catch (RuntimeException e) {
            getLogger().warning("block registry: " + e);
            blockRegistry = BlockRegistry.empty();
            return;
        }
        int bytes = Protocol.GSON.toJson(blockRegistry.message()).getBytes(java.nio.charset.StandardCharsets.UTF_8).length;
        getLogger().info(String.format("REGISTRY %d placeable blocks %s, %d variants, %d bytes, %.1f ms", blockRegistry.size(),
            blockRegistry.counts(), blockRegistry.variants().size(), bytes, (System.nanoTime() - t0) / 1e6));
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
        // [operator] Following a player is the headset's job (it glides and jumps with region_request; PROTOCOL.md "Following a player").
        if (follow) send(s, Protocol.error("unsupported", "subscribe.follow is not supported: headsets follow players themselves (region_request)", refSeq, false));
        subscribe(s, new RegionSpec(wn, origin[0], y0, origin[2], dims[0], y1 - y0, dims[2]), refSeq);
    }

    private void subscribe(Session s, RegionSpec r, Long refSeq) {
        s.region = r;
        s.lastGrid = null; // pause diffs until the new snapshot is captured
        updateTickets();
        reader.readAsync(r, s.readExtras(), d -> { if (r.equals(s.region) && s.state == Session.State.READY) sendSnapshot(s, d); });
    }

    private void sendSnapshot(Session s, RegionData d) { sendSnapshot(s, d, false); }

    boolean sectionsFor(Session s) { return s.caps.contains(SectionCodec.ENCODING) && getConfig().getBoolean("stream.sections-v1", true); }

    /**
     * Sends the session's region. Clients with the "sections_v1" cap get it as 16^3 sections split into messages of
     * at most stream.max-message-bytes, and as a delta (only sections whose blocks or light changed) when the region
     * moved by whole sections from what they already have (PROTOCOL.md 4.18). Others get one rle_palette_v1 message.
     * Light and biomes go to sessions that asked for them (PROTOCOL.md 4.5.1): per section, or whole-region.
     */
    private void sendSnapshot(Session s, RegionData d, boolean forceFull) {
        long t0 = System.nanoTime();
        long readNs = (long) (d.stats().readMs() * 1e6);
        int[] grid = d.grid();
        byte[] light = s.wantsLight ? d.light() : null;
        BiomeGrid biomes = s.wantsBiomes ? d.biomes() : null;
        RegionSpec r = s.region;
        boolean sectioned = sectionsFor(s);
        int[] oldGrid = s.lastGrid;
        byte[] oldLight = s.lastLight;
        int baseId = s.snapshotId;
        // A delta keeps the session palette (base indices stay valid); cap its growth with an occasional full snapshot.
        // With light, the client's light must be known too (lastLight), since unlisted sections keep theirs.
        RegionSpec oldRegion = s.gridRegion;   // [area] may have other dims (a resize delta, PROTOCOL.md 4.25)
        int[] shift = sectioned && !forceFull && oldGrid != null && s.paletteSize < 4096 && (light == null || oldLight != null)
            ? SectionCodec.deltaShift(oldRegion, r) : null;
        // [nav] An incremental read copied its kept sections from exactly what this client has (same arrays): the delta
        // is just the sections it read, no O(volume) compare.
        RegionMerge.Base rb = d.base();
        List<int[]> exactKeys = shift != null && rb != null && rb.grid() == oldGrid && rb.region().equals(oldRegion)
            && (light == null || rb.light() == oldLight) ? d.fresh() : null;
        s.lastGrid = grid;
        s.lastLight = light;
        s.lastBiomes = biomes;
        s.gridRegion = r;
        s.lastGridNs = d.capturedNs();
        s.snapshotId++;
        s.rememberRegion(s.snapshotId, r);
        int id = s.snapshotId;
        int[] margin = s.shared ? sharedMargin.clone() : new int[3];
        long maxMsg = Math.max(64 * 1024, getConfig().getLong("stream.max-message-bytes", 1L << 20));
        long mainNs = System.nanoTime() - t0;
        // Palette remap, encode and serialize run on the outbound executor: it's one thread in message order, so the
        // session palette (also extended by block_update, see applyDiff) always matches what the client has seen.
        if (outbound != null) outbound.execute(() -> {
            long m0 = System.nanoTime();
            Palette pal = reader.palette();
            if (shift == null) {
                s.resetPalette();
                int air = pal.id("minecraft:air");
                boolean hasAir = false;
                for (int g : grid) if (g == air) { hasAir = true; break; }
                if (hasAir) s.localIndex(air, pal, null); // index 0 = air when present
            }
            int[] map = new int[pal.size()];   // the global palette only grows: every id in grid is below its size now
            java.util.Arrays.fill(map, -1);
            int dx = r.sx(), dy = r.sy(), dz = r.sz();
            // [nav] Sectioned: work out the listed sections first and remap only their blocks (a one-section shift of a
            // 288-wide region lists 180 of 3,240 sections). v1: the whole grid, as one message.
            List<int[]> keys = !sectioned ? null : shift == null ? SectionCodec.allSections(dx, dy, dz) : exactKeys != null ? exactKeys
                : SectionCodec.changedSections(oldGrid, grid, light != null ? oldLight : null, light,
                    new int[] { oldRegion.sx(), oldRegion.sy(), oldRegion.sz() }, new int[] { dx, dy, dz }, shift);
            int[] local = null;
            if (sectioned) {
                for (int[] k : keys) {
                    int x0 = k[0] * 16, y0 = k[1] * 16, z0 = k[2] * 16, x1 = Math.min(dx, x0 + 16), y1 = Math.min(dy, y0 + 16), z1 = Math.min(dz, z0 + 16);
                    for (int y = y0; y < y1; y++) for (int z = z0; z < z1; z++)
                        for (int i = (y * dz + z) * dx + x0, e = i + x1 - x0; i < e; i++) { int g = grid[i]; if (map[g] < 0) map[g] = s.localIndex(g, pal, null); }
                }
            } else {
                local = new int[grid.length];
                for (int i = 0; i < grid.length; i++) {
                    int g = grid[i];
                    int l = map[g];
                    if (l < 0) { l = s.localIndex(g, pal, null); map[g] = l; }
                    local[i] = l;
                }
            }
            List<String> palette = List.copyOf(s.palette);
            long remapNs = System.nanoTime() - m0 + mainNs;
            long e0 = System.nanoTime();
            JsonObject timing = new JsonObject();
            timing.addProperty("read_ms", Math.round(readNs / 1e4) / 100.0);
            timing.addProperty("remap_ms", Math.round(remapNs / 1e4) / 100.0);
            if (sectioned) {
                List<SectionCodec.Section> secs = new ArrayList<>(keys.size());
                for (int[] k : keys)
                    secs.add(new SectionCodec.Section(k[0], k[1], k[2], SectionCodec.encodeSection(grid, map, dx, dy, dz, k[0], k[1], k[2]),
                        light != null ? SectionCodec.encodeSectionLight(light, dx, dy, dz, k[0], k[1], k[2]) : null));
                long fixed = 400;
                for (String p : palette) fixed += p.length() + 3;
                // [nav] Biomes ride on part 0 only, RLE-encoded: estimate them as such. Counting 4 bytes per cell on every
                // part made a 352-wide region (1.24 MB of cells, over max-message-bytes) send each section in its own part,
                // the palette repeated 220 times: 6.8 MB for a one-section shift.
                long biomeBytes = biomes != null ? 200 + biomes.palette.size() * 40L + BlockCodec.encode(biomes.cells).length * 6L : 0;
                List<List<SectionCodec.Section>> parts = SectionCodec.split(secs, maxMsg, fixed, biomeBytes);
                long encNs = System.nanoTime() - e0;
                timing.addProperty("encode_ms", Math.round(encNs / 1e4) / 100.0);
                long j0 = System.nanoTime(), bytes = 0, runs = 0, lightRuns = 0;
                for (SectionCodec.Section sec : secs) { runs += sec.data().length / 2; if (sec.light() != null) lightRuns += sec.light().length / 2; }
                for (int i = 0; i < parts.size(); i++) {
                    JsonObject msg = Protocol.chunkSnapshotSections(id, r, palette, margin, i, parts.size(), shift != null ? baseId : -1, shift, parts.get(i),
                        light != null, biomes);
                    if (i == 0) msg.add("server_timing", timing);
                    bytes += s.sendCounted(msg);
                }
                int total = SectionCodec.sections(dx) * SectionCodec.sections(dy) * SectionCodec.sections(dz);
                getLogger().info(String.format("SNAPSHOT session=%s dims=%dx%dx%d blocks=%d palette=%d encoding=sections_v1 %s sections=%d/%d parts=%d runs=%d light_runs=%d biomes=%d bytes=%d read_ms=%.2f remap_ms=%.2f encode_ms=%.2f json_send_ms=%.2f",
                    s.id, dx, dy, dz, r.volume(), palette.size(), shift != null ? "delta=" + shift[0] + "," + shift[1] + "," + shift[2] + (exactKeys != null ? " listed=read" : " listed=compared") : "full",
                    secs.size(), total, parts.size(), runs,
                    light != null ? lightRuns : -1, biomes != null ? biomes.palette.size() : -1, bytes, readNs / 1e6, remapNs / 1e6, encNs / 1e6, (System.nanoTime() - j0) / 1e6));
                return;
            }
            int[] data = BlockCodec.encode(local);
            long encNs = System.nanoTime() - e0;
            JsonObject msg = Protocol.chunkSnapshot(id, r, palette, data);
            long x0 = System.nanoTime();
            Protocol.addExtras(msg, light, biomes);
            long extrasNs = System.nanoTime() - x0;
            msg.add("view_margin", Protocol.ints(margin));   // PROTOCOL.md 4.17
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

    /** The client couldn't apply a snapshot: send its region again, in full (at most every 2 s per session). */
    void onBadSnapshot(Session s) {
        if (s.state != Session.State.READY || s.region == null) return;
        long now = System.currentTimeMillis();
        if (now - s.lastFullResendMs < 2000) return;
        s.lastFullResendMs = now;
        RegionSpec r = s.region;
        if (s.lastGrid != null && r.equals(s.gridRegion)) {
            // Resend exactly what the client should have: the last snapshot plus every block_update sent since. A
            // fresh read would race the update stream (changes not yet diffed would be in the resend and also in
            // nothing the client can check against). Newer changes follow as block_updates for the new snapshot id.
            getLogger().info("BAD_SNAPSHOT from session " + s.id + ": resending what it should have, in full");
            sendSnapshot(s, new RegionData(s.lastGrid, s.lastLight, s.lastBiomes, 0, null, null, s.lastGridNs, RegionData.Stats.NONE), true);
            return;
        }
        getLogger().info("BAD_SNAPSHOT from session " + s.id + ": resending in full");
        reader.readAsync(r, s.readExtras(), d -> { if (r.equals(s.region) && s.state == Session.State.READY) sendSnapshot(s, d, true); });
    }

    private void addBuildHeight(JsonObject limits, String world) {
        World w = Bukkit.getWorld(world);
        if (w != null) limits.add("build_height", Protocol.ints(w.getMinHeight(), w.getMaxHeight()));   // PROTOCOL.md 4.17
    }

    private void diffAll() {
        java.util.Set<RegionSpec> regions = new java.util.HashSet<>();
        for (Session s : ready()) if (s.lastGrid != null) regions.add(s.region);
        for (RegionSpec r : regions) {
            // A diff of this region is still reading: stay dirty so a change made since it started goes out right after
            // it, not with the next light recheck or safety diff (headset edits measured 350 ms instead of 150 ms).
            if (!diffInFlight.add(r)) { dirty = true; continue; }
            Map<Session, Integer> gen = new HashMap<>();
            Map<Session, int[]> base = new HashMap<>();
            Map<Session, byte[]> baseLight = new HashMap<>();
            int extras = 0; // read light/biomes if any session on this region wants them (a re-snapshot may need biomes)
            for (Session s : ready()) if (r.equals(s.region) && s.lastGrid != null) {
                gen.put(s, s.snapshotId); base.put(s, s.lastGrid); baseLight.put(s, s.lastLight); extras |= s.readExtras();
            }
            try {
                // The O(volume) compares run on the async worker (sessions usually share one base array).
                reader.readAsync(r, extras, d -> {
                    Diff diff = new Diff();
                    for (int[] old : base.values()) diff.blocks.computeIfAbsent(old, o -> changedIndices(o, d.grid(), 4097));
                    if (d.light() != null)
                        for (byte[] old : baseLight.values()) if (old != null) diff.light.computeIfAbsent(old, o -> lightDiff(o, d.light(), r, MAX_LIGHT_CHANGES + 1));
                    return diff;
                }, (d, diff) -> { diffInFlight.remove(r); applyDiff(r, d, gen, base, baseLight, diff); });
            } catch (IllegalStateException e) { diffInFlight.remove(r); }
        }
    }

    /** Block and light changes against each distinct base array (identity), computed off the main thread. */
    private static final class Diff {
        final Map<int[], int[]> blocks = new java.util.IdentityHashMap<>();
        final Map<byte[], List<int[]>> light = new java.util.IdentityHashMap<>();
    }

    /** Indices where the grids differ, stopping after limit (more than 4096 means "send a snapshot instead"). */
    static int[] changedIndices(int[] old, int[] g, int limit) {
        if (old.length != g.length) return null;
        int[] out = new int[64];
        int n = 0;
        for (int i = 0; i < g.length && n < limit; i++) {
            if (g[i] == old[i]) continue;
            if (n == out.length) out = java.util.Arrays.copyOf(out, n * 2);
            out[n++] = i;
        }
        return java.util.Arrays.copyOf(out, n);
    }

    /** Cells whose packed light changed, as block_update "light" entries [x, y, z, value], stopping after limit. */
    static List<int[]> lightDiff(byte[] old, byte[] now, RegionSpec r, int limit) {
        List<int[]> out = new ArrayList<>();
        if (old.length != now.length) return out;
        for (int i = 0; i < now.length && out.size() < limit; i++)
            if (old[i] != now[i]) out.add(new int[] { i % r.sx(), i / (r.sx() * r.sz()), (i / r.sx()) % r.sz(), now[i] & 0xFF });
        return out;
    }

    private void applyDiff(RegionSpec r, RegionData d, Map<Session, Integer> gen, Map<Session, int[]> base, Map<Session, byte[]> baseLight, Diff diff) {
        int[] g = d.grid();
        for (var e : gen.entrySet()) {
            Session s = e.getKey();
            // Drop results that started before a newer snapshot, a region change or another diff.
            if (s.state != Session.State.READY || !r.equals(s.region) || s.snapshotId != e.getValue() || s.lastGrid == null
                || s.lastGrid != base.get(s) || s.lastLight != baseLight.get(s)) continue;
            // [nav] A move of the shared region is being read from lastGrid: keep it as it is, diff again after the move.
            if (movesInFlight > 0 && s.shared) { rediffAfterMove = true; continue; }
            int[] idx = diff.blocks.get(s.lastGrid);
            if (idx == null) continue;
            // [blocks] /setblock, /fill, /data... fire no block events: the chunks the diff found changes in are read again.
            if (idx.length <= 4096) for (int i : idx) blockEntities.touch(r.world(), r.ox() + i % r.sx(), r.oz() + (i / r.sx()) % r.sz());
            boolean withLight = s.wantsLight && d.light() != null;
            List<int[]> light = withLight && s.lastLight != null ? diff.light.getOrDefault(s.lastLight, List.of()) : List.of();
            // Many changes: a snapshot instead (a sectioned client gets only the changed sections, against lastGrid).
            if (idx.length > 4096 || light.size() > MAX_LIGHT_CHANGES) { sendSnapshot(s, d); continue; }
            s.lastGrid = g;
            s.lastGridNs = d.capturedNs();
            if (withLight) s.lastLight = d.light();
            if (idx.length == 0 && light.isEmpty()) continue;
            int snap = s.snapshotId;
            List<int[]> lightOut = light;
            // Palette indices are assigned on the outbound thread, in message order with snapshots (see sendSnapshot).
            if (outbound != null) outbound.execute(() -> {
                List<String> added = new ArrayList<>();
                List<Protocol.Change> out = new ArrayList<>(idx.length);
                for (int i : idx) {
                    int lx = i % r.sx(), lz = (i / r.sx()) % r.sz(), ly = i / (r.sx() * r.sz());
                    out.add(new Protocol.Change(lx, ly, lz, s.localIndex(g[i], reader.palette(), added)));
                }
                s.send(Protocol.blockUpdate(snap, added, out, lightOut));
            });
        }
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
                // [comfort] near the region on all three axes; no spectators, invisible or vanished players (PlayerFilter)
                if (!PlayerFilter.near(r, l.getX(), l.getY(), l.getZ(), margin) || !PlayerFilter.shown(p, Bukkit.getOnlinePlayers())) continue;
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
                EntitySampler.playerExtras(e, p);   // additive (PROTOCOL.md 4.6): id, vehicle, pose, equipment
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
        interact.onSessionClosed(s);   // [hands] let go of what it held
        power.onSessionClosed(s);      // [power] its journal and jobs go
        operator.onSessionClosed(s);   // [operator] its container view ends (the lid closes)
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
        o.add("view_margin", Protocol.ints(sharedMargin));   // PROTOCOL.md 4.17
        // [comfort] the new region's limits, build height included: after a move to another world the headset must not
        // keep gliding inside the old world's height (PROTOCOL.md 4.13)
        JsonObject lim = new JsonObject();
        caps.addTo(lim);
        addBuildHeight(lim, r.world());
        o.add("limits", lim);
        o.addProperty("by_session", by == null ? "server" : by.id);
        if (refSeq != null && by != null) o.addProperty("ref_seq", refSeq);
        return o;
    }

    /** Shared-region change requested by a headset: validate, check byte cap, then last-writer-wins broadcast. */
    void onRegionRequest(Session s, RegionRequest q, int[] viewMargin, Long refSeq) {
        if (s.state != Session.State.READY) return;
        String wn = q.world() == null ? defaultRegion().world() : q.world();
        World w = Bukkit.getWorld(wn);
        if (w == null) { send(s, Protocol.error("world_not_found", "no world " + wn, refSeq, false)); return; }
        // [area] A visible width over max_region.view_width is narrowed around its centre, not refused (PROTOCOL.md 4.25).
        RegionRequest asked = q;
        q = caps.clampView(q, viewMargin);
        if (q != asked) getLogger().info(String.format("REGION_CLAMP by=%s dims=%dx%dx%d -> %dx%dx%d (view width max %d)",
            s.id, asked.dx(), asked.dy(), asked.dz(), q.dx(), q.dy(), q.dz(), caps.maxViewWidth()));
        String why = caps.check(q.dx(), q.dy(), q.dz());
        if (why != null) { getLogger().info("REGION_REJECT " + why); send(s, tooLarge(why, refSeq)); return; }
        int y0 = Math.max(q.oy(), w.getMinHeight()), y1 = Math.min(q.oy() + q.dy(), w.getMaxHeight());
        if (y1 <= y0) { send(s, tooLarge("region is outside the build height " + w.getMinHeight() + ".." + w.getMaxHeight(), refSeq)); return; }
        RegionSpec r = new RegionSpec(wn, q.ox(), y0, q.oz(), q.dx(), y1 - y0, q.dz());
        int[] margin = RegionRequest.clampMargin(viewMargin, r.sx(), r.sy(), r.sz());
        long t0 = System.nanoTime();
        int extras = s.readExtras(); // every shared session gets this read: include what any of them wants
        for (Session o : ready()) if (o.shared) extras |= o.readExtras();
        // [nav] Moving or resizing in the same world: read only the sections that weren't in the old region and copy
        // the rest from what the headset has (RegionMerge). Diffs of the shared region wait until it is sent.
        RegionMerge.Base base = moveBase(s, r, extras);
        long prevEst = sharedEstBytes;
        RegionSpec prevEstRegion = sharedEstRegion;
        if (base != null) { movesInFlight++; moveTargets.add(r); moveStartedMs = System.currentTimeMillis(); }
        // Byte cap: estimate from the global-palette RLE (same run structure as each session's encoding), plus the
        // light/biome fields (estimated by the reader), all off the main thread. [nav] An incremental read scales the
        // previous region's estimate to the kept volume and adds the fresh sections' own estimate.
        reader.readAsync(r, extras, base, d -> {
            if (d.base() != null && prevEst >= 0 && d.base().region().equals(prevEstRegion)) {
                long kept = r.volume() - RegionMerge.volume(d.fresh(), r);
                long biomeBytes = d.biomes() == null ? 0 : 200 + d.biomes().palette.size() * 40L + BlockCodec.encode(d.biomes().cells).length * 4L;
                return prevEst * kept / prevEstRegion.volume() + RegionMerge.estimateSectionBytes(d.grid(), d.light(), r, d.fresh()) + biomeBytes;
            }
            int[] data = BlockCodec.encode(d.grid());
            java.util.Set<Integer> ids = new java.util.HashSet<>();
            for (int k = 0; k < data.length; k += 2) ids.add(data[k]);
            List<String> pal = new ArrayList<>();
            for (int id : ids) pal.add(reader.palette().state(id));
            long extra = d.extraBytes();
            if (d.base() != null) {   // an incremental read without a previous estimate: the reader left the extras out
                if (d.light() != null) extra += BlockCodec.estimateEncodedBytes(d.light()) + 40;
                if (d.biomes() != null) extra += 200 + d.biomes().palette.size() * 40L + BlockCodec.encode(d.biomes().cells).length * 4L;
            }
            return RegionRequest.estimateSnapshotBytes(data, pal) + extra;
        }, (d, est) -> {
            if (base != null) endMove(r, base);
            if (s.state != Session.State.READY) return;
            if (est > caps.maxBytes()) {
                String msg = String.format("snapshot would be about %d bytes, over max %d (try a smaller region)", est, caps.maxBytes());
                getLogger().info("REGION_REJECT " + msg);
                send(s, tooLarge(msg, refSeq));
                return;
            }
            RegionData.Stats st = d.stats();
            getLogger().info(String.format("REGION_SET by=%s %s origin=%d,%d,%d dims=%dx%dx%d est_bytes=%d total_ms=%.1f chunk_load_ms=%.1f snapshot_main_ms=%.2f snapshot_ticks=%d walk_async_ms=%.1f read=%s sections_read=%d/%d chunks=%d copy_ms=%.1f",
                s.id, wn, r.ox(), r.oy(), r.oz(), r.sx(), r.sy(), r.sz(), est, (System.nanoTime() - t0) / 1e6, st.loadMs(), st.snapMainMs(), st.snapTicks(), st.walkMs(),
                d.base() != null ? "incremental" : "full", st.sectionsRead(), st.sectionsTotal(), st.chunks(), st.copyMs()));
            sharedEstBytes = est; sharedEstRegion = r;
            getConfig().set("region.world", wn);
            getConfig().set("region.origin.x", r.ox()); getConfig().set("region.origin.y", r.oy()); getConfig().set("region.origin.z", r.oz());
            getConfig().set("region.dims.x", r.sx()); getConfig().set("region.dims.y", r.sy()); getConfig().set("region.dims.z", r.sz());
            sharedMargin = margin;
            getConfig().set("region.view_margin.x", margin[0]); getConfig().set("region.view_margin.y", margin[1]); getConfig().set("region.view_margin.z", margin[2]);
            if (configSaveAt < 0) configSaveAt = System.currentTimeMillis() + 5000;   // debounced (see tick)
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

    /**
     * [nav] What an incremental read of r can copy its kept sections from: the requesting session's last grid (what its
     * client has), else another shared session's; null (a full read) when none has a usable one.
     */
    private RegionMerge.Base moveBase(Session s, RegionSpec r, int extras) {
        List<Session> order = new ArrayList<>();
        order.add(s);
        for (Session o : ready()) if (o != s && o.shared) order.add(o);
        for (Session o : order) {
            if (o.lastGrid == null || o.gridRegion == null || o.lastGrid.length != o.gridRegion.volume()) continue;
            if (o.lastLight != null && o.lastLight.length != o.lastGrid.length) continue;
            RegionMerge.Base b = new RegionMerge.Base(o.gridRegion, o.lastGrid, o.lastLight, o.lastBiomes, o.lastGridNs);
            if (RegionMerge.usable(b, r, extras)) return b;
        }
        return null;
    }

    /** [nav] A move's read is done: once no other move is in flight, diff again if anything changed since its base. */
    private void endMove(RegionSpec r, RegionMerge.Base base) {
        movesInFlight = Math.max(0, movesInFlight - 1);
        moveTargets.remove(r);
        if (lastDirtyNs >= base.capturedNs()) rediffAfterMove = true;
        if (movesInFlight == 0 && rediffAfterMove) { rediffAfterMove = false; dirty = true; }
    }

    void onRegionQuery(Session s, String playerName, Long refSeq) {
        if (s.state != Session.State.READY) return;
        JsonObject o = Protocol.body("region_info");
        RegionSpec r = s.region != null ? s.region : defaultRegion();
        JsonObject reg = new JsonObject();
        reg.addProperty("world", r.world());
        reg.add("origin", Protocol.ints(r.ox(), r.oy(), r.oz()));
        reg.add("dims", Protocol.ints(r.sx(), r.sy(), r.sz()));
        reg.add("view_margin", Protocol.ints(s.shared ? sharedMargin : new int[3]));
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
        addBuildHeight(lim, r.world());
        o.add("limits", lim);
        operator.regionInfo(o, r.world());   // [operator] the region world's spawn and every world
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        send(s, o);
    }

    // ---------- block editing from the headset (PROTOCOL.md 4.20) ----------
    /** edit_v1 is granted when edit.enabled and, with edit.require-token, the server has an auth.token (so every headset had to present it). */
    boolean editAllowed() { return editEnabled && (!editRequireToken || editTokenSet); }
    int editRate() { return editRate; }

    /** [power] Why a box edit or brush may not place this block id (edit.deny, or not in block_registry); null if it may. */
    String refusePlace(String id) {
        if (editDeny.contains(id)) return EditRules.label(id) + " can't be placed from the headset (edit.deny)";
        if (!blockRegistry.placeable(id)) return EditRules.label(id) + " is not a placeable block (not in block_registry)";
        return null;
    }

    /** [power] A block id from edit.deny (a clone leaves those out). */
    boolean denied(String id) { return editDeny.contains(id); }

    /**
     * Applies one block_edit on the main thread: only inside the session's region and the world border, only blocks
     * from block_registry and not from edit.deny, the way vanilla places and breaks them (EditWorld: replaceable
     * cells, nobody in the way, both halves of doors, beds and tall plants, persistent leaves, waterlogging from the
     * cell, connections to the neighbours). A plugin's setBlockData fires no block events, so the region is marked
     * dirty here and the change reaches every headset through the normal block_update diff (this tick).
     */
    void onBlockEdit(Session s, BlockEdit e, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.canEdit) { denyEdit(s, e, "edit_denied", editAllowed() ? "ask for edit_v1 in hello.caps" : "block editing is off on this server (edit.enabled / edit.require-token)", refSeq); return; }
        RegionSpec r = s.region;
        if (r == null || !r.containsWorld(e.x(), e.y(), e.z())) { denyEdit(s, e, "out_of_region", "position is outside the table's region", refSeq); return; }
        World w = Bukkit.getWorld(r.world());
        if (w == null) { denyEdit(s, e, "world_not_found", "no world " + r.world(), refSeq); return; }
        if (!w.isChunkLoaded(e.x() >> 4, e.z() >> 4)) { denyEdit(s, e, "edit_denied", "chunk not loaded", refSeq); return; }
        long t0 = System.nanoTime();
        EditWorld.Outcome o;
        if (e.op() == BlockEdit.Op.BREAK) o = editWorld.breakBlock(w, e, editPhysics);
        else {
            BlockData next;
            try { next = Bukkit.createBlockData(e.state()); }
            catch (IllegalArgumentException ex) { denyEdit(s, e, "bad_state", "not a block state: " + e.state(), refSeq); return; }
            String id = BlockEdit.blockId(next.getAsString());
            if (next.getMaterial().isAir()) { denyEdit(s, e, "bad_state", "use op break to remove a block", refSeq); return; }
            if (editDeny.contains(id)) { denyEdit(s, e, "edit_denied", EditRules.label(id) + " can't be placed from the headset (edit.deny)", refSeq); return; }
            if (!blockRegistry.placeable(id)) {   // [menu] only blocks from block_registry (PROTOCOL.md 4.24)
                denyEdit(s, e, "bad_state", EditRules.label(id) + " is not a placeable block (not in block_registry)", refSeq); return;
            }
            o = editWorld.place(w, r, e, next, editPhysics);
        }
        if (o.denied() != null) { denyEdit(s, e, o.denied().code(), o.denied().message(), refSeq); return; }
        if (o.cells().isEmpty()) { getLogger().info(String.format("EDIT session=%s %s %d,%d,%d: already air", s.id, e.op().id(), e.x(), e.y(), e.z())); return; }
        for (int[] c : o.cells()) markDirty(w.getName(), c[0], c[1], c[2]);
        getLogger().info(String.format("EDIT session=%s %s %s %d,%d,%d%s %s -> %s%s (%.2f ms)", s.id, e.op().id(), w.getName(), e.x(), e.y(), e.z(),
            e.face() == null ? "" : " face=" + e.face(), o.before(), o.after(), o.cells().size() > 1 ? " cells=" + o.cells().size() : "", (System.nanoTime() - t0) / 1e6));
    }

    private void denyEdit(Session s, BlockEdit e, String code, String why, Long refSeq) {
        getLogger().info(String.format("EDIT_DENIED session=%s %s %d,%d,%d %s: %s", s.id, e.op().id(), e.x(), e.y(), e.z(), code, why));
        send(s, e.error(code, why, refSeq));
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
        // The origin of the snapshot the headset's frame is in (pose.snapshot_id): while a moved region is in flight
        // the headset still places RegionRoot for the previous one, and mapping with the new origin would jump.
        RegionSpec r = ps.s().regionFor(ps.snapshotId());
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
        // [nav] Plus a ring of chunks around each region (stream.ticket-ring): the next glide shift's chunks are then
        // loaded already, so its read captures them in the same tick instead of loading them first.
        int ring = Math.max(0, getConfig().getInt("stream.ticket-ring", 1));
        for (RegionSpec r : regions) {
            Set<Long> keys = want.computeIfAbsent(r.world(), k -> new HashSet<>());
            for (int cx = (r.ox() >> 4) - ring; cx <= ((r.ox() + r.sx() - 1) >> 4) + ring; cx++)
                for (int cz = (r.oz() >> 4) - ring; cz <= ((r.oz() + r.sz() - 1) >> 4) + ring; cz++) keys.add(chunkKey(cx, cz));
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
        String sub0 = args[0].toLowerCase(Locale.ROOT);
        // [talk] /tabletop tell is for every player (tabletop.tell, default true); the rest needs tabletop.admin.
        if (sub0.equals("tell")) {
            if (!sender.hasPermission("tabletop.tell")) { sender.sendMessage("[tabletop] you may not message the headsets (tabletop.tell)"); return true; }
            if (args.length < 2) { sender.sendMessage("[tabletop] usage: /tabletop tell <message>"); return true; }
            int n = control.tell(sender, String.join(" ", Arrays.copyOfRange(args, 1, args.length)));
            sender.sendMessage(n < 0 ? "[tabletop] message is empty or too long" : n == 0 ? "[tabletop] no headset with chat is connected" : "[tabletop] sent to " + n + (n == 1 ? " headset" : " headsets"));
            return true;
        }
        if (!sender.hasPermission("tabletop.admin")) { sender.sendMessage("[tabletop] you need tabletop.admin for that"); return true; }
        switch (sub0) {
            case "status" -> {
                RegionSpec r = defaultRegion();
                sender.sendMessage(String.format("[tabletop] default region %s origin %d %d %d dims %d %d %d; clients %d; avatar %s scale %.2f placement %s; poses applied %d",
                    r.world(), r.ox(), r.oy(), r.oz(), r.sx(), r.sy(), r.sz(), ready().size(), avatar.spawned() ? "spawned" : "none", avatar.scale(), avatar.placement.id(), avatar.posesApplied));
                sender.sendMessage("[tabletop] " + control.stats() + "; commands " + (control.commandsAllowed() ? "on, deny " + control.denyList() : "off (" + control.commandsOffReason() + ")"));   // [talk]
                sender.sendMessage("[tabletop] " + interact.stats());   // [hands]
                String aw = assets.why();   // [sound]
                sender.sendMessage(String.format("[tabletop] sound %s (%d blocks in %d groups); assets %s", soundEnabled ? "on" : "off",
                    soundGroups.blocks(), soundGroups.size(), aw != null ? "off (" + aw + ")"
                        : assets.ready() ? "serving " + assets.files().keySet() + String.format(", sent %d files %.1f MB, refused %d", assets.sentFiles, assets.sentBytes / 1e6, assets.refused)
                        : "hashing..."));
                sender.sendMessage("[tabletop] " + power.stats());      // [power]
                sender.sendMessage("[tabletop] " + operator.stats());   // [operator]
            }
            case "unfreeze-all" -> {   // [hands] every mob a headset froze (frozen.txt), also ones in chunks loaded later
                int[] r = interact.unfreezeAll();
                sender.sendMessage("[tabletop] unfroze " + r[0] + " mobs" + (r[1] > 0 ? "; " + r[1] + " more unfreeze when their chunks load" : ""));
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
            case "entities" -> sender.sendMessage("[tabletop] " + entities.lastStats + "; actions messages=" + actions.sent + " bytes=" + actions.bytes.get() + " dropped=" + actions.dropped);
            case "reload" -> { reloadConfig(); stop(); start(); sender.sendMessage("[tabletop] reloaded"); }
            default -> { return false; }
        }
        return true;
    }

    private void resubscribeAll() {
        RegionSpec r = defaultRegion();
        sharedMargin = RegionRequest.clampMargin(sharedMargin, r.sx(), r.sy(), r.sz());
        for (Session s : ready()) if (s.shared) { send(s, regionChanged(r, null, null)); subscribe(s, r, null); }
        updateTickets();
    }

    @Override
    public List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        if (args.length == 1) return sender.hasPermission("tabletop.admin") ? List.of("status", "setorigin", "setsize", "avatar", "entities", "reload", "tell", "unfreeze-all") : List.of("tell");
        if (args.length == 2 && args[0].equalsIgnoreCase("avatar")) return List.of("status", "spawn", "remove", "blocksize", "placement", "skin");
        if (args.length == 3 && args[0].equalsIgnoreCase("avatar") && args[1].equalsIgnoreCase("placement")) return List.of("floating", "ground", "exact");
        return List.of();
    }
}
