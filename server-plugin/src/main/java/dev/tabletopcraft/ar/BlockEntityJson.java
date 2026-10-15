package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.function.LongPredicate;

/**
 * block_entities (PROTOCOL.md 4.29): one entry per block entity whose look depends on more than its block state (sign
 * text, banner patterns, player head profiles, pot sherds, spawned mobs, beacon tiers, campfire/shelf/vault items,
 * conduit activity), built from plain values that BlockEntityStreamer reads from the world, and the per-chunk delta
 * against what a session already holds. Pure: unit-tested with the golden fixture.
 */
final class BlockEntityJson {
    private BlockEntityJson() {}

    /** One sign side: up to 4 lines of plain text, its dye colour (lower case) and whether the text glows. */
    record Side(List<String> lines, String color, boolean glowing) {
        boolean empty() { for (String l : lines) if (l != null && !l.isEmpty()) return false; return true; }
    }

    static JsonObject entry(int x, int y, int z, String kind) {
        JsonObject o = new JsonObject();
        o.add("pos", Protocol.ints(x, y, z));
        o.addProperty("kind", kind);
        return o;
    }

    private static JsonObject side(Side s) {
        JsonObject o = new JsonObject();
        JsonArray lines = new JsonArray();
        for (int i = 0; i < 4; i++) lines.add(s != null && i < s.lines().size() && s.lines().get(i) != null ? s.lines().get(i) : "");
        o.add("lines", lines);
        o.addProperty("color", s != null && s.color() != null ? s.color() : "black");
        o.addProperty("glowing", s != null && s.glowing());
        return o;
    }

    /** A sign or hanging sign; null when both sides are empty (nothing to draw). */
    static JsonObject sign(int x, int y, int z, Side front, Side back) {
        if ((front == null || front.empty()) && (back == null || back.empty())) return null;
        JsonObject o = entry(x, y, z, "sign");
        o.add("front", side(front));
        o.add("back", side(back));
        return o;
    }

    /** A banner's pattern layers [pattern key, dye colour], in order; null without patterns (the base colour is the block id's). */
    static JsonObject banner(int x, int y, int z, List<String[]> patterns) {
        if (patterns == null || patterns.isEmpty()) return null;
        JsonObject o = entry(x, y, z, "banner");
        JsonArray a = new JsonArray();
        for (String[] p : patterns) {
            JsonObject l = new JsonObject();
            l.addProperty("pattern", p[0]);
            l.addProperty("color", p[1]);
            a.add(l);
        }
        o.add("patterns", a);
        return o;
    }

    /** A player head's profile: skin URL (textures.minecraft.net), name and UUID, any of them null; null when all are. */
    static JsonObject skull(int x, int y, int z, String skin, String name, String uuid) {
        if (skin == null && name == null && uuid == null) return null;
        JsonObject o = entry(x, y, z, "skull");
        if (skin != null) o.addProperty("skin", skin);
        if (name != null) o.addProperty("name", name);
        if (uuid != null) o.addProperty("uuid", uuid);
        return o;
    }

    /** A decorated pot's sides back, left, right, front (item keys); null when every side is a plain brick. */
    static JsonObject pot(int x, int y, int z, List<String> sherds) {
        boolean any = false;
        for (String s : sherds) if (s != null && !s.equals("minecraft:brick")) any = true;
        if (!any) return null;
        JsonObject o = entry(x, y, z, "decorated_pot");
        JsonArray a = new JsonArray();
        for (int i = 0; i < 4; i++) a.add(i < sherds.size() && sherds.get(i) != null ? sherds.get(i) : "minecraft:brick");
        o.add("sherds", a);
        return o;
    }

    /** A spawner ("spawner") or trial spawner ("trial_spawner") with the mob it shows; size [width, height] may be null. */
    static JsonObject spawner(String kind, int x, int y, int z, String entity, double[] size, boolean ominous) {
        if (entity == null) return null;
        JsonObject o = entry(x, y, z, kind);
        o.addProperty("entity", entity);
        if (size != null) o.add("size", Protocol.doubles(size[0], size[1]));
        if (kind.equals("trial_spawner")) o.addProperty("ominous", ominous);
        return o;
    }

    static JsonObject beacon(int x, int y, int z, int tier) {
        JsonObject o = entry(x, y, z, "beacon");
        o.addProperty("tier", tier);
        return o;
    }

