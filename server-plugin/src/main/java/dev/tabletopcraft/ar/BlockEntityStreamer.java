package dev.tabletopcraft.ar;

import com.destroystokyo.paper.profile.PlayerProfile;
import com.google.gson.JsonObject;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer;
import org.bukkit.Bukkit;
import org.bukkit.Chunk;
import org.bukkit.DyeColor;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.Tag;
import org.bukkit.World;
import org.bukkit.block.Banner;
import org.bukkit.block.Beacon;
import org.bukkit.block.Block;
import org.bukkit.block.BlockState;
import org.bukkit.block.Campfire;
import org.bukkit.block.Conduit;
import org.bukkit.block.CreatureSpawner;
import org.bukkit.block.DecoratedPot;
import org.bukkit.block.Shelf;
import org.bukkit.block.Sign;
import org.bukkit.block.Skull;
import org.bukkit.block.TrialSpawner;
import org.bukkit.block.Vault;
import org.bukkit.block.banner.Pattern;
import org.bukkit.block.sign.Side;
import org.bukkit.block.sign.SignSide;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.block.Action;
import org.bukkit.event.block.SignChangeEvent;
import org.bukkit.event.player.PlayerInteractEvent;
import org.bukkit.inventory.ItemStack;

import java.util.*;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Streams block_entities (PROTOCOL.md 4.29) to sessions with block_entities_v1: what a block state doesn't say about a
 * block entity (sign text, banner patterns, player head skins, pot sherds, spawned mobs, beacon tiers, campfire, shelf
 * and vault items, conduit activity). ChunkSnapshots carry no block entities, so this reads them on the main thread
 * with Chunk#getTileEntities (only the interesting block types, no snapshot copies), chunk by chunk under a per-tick
 * budget: chunks new to a region and chunks with block events, sign edits or right clicks first, then a round robin:
 * chunks with block entities that change by themselves (campfire items cook, vaults show items, beacons gain tiers)
 * every block-entities.rescan-ticks, the others every STATIC_PASSES times that (catches /data edits).
 * Results are cached per world chunk, so a glide only reads the chunks it brings, and each session gets per-chunk
 * deltas against what it holds (BlockEntityJson). Main thread, except the profile lookups of name-only heads.
 */
final class BlockEntityStreamer implements Listener {
    private final TabletopPlugin plugin;
    volatile boolean enabled = true;
    double budgetMs = 0.3;
    int rescanTicks = 40;

    /** Per world: chunk key -> its entries (position key -> JSON), as last read. */
    private final Map<String, Map<Long, Map<Long, String>>> cache = new HashMap<>();
    /** Per world: chunks to read before the round robin (new to a region, touched by events), in order. */
    private final Map<String, LinkedHashSet<Long>> priority = new HashMap<>();
    /** Per world: the chunks of interest (the union of the sessions' regions), and the round-robin cursor. */
    private final Map<String, List<Long>> interest = new HashMap<>();
    private final Map<String, Integer> cursor = new HashMap<>();
    private Set<RegionSpec> interestFor = Set.of();
    /** Per world: chunks holding block entities that change by themselves (cooking, vault items, beacon tiers...). */
    private final Map<String, Set<Long>> volatileChunks = new HashMap<>();
    /** Per world: chunk key -> the tick it was last read. */
    private final Map<String, Map<Long, Long>> lastRead = new HashMap<>();
    /** Chunks with only event-driven block entities are read again every this many round-robin passes. */
    static final int STATIC_PASSES = 8;
    private long tick;
    private Set<Material> types = Set.of();   // the block types read (configure, once tags are bound)
    private final Map<EntityType, double[]> sizes = new EnumMap<>(EntityType.class);
    // Player heads placed with only a name: their skins are looked up once, off the main thread.
    private final Map<String, String> headSkins = new ConcurrentHashMap<>();   // lower-case name or uuid -> url ("" = none)
    private final Set<String> lookups = ConcurrentHashMap.newKeySet();

