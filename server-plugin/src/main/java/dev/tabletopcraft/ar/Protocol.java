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
     * against the client's snapshot baseId with the origin moved by shift (whole sections).
     */
    public static JsonObject chunkSnapshotSections(int snapshotId, RegionSpec r, List<String> palette, int[] viewMargin,
                                                   int part, int parts, int baseId, int[] shift, List<SectionCodec.Section> sections) {
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
            secs.add(e);
        }
        o.add("sections", secs);
        o.add("biomes", null);
        o.add("light", null);
        return o;
    }

    public record Change(int lx, int ly, int lz, int paletteIndex) {}

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
