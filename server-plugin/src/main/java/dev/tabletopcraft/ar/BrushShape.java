package dev.tabletopcraft.ar;

import java.util.HashMap;
import java.util.Map;
import java.util.function.IntPredicate;

/**
 * [power] Brush shapes and smoothing (PROTOCOL.md "Brushes"). Pure (BrushShapeTest).
 * <ul>
 *   <li>sphere of radius r: the cells whose offset from the centre has dx² + dy² + dz² ≤ r² + r (a round ball even at
 *       small radii; r 0 is the centre alone);</li>
 *   <li>cylinder: dx² + dz² ≤ r² + r, standing upright, {@code height} layers from -(height-1)/2 to height/2 (a
 *       height of 1 is a flat disc);</li>
 *   <li>smooth: one pass of a 3×3×3 majority vote. A cell with 14 or more solid cells around it (itself included) is
 *       solid afterwards, otherwise it is air: a lone bump goes, a lone hole fills, flat ground stays. A cell that
 *       becomes solid takes the most common solid block among its neighbours.</li>
 * </ul>
 */
final class BrushShape {
    private BrushShape() {}

    static boolean inside(String shape, int dx, int dy, int dz, int r, int height) {
        long rr = (long) r * r + r;
        if (shape.equals("cylinder")) return (long) dx * dx + (long) dz * dz <= rr && dy >= -((height - 1) / 2) && dy <= height / 2;
        return (long) dx * dx + (long) dy * dy + (long) dz * dz <= rr;
    }

    /** Lowest and highest dy of the shape. */
    static int minDy(String shape, int r, int height) { return shape.equals("cylinder") ? -((height - 1) / 2) : -r; }
    static int maxDy(String shape, int r, int height) { return shape.equals("cylinder") ? height / 2 : r; }

    /** Cells in the shape (what a stroke costs from brush-blocks-per-second). */
    static int count(String shape, int r, int height) {
        int n = 0;
        for (int dy = minDy(shape, r, height); dy <= maxDy(shape, r, height); dy++)
            for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++) if (inside(shape, dx, dy, dz, r, height)) n++;
        return n;
    }

    /** Air in a smoothing result. */
    static final int AIR = -1;
    /** Unchanged cell in a smoothing result. */
    static final int KEEP = -2;

    /**
     * One smoothing pass. {@code ids} is a window of w×h×d cells (x fastest, then z, then y) holding block ids;
     * {@code solid} says which ids count as solid. Only cells with {@code mask} set are decided (the shape; the window
     * has a one-cell rim around it). Returns per window cell KEEP, AIR, or the id the cell becomes.
     */
    static int[] smooth(int[] ids, int w, int h, int d, boolean[] mask, IntPredicate solid) {
        int[] out = new int[ids.length];
        java.util.Arrays.fill(out, KEEP);
        Map<Integer, Integer> votes = new HashMap<>();
        for (int y = 1; y < h - 1; y++) for (int z = 1; z < d - 1; z++) for (int x = 1; x < w - 1; x++) {
            int i = (y * d + z) * w + x;
            if (!mask[i]) continue;
            int n = 0;
            votes.clear();
            for (int oy = -1; oy <= 1; oy++) for (int oz = -1; oz <= 1; oz++) for (int ox = -1; ox <= 1; ox++) {
                int id = ids[((y + oy) * d + (z + oz)) * w + (x + ox)];
                if (!solid.test(id)) continue;
                n++;
                if (ox != 0 || oy != 0 || oz != 0) votes.merge(id, 1, Integer::sum);
            }
            boolean was = solid.test(ids[i]), now = n >= 14;
            if (was == now) continue;
            if (!now) { out[i] = AIR; continue; }
            int best = -1, bestN = -1;
            for (var e : votes.entrySet()) if (e.getValue() > bestN || (e.getValue() == bestN && e.getKey() < best)) { best = e.getKey(); bestN = e.getValue(); }
            out[i] = best;
        }
        return out;
    }
}
