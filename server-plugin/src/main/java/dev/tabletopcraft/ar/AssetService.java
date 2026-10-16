package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.io.IOException;
import java.io.InputStream;
import java.net.Inet6Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Collection;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.function.BiConsumer;
import java.util.function.Supplier;
import java.util.logging.Logger;

/**
 * [sound] Asset delivery (PROTOCOL.md "Asset delivery", config.yml {@code assets}): the server operator's OWN pack.zip,
 * entity_models.json and sounds.zip go to their own headsets over the WebSocket instead of adb. [setup] Two sources, by
 * name: {@code assets.dir} (with {@code assets.serve}, files the operator made by hand) first, then the files
 * {@link GameAssets} builds from this PC's Minecraft install ({@code assets.auto}, on by default). Only for trusted
 * sessions (paired headsets, or ones that presented {@code auth.token}) from a private, link-local or loopback address,
 * asking for {@code assets_v1}.
 * <p>
 * Files are hashed (SHA-256) on a worker at start and offered after welcome ({@code assets}); the headset pulls them with
 * {@code asset_get} and each request is answered with one binary frame ({@link #frame}), paced to
 * {@code assets.max-mb-per-second} per session on the same worker. Only the three names in {@link #NAMES} are ever read.
 */
final class AssetService {
    static final String CAP = "assets_v1";
    /** The only files served, in offer order (small first). */
    static final List<String> NAMES = List.of("entity_models.json", "sounds.zip", "pack.zip");
    static final int CHUNK = 256 * 1024, MAX_CHUNK = 1 << 20, MAX_IN_FLIGHT = 8;
    static final long MAX_FILE = Integer.MAX_VALUE;
    static final byte[] MAGIC = "TTA1".getBytes(StandardCharsets.US_ASCII);

    record FileInfo(String name, Path path, long size, long mtime, String sha256) {}

    /** A parsed binary frame (tests and tools). */
    record Chunk(String name, long offset, byte[] data) {}

    private final Logger log;
    private final BiConsumer<Session, JsonObject> send;
    private final Supplier<Collection<Session>> sessions;
    private volatile boolean serve;
    private volatile Path dir;
    private volatile GameAssets auto;   // [setup] null or off: only assets.dir
    private volatile double bytesPerSecond = 8e6;
    private volatile Map<String, FileInfo> files = Map.of();
    private volatile boolean ready;
    private ScheduledExecutorService worker;
    volatile long sentBytes;
    volatile int sentFiles, refused;

    AssetService(Logger log, BiConsumer<Session, JsonObject> send, Supplier<Collection<Session>> sessions) {
        this.log = log;
        this.send = send;
        this.sessions = sessions;
    }

    /** Start or reload: read the switches and hash the files on the worker. auto: the zero-config builder (may be off). */
    void configure(boolean serve, String dir, GameAssets auto, double mbPerSecond) {
        stop();
        this.serve = serve;
        this.auto = auto;
        this.dir = dir == null || dir.isBlank() ? null : Path.of(dir.trim()).toAbsolutePath().normalize();
        this.bytesPerSecond = Math.max(0.1, mbPerSecond) * 1e6;
        files = Map.of();
        ready = false;
        String why = why();
        if (why != null) { if (serve) log.warning("ASSETS assets.serve is on but nothing is served: " + why); return; }
        if (serve && (dir == null || !Files.isDirectory(this.dir))) log.warning("ASSETS assets.dir " + this.dir + " is not a folder: only the automatic files are served");
        worker = Executors.newSingleThreadScheduledExecutor(r -> { Thread t = new Thread(r, "tabletop-assets"); t.setDaemon(true); return t; });
        worker.execute(this::scan);
    }

    /** [setup] GameAssets built or changed something: hash again and offer anew. */
    void rescan() {
        ScheduledExecutorService w = worker;
        if (w != null && !w.isShutdown()) w.execute(this::scan);
    }

    void stop() {
        if (worker != null) { worker.shutdownNow(); worker = null; }
        ready = false;
    }

    /** Null when headsets may be offered files, else why not (config). */
    String why() {
        boolean manual = serve && dir != null && Files.isDirectory(dir);
        if (manual || (auto != null && auto.enabled())) return null;
        if (!serve) return "assets.auto and assets.serve are false";
        if (dir == null) return "assets.dir is empty";
        return "assets.dir " + dir + " is not a folder";
    }

    /** [setup] Where the files come from, in lookup order. */
    private List<Path> sources() {
        List<Path> l = new ArrayList<>();
        if (serve && dir != null) l.add(dir);
        GameAssets a = auto;
        if (a != null && a.enabled() && a.dir() != null) l.add(a.dir());
        return l;
    }

    boolean ready() { return ready; }
    Map<String, FileInfo> files() { return files; }