    /** Campfire (4 slots) or shelf (3 slots) items, item keys or null; null when all slots are empty. */
    static JsonObject items(String kind, int x, int y, int z, List<String> items) {
        boolean any = false;
        for (String s : items) if (s != null) any = true;
        if (!any) return null;
        JsonObject o = entry(x, y, z, kind);
        JsonArray a = new JsonArray();
        for (String s : items) { if (s == null) a.add(com.google.gson.JsonNull.INSTANCE); else a.add(s); }
        o.add("items", a);
        return o;
    }

    static JsonObject vault(int x, int y, int z, String item) {
        if (item == null) return null;
        JsonObject o = entry(x, y, z, "vault");
        o.addProperty("item", item);
        return o;
    }

    /** Only active conduits are sent (an inactive one is the closed shell its block state already gives). */
    static JsonObject conduit(int x, int y, int z, boolean active) {
        if (!active) return null;
        JsonObject o = entry(x, y, z, "conduit");
        o.addProperty("active", true);
        return o;
    }

    static JsonObject message(List<JsonObject> entities, List<int[]> removed) {
        JsonObject m = Protocol.body("block_entities");
        JsonArray a = new JsonArray();
        entities.forEach(a::add);
        m.add("entities", a);
        JsonArray r = new JsonArray();
        for (int[] p : removed) r.add(Protocol.ints(p));
        m.add("removed", r);
        return m;
    }

    // ---------- positions ----------

    /** World block position as a long (BlockPos layout: x 26 bits, z 26 bits, y 12 bits). */
    static long key(int x, int y, int z) { return ((long) (x & 0x3FFFFFF) << 38) | ((long) (z & 0x3FFFFFF) << 12) | (y & 0xFFF); }
    static int x(long k) { return (int) (k >> 38); }
    static int y(long k) { return (int) (k << 52 >> 52); }
    static int z(long k) { return (int) (k << 26 >> 38); }

    static long chunkKey(int cx, int cz) { return ((long) cx << 32) ^ (cz & 0xffffffffL); }

    // ---------- deltas ----------

    /**
     * One chunk's delta for a session: now (position -> entry JSON) against held (what the session has for that chunk),
     * keeping only positions inScope (the session's region). Changed and new entries go to add, gone ones to removed;
     * held is updated to match.
     */
    static void diff(Map<Long, String> now, Map<Long, String> held, LongPredicate inScope, List<String> add, List<int[]> removed) {
        for (var e : now.entrySet()) {
            if (!inScope.test(e.getKey())) continue;
            String old = held.get(e.getKey());
            if (!e.getValue().equals(old)) { add.add(e.getValue()); held.put(e.getKey(), e.getValue()); }
        }
        for (Iterator<Map.Entry<Long, String>> it = held.entrySet().iterator(); it.hasNext(); ) {
            var e = it.next();
            if (!now.containsKey(e.getKey()) || !inScope.test(e.getKey())) {
                if (inScope.test(e.getKey())) removed.add(new int[] { x(e.getKey()), y(e.getKey()), z(e.getKey()) });
                it.remove();   // out of scope: the client drops it with the region, so just forget it
            }
        }
    }

    /** Forgets held entries outside a new region (the client prunes them when it applies the region). */
    static void prune(Map<Long, Map<Long, String>> held, LongPredicate inScope, java.util.function.LongPredicate chunkInScope) {
        for (Iterator<Map.Entry<Long, Map<Long, String>>> it = held.entrySet().iterator(); it.hasNext(); ) {
            var c = it.next();
            if (!chunkInScope.test(c.getKey())) { it.remove(); continue; }
            c.getValue().keySet().removeIf(k -> !inScope.test(k));
        }
    }

    /** The JSON strings of a chunk's entries (Protocol.GSON, which compares stably). */
    static Map<Long, String> strings(Map<Long, JsonObject> entries) {
        Map<Long, String> m = new HashMap<>();
        for (var e : entries.entrySet()) m.put(e.getKey(), Protocol.GSON.toJson(e.getValue()));
        return m;
    }

    /** Message entries from their JSON strings (parsed back so the message is one JsonObject). */
    static List<JsonObject> parse(List<String> json) {
        List<JsonObject> out = new ArrayList<>(json.size());
        for (String s : json) out.add(com.google.gson.JsonParser.parseString(s).getAsJsonObject());
        return out;
    }
}
