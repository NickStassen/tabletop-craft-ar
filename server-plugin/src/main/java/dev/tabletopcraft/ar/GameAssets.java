package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.Enumeration;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.logging.Logger;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipOutputStream;

/**
 * [setup] Zero-config game files (config.yml {@code assets.auto}, QUEST_SETUP.md "Store install"): finds this PC's own
 * Minecraft install for the server's version ({@link ClientJarLocator}) and builds, into
 * {@code plugins/TabletopCraftAR/assets/}, the three files the headset reads:
 * <ul>
 *   <li>{@code pack.zip}: the client jar's {@code assets/} folder (textures, models, block states; about 6 MB of 41);</li>
 *   <li>{@code entity_models.json}: {@code tools/entity_models/DumpEntityModels.java}, run in a <b>child JVM</b> (it loads the
 *       game's client classes, which must never be loaded into the server);</li>
 *   <li>{@code sounds.zip}: {@code tools/sounds/PackSounds.java} (the launcher's sound files), also in a child JVM.</li>
 * </ul>
 * The files are rebuilt when the client jar, its version JSON or the plugin version change ({@code build.json} keeps the
 * key). {@link AssetService} serves them only to paired (or token-holding) headsets on the LAN. Nothing is downloaded:
 * if the game isn't on this PC, the files simply don't exist and the log (and the headset) say how to fix it.
 */
final class GameAssets {
    enum State { OFF, SEARCHING, BUILDING, READY, MISSING, FAILED }

    static final String BUILD_FILE = "build.json";
    /** Build order: textures first (fast, and what the headset misses most), then models, then sounds. */
    static final List<String> ORDER = List.of("pack.zip", "entity_models.json", "sounds.zip");
    static final long CHILD_TIMEOUT_S = 300;

    private final Logger log;
    private final Runnable onChange;
    private volatile State state = State.OFF;
    private volatile String note = "";
    private volatile Path dir;
    private volatile ClientJarLocator.Found source;
    private volatile Process child;
    private ExecutorService worker;
    private String version = "", pluginVersion = "", classpath = "", javaCmd = "java";
    private Path override;
    /** The usual launcher folders (tests replace it). */
    java.util.function.Supplier<List<Path>> candidates = () -> ClientJarLocator.candidates(System.getProperty("os.name"), System.getenv(),
        Path.of(System.getProperty("user.home", ".")));

    /** onChange: called (on the worker) after a file was built or the state changed, so the files are hashed and offered. */
    GameAssets(Logger log, Runnable onChange) {
        this.log = log;
        this.onChange = onChange;
    }

    /** Start (or reload): auto off = nothing; else find the game and build what is missing or stale on a worker. */
    synchronized void configure(boolean auto, Path cacheDir, String mcVersion, Path override, String pluginVersion, String classpath, String javaCmd) {
        stop();
        this.dir = cacheDir;
        this.version = mcVersion;
        this.override = override;
        this.pluginVersion = pluginVersion;
        this.classpath = classpath;
        this.javaCmd = javaCmd;
        state = auto ? State.SEARCHING : State.OFF;
        note = "";
        if (!auto) return;
        worker = Executors.newSingleThreadExecutor(r -> { Thread t = new Thread(r, "tabletop-gameassets"); t.setDaemon(true); t.setPriority(Thread.MIN_PRIORITY); return t; });
        worker.execute(() -> run(false));
    }

    /** /tabletop assets rebuild: build all three again. */
    synchronized boolean rebuild() {
        if (worker == null || worker.isShutdown()) return false;
        worker.execute(() -> run(true));
        return true;
    }

    synchronized void stop() {
        Process p = child;
        if (p != null) p.destroyForcibly();
        if (worker != null) { worker.shutdownNow(); worker = null; }
    }

    State state() { return state; }
    /** One line for the headset and /tabletop status: what is missing and how to fix it, or what was built from where. */
    String note() { return note; }
    Path dir() { return dir; }
    ClientJarLocator.Found source() { return source; }
    boolean enabled() { return state != State.OFF; }

