package dev.tabletopcraft.ar;

import io.papermc.paper.event.block.BlockBreakProgressUpdateEvent;
import org.bukkit.Location;
import org.bukkit.block.Block;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Player;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.block.BlockBreakEvent;
import org.bukkit.event.block.BlockDamageAbortEvent;
import org.bukkit.event.block.BlockExplodeEvent;
import org.bukkit.event.block.BlockPlaceEvent;
import org.bukkit.event.entity.EntityDamageByEntityEvent;
import org.bukkit.event.entity.EntityDamageEvent;
import org.bukkit.event.entity.EntityExplodeEvent;
import org.bukkit.event.player.PlayerAnimationEvent;
import org.bukkit.event.player.PlayerAnimationType;

import java.util.Collection;
import java.util.List;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Collects what players and mobs do (PROTOCOL.md 4.22) into one {@link ActionBatch} per tick: arm swings, digging
 * progress, block breaks (players and explosions), block places and damage. The handlers only record (main thread,
 * a few objects each) and do nothing while no session asked for actions_v1; {@link #flush} sends one message per
 * session per tick with the actions inside its region.
 */
final class ActionListener implements Listener {
    /** Explosions can break hundreds of blocks: particles for at most this many of them. */
    static final int MAX_EXPLODED = 48;
    private final TabletopPlugin plugin;
    final ActionBatch batch = new ActionBatch();
    boolean enabled = true;
    private boolean active;   // some ready session wants actions (updated by flush)
    long sent, dropped;
    final AtomicLong bytes = new AtomicLong();   // all sessions (outbound thread adds)

    ActionListener(TabletopPlugin plugin) { this.plugin = plugin; }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onSwing(PlayerAnimationEvent e) {
        if (!active) return;
        Player p = e.getPlayer();
        Location l = p.getLocation();
        batch.swing(l.getWorld().getName(), l.getX(), l.getY(), l.getZ(), p.getEntityId(), e.getAnimationType() == PlayerAnimationType.OFF_ARM_SWING, true);
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onProgress(BlockBreakProgressUpdateEvent e) {
        if (!active) return;
        float p = e.getProgress();
        Entity by = e.getEntity();
        dig(e.getBlock(), p < 0 || p >= 1 ? -1 : (int) Math.floor(p * 10), by == null ? null : by.getEntityId());
    }

    @EventHandler(priority = EventPriority.MONITOR)
    public void onAbort(BlockDamageAbortEvent e) {
        if (active) dig(e.getBlock(), -1, e.getPlayer().getEntityId());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onBreak(BlockBreakEvent e) {
        if (!active) return;
        Block b = e.getBlock();
        batch.breakBlock(b.getWorld().getName(), b.getX(), b.getY(), b.getZ(), b.getBlockData().getAsString(), e.getPlayer().getEntityId());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onPlace(BlockPlaceEvent e) {
        if (!active) return;
        Block b = e.getBlockPlaced();
        batch.place(b.getWorld().getName(), b.getX(), b.getY(), b.getZ(), b.getBlockData().getAsString(), e.getPlayer().getEntityId());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onExplode(EntityExplodeEvent e) { if (active) exploded(e.blockList()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onExplode(BlockExplodeEvent e) { if (active) exploded(e.blockList()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onDamage(EntityDamageEvent e) {
        if (!active) return;
        Entity v = e.getEntity();
        if (v.getScoreboardTags().contains(GiantAvatar.TAG)) return;
        Location l = v.getLocation();
        boolean crit = e instanceof EntityDamageByEntityEvent d && d.isCritical();
        batch.hurt(l.getWorld().getName(), l.getX(), l.getY(), l.getZ(), v.getEntityId(), crit, v instanceof Player);
        // Mobs swing their arms when they hit (players send their own swing).
        if (e instanceof EntityDamageByEntityEvent d && d.getDamager() instanceof LivingEntity m && !(m instanceof Player)) {
            Location ml = m.getLocation();
            batch.swing(ml.getWorld().getName(), ml.getX(), ml.getY(), ml.getZ(), m.getEntityId(), false, false);
        }
    }

    private void dig(Block b, int stage, Integer by) {
        batch.dig(b.getWorld().getName(), b.getX(), b.getY(), b.getZ(), stage, by);
    }

    private void exploded(List<Block> blocks) {
        int n = 0;
        for (Block b : blocks) {
            if (n++ >= MAX_EXPLODED) break;
            if (b.getType().isAir()) continue;
            batch.breakBlock(b.getWorld().getName(), b.getX(), b.getY(), b.getZ(), b.getBlockData().getAsString(), null);
        }
    }

    /** Once per tick: one "actions" message per session that wants them and has something in its region. */
    void flush(Collection<Session> sessions) {
        boolean any = false;
        for (Session s : sessions) {
            if (!s.wantsActions || s.region == null) continue;
            any = true;
            if (batch.isEmpty()) continue;
            var msg = batch.message(s.region);
            if (msg != null) { plugin.sendCounting(s, msg, s.actionBytes, bytes); sent++; }
        }
        dropped += batch.dropped();
        batch.clear();
        active = enabled && any;
    }
}
