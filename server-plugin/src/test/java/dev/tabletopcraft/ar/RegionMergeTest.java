package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;

import java.util.HashSet;
import java.util.List;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.*;

/** [nav] Incremental region reads (RegionMerge): which sections are read, what is copied, and that the result is exact. */
class RegionMergeTest {
    /** A deterministic "world": block id and light at any world position. */
    static int block(int x, int y, int z) { return Math.floorMod(x * 7 + y * 13 + z * 3, 11); }
    static byte light(int x, int y, int z) { return (byte) Math.floorMod(x * 5 + y + z * 9, 256); }

    static int[] read(RegionSpec r) {
        int[] g = new int[r.volume()];
        for (int y = 0; y < r.sy(); y++) for (int z = 0; z < r.sz(); z++) for (int x = 0; x < r.sx(); x++) g[r.index(x, y, z)] = block(r.ox() + x, r.oy() + y, r.oz() + z);
        return g;
    }

    static byte[] readLight(RegionSpec r) {
        byte[] g = new byte[r.volume()];
        for (int y = 0; y < r.sy(); y++) for (int z = 0; z < r.sz(); z++) for (int x = 0; x < r.sx(); x++) g[r.index(x, y, z)] = light(r.ox() + x, r.oy() + y, r.oz() + z);
        return g;
    }

    /** What RegionReader.walkFresh produces: kept sections copied from the base, fresh ones "read from the world". */
    static int[] incremental(RegionMerge.Base b, RegionSpec r, boolean[] fresh) {
        int[] g = RegionMerge.copyKept(b, r, fresh);
        for (int[] k : RegionMerge.keys(fresh, r))
            for (int y = k[1] * 16; y < Math.min(r.sy(), k[1] * 16 + 16); y++)
                for (int z = k[2] * 16; z < Math.min(r.sz(), k[2] * 16 + 16); z++)
                    for (int x = k[0] * 16; x < Math.min(r.sx(), k[0] * 16 + 16); x++) g[r.index(x, y, z)] = block(r.ox() + x, r.oy() + y, r.oz() + z);
        return g;
    }

    static RegionMerge.Base base(RegionSpec r) { return new RegionMerge.Base(r, read(r), readLight(r), null, 0); }

    @Test void oneSectionShiftReadsOneColumn() {
        RegionSpec a = new RegionSpec("world", -144, -16, -144, 288, 160, 288), b = new RegionSpec("world", -128, -16, -144, 288, 160, 288);
        boolean[] fresh = RegionMerge.freshSections(a, b);
        assertEquals(10 * 18, RegionMerge.count(fresh), "one column of sections: 18 along z, 10 high");
        for (int[] k : RegionMerge.keys(fresh, b)) assertEquals(17, k[0]);
        assertEquals(18, RegionMerge.freshChunks(fresh, b).size(), "a chunk-aligned region captures one row of 18 chunks");
        assertEquals(180L * 4096, RegionMerge.volume(RegionMerge.keys(fresh, b), b));
        assertArrayEquals(read(b), incremental(base(a), b, fresh), "kept sections copied + fresh read = a full read");
    }

    @Test void mergedGridEqualsFullReadForAnyOverlap() {
        RegionSpec a = new RegionSpec("world", 3, 60, -21, 40, 35, 50);   // partial edge sections on every axis
        List<RegionSpec> moves = List.of(
            new RegionSpec("world", 19, 60, -21, 40, 35, 50),     // +16 x
            new RegionSpec("world", -13, 60, -5, 40, 35, 50),     // -16 x, +16 z
            new RegionSpec("world", 3, 76, -21, 40, 35, 50),      // up one section
            new RegionSpec("world", -13, 60, -37, 72, 35, 82),    // grow by a ring
            new RegionSpec("world", 19, 60, -5, 16, 35, 16),      // shrink
            new RegionSpec("world", 11, 57, -21, 40, 35, 50),     // not whole sections: still exact, more is read
            new RegionSpec("world", 35, 60, -21, 40, 35, 50));    // +32 x
        RegionMerge.Base b = base(a);
        for (RegionSpec r : moves) {
            assertTrue(RegionMerge.usable(b, r, RegionReader.LIGHT), "usable " + r);
            boolean[] fresh = RegionMerge.freshSections(a, r);
            assertArrayEquals(read(r), incremental(b, r, fresh), "blocks " + r);
            byte[] l = RegionMerge.copyKeptLight(b, r, fresh);
            byte[] want = readLight(r);
            for (int[] k : RegionMerge.keys(fresh, r))   // fresh light comes from the world; compare only kept cells
                for (int y = k[1] * 16; y < Math.min(r.sy(), k[1] * 16 + 16); y++)
                    for (int z = k[2] * 16; z < Math.min(r.sz(), k[2] * 16 + 16); z++)
                        for (int x = k[0] * 16; x < Math.min(r.sx(), k[0] * 16 + 16); x++) l[r.index(x, y, z)] = want[r.index(x, y, z)];
            assertArrayEquals(want, l, "light " + r);
        }
    }

