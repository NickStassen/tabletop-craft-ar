package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.Chunk;
import org.bukkit.ChunkSnapshot;
import org.bukkit.World;
import org.bukkit.plugin.Plugin;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.function.Consumer;

/**
 * Reads regions into dense arrays of global palette ids (ARCHITECTURE.md 3.2):
 * chunks are loaded with getChunkAtAsync, ChunkSnapshots are taken on the main thread (cheap), and the
 * O(volume) walk runs on an async worker. The callback runs back on the main thread.
 */
public final class RegionReader {
    private final Plugin plugin;
    private final Palette palette = new Palette();

    /** Timings of the last completed read, for logs/tests. */
    public volatile double lastLoadMs, lastSnapMs, lastWalkMs;

    public RegionReader(Plugin plugin) { this.plugin = plugin; }

    public Palette palette() { return palette; }

    public void readAsync(RegionSpec r, Consumer<int[]> done) {
        World w = Bukkit.getWorld(r.world());
        if (w == null) throw new IllegalStateException("world not loaded: " + r.world());
        long t0 = System.nanoTime();
        List<CompletableFuture<Chunk>> fs = new ArrayList<>();
        for (int cx = r.ox() >> 4; cx <= (r.ox() + r.sx() - 1) >> 4; cx++)
            for (int cz = r.oz() >> 4; cz <= (r.oz() + r.sz() - 1) >> 4; cz++)
                fs.add(w.getChunkAtAsync(cx, cz));
        CompletableFuture.allOf(fs.toArray(new CompletableFuture[0])).whenComplete((v, err) -> Bukkit.getScheduler().runTask(plugin, () -> {
            if (err != null) { plugin.getLogger().warning("chunk load failed: " + err); return; }
            long t1 = System.nanoTime();
            Map<Long, ChunkSnapshot> snaps = new HashMap<>();
            for (CompletableFuture<Chunk> f : fs) {
                Chunk c = f.join();
                snaps.put(key(c.getX(), c.getZ()), c.getChunkSnapshot(false, false, false));
            }
            long t2 = System.nanoTime();
            int minY = w.getMinHeight(), maxY = w.getMaxHeight();
            Bukkit.getScheduler().runTaskAsynchronously(plugin, () -> {
                long t3 = System.nanoTime();
                int[] grid = walk(r, snaps, minY, maxY);
                long t4 = System.nanoTime();
                lastLoadMs = (t1 - t0) / 1e6; lastSnapMs = (t2 - t1) / 1e6; lastWalkMs = (t4 - t3) / 1e6;
                if (plugin.isEnabled()) Bukkit.getScheduler().runTask(plugin, () -> done.accept(grid));
            });
        }));
    }

    static long key(int cx, int cz) { return (((long) cx) << 32) ^ (cz & 0xffffffffL); }

    private int[] walk(RegionSpec r, Map<Long, ChunkSnapshot> snaps, int minY, int maxY) {
        int[] grid = new int[r.volume()];
        int air = palette.id("minecraft:air");
        // Small per-walk cache: BlockData string -> id (avoids palette lock for repeated states).
        Map<String, Integer> local = new HashMap<>();
        for (int lx = 0; lx < r.sx(); lx++) {
            int x = r.ox() + lx;
            for (int lz = 0; lz < r.sz(); lz++) {
                int z = r.oz() + lz;
                ChunkSnapshot cs = snaps.get(key(x >> 4, z >> 4));
                for (int ly = 0; ly < r.sy(); ly++) {
                    int y = r.oy() + ly;
                    int id;
                    if (y < minY || y >= maxY) id = air;
                    else {
                        String s = cs.getBlockData(x & 15, y, z & 15).getAsString();
                        Integer c = local.get(s);
                        if (c == null) { c = palette.id(s); local.put(s, c); }
                        id = c;
                    }
                    grid[r.index(lx, ly, lz)] = id;
                }
            }
        }
        return grid;
    }
}
