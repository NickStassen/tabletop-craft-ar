package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

/** sections_v1 chunk snapshots (PROTOCOL.md 4.18): encoding, deltas, splitting, and the golden fixtures. */
class SectionCodecTest {
    static int hills(int x, int y, int z) { return y < 4 + ((x * 7 + z * 3) & 7) ? 1 : y == 4 + ((x * 7 + z * 3) & 7) ? 2 : 0; }

    static int[] grid(int ox, int oy, int oz, int dx, int dy, int dz) {
        int[] g = new int[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) g[(y * dz + z) * dx + x] = hills(ox + x, oy + y, oz + z);
        return g;
    }

    @Test void sectionsRoundTripIncludingPartialEdges() {
        int dx = 40, dy = 20, dz = 18;
        int[] g = grid(0, 0, 0, dx, dy, dz);
        int[] back = new int[g.length];
        java.util.Arrays.fill(back, -1);
        List<int[]> keys = SectionCodec.allSections(dx, dy, dz);
        assertEquals(3 * 2 * 2, keys.size());
        for (int[] k : keys) SectionCodec.decodeSection(SectionCodec.encodeSection(g, dx, dy, dz, k[0], k[1], k[2]), back, dx, dy, dz, k[0], k[1], k[2]);
        assertArrayEquals(g, back);
    }

    @Test void deltaListsOnlyNewAndChangedSections() {
        int dx = 48, dy = 16, dz = 32;
        RegionSpec a = new RegionSpec("world", 0, 0, 0, dx, dy, dz), b = new RegionSpec("world", 16, 0, 0, dx, dy, dz);
        int[] shift = SectionCodec.deltaShift(a, b);
        assertArrayEquals(new int[] { 16, 0, 0 }, shift);
        int[] oldG = grid(0, 0, 0, dx, dy, dz), newG = grid(16, 0, 0, dx, dy, dz);
        List<int[]> changed = SectionCodec.changedSections(oldG, newG, dx, dy, dz, shift);
        assertEquals(2, changed.size(), "only the new column of sections");
        for (int[] k : changed) assertEquals(2, k[0]);
        newG[(15 * dz + 3) * dx + 4] = 7;   // a block changed inside the overlap
        assertEquals(3, SectionCodec.changedSections(oldG, newG, dx, dy, dz, shift).size());
        assertNull(SectionCodec.deltaShift(a, new RegionSpec("world", 8, 0, 0, dx, dy, dz)), "not whole sections");
        assertNull(SectionCodec.deltaShift(a, new RegionSpec("world", 16, 0, 0, dx + 16, dy, dz)), "other dims");
        assertNull(SectionCodec.deltaShift(a, new RegionSpec("nether", 16, 0, 0, dx, dy, dz)), "other world");
        assertNull(SectionCodec.deltaShift(a, new RegionSpec("world", 48, 0, 0, dx, dy, dz)), "no overlap");
    }

    @Test void splitKeepsPartsUnderTheLimit() {
        List<SectionCodec.Section> secs = new ArrayList<>();
        for (int i = 0; i < 100; i++) secs.add(new SectionCodec.Section(i, 0, 0, new int[2000]));   // ~4 KB each
        var parts = SectionCodec.split(secs, 64 * 1024, 1000);
        assertTrue(parts.size() > 1);
        int n = 0;
        for (var p : parts) {
            long b = 1000;
            for (var s : p) b += s.estimatedBytes();
            assertTrue(b <= 64 * 1024 || p.size() == 1);
            n += p.size();
        }
        assertEquals(100, n);
        assertEquals(1, SectionCodec.split(List.of(), 1024, 100).size(), "an empty delta is still one message");
    }

    @Test void messageJsonIsCloseToTheEstimate() {
        int dx = 32, dy = 32, dz = 32;
        int[] g = grid(0, 0, 0, dx, dy, dz);
        List<SectionCodec.Section> secs = new ArrayList<>();
        long est = 400;
        for (int[] k : SectionCodec.allSections(dx, dy, dz)) {
            var s = new SectionCodec.Section(k[0], k[1], k[2], SectionCodec.encodeSection(g, dx, dy, dz, k[0], k[1], k[2]));
            secs.add(s); est += s.estimatedBytes();
        }
        JsonObject m = Protocol.chunkSnapshotSections(1, new RegionSpec("world", 0, 0, 0, dx, dy, dz), List.of("minecraft:air", "minecraft:stone", "minecraft:grass_block"),
            new int[] { 8, 0, 8 }, 0, 1, -1, null, secs, false, null);
        int real = Protocol.GSON.toJson(m).length();
        assertTrue(Math.abs(est - real) < real * 0.1 + 200, "estimate " + est + " vs real " + real);
        assertFalse(m.has("base"));
        assertEquals("sections_v1", m.get("encoding").getAsString());
    }

    static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    static int[] ints(JsonArray a) { int[] r = new int[a.size()]; for (int i = 0; i < r.length; i++) r[i] = a.get(i).getAsInt(); return r; }

