package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.destroystokyo.paper.profile.PlayerProfile;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.scheduler.BukkitTask;

import java.util.*;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class TabletopPlugin extends JavaPlugin {
    public static final int[] MAX_DIMS = { 64, 384, 64 };

    private LinkServer link;
    private ExecutorService outbound;
    private final RegionReader reader = new RegionReader();
    private final GiantAvatar avatar = new GiantAvatar();
    private final ConcurrentLinkedQueue<Runnable> queue = new ConcurrentLinkedQueue<>();
    private record PoseSet(Session s, Pose head, Pose left, Pose right, long seq) {}
    private final AtomicReference<PoseSet> latestPose = new AtomicReference<>();
    private BukkitTask tickTask;
    private final Set<Long> tickets = new HashSet<>();
    private String ticketWorld;
    private boolean dirty;
    private long tickCount;
    private Session avatarOwner;
    private long avatarOwnerGoneMs = -1;

    @Override public void onEnable() {
        saveDefaultConfig();
        getServer().getPluginManager().registerEvents(new BlockListener(this), this);
        start();
    }

    @Override public void onDisable() { stop(); }

    private void start() {
        var c = getConfig();
        avatar.blockSizeM = c.getDouble("avatar.block-size-m", 0.01);
        avatar.maxScale = c.getDouble("avatar.max-scale", 16.0);
        avatar.userHeightM = c.getDouble("avatar.user-height-m", 1.75);
        avatar.skinPlayer = c.getString("avatar.skin-player", "");
        avatar.interpolationTicks = c.getInt("avatar.interpolation-ticks", 2);
        outbound = Executors.newSingleThreadExecutor(r -> { Thread t = new Thread(r, "tabletop-outbound"); t.setDaemon(true); return t; });
        link = new LinkServer(this, c.getString("bind.host", "0.0.0.0"), c.getInt("bind.port", 8765), c.getString("bind.path", "/tabletop"), c.getString("auth.token", ""));
        link.start();
        tickTask = Bukkit.getScheduler().runTaskTimer(this, this::tick, 1, 1);
    }

    private void stop() {
        if (tickTask != null) tickTask.cancel();
        avatar.remove();
        releaseTickets();
        if (link != null) {
            for (Session s : link.sessions()) s.conn.close(Protocol.CLOSE_SHUTDOWN, "plugin disabled");
            try { link.stop(1000); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
            link = null;
        }
        if (outbound != null) { outbound.shutdown(); try { outbound.awaitTermination(2, TimeUnit.SECONDS); } catch (InterruptedException ignored) {} }
    }

    // ---------- thread hand-off ----------
    void enqueue(Runnable r) { queue.add(r); }
    void offerPose(Session s, Pose h, Pose l, Pose r, long seq) { latestPose.set(new PoseSet(s, h, l, r, seq)); }
    private void send(Session s, JsonObject body) { if (outbound != null && !outbound.isShutdown()) outbound.execute(() -> s.send(body)); }

    void markDirty(String world, int x, int y, int z) {
        if (link == null) return;
        for (Session s : link.sessions()) if (s.region != null && s.region.world().equals(world) && s.region.containsWorld(x, y, z)) { dirty = true; return; }
    }

    RegionSpec defaultRegion() {
        var c = getConfig();
        return new RegionSpec(c.getString("region.world", "world"), c.getInt("region.origin.x"), c.getInt("region.origin.y", 60), c.getInt("region.origin.z"),
            c.getInt("region.dims.x", 32), c.getInt("region.dims.y", 64), c.getInt("region.dims.z", 32));
    }

    // ---------- main tick ----------
    private void tick() {
        tickCount++;
        Runnable r;
        while ((r = queue.poll()) != null) {
            try { r.run(); } catch (RuntimeException e) { getLogger().warning("command failed: " + e); }
        }
        applyPose();
        int safety = Math.max(1, getConfig().getInt("stream.safety-diff-ticks", 20));
        if (dirty || tickCount % safety == 0) { boolean wasDirty = dirty; dirty = false; if (wasDirty) reader.invalidate(); diffAll(); }
        if (tickCount % Math.max(1, getConfig().getInt("stream.players-every-ticks", 2)) == 0) sendPlayers();
        if (tickCount % 20 == 0) timeouts();
        if (tickCount % 10 == 0 && getConfig().getBoolean("debug.avatar-state", false) && avatar.spawned()) broadcast(avatar.state(true));
    }

    private Collection<Session> ready() {
        List<Session> l = new ArrayList<>();
        if (link != null) for (Session s : link.sessions()) if (s.state == Session.State.READY && s.region != null) l.add(s);
        return l;
    }

    private void broadcast(JsonObject body) { for (Session s : ready()) send(s, body.deepCopy()); }

    void onReady(Session s) {
        JsonObject w = Protocol.body("welcome");
        w.addProperty("protocol", Protocol.VERSION);
        w.addProperty("server", "tabletop-plugin");
        w.addProperty("server_version", getPluginMeta().getVersion());
        w.addProperty("mc_version", Bukkit.getMinecraftVersion());
        w.addProperty("session", s.id);
        RegionSpec r = defaultRegion();
        w.addProperty("world", r.world());
        JsonObject reg = new JsonObject();
        reg.add("origin", Protocol.ints(r.ox(), r.oy(), r.oz()));
        reg.add("dims", Protocol.ints(r.sx(), r.sy(), r.sz()));
        w.add("region", reg);
        JsonObject lim = new JsonObject();
        lim.add("max_dims", Protocol.ints(MAX_DIMS));
        lim.addProperty("pose_hz_max", 60);
        lim.addProperty("player_hz", 20 / Math.max(1, getConfig().getInt("stream.players-every-ticks", 2)));
        w.add("limits", lim);
        w.addProperty("avatar_supported", true);
        send(s, w);
        subscribe(s, r, null);
    }

    void onSubscribe(Session s, String world, int[] origin, int[] dims, Long refSeq, boolean follow) {
        if (s.state != Session.State.READY) return;
        String wn = world == null ? defaultRegion().world() : world;
        World w = Bukkit.getWorld(wn);
        if (w == null) { send(s, Protocol.error("world_not_found", "no world " + wn, refSeq, false)); return; }
        for (int i = 0; i < 3; i++) if (dims[i] < 1 || dims[i] > MAX_DIMS[i]) {
            send(s, Protocol.error("region_too_large", String.format("dims %dx%dx%d exceed max %dx%dx%d", dims[0], dims[1], dims[2], MAX_DIMS[0], MAX_DIMS[1], MAX_DIMS[2]), refSeq, false));
            return;
        }
        int y0 = Math.max(origin[1], w.getMinHeight());
        int y1 = Math.min(origin[1] + dims[1], w.getMaxHeight());
        if (y1 <= y0) { send(s, Protocol.error("region_too_large", "region is outside the build height", refSeq, false)); return; }
        if (follow) send(s, Protocol.error("unsupported", "follow is not implemented in v0.1", refSeq, false));
        subscribe(s, new RegionSpec(wn, origin[0], y0, origin[2], dims[0], y1 - y0, dims[2]), refSeq);
    }

    private void subscribe(Session s, RegionSpec r, Long refSeq) {
        s.region = r;
        updateTickets();
        sendSnapshot(s);
    }

    private void sendSnapshot(Session s) {
        int[] grid = reader.read(s.region);
        s.lastGrid = grid;
        s.snapshotId++;
        s.resetPalette();
        Palette pal = reader.palette();
        int air = pal.id("minecraft:air");
        boolean hasAir = false;
        for (int g : grid) if (g == air) { hasAir = true; break; }
        if (hasAir) s.localIndex(air, pal, null); // index 0 = air when present
        int[] local = new int[grid.length];
        for (int i = 0; i < grid.length; i++) local[i] = s.localIndex(grid[i], pal, null);
        List<String> palette = List.copyOf(s.palette);
        RegionSpec r = s.region;
        int id = s.snapshotId;
        // Encode + serialize off the main thread; the outbound executor keeps message order.
        if (outbound != null) outbound.execute(() -> {
            JsonObject msg = Protocol.chunkSnapshot(id, r, palette, BlockCodec.encode(local));
            s.send(msg);
        });
    }

    private void diffAll() {
        for (Session s : ready()) {
            if (s.lastGrid == null) continue;
            int[] g;
            try { g = reader.read(s.region); } catch (IllegalStateException e) { continue; }
            int[] old = s.lastGrid;
            if (g == old) continue;
            RegionSpec r = s.region;
            List<int[]> changed = new ArrayList<>();
            for (int i = 0; i < g.length; i++) if (g[i] != old[i]) changed.add(new int[] { i, g[i] });
            s.lastGrid = g;
            if (changed.isEmpty()) continue;
            if (changed.size() > 4096) { sendSnapshot(s); continue; }
            List<String> added = new ArrayList<>();
            List<Protocol.Change> out = new ArrayList<>();
            for (int[] c : changed) {
                int i = c[0];
                int lx = i % r.sx(), lz = (i / r.sx()) % r.sz(), ly = i / (r.sx() * r.sz());
                out.add(new Protocol.Change(lx, ly, lz, s.localIndex(c[1], reader.palette(), added)));
            }
            send(s, Protocol.blockUpdate(s.snapshotId, added, out));
        }
    }

    private void sendPlayers() {
        int margin = getConfig().getInt("stream.players-margin", 16);
        for (Session s : ready()) {
            RegionSpec r = s.region;
            World w = Bukkit.getWorld(r.world());
            if (w == null) continue;
            com.google.gson.JsonArray arr = new com.google.gson.JsonArray();
            for (Player p : w.getPlayers()) {
                Location l = p.getLocation();
                if (l.getX() < r.ox() - margin || l.getX() > r.ox() + r.sx() + margin || l.getZ() < r.oz() - margin || l.getZ() > r.oz() + r.sz() + margin) continue;
                JsonObject e = new JsonObject();
                e.addProperty("uuid", p.getUniqueId().toString());
                e.addProperty("name", p.getName());
                e.add("pos", Protocol.doubles(l.getX(), l.getY(), l.getZ()));
                e.addProperty("yaw", Math.round(l.getYaw() * 10) / 10.0);
                e.addProperty("pitch", Math.round(l.getPitch() * 10) / 10.0);
                e.addProperty("sneaking", p.isSneaking());
                if (s.skinSent.add(p.getUniqueId())) {
                    PlayerProfile prof = p.getPlayerProfile();
                    var skin = prof.getTextures().getSkin();
                    e.addProperty("skin", skin == null ? null : skin.toString());
                }
                arr.add(e);
            }
            if (arr.isEmpty() && !s.lastPlayersNonEmpty) continue;
            s.lastPlayersNonEmpty = !arr.isEmpty();
            JsonObject o = Protocol.body("player_positions");
            o.add("players", arr);
            send(s, o);
        }
    }

    private void timeouts() {
        long now = System.currentTimeMillis();
        if (link != null) for (Session s : link.sessions()) {
            if (s.state == Session.State.AWAIT_HELLO && now - s.openedMs > 5000) s.conn.close(Protocol.CLOSE_HELLO_TIMEOUT, "hello timeout");
            else if (s.state == Session.State.READY && now - s.lastInboundMs > 15000) s.conn.close(Protocol.CLOSE_IDLE, "idle timeout");
        }
        if (avatarOwnerGoneMs > 0 && now - avatarOwnerGoneMs > getConfig().getLong("avatar.despawn-after-disconnect-ms", 10000)) {
            avatarOwnerGoneMs = -1;
            if (avatar.spawned()) { avatar.remove(); avatar.enabled = false; getLogger().info("Avatar despawned (owner disconnected)"); }
        }
    }

    void onSessionClosed(Session s) {
        if (s == avatarOwner) avatarOwnerGoneMs = System.currentTimeMillis();
        updateTickets();
    }

    // ---------- avatar ----------
    void onAvatarConfig(Session s, AvatarConfig c, Long refSeq) {
        if (c.skinPlayer() != null) avatar.skinPlayer = c.skinPlayer();
        if (c.model() != null) avatar.model = c.model();
        if (c.blockSizeM() != null) avatar.blockSizeM = c.blockSizeM();
        if (c.maxScale() != null) avatar.maxScale = c.maxScale();
        if (c.showHands() != null) avatar.showHands = c.showHands();
        if (c.nameTagPresent()) avatar.nameTag = c.nameTag();
        if (c.skinUrl() != null) send(s, Protocol.error("unsupported", "skin_url is not implemented in v0.1; use skin_player", refSeq, false));
        boolean respawn = avatar.spawned();
        if (c.enabled() != null) avatar.enabled = c.enabled();
        avatarOwner = s; avatarOwnerGoneMs = -1;
        if (!avatar.enabled) avatar.remove();
        else if (respawn || !avatar.spawned()) spawnAvatarAtAnchor();
        send(s, avatar.state(getConfig().getBoolean("debug.avatar-state", false)));
    }

    private void spawnAvatarAtAnchor() {
        RegionSpec r = defaultRegion();
        World w = Bukkit.getWorld(r.world());
        if (w == null) return;
        avatar.spawn(w, new Location(w, r.ox() + r.sx() / 2.0, r.oy(), r.oz() + r.sz() / 2.0));
        getLogger().info(String.format("Avatar spawned: scale %.2f (%s)", avatar.scale(), avatar.note == null ? "no clamp" : avatar.note));
    }

    private void applyPose() {
        PoseSet ps = latestPose.getAndSet(null);
        if (ps == null || ps.s().region == null) return;
        if (!avatar.enabled && getConfig().getBoolean("avatar.spawn-on-first-pose", true) && !avatar.spawned()) {
            avatar.enabled = true; avatarOwner = ps.s(); spawnAvatarAtAnchor();
            send(ps.s(), avatar.state(getConfig().getBoolean("debug.avatar-state", false)));
        }
        if (!avatar.spawned()) return;
        RegionSpec r = ps.s().region;
        World w = Bukkit.getWorld(r.world());
        if (w == null) return;
        double bs = avatar.blockSizeM;
        double[] hp = ps.head().toWorld(r.ox(), r.oy(), r.oz(), bs);
        double[] lp = ps.left() == null ? null : ps.left().toWorld(r.ox(), r.oy(), r.oz(), bs);
        double[] rp = ps.right() == null ? null : ps.right().toWorld(r.ox(), r.oy(), r.oz(), bs);
        avatar.apply(w, hp, ps.head(), lp, ps.left(), rp, ps.right(), ps.seq());
        if (getConfig().getBoolean("debug.log-poses", false))
            getLogger().info(String.format("pose seq=%d head=%.2f,%.2f,%.2f yaw=%.1f", ps.seq(), hp[0], hp[1], hp[2], ps.head().yawDeg()));
    }

    // ---------- chunk tickets ----------
    private void updateTickets() {
        releaseTickets();
        Set<RegionSpec> regions = new HashSet<>();
        regions.add(defaultRegion());
        for (Session s : ready()) regions.add(s.region);
        for (RegionSpec r : regions) {
            World w = Bukkit.getWorld(r.world());
            if (w == null) continue;
            for (int cx = r.ox() >> 4; cx <= (r.ox() + r.sx() - 1) >> 4; cx++)
                for (int cz = r.oz() >> 4; cz <= (r.oz() + r.sz() - 1) >> 4; cz++) {
                    w.addPluginChunkTicket(cx, cz, this);
                }
        }
    }

    private void releaseTickets() { for (World w : Bukkit.getWorlds()) w.removePluginChunkTickets(this); }

    // ---------- commands ----------
    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        if (args.length == 0) return false;
        switch (args[0].toLowerCase(Locale.ROOT)) {
            case "status" -> {
                RegionSpec r = defaultRegion();
                sender.sendMessage(String.format("[tabletop] default region %s origin %d %d %d dims %d %d %d; clients %d; avatar %s scale %.2f; poses applied %d",
                    r.world(), r.ox(), r.oy(), r.oz(), r.sx(), r.sy(), r.sz(), ready().size(), avatar.spawned() ? "spawned" : "none", avatar.scale(), avatar.posesApplied));
            }
            case "setorigin" -> {
                int x, y, z; String world;
                if (args.length >= 4) {
                    try { x = Integer.parseInt(args[1]); y = Integer.parseInt(args[2]); z = Integer.parseInt(args[3]); }
                    catch (NumberFormatException e) { sender.sendMessage("[tabletop] usage: /tabletop setorigin [x y z]"); return true; }
                    world = sender instanceof Player p ? p.getWorld().getName() : defaultRegion().world();
                } else if (sender instanceof Player p) {
                    Location l = p.getLocation(); x = l.getBlockX(); y = l.getBlockY(); z = l.getBlockZ(); world = l.getWorld().getName();
                } else { sender.sendMessage("[tabletop] console must pass x y z"); return true; }
                getConfig().set("region.world", world);
                getConfig().set("region.origin.x", x); getConfig().set("region.origin.y", y); getConfig().set("region.origin.z", z);
                saveConfig();
                resubscribeAll();
                sender.sendMessage(String.format("[tabletop] origin set to %s %d %d %d", world, x, y, z));
            }
            case "setsize" -> {
                if (args.length < 4) { sender.sendMessage("[tabletop] usage: /tabletop setsize <dx> <dy> <dz>"); return true; }
                try {
                    int dx = Integer.parseInt(args[1]), dy = Integer.parseInt(args[2]), dz = Integer.parseInt(args[3]);
                    if (dx < 1 || dy < 1 || dz < 1 || dx > MAX_DIMS[0] || dy > MAX_DIMS[1] || dz > MAX_DIMS[2]) throw new NumberFormatException();
                    getConfig().set("region.dims.x", dx); getConfig().set("region.dims.y", dy); getConfig().set("region.dims.z", dz);
                } catch (NumberFormatException e) { sender.sendMessage("[tabletop] dims must be within 64 x 384 x 64"); return true; }
                saveConfig();
                resubscribeAll();
                sender.sendMessage("[tabletop] size updated");
            }
            case "avatar" -> {
                String sub = args.length > 1 ? args[1].toLowerCase(Locale.ROOT) : "status";
                switch (sub) {
                    case "spawn" -> { avatar.enabled = true; spawnAvatarAtAnchor(); sender.sendMessage("[tabletop] avatar spawned, scale " + avatar.scale()); }
                    case "remove" -> { avatar.enabled = false; avatar.remove(); sender.sendMessage("[tabletop] avatar removed"); }
                    case "blocksize" -> {
                        if (args.length < 3) { sender.sendMessage("[tabletop] usage: /tabletop avatar blocksize <metres per block>"); return true; }
                        try { double b = Double.parseDouble(args[2]); if (b < 0.002 || b > 0.1) throw new NumberFormatException(); avatar.blockSizeM = b; getConfig().set("avatar.block-size-m", b); saveConfig(); }
                        catch (NumberFormatException e) { sender.sendMessage("[tabletop] block size must be 0.002..0.1"); return true; }
                        if (avatar.spawned()) spawnAvatarAtAnchor();
                        sender.sendMessage("[tabletop] block size set");
                    }
                    default -> {
                        String json = Protocol.GSON.toJson(avatar.state(true));
                        sender.sendMessage("[tabletop] " + json);
                        getLogger().info("AVATAR_STATE " + json);
                        for (Entity e : avatar.entities()) {
                            Location l = e.getLocation();
                            getLogger().info(String.format("AVATAR_ENTITY %s id=%d %.2f %.2f %.2f yaw=%.1f", e.getType(), e.getEntityId(), l.getX(), l.getY(), l.getZ(), l.getYaw()));
                        }
                    }
                }
            }
            case "reload" -> { reloadConfig(); stop(); start(); sender.sendMessage("[tabletop] reloaded"); }
            default -> { return false; }
        }
        return true;
    }

    private void resubscribeAll() {
        RegionSpec r = defaultRegion();
        for (Session s : ready()) subscribe(s, r, null);
        updateTickets();
    }

    @Override
    public List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        if (args.length == 1) return List.of("status", "setorigin", "setsize", "avatar", "reload");
        if (args.length == 2 && args[0].equalsIgnoreCase("avatar")) return List.of("status", "spawn", "remove", "blocksize");
        return List.of();
    }
}
