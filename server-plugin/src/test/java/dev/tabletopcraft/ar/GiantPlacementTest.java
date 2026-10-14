package dev.tabletopcraft.ar;

import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class GiantPlacementTest {
    private static final RegionSpec R = new RegionSpec("world", 100, 60, -40, 32, 64, 32);
    private static final GiantPlacement.Ground FLAT_70 = (x, z) -> 70;

    private static Pose p(double x, double y, double z) {
        return Pose.parse(JsonParser.parseString("{\"p\":[" + x + "," + y + "," + z + "],\"q\":[0,0,0,1]}"));
    }

    @Test void modeParsesWithGroundDefault() {
        assertEquals(GiantPlacement.Mode.GROUND, GiantPlacement.Mode.parse(null));
        assertEquals(GiantPlacement.Mode.GROUND, GiantPlacement.Mode.parse("ground"));
        assertEquals(GiantPlacement.Mode.GROUND, GiantPlacement.Mode.parse("bogus"));
        assertEquals(GiantPlacement.Mode.EXACT, GiantPlacement.Mode.parse(" EXACT "));
        assertEquals("ground", GiantPlacement.Mode.GROUND.id());
    }

    @Test void groundStandsOnTerrainUnderTheHead() {
        // Head 0.16 m east/south of the region corner, 0.5 m above the table: inside the 32-block region.
        var pl = GiantPlacement.place(GiantPlacement.Mode.GROUND, p(0.16, 0.5, 0.16), null, null, R, 0.01, 16, 1.75, FLAT_70, -64);
        assertArrayEquals(new double[] { 116, 70, -24 }, pl.feet(), 1e-9);
        assertEquals(70 + 1.62 * 16, pl.eye()[1], 1e-9);
        assertEquals(70, pl.groundY());
        assertNull(pl.leftOffset());
    }

    @Test void groundClampsIntoTheRegion() {
        // Standing back from the table: 1 m west and 1.5 m north of the corner, head 1.6 m up.
        var pl = GiantPlacement.place(GiantPlacement.Mode.GROUND, p(-1.0, 1.6, -1.5), null, null, R, 0.01, 16, 1.75, FLAT_70, -64);
        assertEquals(100.5, pl.feet()[0], 1e-9);    // ox + inset, not 0
        assertEquals(-39.5, pl.feet()[2], 1e-9);    // oz + inset, not -190
        assertEquals(70, pl.feet()[1], 1e-9);       // on the ground, not 160 blocks up
        var far = GiantPlacement.place(GiantPlacement.Mode.GROUND, p(5, 1.6, 5), null, null, R, 0.01, 16, 1.75, FLAT_70, -64);
        assertEquals(131.5, far.feet()[0], 1e-9);
        assertEquals(-8.5, far.feet()[2], 1e-9);
    }

    @Test void groundTakesTheHighestColumnOfTheFootprint() {
        // A 1-block hole under the centre and a hill 4 blocks east: the scale-16 footprint (half width 4.8) sees the hill.
        GiantPlacement.Ground g = (x, z) -> x == 116 && z == -24 ? 60 : x >= 120 ? 75 : 70;
        assertEquals(75, GiantPlacement.groundUnder(116.5, -23.5, 0.3 * 16, g));
        // Unknown side columns are skipped, an unknown centre means "can't place yet".
        GiantPlacement.Ground partial = (x, z) -> x == 116 ? 66 : Double.NaN;
        assertEquals(66, GiantPlacement.groundUnder(116.5, -23.5, 4.8, partial));
        assertNull(GiantPlacement.place(GiantPlacement.Mode.GROUND, p(0.16, 0.5, 0.16), null, null, R, 0.01, 16, 1.75, (x, z) -> Double.NaN, -64));
    }

    @Test void groundScalesHandOffsetsToTheGiant() {
        // Right hand 0.35 m right (east), 0.5 m below and 0.2 m in front (north) of the head of a 1.75 m user.
        Pose head = p(0.16, 0.5, 0.16), right = p(0.51, 0.0, -0.04);
        var pl = GiantPlacement.place(GiantPlacement.Mode.GROUND, head, null, right, R, 0.01, 16, 1.75, FLAT_70, -64);
        double k = 1.8 * 16 / 1.75; // 28.8-block giant
        assertArrayEquals(new double[] { 0.35 * k, -0.5 * k, -0.2 * k }, pl.rightOffset(), 1e-9);
        // An outstretched arm (0.7 m) reaches about 11.5 blocks on the giant, whatever the table scale.
        assertEquals(0.7 * k, GiantPlacement.scaledOffset(p(0, 0, 0), p(0.7, 0, 0), k)[0], 1e-9);
        var bigBlocks = GiantPlacement.place(GiantPlacement.Mode.GROUND, head, null, right, R, 0.05, 16, 1.75, FLAT_70, -64);
        assertArrayEquals(pl.rightOffset(), bigBlocks.rightOffset(), 1e-9);
    }

    @Test void exactMapsHeadAndHandsLikeBefore() {
        Pose head = p(0.16, 0.5, 0.16), left = p(0.11, 0.3, 0.16);
        var pl = GiantPlacement.place(GiantPlacement.Mode.EXACT, head, left, null, R, 0.01, 16, 1.75, FLAT_70, -64);
        assertArrayEquals(new double[] { 116, 110, -24 }, pl.eye(), 1e-9);
        assertEquals(110 - 1.62 * 16, pl.feet()[1], 1e-9);
        assertArrayEquals(new double[] { -5, -20, 0 }, pl.leftOffset(), 1e-9);
        // Feet never below the world floor.
        var low = GiantPlacement.place(GiantPlacement.Mode.EXACT, p(0, -10, 0), null, null, R, 0.01, 16, 1.75, FLAT_70, -64);
        assertEquals(-64, low.feet()[1], 1e-9);
    }

    @Test void smoothingAndYawLag() {
        double[] a = GiantPlacement.approach(new double[] { 0, 0, 0 }, new double[] { 10, 0, -4 }, 0.5);
        assertArrayEquals(new double[] { 5, 0, -2 }, a, 1e-9);
        assertArrayEquals(new double[] { 10, 0, -4 }, GiantPlacement.approach(null, new double[] { 10, 0, -4 }, 0.5), 1e-9);
        // Shorter way round across +-180.
        assertEquals(180f, Math.abs(GiantPlacement.approachAngle(170, -170, 0.5)), 1e-4);
        assertEquals(175f, GiantPlacement.approachAngle(170, -170, 0.25), 1e-4);
        // Body follows only beyond 50 degrees.
        assertEquals(0f, GiantPlacement.lagBodyYaw(0, 40, 50), 1e-4);
        assertEquals(30f, GiantPlacement.lagBodyYaw(0, 80, 50), 1e-4);
        assertEquals(-150f, GiantPlacement.lagBodyYaw(170, -100, 50), 1e-4);
        assertEquals(-180f, GiantPlacement.wrap(180), 1e-6);
    }

    @Test void clampHandlesNarrowRegions() {
        assertEquals(0.5, GiantPlacement.clamp(-3, 0, 1, 0.5), 1e-9);
        assertEquals(5.5, GiantPlacement.clamp(99, 0, 6, 0.5), 1e-9);
    }
}
