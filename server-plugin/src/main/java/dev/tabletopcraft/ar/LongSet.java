package dev.tabletopcraft.ar;

import java.util.Arrays;

/**
 * [power] An insertion-ordered set of longs without boxing (open addressing, linear probing), for the positions a box
 * edit changed: 32768 of them would otherwise be 32768 Long objects plus HashMap nodes per job.
 */
final class LongSet {
    private static final long EMPTY = Long.MIN_VALUE;
    private long[] table = new long[64];
    private long[] order = new long[16];
    private int size;
    private boolean hasEmptyKey;

    int size() { return size; }

    /** The i-th value added. */
    long get(int i) { return order[i]; }

    boolean contains(long v) {
        if (v == EMPTY) return hasEmptyKey;
        int mask = table.length - 1;
        for (int i = mix(v) & mask; ; i = (i + 1) & mask) {
            long t = table[i];
            if (t == v) return true;
            if (t == EMPTY) return false;
        }
    }

    /** Adds v; false when it was there already. */
    boolean add(long v) {
        if (v == EMPTY) {
            if (hasEmptyKey) return false;
            hasEmptyKey = true;
        } else {
            if ((size + 1) * 2 > table.length) grow();
            int mask = table.length - 1;
            int i = mix(v) & mask;
            for (; table[i] != EMPTY; i = (i + 1) & mask) if (table[i] == v) return false;
            table[i] = v;
        }
        if (size == order.length) order = Arrays.copyOf(order, size * 2);
        order[size++] = v;
        return true;
    }

    void clear() {
        if (size == 0 && !hasEmptyKey) return;
        Arrays.fill(table, EMPTY);
        size = 0;
        hasEmptyKey = false;
    }

    LongSet() { Arrays.fill(table, EMPTY); }

    private void grow() {
        long[] old = table;
        table = new long[old.length * 2];
        Arrays.fill(table, EMPTY);
        int mask = table.length - 1;
        for (long v : old) {
            if (v == EMPTY) continue;
            int i = mix(v) & mask;
            while (table[i] != EMPTY) i = (i + 1) & mask;
            table[i] = v;
        }
    }

    private static int mix(long v) {
        long h = v * 0x9E3779B97F4A7C15L;
        return (int) (h ^ (h >>> 32));
    }
}
