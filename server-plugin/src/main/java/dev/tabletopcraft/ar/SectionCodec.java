package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

/**
 * {@code chunk_snapshot} encoding {@code sections_v1} (PROTOCOL.md 4.18): the region as 16x16x16 sections, each an
 * RLE of [paletteIndex, runLength] pairs over the section's box (clamped to the region) in yzx order. A snapshot can
 * be split over several messages, and can be a delta against the client's current snapshot: sections that are
 * unchanged after a whole-section shift (or a section-aligned resize) are left out and the client copies them.
 * Pure functions, no Bukkit.
 */
public final class SectionCodec {
    public static final int S = 16;
    public static final String ENCODING = "sections_v1";

    private SectionCodec() {}

    /** One encoded section: key (sx, sy, sz), its block RLE pairs, and (light_v1) its packed-light RLE pairs or null. */
    public record Section(int sx, int sy, int sz, int[] data, int[] light) {
        public Section(int sx, int sy, int sz, int[] data) { this(sx, sy, sz, data, null); }

        /** Rough JSON size: {"s":[x,y,z],"data":[...],"light":[...]} with digits and commas. */
        public long estimatedBytes() {
            long b = 30;
            for (int v : data) b += RegionRequest.digits(v) + 1;
            if (light != null) { b += 10; for (int v : light) b += RegionRequest.digits(v) + 1; }
            return b;
        }
    }

    public static int sections(int d) { return (d + S - 1) / S; }

    /**
     * Shift (in blocks) to send {@code to} as a delta against {@code from}, or null when it can't be one: other
     * world, an origin move that isn't whole sections, or no overlap. [area] The dims may differ (a resize, PROTOCOL.md
     * 4.25): the client copies the overlap and the delta lists the sections that aren't fully inside the old region.
     */
    public static int[] deltaShift(RegionSpec from, RegionSpec to) {
        if (from == null || to == null || !from.world().equals(to.world())) return null;
        int[] sh = { to.ox() - from.ox(), to.oy() - from.oy(), to.oz() - from.oz() };
        int[] od = { from.sx(), from.sy(), from.sz() }, nd = { to.sx(), to.sy(), to.sz() };
        for (int a = 0; a < 3; a++) if (sh[a] % S != 0 || sh[a] >= od[a] || sh[a] + nd[a] <= 0) return null;
        return sh;
    }

