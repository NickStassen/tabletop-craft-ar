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
        int[] tmp = new int[grid.length * 2 + 2];
        int n = 0, i = 0;
        while (i < grid.length) {
            int v = grid[i];
            int j = i + 1;
            while (j < grid.length && grid[j] == v) j++;
            tmp[n++] = v;
            tmp[n++] = j - i;
            i = j;
        }
        return Arrays.copyOf(tmp, n);
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
