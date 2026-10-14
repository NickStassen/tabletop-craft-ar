package dev.tabletopcraft.ar;

import com.destroystokyo.paper.profile.PlayerProfile;
import com.google.gson.JsonObject;
import io.papermc.paper.datacomponent.item.ResolvableProfile;
import net.kyori.adventure.text.Component;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.attribute.Attribute;
import org.bukkit.attribute.AttributeInstance;
import org.bukkit.entity.Display;
import org.bukkit.entity.Entity;
import org.bukkit.entity.ItemDisplay;
import org.bukkit.entity.Mannequin;
import org.bukkit.inventory.ItemStack;
import org.bukkit.inventory.MainHand;
import org.bukkit.util.Transformation;
import org.joml.Quaternionf;
import org.joml.Vector3f;

import java.util.ArrayList;
import java.util.List;

/**
 * ARCHITECTURE.md 3.5: a Mannequin (real player model + skin, SCALE attribute) for body and head, plus two
 * ItemDisplay hands. Where it stands comes from {@link GiantPlacement} (ground by default, or exact); this class
 * smooths the motion and owns the entities.
 */
public final class GiantAvatar {
    public static final String TAG = "tabletop_avatar";
    public static final double EYE_HEIGHT = GiantPlacement.EYE_HEIGHT;
    /** Upper bound of the SCALE attribute. */
    static final double SCALE_ATTRIBUTE_MAX = 16.0;
    /** Jumps longer than this (blocks) aren't smoothed: region change, mode switch, first pose. */
    static final double SNAP_BLOCKS = 16.0;
    /** Max angle between head and body yaw, as for vanilla players. */
    static final float BODY_YAW_LAG = 50f;

    // Config (main thread).
    public boolean enabled;
    public String skinPlayer = "";
    public String model = "wide";
    public double blockSizeM = 0.01;
    public double maxScale = 16.0;
    public double userHeightM = 1.75;
    public boolean showHands = true;
    public String nameTag;
    public int interpolationTicks = 2;
    public GiantPlacement.Mode placement = GiantPlacement.Mode.GROUND;
    /** Per-tick smoothing factor for position and head rotation (1 = off). */
    public double smoothing = 0.5;
    public String note;
    /** Server-resolved profile (signed textures) for {@link #skinPlayer}, or null while unresolved. */
    public PlayerProfile resolvedSkin;
    public String skinNote;

    private Mannequin body;
    private ItemDisplay left, right;
    private double scale = 1;
    private double[] eye;   // applied (smoothed) eye position
    private float yaw, pitch, bodyYaw;
    private double lastGroundY = Double.NaN;
    private boolean settling;
    public long posesApplied, lastSeq = -1;

    /** Next smoothed state, computed by {@link #step} and applied by {@link #commit} once its chunks are loaded. */
    public record Step(double[] feet, double[] eye, double[] left, double[] right, float yaw, float pitch, double groundY, boolean settling) {}

    public boolean spawned() { return body != null && body.isValid(); }
    public double scale() { return scale; }
    /** True while the smoothed state hasn't caught up with the last pose; the plugin keeps stepping meanwhile. */
    public boolean settling() { return settling; }

    /** Scale clamped to max_scale and the attribute cap; sets {@link #note} when clamped. */
    public double computeScale(double attributeMax) {
        double sTrue = (userHeightM / blockSizeM) / GiantPlacement.HEIGHT;
        double cap = Math.min(maxScale, attributeMax);
        note = null;
        if (sTrue > cap) { note = String.format("scale clamped from %.1f to %.1f", sTrue, cap); return cap; }
        return Math.max(0.0625, sTrue);
    }

