package dev.tabletopcraft.ar;

import java.util.Arrays;

/**
 * {@code rle_palette_v1} from PROTOCOL.md section 4.4: a flat int array of pairs
 * {@code [paletteIndex, runLength, paletteIndex, runLength, ...]} over the region in yzx order.
 */
public final class BlockCodec {
    private BlockCodec() {}

    /** Encodes a grid whose values are already indices into the target palette. */
    public static int[] encode(int[] grid) {
        // Count the runs first: big regions (millions of blocks) would otherwise need a 2N scratch array.
        int runs = grid.length == 0 ? 0 : 1;
        for (int i = 1; i < grid.length; i++) if (grid[i] != grid[i - 1]) runs++;
        int[] out = new int[runs * 2];
        int n = 0, i = 0;
        while (i < grid.length) {
            int v = grid[i];
            int j = i + 1;
            while (j < grid.length && grid[j] == v) j++;
            out[n++] = v;
            out[n++] = j - i;
            i = j;
        }
        return out;
    }

    /** Same pair encoding for unsigned bytes (packed light, PROTOCOL.md 4.4.1 {@code rle_v1}). */
    public static int[] encode(byte[] grid) {
        int[] tmp = new int[grid.length * 2 + 2];
        int n = 0, i = 0;
        while (i < grid.length) {
            byte v = grid[i];
            int j = i + 1;
            while (j < grid.length && grid[j] == v) j++;
            tmp[n++] = v & 0xFF;
            tmp[n++] = j - i;
            i = j;
        }
        return Arrays.copyOf(tmp, n);
    }

    /** JSON size of {@link #encode(byte[])}'s output without building it (for the max_bytes check, off-thread). */
    public static long estimateEncodedBytes(byte[] grid) {
        long b = 0;
        int i = 0;
        while (i < grid.length) {
            byte v = grid[i];
            int j = i + 1;
            while (j < grid.length && grid[j] == v) j++;
            b += RegionRequest.digits(v & 0xFF) + RegionRequest.digits(j - i) + 2;
            i = j;
        }
        return b;
    }

    /** Cells whose packed light changed, as block_update "light" entries {@code [x, y, z, sky << 4 | block]} (region-local). */
    public static java.util.List<int[]> lightDiff(byte[] old, byte[] now, RegionSpec r) {
        java.util.List<int[]> out = new java.util.ArrayList<>();
        for (int i = 0; i < now.length; i++)
            if (old[i] != now[i]) out.add(new int[] { i % r.sx(), i / (r.sx() * r.sz()), (i / r.sx()) % r.sz(), now[i] & 0xFF });
        return out;
    }

    public static int[] decode(int[] data, int volume) {
        if (data.length % 2 != 0) throw new IllegalArgumentException("odd data length");
        int[] out = new int[volume];
        int p = 0;
        for (int k = 0; k < data.length; k += 2) {
            int v = data[k], count = data[k + 1];
            if (count < 1 || p + count > volume) throw new IllegalArgumentException("bad run at " + k);
            Arrays.fill(out, p, p + count, v);
            p += count;
        }
        if (p != volume) throw new IllegalArgumentException("runs cover " + p + " of " + volume);
        return out;
    }
}
