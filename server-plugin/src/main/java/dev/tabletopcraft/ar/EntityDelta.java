package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import com.google.gson.JsonPrimitive;

import java.util.Arrays;
import java.util.HashSet;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;

/**
 * Builds {@code entities} messages (PROTOCOL.md 4.21) as deltas against what one session already holds: an entity
 * the client doesn't know yet gets a full entry, a known one only the fields that changed (positions by at least
 * {@link #POS_EPS} blocks, angles by at least {@link #ANGLE_EPS} degrees), and ids that left are listed in
 * {@code removed}. A full message ({@code full: true}) replaces the client's whole set. Pure (no Bukkit).
 */
public final class EntityDelta {
    public static final double POS_EPS = 0.01;
    public static final float ANGLE_EPS = 1.0f;

    private EntityDelta() {}

    /**
     * The message for one session, or null when nothing changed (a full message is always built). {@code sent} is the
     * session's view of the client: it's updated to what the client holds after this message.
     */
    public static JsonObject message(Map<Integer, EntityState> sent, List<EntityState> now, boolean full) {
        JsonArray list = new JsonArray();
        Set<Integer> seen = new HashSet<>();
        if (full) sent.clear();
        for (EntityState e : now) {
            if (!seen.add(e.id)) continue;
            EntityState was = sent.get(e.id);
            if (was == null) {
                list.add(fullEntry(e));
                sent.put(e.id, copy(e));
            } else {
                JsonObject d = deltaEntry(e, was);
                if (d != null) list.add(d);
            }
        }
        JsonArray removed = new JsonArray();
        for (Iterator<Integer> it = sent.keySet().iterator(); it.hasNext(); ) {
            int id = it.next();
            if (!seen.contains(id)) { removed.add(id); it.remove(); }
        }
        if (!full && list.isEmpty() && removed.isEmpty()) return null;
        JsonObject o = Protocol.body("entities");
        o.addProperty("full", full);
        o.add("entities", list);
        if (!removed.isEmpty()) o.add("removed", removed);
        return o;
    }

    /** Every field that applies; false/null/empty ones are left out. */
    public static JsonObject fullEntry(EntityState e) {
        JsonObject o = new JsonObject();
        o.addProperty("id", e.id);
        o.addProperty("type", e.type);
        if (e.uuid != null) o.addProperty("uuid", e.uuid);
        o.add("pos", pos(e));
        o.addProperty("yaw", angle(e.yaw));
        o.addProperty("pitch", angle(e.pitch));
        if (e.bodyYaw != null) o.addProperty("body_yaw", angle(e.bodyYaw));
        if (e.onGround) o.addProperty("on_ground", true);
        if (e.vehicle != null) o.addProperty("vehicle", e.vehicle);
        if (e.passengers.length > 0) o.add("passengers", ids(e.passengers));
        o.add("size", size(e));
        flag(o, "baby", e.baby); flag(o, "small", e.small); flag(o, "sheared", e.sheared); flag(o, "invisible", e.invisible);
        flag(o, "tamed", e.tamed); flag(o, "angry", e.angry); flag(o, "arms", e.arms); flag(o, "base_plate", e.basePlate);
        if (e.standPose != null) o.add("stand_pose", standPose(e.standPose));
        str(o, "variant", e.variant); str(o, "color", e.color); str(o, "markings", e.markings); str(o, "profession", e.profession);
        str(o, "pose", e.pose); str(o, "name", e.name); str(o, "item", e.item); str(o, "block", e.block); str(o, "facing", e.facing);
        if (e.count > 1) o.addProperty("count", e.count);
        if (e.rotation != 0) o.addProperty("rotation", e.rotation);
        if (e.equipment != null && !e.equipment.isEmpty()) o.add("equipment", equipment(e.equipment));
        living(o, e);
        return o;
    }

