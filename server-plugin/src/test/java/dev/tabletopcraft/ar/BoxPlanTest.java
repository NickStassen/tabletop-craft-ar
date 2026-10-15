package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.HashSet;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.*;

/** [power] The box planner (which cells, in what order, where a clone puts them) and the brush shapes. */
class BoxPlanTest {
    static int count(String op, int sx, int sy, int sz, BoxPlan.Kind kind) {
        int n = 0;
        for (long i = 0; i < (long) sx * sy * sz; i++) {
            int[] l = BoxPlan.local(i, sx, sz);
            if (BoxPlan.kind(op, l[0], l[1], l[2], sx, sy, sz, false) == kind) n++;
        }
        return n;
    }

    @Test void walkOrderIsBottomUpXFastest() {
        assertArrayEquals(new int[] { 0, 0, 0 }, BoxPlan.local(0, 4, 3));
        assertArrayEquals(new int[] { 1, 0, 0 }, BoxPlan.local(1, 4, 3));
        assertArrayEquals(new int[] { 0, 0, 1 }, BoxPlan.local(4, 4, 3));
        assertArrayEquals(new int[] { 0, 1, 0 }, BoxPlan.local(12, 4, 3));
    }

    @Test void opsPickTheirCells() {
        assertEquals(5 * 4 * 6, count("fill", 5, 4, 6, BoxPlan.Kind.SET));
        assertEquals(5 * 4 * 6, count("clear", 5, 4, 6, BoxPlan.Kind.AIR));
        assertEquals(5 * 4 * 6, count("replace", 5, 4, 6, BoxPlan.Kind.MATCH));
        // walls: the 4 vertical sides only
        assertEquals((5 * 6 - 3 * 4) * 4, count("walls", 5, 4, 6, BoxPlan.Kind.SET));
        assertEquals(3 * 4 * 4, count("walls", 5, 4, 6, BoxPlan.Kind.SKIP));
        assertEquals(BoxPlan.cells("walls", 5, 4, 6), count("walls", 5, 4, 6, BoxPlan.Kind.SET));
        // hollow: the 6 faces, air inside
        assertEquals(3 * 2 * 4, count("hollow", 5, 4, 6, BoxPlan.Kind.AIR));
        assertEquals(5 * 4 * 6 - 3 * 2 * 4, count("hollow", 5, 4, 6, BoxPlan.Kind.SET));
        // thin boxes are all wall
        assertEquals(1 * 3 * 7, count("walls", 1, 3, 7, BoxPlan.Kind.SET));
        assertEquals(BoxPlan.cells("walls", 2, 3, 7), 2 * 3 * 7);
        assertEquals(BoxPlan.Kind.MATCH_AIR, BoxPlan.kind("clear", 0, 0, 0, 2, 2, 2, true), "clear with a filter only clears matches");
        assertEquals(BoxPlan.Kind.MATCH, BoxPlan.kind("fill", 0, 0, 0, 2, 2, 2, true));
        assertThrows(IllegalArgumentException.class, () -> BoxPlan.kind("melt", 0, 0, 0, 1, 1, 1, false));
    }

    @Test void cloneTurnsClockwiseSeenFromAbove() {
        // a 3 (x) by 2 (z) box turned a quarter: 2 by 3; the source's west-north corner (0,0) goes to the east-north (1,0)
        assertArrayEquals(new int[] { 2, 1, 3 }, BoxPlan.rotatedDims(3, 1, 2, 1));
        assertArrayEquals(new int[] { 3, 1, 2 }, BoxPlan.rotatedDims(3, 1, 2, 2));
        assertArrayEquals(new int[] { 1, 0, 0 }, BoxPlan.rotate(0, 0, 0, 3, 2, 1));
        // north (-z) turns east (+x): the cell north of another lands east of it
        int[] a = BoxPlan.rotate(1, 0, 1, 3, 2, 1), b = BoxPlan.rotate(1, 0, 0, 3, 2, 1);
        assertEquals(1, b[0] - a[0]);
        assertEquals(0, b[2] - a[2]);
        // every turn is a bijection onto the turned box, and four turns are the identity
        for (int rot = 0; rot < 4; rot++) {
            int[] d = BoxPlan.rotatedDims(4, 2, 3, rot);
            Set<String> seen = new HashSet<>();
            for (int y = 0; y < 2; y++) for (int z = 0; z < 3; z++) for (int x = 0; x < 4; x++) {
                int[] t = BoxPlan.rotate(x, y, z, 4, 3, rot);
                assertTrue(t[0] >= 0 && t[0] < d[0] && t[2] >= 0 && t[2] < d[2] && t[1] == y, "inside the turned box");
                assertTrue(seen.add(t[0] + "," + t[1] + "," + t[2]), "no two cells meet");
            }
        }
        int[] p = { 2, 1, 1 };
        int sx = 4, sz = 3;
        for (int k = 0; k < 4; k++) { p = BoxPlan.rotate(p[0], p[1], p[2], sx, sz, 1); int t = sx; sx = sz; sz = t; }
        assertArrayEquals(new int[] { 2, 1, 1 }, p);
    }

    @Test void brushShapes() {
        assertEquals(1, BrushShape.count("sphere", 0, 1));
        assertEquals(19, BrushShape.count("sphere", 1, 3), "radius 1: the 3x3x3 cube without its corners");
        assertEquals(2553, BrushShape.count("sphere", 8, 17), "radius 8: the points within sqrt(72) of the centre (about 4/3 pi 8.5^3)");
        assertEquals(BrushShape.count("cylinder", 3, 1) * 5, BrushShape.count("cylinder", 3, 5), "a cylinder is its disc times its height");
        assertTrue(BrushShape.inside("cylinder", 0, 0, 0, 2, 1) && !BrushShape.inside("cylinder", 0, 1, 0, 2, 1), "height 1 is a flat disc");
        assertEquals(-1, BrushShape.minDy("cylinder", 2, 4));
        assertEquals(2, BrushShape.maxDy("cylinder", 2, 4));
        assertEquals(-3, BrushShape.minDy("sphere", 3, 7));
    }

    @Test void smoothingRemovesBumpsAndFillsHoles() {
        // a 7x5x7 window: ground (id 1) at y 0..1, air (0) above; a bump at (3,2,3) and a hole at (1,1,1)... inside the mask
        int w = 7, h = 5, d = 7;
        int[] ids = new int[w * h * d];
        for (int y = 0; y < h; y++) for (int z = 0; z < d; z++) for (int x = 0; x < w; x++) ids[(y * d + z) * w + x] = y <= 1 ? 1 : 0;
        ids[(2 * d + 3) * w + 3] = 2;   // a lone bump of another block
        ids[(1 * d + 4) * w + 4] = 0;   // a lone hole in the top layer
        boolean[] mask = new boolean[ids.length];
        for (int y = 1; y < h - 1; y++) for (int z = 1; z < d - 1; z++) for (int x = 1; x < w - 1; x++) mask[(y * d + z) * w + x] = true;
        int[] out = BrushShape.smooth(ids, w, h, d, mask, id -> id != 0);
        assertEquals(BrushShape.AIR, out[(2 * d + 3) * w + 3], "the bump goes");
        assertEquals(1, out[(1 * d + 4) * w + 4], "the hole fills with the ground's block");
        assertEquals(BrushShape.KEEP, out[(1 * d + 2) * w + 2], "flat ground stays");
        assertEquals(BrushShape.KEEP, out[(2 * d + 2) * w + 2], "air above flat ground stays");
        assertEquals(BrushShape.KEEP, out[0], "outside the mask nothing is decided");
    }
}
