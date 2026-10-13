package dev.tabletopcraft.ar;

import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class PoseTest {
    private static Pose p(String json) { return Pose.parse(JsonParser.parseString(json)); }

    @Test void identityFacesNorth() {
        Pose q = p("{\"p\":[0,0,0],\"q\":[0,0,0,1]}");
        // Facing -Z (north) is Minecraft yaw 180.
        assertEquals(180.0, Math.abs(q.yawDeg()), 1e-4);
        assertEquals(0.0, q.pitchDeg(), 1e-4);
    }

    @Test void yaw180FacesSouth() {
        Pose q = p("{\"p\":[0,0,0],\"q\":[0,1,0,0]}");
        assertEquals(0.0, q.yawDeg(), 1e-4);
    }

    @Test void lookingDownIsPositivePitch() {
        double a = Math.toRadians(-45); // rotate about +X by -45 deg tilts (0,0,-1) downward
        Pose q = p("{\"p\":[0,0,0],\"q\":[" + Math.sin(a / 2) + ",0,0," + Math.cos(a / 2) + "]}");
        assertEquals(45.0, q.pitchDeg(), 1e-3);
    }

    @Test void mapsWithBlockSize() {
        Pose q = p("{\"p\":[0.16,0.5,0.16]}");
        assertArrayEquals(new double[] { 116, 110, -24 }, q.toWorld(100, 60, -40, 0.01), 1e-9);
    }

    @Test void trackedDefaultsTrueAndClamps() {
        Pose q = p("{\"p\":[0,999,0],\"tracked\":false}");
        assertFalse(q.tracked());
        assertEquals(Pose.MAX_METRES, q.py());
        assertTrue(p("{\"p\":[0,0,0]}").tracked());
        assertThrows(IllegalArgumentException.class, () -> p("{\"p\":[0,0]}"));
    }

    @Test void scaleClampNote() {
        GiantAvatar g = new GiantAvatar();
        g.blockSizeM = 0.01;
        assertEquals(16.0, g.computeScale(16.0));
        assertNotNull(g.note);
        g.blockSizeM = 0.1;
        assertEquals(1.75 / 0.1 / 1.8, g.computeScale(16.0), 1e-9);
        assertNull(g.note);
    }

    @Test void avatarConfigValidation() {
        assertThrows(IllegalArgumentException.class, () -> AvatarConfig.parse(JsonParser.parseString("{\"block_size_m\":5}").getAsJsonObject()));
        AvatarConfig c = AvatarConfig.parse(JsonParser.parseString("{\"enabled\":true,\"skin_player\":\"Steve\"}").getAsJsonObject());
        assertTrue(c.enabled());
        assertNull(c.blockSizeM());
    }
}
