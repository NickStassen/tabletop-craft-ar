package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;

import java.util.List;
import java.util.Locale;

/**
 * block_edit (PROTOCOL.md 4.20): one block broken or placed from the headset. Parsed and checked on the websocket
 * thread (pure, unit-tested); TabletopPlugin#onBlockEdit applies it on the main thread.
 *
 * @param state block-state string for "place" (null for "break")
 * @param face  clicked face (Minecraft direction name), informational; null if not sent
 */
public record BlockEdit(Op op, int x, int y, int z, String state, String face) {
    public enum Op { BREAK, PLACE; public String id() { return name().toLowerCase(Locale.ROOT); } }

    /** Face names in the protocol's face index order (0=-X ... 5=+Z), Minecraft's Direction names. */
    public static final List<String> FACES = List.of("west", "east", "down", "up", "north", "south");
    /** Longest state string accepted (real states are well under 200 characters). */
    static final int MAX_STATE = 512;

    /** Throws IllegalArgumentException (sent back as bad_message) for a malformed message. */
    public static BlockEdit parse(JsonObject m) {
        String o = str(m, "op");
        if (o == null) throw new IllegalArgumentException("block_edit needs op (break or place)");
        Op op = switch (o) {
            case "break" -> Op.BREAK;
            case "place" -> Op.PLACE;
            default -> throw new IllegalArgumentException("block_edit op must be break or place, got " + o);
        };
        JsonArray p = m.has("pos") && m.get("pos").isJsonArray() ? m.getAsJsonArray("pos") : null;
        if (p == null || p.size() != 3) throw new IllegalArgumentException("block_edit needs pos [x, y, z]");
        int[] v = new int[3];
        for (int i = 0; i < 3; i++) {
            JsonElement e = p.get(i);
            if (!e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) throw new IllegalArgumentException("block_edit pos must be integers");
            double d = e.getAsDouble();
            if (d != Math.rint(d) || Math.abs(d) > 30_000_000) throw new IllegalArgumentException("block_edit pos must be integer block coordinates");
            v[i] = (int) d;
        }
        String state = op == Op.PLACE ? str(m, "state") : null;
        if (op == Op.PLACE && (state == null || state.isBlank())) throw new IllegalArgumentException("block_edit place needs state");
        if (state != null && state.length() > MAX_STATE) throw new IllegalArgumentException("block_edit state too long");
        String face = str(m, "face");
        if (face != null && !FACES.contains(face)) throw new IllegalArgumentException("block_edit face must be one of " + FACES);
        return new BlockEdit(op, v[0], v[1], v[2], state, face);
    }

    private static String str(JsonObject m, String k) {
        return m.has(k) && m.get(k).isJsonPrimitive() ? m.get(k).getAsString() : null;
    }

    /** Namespaced block id of a state string: "minecraft:oak_log[axis=x]" -> "minecraft:oak_log" ("stone" -> "minecraft:stone"). */
    public static String blockId(String state) {
        int i = state.indexOf('[');
        String id = (i < 0 ? state : state.substring(0, i)).trim().toLowerCase(Locale.ROOT);
        return id.contains(":") ? id : "minecraft:" + id;
    }

    public JsonArray posJson() { return Protocol.ints(x, y, z); }

    /** Error reply for this edit: the standard error plus "op" and "pos", so the client can undo its optimistic change. */
    public JsonObject error(String code, String message, Long refSeq) {
        JsonObject e = Protocol.error(code, message, refSeq, false);
        e.addProperty("op", op.id());
        e.add("pos", posJson());
        return e;
    }
}
