package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.GameRule;
import org.bukkit.World;

import java.util.Collection;
import java.util.HashMap;
import java.util.Map;

/**
 * [living] Sends {@code world_state} (time of day and weather, PROTOCOL.md "World state: time and weather") to sessions
 * that asked for it (hello caps world_v1): once their region is known, whenever the weather, the world or the clock
 * mode changes or the clock jumps (/time set), and about once a second otherwise. Reads each world once per tick it is
 * needed (a few getters). Main thread.
 */
final class WorldStateStreamer {
    private final TabletopPlugin plugin;
    boolean enabled = true;
    private long tick;

    WorldStateStreamer(TabletopPlugin plugin) { this.plugin = plugin; }

    void tick(Collection<Session> sessions) {
        tick++;
        if (!enabled) return;
        Map<String, WorldState> byWorld = new HashMap<>();
        for (Session s : sessions) {
            if (!s.wantsWorld || s.region == null) continue;
            WorldState now = byWorld.computeIfAbsent(s.region.world(), WorldStateStreamer::read);
            if (now == null) continue;
            if (!WorldState.due(s.worldStateSent, tick - s.worldStateTick, now)) continue;
            s.worldStateSent = now;
            s.worldStateTick = tick;
            plugin.sendWorldState(s, now.message());
        }
    }

    /** The state of a world by name, or null if it isn't loaded. */
    @SuppressWarnings("removal")   // DO_DAYLIGHT_CYCLE: Paper 26.3 has no replacement constant yet
    static WorldState read(String name) {
        World w = Bukkit.getWorld(name);
        if (w == null) return null;
        boolean daylight = !Boolean.FALSE.equals(w.getGameRuleValue(GameRule.DO_DAYLIGHT_CYCLE));
        boolean ticking = daylight && !Bukkit.getServerTickManager().isFrozen();
        return new WorldState(w.getName(), WorldState.dimension(w.getEnvironment().name()), w.hasSkyLight(), w.getFullTime(),
            Math.floorMod(w.getTime(), 24000L), w.isFixedTime(), w.hasStorm(), w.isThundering() && w.hasStorm(), ticking);
    }
}
