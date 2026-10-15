package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.*;

/**
 * [sound] sound_groups, the sound actions and asset delivery (PROTOCOL.md "Sound", "Asset delivery") against the
 * fixtures: the group message, the sounds the server sends, the binary asset frame and the LAN check.
 */
class SoundTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    static SoundGroups.Group group(String family, float volume) {
        String b = "block." + family + ".";
        return new SoundGroups.Group(b + "break", b + "step", b + "place", b + "hit", b + "fall", volume, 1f);
    }

    @Test void soundGroupsMatchFixture() throws Exception {
        SoundGroups g = SoundGroups.empty();
        for (String id : List.of("minecraft:stone", "minecraft:cobblestone", "minecraft:stone_bricks")) g.add(id, group("stone", 1f));
        g.add("minecraft:oak_planks", group("wood", 1f));
        g.add("minecraft:oak_log", group("wood", 1f));
        g.add("minecraft:grass_block", group("grass", 1f));
        g.add("minecraft:anvil", group("anvil", 0.3f));
        JsonObject m = g.message(), f = fixture("sound_groups");
        assertEquals(f.get("type"), m.get("type"));
        assertEquals(f.get("count"), m.get("count"));
        assertEquals(f.get("groups"), m.get("groups"), "groups, their order, sounds, volume, pitch and blocks");
        assertEquals(4, g.size());
        assertEquals("block.wood.step", g.groupOf("minecraft:oak_log").step());
        assertEquals("block.stone.break", SoundGroups.key("minecraft:block.stone.break"));
        assertEquals("mymod:block.thing.break", SoundGroups.key("mymod:block.thing.break"));
    }

    @Test void soundRules() {
        assertEquals(1f, SoundRules.notePitch(12), 1e-6);
        assertEquals(0.5f, SoundRules.notePitch(0), 1e-6);
        assertEquals(2f, SoundRules.notePitch(24), 1e-6);
        assertEquals(1.189f, SoundRules.notePitch(15), 1e-3);
        assertEquals("block.chest.open", SoundRules.container("minecraft:trapped_chest", true, 0.5f).sound());
        assertEquals(0.95f, SoundRules.container("minecraft:chest", false, 0.5f).pitch(), 1e-6);
        assertEquals("block.barrel.close", SoundRules.container("minecraft:barrel", false, 0f).sound());
        assertEquals("block.shulker_box.open", SoundRules.container("minecraft:lime_shulker_box", true, 0f).sound());
        assertEquals("block.ender_chest.open", SoundRules.container("minecraft:ender_chest", true, 0f).sound());
        assertEquals("block.copper_chest_weathered.open", SoundRules.container("minecraft:waxed_weathered_copper_chest", true, 0f).sound());
        assertEquals("block.copper_chest.close", SoundRules.container("minecraft:exposed_copper_chest", false, 0f).sound());
        assertNull(SoundRules.container("minecraft:furnace", true, 0f), "furnaces make no container sound");
        var boom = SoundRules.explosion("minecraft:tnt", 0.5f, 0.5f);
        assertEquals("entity.generic.explode", boom.sound());
        assertEquals(4f, boom.volume());
        assertEquals(0.7f, boom.pitch(), 1e-6);
        assertEquals("entity.wind_charge.wind_burst", SoundRules.explosion("minecraft:wind_charge", 0, 0).sound());
        assertEquals("entity.generic.explode", SoundRules.explosion(null, 0, 0).sound(), "beds and respawn anchors");
        assertEquals("item.bucket.empty_lava", SoundRules.bucket("minecraft:lava_bucket", false).sound());
        assertEquals("item.bucket.fill_powder_snow", SoundRules.bucket("minecraft:powder_snow", true).sound());
        assertEquals("item.bucket.empty_fish", SoundRules.bucket("minecraft:cod_bucket", false).sound());
        assertNull(SoundRules.bucket("minecraft:milk_bucket", false));
        assertEquals("item.flintandsteel.use", SoundRules.ignite("FLINT_AND_STEEL", 0.5f).sound());
        assertNull(SoundRules.ignite("SPREAD", 0.5f), "spreading fire is silent");
        assertEquals("music_disc.cat", SoundRules.song("minecraft:music_disc.cat").sound());
    }

    @Test void soundActionsMatchFixtureAndOnlyGoToSoundSessions() throws Exception {
        ActionBatch b = new ActionBatch();
        b.sound("world", 105.5, 64.5, -35.5, "block.note_block.harp", 3f, SoundRules.notePitch(15), false);
        b.sound("world", 107.5, 64.5, -35.5, "block.chest.open", 0.5f, 0.94f, false);
        b.sound("world", 110.2, 63.0, -30.7, "entity.generic.explode", 4f, 0.71f, false);
        b.sound("world", 103.5, 64.5, -38.5, "music_disc.cat", 4f, 1f, true);
        b.soundStop("world", 103, 64, -38);
        b.breakBlock("world", 106, 63, -36, "minecraft:stone", 7);
        b.sound("world", 105.5, 64.5, -35.5, "block.note_block.harp", 3f, SoundRules.notePitch(15), false);   // repeat in the tick collapses
        b.sound("world_nether", 105.5, 64.5, -35.5, "block.bell.use", 2f, 1f, false);                          // other world
        b.sound("world", 300.5, 64.5, -35.5, "block.bell.use", 2f, 1f, false);                                 // far outside
        RegionSpec r = new RegionSpec("world", 100, 60, -40, 32, 64, 32);
        JsonArray with = b.forRegion(r, true), without = b.forRegion(r, false);
        JsonArray want = fixture("actions_sound").getAsJsonArray("actions");
        assertEquals(want.size(), with.size(), "sound session: " + with);
        for (int i = 0; i < want.size(); i++) {
            JsonObject w = want.get(i).getAsJsonObject(), g = findSame(with, w);
            assertNotNull(g, "fixture action " + w + " in " + with);
            for (String k : w.keySet()) assertEquals(w.get(k), g.get(k), k + " of " + w);
        }
        assertEquals(1, without.size(), "a session without sound_v1 gets only the break");
        assertEquals("break", without.get(0).getAsJsonObject().get("kind").getAsString());
        assertNull(new ActionBatch().message(r, true));
        // Sounds just outside the region still count (SOUND_MARGIN), like a door slammed at its edge.
        ActionBatch edge = new ActionBatch();
        edge.sound("world", 100 - 6.5, 64.5, -35.5, "block.wooden_door.open", 1f, 1f, false);
        assertEquals(1, edge.forRegion(r, true).size());
    }

    private static JsonObject findSame(JsonArray a, JsonObject w) {
        for (var e : a) {
            JsonObject o = e.getAsJsonObject();
            if (o.get("kind").equals(w.get("kind")) && o.get("pos").equals(w.get("pos"))) return o;
        }
        return null;
    }

    @Test void assetFrameMatchesFixture() throws Exception {
        byte[] bin = Files.readAllBytes(Path.of("..", "tools", "fixtures", "asset_chunk.bin"));
        AssetService.Chunk c = AssetService.parse(ByteBuffer.wrap(bin));
        assertEquals("sounds.zip", c.name());
        assertEquals(262144, c.offset());
        assertEquals("tabletop asset chunk", new String(c.data(), java.nio.charset.StandardCharsets.US_ASCII));
        ByteBuffer built = AssetService.frame("sounds.zip", 262144, "tabletop asset chunk".getBytes(java.nio.charset.StandardCharsets.US_ASCII));
        byte[] out = new byte[built.remaining()];
        built.get(out);
        assertArrayEquals(bin, out, "the server's frame is byte for byte the fixture");
        assertThrows(IllegalArgumentException.class, () -> AssetService.parse(ByteBuffer.wrap("XXXX".getBytes())));
        assertThrows(IllegalArgumentException.class, () -> AssetService.frame("", 0, new byte[1]));
    }

    @Test void assetOfferMatchesFixtureShape() throws Exception {
        JsonObject f = fixture("assets"), g = fixture("asset_get"), e = fixture("error_asset_denied");
        assertEquals(AssetService.CHUNK, f.get("chunk").getAsInt());
        for (var x : f.getAsJsonArray("files")) {
            JsonObject o = x.getAsJsonObject();
            assertTrue(AssetService.NAMES.contains(o.get("name").getAsString()));
            assertEquals(64, o.get("sha256").getAsString().length());
        }
        assertTrue(g.get("length").getAsInt() <= AssetService.MAX_CHUNK);
        JsonObject err = AssetService.error("asset_denied", "assets.serve is false", "sounds.zip", 12L);
        for (String k : List.of("type", "code", "message", "ref_seq", "fatal", "op", "name")) assertEquals(e.get(k), err.get(k), k);
        assertEquals(Set.of("entity_models.json", "sounds.zip", "pack.zip"), Set.copyOf(AssetService.NAMES), "only these names are ever read");
    }

    @Test void assetsOnlyOnTheLan() throws Exception {
        for (String a : List.of("192.168.1.20", "10.0.0.5", "172.16.3.4", "127.0.0.1", "169.254.1.1", "fd12:3456::1", "fe80::1", "::1"))
            assertTrue(AssetService.lan(InetAddress.getByName(a)), a);
        for (String a : List.of("8.8.8.8", "172.32.0.1", "2001:db8::1", "100.64.0.1"))
            assertFalse(AssetService.lan(InetAddress.getByName(a)), a);
        assertFalse(AssetService.lan(null));
    }

    @Test void assetHashes(@org.junit.jupiter.api.io.TempDir Path dir) throws Exception {
        Path p = dir.resolve("sounds.zip");
        Files.writeString(p, "abc");
        assertEquals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AssetService.sha256(p));
    }
}
