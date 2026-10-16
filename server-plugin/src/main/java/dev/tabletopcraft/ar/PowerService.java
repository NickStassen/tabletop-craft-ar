package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import org.bukkit.Bukkit;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.BlockState;
import org.bukkit.Location;
import org.bukkit.block.Chest;
import org.bukkit.block.Container;
import org.bukkit.block.Sign;
import org.bukkit.block.TileState;
import org.bukkit.block.sign.Side;
import org.bukkit.block.sign.SignSide;
import org.bukkit.inventory.BlockInventoryHolder;
import org.bukkit.inventory.DoubleChestInventory;
import org.bukkit.block.data.BlockData;
import org.bukkit.block.data.Levelled;
import org.bukkit.block.data.MultipleFacing;
import org.bukkit.block.data.Rail;
import org.bukkit.block.data.Snowable;
import org.bukkit.block.data.Waterlogged;
import org.bukkit.block.data.type.Gate;
import org.bukkit.block.data.type.Leaves;
import org.bukkit.block.data.type.RedstoneWire;
import org.bukkit.block.data.type.Stairs;
import org.bukkit.block.data.type.Wall;
import org.bukkit.block.structure.StructureRotation;
import org.bukkit.configuration.file.FileConfiguration;

import java.lang.reflect.Constructor;
import java.lang.reflect.Method;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;

/**
 * [power] Undo/redo, box edits and brushes for the headset (PROTOCOL.md "Undo and redo", "Box edits", "Brushes"; cap
 * power_v1, config.yml {@code edit}). Messages are parsed and rate-limited on the websocket thread (LinkServer);
 * everything here runs on the main thread.
 * <ul>
 *   <li>Single edits ({@code block_edit}, {@code interact}) are journalled by looking at the cells around them before and
 *       after the edit runs (3×3×3, so both halves of a door or bed and the neighbours whose connections changed; a
 *       bone meal use looks 7×14×7 for what grew). Block entities in that window are snapshotted first
 *       ({@code Block#getState()}), so a broken chest comes back with its contents.</li>
 *   <li>Box edits, undo, redo and brush strokes are jobs: each session runs one at a time (strokes queue up to
 *       {@link #MAX_STROKES}), and all of them together get {@code edit.box-budget-ms} of main-thread time per tick.
 *       A job writes with {@code setBlockData(data, false)} (no neighbour reactions, like vanilla's /fill), records each
 *       change for undo, then a fix-up pass recomputes the connections of the blocks it touched and of their
 *       neighbours (fences, walls, panes, stairs, redstone, snowy grass: vanilla {@code Block.updateFromNeighbourShapes})
 *       and, with {@code edit.physics}, updates their neighbours (sand falls, water flows in, redstone sees it; vanilla
 *       {@code Level#updateNeighborsAt}, as /fill does). Light follows by itself.</li>
 *   <li>Undo and redo restore a group's cells in reverse (forward) order and skip cells whose block isn't the one the
 *       group left there (someone or something changed it since; flowing water and lava count as air), and cells
 *       outside the table's region now. The headset hears how many in the job's last {@code box_progress}.</li>
 * </ul>
 * Nothing changes outside the session's region or the world border; block entity snapshots and the journal are capped.
 */
final class PowerService {
    static final int MAX_STROKES = 3;
    static final int PROGRESS_TICKS = 10;
    static final int[][] DIRS = { { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 } };

    private final TabletopPlugin plugin;
    private EditWorld editWorld;
    // Config (volatile: the grant and the limits are read on websocket threads).
    volatile boolean enabled = true, requireToken, tokenSet, physics = true;
    volatile int undoDepth = 10000, tileCap = 256, boxMax = 32768, brushMaxRadius = 8, brushRate = 20000, actionsPerSecond = 5;
    volatile double budgetMs = 2;

    /** A session's journal, jobs and limits. */
    final class PS {
        final Session s;
        final UndoJournal<BlockData, BlockState> journal = new UndoJournal<>(undoDepth, tileCap);
        final ArrayDeque<Job> jobs = new ArrayDeque<>();
        final EditLimiter actions = new EditLimiter(actionsPerSecond, actionsPerSecond);
        final Bucket brushBlocks = new Bucket(Math.max(1, brushRate));
        int sentVersion;
        PS(Session s) { this.s = s; }
    }
    private final Map<Session, PS> sessions = new ConcurrentHashMap<>();
    private final List<PS> order = new ArrayList<>();
    private int rr;
    private final Map<Material, Boolean> tiles = new HashMap<>();
    long jobsDone, cellsChanged, journalled;

    PowerService(TabletopPlugin plugin) { this.plugin = plugin; }

    void init(EditWorld editWorld) { this.editWorld = editWorld; }

    void configure(FileConfiguration c) {
        enabled = c.getBoolean("edit.power", true);
        requireToken = c.getBoolean("edit.power-require-token", false);
        tokenSet = !c.getString("auth.token", "").isEmpty();
        physics = c.getBoolean("edit.physics", true);
        undoDepth = Math.max(0, c.getInt("edit.undo-depth", 10000));
        tileCap = Math.max(0, c.getInt("edit.undo-tile-entities", 256));
        boxMax = Math.max(0, c.getInt("edit.box-max-blocks", 32768));
        budgetMs = Math.max(0.1, c.getDouble("edit.box-budget-ms", 2));
        brushMaxRadius = Math.max(0, Math.min(16, c.getInt("edit.brush-max-radius", 8)));
        brushRate = Math.max(0, c.getInt("edit.brush-blocks-per-second", 20000));
        actionsPerSecond = Math.max(1, c.getInt("edit.power-actions-per-second", 5));
    }

    /** power_v1 is granted with edit_v1, when edit.power is on and, with edit.power-require-token, auth.token is set. */
    boolean allowed() { return enabled && plugin.editAllowed() && (!requireToken || tokenSet); }
    /** [setup] power_v1 for this session: with edit.power-require-token only for trusted sessions (auth.token or paired). */
    boolean allowed(Session s) { return enabled && plugin.editAllowed(s) && (!requireToken || s.trusted); }

