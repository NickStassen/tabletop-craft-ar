package dev.tabletopcraft.ar;

import io.papermc.paper.event.block.BlockBreakProgressUpdateEvent;
import org.bukkit.GameEvent;
import org.bukkit.Instrument;
import org.bukkit.Location;
import org.bukkit.Sound;
import org.bukkit.block.Block;
import org.bukkit.block.Jukebox;
import org.bukkit.event.block.BellRingEvent;
import org.bukkit.event.block.BlockFertilizeEvent;
import org.bukkit.event.block.BlockIgniteEvent;
import org.bukkit.event.block.NotePlayEvent;
import org.bukkit.event.player.PlayerBucketEmptyEvent;
import org.bukkit.event.player.PlayerBucketFillEvent;
import org.bukkit.event.world.GenericGameEvent;
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
 * session per tick with the actions inside its region. [sound] For sessions that also have sound_v1 it adds the sounds
 * a headset can't infer (PROTOCOL.md "Sound"): note blocks, bells, containers, jukebox songs, explosions, fire lit,
 * buckets and bone meal by Java players.
 */
final class ActionListener implements Listener {
    /** Explosions can break hundreds of blocks: particles for at most this many of them. */
    static final int MAX_EXPLODED = 48;
    private final TabletopPlugin plugin;
    final ActionBatch batch = new ActionBatch();
    boolean enabled = true;
    private boolean active;   // some ready session wants actions (updated by flush)
    private boolean soundActive;   // [sound] ... and one of them has sound_v1
    private final java.util.Random rnd = new java.util.Random();
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
    public void onExplode(EntityExplodeEvent e) {
        if (!active) return;
        exploded(e.blockList());
        Location l = e.getLocation();   // [motion] the cloud and flash (wind charges only gust)
        String type = e.getEntityType().getKey().toString();
        boolean charged = e.getEntity() instanceof org.bukkit.entity.Creeper c && c.isPowered();
        if (!type.endsWith("wind_charge")) batch.explode(l.getWorld().getName(), l.getX(), l.getY(), l.getZ(), ActionBatch.explosionPower(type, charged));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onExplode(BlockExplodeEvent e) {
        if (!active) return;
        exploded(e.blockList());
        Block b = e.getBlock();   // [motion] a bed or respawn anchor going off
        batch.explode(b.getWorld().getName(), b.getX() + 0.5, b.getY() + 0.5, b.getZ() + 0.5, ActionBatch.explosionPower(null, false));
    }

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

    // ---------- [sound] what sound_v1 headsets can't infer (PROTOCOL.md "Sound"; SoundRules) ----------

    /** A sound at a position for sound_v1 sessions (also used by InteractService for the headset's own uses). */
    void sound(Location l, SoundRules.Play p, boolean stream) {
        if (soundActive && p != null && l.getWorld() != null)
            batch.sound(l.getWorld().getName(), l.getX(), l.getY(), l.getZ(), p.sound(), p.volume(), p.pitch(), stream);
    }

    private void blockSound(Block b, SoundRules.Play p) { sound(b.getLocation().add(0.5, 0.5, 0.5), p, false); }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onNote(NotePlayEvent e) {
        if (!soundActive) return;
        Instrument in = e.getInstrument();
        Sound snd = in == null ? null : in.getSound();
        if (snd == null) return;   // a custom head's note_block_sound: not known here
        blockSound(e.getBlock(), new SoundRules.Play(SoundGroups.id(snd), 3f, SoundRules.notePitch(e.getNote().getId())));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onBell(BellRingEvent e) {
        if (!active) return;
        Block b = e.getBlock();
        org.bukkit.block.BlockFace f = e.getDirection();   // [motion] the swing: away from the side it was hit on
        if (f == null && b.getBlockData() instanceof org.bukkit.block.data.Directional d) f = d.getFacing();
        if (f != null && f.getModY() == 0) batch.bell(b.getWorld().getName(), b.getX(), b.getY(), b.getZ(), f.name().toLowerCase(java.util.Locale.ROOT));
        if (soundActive) blockSound(b, SoundRules.bell());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onGameEvent(GenericGameEvent e) {
        if (!active) return;
        GameEvent g = e.getEvent();
        boolean open = g == GameEvent.CONTAINER_OPEN;
        if (open || g == GameEvent.CONTAINER_CLOSE) {
            Block b = e.getLocation().getBlock();
            String type = b.getType().getKey().toString();
            if (ActionBatch.lidded(type)) batch.lid(b.getWorld().getName(), b.getX(), b.getY(), b.getZ(), open);   // [motion]
            if (soundActive) blockSound(b, SoundRules.container(type, open, rnd.nextFloat()));
            return;
        }
        if (!soundActive) return;
        if (g == GameEvent.JUKEBOX_PLAY) {
            Block b = e.getLocation().getBlock();
            String song = song(b);
            if (song != null) sound(b.getLocation().add(0.5, 0.5, 0.5), SoundRules.song(song), true);
        } else if (g == GameEvent.JUKEBOX_STOP_PLAY) {
            Block b = e.getLocation().getBlock();
            batch.soundStop(b.getWorld().getName(), b.getX(), b.getY(), b.getZ());
        }
    }

    /** The sound event of the disc in a jukebox, or null. */
    @SuppressWarnings("UnstableApiUsage")
    private static String song(Block b) {
        if (!(b.getState() instanceof Jukebox j) || !j.hasRecord()) return null;
        var jp = j.getRecord().getData(io.papermc.paper.datacomponent.DataComponentTypes.JUKEBOX_PLAYABLE);
        if (jp == null || jp.jukeboxSong() == null) return null;
        return SoundGroups.id(jp.jukeboxSong().getSound());
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onExplodeSound(EntityExplodeEvent e) {
        if (soundActive) sound(e.getLocation(), SoundRules.explosion(e.getEntityType().getKey().toString(), rnd.nextFloat(), rnd.nextFloat()), false);
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onExplodeSound(BlockExplodeEvent e) {
        if (soundActive) blockSound(e.getBlock(), SoundRules.explosion(null, rnd.nextFloat(), rnd.nextFloat()));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onIgnite(BlockIgniteEvent e) {
        if (soundActive && e.getPlayer() != null) blockSound(e.getBlock(), SoundRules.ignite(e.getCause().name(), rnd.nextFloat()));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onBucketEmpty(PlayerBucketEmptyEvent e) {
        if (soundActive) blockSound(e.getBlock(), SoundRules.bucket(e.getBucket().getKey().toString(), false));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onBucketFill(PlayerBucketFillEvent e) {
        if (soundActive) blockSound(e.getBlockClicked(), SoundRules.bucket(e.getBlockClicked().getType().getKey().toString(), true));
    }

    @EventHandler(priority = EventPriority.MONITOR, ignoreCancelled = true)
    public void onFertilize(BlockFertilizeEvent e) {
        if (soundActive && e.getPlayer() != null) blockSound(e.getBlock(), SoundRules.boneMeal());
    }

    /** Once per tick: one "actions" message per session that wants them and has something in its region. */
    void flush(Collection<Session> sessions) {
        boolean any = false, sounds = false;
        for (Session s : sessions) {
            if (!s.wantsActions || s.region == null) continue;
            any = true;
            sounds |= s.wantsSound;
            if (batch.isEmpty()) continue;
            var msg = batch.message(s.region, s.wantsSound);
            if (msg != null) { plugin.sendCounting(s, msg, s.actionBytes, bytes); sent++; }
        }
        dropped += batch.dropped();
        batch.clear();
        active = enabled && any;
        soundActive = active && sounds;
    }
}
