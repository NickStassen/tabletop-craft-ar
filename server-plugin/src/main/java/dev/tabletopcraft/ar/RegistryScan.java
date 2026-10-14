package dev.tabletopcraft.ar;

import org.bukkit.Bukkit;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Registry;
import org.bukkit.Tag;
import org.bukkit.World;
import org.bukkit.block.BlockType;
import org.bukkit.inventory.ItemType;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Reads the block registry candidates from the running server (main thread, once per start/reload): every item with a
 * block form in item-registry order (vanilla's, which groups block families), then every other block whose item maps
 * to one of those (wall torches, wall signs, wall heads, wall banners, attached stems...). BlockRegistry#build filters
 * and categorises them.
 */
final class RegistryScan {
    private RegistryScan() {}

    @SuppressWarnings("deprecation")   // BlockType#asMaterial: Bukkit's Tag API is still keyed by Material
    static List<BlockRegistry.Candidate> candidates(World world) {
        Map<String, Tag<Material>> tags = new HashMap<>();
        for (String t : BlockRegistry.TAGS) {
            Tag<Material> tag = Bukkit.getTag(Tag.REGISTRY_BLOCKS, NamespacedKey.minecraft(t), Material.class);
            if (tag != null) tags.put(t, tag);
        }
        List<BlockRegistry.Candidate> out = new ArrayList<>();
        Set<String> listed = new HashSet<>();
        for (ItemType it : Registry.ITEM) {
            if (!it.hasBlockType()) continue;
            BlockType bt = it.getBlockType();
            if (bt == null || bt.isAir()) continue;
            String id = bt.getKey().toString();
            if (!listed.add(id)) continue;
            out.add(candidate(bt, it, world, tags));
        }
        for (BlockType bt : Registry.BLOCK) {
            String id = bt.getKey().toString();
            if (bt.isAir() || !bt.hasItemType() || listed.contains(id)) continue;
            listed.add(id);
            out.add(candidate(bt, bt.getItemType(), world, tags));
        }
        return out;
    }

    @SuppressWarnings("deprecation")
    private static BlockRegistry.Candidate candidate(BlockType bt, ItemType it, World world, Map<String, Tag<Material>> tags) {
        String id = bt.getKey().toString();
        String home = it != null && it.hasBlockType() && it.getBlockType() != null ? it.getBlockType().getKey().toString() : null;
        Set<String> in = new HashSet<>();
        Material m = bt.asMaterial();
        if (m != null) tags.forEach((name, tag) -> { if (tag.isTagged(m)) in.add(name); });
        boolean enabled = world == null || world.isEnabled(bt);
        String state;
        try { state = bt.createBlockData().getAsString(); } catch (RuntimeException e) { state = id; }
        return new BlockRegistry.Candidate(id, state, it == null ? null : it.getKey().toString(), home, Set.copyOf(in), enabled);
    }
}
