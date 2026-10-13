package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** Maps block-state strings (e.g. "minecraft:oak_stairs[facing=north,...]") to dense ids. */
public final class Palette {
    private final Map<String, Integer> ids = new HashMap<>();
    private final List<String> states = new ArrayList<>();

    public synchronized int id(String state) {
        Integer id = ids.get(state);
        if (id != null) return id;
        int n = states.size();
        ids.put(state, n);
        states.add(state);
        return n;
    }

    public synchronized String state(int id) { return states.get(id); }
    public synchronized List<String> states() { return List.copyOf(states); }
    public synchronized int size() { return states.size(); }
}
