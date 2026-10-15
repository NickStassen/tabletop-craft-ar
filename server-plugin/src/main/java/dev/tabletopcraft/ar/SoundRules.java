package dev.tabletopcraft.ar;

import java.util.Locale;

/**
 * [sound] The sounds the server tells sound_v1 headsets about because they can't be told from block states or entity
 * data (PROTOCOL.md "Sound": the {@code sound} action): note blocks, bells, containers opening and closing, jukebox
 * songs, explosions, fire lit, buckets, bone meal. Volumes and pitches are vanilla's (26.3). Pure (no Bukkit).
 */
public final class SoundRules {
    private SoundRules() {}

    /** A sound to send: event id (no minecraft: namespace), vanilla volume (over 1 = heard further) and pitch. */
    public record Play(String sound, float volume, float pitch) {}

    /** NoteBlock: volume 3, pitch 2^((note - 12) / 12) for notes 0..24 (custom heads keep pitch 1). */
    public static float notePitch(int note) { return (float) Math.pow(2.0, (note - 12) / 12.0); }

    /**
     * Opening or closing a container (ChestBlockEntity, BarrelBlockEntity, ShulkerBoxBlockEntity, EnderChestBlockEntity,
     * the copper chests): the block id decides the sound; null for blocks that make none. {@code rnd} is a random 0..1.
     */
    public static Play container(String blockId, boolean open, float rnd) {
        String p = path(blockId);
        String verb = open ? "open" : "close";
        float pitch = rnd * 0.1f + 0.9f;
        if (p.equals("chest") || p.equals("trapped_chest")) return new Play("block.chest." + verb, 0.5f, pitch);
        if (p.equals("ender_chest")) return new Play("block.ender_chest." + verb, 0.5f, pitch);
        if (p.equals("barrel")) return new Play("block.barrel." + verb, 0.5f, pitch);
        if (p.endsWith("shulker_box")) return new Play("block.shulker_box." + verb, 0.5f, pitch);
        if (p.endsWith("copper_chest")) {
            String stage = p.contains("oxidized") ? "_oxidized" : p.contains("weathered") ? "_weathered" : "";
            return new Play("block.copper_chest" + stage + "." + verb, 0.5f, pitch);
        }
        return null;
    }

    /** An explosion by entity type (null = a block: beds, respawn anchors). {@code r1}, {@code r2}: random 0..1. */
    public static Play explosion(String entityType, float r1, float r2) {
        String p = entityType == null ? "" : path(entityType);
        if (p.equals("wind_charge")) return new Play("entity.wind_charge.wind_burst", 1f, 1f);
        if (p.equals("breeze_wind_charge")) return new Play("entity.breeze.wind_burst", 1f, 1f);
        return new Play("entity.generic.explode", 4f, (1f + (r1 - r2) * 0.2f) * 0.7f);
    }

    /** A Java player's bucket: emptying (the bucket item) or filling (the fluid block it took). Null when unknown. */
    public static Play bucket(String id, boolean fill) {
        String p = path(id);
        if (fill) {
            if (p.equals("water") || p.equals("bubble_column")) return new Play("item.bucket.fill", 1f, 1f);
            if (p.equals("lava")) return new Play("item.bucket.fill_lava", 1f, 1f);
            if (p.equals("powder_snow")) return new Play("item.bucket.fill_powder_snow", 1f, 1f);
            return new Play("item.bucket.fill", 1f, 1f);   // a waterlogged block's water
        }
        return switch (p) {
            case "water_bucket" -> new Play("item.bucket.empty", 1f, 1f);
            case "lava_bucket" -> new Play("item.bucket.empty_lava", 1f, 1f);
            case "powder_snow_bucket" -> new Play("item.bucket.empty_powder_snow", 1f, 1f);
            case "axolotl_bucket" -> new Play("item.bucket.empty_axolotl", 1f, 1f);
            case "tadpole_bucket" -> new Play("item.bucket.empty_tadpole", 1f, 1f);
            default -> p.endsWith("_bucket") && !p.equals("milk_bucket") ? new Play("item.bucket.empty_fish", 1f, 1f) : null;
        };
    }

    /** Fire lit by a Java player: flint and steel or a fire charge (BlockIgniteEvent causes); null for spreading fire. */
    public static Play ignite(String cause, float rnd) {
        return switch (cause.toUpperCase(Locale.ROOT)) {
            case "FLINT_AND_STEEL" -> new Play("item.flintandsteel.use", 1f, rnd * 0.4f + 0.8f);
            case "FIREBALL" -> new Play("item.firecharge.use", 1f, rnd * 0.4f + 0.8f);
            default -> null;
        };
    }

    /** Bone meal (BlockFertilizeEvent): vanilla's level event 1505 plays this. */
    public static Play boneMeal() { return new Play("item.bone_meal.use", 1f, 1f); }

    /** A bell rung (BellBlock#attemptToRing): volume 2, pitch 1. */
    public static Play bell() { return new Play("block.bell.use", 2f, 1f); }

    /** A jukebox song (JukeboxSongPlayer, level event 1010): volume 4, pitch 1, streamed. */
    public static Play song(String soundId) { return new Play(SoundGroups.key(soundId), 4f, 1f); }

    static String path(String id) {
        if (id == null) return "";
        String s = id.toLowerCase(Locale.ROOT);
        int c = s.indexOf(':');
        s = c >= 0 ? s.substring(c + 1) : s;
        int b = s.indexOf('[');
        return b >= 0 ? s.substring(0, b) : s;
    }
}