    @Test void freshSectionsAreExactlyWhatADeltaLists() {
        RegionSpec a = new RegionSpec("world", 3, 60, -21, 40, 35, 50);
        for (RegionSpec r : List.of(new RegionSpec("world", 19, 60, -21, 40, 35, 50), new RegionSpec("world", 3, 76, -5, 40, 35, 50),
                                    new RegionSpec("world", -13, 60, -37, 72, 35, 82), new RegionSpec("world", 19, 60, -5, 16, 35, 16))) {
            boolean[] fresh = RegionMerge.freshSections(a, r);
            int[] merged = incremental(base(a), r, fresh);
            int[] shift = SectionCodec.deltaShift(a, r);
            assertNotNull(shift);
            List<int[]> listed = SectionCodec.changedSections(read(a), merged, null, null, new int[] { a.sx(), a.sy(), a.sz() }, new int[] { r.sx(), r.sy(), r.sz() }, shift);
            assertEquals(keySet(RegionMerge.keys(fresh, r)), keySet(listed), "fresh == the sections the compare would list, for " + r);
        }
        assertEquals(0, RegionMerge.count(RegionMerge.freshSections(a, a)), "the same region reads nothing");
    }

    @Test void shrinkReadsNothingAndGrowReadsTheRing() {
        RegionSpec big = new RegionSpec("world", -48, 0, -48, 96, 32, 96), small = new RegionSpec("world", -16, 0, -16, 32, 32, 32);
        assertEquals(0, RegionMerge.count(RegionMerge.freshSections(big, small)));
        assertTrue(RegionMerge.freshChunks(RegionMerge.freshSections(big, small), small).isEmpty(), "nothing to capture");
        boolean[] ring = RegionMerge.freshSections(small, big);
        assertEquals(6 * 6 * 2 - 2 * 2 * 2, RegionMerge.count(ring));
    }

    @Test void usableNeedsSameWorldOverlapAndTheExtras() {
        RegionSpec a = new RegionSpec("world", 0, 0, 0, 32, 32, 32);
        RegionMerge.Base noLight = new RegionMerge.Base(a, read(a), null, null, 0);
        assertFalse(RegionMerge.usable(noLight, new RegionSpec("world_nether", 16, 0, 0, 32, 32, 32), 0), "other world");
        assertFalse(RegionMerge.usable(noLight, new RegionSpec("world", 32, 0, 0, 32, 32, 32), 0), "no overlap");
        assertFalse(RegionMerge.usable(noLight, new RegionSpec("world", 16, 0, 0, 32, 32, 32), RegionReader.LIGHT), "base without light");
        assertFalse(RegionMerge.usable(noLight, new RegionSpec("world", 16, 0, 0, 32, 32, 32), RegionReader.BIOMES), "base without biomes");
        assertTrue(RegionMerge.usable(noLight, new RegionSpec("world", 16, 0, 0, 32, 32, 32), 0));
        assertFalse(RegionMerge.usable(null, a, 0));
        assertThrows(IllegalArgumentException.class, () -> new RegionMerge.Base(a, new int[5], null, null, 0));
    }

    @Test void baseBiomeLooksUpTheCellAndClamps() {
        BiomeGrid g = BiomeGrid.covering(new RegionSpec("world", 0, 0, 0, 8, 4, 8));
        for (int qz = 0; qz < g.qdz; qz++) for (int qx = 0; qx < g.qdx; qx++) g.set(qx, 0, qz, qx == 1 ? "minecraft:desert" : "minecraft:plains", qx * 4, 0, qz * 4);
        assertEquals("minecraft:desert", RegionReader.baseBiome(g, 5, 2, 1));
        assertEquals("minecraft:plains", RegionReader.baseBiome(g, 2, 2, 7));
        assertEquals("minecraft:desert", RegionReader.baseBiome(g, 6, 99, -5), "clamped into the grid");
    }

    @Test void sectionEstimateGrowsWithNoise() {
        RegionSpec r = new RegionSpec("world", 0, 0, 0, 32, 16, 16);
        int[] flat = new int[r.volume()];
        List<int[]> keys = SectionCodec.allSections(32, 16, 16);
        long quiet = RegionMerge.estimateSectionBytes(flat, null, r, keys);
        long noisy = RegionMerge.estimateSectionBytes(read(r), null, r, keys);
        assertTrue(quiet > 0 && quiet < 200 && noisy > 10 * quiet, quiet + " vs " + noisy);
        assertTrue(RegionMerge.estimateSectionBytes(flat, readLight(r), r, keys) > quiet, "light adds its runs");
    }

    @Test void mappedSectionEncodingUsesTheMap() {
        int[] g = { 5, 5, 7, 7 };
        int[] map = new int[8];
        map[5] = 0; map[7] = 1;
        assertArrayEquals(new int[] { 0, 2, 1, 2 }, SectionCodec.encodeSection(g, map, 4, 1, 1, 0, 0, 0));
        assertArrayEquals(new int[] { 5, 2, 7, 2 }, SectionCodec.encodeSection(g, 4, 1, 1, 0, 0, 0));
    }

    static Set<String> keySet(List<int[]> keys) {
        Set<String> s = new HashSet<>();
        for (int[] k : keys) s.add(k[0] + "," + k[1] + "," + k[2]);
        return s;
    }
}