    public void spawn(World w, Location at) {
        remove();
        body = w.spawn(at, Mannequin.class, m -> {
            m.setPersistent(false);
            m.addScoreboardTag(TAG);
            m.setImmovable(true);
            m.setAI(false);
            m.setGravity(false);
            m.setInvulnerable(true);
            m.setSilent(true);
            m.setCollidable(false);
            m.setDescription(null);
            m.setMainHand(MainHand.RIGHT);
            ResolvableProfile p = profile();
            if (p != null) m.setProfile(p);
            if (nameTag != null && !nameTag.isBlank()) { m.customName(Component.text(nameTag)); m.setCustomNameVisible(true); }
        });
        AttributeInstance attr = body.getAttribute(Attribute.SCALE);
        scale = computeScale(SCALE_ATTRIBUTE_MAX);
        if (attr != null) attr.setBaseValue(scale);
        if (showHands) {
            left = hand(w, at);
            right = hand(w, at);
        }
        eye = null; // first pose snaps
    }

    /**
     * The skin: the server-resolved profile (static, signed textures) when we have one, else a name-only (dynamic)
     * profile. Since 1.21.9 game clients resolve dynamic profiles themselves, so Java players see the skin even
     * before (or without) a server-side lookup, as long as their own client can reach Mojang.
     */
    private ResolvableProfile profile() {
        if (skinPlayer == null || skinPlayer.isBlank()) return null;
        if (resolvedSkin != null && skinPlayer.equalsIgnoreCase(resolvedSkin.getName())) return ResolvableProfile.resolvableProfile(resolvedSkin);
        return ResolvableProfile.resolvableProfile().name(skinPlayer).build();
    }

    /** Called when a server-side skin lookup finishes: switch the spawned giant to the static profile. */
    public void refreshSkin() {
        if (!spawned()) return;
        ResolvableProfile p = profile();
        if (p != null) body.setProfile(p);
    }

    public boolean skinResolved() {
        return spawned() && body.getProfile() != null && !body.getProfile().properties().isEmpty();
    }

    private ItemDisplay hand(World w, Location at) {
        float s = (float) (scale * 0.3);
        return w.spawn(at, ItemDisplay.class, d -> {
            d.setItemStack(new ItemStack(Material.WHITE_CONCRETE));
            d.setPersistent(false);
            d.addScoreboardTag(TAG);
            d.setBillboard(Display.Billboard.FIXED);
            d.setTeleportDuration(Math.max(0, Math.min(59, interpolationTicks)));
            d.setInterpolationDuration(interpolationTicks);
            d.setViewRange(4.0f);
            d.setTransformation(new Transformation(new Vector3f(), new Quaternionf(), new Vector3f(s, s, s), new Quaternionf()));
        });
    }

    /** Re-applies the scale after a block size or max_scale change; the hands pick it up with the next pose. */
    public void rescale() {
        if (!spawned()) return;
        scale = computeScale(SCALE_ATTRIBUTE_MAX);
        AttributeInstance attr = body.getAttribute(Attribute.SCALE);
        if (attr != null) attr.setBaseValue(scale);
    }

    public void remove() {
        for (Entity e : entities()) e.remove();
        body = null; left = right = null;
        eye = null; settling = false;
    }

    public List<Entity> entities() {
        List<Entity> l = new ArrayList<>();
        if (body != null && body.isValid()) l.add(body);
        if (left != null && left.isValid()) l.add(left);
        if (right != null && right.isValid()) l.add(right);
        return l;
    }

    /**
     * Next smoothed state toward a placement target (does not touch entities). Position and head rotation ease in
     * by {@link #smoothing} per tick; a jump over {@link #SNAP_BLOCKS} snaps. Body yaw lags the head as in vanilla.
     */
    public Step step(GiantPlacement.Placement p, Pose head) {
        float ty = head.yawDeg(), tp = head.pitchDeg();
        boolean snap = eye == null || GiantPlacement.distance(eye, p.eye()) > SNAP_BLOCKS;
        double a = snap ? 1 : Math.max(0.05, Math.min(1, smoothing));
        double[] e = GiantPlacement.approach(eye, p.eye(), a);
        float y = snap ? GiantPlacement.wrap(ty) : GiantPlacement.approachAngle(yaw, ty, a);
        float pi = snap ? tp : (float) (pitch + (tp - pitch) * a);
        double feetDy = p.feet()[1] - p.eye()[1];
        double[] feet = { e[0], e[1] + feetDy, e[2] };
        double[] l = p.leftOffset() == null ? null : GiantPlacement.add(e, p.leftOffset());
        double[] r = p.rightOffset() == null ? null : GiantPlacement.add(e, p.rightOffset());
        boolean behind = GiantPlacement.distance(e, p.eye()) > 0.01 || Math.abs(GiantPlacement.wrap(ty - y)) > 0.5 || Math.abs(tp - pi) > 0.5;
        return new Step(feet, e, l, r, y, pi, p.groundY(), behind);
    }