    /** [living] The leash, gear and look fields of a full entry (left out when not set). */
    private static void living(JsonObject o, EntityState e) {
        if (e.leash != null) o.addProperty("leash", e.leash);
        if (e.eye > 0) o.addProperty("eye", round2(e.eye));
        flag(o, "chested", e.chested); flag(o, "name_visible", e.nameVisible);
        if (e.xp > 0) o.addProperty("xp", e.xp);
        if (e.owner != null) o.addProperty("owner", e.owner);
        str(o, "carried", e.carried);
        if (e.peek > 0) o.addProperty("peek", round2(e.peek));
        if (e.fuse > 0) o.addProperty("fuse", e.fuse);                 // [motion]
        if (e.swell > 0) o.addProperty("swell", round2(e.swell));
        str(o, "attach", e.attach);
        if (e.display != null) o.add("display", display(e.display));
    }

    private static double round2(double v) { return Math.round(v * 100) / 100.0; }

    /** A display entity's state (PROTOCOL.md "Display entities"): always the whole object. */
    static JsonObject display(EntityState.DisplayState d) {
        JsonObject o = new JsonObject();
        o.add("translation", floats(d.translation)); o.add("left_rotation", floats(d.leftRotation));
        o.add("scale", floats(d.scale)); o.add("right_rotation", floats(d.rightRotation));
        o.addProperty("billboard", d.billboard);
        if (d.brightness != null) o.add("brightness", Protocol.ints(d.brightness));
        if (d.itemTransform != null) o.addProperty("item_transform", d.itemTransform);
        if (d.text != null) {
            o.addProperty("text", d.text);
            o.addProperty("line_width", d.lineWidth);
            o.addProperty("background", d.background);
            o.addProperty("text_opacity", d.textOpacity);
            if (d.shadow) o.addProperty("shadow", true);
            if (d.seeThrough) o.addProperty("see_through", true);
            if (d.defaultBackground) o.addProperty("default_background", true);
            o.addProperty("align", d.align);
        }
        return o;
    }

    private static JsonArray floats(float[] v) {
        JsonArray a = new JsonArray();
        for (float f : v) a.add(Double.parseDouble(Float.toString(f)));   // the float's shortest decimal, not its double expansion
        return a;
    }

    /** [living] Changed leash, gear and look fields of a known entity (cleared ones as null/false/0); updates was. */
    private static void livingDelta(JsonObject o, EntityState e, EntityState was) {
        if (!Objects.equals(e.leash, was.leash)) { o.add("leash", e.leash == null ? JsonNull.INSTANCE : new JsonPrimitive(e.leash)); was.leash = e.leash; }
        if (Math.abs(e.eye - was.eye) >= POS_EPS) { o.addProperty("eye", round2(e.eye)); was.eye = e.eye; }
        if (e.chested != was.chested) { o.addProperty("chested", e.chested); was.chested = e.chested; }
        if (e.nameVisible != was.nameVisible) { o.addProperty("name_visible", e.nameVisible); was.nameVisible = e.nameVisible; }
        if (e.xp != was.xp) { o.addProperty("xp", e.xp); was.xp = e.xp; }
        if (!Objects.equals(e.owner, was.owner)) { o.add("owner", e.owner == null ? JsonNull.INSTANCE : new JsonPrimitive(e.owner)); was.owner = e.owner; }
        if (!Objects.equals(e.carried, was.carried)) { o.addProperty("carried", e.carried); was.carried = e.carried; }
        if (Math.abs(e.peek - was.peek) >= PEEK_EPS || (e.peek == 0) != (was.peek == 0)) { o.addProperty("peek", round2(e.peek)); was.peek = e.peek; }
        // [motion] the client counts a fuse down itself: resend when it drifts; a swell in steps like peek
        if (Math.abs(e.fuse - was.fuse) >= FUSE_EPS || (e.fuse == 0) != (was.fuse == 0)) { o.addProperty("fuse", e.fuse); was.fuse = e.fuse; }
        if (Math.abs(e.swell - was.swell) >= PEEK_EPS || (e.swell == 0) != (was.swell == 0)) { o.addProperty("swell", round2(e.swell)); was.swell = e.swell; }
        if (!Objects.equals(e.attach, was.attach)) { o.addProperty("attach", e.attach); was.attach = e.attach; }
        if (!Objects.equals(e.display, was.display)) { o.add("display", e.display == null ? JsonNull.INSTANCE : display(e.display)); was.display = e.display; }
    }