    @Test void fixturesDecodeAndTheDeltaMatchesTheEncoder() throws Exception {
        JsonObject full = fixture("chunk_snapshot_sections");
        int[] d = ints(full.getAsJsonArray("dims"));
        int[] g8 = new int[d[0] * d[1] * d[2]];
        for (var e : full.getAsJsonArray("sections")) {
            JsonObject s = e.getAsJsonObject();
            int[] k = ints(s.getAsJsonArray("s"));
            SectionCodec.decodeSection(ints(s.getAsJsonArray("data")), g8, d[0], d[1], d[2], k[0], k[1], k[2]);
        }
        assertEquals(2, g8[(8 * d[2]) * d[0]]);       // grass at y=8 in section 0
        assertEquals(1, g8[(3 * d[2] + 15) * d[0] + 20]);

        JsonObject delta = fixture("chunk_snapshot_delta");
        JsonObject base = delta.getAsJsonObject("base");
        assertEquals(full.get("snapshot_id").getAsInt(), base.get("snapshot_id").getAsInt());
        int[] shift = ints(base.getAsJsonArray("shift"));
        // Rebuild snapshot 9 the way a client does: copy the base shifted, then apply the listed sections.
        int[] g9 = new int[g8.length];
        for (int y = 0; y < d[1]; y++) for (int z = 0; z < d[2]; z++) for (int x = 0; x < d[0]; x++) {
            int ox = x + shift[0];
            if (ox >= 0 && ox < d[0]) g9[(y * d[2] + z) * d[0] + x] = g8[(y * d[2] + z) * d[0] + ox];
        }
        List<int[]> listed = new ArrayList<>();
        for (var e : delta.getAsJsonArray("sections")) {
            JsonObject s = e.getAsJsonObject();
            int[] k = ints(s.getAsJsonArray("s"));
            listed.add(k);
            SectionCodec.decodeSection(ints(s.getAsJsonArray("data")), g9, d[0], d[1], d[2], k[0], k[1], k[2]);
        }
        assertEquals(3, g9[16], "oak log row in the new section");
        // The server's encoder would list exactly the same sections for this pair of grids.
        List<int[]> want = SectionCodec.changedSections(g8, g9, d[0], d[1], d[2], shift);
        assertEquals(listed.size(), want.size());
        assertArrayEquals(listed.get(0), want.get(0));
        // And the fixture's view_margin parses like a region_request's.
        assertArrayEquals(new int[] { 8, 0, 0 }, RegionRequest.viewMargin(fixture("region_request_nav")));
    }

    @Test void lightTravelsPerSectionAndDeltasListLightChanges() throws Exception {
        int dx = 48, dy = 16, dz = 32;
        int[] oldG = grid(0, 0, 0, dx, dy, dz), newG = grid(16, 0, 0, dx, dy, dz);
        byte[] oldL = new byte[oldG.length], newL = new byte[newG.length];
        for (int i = 0; i < oldL.length; i++) { oldL[i] = (byte) (oldG[i] == 0 ? 240 : 0); newL[i] = (byte) (newG[i] == 0 ? 240 : 0); }
        // Same blocks after the shift, but a torch's light reaches into a kept section: it must be listed.
        newL[(15 * dz + 3) * dx + 4] = (byte) 254;
        int[] shift = { 16, 0, 0 };
        assertEquals(2, SectionCodec.changedSections(oldG, newG, dx, dy, dz, shift).size(), "blocks alone");
        assertEquals(3, SectionCodec.changedSections(oldG, newG, oldL, newL, dx, dy, dz, shift).size(), "blocks or light");
        // Per-section light round trip (same pair format as blocks).
        int[] back = new int[newL.length];
        for (int[] k : SectionCodec.allSections(dx, dy, dz))
            SectionCodec.decodeSection(SectionCodec.encodeSectionLight(newL, dx, dy, dz, k[0], k[1], k[2]), back, dx, dy, dz, k[0], k[1], k[2]);
        for (int i = 0; i < back.length; i++) assertEquals(newL[i] & 0xFF, back[i]);
        // The message: per-section "light", top-level light marker, biomes on part 0 only.
        JsonObject f = fixture("chunk_snapshot_sections_light");
        assertTrue(f.getAsJsonObject("light").get("in_sections").getAsBoolean());
        var sec = f.getAsJsonArray("sections").get(0).getAsJsonObject();
        int[] l = new int[16 * 4 * 16];
        SectionCodec.decodeSection(ints(sec.getAsJsonArray("light")), l, 16, 4, 16, 0, 0, 0);
        assertEquals(0, l[0]); assertEquals(240, l[256]); assertEquals(254, l[1023]);
        BiomeGrid b = BiomeGrid.covering(new RegionSpec("world", 0, 60, 0, 16, 4, 16));
        List<SectionCodec.Section> one = List.of(new SectionCodec.Section(0, 0, 0, new int[] { 1, 1024 }, new int[] { 240, 1024 }));
        JsonObject p0 = Protocol.chunkSnapshotSections(1, new RegionSpec("world", 0, 60, 0, 16, 4, 16), List.of("minecraft:air", "minecraft:stone"), null, 0, 2, -1, null, one, true, b);
        JsonObject p1 = Protocol.chunkSnapshotSections(1, new RegionSpec("world", 0, 60, 0, 16, 4, 16), List.of("minecraft:air", "minecraft:stone"), null, 1, 2, -1, null, List.of(), true, b);
        assertTrue(p0.get("biomes").isJsonObject() && p1.get("biomes").isJsonNull());
        assertEquals("rle_v1", p1.getAsJsonObject("light").get("encoding").getAsString());
        assertEquals(2, p0.getAsJsonArray("sections").get(0).getAsJsonObject().getAsJsonArray("light").size());
    }

    @Test void diffIndicesStopAtTheLimit() {
        int[] a = new int[10000], b = new int[10000];
        for (int i = 0; i < b.length; i += 2) b[i] = 1;
        assertEquals(4097, TabletopPlugin.changedIndices(a, b, 4097).length);
        int[] c = a.clone(); c[42] = 3;
        assertArrayEquals(new int[] { 42 }, TabletopPlugin.changedIndices(a, c, 4097));
        assertNull(TabletopPlugin.changedIndices(a, new int[5], 4097));
    }
}
