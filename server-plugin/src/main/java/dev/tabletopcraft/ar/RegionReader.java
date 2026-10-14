package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.Chunk;
import org.bukkit.ChunkSnapshot;
import org.bukkit.World;
import org.bukkit.block.Biome;
import org.bukkit.plugin.Plugin;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.function.Consumer;

/**
 * Reads regions into dense arrays of global palette ids (ARCHITECTURE.md 3.2):
 * chunks are loaded with getChunkAtAsync, ChunkSnapshots are taken on the main thread (cheap), and the
 * O(volume) walk runs on an async worker. The callback runs back on the main thread.
 * With {@link #LIGHT} / {@link #BIOMES} the same walk also reads per-block light and 4x4x4 biomes (PROTOCOL.md 4.4.1);
 * without them the snapshot skips copying light arrays and biome containers.
 */
public final class RegionReader {
    public static final int LIGHT = 1, BIOMES = 2;

    private final Plugin plugin;
    private final Palette palette = new Palette();
    /** Biome key -> [temperature, downfall]; World#getTemperature/getHumidity are only called on the main thread. */
    private final Map<String, double[]> climate = new ConcurrentHashMap<>();

    /** Timings of the last completed read, for logs/tests. lastSnapMs is main-thread time over lastSnapTicks ticks. */
    public volatile double lastLoadMs, lastSnapMs, lastWalkMs;
    public volatile int lastSnapTicks = 1;
    /** [area] Main-thread time per tick for taking ChunkSnapshots (config stream.snapshot-budget-ms; 0 = no limit). */
    public volatile double snapshotBudgetMs = 1.5;

    public RegionReader(Plugin plugin) { this.plugin = plugin; }

    /**
     * [nav] Walks run on this pool rather than Bukkit's async scheduler, which only starts queued tasks on its next
     * 50 ms heartbeat: that was a whole tick of every glide shift. Daemon threads; idle ones exit after 30 s.
     */
    private final java.util.concurrent.ThreadPoolExecutor workers = new java.util.concurrent.ThreadPoolExecutor(3, 3, 30, java.util.concurrent.TimeUnit.SECONDS,
        new java.util.concurrent.LinkedBlockingQueue<>(), r -> { Thread t = new Thread(r, "tabletop-reader"); t.setDaemon(true); return t; });
    { workers.allowCoreThreadTimeOut(true); }

    /** Plugin disable: stop the walk threads (a walk in progress finishes; its callback is dropped once disabled). */
    public void shutdown() { workers.shutdown(); }

    public Palette palette() { return palette; }

    public void readAsync(RegionSpec r, Consumer<int[]> done) { readAsync(r, 0, d -> done.accept(d.grid())); }

    /** extras: a combination of {@link #LIGHT} and {@link #BIOMES} (0 = blocks only). */
    public void readAsync(RegionSpec r, int extras, Consumer<RegionData> done) { readAsync(r, extras, d -> null, (d, x) -> done.accept(d)); }

    /**
     * Like {@link #readAsync(RegionSpec, int, Consumer)}, plus a step that runs on the async worker right after the
     * walk (diffs, size estimates: O(volume) work that must stay off the main thread); its result goes to done.
     */
    public <T> void readAsync(RegionSpec r, int extras, java.util.function.Function<RegionData, T> offThread, java.util.function.BiConsumer<RegionData, T> done) {
        readAsync(r, extras, null, offThread, done);
    }

