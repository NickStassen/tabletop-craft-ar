package dev.tabletopcraft.ar;

/**
 * Where the giant is, as pure maths (no Bukkit types, unit-tested in GiantPlacementTest). ARCHITECTURE.md 3.5.
 * <ul>
 * <li>{@link Mode#FLOATING} (default): no body. A player-head display and two hand displays sit exactly where the
 *     headset and controllers are relative to the table ({@code origin + p / block_size_m}), sized like a real
 *     head and hands at the table's scale, turned with the full headset/controller orientation.</li>
 * <li>{@link Mode#GROUND}: a Mannequin at X/Z of the mapped head, clamped into the region's X/Z bounds, feet on
 *     the ground there; hands keep the user's real head-to-hand offsets, scaled from the user's height to the giant's.</li>
 * <li>{@link Mode#EXACT}: a Mannequin whose head and hands are mapped exactly; feet hang below the head. With a
 *     small block size this puts the giant far outside the region and high in the air.</li>
 * </ul>
 */
public final class GiantPlacement {
    private GiantPlacement() {}

    public enum Mode {
        FLOATING, GROUND, EXACT;

        /** "ground" or "exact" (any case); anything else, including null, is the default FLOATING. */
        public static Mode parse(String s) {
            String t = s == null ? "" : s.trim().toLowerCase(java.util.Locale.ROOT);
            return t.equals("ground") ? GROUND : t.equals("exact") ? EXACT : FLOATING;
        }

        public String id() { return name().toLowerCase(java.util.Locale.ROOT); }

        /** Ground and exact use a Mannequin body; floating is a head and two hands only. */
        public boolean hasBody() { return this != FLOATING; }
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
        if (mode == Mode.FLOATING)
            return new Placement(hp.clone(), hp, exactOffset(hp, left, r, blockSizeM), exactOffset(hp, right, r, blockSizeM), Double.NaN);
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

    // ---------- floating head and hands ----------
    /** A player_head item in an item display with the FIXED transform is a cube this many blocks wide, centred on the entity. */
    public static final double PLAYER_HEAD_BLOCKS = 0.5;
    /** A block item in an item display with the NONE transform is a 1-block cube centred on the entity. */
    public static final double BLOCK_ITEM_BLOCKS = 1.0;
    /** Hand box proportions: x across the hand, y thickness, z length along the controller's forward axis. */
    public static final float[] HAND_SHAPE = { 0.8f, 0.45f, 1.0f };

    /** Head and hand sizes in blocks; both shrink by the same factor when the head would exceed the cap. */
    public record FloatingSizes(double headBlocks, double handBlocks, double unclampedHeadBlocks) {
        public boolean clamped() { return unclampedHeadBlocks > headBlocks + 1e-9; }
    }

    public static FloatingSizes floatingSizes(double blockSizeM, double headM, double handM, double maxHeadBlocks) {
        double head = headM / blockSizeM, hand = handM / blockSizeM;
        double k = maxHeadBlocks > 0 && head > maxHeadBlocks ? maxHeadBlocks / head : 1;
        return new FloatingSizes(head * k, hand * k, head);
    }

    /**
     * [bedrock] SCALE of the Mannequin that stands in for a floating head on Bedrock clients: its 0.5-block head as big
     * as the floating head, at most {@code cap} (the attribute's 16, or max-scale), at least the attribute's 0.0625.
     * Its eyes go where the head is, so its feet are {@link #EYE_HEIGHT} x this below.
     */
    public static double bedrockScale(double headBlocks, double cap) {
        return Math.max(0.0625, Math.min(cap, headBlocks / PLAYER_HEAD_BLOCKS));
    }

    /** Display transformation pieces: left and right rotations as quaternions (x, y, z, w), and a scale. */
    public record DisplayTransform(float[] left, float[] scale, float[] right) {}

    /**
     * The head display's transformation. In the item display's own frame (entity yaw and pitch 0) a player_head with
     * the FIXED item transform is a centred cube facing +Z: the game turns item displays 180 degrees about Y, and
     * template_skull's "fixed" transform turns the head another 180 degrees and lifts it to the centre. The right
     * rotation (applied first) turns the face to -Z, which is "identity" in the pose frame (PROTOCOL.md section 3).
     * The left rotation is then the headset quaternion unchanged, because the tabletop frame's axes are the world's
     * and both are right-handed.
     */
    public static DisplayTransform headTransform(Pose head, double headBlocks) {
        float s = (float) (headBlocks / PLAYER_HEAD_BLOCKS);
        return new DisplayTransform(new float[] { head.qx(), head.qy(), head.qz(), head.qw() }, new float[] { s, s, s }, new float[] { 0, 1, 0, 0 });
    }

    /** A hand: a flat box (HAND_SHAPE x handBlocks) turned with the controller; its long side follows the controller's forward. */
    public static DisplayTransform handTransform(Pose hand, double handBlocks) {
        float s = (float) (handBlocks / BLOCK_ITEM_BLOCKS);
        return new DisplayTransform(new float[] { hand.qx(), hand.qy(), hand.qz(), hand.qw() },
            new float[] { HAND_SHAPE[0] * s, HAND_SHAPE[1] * s, HAND_SHAPE[2] * s }, new float[] { 0, 0, 0, 1 });
    }

    /** Rotate v by the unit quaternion q = (x, y, z, w): the standard right-handed rotation, as JOML applies it. */
    public static double[] rotate(float[] q, double[] v) {
        double x = q[0], y = q[1], z = q[2], w = q[3];
        // v' = v + 2w (u x v) + 2 u x (u x v), u = (x, y, z)
        double cx = y * v[2] - z * v[1], cy = z * v[0] - x * v[2], cz = x * v[1] - y * v[0];
        double dx = y * cz - z * cy, dy = z * cx - x * cz, dz = x * cy - y * cx;
        return new double[] { v[0] + 2 * (w * cx + dx), v[1] + 2 * (w * cy + dy), v[2] + 2 * (w * cz + dz) };
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
