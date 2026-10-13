package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

/**
 * Parsed region_request (PROTOCOL.md 4.13). Either origin+dims, or center+size. With center, X/Z (and Y unless
 * y_origin is given) are centred: origin = center - floor(size/2).
 */
public record RegionRequest(String world, int ox, int oy, int oz, int dx, int dy, int dz) {

    public static RegionRequest parse(JsonObject m) {
        String world = m.has("world") && !m.get("world").isJsonNull() ? m.get("world").getAsString() : null;
        int[] dims;
        int[] origin;
        if (m.has("dims") && m.has("origin")) {
            origin = three(m, "origin");
            dims = three(m, "dims");
        } else if (m.has("center") && m.has("size")) {
            int[] c = three(m, "center");
            dims = three(m, "size");
            int oy = m.has("y_origin") && !m.get("y_origin").isJsonNull() ? m.get("y_origin").getAsInt() : c[1] - dims[1] / 2;
            origin = new int[] { c[0] - dims[0] / 2, oy, c[2] - dims[2] / 2 };
        } else throw new IllegalArgumentException("region_request needs origin+dims or center+size");
        for (int d : dims) if (d < 1) throw new IllegalArgumentException("dims must be >= 1");
        return new RegionRequest(world, origin[0], origin[1], origin[2], dims[0], dims[1], dims[2]);
    }

    private static int[] three(JsonObject m, String k) {
        JsonArray a = m.getAsJsonArray(k);
        if (a == null || a.size() != 3) throw new IllegalArgumentException(k + " must be [x,y,z]");
        double x = a.get(0).getAsDouble(), y = a.get(1).getAsDouble(), z = a.get(2).getAsDouble();
        return new int[] { (int) Math.floor(x), (int) Math.floor(y), (int) Math.floor(z) };
    }

    /** Rough JSON size of a chunk_snapshot for an RLE pair array + palette (bytes). Used for the max_bytes cap. */
    public static long estimateSnapshotBytes(int[] data, java.util.List<String> palette) {
        long b = 300; // envelope and fixed fields
        for (String s : palette) b += s.length() + 3;
        for (int v : data) b += digits(v) + 1;
        return b;
    }

    static int digits(int v) { return v < 10 ? 1 : v < 100 ? 2 : v < 1000 ? 3 : v < 10000 ? 4 : v < 100000 ? 5 : v < 1000000 ? 6 : v < 10000000 ? 7 : 10; }
}