    private void run(boolean force) {
        Path d = dir;
        try {
            state = State.SEARCHING;
            List<Path> cands = candidates.get();
            ClientJarLocator.Found f = ClientJarLocator.find(version, override, cands);
            if (f == null) {
                source = null;
                state = State.MISSING;
                note = "No Minecraft " + version + " on the server's PC: start Minecraft: Java Edition " + version
                    + " once in the launcher there, then /tabletop assets rebuild";
                log.warning("ASSETS auto: no Minecraft " + version + " found (looked in " + ClientJarLocator.describe(override, cands)
                    + "). Launch Minecraft: Java Edition " + version + " once in the launcher on this PC, or point assets.minecraft-dir"
                    + " at the launcher folder (/tabletop assets dir <folder>), then /tabletop assets rebuild.");
                onChange.run();
                return;
            }
            source = f;
            Files.createDirectories(d);
            String key = sha1(f.jar()) + "/" + sha1(f.versionJson()) + "/" + pluginVersion;
            Map<String, String> built = readBuild(d.resolve(BUILD_FILE));
            List<String> todo = new ArrayList<>();
            for (String n : ORDER) if (force || !key.equals(built.get(n)) || !Files.isRegularFile(d.resolve(n))) todo.add(n);
            if (todo.isEmpty()) {
                state = State.READY;
                note = "built from " + f.jar();
                log.info("ASSETS auto: up to date, from " + f.jar() + " (" + d + ")");
                onChange.run();
                return;
            }
            state = State.BUILDING;
            note = "building " + String.join(", ", todo) + " from the server PC's Minecraft " + version;
            log.info("ASSETS auto: building " + String.join(", ", todo) + " from " + f.jar() + " into " + d + " (your own files: served only to paired headsets on the LAN)");
            onChange.run();
            List<String> failed = new ArrayList<>();
            for (String n : todo) {
                long t0 = System.nanoTime();
                try {
                    Path out = d.resolve(n);
                    switch (n) {
                        case "pack.zip" -> packZip(f.jar(), out);
                        case "entity_models.json" -> runTool("DumpEntityModels", f.launcherDir(), out);
                        case "sounds.zip" -> runTool("PackSounds", f.launcherDir(), out);
                        default -> throw new IllegalStateException(n);
                    }
                    built.put(n, key);
                    writeBuild(d.resolve(BUILD_FILE), built, f);
                    log.info(String.format("ASSETS auto: %s %.1f MB in %.1f s", n, Files.size(out) / 1e6, (System.nanoTime() - t0) / 1e9));
                    onChange.run();
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    return;
                } catch (Exception e) {
                    failed.add(n + " (" + e.getMessage() + ")");
                    log.warning("ASSETS auto: " + n + " failed: " + e.getMessage());
                }
            }
            state = failed.isEmpty() ? State.READY : State.FAILED;
            note = failed.isEmpty() ? "built from " + f.jar() : "could not build " + String.join(", ", failed);
            onChange.run();
        } catch (IOException | RuntimeException e) {
            state = State.FAILED;
            note = "asset build failed: " + e.getMessage();
            log.warning("ASSETS auto: " + note);
            onChange.run();
        }
    }