    /** hello asked for assets_v1: granted when serving, the session is trusted (paired, or presented auth.token) and on the LAN. */
    void grant(Session s) {
        String why = why();
        InetAddress a = s.conn.getRemoteSocketAddress() instanceof InetSocketAddress isa ? isa.getAddress() : null;
        if (why == null && !s.trusted) why = "the headset isn't paired (/tabletop pair <code>) and didn't present auth.token";
        if (why == null && !lan(a)) why = "the headset's address " + a + " is not a private, link-local or loopback address";
        if (why == null) { s.canAssets = true; return; }
        if (serve || (auto != null && auto.enabled())) { refused++; log.info("ASSETS not for session " + s.id + ": " + why); }
    }

    /** After welcome: the offer, now or as soon as the files are hashed. */
    void offer(Session s) {
        if (!s.canAssets || !ready) return;
        s.assetsOffered = true;
        send.accept(s, message());
    }

    JsonObject message() {
        JsonObject o = Protocol.body("assets");
        o.addProperty("chunk", CHUNK);
        JsonArray a = new JsonArray();
        for (FileInfo f : files.values()) {
            JsonObject j = new JsonObject();
            j.addProperty("name", f.name());
            j.addProperty("size", f.size());
            j.addProperty("sha256", f.sha256());
            a.add(j);
        }
        o.add("files", a);
        GameAssets g = auto;   // [setup] what the automatic files are doing (optional fields, PROTOCOL.md "Asset delivery")
        if (g != null && g.enabled()) {
            o.addProperty("state", switch (g.state()) { case SEARCHING, BUILDING -> "building"; case MISSING -> "missing"; case FAILED -> "failed"; default -> "ready"; });
            if (!g.note().isEmpty() && g.state() != GameAssets.State.READY) o.addProperty("note", g.note());
        }
        return o;
    }

    private void scan() {
        long t0 = System.nanoTime();
        Map<String, FileInfo> m = new LinkedHashMap<>();
        List<Path> src = sources();
        Map<String, FileInfo> old = files;
        for (String n : NAMES) {
            Path p = null;
            for (Path d : src) if (Files.isRegularFile(d.resolve(n))) { p = d.resolve(n); break; }
            if (p == null) continue;
            try {
                FileInfo was = old.get(n);   // unchanged since the last scan: keep its hash
                if (was != null && was.path().equals(p) && Files.size(p) == was.size() && Files.getLastModifiedTime(p).toMillis() == was.mtime()) { m.put(n, was); continue; }
                long size = Files.size(p);
                if (size <= 0 || size > MAX_FILE) { log.warning("ASSETS skipping " + p + ": size " + size); continue; }
                m.put(n, new FileInfo(n, p, size, Files.getLastModifiedTime(p).toMillis(), sha256(p)));
            } catch (IOException e) { log.warning("ASSETS can't read " + p + ": " + e.getMessage()); }
        }
        files = Map.copyOf(m);
        ready = true;
        List<String> desc = new ArrayList<>();
        for (FileInfo f : m.values()) desc.add(String.format("%s (%.1f MB)", f.name(), f.size() / 1e6));
        log.info(String.format("ASSETS serving %s from %s to paired headsets on the LAN, %.0f MB/s each (hashed in %.1f s)",
            desc.isEmpty() ? "nothing yet (no pack.zip, entity_models.json or sounds.zip)" : String.join(", ", desc), src,
            bytesPerSecond / 1e6, (System.nanoTime() - t0) / 1e9));
        Collection<Session> all = sessions.get();
        // Sessions past onReady (it set their region after queueing welcome): the offer follows their welcome.
        if (all != null) for (Session s : all) if (s.canAssets && s.state == Session.State.READY && s.region != null) offer(s);
    }

    /** asset_get (websocket thread): checked, then paced onto the worker. */
    void onGet(Session s, JsonObject m, Long refSeq) {
        String name = m.has("name") && m.get("name").isJsonPrimitive() ? m.get("name").getAsString() : "";
        if (!s.canAssets) {
            String why = why() != null ? why() : !s.trusted ? "this headset isn't paired: pair it (/tabletop pair <code>) to get the game files"
                : "ask for assets_v1 in hello.caps (from the LAN)";
            s.send(error("asset_denied", why, name, refSeq));
            return;
        }
        FileInfo f = files.get(name);
        if (f == null) { s.send(error("not_found", "no " + name + " offered", name, refSeq)); return; }
        long offset = m.has("offset") ? m.get("offset").getAsLong() : -1;
        long length = m.has("length") ? m.get("length").getAsLong() : CHUNK;
        if (offset < 0 || offset >= f.size() || length < 1 || length > MAX_CHUNK) {
            s.send(error("bad_message", "offset must be 0.." + (f.size() - 1) + " and length 1.." + MAX_CHUNK, name, refSeq));
            return;
        }
        int len = (int) Math.min(length, f.size() - offset);
        if (s.assetsInFlight.incrementAndGet() > MAX_IN_FLIGHT) {
            s.assetsInFlight.decrementAndGet();
            s.send(error("rate_limited", "more than " + MAX_IN_FLIGHT + " asset_get in flight", name, refSeq));
            return;
        }
        long delay;
        synchronized (s) {
            long now = System.nanoTime(), start = Math.max(now, s.assetsNextNs);
            s.assetsNextNs = start + (long) (len * 1e9 / bytesPerSecond);
            delay = start - now;
        }
        ScheduledExecutorService w = worker;
        if (w == null || w.isShutdown()) { s.assetsInFlight.decrementAndGet(); s.send(error("asset_denied", "asset delivery stopped", name, refSeq)); return; }
        w.schedule(() -> sendChunk(s, f, offset, len, refSeq), delay, TimeUnit.NANOSECONDS);
    }

