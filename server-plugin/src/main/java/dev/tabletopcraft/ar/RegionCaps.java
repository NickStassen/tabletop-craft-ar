package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;

/** Server-side limits for regions (config.yml max_region). */
public record RegionCaps(int maxX, int maxY, int maxZ, long maxBlocks, long maxBytes) {
    public static final RegionCaps DEFAULT = new RegionCaps(128, 384, 128, 1L << 21, 4L * 1024 * 1024);

    /** Returns null if allowed, else a human-readable reason. */
    public String check(int dx, int dy, int dz) {
        if (dx < 1 || dy < 1 || dz < 1) return "dims must be >= 1";
        if (dx > maxX || dy > maxY || dz > maxZ)
            return String.format("dims %dx%dx%d exceed max %dx%dx%d", dx, dy, dz, maxX, maxY, maxZ);
        long v = (long) dx * dy * dz;
        if (v > maxBlocks) return String.format("%d blocks exceed max %d", v, maxBlocks);
        return null;
    }

    public void addTo(JsonObject o) {
        o.add("max_dims", Protocol.ints(maxX, maxY, maxZ));
        o.addProperty("max_blocks", maxBlocks);
        o.addProperty("max_bytes", maxBytes);
    }
}
