package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import net.kyori.adventure.text.Component;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Particle;
import org.bukkit.Registry;
import org.bukkit.SoundCategory;
import org.bukkit.Tag;
import org.bukkit.World;
import org.bukkit.block.Bell;
import org.bukkit.block.Block;
import org.bukkit.block.BlockFace;
import org.bukkit.block.CreatureSpawner;
import org.bukkit.block.Jukebox;
import org.bukkit.block.data.BlockData;
import org.bukkit.block.data.type.NoteBlock;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.damage.DamageSource;
import org.bukkit.damage.DamageType;
import org.bukkit.entity.Ageable;
import org.bukkit.entity.Animals;
import org.bukkit.entity.Damageable;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.LeashHitch;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Mob;
import org.bukkit.entity.Player;
import org.bukkit.entity.TNTPrimed;
import org.bukkit.entity.Tameable;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.entity.CreatureSpawnEvent;
import org.bukkit.event.player.PlayerTeleportEvent;
import org.bukkit.event.world.EntitiesLoadEvent;
import org.bukkit.inventory.ItemType;
import org.bukkit.util.Vector;

import java.io.IOException;
import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

/**
 * [hands] Using blocks, items and mobs from the headset (PROTOCOL.md "Using blocks and items" and "Acting on mobs",
 * cap interact_v1, config.yml section {@code interact}). Messages are parsed and rate-limited on the websocket thread
 * (LinkServer); everything here runs on the main thread.
 * <ul>
 *   <li>{@code interact}: {@link InteractRules} decides (pure), this applies it with the Paper API:
 *       {@code setBlockData(data, true)}, sounds, particles, and the extras (note, bell, jukebox, drops, primed TNT,
 *       bone meal, spawn eggs, lightning). Levers and buttons also update the neighbours of the block they hang on
 *       through one guarded reflective call ({@code Level#updateNeighborsAt}), so redstone behind a wall sees them;
 *       buttons release through {@code Block#tick()} after 20 or 30 ticks.</li>
 *   <li>{@code entity_op}: attack, kill, remove, use an item, name, move, hold (gravity and AI off while held,
 *       restored on drop, disconnect, a 3 s timeout or shutdown), freeze (kept in frozen.txt for
 *       {@code /tabletop unfreeze-all}), glow, info.</li>
 * </ul>
 * Protection plugins are not consulted (no player is involved), like block_edit; the region, the per-family switches
 * and the rate limits are what restrict it.
 */
final class InteractService implements Listener {
    static final long HOLD_TIMEOUT_MS = 3000;

    private final TabletopPlugin plugin;
    private EditWorld editWorld;
    // Config, read on websocket threads (volatile).
    volatile boolean enabled = true, requireToken, tokenSet, entities = true, harmPlayers;
    volatile double rate = 10, attackDamage = 5;
    volatile int spawnsPerMinute = 60, entityMargin = 2;
    volatile InteractRules.Flags flags = InteractRules.Flags.ALL;
    volatile Set<String> denySpawn = Set.of("minecraft:wither", "minecraft:ender_dragon");
    private volatile JsonObject itemRegistry = InteractMessages.itemRegistry(List.of(), List.of());
    private volatile Set<String> usable = Set.of(), eggs = Set.of();

    /** An entity held by a headset: what to restore and when it was last moved. */
    private static final class Held { Session s; boolean gravity; Boolean ai; long lastMs; }
    private final Map<UUID, Held> held = new HashMap<>();
    /** Mobs frozen from a headset (Mob#setAware(false) is saved with the mob): /tabletop unfreeze-all. */
    private final Set<UUID> frozen = new LinkedHashSet<>(), unfreezePending = new HashSet<>();
    private Path frozenFile;
    long interacts, entityOps, refused;

    InteractService(TabletopPlugin plugin) { this.plugin = plugin; }

    void init(EditWorld editWorld) { this.editWorld = editWorld; }

    void configure(FileConfiguration c) {
        enabled = c.getBoolean("interact.enabled", true);
        requireToken = c.getBoolean("interact.require-token", false);
        tokenSet = !c.getString("auth.token", "").isEmpty();
        rate = Math.max(1, c.getDouble("interact.max-per-second", 10));
        entities = c.getBoolean("interact.entities", true);
        harmPlayers = c.getBoolean("interact.harm-players", false);
        attackDamage = Math.max(0, c.getDouble("interact.attack-damage", 5));
        spawnsPerMinute = Math.max(1, c.getInt("interact.max-spawns-per-minute", 60));
        entityMargin = Math.max(0, c.getInt("entities.margin", 2));
        String lightning = String.valueOf(c.get("interact.lightning", "true")).toLowerCase(Locale.ROOT);
        lightning = switch (lightning) { case "true", "real", "on" -> "real"; case "effect" -> "effect"; default -> "off"; };
        flags = new InteractRules.Flags(c.getBoolean("interact.iron-doors", true), c.getBoolean("interact.fire", true),
            c.getBoolean("interact.tnt", true), c.getBoolean("interact.fluids", true), lightning);
        Set<String> deny = new HashSet<>();
        for (String id : c.getStringList("interact.deny-spawn")) deny.add(BlockEdit.blockId(id));
        if (!c.isList("interact.deny-spawn")) deny.addAll(Set.of("minecraft:wither", "minecraft:ender_dragon"));
        denySpawn = Set.copyOf(deny);
        frozenFile = plugin.getDataFolder().toPath().resolve("frozen.txt");
        loadFrozen();
    }

