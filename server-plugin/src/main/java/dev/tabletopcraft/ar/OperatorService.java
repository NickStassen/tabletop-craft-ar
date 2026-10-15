package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer;
import org.bukkit.Bukkit;
import org.bukkit.DyeColor;
import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.Registry;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.block.BlockState;
import org.bukkit.block.Container;
import org.bukkit.block.Lidded;
import org.bukkit.block.Sign;
import org.bukkit.block.sign.Side;
import org.bukkit.block.sign.SignSide;
import org.bukkit.configuration.file.FileConfiguration;
import org.bukkit.configuration.file.YamlConfiguration;
import org.bukkit.event.block.SignChangeEvent;
import org.bukkit.inventory.DoubleChestInventory;
import org.bukkit.inventory.Inventory;
import org.bukkit.inventory.ItemStack;
import org.bukkit.inventory.ItemType;

import java.io.File;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;

/**
 * [operator] The headset operator's navigation and block-entity tools (PROTOCOL.md "Marks: bookmarks, spawn and
 * worlds", "Containers", "Signs"). Messages are parsed and rate-limited on the websocket thread (LinkServer); everything
 * here runs on the main thread.
 * <ul>
 *   <li><b>Worlds and spawn:</b> welcome.worlds and region_info.worlds/spawn list every world with its environment, build
 *       height and spawn block, so a headset can switch dimension and jump to spawn with an ordinary region_request.</li>
 *   <li><b>Bookmarks</b> (cap marks_v1, config section {@code marks}): add and remove named positions, stored in
 *       plugins/TabletopCraftAR/bookmarks.yml (written off the main thread) and sent to every marks_v1 headset.</li>
 *   <li><b>Containers</b> (interact_v1, {@code interact.containers}: edit, view or off): container_query opens a view of
 *       a chest, barrel, shulker box, hopper, dispenser, dropper, furnace, smoker, blast furnace, brewing stand or
 *       crafter (a double chest as one view); the view is watched every 4 ticks and re-sent when it changes, and the lid
 *       stays open ({@link Lidded#open}) while any headset looks. container_edit sets a slot (checked again here:
 *       region, stack size, the slot rules of {@link ContainerRules}, an optional "was" precondition) or sorts.</li>
 *   <li><b>Signs</b> ({@code interact.signs}): sign_edit sets one side's lines, colour and glow. With
 *       {@code interact.sign-event} it first fires a {@link SignChangeEvent} so protection and logging plugins can see,
 *       change or cancel it; that event has no player (the headset isn't one).</li>
 * </ul>
 * Protection plugins are otherwise not consulted, as for block_edit and interact.
 */
final class OperatorService {
    static final int WATCH_TICKS = 4;
    private final TabletopPlugin plugin;
    // Config (volatile: read on websocket threads at hello).
    volatile boolean marksEnabled = true, marksRequireToken, marksEdit = true, tokenSet;
    volatile String containers = "edit";   // edit, view, off
    volatile boolean signs = true, signEvent = true;
    private Bookmarks bookmarks = new Bookmarks(200);
    private File bookmarksFile;
    private long saveSeq;

    /** One open container view per session: where it is and what was sent last. */
    private static final class Watch { String world; int x, y, z; String sent; List<Block> lids = List.of(); boolean dirty; }
    private final Map<Session, Watch> watches = new IdentityHashMap<>();
    /** Lids held open, by block key: how many views hold each. */
    private final Map<String, Integer> lidViews = new HashMap<>();
    long views, edits, signEdits, marksChanged, refused;

    OperatorService(TabletopPlugin plugin) { this.plugin = plugin; }

    void configure(FileConfiguration c) {
        marksEnabled = c.getBoolean("marks.enabled", true);
        marksRequireToken = c.getBoolean("marks.require-token", false);
        marksEdit = c.getBoolean("marks.edit", true);
        tokenSet = !c.getString("auth.token", "").isEmpty();
        String mode = String.valueOf(c.get("interact.containers", "edit")).toLowerCase(Locale.ROOT);
        containers = switch (mode) { case "true", "edit", "on" -> "edit"; case "view", "read-only", "readonly" -> "view"; default -> "off"; };
        signs = c.getBoolean("interact.signs", true);
        signEvent = c.getBoolean("interact.sign-event", true);
        int max = Math.max(1, c.getInt("marks.max", 200));
        bookmarksFile = new File(plugin.getDataFolder(), "bookmarks.yml");
        Bookmarks b = new Bookmarks(max);
        if (bookmarksFile.exists()) {
            YamlConfiguration y = YamlConfiguration.loadConfiguration(bookmarksFile);
            int bad = b.load(y.getList("bookmarks"));
            if (bad > 0) plugin.getLogger().warning("bookmarks.yml: " + bad + " entries skipped (bad or over marks.max)");
        }
        bookmarks = b;
    }

