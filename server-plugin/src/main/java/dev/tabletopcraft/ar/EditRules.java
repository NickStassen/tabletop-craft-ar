package dev.tabletopcraft.ar;

import java.util.Locale;
import java.util.Set;

/**
 * Block-edit rules that work on state strings (pure, unit-tested; PROTOCOL.md 4.20). The Quest app applies the same
 * rules before it sends (World/BlockPlacement.cs), so its optimistic guess matches what the server does:
 * <ul>
 *   <li>which cells a placed block may replace (vanilla 26.3 {@code canBeReplaced}: Paper's {@code isReplaceable} flag,
 *       but a snow cover only as a single layer, sculk vein too, and never by its own block);</li>
 *   <li>the second half of two-block structures (doors and double plants one cell up, beds one cell along facing);</li>
 *   <li>whether a cell holds a water source (decides {@code waterlogged} for a block placed there);</li>
 *   <li>readable names for refusals ("oak stairs").</li>
 * </ul>
 */
public final class EditRules {
    private EditRules() {}

    /** "minecraft:oak_stairs[facing=east]" -> "oak stairs" (another namespace keeps its prefix: "mymod:thing" -> "mymod:thing"). */
    public static String label(String stateOrId) {
        String id = BlockEdit.blockId(stateOrId);
        String path = id.startsWith("minecraft:") ? id.substring("minecraft:".length()) : id;
        return path.replace('_', ' ');
    }

    /** "a cow", "an armadillo": an entity type key for refusals. */
    public static String article(String label) {
        return (label.isEmpty() || "aeiou".indexOf(label.charAt(0)) < 0 ? "a " : "an ") + label;
    }

    /** Value of one block-state property, or null. */
    public static String prop(String state, String key) {
        int open = state.indexOf('[');
        if (open < 0) return null;
        for (String kv : state.substring(open + 1, state.endsWith("]") ? state.length() - 1 : state.length()).split(",")) {
            int eq = kv.indexOf('=');
            if (eq > 0 && kv.substring(0, eq).trim().equals(key)) return kv.substring(eq + 1).trim();
        }
        return null;
    }

    /** The state with one existing property set (unchanged without it). */
    public static String withProp(String state, String key, String value) {
        int open = state.indexOf('[');
        if (open < 0) return state;
        String[] kvs = state.substring(open + 1, state.endsWith("]") ? state.length() - 1 : state.length()).split(",");
        boolean found = false;
        for (int i = 0; i < kvs.length; i++) {
            int eq = kvs[i].indexOf('=');
            if (eq > 0 && kvs[i].substring(0, eq).trim().equals(key)) { kvs[i] = key + "=" + value; found = true; }
        }
        return found ? state.substring(0, open) + "[" + String.join(",", kvs) + "]" : state;
    }

    /** Multiface blocks vanilla lets any other block replace although they aren't flagged replaceable. */
    static final Set<String> ALSO_REPLACEABLE = Set.of("minecraft:sculk_vein");

    /**
     * Vanilla's {@code canBeReplaced} for placing {@code next} into a cell holding {@code old}.
     *
     * @param flagged Paper's {@code BlockData#isReplaceable()} for {@code old} (the block's replaceable property)
     */
    public static boolean replaceable(String old, boolean flagged, String next) {
        String oldId = BlockEdit.blockId(old);
        if (next != null && oldId.equals(BlockEdit.blockId(next))) return false;   // a block's own item stacks or merges instead
        if (oldId.equals("minecraft:snow")) return "1".equals(prop(old, "layers"));
        return flagged || ALSO_REPLACEABLE.contains(oldId);
    }

    /** The cell holds a water source: vanilla's {@code getFluidState(pos).is(Fluids.WATER)}. */
    public static boolean holdsWaterSource(String state) {
        String id = BlockEdit.blockId(state);
        if (id.equals("minecraft:water")) return "0".equals(prop(state, "level"));
        if ("true".equals(prop(state, "waterlogged"))) return true;
        return switch (id) {
            case "minecraft:kelp", "minecraft:kelp_plant", "minecraft:seagrass", "minecraft:tall_seagrass", "minecraft:bubble_column" -> true;
            default -> false;
        };
    }

    /** The other half of a two-block structure: where it is (offset) and its state. */
    public record Partner(int dx, int dy, int dz, String state) {}

    /**
     * The other half of {@code state}: doors and double plants ({@code half=lower/upper}; not the pitcher crop, which
     * only grows a top half) one cell up or down, beds ({@code part=foot/head}) one cell along their facing. Null for
     * anything else (stairs and trapdoors use half=top/bottom and are single blocks).
     */
    public static Partner partner(String state) {
        String id = BlockEdit.blockId(state);
        String half = prop(state, "half"), part = prop(state, "part");
        if (("lower".equals(half) || "upper".equals(half)) && !id.equals("minecraft:pitcher_crop")) {
            boolean lower = half.equals("lower");
            return new Partner(0, lower ? 1 : -1, 0, withProp(state, "half", lower ? "upper" : "lower"));
        }
        if (("foot".equals(part) || "head".equals(part)) && id.endsWith("_bed")) {
            String f = prop(state, "facing");
            int[] d = step(f);
            if (d == null) return null;
            int k = part.equals("foot") ? 1 : -1;
            return new Partner(d[0] * k, 0, d[2] * k, withProp(state, "part", part.equals("foot") ? "head" : "foot"));
        }
        return null;
    }

    /** The part a two-block structure is placed by: the lower half, the foot. Other states unchanged. */
    public static String placedHalf(String state) {
        if (partner(state) == null) return state;
        if ("upper".equals(prop(state, "half"))) return withProp(state, "half", "lower");
        if ("head".equals(prop(state, "part"))) return withProp(state, "part", "foot");
        return state;
    }

    static int[] step(String dir) {
        if (dir == null) return null;
        return switch (dir.toLowerCase(Locale.ROOT)) {
            case "north" -> new int[] { 0, 0, -1 };
            case "south" -> new int[] { 0, 0, 1 };
            case "west" -> new int[] { -1, 0, 0 };
            case "east" -> new int[] { 1, 0, 0 };
            case "up" -> new int[] { 0, 1, 0 };
            case "down" -> new int[] { 0, -1, 0 };
            default -> null;
        };
    }
}