    /** A shulker's lid moves in small steps: resend its peek when it changed by this much (or opened/closed fully). */
    public static final double PEEK_EPS = 0.05;
    /** [motion] Fuse ticks the client may count down on its own before the server corrects it. */
    public static final int FUSE_EPS = 10;

    /** Changed fields of a known entity (cleared ones as false/null/[]), or null; updates {@code was} to match. */
    static JsonObject deltaEntry(EntityState e, EntityState was) {
        JsonObject o = new JsonObject();
        if (Math.abs(e.x - was.x) >= POS_EPS || Math.abs(e.y - was.y) >= POS_EPS || Math.abs(e.z - was.z) >= POS_EPS) {
            o.add("pos", pos(e)); was.x = e.x; was.y = e.y; was.z = e.z;
        }
        if (turned(e.yaw, was.yaw)) { o.addProperty("yaw", angle(e.yaw)); was.yaw = e.yaw; }
        if (Math.abs(e.pitch - was.pitch) >= ANGLE_EPS) { o.addProperty("pitch", angle(e.pitch)); was.pitch = e.pitch; }
        if (e.bodyYaw != null && (was.bodyYaw == null || turned(e.bodyYaw, was.bodyYaw))) { o.addProperty("body_yaw", angle(e.bodyYaw)); was.bodyYaw = e.bodyYaw; }
        if (e.onGround != was.onGround) { o.addProperty("on_ground", e.onGround); was.onGround = e.onGround; }
        if (!Objects.equals(e.vehicle, was.vehicle)) { o.add("vehicle", e.vehicle == null ? JsonNull.INSTANCE : new JsonPrimitive(e.vehicle)); was.vehicle = e.vehicle; }
        if (!Arrays.equals(e.passengers, was.passengers)) { o.add("passengers", ids(e.passengers)); was.passengers = e.passengers; }
        if (Math.abs(e.width - was.width) >= POS_EPS || Math.abs(e.height - was.height) >= POS_EPS) { o.add("size", size(e)); was.width = e.width; was.height = e.height; }
        if (e.baby != was.baby) { o.addProperty("baby", e.baby); was.baby = e.baby; }
        if (e.small != was.small) { o.addProperty("small", e.small); was.small = e.small; }
        if (e.sheared != was.sheared) { o.addProperty("sheared", e.sheared); was.sheared = e.sheared; }
        if (e.invisible != was.invisible) { o.addProperty("invisible", e.invisible); was.invisible = e.invisible; }
        if (e.tamed != was.tamed) { o.addProperty("tamed", e.tamed); was.tamed = e.tamed; }
        if (e.angry != was.angry) { o.addProperty("angry", e.angry); was.angry = e.angry; }
        if (e.arms != was.arms) { o.addProperty("arms", e.arms); was.arms = e.arms; }
        if (e.basePlate != was.basePlate) { o.addProperty("base_plate", e.basePlate); was.basePlate = e.basePlate; }
        if (!Arrays.equals(e.standPose, was.standPose)) {
            o.add("stand_pose", e.standPose == null ? JsonNull.INSTANCE : standPose(e.standPose)); was.standPose = e.standPose;
        }
        if (!Objects.equals(e.variant, was.variant)) { o.addProperty("variant", e.variant); was.variant = e.variant; }
        if (!Objects.equals(e.color, was.color)) { o.addProperty("color", e.color); was.color = e.color; }
        if (!Objects.equals(e.markings, was.markings)) { o.addProperty("markings", e.markings); was.markings = e.markings; }
        if (!Objects.equals(e.profession, was.profession)) { o.addProperty("profession", e.profession); was.profession = e.profession; }
        if (!Objects.equals(e.pose, was.pose)) { o.addProperty("pose", e.pose); was.pose = e.pose; }
        if (!Objects.equals(e.name, was.name)) { o.addProperty("name", e.name); was.name = e.name; }
        if (!Objects.equals(e.item, was.item)) { o.addProperty("item", e.item); was.item = e.item; }
        if (!Objects.equals(e.block, was.block)) { o.addProperty("block", e.block); was.block = e.block; }
        if (!Objects.equals(e.facing, was.facing)) { o.addProperty("facing", e.facing); was.facing = e.facing; }
        if (e.count != was.count) { o.addProperty("count", e.count); was.count = e.count; }
        if (e.rotation != was.rotation) { o.addProperty("rotation", e.rotation); was.rotation = e.rotation; }
        if (!Objects.equals(empty(e.equipment), empty(was.equipment))) { o.add("equipment", equipment(e.equipment)); was.equipment = e.equipment; }
        livingDelta(o, e, was);
        if (o.size() == 0) return null;
        JsonObject withId = new JsonObject();
        withId.addProperty("id", e.id);
        for (var kv : o.entrySet()) withId.add(kv.getKey(), kv.getValue());
        return withId;
    }

