package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.util.List;
import java.util.Locale;

/**
 * [power] Parsers and builders for the headset's power tools (PROTOCOL.md "Undo and redo", "Box edits", "Brushes";
 * cap power_v1): the optional {@code group} of block_edit and interact, undo, redo, box_edit, brush (C->S); history,
 * box_progress and the welcome limits (S->C). Pure and unit-tested against tools/fixtures (PowerFixtureTest); the
 * envelope fields are added by {@link Session#send}.
 */
public final class PowerMessages {
    public static final String CAP = "power_v1";
    /** box_edit ops, brush ops and brush shapes. */
    public static final List<String> BOX_OPS = List.of("fill", "replace", "clear", "walls", "hollow", "clone");
    public static final List<String> BRUSH_OPS = List.of("paint", "replace", "erase", "smooth");
    public static final List<String> SHAPES = List.of("sphere", "cylinder");
    /** Positions listed in a finished job's skipped_pos. */
    static final int MAX_SKIPPED_POS = 16;

    private PowerMessages() {}

    /** The optional group of a block_edit, interact, box_edit or brush: a non-negative integer, null when absent. */
    public static Long group(JsonObject m) {
        if (!m.has("group") || m.get("group").isJsonNull()) return null;
        JsonElement e = m.get("group");
        if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("group must be a non-negative integer");
        double d = e.getAsDouble();
        if (d != Math.rint(d) || d < 0 || d > 4294967295.0) throw new IllegalArgumentException("group must be a non-negative integer");
        return (long) d;
    }

    /** box_edit: op, the box's corners (inclusive, any order), the state placed, the filter, a clone's dest and turns. */
    public record BoxEdit(String op, int[] from, int[] to, String state, String filter, int[] dest, int rotate, Long group) {
        public int minX() { return Math.min(from[0], to[0]); }
        public int minY() { return Math.min(from[1], to[1]); }
        public int minZ() { return Math.min(from[2], to[2]); }
        public int maxX() { return Math.max(from[0], to[0]); }
        public int maxY() { return Math.max(from[1], to[1]); }
        public int maxZ() { return Math.max(from[2], to[2]); }
        public int sx() { return maxX() - minX() + 1; }
        public int sy() { return maxY() - minY() + 1; }
        public int sz() { return maxZ() - minZ() + 1; }
        public long volume() { return (long) sx() * sy() * sz(); }

        /** An error about this box edit: the standard error plus op "box_edit", the box op and its group. */
        public JsonObject error(String code, String message, Long refSeq) { return PowerMessages.error(code, message, refSeq, "box_edit", op, group); }
    }

    /** Throws IllegalArgumentException (bad_message) when malformed. */
    public static BoxEdit boxEdit(JsonObject m) {
        String op = str(m, "op");
        if (op == null || !BOX_OPS.contains(op)) throw new IllegalArgumentException("box_edit op must be one of " + BOX_OPS);
        int[] from = intPos(m, "from", "box_edit"), to = intPos(m, "to", "box_edit");
        String state = str(m, "state"), filter = str(m, "filter");
        boolean needsState = !op.equals("clear") && !op.equals("clone");
        if (needsState && (state == null || state.isBlank())) throw new IllegalArgumentException("box_edit " + op + " needs state");
        if (op.equals("replace") && (filter == null || filter.isBlank())) throw new IllegalArgumentException("box_edit replace needs filter");
        checkState(state, "box_edit state");
        checkState(filter, "box_edit filter");
        int[] dest = null;
        int rotate = 0;
        if (op.equals("clone")) {
            dest = intPos(m, "dest", "box_edit clone");
            if (m.has("rotate") && !m.get("rotate").isJsonNull()) {
                JsonElement r = m.get("rotate");
                if (!r.isJsonPrimitive() || !r.getAsJsonPrimitive().isNumber() || r.getAsDouble() != Math.rint(r.getAsDouble()))
                    throw new IllegalArgumentException("box_edit rotate must be 0, 1, 2 or 3 quarter turns");
                rotate = (int) r.getAsDouble();
                if (rotate < 0 || rotate > 3) throw new IllegalArgumentException("box_edit rotate must be 0, 1, 2 or 3 quarter turns");
            }
        }
        return new BoxEdit(op, from, to, needsState ? state : null, filter, dest, rotate, group(m));
    }

    /** brush: one stroke of a sphere or cylinder at center. */
    public record Brush(String op, String shape, int[] center, int radius, int height, String state, String filter, Long group) {
        public JsonObject error(String code, String message, Long refSeq) { return PowerMessages.error(code, message, refSeq, "brush", op, group); }
    }

