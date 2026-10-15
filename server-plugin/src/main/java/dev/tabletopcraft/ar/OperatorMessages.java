package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Set;

/**
 * [operator] Parsers and builders for the operator's navigation and block-entity tools (PROTOCOL.md "Marks:
 * bookmarks, spawn and worlds", "Containers", "Signs"): bookmark, container_query, container_edit, container_close,
 * sign_edit (C->S); bookmarks, container, the welcome "worlds" and "marks" objects (S->C). Pure and unit-tested against
 * tools/fixtures (OperatorFixtureTest); envelope fields are added by {@link Session#send}.
 */
public final class OperatorMessages {
    public static final String CAP_MARKS = "marks_v1";
    /** Longest bookmark name and sign line (characters, after cleaning). */
    static final int MAX_MARK_NAME = 32, MAX_SIGN_LINE = 90;
    static final Set<String> SIDES = Set.of("front", "back");
    static final Set<String> DYES = Set.of("white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan",
        "purple", "blue", "brown", "green", "red", "black");
    /** container_edit ops: set one slot, or sort the whole container. */
    static final Set<String> CONTAINER_OPS = Set.of("set", "sort");

    private OperatorMessages() {}

    // ---------- worlds (welcome, region_info) ----------

    /** One world the headset can switch to: its environment, build height [minY, maxY) and spawn block. */
    public record WorldEntry(String name, String env, int minY, int maxY, int[] spawn) {}

    public static JsonArray worlds(List<WorldEntry> worlds) {
        JsonArray a = new JsonArray();
        for (WorldEntry w : worlds) {
            JsonObject o = new JsonObject();
            o.addProperty("name", w.name());
            o.addProperty("env", w.env());
            o.addProperty("min_y", w.minY());
            o.addProperty("max_y", w.maxY());
            o.add("spawn", Protocol.ints(w.spawn()));
            a.add(o);
        }
        return a;
    }

    /** welcome "marks": the bookmark limit and whether this headset may add and remove them. */
    public static JsonObject welcomeMarks(int max, boolean edit) {
        JsonObject o = new JsonObject();
        o.addProperty("max", max);
        o.addProperty("edit", edit);
        return o;
    }

    // ---------- bookmarks ----------

    /** A bookmark: a named world position shared by every headset. */
    public record Mark(String name, String world, int x, int y, int z, String by) {
        public JsonObject json() {
            JsonObject o = new JsonObject();
            o.addProperty("name", name);
            o.addProperty("world", world);
            o.add("pos", Protocol.ints(x, y, z));
            if (by != null && !by.isEmpty()) o.addProperty("by", by);
            return o;
        }
    }

    /** bookmark (C->S): op add (name, world, pos) or remove (name). */
    public record BookmarkOp(String op, String name, String world, int[] pos) {
        /** An error about this change: the standard error plus op "bookmark" and the name. */
        public JsonObject error(String code, String message, Long refSeq) {
            JsonObject e = Protocol.error(code, message, refSeq, false);
            e.addProperty("op", "bookmark");
            if (name != null) e.addProperty("name", name);
            return e;
        }
    }

    /** Throws IllegalArgumentException (bad_message) when malformed. */
    public static BookmarkOp bookmark(JsonObject m) {
        String op = str(m, "op");
        if (!"add".equals(op) && !"remove".equals(op)) throw new IllegalArgumentException("bookmark op must be add or remove");
        String name = cleanMarkName(str(m, "name"));
        if (name.isEmpty()) throw new IllegalArgumentException("bookmark needs a name (letters, digits, spaces; at most " + MAX_MARK_NAME + ")");
        if (op.equals("remove")) return new BookmarkOp(op, name, null, null);
        String world = str(m, "world");
        if (world == null || world.isBlank() || world.length() > 64) throw new IllegalArgumentException("bookmark add needs a world");
        return new BookmarkOp(op, name, world, intPos(m, "pos", "bookmark"));
    }

