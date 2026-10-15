package dev.tabletopcraft.ar;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * [operator] The shared bookmarks (PROTOCOL.md "Marks: bookmarks, spawn and worlds"): named world positions every
 * headset sees, kept in plugins/TabletopCraftAR/bookmarks.yml. Pure (the file is read and written by OperatorService):
 * names are unique ignoring case (adding an existing name moves it), the list is sorted by name, at most {@code max}.
 */
final class Bookmarks {
    private final List<OperatorMessages.Mark> marks = new ArrayList<>();
    int max;

    Bookmarks(int max) { this.max = Math.max(1, max); }

    List<OperatorMessages.Mark> list() { return List.copyOf(marks); }
    int size() { return marks.size(); }

    OperatorMessages.Mark find(String name) {
        for (OperatorMessages.Mark m : marks) if (m.name().equalsIgnoreCase(name)) return m;
        return null;
    }

    /** Adds (or moves) a bookmark; returns a refusal, or null when it was stored. */
    String add(OperatorMessages.Mark m) {
        OperatorMessages.Mark old = find(m.name());
        if (old == null && marks.size() >= max) return "at most " + max + " bookmarks (marks.max): remove one first";
        if (old != null) marks.remove(old);
        marks.add(m);
        marks.sort((a, b) -> {
            int c = a.name().toLowerCase(Locale.ROOT).compareTo(b.name().toLowerCase(Locale.ROOT));
            return c != 0 ? c : a.name().compareTo(b.name());
        });
        return null;
    }

    /** Removes a bookmark by name (ignoring case); false when there is none. */
    boolean remove(String name) {
        OperatorMessages.Mark old = find(name);
        return old != null && marks.remove(old);
    }

    /** The bookmarks as YAML-friendly maps (name, world, x, y, z, by). */
    List<Map<String, Object>> toMaps() {
        List<Map<String, Object>> l = new ArrayList<>();
        for (OperatorMessages.Mark m : marks) {
            Map<String, Object> o = new LinkedHashMap<>();
            o.put("name", m.name());
            o.put("world", m.world());
            o.put("x", m.x()); o.put("y", m.y()); o.put("z", m.z());
            if (m.by() != null && !m.by().isEmpty()) o.put("by", m.by());
            l.add(o);
        }
        return l;
    }

    /** Reads maps written by {@link #toMaps} (bad entries are skipped and counted). */
    int load(List<?> maps) {
        marks.clear();
        int bad = 0;
        if (maps == null) return 0;
        for (Object o : maps) {
            if (!(o instanceof Map<?, ?> m)) { bad++; continue; }
            String name = OperatorMessages.cleanMarkName(m.get("name") == null ? null : String.valueOf(m.get("name")));
            Object w = m.get("world");
            if (name.isEmpty() || w == null || !(m.get("x") instanceof Number x) || !(m.get("y") instanceof Number y) || !(m.get("z") instanceof Number z)) { bad++; continue; }
            Object by = m.get("by");
            if (add(new OperatorMessages.Mark(name, String.valueOf(w), x.intValue(), y.intValue(), z.intValue(), by == null ? null : String.valueOf(by))) != null) bad++;
        }
        return bad;
    }
}
