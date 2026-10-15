package dev.tabletopcraft.ar;

import com.google.gson.JsonObject;
import net.kyori.adventure.text.Component;
import net.kyori.adventure.text.serializer.plain.PlainTextComponentSerializer;
import org.bukkit.DyeColor;
import org.bukkit.World;
import org.bukkit.entity.*;
import org.bukkit.inventory.EntityEquipment;
import org.bukkit.inventory.EquipmentSlot;
import org.bukkit.inventory.ItemStack;
import org.bukkit.util.BoundingBox;

import java.util.ArrayList;
import java.util.Collection;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;

/**
 * Reads entities in a region into {@link EntityState}s (main thread, Paper API only). Players are left out (they go
 * in player_positions), and so are the giant's own entities (scoreboard tag {@link GiantAvatar#TAG}) and entities
 * nobody sees (markers, interactions, text displays, effect clouds). Equipment and item stacks are copies in the API,
 * so they're re-read only every {@link #REFRESH} samples per entity (spread by id) and cached in between.
 */
public final class EntitySampler {
    static final int REFRESH = 5;
    static final Set<EntityType> HIDDEN = Set.of(EntityType.MARKER, EntityType.INTERACTION, EntityType.AREA_EFFECT_CLOUD);
    private static final EquipmentSlot[] SLOTS = { EquipmentSlot.HAND, EquipmentSlot.OFF_HAND, EquipmentSlot.HEAD, EquipmentSlot.CHEST,
        EquipmentSlot.LEGS, EquipmentSlot.FEET, EquipmentSlot.BODY, EquipmentSlot.SADDLE };
    private static final String[] SLOT_NAMES = { "main_hand", "off_hand", "head", "chest", "legs", "feet", "body", "saddle" };

    private record Cached(Map<String, String> equipment, String item, int count, String text) {
        Cached(Map<String, String> equipment, String item, int count) { this(equipment, item, count, null); }
    }
    private final Map<Integer, Cached> cache = new HashMap<>();
    private long sampleNo;

    /** Entities in the region box grown by margin, nearest to its centre first, at most max. */
    public List<EntityState> sample(World w, RegionSpec r, int margin, int max) {
        sampleNo++;
        BoundingBox box = new BoundingBox(r.ox() - margin, r.oy() - margin, r.oz() - margin,
            r.ox() + r.sx() + margin, r.oy() + r.sy() + margin, r.oz() + r.sz() + margin);
        Collection<Entity> found = w.getNearbyEntities(box, EntitySampler::visible);
        double cx = r.ox() + r.sx() / 2.0, cy = r.oy() + r.sy() / 2.0, cz = r.oz() + r.sz() / 2.0;
        List<EntityState> out = new ArrayList<>(found.size());
        Set<Integer> seen = new HashSet<>();
        for (Entity e : found) {
            try {
                EntityState s = state(e);
                s.distSq = (s.x - cx) * (s.x - cx) + (s.y - cy) * (s.y - cy) + (s.z - cz) * (s.z - cz);
                out.add(s);
                seen.add(s.id);
            } catch (RuntimeException ex) { /* an entity removed mid-read; it's gone next sample */ }
        }
        if (out.size() > max) {
            out.sort(Comparator.comparingDouble(s -> s.distSq));
            out = new ArrayList<>(out.subList(0, max));
        }
        cache.keySet().retainAll(seen);
        return out;
    }

    static boolean visible(Entity e) {
        return !(e instanceof Player) && !HIDDEN.contains(e.getType()) && !e.getScoreboardTags().contains(GiantAvatar.TAG);
    }

