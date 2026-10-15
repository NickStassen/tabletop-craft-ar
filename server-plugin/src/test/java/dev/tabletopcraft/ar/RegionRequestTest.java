package dev.tabletopcraft.ar;

import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

class RegionRequestTest {
    private static RegionRequest p(String j) { return RegionRequest.parse(JsonParser.parseString(j).getAsJsonObject()); }

    @Test void originDims() {
        RegionRequest q = p("{\"origin\":[10,-64,-20],\"dims\":[16,64,16]}");
        assertEquals(new RegionRequest(null, 10, -64, -20, 16, 64, 16), q);
    }

    @Test void centerSizeCentresXZAndY() {
        RegionRequest q = p("{\"world\":\"world\",\"center\":[100.7,70.2,-5.5],\"size\":[32,64,32]}");
        assertEquals(84, q.ox()); assertEquals(38, q.oy()); assertEquals(-22, q.oz());
        assertEquals("world", q.world());
    }

    @Test void centerWithYOrigin() {
        RegionRequest q = p("{\"center\":[0,70,0],\"size\":[16,64,16],\"y_origin\":-64}");
        assertEquals(-64, q.oy()); assertEquals(-8, q.ox());
    }

    @Test void rejectsMalformed() {
        assertThrows(IllegalArgumentException.class, () -> p("{\"origin\":[0,0,0]}"));
        assertThrows(IllegalArgumentException.class, () -> p("{\"origin\":[0,0,0],\"dims\":[0,1,1]}"));
        assertThrows(IllegalArgumentException.class, () -> p("{\"center\":[0,0],\"size\":[1,1,1]}"));
    }

    @Test void viewMargin() {
        var m = JsonParser.parseString("{\"origin\":[0,0,0],\"dims\":[96,128,96],\"view_margin\":[16,16,16]}").getAsJsonObject();
        assertArrayEquals(new int[] { 16, 16, 16 }, RegionRequest.viewMargin(m));
        assertArrayEquals(new int[3], RegionRequest.viewMargin(JsonParser.parseString("{\"origin\":[0,0,0],\"dims\":[1,1,1]}").getAsJsonObject()));
        assertThrows(IllegalArgumentException.class, () -> RegionRequest.viewMargin(JsonParser.parseString("{\"view_margin\":[-1,0,0]}").getAsJsonObject()));
        // The visible box keeps at least one block.
        assertArrayEquals(new int[] { 15, 0, 16 }, RegionRequest.clampMargin(new int[] { 16, 16, 16 }, 32, 1, 64));
    }

    @Test void caps() {
        RegionCaps c = new RegionCaps(128, 384, 128, 1L << 20, 4_000_000);
        assertNull(c.check(128, 64, 128));
        assertNotNull(c.check(129, 64, 128));
        assertNotNull(c.check(128, 65, 128)); // 1,064,960 blocks > 2^20
        assertNotNull(c.check(1, 0, 1));
    }

    /** [area] PROTOCOL.md 4.25: a visible width over max_view_width is narrowed around its centre, by whole sections. */
    @Test void viewWidthIsClampedAroundTheCentre() throws Exception {
        RegionCaps c = new RegionCaps(288, 384, 288, 1L << 24, 32L << 20, 256);
        var j = JsonParser.parseString(java.nio.file.Files.readString(java.nio.file.Path.of("..", "tools", "fixtures", "region_request_resize.json"))).getAsJsonObject();
        RegionRequest q = RegionRequest.parse(j);
        RegionRequest r = c.clampView(q, RegionRequest.viewMargin(j));
        assertEquals(new RegionRequest("world", -144, 32, -144, 288, 128, 288), r, "window 320 -> 256, centre and margin kept");
        assertNull(c.check(r.dx(), r.dy(), r.dz()));
        RegionRequest ok = new RegionRequest("world", 0, 0, 0, 288, 128, 160);
        assertSame(ok, c.clampView(ok, new int[] { 16, 16, 16 }), "256 x 128 is allowed as is");
        // Only the over-wide axis changes; a cut that isn't a multiple of 32 moves the origin by the nearest whole section.
        assertEquals(new RegionRequest(null, 26, 0, 20, 288, 64, 100), c.clampView(new RegionRequest(null, 10, 0, 20, 330, 64, 100), new int[] { 16, 0, 16 }));
        // No margin: the whole region is the window.
        assertEquals(new RegionRequest(null, 64, 0, 0, 256, 64, 64), c.clampView(new RegionRequest(null, 0, 0, 0, 384, 64, 64), null));
        // A small server max.
        RegionCaps small = new RegionCaps(288, 384, 288, 1L << 24, 32L << 20, 96);
        assertEquals(new RegionRequest(null, 80, 0, 80, 128, 64, 128), small.clampView(new RegionRequest(null, 0, 0, 0, 288, 64, 288), new int[] { 16, 0, 16 }));
        com.google.gson.JsonObject lim = new com.google.gson.JsonObject();
        small.addTo(lim);
        assertEquals(96, lim.get("max_view_width").getAsInt());
        assertEquals(RegionCaps.DEFAULT_VIEW_WIDTH, RegionCaps.DEFAULT.maxViewWidth());
        assertTrue(RegionCaps.DEFAULT.maxX() >= RegionCaps.DEFAULT_VIEW_WIDTH + 32, "default max_region fits a 256 window plus the 16-block margins");
        // [nav] ... and the 48-block glide margin the headset uses at 256, 128 blocks of window height plus 16 above and below.
        assertNull(RegionCaps.DEFAULT.check(RegionCaps.DEFAULT_VIEW_WIDTH + 96, 160, RegionCaps.DEFAULT_VIEW_WIDTH + 96));
        assertNotNull(RegionCaps.OLD_DEFAULT.check(352, 160, 352), "the 0.3.0 defaults did not");
    }

    @Test void byteEstimateIsCloseToRealJson() {
        Palette pal = new Palette();
        int a = pal.id("minecraft:air"), s = pal.id("minecraft:stone");
        int[] grid = new int[16 * 16 * 16];
        for (int i = 0; i < grid.length; i++) grid[i] = (i * 7919 % 3 == 0) ? s : a;
        int[] data = BlockCodec.encode(grid);
        long est = RegionRequest.estimateSnapshotBytes(data, List.of("minecraft:air", "minecraft:stone"));
        int real = Protocol.GSON.toJson(Protocol.chunkSnapshot(1, new RegionSpec("world", 0, 0, 0, 16, 16, 16), List.of("minecraft:air", "minecraft:stone"), data)).length();
        assertTrue(Math.abs(est - real) < real * 0.05 + 300, "estimate " + est + " vs real " + real);
    }
}
