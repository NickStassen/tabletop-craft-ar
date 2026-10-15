package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Test;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import static org.junit.jupiter.api.Assertions.*;

/** [motion] Block animations (lid, bell, explode actions) and the fuse/swell entity fields (PROTOCOL.md "Block animations"). */
class MotionTest {
    private static JsonObject fixture(String name) throws Exception {
        return JsonParser.parseString(Files.readString(Path.of("..", "tools", "fixtures", name + ".json"))).getAsJsonObject();
    }

    private static EntityState st(int id, String type, double x, double y, double z, float yaw) {
        EntityState s = new EntityState(id, type, x, y, z, yaw, 0);
        s.uuid = "3c1e7f0a-0000-4000-8000-000000000" + id;
        return s;
    }

    @Test void motionActionsMatchFixture() throws Exception {
        ActionBatch b = new ActionBatch();
        b.lid("world", 107, 64, -35, true);
        b.lid("world", 109, 64, -35, true);
        b.lid("world", 109, 64, -35, false);   // opened and closed in one tick: the last wins
        b.bell("world", 104, 65, -33, "east");
        b.explode("world", 110.2, 63.0, -30.7, ActionBatch.explosionPower("minecraft:tnt", false));
        b.explode("world", 300.5, 63.0, -30.7, 4f);   // far outside the region
        RegionSpec r = new RegionSpec("world", 100, 60, -40, 32, 64, 32);
        JsonArray got = b.forRegion(r, false);
        assertEquals(fixture("actions_motion").getAsJsonArray("actions"), got, got.toString());
        // An explosion just outside the region still shows its cloud (EXPLOSION_MARGIN); a lid doesn't.
        ActionBatch edge = new ActionBatch();
        edge.explode("world", 100 - 4.5, 63, -30.5, 3f);
        edge.lid("world", 99, 64, -30, true);
        assertEquals(1, edge.forRegion(r, false).size());
    }

    @Test void lidsAndPowers() {
        for (String id : new String[] { "minecraft:chest", "minecraft:trapped_chest", "minecraft:ender_chest", "minecraft:copper_chest",
                "minecraft:weathered_copper_chest", "minecraft:shulker_box", "minecraft:red_shulker_box" })
            assertTrue(ActionBatch.lidded(id), id);
        for (String id : new String[] { "minecraft:barrel", "minecraft:hopper", "minecraft:furnace" }) assertFalse(ActionBatch.lidded(id), id);
        assertEquals(4f, ActionBatch.explosionPower("minecraft:tnt", false));
        assertEquals(3f, ActionBatch.explosionPower("minecraft:creeper", false));
        assertEquals(6f, ActionBatch.explosionPower("minecraft:creeper", true));
        assertEquals(6f, ActionBatch.explosionPower("minecraft:end_crystal", false));
        assertEquals(5f, ActionBatch.explosionPower(null, false));   // beds, respawn anchors
    }

    /** tools/fixtures/entities_motion.json. */
    static List<EntityState> motion() {
        List<EntityState> l = new ArrayList<>();
        EntityState tnt = st(401, "minecraft:tnt", 120.5, 64, -25.5, 0);
        tnt.onGround = true; tnt.width = 0.98; tnt.height = 0.98; tnt.fuse = 37;
        EntityState creeper = st(402, "minecraft:creeper", 122.5, 64, -25.5, 90);
        creeper.bodyYaw = 90f; creeper.onGround = true; creeper.width = 0.6; creeper.height = 1.7; creeper.eye = 1.45; creeper.swell = 0.5;
        EntityState zombie = st(403, "minecraft:zombie", 124.5, 64, -25.5, 180);
        zombie.bodyYaw = 180f; zombie.onGround = true; zombie.width = 0.6; zombie.height = 1.95; zombie.eye = 1.74; zombie.pose = "dying";
        EntityState wolf = st(404, "minecraft:wolf", 126.5, 64, -25.5, -90);
        wolf.bodyYaw = -90f; wolf.onGround = true; wolf.width = 0.6; wolf.height = 0.85; wolf.tamed = true; wolf.pose = "sitting"; wolf.eye = 0.68;
        EntityState villager = st(405, "minecraft:villager", 128.5, 64.69, -25.5, 0);
        villager.bodyYaw = 0f; villager.width = 0.2; villager.height = 0.2; villager.variant = "minecraft:plains"; villager.pose = "sleeping"; villager.eye = 0.2;
        l.add(tnt); l.add(creeper); l.add(zombie); l.add(wolf); l.add(villager);
        return l;
    }

    @Test void motionEntitiesMatchFixture() throws Exception {
        JsonObject m = EntityDelta.message(new HashMap<>(), motion(), true);
        assertEquals(fixture("entities_motion").get("entities"), m.get("entities"), m.get("entities").toString());
    }

    @Test void fuseCountsDownOnTheClient() {
        Map<Integer, EntityState> sent = new HashMap<>();
        EntityDelta.message(sent, motion(), true);
        List<EntityState> next = motion();
        next.get(0).fuse = 37 - 5;        // half a second: under FUSE_EPS, the headset counts it down itself
        next.get(1).swell = 0.53;         // under PEEK_EPS
        assertNull(EntityDelta.message(sent, next, false));
        next.get(0).fuse = 37 - EntityDelta.FUSE_EPS;
        next.get(1).swell = 0;            // the creeper calmed down: always sent
        JsonObject d = EntityDelta.message(sent, next, false);
        Map<Integer, JsonObject> byId = new HashMap<>();
        for (var e : d.getAsJsonArray("entities")) byId.put(e.getAsJsonObject().get("id").getAsInt(), e.getAsJsonObject());
        assertEquals(27, byId.get(401).get("fuse").getAsInt());
        assertEquals(0.0, byId.get(402).get("swell").getAsDouble());
    }
}
