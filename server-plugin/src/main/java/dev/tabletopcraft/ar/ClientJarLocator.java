package dev.tabletopcraft.ar;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * [setup] Finds the server PC's own Minecraft: Java Edition install for the server's game version (QUEST_SETUP.md
 * "Store install", config.yml {@code assets.auto}): a launcher folder holding {@code versions/<v>/<v>.jar} and
 * {@code versions/<v>/<v>.json} (the official launcher's layout, which the asset tools read). Pure: the OS, environment
 * and home folder are parameters, so tests can fake them.
 */
final class ClientJarLocator {
    /** A launcher folder with the client jar and version JSON of {@code version}. */
    record Found(Path launcherDir, Path jar, Path versionJson) {}

    private ClientJarLocator() {}

    /** The usual launcher folders on this OS, most likely first (an override from config.yml goes before them). */
    static List<Path> candidates(String osName, Map<String, String> env, Path home) {
        String os = osName == null ? "" : osName.toLowerCase(Locale.ROOT);
        Set<Path> out = new LinkedHashSet<>();
        if (os.contains("win")) {
            String appData = env.get("APPDATA");
            if (appData != null && !appData.isBlank()) out.add(Path.of(appData, ".minecraft"));
            out.add(home.resolve("AppData").resolve("Roaming").resolve(".minecraft"));
        } else if (os.contains("mac") || os.contains("darwin")) {
            out.add(home.resolve("Library").resolve("Application Support").resolve("minecraft"));
        } else {
            out.add(home.resolve(".minecraft"));
            out.add(home.resolve(".var/app/com.mojang.Minecraft/.minecraft"));   // the launcher as a Flatpak
            out.add(home.resolve("snap/mc-installer/current/.minecraft"));        // ... and as a Snap
        }
        return new ArrayList<>(out);
    }

    /**
     * The first folder that has {@code version}'s jar and JSON. {@code override} (config.yml {@code assets.minecraft-dir};
     * may be null) is tried first and may also name the jar itself or its {@code versions/<v>} folder.
     */
    static Found find(String version, Path override, List<Path> candidates) {
        List<Path> dirs = new ArrayList<>();
        if (override != null) {
            Path o = override.toAbsolutePath().normalize();
            if (o.getFileName() != null && o.getFileName().toString().endsWith(".jar") && o.getParent() != null) o = o.getParent();
            // versions/<v> -> the launcher folder two levels up
            if (o.getFileName() != null && o.getFileName().toString().equals(version) && o.getParent() != null
                && o.getParent().getFileName() != null && o.getParent().getFileName().toString().equals("versions")) o = o.getParent().getParent();
            dirs.add(o);
        }
        dirs.addAll(candidates);
        for (Path d : dirs) {
            Path v = d.resolve("versions").resolve(version);
            Path jar = v.resolve(version + ".jar"), json = v.resolve(version + ".json");
            if (Files.isRegularFile(jar) && Files.isRegularFile(json)) return new Found(d, jar, json);
        }
        return null;
    }

    /** "~/.minecraft, ..." for the log line when nothing was found. */
    static String describe(Path override, List<Path> candidates) {
        List<String> s = new ArrayList<>();
        if (override != null) s.add(override.toString());
        for (Path p : candidates) s.add(p.toString());
        return String.join(", ", s);
    }
}
