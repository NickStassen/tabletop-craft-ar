package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import org.bukkit.configuration.ConfigurationSection;
import org.bukkit.configuration.InvalidConfigurationException;
import org.bukkit.configuration.file.YamlConfiguration;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Deque;
import java.util.HashMap;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.logging.Logger;

/**
 * [setup] Pairing headsets with a 6-digit code (PROTOCOL.md "Pairing", cap {@code pair_v1}): a headset without a
 * credential asks for a code ({@code pair_request}, or automatically when the server needs one), shows it, and an
 * operator who can see the headset types {@code /tabletop pair <code>} (console or tabletop.admin). The server then
 * issues a random 256-bit device token ({@code paired}); the headset keeps it for this server and presents it in
 * {@code hello.device_token} from then on. Only its SHA-256 is stored, in {@code devices.yml}; {@code /tabletop unpair}
 * revokes it. The code is never printed on the server: typing it proves the operator sees that headset.
 * <p>
 * A paired session is trusted (Session.trusted): it may use what the require-token switches keep for trusted headsets,
 * and download the game files. Thread-safe (websocket threads ask for codes, the main thread approves).
 */
final class PairingService {
    static final String CAP = "pair_v1";
    static final String FILE = "devices.yml";
    static final long CODE_TTL_MS = 5 * 60_000L;
    static final int MAX_PENDING = 16, REQUESTS_PER_MINUTE = 6, MAX_DEVICES = 256, MAX_NAME = 32;

    record Device(String id, String name, String tokenSha256, long pairedMs, long lastSeenMs, String player) {
        Device seen(long now) { return new Device(id, name, tokenSha256, pairedMs, now, player); }
    }

    /** A headset waiting with a code shown. */
    record Pending(String code, Session session, String name, String address, long expiresMs) {}

    /** A new pairing: the device record, the token to send (only this once) and the session waiting for it. */
    record Issued(Device device, String token, Session session) {}

    private final Logger log;
    private final SecureRandom random = new SecureRandom();
    private final Map<String, Device> byHash = new LinkedHashMap<>();   // token SHA-256 -> device
    private final Map<String, Pending> pending = new HashMap<>();       // code -> waiting headset
    private final Map<String, Deque<Long>> requests = new HashMap<>();  // address -> recent request times
    private Path file;
    private volatile boolean enabled = true, required;
    private volatile String serverId = "";
    private long seenSavedMs;

    PairingService(Logger log) { this.log = log; }

    /** Start or reload: devices.yml in the plugin folder (server-id is created once and kept). */
    synchronized void configure(Path dataFolder, boolean enabled, boolean required) {
        this.enabled = enabled;
        this.required = required;
        this.file = dataFolder.resolve(FILE);
        byHash.clear();
        load();
        if (serverId.isEmpty()) { serverId = HexFormat.of().formatHex(bytes(8)); save(); }
    }

    boolean enabled() { return enabled; }
    /** auth.require-pairing: headsets without the token must be paired before they get anything. */
    boolean required() { return required; }
    /** Stable random id of this server (discovery reply id, welcome.server_id): the headset keys its tokens by it. */
    String serverId() { return serverId; }

    /** The paired device a token belongs to, or null (unknown or revoked). Marks it seen. */
    synchronized Device check(String token) {
        if (token == null || token.isEmpty() || token.length() > 128) return null;
        String h = hash(token);
        Device d = byHash.get(h);
        if (d == null) return null;
        long now = System.currentTimeMillis();
        byHash.put(h, d.seen(now));
        if (now - seenSavedMs > 60_000) { seenSavedMs = now; save(); }
        return d;
    }

    /**
     * A headset asks for a code. Returns it, or null when refused: pairing off, too many requests from this address
     * ({@link #REQUESTS_PER_MINUTE}) or too many headsets waiting. A new request from the same session replaces its code.
     */
    synchronized Pending request(Session s, String deviceName, String address, long now) {
        if (!enabled) return null;
        expire(now);
        Deque<Long> times = requests.computeIfAbsent(address == null ? "?" : address, k -> new ArrayDeque<>());
        while (!times.isEmpty() && now - times.peekFirst() > 60_000) times.pollFirst();
        if (times.size() >= REQUESTS_PER_MINUTE) return null;
        pending.values().removeIf(p -> p.session() == s);
        if (pending.size() >= MAX_PENDING) return null;
        times.addLast(now);
        String code;
        do code = String.format(Locale.ROOT, "%06d", random.nextInt(1_000_000)); while (pending.containsKey(code));
        Pending p = new Pending(code, s, cleanName(deviceName), address == null ? "?" : address, now + CODE_TTL_MS);
        pending.put(code, p);
        return p;
    }

    /** Why request() said no, for the error message. */
    synchronized String refusal(String address, long now) {
        if (!enabled) return "pairing is off on this server (auth.pairing)";
        Deque<Long> times = requests.get(address == null ? "?" : address);
        if (times != null && times.size() >= REQUESTS_PER_MINUTE) return "too many pairing requests from this address: try again in a minute";
        return "too many headsets are waiting to pair: try again in a few minutes";
    }