    /** Throws IllegalArgumentException (bad_message) when malformed. {@code maxRadius} bounds radius and height. */
    public static Brush brush(JsonObject m, int maxRadius) {
        String op = str(m, "op");
        if (op == null || !BRUSH_OPS.contains(op)) throw new IllegalArgumentException("brush op must be one of " + BRUSH_OPS);
        String shape = str(m, "shape");
        if (shape == null) shape = "sphere";
        if (!SHAPES.contains(shape)) throw new IllegalArgumentException("brush shape must be one of " + SHAPES);
        int[] c = intPos(m, "center", "brush");
        int r = intField(m, "radius", -1, "brush radius");
        if (r < 0 || r > maxRadius) throw new IllegalArgumentException("brush radius must be 0.." + maxRadius);
        int h = shape.equals("cylinder") ? intField(m, "height", 2 * r + 1, "brush height") : 2 * r + 1;
        if (h < 1 || h > 2 * maxRadius + 1) throw new IllegalArgumentException("brush height must be 1.." + (2 * maxRadius + 1));
        String state = str(m, "state"), filter = str(m, "filter");
        if ((op.equals("paint") || op.equals("replace")) && (state == null || state.isBlank())) throw new IllegalArgumentException("brush " + op + " needs state");
        if (op.equals("replace") && (filter == null || filter.isBlank())) throw new IllegalArgumentException("brush replace needs filter");
        checkState(state, "brush state");
        checkState(filter, "brush filter");
        return new Brush(op, shape, c, r, h, op.equals("paint") || op.equals("replace") ? state : null, filter, group(m));
    }

    // ---------- server -> client ----------

    /** An error about a power message: op ("box_edit", "brush", "undo", "redo"), the box_op or brush_op, the group. */
    public static JsonObject error(String code, String message, Long refSeq, String op, String subOp, Long group) {
        JsonObject e = Protocol.error(code, message, refSeq, false);
        e.addProperty("op", op);
        if (subOp != null && (op.equals("box_edit") || op.equals("brush"))) e.addProperty(op.equals("box_edit") ? "box_op" : "brush_op", subOp);
        if (group != null) e.addProperty("group", group);
        return e;
    }

    /** welcome.limits for power_v1 sessions. */
    public static void limits(JsonObject limits, int undoDepth, int boxMaxBlocks, int brushMaxRadius, int brushBlocksPerSecond) {
        limits.addProperty("undo_depth", undoDepth);
        limits.addProperty("box_max_blocks", boxMaxBlocks);
        limits.addProperty("brush_max_radius", brushMaxRadius);
        limits.addProperty("brush_blocks_per_second", brushBlocksPerSecond);
    }

    /** history: how many groups (and blocks) can be undone and redone. */
    public static JsonObject history(int undo, int redo, int undoBlocks, int redoBlocks, int depth) {
        JsonObject o = Protocol.body("history");
        o.addProperty("undo", undo);
        o.addProperty("redo", redo);
        o.addProperty("undo_blocks", undoBlocks);
        o.addProperty("redo_blocks", redoBlocks);
        o.addProperty("depth", depth);
        return o;
    }

    /** A power job's progress (box_edit, undo, redo); the finished one adds skipped_pos (when any) and timing. */
    public record Progress(String op, Long group, Long refSeq, String phase, long processed, long total, long changed, long skipped, long outside,
                           boolean finished, List<int[]> skippedPos, double ms, int ticks) {}

    public static JsonObject boxProgress(Progress p) {
        JsonObject o = Protocol.body("box_progress");
        o.addProperty("op", p.op());
        if (p.group() != null) o.addProperty("group", p.group());
        if (p.refSeq() != null) o.addProperty("ref_seq", p.refSeq());
        o.addProperty("phase", p.phase());
        o.addProperty("processed", p.processed());
        o.addProperty("total", p.total());
        o.addProperty("changed", p.changed());
        o.addProperty("skipped", p.skipped());
        o.addProperty("outside", p.outside());
        o.addProperty("finished", p.finished());
        if (p.finished()) {
            if (p.skippedPos() != null && !p.skippedPos().isEmpty()) {
                JsonArray a = new JsonArray();
                for (int i = 0; i < p.skippedPos().size() && i < MAX_SKIPPED_POS; i++) a.add(Protocol.ints(p.skippedPos().get(i)));
                o.add("skipped_pos", a);
            }
            o.addProperty("ms", Math.round(p.ms() * 10) / 10.0);
            o.addProperty("ticks", p.ticks());
        }
        return o;
    }

    // ---------- parsing helpers ----------

    private static void checkState(String s, String what) {
        if (s == null) return;
        if (s.length() > BlockEdit.MAX_STATE || !s.toLowerCase(Locale.ROOT).matches("[a-z0-9_.\\-]+(:[a-z0-9_./\\-]+)?(\\[[a-z0-9_=,]*\\])?"))
            throw new IllegalArgumentException(what + " must be a block id or block state");
    }

    private static String str(JsonObject m, String k) {
        return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null;
    }

    private static int intField(JsonObject m, String k, int dflt, String what) {
        if (!m.has(k) || m.get(k).isJsonNull()) return dflt;
        JsonElement e = m.get(k);
        if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber() || e.getAsDouble() != Math.rint(e.getAsDouble()) || Math.abs(e.getAsDouble()) > 1e6)
            throw new IllegalArgumentException(what + " must be an integer");
        return (int) e.getAsDouble();
    }

    static int[] intPos(JsonObject m, String k, String what) {
        JsonArray p = m.has(k) && m.get(k).isJsonArray() ? m.getAsJsonArray(k) : null;
        if (p == null || p.size() != 3) throw new IllegalArgumentException(what + " needs " + k + " [x, y, z]");
        int[] v = new int[3];
        for (int i = 0; i < 3; i++) {
            JsonElement e = p.get(i);
            if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException(what + " " + k + " must be integers");
            double d = e.getAsDouble();
            if (d != Math.rint(d) || Math.abs(d) > 30_000_000) throw new IllegalArgumentException(what + " " + k + " must be integer block coordinates");
            v[i] = (int) d;
        }
        return v;
    }
}
