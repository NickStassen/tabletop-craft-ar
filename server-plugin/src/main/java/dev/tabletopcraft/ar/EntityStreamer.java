package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import org.bukkit.Bukkit;
import org.bukkit.World;

import java.util.Collection;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Streams non-player entities (PROTOCOL.md 4.21) to sessions that asked for them (hello caps entities_v1), at the
 * player_positions rate. Each region is sampled once per send even when several headsets share it; each session gets
 * a delta against what it already holds (EntityDelta), and a full list after its region changes. Main thread.
 */
final class EntityStreamer {
    private final TabletopPlugin plugin;
    private final EntitySampler sampler = new EntitySampler();
    boolean enabled = true;
    int max = 256, margin = 2;

    // Stats for the log line and /tabletop entities (main thread).
    private long rounds, entitiesSum;
    private double sampleMsSum, sampleMsMax, deltaMsSum, deltaMsMax;
    private long lastLogMs = System.currentTimeMillis();
    private final AtomicLong windowBytes = new AtomicLong();   // all sessions, since the last log line (outbound thread adds)
    private int windowSessions;
    String lastStats = "no entity samples yet";

    EntityStreamer(TabletopPlugin plugin) { this.plugin = plugin; }

    void configure(boolean enabled, int max, int margin) {
        this.enabled = enabled;
        this.max = Math.max(1, max);
        this.margin = Math.max(0, margin);
    }

    void tick(Collection<Session> sessions) {
        if (!enabled) return;
        Map<RegionSpec, List<EntityState>> byRegion = new HashMap<>();
        double sampleMs = 0, deltaMs = 0;
        int entities = 0;
        boolean any = false;
        for (Session s : sessions) {
            if (!s.wantsEntities || s.region == null) continue;
            any = true;
            List<EntityState> now = byRegion.get(s.region);
            if (now == null) {
                World w = Bukkit.getWorld(s.region.world());
                if (w == null) continue;
                long t0 = System.nanoTime();
                now = sampler.sample(w, s.region, margin, max);
                sampleMs += (System.nanoTime() - t0) / 1e6;
                entities += now.size();
                byRegion.put(s.region, now);
            }
            long t1 = System.nanoTime();
            boolean full = !s.region.equals(s.entitiesRegion);   // new or changed region: replace the client's set
            if (full) s.entitiesRegion = s.region;
            JsonObject msg = EntityDelta.message(s.entitiesSent, now, full);
            deltaMs += (System.nanoTime() - t1) / 1e6;
            if (msg != null) plugin.sendCounting(s, msg, s.entityBytes, windowBytes);
        }
        if (!any) return;
        windowSessions = Math.max(windowSessions, (int) sessions.stream().filter(x -> x.wantsEntities).count());
        rounds++;
        entitiesSum += entities;
        sampleMsSum += sampleMs; sampleMsMax = Math.max(sampleMsMax, sampleMs);
        deltaMsSum += deltaMs; deltaMsMax = Math.max(deltaMsMax, deltaMs);
        long now = System.currentTimeMillis();
        if (now - lastLogMs >= 30_000) log(now);
    }

    /** One ENTITIES line per 30 s while clients take entities: counts, main-thread ms per round, bytes/s. */
    private void log(long now) {
        double secs = (now - lastLogMs) / 1000.0;
        lastStats = String.format("ENTITIES rounds=%d avg_entities=%.1f sample_ms avg=%.3f max=%.3f delta_ms avg=%.3f max=%.3f bytes_per_s=%d sessions=%d",
            rounds, entitiesSum / (double) Math.max(1, rounds), sampleMsSum / Math.max(1, rounds), sampleMsMax,
            deltaMsSum / Math.max(1, rounds), deltaMsMax, Math.round(windowBytes.getAndSet(0) / secs), windowSessions);
        plugin.getLogger().info(lastStats);
        rounds = 0; entitiesSum = 0; windowSessions = 0; sampleMsSum = sampleMsMax = deltaMsSum = deltaMsMax = 0;
        lastLogMs = now;
    }
}
