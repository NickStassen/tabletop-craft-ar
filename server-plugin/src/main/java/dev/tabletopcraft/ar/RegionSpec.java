package dev.tabletopcraft.ar;

/** Axis-aligned box of blocks streamed to the headset. Origin is the minimum (north-west, bottom) corner. */
public record RegionSpec(String world, int ox, int oy, int oz, int sx, int sy, int sz) {
    public RegionSpec {
        if (sx <= 0 || sy <= 0 || sz <= 0) throw new IllegalArgumentException("size must be positive");
        // Sanity bound only; config.yml max_region is the real limit (an int[] of this many ids is 128 MB).
        if ((long) sx * sy * sz > 32_000_000L) throw new IllegalArgumentException("region too large");
    }

    public int volume() { return sx * sy * sz; }

    /** Linear index in Y-Z-X order (x fastest), matching PROTOCOL.md. */
    public int index(int lx, int ly, int lz) { return (ly * sz + lz) * sx + lx; }

    public boolean containsWorld(int x, int y, int z) {
        return x >= ox && x < ox + sx && y >= oy && y < oy + sy && z >= oz && z < oz + sz;
    }
}
