package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;

/** Parsed avatar_config (PROTOCOL.md 4.8). Null fields mean "keep previous value". */
public record AvatarConfig(Boolean enabled, String skinPlayer, String skinUrl, String model, Double blockSizeM,
                           Double maxScale, Boolean showHands, String nameTag, boolean nameTagPresent) {
    public static AvatarConfig parse(JsonObject m) {
        Double bs = num(m, "block_size_m");
        if (bs != null && (bs < 0.002 || bs > 0.1)) throw new IllegalArgumentException("block_size_m must be 0.002..0.1");
        Double ms = num(m, "max_scale");
        if (ms != null && !(ms > 0)) throw new IllegalArgumentException("max_scale must be > 0");
        String model = str(m, "model");
        if (model != null && !model.equals("wide") && !model.equals("slim")) throw new IllegalArgumentException("model must be wide or slim");
        return new AvatarConfig(bool(m, "enabled"), str(m, "skin_player"), str(m, "skin_url"), model, bs, ms,
            bool(m, "show_hands"), str(m, "name_tag"), m.has("name_tag"));
    }

    private static Double num(JsonObject m, String k) { return m.has(k) && !m.get(k).isJsonNull() ? m.get(k).getAsDouble() : null; }
    private static String str(JsonObject m, String k) { return m.has(k) && !m.get(k).isJsonNull() ? m.get(k).getAsString() : null; }
    private static Boolean bool(JsonObject m, String k) { return m.has(k) && !m.get(k).isJsonNull() ? m.get(k).getAsBoolean() : null; }
}
