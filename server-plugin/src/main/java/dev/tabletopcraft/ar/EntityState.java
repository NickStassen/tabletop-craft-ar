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
    public String variant, color, markings, profession, pose, name, item, block, facing;
    public int count, rotation;
    /** Equipment slot (main_hand, off_hand, head, chest, legs, feet, body, saddle) -> item key; only non-empty slots. */
    public Map<String, String> equipment;
    /** Squared distance to the region centre: the nearest win when a region holds more than the cap. */
    public double distSq;

    public EntityState() {}

    public EntityState(int id, String type, double x, double y, double z, float yaw, float pitch) {
        this.id = id; this.type = type; this.x = x; this.y = y; this.z = z; this.yaw = yaw; this.pitch = pitch;
    }
}
