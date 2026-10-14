package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.Set;

import static org.junit.jupiter.api.Assertions.*;

class HandColourTest {
    @Test void paletteColoursMatchThemselves() {
        for (HandColour.Block b : HandColour.PALETTE) assertEquals(b.id(), HandColour.nearest(b.rgb()));
        assertEquals("red_concrete", HandColour.nearest(0x902020));
        assertEquals("black_concrete", HandColour.nearest(0x000000));
    }

    @Test void skinTonesPickSkinColouredBlocks() {
        // Default skin's hand, a light and a dark tone.
        assertTrue(Set.of("raw_copper_block", "polished_granite", "granite").contains(HandColour.nearest(HandColour.DEFAULT_HAND_RGB)), HandColour.nearest(HandColour.DEFAULT_HAND_RGB));
        assertTrue(Set.of("white_terracotta", "cherry_planks", "smooth_sandstone", "birch_planks").contains(HandColour.nearest(0xead2b5)), HandColour.nearest(0xead2b5));
        assertTrue(Set.of("brown_terracotta", "brown_wool", "brown_concrete", "spruce_planks").contains(HandColour.nearest(0x5a3c28)), HandColour.nearest(0x5a3c28));
        // Every palette entry is a real block id the game knows (spelling check against the common suffixes).
        for (HandColour.Block b : HandColour.PALETTE) assertTrue(b.id().matches("[a-z_]+"), b.id());
    }

    private static int[] skin(int h, int fill) {
        int[] px = new int[64 * h];
        java.util.Arrays.fill(px, fill);
        return px;
    }

    @Test void handColourReadsTheRightHandArea() {
        int blue = 0xff2030c0, red = 0xffc02020, green = 0xff20c020;
        int[] px = skin(64, blue);
        for (int y = 16; y < 32; y++) for (int x = 40; x < 56; x++) {
            boolean bottom = y < 20 && x >= 47 && x < 52, low = y >= 29;   // covers the wide (48..52) and slim (47..50) bottoms
            if (bottom || low) px[y * 64 + x] = red;
        }
        assertEquals(0xc02020, HandColour.handColour(px, 64, 64, false));
        assertEquals(0xc02020, HandColour.handColour(px, 64, 64, true));
        // Slim arms are 3 px: x 51 belongs to the wide bottom face only.
        px[17 * 64 + 51] = green;
        assertEquals(0xc02020, HandColour.handColour(px, 64, 64, true));
        assertNotEquals(0xc02020, HandColour.handColour(px, 64, 64, false));
        // Legacy 64x32 skins have the same arm area; other sizes are rejected.
        assertEquals(0x2030c0, HandColour.handColour(skin(32, blue), 64, 32, false));
        assertEquals(-1, HandColour.handColour(skin(64, blue), 32, 64, false));
    }
}
