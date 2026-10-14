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
        flag(o, "tamed", e.tamed); flag(o, "angry", e.angry);
        str(o, "variant", e.variant); str(o, "color", e.color); str(o, "markings", e.markings); str(o, "profession", e.profession);
        str(o, "pose", e.pose); str(o, "name", e.name); str(o, "item", e.item); str(o, "block", e.block); str(o, "facing", e.facing);
        if (e.count > 1) o.addProperty("count", e.count);
        if (e.rotation != 0) o.addProperty("rotation", e.rotation);
        if (e.equipment != null && !e.equipment.isEmpty()) o.add("equipment", equipment(e.equipment));
        return o;
    }

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
        c.tamed = e.tamed; c.angry = e.angry;
        c.variant = e.variant; c.color = e.color; c.markings = e.markings; c.profession = e.profession; c.pose = e.pose; c.name = e.name;
        c.item = e.item; c.block = e.block; c.facing = e.facing; c.count = e.count; c.rotation = e.rotation; c.equipment = e.equipment;
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

    private static JsonArray ids(int[] v) { return Protocol.ints(v); }

    private static JsonElement equipment(Map<String, String> m) {
        JsonObject o = new JsonObject();
        if (m != null) for (var kv : m.entrySet()) o.addProperty(kv.getKey(), kv.getValue());
        return o;
    }

    private static void flag(JsonObject o, String k, boolean v) { if (v) o.addProperty(k, true); }

    private static void str(JsonObject o, String k, String v) { if (v != null) o.addProperty(k, v); }
}
