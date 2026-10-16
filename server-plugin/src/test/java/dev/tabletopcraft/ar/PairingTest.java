package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashSet;
import java.util.Set;
import java.util.logging.Logger;

import static org.junit.jupiter.api.Assertions.*;

/** [setup] Pairing with a 6-digit code (PROTOCOL.md "Pairing"): codes, tokens, devices.yml, revoking, limits, messages. */
class PairingTest {
    @TempDir Path dir;

    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("../tools/fixtures/" + name + ".json"))).getAsJsonObject();
    }

    private PairingService service() {
        PairingService p = new PairingService(Logger.getLogger("test"));
        p.configure(dir, true, false);
        return p;
    }

    @Test void codeThenApproveIssuesATokenThatChecks() {
        PairingService p = service();
        Session s = new Session(null);
        long now = 1_000_000;
        PairingService.Pending code = p.request(s, "Quest 3", "192.168.1.20", now);
        assertNotNull(code);
        assertTrue(code.code().matches("[0-9]{6}"));
        assertNull(p.approve("000000".equals(code.code()) ? "000001" : "000000", null, null, now), "a wrong code pairs nothing");
        PairingService.Issued is = p.approve(code.code(), null, "Steve", now + 1000);
        assertNotNull(is);
        assertSame(s, is.session());
        assertEquals("Quest 3", is.device().name());
        assertEquals("Steve", is.device().player());
        assertTrue(is.token().matches("[A-Za-z0-9_-]{43}"), "256 bits, base64url: " + is.token());
        assertEquals(is.device().id(), p.check(is.token()).id());
        assertNull(p.check(is.token() + "x"));
        assertNull(p.approve(code.code(), null, null, now + 2000), "a code works once");
    }

    @Test void onlyTheHashIsStoredAndSurvivesARestart() throws Exception {
        PairingService p = service();
        PairingService.Pending c = p.request(new Session(null), "Quest 3", "10.0.0.5", 0);
        PairingService.Issued is = p.approve(c.code(), "Desk headset", null, 0);
        String yml = Files.readString(dir.resolve(PairingService.FILE));
        assertFalse(yml.contains(is.token()), "the token itself is never written");
        assertTrue(yml.contains(PairingService.hash(is.token())));
        assertTrue(yml.contains("Desk headset"));
        String id = p.serverId();
        assertTrue(id.matches("[0-9a-f]{16}"));
        PairingService again = service();
        assertEquals(id, again.serverId(), "the server id is kept");
        assertEquals("Desk headset", again.check(is.token()).name());
    }

    @Test void unpairRevokesByIdNameOrAll() {
        PairingService p = service();
        String[] tokens = new String[3];
        for (int i = 0; i < 3; i++) {
            PairingService.Pending c = p.request(new Session(null), i == 2 ? "Other" : "Quest 3", "10.0.0." + i, 0);
            tokens[i] = p.approve(c.code(), null, null, 0).token();
        }
        String id0 = p.check(tokens[0]).id();
        assertEquals(1, p.unpair(id0).size());
        assertNull(p.check(tokens[0]));
        assertEquals(1, p.unpair("quest 3").size(), "by name, case-insensitive");
        assertTrue(p.unpair("nobody").isEmpty());
        assertEquals(1, p.unpair("all").size());
        assertTrue(p.devices().isEmpty());
        assertNull(p.check(tokens[2]));
    }

    @Test void codesExpireAndRequestsAreLimited() {
        PairingService p = service();
        Session s = new Session(null);
        PairingService.Pending c = p.request(s, "Quest", "10.0.0.9", 0);
        assertNull(p.approve(c.code(), null, null, PairingService.CODE_TTL_MS + 1), "five minutes, then the code is gone");
        // A new request from the same session replaces its code.
        PairingService.Pending c1 = p.request(s, "Quest", "10.0.0.9", 10);
        PairingService.Pending c2 = p.request(s, "Quest", "10.0.0.9", 20);
        assertEquals(1, p.waiting(30).size());
        assertNotEquals(c1, c2);
        // At most REQUESTS_PER_MINUTE per address (3 used so far).
        for (int i = 3; i < PairingService.REQUESTS_PER_MINUTE; i++) assertNotNull(p.request(new Session(null), "Q", "10.0.0.9", 40 + i));
        assertNull(p.request(new Session(null), "Q", "10.0.0.9", 100));
        assertTrue(p.refusal("10.0.0.9", 100).contains("too many pairing requests"));
        assertNotNull(p.request(new Session(null), "Q", "10.0.0.9", 61_000), "a minute later it works again");
        // A closed session's code is dropped.
        Session gone = new Session(null);
        PairingService.Pending g = p.request(gone, "Gone", "10.0.0.10", 0);
        gone.state = Session.State.CLOSED;
        assertNull(p.approve(g.code(), null, null, 1));
        // Off: no codes at all.
        PairingService off = new PairingService(Logger.getLogger("test"));
        off.configure(dir, false, false);
        assertNull(off.request(new Session(null), "Q", "10.0.0.11", 0));
        assertTrue(off.refusal("10.0.0.11", 0).contains("auth.pairing"));
    }

    @Test void codesAreUniqueAmongWaitingHeadsets() {
        PairingService p = service();
        Set<String> codes = new HashSet<>();
        for (int i = 0; i < PairingService.MAX_PENDING; i++) codes.add(p.request(new Session(null), "Q", "10.0.1." + i, 0).code());
        assertEquals(PairingService.MAX_PENDING, codes.size());
        assertNull(p.request(new Session(null), "Q", "10.0.2.1", 0), "too many waiting");
    }

    @Test void namesAreCleaned() {
        assertEquals("Headset", PairingService.cleanName(null));
        assertEquals("Headset", PairingService.cleanName(" \n\t"));
        assertEquals("Quest 3", PairingService.cleanName("Quest\n 3".replace("\n", "")));
        assertEquals("ab", PairingService.cleanName("a§b"));
        assertEquals(PairingService.MAX_NAME, PairingService.cleanName("x".repeat(100)).length());
    }

    @Test void messagesMatchTheFixtures() throws Exception {
        PairingService p = service();
        PairingService.Pending c = p.request(new Session(null), "Quest 3", "10.0.0.1", 0);
        JsonObject code = PairingService.codeMessage(c, true, false), fc = fixture("pair_code");
        for (String k : fc.keySet()) if (!k.equals("seq") && !k.equals("t")) assertTrue(code.has(k), "pair_code." + k);
        assertEquals(fc.get("expires_s").getAsInt(), code.get("expires_s").getAsInt());
        assertTrue(PairingService.codeMessage(c, false, true).get("token_rejected").getAsBoolean());
        PairingService.Issued is = p.approve(c.code(), null, null, 0);
        JsonObject paired = PairingService.pairedMessage(is, p.serverId()), fp = fixture("paired");
        for (String k : fp.keySet()) if (!k.equals("seq") && !k.equals("t")) assertTrue(paired.has(k), "paired." + k);
        assertEquals(fp.get("device").getAsString().length(), paired.get("device").getAsString().length());
        // hello and welcome fixtures carry the same token and ids
        assertEquals(fixture("hello_pair").get("device_token").getAsString(), fp.get("token").getAsString());
        assertEquals(fixture("welcome_pair").get("server_id").getAsString(), fp.get("server_id").getAsString());
        JsonObject err = LinkServer.pairError("pair_denied", "x", 3L, false), fe = fixture("error_pair_denied");
        assertEquals(fe.get("op").getAsString(), err.get("op").getAsString());
    }

    @Test void discoveryReplyCarriesIdAndPair() throws Exception {
        JsonObject r = LanBeacon.reply("A Minecraft Server", 8765, "/tabletop", false, "1.1.0", "26.3", 2, "3f9c2a7e1b4d5c6a", true);
        JsonObject f = fixture("discovery_reply_pair");
        assertEquals(f, r);
    }
}
