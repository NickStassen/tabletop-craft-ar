package dev.tabletopcraft.ar;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Biomes of a region at vanilla's storage resolution (one biome per 4x4x4 "quart" cell), PROTOCOL.md 4.4.1.
 * The grid covers every quart cell the region touches: {@code q_origin = floor(region origin / 4)} in world quart
 * coordinates, and a block at region-local (x,y,z) uses cell {@code floor((origin+local)/4) - q_origin}.
 */
public final class BiomeGrid {
    public final int qx, qy, qz, qdx, qdy, qdz;
    public final List<String> palette = new ArrayList<>();
    public final int[] cells;
    /** World block position of the lowest cell seen per palette entry (where climate is sampled). */
    final List<int[]> samples = new ArrayList<>();
    private final Map<String, Integer> ids = new HashMap<>();
    /** [temperature, downfall] per palette entry; filled on the main thread, may stay null. */
    public double[][] climate;

    public BiomeGrid(int qx, int qy, int qz, int qdx, int qdy, int qdz) {
        this.qx = qx; this.qy = qy; this.qz = qz; this.qdx = qdx; this.qdy = qdy; this.qdz = qdz;
        this.cells = new int[qdx * qdy * qdz];
    }

    /** The quart grid covering region r (floor division, so negative coordinates work). */
    public static BiomeGrid covering(RegionSpec r) {
        int x0 = r.ox() >> 2, y0 = r.oy() >> 2, z0 = r.oz() >> 2;
        int x1 = (r.ox() + r.sx() - 1) >> 2, y1 = (r.oy() + r.sy() - 1) >> 2, z1 = (r.oz() + r.sz() - 1) >> 2;
        return new BiomeGrid(x0, y0, z0, x1 - x0 + 1, y1 - y0 + 1, z1 - z0 + 1);
    }

    /** Linear index in yzx order (x fastest), like the block grid. */
    public int index(int lqx, int lqy, int lqz) { return (lqy * qdz + lqz) * qdx + lqx; }

    /** Records the biome key of a cell; the first (lowest) sighting of a key becomes its climate sample point. */
    public void set(int lqx, int lqy, int lqz, String key, int wx, int wy, int wz) {
        Integer id = ids.get(key);
        if (id == null) {
            id = palette.size();
            ids.put(key, id);
            palette.add(key);
            samples.add(new int[] { wx, wy, wz });
        }
        cells[index(lqx, lqy, lqz)] = id;
    }

    public int[] sample(int paletteIndex) { return samples.get(paletteIndex); }

    public JsonObject toJson() {
        JsonObject o = new JsonObject();
        o.addProperty("res", 4);
        o.add("q_origin", Protocol.ints(qx, qy, qz));
        o.add("q_dims", Protocol.ints(qdx, qdy, qdz));
        JsonArray pal = new JsonArray();
        palette.forEach(pal::add);
        o.add("palette", pal);
        if (climate != null) {
            JsonArray cl = new JsonArray();
            for (double[] c : climate) cl.add(c == null ? null : Protocol.doubles(c[0], c[1]));
            o.add("climate", cl);
        }
        o.addProperty("encoding", "rle_palette_v1");
        o.add("data", Protocol.ints(BlockCodec.encode(cells)));
        return o;
    }
}