    private EntityState state(Entity e) {
        var l = e.getLocation();
        EntityState s = new EntityState(e.getEntityId(), typeKey(e.getType()), l.getX(), l.getY(), l.getZ(), l.getYaw(), l.getPitch());
        s.uuid = e.getUniqueId().toString();
        s.onGround = e.isOnGround();
        Entity v = e.getVehicle();
        if (v != null) s.vehicle = v.getEntityId();
        List<Entity> ps = e.getPassengers();
        if (!ps.isEmpty()) {
            s.passengers = new int[ps.size()];
            for (int i = 0; i < ps.size(); i++) s.passengers[i] = ps.get(i).getEntityId();
        }
        s.width = e.getWidth(); s.height = e.getHeight();
        Component cn = e.customName();
        if (cn != null) s.name = PlainTextComponentSerializer.plainText().serialize(cn);
        s.pose = pose(e);

        // Item stacks and equipment are API copies: refresh them every REFRESH samples per entity.
        Cached c = cache.get(s.id);
        boolean refresh = c == null || (sampleNo + s.id) % REFRESH == 0;
        if (e instanceof LivingEntity le) {
            s.bodyYaw = le.getBodyYaw();
            s.invisible = le.isInvisible();
            if (refresh) c = new Cached(equipment(le.getEquipment()), null, 0);
            s.equipment = c.equipment();
        } else if (e instanceof Item it) {
            if (refresh) { ItemStack st = it.getItemStack(); c = new Cached(null, itemKey(st), st.getAmount()); }
            s.item = c.item(); s.count = c.count();
        } else if (e instanceof ItemFrame f) {
            if (refresh) c = new Cached(null, itemKey(f.getItem()), 1);
            s.item = c.item();
            s.rotation = f.getRotation().ordinal();
            s.invisible = !f.isVisible();
        } else if (e instanceof ItemDisplay d) {
            if (refresh) c = new Cached(null, itemKey(d.getItemStack()), 1);
            s.item = c.item();
        } else if (e instanceof OminousItemSpawner os) {
            if (refresh) c = new Cached(null, itemKey(os.getItem()), 1);
            s.item = c.item();
        } else if (e instanceof TextDisplay td) {
            if (refresh) c = new Cached(null, null, 0, PlainTextComponentSerializer.plainText().serialize(td.text()));
        } else if (refresh) c = new Cached(null, null, 0);
        cache.put(s.id, c);
        if (e instanceof Hanging h) s.facing = h.getFacing().name().toLowerCase(Locale.ROOT);
        if (e instanceof FallingBlock fb) s.block = fb.getBlockData().getAsString();
        else if (e instanceof BlockDisplay bd) s.block = bd.getBlock().getAsString();
        if (e instanceof Display d) s.display = display(d, c.text());
        living(e, s);
        variants(e, s);
        return s;
    }

    /** [living] Lead, eye height, name visibility, chests, orb value, bobber owner, carried block, shulker lid. */
    static void living(Entity e, EntityState s) {
        if (e instanceof io.papermc.paper.entity.Leashable l && l.isLeashed()) {
            try { s.leash = l.getLeashHolder().getEntityId(); } catch (IllegalStateException ex) { /* let go mid-read */ }
        }
        if (e instanceof LivingEntity le) s.eye = le.getEyeHeight();
        s.nameVisible = e.isCustomNameVisible() && e.customName() != null;
        if (e instanceof ChestedHorse ch) s.chested = ch.isCarryingChest();
        if (e instanceof ExperienceOrb xo) s.xp = Math.max(1, xo.getExperience());
        if (e instanceof FishHook fh && fh.getShooter() instanceof Entity owner) s.owner = owner.getEntityId();
        if (e instanceof Enderman en && en.getCarriedBlock() != null) s.carried = en.getCarriedBlock().getAsString();
        if (e instanceof Shulker sk) {
            s.peek = Math.max(0, Math.min(1, sk.getPeek()));
            var face = sk.getAttachedFace();
            if (face != null && face != org.bukkit.block.BlockFace.DOWN) s.attach = face.name().toLowerCase(Locale.ROOT);
        }
        if (e instanceof Snowman sn && sn.isDerp()) s.sheared = true;   // a sheared snow golem has no pumpkin
    }

