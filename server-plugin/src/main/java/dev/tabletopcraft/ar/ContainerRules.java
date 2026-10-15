package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * [operator] What the headset's container view may do, on block and item ids so it is pure and unit-tested
 * (PROTOCOL.md "Containers"). OperatorService reads and writes the real inventories with the Paper API.
 * <ul>
 *   <li>which blocks open the view ({@link InteractRules#container}), their default titles (vanilla's en_us names);</li>
 *   <li>which items a slot takes ({@link #placeProblem}): furnace output slots take nothing, fuel slots only fuel, a
 *       brewing stand's bottle slots only bottles and potions, its fuel slot only blaze powder, and a shulker box no
 *       shulker box (as vanilla's {@code canPlaceItem});</li>
 *   <li>sorting ({@link #sort}): stacks of the same item (and the same components) merged up to their stack size,
 *       then ordered by item id, the biggest stacks first; only for plain storage, where slots mean nothing.</li>
 * </ul>
 */
public final class ContainerRules {
    private ContainerRules() {}

    static final Set<String> FURNACES = Set.of("furnace", "smoker", "blast_furnace");
    static final Set<String> BOTTLES = Set.of("potion", "splash_potion", "lingering_potion", "glass_bottle");

    static String path(String id) {
        String s = BlockEdit.blockId(id);
        return s.startsWith("minecraft:") ? s.substring(10) : s;
    }

    /** A block the container view opens (the same families as the left trigger's hook). */
    public static boolean container(String blockId) { return InteractRules.container(path(blockId)); }

    /** Plain storage, where slot order means nothing: chests, barrels, shulker boxes, hoppers, dispensers, droppers. */
    public static boolean sortable(String blockId) {
        String p = path(blockId);
        return p.equals("chest") || p.equals("trapped_chest") || p.equals("barrel") || p.endsWith("shulker_box") || p.endsWith("copper_chest")
            || p.equals("hopper") || p.equals("dispenser") || p.equals("dropper");
    }

    /** The title vanilla shows (en_us) for a container block without a custom name. */
    public static String title(String blockId, boolean doubleChest) {
        String p = path(blockId);
        if (doubleChest) return "Large Chest";
        if (p.endsWith("shulker_box")) return "Shulker Box";
        if (p.equals("trapped_chest")) return "Chest";
        if (p.equals("hopper")) return "Item Hopper";
        return capitalise(EditRules.label(blockId));
    }

    static String capitalise(String label) {
        StringBuilder b = new StringBuilder(label.length());
        boolean up = true;
        for (char c : label.toCharArray()) {
            b.append(up ? Character.toUpperCase(c) : c);
            up = c == ' ';
        }
        return b.toString();
    }

    /**
     * Why {@code itemId} can't go into {@code slot} of a {@code blockId} container ({@code fuel}: the item burns in a
     * furnace, Material#isFuel), or null when it can.
     */
    public static String placeProblem(String blockId, int slot, String itemId, boolean fuel) {
        String p = path(blockId), item = path(itemId), label = EditRules.label(itemId);
        if (p.endsWith("shulker_box") && item.endsWith("shulker_box")) return "a shulker box can't go inside a shulker box";
        if (FURNACES.contains(p)) {
            if (slot == 2) return "that's the " + EditRules.label(blockId) + "'s output slot";
            if (slot == 1 && !fuel) return label + " isn't fuel";
        }
        if (p.equals("brewing_stand")) {
            if (slot <= 2 && !BOTTLES.contains(item)) return "the brewing stand's bottle slots take potions and glass bottles";
            if (slot == 4 && !item.equals("blaze_powder")) return "the brewing stand's fuel slot takes blaze powder";
        }
        return null;
    }

    /** A stack in a container: item id, a key for its components (stacks merge only when both match), count and stack size. */
    public record Stack<T>(String item, String meta, int count, int max, T handle) {
        Stack<T> withCount(int c) { return new Stack<>(item, meta, c, max, handle); }
    }

    /**
     * The container's stacks sorted: equal items merged up to their stack size, ordered by item id, then by components,
     * biggest stacks first. The result has at most as many stacks as the input (never more than the slots).
     */
    public static <T> List<Stack<T>> sort(List<Stack<T>> stacks) {
        Map<String, List<Stack<T>>> groups = new LinkedHashMap<>();
        for (Stack<T> s : stacks) if (s != null && s.count() > 0) groups.computeIfAbsent(s.item() + "\u0000" + s.meta(), k -> new ArrayList<>()).add(s);
        List<Stack<T>> out = new ArrayList<>();
        for (List<Stack<T>> g : groups.values()) {
            int total = 0;
            for (Stack<T> s : g) total += s.count();
            Stack<T> first = g.get(0);
            int max = Math.max(1, first.max());
            while (total > 0) { int n = Math.min(max, total); out.add(first.withCount(n)); total -= n; }
        }
        out.sort(Comparator.<Stack<T>, String>comparing(Stack::item).thenComparing(Stack::meta).thenComparing(Stack::count, Comparator.reverseOrder()));
        return out;
    }
}