    /**
     * /tabletop pair: the code a headset shows. Issues its device token (name: the operator's choice, else the headset's;
     * player: the Java account whose skin and name it uses, may be null). Null when no live headset waits with that code.
     */
    synchronized Issued approve(String code, String name, String player, long now) {
        expire(now);
        Pending p = code == null ? null : pending.remove(code.trim());
        if (p == null || p.session().state == Session.State.CLOSED) return null;
        while (byHash.size() >= MAX_DEVICES) byHash.remove(byHash.keySet().iterator().next());   // the oldest pairing goes
        String token = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes(32));
        String id;
        do id = HexFormat.of().formatHex(bytes(4)); while (find(id).isPresent());
        String n = name != null && !name.isBlank() ? cleanName(name) : p.name();
        Device d = new Device(id, n, hash(token), now, now, player == null || player.isBlank() ? null : ChatText.name(player, null));
        byHash.put(d.tokenSha256(), d);
        save();
        return new Issued(d, token, p.session());
    }

    /** Paired headsets, oldest first. */
    synchronized List<Device> devices() { return new ArrayList<>(byHash.values()); }

    /** Headsets waiting with a code (the codes themselves are never shown). */
    synchronized List<Pending> waiting(long now) { expire(now); return new ArrayList<>(pending.values()); }

    /** /tabletop unpair: by id, by name (all with that name) or "all". The removed devices. */
    synchronized List<Device> unpair(String key) {
        List<Device> out = new ArrayList<>();
        if (key == null || key.isBlank()) return out;
        String k = key.trim();
        byHash.values().removeIf(d -> {
            boolean hit = k.equalsIgnoreCase("all") || d.id().equalsIgnoreCase(k) || d.name().equalsIgnoreCase(k);
            if (hit) out.add(d);
            return hit;
        });
        if (!out.isEmpty()) save();
        return out;
    }

    /** pair_code (S->C): the code to show, how long it lasts, whether the world waits for it. */
    static JsonObject codeMessage(Pending p, boolean required, boolean tokenRejected) {
        JsonObject o = Protocol.body("pair_code");
        o.addProperty("code", p.code());
        o.addProperty("expires_s", CODE_TTL_MS / 1000);
        o.addProperty("required", required);
        if (tokenRejected) o.addProperty("token_rejected", true);
        return o;
    }

    /** paired (S->C): the device token, sent once. */
    static JsonObject pairedMessage(Issued is, String serverId) {
        JsonObject o = Protocol.body("paired");
        o.addProperty("token", is.token());
        o.addProperty("device", is.device().id());
        o.addProperty("name", is.device().name());
        o.addProperty("server_id", serverId);
        return o;
    }

    /** A closed session's code stops working. */
    synchronized void closed(Session s) { pending.values().removeIf(p -> p.session() == s); }

    private java.util.Optional<Device> find(String id) { return byHash.values().stream().filter(d -> d.id().equals(id)).findFirst(); }

    private void expire(long now) { pending.values().removeIf(p -> p.expiresMs() <= now || p.session().state == Session.State.CLOSED); }

    private byte[] bytes(int n) { byte[] b = new byte[n]; random.nextBytes(b); return b; }

    static String hash(String token) {
        try { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(token.getBytes(StandardCharsets.UTF_8))); }
        catch (java.security.NoSuchAlgorithmException e) { throw new IllegalStateException(e); }
    }

    /** A device name as shown: printable, one line, at most MAX_NAME characters; "Headset" when empty. */
    static String cleanName(String s) {
        if (s == null) return "Headset";
        StringBuilder b = new StringBuilder();
        for (int i = 0; i < s.length() && b.length() < MAX_NAME; i++) {
            char c = s.charAt(i);
            if (c >= 32 && c != 127 && c != '§') b.append(c);
        }
        String t = b.toString().trim();
        return t.isEmpty() ? "Headset" : t;
    }

    // ---------- devices.yml ----------

    private void load() {
        if (file == null || !Files.isRegularFile(file)) return;
        YamlConfiguration y = new YamlConfiguration();
        try { y.loadFromString(Files.readString(file)); }
        catch (IOException | InvalidConfigurationException e) { log.warning("PAIR can't read " + file + ": " + e.getMessage() + " (no headset is paired until it is fixed)"); return; }
        serverId = y.getString("server-id", "");
        ConfigurationSection ds = y.getConfigurationSection("devices");
        if (ds == null) return;
        for (String id : ds.getKeys(false)) {
            ConfigurationSection d = ds.getConfigurationSection(id);
            if (d == null) continue;
            String h = d.getString("token-sha256", "");
            if (h.length() != 64) continue;
            byHash.put(h, new Device(id, cleanName(d.getString("name")), h, d.getLong("paired"), d.getLong("last-seen"), d.getString("player")));
        }
    }

    private void save() {
        if (file == null) return;
        YamlConfiguration y = new YamlConfiguration();
        y.options().setHeader(List.of(
            "Headsets paired with this server (/tabletop pair <code>, /tabletop devices, /tabletop unpair <name|id|all>).",
            "Only a hash of each device token is stored. Deleting a device here (or the file) unpairs it; the headset then shows a new code."));
        y.set("server-id", serverId);
        for (Device d : byHash.values()) {
            String k = "devices." + d.id();
            y.set(k + ".name", d.name());
            y.set(k + ".token-sha256", d.tokenSha256());
            y.set(k + ".paired", d.pairedMs());
            y.set(k + ".last-seen", d.lastSeenMs());
            if (d.player() != null) y.set(k + ".player", d.player());
        }
        try {
            Files.createDirectories(file.getParent());
            Path tmp = file.resolveSibling(FILE + ".tmp");
            Files.writeString(tmp, y.saveToString());
            Files.move(tmp, file, StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException e) { log.warning("PAIR can't write " + file + ": " + e.getMessage()); }
    }
}
