package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.function.Supplier;
import java.util.logging.Logger;

/**
 * LAN discovery (PROTOCOL.md 7, config.yml {@code discovery}). A daemon thread listens on UDP {@code discovery.port}
 * (default 8765, the same number as the WebSocket's TCP port) for the headset's broadcast probe
 * {@code {"type":"tabletop_discover","v":1}} and answers the sender directly with {@code tabletop_server}: a name, the
 * WebSocket port and path, and whether a token is needed. The headset builds the URL from the reply's source address.
 * Answering a probe instead of broadcasting beacons means the headset needs no multicast lock or extra Android
 * permission. Only private, link-local and loopback senders get an answer, at most {@link #MAX_REPLIES_PER_SECOND}.
 */
final class LanBeacon implements Runnable {
    static final String PROBE = "tabletop_discover", REPLY = "tabletop_server";
    static final int MAX_REPLIES_PER_SECOND = 20, MAX_PROBE_BYTES = 512;

    private final Logger log;
    private final DatagramSocket socket;
    private final Supplier<JsonObject> reply;
    private final Thread thread;
    private volatile boolean running = true;
    private long windowStart;
    private int windowCount;
    volatile int replies, ignored;

    private LanBeacon(Logger log, DatagramSocket socket, Supplier<JsonObject> reply) {
        this.log = log;
        this.socket = socket;
        this.reply = reply;
        this.thread = new Thread(this, "tabletop-discovery");
        thread.setDaemon(true);
    }

    /** Binds UDP port (all interfaces: broadcasts don't reach a socket bound to one address) and starts answering. */
    static LanBeacon start(Logger log, int port, Supplier<JsonObject> reply) throws SocketException {
        DatagramSocket s = new DatagramSocket(null);
        s.setReuseAddress(true);   // several servers on one PC can all answer (Linux delivers broadcasts to each)
        s.bind(new InetSocketAddress(port));
        LanBeacon b = new LanBeacon(log, s, reply);
        b.thread.start();
        return b;
    }

    int port() { return socket.getLocalPort(); }

    @Override public void run() {
        byte[] buf = new byte[MAX_PROBE_BYTES];
        while (running) {
            DatagramPacket p = new DatagramPacket(buf, buf.length);
            try { socket.receive(p); }
            catch (Exception e) { if (running) log.warning("discovery: " + e); return; }
            if (!allowed(p.getAddress()) || !isProbe(p.getData(), p.getLength()) || !take(System.nanoTime())) { ignored++; continue; }
            try {
                byte[] out = Protocol.GSON.toJson(reply.get()).getBytes(StandardCharsets.UTF_8);
                socket.send(new DatagramPacket(out, out.length, p.getSocketAddress()));
                replies++;
            } catch (Exception e) { if (running) log.warning("discovery reply to " + p.getAddress() + ": " + e); }
        }
    }

    /** One-second window rate limit (replies are ~200 bytes, probes ~30: keep the amplification harmless). */
    synchronized boolean take(long nowNs) {
        if (nowNs - windowStart > 1_000_000_000L) { windowStart = nowNs; windowCount = 0; }
        return ++windowCount <= MAX_REPLIES_PER_SECOND;
    }

    void stop() {
        running = false;
        socket.close();
    }

    /** A discovery probe: a JSON object with type tabletop_discover and v 1. */
    static boolean isProbe(byte[] data, int len) {
        if (len <= 0 || len > MAX_PROBE_BYTES) return false;
        try {
            JsonObject m = JsonParser.parseString(new String(data, 0, len, StandardCharsets.UTF_8)).getAsJsonObject();
            return m.has("type") && PROBE.equals(m.get("type").getAsString()) && m.has("v") && m.get("v").getAsInt() == Protocol.VERSION;
        } catch (RuntimeException e) { return false; }
    }

    /** Only answer the LAN: private (10/8, 172.16/12, 192.168/16), link-local and loopback senders. */
    static boolean allowed(InetAddress a) {
        return a != null && (a.isSiteLocalAddress() || a.isLinkLocalAddress() || a.isLoopbackAddress());
    }

    /** The reply (PROTOCOL.md 7). name is cut to 48 characters, one line. */
    static JsonObject reply(String name, int port, String path, boolean tokenNeeded, String version, String mc, int players) {
        JsonObject o = new JsonObject();
        o.addProperty("type", REPLY);
        o.addProperty("v", Protocol.VERSION);
        String n = name == null ? "" : name.replaceAll("[\\r\\n\\t]+", " ").strip();
        o.addProperty("name", n.length() > 48 ? n.substring(0, 48) : n);
        o.addProperty("port", port);
        o.addProperty("path", path);
        o.addProperty("token", tokenNeeded);
        o.addProperty("version", version);
        o.addProperty("mc", mc);
        o.addProperty("players", players);
        return o;
    }

    /** [setup] The reply with the server's id (welcome.server_id) and whether it pairs headsets (PROTOCOL.md 7, "Pairing"). */
    static JsonObject reply(String name, int port, String path, boolean tokenNeeded, String version, String mc, int players, String id, boolean pair) {
        JsonObject o = reply(name, port, path, tokenNeeded, version, mc, players);
        if (id != null && !id.isEmpty()) o.addProperty("id", id);
        o.addProperty("pair", pair);
        return o;
    }

    // ---------- the "Headset URL" lines logged at startup ----------

    /** An interface address: the interface name and the IPv4 address. */
    record Nic(String name, InetAddress address) {}

    /**
     * The URLs a headset can use: the bind host itself when it is a specific address, else one per private IPv4
     * address of an up interface (192.168 first, then 10, then 172.16/12), each with its interface name.
     */
    static List<String> headsetUrls(String bindHost, int port, String path, List<Nic> nics) {
        List<String> out = new ArrayList<>();
        boolean wildcard = bindHost == null || bindHost.isBlank() || bindHost.equals("0.0.0.0") || bindHost.equals("::");
        if (!wildcard) { out.add("ws://" + bindHost + ":" + port + path); return out; }
        List<Nic> lan = new ArrayList<>();
        for (Nic n : nics)
            if (n.address().getAddress().length == 4 && n.address().isSiteLocalAddress()) lan.add(n);
        lan.sort((a, b) -> Integer.compare(rank(a.address()), rank(b.address())));
        for (Nic n : lan) out.add("ws://" + n.address().getHostAddress() + ":" + port + path + " (" + n.name() + ")");
        return out;
    }

    private static int rank(InetAddress a) {
        int b0 = a.getAddress()[0] & 0xff;
        return b0 == 192 ? 0 : b0 == 10 ? 1 : 2;
    }

    /** The up, non-loopback interfaces' IPv4 addresses (empty if the JVM can't list them). */
    static List<Nic> localNics() {
        List<Nic> l = new ArrayList<>();
        try {
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (InetAddress a : Collections.list(ni.getInetAddresses())) if (a.getAddress().length == 4) l.add(new Nic(ni.getName(), a));
            }
        } catch (SocketException e) { /* none */ }
        return l;
    }
}
