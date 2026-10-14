package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.data.BlockData;
import org.bukkit.block.data.Waterlogged;
import org.bukkit.block.data.type.Leaves;
import org.bukkit.entity.ArmorStand;
import org.bukkit.entity.Boat;
import org.bukkit.entity.EnderCrystal;
import org.bukkit.entity.Entity;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Minecart;
import org.bukkit.entity.Player;
import org.bukkit.util.BoundingBox;

import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.Collection;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.UUID;
import java.util.function.Supplier;
import java.util.logging.Logger;

/**
 * Applies a block_edit to the world on the main thread (PROTOCOL.md 4.20), after TabletopPlugin#onBlockEdit's session,
 * region and registry checks, the way vanilla 26.3 places and breaks blocks:
 * <ul>
 *   <li>a place needs a replaceable cell ({@link EditRules#replaceable}) inside the world border, and a block with a
 *       collision box must not go into a player or mob (the giant avatar's own entities don't count);</li>
 *   <li>doors, beds and tall plants get both halves (the second cell must be in the region and replaceable); breaking
 *       either half removes both, without drops; replacing the lower half of tall grass removes its top too;</li>
 *   <li>leaves are placed persistent; {@code waterlogged} is whether the cell held a water source;</li>
 *   <li>a single block's own connections (fences, panes, walls, bars, stairs, redstone, snowy grass) are computed from
 *       its neighbours like vanilla's getStateForPlacement, through vanilla's
 *       {@code Block.updateFromNeighbourShapes} (Paper has no API for it; called reflectively on the unobfuscated
 *       server, logged once and skipped if that ever fails).</li>
 * </ul>
 */
final class EditWorld {
    /** A refused edit: the error code and a readable message. */
    record Denied(String code, String message) {}

    /** What happened: {@code denied} or the cells that changed (world coordinates) and the main cell's before/after. */
    record Outcome(Denied denied, String before, String after, List<int[]> cells) {
        static Outcome deny(String code, String message) { return new Outcome(new Denied(code, message), null, null, List.of()); }
    }

    private final Logger log;
    private final Supplier<Collection<Entity>> avatarEntities;
    private boolean shapesTried, shapesOk;
    private Method getState, createData, getHandle, update;
    private java.lang.reflect.Constructor<?> blockPos;

    EditWorld(Logger log, Supplier<Collection<Entity>> avatarEntities) {
        this.log = log;
        this.avatarEntities = avatarEntities;
    }

    static boolean insideBorder(World w, int x, int y, int z) {
        return w.getWorldBorder().isInside(new Location(w, x + 0.5, y, z + 0.5));
    }

