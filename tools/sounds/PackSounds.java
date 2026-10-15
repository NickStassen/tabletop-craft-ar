// Writes sounds.zip: the game's sound definitions (sounds.json) and the .ogg files they name, copied from YOUR OWN
// Minecraft launcher install, so the Quest app can play spatial sound on the table (docs/QUEST_SETUP.md Part E). Run it
// on your PC and copy the result to the headset next to pack.zip (or let your own server hand it to your own headset:
// QUEST_SETUP.md Part N). The sounds belong to Mojang: never commit, share or redistribute sounds.zip (it is git-ignored).
// Any JDK 17+ runs it directly with Java's single-file launcher:
//
//   java tools/sounds/PackSounds.java ~/.minecraft 26.3 sounds.zip
//   (Windows: java tools\sounds\PackSounds.java %APPDATA%\.minecraft 26.3 sounds.zip)
//   java tools/sounds/PackSounds.java ~/.minecraft 26.3 sounds.zip blocks,mobs,weather     (only these groups)
//   java tools/sounds/PackSounds.java ~/.minecraft 26.3 --list                             (sizes per group, no file)
//
// The launcher keeps sounds outside the client jar: versions/<v>/<v>.json names an asset index
// (assets/indexes/<id>.json), which maps "minecraft/sounds.json" and every "minecraft/sounds/**.ogg" to a file under
// assets/objects/<first two hex digits>/<sha1>. Launch the version once in the launcher so they are downloaded.
//
// Groups (by sound event id): blocks (block.*), mobs (entity.*), items (item.*), weather (weather.*), ui (ui.*),
// ambient (ambient.*: caves, biome loops, underwater), music (music.*), records (music_disc.*), other (the rest:
// enchant.*, event.*, particle.*...). Default: everything but ambient, music and records (about 60 MB for 26.3). An event
// that refers to another one ({"type": "event"}) brings that one along. The zip mirrors a resource pack:
// assets/minecraft/sounds.json, assets/minecraft/sounds/<name>.ogg (stored, not recompressed), plus
// tabletop_sounds.json (version, index, groups, counts).
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.*;
import java.util.zip.CRC32;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

public class PackSounds {
    static final List<String> GROUPS = List.of("blocks", "mobs", "items", "weather", "ui", "ambient", "music", "records", "other");
    static final Set<String> DEFAULT = Set.of("blocks", "mobs", "items", "weather", "ui", "other");

