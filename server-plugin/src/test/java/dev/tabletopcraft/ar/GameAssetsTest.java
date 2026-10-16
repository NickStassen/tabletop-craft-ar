package dev.tabletopcraft.ar;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.logging.Logger;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipOutputStream;

import static org.junit.jupiter.api.Assertions.*;

/** [setup] Zero-config game files: finding the launcher folder, pack.zip from the jar, the tools in a child JVM. */
class GameAssetsTest {
    @TempDir Path tmp;

    @Test void candidatesPerOs() {
        Path home = Path.of("/home/u");
        assertEquals(Path.of("C:/Users/u/AppData/Roaming", ".minecraft"),
            ClientJarLocator.candidates("Windows 11", Map.of("APPDATA", "C:/Users/u/AppData/Roaming"), home).get(0));
        assertEquals(home.resolve("Library/Application Support/minecraft"), ClientJarLocator.candidates("Mac OS X", Map.of(), home).get(0));
        List<Path> linux = ClientJarLocator.candidates("Linux", Map.of(), home);
        assertEquals(home.resolve(".minecraft"), linux.get(0));
        assertTrue(linux.contains(home.resolve(".var/app/com.mojang.Minecraft/.minecraft")), "the Flatpak launcher");
    }

    @Test void findsTheServersVersionOnly() throws IOException {
        Path a = tmp.resolve("a"), b = tmp.resolve("b");
        launcher(a, "26.1");
        launcher(b, "26.3");
        ClientJarLocator.Found f = ClientJarLocator.find("26.3", null, List.of(a, b));
        assertNotNull(f);
        assertEquals(b, f.launcherDir());
        assertEquals(b.resolve("versions/26.3/26.3.jar"), f.jar());
        assertNull(ClientJarLocator.find("26.4", null, List.of(a, b)), "never another version");
        // The override goes first and may name the launcher folder, the version folder or the jar itself.
        assertEquals(a, ClientJarLocator.find("26.1", a, List.of(b)).launcherDir());
        assertEquals(b, ClientJarLocator.find("26.3", b.resolve("versions/26.3"), List.of()).launcherDir());
        assertEquals(b, ClientJarLocator.find("26.3", b.resolve("versions/26.3/26.3.jar"), List.of()).launcherDir());
        // A version folder without its JSON (an interrupted download) doesn't count.
        Files.delete(a.resolve("versions/26.1/26.1.json"));
        assertNull(ClientJarLocator.find("26.1", null, List.of(a)));
    }

    @Test void packZipKeepsOnlyAssets() throws IOException {
        Path jar = launcher(tmp.resolve("mc"), "26.3").resolve("versions/26.3/26.3.jar");
        Path out = tmp.resolve("pack.zip");
        assertEquals(2, GameAssets.packZip(jar, out));
        try (ZipFile z = new ZipFile(out.toFile())) {
            List<String> names = new ArrayList<>();
            for (var e : Collections.list(z.entries())) names.add(e.getName());
            assertEquals(List.of("assets/minecraft/textures/block/stone.png", "assets/minecraft/blockstates/stone.json"), names);
            assertEquals("png", new String(z.getInputStream(z.getEntry("assets/minecraft/textures/block/stone.png")).readAllBytes(), StandardCharsets.UTF_8));
        }
        assertFalse(Files.exists(tmp.resolve("pack.zip.tmp")));
    }

    @Test void buildsEverythingItCanAndRebuildsOnlyWhenTheGameChanges() throws Exception {
        Path mc = launcher(tmp.resolve("mc"), "26.3");
        sounds(mc, "26.3");
        Path cache = tmp.resolve("cache");
        int[] changes = { 0 };
        GameAssets g = new GameAssets(Logger.getLogger("test"), () -> changes[0]++);
        g.candidates = List::of;
        g.configure(true, cache, "26.3", mc, "1.1.0", System.getProperty("java.class.path"), GameAssets.javaCommand());
        waitFor(g);
        // No client classes in the fake jar: entity models fail, the other two are built.
        assertEquals(GameAssets.State.FAILED, g.state());
        assertTrue(g.note().contains("entity_models.json"), g.note());
        assertTrue(Files.isRegularFile(cache.resolve("pack.zip")));
        try (ZipFile z = new ZipFile(cache.resolve("sounds.zip").toFile())) {
            assertNotNull(z.getEntry("assets/minecraft/sounds.json"));
            assertNotNull(z.getEntry("assets/minecraft/sounds/block/stone/break1.ogg"));
        }
        Map<String, String> built = GameAssets.readBuild(cache.resolve(GameAssets.BUILD_FILE));
        assertEquals(Set2.of("pack.zip", "sounds.zip"), built.keySet());
        assertTrue(changes[0] >= 2);
        long packTime = Files.getLastModifiedTime(cache.resolve("pack.zip")).toMillis();
        // Same game, same plugin: nothing is built again (only the failed one is tried).
        Thread.sleep(20);
        g.configure(true, cache, "26.3", mc, "1.1.0", System.getProperty("java.class.path"), GameAssets.javaCommand());
        waitFor(g);
        assertEquals(packTime, Files.getLastModifiedTime(cache.resolve("pack.zip")).toMillis());
        g.stop();
    }

    @Test void missingGameSaysHowToFixIt() throws Exception {
        GameAssets g = new GameAssets(Logger.getLogger("test"), () -> { });
        g.candidates = () -> List.of(tmp.resolve("also-nowhere"));
        g.configure(true, tmp.resolve("cache"), "26.3", tmp.resolve("nowhere"), "1.1.0", "", "java");
        waitFor(g);
        assertEquals(GameAssets.State.MISSING, g.state());
        assertTrue(g.note().contains("once in the launcher"), g.note());
        assertNull(g.source());
        g.stop();
        GameAssets off = new GameAssets(Logger.getLogger("test"), () -> { });
        off.configure(false, tmp.resolve("cache"), "26.3", null, "1.1.0", "", "java");
        assertEquals(GameAssets.State.OFF, off.state());
        assertFalse(off.enabled());
    }