    /** interact_v1 is granted when interact.enabled and, with interact.require-token, the server has an auth.token. */
    boolean allowed() { return enabled && (!requireToken || tokenSet); }
    String offReason() { return !enabled ? "using blocks and items is off on this server (interact.enabled)" : "using blocks and items needs auth.token (interact.require-token)"; }

    /** Per-session buckets, set at hello together with the cap. */
    void grant(Session s) {
        s.canInteract = true;
        s.interactLimiter = new EditLimiter(rate, rate);
        s.holdLimiter = new EditLimiter(20, 20);
        s.spawnLimiter = new EditLimiter(spawnsPerMinute / 60.0, spawnsPerMinute);
    }

    void welcome(Session s, JsonObject w) {
        if (s.canInteract) w.add("interact", InteractMessages.welcomeInteract(flags, entities, harmPlayers, attackDamage, spawnsPerMinute));
    }

    JsonObject itemRegistryMessage() { return itemRegistry.deepCopy(); }

    /** The usable items and spawn eggs this server has (feature flags of the region's world), minus interact.deny-spawn. */
    void buildItemRegistry(World w) {
        long t0 = System.nanoTime();
        List<String> use = new ArrayList<>();
        for (String id : InteractRules.usable(flags, entities))
            if (id.equals(InteractRules.LIGHTNING) || itemExists(id)) use.add(id);
        List<String> egg = new ArrayList<>();
        for (EntityType t : EntityType.values()) {
            if (t == EntityType.UNKNOWN || !t.isSpawnable()) continue;
            try {
                if (w != null && !w.isEnabled(t)) continue;
                Material m = Bukkit.getItemFactory().getSpawnEgg(t);
                if (m == null || denySpawn.contains(t.getKey().asString())) continue;
                egg.add(m.getKey().asString());
            } catch (RuntimeException ignored) { /* a type without a key or egg */ }
        }
        egg.sort(String::compareTo);
        usable = Set.copyOf(use);
        eggs = Set.copyOf(egg);
        itemRegistry = InteractMessages.itemRegistry(use, egg);
        plugin.getLogger().info(String.format("ITEMS %d usable, %d spawn eggs (%.1f ms)", use.size(), egg.size(), (System.nanoTime() - t0) / 1e6));
    }

    static boolean itemExists(String id) {
        NamespacedKey k = NamespacedKey.fromString(id);
        return k != null && Registry.ITEM.get(k) != null;
    }

    /** An item the headset may use: one from item_registry (any axe, hoe or shovel counts as its tool class). */
    boolean offered(String item) {
        if (item == null) return true;
        if (usable.contains(item) || eggs.contains(item)) return true;
        String p = InteractRules.itemPath(item);
        return (p.endsWith("_axe") && usable.contains("minecraft:iron_axe")) || (p.endsWith("_hoe") && usable.contains("minecraft:iron_hoe"))
            || (p.endsWith("_shovel") && usable.contains("minecraft:iron_shovel"));
    }

    // ---------- interact (main thread) ----------