    /**
     * A display entity's render state (vanilla Display.RenderState): the transformation as translation, left rotation,
     * scale and right rotation, the billboard, the light override, the item display transform and, for text displays,
     * the plain text and its look (text read with the cached stacks, every REFRESH samples).
     */
    static EntityState.DisplayState display(Display d, String text) {
        var ds = new EntityState.DisplayState();
        var t = d.getTransformation();
        var tr = t.getTranslation(); var l = t.getLeftRotation(); var sc = t.getScale(); var r = t.getRightRotation();
        ds.translation = EntityState.DisplayState.round(new float[] { tr.x, tr.y, tr.z }, 3);
        ds.leftRotation = EntityState.DisplayState.round(new float[] { l.x, l.y, l.z, l.w }, 4);
        ds.scale = EntityState.DisplayState.round(new float[] { sc.x, sc.y, sc.z }, 3);
        ds.rightRotation = EntityState.DisplayState.round(new float[] { r.x, r.y, r.z, r.w }, 4);
        ds.billboard = d.getBillboard().name().toLowerCase(Locale.ROOT);
        var b = d.getBrightness();
        if (b != null) ds.brightness = new int[] { b.getBlockLight(), b.getSkyLight() };
        if (d instanceof ItemDisplay id) ds.itemTransform = id.getItemDisplayTransform().name().toLowerCase(Locale.ROOT);
        if (d instanceof TextDisplay td) {
            ds.text = text == null ? "" : text;
            ds.lineWidth = td.getLineWidth();
            var bg = td.getBackgroundColor();
            ds.background = bg == null ? 0x40000000 : bg.asARGB();
            ds.textOpacity = td.getTextOpacity() & 0xFF;
            ds.shadow = td.isShadowed(); ds.seeThrough = td.isSeeThrough(); ds.defaultBackground = td.isDefaultBackground();
            ds.align = td.getAlignment().name().toLowerCase(Locale.ROOT);
        }
        return ds;
    }

    /** Baby flag, variants, colours and similar per-type looks (PROTOCOL.md 4.21 lists the fields). */
    static void variants(Entity e, EntityState s) {
        if (e instanceof Ageable a) s.baby = !a.isAdult();
        if (e instanceof ArmorStand as) {
            s.small = as.isSmall(); s.arms = as.hasArms(); s.basePlate = as.hasBasePlate();
            s.standPose = standPose(as.getHeadPose(), as.getBodyPose(), as.getLeftArmPose(), as.getRightArmPose(), as.getLeftLegPose(), as.getRightLegPose());
        }
        if (e instanceof Tameable t && t.isTamed()) s.tamed = true;
        if (e instanceof Sheep sh) { s.color = dye(sh.getColor()); s.sheared = sh.isSheared(); }
        else if (e instanceof Shulker sk) s.color = dye(sk.getColor());
        else if (e instanceof Cow cow) s.variant = key(cow.getVariant());
        else if (e instanceof MushroomCow m) s.variant = key(m.getVariant());
        else if (e instanceof Pig p) s.variant = key(p.getVariant());
        else if (e instanceof Chicken ch) s.variant = key(ch.getVariant());
        else if (e instanceof Wolf w) { s.variant = key(w.getVariant()); s.angry = w.isAngry(); if (w.isTamed()) s.color = dye(w.getCollarColor()); }
        else if (e instanceof Cat cat) { s.variant = key(cat.getCatType()); if (cat.isTamed()) s.color = dye(cat.getCollarColor()); }
        else if (e instanceof Frog f) s.variant = key(f.getVariant());
        else if (e instanceof Horse h) { s.variant = key(h.getColor()); s.markings = h.getStyle() == Horse.Style.NONE ? null : key(h.getStyle()); }
        else if (e instanceof Llama ll) s.variant = key(ll.getColor());
        else if (e instanceof Fox fx) s.variant = key(fx.getFoxType());
        else if (e instanceof Rabbit rb) s.variant = key(rb.getRabbitType());
        else if (e instanceof Parrot pa) s.variant = key(pa.getVariant());
        else if (e instanceof Axolotl ax) s.variant = key(ax.getVariant());
        else if (e instanceof Panda pd) s.variant = key(pd.getMainGene());
        else if (e instanceof Villager vi) { s.variant = key(vi.getVillagerType()); s.profession = profession(key(vi.getProfession())); }
        else if (e instanceof ZombieVillager zv) { s.variant = key(zv.getVillagerType()); s.profession = profession(key(zv.getVillagerProfession())); }
        else if (e instanceof Salmon sa) s.variant = key(sa.getVariant());
        else if (e instanceof PufferFish pf) s.variant = pf.getPuffState() <= 0 ? "small" : pf.getPuffState() == 1 ? "medium" : "big";
        else if (e instanceof TropicalFish tf) { s.variant = key(tf.getPattern()); s.color = dye(tf.getBodyColor()); s.markings = dye(tf.getPatternColor()); }
        else if (e instanceof Painting pt) s.variant = key(pt.getArt());
        else if (e instanceof Bee b) s.angry = b.getAnger() > 0;
    }