    // Stats (BLOCK_ENTITIES log line every 30 s while sessions take them).
    private long ticks, chunksRead, entriesRead;
    private double mainMsSum, mainMsMax, readMsMax;
    private long lastLogMs = System.currentTimeMillis();
    private final AtomicLong windowBytes = new AtomicLong();
    String lastStats = "no block entity reads yet";

    BlockEntityStreamer(TabletopPlugin plugin) { this.plugin = plugin; }

    /** Block types with data worth sending: signs, banners, player heads, pots, spawners, beacons, campfires, shelves, vaults, conduits. */
    private static Set<Material> interestingTypes() {
        Set<Material> t = EnumSet.noneOf(Material.class);
        for (Material m : Material.values()) {
            if (!m.isBlock() || m.isLegacy()) continue;
            String n = m.name();
            if (Tag.ALL_SIGNS.isTagged(m) || Tag.BANNERS.isTagged(m) || n.endsWith("_SHELF")) t.add(m);
        }
        t.addAll(List.of(Material.PLAYER_HEAD, Material.PLAYER_WALL_HEAD, Material.DECORATED_POT, Material.SPAWNER, Material.TRIAL_SPAWNER,
            Material.BEACON, Material.CAMPFIRE, Material.SOUL_CAMPFIRE, Material.VAULT, Material.CONDUIT));
        return t;
    }

    void configure(boolean enabled, double budgetMs, int rescanTicks) {
        if (types.isEmpty()) types = interestingTypes();
        this.enabled = enabled;
        this.budgetMs = Math.max(0.05, budgetMs);
        this.rescanTicks = Math.max(1, rescanTicks);
    }

    // ---------- events: read the chunk again on the next tick ----------

