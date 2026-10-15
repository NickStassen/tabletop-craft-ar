package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import static org.junit.jupiter.api.Assertions.*;

/**
 * [living] The world_state message and its send rule, and the entities fields for leads, gear, orbs, bobbers, carried
 * blocks, shulker lids and display entities (PROTOCOL.md "World state" and "Living entities"), against the fixtures.
 */
class LivingTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static EntityState st(int id, String type, double x, double y, double z, float yaw, String uuidTail) {
        EntityState s = new EntityState(id, type, x, y, z, yaw, 0);
        s.uuid = "3c1e7f0a-0000-4000-8000-000000000" + uuidTail;
        return s;
    }

    static WorldState stormyMidnight() {
        return new WorldState("world", "overworld", true, 114000, 18000, false, true, true, true);
    }

    @Test void worldStateMatchesFixture() throws Exception {
        JsonObject m = stormyMidnight().message();
        JsonObject f = fixture("world_state");
        for (String k : new String[] { "type", "world", "dimension", "skylight", "time", "day_time", "rain", "thunder" }) assertEquals(f.get(k), m.get(k), k);
        assertFalse(m.has("fixed_time"));
        assertFalse(m.has("ticking"));
        JsonObject nether = new WorldState("world_nether", WorldState.dimension("NETHER"), false, 5000, 6000, true, false, false, true).message();
        assertEquals("the_nether", nether.get("dimension").getAsString());
        assertTrue(nether.get("fixed_time").getAsBoolean());
        assertEquals("the_end", WorldState.dimension("THE_END"));
        assertEquals("custom", WorldState.dimension("CUSTOM"));
    }

    @Test void worldStateIsSentOnChangeAndOnceASecond() {
        WorldState a = stormyMidnight();
        assertTrue(WorldState.due(null, 0, a), "first message");
        assertFalse(WorldState.due(a, 5, new WorldState("world", "overworld", true, 114005, 18005, false, true, true, true)), "clock runs as predicted");
        assertTrue(WorldState.due(a, 5, new WorldState("world", "overworld", true, 114005, 18005, false, true, false, true)), "thunder stops");
        assertTrue(WorldState.due(a, 5, new WorldState("world", "overworld", true, 101000, 5000, false, true, true, true)), "time set");
        assertTrue(WorldState.due(a, 5, new WorldState("world_nether", "the_nether", false, 114005, 18005, true, false, false, true)), "other world");
        assertTrue(WorldState.due(a, WorldState.EVERY_TICKS, new WorldState("world", "overworld", true, 114020, 18020, false, true, true, true)), "once a second");
        WorldState frozen = new WorldState("world", "overworld", true, 114000, 18000, false, true, true, false);
        assertTrue(WorldState.due(a, 1, frozen), "the clock stopped");
        assertFalse(WorldState.due(frozen, 10, frozen), "a stopped clock isn't predicted to move");
    }

    /** tools/fixtures/entities_living.json. */
    static List<EntityState> living() {
        List<EntityState> l = new ArrayList<>();
        EntityState wolf = st(301, "minecraft:wolf", 100.5, 64, -20.5, 30, "301");
        wolf.bodyYaw = 30f; wolf.onGround = true; wolf.width = 0.6; wolf.height = 0.85; wolf.tamed = true; wolf.color = "red";
        wolf.variant = "pale"; wolf.leash = 7; wolf.eye = 0.68; wolf.equipment = Map.of("body", "minecraft:wolf_armor");
        EntityState knot = st(302, "minecraft:leash_knot", 103.5, 64.5, -20.5, 0, "302");
        knot.width = 0.375; knot.height = 0.5;
        EntityState horse = st(303, "minecraft:horse", 104.5, 64, -18.5, -90, "303");
        horse.bodyYaw = -90f; horse.onGround = true; horse.width = 1.3964844; horse.height = 1.6; horse.variant = "chestnut";
        horse.leash = 302; horse.eye = 1.52;
        horse.equipment = new LinkedHashMap<>(); horse.equipment.put("body", "minecraft:iron_horse_armor"); horse.equipment.put("saddle", "minecraft:saddle");
        EntityState llama = st(304, "minecraft:llama", 107.5, 64, -18.5, 180, "304");
        llama.bodyYaw = 180f; llama.onGround = true; llama.width = 0.9; llama.height = 1.87; llama.variant = "creamy"; llama.chested = true;
        llama.eye = 1.7; llama.equipment = Map.of("body", "minecraft:red_carpet");
        EntityState orb = st(305, "minecraft:experience_orb", 101.2, 64, -16.8, 0, "305");
        orb.onGround = true; orb.width = 0.5; orb.height = 0.5; orb.xp = 17;
        EntityState bobber = st(306, "minecraft:fishing_bobber", 98.5, 62.9, -14.5, 0, "306");
        bobber.width = 0.25; bobber.height = 0.25; bobber.owner = 7;
        EntityState enderman = st(307, "minecraft:enderman", 110.5, 64, -22.5, 45, "307");
        enderman.bodyYaw = 45f; enderman.onGround = true; enderman.width = 0.6; enderman.height = 2.9; enderman.eye = 2.55;
        enderman.carried = "minecraft:grass_block[snowy=false]";
        EntityState shulker = st(308, "minecraft:shulker", 112.5, 65, -22.5, 0, "308");
        shulker.bodyYaw = 0f; shulker.width = 1; shulker.height = 1.5; shulker.eye = 0.5; shulker.peek = 0.5; shulker.attach = "north";
        EntityState text = st(309, "minecraft:text_display", 100.5, 66, -24.5, 0, "309");
        text.width = 0; text.height = 0;
        var td = new EntityState.DisplayState();
        td.billboard = "center"; td.text = "Welcome\nto the farm"; td.lineWidth = 200; td.background = 0x40000000; td.textOpacity = 255;
        td.shadow = true; td.align = "center";
        text.display = td;
        EntityState item = st(310, "minecraft:item_display", 102.5, 66, -24.5, 90, "310");
        item.width = 0; item.height = 0; item.item = "minecraft:diamond_sword";
        var id = new EntityState.DisplayState();
        id.translation = new float[] { 0, 0.5f, 0 }; id.leftRotation = new float[] { 0, 0, 0.3827f, 0.9239f }; id.scale = new float[] { 2, 2, 2 };
        id.billboard = "fixed"; id.brightness = new int[] { 15, 15 }; id.itemTransform = "fixed";
        item.display = id;
        EntityState block = st(311, "minecraft:block_display", 104, 66, -25, 0, "311");
        block.width = 0; block.height = 0; block.block = "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]";
        var bd = new EntityState.DisplayState();
        bd.translation = new float[] { -0.25f, 0, -0.25f }; bd.scale = new float[] { 0.5f, 0.5f, 0.5f };
        block.display = bd;
        EntityState bolt = st(312, "minecraft:lightning_bolt", 108.5, 64, -14.5, 0, "312");
        EntityState golem = st(313, "minecraft:snow_golem", 106.5, 64, -26.5, 0, "313");
        golem.bodyYaw = 0f; golem.onGround = true; golem.width = 0.7; golem.height = 1.9; golem.sheared = true; golem.eye = 1.7;
        EntityState villager = st(314, "minecraft:villager", 99.5, 64, -26.5, 0, "314");
        villager.bodyYaw = 0f; villager.onGround = true; villager.width = 0.6; villager.height = 1.95; villager.eye = 1.62;
        villager.variant = "plains"; villager.name = "Bob"; villager.nameVisible = true;
        l.addAll(List.of(wolf, knot, horse, llama, orb, bobber, enderman, shulker, text, item, block, bolt, golem, villager));
        return l;
    }

    @Test void livingEntitiesMatchFixture() throws Exception {
        Map<Integer, EntityState> sent = new HashMap<>();
        JsonObject m = EntityDelta.message(sent, living(), true);
        assertEquals(fixture("entities_living").get("entities"), m.get("entities"), m.get("entities").toString());
    }

    @Test void livingFieldsAreClearedExplicitly() {
        Map<Integer, EntityState> sent = new HashMap<>();
        EntityDelta.message(sent, living(), true);
        List<EntityState> next = living();
        next.get(0).leash = null;                         // the player let go of the wolf
        next.get(3).chested = false;                      // the llama's chest was removed
        next.get(6).carried = null;                       // the enderman put its block down
        next.get(7).peek = 0.52;                          // lid creeping: under PEEK_EPS, not sent
        next.get(9).display = null;                       // (a display can't lose its state; just the null path)
        next.get(13).nameVisible = false;
        JsonObject d = EntityDelta.message(sent, next, false);
        Map<Integer, JsonObject> byId = new HashMap<>();
        for (var e : d.getAsJsonArray("entities")) byId.put(e.getAsJsonObject().get("id").getAsInt(), e.getAsJsonObject());
        assertTrue(byId.get(301).get("leash").isJsonNull());
        assertFalse(byId.get(304).get("chested").getAsBoolean());
        assertTrue(byId.get(307).get("carried").isJsonNull());
        assertFalse(byId.containsKey(308), "peek moved less than PEEK_EPS");
        assertTrue(byId.get(310).get("display").isJsonNull());
        assertFalse(byId.get(314).get("name_visible").getAsBoolean());
        next.get(7).peek = 0;                             // closed: always sent
        JsonObject closed = EntityDelta.message(sent, next, false);
        assertEquals(0.0, closed.getAsJsonArray("entities").get(0).getAsJsonObject().get("peek").getAsDouble());
        assertNull(EntityDelta.message(sent, next, false));
    }

    @Test void displayStateRoundsLikeTheWire() {
        float[] r = EntityState.DisplayState.round(new float[] { 0.38268343f, -0.0000001f, 0.92387953f, 1f }, 4);
        assertArrayEquals(new float[] { 0.3827f, 0f, 0.9239f, 1f }, r);
        assertEquals(Float.floatToIntBits(0f), Float.floatToIntBits(r[1]), "no negative zero on the wire");
        var a = new EntityState.DisplayState(); var b = new EntityState.DisplayState();
        assertEquals(a, b);
        b.scale = new float[] { 1, 1, 1.001f };
        assertNotEquals(a, b);
        assertEquals("[0.3827,0.0,0.9239,1.0]", EntityDelta.display(withLeft(r)).get("left_rotation").toString());
    }

    private static EntityState.DisplayState withLeft(float[] q) { var d = new EntityState.DisplayState(); d.leftRotation = q; return d; }
}
