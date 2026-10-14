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

    public Palette palette() { return palette; }

    public void readAsync(RegionSpec r, Consumer<int[]> done) { readAsync(r, 0, d -> done.accept(d.grid())); }

    /** extras: a combination of {@link #LIGHT} and {@link #BIOMES} (0 = blocks only). */
    public void readAsync(RegionSpec r, int extras, Consumer<RegionData> done) { readAsync(r, extras, d -> null, (d, x) -> done.accept(d)); }

    /**
     * Like {@link #readAsync(RegionSpec, int, Consumer)}, plus a step that runs on the async worker right after the
     * walk (diffs, size estimates: O(volume) work that must stay off the main thread); its result goes to done.
     */
    public <T> void readAsync(RegionSpec r, int extras, java.util.function.Function<RegionData, T> offThread, java.util.function.BiConsumer<RegionData, T> done) {
        World w = Bukkit.getWorld(r.world());
        boolean light = (extras & LIGHT) != 0, biomes = (extras & BIOMES) != 0;
        if (w == null) throw new IllegalStateException("world not loaded: " + r.world());
        long t0 = System.nanoTime();
        List<long[]> coords = new ArrayList<>();
        for (int cx = r.ox() >> 4; cx <= (r.ox() + r.sx() - 1) >> 4; cx++)
            for (int cz = r.oz() >> 4; cz <= (r.oz() + r.sz() - 1) >> 4; cz++) coords.add(new long[] { cx, cz });
        Map<Long, ChunkSnapshot> snaps = new HashMap<>();
        load(w, coords, 0, () -> {
            long t1 = System.nanoTime();
            capture(w, coords, 0, snaps, biomes, light, 0, 1, 0, (mainNs, ticks) -> {
                int minY = w.getMinHeight(), maxY = w.getMaxHeight();
                Bukkit.getScheduler().runTaskAsynchronously(plugin, () -> {
                    long t3 = System.nanoTime();
                    RegionData d = walk(r, snaps, minY, maxY, light, biomes);
                    long t4 = System.nanoTime();
                    lastLoadMs = (t1 - t0) / 1e6; lastSnapMs = mainNs / 1e6; lastSnapTicks = ticks; lastWalkMs = (t4 - t3) / 1e6;
                    T extra = offThread.apply(d);
                    if (plugin.isEnabled()) Bukkit.getScheduler().runTask(plugin, () -> {
                        if (d.biomes() != null) fillClimate(w, d.biomes());
                        done.accept(d, extra);
                    });
                });
            });
        });
    }

    /** Loads coords[from..] with getChunkAtAsync, then runs next on the main thread. */
    private void load(World w, List<long[]> coords, int from, Runnable next) {
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

    private RegionData walk(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY, boolean withLight, boolean withBiomes) {
        int[] grid = new int[r.volume()];
        byte[] light = withLight ? new byte[r.volume()] : null;
        int air = palette.id("minecraft:air");
        // Per-walk cache keyed by BlockData (equals/hashCode compare the underlying block state), so the state
        // string is built once per distinct state instead of once per block.
        Map<org.bukkit.block.data.BlockData, Integer> local = new HashMap<>();
        int sx = r.sx(), sz = r.sz();
        for (int lx = 0; lx < sx; lx++) {
            int x = r.ox() + lx;
            for (int lz = 0; lz < sz; lz++) {
                int z = r.oz() + lz;
                ChunkSnapshot cs = snaps.get(key(x >> 4, z >> 4));
                int i = lz * sx + lx, step = sx * sz;
                for (int ly = 0; ly < r.sy(); ly++, i += step) {
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
        BiomeGrid biomes = withBiomes ? walkBiomes(r, snaps, minY, maxY) : null;
        long extra = 0;
        if (light != null) extra += BlockCodec.estimateEncodedBytes(light) + 40;
        if (biomes != null) extra += 200 + biomes.palette.size() * 40L + BlockCodec.encode(biomes.cells).length * 4L;
        return new RegionData(grid, light, biomes, extra);
    }

    /** One biome per 4x4x4 cell the region touches, sampled at a block inside both the cell and the region. */
    private static BiomeGrid walkBiomes(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY) {
        BiomeGrid b = BiomeGrid.covering(r);
        Map<Biome, String> keys = new HashMap<>();
        for (int qy = 0; qy < b.qdy; qy++) {
            int y = Math.min(Math.max(Math.max((b.qy + qy) << 2, r.oy()), minY), maxY - 1);
            for (int qz = 0; qz < b.qdz; qz++) {
                int z = Math.max((b.qz + qz) << 2, r.oz());
                for (int qx = 0; qx < b.qdx; qx++) {
                    int x = Math.max((b.qx + qx) << 2, r.ox());
                    Biome bio = snaps.get(key(x >> 4, z >> 4)).getBiome(x & 15, y, z & 15);
                    b.set(qx, qy, qz, keys.computeIfAbsent(bio, RegionReader::biomeKey), x, y, z);
                }
            }
        }
        return b;
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