    /** player_positions additions (PROTOCOL.md 4.6): entity id, vehicle, pose and equipment. */
    public static void playerExtras(JsonObject o, Player p) {
        o.addProperty("id", p.getEntityId());
        Entity v = p.getVehicle();
        if (v != null) o.addProperty("vehicle", v.getEntityId());
        String pose = v != null ? "riding" : pose(p);
        if (pose != null) o.addProperty("pose", pose);
        Map<String, String> eq = equipment(p.getEquipment());
        if (!eq.isEmpty()) {
            JsonObject e = new JsonObject();
            eq.forEach(e::addProperty);
            o.add("equipment", e);
        }
    }

    /** Minecraft pose as the protocol names it, or null for standing. */
    static String pose(Entity e) {
        if (e instanceof Sittable st && st.isSitting()) return "sitting";
        if (e instanceof LivingEntity le && le.isSleeping()) return "sleeping";
        return poseName(e.getPose().name());
    }

    /** Bukkit Pose name -> protocol pose (null = standing; FALL_FLYING is "gliding"). */
    public static String poseName(String bukkit) {
        return switch (bukkit) {
            case "STANDING" -> null;
            case "FALL_FLYING" -> "gliding";
            default -> bukkit.toLowerCase(Locale.ROOT);
        };
    }

    static Map<String, String> equipment(EntityEquipment eq) {
        if (eq == null) return Map.of();
        Map<String, String> m = null;
        for (int i = 0; i < SLOTS.length; i++) {
            ItemStack st;
            try { st = eq.getItem(SLOTS[i]); } catch (RuntimeException ex) { continue; }   // slot not supported by this entity
            String k = itemKey(st);
            if (k == null) continue;
            if (m == null) m = new LinkedHashMap<>();
            m.put(SLOT_NAMES[i], k);
        }
        return m == null ? Map.of() : m;
    }

    static String itemKey(ItemStack st) {
        if (st == null || st.isEmpty()) return null;
        return st.getType().getKey().toString();
    }

    static String typeKey(EntityType t) {
        try { return t.getKey().toString(); } catch (RuntimeException e) { return "minecraft:unknown"; }
    }

    private static String dye(DyeColor c) { return c == null ? null : c.name().toLowerCase(Locale.ROOT); }

    /**
     * An armour stand's part angles (Bukkit EulerAngles, radians) as {@link EntityState#standPose}: degrees rounded to
     * 0.1, in head, body, left arm, right arm, left leg, right leg order; null when all are vanilla's defaults.
     */
    static float[] standPose(org.bukkit.util.EulerAngle... parts) {
        float[] p = new float[18];
        boolean custom = false;
        for (int i = 0; i < 6; i++) {
            double[] r = { parts[i].getX(), parts[i].getY(), parts[i].getZ() };
            for (int a = 0; a < 3; a++) {
                float deg = Math.round(Math.toDegrees(r[a]) * 10) / 10f;
                p[i * 3 + a] = deg;
                if (Math.abs(deg - EntityState.DEFAULT_STAND_POSE[i * 3 + a]) > 0.05f) custom = true;
            }
        }
        return custom ? p : null;
    }

    private static String profession(String p) { return p == null || p.equals("none") ? null : p; }

    /**
     * Variant name: a registry key's path for vanilla ids ("temperate"), "namespace:path" for others, an enum's
     * lower-case name ("dark_brown").
     */
    static String key(Object v) {
        if (v == null) return null;
        if (v instanceof net.kyori.adventure.key.Keyed k) return strip(k.key().asString());
        if (v instanceof org.bukkit.Keyed k) return strip(k.getKey().toString());
        if (v instanceof Enum<?> en) return en.name().toLowerCase(Locale.ROOT);
        return v.toString().toLowerCase(Locale.ROOT);
    }

    static String strip(String k) { return k.startsWith("minecraft:") ? k.substring(10) : k; }
}
