package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * [nav] Incremental region reads (ARCHITECTURE.md 3.2): when the region moves or resizes in the same world, only the
 * 16^3 sections that weren't fully inside the old region are read from the world; every other section is copied from
 * the old region's data (what the headset already has), so a one-section glide shift reads 1/18 of a 288-wide region
 * instead of all of it. Pure functions, no Bukkit (unit-tested in RegionMergeTest).
 *
 * The copied sections are as old as the base. Changes made since then reach the headset through the normal diff
 * (TabletopPlugin re-diffs after the move when anything was marked dirty since the base was captured).
 */
public final class RegionMerge {
    private RegionMerge() {}

    /**
     * The data the old region is copied from: the session's last grid (global palette ids), light and biomes, as the
     * client has them, and when its chunks were captured (System.nanoTime(), for "changed since").
     */
    public record Base(RegionSpec region, int[] grid, byte[] light, BiomeGrid biomes, long capturedNs) {
        public Base {
            if (grid.length != region.volume()) throw new IllegalArgumentException("base grid does not match its region");
            if (light != null && light.length != grid.length) throw new IllegalArgumentException("base light does not match its grid");
        }
    }

    /** True if an incremental read of r from b is possible: same world, overlapping, and b has the extras the read needs. */
    public static boolean usable(Base b, RegionSpec r, int extras) {
        if (b == null || r == null || !b.region().world().equals(r.world())) return false;
        if ((extras & RegionReader.LIGHT) != 0 && b.light() == null) return false;
        if ((extras & RegionReader.BIOMES) != 0 && b.biomes() == null) return false;
        RegionSpec o = b.region();
        return r.ox() < o.ox() + o.sx() && o.ox() < r.ox() + r.sx() && r.oy() < o.oy() + o.sy() && o.oy() < r.oy() + r.sy()
            && r.oz() < o.oz() + o.sz() && o.oz() < r.oz() + r.sz();
    }

    /** Section grid of r: [nx, ny, nz]. */
    public static int[] sectionCounts(RegionSpec r) {
        return new int[] { SectionCodec.sections(r.sx()), SectionCodec.sections(r.sy()), SectionCodec.sections(r.sz()) };
    }

    /** Index of section (kx, ky, kz) in a section grid n (x fastest, then z, then y, like SectionCodec.allSections). */
    public static int sectionIndex(int[] n, int kx, int ky, int kz) { return (ky * n[2] + kz) * n[0] + kx; }

    /**
     * Per section of r (sectionIndex order): true when its box (clamped to r) is not fully inside {@code old}, i.e. it
     * must be read from the world. These are exactly the sections a delta against {@code old} has to list.
     */
    public static boolean[] freshSections(RegionSpec old, RegionSpec r) {
        int[] n = sectionCounts(r);
        boolean[] fresh = new boolean[n[0] * n[1] * n[2]];
        boolean sameWorld = old != null && old.world().equals(r.world());
        for (int ky = 0; ky < n[1]; ky++)
            for (int kz = 0; kz < n[2]; kz++)
                for (int kx = 0; kx < n[0]; kx++) {
                    boolean inside = sameWorld
                        && inside(r.ox() + kx * 16, Math.min(16, r.sx() - kx * 16), old.ox(), old.sx())
                        && inside(r.oy() + ky * 16, Math.min(16, r.sy() - ky * 16), old.oy(), old.sy())
                        && inside(r.oz() + kz * 16, Math.min(16, r.sz() - kz * 16), old.oz(), old.sz());
                    fresh[sectionIndex(n, kx, ky, kz)] = !inside;
                }
        return fresh;
    }

    private static boolean inside(int a, int len, int o, int olen) { return a >= o && a + len <= o + olen; }

    /** The fresh section keys [kx, ky, kz] (same order as SectionCodec.allSections). */
    public static List<int[]> keys(boolean[] fresh, RegionSpec r) {
        int[] n = sectionCounts(r);
        List<int[]> out = new ArrayList<>();
        for (int ky = 0; ky < n[1]; ky++)
            for (int kz = 0; kz < n[2]; kz++)
                for (int kx = 0; kx < n[0]; kx++) if (fresh[sectionIndex(n, kx, ky, kz)]) out.add(new int[] { kx, ky, kz });
        return out;
    }

