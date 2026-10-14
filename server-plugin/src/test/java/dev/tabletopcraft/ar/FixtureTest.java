package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;

import static org.junit.jupiter.api.Assertions.*;

/** Round-trips the golden PROTOCOL.md examples shipped in tools/fixtures (PROTOCOL.md section 6.4). */
class FixtureTest {
    private static JsonObject fixture(String name) throws Exception {
        Path p = Path.of("..", "tools", "fixtures", name + ".json");
        return JsonParser.parseString(Files.readString(p)).getAsJsonObject();
    }

    @Test void snapshotFixtureDecodesAndReencodes() throws Exception {
        JsonObject m = fixture("chunk_snapshot");
        var dims = m.getAsJsonArray("dims");
        int volume = dims.get(0).getAsInt() * dims.get(1).getAsInt() * dims.get(2).getAsInt();
        var arr = m.getAsJsonArray("data");
        int[] data = new int[arr.size()];
        for (int i = 0; i < data.length; i++) data[i] = arr.get(i).getAsInt();
        int[] grid = BlockCodec.decode(data, volume);
        assertArrayEquals(data, BlockCodec.encode(grid));
    }

    private static int[] ints(com.google.gson.JsonArray a) {
        int[] r = new int[a.size()];
        for (int i = 0; i < r.length; i++) r[i] = a.get(i).getAsInt();
        return r;
    }

    /** PROTOCOL.md 4.4.1: optional light (rle_v1) and biomes (4x4x4, world-aligned quart grid). */
    @Test void extrasFixtureDecodesAndMatchesTheEncoders() throws Exception {
        JsonObject m = fixture("chunk_snapshot_extras");
        var o = m.getAsJsonArray("origin");
        var d = m.getAsJsonArray("dims");
        RegionSpec r = new RegionSpec("world", o.get(0).getAsInt(), o.get(1).getAsInt(), o.get(2).getAsInt(), d.get(0).getAsInt(), d.get(1).getAsInt(), d.get(2).getAsInt());
        JsonObject light = m.getAsJsonObject("light");
        assertEquals("rle_v1", light.get("encoding").getAsString());
        int[] lightData = ints(light.getAsJsonArray("data"));
        int[] lv = BlockCodec.decode(lightData, r.volume());
        byte[] packed = new byte[lv.length];
        for (int i = 0; i < lv.length; i++) { assertTrue(lv[i] >= 0 && lv[i] <= 255); packed[i] = (byte) lv[i]; }
        assertArrayEquals(lightData, BlockCodec.encode(packed));
        assertEquals(254, lv[r.index(2, 1, 3)]); // sky 15, block 14
        long est = BlockCodec.estimateEncodedBytes(packed), real = Protocol.ints(lightData).toString().length();
        assertTrue(Math.abs(est - real) <= 2, "light size estimate " + est + " vs " + real);

        JsonObject b = m.getAsJsonObject("biomes");
        BiomeGrid g = BiomeGrid.covering(r);
        assertArrayEquals(ints(b.getAsJsonArray("q_origin")), new int[] { g.qx, g.qy, g.qz });
        assertArrayEquals(ints(b.getAsJsonArray("q_dims")), new int[] { g.qdx, g.qdy, g.qdz });
        int[] cells = BlockCodec.decode(ints(b.getAsJsonArray("data")), g.qdx * g.qdy * g.qdz);
        var pal = b.getAsJsonArray("palette");
        for (int qy = 0; qy < g.qdy; qy++) for (int qz = 0; qz < g.qdz; qz++) for (int qx = 0; qx < g.qdx; qx++)
            g.set(qx, qy, qz, pal.get(cells[g.index(qx, qy, qz)]).getAsString(), 0, 0, 0);
        g.climate = new double[][] { { 0.8, 0.4 }, { 0.8, 0.9 } };
        JsonObject out = g.toJson();
        for (String k : new String[] { "res", "q_origin", "q_dims", "palette", "climate", "encoding", "data" }) assertEquals(b.get(k), out.get(k), k);
    }

    @Test void negativeOriginsUseFloorQuarts() {
        BiomeGrid g = BiomeGrid.covering(new RegionSpec("w", -5, -64, -1, 6, 8, 2));
        // x -5..0 -> quarts -2..0, y -64..-57 -> -16..-15, z -1..0 -> -1..0
        assertArrayEquals(new int[] { -2, -16, -1, 3, 2, 2 }, new int[] { g.qx, g.qy, g.qz, g.qdx, g.qdy, g.qdz });
    }

