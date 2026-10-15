package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;

/**
 * Server-side limits for regions (config.yml max_region). maxViewWidth caps the visible width a headset may ask for
 * (x and z of dims - 2 * view_margin, PROTOCOL.md 4.25): a wider region_request is narrowed around its centre, not
 * refused. The region with its margins must still fit maxX/maxZ/maxBlocks.
 */
public record RegionCaps(int maxX, int maxY, int maxZ, long maxBlocks, long maxBytes, int maxViewWidth) {
    public static final int DEFAULT_VIEW_WIDTH = 256;
    /** [nav] Sized for a 256-wide window plus the headset's 48-block glide margin on each side (352), up to 203 high. */
    public static final RegionCaps DEFAULT = new RegionCaps(352, 384, 352, 24L << 20, 32L * 1024 * 1024, DEFAULT_VIEW_WIDTH);
    /** The 0.3.0 defaults: a config.yml still holding them is raised to DEFAULT (TabletopPlugin.start). */
    static final RegionCaps OLD_DEFAULT = new RegionCaps(288, 384, 288, 1L << 24, 32L * 1024 * 1024, DEFAULT_VIEW_WIDTH);

    public RegionCaps(int maxX, int maxY, int maxZ, long maxBlocks, long maxBytes) { this(maxX, maxY, maxZ, maxBlocks, maxBytes, DEFAULT_VIEW_WIDTH); }

    public RegionCaps {
        if (maxViewWidth < 1) throw new IllegalArgumentException("max view width must be >= 1");
    }

    /** Returns null if allowed, else a human-readable reason. */
    public String check(int dx, int dy, int dz) {
        if (dx < 1 || dy < 1 || dz < 1) return "dims must be >= 1";
        if (dx > maxX || dy > maxY || dz > maxZ)
            return String.format("dims %dx%dx%d exceed max %dx%dx%d", dx, dy, dz, maxX, maxY, maxZ);
        long v = (long) dx * dy * dz;
        if (v > maxBlocks) return String.format("%d blocks exceed max %d", v, maxBlocks);
        return null;
    }

    /**
     * [area] A region_request whose visible width on x or z (dims - 2 * view_margin, the margin clamped as the plugin
     * clamps it) is over maxViewWidth, narrowed to maxViewWidth on that axis. The margin is kept, and the origin moves
     * by half the cut rounded to whole 16-block sections, so the window stays centred to within 8 blocks and the reply
     * can still be a sectioned delta (PROTOCOL.md 4.18, 4.25). Returns q itself when nothing is clamped.
     */
    public RegionRequest clampView(RegionRequest q, int[] viewMargin) {
        int[] m = RegionRequest.clampMargin(viewMargin, q.dx(), q.dy(), q.dz());
        int ox = q.ox(), oz = q.oz(), dx = q.dx(), dz = q.dz();
        if (dx - 2 * m[0] > maxViewWidth) { int n = maxViewWidth + 2 * m[0]; ox += centreShift(dx - n); dx = n; }
        if (dz - 2 * m[2] > maxViewWidth) { int n = maxViewWidth + 2 * m[2]; oz += centreShift(dz - n); dz = n; }
        if (dx == q.dx() && dz == q.dz()) return q;
        return new RegionRequest(q.world(), ox, q.oy(), oz, dx, q.dy(), dz);
    }

    /** Half of a (positive) cut in blocks, rounded to whole sections. */
    static int centreShift(int cut) { return Math.round(cut / 2f / SectionCodec.S) * SectionCodec.S; }

    public void addTo(JsonObject o) {
        o.add("max_dims", Protocol.ints(maxX, maxY, maxZ));
        o.addProperty("max_blocks", maxBlocks);
        o.addProperty("max_bytes", maxBytes);
        o.addProperty("max_view_width", maxViewWidth);   // [area] PROTOCOL.md 4.25
    }
}
