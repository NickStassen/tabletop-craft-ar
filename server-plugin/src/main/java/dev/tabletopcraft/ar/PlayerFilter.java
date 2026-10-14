package dev.tabletopcraft.ar;

import org.bukkit.GameMode;
import org.bukkit.entity.Player;
import org.bukkit.metadata.MetadataValue;
import org.bukkit.potion.PotionEffectType;

import java.util.Collection;

/**
 * Which Java players go into player_positions (PROTOCOL.md 4.6): only players near the region in all three axes, and
 * never spectators, invisible players (the flag or the potion) or vanished ones. "Vanished" is the de-facto convention
 * of vanish plugins (a true "vanished" metadata value) or being hidden from any other online player (Player#canSee):
 * what a Java player can't see, the table doesn't show either.
 */
final class PlayerFilter {
    private PlayerFilter() {}

    /** Inside the region box grown by margin blocks on every side (x, y and z). */
    static boolean near(RegionSpec r, double x, double y, double z, int margin) {
        return x >= r.ox() - margin && x <= r.ox() + r.sx() + margin
            && z >= r.oz() - margin && z <= r.oz() + r.sz() + margin
            && y >= r.oy() - margin && y <= r.oy() + r.sy() + margin;
    }

    /** The rule, with the facts already looked up. */
    static boolean shown(boolean spectator, boolean invisible, boolean vanished) {
        return !spectator && !invisible && !vanished;
    }

    /** Main thread only. */
    static boolean shown(Player p, Collection<? extends Player> online) {
        return shown(p.getGameMode() == GameMode.SPECTATOR, p.isInvisible() || p.hasPotionEffect(PotionEffectType.INVISIBILITY), vanished(p, online));
    }

    static boolean vanished(Player p, Collection<? extends Player> online) {
        for (MetadataValue v : p.getMetadata("vanished")) if (v.asBoolean()) return true;
        for (Player o : online) if (o != p && !o.canSee(p)) return true;
        return false;
    }
}
