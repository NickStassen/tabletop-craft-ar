package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.Random;

import static org.junit.jupiter.api.Assertions.*;

class BlockCodecTest {
    @Test void pairsAreIndexThenRun() {
        assertArrayEquals(new int[] { 0, 3, 1, 2, 0, 1 }, BlockCodec.encode(new int[] { 0, 0, 0, 1, 1, 0 }));
    }

    @Test void roundTripRandom() {
        int[] g = new int[32 * 64 * 32];
        Random r = new Random(1);
        for (int i = 0; i < g.length; i++) g[i] = r.nextInt(10) < 8 ? 0 : r.nextInt(5);
        assertArrayEquals(g, BlockCodec.decode(BlockCodec.encode(g), g.length));
    }

    @Test void decodeRejectsWrongTotal() {
        assertThrows(IllegalArgumentException.class, () -> BlockCodec.decode(new int[] { 0, 2 }, 3));
        assertThrows(IllegalArgumentException.class, () -> BlockCodec.decode(new int[] { 0, 0, 1, 3 }, 3));
    }

    @Test void indexIsYzx() {
        RegionSpec r = new RegionSpec("w", 0, 0, 0, 4, 3, 5);
        assertEquals(1, r.index(1, 0, 0));
        assertEquals(4, r.index(0, 0, 1));
        assertEquals(20, r.index(0, 1, 0));
    }

    @Test void sessionPaletteAppends() {
        Palette g = new Palette();
        int a = g.id("minecraft:air"), s = g.id("minecraft:stone");
        Session sess = new Session(null);
        assertEquals(0, sess.localIndex(a, g, null));
        var added = new java.util.ArrayList<String>();
        assertEquals(1, sess.localIndex(s, g, added));
        assertEquals(1, sess.localIndex(s, g, added));
        assertEquals(java.util.List.of("minecraft:stone"), added);
    }
}
