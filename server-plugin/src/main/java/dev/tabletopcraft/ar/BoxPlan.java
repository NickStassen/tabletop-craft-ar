package dev.tabletopcraft.ar;

/**
 * [power] Which cells of a box edit change and how (PROTOCOL.md "Box edits"), and where a clone puts each cell. Pure
 * (BoxPlanTest); PowerService walks the cells in this order on the main thread within its per-tick budget.
 * <ul>
 *   <li>Cells go bottom layer first (Y, then Z, then X fastest), so sand and gravel of a fill land on what is
 *       already there.</li>
 *   <li>fill: every cell gets the state; replace: the cells matching the filter; clear: every cell becomes air (with a
 *       filter only the matching ones); walls: the four vertical sides (no floor or ceiling); hollow: the six faces get the
 *       state and the inside becomes air (vanilla's /fill hollow).</li>
 *   <li>clone turns the copy a quarter turn clockwise (seen from above) per {@code rotate}: a cell at local (x, z) of a
 *       box sx by sz goes to (sz-1-z, x) of the turned box, which is sz by sx, the way Minecraft's
 *       {@code Rotation.CLOCKWISE_90} turns a north-facing block east.</li>
 * </ul>
 */
final class BoxPlan {
    enum Kind { SET, AIR, SKIP, MATCH, MATCH_AIR }

    private BoxPlan() {}

    /** What happens to local cell (x, y, z) of a box sx by sy by sz for op (clone: SET, the source cell's state). */
    static Kind kind(String op, int x, int y, int z, int sx, int sy, int sz, boolean filter) {
        return switch (op) {
            case "fill" -> filter ? Kind.MATCH : Kind.SET;
            case "replace" -> Kind.MATCH;
            case "clear" -> filter ? Kind.MATCH_AIR : Kind.AIR;
            case "walls" -> x == 0 || x == sx - 1 || z == 0 || z == sz - 1 ? Kind.SET : Kind.SKIP;
            case "hollow" -> x == 0 || x == sx - 1 || z == 0 || z == sz - 1 || y == 0 || y == sy - 1 ? Kind.SET : Kind.AIR;
            case "clone" -> Kind.SET;
            default -> throw new IllegalArgumentException("unknown box op " + op);
        };
    }

    /** Cells the op looks at (all but walls' inside): the total box_progress counts to. */
    static long cells(String op, int sx, int sy, int sz) {
        long all = (long) sx * sy * sz;
        if (!op.equals("walls") || sx <= 2 || sz <= 2) return all;
        return all - (long) (sx - 2) * (sz - 2) * sy;
    }

    /** Local position of the i-th cell in walk order (x fastest, then z, then y). */
    static int[] local(long i, int sx, int sz) {
        int x = (int) (i % sx), z = (int) ((i / sx) % sz), y = (int) (i / ((long) sx * sz));
        return new int[] { x, y, z };
    }

    /** The turned box's size: a quarter or three-quarter turn swaps X and Z. */
    static int[] rotatedDims(int sx, int sy, int sz, int rotate) {
        return (rotate & 1) == 1 ? new int[] { sz, sy, sx } : new int[] { sx, sy, sz };
    }

    /** Where source local (x, y, z) of a box sx by sz lands in the copy turned {@code rotate} quarter turns clockwise. */
    static int[] rotate(int x, int y, int z, int sx, int sz, int rotate) {
        return switch (rotate & 3) {
            case 1 -> new int[] { sz - 1 - z, y, x };
            case 2 -> new int[] { sx - 1 - x, y, sz - 1 - z };
            case 3 -> new int[] { z, y, sx - 1 - x };
            default -> new int[] { x, y, z };
        };
    }

    /** Readable name of an op for messages ("filled", "replaced"...). */
    static String done(String op) {
        return switch (op) {
            case "fill" -> "filled"; case "replace" -> "replaced"; case "clear" -> "cleared"; case "walls" -> "walled";
            case "hollow" -> "hollowed"; case "clone" -> "cloned"; case "undo" -> "undone"; case "redo" -> "redone";
            default -> op;
        };
    }
}