    public static void main(String[] args) throws Exception {
        if (args.length < 3) {
            System.err.println("usage: PackSounds <minecraft dir> <version> <out.zip | --list> [groups, e.g. blocks,mobs,weather or all]");
            System.err.println("groups: " + String.join(",", GROUPS) + " (default: all but ambient, music and records)");
            System.exit(2);
        }
        Path mc = Path.of(args[0]);
        String ver = args[1];
        boolean list = args[2].equals("--list");
        Set<String> groups = new LinkedHashSet<>();
        if (args.length > 3) {
            for (String g : args[3].split(",")) {
                g = g.trim();
                if (g.equals("all")) groups.addAll(GROUPS);
                else if (GROUPS.contains(g)) groups.add(g);
                else { System.err.println("unknown group " + g + "; groups: " + String.join(",", GROUPS)); System.exit(2); }
            }
        } else for (String g : GROUPS) if (DEFAULT.contains(g)) groups.add(g);

        Path versionJson = mc.resolve("versions").resolve(ver).resolve(ver + ".json");
        if (!Files.exists(versionJson)) fail("no " + versionJson + ": launch " + ver + " once in the launcher");
        Map<String, Object> vj = obj(Json.parse(Files.readString(versionJson)));
        Map<String, Object> ai = obj(vj.get("assetIndex"));
        String indexId = ai != null ? (String) ai.get("id") : (String) vj.get("assets");
        if (indexId == null) fail(versionJson + " names no asset index");
        Path indexPath = mc.resolve("assets").resolve("indexes").resolve(indexId + ".json");
        if (!Files.exists(indexPath)) fail("no asset index " + indexPath + ": launch " + ver + " once in the launcher");
        Map<String, Object> objects = obj(obj(Json.parse(Files.readString(indexPath))).get("objects"));
        Map<String, Object> sj = obj(objects.get("minecraft/sounds.json"));
        if (sj == null) fail("asset index " + indexId + " has no minecraft/sounds.json");
        byte[] soundsJsonBytes = object(mc, sj, "minecraft/sounds.json");
        Map<String, Object> events = obj(Json.parse(new String(soundsJsonBytes, java.nio.charset.StandardCharsets.UTF_8)));

        // Events in the chosen groups, plus every event they refer to.
        Set<String> keep = new TreeSet<>();
        Deque<String> todo = new ArrayDeque<>();
        for (String k : events.keySet()) if (groups.contains(group(k))) todo.add(k);
        while (!todo.isEmpty()) {
            String k = todo.poll();
            if (!events.containsKey(k) || !keep.add(k)) continue;
            for (Object s : list(obj(events.get(k)).get("sounds")))
                if (s instanceof Map<?, ?> m && "event".equals(m.get("type"))) todo.add(strip((String) m.get("name")));
        }
        // Files (index keys) the kept events play.
        Map<String, String> files = new TreeMap<>();   // zip path -> index key
        int missing = 0;
        for (String k : keep)
            for (Object s : list(obj(events.get(k)).get("sounds"))) {
                String name; boolean ev = false;
                if (s instanceof String str) name = str;
                else { Map<String, Object> m = obj(s); name = (String) m.get("name"); ev = "event".equals(m.get("type")); }
                if (ev || name == null) continue;
                String ns = name.contains(":") ? name.substring(0, name.indexOf(':')) : "minecraft";
                String path = name.contains(":") ? name.substring(name.indexOf(':') + 1) : name;
                String key = ns + "/sounds/" + path + ".ogg";
                if (!objects.containsKey(key)) { missing++; continue; }
                files.put("assets/" + key, key);
            }

        if (list) {
            Map<String, long[]> per = new TreeMap<>();
            Map<String, Set<String>> seenPer = new HashMap<>();   // a file counts once per group
            for (String k : events.keySet()) {
                long[] a = per.computeIfAbsent(group(k), x -> new long[2]);
                a[0]++;
                Set<String> seen = seenPer.computeIfAbsent(group(k), x -> new HashSet<>());
                for (Object s : list(obj(events.get(k)).get("sounds"))) {
                    String name = s instanceof String str ? str : (String) obj(s).get("name");
                    if (name == null || (s instanceof Map<?, ?> m && "event".equals(m.get("type")))) continue;
                    String key = (name.contains(":") ? name.replace(":", "/sounds/") : "minecraft/sounds/" + name) + ".ogg";
                    Map<String, Object> o = obj(objects.get(key));
                    if (o != null && seen.add(key)) a[1] += ((Number) o.get("size")).longValue();
                }
            }
            System.out.printf("Minecraft %s, asset index %s: %d sound events%n", ver, indexId, events.size());
            for (String g : GROUPS) {
                long[] a = per.getOrDefault(g, new long[2]);
                System.out.printf("  %-8s %5d events %7.1f MB%s%n", g, a[0], a[1] / 1e6, DEFAULT.contains(g) ? "  (default)" : "");
            }
            System.out.printf("Chosen (%s): %d events, %d files, %.1f MB%n", String.join(",", groups), keep.size(), files.size(),
                files.values().stream().mapToLong(k -> ((Number) obj(objects.get(k)).get("size")).longValue()).sum() / 1e6);
            return;
        }

        Path out = Path.of(args[2]);
        Path tmp = out.resolveSibling(out.getFileName() + ".tmp");
        long bytes = 0;
        int bad = 0;
        try (ZipOutputStream zip = new ZipOutputStream(Files.newOutputStream(tmp))) {
            Map<String, Object> kept = new LinkedHashMap<>();
            for (String k : keep) kept.put(k, events.get(k));
            deflated(zip, "assets/minecraft/sounds.json", Json.write(kept).getBytes(java.nio.charset.StandardCharsets.UTF_8));
            for (var e : files.entrySet()) {
                byte[] data;
                try { data = object(mc, obj(objects.get(e.getValue())), e.getValue()); }
                catch (IOException ex) { System.err.println("skipped " + e.getValue() + ": " + ex.getMessage()); bad++; continue; }
                stored(zip, e.getKey(), data);
                bytes += data.length;
            }
            Map<String, Object> meta = new LinkedHashMap<>();
            meta.put("tool", "tabletop-craft-ar tools/sounds/PackSounds.java");
            meta.put("minecraft", ver);
            meta.put("asset_index", indexId);
            meta.put("groups", new ArrayList<>(groups));
            meta.put("events", keep.size());
            meta.put("files", files.size() - bad);
            meta.put("note", "Sounds copied from the user's own launcher install. Do not share or redistribute.");
            deflated(zip, "tabletop_sounds.json", Json.write(meta).getBytes(java.nio.charset.StandardCharsets.UTF_8));
        }
        Files.move(tmp, out, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        System.out.printf("wrote %s: %d sound events, %d files, %.1f MB (%s; Minecraft %s, asset index %s)%n", out, keep.size(),
            files.size() - bad, Files.size(out) / 1e6, String.join(",", groups), ver, indexId);
        if (missing > 0) System.out.println(missing + " sounds named in sounds.json are not in the asset index (skipped)");
        if (bad > 0) System.out.println(bad + " files missing or damaged in assets/objects: launch " + ver + " in the launcher to repair them, then run this again");
        System.out.println("Copy it to the headset next to pack.zip (QUEST_SETUP.md Part E). It holds Mojang's sounds: keep it to yourself.");
    }

    static String group(String event) {
        if (event.startsWith("block.")) return "blocks";
        if (event.startsWith("entity.")) return "mobs";
        if (event.startsWith("item.")) return "items";
        if (event.startsWith("weather.")) return "weather";
        if (event.startsWith("ui.")) return "ui";
        if (event.startsWith("ambient.")) return "ambient";
        if (event.startsWith("music_disc.")) return "records";
        if (event.startsWith("music.")) return "music";
        return "other";
    }

    static String strip(String id) { return id != null && id.startsWith("minecraft:") ? id.substring(10) : id; }

    /** An asset object's bytes, checked against the index's size and SHA-1 (its file name). */
    static byte[] object(Path mc, Map<String, Object> o, String key) throws Exception {
        String hash = (String) o.get("hash");
        Path p = mc.resolve("assets").resolve("objects").resolve(hash.substring(0, 2)).resolve(hash);
        if (!Files.exists(p)) throw new IOException("not downloaded (" + p + ")");
        byte[] data = Files.readAllBytes(p);
        if (data.length != ((Number) o.get("size")).longValue()) throw new IOException("wrong size");
        byte[] sha = MessageDigest.getInstance("SHA-1").digest(data);
        if (!HexFormat.of().formatHex(sha).equals(hash)) throw new IOException("damaged (SHA-1 mismatch)");
        return data;
    }

    static void stored(ZipOutputStream zip, String name, byte[] data) throws IOException {
        ZipEntry e = new ZipEntry(name);
        e.setMethod(ZipEntry.STORED);
        e.setSize(data.length);
        e.setCompressedSize(data.length);
        CRC32 crc = new CRC32();
        crc.update(data);
        e.setCrc(crc.getValue());
        e.setTime(0);
        zip.putNextEntry(e);
        zip.write(data);
        zip.closeEntry();
    }

    static void deflated(ZipOutputStream zip, String name, byte[] data) throws IOException {
        ZipEntry e = new ZipEntry(name);
        e.setTime(0);
        zip.putNextEntry(e);
        zip.write(data);
        zip.closeEntry();
    }

    static void fail(String why) { System.err.println(why); System.exit(1); }

    @SuppressWarnings("unchecked") static Map<String, Object> obj(Object o) { return o instanceof Map<?, ?> m ? (Map<String, Object>) m : null; }
    @SuppressWarnings("unchecked") static List<Object> list(Object o) { return o instanceof List<?> l ? (List<Object>) l : List.of(); }

    /** A small JSON reader and writer (objects keep their key order). */
    static final class Json {
        final String s; int i;
        Json(String s) { this.s = s; }
        static Object parse(String s) { Json j = new Json(s); Object v = j.value(); j.ws(); if (j.i != s.length()) throw new IllegalArgumentException("trailing data at " + j.i); return v; }
        void ws() { while (i < s.length() && Character.isWhitespace(s.charAt(i))) i++; }
        Object value() {
            ws();
            char c = s.charAt(i);
            if (c == '{') {
                i++; Map<String, Object> m = new LinkedHashMap<>(); ws();
                if (s.charAt(i) == '}') { i++; return m; }
                while (true) { ws(); String k = string(); ws(); expect(':'); m.put(k, value()); ws(); if (s.charAt(i) == ',') { i++; continue; } expect('}'); return m; }
            }
            if (c == '[') {
                i++; List<Object> l = new ArrayList<>(); ws();
                if (s.charAt(i) == ']') { i++; return l; }
                while (true) { l.add(value()); ws(); if (s.charAt(i) == ',') { i++; continue; } expect(']'); return l; }
            }
            if (c == '"') return string();
            if (s.startsWith("true", i)) { i += 4; return true; }
            if (s.startsWith("false", i)) { i += 5; return false; }
            if (s.startsWith("null", i)) { i += 4; return null; }
            int b = i;
            while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0) i++;
            String n = s.substring(b, i);
            if (n.isEmpty()) throw new IllegalArgumentException("bad JSON at " + b);
            return n.contains(".") || n.contains("e") || n.contains("E") ? (Object) Double.parseDouble(n) : (Object) Long.parseLong(n);
        }
        void expect(char c) { if (s.charAt(i) != c) throw new IllegalArgumentException("expected " + c + " at " + i); i++; }
        String string() {
            expect('"');
            StringBuilder b = new StringBuilder();
            while (true) {
                char c = s.charAt(i++);
                if (c == '"') return b.toString();
                if (c != '\\') { b.append(c); continue; }
                char e = s.charAt(i++);
                switch (e) {
                    case 'n' -> b.append('\n'); case 't' -> b.append('\t'); case 'r' -> b.append('\r'); case 'b' -> b.append('\b'); case 'f' -> b.append('\f');
                    case 'u' -> { b.append((char) Integer.parseInt(s.substring(i, i + 4), 16)); i += 4; }
                    default -> b.append(e);
                }
            }
        }
        static String write(Object v) { StringBuilder b = new StringBuilder(); write(b, v); return b.toString(); }
        static void write(StringBuilder b, Object v) {
            if (v == null) b.append("null");
            else if (v instanceof String str) quote(b, str);
            else if (v instanceof Map<?, ?> m) {
                b.append('{'); boolean first = true;
                for (var e : m.entrySet()) { if (!first) b.append(','); first = false; quote(b, (String) e.getKey()); b.append(':'); write(b, e.getValue()); }
                b.append('}');
            } else if (v instanceof List<?> l) {
                b.append('['); for (int k = 0; k < l.size(); k++) { if (k > 0) b.append(','); write(b, l.get(k)); } b.append(']');
            } else b.append(v);
        }
        static void quote(StringBuilder b, String str) {
            b.append('"');
            for (char c : str.toCharArray()) {
                if (c == '"' || c == '\\') b.append('\\').append(c);
                else if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
                else b.append(c);
            }
            b.append('"');
        }
    }

}
