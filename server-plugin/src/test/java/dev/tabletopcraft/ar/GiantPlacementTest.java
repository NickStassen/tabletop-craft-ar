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

    @Test void modeParsesWithFloatingDefault() {
        assertEquals(GiantPlacement.Mode.FLOATING, GiantPlacement.Mode.parse(null));
        assertEquals(GiantPlacement.Mode.FLOATING, GiantPlacement.Mode.parse("bogus"));
        assertEquals(GiantPlacement.Mode.FLOATING, GiantPlacement.Mode.parse("floating"));
        assertEquals(GiantPlacement.Mode.GROUND, GiantPlacement.Mode.parse("ground"));
        assertEquals(GiantPlacement.Mode.EXACT, GiantPlacement.Mode.parse(" EXACT "));
        assertEquals("floating", GiantPlacement.Mode.FLOATING.id());
        assertFalse(GiantPlacement.Mode.FLOATING.hasBody());
        assertTrue(GiantPlacement.Mode.GROUND.hasBody() && GiantPlacement.Mode.EXACT.hasBody());
    }

    private static Pose pq(double x, double y, double z, double qx, double qy, double qz, double qw) {
        return Pose.parse(JsonParser.parseString("{\"p\":[" + x + "," + y + "," + z + "],\"q\":[" + qx + "," + qy + "," + qz + "," + qw + "]}"));
    }

    @Test void floatingMapsHeadAndHandsExactly() {
        // Headset 0.16 m east/south of the corner, 0.5 m above the table; left hand 0.2 m left, 0.3 m lower, 0.25 m ahead.
        Pose head = p(0.16, 0.5, 0.16), left = p(-0.04, 0.2, -0.09), right = p(0.36, 0.2, -0.09);
        var pl = GiantPlacement.place(GiantPlacement.Mode.FLOATING, head, left, right, R, 0.01, 16, 1.75, (x, z) -> { throw new AssertionError("no ground lookup"); }, -64);
        assertArrayEquals(new double[] { 116, 110, -24 }, pl.eye(), 1e-9);     // origin + p / 0.01
        assertArrayEquals(pl.eye(), pl.feet(), 1e-9);                           // no body
        assertArrayEquals(new double[] { -20, -30, -25 }, pl.leftOffset(), 1e-9);
        assertArrayEquals(new double[] { 116 + 20, 110 - 30, -24 - 25 }, GiantPlacement.add(pl.eye(), pl.rightOffset()), 1e-9);
        assertTrue(Double.isNaN(pl.groundY()));
    }

    @Test void floatingSizesFollowTheTableScaleWithACap() {
        var s = GiantPlacement.floatingSizes(0.01, 0.25, 0.1, 64);
        assertEquals(25, s.headBlocks(), 1e-9);
        assertEquals(10, s.handBlocks(), 1e-9);
        assertFalse(s.clamped());
        var small = GiantPlacement.floatingSizes(0.002, 0.25, 0.1, 64);   // 125-block head -> capped, hands shrink alike
        assertEquals(64, small.headBlocks(), 1e-9);
        assertEquals(64 * 0.4, small.handBlocks(), 1e-9);
        assertTrue(small.clamped());
        assertEquals(125, small.unclampedHeadBlocks(), 1e-9);
        assertEquals(2.5, GiantPlacement.floatingSizes(0.1, 0.25, 0.1, 0).headBlocks(), 1e-9);  // 0 = no cap
    }

    private static void assertVec(double[] want, double[] got) { assertArrayEquals(want, got, 1e-5); }

    /** Face direction of the floating head in the world: the display shows the head facing +Z before its transformation. */
    private static double[] headFace(GiantPlacement.DisplayTransform t) {
        double[] v = GiantPlacement.rotate(t.right(), new double[] { 0, 0, 1 });
        return GiantPlacement.rotate(t.left(), v);
    }

    @Test void floatingHeadTurnsWithTheFullHeadsetOrientation() {
        // Identity pose faces north (-Z), as PROTOCOL.md section 3 defines.
        var id = GiantPlacement.headTransform(p(0, 0, 0), 25);
        assertVec(new double[] { 0, 0, -1 }, headFace(id));
        assertEquals(50f, id.scale()[0], 1e-6);                                   // 25 blocks / 0.5-block player head
        assertVec(new double[] { 0, 1, 0 }, GiantPlacement.rotate(id.left(), GiantPlacement.rotate(id.right(), new double[] { 0, 1, 0 })));
        // Any orientation, including roll: the face follows Pose.forward() and the top follows q * up.
        double a = Math.toRadians(35), b = Math.toRadians(-20), c = Math.toRadians(15);
        // q = Ry(a) * Rx(b) * Rz(c), built by hand.
        double[] qy = { 0, Math.sin(a / 2), 0, Math.cos(a / 2) }, qx = { Math.sin(b / 2), 0, 0, Math.cos(b / 2) }, qz = { 0, 0, Math.sin(c / 2), Math.cos(c / 2) };
        double[] q = mul(mul(qy, qx), qz);
        Pose h = pq(0.1, 0.5, 0.2, q[0], q[1], q[2], q[3]);
        var t = GiantPlacement.headTransform(h, 25);
        assertVec(h.forward(), headFace(t));
        double[] up = GiantPlacement.rotate(t.left(), GiantPlacement.rotate(t.right(), new double[] { 0, 1, 0 }));
        assertTrue(up[1] < 0.99 && Math.abs(up[0]) + Math.abs(up[2]) > 0.1, "rolled/pitched head tilts its top");
        // Yaw 90 (turned left from north) faces west, Minecraft yaw 90.
        Pose west = pq(0, 0, 0, 0, Math.sin(Math.PI / 4), 0, Math.cos(Math.PI / 4));
        assertVec(new double[] { -1, 0, 0 }, headFace(GiantPlacement.headTransform(west, 25)));
        assertEquals(90f, west.yawDeg(), 1e-3);
    }

    @Test void floatingHandIsAFlatBoxAlongTheController() {
        Pose west = pq(0, 0, 0, 0, Math.sin(Math.PI / 4), 0, Math.cos(Math.PI / 4));
        var t = GiantPlacement.handTransform(west, 10);
        assertArrayEquals(new float[] { 8f, 4.5f, 10f }, t.scale(), 1e-5f);
        assertArrayEquals(new float[] { 0, 0, 0, 1 }, t.right(), 0f);
        // The long side (local z) lies along the controller's forward/back axis, which faces west here.
        assertVec(new double[] { -10, 0, 0 }, GiantPlacement.rotate(t.left(), new double[] { 0, 0, -t.scale()[2] }));
    }

    @Test void rotateMatchesPoseForward() {
        Pose h = pq(0, 0, 0, 0.1, 0.3, -0.2, 0.92);
        float[] q = { h.qx(), h.qy(), h.qz(), h.qw() };
        assertVec(h.forward(), GiantPlacement.rotate(q, new double[] { 0, 0, -1 }));
    }

    private static double[] mul(double[] a, double[] b) {
        return new double[] {
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] - a[0] * b[2] + a[1] * b[3] + a[2] * b[0],
            a[3] * b[2] + a[0] * b[1] - a[1] * b[0] + a[2] * b[3],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2] };
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