    /** Called at hello (websocket thread) for a session that asked for power_v1 and got edit_v1. */
    void grant(Session s) { sessions.put(s, new PS(s)); }

    boolean granted(Session s) { return sessions.containsKey(s); }

    /** welcome.limits for a power_v1 session. */
    void limits(Session s, JsonObject limits) {
        if (granted(s)) PowerMessages.limits(limits, undoDepth, boxMax, brushMaxRadius, brushRate);
    }

    /** Websocket thread: true when the session may send one more undo, redo or box_edit now. */
    boolean admit(Session s) {
        PS ps = sessions.get(s);
        return ps == null || ps.actions.tryAcquire(System.nanoTime());
    }

    void onSessionClosed(Session s) {
        PS ps = sessions.remove(s);
        if (ps != null) { ps.jobs.clear(); order.remove(ps); }
    }

    void shutdown() { sessions.clear(); order.clear(); }

    String stats() {
        int jobs = 0, groups = 0, blocks = 0;
        for (PS ps : sessions.values()) { jobs += ps.jobs.size(); groups += ps.journal.undoGroups(); blocks += ps.journal.undoBlocks(); }
        return String.format("power %s: %d sessions, %d jobs running, %d undo groups (%d blocks), %d jobs done, %d cells changed, %d edits journalled",
            allowed() ? "on" : "off", sessions.size(), jobs, groups, blocks, jobsDone, cellsChanged, journalled);
    }

    // ---------- block entities ----------

    /** The block has a block entity (chest, sign, banner, spawner...): its contents need a snapshot. Cached per material. */
    boolean hasTile(BlockData d) {
        return tiles.computeIfAbsent(d.getMaterial(), m -> {
            try { return m.createBlockData().createBlockState() instanceof TileState; } catch (RuntimeException e) { return false; }
        });
    }

    private BlockState snapshot(Block b, BlockData d) { return hasTile(d) ? b.getState() : null; }

    // ---------- single edits ----------

    /**
     * Runs {@code action} (a block_edit or interact) and journals what changed in the window around (x, y, z):
     * ±{@code rh} on X and Z, from y-1 to y+{@code up}. Without power_v1 it just runs the action.
     */
    void journalled(Session s, Long group, String label, int x, int y, int z, int rh, int up, Runnable action) {
        PS ps = sessions.get(s);
        RegionSpec r = s.region;
        World w = ps == null || !ps.journal.enabled() || r == null ? null : Bukkit.getWorld(r.world());
        if (w == null) { action.run(); return; }
        int y0 = Math.max(w.getMinHeight(), y - 1), y1 = Math.min(w.getMaxHeight() - 1, y + up);
        List<Block> blocks = new ArrayList<>();
        List<BlockData> before = new ArrayList<>();
        List<BlockState> tilesBefore = new ArrayList<>();
        for (int cx = x - rh; cx <= x + rh; cx++)
            for (int cz = z - rh; cz <= z + rh; cz++) {
                if (!w.isChunkLoaded(cx >> 4, cz >> 4)) continue;
                for (int cy = y0; cy <= y1; cy++) {
                    Block b = w.getBlockAt(cx, cy, cz);
                    BlockData d = b.getBlockData();
                    blocks.add(b); before.add(d); tilesBefore.add(snapshot(b, d));
                }
            }
        action.run();
        long id = group != null ? group : ps.journal.anonymousGroup();
        int n = 0;
        for (int i = 0; i < blocks.size(); i++) {
            Block b = blocks.get(i);
            BlockData after = b.getBlockData();
            if (after.equals(before.get(i))) continue;
            ps.journal.record(id, label, w.getName(), b.getX(), b.getY(), b.getZ(), before.get(i), tilesBefore.get(i), after, BlockData::equals);
            n++;
        }
        if (n > 0) journalled++;
    }

    /**
     * [operator] Runs a container_edit or sign_edit and journals the block entity it changed: the block at pos and,
     * for a double chest, its other half (a slot index can be in either). A cell is recorded when its block or its
     * block entity's contents (inventory, sign text) differ afterwards; undo puts the whole block entity back as it was.
     */
    void journalledTiles(Session s, Long group, String label, int[] pos, Runnable action) {
        PS ps = sessions.get(s);
        RegionSpec r = s.region;
        World w = ps == null || !ps.journal.enabled() || r == null ? null : Bukkit.getWorld(r.world());
        if (w == null || !w.isChunkLoaded(pos[0] >> 4, pos[2] >> 4) || pos[1] < w.getMinHeight() || pos[1] >= w.getMaxHeight()) { action.run(); return; }
        List<Block> blocks = new ArrayList<>();
        Block b = w.getBlockAt(pos[0], pos[1], pos[2]);
        blocks.add(b);
        if (b.getState(false) instanceof Chest c && c.getInventory() instanceof DoubleChestInventory d) {
            for (var side : new Location[] { d.getLeftSide().getLocation(), d.getRightSide().getLocation() })
                if (side != null && !(side.getBlockX() == pos[0] && side.getBlockY() == pos[1] && side.getBlockZ() == pos[2])) blocks.add(side.getBlock());
        }
        List<BlockData> before = new ArrayList<>();
        List<BlockState> tilesBefore = new ArrayList<>();
        for (Block x : blocks) { BlockData d = x.getBlockData(); before.add(d); tilesBefore.add(snapshot(x, d)); }
        action.run();
        long id = group != null ? group : ps.journal.anonymousGroup();
        for (int i = 0; i < blocks.size(); i++) {
            Block x = blocks.get(i);
            BlockData after = x.getBlockData();
            if (after.equals(before.get(i)) && tileSame(tilesBefore.get(i), snapshot(x, after))) continue;
            ps.journal.record(id, label, w.getName(), x.getX(), x.getY(), x.getZ(), before.get(i), tilesBefore.get(i), after, BlockData::equals);
            journalled++;
        }
    }