    /** pack.zip = the client jar's assets/ entries (the headset reads nothing else), written to a temp file and moved. */
    static int packZip(Path jar, Path out) throws IOException {
        Path tmp = out.resolveSibling(out.getFileName() + ".tmp");
        int n = 0;
        try (ZipFile in = new ZipFile(jar.toFile());
             ZipOutputStream zip = new ZipOutputStream(Files.newOutputStream(tmp))) {
            byte[] buf = new byte[64 * 1024];
            for (Enumeration<? extends ZipEntry> en = in.entries(); en.hasMoreElements(); ) {
                ZipEntry e = en.nextElement();
                String name = e.getName();
                if (e.isDirectory() || !name.startsWith("assets/") || name.contains("..")) continue;
                ZipEntry o = new ZipEntry(name);
                o.setTime(e.getTime());
                zip.putNextEntry(o);
                try (InputStream is = in.getInputStream(e)) {
                    for (int r; (r = is.read(buf)) > 0; ) zip.write(buf, 0, r);
                }
                zip.closeEntry();
                n++;
            }
        } catch (IOException e) {
            Files.deleteIfExists(tmp);
            throw e;
        }
        if (n == 0) { Files.deleteIfExists(tmp); throw new IOException(jar + " has no assets/ folder"); }
        Files.move(tmp, out, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        return n;
    }

    /** Runs one of the asset tools (default package, in the plugin jar) in a child JVM: tool launcherDir version out. */
    private void runTool(String main, Path launcherDir, Path out) throws IOException, InterruptedException {
        Path tmp = out.resolveSibling(out.getFileName() + ".part");
        Files.deleteIfExists(tmp);
        List<String> cmd = List.of(javaCmd, "-Xmx1g", "-Djava.awt.headless=true", "-cp", classpath, main,
            launcherDir.toString(), version, tmp.toString());
        Deque<String> tail = new ArrayDeque<>();
        int code = runChild(cmd, dir, tail);
        if (code != 0 || !Files.isRegularFile(tmp) || Files.size(tmp) == 0) {
            Files.deleteIfExists(tmp);
            throw new IOException(main + " exited " + code + (tail.isEmpty() ? "" : ": " + tail.peekLast()));
        }
        for (String l : tail) if (l.startsWith("WARN")) log.fine("ASSETS " + main + ": " + l);
        Files.move(tmp, out, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
    }

    /** Runs cmd in workDir, keeps the last 20 output lines in tail; destroyed after CHILD_TIMEOUT_S. */
    int runChild(List<String> cmd, Path workDir, Deque<String> tail) throws IOException, InterruptedException {
        ProcessBuilder pb = new ProcessBuilder(cmd).directory(workDir.toFile()).redirectErrorStream(true);
        Process p = pb.start();
        child = p;
        try {
            p.getOutputStream().close();
            Thread reader = new Thread(() -> {
                try (BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8))) {
                    for (String l; (l = r.readLine()) != null; ) synchronized (tail) { tail.add(l); if (tail.size() > 20) tail.poll(); }
                } catch (IOException ignored) { }
            }, "tabletop-gameassets-out");
            reader.setDaemon(true);
            reader.start();
            if (!p.waitFor(CHILD_TIMEOUT_S, TimeUnit.SECONDS)) { p.destroyForcibly(); throw new IOException("stopped after " + CHILD_TIMEOUT_S + " s"); }
            reader.join(2000);
            return p.exitValue();
        } finally {
            child = null;
            if (p.isAlive()) p.destroyForcibly();
        }
    }

    /** The java binary of this JVM (Java 25 on a Paper 26.3 server). */
    static String javaCommand() {
        return ProcessHandle.current().info().command().orElse(Path.of(System.getProperty("java.home"), "bin", "java").toString());
    }

    static Map<String, String> readBuild(Path p) {
        Map<String, String> m = new LinkedHashMap<>();
        try {
            if (!Files.isRegularFile(p)) return m;
            JsonObject o = JsonParser.parseString(Files.readString(p)).getAsJsonObject();
            JsonObject files = o.getAsJsonObject("files");
            if (files != null) for (var e : files.entrySet()) if (e.getValue().isJsonPrimitive()) m.put(e.getKey(), e.getValue().getAsString());
        } catch (IOException | RuntimeException ignored) { }
        return m;
    }

    private void writeBuild(Path p, Map<String, String> built, ClientJarLocator.Found f) throws IOException {
        JsonObject o = new JsonObject();
        o.addProperty("note", "Built by the TabletopCraftAR plugin from this PC's own Minecraft install. Never share these files.");
        o.addProperty("minecraft", version);
        o.addProperty("client_jar", f.jar().toString());
        JsonObject files = new JsonObject();
        for (var e : built.entrySet()) files.addProperty(e.getKey(), e.getValue());
        o.add("files", files);
        Path tmp = p.resolveSibling(BUILD_FILE + ".tmp");
        Files.writeString(tmp, o.toString());
        Files.move(tmp, p, StandardCopyOption.REPLACE_EXISTING);
    }

    static String sha1(Path p) throws IOException {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-1");
            try (InputStream in = Files.newInputStream(p)) {
                byte[] buf = new byte[1 << 16];
                for (int n; (n = in.read(buf)) > 0; ) md.update(buf, 0, n);
            }
            return HexFormat.of().formatHex(md.digest());
        } catch (java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }
    }
}
