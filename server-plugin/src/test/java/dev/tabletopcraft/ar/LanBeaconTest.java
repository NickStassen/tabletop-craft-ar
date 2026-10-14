package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.logging.Logger;

import static org.junit.jupiter.api.Assertions.*;

/** LAN discovery (PROTOCOL.md 7), the startup "Headset URL" lines, and the constant-time token check. */
class LanBeaconTest {
    private static byte[] bytes(String s) { return s.getBytes(StandardCharsets.UTF_8); }

    @Test void probesAreRecognised() throws Exception {
        byte[] fx = Files.readAllBytes(Path.of("..", "tools", "fixtures", "discovery_probe.json"));
        assertTrue(LanBeacon.isProbe(fx, fx.length));
        byte[] v2 = bytes("{\"type\":\"tabletop_discover\",\"v\":2}");
        assertFalse(LanBeacon.isProbe(v2, v2.length));
        byte[] other = bytes("{\"type\":\"hello\",\"v\":1}");
        assertFalse(LanBeacon.isProbe(other, other.length));
        byte[] junk = bytes("not json at all");
        assertFalse(LanBeacon.isProbe(junk, junk.length));
        assertFalse(LanBeacon.isProbe(new byte[0], 0));
    }

    @Test void onlyLanSendersGetAnAnswer() throws Exception {
        assertTrue(LanBeacon.allowed(InetAddress.getByName("192.168.1.50")));
        assertTrue(LanBeacon.allowed(InetAddress.getByName("10.0.0.7")));
        assertTrue(LanBeacon.allowed(InetAddress.getByName("172.20.0.3")));
        assertTrue(LanBeacon.allowed(InetAddress.getByName("127.0.0.1")));
        assertTrue(LanBeacon.allowed(InetAddress.getByName("169.254.10.1")));
        assertFalse(LanBeacon.allowed(InetAddress.getByName("8.8.8.8")));
        assertFalse(LanBeacon.allowed(InetAddress.getByName("172.32.0.1")));
    }

    @Test void replyMatchesTheFixture() throws Exception {
        JsonObject fx = JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", "discovery_reply.json"))).getAsJsonObject();
        JsonObject r = LanBeacon.reply("A Minecraft Server", 8765, "/tabletop", false, "0.3.0", "26.3", 2);
        assertEquals(fx, r);
        JsonObject longName = LanBeacon.reply("a\nvery long name ".repeat(10), 8765, "/tabletop", true, "x", "y", 0);
        assertEquals(48, longName.get("name").getAsString().length());
        assertFalse(longName.get("name").getAsString().contains("\n"));
        assertTrue(longName.get("token").getAsBoolean());
    }

    @Test void rateLimited() throws Exception {
        LanBeacon b = LanBeacon.start(Logger.getAnonymousLogger(), 0, () -> new JsonObject());
        try {
            int ok = 0;
            for (int i = 0; i < 50; i++) if (b.take(5_000_000_000L)) ok++;
            assertEquals(LanBeacon.MAX_REPLIES_PER_SECOND, ok);
            assertTrue(b.take(7_000_000_000L), "a new second answers again");
        } finally { b.stop(); }
    }

    @Test void answersAProbeOverUdp() throws Exception {
        LanBeacon b = LanBeacon.start(Logger.getAnonymousLogger(), 0, () -> LanBeacon.reply("Test", 8802, "/tabletop", true, "0.3.1", "26.3", 1));
        try (DatagramSocket c = new DatagramSocket(0, InetAddress.getLoopbackAddress())) {
            c.setSoTimeout(2000);
            byte[] probe = bytes("{\"type\":\"tabletop_discover\",\"v\":1}");
            c.send(new DatagramPacket(probe, probe.length, InetAddress.getLoopbackAddress(), b.port()));
            DatagramPacket p = new DatagramPacket(new byte[2048], 2048);
            c.receive(p);
            JsonObject r = JsonParser.parseString(new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8)).getAsJsonObject();
            assertEquals("tabletop_server", r.get("type").getAsString());
            assertEquals(8802, r.get("port").getAsInt());
            assertTrue(r.get("token").getAsBoolean());
            byte[] junk = bytes("{\"type\":\"nope\"}");
            c.send(new DatagramPacket(junk, junk.length, InetAddress.getLoopbackAddress(), b.port()));
            c.setSoTimeout(300);
            assertThrows(java.net.SocketTimeoutException.class, () -> c.receive(new DatagramPacket(new byte[64], 64)), "junk gets no answer");
        } finally { b.stop(); }
    }

    @Test void headsetUrlsListTheLanAddresses() throws Exception {
        var nics = List.of(new LanBeacon.Nic("docker0", InetAddress.getByName("172.17.0.1")), new LanBeacon.Nic("wlan0", InetAddress.getByName("192.168.1.50")),
                           new LanBeacon.Nic("eth1", InetAddress.getByName("10.0.0.5")), new LanBeacon.Nic("tun0", InetAddress.getByName("100.64.1.2")));
        assertEquals(List.of("ws://192.168.1.50:8765/tabletop (wlan0)", "ws://10.0.0.5:8765/tabletop (eth1)", "ws://172.17.0.1:8765/tabletop (docker0)"),
                     LanBeacon.headsetUrls("0.0.0.0", 8765, "/tabletop", nics));
        assertEquals(List.of("ws://192.168.1.50:8800/x"), LanBeacon.headsetUrls("192.168.1.50", 8800, "/x", nics));
        assertTrue(LanBeacon.headsetUrls("0.0.0.0", 8765, "/tabletop", List.of()).isEmpty());
    }

    @Test void tokenCheck() {
        assertTrue(LinkServer.tokenMatches("s3cret", "s3cret"));
        assertFalse(LinkServer.tokenMatches("s3cret", "s3cre"));
        assertFalse(LinkServer.tokenMatches("s3cret", ""));
        assertFalse(LinkServer.tokenMatches("s3cret", null));
        assertFalse(LinkServer.tokenMatches("s3cret", "S3cret"));
    }

    @Test void playerFilter() {
        RegionSpec r = new RegionSpec("world", 0, 60, 0, 32, 64, 32);
        assertTrue(PlayerFilter.near(r, 10, 70, 10, 16));
        assertTrue(PlayerFilter.near(r, -16, 44, 48, 16), "the margin applies on every axis");
        assertFalse(PlayerFilter.near(r, 10, 43.9, 10, 16), "deep below the region");
        assertFalse(PlayerFilter.near(r, 10, 140.1, 10, 16), "high above the region");
        assertFalse(PlayerFilter.near(r, 48.1, 70, 10, 16));
        assertTrue(PlayerFilter.shown(false, false, false));
        assertFalse(PlayerFilter.shown(true, false, false), "spectator");
        assertFalse(PlayerFilter.shown(false, true, false), "invisible");
        assertFalse(PlayerFilter.shown(false, false, true), "vanished");
    }
}