    /** Two block entity snapshots hold the same inventory (a chest: its own half) or the same sign text, colour and ink. */
    static boolean tileSame(BlockState a, BlockState b) {
        if (a == null || b == null) return a == b;
        if (a instanceof Sign sa && b instanceof Sign sb) {
            if (sa.isWaxed() != sb.isWaxed()) return false;
            for (Side side : Side.values()) {
                SignSide x = sa.getSide(side), y = sb.getSide(side);
                if (!x.lines().equals(y.lines()) || x.getColor() != y.getColor() || x.isGlowingText() != y.isGlowingText()) return false;
            }
            return true;
        }
        // A placed state's getInventory()/getBlockInventory() is the live one: compare the snapshots' own copies
        // (a chest's: its own half).
        if (a instanceof Container ca && b instanceof Container cb)
            return java.util.Arrays.equals(ca.getSnapshotInventory().getContents(), cb.getSnapshotInventory().getContents());
        return true;
    }

    /** The window a use of this item can change at once (bone meal grows plants and trees). */
    static int[] window(String item) {
        return "minecraft:bone_meal".equals(item) ? new int[] { 3, 12 } : new int[] { 1, 1 };
    }

    // ---------- undo / redo ----------

    void onUndo(Session s, boolean redo, Long refSeq) {
        if (s.state != Session.State.READY) return;
        String op = redo ? "redo" : "undo";
        PS ps = sessions.get(s);
        if (ps == null) { deny(s, op, null, null, "edit_denied", offReason(s), refSeq); return; }
        if (!ps.journal.enabled()) { deny(s, op, null, null, "edit_denied", "undo is off on this server (edit.undo-depth: 0)", refSeq); return; }
        if (!ps.jobs.isEmpty()) { deny(s, op, null, null, "edit_denied", "wait: " + ps.jobs.peek().describe() + " is still running", refSeq); return; }
        UndoJournal.Group<BlockData, BlockState> g = redo ? ps.journal.popRedo() : ps.journal.popUndo();
        if (g == null) { deny(s, op, null, null, "edit_denied", redo ? "nothing to redo" : "nothing to undo", refSeq); return; }
        ps.jobs.add(new UndoJob(ps, g, redo, refSeq));
        if (!order.contains(ps)) order.add(ps);
    }

    private String offReason(Session s) {
        if (!s.canEdit) return plugin.editAllowed(s) ? "ask for edit_v1 and power_v1 in hello.caps" : "block editing is off on this server, or needs a paired headset (edit.enabled / edit.require-token)";
        if (!enabled) return "power tools are off on this server (edit.power)";
        if (requireToken && !s.trusted) return "power tools need a paired headset or auth.token (edit.power-require-token)";
        return "ask for power_v1 in hello.caps";
    }

    private void deny(Session s, String op, String sub, Long group, String code, String why, Long refSeq) {
        plugin.getLogger().info(String.format("POWER_DENIED session=%s %s%s %s: %s", s.id, op, sub == null ? "" : " " + sub, code, why));
        plugin.send(s, PowerMessages.error(code, why, refSeq, op, sub, group));
    }

    // ---------- box edits ----------

    void onBoxEdit(Session s, PowerMessages.BoxEdit e, Long refSeq) {
        if (s.state != Session.State.READY) return;
        PS ps = sessions.get(s);
        if (ps == null) { deny(s, "box_edit", e.op(), e.group(), "edit_denied", offReason(s), refSeq); return; }
        if (boxMax <= 0) { deny(s, "box_edit", e.op(), e.group(), "edit_denied", "box tools are off on this server (edit.box-max-blocks: 0)", refSeq); return; }
        if (!ps.jobs.isEmpty()) { deny(s, "box_edit", e.op(), e.group(), "edit_denied", "wait: " + ps.jobs.peek().describe() + " is still running", refSeq); return; }
        if (e.volume() > boxMax)
            { deny(s, "box_edit", e.op(), e.group(), "edit_denied", String.format("the box is %d blocks, more than %d (edit.box-max-blocks)", e.volume(), boxMax), refSeq); return; }
        RegionSpec r = s.region;
        World w = r == null ? null : Bukkit.getWorld(r.world());
        if (w == null) { deny(s, "box_edit", e.op(), e.group(), "world_not_found", "no world", refSeq); return; }
        int[] dims = { e.sx(), e.sy(), e.sz() };
        int[] min = { e.minX(), e.minY(), e.minZ() };
        String why = boxProblem(w, r, min, dims, e.op().equals("clone") ? "the copy's source" : "the box");
        if (why != null) { deny(s, "box_edit", e.op(), e.group(), why.startsWith("outside the table") ? "out_of_region" : "edit_denied", why, refSeq); return; }
        int[] dest = null;
        if (e.op().equals("clone")) {
            dest = e.dest();
            why = boxProblem(w, r, dest, BoxPlan.rotatedDims(dims[0], dims[1], dims[2], e.rotate()), "the copy");
            if (why != null) { deny(s, "box_edit", e.op(), e.group(), why.startsWith("outside the table") ? "out_of_region" : "edit_denied", why, refSeq); return; }
        }
        BlockData state = null, filter = null;
        if (e.state() != null) {
            String[] err = new String[2];
            state = placeable(e.state(), err);
            if (state == null) { deny(s, "box_edit", e.op(), e.group(), err[0], err[1], refSeq); return; }
        }
        if (e.filter() != null) {
            try { filter = Bukkit.createBlockData(e.filter()); }
            catch (IllegalArgumentException ex) { deny(s, "box_edit", e.op(), e.group(), "bad_state", "not a block state: " + e.filter(), refSeq); return; }
        }
        ps.jobs.add(new BoxJob(ps, w, e, min, dims, dest, state, filter, refSeq));
        if (!order.contains(ps)) order.add(ps);
        plugin.getLogger().info(String.format("POWER session=%s box %s %d,%d,%d %dx%dx%d%s%s%s group=%s queued", s.id, e.op(), min[0], min[1], min[2], dims[0], dims[1], dims[2],
            e.state() != null ? " state=" + e.state() : "", e.filter() != null ? " filter=" + e.filter() : "",
            dest != null ? String.format(" dest=%d,%d,%d rotate=%d", dest[0], dest[1], dest[2], e.rotate()) : "", e.group()));
    }

