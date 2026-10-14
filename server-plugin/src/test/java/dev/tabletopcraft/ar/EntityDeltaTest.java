package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
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

/** PROTOCOL.md 4.21: the entities message built by EntityDelta against the golden fixtures, and its delta rules. */
class EntityDeltaTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static EntityState st(int id, String type, double x, double y, double z, float yaw, float pitch, String uuidTail) {
        EntityState s = new EntityState(id, type, x, y, z, yaw, pitch);
        s.uuid = "3c1e7f0a-0000-4000-8000-000000000" + uuidTail;
        return s;
    }

    /** The scene in tools/fixtures/entities.json: a boat carrying a player and a baby pig, a sheep, a zombie, an item, a frame. */
    static List<EntityState> scene() {
        List<EntityState> l = new ArrayList<>();
        EntityState boat = st(101, "minecraft:oak_boat", 112.5, 62.53, -30.25, 90, 0, "101");
        boat.passengers = new int[] { 7, 102 }; boat.width = 1.375; boat.height = 0.5625;
        EntityState pig = st(102, "minecraft:pig", 112.1, 62.6, -30.25, 80, 0, "102");
        pig.bodyYaw = 90f; pig.vehicle = 101; pig.width = 0.45; pig.height = 0.45; pig.baby = true; pig.variant = "temperate";
        EntityState sheep = st(103, "minecraft:sheep", 105.3, 64, -35.5, 10, 0, "103");
        sheep.bodyYaw = 10f; sheep.onGround = true; sheep.width = 0.9; sheep.height = 1.3; sheep.color = "light_blue";
        EntityState zombie = st(104, "minecraft:zombie", 108.7, 64, -28, -45, 12.5f, "104");
        zombie.bodyYaw = -40f; zombie.onGround = true; zombie.width = 0.6; zombie.height = 1.95;
        zombie.equipment = new LinkedHashMap<>(Map.of("main_hand", "minecraft:iron_sword"));
        zombie.equipment.put("head", "minecraft:leather_helmet");
        EntityState item = st(105, "minecraft:item", 110.42, 64, -33.9, 0, 0, "105");
        item.onGround = true; item.width = 0.25; item.height = 0.25; item.item = "minecraft:diamond"; item.count = 3;
        EntityState frame = st(106, "minecraft:item_frame", 104.5, 65.5, -36.96875, 180, 0, "106");
        frame.width = 0.75; frame.height = 0.75; frame.item = "minecraft:compass"; frame.facing = "north"; frame.rotation = 2;
        l.add(boat); l.add(pig); l.add(sheep); l.add(zombie); l.add(item); l.add(frame);
        return l;
    }

    @Test void fullMessageMatchesFixture() throws Exception {
        Map<Integer, EntityState> sent = new HashMap<>();
        JsonObject m = EntityDelta.message(sent, scene(), true);
        JsonObject f = fixture("entities");
        assertEquals("entities", m.get("type").getAsString());
        assertTrue(m.get("full").getAsBoolean());
        assertEquals(f.get("entities"), m.get("entities"));
        assertFalse(m.has("removed"));
        assertEquals(6, sent.size());
    }

    @Test void deltaMessageMatchesFixture() throws Exception {
        Map<Integer, EntityState> sent = new HashMap<>();
        EntityDelta.message(sent, scene(), true);
        List<EntityState> next = scene();
        next.removeIf(s -> s.id == 105);                       // picked up
        next.get(0).x = 113.0; next.get(0).yaw = 95;            // boat paddles east and turns
        next.get(1).x = 112.6; next.get(1).yaw = 85; next.get(1).bodyYaw = 95f;
        next.get(2).x += 0.004; next.get(2).yaw += 0.5f;        // sheep jitter under the thresholds: nothing sent
        next.get(3).equipment = Map.of("main_hand", "minecraft:iron_sword");
        JsonObject m = EntityDelta.message(sent, next, false);
        JsonObject f = fixture("entities_delta");
        assertFalse(m.get("full").getAsBoolean());
        assertEquals(f.get("entities"), m.get("entities"));
        assertEquals(f.get("removed"), m.get("removed"));
        assertNull(EntityDelta.message(sent, next, false), "no change -> no message");
    }

    /** tools/fixtures/entities_armor_stand.json: a survival stand, a posed stand with arms and a sword, an invisible small stand. */
    static List<EntityState> armorStands() {
        EntityState plain = st(201, "minecraft:armor_stand", 100.5, 64, -20.5, 45, 0, "201");
        plain.bodyYaw = 45f; plain.onGround = true; plain.width = 0.5; plain.height = 1.975; plain.basePlate = true;
        EntityState posed = st(202, "minecraft:armor_stand", 102.5, 64, -20.5, -90, 0, "202");
        posed.bodyYaw = -90f; posed.onGround = true; posed.width = 0.5; posed.height = 1.975; posed.arms = true;
        posed.standPose = EntitySampler.standPose(
            new org.bukkit.util.EulerAngle(Math.toRadians(10), Math.toRadians(20), 0), new org.bukkit.util.EulerAngle(0, Math.toRadians(5), 0),
            new org.bukkit.util.EulerAngle(Math.toRadians(-30), 0, Math.toRadians(-10)), new org.bukkit.util.EulerAngle(Math.toRadians(-110), Math.toRadians(25), Math.toRadians(10)),
            new org.bukkit.util.EulerAngle(Math.toRadians(-1), 0, Math.toRadians(-1)), new org.bukkit.util.EulerAngle(Math.toRadians(1), 0, Math.toRadians(1)));
        posed.equipment = Map.of("main_hand", "minecraft:iron_sword");
        EntityState small = st(203, "minecraft:armor_stand", 104.5, 64, -20.5, 180, 0, "203");
        small.bodyYaw = 180f; small.onGround = true; small.width = 0.25; small.height = 0.9875; small.small = true; small.invisible = true; small.basePlate = true;
        small.equipment = Map.of("head", "minecraft:iron_helmet");
        return new ArrayList<>(List.of(plain, posed, small));
    }

    @Test void armorStandFixture() throws Exception {
        Map<Integer, EntityState> sent = new HashMap<>();
        JsonObject m = EntityDelta.message(sent, armorStands(), true);
        assertEquals(fixture("entities_armor_stand").get("entities"), m.get("entities"));
        // Vanilla's default pose is not sent at all; a pose back to default is cleared with null.
        var defaults = EntityState.DEFAULT_STAND_POSE;
        var radians = new org.bukkit.util.EulerAngle[6];
        for (int i = 0; i < 6; i++) radians[i] = new org.bukkit.util.EulerAngle(Math.toRadians(defaults[i * 3]), Math.toRadians(defaults[i * 3 + 1]), Math.toRadians(defaults[i * 3 + 2]));
        assertNull(EntitySampler.standPose(radians));
        List<EntityState> next = armorStands();
        next.get(1).standPose = null; next.get(1).arms = false; next.get(0).basePlate = false;
        JsonObject d = EntityDelta.message(sent, next, false);
        JsonObject posed = d.getAsJsonArray("entities").get(1).getAsJsonObject();
        assertTrue(posed.get("stand_pose").isJsonNull());
        assertFalse(posed.get("arms").getAsBoolean());
        assertFalse(d.getAsJsonArray("entities").get(0).getAsJsonObject().get("base_plate").getAsBoolean());
        assertNull(EntityDelta.message(sent, next, false), "no change -> no message");
    }

    @Test void smallMovesAccumulateUntilTheyCount() {
        Map<Integer, EntityState> sent = new HashMap<>();
        EntityState cow = new EntityState(1, "minecraft:cow", 0, 64, 0, 0, 0);
        EntityDelta.message(sent, List.of(cow), true);
        double x = 0;
        int msgs = 0;
        for (int i = 0; i < 10; i++) {
            x += 0.004;
            EntityState c = new EntityState(1, "minecraft:cow", x, 64, 0, 0, 0);
            if (EntityDelta.message(sent, List.of(c), false) != null) msgs++;
        }
        assertEquals(3, msgs, "0.004 steps: a pos update every 0.012 blocks");
        assertTrue(Math.abs(sent.get(1).x - 0.036) < 1e-9);
    }

    @Test void clearedFieldsAreSentExplicitly() {
        Map<Integer, EntityState> sent = new HashMap<>();
        EntityState pig = new EntityState(2, "minecraft:pig", 0, 64, 0, 0, 0);
        pig.vehicle = 9; pig.baby = true; pig.name = "Bacon";
        EntityDelta.message(sent, List.of(pig), true);
        EntityState off = new EntityState(2, "minecraft:pig", 0, 64, 0, 359.5f, 0);   // 0.5 degrees across the wrap: no yaw
        JsonObject m = EntityDelta.message(sent, List.of(off), false);
        JsonObject e = m.getAsJsonArray("entities").get(0).getAsJsonObject();
        assertTrue(e.get("vehicle").isJsonNull());
        assertFalse(e.get("baby").getAsBoolean());
        assertTrue(e.get("name").isJsonNull());
        assertFalse(e.has("yaw"));
        assertEquals(2, e.get("id").getAsInt());
    }

    @Test void fullAfterRegionChangeReplacesEverything() {
        Map<Integer, EntityState> sent = new HashMap<>();
        EntityDelta.message(sent, scene(), true);
        JsonObject m = EntityDelta.message(sent, List.of(), true);
        assertTrue(m.get("full").getAsBoolean());
        assertEquals(0, m.getAsJsonArray("entities").size());
        assertFalse(m.has("removed"), "a full list implies the removals");
        assertTrue(sent.isEmpty());
    }

    @Test void poseNames() {
        assertNull(EntitySampler.poseName("STANDING"));
        assertEquals("gliding", EntitySampler.poseName("FALL_FLYING"));
        assertEquals("sneaking", EntitySampler.poseName("SNEAKING"));
        assertEquals("spin_attack", EntitySampler.poseName("SPIN_ATTACK"));
        assertEquals("temperate", EntitySampler.strip("minecraft:temperate"));
        assertEquals("mypack:odd", EntitySampler.strip("mypack:odd"));
    }

    @Test void ridingPlayerFixtureShape() throws Exception {
        JsonObject p = fixture("player_positions_riding").getAsJsonArray("players").get(0).getAsJsonObject();
        JsonArray boatPassengers = fixture("entities").getAsJsonArray("entities").get(0).getAsJsonObject().getAsJsonArray("passengers");
        assertEquals(p.get("id"), boatPassengers.get(0), "the boat lists the player's entity id first");
        assertEquals(101, p.get("vehicle").getAsInt());
        assertEquals("riding", p.get("pose").getAsString());
        assertEquals("minecraft:diamond_sword", p.getAsJsonObject("equipment").get("main_hand").getAsString());
    }
}