    /**
     * [nav] With a usable base (RegionMerge.usable: same world, overlapping, with the extras this read needs), only the
     * sections of r that weren't fully inside the base region are read: just their chunks are loaded and captured on
     * the main thread, and the walk fills those sections into a copy of the base's kept sections (RegionData.base and
     * .fresh say which). A one-section shift of a 288-wide region then captures one row of chunks and walks 1/18 of it.
     */
    public <T> void readAsync(RegionSpec r, int extras, RegionMerge.Base base, java.util.function.Function<RegionData, T> offThread,
                              java.util.function.BiConsumer<RegionData, T> done) {
        World w = Bukkit.getWorld(r.world());
        boolean light = (extras & LIGHT) != 0, biomes = (extras & BIOMES) != 0;
        if (w == null) throw new IllegalStateException("world not loaded: " + r.world());
        long t0 = System.nanoTime();
        boolean[] fresh = RegionMerge.usable(base, r, extras) ? RegionMerge.freshSections(base.region(), r) : null;
        RegionMerge.Base b = fresh != null ? base : null;
        List<long[]> coords = new ArrayList<>();
        if (fresh != null) {
            for (long k : RegionMerge.freshChunks(fresh, r)) coords.add(new long[] { (int) (k >> 32), (int) k });
        } else {
            for (int cx = r.ox() >> 4; cx <= (r.ox() + r.sx() - 1) >> 4; cx++)
                for (int cz = r.oz() >> 4; cz <= (r.oz() + r.sz() - 1) >> 4; cz++) coords.add(new long[] { cx, cz });
        }
        Map<Long, ChunkSnapshot> snaps = new HashMap<>();
        load(w, coords, 0, () -> {
            long t1 = System.nanoTime();
            capture(w, coords, 0, snaps, biomes, light, 0, 1, 0, (mainNs, ticks) -> {
                int minY = w.getMinHeight(), maxY = w.getMaxHeight();
                if (workers.isShutdown()) return;
                workers.execute(() -> {
                    try {
                        long t3 = System.nanoTime();
                        RegionData d = b == null ? walk(r, snaps, minY, maxY, light, biomes, t1)
                                                 : walkFresh(r, snaps, minY, maxY, light, biomes, b, fresh);
                        long t4 = System.nanoTime();
                        lastLoadMs = (t1 - t0) / 1e6; lastSnapMs = mainNs / 1e6; lastSnapTicks = ticks; lastWalkMs = (t4 - t3) / 1e6;
                        int[] n = RegionMerge.sectionCounts(r);
                        int total = n[0] * n[1] * n[2];
                        RegionData fd = new RegionData(d.grid(), d.light(), d.biomes(), d.extraBytes(), d.base(), d.fresh(), d.capturedNs(),
                            new RegionData.Stats((t1 - t0) / 1e6, mainNs / 1e6, ticks, (t4 - t3) / 1e6, d.stats().copyMs(), coords.size(),
                                fresh != null ? RegionMerge.count(fresh) : total, total));
                        T extra = offThread.apply(fd);
                        if (plugin.isEnabled()) Bukkit.getScheduler().runTask(plugin, () -> {
                            if (fd.biomes() != null) fillClimate(w, fd.biomes());
                            done.accept(fd, extra);
                        });
                    } catch (RuntimeException | Error e) {
                        plugin.getLogger().log(java.util.logging.Level.WARNING, "region read of " + r + " failed", e);
                    }
                });
            });
        });
    }

    /**
     * Loads coords[from..] with getChunkAtAsync, then runs next on the main thread. [nav] When they are all loaded
     * already (the usual case: the region's chunks are ticketed) and this is the main thread, next runs right away
     * instead of a tick later (50 ms of a glide shift's round trip).
     */
    private void load(World w, List<long[]> coords, int from, Runnable next) {
        if (Bukkit.isPrimaryThread()) {
            boolean all = true;
            for (int i = from; i < coords.size() && all; i++) all = w.isChunkLoaded((int) coords.get(i)[0], (int) coords.get(i)[1]);
            if (all) { next.run(); return; }
        }
        List<CompletableFuture<Chunk>> fs = new ArrayList<>();
        for (int i = from; i < coords.size(); i++) fs.add(w.getChunkAtAsync((int) coords.get(i)[0], (int) coords.get(i)[1]));
        CompletableFuture.allOf(fs.toArray(new CompletableFuture[0])).whenComplete((v, err) -> Bukkit.getScheduler().runTask(plugin, () -> {
            if (err != null) { plugin.getLogger().warning("chunk load failed: " + err); return; }
            next.run();
        }));
    }

