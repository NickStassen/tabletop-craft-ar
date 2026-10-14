// Biome tints the vanilla way, for the per-vertex colour path (BlockShading). Engine-free (runs on meshing workers and
// in Tests~). Sources: the 26.3 client jar's data/minecraft/worldgen/biome/*.json (temperature, downfall, effects
// colours) and its colormap/*.png, plus vanilla's BlockColors rules for which blocks take which tint.
//   grass / foliage / dry foliage = the biome's override colour, else colormap[(1-temp)*255, (1-temp*downfall)*255]
//   swamp & mangrove grass        = two fixed colours chosen by a fixed simplex noise (seed 2345) at x,z * 0.0225
//   dark forest grass             = ((c & 0xFEFEFE) + 0x28340A) >> 1
//   water                         = the biome's water_color
// Tints are blended over a 5x5 column window like vanilla's default "Biome Blend" (radius 2).
using System;
using System.Collections.Generic;
using System.Threading;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>Which biome colour a tinted face takes. The atlas picks it per block from the model's tintindex.</summary>
    public enum TintKind : byte { None = 0, Grass = 1, Foliage = 2, DryFoliage = 3, Water = 4 }

    public enum GrassModifier : byte { None, DarkForest, Swamp }

    public sealed class BiomeInfo
    {
        public float Temperature = 0.8f, Downfall = 0.4f;          // floats, like vanilla's ClimateSettings
        public int Grass = -1, Foliage = -1, DryFoliage = -1;      // 0xRRGGBB overrides, -1 = use the colour map
        public int Water = 0x3F76E4;
        public GrassModifier Modifier;
    }

    public static class BiomeColors
    {
        /// <summary>256x256 colour maps as 0xRRGGBB, row-major from the PNG's top-left. Null until a pack provides them.</summary>
        public static int[] GrassMap, FoliageMap, DryFoliageMap;
        public const int BlendRadius = 2;   // vanilla default (5x5)

        public static void SetColormaps(int[] grass, int[] foliage, int[] dryFoliage)
        {
            GrassMap = grass != null && grass.Length == 65536 ? grass : null;
            FoliageMap = foliage != null && foliage.Length == 65536 ? foliage : null;
            DryFoliageMap = dryFoliage != null && dryFoliage.Length == 65536 ? dryFoliage : null;
        }

        /// <summary>Vanilla ColorMapColorUtil.get with the clamping Biome applies first. -1 when the map is missing.</summary>
        public static int Lookup(int[] map, float temperature, float downfall)
        {
            if (map == null) return -1;
            double t = Math.Min(Math.Max((double)temperature, 0.0), 1.0), d = Math.Min(Math.Max((double)downfall, 0.0), 1.0);
            d *= t;
            int i = (int)((1.0 - t) * 255.0), j = (int)((1.0 - d) * 255.0);
            int k = j << 8 | i;
            return k >= map.Length ? -1 : map[k] & 0xFFFFFF;
        }

        /// <summary>A biome's colour for one tint kind at world column (x, z), before blending. -1 = unknown (no colour map).</summary>
        public static int ColorOf(BiomeInfo b, TintKind kind, int wx, int wz)
        {
            switch (kind)
            {
                case TintKind.Water: return b.Water;
                case TintKind.Foliage: return b.Foliage >= 0 ? b.Foliage : Lookup(FoliageMap, b.Temperature, b.Downfall);
                case TintKind.DryFoliage: return b.DryFoliage >= 0 ? b.DryFoliage : Lookup(DryFoliageMap, b.Temperature, b.Downfall);
                case TintKind.Grass:
                    if (b.Modifier == GrassModifier.Swamp) return SwampNoise.Get(wx * 0.0225, wz * 0.0225) < -0.1 ? 0x4C763C : 0x6A7039;
                    int c = b.Grass >= 0 ? b.Grass : Lookup(GrassMap, b.Temperature, b.Downfall);
                    if (c >= 0 && b.Modifier == GrassModifier.DarkForest) c = ((c & 0xFEFEFE) + 0x28340A) >> 1;
                    return c;
                default: return -1;
            }
        }

        /// <summary>True when the colour depends on the exact column, not just the biome (so blending can't take the fast path).</summary>
        public static bool PositionDependent(BiomeInfo b, TintKind kind) => kind == TintKind.Grass && b.Modifier == GrassModifier.Swamp;

        /// <summary>
        /// Vanilla BlockColors: which tint a block's tinted faces (model "tintindex") take. Returns None with a fixed colour
        /// for blocks whose tint doesn't depend on the biome (spruce/birch leaves, lily pads, stems, redstone wire).
        /// </summary>
        public static TintKind BlockTint(string state, out Color32? fixedColor)
        {
            fixedColor = null;
            string p = BlockRules.Path(state);
            switch (p)
            {
                case "spruce_leaves": fixedColor = Rgb(0x619961); return TintKind.None;
                case "birch_leaves": fixedColor = Rgb(0x80A755); return TintKind.None;
                case "lily_pad": fixedColor = Rgb(0x208030); return TintKind.None;
                case "attached_melon_stem": case "attached_pumpkin_stem": fixedColor = Rgb(0xE0C71C); return TintKind.None;
                case "melon_stem": case "pumpkin_stem":
                {
                    int age = Prop(state, "age");
                    fixedColor = new Color32((byte)(age * 32), (byte)(255 - age * 8), (byte)(age * 4), 255);
                    return TintKind.None;
                }
                case "redstone_wire":
                {
                    float f = Prop(state, "power") / 15f;
                    float r = f * 0.6f + (f > 0 ? 0.4f : 0.3f), g = Mathf.Clamp01(f * f * 0.7f - 0.5f), bl = Mathf.Clamp01(f * f * 0.6f - 0.7f);
                    fixedColor = new Color32((byte)(r * 255), (byte)(g * 255), (byte)(bl * 255), 255);
                    return TintKind.None;
                }
                case "leaf_litter": return TintKind.DryFoliage;
                case "water": case "bubble_column": case "water_cauldron": return TintKind.Water;
            }
            if (p.EndsWith("leaves") || p == "vine") return TintKind.Foliage;
            return TintKind.Grass; // grass_block, short/tall grass, ferns, sugar cane, bush, pink petals, wildflowers, ...
        }

        static int Prop(string state, string key)
        {
            int i = state.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return 0;
            int v = 0;
            for (int k = i + key.Length + 1; k < state.Length && char.IsDigit(state[k]); k++) v = v * 10 + (state[k] - '0');
            return v;
        }

        public static Color32 Rgb(int c) => new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, 255);

        /// <summary>Biome by key: the built-in vanilla table first, else the server's [temperature, downfall], else plains.</summary>
        public static BiomeInfo Info(string key, double[] climate)
        {
            string k = key != null && key.StartsWith("minecraft:") ? key.Substring(10) : key;
            if (k != null && key.StartsWith("minecraft:") && Vanilla.TryGetValue(k, out var b)) return b;
            var custom = new BiomeInfo();
            if (climate != null && climate.Length >= 2) { custom.Temperature = (float)climate[0]; custom.Downfall = (float)climate[1]; }
            return custom;
        }

        static KeyValuePair<string, BiomeInfo> B(string n, float t, float d, int water, int grass, int foliage, int dry, GrassModifier mod) =>
            new KeyValuePair<string, BiomeInfo>(n, new BiomeInfo { Temperature = t, Downfall = d, Water = water, Grass = grass, Foliage = foliage, DryFoliage = dry, Modifier = mod });

        /// <summary>Vanilla 26.3 overworld/nether/end biomes (data/minecraft/worldgen/biome/*.json in the client jar).</summary>
        public static readonly Dictionary<string, BiomeInfo> Vanilla = new Dictionary<string, BiomeInfo>(new[]
        {
            B("badlands", 2.0f, 0.0f, water: 0x3F76E4, grass: 0x90814D, foliage: 0x9E814D, dry: -1, mod: GrassModifier.None),
            B("bamboo_jungle", 0.95f, 0.9f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("basalt_deltas", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("beach", 0.8f, 0.4f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("birch_forest", 0.6f, 0.6f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("cherry_grove", 0.5f, 0.8f, water: 0x5DB7EF, grass: 0xB6DB61, foliage: 0xB6DB61, dry: -1, mod: GrassModifier.None),
            B("cold_ocean", 0.5f, 0.5f, water: 0x3D57D6, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("crimson_forest", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("dappled_forest", 0.6f, 0.6f, water: 0x375154, grass: 0xDF6827, foliage: 0xE68E30, dry: 0x8C3A04, mod: GrassModifier.None),
            B("dark_forest", 0.7f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: 0x7B5334, mod: GrassModifier.DarkForest),
            B("deep_cold_ocean", 0.5f, 0.5f, water: 0x3D57D6, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("deep_dark", 0.8f, 0.4f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("deep_frozen_ocean", 0.5f, 0.5f, water: 0x3938C9, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("deep_lukewarm_ocean", 0.5f, 0.5f, water: 0x45ADF2, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("deep_ocean", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("desert", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("dripstone_caves", 0.8f, 0.4f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("end_barrens", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("end_highlands", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("end_midlands", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("eroded_badlands", 2.0f, 0.0f, water: 0x3F76E4, grass: 0x90814D, foliage: 0x9E814D, dry: -1, mod: GrassModifier.None),
            B("flower_forest", 0.7f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("forest", 0.7f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("frozen_ocean", 0.0f, 0.5f, water: 0x3938C9, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("frozen_peaks", -0.7f, 0.9f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("frozen_river", 0.0f, 0.5f, water: 0x3938C9, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("grove", -0.2f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("ice_spikes", 0.0f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("jagged_peaks", -0.7f, 0.9f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("jungle", 0.95f, 0.9f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("lukewarm_ocean", 0.5f, 0.5f, water: 0x45ADF2, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("lush_caves", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("mangrove_swamp", 0.8f, 0.9f, water: 0x3A7A6A, grass: -1, foliage: 0x8DB127, dry: 0x7B5334, mod: GrassModifier.Swamp),
            B("meadow", 0.5f, 0.8f, water: 0x0E4ECF, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("mushroom_fields", 0.9f, 1.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("nether_wastes", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("ocean", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("old_growth_birch_forest", 0.6f, 0.6f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("old_growth_pine_taiga", 0.3f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("old_growth_spruce_taiga", 0.25f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("pale_garden", 0.7f, 0.8f, water: 0x76889D, grass: 0x778272, foliage: 0x878D76, dry: 0xA0A69C, mod: GrassModifier.None),
            B("plains", 0.8f, 0.4f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("river", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("savanna", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("savanna_plateau", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("small_end_islands", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("snowy_beach", 0.05f, 0.3f, water: 0x3D57D6, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("snowy_plains", 0.0f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("snowy_slopes", -0.3f, 0.9f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("snowy_taiga", -0.5f, 0.4f, water: 0x3D57D6, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("soul_sand_valley", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("sparse_jungle", 0.95f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("stony_peaks", 1.0f, 0.3f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("stony_shore", 0.2f, 0.3f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("sulfur_caves", 0.8f, 0.4f, water: 0x34BF89, grass: 0xABA64F, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("sunflower_plains", 0.8f, 0.4f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("swamp", 0.8f, 0.9f, water: 0x617B64, grass: -1, foliage: 0x6A7039, dry: 0x7B5334, mod: GrassModifier.Swamp),
            B("taiga", 0.25f, 0.8f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("the_end", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("the_void", 0.5f, 0.5f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("warm_ocean", 0.5f, 0.5f, water: 0x43D5EE, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("warped_forest", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("windswept_forest", 0.2f, 0.3f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("windswept_gravelly_hills", 0.2f, 0.3f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("windswept_hills", 0.2f, 0.3f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("windswept_savanna", 2.0f, 0.0f, water: 0x3F76E4, grass: -1, foliage: -1, dry: -1, mod: GrassModifier.None),
            B("wooded_badlands", 2.0f, 0.0f, water: 0x3F76E4, grass: 0x90814D, foliage: 0x9E814D, dry: -1, mod: GrassModifier.None),
        });
    }

    /// <summary>
    /// Vanilla's BIOME_INFO_NOISE: 2D simplex noise seeded with java.util.Random(2345) (offsets are drawn but scaled by 0).
    /// Only used to pick the swamp grass colour, so swamp patches line up with the Java client.
    /// </summary>
    public static class SwampNoise
    {
        static readonly byte[] P = Build();
        static readonly int[,] Grad = { { 1, 1 }, { -1, 1 }, { 1, -1 }, { -1, -1 }, { 1, 0 }, { -1, 0 }, { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 }, { 0, 1 }, { 0, -1 } };
        static readonly double F2 = 0.5 * (Math.Sqrt(3.0) - 1.0), G2 = (3.0 - Math.Sqrt(3.0)) / 6.0;

        static byte[] Build()
        {
            long seed = (2345L ^ 0x5DEECE66DL) & ((1L << 48) - 1);
            int Next(int bits) { seed = (seed * 0x5DEECE66DL + 0xBL) & ((1L << 48) - 1); return (int)(seed >> (48 - bits)); }
            int NextInt(int bound)
            {
                if ((bound & (bound - 1)) == 0) return (int)((bound * (long)Next(31)) >> 31);
                int bits, val;
                do { bits = Next(31); val = bits % bound; } while (bits - val + (bound - 1) < 0);
                return val;
            }
            for (int i = 0; i < 3; i++) { Next(26); Next(27); } // three nextDouble() offsets
            var p = new byte[256];
            for (int i = 0; i < 256; i++) p[i] = (byte)i;
            for (int i = 0; i < 256; i++) { int j = NextInt(256 - i); byte t = p[i]; p[i] = p[i + j]; p[i + j] = t; }
            return p;
        }

        static int Perm(int i) => P[i & 255];

        static double Corner(int g, double x, double y)
        {
            double d = 0.5 - x * x - y * y;
            if (d < 0) return 0;
            d *= d;
            return d * d * (Grad[g, 0] * x + Grad[g, 1] * y);
        }

        public static float Get(double x, double y)
        {
            double s = (x + y) * F2;
            int i = (int)Math.Floor(x + s), j = (int)Math.Floor(y + s);
            double t = (i + j) * G2;
            double x0 = x - (i - t), y0 = y - (j - t);
            int i1 = x0 > y0 ? 1 : 0, j1 = x0 > y0 ? 0 : 1;
            double x1 = x0 - i1 + G2, y1 = y0 - j1 + G2, x2 = x0 - 1.0 + 2.0 * G2, y2 = y0 - 1.0 + 2.0 * G2;
            int ii = i & 255, jj = j & 255;
            int g0 = Perm(ii + Perm(jj)) % 12, g1 = Perm(ii + i1 + Perm(jj + j1)) % 12, g2 = Perm(ii + 1 + Perm(jj + 1)) % 12;
            return (float)(70.0 * (Corner(g0, x0, y0) + Corner(g1, x1, y1) + Corner(g2, x2, y2)));
        }
    }

    /// <summary>chunk_snapshot.biomes (PROTOCOL.md 4.4.1): one palette index per world-aligned 4x4x4 cell.</summary>
    public sealed class BiomeGrid
    {
        public Vector3Int QOrigin, QDims;
        public string[] Palette;
        public double[][] Climate;   // [temperature, downfall] per palette entry, or null
        public ushort[] Cells;       // yzx

        /// <summary>Parses the optional "biomes" object; null when absent (old server or no biomes_v1).</summary>
        public static BiomeGrid From(Dictionary<string, object> o)
        {
            if (o == null) return null;
            if (o.I("res") != 4 || o.S("encoding") != "rle_palette_v1") throw new FormatException("unsupported biome res/encoding");
            var q = Json.Ints(o.A("q_origin")); var d = Json.Ints(o.A("q_dims"));
            var pal = o.A("palette");
            var g = new BiomeGrid { QOrigin = new Vector3Int(q[0], q[1], q[2]), QDims = new Vector3Int(d[0], d[1], d[2]), Palette = new string[pal.Count] };
            for (int i = 0; i < pal.Count; i++) g.Palette[i] = (string)pal[i];
            if (o.A("climate") is List<object> cl && cl.Count == pal.Count)
            {
                g.Climate = new double[cl.Count][];
                for (int i = 0; i < cl.Count; i++) if (cl[i] is List<object> c && c.Count >= 2) g.Climate[i] = Json.Doubles(c);
            }
            g.Cells = ChunkSnapshot.DecodeRle(o.A("data"), d[0] * d[1] * d[2], pal.Count);
            return g;
        }

        /// <summary>Palette index of the cell holding world block (wx, wy, wz), clamped to the grid.</summary>
        public int At(int wx, int wy, int wz)
        {
            int x = Mathf.Clamp((wx >> 2) - QOrigin.x, 0, QDims.x - 1), y = Mathf.Clamp((wy >> 2) - QOrigin.y, 0, QDims.y - 1), z = Mathf.Clamp((wz >> 2) - QOrigin.z, 0, QDims.z - 1);
            return Cells[(y * QDims.z + z) * QDims.x + x];
        }
    }

    /// <summary>
    /// Blended biome tints for one snapshot, cached per (column, 4-block y layer, kind). Filled lazily by the meshing workers:
    /// a cache slot is an int written once with the same value by whichever thread gets there, so no locks are needed.
    /// </summary>
    public sealed class BiomeTints
    {
        readonly BiomeGrid _g;
        readonly Vector3Int _origin, _dims;
        readonly BiomeInfo[] _info;
        readonly int[][] _cache = new int[5][];
        readonly int _layers, _qy0;

        public BiomeTints(BiomeGrid g, Vector3Int origin, Vector3Int dims)
        {
            _g = g; _origin = origin; _dims = dims;
            _info = new BiomeInfo[g.Palette.Length];
            for (int i = 0; i < _info.Length; i++) _info[i] = BiomeColors.Info(g.Palette[i], g.Climate?[i]);
            _qy0 = origin.y >> 2;
            _layers = ((origin.y + dims.y - 1) >> 2) - _qy0 + 1;
        }

        /// <summary>Blended 0xRRGGBB tint at region-local (x, y, z), or -1 if the colour maps it needs aren't loaded.</summary>
        public int Tint(TintKind kind, int x, int y, int z)
        {
            if (kind == TintKind.None) return -1;
            x = Mathf.Clamp(x, 0, _dims.x - 1); y = Mathf.Clamp(y, 0, _dims.y - 1); z = Mathf.Clamp(z, 0, _dims.z - 1);
            var cache = _cache[(int)kind];
            if (cache == null)
            {
                Interlocked.CompareExchange(ref _cache[(int)kind], new int[_dims.x * _dims.z * _layers], null);
                cache = _cache[(int)kind];
            }
            int layer = ((_origin.y + y) >> 2) - _qy0;
            int slot = (layer * _dims.z + z) * _dims.x + x;
            int v = cache[slot];
            if (v != 0) return v == 1 ? -1 : v & 0xFFFFFF;
            int c = Blend(kind, x, y, z);
            cache[slot] = c < 0 ? 1 : c | unchecked((int)0xFF000000);
            return c;
        }

        int Blend(TintKind kind, int x, int y, int z)
        {
            int wy = _origin.y + y, R = BiomeColors.BlendRadius;
            int x0 = Math.Max(0, x - R), x1 = Math.Min(_dims.x - 1, x + R), z0 = Math.Max(0, z - R), z1 = Math.Min(_dims.z - 1, z + R);
            // Fast path: the window lies in one biome whose colour doesn't vary per column.
            int first = _g.At(_origin.x + x0, wy, _origin.z + z0);
            bool uniform = !BiomeColors.PositionDependent(_info[first], kind);
            for (int qz = (_origin.z + z0) >> 2; uniform && qz <= (_origin.z + z1) >> 2; qz++)
                for (int qx = (_origin.x + x0) >> 2; uniform && qx <= (_origin.x + x1) >> 2; qx++)
                    if (_g.At(qx << 2, wy, qz << 2) != first) uniform = false;
            if (uniform) return BiomeColors.ColorOf(_info[first], kind, _origin.x + x, _origin.z + z);
            // Vanilla: average of the (2R+1)^2 columns around the block at its own y (clamped to the region here).
            int r = 0, g = 0, b = 0, n = (2 * R + 1) * (2 * R + 1);
            for (int dz = -R; dz <= R; dz++)
                for (int dx = -R; dx <= R; dx++)
                {
                    int sx = Mathf.Clamp(x + dx, 0, _dims.x - 1), sz = Mathf.Clamp(z + dz, 0, _dims.z - 1);
                    int wx = _origin.x + sx, wz = _origin.z + sz;
                    int c = BiomeColors.ColorOf(_info[_g.At(wx, wy, wz)], kind, wx, wz);
                    if (c < 0) return -1;
                    r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                }
            return (r / n) << 16 | (g / n) << 8 | (b / n);
        }
    }
}