    /** Why a box at min with dims can't be edited: outside the region, the world border or the build height; null if fine. */
    private static String boxProblem(World w, RegionSpec r, int[] min, int[] dims, String what) {
        int x1 = min[0] + dims[0] - 1, y1 = min[1] + dims[1] - 1, z1 = min[2] + dims[2] - 1;
        if (!r.containsWorld(min[0], min[1], min[2]) || !r.containsWorld(x1, y1, z1)) return "outside the table's region: " + what + " must be inside the area";
        if (min[1] < w.getMinHeight() || y1 >= w.getMaxHeight()) return what + " is outside the build height";
        if (!EditWorld.insideBorder(w, min[0], min[1], min[2]) || !EditWorld.insideBorder(w, x1, y1, z1)) return what + " is outside the world border";
        return null;
    }

    /**
     * A state a box or brush may place: a block_registry block, not edit.deny, not air (use clear or erase), not one
     * half of a door, bed or tall plant. Leaves come persistent. Null with err[0] = code, err[1] = message otherwise.
     */
    private BlockData placeable(String state, String[] err) {
        BlockData d;
        try { d = Bukkit.createBlockData(state); }
        catch (IllegalArgumentException ex) { err[0] = "bad_state"; err[1] = "not a block state: " + state; return null; }
        String id = BlockEdit.blockId(d.getAsString());
        if (d.getMaterial().isAir()) { err[0] = "bad_state"; err[1] = "use clear (or erase) to remove blocks"; return null; }
        String refusal = plugin.refusePlace(id);
        if (refusal != null) { err[0] = refusal.endsWith("(edit.deny)") ? "edit_denied" : "bad_state"; err[1] = refusal; return null; }
        if (EditRules.partner(d.getAsString()) != null) { err[0] = "edit_denied"; err[1] = EditRules.label(id) + " has two halves: place it with the trigger"; return null; }
        if (d instanceof Leaves lv) lv.setPersistent(true);
        return d;
    }

    // ---------- brushes ----------

    void onBrush(Session s, PowerMessages.Brush b, Long refSeq) {
        if (s.state != Session.State.READY) return;
        PS ps = sessions.get(s);
        if (ps == null) { deny(s, "brush", b.op(), b.group(), "edit_denied", offReason(s), refSeq); return; }
        if (brushRate <= 0 || brushMaxRadius <= 0 && b.radius() > 0) { deny(s, "brush", b.op(), b.group(), "edit_denied", "brushes are off on this server (edit.brush-blocks-per-second)", refSeq); return; }
        if (b.radius() > brushMaxRadius) { deny(s, "brush", b.op(), b.group(), "edit_denied", "the brush is bigger than " + brushMaxRadius + " (edit.brush-max-radius)", refSeq); return; }
        RegionSpec r = s.region;
        World w = r == null ? null : Bukkit.getWorld(r.world());
        if (w == null) return;
        int[] c = b.center();
        if (!r.containsWorld(c[0], c[1], c[2])) { deny(s, "brush", b.op(), b.group(), "out_of_region", "position is outside the table's region", refSeq); return; }
        int queued = 0;
        for (Job j : ps.jobs) { if (!(j instanceof BrushJob)) { deny(s, "brush", b.op(), b.group(), "edit_denied", "wait: " + j.describe() + " is still running", refSeq); return; } queued++; }
        if (queued >= MAX_STROKES) { deny(s, "brush", b.op(), b.group(), "rate_limited", "brush strokes are queueing up (the server is busy)", refSeq); return; }
        int cost = BrushShape.count(b.shape(), b.radius(), b.height());
        if (!ps.brushBlocks.take(cost, System.nanoTime())) {
            deny(s, "brush", b.op(), b.group(), "rate_limited", "more than " + brushRate + " brushed blocks per second (edit.brush-blocks-per-second)", refSeq);
            return;
        }
        BlockData state = null, filter = null;
        if (b.state() != null) {
            String[] err = new String[2];
            state = placeable(b.state(), err);
            if (state == null) { deny(s, "brush", b.op(), b.group(), err[0], err[1], refSeq); return; }
        }
        if (b.filter() != null) {
            try { filter = Bukkit.createBlockData(b.filter()); }
            catch (IllegalArgumentException ex) { deny(s, "brush", b.op(), b.group(), "bad_state", "not a block state: " + b.filter(), refSeq); return; }
        }
        ps.jobs.add(new BrushJob(ps, w, r, b, state, filter, refSeq));
        if (!order.contains(ps)) order.add(ps);
    }

    // ---------- the tick ----------

