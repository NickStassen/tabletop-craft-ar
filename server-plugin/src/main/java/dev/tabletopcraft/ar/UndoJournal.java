package dev.tabletopcraft.ar;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * [power] One headset's undo history (PROTOCOL.md "Undo and redo"): groups of changed cells, newest last, and the
 * groups undone since the last new edit (redo). Pure and generic so it is unit-tested with strings (UndoJournalTest);
 * PowerService keeps Paper BlockData ({@code S}) and block entity snapshots ({@code T}) in it, on the main thread.
 * <ul>
 *   <li>A cell is recorded with its state before and after one change; recording the same position again in the same
 *       group keeps the first "before" (and its block entity) and the latest "after". A cell that ends as it began
 *       leaves the group.</li>
 *   <li>A group is one trigger hold or one tool action: the headset sends the same {@code group} id with every edit of
 *       a hold, and the records merge while that group is the newest. A new group clears the redo list.</li>
 *   <li>Bounded by {@code depth} blocks: the oldest groups are dropped while the total is over it, but the newest group
 *       is always kept whole (a box edit bigger than the depth can be undone until the next edit).</li>
 *   <li>Block entity snapshots (chest contents, sign text) are bounded by {@code tileCap}: the oldest are dropped
 *       first, and those cells then restore their block state only.</li>
 * </ul>
 */
final class UndoJournal<S, T> {
    /** One changed cell: world position, state and block entity before and after. */
    static final class Cell<S, T> {
        final int x, y, z;
        S before, after;
        T beforeTile, afterTile;
        Cell(int x, int y, int z, S before, T beforeTile, S after) { this.x = x; this.y = y; this.z = z; this.before = before; this.beforeTile = beforeTile; this.after = after; }
        long key() { return key(x, y, z); }
        /** A position as one long: x and z in 26 bits each, y in 12 (worlds are at most 4064 high). */
        static long key(int x, int y, int z) { return ((long) (x & 0x3FFFFFF) << 38) | ((long) (z & 0x3FFFFFF) << 12) | (y & 0xFFF); }
        static int keyX(long k) { return (int) (k >> 38); }
        static int keyZ(long k) { return (int) ((k << 26) >> 38); }
        static int keyY(long k) { return (int) ((k << 52) >> 52); }
    }

    /** The cells of one trigger hold or tool action in one world, in the order they changed. */
    static final class Group<S, T> {
        final long id;
        final String label, world;
        final LinkedHashMap<Long, Cell<S, T>> cells = new LinkedHashMap<>();
        Group(long id, String label, String world) { this.id = id; this.label = label; this.world = world; }
        int size() { return cells.size(); }
        List<Cell<S, T>> list() { return new ArrayList<>(cells.values()); }
        int tiles() { int n = 0; for (Cell<S, T> c : cells.values()) { if (c.beforeTile != null) n++; if (c.afterTile != null) n++; } return n; }
    }

    private final Deque<Group<S, T>> undo = new ArrayDeque<>(), redo = new ArrayDeque<>();
    private int undoBlocks, redoBlocks, tiles;
    private final int depth, tileCap;
    /** Group ids given to records without one (negative, so they never meet a headset's). */
    private long anonymous = -1;
    /** Bumped on every change of the counts (PowerService sends history when it moved). */
    private int version;

    UndoJournal(int depth, int tileCap) { this.depth = Math.max(0, depth); this.tileCap = Math.max(0, tileCap); }

    int depth() { return depth; }
    int undoGroups() { return undo.size(); }
    int redoGroups() { return redo.size(); }
    int undoBlocks() { return undoBlocks; }
    int redoBlocks() { return redoBlocks; }
    int tiles() { return tiles; }
    int version() { return version; }
    boolean enabled() { return depth > 0; }

    /** A fresh id for an edit that came without a group (each is its own group). */
    long anonymousGroup() { return anonymous--; }

    /** The newest group, if it is the given one (still open for more records). */
    private Group<S, T> open(long id, String world) {
        Group<S, T> g = undo.peekLast();
        return g != null && g.id == id && g.world.equals(world) ? g : null;
    }