    @Test void blockUpdateLightFixtureAndBuilder() throws Exception {
        JsonObject m = fixture("block_update_light");
        for (var e : m.getAsJsonArray("light")) {
            int[] c = ints(e.getAsJsonArray());
            assertEquals(4, c.length);
            assertTrue(c[3] >= 0 && c[3] <= 255);
        }
        RegionSpec r = new RegionSpec("w", 0, 0, 0, 4, 2, 4);
        byte[] before = new byte[r.volume()], after = new byte[r.volume()];
        after[r.index(1, 1, 1)] = (byte) 254;
        after[r.index(2, 1, 1)] = (byte) 253;
        after[r.index(1, 1, 2)] = (byte) 253;
        var diff = BlockCodec.lightDiff(before, after, r);
        JsonObject built = Protocol.blockUpdate(7, java.util.List.of("minecraft:torch"), java.util.List.of(new Protocol.Change(1, 1, 1, 3)), diff);
        assertEquals(m.get("light"), built.get("light"));
        assertEquals(m.get("changes"), built.get("changes"));
        assertFalse(Protocol.blockUpdate(7, java.util.List.of(), java.util.List.of(), java.util.List.of()).has("light"));
    }

    @Test void snapshotExtrasStayNullWithoutCaps() {
        JsonObject s = Protocol.chunkSnapshot(1, new RegionSpec("w", 0, 0, 0, 1, 1, 1), java.util.List.of("minecraft:air"), new int[] { 0, 1 });
        Protocol.addExtras(s, null, null);
        assertTrue(s.get("light").isJsonNull() && s.get("biomes").isJsonNull());
        Protocol.addExtras(s, new byte[] { (byte) 0xF0 }, null);
        assertEquals("rle_v1", s.getAsJsonObject("light").get("encoding").getAsString());
        assertEquals(Protocol.ints(240, 1), s.getAsJsonObject("light").get("data"));
    }

    @Test void poseFixtureParses() throws Exception {
        JsonObject m = fixture("pose");
        Pose h = Pose.parse(m.get("head"));
        assertEquals(0.55, h.py(), 1e-9);
        assertFalse(Pose.parse(m.get("right")).tracked());
        // 45 degrees about +Y from "facing north" (yaw 180) -> facing north-west (yaw 135).
        assertEquals(135.0, h.yawDeg(), 0.01);
    }

    @Test void playerPositionsFixtureHasSkinModel() throws Exception {
        JsonObject p = fixture("player_positions").getAsJsonArray("players").get(0).getAsJsonObject();
        assertTrue(p.get("skin").getAsString().startsWith("https://textures.minecraft.net/"));
        assertEquals(SkinLookup.model(true), p.get("skin_model").getAsString());
    }

    /** PROTOCOL.md 4.20: block_edit (place, break) and the error reply that carries the edit's op and pos. */
    @Test void blockEditFixturesParseAndErrorMatchesBuilder() throws Exception {
        BlockEdit p = BlockEdit.parse(fixture("block_edit"));
        assertEquals(new BlockEdit(BlockEdit.Op.PLACE, 105, 64, -35, "minecraft:oak_log[axis=x]", "east"), p);
        BlockEdit b = BlockEdit.parse(fixture("block_edit_break"));
        assertEquals(BlockEdit.Op.BREAK, b.op());
        assertEquals("up", b.face());
        JsonObject fx = fixture("error_edit_denied");
        JsonObject built = p.error(fx.get("code").getAsString(), fx.get("message").getAsString(), fx.get("ref_seq").getAsLong());
        for (String k : new String[] { "type", "code", "message", "ref_seq", "fatal", "op", "pos" }) assertEquals(fx.get(k), built.get(k), k);
    }

    /** PROTOCOL.md 4.24: block_registry, built from the same blocks as the fixture (BlockRegistryTest.sample). */
    @Test void blockRegistryFixtureMatchesBuilder() throws Exception {
        JsonObject fx = fixture("block_registry");
        JsonObject built = BlockRegistry.build(BlockRegistryTest.sample(), java.util.Set.of("minecraft:command_block")).message();
        for (String k : new String[] { "type", "count", "categories", "items", "variants" }) assertEquals(fx.get(k), built.get(k), k);
        int n = 0;
        var cats = fx.getAsJsonArray("categories");
        for (int i = 0; i < cats.size(); i++) {
            assertEquals(BlockRegistry.CATEGORIES.get(i)[0], cats.get(i).getAsJsonObject().get("id").getAsString());
            n += cats.get(i).getAsJsonObject().getAsJsonArray("blocks").size();
        }
        assertEquals(fx.get("count").getAsInt(), n);
    }

    @Test void avatarConfigFixtureParses() throws Exception {
        AvatarConfig c = AvatarConfig.parse(fixture("avatar_config"));
        assertEquals(0.01, c.blockSizeM());
        assertEquals("wide", c.model());
    }
}