    /** A bookmark name as typed: control characters and section signs out, whitespace collapsed, at most 32 characters. */
    public static String cleanMarkName(String raw) {
        if (raw == null) return "";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < raw.length(); i++) {
            char c = raw.charAt(i);
            if (c == '§') { i++; continue; }
            if (Character.isWhitespace(c) || Character.isSpaceChar(c)) b.append(' ');
            else if (!Character.isISOControl(c)) b.append(c);
        }
        String s = b.toString().trim().replaceAll(" {2,}", " ");
        return s.length() > MAX_MARK_NAME ? s.substring(0, MAX_MARK_NAME).trim() : s;
    }

    /** bookmarks (S->C): every bookmark, sorted by name; sent after welcome and whenever one changes. */
    public static JsonObject bookmarks(List<Mark> marks, int max) {
        JsonObject o = Protocol.body("bookmarks");
        o.addProperty("max", max);
        JsonArray a = new JsonArray();
        for (Mark k : marks) a.add(k.json());
        o.add("marks", a);
        return o;
    }

    // ---------- containers ----------

    /** container_query / container_close: the container's position. */
    public static int[] containerPos(JsonObject m, String what) { return intPos(m, "pos", what); }

    /**
     * container_edit: {@code op} "set" puts {@code count} x {@code item} into {@code slot} (item null or count 0 empties it),
     * optionally only if the slot still holds {@code was} (wasItem, wasCount); "sort" merges and sorts the container.
     */
    public record ContainerEdit(int x, int y, int z, String op, int slot, String item, int count, boolean checkWas, String wasItem, int wasCount) {
        public int[] pos() { return new int[] { x, y, z }; }
        public boolean clears() { return item == null || count <= 0; }
    }

    public static ContainerEdit containerEdit(JsonObject m) {
        int[] p = intPos(m, "pos", "container_edit");
        String op = m.has("op") && !m.get("op").isJsonNull() ? str(m, "op") : "set";
        if (op == null || !CONTAINER_OPS.contains(op)) throw new IllegalArgumentException("container_edit op must be one of " + CONTAINER_OPS);
        if (op.equals("sort")) return new ContainerEdit(p[0], p[1], p[2], op, -1, null, 0, false, null, 0);
        int slot = intField(m, "slot", "container_edit");
        if (slot < 0 || slot > 255) throw new IllegalArgumentException("container_edit slot must be 0..255");
        String item = m.has("item") && !m.get("item").isJsonNull() ? itemId(str(m, "item"), "container_edit item") : null;
        int count = m.has("count") && !m.get("count").isJsonNull() ? intField(m, "count", "container_edit") : item == null ? 0 : 1;
        if (count < 0 || count > 127) throw new IllegalArgumentException("container_edit count must be 0..127");
        boolean checkWas = false; String wasItem = null; int wasCount = 0;
        if (m.has("was") && !m.get("was").isJsonNull()) {
            if (!m.get("was").isJsonArray() || m.getAsJsonArray("was").size() != 2) throw new IllegalArgumentException("container_edit was must be [item or null, count]");
            JsonArray w = m.getAsJsonArray("was");
            wasItem = w.get(0).isJsonNull() ? null : itemId(w.get(0).isJsonPrimitive() ? w.get(0).getAsString() : null, "container_edit was");
            if (!w.get(1).isJsonPrimitive() || !w.get(1).getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("container_edit was count must be a number");
            wasCount = w.get(1).getAsInt();
            if (wasItem == null) wasCount = 0;
            checkWas = true;
        }
        return new ContainerEdit(p[0], p[1], p[2], op, slot, item, count, checkWas, wasItem, wasCount);
    }

    /** One filled slot of a container. */
    public record Slot(int slot, String item, int count) {}

    /**
     * container (S->C): the slots that hold something, as [slot, item, count]. {@code other}: the second half of a
     * double chest (null otherwise); {@code edit}: "edit" or "view" (what this headset may do).
     */
    public static JsonObject container(Long refSeq, int[] pos, String block, String title, int size, List<Slot> items, int[] other, String edit) {
        JsonObject o = Protocol.body("container");
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        o.add("pos", Protocol.ints(pos));
        o.addProperty("block", block);
        o.addProperty("title", title);
        o.addProperty("size", size);
        if (other != null) o.add("other", Protocol.ints(other));
        o.addProperty("edit", edit);
        JsonArray a = new JsonArray();
        for (Slot s : items) {
            JsonArray e = new JsonArray();
            e.add(s.slot());
            e.add(s.item());
            e.add(s.count());
            a.add(e);
        }
        o.add("items", a);
        return o;
    }

    /** container (S->C) when the view ends by itself: the block is gone, or the region moved away from it. */
    public static JsonObject containerClosed(int[] pos, String reason) {
        JsonObject o = Protocol.body("container");
        o.add("pos", Protocol.ints(pos));
        o.addProperty("closed", true);
        o.addProperty("reason", reason);
        return o;
    }

    /** An error about a container or sign message: the standard error plus its op and pos. */
    public static JsonObject error(String code, String message, Long refSeq, String op, int[] pos) {
        JsonObject e = Protocol.error(code, message, refSeq, false);
        e.addProperty("op", op);
        if (pos != null) e.add("pos", Protocol.ints(pos));
        return e;
    }

    // ---------- signs ----------

    /** sign_edit: one side's four lines, and optionally its dye colour and glowing ink. */
    public record SignEdit(int x, int y, int z, String side, List<String> lines, String color, Boolean glowing) {
        public int[] pos() { return new int[] { x, y, z }; }
    }

    public static SignEdit signEdit(JsonObject m) {
        int[] p = intPos(m, "pos", "sign_edit");
        String side = str(m, "side");
        if (side == null || !SIDES.contains(side)) throw new IllegalArgumentException("sign_edit side must be front or back");
        if (!m.has("lines") || !m.get("lines").isJsonArray() || m.getAsJsonArray("lines").size() != 4)
            throw new IllegalArgumentException("sign_edit needs lines: 4 strings");
        List<String> lines = new ArrayList<>(4);
        for (JsonElement e : m.getAsJsonArray("lines")) {
            if (e.isJsonNull()) { lines.add(""); continue; }
            if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isString()) throw new IllegalArgumentException("sign_edit lines must be strings");
            if (e.getAsString().length() > 4 * MAX_SIGN_LINE) throw new IllegalArgumentException("sign_edit line too long");
            lines.add(cleanSignLine(e.getAsString()));
        }
        String color = m.has("color") && !m.get("color").isJsonNull() ? str(m, "color") : null;
        if (color != null) {
            color = color.toLowerCase(Locale.ROOT);
            if (!DYES.contains(color)) throw new IllegalArgumentException("sign_edit color must be a dye colour (" + color + ")");
        }
        Boolean glowing = m.has("glowing") && m.get("glowing").isJsonPrimitive() && m.get("glowing").getAsJsonPrimitive().isBoolean()
            ? m.get("glowing").getAsBoolean() : null;
        return new SignEdit(p[0], p[1], p[2], side, List.copyOf(lines), color, glowing);
    }

    /** A sign line as typed: control characters and section signs (legacy formatting codes) out, at most 90 characters. */
    public static String cleanSignLine(String raw) {
        if (raw == null) return "";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < raw.length(); i++) {
            char c = raw.charAt(i);
            if (c == '§') { i++; continue; }
            if (c == '\t') b.append(' ');
            else if (!Character.isISOControl(c)) b.append(c);
        }
        String s = b.toString();
        return s.length() > MAX_SIGN_LINE ? s.substring(0, MAX_SIGN_LINE) : s;
    }

    // ---------- parsing helpers ----------

    private static String str(JsonObject m, String k) {
        return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null;
    }

    private static int intField(JsonObject m, String k, String what) {
        if (!m.has(k) || !m.get(k).isJsonPrimitive() || !m.get(k).getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException(what + " needs " + k);
        double d = m.get(k).getAsDouble();
        if (d != Math.rint(d)) throw new IllegalArgumentException(what + " " + k + " must be an integer");
        return (int) d;
    }

    private static String itemId(String id, String what) {
        if (id == null || id.isBlank() || id.length() > InteractMessages.MAX_ITEM || !id.matches("[a-z0-9_.\\-]+:[a-z0-9_./\\-]+"))
            throw new IllegalArgumentException(what + " must be a namespaced item id");
        return id;
    }

    private static int[] intPos(JsonObject m, String k, String what) {
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