    public static int count(boolean[] fresh) { int c = 0; for (boolean f : fresh) if (f) c++; return c; }

    /** Blocks of r in the given sections. */
    public static long volume(List<int[]> keys, RegionSpec r) {
        long v = 0;
        for (int[] k : keys) v += (long) Math.min(16, r.sx() - k[0] * 16) * Math.min(16, r.sy() - k[1] * 16) * Math.min(16, r.sz() - k[2] * 16);
        return v;
    }

    /** Chunk keys (RegionReader.key) of the chunks that hold at least one fresh section: the only ones to capture. */
    public static Set<Long> freshChunks(boolean[] fresh, RegionSpec r) {
        Set<Long> out = new HashSet<>();
        for (int[] k : keys(fresh, r)) {
            int x0 = r.ox() + k[0] * 16, x1 = r.ox() + Math.min(r.sx(), k[0] * 16 + 16) - 1;
            int z0 = r.oz() + k[2] * 16, z1 = r.oz() + Math.min(r.sz(), k[2] * 16 + 16) - 1;
            for (int cx = x0 >> 4; cx <= x1 >> 4; cx++) for (int cz = z0 >> 4; cz <= z1 >> 4; cz++) out.add(RegionReader.key(cx, cz));
        }
        return out;
    }

    /**
     * A yzx int grid for r with every non-fresh section copied from the base (new local p = base local p + shift, where
     * shift = r.origin - base.origin); fresh sections are left 0 for the walk to fill. One arraycopy per run of
     * consecutive kept sections in a row.
     */
    public static int[] copyKept(Base b, RegionSpec r, boolean[] fresh) {
        int[] out = new int[r.volume()];
        copy(b.region(), r, fresh, (src, dst, len) -> System.arraycopy(b.grid(), src, out, dst, len));
        return out;
    }

    /** Like copyKept for packed light (requires base light). */
    public static byte[] copyKeptLight(Base b, RegionSpec r, boolean[] fresh) {
        byte[] out = new byte[r.volume()];
        copy(b.region(), r, fresh, (src, dst, len) -> System.arraycopy(b.light(), src, out, dst, len));
        return out;
    }

    private interface RowCopy { void copy(int src, int dst, int len); }

    private static void copy(RegionSpec o, RegionSpec r, boolean[] fresh, RowCopy c) {
        int[] n = sectionCounts(r);
        int shx = r.ox() - o.ox(), shy = r.oy() - o.oy(), shz = r.oz() - o.oz();
        for (int ky = 0; ky < n[1]; ky++)
            for (int kz = 0; kz < n[2]; kz++) {
                int ySpan = Math.min(16, r.sy() - ky * 16), zSpan = Math.min(16, r.sz() - kz * 16);
                int kx = 0;
                while (kx < n[0]) {
                    if (fresh[sectionIndex(n, kx, ky, kz)]) { kx++; continue; }
                    int k0 = kx;
                    while (kx < n[0] && !fresh[sectionIndex(n, kx, ky, kz)]) kx++;
                    int x0 = k0 * 16, x1 = Math.min(r.sx(), kx * 16);   // a run of kept sections, all inside the base
                    for (int y = ky * 16; y < ky * 16 + ySpan; y++)
                        for (int z = kz * 16; z < kz * 16 + zSpan; z++)
                            c.copy(((y + shy) * o.sz() + z + shz) * o.sx() + x0 + shx, (y * r.sz() + z) * r.sx() + x0, x1 - x0);
                }
            }
    }

    /**
     * Rough JSON bytes of these sections as sections_v1 (block runs, and light runs when light != null), for the
     * max_bytes check of an incremental read: the kept part's share is scaled from the previous region's estimate.
     */
    public static long estimateSectionBytes(int[] grid, byte[] light, RegionSpec r, List<int[]> keys) {
        long b = 0;
        for (int[] k : keys) {
            b += new SectionCodec.Section(k[0], k[1], k[2], SectionCodec.encodeSection(grid, r.sx(), r.sy(), r.sz(), k[0], k[1], k[2]),
                light != null ? SectionCodec.encodeSectionLight(light, r.sx(), r.sy(), r.sz(), k[0], k[1], k[2]) : null).estimatedBytes();
        }
        return b;
    }
}
