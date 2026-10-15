package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.util.List;
import java.util.Locale;
import java.util.UUID;

/**
 * [hands] Parsers and builders for using blocks, items and mobs from the headset (PROTOCOL.md "Using blocks and
 * items" and "Acting on mobs", cap interact_v1): interact, entity_op (C->S); item_registry, entity_info and the
 * welcome "interact" object (S->C). Pure and unit-tested against tools/fixtures (InteractFixtureTest); envelope
 * fields are added by {@link Session#send}.
 */
public final class InteractMessages {
    public static final String CAP = "interact_v1";
    /** Longest item id and entity name accepted. */
    static final int MAX_ITEM = 128, MAX_NAME = 64;
    /** entity_op op values. */
    public static final List<String> OPS = List.of("attack", "kill", "remove", "use", "name", "move", "hold", "drop", "freeze", "unfreeze", "glow", "info");

    private InteractMessages() {}

    /**
     * interact: the clicked block, its face, an optional hit point inside it (0..1), the item used (null = empty hand),
     * sneak (informational), the ray direction (world axes, optional).
     */
    public record Interact(int x, int y, int z, String face, double[] hit, String item, boolean sneak, double[] dir) {
        public JsonArray posJson() { return Protocol.ints(x, y, z); }
        /** An error about this interact: the standard error plus op "interact" and pos. */
        public JsonObject error(String code, String message, Long refSeq) {
            JsonObject e = Protocol.error(code, message, refSeq, false);
            e.addProperty("op", "interact");
            e.add("pos", posJson());
            return e;
        }
    }

    /** Throws IllegalArgumentException (bad_message) when malformed. */
    public static Interact interact(JsonObject m) {
        int[] p = intPos(m, "pos", "interact");
        String face = str(m, "face");
        if (face == null || !BlockEdit.FACES.contains(face)) throw new IllegalArgumentException("interact face must be one of " + BlockEdit.FACES);
        double[] hit = vec(m, "hit", "interact hit");
        if (hit != null) for (double v : hit) if (v < -0.01 || v > 1.01) throw new IllegalArgumentException("interact hit must be inside the block (0..1)");
        String item = m.has("item") && !m.get("item").isJsonNull() ? str(m, "item") : null;
        if (item != null && (item.isBlank() || item.length() > MAX_ITEM || !item.matches("[a-z0-9_.\\-]+:[a-z0-9_./\\-]+")))
            throw new IllegalArgumentException("interact item must be a namespaced item id");
        boolean sneak = m.has("sneak") && m.get("sneak").isJsonPrimitive() && m.get("sneak").getAsBoolean();
        return new Interact(p[0], p[1], p[2], face, hit, item, sneak, vec(m, "dir", "interact dir"));
    }

    /** entity_op: what to do to which entity (by uuid), with the op's own fields. */
    public record EntityOp(UUID uuid, String op, double[] dir, double[] to, Float yaw, String item, String text) {
        /** An error about this op: the standard error plus op "entity_op", the entity op and its uuid. */
        public JsonObject error(String code, String message, Long refSeq) {
            JsonObject e = Protocol.error(code, message, refSeq, false);
            e.addProperty("op", "entity_op");
            e.addProperty("entity_op", op);
            e.addProperty("uuid", uuid.toString());
            return e;
        }
    }

    public static EntityOp entityOp(JsonObject m) {
        String u = str(m, "uuid");
        UUID uuid;
        try { uuid = UUID.fromString(u == null ? "" : u); } catch (IllegalArgumentException e) { throw new IllegalArgumentException("entity_op needs a uuid"); }
        String op = str(m, "op");
        if (op == null || !OPS.contains(op)) throw new IllegalArgumentException("entity_op op must be one of " + OPS);
        double[] to = vec(m, "to", "entity_op to");
        if (to != null) for (double v : to) if (Math.abs(v) > 30_000_000) throw new IllegalArgumentException("entity_op to is out of the world");
        if ((op.equals("move") || op.equals("hold")) && to == null) throw new IllegalArgumentException("entity_op " + op + " needs to [x, y, z]");
        Float yaw = m.has("yaw") && m.get("yaw").isJsonPrimitive() && m.get("yaw").getAsJsonPrimitive().isNumber() ? m.get("yaw").getAsFloat() : null;
        String item = m.has("item") && !m.get("item").isJsonNull() ? str(m, "item") : null;
        if (item != null && (item.length() > MAX_ITEM || !item.matches("[a-z0-9_.\\-]+:[a-z0-9_./\\-]+"))) throw new IllegalArgumentException("entity_op item must be a namespaced item id");
        String text = m.has("text") && !m.get("text").isJsonNull() ? str(m, "text") : null;
        if (op.equals("name") && text == null) throw new IllegalArgumentException("entity_op name needs text (empty removes the name)");
        return new EntityOp(uuid, op, vec(m, "dir", "entity_op dir"), to, yaw, item, text);
    }

