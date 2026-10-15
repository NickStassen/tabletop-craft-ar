package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.plugin.Plugin;
import org.bukkit.plugin.PluginManager;

import java.lang.reflect.Method;
import java.util.Locale;
import java.util.UUID;
import java.util.function.Predicate;
import java.util.logging.Logger;

/**
 * [bedrock] Bedrock Edition players join the Paper server through Geyser, usually with Floodgate (ARCHITECTURE.md 3.5).
 * They are ordinary Bukkit players to the rest of the plugin (table figures, chat, the operator panel); the only thing
 * that needs help is the giant: Geyser has no Bedrock translation for item displays, so the floating head and hands
 * never reach a Bedrock client. With config {@code avatar.bedrock: auto} a floating giant also gets a Mannequin that
 * is hidden from everyone ({@code Entity#setVisibleByDefault(false)}) and shown only to Bedrock players
 * ({@code Player#showEntity}), in place of the head ({@code Player#hideEntity}). See {@link GiantAvatar#syncViewer}.
 * <p>
 * Floodgate and Geyser are optional (plugin.yml {@code softdepend}, no compile dependency): their public APIs are
 * called by reflection, Floodgate's {@code FloodgateApi#isFloodgatePlayer(UUID)} first (it also knows players coming
 * from a Geyser proxy), else Geyser's {@code GeyserApi#isBedrockPlayer(UUID)}. Without either, nobody is a Bedrock player.
 */
final class BedrockSupport implements Listener {
    /** config.yml avatar.bedrock. */
    enum Mode {
        AUTO, OFF;

        static Mode parse(String s) {
            return s != null && s.trim().toLowerCase(Locale.ROOT).matches("off|false|no|none") ? OFF : AUTO;
        }
    }

    private final GiantAvatar avatar;
    private volatile Predicate<UUID> api;
    private volatile String source = "none";
    private boolean warned;

    BedrockSupport(GiantAvatar avatar) { this.avatar = avatar; }

    /** Floodgate, Geyser or none: which API answers {@link #isBedrock}. */
    String source() { return source; }

    /** Is a Bedrock bridge installed (Floodgate or Geyser)? */
    boolean detected() { return api != null; }

    /** The giant gets its Bedrock Mannequin only with avatar.bedrock auto and a Bedrock bridge on the server. */
    static boolean wantBody(Mode mode, boolean detected) { return mode == Mode.AUTO && detected; }

    /** Looks for Floodgate, then Geyser (both enabled before us through softdepend). Main thread. */
    void detect(PluginManager pm, Logger log) {
        api = null;
        source = "none";
        for (String[] c : new String[][] {
            { "floodgate", "org.geysermc.floodgate.api.FloodgateApi", "getInstance", "isFloodgatePlayer" },
            { "Geyser-Spigot", "org.geysermc.geyser.api.GeyserApi", "api", "isBedrockPlayer" } }) {
            Plugin p = pm.getPlugin(c[0]);
            if (p == null || !p.isEnabled()) continue;
            try {
                Class<?> type = Class.forName(c[1], true, p.getClass().getClassLoader());
                Object instance = type.getMethod(c[2]).invoke(null);
                Method check = type.getMethod(c[3], UUID.class);
                if (instance == null) continue;
                api = u -> {
                    try { return Boolean.TRUE.equals(check.invoke(instance, u)); }
                    catch (ReflectiveOperationException | RuntimeException e) { warnOnce(log, c[0], e); return false; }
                };
                source = c[0];
                log.info("BEDROCK players detected through " + c[0] + " " + p.getPluginMeta().getVersion());
                return;
            } catch (ReflectiveOperationException | RuntimeException | LinkageError e) {
                log.warning("BEDROCK " + c[0] + " is installed but its API isn't usable (" + e + "); trying the next one");
            }
        }
    }

    private void warnOnce(Logger log, String from, Exception e) {
        if (warned) return;
        warned = true;
        log.warning("BEDROCK " + from + " API call failed (" + e + "); treating players as Java players");
    }

    boolean isBedrock(Player p) {
        Predicate<UUID> a = api;
        return a != null && a.test(p.getUniqueId());
    }

    /** How many Bedrock players are online (for /tabletop status). Main thread. */
    int online() {
        int n = 0;
        for (Player p : Bukkit.getOnlinePlayers()) if (isBedrock(p)) n++;
        return n;
    }

    /** A Bedrock player joining while the giant is up gets its Mannequin (Floodgate knows them before the join). */
    @EventHandler(priority = EventPriority.MONITOR)
    public void onJoin(PlayerJoinEvent e) { avatar.syncViewer(e.getPlayer()); }
}
