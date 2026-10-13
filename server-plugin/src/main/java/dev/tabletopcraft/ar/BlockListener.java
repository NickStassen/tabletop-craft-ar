package dev.tabletopcraft.ar;

import org.bukkit.block.Block;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.block.*;
import org.bukkit.event.entity.EntityChangeBlockEvent;
import org.bukkit.event.entity.EntityExplodeEvent;

/** Marks the tracker dirty when a block in any subscribed region is about to change (ARCHITECTURE.md 3.3). */
public final class BlockListener implements Listener {
    private final TabletopPlugin plugin;
    public BlockListener(TabletopPlugin plugin) { this.plugin = plugin; }

    private void mark(Block b) { plugin.markDirty(b.getWorld().getName(), b.getX(), b.getY(), b.getZ()); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockPlaceEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockBreakEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockFromToEvent e) { mark(e.getToBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockBurnEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockFadeEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockFormEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockSpreadEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(LeavesDecayEvent e) { mark(e.getBlock()); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockPistonExtendEvent e) { mark(e.getBlock()); e.getBlocks().forEach(this::mark); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockPistonRetractEvent e) { mark(e.getBlock()); e.getBlocks().forEach(this::mark); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(BlockExplodeEvent e) { e.blockList().forEach(this::mark); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(EntityExplodeEvent e) { e.blockList().forEach(this::mark); }
    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true) public void on(EntityChangeBlockEvent e) { mark(e.getBlock()); }
}
