// Writes entity_models.json: the box geometry (pivots, rotations, cube sizes, skin UV offsets) of the entity models the
// Quest draws, plus the dye colours, read from YOUR OWN Minecraft client jar. Run it on your PC and copy the result to the
// headset next to pack.zip (docs/QUEST_SETUP.md Part E). The output is derived from the game's code: never commit or
// share it (it is git-ignored). Without it the Quest draws entities as boxes of their size.
// Needs JDK 25 (the 26.x classes are Java 25 bytecode); Java's single-file launcher runs it directly:
//
//   java tools/entity_models/DumpEntityModels.java ~/.minecraft 26.3 entity_models.json
//   (Windows: java tools\entity_models\DumpEntityModels.java %APPDATA%\.minecraft 26.3 entity_models.json)
//
// It loads the client classes from versions/<v>/<v>.jar plus the libraries the version JSON lists, calls
// LayerDefinitions.createRoots() (pure model data, the game doesn't start) and walks each LayerDefinition's mesh.
import java.io.File;
import java.lang.reflect.Field;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.*;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class DumpEntityModels {
    /** Model layers ("name" = minecraft:name#main, "name#layer" otherwise) the Quest catalog uses (EntityCatalog.cs). */
    static final String[] LAYERS = {
        // vehicles
        "boat/oak", "boat/spruce", "boat/birch", "boat/jungle", "boat/acacia", "boat/cherry", "boat/dark_oak", "boat/pale_oak",
        "boat/mangrove", "boat/bamboo", "boat/poplar",
        "chest_boat/oak", "chest_boat/spruce", "chest_boat/birch", "chest_boat/jungle", "chest_boat/acacia", "chest_boat/cherry",
        "chest_boat/dark_oak", "chest_boat/pale_oak", "chest_boat/mangrove", "chest_boat/bamboo", "chest_boat/poplar",
        "minecart", "chest_minecart", "furnace_minecart", "hopper_minecart", "tnt_minecart", "spawner_minecart", "command_block_minecart",
        // farm and other animals
        "cow", "cold_cow", "warm_cow", "cow_baby", "cold_cow_baby", "warm_cow_baby", "mooshroom", "mooshroom_baby",
        "pig", "cold_pig", "pig_baby", "chicken", "cold_chicken", "chicken_baby", "sheep", "sheep_baby", "sheep#wool", "sheep_baby#wool",
        "horse", "horse_baby", "donkey", "donkey_baby", "mule", "mule_baby", "skeleton_horse", "skeleton_horse_baby", "zombie_horse",
        "zombie_horse_baby", "camel", "camel_baby", "llama", "llama_baby", "trader_llama", "trader_llama_baby",
        "wolf", "wolf_baby", "cat", "cat_baby", "ocelot", "ocelot_baby", "fox", "fox_baby", "rabbit", "rabbit_baby", "goat", "goat_baby",
        "polar_bear", "polar_bear_baby", "panda", "panda_baby", "bee", "bee_baby", "parrot", "bat", "frog", "tadpole",
        "armadillo", "armadillo_baby", "sniffer", "sniffer_baby", "strider", "strider_baby", "hoglin", "hoglin_baby", "zoglin", "zoglin_baby",
        "allay", "happy_ghast", "happy_ghast_baby",
        // water
        "axolotl", "axolotl_baby", "cod", "salmon", "salmon_small", "salmon_large", "tropical_fish_small", "tropical_fish_large",
        "pufferfish_small", "pufferfish_medium", "pufferfish_big", "squid", "squid_baby", "glow_squid", "glow_squid_baby",
        "dolphin", "dolphin_baby", "turtle", "turtle_baby", "guardian", "elder_guardian", "nautilus", "nautilus_baby", "zombie_nautilus",
        // humanoids
        "zombie", "zombie_baby", "husk", "husk_baby", "drowned", "drowned_baby", "drowned#outer", "drowned_baby#outer",
        "zombie_villager", "zombie_villager_baby", "skeleton", "stray", "stray#outer", "wither_skeleton", "bogged", "bogged#outer",
        "parched", "villager", "villager_baby", "wandering_trader", "witch", "pillager", "vindicator", "evoker", "illusioner",
        "piglin", "piglin_baby", "piglin_brute", "zombified_piglin", "zombified_piglin_baby", "enderman", "player", "player_slim",
        "iron_golem", "snow_golem", "copper_golem", "creaking", "vex", "blaze", "breeze", "warden", "ravager",
        // others
        "creeper", "spider", "cave_spider", "silverfish", "endermite", "slime", "slime#outer", "magma_cube", "phantom", "ghast",
        "shulker", "armor_stand", "armor_stand_small", "arrow", "trident", "end_crystal", "leash_knot",
        // armour worn by players and humanoid mobs (equipment/humanoid and humanoid_leggings textures)
        "player#helmet", "player#chestplate", "player#leggings", "player#boots",
    };

    static Object f(Object o, String name) throws Exception {
        for (Class<?> c = o.getClass(); c != null; c = c.getSuperclass()) {
            try { Field fl = c.getDeclaredField(name); fl.setAccessible(true); return fl.get(o); } catch (NoSuchFieldException e) { /* up */ }
        }
        throw new NoSuchFieldException(name + " in " + o.getClass());
    }

    static float fv(Object o, String name) throws Exception { return (Float) f(o, name); }

    static String num(double v) {
        double r = Math.round(v * 10000) / 10000.0;
        if (r == Math.rint(r)) return Long.toString((long) r);
        return Double.toString(r);
    }

    static final List<String> warnings = new ArrayList<>();

    static void part(StringBuilder sb, String name, Object pd, String layer) throws Exception {
        Object pose = f(pd, "partPose");
        sb.append("[\"").append(name).append("\",[");
        float[] p = { fv(pose, "x"), fv(pose, "y"), fv(pose, "z"), fv(pose, "xRot"), fv(pose, "yRot"), fv(pose, "zRot"),
            fv(pose, "xScale"), fv(pose, "yScale"), fv(pose, "zScale") };
        int n = p[6] != 1 || p[7] != 1 || p[8] != 1 ? 9 : 6;
        for (int i = 0; i < n; i++) sb.append(i > 0 ? "," : "").append(num(p[i]));
        sb.append("],[");
        boolean first = true;
        for (Object c : (List<?>) f(pd, "cubes")) {
            Object o = f(c, "origin"), d = f(c, "dimensions"), g = f(c, "grow"), uv = f(c, "texCoord"), ts = f(c, "texScale");
            float gx = fv(g, "growX"), gy = fv(g, "growY"), gz = fv(g, "growZ");
            boolean mirror = (Boolean) f(c, "mirror");
            int faces = 0;
            for (Object dir : (Set<?>) f(c, "visibleFaces")) {
                faces |= switch (dir.toString()) { case "down" -> 1; case "up" -> 2; case "north" -> 4; case "south" -> 8; case "west" -> 16; case "east" -> 32; default -> 0; };
            }
            float su = (Float) uv.getClass().getMethod("u").invoke(ts), sv = (Float) uv.getClass().getMethod("v").invoke(ts);
            if (su != 1 || sv != 1) warnings.add(layer + "/" + name + ": texScale " + su + "," + sv);
            List<String> v = new ArrayList<>(List.of(num((Float) uv.getClass().getMethod("u").invoke(uv)), num((Float) uv.getClass().getMethod("v").invoke(uv)),
                num((Float) o.getClass().getMethod("x").invoke(o)), num((Float) o.getClass().getMethod("y").invoke(o)), num((Float) o.getClass().getMethod("z").invoke(o)),
                num((Float) d.getClass().getMethod("x").invoke(d)), num((Float) d.getClass().getMethod("y").invoke(d)), num((Float) d.getClass().getMethod("z").invoke(d))));
            // Optional tail: grow (one value, or [x,y,z] when uneven), mirror 0/1, face mask (63 = all); trailing defaults dropped.
            String grow = gx == gy && gy == gz ? num(gx) : "[" + num(gx) + "," + num(gy) + "," + num(gz) + "]";
            List<String> tail = new ArrayList<>(List.of(grow, mirror ? "1" : "0", Integer.toString(faces)));
            if (faces == 63) { tail.remove(2); if (!mirror) { tail.remove(1); if (grow.equals("0")) tail.remove(0); } }
            v.addAll(tail);
            sb.append(first ? "" : ",").append('[').append(String.join(",", v)).append(']');
            first = false;
        }
        sb.append("],[");
        first = true;
        @SuppressWarnings("unchecked") Map<String, Object> ch = (Map<String, Object>) f(pd, "children");
        for (var e : new TreeMap<>(ch).entrySet()) {   // sorted: stable output
            if (!first) sb.append(',');
            part(sb, e.getKey(), e.getValue(), layer);
            first = false;
        }
        sb.append("]]");
    }

    /** DyeColor name -> getTextureDiffuseColor() (sheep wool, tropical fish); "{}" if the enum can't be read. */
    static String dyes(ClassLoader cl) {
        try {
            Class<?> dye = Class.forName("net.minecraft.world.item.DyeColor", true, cl);
            StringBuilder sb = new StringBuilder("{");
            for (Object d : (Object[]) dye.getMethod("values").invoke(null)) {
                if (sb.length() > 1) sb.append(',');
                sb.append('"').append(dye.getMethod("getName").invoke(d)).append("\":").append((int) dye.getMethod("getTextureDiffuseColor").invoke(d) & 0xFFFFFF);
            }
            return sb.append('}').toString();
        } catch (ReflectiveOperationException | LinkageError e) {
            warnings.add("no dye colours: " + e);
            return "{}";
        }
    }

    public static void main(String[] args) throws Exception {
        if (args.length < 3) { System.err.println("usage: DumpEntityModels <minecraft dir> <version> <out.json>"); System.exit(2); }
        Path mc = Path.of(args[0]);
        String ver = args[1];
        // The client's log4j2.xml would write logs/latest.log into the working directory: use log4j's console default.
        System.setProperty("log4j2.configurationFile", "tabletop-no-such-log4j2.xml");
        System.setProperty("log4j2.statusLoggerLevel", "OFF");
        List<URL> urls = new ArrayList<>();
        urls.add(mc.resolve("versions").resolve(ver).resolve(ver + ".jar").toUri().toURL());
        String json = Files.readString(mc.resolve("versions").resolve(ver).resolve(ver + ".json"));
        Matcher m = Pattern.compile("\"artifact\"\\s*:\\s*\\{[^}]*?\"path\"\\s*:\\s*\"([^\"]+)\"").matcher(json);
        while (m.find()) {
            File lib = mc.resolve("libraries").resolve(m.group(1)).toFile();
            if (lib.exists() && !lib.getName().contains("natives")) urls.add(lib.toURI().toURL());
        }
        // Not closed: log4j's shutdown hook still loads classes from it.
        URLClassLoader cl = new URLClassLoader(urls.toArray(new URL[0]), DumpEntityModels.class.getClassLoader());
        {
            Class<?> ld = Class.forName("net.minecraft.client.model.geom.LayerDefinitions", true, cl);
            Map<?, ?> roots = (Map<?, ?>) ld.getMethod("createRoots").invoke(null);
            Map<String, Object> byName = new HashMap<>();
            for (var e : roots.entrySet()) {
                String k = e.getKey().toString().replace("minecraft:", "");
                byName.put(k.endsWith("#main") ? k.substring(0, k.length() - 5) : k, e.getValue());
            }
            StringBuilder data = new StringBuilder("{");
            Map<String, String> bodies = new LinkedHashMap<>();   // body json -> first name (identical models become aliases)
            int models = 0, aliases = 0;
            for (String name : LAYERS) {
                Object def = byName.get(name);
                if (def == null) { warnings.add("no layer " + name); continue; }
                Object mat = f(def, "material");
                StringBuilder body = new StringBuilder();
                body.append("{\"t\":[").append(f(mat, "xTexSize")).append(',').append(f(mat, "yTexSize")).append("],\"r\":");
                part(body, "", f(f(def, "mesh"), "root"), name);
                body.append('}');
                if (data.length() > 1) data.append(",\n");
                String same = bodies.get(body.toString());
                if (same != null) { data.append('"').append(name).append("\":\"").append(same).append('"'); aliases++; }
                else { bodies.put(body.toString(), name); data.append('"').append(name).append("\":").append(body); models++; }
            }
            data.append('}');
            // Format (EntityModels.LoadJson): {"format": 1, "minecraft": version, "dyes": {name: rgb},
            // "models": {name: {"t": [texW, texH], "r": part} | "alias"}};
            // part = [name, [x, y, z, xRot, yRot, zRot(, xScale, yScale, zScale)], [cube...], [child part...]],
            // cube = [u, v, x, y, z, w, h, d(, grow or [gx, gy, gz](, mirror 0/1(, face mask: 1 down 2 up 4 north 8 south 16 west 32 east)))].
            String out = "{\"generator\":\"tabletop-craft-ar tools/entity_models/DumpEntityModels.java\",\"format\":1,\"minecraft\":\"" + ver + "\",\n"
                + "\"dyes\":" + dyes(cl) + ",\n\"models\":" + data + "}\n";
            Files.writeString(Path.of(args[2]), out);
            System.out.println("models " + models + ", aliases " + aliases + ", " + out.length() + " bytes -> " + args[2]);
            for (String w : warnings) System.out.println("WARN " + w);
        }
    }
}
