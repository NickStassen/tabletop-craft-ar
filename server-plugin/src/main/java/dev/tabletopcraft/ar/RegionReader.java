package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.ChunkSnapshot;
import org.bukkit.World;

import java.util.HashMap;
import java.util.Map;

/** Reads regions into dense arrays of global palette ids (main thread). Results are cached for the current tick. */
public final class RegionReader {
    private final Palette palette = new Palette();
    private final Map<RegionSpec, int[]> cache = new HashMap<>();
    private int cacheTick = -1;

    public Palette palette() { return palette; }

    public int[] read(RegionSpec r) {
        int tick = Bukkit.getCurrentTick();
        if (tick != cacheTick) { cache.clear(); cacheTick = tick; }
        return cache.computeIfAbsent(r, this::readUncached);
    }

    /** Drop the cache (after events in the same tick). */
    public void invalidate() { cache.clear(); }

    private int[] readUncached(RegionSpec r) {
        World w = Bukkit.getWorld(r.world());
        if (w == null) throw new IllegalStateException("world not loaded: " + r.world());
        Map<Long, ChunkSnapshot> chunks = new HashMap<>();
        int[] grid = new int[r.volume()];
        int minY = w.getMinHeight(), maxY = w.getMaxHeight();
        int air = palette.id("minecraft:air");
        for (int lx = 0; lx < r.sx(); lx++) {
            int x = r.ox() + lx;
            for (int lz = 0; lz < r.sz(); lz++) {
                int z = r.oz() + lz;
                long key = (((long) (x >> 4)) << 32) ^ ((z >> 4) & 0xffffffffL);
                ChunkSnapshot cs = chunks.computeIfAbsent(key, k -> w.getChunkAt(x >> 4, z >> 4).getChunkSnapshot(false, false, false));
                for (int ly = 0; ly < r.sy(); ly++) {
                    int y = r.oy() + ly;
                    grid[r.index(lx, ly, lz)] = (y < minY || y >= maxY) ? air : palette.id(cs.getBlockData(x & 15, y, z & 15).getAsString());
                }
            }
        }
        return grid;
    }
}