    /** Places {@code next} (already parsed and checked against the registry and edit.deny) at the edit's position. */
    Outcome place(World w, RegionSpec r, BlockEdit e, BlockData next, boolean physics) {
        if (!insideBorder(w, e.x(), e.y(), e.z())) return Outcome.deny("edit_denied", "outside the world border");
        Block b = w.getBlockAt(e.x(), e.y(), e.z());
        BlockData old = b.getBlockData();
        String oldS = old.getAsString();
        // Two-block structures are placed by their lower half or foot (a picked top half places the whole door).
        String asked = next.getAsString(), base = EditRules.placedHalf(asked);
        if (!base.equals(asked)) next = Bukkit.createBlockData(base);
        String label = EditRules.label(asked);
        if (!EditRules.replaceable(oldS, old.isReplaceable(), base)) return Outcome.deny("edit_denied", EditRules.label(oldS) + " is in the way");
        if (next instanceof Leaves lv) lv.setPersistent(true);   // placed leaves never decay
        if (next instanceof Waterlogged wl) wl.setWaterlogged(EditRules.holdsWaterSource(oldS));
        // Vanilla's BlockItem#canPlace: the block must be able to stay there (a torch needs a block under or behind it,
        // grass needs dirt, a door a floor). Without this, physics would pop it straight away or leave it floating.
        if (!next.isSupported(b)) return Outcome.deny("edit_denied", label + " can't stay there");

        Block b2 = null;
        BlockData old2 = null, next2 = null;
        EditRules.Partner p = EditRules.partner(next.getAsString());
        if (p != null) {
            int x2 = e.x() + p.dx(), y2 = e.y() + p.dy(), z2 = e.z() + p.dz();
            if (!r.containsWorld(x2, y2, z2) || y2 < w.getMinHeight() || y2 >= w.getMaxHeight())
                return Outcome.deny("edit_denied", "no room for the " + label + " (its other half would be outside the area)");
            if (!w.isChunkLoaded(x2 >> 4, z2 >> 4)) return Outcome.deny("edit_denied", "chunk not loaded");
            if (!insideBorder(w, x2, y2, z2)) return Outcome.deny("edit_denied", "no room for the " + label + " (outside the world border)");
            b2 = w.getBlockAt(x2, y2, z2);
            old2 = b2.getBlockData();
            if (!EditRules.replaceable(old2.getAsString(), old2.isReplaceable(), base))
                return Outcome.deny("edit_denied", "no room for the " + label + ": " + EditRules.label(old2.getAsString()) + " is in the way");
            next2 = Bukkit.createBlockData(p.state());
            if (next2 instanceof Waterlogged wl2) wl2.setWaterlogged(EditRules.holdsWaterSource(old2.getAsString()));
        }
        String blocker = blocker(w, b, next);
        if (blocker == null && b2 != null) blocker = blocker(w, b2, next2);
        if (blocker != null) return Outcome.deny("edit_denied", blocker + " is in the way");
        if (p == null) next = shapeFromNeighbours(w, b, next);

        List<int[]> cells = new ArrayList<>(4);
        // Replacing one half of tall grass or a large fern: its other half goes too (vanilla pops it).
        clearPartner(w, b, oldS, b2, cells);
        if (b2 != null) clearPartner(w, b2, old2.getAsString(), b, cells);
        b.setBlockData(next, physics);
        cells.add(new int[] { b.getX(), b.getY(), b.getZ() });
        if (b2 != null) { b2.setBlockData(next2, physics); cells.add(new int[] { b2.getX(), b2.getY(), b2.getZ() }); }
        return new Outcome(null, oldS, b.getBlockData().getAsString(), cells);
    }

    /** Breaks the block at the edit's position (a block that holds water leaves water), and the other half of a pair. */
    Outcome breakBlock(World w, BlockEdit e, boolean physics) {
        if (!insideBorder(w, e.x(), e.y(), e.z())) return Outcome.deny("edit_denied", "outside the world border");
        Block b = w.getBlockAt(e.x(), e.y(), e.z());
        BlockData old = b.getBlockData();
        if (old.getMaterial().isAir()) return new Outcome(null, old.getAsString(), old.getAsString(), List.of());
        List<int[]> cells = new ArrayList<>(2);
        // The other half first, without physics: no drops, and the main cell's update then finds it gone.
        clearPartner(w, b, old.getAsString(), null, cells);
        b.setBlockData(Bukkit.createBlockData(EditRules.holdsWaterSource(old.getAsString()) && old.getMaterial() != Material.WATER ? Material.WATER : Material.AIR), physics);
        cells.add(new int[] { b.getX(), b.getY(), b.getZ() });
        return new Outcome(null, old.getAsString(), b.getBlockData().getAsString(), cells);
    }

    /** Removes the other half of the two-block structure {@code state} at {@code b} (unless it's {@code keep}). */
    private void clearPartner(World w, Block b, String state, Block keep, List<int[]> cells) {
        EditRules.Partner p = EditRules.partner(state);
        if (p == null) return;
        int x = b.getX() + p.dx(), y = b.getY() + p.dy(), z = b.getZ() + p.dz();
        if (y < w.getMinHeight() || y >= w.getMaxHeight() || !w.isChunkLoaded(x >> 4, z >> 4)) return;
        if (keep != null && keep.getX() == x && keep.getY() == y && keep.getZ() == z) return;
        Block o = w.getBlockAt(x, y, z);
        String there = o.getBlockData().getAsString();
        EditRules.Partner back = EditRules.partner(there);
        if (!BlockEdit.blockId(there).equals(BlockEdit.blockId(state)) || back == null || back.dx() != -p.dx() || back.dy() != -p.dy() || back.dz() != -p.dz()) return;
        o.setBlockData(Bukkit.createBlockData(EditRules.holdsWaterSource(there) ? "minecraft:water[level=0]" : "minecraft:air"), false);
        cells.add(new int[] { x, y, z });
    }

