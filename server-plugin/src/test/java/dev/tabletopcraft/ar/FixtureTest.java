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

    @Test void poseFixtureParses() throws Exception {
        JsonObject m = fixture("pose");
        Pose h = Pose.parse(m.get("head"));
        assertEquals(0.55, h.py(), 1e-9);
        assertFalse(Pose.parse(m.get("right")).tracked());
        // 45 degrees about +Y from "facing north" (yaw 180) -> facing north-west (yaw 135).
        assertEquals(135.0, h.yawDeg(), 0.01);
    }

    @Test void avatarConfigFixtureParses() throws Exception {
        AvatarConfig c = AvatarConfig.parse(fixture("avatar_config"));
        assertEquals(0.01, c.blockSizeM());
        assertEquals("wide", c.model());
    }
}