    /**
     * Records one change in group {@code id} ({@code equal} decides whether two states are the same block state).
     * Merges into the newest group when it has this id; otherwise starts a new group and clears redo.
     */
    void record(long id, String label, String world, int x, int y, int z, S before, T beforeTile, S after, java.util.function.BiPredicate<S, S> equal) {
        if (!enabled()) return;
        Group<S, T> g = open(id, world);
        long k = Cell.key(x, y, z);
        if ((g == null || !g.cells.containsKey(k)) && equal.test(before, after) && beforeTile == null) return;   // nothing changed there
        if (g == null) {
            clearRedo();
            g = new Group<>(id, label, world);
            undo.addLast(g);
        }
        Cell<S, T> c = g.cells.get(k);
        if (c == null) {
            g.cells.put(k, new Cell<>(x, y, z, before, beforeTile, after));
            undoBlocks++;
            if (beforeTile != null) tiles++;
        } else {
            c.after = after;
            if (equal.test(c.before, c.after) && c.beforeTile == null) { g.cells.remove(k); undoBlocks--; }
        }
        if (g.cells.isEmpty()) { undo.removeLast(); }
        trim();
        version++;
    }

    /** Puts a whole group on top (a finished box edit or brush stroke); merges with the newest group of the same id. */
    void push(Group<S, T> done) {
        if (!enabled() || done.cells.isEmpty()) return;
        Group<S, T> g = open(done.id, done.world);
        if (g == null) {
            clearRedo();
            g = new Group<>(done.id, done.label, done.world);
            undo.addLast(g);
        }
        for (Cell<S, T> c : done.cells.values()) {
            Cell<S, T> had = g.cells.get(c.key());
            if (had == null) { g.cells.put(c.key(), c); undoBlocks++; if (c.beforeTile != null) tiles++; }
            else { had.after = c.after; }
        }
        trim();
        version++;
    }

    /** Takes the newest group off the undo list (null when empty). Its cells count again once it goes to redo or back. */
    Group<S, T> popUndo() {
        Group<S, T> g = undo.pollLast();
        if (g == null) return null;
        undoBlocks -= g.size();
        tiles -= g.tiles();
        version++;
        return g;
    }

    /** Takes the newest undone group off the redo list (null when empty). */
    Group<S, T> popRedo() {
        Group<S, T> g = redo.pollLast();
        if (g == null) return null;
        redoBlocks -= g.size();
        tiles -= g.tiles();
        version++;
        return g;
    }

    /** An undone group (only its restored cells) goes to the redo list. */
    void pushRedo(Group<S, T> g) {
        if (g.cells.isEmpty()) { version++; return; }
        redo.addLast(g);
        redoBlocks += g.size();
        tiles += g.tiles();
        trimTiles();
        version++;
    }

    /** A redone group (only its re-applied cells) goes back on the undo list, without clearing redo. */
    void pushUndoKeepRedo(Group<S, T> g) {
        if (g.cells.isEmpty()) { version++; return; }
        undo.addLast(g);
        undoBlocks += g.size();
        tiles += g.tiles();
        trim();
        version++;
    }

    private void clearRedo() {
        if (redo.isEmpty()) return;
        for (Group<S, T> g : redo) tiles -= g.tiles();
        redo.clear();
        redoBlocks = 0;
    }

    /** Drops the oldest groups while over the depth (the newest stays whole), then the oldest block entity snapshots. */
    private void trim() {
        while (undoBlocks + redoBlocks > depth && undo.size() > 1) {
            Group<S, T> old = undo.pollFirst();
            undoBlocks -= old.size();
            tiles -= old.tiles();
        }
        trimTiles();
    }

    private void trimTiles() {
        if (tiles <= tileCap) return;
        List<Deque<Group<S, T>>> lists = List.of(undo, redo);
        for (Deque<Group<S, T>> list : lists)
            for (Iterator<Group<S, T>> it = list.iterator(); it.hasNext() && tiles > tileCap; ) {
                for (Cell<S, T> c : it.next().cells.values()) {
                    if (tiles <= tileCap) break;
                    if (c.beforeTile != null) { c.beforeTile = null; tiles--; }
                    if (c.afterTile != null && tiles > tileCap) { c.afterTile = null; tiles--; }
                }
            }
    }

    /** For tests: the ids of the undo list, oldest first. */
    List<Long> undoIds() { List<Long> l = new ArrayList<>(); for (Group<S, T> g : undo) l.add(g.id); return l; }
    List<Long> redoIds() { List<Long> l = new ArrayList<>(); for (Group<S, T> g : redo) l.add(g.id); return l; }

    /** For tests: the cells of the newest undo group by key. */
    Map<Long, Cell<S, T>> top() { Group<S, T> g = undo.peekLast(); return g == null ? Map.of() : g.cells; }
}
