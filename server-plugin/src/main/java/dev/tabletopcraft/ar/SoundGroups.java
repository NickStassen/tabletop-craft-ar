package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import org.bukkit.Registry;
import org.bukkit.Sound;
import org.bukkit.SoundGroup;
import org.bukkit.block.BlockType;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * [sound] Which sounds each block makes when it is broken, stepped on, placed, hit while digging and landed on
 * (PROTOCOL.md "Sound": sound_groups). The game keeps this in code (each block's SoundType), not in data a headset could
 * read, so the server reads it once from {@code BlockData#getSoundGroup} and sends it to sound_v1 sessions. Blocks with
 * the same five sounds, volume and pitch share one group. Pure except {@link #scan()}.
 */
public final class SoundGroups {
    /** One SoundType: sound event ids (namespace minecraft: left out), volume and pitch. */
    public record Group(String brk, String step, String place, String hit, String fall, float volume, float pitch) {}

    private final Map<Group, List<String>> groups = new LinkedHashMap<>();
    private int blocks;

    public static SoundGroups empty() { return new SoundGroups(); }

    public void add(String blockId, Group g) {
        groups.computeIfAbsent(g, k -> new ArrayList<>()).add(blockId);
        blocks++;
    }

    public int blocks() { return blocks; }
    public int size() { return groups.size(); }

    /** The group a block is in, or null (tests). */
    public Group groupOf(String blockId) {
        for (var e : groups.entrySet()) if (e.getValue().contains(blockId)) return e.getKey();
        return null;
    }

    /** A sound's id as clients look it up in sounds.json: minecraft:block.stone.break -> block.stone.break. */
    public static String key(String namespaced) {
        if (namespaced == null) return null;
        return namespaced.startsWith("minecraft:") ? namespaced.substring(10) : namespaced;
    }

    public JsonObject message() {
        JsonObject o = Protocol.body("sound_groups");
        o.addProperty("count", blocks);
        JsonArray a = new JsonArray();
        for (var e : groups.entrySet()) {
            Group g = e.getKey();
            JsonObject j = new JsonObject();
            j.addProperty("break", g.brk());
            j.addProperty("step", g.step());
            j.addProperty("place", g.place());
            j.addProperty("hit", g.hit());
            j.addProperty("fall", g.fall());
            j.addProperty("volume", round(g.volume()));
            j.addProperty("pitch", round(g.pitch()));
            JsonArray b = new JsonArray();
            e.getValue().forEach(b::add);
            j.add("blocks", b);
            a.add(j);
        }
        o.add("groups", a);
        return o;
    }

    static double round(float f) { return Math.round(f * 1000.0) / 1000.0; }

    /** Every block of the running server (main thread, once per start/reload). */
    static SoundGroups scan() {
        SoundGroups s = new SoundGroups();
        for (BlockType bt : Registry.BLOCK) {
            if (bt.isAir()) continue;
            SoundGroup g;
            try { g = bt.createBlockData().getSoundGroup(); } catch (RuntimeException e) { continue; }
            s.add(bt.getKey().toString(), new Group(id(g.getBreakSound()), id(g.getStepSound()), id(g.getPlaceSound()),
                id(g.getHitSound()), id(g.getFallSound()), g.getVolume(), g.getPitch()));
        }
        return s;
    }

    /** A Bukkit sound's event id as sounds.json names it (block.stone.break), or null. */
    static String id(Sound sound) {
        if (sound == null) return null;
        var k = Registry.SOUND_EVENT.getKey(sound);
        return k == null ? null : key(k.toString());
    }
}
