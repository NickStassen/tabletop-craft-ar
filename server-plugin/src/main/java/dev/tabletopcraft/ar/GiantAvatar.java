package dev.tabletopcraft.ar;

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
 * ItemDisplay hands. Positions are mapped exactly from the tabletop frame; the body scale is clamped.
 */
public final class GiantAvatar {
    public static final String TAG = "tabletop_avatar";
    public static final double EYE_HEIGHT = 1.62;

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
    public String note;

    private Mannequin body;
    private ItemDisplay left, right;
    private double scale = 1;
    private double[] lastHead, lastLeft, lastRight;
    private float lastYaw, lastPitch, bodyYaw;
    public long posesApplied, lastSeq = -1;

    public boolean spawned() { return body != null && body.isValid(); }
    public double scale() { return scale; }

    /** Scale clamped to max_scale and the attribute cap; sets {@link #note} when clamped. */
    public double computeScale(double attributeMax) {
        double sTrue = (userHeightM / blockSizeM) / 1.8;
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
            if (skinPlayer != null && !skinPlayer.isBlank()) m.setProfile(ResolvableProfile.resolvableProfile().name(skinPlayer).build());
            if (nameTag != null && !nameTag.isBlank()) { m.customName(Component.text(nameTag)); m.setCustomNameVisible(true); }
        });
        AttributeInstance attr = body.getAttribute(Attribute.SCALE);
        double attrMax = 16.0;
        scale = computeScale(attrMax);
        if (attr != null) attr.setBaseValue(scale);
        if (showHands) {
            left = hand(w, at);
            right = hand(w, at);
        }
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

    public void remove() {
        for (Entity e : entities()) e.remove();
        body = null; left = right = null;
    }

    public List<Entity> entities() {
        List<Entity> l = new ArrayList<>();
        if (body != null && body.isValid()) l.add(body);
        if (left != null && left.isValid()) l.add(left);
        if (right != null && right.isValid()) l.add(right);
        return l;
    }

    /** Apply one pose set; positions already mapped to world coordinates. */
    public void apply(World w, double[] head, Pose h, double[] lp, Pose l, double[] rp, Pose r, long seq) {
        if (!spawned()) return;
        float yaw = h.yawDeg(), pitch = h.pitchDeg();
        // Body yaw follows head yaw with a lag (max 50 degrees apart).
        float diff = wrap(yaw - bodyYaw);
        if (Math.abs(diff) > 50) bodyYaw = wrap(bodyYaw + diff - Math.signum(diff) * 50);
        double feetY = Math.max(w.getMinHeight(), head[1] - EYE_HEIGHT * scale);
        Location loc = new Location(w, head[0], feetY, head[2], yaw, pitch);
        body.teleport(loc);
        body.setRotation(yaw, pitch);
        body.setBodyYaw(bodyYaw);
        lastHead = head; lastYaw = yaw; lastPitch = pitch;
        if (lp != null && left != null) { moveHand(left, w, lp, l); lastLeft = lp; }
        if (rp != null && right != null) { moveHand(right, w, rp, r); lastRight = rp; }
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

    private static float wrap(float a) { a %= 360; if (a >= 180) a -= 360; if (a < -180) a += 360; return a; }

    public JsonObject state(boolean debug) {
        JsonObject o = Protocol.body("avatar_state");
        o.addProperty("enabled", enabled && spawned());
        o.addProperty("entity_uuid", spawned() ? body.getUniqueId().toString() : null);
        o.addProperty("scale", Math.round(scale * 100) / 100.0);
        o.addProperty("world_height_blocks", Math.round(1.8 * scale * 100) / 100.0);
        o.addProperty("skin_resolved", spawned() && body.getProfile() != null && !body.getProfile().properties().isEmpty());
        o.addProperty("note", note);
        if (debug) {
            // Extension (unknown fields must be ignored by v1 peers): live entity positions for testing.
            JsonObject d = new JsonObject();
            if (spawned()) { Location b = body.getLocation(); d.add("feet", Protocol.doubles(b.getX(), b.getY(), b.getZ())); d.addProperty("yaw", Math.round(b.getYaw() * 10) / 10.0); d.addProperty("pitch", Math.round(b.getPitch() * 10) / 10.0); }
            if (lastHead != null) d.add("head", Protocol.doubles(lastHead));
            if (left != null && left.isValid()) { Location x = left.getLocation(); d.add("left", Protocol.doubles(x.getX(), x.getY(), x.getZ())); }
            if (right != null && right.isValid()) { Location x = right.getLocation(); d.add("right", Protocol.doubles(x.getX(), x.getY(), x.getZ())); }
            d.addProperty("poses_applied", posesApplied);
            d.addProperty("pose_seq", lastSeq);
            o.add("debug", d);
        }
        return o;
    }
}