    static EntityState copy(EntityState e) {
        EntityState c = new EntityState(e.id, e.type, e.x, e.y, e.z, e.yaw, e.pitch);
        c.uuid = e.uuid; c.bodyYaw = e.bodyYaw; c.onGround = e.onGround; c.vehicle = e.vehicle; c.passengers = e.passengers;
        c.width = e.width; c.height = e.height; c.baby = e.baby; c.sheared = e.sheared; c.invisible = e.invisible; c.small = e.small;
        c.tamed = e.tamed; c.angry = e.angry; c.arms = e.arms; c.basePlate = e.basePlate; c.standPose = e.standPose;
        c.variant = e.variant; c.color = e.color; c.markings = e.markings; c.profession = e.profession; c.pose = e.pose; c.name = e.name;
        c.item = e.item; c.block = e.block; c.facing = e.facing; c.count = e.count; c.rotation = e.rotation; c.equipment = e.equipment;
        c.leash = e.leash; c.eye = e.eye; c.chested = e.chested; c.nameVisible = e.nameVisible; c.xp = e.xp; c.owner = e.owner;
        c.carried = e.carried; c.peek = e.peek; c.attach = e.attach; c.display = e.display;
        c.fuse = e.fuse; c.swell = e.swell;
        return c;
    }

    private static boolean turned(float a, float b) {
        float d = (a - b) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return Math.abs(d) >= ANGLE_EPS;
    }

    private static Map<String, String> empty(Map<String, String> m) { return m == null || m.isEmpty() ? Map.of() : m; }

    private static JsonArray pos(EntityState e) {
        JsonArray a = new JsonArray();
        a.add(Math.round(e.x * 100) / 100.0); a.add(Math.round(e.y * 100) / 100.0); a.add(Math.round(e.z * 100) / 100.0);
        return a;
    }

    private static JsonArray size(EntityState e) {
        JsonArray a = new JsonArray();
        a.add(Math.round(e.width * 100) / 100.0); a.add(Math.round(e.height * 100) / 100.0);
        return a;
    }

    private static double angle(float a) { return Math.round(a * 10) / 10.0; }

    /** An armour stand's pose: {"head": [x, y, z], "body": ..., "right_leg": ...} in degrees (EntityState.standPose). */
    static JsonObject standPose(float[] p) {
        JsonObject o = new JsonObject();
        for (int i = 0; i < EntityState.STAND_PARTS.length; i++) {
            JsonArray a = new JsonArray();
            for (int k = 0; k < 3; k++) a.add(angle(p[i * 3 + k]));
            o.add(EntityState.STAND_PARTS[i], a);
        }
        return o;
    }

    private static JsonArray ids(int[] v) { return Protocol.ints(v); }

    private static JsonElement equipment(Map<String, String> m) {
        JsonObject o = new JsonObject();
        if (m != null) for (var kv : m.entrySet()) o.addProperty(kv.getKey(), kv.getValue());
        return o;
    }

    private static void flag(JsonObject o, String k, boolean v) { if (v) o.addProperty(k, true); }

    private static void str(JsonObject o, String k, String v) { if (v != null) o.addProperty(k, v); }
}
