package dev.tabletopcraft.ar;

import java.util.List;

/**
 * One read of a region (RegionReader): block ids in the global palette, plus the optional extras of PROTOCOL.md 4.4.1.
 *
 * @param grid       global palette ids, yzx order
 * @param light      packed light per block ({@code sky << 4 | block}, yzx), or null when not read
 * @param biomes     4x4x4 biome cells, or null when not read
 * @param extraBytes estimated JSON bytes the light + biome fields add to a chunk_snapshot (computed off-thread)
 * @param base       [nav] incremental read: the base whose sections were copied (null = everything read from the world)
 * @param fresh      [nav] incremental read: the section keys read from the world (all others are copies of base)
 * @param capturedNs System.nanoTime() when the oldest data in it was captured (the base's, for an incremental read)
 * @param stats      timings and sizes of the read (logs)
 */
public record RegionData(int[] grid, byte[] light, BiomeGrid biomes, long extraBytes, RegionMerge.Base base, List<int[]> fresh,
                         long capturedNs, Stats stats) {
    public RegionData(int[] grid, byte[] light, BiomeGrid biomes, long extraBytes) { this(grid, light, biomes, extraBytes, null, null, System.nanoTime(), Stats.NONE); }
    public RegionData(int[] grid) { this(grid, null, null, 0); }

    /** [nav] Read timings: chunk loading, main-thread snapshot time over ticks, the async walk (and copy), sizes. */
    public record Stats(double loadMs, double snapMainMs, int snapTicks, double walkMs, double copyMs, int chunks, int sectionsRead, int sectionsTotal) {
        public static final Stats NONE = new Stats(0, 0, 0, 0, 0, 0, 0, 0);
        public double readMs() { return snapMainMs + walkMs; }
    }
}