    /** marks_v1 is granted when marks.enabled and, with marks.require-token, the server has an auth.token. */
    boolean marksAllowed() { return marksEnabled && (!marksRequireToken || tokenSet); }

    /** welcome additions: every client gets the worlds; marks_v1 sessions the bookmark limit and whether they may edit. */
    void welcome(Session s, JsonObject w) {
        w.add("worlds", OperatorMessages.worlds(worlds()));
        if (s.wantsMarks) w.add("marks", OperatorMessages.welcomeMarks(bookmarks.max, marksEdit));
        // welcome.interact (interact_v1 sessions): what the container view and the sign editor may do
        if (w.has("interact") && w.get("interact").isJsonObject()) {
            w.getAsJsonObject("interact").addProperty("containers", containers);
            w.getAsJsonObject("interact").addProperty("signs", signs);
        }
    }

    /** Sent once after welcome to marks_v1 sessions. */
    JsonObject bookmarksMessage() { return OperatorMessages.bookmarks(bookmarks.list(), bookmarks.max); }

    /** Every loaded world: name, environment, build height and spawn block. */
    static List<OperatorMessages.WorldEntry> worlds() {
        List<OperatorMessages.WorldEntry> l = new ArrayList<>();
        for (World w : Bukkit.getWorlds()) l.add(entry(w));
        return l;
    }

    static OperatorMessages.WorldEntry entry(World w) {
        Location sp = w.getSpawnLocation();
        return new OperatorMessages.WorldEntry(w.getName(), ControlService.env(w), w.getMinHeight(), w.getMaxHeight(),
            new int[] { sp.getBlockX(), sp.getBlockY(), sp.getBlockZ() });
    }

    /** region_info additions: the region world's spawn and every world. */
    void regionInfo(JsonObject o, String world) {
        World w = Bukkit.getWorld(world);
        if (w != null) { var e = entry(w); o.add("spawn", Protocol.ints(e.spawn())); }
        o.add("worlds", OperatorMessages.worlds(worlds()));
    }

    // ---------- bookmarks ----------

    void onBookmark(Session s, OperatorMessages.BookmarkOp b, Long refSeq) {
        if (s.state != Session.State.READY) return;
        if (!s.wantsMarks) { denyMark(s, b, marksAllowed() ? "ask for marks_v1 in hello.caps" : "bookmarks are off on this server (marks.enabled / marks.require-token)", refSeq); return; }
        if (!marksEdit) { denyMark(s, b, "bookmarks are read-only on this server (marks.edit)", refSeq); return; }
        if (b.op().equals("remove")) {
            if (!bookmarks.remove(b.name())) { denyMark(s, b, "no bookmark named " + b.name(), refSeq); return; }
        } else {
            World w = Bukkit.getWorld(b.world());
            if (w == null) { plugin.send(s, b.error("world_not_found", "no world " + b.world(), refSeq)); return; }
            int y = Math.max(w.getMinHeight(), Math.min(w.getMaxHeight() - 1, b.pos()[1]));
            String why = bookmarks.add(new OperatorMessages.Mark(b.name(), w.getName(), b.pos()[0], y, b.pos()[2], s.operatorName));
            if (why != null) { denyMark(s, b, why, refSeq); return; }
        }
        marksChanged++;
        plugin.getLogger().info(String.format("MARK session=%s %s \"%s\"%s (%d bookmarks)", s.id, b.op(), b.name(),
            b.op().equals("add") ? " " + b.world() + " " + b.pos()[0] + "," + b.pos()[1] + "," + b.pos()[2] : "", bookmarks.size()));
        save();
        JsonObject msg = bookmarksMessage();
        for (Session o : plugin.allSessions()) if (o.state == Session.State.READY && o.wantsMarks) plugin.send(o, msg.deepCopy());
    }

    private void denyMark(Session s, OperatorMessages.BookmarkOp b, String why, Long refSeq) {
        refused++;
        plugin.getLogger().info("MARK_DENIED session=" + s.id + " " + b.op() + " \"" + b.name() + "\": " + why);
        plugin.send(s, b.error("mark_denied", why, refSeq));
    }

