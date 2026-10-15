package dev.tabletopcraft.ar;

import java.util.Map;

/**
 * One non-player entity as the {@code entities} message describes it (PROTOCOL.md 4.21). Plain data, so the delta
 * logic ({@link EntityDelta}) is testable without a server; {@link EntitySampler} fills it from Bukkit on the main
 * thread. Optional fields are null/false/empty when they don't apply.
 */
public final class EntityState {
    public static final int[] NO_IDS = new int[0];

    public int id;
    public String uuid, type;
    public double x, y, z;
    public float yaw, pitch;
    /** Body yaw of living entities (yaw is the head's), else null. */
    public Float bodyYaw;
    public boolean onGround;
    public Integer vehicle;
    public int[] passengers = NO_IDS;
    public double width, height;
    public boolean baby, sheared, invisible, small, tamed, angry;
    /** Armour stands: shows its arms / its base plate (PROTOCOL.md 4.21). */
    public boolean arms, basePlate;
    /**
     * Armour stand pose in degrees, 18 values: head, body, left arm, right arm, left leg, right leg (x, y, z each);
     * null when every part has vanilla's default pose ({@link #DEFAULT_STAND_POSE}).
     */
    public float[] standPose;
    /** ArmorStand.DEFAULT_*_POSE in 26.3 (degrees), in standPose order. */
    public static final float[] DEFAULT_STAND_POSE = { 0, 0, 0, 0, 0, 0, -10, 0, -10, -15, 0, 10, -1, 0, -1, 1, 0, 1 };
    /** Part names of standPose's triples, as the protocol sends them. */
    public static final String[] STAND_PARTS = { "head", "body", "left_arm", "right_arm", "left_leg", "right_leg" };
    public String variant, color, markings, profession, pose, name, item, block, facing;
    public int count, rotation;
    /** Equipment slot (main_hand, off_hand, head, chest, legs, feet, body, saddle) -> item key; only non-empty slots. */
    public Map<String, String> equipment;
    // [living] Lead ropes, gear and the rest of vanilla's looks (PROTOCOL.md "Living world"). Defaults are "not sent".
    /** Entity id of whoever holds this entity's lead (a player, a leash knot, a mob, a boat...), or null. */
    public Integer leash;
    /** Eye height in blocks of living entities (vanilla's leash offsets and rope hold points use it); 0 = not sent. */
    public double eye;
    /** Donkeys, mules and llamas carrying a chest; a custom name shown always (CustomNameVisible). */
    public boolean chested, nameVisible;
    /** Experience orb value (the sprite depends on it); 0 = not an orb. */
    public int xp;
    /** Fishing bobber: entity id of the player holding the rod (the line goes to their hand), or null. */
    public Integer owner;
    /** Enderman: the block state it carries, or null. */
    public String carried;
    /** Shulker: how far the lid is open (0..1, 2 decimals) and the face it sticks to when not "down". */
    public double peek;
    public String attach;
    /** Display entities (block, item and text displays): transformation, billboard, light and text; null otherwise. */
    public DisplayState display;
    /** Squared distance to the region centre: the nearest win when a region holds more than the cap. */
    public double distSq;

    public EntityState() {}

    public EntityState(int id, String type, double x, double y, double z, float yaw, float pitch) {
        this.id = id; this.type = type; this.x = x; this.y = y; this.z = z; this.yaw = yaw; this.pitch = pitch;
    }

    /**
     * A display entity's render state (vanilla Display.RenderState): Display#getTransformation (translation, left
     * rotation, scale, right rotation), billboard, brightness override, item display transform and text display text.
     * Values are rounded as they are sent, so equals() is the "changed" test of the deltas.
     */
    public static final class DisplayState {
        public float[] translation = { 0, 0, 0 }, leftRotation = { 0, 0, 0, 1 }, scale = { 1, 1, 1 }, rightRotation = { 0, 0, 0, 1 };
        public String billboard = "fixed";
        /** [block, sky] light override, or null for the world's light at the entity. */
        public int[] brightness;
        /** Item displays: ItemDisplayContext name in lower case ("none", "fixed", "ground", "head", "gui", "thirdperson_righthand"...). */
        public String itemTransform;
        /** Text displays: plain text (lines split by \n), line width, ARGB background, text opacity (0-255), flags, alignment. */
        public String text;
        public int lineWidth, background, textOpacity;
        public boolean shadow, seeThrough, defaultBackground;
        public String align;

        @Override public boolean equals(Object o) {
            if (!(o instanceof DisplayState d)) return false;
            return java.util.Arrays.equals(translation, d.translation) && java.util.Arrays.equals(leftRotation, d.leftRotation)
                && java.util.Arrays.equals(scale, d.scale) && java.util.Arrays.equals(rightRotation, d.rightRotation)
                && java.util.Objects.equals(billboard, d.billboard) && java.util.Arrays.equals(brightness, d.brightness)
                && java.util.Objects.equals(itemTransform, d.itemTransform) && java.util.Objects.equals(text, d.text)
                && lineWidth == d.lineWidth && background == d.background && textOpacity == d.textOpacity && shadow == d.shadow
                && seeThrough == d.seeThrough && defaultBackground == d.defaultBackground && java.util.Objects.equals(align, d.align);
        }

        @Override public int hashCode() { return java.util.Arrays.hashCode(translation) * 31 + java.util.Objects.hashCode(text); }

        /** Rounds a vector or quaternion as it is sent (3 decimals for vectors, 4 for rotations). */
        public static float[] round(float[] v, int decimals) {
            float k = (float) Math.pow(10, decimals);
            float[] r = new float[v.length];
            for (int i = 0; i < v.length; i++) { r[i] = Math.round(v[i] * k) / k; if (r[i] == 0f) r[i] = 0f; }   // no -0.0
            return r;
        }
    }
}