    /**
     * Who would end up inside the block: a living entity (not a spectator, dead mob or marker armour stand), a boat,
     * a minecart or an end crystal (vanilla's Entity#blocksBuilding) whose box overlaps the block's collision shape.
     * Null when nothing is in the way (blocks without collision, like torches and flowers, never are).
     */
    String blocker(World w, Block b, BlockData data) {
        Collection<BoundingBox> shape;
        try { shape = data.getCollisionShape(b.getLocation()).getBoundingBoxes(); }
        catch (RuntimeException ex) { shape = List.of(new BoundingBox(0, 0, 0, 1, 1, 1)); }
        if (shape.isEmpty()) return null;
        List<BoundingBox> boxes = new ArrayList<>(shape.size());
        BoundingBox all = null;
        for (BoundingBox bb : shape) {
            BoundingBox s = bb.clone().shift(b.getX(), b.getY(), b.getZ());
            boxes.add(s);
            all = all == null ? s.clone() : all.union(s);
        }
        Set<UUID> giant = new HashSet<>();
        for (Entity a : avatarEntities.get()) giant.add(a.getUniqueId());
        for (Entity en : w.getNearbyEntities(all)) {
            if (giant.contains(en.getUniqueId()) || !blocksBuilding(en)) continue;
            BoundingBox eb = en.getBoundingBox();
            for (BoundingBox s : boxes)
                if (s.overlaps(eb)) return en instanceof Player pl ? pl.getName() : EditRules.article(en.getType().getKey().getKey().replace('_', ' '));
        }
        return null;
    }

    static boolean blocksBuilding(Entity en) {
        if (en instanceof Player pl) return pl.getGameMode() != GameMode.SPECTATOR && !pl.isDead();
        if (en instanceof ArmorStand as) return !as.isMarker();
        if (en instanceof LivingEntity le) return !le.isDead();
        return en instanceof Boat || en instanceof Minecart || en instanceof EnderCrystal;
    }

    /**
     * The state with its connections computed from the neighbours, as vanilla's getStateForPlacement does for fences,
     * panes, walls, bars, stairs (shape), redstone wire, snowy grass...: {@code Block.updateFromNeighbourShapes}. Only a
     * result of the same block is used (a torch without support would come back as air: the given state is kept).
     */
    BlockData shapeFromNeighbours(World w, Block b, BlockData data) {
        if (!shapesReady()) return data;
        try {
            Object state = getState.invoke(data);
            Object shaped = update.invoke(null, state, getHandle.invoke(w), blockPos.newInstance(b.getX(), b.getY(), b.getZ()));
            BlockData out = (BlockData) createData.invoke(null, shaped);
            return out.getMaterial() == data.getMaterial() ? out : data;
        } catch (ReflectiveOperationException | RuntimeException | LinkageError ex) {
            shapesOk = false;
            log.warning("EDIT_SHAPES off: placed blocks keep the state the headset sent (" + ex + ")");
            return data;
        }
    }

    private boolean shapesReady() {
        if (shapesTried) return shapesOk;
        shapesTried = true;
        try {
            ClassLoader cl = Bukkit.getServer().getClass().getClassLoader();
            Class<?> craftData = Class.forName("org.bukkit.craftbukkit.block.data.CraftBlockData", true, cl);
            Class<?> nmsState = Class.forName("net.minecraft.world.level.block.state.BlockState", true, cl);
            Class<?> nmsBlock = Class.forName("net.minecraft.world.level.block.Block", true, cl);
            Class<?> pos = Class.forName("net.minecraft.core.BlockPos", true, cl);
            getState = craftData.getMethod("getState");
            createData = craftData.getMethod("createData", nmsState);
            getHandle = Class.forName("org.bukkit.craftbukkit.CraftWorld", true, cl).getMethod("getHandle");
            blockPos = pos.getConstructor(int.class, int.class, int.class);
            for (Method m : nmsBlock.getMethods())
                if (m.getName().equals("updateFromNeighbourShapes") && m.getParameterCount() == 3 && m.getParameterTypes()[0] == nmsState && m.getParameterTypes()[2] == pos) update = m;
            if (update == null) throw new NoSuchMethodException("Block.updateFromNeighbourShapes(BlockState, LevelAccessor, BlockPos)");
            shapesOk = true;
            log.info("EDIT_SHAPES on: placed blocks connect to their neighbours (vanilla Block.updateFromNeighbourShapes)");
        } catch (ReflectiveOperationException | RuntimeException | LinkageError ex) {
            shapesOk = false;
            log.warning("EDIT_SHAPES off: placed blocks keep the state the headset sent (" + ex + ")");
        }
        return shapesOk;
    }
}