    /** Writes bookmarks.yml off the main thread (atomically, through a temporary file); the newest write wins. */
    private void save() {
        YamlConfiguration y = new YamlConfiguration();
        y.set("bookmarks", bookmarks.toMaps());
        String text = y.saveToString();
        long seq = ++saveSeq;
        File f = bookmarksFile;
        Bukkit.getScheduler().runTaskAsynchronously(plugin, () -> {
            synchronized (this) {
                if (seq < saveSeq) return;   // a newer list is on its way
                try {
                    Files.createDirectories(f.getParentFile().toPath());
                    File tmp = new File(f.getParentFile(), f.getName() + ".tmp");
                    Files.writeString(tmp.toPath(), text, StandardCharsets.UTF_8);
                    Files.move(tmp.toPath(), f.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                } catch (IOException e) { plugin.getLogger().warning("can't write bookmarks.yml: " + e.getMessage()); }
            }
        });
    }

    // ---------- containers ----------

    /** Why this session can't see (or edit) containers now, or null. */
    private String containerGate(Session s, boolean edit) {
        if (!s.canInteract) return plugin.interact.allowed() ? "ask for interact_v1 in hello.caps" : plugin.interact.offReason();
        if (containers.equals("off")) return "containers are off on this server (interact.containers)";
        if (edit && !containers.equals("edit")) return "containers are view-only on this server (interact.containers: view)";
        return null;
    }

    /** The live container at a position inside the session's region, or a refusal (String). */
    private Object containerAt(Session s, int[] p) {
        RegionSpec r = s.region;
        if (r == null || !r.containsWorld(p[0], p[1], p[2])) return "position is outside the table's region";
        World w = Bukkit.getWorld(r.world());
        if (w == null) return "no world " + r.world();
        if (!w.isChunkLoaded(p[0] >> 4, p[2] >> 4)) return "chunk not loaded";
        Block b = w.getBlockAt(p[0], p[1], p[2]);
        BlockState st = b.getState();
        if (!(st instanceof Container c) || !ContainerRules.container(b.getType().getKey().asString())) return "no container there (" + EditRules.label(b.getBlockData().getAsString()) + ")";
        return c;
    }

    void onContainerQuery(Session s, int[] p, Long refSeq) {
        if (s.state != Session.State.READY) return;
        String gate = containerGate(s, false);
        if (gate != null) { deny(s, "container_query", "interact_denied", gate, refSeq, p); return; }
        Object at = containerAt(s, p);
        if (at instanceof String why) { deny(s, "container_query", why.startsWith("position is outside") ? "out_of_region" : "interact_denied", why, refSeq, p); return; }
        Container c = (Container) at;
        Watch old = watches.get(s);
        if (old != null && !(old.x == p[0] && old.y == p[1] && old.z == p[2] && old.world.equals(s.region.world()))) closeWatch(s, null);
        Watch wt = watches.computeIfAbsent(s, k -> new Watch());
        wt.world = s.region.world(); wt.x = p[0]; wt.y = p[1]; wt.z = p[2];
        JsonObject view = view(s, c, p, refSeq);
        wt.sent = signature(view);
        if (wt.lids.isEmpty()) { wt.lids = lidBlocks(c); for (Block b : wt.lids) openLid(b); }
        views++;
        plugin.getLogger().info(String.format("CONTAINER session=%s view %s %d,%d,%d size=%d", s.id, view.get("block").getAsString(), p[0], p[1], p[2], view.get("size").getAsInt()));
        plugin.send(s, view);
    }

    void onContainerClose(Session s, int[] p) {
        Watch w = watches.get(s);
        if (w != null && w.x == p[0] && w.y == p[1] && w.z == p[2]) closeWatch(s, null);
    }

    void onContainerEdit(Session s, OperatorMessages.ContainerEdit e, Long refSeq) {
        if (s.state != Session.State.READY) return;
        int[] p = e.pos();
        String gate = containerGate(s, true);
        if (gate != null) { deny(s, "container_edit", "interact_denied", gate, refSeq, p); return; }
        Object at = containerAt(s, p);
        if (at instanceof String why) { deny(s, "container_edit", why.startsWith("position is outside") ? "out_of_region" : "interact_denied", why, refSeq, p); return; }
        Container c = (Container) at;
        Inventory inv = c.getInventory();
        String blockId = c.getBlock().getType().getKey().asString();
        long t0 = System.nanoTime();
        String done;
        if (e.op().equals("sort")) {
            if (!ContainerRules.sortable(blockId)) { deny(s, "container_edit", "interact_denied", "only storage (chests, barrels, shulker boxes, hoppers, dispensers, droppers) can be sorted", refSeq, p); return; }
            done = sort(inv);
        } else {
            if (e.slot() >= inv.getSize()) { deny(s, "container_edit", "interact_denied", "slot " + e.slot() + " is past the last slot (" + (inv.getSize() - 1) + ")", refSeq, p); return; }
            ItemStack cur = inv.getItem(e.slot());
            String curId = cur == null || cur.isEmpty() ? null : cur.getType().getKey().asString();
            int curCount = curId == null ? 0 : cur.getAmount();
            if (e.checkWas() && (!Objects.equals(curId, e.wasItem()) || curCount != e.wasCount())) {
                deny(s, "container_edit", "interact_denied", "the slot changed meanwhile (it holds " + (curId == null ? "nothing" : curCount + " " + EditRules.label(curId)) + ")", refSeq, p); return;
            }
            if (e.clears()) {
                inv.setItem(e.slot(), null);
                done = "clear slot " + e.slot() + (curId == null ? "" : " (was " + curCount + " " + curId + ")");
            } else {
                NamespacedKey k = NamespacedKey.fromString(e.item());
                ItemType type = k == null ? null : Registry.ITEM.get(k);
                if (type == null || type == ItemType.AIR) { deny(s, "container_edit", "interact_denied", e.item() + " isn't an item on this server", refSeq, p); return; }
                String why = ContainerRules.placeProblem(blockId, e.slot(), e.item(), type.isFuel());
                if (why != null) { deny(s, "container_edit", "interact_denied", why, refSeq, p); return; }
                int n = Math.min(e.count(), Math.max(1, type.getMaxStackSize()));
                // The same item (with its components) keeps them: a named or enchanted stack only changes its count.
                ItemStack next = curId != null && curId.equals(e.item()) ? cur.asQuantity(n) : type.createItemStack(n);
                inv.setItem(e.slot(), next);
                done = "set slot " + e.slot() + " " + e.item() + " x" + n + (n < e.count() ? " (stack size " + n + ")" : "") + (curId == null ? "" : " (was " + curCount + " " + curId + ")");
            }
        }
        // [power] journalled for undo by PowerService.journalledTiles, which LinkServer wraps around this call (both chest halves).
        edits++;
        Watch w = watches.get(s);
        if (w != null) w.dirty = true;
        for (Watch o : watches.values()) if (o.world.equals(s.region.world()) && o.x == p[0] && o.y == p[1] && o.z == p[2]) o.dirty = true;
        plugin.getLogger().info(String.format("CONTAINER session=%s %s %s %d,%d,%d (%.2f ms)", s.id, done, blockId, p[0], p[1], p[2], (System.nanoTime() - t0) / 1e6));
    }

    /** Merges and orders a storage container's stacks (ContainerRules.sort); returns what happened. */
    private static String sort(Inventory inv) {
        List<ContainerRules.Stack<ItemStack>> stacks = new ArrayList<>();
        ItemStack[] contents = inv.getContents();
        int filled = 0;
        for (ItemStack it : contents) {
            if (it == null || it.isEmpty()) continue;
            filled++;
            String meta = it.hasItemMeta() ? it.getItemMeta().getAsComponentString() : "";
            stacks.add(new ContainerRules.Stack<>(it.getType().getKey().asString(), meta, it.getAmount(), it.getMaxStackSize(), it));
        }
        List<ContainerRules.Stack<ItemStack>> sorted = ContainerRules.sort(stacks);
        ItemStack[] out = new ItemStack[contents.length];
        for (int i = 0; i < sorted.size() && i < out.length; i++) out[i] = sorted.get(i).handle().asQuantity(sorted.get(i).count());
        inv.setContents(out);
        return "sort " + filled + " stacks -> " + sorted.size();
    }

    /** The container message for a view (double chests as one inventory). */
    private JsonObject view(Session s, Container c, int[] p, Long refSeq) {
        Inventory inv = c.getInventory();
        String blockId = c.getBlock().getType().getKey().asString();
        int[] other = null;
        if (inv instanceof DoubleChestInventory d) {
            Location l = d.getLeftSide().getLocation(), r = d.getRightSide().getLocation();
            Location o = l != null && l.getBlockX() == p[0] && l.getBlockY() == p[1] && l.getBlockZ() == p[2] ? r : l;
            if (o != null) other = new int[] { o.getBlockX(), o.getBlockY(), o.getBlockZ() };
        }
        Component name = c.customName();
        String title = name != null ? PlainTextComponentSerializer.plainText().serialize(name) : ContainerRules.title(blockId, other != null);
        List<OperatorMessages.Slot> items = new ArrayList<>();
        ItemStack[] contents = inv.getContents();
        for (int i = 0; i < contents.length; i++) {
            ItemStack it = contents[i];
            if (it != null && !it.isEmpty()) items.add(new OperatorMessages.Slot(i, it.getType().getKey().asString(), it.getAmount()));
        }
        return OperatorMessages.container(refSeq, p, blockId, title, inv.getSize(), items, other, containers.equals("edit") ? "edit" : "view");
    }

    /** What a view shows, without the envelope and ref_seq (to notice changes). */
    private static String signature(JsonObject view) {
        JsonObject c = view.deepCopy();
        c.remove("ref_seq");
        return c.toString();
    }

    /** Both halves of a double chest, else the block itself, when it has a lid. */
    private static List<Block> lidBlocks(Container c) {
        List<Block> l = new ArrayList<>();
        if (!(c instanceof Lidded)) return l;
        l.add(c.getBlock());
        if (c.getInventory() instanceof DoubleChestInventory d)
            for (Inventory side : new Inventory[] { d.getLeftSide(), d.getRightSide() }) {
                Location loc = side.getLocation();
                if (loc != null && !loc.getBlock().equals(c.getBlock())) l.add(loc.getBlock());
            }
        return l;
    }

    private static String key(Block b) { return b.getWorld().getName() + "/" + b.getX() + "/" + b.getY() + "/" + b.getZ(); }

    private void openLid(Block b) {
        String k = key(b);
        int n = lidViews.merge(k, 1, Integer::sum);
        if (n == 1 && b.getState() instanceof Lidded l) {
            try { l.open(); } catch (RuntimeException ex) { plugin.getLogger().fine("lid open: " + ex); }
            plugin.markDirty(b.getWorld().getName(), b.getX(), b.getY(), b.getZ());   // a barrel's open property
        }
    }

    private void closeLid(Block b) {
        String k = key(b);
        Integer n = lidViews.get(k);
        if (n == null) return;
        if (n > 1) { lidViews.put(k, n - 1); return; }
        lidViews.remove(k);
        if (!b.getWorld().isChunkLoaded(b.getX() >> 4, b.getZ() >> 4)) return;
        if (b.getState() instanceof Lidded l) {
            try { l.close(); } catch (RuntimeException ex) { plugin.getLogger().fine("lid close: " + ex); }
            plugin.markDirty(b.getWorld().getName(), b.getX(), b.getY(), b.getZ());
        }
    }

    /** Ends a session's view (the lid closes when nobody else looks); with a reason the headset is told. */
    private void closeWatch(Session s, String reason) {
        Watch w = watches.remove(s);
        if (w == null) return;
        for (Block b : w.lids) closeLid(b);
        if (reason != null) plugin.send(s, OperatorMessages.containerClosed(new int[] { w.x, w.y, w.z }, reason));
    }

    /** Every WATCH_TICKS ticks: re-send views that changed, end views of containers that are gone. */
    void tick(long tickCount) {
        if (watches.isEmpty()) return;
        for (Session s : new ArrayList<>(watches.keySet())) {
            Watch w = watches.get(s);
            if (s.state != Session.State.READY) { closeWatch(s, null); continue; }
            if (!w.dirty && tickCount % WATCH_TICKS != 0) continue;
            w.dirty = false;
            int[] p = { w.x, w.y, w.z };
            if (s.region == null || !s.region.world().equals(w.world)) { closeWatch(s, "the table moved to another world"); continue; }
            Object at = containerAt(s, p);
            if (at instanceof String why) {
                closeWatch(s, why.startsWith("position is outside") ? "the container is no longer on the table" : why.startsWith("no container") ? "the container is gone" : why);
                continue;
            }
            if (containerGate(s, false) != null) { closeWatch(s, containerGate(s, false)); continue; }
            JsonObject view = view(s, (Container) at, p, null);
            String sig = signature(view);
            if (sig.equals(w.sent)) continue;
            w.sent = sig;
            plugin.send(s, view);
        }
    }

    void onSessionClosed(Session s) { closeWatch(s, null); }

    /** Plugin stop or reload: every lid closes. */
    void shutdown() {
        for (Session s : new ArrayList<>(watches.keySet())) closeWatch(s, null);
        lidViews.clear();
    }

    // ---------- signs ----------

    void onSignEdit(Session s, OperatorMessages.SignEdit e, Long refSeq) {
        if (s.state != Session.State.READY) return;
        int[] p = e.pos();
        if (!s.canInteract) { deny(s, "sign_edit", "interact_denied", plugin.interact.allowed() ? "ask for interact_v1 in hello.caps" : plugin.interact.offReason(), refSeq, p); return; }
        if (!signs) { deny(s, "sign_edit", "interact_denied", "editing signs is off on this server (interact.signs)", refSeq, p); return; }
        RegionSpec r = s.region;
        if (r == null || !r.containsWorld(p[0], p[1], p[2])) { deny(s, "sign_edit", "out_of_region", "position is outside the table's region", refSeq, p); return; }
        World w = Bukkit.getWorld(r.world());
        if (w == null || !w.isChunkLoaded(p[0] >> 4, p[2] >> 4)) { deny(s, "sign_edit", "interact_denied", "chunk not loaded", refSeq, p); return; }
        Block b = w.getBlockAt(p[0], p[1], p[2]);
        if (!(b.getState() instanceof Sign sign)) { deny(s, "sign_edit", "interact_denied", "no sign there (" + EditRules.label(b.getBlockData().getAsString()) + ")", refSeq, p); return; }
        if (sign.isWaxed()) { deny(s, "sign_edit", "interact_denied", "the sign is waxed (honeycomb): it can't be edited", refSeq, p); return; }
        long t0 = System.nanoTime();
        Side side = e.side().equals("back") ? Side.BACK : Side.FRONT;
        List<Component> lines = new ArrayList<>();
        for (String l : e.lines()) lines.add(Component.text(l));
        if (signEvent) {
            // No player: the headset isn't one. Protection and logging plugins see the change and may alter or cancel it;
            // a listener that needs a player fails inside its own handler (Bukkit logs it) and doesn't block the edit.
            SignChangeEvent ev = new SignChangeEvent(b, null, lines, side);
            try { Bukkit.getPluginManager().callEvent(ev); } catch (RuntimeException ex) { plugin.getLogger().warning("SignChangeEvent: " + ex); }
            if (ev.isCancelled()) { deny(s, "sign_edit", "interact_denied", "another plugin refused the sign change (protection)", refSeq, p); return; }
            lines = new ArrayList<>(ev.lines());
        }
        if (!(b.getState() instanceof Sign live)) { deny(s, "sign_edit", "interact_denied", "the sign is gone", refSeq, p); return; }
        SignSide ss = live.getSide(side);
        for (int i = 0; i < 4; i++) ss.line(i, i < lines.size() && lines.get(i) != null ? lines.get(i) : Component.empty());
        if (e.color() != null) ss.setColor(DyeColor.valueOf(e.color().toUpperCase(Locale.ROOT)));
        if (e.glowing() != null) ss.setGlowingText(e.glowing());
        live.update(true, false);
        // [power] journalled for undo by PowerService.journalledTiles, which LinkServer wraps around this call.
        signEdits++;
        plugin.markDirty(w.getName(), p[0], p[1], p[2]);   // block_entities reads the chunk again next tick
        plugin.getLogger().info(String.format("SIGN session=%s %s %d,%d,%d %s %s%s%s (%.2f ms)", s.id, w.getName(), p[0], p[1], p[2], e.side(),
            String.join(" | ", e.lines()), e.color() == null ? "" : " color=" + e.color(), e.glowing() == null ? "" : " glowing=" + e.glowing(), (System.nanoTime() - t0) / 1e6));
    }

    private void deny(Session s, String op, String code, String why, Long refSeq, int[] p) {
        refused++;
        plugin.getLogger().info(String.format("%s_DENIED session=%s %d,%d,%d %s: %s", op.startsWith("sign") ? "SIGN" : "CONTAINER", s.id, p[0], p[1], p[2], code, why));
        plugin.send(s, OperatorMessages.error(code, why, refSeq, op, p));
    }

    String stats() {
        return String.format("operator: %d bookmarks, %d container views (%d open), %d container edits, %d sign edits, %d refused; containers %s, signs %s",
            bookmarks.size(), views, watches.size(), edits, signEdits, refused, containers, signs ? "on" : "off");
    }
}