    /** Main thread, every tick: runs the sessions' jobs within box-budget-ms (round robin), then sends history. */
    void tick() {
        if (!order.isEmpty()) {
            long start = System.nanoTime(), end = start + (long) (budgetMs * 1e6);
            int n = order.size();
            for (int k = 0; k < n && System.nanoTime() < end; k++) {
                PS ps = order.get((rr + k) % n);
                Job j = ps.jobs.peek();
                if (j == null) continue;
                long t0 = System.nanoTime();
                boolean done;
                try { done = j.step(end); }
                catch (RuntimeException ex) {
                    plugin.getLogger().warning("POWER job " + j.describe() + " failed: " + ex);
                    j.phase = "failed";
                    done = true;
                }
                long spent = System.nanoTime() - t0;
                j.tickNs = Math.max(j.tickNs, spent);
                j.busyNs += spent;
                if (spent > budgetMs * 1.5e6) j.slowTicks++;
                j.ticks++;
                if (done) { ps.jobs.poll(); j.finish(); }
                else if (j.ticks % PROGRESS_TICKS == 0 && !(j instanceof BrushJob)) plugin.send(ps.s, PowerMessages.boxProgress(j.progress(false)));
            }
            rr++;
            order.removeIf(ps -> ps.jobs.isEmpty());
        }
        for (PS ps : sessions.values()) {
            if (ps.journal.version() == ps.sentVersion || ps.s.state != Session.State.READY) continue;
            ps.sentVersion = ps.journal.version();
            UndoJournal<BlockData, BlockState> j = ps.journal;
            plugin.send(ps.s, PowerMessages.history(j.undoGroups(), j.redoGroups(), j.undoBlocks(), j.redoBlocks(), j.depth()));
        }
    }

    // ---------- jobs ----------

    /** A unit of budgeted work: writes cells, then the fix-up pass, then reports. */
    abstract class Job {
        final PS ps;
        final World w;
        final String op;
        final Long group, refSeq;
        final long started = System.nanoTime();
        String phase = "write";
        long processed, total, changedCount, skipped, outside;
        int ticks;
        long tickNs, busyNs, fixNs;
        int slowTicks;
        final List<int[]> skippedPos = new ArrayList<>();
        /** The cells this job changed (keys), for the fix-up pass. */
        final LongSet changedKeys = new LongSet();
        private int fix;
        private final LongSet dirtyChunks = new LongSet();

        Job(PS ps, World w, String op, Long group, Long refSeq) { this.ps = ps; this.w = w; this.op = op; this.group = group; this.refSeq = refSeq; }

        /** Works until {@code end} (System.nanoTime); true when finished. */
        abstract boolean step(long end);

        String describe() { return total > 0 ? String.format("a %s (%d %%)", op, processed * 100 / Math.max(1, total)) : "a " + op; }

        PowerMessages.Progress progress(boolean finished) {
            return new PowerMessages.Progress(op, group, refSeq, phase, processed, total, changedCount, skipped, outside, finished, skippedPos,
                (System.nanoTime() - started) / 1e6, ticks);
        }

        /**
         * Sets b (now holding {@code now}) to data (or restores the block entity snapshot, which carries its state),
         * without neighbour reactions. A block with an inventory is emptied first: Paper's setBlockData(..., false)
         * still runs block entity side effects, so a chest would spill what the journal already keeps (vanilla's /fill
         * skips them too). A chest's own half only, never the double chest's other half.
         */
        void put(Block b, BlockData now, BlockData data, BlockState tile) {
            if (hasTile(now)) {
                BlockState live = b.getState(false);
                if (live instanceof Chest c) c.getBlockInventory().clear();
                else if (live instanceof BlockInventoryHolder h) h.getInventory().clear();
            }
            if (tile != null) {
                if (!tile.getBlockData().equals(data)) tile.setBlockData(data);
                tile.update(true, false);
            } else b.setBlockData(data, false);
            long k = UndoJournal.Cell.key(b.getX(), b.getY(), b.getZ());
            changedKeys.add(k);
            changedCount++;
            cellsChanged++;
            if (dirtyChunks.add(((long) (b.getX() >> 4) << 32) ^ (b.getZ() >> 4) & 0xffffffffL)) plugin.markDirty(w.getName(), b.getX(), b.getY(), b.getZ());
        }

        /** Marks the chunks touched since the last call dirty again (a diff runs at most once per tick per region anyway). */
        void flushDirty() { dirtyChunks.clear(); }

        /**
         * The fix-up pass, a slice at a time: the touched blocks' and their neighbours' connections from their
         * neighbours (inside the region), then neighbour updates when edit.physics is on. Returns true when done.
         */
        boolean fixup(long end, UndoJournal.Group<BlockData, BlockState> record) {
            phase = "fixup";
            long f0 = System.nanoTime();
            try { return fixupSlice(end, record); } finally { fixNs += System.nanoTime() - f0; }
        }

        private boolean fixupSlice(long end, UndoJournal.Group<BlockData, BlockState> record) {
            RegionSpec r = ps.s.region;
            int n = 0;
            while (fix < changedKeys.size()) {
                if ((++n & 15) == 0 && System.nanoTime() >= end) return false;
                long k = changedKeys.get(fix++);
                int x = UndoJournal.Cell.keyX(k), y = UndoJournal.Cell.keyY(k), z = UndoJournal.Cell.keyZ(k);
                Block b = w.getBlockAt(x, y, z);
                reshape(b, record, r);
                for (int[] d : DIRS) {
                    int nx = x + d[0], ny = y + d[1], nz = z + d[2];
                    if (changedKeys.contains(UndoJournal.Cell.key(nx, ny, nz)) || ny < w.getMinHeight() || ny >= w.getMaxHeight()) continue;
                    if (r == null || !r.containsWorld(nx, ny, nz) || !w.isChunkLoaded(nx >> 4, nz >> 4)) continue;
                    reshape(w.getBlockAt(nx, ny, nz), record, r);
                }
                if (physics) neighbours(w, b);
            }
            return true;
        }

        /** Recomputes a connecting block's state from its neighbours; the change is journalled with the job. */
        private void reshape(Block b, UndoJournal.Group<BlockData, BlockState> record, RegionSpec r) {
            BlockData d = b.getBlockData();
            if (!connects(d)) return;
            BlockData shaped = editWorld.shapeFromNeighbours(w, b, d);
            if (shaped.equals(d)) return;
            b.setBlockData(shaped, false);
            if (record != null) remember(record, b, d, null, shaped);
            plugin.markDirty(w.getName(), b.getX(), b.getY(), b.getZ());
        }