    @Test void offerCarriesStateAndNoteAndTheOperatorsOwnFilesWin() throws Exception {
        Path manual = Files.createDirectories(tmp.resolve("manual"));
        Files.writeString(manual.resolve("sounds.zip"), "mine");
        GameAssets g = new GameAssets(Logger.getLogger("test"), () -> { });
        g.candidates = List::of;
        g.configure(true, Files.createDirectories(tmp.resolve("cache")), "26.3", null, "1.1.0", "", "java");
        waitFor(g);
        assertEquals(GameAssets.State.MISSING, g.state());
        Files.writeString(tmp.resolve("cache/sounds.zip"), "automatic");
        Files.writeString(tmp.resolve("cache/pack.zip"), "automatic pack");
        AssetService a = new AssetService(Logger.getLogger("test"), (s, m) -> { }, List::of);
        a.configure(true, manual.toString(), g, 8);
        long end = System.nanoTime() + TimeUnit.SECONDS.toNanos(10);
        while (!a.ready() && System.nanoTime() < end) Thread.sleep(20);
        assertNull(a.why());
        assertEquals(manual.resolve("sounds.zip"), a.files().get("sounds.zip").path(), "assets.dir wins over the automatic file");
        assertEquals(tmp.resolve("cache/pack.zip"), a.files().get("pack.zip").path());
        var m = a.message();
        assertEquals("missing", m.get("state").getAsString());
        assertTrue(m.get("note").getAsString().contains("26.3"));
        var fx = com.google.gson.JsonParser.parseString(Files.readString(Path.of("../tools/fixtures/assets_building.json"))).getAsJsonObject();
        assertEquals("building", fx.get("state").getAsString());
        assertTrue(fx.has("note") && fx.getAsJsonArray("files").isEmpty());
        a.stop();
        g.stop();
        // Nothing to serve: assets.auto off and no assets.dir.
        AssetService none = new AssetService(Logger.getLogger("test"), (s, x) -> { }, List::of);
        GameAssets off = new GameAssets(Logger.getLogger("test"), () -> { });
        off.configure(false, tmp, "26.3", null, "1.1.0", "", "java");
        none.configure(false, "", off, 8);
        assertNotNull(none.why());
    }

    // ---------- helpers ----------

    private static final class Set2 { static java.util.Set<String> of(String... s) { return new java.util.LinkedHashSet<>(List.of(s)); } }

    private static void waitFor(GameAssets g) throws InterruptedException {
        long end = System.nanoTime() + TimeUnit.SECONDS.toNanos(60);
        Thread.sleep(50);
        while (System.nanoTime() < end && (g.state() == GameAssets.State.SEARCHING || g.state() == GameAssets.State.BUILDING)) Thread.sleep(50);
    }

    /** A launcher folder with versions/v/v.jar (two asset entries and some game classes) and its JSON. */
    private static Path launcher(Path dir, String v) throws IOException {
        Path vd = Files.createDirectories(dir.resolve("versions").resolve(v));
        try (ZipOutputStream z = new ZipOutputStream(Files.newOutputStream(vd.resolve(v + ".jar")))) {
            for (String n : List.of("net/minecraft/Foo.class", "assets/minecraft/textures/block/stone.png", "data/minecraft/tags/x.json",
                "assets/minecraft/blockstates/stone.json", "version.json", "META-INF/MANIFEST.MF")) {
                z.putNextEntry(new ZipEntry(n));
                z.write(n.endsWith(".png") ? "png".getBytes(StandardCharsets.UTF_8) : "{}".getBytes(StandardCharsets.UTF_8));
                z.closeEntry();
            }
        }
        Files.writeString(vd.resolve(v + ".json"), "{\"id\":\"" + v + "\",\"assetIndex\":{\"id\":\"30\"},\"libraries\":[]}");
        return dir;
    }

    /** The launcher's asset index and objects for one sound event (what PackSounds reads). */
    private static void sounds(Path mc, String v) throws IOException {
        byte[] json = "{\"block.stone.break\":{\"sounds\":[\"block/stone/break1\"]}}".getBytes(StandardCharsets.UTF_8);
        byte[] ogg = "OggS fake".getBytes(StandardCharsets.UTF_8);
        String hj = GameAssetsTest.sha1(json), ho = GameAssetsTest.sha1(ogg);
        for (var e : Map.of(hj, json, ho, ogg).entrySet()) {
            Path p = Files.createDirectories(mc.resolve("assets/objects").resolve(e.getKey().substring(0, 2))).resolve(e.getKey());
            Files.write(p, e.getValue());
        }
        Files.createDirectories(mc.resolve("assets/indexes"));
        Files.writeString(mc.resolve("assets/indexes/30.json"), "{\"objects\":{"
            + "\"minecraft/sounds.json\":{\"hash\":\"" + hj + "\",\"size\":" + json.length + "},"
            + "\"minecraft/sounds/block/stone/break1.ogg\":{\"hash\":\"" + ho + "\",\"size\":" + ogg.length + "}}}");
    }

    static String sha1(byte[] b) {
        try { return java.util.HexFormat.of().formatHex(java.security.MessageDigest.getInstance("SHA-1").digest(b)); }
        catch (java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }
    }

    @SuppressWarnings("unused") private static final ArrayDeque<String> UNUSED = new ArrayDeque<>();
}
