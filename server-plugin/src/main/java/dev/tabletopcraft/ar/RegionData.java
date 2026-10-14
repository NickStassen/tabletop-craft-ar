package dev.tabletopcraft.ar;

/**
 * One read of a region (RegionReader): block ids in the global palette, plus the optional extras of PROTOCOL.md 4.4.1.
 *
 * @param grid       global palette ids, yzx order
 * @param light      packed light per block ({@code sky << 4 | block}, yzx), or null when not read
 * @param biomes     4x4x4 biome cells, or null when not read
 * @param extraBytes estimated JSON bytes the light + biome fields add to a chunk_snapshot (computed off-thread)
 */
public record RegionData(int[] grid, byte[] light, BiomeGrid biomes, long extraBytes) {
    public RegionData(int[] grid) { this(grid, null, null, 0); }
}
