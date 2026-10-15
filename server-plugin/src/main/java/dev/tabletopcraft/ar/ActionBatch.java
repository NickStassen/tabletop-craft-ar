package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * One tick's worth of actions (PROTOCOL.md 4.22): arm swings, digging progress, block breaks and places, hurt entities.
 * Repeats within a tick collapse (the latest dig stage per block, one swing per entity and hand, one hurt per entity),
 * at most {@link #MAX} survive, and each session gets the ones inside its region. Pure (no Bukkit).
 */
public final class ActionBatch {
    public static final int MAX = 128;
    /** Blocks around the region an entity action may come from (players are listed up to 16 blocks out, others 2). */
    public static final int PLAYER_MARGIN = 16, ENTITY_MARGIN = 2;

    /** Where an action happened (world block coords) and how far outside a region it still counts. */
    public record Action(String world, double x, double y, double z, int margin, JsonObject json) {}

    private final Map<String, Action> actions = new LinkedHashMap<>();
    private int dropped;

    public boolean isEmpty() { return actions.isEmpty(); }
    public int size() { return actions.size(); }
    public int dropped() { return dropped; }
    public void clear() { actions.clear(); dropped = 0; }

    private void put(String key, Action a) {
        if (!actions.containsKey(key) && actions.size() >= MAX) { dropped++; return; }
        actions.remove(key);   // re-insert: the latest of a repeated action keeps its place at the end
        actions.put(key, a);
    }

    public void swing(String world, double x, double y, double z, int id, boolean offHand, boolean player) {
        JsonObject o = kind("swing");
        o.addProperty("id", id);
        o.addProperty("hand", offHand ? "off" : "main");
        put("swing:" + id + (offHand ? ":off" : ":main"), new Action(world, x, y, z, player ? PLAYER_MARGIN : ENTITY_MARGIN, o));
    }

    /** Digging progress 0..9 at a block, or -1 to clear it; {@code by} is the digging entity's id (or null). */
    public void dig(String world, int x, int y, int z, int stage, Integer by) {
        JsonObject o = kind("dig");
        o.add("pos", Protocol.ints(x, y, z));
        o.addProperty("stage", Math.max(-1, Math.min(9, stage)));
        if (by != null) o.addProperty("id", by);
        put("dig:" + x + "," + y + "," + z, new Action(world, x, y, z, 0, o));
    }

    /** A block broken (by a player or an explosion): clients burst its particles and clear any dig overlay there. */
    public void breakBlock(String world, int x, int y, int z, String state, Integer by) {
        actions.remove("dig:" + x + "," + y + "," + z);   // the break clears the cracks anyway
        JsonObject o = kind("break");
        o.add("pos", Protocol.ints(x, y, z));
        o.addProperty("block", state);
        if (by != null) o.addProperty("id", by);
        put("break:" + x + "," + y + "," + z, new Action(world, x, y, z, 0, o));
    }

    public void place(String world, int x, int y, int z, String state, Integer by) {
        JsonObject o = kind("place");
        o.add("pos", Protocol.ints(x, y, z));
        o.addProperty("block", state);
        if (by != null) o.addProperty("id", by);
        put("place:" + x + "," + y + "," + z, new Action(world, x, y, z, 0, o));
    }

    /** An entity (or player) took damage: clients flash it red; crit adds the critical-hit sparks. */
    public void hurt(String world, double x, double y, double z, int id, boolean crit, boolean player) {
        String key = "hurt:" + id;
        Action old = actions.get(key);
        JsonObject o = kind("hurt");
        o.addProperty("id", id);
        if (crit || (old != null && old.json().has("crit"))) o.addProperty("crit", true);
        put(key, new Action(world, x, y, z, player ? PLAYER_MARGIN : ENTITY_MARGIN, o));
    }

    /** [sound] Sounds around the region a headset still hears (PROTOCOL.md "Sound"). */
    public static final int SOUND_MARGIN = 8;

    /**
     * [sound] A sound the headset can't infer (note blocks, bells, containers, jukeboxes, explosions, the headset's own
     * uses): event id, world position (block sounds at the block centre), vanilla volume and pitch, streamed or not. The
     * same sound at the same block collapses within a tick. Only sound_v1 sessions get it.
     */
    public void sound(String world, double x, double y, double z, String sound, float volume, float pitch, boolean stream) {
        JsonObject o = kind("sound");
        o.addProperty("sound", sound);
        o.add("pos", Protocol.doubles(x, y, z));
        o.addProperty("volume", Math.round(volume * 1000.0) / 1000.0);
        o.addProperty("pitch", Math.round(pitch * 1000.0) / 1000.0);
        if (stream) o.addProperty("stream", true);
        put("sound:" + sound + "@" + (int) Math.floor(x) + "," + (int) Math.floor(y) + "," + (int) Math.floor(z), new Action(world, x, y, z, SOUND_MARGIN, o));
    }

    /** [sound] Stop what plays at a block (a jukebox stopped or emptied). */
    public void soundStop(String world, int x, int y, int z) {
        JsonObject o = kind("sound_stop");
        o.add("pos", Protocol.ints(x, y, z));
        put("sound_stop:" + x + "," + y + "," + z, new Action(world, x + 0.5, y + 0.5, z + 0.5, SOUND_MARGIN, o));
    }

    // ---------- [motion] what animates on the table (PROTOCOL.md "Block animations") ----------

    /** Explosions are seen from a little outside the region (their cloud is several blocks wide). */
    public static final int EXPLOSION_MARGIN = 6;

    /**
     * [motion] A container's lid opens (its first viewer) or closes (its last): chests, trapped, ender and copper
     * chests, shulker boxes. Barrels need nothing (their open state is in the block state).
     */
    public void lid(String world, int x, int y, int z, boolean open) {
        JsonObject o = kind("lid");
        o.add("pos", Protocol.ints(x, y, z));
        o.addProperty("open", open);
        put("lid:" + x + "," + y + "," + z, new Action(world, x, y, z, 0, o));
    }

    /** [motion] Blocks whose lid the client animates (ChestRenderer and ShulkerBoxRenderer blocks). */
    public static boolean lidded(String blockId) {
        String p = blockId.startsWith("minecraft:") ? blockId.substring(10) : blockId;
        return p.equals("chest") || p.equals("trapped_chest") || p.equals("ender_chest") || p.endsWith("copper_chest") || p.endsWith("shulker_box");
    }

    /** [motion] A bell was rung from {@code dir} (north, south, east, west: the side it swings away from). */
    public void bell(String world, int x, int y, int z, String dir) {
        JsonObject o = kind("bell");
        o.add("pos", Protocol.ints(x, y, z));
        o.addProperty("dir", dir);
        put("bell:" + x + "," + y + "," + z, new Action(world, x, y, z, 0, o));
    }

    /** [motion] An explosion at a point: clients draw its cloud and flash, sized by its power (TNT 4). */
    public void explode(String world, double x, double y, double z, float power) {
        JsonObject o = kind("explode");
        o.add("pos", Protocol.doubles(x, y, z));
        o.addProperty("power", Math.round(power * 10.0) / 10.0);
        put("explode:" + (int) Math.floor(x) + "," + (int) Math.floor(y) + "," + (int) Math.floor(z), new Action(world, x, y, z, EXPLOSION_MARGIN, o));
    }

    /**
     * [motion] Vanilla's explosion power for what exploded (Bukkit's events don't carry it): TNT and TNT minecarts 4,
     * creepers 3 (charged 6), end crystals 6, the wither's birth 7, ghast fireballs 1, wither skulls 1, beds and respawn
     * anchors (a block, null type) 5; anything else 3.
     */
    public static float explosionPower(String entityType, boolean charged) {
        if (entityType == null) return 5f;
        return switch (entityType) {
            case "minecraft:tnt", "minecraft:tnt_minecart" -> 4f;
            case "minecraft:creeper" -> charged ? 6f : 3f;
            case "minecraft:end_crystal" -> 6f;
            case "minecraft:wither" -> 7f;
            case "minecraft:fireball", "minecraft:wither_skull" -> 1f;
            default -> 3f;
        };
    }

    static boolean isSound(JsonObject a) {
        String k = a.get("kind").getAsString();
        return k.equals("sound") || k.equals("sound_stop");
    }

    /** The actions inside a region (its box grown by each action's margin; y without margin for blocks), in order. */
    public JsonArray forRegion(RegionSpec r) { return forRegion(r, true); }

    /** Like {@link #forRegion(RegionSpec)}; {@code sounds} false leaves out sound and sound_stop (sessions without sound_v1). */
    public JsonArray forRegion(RegionSpec r, boolean sounds) {
        JsonArray a = new JsonArray();
        for (Action x : actions.values()) {
            if (!x.world().equals(r.world())) continue;
            if (!sounds && isSound(x.json())) continue;
            int m = x.margin();
            boolean in = x.x() >= r.ox() - m && x.x() < r.ox() + r.sx() + m && x.z() >= r.oz() - m && x.z() < r.oz() + r.sz() + m
                && x.y() >= r.oy() - Math.min(m, ENTITY_MARGIN) && x.y() < r.oy() + r.sy() + Math.min(m, ENTITY_MARGIN);
            if (in) a.add(x.json().deepCopy());
        }
        return a;
    }

    /** The message for one region, or null when nothing happened there. */
    public JsonObject message(RegionSpec r) { return message(r, true); }

    public JsonObject message(RegionSpec r, boolean sounds) {
        JsonArray a = forRegion(r, sounds);
        if (a.isEmpty()) return null;
        JsonObject o = Protocol.body("actions");
        o.add("actions", a);
        return o;
    }

    public List<Action> all() { return new ArrayList<>(actions.values()); }

    private static JsonObject kind(String k) {
        JsonObject o = new JsonObject();
        o.addProperty("kind", k);
        return o;
    }
}
