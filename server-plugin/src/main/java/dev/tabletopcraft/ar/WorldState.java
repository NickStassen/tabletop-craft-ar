package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;

/**
 * [living] The {@code world_state} message (PROTOCOL.md "World state: time and weather"): the time of day and the
 * weather of a session's world, so a headset lights its miniature like vanilla's lightmap (night, rain, thunder) and
 * draws rain and snow. Plain data plus the send rule, so it is testable without a server; {@link WorldStateStreamer}
 * reads it from Bukkit on the main thread.
 *
 * @param world      world name (the region's world)
 * @param dimension  "overworld", "the_nether", "the_end" or "custom" (World#getEnvironment)
 * @param skylight   the dimension has sky light (World#hasSkyLight)
 * @param time       total ticks of the world's clock (World#getFullTime): day count and moon phase
 * @param dayTime    the clock within the day, 0..23999 (World#getTime): vanilla's timelines key on it
 * @param fixedTime  the clock doesn't run (World#isFixedTime: the Nether and the End)
 * @param rain       it rains or snows (World#hasStorm)
 * @param thunder    a thunderstorm (World#isThundering; only while it rains)
 * @param ticking    the clock advances (false while the daylight cycle gamerule is off or the server tick is frozen)
 */
public record WorldState(String world, String dimension, boolean skylight, long time, long dayTime, boolean fixedTime,
                         boolean rain, boolean thunder, boolean ticking) {

    /** The client predicts the clock between messages: resend at once when it jumped by more than this (time set). */
    public static final long JUMP_TICKS = 40;
    /** Otherwise one message every this many ticks (about once a second), so a new client and a drifting clock catch up. */
    public static final int EVERY_TICKS = 20;

    public JsonObject message() {
        JsonObject o = Protocol.body("world_state");
        o.addProperty("world", world);
        o.addProperty("dimension", dimension);
        o.addProperty("skylight", skylight);
        o.addProperty("time", time);
        o.addProperty("day_time", dayTime);
        o.addProperty("rain", rain ? 1 : 0);
        o.addProperty("thunder", thunder ? 1 : 0);
        if (fixedTime) o.addProperty("fixed_time", true);
        if (!ticking) o.addProperty("ticking", false);
        return o;
    }

    /** The protocol's dimension name for a Bukkit environment name (NORMAL, NETHER, THE_END, CUSTOM). */
    public static String dimension(String environment) {
        return switch (environment) {
            case "NORMAL" -> "overworld";
            case "NETHER" -> "the_nether";
            case "THE_END" -> "the_end";
            default -> "custom";
        };
    }

    /**
     * Whether a session that last got {@code last}, {@code ticksSince} ticks ago, needs {@code now}: always when it has
     * nothing yet or the world, dimension, weather or clock mode changed; when the clock is off the client's prediction
     * (last time plus the ticks since, while it ticks) by more than {@link #JUMP_TICKS}; else every {@link #EVERY_TICKS}.
     */
    public static boolean due(WorldState last, long ticksSince, WorldState now) {
        if (last == null) return true;
        if (!last.world.equals(now.world) || !last.dimension.equals(now.dimension) || last.rain != now.rain || last.thunder != now.thunder
            || last.fixedTime != now.fixedTime || last.ticking != now.ticking || last.skylight != now.skylight) return true;
        long predicted = last.time + (last.ticking && !last.fixedTime ? ticksSince : 0);
        if (Math.abs(now.time - predicted) > JUMP_TICKS) return true;
        return ticksSince >= EVERY_TICKS;
    }
}
