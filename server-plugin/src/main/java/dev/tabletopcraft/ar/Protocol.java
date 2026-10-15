package dev.tabletopcraft.ar;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.util.List;

/** Message helpers for PROTOCOL.md v1. Envelope fields (v, type, seq, t) are added by {@link Session#send}. */
public final class Protocol {
    public static final int VERSION = 1;
    public static final Gson GSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();

    public static final int CLOSE_VERSION = 4001, CLOSE_HELLO_TIMEOUT = 4002, CLOSE_AUTH = 4003, CLOSE_IDLE = 4004, CLOSE_SHUTDOWN = 4005;
    public static final int MAX_INBOUND_CHARS = 64 * 1024;
    public static final int MAX_OUTBOUND_BYTES = 4 * 1024 * 1024;

    private Protocol() {}

    public static JsonObject body(String type) {
        JsonObject o = new JsonObject();
        o.addProperty("v", VERSION);
        o.addProperty("type", type);
        return o;
    }

    public static JsonArray ints(int... v) {
        JsonArray a = new JsonArray();
        for (int i : v) a.add(i);
        return a;
    }

    public static JsonArray doubles(double... v) {
        JsonArray a = new JsonArray();
        for (double d : v) a.add(Math.round(d * 1000.0) / 1000.0);
        return a;
    }

    public static JsonObject chunkSnapshot(int snapshotId, RegionSpec r, List<String> palette, int[] data) {
        JsonObject o = body("chunk_snapshot");
        o.addProperty("snapshot_id", snapshotId);
        o.addProperty("world", r.world());
        o.add("origin", ints(r.ox(), r.oy(), r.oz()));
        o.add("dims", ints(r.sx(), r.sy(), r.sz()));
        o.addProperty("order", "yzx");
        JsonArray pal = new JsonArray();
        palette.forEach(pal::add);
        o.add("palette", pal);
        o.addProperty("encoding", "rle_palette_v1");
        o.add("data", ints(data));
        o.add("biomes", null);
        o.add("light", null);
        return o;
    }

    /**
     * One part of a {@code sections_v1} chunk_snapshot (PROTOCOL.md 4.18). baseId &lt; 0 = full snapshot, else a delta
     * against the client's snapshot baseId with the origin moved by shift (whole sections). With light, each section
     * carries its own "light" runs and the top-level "light" says so; biomes (whole region) go on part 0.
     */
    public static JsonObject chunkSnapshotSections(int snapshotId, RegionSpec r, List<String> palette, int[] viewMargin,
                                                   int part, int parts, int baseId, int[] shift, List<SectionCodec.Section> sections,
                                                   boolean light, BiomeGrid biomes) {
        JsonObject o = body("chunk_snapshot");
        o.addProperty("snapshot_id", snapshotId);
        o.addProperty("world", r.world());
        o.add("origin", ints(r.ox(), r.oy(), r.oz()));
        o.add("dims", ints(r.sx(), r.sy(), r.sz()));
        if (viewMargin != null) o.add("view_margin", ints(viewMargin));
        o.addProperty("order", "yzx");
        JsonArray pal = new JsonArray();
        palette.forEach(pal::add);
        o.add("palette", pal);
        o.addProperty("encoding", SectionCodec.ENCODING);
        if (baseId >= 0) {
            JsonObject b = new JsonObject();
            b.addProperty("snapshot_id", baseId);
            b.add("shift", ints(shift));
            o.add("base", b);
        }
        o.addProperty("part", part);
        o.addProperty("parts", parts);
        JsonArray secs = new JsonArray();
        for (SectionCodec.Section s : sections) {
            JsonObject e = new JsonObject();
            e.add("s", ints(s.sx(), s.sy(), s.sz()));
            e.add("data", ints(s.data()));
            if (s.light() != null) e.add("light", ints(s.light()));
            secs.add(e);
        }
        o.add("sections", secs);
        o.add("biomes", biomes != null && part == 0 ? biomes.toJson() : null);
        if (light) {
            JsonObject l = new JsonObject();
            l.addProperty("encoding", "rle_v1");
            l.addProperty("in_sections", true);
            o.add("light", l);
        } else o.add("light", null);
        return o;
    }

    /** Optional per-session features a client opts into via hello.caps (PROTOCOL.md 4.4.1). */
    public static final String CAP_LIGHT = "light_v1", CAP_BIOMES = "biomes_v1";
    /** Non-player entities in an "entities" message (PROTOCOL.md 4.21). */
    public static final String CAP_ENTITIES = "entities_v1";
    /** Swings, digging, block breaks/places and damage in an "actions" message (PROTOCOL.md 4.22). */
    public static final String CAP_ACTIONS = "actions_v1";
    /** Block editing from the headset (block_edit, PROTOCOL.md 4.20). */
    public static final String CAP_EDIT = "edit_v1";
    /** [blocks] Block entity data (sign text, banner patterns, heads...) in a "block_entities" message (PROTOCOL.md 4.29). */
    public static final String CAP_BLOCK_ENTITIES = "block_entities_v1";

    /** Fills the snapshot's optional "light" / "biomes" fields (left null when the session didn't ask for them). */
    public static void addExtras(JsonObject snapshot, byte[] light, BiomeGrid biomes) {
        if (light != null) {
            JsonObject l = new JsonObject();
            l.addProperty("encoding", "rle_v1");
            l.add("data", ints(BlockCodec.encode(light)));
            snapshot.add("light", l);
        }
        if (biomes != null) snapshot.add("biomes", biomes.toJson());
    }

    public record Change(int lx, int ly, int lz, int paletteIndex) {}

    /** block_update, plus the optional "light" list of [x, y, z, sky << 4 | block] for cells whose light changed. */
    public static JsonObject blockUpdate(int snapshotId, List<String> paletteAdd, List<Change> changes, List<int[]> light) {
        JsonObject o = blockUpdate(snapshotId, paletteAdd, changes);
        if (light != null && !light.isEmpty()) {
            JsonArray a = new JsonArray();
            for (int[] c : light) a.add(ints(c));
            o.add("light", a);
        }
        return o;
    }

    public static JsonObject blockUpdate(int snapshotId, List<String> paletteAdd, List<Change> changes) {
        JsonObject o = body("block_update");
        o.addProperty("snapshot_id", snapshotId);
        JsonArray add = new JsonArray();
        paletteAdd.forEach(add::add);
        o.add("palette_add", add);
        JsonArray a = new JsonArray();
        for (Change c : changes) a.add(ints(c.lx(), c.ly(), c.lz(), c.paletteIndex()));
        o.add("changes", a);
        return o;
    }

    public static JsonObject error(String code, String message, Long refSeq, boolean fatal) {
        JsonObject o = body("error");
        o.addProperty("code", code);
        o.addProperty("message", message);
        if (refSeq != null) o.addProperty("ref_seq", refSeq);
        o.addProperty("fatal", fatal);
        return o;
    }
}