    /** Apply a step to the entities. */
    public void commit(World w, Step s, Pose l, Pose r, long seq) {
        if (!spawned()) return;
        eye = s.eye(); yaw = s.yaw(); pitch = s.pitch(); lastGroundY = s.groundY(); settling = s.settling();
        bodyYaw = GiantPlacement.lagBodyYaw(bodyYaw, yaw, BODY_YAW_LAG);
        body.teleport(new Location(w, s.feet()[0], s.feet()[1], s.feet()[2], yaw, pitch));
        body.setRotation(yaw, pitch);
        body.setBodyYaw(bodyYaw);
        if (s.left() != null && left != null && l != null) moveHand(left, w, s.left(), l);
        if (s.right() != null && right != null && r != null) moveHand(right, w, s.right(), r);
        posesApplied++;
        lastSeq = seq;
    }

    private void moveHand(ItemDisplay d, World w, double[] p, Pose q) {
        if (!q.tracked()) return; // freeze stale hands
        float s = (float) (scale * 0.3);
        d.teleport(new Location(w, p[0], p[1], p[2]));
        d.setInterpolationDelay(0);
        d.setTransformation(new Transformation(new Vector3f(), new Quaternionf(q.qx(), q.qy(), q.qz(), q.qw()), new Vector3f(s, s, s), new Quaternionf()));
    }

    public JsonObject state(boolean debug) {
        JsonObject o = Protocol.body("avatar_state");
        o.addProperty("enabled", enabled && spawned());
        o.addProperty("entity_uuid", spawned() ? body.getUniqueId().toString() : null);
        o.addProperty("scale", Math.round(scale * 100) / 100.0);
        o.addProperty("world_height_blocks", Math.round(GiantPlacement.HEIGHT * scale * 100) / 100.0);
        o.addProperty("skin_resolved", skinResolved());
        String n = note == null ? skinNote : skinNote == null ? note : note + "; " + skinNote;
        o.addProperty("note", n);
        if (debug) {
            // Extension (unknown fields must be ignored by v1 peers): live entity positions for testing.
            JsonObject d = new JsonObject();
            d.addProperty("placement", placement.id());
            if (spawned()) { Location b = body.getLocation(); d.add("feet", Protocol.doubles(b.getX(), b.getY(), b.getZ())); d.addProperty("yaw", Math.round(b.getYaw() * 10) / 10.0); d.addProperty("pitch", Math.round(b.getPitch() * 10) / 10.0); }
            if (eye != null) d.add("head", Protocol.doubles(eye));
            if (!Double.isNaN(lastGroundY)) d.addProperty("ground_y", lastGroundY);
            if (left != null && left.isValid()) { Location x = left.getLocation(); d.add("left", Protocol.doubles(x.getX(), x.getY(), x.getZ())); }
            if (right != null && right.isValid()) { Location x = right.getLocation(); d.add("right", Protocol.doubles(x.getX(), x.getY(), x.getZ())); }
            d.addProperty("body_yaw", Math.round(bodyYaw * 10) / 10.0);
            d.addProperty("skin_player", skinPlayer);
            d.addProperty("poses_applied", posesApplied);
            d.addProperty("pose_seq", lastSeq);
            o.add("debug", d);
        }
        return o;
    }
}