    /** A block at (x, z) is about to change (TabletopPlugin.markDirty): its chunk is read again next tick. */
    void touch(String world, int x, int z) {
        if (!enabled || !interest.containsKey(world)) return;
        priority.computeIfAbsent(world, w -> new LinkedHashSet<>()).add(BlockEntityJson.chunkKey(x >> 4, z >> 4));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void on(SignChangeEvent e) { Block b = e.getBlock(); touch(b.getWorld().getName(), b.getX(), b.getZ()); }

    @EventHandler(priority = EventPriority.MONITOR)
    public void on(PlayerInteractEvent e) {
        // Food on a campfire, items on a shelf, a spawn egg on a spawner, dye or a glow ink sac on a sign...
        Block b = e.getClickedBlock();
        if (e.getAction() == Action.RIGHT_CLICK_BLOCK && b != null && types.contains(b.getType())) touch(b.getWorld().getName(), b.getX(), b.getZ());
    }

    // ---------- per tick ----------

    void tick(Collection<Session> all) {
        if (!enabled) return;
        tick++;
        List<Session> sessions = new ArrayList<>();
        for (Session s : all) if (s.wantsBlockEntities && s.region != null) sessions.add(s);
        if (sessions.isEmpty()) {
            if (!interest.isEmpty()) { interest.clear(); cursor.clear(); priority.clear(); cache.clear(); volatileChunks.clear(); lastRead.clear(); interestFor = Set.of(); }
            return;
        }
        long t0 = System.nanoTime();
        updateInterest(sessions);
        Map<Session, List<String>> adds = new HashMap<>();
        Map<Session, List<int[]>> removes = new HashMap<>();
        Set<Session> resets = new HashSet<>();
        // Sessions whose region changed: forget what's outside, send what the cache already knows for the rest.
        for (Session s : sessions) if (!s.region.equals(s.blockEntitiesRegion)) regionChanged(s, adds, removes, resets);
        // Read chunks: priority first, then the round robin, within the budget.
        long budget = (long) (budgetMs * 1e6);
        List<long[]> changed = new ArrayList<>();   // [world index into names, chunk key]
        List<String> names = new ArrayList<>(interest.keySet());
        for (int wi = 0; wi < names.size(); wi++) {
            String wn = names.get(wi);
            World w = Bukkit.getWorld(wn);
            if (w == null) continue;
            List<Long> chunks = interest.get(wn);
            Set<Long> inInterest = new HashSet<>(chunks);
            LinkedHashSet<Long> pri = priority.getOrDefault(wn, new LinkedHashSet<>());
            int quota = (chunks.size() + rescanTicks - 1) / rescanTicks;
            while (!pri.isEmpty() && System.nanoTime() - t0 < budget) {
                long k = pri.iterator().next();
                pri.remove(k);
                if (inInterest.contains(k) && read(w, k)) changed.add(new long[] { wi, k });
            }
            int cur = cursor.getOrDefault(wn, 0);
            var vol = volatileChunks.getOrDefault(wn, Set.of());
            var last = lastRead.getOrDefault(wn, Map.of());
            for (int n = 0; n < quota && !chunks.isEmpty() && System.nanoTime() - t0 < budget; n++) {
                cur = (cur + 1) % chunks.size();
                long k = chunks.get(cur);
                // Chunks with campfires, shelves, vaults, beacons, spawners or conduits every pass; the others (signs,
                // banners, heads, pots: they change through events) every STATIC_PASSES-th pass, to catch /data.
                if (!vol.contains(k) && tick - last.getOrDefault(k, Long.MIN_VALUE / 2) < (long) rescanTicks * STATIC_PASSES) continue;
                if (read(w, k)) changed.add(new long[] { wi, k });
            }
            cursor.put(wn, cur);
        }
        for (long[] c : changed) {
            String wn = names.get((int) c[0]);
            for (Session s : sessions) if (s.region.world().equals(wn)) deltaFor(s, wn, c[1], adds, removes);
        }
        for (Session s : sessions) {
            List<String> a = adds.getOrDefault(s, List.of());
            List<int[]> r = removes.getOrDefault(s, List.of());
            boolean reset = resets.contains(s);
            if (a.isEmpty() && r.isEmpty() && !reset) continue;
            plugin.sendCountingLater(s, () -> BlockEntityJson.message(BlockEntityJson.parse(a), r, reset), s.blockEntityBytes, windowBytes);   // JSON parsed off the main thread
        }
        double ms = (System.nanoTime() - t0) / 1e6;
        ticks++; mainMsSum += ms; mainMsMax = Math.max(mainMsMax, ms);
        long now = System.currentTimeMillis();
        if (now - lastLogMs >= 30_000) log(now, sessions.size());
    }

    /** The chunks the sessions' regions cover, per world; chunks new to it are read first. */
    private void updateInterest(List<Session> sessions) {
        Set<RegionSpec> regions = new HashSet<>();
        for (Session s : sessions) regions.add(s.region);
        if (regions.equals(interestFor)) return;
        interestFor = regions;
        Map<String, LinkedHashSet<Long>> next = new HashMap<>();
        for (RegionSpec r : regions) {
            var set = next.computeIfAbsent(r.world(), w -> new LinkedHashSet<>());
            // Nearest the region centre first, so a new area fills in from the middle.
            int ccx = (r.ox() + r.sx() / 2) >> 4, ccz = (r.oz() + r.sz() / 2) >> 4;
            List<int[]> cs = new ArrayList<>();
            for (int cx = r.ox() >> 4; cx <= (r.ox() + r.sx() - 1) >> 4; cx++)
                for (int cz = r.oz() >> 4; cz <= (r.oz() + r.sz() - 1) >> 4; cz++) cs.add(new int[] { cx, cz });
            cs.sort(Comparator.comparingInt(c -> Math.abs(c[0] - ccx) + Math.abs(c[1] - ccz)));
            for (int[] c : cs) set.add(BlockEntityJson.chunkKey(c[0], c[1]));
        }
        for (var e : next.entrySet()) {
            String wn = e.getKey();
            var known = cache.computeIfAbsent(wn, w -> new HashMap<>());
            var pri = priority.computeIfAbsent(wn, w -> new LinkedHashSet<>());
            for (long k : e.getValue()) if (!known.containsKey(k)) pri.add(k);
            interest.put(wn, new ArrayList<>(e.getValue()));
            // Forget chunks no region needs any more.
            known.keySet().retainAll(e.getValue());
            pri.retainAll(e.getValue());
        }
        interest.keySet().retainAll(next.keySet());
        cache.keySet().retainAll(next.keySet());
        priority.keySet().retainAll(next.keySet());
        volatileChunks.keySet().retainAll(next.keySet());
        lastRead.keySet().retainAll(next.keySet());
        for (var e2 : next.entrySet()) {
            volatileChunks.getOrDefault(e2.getKey(), new HashSet<>()).retainAll(e2.getValue());
            lastRead.getOrDefault(e2.getKey(), new HashMap<>()).keySet().retainAll(e2.getValue());
        }
    }

    private void regionChanged(Session s, Map<Session, List<String>> adds, Map<Session, List<int[]>> removes, Set<Session> resets) {
        RegionSpec r = s.region;
        // A session's first region, or one in another world: the client forgets what it holds (reset), we start afresh.
        if (s.blockEntitiesRegion == null || !s.blockEntitiesRegion.world().equals(r.world())) { s.blockEntitiesSent.clear(); resets.add(s); }
        BlockEntityJson.prune(s.blockEntitiesSent, k -> r.containsWorld(BlockEntityJson.x(k), BlockEntityJson.y(k), BlockEntityJson.z(k)), ck -> chunkInRegion(r, ck));
        s.blockEntitiesRegion = r;
        var known = cache.get(r.world());
        if (known == null) return;
        for (long ck : interest.getOrDefault(r.world(), List.of())) if (known.containsKey(ck) && chunkInRegion(r, ck)) deltaFor(s, r.world(), ck, adds, removes);
    }

    private static boolean chunkInRegion(RegionSpec r, long ck) {
        int cx = (int) (ck >> 32), cz = (int) ck;
        return cx >= r.ox() >> 4 && cx <= (r.ox() + r.sx() - 1) >> 4 && cz >= r.oz() >> 4 && cz <= (r.oz() + r.sz() - 1) >> 4;
    }

    private void deltaFor(Session s, String world, long ck, Map<Session, List<String>> adds, Map<Session, List<int[]>> removes) {
        if (!chunkInRegion(s.region, ck)) return;
        var entries = cache.get(world).getOrDefault(ck, Map.of());
        var held = s.blockEntitiesSent.computeIfAbsent(ck, k -> new HashMap<>());
        RegionSpec r = s.region;
        BlockEntityJson.diff(entries, held, k -> r.containsWorld(BlockEntityJson.x(k), BlockEntityJson.y(k), BlockEntityJson.z(k)),
            adds.computeIfAbsent(s, x -> new ArrayList<>()), removes.computeIfAbsent(s, x -> new ArrayList<>()));
    }

    // ---------- reading ----------

    /** Reads one chunk's block entities into the cache; true when they differ from the last read. */
    private boolean read(World w, long ck) {
        int cx = (int) (ck >> 32), cz = (int) ck;
        var known = cache.computeIfAbsent(w.getName(), x -> new HashMap<>());
        if (!w.isChunkLoaded(cx, cz)) return false;   // region chunks are ticketed (TabletopPlugin); read when it's back
        long r0 = System.nanoTime();
        Chunk c = w.getChunkAt(cx, cz);
        Map<Long, String> m = new HashMap<>();
        boolean changing = false;
        for (BlockState st : c.getTileEntities(b -> types.contains(b.getType()), false)) {
            JsonObject e;
            try { e = entry(w, st); } catch (RuntimeException ex) { e = null; }
            if (e != null) m.put(BlockEntityJson.key(st.getX(), st.getY(), st.getZ()), Protocol.GSON.toJson(e));
            changing |= st instanceof Campfire || st instanceof Shelf || st instanceof Vault || st instanceof Beacon || st instanceof CreatureSpawner
                || st instanceof TrialSpawner || st instanceof Conduit;
        }
        var vol = volatileChunks.computeIfAbsent(w.getName(), x -> new HashSet<>());
        if (changing) vol.add(ck); else vol.remove(ck);
        lastRead.computeIfAbsent(w.getName(), x -> new HashMap<>()).put(ck, tick);
        chunksRead++; entriesRead += m.size();
        readMsMax = Math.max(readMsMax, (System.nanoTime() - r0) / 1e6);
        Map<Long, String> old = known.put(ck, m);
        return old == null ? !m.isEmpty() : !old.equals(m);
    }

    private static final PlainTextComponentSerializer PLAIN = PlainTextComponentSerializer.plainText();

    private static BlockEntityJson.Side side(SignSide s) {
        List<String> lines = new ArrayList<>(4);
        for (Component c : s.lines()) lines.add(PLAIN.serialize(c));
        DyeColor col = s.getColor();
        return new BlockEntityJson.Side(lines, col == null ? "black" : dye(col), s.isGlowingText());
    }

    static String dye(DyeColor c) { return c.name().toLowerCase(Locale.ROOT); }

    private static String item(ItemStack i) { return i == null || i.getType().isAir() ? null : i.getType().getKey().toString(); }

    private JsonObject entry(World w, BlockState st) {
        int x = st.getX(), y = st.getY(), z = st.getZ();
        if (st instanceof Sign s) return BlockEntityJson.sign(x, y, z, side(s.getSide(Side.FRONT)), side(s.getSide(Side.BACK)));
        if (st instanceof Banner b) {
            List<String[]> ps = new ArrayList<>();
            for (Pattern p : b.getPatterns()) ps.add(new String[] { org.bukkit.Registry.BANNER_PATTERN.getKeyOrThrow(p.getPattern()).toString(), dye(p.getColor()) });
            return BlockEntityJson.banner(x, y, z, ps);
        }
        if (st instanceof Skull k) return skull(w, x, y, z, k);
        if (st instanceof DecoratedPot p) {
            var sh = p.getSherds();
            List<String> l = new ArrayList<>();
            for (DecoratedPot.Side sd : new DecoratedPot.Side[] { DecoratedPot.Side.BACK, DecoratedPot.Side.LEFT, DecoratedPot.Side.RIGHT, DecoratedPot.Side.FRONT }) {
                Material m = sh.get(sd);
                l.add(m == null ? "minecraft:brick" : m.getKey().toString());
            }
            return BlockEntityJson.pot(x, y, z, l);
        }
        if (st instanceof CreatureSpawner cs) {
            EntityType t = cs.getSpawnedType();
            return t == null ? null : BlockEntityJson.spawner("spawner", x, y, z, t.getKey().toString(), size(w, t), false);
        }
        if (st instanceof TrialSpawner ts) {
            var conf = ts.isOminous() ? ts.getOminousConfiguration() : ts.getNormalConfiguration();
            EntityType t = conf == null ? null : conf.getSpawnedType();
            return t == null ? null : BlockEntityJson.spawner("trial_spawner", x, y, z, t.getKey().toString(), size(w, t), ts.isOminous());
        }
        if (st instanceof Beacon b) return BlockEntityJson.beacon(x, y, z, b.getTier());
        if (st instanceof Campfire c) {
            List<String> l = new ArrayList<>();
            for (int i = 0; i < c.getSize(); i++) l.add(item(c.getItem(i)));
            return BlockEntityJson.items("campfire", x, y, z, l);
        }
        if (st instanceof Shelf sh) {
            List<String> l = new ArrayList<>();
            var inv = sh.getInventory();
            for (int i = 0; i < inv.getSize(); i++) l.add(item(inv.getItem(i)));
            return BlockEntityJson.items("shelf", x, y, z, l);
        }
        if (st instanceof Vault v) return BlockEntityJson.vault(x, y, z, item(v.getDisplayedItem()));
        if (st instanceof Conduit c) return BlockEntityJson.conduit(x, y, z, c.isActive());
        return null;
    }

    private JsonObject skull(World w, int x, int y, int z, Skull k) {
        Material t = k.getType();
        if (t != Material.PLAYER_HEAD && t != Material.PLAYER_WALL_HEAD) return null;
        PlayerProfile p = k.getPlayerProfile();
        if (p == null) return null;
        String name = p.getName(), uuid = p.getId() != null ? p.getId().toString() : null, skin = null;
        try { if (p.getTextures().getSkin() != null) skin = p.getTextures().getSkin().toString(); } catch (RuntimeException ignored) { }
        if (skin == null) skin = lookedUp(name, uuid, w.getName(), x, z);
        return BlockEntityJson.skull(x, y, z, skin, name, uuid);
    }

    /**
     * A name- or UUID-only head: its skin from an earlier lookup, else null after starting one (off the main thread);
     * when it finishes, the head's chunk is read again on the next tick.
     */
    private String lookedUp(String name, String uuid, String world, int x, int z) {
        String key = uuid != null ? uuid : name != null && SkinLookup.VALID_NAME.matcher(name).matches() ? name.toLowerCase(Locale.ROOT) : null;
        if (key == null) return null;
        String done = headSkins.get(key);
        if (done != null) return done.isEmpty() ? null : done;
        if (lookups.size() >= 4 || !lookups.add(key)) return null;
        PlayerProfile profile = uuid != null ? Bukkit.createProfile(UUID.fromString(uuid), name) : Bukkit.createProfile(name);
        Bukkit.getScheduler().runTaskAsynchronously(plugin, () -> {
            String url = "";
            try {
                if (profile.complete(true, true) && profile.getTextures().getSkin() != null) url = profile.getTextures().getSkin().toString();
                else if (uuid == null) {
                    // An offline-mode server maps a name to an offline UUID without textures: ask the public API for
                    // the account's UUID, then Paper for its textures (as the giant's skin_player does).
                    UUID id = SkinLookup.uuidForName(name);
                    PlayerProfile byId = id == null ? null : Bukkit.createProfile(id, name);
                    if (byId != null && byId.complete(true, true) && byId.getTextures().getSkin() != null) url = byId.getTextures().getSkin().toString();
                }
            } catch (Exception e) { plugin.getLogger().fine("head skin " + key + ": " + e); }
            headSkins.put(key, url);
            lookups.remove(key);
            // Read the chunk again: this head gets its skin, and heads that waited for a free lookup start theirs.
            if (plugin.isEnabled()) Bukkit.getScheduler().runTask(plugin, () -> touch(world, x, z));
        });
        return null;
    }

    /** Width and height of a spawner's mob (SpawnerRenderer scales big mobs down), measured once per type. */
    private double[] size(World w, EntityType t) {
        if (sizes.containsKey(t)) return sizes.get(t);
        double[] s = null;
        try {
            var cls = t.getEntityClass();
            if (cls != null) {
                Entity e = w.createEntity(new Location(w, 0, w.getMinHeight(), 0), cls);
                s = new double[] { e.getWidth(), e.getHeight() };
            }
        } catch (RuntimeException e) { s = null; }
        sizes.put(t, s);
        return s;
    }

    private void log(long now, int sessions) {
        double secs = (now - lastLogMs) / 1000.0;
        lastStats = String.format("BLOCK_ENTITIES ticks=%d chunks_read=%d entries_read=%d main_ms avg=%.3f max=%.3f chunk_read_ms_max=%.3f bytes_per_s=%d sessions=%d cached_chunks=%d",
            ticks, chunksRead, entriesRead, mainMsSum / Math.max(1, ticks), mainMsMax, readMsMax, Math.round(windowBytes.getAndSet(0) / secs), sessions,
            cache.values().stream().mapToInt(Map::size).sum());
        plugin.getLogger().info(lastStats);
        ticks = chunksRead = entriesRead = 0; mainMsSum = mainMsMax = readMsMax = 0;
        lastLogMs = now;
    }
}
