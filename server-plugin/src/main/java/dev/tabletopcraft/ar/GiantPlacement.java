package dev.tabletopcraft.ar;

/**
 * Where the giant stands, as pure maths (no Bukkit types, unit-tested in GiantPlacementTest). ARCHITECTURE.md 3.5.
 * <ul>
 * <li>{@link Mode#GROUND} (default): X/Z of the mapped head, clamped into the region's X/Z bounds; feet on the
 *     ground there; hands keep the user's real head-to-hand offsets, scaled from the user's height to the giant's.</li>
 * <li>{@link Mode#EXACT}: head and hands mapped exactly ({@code origin + p / block_size_m}); feet hang below the head.
 *     With a small block size this puts the giant far outside the region and high in the air.</li>
 * </ul>
 */
public final class GiantPlacement {
    private GiantPlacement() {}

    public enum Mode {
        GROUND, EXACT;

        /** "exact" (any case) is EXACT; anything else, including null, is the default GROUND. */
        public static Mode parse(String s) { return s != null && s.trim().equalsIgnoreCase("exact") ? EXACT : GROUND; }

        public String id() { return name().toLowerCase(java.util.Locale.ROOT); }
    }

    /** Ground height lookup: Y of the first free block above the ground in column (x, z), NaN if unknown (chunk not loaded). */
    @FunctionalInterface
    public interface Ground { double feetY(int x, int z); }

    /** Player eye height and model height at scale 1, in blocks. */
    public static final double EYE_HEIGHT = 1.62, HEIGHT = 1.8;
    /** Half the player hitbox width at scale 1 (0.6 / 2), used for the ground footprint. */
    public static final double HALF_WIDTH = 0.3;
    /** Keep the giant's centre this far inside the region edge. */
    public static final double EDGE_INSET = 0.5;

    /**
     * Target placement: {@code feet} and {@code eye} are world positions; {@code leftOffset}/{@code rightOffset} are
     * hand positions relative to the eye (null when the pose has no hand). Offsets, not positions, so a smoothed eye
     * carries the hands with it.
     */
    public record Placement(double[] feet, double[] eye, double[] leftOffset, double[] rightOffset, double groundY) {}

    /**
     * @param r            the region the pose frame is anchored to (tabletop origin = region origin)
     * @param scale        the Mannequin SCALE attribute (already clamped)
     * @param userHeightM  real height of the headset user
     * @param minY         world min build height (exact mode never puts the feet below it)
     * @return the placement, or null in ground mode when the ground under the centre column is unknown
     */
    public static Placement place(Mode mode, Pose head, Pose left, Pose right, RegionSpec r, double blockSizeM,
                                  double scale, double userHeightM, Ground ground, double minY) {
        double[] hp = head.toWorld(r.ox(), r.oy(), r.oz(), blockSizeM);
        if (mode == Mode.EXACT) {
            double feetY = Math.max(minY, hp[1] - EYE_HEIGHT * scale);
            return new Placement(new double[] { hp[0], feetY, hp[2] }, hp,
                exactOffset(hp, left, r, blockSizeM), exactOffset(hp, right, r, blockSizeM), Double.NaN);
        }
        double x = clamp(hp[0], r.ox(), r.ox() + r.sx(), EDGE_INSET);
        double z = clamp(hp[2], r.oz(), r.oz() + r.sz(), EDGE_INSET);
        double gy = groundUnder(x, z, HALF_WIDTH * scale, ground);
        if (Double.isNaN(gy)) return null;
        double[] feet = { x, gy, z };
        double[] eye = { x, gy + EYE_HEIGHT * scale, z };
        double k = handScale(scale, userHeightM);
        return new Placement(feet, eye, scaledOffset(head, left, k), scaledOffset(head, right, k), gy);
    }

    /** Clamp v into [lo + inset, hi - inset]; the middle when the span is narrower than two insets. */
    public static double clamp(double v, double lo, double hi, double inset) {
        double a = lo + inset, b = hi - inset;
        if (a > b) return (lo + hi) / 2;
        return Math.max(a, Math.min(b, v));
    }

    /**
     * Highest ground over the giant's footprint: the centre column plus four columns {@code halfWidth} out, so the
     * feet don't sink into a hillside or a one-block hole. Unknown side columns are skipped; an unknown centre is NaN.
     */
    public static double groundUnder(double x, double z, double halfWidth, Ground ground) {
        double c = ground.feetY(floor(x), floor(z));
        if (Double.isNaN(c)) return Double.NaN;
        double best = c;
        double[][] side = { { halfWidth, 0 }, { -halfWidth, 0 }, { 0, halfWidth }, { 0, -halfWidth } };
        for (double[] d : side) {
            double y = ground.feetY(floor(x + d[0]), floor(z + d[1]));
            if (!Double.isNaN(y)) best = Math.max(best, y);
        }
        return best;
    }

    /** Blocks per real metre for hand offsets: giant height in blocks / user height in metres. */
    public static double handScale(double scale, double userHeightM) {
        return HEIGHT * scale / Math.max(0.5, userHeightM);
    }

    /** (hand - head) in metres, scaled to blocks. The tabletop frame's axes are the world's, so no rotation. */
    public static double[] scaledOffset(Pose head, Pose hand, double blocksPerMetre) {
        if (hand == null) return null;
        return new double[] { (hand.px() - head.px()) * blocksPerMetre, (hand.py() - head.py()) * blocksPerMetre, (hand.pz() - head.pz()) * blocksPerMetre };
    }

    private static double[] exactOffset(double[] headWorld, Pose hand, RegionSpec r, double blockSizeM) {
        if (hand == null) return null;
        double[] w = hand.toWorld(r.ox(), r.oy(), r.oz(), blockSizeM);
        return new double[] { w[0] - headWorld[0], w[1] - headWorld[1], w[2] - headWorld[2] };
    }

    public static double[] add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }

    public static double distance(double[] a, double[] b) {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Math.sqrt(dx * dx + dy * dy + dz * dz);
    }

    /** One smoothing step: move {@code alpha} (0..1] of the way to the target; alpha 1 = no smoothing. */
    public static double[] approach(double[] cur, double[] target, double alpha) {
        if (cur == null || alpha >= 1) return target.clone();
        return new double[] { cur[0] + (target[0] - cur[0]) * alpha, cur[1] + (target[1] - cur[1]) * alpha, cur[2] + (target[2] - cur[2]) * alpha };
    }

    /** Angle version of {@link #approach}, along the shorter way round; degrees. */
    public static float approachAngle(float cur, float target, double alpha) {
        if (alpha >= 1) return wrap(target);
        return wrap((float) (cur + wrap(target - cur) * alpha));
    }

    /** Body yaw follows the head yaw, but at most {@code maxDiff} degrees behind (vanilla players: 50). */
    public static float lagBodyYaw(float bodyYaw, float headYaw, float maxDiff) {
        float diff = wrap(headYaw - bodyYaw);
        if (Math.abs(diff) > maxDiff) return wrap(bodyYaw + diff - Math.signum(diff) * maxDiff);
        return bodyYaw;
    }

    /** Wrap degrees into [-180, 180). */
    public static float wrap(float a) {
        a %= 360;
        if (a >= 180) a -= 360;
        if (a < -180) a += 360;
        return a;
    }

    private static int floor(double v) { return (int) Math.floor(v); }
}