    /** A mob's name as typed on the headset: formatting codes (section sign + code) and control characters out, any
     *  whitespace a single space, at most 64 characters. */
    public static String cleanName(String raw) {
        if (raw == null) return "";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < raw.length(); i++) {
            char c = raw.charAt(i);
            if (c == '§') { i++; continue; }
            if (Character.isWhitespace(c) || Character.isSpaceChar(c)) b.append(' ');
            else if (!Character.isISOControl(c)) b.append(c);
        }
        String s = b.toString().trim().replaceAll(" {2,}", " ");
        return s.length() > MAX_NAME ? s.substring(0, MAX_NAME).trim() : s;
    }

    // ---------- server -> client ----------

    /** welcome "interact": the switches the headset needs to colour its outline and fill its menus. */
    public static JsonObject welcomeInteract(InteractRules.Flags f, boolean entities, boolean harmPlayers, double attackDamage, int spawnsPerMinute) {
        JsonObject o = new JsonObject();
        o.addProperty("iron_doors", f.ironDoors());
        o.addProperty("fire", f.fire());
        o.addProperty("tnt", f.tnt());
        o.addProperty("fluids", f.fluids());
        o.addProperty("lightning", f.lightning());
        o.addProperty("entities", entities);
        o.addProperty("harm_players", harmPlayers);
        o.addProperty("attack_damage", attackDamage);
        o.addProperty("max_spawns_per_minute", spawnsPerMinute);
        return o;
    }

    /** item_registry: the usable items, then the spawn eggs (both in menu order). */
    public static JsonObject itemRegistry(List<String> use, List<String> eggs) {
        JsonObject o = Protocol.body("item_registry");
        JsonArray cats = new JsonArray();
        cats.add(category("use", "Use", use));
        cats.add(category("spawn_eggs", "Spawn Eggs", eggs));
        o.addProperty("count", use.size() + eggs.size());
        o.add("categories", cats);
        return o;
    }

    private static JsonObject category(String id, String label, List<String> items) {
        JsonObject c = new JsonObject();
        c.addProperty("id", id);
        c.addProperty("label", label);
        JsonArray a = new JsonArray();
        items.forEach(a::add);
        c.add("items", a);
        return c;
    }

    /** What entity_info says about one entity. Null fields are left out. */
    public record Info(String uuid, int id, String entityType, String name, double[] pos, double[] health, String owner, Integer age,
                       boolean baby, Boolean ai, Boolean frozen, boolean glowing, boolean held, boolean leashed, boolean player, String vehicle,
                       int passengers) {}

    public static JsonObject entityInfo(Long refSeq, Info i) {
        JsonObject o = Protocol.body("entity_info");
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        o.addProperty("uuid", i.uuid());
        o.addProperty("id", i.id());
        o.addProperty("entity_type", i.entityType());
        if (i.name() != null) o.addProperty("name", i.name());
        o.add("pos", Protocol.doubles(i.pos()));
        if (i.health() != null) o.add("health", round(i.health()));
        if (i.owner() != null) o.addProperty("owner", i.owner());
        if (i.age() != null) o.addProperty("age", i.age());
        if (i.baby()) o.addProperty("baby", true);
        if (i.ai() != null) o.addProperty("ai", i.ai());
        if (i.frozen() != null) o.addProperty("frozen", i.frozen());
        o.addProperty("glowing", i.glowing());
        if (i.held()) o.addProperty("held", true);
        if (i.leashed()) o.addProperty("leashed", true);
        if (i.player()) o.addProperty("player", true);
        if (i.vehicle() != null) o.addProperty("vehicle", i.vehicle());
        if (i.passengers() > 0) o.addProperty("passengers", i.passengers());
        return o;
    }

    private static JsonArray round(double[] v) {
        JsonArray a = new JsonArray();
        for (double d : v) a.add(Math.round(d * 10) / 10.0);
        return a;
    }

    // ---------- parsing helpers ----------

    private static String str(JsonObject m, String k) {
        return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null;
    }

    private static int[] intPos(JsonObject m, String k, String what) {
        JsonArray p = m.has(k) && m.get(k).isJsonArray() ? m.getAsJsonArray(k) : null;
        if (p == null || p.size() != 3) throw new IllegalArgumentException(what + " needs " + k + " [x, y, z]");
        int[] v = new int[3];
        for (int i = 0; i < 3; i++) {
            JsonElement e = p.get(i);
            if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException(what + " " + k + " must be integers");
            double d = e.getAsDouble();
            if (d != Math.rint(d) || Math.abs(d) > 30_000_000) throw new IllegalArgumentException(what + " " + k + " must be integer block coordinates");
            v[i] = (int) d;
        }
        return v;
    }

    /** An optional [x, y, z] of finite numbers; null when absent. */
    private static double[] vec(JsonObject m, String k, String what) {
        if (!m.has(k) || m.get(k).isJsonNull()) return null;
        if (!m.get(k).isJsonArray() || m.getAsJsonArray(k).size() != 3) throw new IllegalArgumentException(what + " must be [x, y, z]");
        double[] v = new double[3];
        JsonArray a = m.getAsJsonArray(k);
        for (int i = 0; i < 3; i++) {
            JsonElement e = a.get(i);
            if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException(what + " must be numbers");
            v[i] = e.getAsDouble();
            if (!Double.isFinite(v[i])) throw new IllegalArgumentException(what + " must be finite");
        }
        return v;
    }

    static String lower(String s) { return s == null ? null : s.toLowerCase(Locale.ROOT); }
}