    private void sendChunk(Session s, FileInfo f, long offset, int len, Long refSeq) {
        s.assetsInFlight.decrementAndGet();
        if (!s.conn.isOpen()) return;
        try {
            if (!Files.isRegularFile(f.path()) || Files.size(f.path()) != f.size() || Files.getLastModifiedTime(f.path()).toMillis() != f.mtime()) {
                // The operator replaced the file: hash it again (once) and offer it anew (after the error, so the client
                // drops the old download first and starts the new one from the offer).
                s.send(error("not_found", f.name() + " changed on the server: a new offer follows", f.name(), refSeq));
                if (files.get(f.name()) == f) scan();
                return;
            }
            byte[] name = f.name().getBytes(StandardCharsets.US_ASCII);
            ByteBuffer buf = ByteBuffer.allocate(13 + name.length + len);
            header(buf, name, offset);
            try (FileChannel ch = FileChannel.open(f.path(), StandardOpenOption.READ)) {
                long pos = offset;
                while (buf.hasRemaining()) {
                    int n = ch.read(buf, pos);
                    if (n < 0) throw new IOException("file ended early");
                    pos += n;
                }
            }
            buf.flip();
            if (offset == 0) s.assetsStartNs.put(f.name(), System.nanoTime());
            s.conn.send(buf);
            sentBytes += len;
            if (offset + len == f.size()) {
                sentFiles++;
                Long t0 = s.assetsStartNs.remove(f.name());
                double secs = t0 == null ? 0 : (System.nanoTime() - t0) / 1e9;
                log.info(String.format("ASSETS session=%s sent %s %.1f MB in %.1f s (%.1f MB/s) to %s", s.id, f.name(), f.size() / 1e6, secs,
                    secs > 0 ? f.size() / 1e6 / secs : 0, s.conn.getRemoteSocketAddress()));
            }
        } catch (IOException | RuntimeException e) {
            s.send(error("internal", "reading " + f.name() + ": " + e.getMessage(), f.name(), refSeq));
        }
    }

    static JsonObject error(String code, String message, String name, Long refSeq) {
        JsonObject o = Protocol.error(code, message, refSeq, false);
        o.addProperty("op", "asset_get");
        o.addProperty("name", name);
        return o;
    }

    /** "TTA1", the name's length and ASCII bytes, the offset (8 bytes, big-endian); the data follows. */
    static void header(ByteBuffer buf, byte[] name, long offset) {
        if (name.length < 1 || name.length > 64) throw new IllegalArgumentException("asset name length " + name.length);
        buf.put(MAGIC).put((byte) name.length).put(name).putLong(offset);
    }

    /** A whole frame (tests). */
    static ByteBuffer frame(String name, long offset, byte[] data) {
        byte[] n = name.getBytes(StandardCharsets.US_ASCII);
        ByteBuffer b = ByteBuffer.allocate(13 + n.length + data.length);
        header(b, n, offset);
        b.put(data).flip();
        return b;
    }

    /** Parses a frame; throws on a bad magic or length. */
    static Chunk parse(ByteBuffer b) {
        byte[] magic = new byte[4];
        b.get(magic);
        if (!java.util.Arrays.equals(magic, MAGIC)) throw new IllegalArgumentException("not an asset frame");
        int n = b.get() & 0xff;
        if (n < 1 || n > 64 || b.remaining() < n + 8) throw new IllegalArgumentException("bad asset frame header");
        byte[] name = new byte[n];
        b.get(name);
        long offset = b.getLong();
        byte[] data = new byte[b.remaining()];
        b.get(data);
        return new Chunk(new String(name, StandardCharsets.US_ASCII), offset, data);
    }

    /** Private (10/8, 172.16/12, 192.168/16, IPv6 fc00::/7), link-local or loopback. */
    static boolean lan(InetAddress a) {
        if (a == null) return false;
        if (LanBeacon.allowed(a)) return true;
        return a instanceof Inet6Address && (a.getAddress()[0] & 0xfe) == 0xfc;
    }

    static String sha256(Path p) throws IOException {
        try (InputStream in = Files.newInputStream(p)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[1 << 16];
            for (int n; (n = in.read(buf)) > 0; ) md.update(buf, 0, n);
            return HexFormat.of().formatHex(md.digest());
        } catch (java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }
    }
}