        /** Notes a change in the job's group (first "before" wins, latest "after"). */
        void remember(UndoJournal.Group<BlockData, BlockState> g, Block b, BlockData before, BlockState beforeTile, BlockData after) {
            long k = UndoJournal.Cell.key(b.getX(), b.getY(), b.getZ());
            UndoJournal.Cell<BlockData, BlockState> c = g.cells.get(k);
            if (c == null) g.cells.put(k, new UndoJournal.Cell<>(b.getX(), b.getY(), b.getZ(), before, beforeTile, after));
            else c.after = after;
        }

        void skip(int x, int y, int z) { skipped++; if (skippedPos.size() < PowerMessages.MAX_SKIPPED_POS) skippedPos.add(new int[] { x, y, z }); }

        /** Done: report, log. */
        void finish() {
            phase = phase.equals("failed") ? "failed" : "done";
            jobsDone++;
            done();
            String line = String.format("POWER session=%s %s group=%s %s changed=%d skipped=%d outside=%d of %d in %.1f ms over %d ticks, main thread %.1f ms (fix-up %.1f), max %.2f ms in a tick, %d ticks over 1.5x the budget",
                ps.s.id, op, group, BoxPlan.done(op), changedCount, skipped, outside, total, (System.nanoTime() - started) / 1e6, ticks, busyNs / 1e6, fixNs / 1e6, tickNs / 1e6, slowTicks);
            // Brush strokes come several a second while the trigger is held: no box_progress, and logged only at FINE.
            if (this instanceof BrushJob) { plugin.getLogger().fine(line); return; }
            plugin.send(ps.s, PowerMessages.boxProgress(progress(true)));
            plugin.getLogger().info(line);
        }

        abstract void done();
    }

    /** Fences, panes, bars, walls, stairs, rails, redstone dust, snowy grass, gates and leaves follow their neighbours. */
    static boolean connects(BlockData d) {
        return d instanceof MultipleFacing || d instanceof Wall || d instanceof Stairs || d instanceof Rail || d instanceof RedstoneWire
            || d instanceof Snowable || d instanceof Gate || d instanceof Leaves;
    }

    /** Air, or water or lava that only flowed in: an undo may put the block back there. */
    static boolean airish(BlockData d) {
        if (d.getMaterial().isAir()) return true;
        return (d.getMaterial() == Material.WATER || d.getMaterial() == Material.LAVA) && d instanceof Levelled l && l.getLevel() != 0;
    }

    /** The block is still what the journal expects there. */
    static boolean same(BlockData now, BlockData expected) {
        return now.equals(expected) || (airish(now) && airish(expected));
    }

    /** box_edit: fill, replace, clear, walls, hollow, or clone (a read pass first). */
    final class BoxJob extends Job {
        final PowerMessages.BoxEdit e;
        final int[] min, dims, dest;
        final BlockData state, filter;
        final UndoJournal.Group<BlockData, BlockState> record;
        final RegionSpec region;
        long i;
        // clone: the source, read first (states, and block entity snapshots)
        BlockData[] src;
        BlockState[] srcTiles;
        int[] cdims;
        long read;

        BoxJob(PS ps, World w, PowerMessages.BoxEdit e, int[] min, int[] dims, int[] dest, BlockData state, BlockData filter, Long refSeq) {
            super(ps, w, e.op(), e.group(), refSeq);
            this.e = e; this.min = min; this.dims = dims; this.dest = dest; this.state = state; this.filter = filter;
            this.region = ps.s.region;
            this.record = new UndoJournal.Group<>(e.group() != null ? e.group() : ps.journal.anonymousGroup(), e.op(), w.getName());
            total = BoxPlan.cells(e.op(), dims[0], dims[1], dims[2]);
            if (dest != null) {
                int v = dims[0] * dims[1] * dims[2];
                src = new BlockData[v]; srcTiles = new BlockState[v];
                cdims = BoxPlan.rotatedDims(dims[0], dims[1], dims[2], e.rotate());
                phase = "read";
            }
        }

        @Override boolean step(long end) {
            flushDirty();
            if (src != null && read < src.length) {
                int n = 0;
                while (read < src.length) {
                    if ((++n & 15) == 0 && System.nanoTime() >= end) return false;
                    int[] l = BoxPlan.local(read, dims[0], dims[2]);
                    Block b = w.getBlockAt(min[0] + l[0], min[1] + l[1], min[2] + l[2]);
                    BlockData d = b.getBlockData();
                    src[(int) read] = d;
                    srcTiles[(int) read] = snapshot(b, d);
                    read++;
                }
                phase = "write";
            }
            if (i < (long) dims[0] * dims[1] * dims[2]) {
                long v = (long) dims[0] * dims[1] * dims[2];
                int n = 0;
                while (i < v) {
                    if ((++n & 15) == 0 && System.nanoTime() >= end) return false;
                    long idx = i++;
                    int[] l = BoxPlan.local(idx, dims[0], dims[2]);
                    BoxPlan.Kind kind = BoxPlan.kind(op, l[0], l[1], l[2], dims[0], dims[1], dims[2], filter != null);
                    if (kind == BoxPlan.Kind.SKIP) continue;
                    processed++;
                    if (src != null) { cloneCell(idx, l); continue; }
                    int x = min[0] + l[0], y = min[1] + l[1], z = min[2] + l[2];
                    if (!w.isChunkLoaded(x >> 4, z >> 4)) { outside++; continue; }
                    Block b = w.getBlockAt(x, y, z);
                    BlockData now = b.getBlockData();
                    BlockData to = switch (kind) {
                        case SET -> state;
                        case AIR -> air();
                        case MATCH -> filter.matches(now) ? state : null;
                        case MATCH_AIR -> filter.matches(now) ? air() : null;
                        default -> null;
                    };
                    if (to == null) continue;
                    to = wet(to, now);
                    if (now.equals(to)) continue;
                    BlockState tile = snapshot(b, now);
                    put(b, now, to, null);
                    remember(record, b, now, tile, to);
                }
            }
            return fixup(end, record);
        }