    /** RLE pairs over section (kx, ky, kz) of a yzx grid with dims dx, dy, dz. */
    public static int[] encodeSection(int[] grid, int dx, int dy, int dz, int kx, int ky, int kz) {
        int x0 = kx * S, y0 = ky * S, z0 = kz * S;
        int sx = Math.min(S, dx - x0), sy = Math.min(S, dy - y0), sz = Math.min(S, dz - z0);
        int[] tmp = new int[16];
        int n = 0, cur = -1, run = 0;
        for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++) {
                int base = ((y0 + y) * dz + z0 + z) * dx + x0;
                for (int x = 0; x < sx; x++) {
                    int v = grid[base + x];
                    if (v == cur) { run++; continue; }
                    if (run > 0) { if (n + 2 > tmp.length) tmp = Arrays.copyOf(tmp, tmp.length * 2); tmp[n++] = cur; tmp[n++] = run; }
                    cur = v; run = 1;
                }
            }
        if (run > 0) { if (n + 2 > tmp.length) tmp = Arrays.copyOf(tmp, tmp.length + 2); tmp[n++] = cur; tmp[n++] = run; }
        return Arrays.copyOf(tmp, n);
    }

    /** RLE pairs of packed light (0..255, PROTOCOL.md 4.5.1) over section (kx, ky, kz) of a yzx light array. */
    public static int[] encodeSectionLight(byte[] light, int dx, int dy, int dz, int kx, int ky, int kz) {
        int x0 = kx * S, y0 = ky * S, z0 = kz * S;
        int sx = Math.min(S, dx - x0), sy = Math.min(S, dy - y0), sz = Math.min(S, dz - z0);
        int[] tmp = new int[16];
        int n = 0, cur = -1, run = 0;
        for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++) {
                int base = ((y0 + y) * dz + z0 + z) * dx + x0;
                for (int x = 0; x < sx; x++) {
                    int v = light[base + x] & 0xFF;
                    if (v == cur) { run++; continue; }
                    if (run > 0) { if (n + 2 > tmp.length) tmp = Arrays.copyOf(tmp, tmp.length * 2); tmp[n++] = cur; tmp[n++] = run; }
                    cur = v; run = 1;
                }
            }
        if (run > 0) { if (n + 2 > tmp.length) tmp = Arrays.copyOf(tmp, tmp.length + 2); tmp[n++] = cur; tmp[n++] = run; }
        return Arrays.copyOf(tmp, n);
    }

    /** Decodes one section's RLE into a yzx grid (tests and tools). Throws on a wrong run total. */
    public static void decodeSection(int[] data, int[] grid, int dx, int dy, int dz, int kx, int ky, int kz) {
        int x0 = kx * S, y0 = ky * S, z0 = kz * S;
        int sx = Math.min(S, dx - x0), sy = Math.min(S, dy - y0), sz = Math.min(S, dz - z0);
        int vol = sx * sy * sz, p = 0, x = 0, y = 0, z = 0;
        if (data.length % 2 != 0) throw new IllegalArgumentException("odd section data");
        for (int i = 0; i < data.length; i += 2) {
            int v = data[i], run = data[i + 1];
            if (run < 1 || p + run > vol) throw new IllegalArgumentException("bad run in section " + kx + "," + ky + "," + kz);
            for (int j = 0; j < run; j++) {
                grid[((y0 + y) * dz + z0 + z) * dx + x0 + x] = v;
                if (++x == sx) { x = 0; if (++z == sz) { z = 0; y++; } }
            }
            p += run;
        }
        if (p != vol) throw new IllegalArgumentException("section runs cover " + p + " of " + vol);
    }

    /** Every section key, x fastest, then z, then y (any order is valid; this one is stable). */
    public static List<int[]> allSections(int dx, int dy, int dz) {
        List<int[]> keys = new ArrayList<>();
        for (int y = 0; y < sections(dy); y++) for (int z = 0; z < sections(dz); z++) for (int x = 0; x < sections(dx); x++) keys.add(new int[] { x, y, z });
        return keys;
    }

    /**
     * Sections of the new grid that differ from the old grid after the shift (both grids hold the same kind of ids,
     * same dims), plus sections that weren't in the old region. Unlisted sections lie fully inside the old region.
     */
    public static List<int[]> changedSections(int[] oldGrid, int[] newGrid, int dx, int dy, int dz, int[] shiftBlocks) {
        return changedSections(oldGrid, newGrid, null, null, dx, dy, dz, shiftBlocks);
    }

    /** As above; with light (both arrays non-null) a section whose light changed is listed too. */
    public static List<int[]> changedSections(int[] oldGrid, int[] newGrid, byte[] oldLight, byte[] newLight, int dx, int dy, int dz, int[] shiftBlocks) {
        return changedSections(oldGrid, newGrid, oldLight, newLight, new int[] { dx, dy, dz }, new int[] { dx, dy, dz }, shiftBlocks);
    }

    /**
     * [area] As above for a resize: the old grid has dims od, the new one nd, and new local p is old local p + shift.
     * A section is left out only when its whole box lies inside the old region and its blocks (and light) are unchanged.
     */
    public static List<int[]> changedSections(int[] oldGrid, int[] newGrid, byte[] oldLight, byte[] newLight, int[] od, int[] nd, int[] shiftBlocks) {
        List<int[]> keys = new ArrayList<>();
        int ox = shiftBlocks[0], oy = shiftBlocks[1], oz = shiftBlocks[2];
        int dx = nd[0], dy = nd[1], dz = nd[2];
        boolean light = oldLight != null && newLight != null;
        for (int[] k : allSections(dx, dy, dz)) {
            int x0 = k[0] * S, y0 = k[1] * S, z0 = k[2] * S;
            int sx = Math.min(S, dx - x0), sy = Math.min(S, dy - y0), sz = Math.min(S, dz - z0);
            boolean inside = x0 + ox >= 0 && x0 + sx + ox <= od[0] && y0 + oy >= 0 && y0 + sy + oy <= od[1] && z0 + oz >= 0 && z0 + sz + oz <= od[2];
            if (!inside || !same(oldGrid, newGrid, od, nd, x0, y0, z0, sx, sy, sz, ox, oy, oz)
                || (light && !sameLight(oldLight, newLight, od, nd, x0, y0, z0, sx, sy, sz, ox, oy, oz))) keys.add(k);
        }
        return keys;
    }

    private static boolean sameLight(byte[] oldL, byte[] newL, int[] od, int[] nd, int x0, int y0, int z0, int sx, int sy, int sz, int ox, int oy, int oz) {
        for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++) {
                int ni = ((y0 + y) * nd[2] + z0 + z) * nd[0] + x0, oi = ((y0 + y + oy) * od[2] + z0 + z + oz) * od[0] + x0 + ox;
                if (!Arrays.equals(newL, ni, ni + sx, oldL, oi, oi + sx)) return false;
            }
        return true;
    }

    private static boolean same(int[] oldGrid, int[] newGrid, int[] od, int[] nd, int x0, int y0, int z0, int sx, int sy, int sz, int ox, int oy, int oz) {
        for (int y = 0; y < sy; y++)
            for (int z = 0; z < sz; z++) {
                int ni = ((y0 + y) * nd[2] + z0 + z) * nd[0] + x0, oi = ((y0 + y + oy) * od[2] + z0 + z + oz) * od[0] + x0 + ox;
                if (!Arrays.equals(newGrid, ni, ni + sx, oldGrid, oi, oi + sx)) return false;
            }
        return true;
    }

    /** Groups sections into parts whose estimated JSON stays under maxBytes (a single huge section gets its own part). */
    public static List<List<Section>> split(List<Section> secs, long maxBytes, long fixedBytes) {
        List<List<Section>> parts = new ArrayList<>();
        List<Section> cur = new ArrayList<>();
        long size = fixedBytes;
        for (Section s : secs) {
            long b = s.estimatedBytes();
            if (!cur.isEmpty() && size + b > maxBytes) { parts.add(cur); cur = new ArrayList<>(); size = fixedBytes; }
            cur.add(s); size += b;
        }
        parts.add(cur);   // always at least one part (a delta with nothing changed is still a snapshot)
        return parts;
    }
}