    /**
     * [area] Takes the ChunkSnapshots on the main thread, at most snapshotBudgetMs per tick: a 288x288 region is 361
     * chunks, about 3 ms warm and 6 ms right after generation, so big regions are spread over a few ticks
     * (TESTING.md "Area size presets"). Chunks are only ticketed once the region is set, so one that unloaded between
     * ticks is loaded again asynchronously first (after two such reloads it is loaded in place, so a read always ends).
     * Snapshots taken a tick apart are fine: diffs compare each block with what was last sent, and events re-trigger a
     * diff for anything that changed in between.
     */
    private void capture(World w, List<long[]> coords, int from, Map<Long, ChunkSnapshot> snaps, boolean biomes, boolean light,
                         long mainNs, int ticks, int reloads, java.util.function.BiConsumer<Long, Integer> done) {
        long t0 = System.nanoTime(), budget = (long) (snapshotBudgetMs * 1e6);
        int i = from;
        while (i < coords.size()) {
            int cx = (int) coords.get(i)[0], cz = (int) coords.get(i)[1];
            if (reloads < 2 && !w.isChunkLoaded(cx, cz)) {
                int at = i;
                long spent = mainNs + System.nanoTime() - t0;
                load(w, coords, at, () -> capture(w, coords, at, snaps, biomes, light, spent, ticks + 1, reloads + 1, done));
                return;
            }
            snaps.put(key(cx, cz), w.getChunkAt(cx, cz).getChunkSnapshot(false, biomes, false, light));
            i++;
            if (budget > 0 && i < coords.size() && System.nanoTime() - t0 > budget) break;
        }
        long tick = System.nanoTime() - t0, spent = mainNs + tick;
        statMaxTickNs = Math.max(statMaxTickNs, tick);
        if (i < coords.size()) {
            int at = i;
            Bukkit.getScheduler().runTask(plugin, () -> capture(w, coords, at, snaps, biomes, light, spent, ticks + 1, reloads, done));
            return;
        }
        statReads++; statMainNs += spent; statTicks += ticks; statChunks += coords.size();
        done.accept(spent, ticks);
    }

    // [area] Main-thread cost of reads (snapshots, diffs) since the last takeStats(); main thread only.
    private long statReads, statMainNs, statMaxTickNs, statTicks, statChunks;

    /** "reads=N chunks=C main_ms_total=T main_ms_max_tick=M ticks=K" since the last call, or null when nothing was read. */
    public String takeStats() {
        if (statReads == 0) return null;
        String s = String.format("reads=%d chunks=%d main_ms_total=%.1f main_ms_max_tick=%.2f ticks=%d budget_ms=%.1f",
            statReads, statChunks, statMainNs / 1e6, statMaxTickNs / 1e6, statTicks, snapshotBudgetMs);
        statReads = statMainNs = statMaxTickNs = statTicks = statChunks = 0;
        return s;
    }

    static long key(int cx, int cz) { return (((long) cx) << 32) ^ (cz & 0xffffffffL); }

    private RegionData walk(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY, boolean withLight, boolean withBiomes, long capturedNs) {
        int[] grid = new int[r.volume()];
        byte[] light = withLight ? new byte[r.volume()] : null;
        walkBox(r, snaps, minY, maxY, grid, light, 0, r.sx(), 0, r.sy(), 0, r.sz(), new HashMap<>());
        BiomeGrid biomes = withBiomes ? walkBiomes(r, snaps, minY, maxY, null) : null;
        long extra = 0;
        if (light != null) extra += BlockCodec.estimateEncodedBytes(light) + 40;
        if (biomes != null) extra += 200 + biomes.palette.size() * 40L + BlockCodec.encode(biomes.cells).length * 4L;
        return new RegionData(grid, light, biomes, extra, null, null, capturedNs, RegionData.Stats.NONE);
    }

    /**
     * [nav] Incremental walk: the base's kept sections are copied (RegionMerge.copyKept), the fresh ones read from the
     * captured chunks. Biomes: cells in captured chunks are sampled, the others copied from the base's biome grid.
     * extraBytes stays 0: the caller estimates an incremental read's size itself (TabletopPlugin.onRegionRequest).
     */
    private RegionData walkFresh(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY, boolean withLight, boolean withBiomes,
                                 RegionMerge.Base b, boolean[] fresh) {
        long c0 = System.nanoTime();
        int[] grid = RegionMerge.copyKept(b, r, fresh);
        byte[] light = withLight ? RegionMerge.copyKeptLight(b, r, fresh) : null;
        double copyMs = (System.nanoTime() - c0) / 1e6;
        Map<org.bukkit.block.data.BlockData, Integer> local = new HashMap<>();
        List<int[]> keys = RegionMerge.keys(fresh, r);
        for (int[] k : keys)
            walkBox(r, snaps, minY, maxY, grid, light, k[0] * 16, Math.min(r.sx(), k[0] * 16 + 16), k[1] * 16, Math.min(r.sy(), k[1] * 16 + 16),
                k[2] * 16, Math.min(r.sz(), k[2] * 16 + 16), local);
        BiomeGrid biomes = withBiomes ? walkBiomes(r, snaps, minY, maxY, b.biomes()) : null;
        return new RegionData(grid, light, biomes, 0, b, keys, b.capturedNs(), new RegionData.Stats(0, 0, 0, 0, copyMs, 0, 0, 0));
    }