        /** One cell of a clone: the source's state turned, its block entity copied; edit.deny blocks are left out. */
        private void cloneCell(long idx, int[] l) {
            int[] t = BoxPlan.rotate(l[0], l[1], l[2], dims[0], dims[2], e.rotate());
            int x = dest[0] + t[0], y = dest[1] + t[1], z = dest[2] + t[2];
            if (!w.isChunkLoaded(x >> 4, z >> 4)) { outside++; return; }
            BlockData from = src[(int) idx];
            if (plugin.denied(BlockEdit.blockId(from.getAsString()))) { skip(x, y, z); return; }
            BlockData to = from;
            if (e.rotate() != 0) { to = from.clone(); to.rotate(ROTATIONS[e.rotate()]); }
            Block b = w.getBlockAt(x, y, z);
            BlockData now = b.getBlockData();
            BlockState srcTile = srcTiles[(int) idx];
            if (now.equals(to) && srcTile == null) return;
            BlockState beforeTile = snapshot(b, now);
            put(b, now, to, srcTile != null ? srcTile.copy(b.getLocation()) : null);
            remember(record, b, now, beforeTile, b.getBlockData());
        }

        @Override void done() {
            ps.journal.push(record);
        }
    }

    static final StructureRotation[] ROTATIONS = { StructureRotation.NONE, StructureRotation.CLOCKWISE_90, StructureRotation.CLOCKWISE_180, StructureRotation.COUNTERCLOCKWISE_90 };
    private static BlockData air;
    /** Plain air (created on first use: the class also loads in unit tests, without a server). */
    static BlockData air() { if (air == null) air = Bukkit.createBlockData(Material.AIR); return air; }

    /** A waterloggable state goes in wet exactly when the cell held a water source (as block_edit does). */
    static BlockData wet(BlockData to, BlockData now) {
        if (!(to instanceof Waterlogged)) return to;
        boolean water = (now instanceof Waterlogged nw && nw.isWaterlogged())
            || (now.getMaterial() == Material.WATER && now instanceof Levelled l && l.getLevel() == 0);
        if (((Waterlogged) to).isWaterlogged() == water) return to;
        BlockData c = to.clone();
        ((Waterlogged) c).setWaterlogged(water);
        return c;
    }

    /** undo or redo of one journal group. */
    final class UndoJob extends Job {
        final UndoJournal.Group<BlockData, BlockState> g;
        final List<UndoJournal.Cell<BlockData, BlockState>> cells;
        final boolean redo;
        final UndoJournal.Group<BlockData, BlockState> kept;
        int i;

        UndoJob(PS ps, UndoJournal.Group<BlockData, BlockState> g, boolean redo, Long refSeq) {
            super(ps, Bukkit.getWorld(g.world), redo ? "redo" : "undo", g.id >= 0 ? g.id : null, refSeq);
            this.g = g; this.redo = redo;
            this.cells = g.list();
            if (!redo) java.util.Collections.reverse(cells);
            this.kept = new UndoJournal.Group<>(g.id, g.label, g.world);
            total = cells.size();
        }

        @Override boolean step(long end) {
            flushDirty();
            RegionSpec r = ps.s.region;
            int n = 0;
            while (i < cells.size()) {
                if ((++n & 7) == 0 && System.nanoTime() >= end) return false;
                UndoJournal.Cell<BlockData, BlockState> c = cells.get(i++);
                processed++;
                if (w == null || r == null || !r.world().equals(w.getName()) || !r.containsWorld(c.x, c.y, c.z) || !w.isChunkLoaded(c.x >> 4, c.z >> 4)
                    || !EditWorld.insideBorder(w, c.x, c.y, c.z)) { outside++; continue; }
                Block b = w.getBlockAt(c.x, c.y, c.z);
                BlockData now = b.getBlockData();
                if (!same(now, redo ? c.before : c.after)) { skip(c.x, c.y, c.z); continue; }
                if (redo) {
                    c.beforeTile = snapshot(b, now);
                    put(b, now, c.after, c.afterTile);
                } else {
                    c.afterTile = snapshot(b, now);
                    put(b, now, c.before, c.beforeTile);
                }
                kept.cells.put(c.key(), c);
            }
            if (w == null) return true;
            return fixup(end, null);
        }

        @Override void done() {
            // the undone cells, oldest first again, go to redo; redone ones back to undo
            UndoJournal.Group<BlockData, BlockState> back = new UndoJournal.Group<>(g.id, g.label, g.world);
            for (UndoJournal.Cell<BlockData, BlockState> c : g.list()) if (kept.cells.containsKey(c.key())) back.cells.put(c.key(), c);
            if (redo) ps.journal.pushUndoKeepRedo(back); else ps.journal.pushRedo(back);
        }
    }

    /** One brush stroke: paint, replace, erase or smooth inside a sphere or cylinder (clipped to the region). */
    final class BrushJob extends Job {
        final PowerMessages.Brush b;
        final BlockData state, filter;
        final RegionSpec region;
        final UndoJournal.Group<BlockData, BlockState> record;
        final int r, h, dy0, dy1;
        int dx, dy, dz;
        boolean started;
        // smooth: the decided targets per cell of the window
        int[] smooth;
        List<BlockData> palette;
        int ww, wh, wd;

        BrushJob(PS ps, World w, RegionSpec region, PowerMessages.Brush b, BlockData state, BlockData filter, Long refSeq) {
            super(ps, w, "brush", b.group(), refSeq);
            this.b = b; this.state = state; this.filter = filter; this.region = region;
            this.r = b.radius(); this.h = b.height();
            this.dy0 = BrushShape.minDy(b.shape(), r, h); this.dy1 = BrushShape.maxDy(b.shape(), r, h);
            this.record = new UndoJournal.Group<>(b.group() != null ? b.group() : ps.journal.anonymousGroup(), "brush", w.getName());
            total = BrushShape.count(b.shape(), r, h);
        }

