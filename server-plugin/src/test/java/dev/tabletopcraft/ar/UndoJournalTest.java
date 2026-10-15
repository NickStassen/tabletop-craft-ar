package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/** [power] The undo journal on plain strings: groups, merging, depth, redo, block entity snapshots. */
class UndoJournalTest {
    static final String AIR = "minecraft:air", STONE = "minecraft:stone", DIRT = "minecraft:dirt", CHEST = "minecraft:chest[facing=north,type=single,waterlogged=false]";

    static UndoJournal<String, String> journal(int depth, int tiles) { return new UndoJournal<>(depth, tiles); }

    static void rec(UndoJournal<String, String> j, long group, int x, String before, String after) {
        j.record(group, "edit", "world", x, 64, 0, before, null, after, String::equals);
    }

    @Test void oneHoldIsOneGroup() {
        var j = journal(100, 10);
        rec(j, 1, 0, AIR, STONE);
        rec(j, 1, 1, AIR, STONE);
        rec(j, 1, 2, AIR, STONE);
        rec(j, 2, 3, STONE, AIR);
        assertEquals(List.of(1L, 2L), j.undoIds());
        assertEquals(4, j.undoBlocks());
        var g = j.popUndo();
        assertEquals(2L, g.id);
        assertEquals(1, g.size());
        assertEquals(3, j.undoBlocks());
    }

    @Test void sameCellKeepsTheFirstBefore() {
        var j = journal(100, 10);
        rec(j, 1, 0, AIR, STONE);
        rec(j, 1, 0, STONE, DIRT);
        var c = j.top().values().iterator().next();
        assertEquals(AIR, c.before);
        assertEquals(DIRT, c.after);
        rec(j, 1, 0, DIRT, AIR);   // back where it started: the cell leaves the group, and the empty group goes
        assertEquals(0, j.undoGroups());
        assertEquals(0, j.undoBlocks());
        rec(j, 3, 5, STONE, STONE);   // no change at all: nothing recorded
        assertEquals(0, j.undoGroups());
    }

    @Test void aNewGroupClearsRedo() {
        var j = journal(100, 10);
        rec(j, 1, 0, AIR, STONE);
        rec(j, 2, 1, AIR, STONE);
        var g = j.popUndo();
        j.pushRedo(g);
        assertEquals(1, j.redoGroups());
        assertEquals(1, j.redoBlocks());
        var r = j.popRedo();
        j.pushUndoKeepRedo(r);
        assertEquals(List.of(1L, 2L), j.undoIds());
        j.pushRedo(j.popUndo());
        rec(j, 3, 9, AIR, DIRT);
        assertEquals(0, j.redoGroups(), "a new edit after an undo clears redo");
        assertEquals(List.of(1L, 3L), j.undoIds());
    }

    @Test void groupsOfDifferentWorldsDontMerge() {
        var j = journal(100, 10);
        j.record(1, "edit", "world", 0, 64, 0, AIR, null, STONE, String::equals);
        j.record(1, "edit", "world_nether", 0, 64, 0, AIR, null, STONE, String::equals);
        assertEquals(2, j.undoGroups());
    }

    @Test void depthDropsTheOldestButKeepsTheNewestWhole() {
        var j = journal(5, 10);
        for (int i = 0; i < 3; i++) rec(j, 1, i, AIR, STONE);
        for (int i = 0; i < 3; i++) rec(j, 2, 10 + i, AIR, STONE);
        assertEquals(List.of(2L), j.undoIds(), "6 blocks over a depth of 5: the oldest group goes");
        var big = new UndoJournal.Group<String, String>(3, "fill", "world");
        for (int i = 0; i < 20; i++) big.cells.put(UndoJournal.Cell.key(i, 70, 0), new UndoJournal.Cell<>(i, 70, 0, AIR, null, STONE));
        j.push(big);
        assertEquals(List.of(3L), j.undoIds(), "a box bigger than the depth is kept whole while it is the newest");
        assertEquals(20, j.undoBlocks());
        rec(j, 4, 99, AIR, DIRT);
        assertEquals(List.of(4L), j.undoIds(), "until the next edit");
    }

    @Test void pushMergesStrokesOfOneHold() {
        var j = journal(100, 10);
        for (int stroke = 0; stroke < 3; stroke++) {
            var g = new UndoJournal.Group<String, String>(7, "brush", "world");
            g.cells.put(UndoJournal.Cell.key(stroke, 64, 0), new UndoJournal.Cell<>(stroke, 64, 0, AIR, null, STONE));
            g.cells.put(UndoJournal.Cell.key(0, 64, 0), new UndoJournal.Cell<>(0, 64, 0, stroke == 0 ? AIR : STONE, null, STONE));
            j.push(g);
        }
        assertEquals(List.of(7L), j.undoIds());
        assertEquals(3, j.undoBlocks());
        assertEquals(AIR, j.top().get(UndoJournal.Cell.key(0, 64, 0)).before, "the first stroke's before wins");
    }

    @Test void tileSnapshotsAreCapped() {
        var j = journal(100, 2);
        for (int i = 0; i < 4; i++) j.record(i, "edit", "world", i, 64, 0, CHEST, "items " + i, AIR, String::equals);
        assertEquals(2, j.tiles());
        var newest = j.popUndo();
        assertEquals("items 3", newest.cells.values().iterator().next().beforeTile, "the newest keep theirs");
        j.pushRedo(newest);
        var oldest = j.undoIds().get(0);
        assertEquals(0L, oldest);
    }

    @Test void anonymousGroupsAreNegativeAndDistinct() {
        var j = journal(100, 10);
        long a = j.anonymousGroup(), b = j.anonymousGroup();
        assertTrue(a < 0 && b < 0 && a != b);
        rec(j, a, 0, AIR, STONE);
        rec(j, b, 1, AIR, STONE);
        assertEquals(2, j.undoGroups());
    }

    @Test void positionKeysRoundTrip() {
        int[][] ps = { { 0, 0, 0 }, { -1, -64, -1 }, { 29_999_999, 319, -29_999_999 }, { -30_000_000, -2048, 12345 }, { 105, 64, -35 } };
        for (int[] p : ps) {
            long k = UndoJournal.Cell.key(p[0], p[1], p[2]);
            assertArrayEquals(p, new int[] { UndoJournal.Cell.keyX(k), UndoJournal.Cell.keyY(k), UndoJournal.Cell.keyZ(k) });
        }
    }

    @Test void disabledJournalRecordsNothing() {
        var j = journal(0, 10);
        rec(j, 1, 0, AIR, STONE);
        assertFalse(j.enabled());
        assertEquals(0, j.undoGroups());
    }
}