    /** Reads the region-local box [x0,x1) x [y0,y1) x [z0,z1) from the captured chunks into grid (and light). */
    private void walkBox(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY, int[] grid, byte[] light,
                         int x0, int x1, int y0, int y1, int z0, int z1, Map<org.bukkit.block.data.BlockData, Integer> local) {
        int air = palette.id("minecraft:air");
        // local: per-walk cache keyed by BlockData (equals/hashCode compare the underlying block state), so the state
        // string is built once per distinct state instead of once per block.
        int sx = r.sx(), sz = r.sz(), step = sx * sz;
        for (int lx = x0; lx < x1; lx++) {
            int x = r.ox() + lx;
            for (int lz = z0; lz < z1; lz++) {
                int z = r.oz() + lz;
                ChunkSnapshot cs = snaps.get(key(x >> 4, z >> 4));
                int i = (y0 * sz + lz) * sx + lx;
                for (int ly = y0; ly < y1; ly++, i += step) {
                    int y = r.oy() + ly;
                    int id;
                    if (y < minY || y >= maxY) {
                        id = air;
                        if (light != null) light[i] = (byte) (y >= maxY ? 0xF0 : 0); // open sky above the world
                    } else {
                        var bd = cs.getBlockData(x & 15, y, z & 15);
                        Integer c = local.get(bd);
                        if (c == null) { c = palette.id(bd.getAsString()); local.put(bd, c); }
                        id = c;
                        if (light != null)
                            light[i] = (byte) ((cs.getBlockSkyLight(x & 15, y, z & 15) << 4) | cs.getBlockEmittedLight(x & 15, y, z & 15));
                    }
                    grid[i] = id;
                }
            }
        }
    }

    /**
     * One biome per 4x4x4 cell the region touches, sampled at a block inside both the cell and the region. [nav] With a
     * base (incremental read), cells whose chunk wasn't captured are copied from the base's grid: such a chunk holds only
     * kept sections, so the cell lies in the base region too.
     */
    private static BiomeGrid walkBiomes(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY, BiomeGrid base) {
        BiomeGrid b = BiomeGrid.covering(r);
        Map<Biome, String> keys = new HashMap<>();
        for (int qy = 0; qy < b.qdy; qy++) {
            int y = Math.min(Math.max(Math.max((b.qy + qy) << 2, r.oy()), minY), maxY - 1);
            for (int qz = 0; qz < b.qdz; qz++) {
                int z = Math.max((b.qz + qz) << 2, r.oz());
                for (int qx = 0; qx < b.qdx; qx++) {
                    int x = Math.max((b.qx + qx) << 2, r.ox());
                    ChunkSnapshot cs = snaps.get(key(x >> 4, z >> 4));
                    String k = cs == null && base != null ? baseBiome(base, x, y, z)
                                                          : keys.computeIfAbsent(cs.getBiome(x & 15, y, z & 15), RegionReader::biomeKey);
                    b.set(qx, qy, qz, k, x, y, z);
                }
            }
        }
        return b;
    }

    /** The base grid's biome at world block (x, y, z), clamped into the base grid. */
    static String baseBiome(BiomeGrid base, int x, int y, int z) {
        int qx = Math.max(0, Math.min(base.qdx - 1, (x >> 2) - base.qx));
        int qy = Math.max(0, Math.min(base.qdy - 1, (y >> 2) - base.qy));
        int qz = Math.max(0, Math.min(base.qdz - 1, (z >> 2) - base.qz));
        return base.palette.get(base.cells[base.index(qx, qy, qz)]);
    }

    @SuppressWarnings("deprecation")
    private static String biomeKey(Biome b) {
        try { return b.getKey().toString(); } catch (RuntimeException e) { return "minecraft:plains"; }
    }

    /**
     * Temperature and downfall per biome palette entry, from World#getTemperature/getHumidity at the lowest cell seen.
     * Vanilla's colour maps use the biome's base temperature; getTemperature cools above sea level + 17, so a biome
     * only seen high up reports a slightly lower value (PROTOCOL.md 4.4.1). Main thread only; cached per biome key.
     */
    private void fillClimate(World w, BiomeGrid b) {
        b.climate = new double[b.palette.size()][];
        for (int i = 0; i < b.palette.size(); i++) {
            int[] s = b.sample(i);
            b.climate[i] = climate.computeIfAbsent(b.palette.get(i), k -> {
                try { return new double[] { w.getTemperature(s[0], s[1], s[2]), w.getHumidity(s[0], s[1], s[2]) }; }
                catch (RuntimeException e) { return null; }
            });
        }
    }
}