        @Override String describe() { return "a brush stroke"; }

        @Override boolean step(long end) {
            flushDirty();
            if (!started) { started = true; dx = -r; dy = dy0; dz = -r; if (b.op().equals("smooth")) readSmooth(); }
            int n = 0;
            while (dy <= dy1) {
                if ((++n & 15) == 0 && System.nanoTime() >= end) return false;
                int cx = dx, cy = dy, cz = dz;
                if (++dx > r) { dx = -r; if (++dz > r) { dz = -r; dy++; } }
                if (!BrushShape.inside(b.shape(), cx, cy, cz, r, h)) continue;
                processed++;
                int x = b.center()[0] + cx, y = b.center()[1] + cy, z = b.center()[2] + cz;
                if (!region.containsWorld(x, y, z) || y < w.getMinHeight() || y >= w.getMaxHeight() || !w.isChunkLoaded(x >> 4, z >> 4)
                    || !EditWorld.insideBorder(w, x, y, z)) { outside++; continue; }
                Block blk = w.getBlockAt(x, y, z);
                BlockData now = blk.getBlockData();
                BlockData to = switch (b.op()) {
                    case "paint" -> state;
                    case "replace" -> filter.matches(now) ? state : null;
                    case "erase" -> now.getMaterial().isAir() ? null : air();
                    case "smooth" -> smoothTarget(cx, cy, cz);
                    default -> null;
                };
                if (to == null) continue;
                to = wet(to, now);
                if (now.equals(to)) continue;
                BlockState tile = snapshot(blk, now);
                put(blk, now, to, null);
                remember(record, blk, now, tile, to);
            }
            return fixup(end, record);
        }

        /** Smooth reads its window (the shape plus a one-cell rim) in one go: at most 19×19×19 cells for radius 8. */
        private void readSmooth() {
            ww = 2 * r + 3; wd = 2 * r + 3; wh = dy1 - dy0 + 3;
            int[] ids = new int[ww * wh * wd];
            boolean[] mask = new boolean[ids.length];
            palette = new ArrayList<>();
            Map<BlockData, Integer> index = new HashMap<>();
            List<Boolean> solid = new ArrayList<>();
            int[] c = b.center();
            for (int y = 0; y < wh; y++) for (int z = 0; z < wd; z++) for (int x = 0; x < ww; x++) {
                int wx = c[0] + x - r - 1, wy = c[1] + y + dy0 - 1, wz = c[2] + z - r - 1;
                BlockData d = wy < w.getMinHeight() || wy >= w.getMaxHeight() || !w.isChunkLoaded(wx >> 4, wz >> 4) ? air() : w.getBlockAt(wx, wy, wz).getBlockData();
                Integer id = index.get(d);
                if (id == null) { id = palette.size(); palette.add(d); index.put(d, id); solid.add(d.getMaterial().isSolid() && d.getMaterial().isOccluding()); }
                int i = (y * wd + z) * ww + x;
                ids[i] = id;
                mask[i] = x > 0 && x < ww - 1 && z > 0 && z < wd - 1 && y > 0 && y < wh - 1 && BrushShape.inside(b.shape(), x - r - 1, y + dy0 - 1, z - r - 1, r, h);
            }
            smooth = BrushShape.smooth(ids, ww, wh, wd, mask, id -> solid.get(id));
        }

        private BlockData smoothTarget(int cx, int cy, int cz) {
            int i = ((cy - dy0 + 1) * wd + (cz + r + 1)) * ww + (cx + r + 1);
            int t = smooth[i];
            return t == BrushShape.KEEP ? null : t == BrushShape.AIR ? air() : palette.get(t);
        }

        @Override void done() { ps.journal.push(record); }
    }

    // ---------- the neighbour bridge ----------

    private boolean bridgeTried, bridgeOk;
    private Method getHandle, getNmsState, getNmsBlock, updateNeighbors;
    private Constructor<?> blockPos;

    /**
     * Vanilla's {@code Level#updateNeighborsAt(pos, block)} for a cell a job changed (what /fill does after placing):
     * sand above falls, water next to a cleared cell flows in, redstone sees the change. Paper has no API for it; one
     * guarded reflective call on the unobfuscated 26.3 server, logged once and skipped if it ever fails.
     */
    private void neighbours(World w, Block b) {
        if (!bridgeReady()) return;
        try {
            Object block = getNmsBlock.invoke(getNmsState.invoke(b.getBlockData()));
            updateNeighbors.invoke(getHandle.invoke(w), blockPos.newInstance(b.getX(), b.getY(), b.getZ()), block);
        } catch (ReflectiveOperationException | RuntimeException | LinkageError ex) {
            bridgeOk = false;
            plugin.getLogger().warning("POWER_NEIGHBOURS off: box edits don't wake their neighbours (" + ex + ")");
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
            plugin.getLogger().info("POWER_NEIGHBOURS on: box edits, undo and brushes update their neighbours like /fill (vanilla Level#updateNeighborsAt)");
        } catch (ReflectiveOperationException | RuntimeException | LinkageError ex) {
            bridgeOk = false;
            plugin.getLogger().warning("POWER_NEIGHBOURS off: box edits don't wake their neighbours (" + ex + ")");
        }
        return bridgeOk;
    }

    /** A token bucket of blocks per second (brush strokes take their whole cost at once or nothing). */
    static final class Bucket {
        private final double rate;
        private double tokens;
        private long last = Long.MIN_VALUE;
        Bucket(double perSecond) { rate = perSecond; tokens = perSecond; }
        synchronized boolean take(double n, long nowNanos) {
            if (last != Long.MIN_VALUE) tokens = Math.min(rate, tokens + (nowNanos - last) / 1e9 * rate);
            last = nowNanos;
            if (tokens < n) return false;
            tokens -= n;
            return true;
        }
    }
}