    void onInteract(Session s, InteractMessages.Interact in, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.canInteract) { deny(s, in, "interact_denied", allowed() ? "ask for interact_v1 in hello.caps" : offReason(), refSeq); return; }
        RegionSpec r = s.region;
        if (r == null || !r.containsWorld(in.x(), in.y(), in.z())) { deny(s, in, "out_of_region", "position is outside the table's region", refSeq); return; }
        World w = Bukkit.getWorld(r.world());
        if (w == null) { deny(s, in, "world_not_found", "no world " + r.world(), refSeq); return; }
        if (!w.isChunkLoaded(in.x() >> 4, in.z() >> 4)) { deny(s, in, "interact_denied", "chunk not loaded", refSeq); return; }
        if (!EditWorld.insideBorder(w, in.x(), in.y(), in.z())) { deny(s, in, "interact_denied", "outside the world border", refSeq); return; }
        if (!offered(in.item())) { deny(s, in, "interact_denied", in.item() + " isn't one of the server's usable items (item_registry)", refSeq); return; }
        long t0 = System.nanoTime();
        Block b = w.getBlockAt(in.x(), in.y(), in.z());
        String state = b.getBlockData().getAsString();
        InteractRules.Effect fx = in.item() == null
            ? InteractRules.emptyHand(state, in.face(), in.dir(), flags)
            : InteractRules.useItem(state, in.item(), in.face(), in.dir(), flags, (dx, dy, dz) -> cell(w, b, dx, dy, dz),
                                    id -> Registry.BLOCK.get(NamespacedKey.fromString(id)) != null, w.isUltraWarm());
        String why = fx.ok() ? apply(s, w, r, b, state, in, fx) : fx.denied();
        if (why != null) { deny(s, in, "interact_denied", why, refSeq); return; }
        interacts++;
        plugin.getLogger().info(String.format("INTERACT session=%s %s %s %d,%d,%d face=%s %s -> %s (%.2f ms)", s.id, in.item() == null ? "hand" : in.item(),
            EditRules.label(state), in.x(), in.y(), in.z(), in.face(), fx.extra() == InteractRules.Extra.NONE ? "" : fx.extra().name().toLowerCase(Locale.ROOT),
            b.getBlockData().getAsString(), (System.nanoTime() - t0) / 1e6));
    }

    private static InteractRules.Cell cell(World w, Block b, int dx, int dy, int dz) {
        int y = b.getY() + dy;
        if (y < w.getMinHeight() || y >= w.getMaxHeight()) return new InteractRules.Cell("minecraft:void_air", false);
        Block o = b.getRelative(dx, dy, dz);
        if (!w.isChunkLoaded(o.getX() >> 4, o.getZ() >> 4)) return new InteractRules.Cell("minecraft:barrier", false);
        BlockData d = o.getBlockData();
        return new InteractRules.Cell(d.getAsString(), d.isReplaceable());
    }

    /** Applies an allowed effect. Returns a refusal, or null when it happened. */
    private String apply(Session s, World w, RegionSpec r, Block b, String before, InteractMessages.Interact in, InteractRules.Effect fx) {
        String clickedId = BlockEdit.blockId(before);
        // Check every cell first, then change them (a refusal leaves the world as it was).
        List<Block> cells = new ArrayList<>();
        List<BlockData> datas = new ArrayList<>();
        for (InteractRules.Change c : fx.changes()) {
            Block t = b.getRelative(c.dx(), c.dy(), c.dz());
            boolean partner = (c.dx() != 0 || c.dy() != 0 || c.dz() != 0) && BlockEdit.blockId(c.state()).equals(clickedId);
            if (partner && !BlockEdit.blockId(t.getBlockData().getAsString()).equals(clickedId)) continue;   // a lone door half
            String bad = cellProblem(w, r, t);
            if (bad != null) return bad;
            BlockData d;
            try { d = Bukkit.createBlockData(c.state()); } catch (IllegalArgumentException e) { return "not a block state on this server: " + c.state(); }
            String id = BlockEdit.blockId(c.state());
            if (id.equals("minecraft:fire") || id.equals("minecraft:soul_fire")) {
                d = editWorld.shapeFromNeighbours(w, t, d);
                if (!d.isSupported(t)) return "fire can't burn there";
            }
            cells.add(t); datas.add(d);
        }
        int[] at = fx.at();
        Block target = at == null ? b : b.getRelative(at[0], at[1], at[2]);
        if (fx.extra() == InteractRules.Extra.SPAWN || fx.extra() == InteractRules.Extra.LIGHTNING) {
            String bad = cellProblem(w, r, target);
            if (bad != null) return bad;
        }
        // Extras that can still refuse before anything changed.
        EntityType spawn = null;
        switch (fx.extra()) {
            case SPAWN, SET_SPAWNER -> {
                spawn = spawnable(fx.arg());
                if (spawn == null) return fx.arg() + " can't be spawned from the headset (interact.deny-spawn)";
                if (fx.extra() == InteractRules.Extra.SET_SPAWNER && !(b.getState() instanceof CreatureSpawner)) return "not a spawner";
                if (fx.extra() == InteractRules.Extra.SPAWN && !s.spawnLimiter.tryAcquire(System.nanoTime()))
                    return "more than " + spawnsPerMinute + " spawns a minute (interact.max-spawns-per-minute)";
            }
            case EJECT -> { if (!(b.getState() instanceof Jukebox j) || !j.hasRecord()) return "the jukebox is empty"; }
            case BONE_MEAL -> {
                // Vanilla's BoneMealItem (crops, saplings, grass, flowers, seagrass...); it changes the world itself.
                BlockFace f = face(in.face());
                if (!b.applyBoneMeal(f == null ? BlockFace.UP : f)) return "bone meal does nothing to " + EditRules.label(before);
            }
            default -> { }
        }
        for (int i = 0; i < cells.size(); i++) {
            cells.get(i).setBlockData(datas.get(i), true);
            plugin.markDirty(w.getName(), cells.get(i).getX(), cells.get(i).getY(), cells.get(i).getZ());
        }
        Location centre = b.getLocation().add(0.5, 0.5, 0.5);
        if (fx.sound() != null) w.playSound(centre, "minecraft:" + fx.sound(), SoundCategory.BLOCKS, 1f, fx.pitch());
        switch (fx.extra()) {
            case NEIGHBOURS_BEHIND -> neighbours(w, b.getRelative(at[0], at[1], at[2]), b.getBlockData());
            case RELEASE_LATER -> {
                Block support = b.getRelative(at[0], at[1], at[2]);
                neighbours(w, support, b.getBlockData());
                String id = clickedId;
                Bukkit.getScheduler().runTaskLater(plugin, () -> {
                    String now = b.getBlockData().getAsString();
                    if (BlockEdit.blockId(now).equals(id) && "true".equals(EditRules.prop(now, "powered"))) {
                        b.tick();   // vanilla ButtonBlock#tick: up again, both sides updated
                        plugin.markDirty(w.getName(), b.getX(), b.getY(), b.getZ());
                    }
                }, fx.count());
            }
            case REFRESH -> b.tick();   // ComparatorBlock#tick refreshes its output for the new mode
            case NOTE -> {
                if (b.getBlockData() instanceof NoteBlock nb && b.getRelative(BlockFace.UP).getType().isAir()) {
                    w.playNote(b.getLocation(), nb.getInstrument(), nb.getNote());
                    w.spawnParticle(Particle.NOTE, b.getLocation().add(0.5, 1.2, 0.5), 0, nb.getNote().getId() / 24.0, 0, 0, 1);
                }
            }
            case BELL -> {
                if (b.getState() instanceof Bell bell) {
                    BlockFace f = face(fx.arg());
                    if (f == null || f == BlockFace.UP || f == BlockFace.DOWN || !bell.ring(f)) bell.ring();
                }
            }
            case EJECT -> { if (b.getState() instanceof Jukebox j) j.eject(); }
            case DROP -> drop(w, target, fx.arg(), fx.count());
            case PRIME_TNT -> {
                w.spawn(b.getLocation().add(0.5, 0, 0.5), TNTPrimed.class, t -> t.setFuseTicks(fx.count()));
            }
            case BONE_MEAL -> {
                w.spawnParticle(Particle.HAPPY_VILLAGER, centre, 12, 0.4, 0.4, 0.4, 0);
                markAround(w, b, 3);
            }
            case SPAWN -> {
                // Vanilla SpawnEggItem: into the clicked cell when it has no collision (grass), else in front of the face.
                Block cell = b.getCollisionShape().getBoundingBoxes().isEmpty() ? b : target;
                Location loc = cell.getLocation().add(0.5, 0, 0.5);
                loc.setYaw((float) (Math.random() * 360 - 180));
                Entity e = w.spawnEntity(loc, spawn, CreatureSpawnEvent.SpawnReason.SPAWNER_EGG);
                plugin.getLogger().info("SPAWN session=" + s.id + " " + spawn.getKey() + " " + e.getUniqueId());
            }
            case SET_SPAWNER -> {
                if (b.getState() instanceof CreatureSpawner cs) { cs.setSpawnedType(spawn); cs.update(true); plugin.markDirty(w.getName(), b.getX(), b.getY(), b.getZ()); }
            }
            case LIGHTNING -> {
                Location loc = target.getLocation().add(0.5, 0, 0.5);
                if ("effect".equals(fx.arg())) w.strikeLightningEffect(loc); else w.strikeLightning(loc);
                markAround(w, target, 2);   // fire it lights
            }
            default -> { }
        }
        return null;
    }

    /** Why a cell can't change: outside the region, the build height, the border, or an unloaded chunk; null if fine. */
    private static String cellProblem(World w, RegionSpec r, Block t) {
        if (!r.containsWorld(t.getX(), t.getY(), t.getZ()) || t.getY() < w.getMinHeight() || t.getY() >= w.getMaxHeight()) return "that would change a block outside the table's region";
        if (!w.isChunkLoaded(t.getX() >> 4, t.getZ() >> 4)) return "chunk not loaded";
        if (!EditWorld.insideBorder(w, t.getX(), t.getY(), t.getZ())) return "outside the world border";
        return null;
    }

    private void markAround(World w, Block b, int rad) {
        for (int dx = -rad; dx <= rad; dx += rad) for (int dy = -rad; dy <= rad; dy += rad) for (int dz = -rad; dz <= rad; dz += rad)
            plugin.markDirty(w.getName(), b.getX() + dx, b.getY() + dy, b.getZ() + dz);
    }

    private static BlockFace face(String f) {
        if (f == null) return null;
        try { return BlockFace.valueOf(f.toUpperCase(Locale.ROOT)); } catch (IllegalArgumentException e) { return null; }
    }

    private static void drop(World w, Block at, String item, int count) {
        NamespacedKey k = NamespacedKey.fromString(item);
        ItemType t = k == null ? null : Registry.ITEM.get(k);
        if (t != null) w.dropItemNaturally(at.getLocation().add(0.5, 0.5, 0.5), t.createItemStack(Math.max(1, count)));
    }

    /** The entity type of a spawn egg's id, if it is offered and allowed here. */
    private EntityType spawnable(String typeId) {
        NamespacedKey k = NamespacedKey.fromString(typeId);
        EntityType t = k == null ? null : Registry.ENTITY_TYPE.get(k);
        if (t == null || !t.isSpawnable() || denySpawn.contains(typeId)) return null;
        Material egg = Bukkit.getItemFactory().getSpawnEgg(t);
        return egg != null && eggs.contains(egg.getKey().asString()) ? t : null;
    }

    private void deny(Session s, InteractMessages.Interact in, String code, String why, Long refSeq) {
        refused++;
        plugin.getLogger().info(String.format("INTERACT_DENIED session=%s %s %d,%d,%d %s: %s", s.id, in.item() == null ? "hand" : in.item(), in.x(), in.y(), in.z(), code, why));
        plugin.send(s, in.error(code, why, refSeq));
    }

    // ---------- the neighbour bridge (levers and buttons) ----------

    private boolean bridgeTried, bridgeOk;
    private Method getHandle, getNmsState, getNmsBlock, updateNeighbors;
    private Constructor<?> blockPos;

    /**
     * Vanilla's {@code Level#updateNeighborsAt(pos, block)} for the block a lever or button hangs on: Paper has no API
     * for neighbour updates at another position. One reflective call on the unobfuscated 26.3 server, logged once
     * and skipped (redstone behind the support block then waits for the next update) if it ever fails.
     */
    private void neighbours(World w, Block at, BlockData source) {
        if (!bridgeReady()) return;
        try {
            Object block = getNmsBlock.invoke(getNmsState.invoke(source));
            updateNeighbors.invoke(getHandle.invoke(w), blockPos.newInstance(at.getX(), at.getY(), at.getZ()), block);
        } catch (ReflectiveOperationException | RuntimeException | LinkageError ex) {
            bridgeOk = false;
            plugin.getLogger().warning("INTERACT_NEIGHBOURS off: redstone behind a lever's or button's block updates later (" + ex + ")");
        }
    }

    private boolean bridgeReady() {
        if (bridgeTried) return bridgeOk;
        bridgeTried = true;
        try {
            ClassLoader cl = Bukkit.getServer().getClass().getClassLoader();
            Class<?> craftData = Class.forName("org.bukkit.craftbukkit.block.data.CraftBlockData", true, cl);
            Class<?> nmsState = Class.forName("net.minecraft.world.level.block.state.BlockState", true, cl);
            Class<?> nmsBlock = Class.forName("net.minecraft.world.level.block.Block", true, cl);
            Class<?> level = Class.forName("net.minecraft.world.level.Level", true, cl);
            Class<?> pos = Class.forName("net.minecraft.core.BlockPos", true, cl);
            getHandle = Class.forName("org.bukkit.craftbukkit.CraftWorld", true, cl).getMethod("getHandle");
            getNmsState = craftData.getMethod("getState");
            getNmsBlock = nmsState.getMethod("getBlock");
            updateNeighbors = level.getMethod("updateNeighborsAt", pos, nmsBlock);
            blockPos = pos.getConstructor(int.class, int.class, int.class);
            bridgeOk = true;
            plugin.getLogger().info("INTERACT_NEIGHBOURS on: levers and buttons update the block they hang on (vanilla Level#updateNeighborsAt)");
        } catch (ReflectiveOperationException | RuntimeException | LinkageError ex) {
            bridgeOk = false;
            plugin.getLogger().warning("INTERACT_NEIGHBOURS off: redstone behind a lever's or button's block updates later (" + ex + ")");
        }
        return bridgeOk;
    }

    // ---------- entity_op (main thread) ----------

    void onEntityOp(Session s, InteractMessages.EntityOp op, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.canInteract) { denyOp(s, op, "interact_denied", allowed() ? "ask for interact_v1 in hello.caps" : offReason(), refSeq); return; }
        if (!entities) { denyOp(s, op, "interact_denied", "acting on mobs is off on this server (interact.entities)", refSeq); return; }
        Entity e = Bukkit.getEntity(op.uuid());
        if (e == null || !e.isValid() || e.getScoreboardTags().contains(GiantAvatar.TAG)) {
            if (op.op().equals("drop")) restore(op.uuid());
            denyOp(s, op, "not_found", "no such entity (it may have died or left)", refSeq);
            return;
        }
        RegionSpec r = s.region;
        if (r == null || !nearRegion(r, e)) { if (op.op().equals("drop")) restore(op.uuid()); denyOp(s, op, "out_of_region", "the entity is outside the table's region", refSeq); return; }
        String why = entityOp(s, e, r, op, refSeq);
        if (why != null) { denyOp(s, op, "interact_denied", why, refSeq); return; }
        entityOps++;
        if (!op.op().equals("hold") && !op.op().equals("info"))
            plugin.getLogger().info(String.format("ENTITY session=%s %s %s %s%s", s.id, op.op(), e.getType().getKey().getKey(), e.getUniqueId(),
                op.item() != null ? " item=" + op.item() : ""));
    }

    private boolean nearRegion(RegionSpec r, Entity e) {
        Location l = e.getLocation();
        int m = entityMargin;
        return l.getWorld().getName().equals(r.world()) && l.getX() >= r.ox() - m && l.getX() < r.ox() + r.sx() + m
            && l.getY() >= r.oy() - m && l.getY() < r.oy() + r.sy() + m && l.getZ() >= r.oz() - m && l.getZ() < r.oz() + r.sz() + m;
    }

    private String entityOp(Session s, Entity e, RegionSpec r, InteractMessages.EntityOp op, Long refSeq) {
        boolean player = e instanceof Player;
        String label = EditRules.article(e.getType().getKey().getKey().replace('_', ' '));
        switch (op.op()) {
            case "info" -> { plugin.send(s, InteractMessages.entityInfo(refSeq, info(e))); return null; }
            case "attack" -> {
                if (player && !harmPlayers) return "players can't be harmed from the headset (interact.harm-players)";
                if (!(e instanceof Damageable d)) return label + " can't be hurt (kill or remove it)";
                d.damage(attackDamage, DamageSource.builder(DamageType.GENERIC).build());
                if (e instanceof LivingEntity && op.dir() != null) {
                    double len = Math.hypot(op.dir()[0], op.dir()[2]);
                    if (len > 1e-6) {
                        Vector v = e.getVelocity();
                        e.setVelocity(new Vector(v.getX() / 2 + op.dir()[0] / len * 0.4, e.isOnGround() ? Math.min(0.4, v.getY() / 2 + 0.4) : v.getY(), v.getZ() / 2 + op.dir()[2] / len * 0.4));
                    }
                }
                return null;
            }
            case "kill" -> {
                if (player && !harmPlayers) return "players can't be harmed from the headset (interact.harm-players)";
                if (e instanceof Damageable d) d.kill(DamageSource.builder(DamageType.GENERIC_KILL).build());
                else e.remove();
                return null;
            }
            case "remove" -> {
                if (player) return "players can't be removed";
                restore(e.getUniqueId());
                e.remove();
                return null;
            }
            case "use" -> { return use(s, e, op.item()); }
            case "name" -> {
                if (player) return "players keep their own names";
                String name = InteractMessages.cleanName(op.text());
                e.customName(name.isEmpty() ? null : Component.text(name));
                if (e instanceof Mob m && !name.isEmpty()) m.setRemoveWhenFarAway(false);   // like a name tag: never despawns
                return null;
            }
            case "move" -> { return teleport(e, r, op, true); }
            case "hold" -> {
                if (player) return "players can't be held; let go to move them";
                Held h = held.get(e.getUniqueId());
                if (h == null) {
                    h = new Held();
                    h.s = s; h.gravity = e.hasGravity(); h.ai = e instanceof LivingEntity le ? le.hasAI() : null;
                    held.put(e.getUniqueId(), h);
                    e.setGravity(false);
                    if (e instanceof LivingEntity le) le.setAI(false);
                }
                h.lastMs = System.currentTimeMillis();
                e.setVelocity(new Vector());
                String bad = teleport(e, r, op, false);
                if (bad != null) { restore(e.getUniqueId()); return bad; }
                return null;
            }
            case "drop" -> {
                String bad = op.to() == null ? null : teleport(e, r, op, false);
                restore(e.getUniqueId());
                if (!player) e.setVelocity(new Vector());
                return bad;
            }
            case "freeze", "unfreeze" -> {
                if (!(e instanceof Mob m)) return label + " has no AI to freeze";
                boolean freeze = op.op().equals("freeze");
                m.setAware(!freeze);
                if (freeze) frozen.add(e.getUniqueId()); else frozen.remove(e.getUniqueId());
                saveFrozen();
                return null;
            }
            case "glow" -> { e.setGlowing(!e.isGlowing()); return null; }
            default -> { return "unknown op " + op.op(); }
        }
    }

    /** Teleports to op.to (which must be in the region), keeping passengers; players by TeleportCause.PLUGIN. */
    private String teleport(Entity e, RegionSpec r, InteractMessages.EntityOp op, boolean turn) {
        double[] to = op.to();
        if (!r.containsWorld((int) Math.floor(to[0]), (int) Math.floor(to[1]), (int) Math.floor(to[2]))) return "that's outside the table's region";
        World w = Bukkit.getWorld(r.world());
        if (w == null) return "no world " + r.world();
        Location loc = new Location(w, to[0], to[1], to[2], turn && op.yaw() != null ? op.yaw() : e.getYaw(), e.getPitch());
        if (e.isInsideVehicle()) e.leaveVehicle();
        // Paper 26.x keeps passengers on a teleport by itself (TeleportFlag.EntityState is deprecated for removal).
        boolean ok = e.teleport(loc, PlayerTeleportEvent.TeleportCause.PLUGIN);
        return ok ? null : "the teleport was refused (another plugin?)";
    }

    /** An item used on a mob: shears, a breeding food, a spawn egg (same type: a baby), a lead, the lightning wand. */
    private String use(Session s, Entity e, String item) {
        String label = EditRules.article(e.getType().getKey().getKey().replace('_', ' '));
        if (item == null) return null;
        if (!offered(item)) return item + " isn't one of the server's usable items (item_registry)";
        World w = e.getWorld();
        if (item.equals(InteractRules.LIGHTNING)) {
            if (!flags.lightningOn()) return "lightning is off on this server (interact.lightning)";
            if (e instanceof Player && !harmPlayers && "real".equals(flags.lightning())) return "players can't be harmed from the headset (interact.harm-players)";
            if ("effect".equals(flags.lightning())) w.strikeLightningEffect(e.getLocation()); else w.strikeLightning(e.getLocation());
            return null;
        }
        String egg = InteractRules.eggType(item);
        if (egg != null) {
            EntityType t = spawnable(egg);
            if (t == null) return egg + " can't be spawned from the headset (interact.deny-spawn)";
            if (!s.spawnLimiter.tryAcquire(System.nanoTime())) return "more than " + spawnsPerMinute + " spawns a minute (interact.max-spawns-per-minute)";
            Entity n = w.spawnEntity(e.getLocation(), t, CreatureSpawnEvent.SpawnReason.SPAWNER_EGG);
            if (t == e.getType() && n instanceof Ageable a) a.setBaby();   // vanilla: an egg of the same mob makes a baby
            return null;
        }
        String p = InteractRules.itemPath(item);
        switch (p) {
            case "shears" -> {
                if (e instanceof io.papermc.paper.entity.Shearable sh) {
                    if (!sh.readyToBeSheared()) return label + " isn't ready to be sheared";
                    sh.shear();
                    return null;
                }
                return label + " can't be sheared";
            }
            case "name_tag" -> { return "type the name tag's text in the menu first"; }
            case "lead" -> { return lead(e, label); }
            default -> { }
        }
        if (e instanceof Animals a) {
            Material m = Material.matchMaterial(item);
            if (m == null || !a.isBreedItem(m)) return label + " doesn't eat " + p.replace('_', ' ');
            if (a instanceof Ageable ag && !ag.isAdult()) { ag.setAge(Math.min(0, ag.getAge() + 2400)); return null; }   // babies grow faster
            if (a instanceof Ageable ag && !ag.canBreed()) return label + " can't breed right now";
            a.setLoveModeTicks(600);
            w.spawnParticle(Particle.HEART, e.getLocation().add(0, e.getHeight(), 0), 5, 0.3, 0.3, 0.3, 0);
            return null;
        }
        return "nothing happens when " + p.replace('_', ' ') + " is used on " + label;
    }

    /** Leashed: let go. Else tie it to the nearest fence within 7 blocks (a leash knot), as a player would. */
    private String lead(Entity e, String label) {
        boolean leashed = e instanceof io.papermc.paper.entity.Leashable l ? l.isLeashed() : e instanceof LivingEntity le && le.isLeashed();
        if (leashed) {
            if (e instanceof io.papermc.paper.entity.Leashable l) l.setLeashHolder(null); else ((LivingEntity) e).setLeashHolder(null);
            return null;
        }
        if (!(e instanceof io.papermc.paper.entity.Leashable) && !(e instanceof LivingEntity)) return label + " can't be leashed";
        Block best = null;
        double bestD = Double.MAX_VALUE;
        Location l = e.getLocation();
        for (int dx = -7; dx <= 7; dx++) for (int dy = -4; dy <= 4; dy++) for (int dz = -7; dz <= 7; dz++) {
            Block b = l.getBlock().getRelative(dx, dy, dz);
            if (!Tag.FENCES.isTagged(b.getType())) continue;
            double d = b.getLocation().add(0.5, 0.5, 0.5).distanceSquared(l);
            if (d < bestD) { bestD = d; best = b; }
        }
        if (best == null) return "no fence within 7 blocks to tie " + label + " to";
        LeashHitch knot = e.getWorld().spawn(best.getLocation(), LeashHitch.class);
        boolean ok = e instanceof io.papermc.paper.entity.Leashable l2 ? l2.setLeashHolder(knot) : ((LivingEntity) e).setLeashHolder(knot);
        if (!ok) { knot.remove(); return label + " can't be leashed"; }
        return null;
    }

    private InteractMessages.Info info(Entity e) {
        Location l = e.getLocation();
        double[] health = e instanceof LivingEntity le ? new double[] { le.getHealth(), maxHealth(le) } : null;
        String owner = e instanceof Tameable t && t.getOwner() != null ? t.getOwner().getName() : null;
        Integer age = e instanceof Ageable a ? a.getAge() : null;
        boolean baby = e instanceof Ageable a && !a.isAdult();
        Boolean ai = e instanceof LivingEntity le && !(e instanceof Player) ? le.hasAI() : null;
        Boolean frozenNow = e instanceof Mob m ? !m.isAware() : null;
        boolean leashed = e instanceof LivingEntity le && le.isLeashed();
        String name = e instanceof Player p ? p.getName() : e.customName() == null ? null : net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer.plainText().serialize(e.customName());
        return new InteractMessages.Info(e.getUniqueId().toString(), e.getEntityId(), e.getType().getKey().asString(), name,
            new double[] { l.getX(), l.getY(), l.getZ() }, health, owner, age, baby, ai, frozenNow, e.isGlowing(), held.containsKey(e.getUniqueId()),
            leashed, e instanceof Player, e.getVehicle() == null ? null : e.getVehicle().getType().getKey().asString(), e.getPassengers().size());
    }

    private static double maxHealth(LivingEntity le) {
        var a = le.getAttribute(org.bukkit.attribute.Attribute.MAX_HEALTH);
        return a != null ? a.getValue() : le.getHealth();
    }

    private void denyOp(Session s, InteractMessages.EntityOp op, String code, String why, Long refSeq) {
        refused++;
        plugin.getLogger().info(String.format("ENTITY_DENIED session=%s %s %s %s: %s", s.id, op.op(), op.uuid(), code, why));
        plugin.send(s, op.error(code, why, refSeq));
    }

    // ---------- held entities ----------

    /** Gives a held entity its gravity and AI back. */
    void restore(UUID id) {
        Held h = held.remove(id);
        if (h == null) return;
        Entity e = Bukkit.getEntity(id);
        if (e == null) return;
        e.setGravity(h.gravity);
        if (h.ai != null && e instanceof LivingEntity le) le.setAI(h.ai);
    }

    /** Each tick: holds that got no update for 3 s are dropped where they are. */
    void tick() {
        if (held.isEmpty()) return;
        long now = System.currentTimeMillis();
        for (UUID id : new ArrayList<>(held.keySet())) {
            Held h = held.get(id);
            if (now - h.lastMs > HOLD_TIMEOUT_MS || h.s.state != Session.State.READY) restore(id);
        }
    }

    void onSessionClosed(Session s) {
        for (UUID id : new ArrayList<>(held.keySet())) if (held.get(id).s == s) restore(id);
    }

    void shutdown() {
        for (UUID id : new ArrayList<>(held.keySet())) restore(id);
    }

    // ---------- frozen mobs ----------

    /** /tabletop unfreeze-all: loaded mobs now, the others when their chunk loads. Returns {unfrozen, pending}. */
    int[] unfreezeAll() {
        int done = 0;
        for (UUID id : new ArrayList<>(frozen)) {
            Entity e = Bukkit.getEntity(id);
            if (e instanceof Mob m) { m.setAware(true); frozen.remove(id); done++; }
            else unfreezePending.add(id);
        }
        saveFrozen();
        return new int[] { done, unfreezePending.size() };
    }

    @EventHandler
    public void onEntitiesLoad(EntitiesLoadEvent ev) {
        if (unfreezePending.isEmpty()) return;
        boolean changed = false;
        for (Entity e : ev.getEntities())
            if (unfreezePending.remove(e.getUniqueId()) && e instanceof Mob m) { m.setAware(true); frozen.remove(e.getUniqueId()); changed = true; }
        if (changed) saveFrozen();
    }

    int frozenCount() { return frozen.size(); }

    private void loadFrozen() {
        frozen.clear();
        try {
            if (frozenFile != null && Files.exists(frozenFile))
                for (String line : Files.readAllLines(frozenFile)) {
                    try { if (!line.isBlank()) frozen.add(UUID.fromString(line.trim())); } catch (IllegalArgumentException ignored) { }
                }
        } catch (IOException e) { plugin.getLogger().warning("frozen.txt: " + e); }
    }

    private void saveFrozen() {
        if (frozenFile == null) return;
        try {
            Files.createDirectories(frozenFile.getParent());
            List<String> lines = new ArrayList<>();
            for (UUID id : frozen) lines.add(id.toString());
            Files.write(frozenFile, lines);
        } catch (IOException e) { plugin.getLogger().warning("frozen.txt: " + e); }
    }

    String stats() {
        return String.format(Locale.ROOT, "interact %s: %d uses, %d mob actions, %d refused, %d held, %d frozen", allowed() ? "on" : "off (" + offReason() + ")",
            interacts, entityOps, refused, held.size(), frozen.size());
    }
}
