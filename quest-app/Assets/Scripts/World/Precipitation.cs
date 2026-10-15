// [living] Where it rains and where it snows on the miniature, as vanilla 26.3 decides it (Biome.getPrecipitationAt):
// none in biomes without precipitation (deserts, savannas, badlands, the Nether and the End), snow where the biome's
// temperature at that height is below 0.15 (Biome.getHeightAdjustedTemperature: 0.05 / 40 colder per block above sea
// level + 17, with the game's seed-1234 temperature noise), rain elsewhere; drops stop at the highest motion-blocking
// block of their column (the MOTION_BLOCKING heightmap). Engine-free (Tests~); WeatherRenderer builds the mesh.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum Precip : byte { None, Rain, Snow }

    /// <summary>One drop: its column (region-local block coordinates of the column's centre), where it lands and where it
    /// starts, a phase 0..1 so drops don't fall in step, its speed in blocks per second, rain or snow.</summary>
    public struct Drop
    {
        public float X, Z, Ground, Top, Phase, Speed;
        public Precip Kind;
    }

    public static class Precipitation
    {
        public const int SeaLevel = 63;   // the overworld's (world_state doesn't send it; custom worlds may differ)
        public const float SnowBelow = 0.15f;
        public const float RainSpeed = 18f, SnowSpeed = 2f;   // blocks per second on the table

        /// <summary>26.3 worldgen biomes with "has_precipitation": false.</summary>
        static readonly HashSet<string> Dry = new HashSet<string>
        {
            "badlands", "basalt_deltas", "crimson_forest", "desert", "end_barrens", "end_highlands", "end_midlands", "eroded_badlands",
            "nether_wastes", "savanna", "savanna_plateau", "small_end_islands", "soul_sand_valley", "the_end", "the_void",
            "warped_forest", "windswept_savanna", "wooded_badlands",
        };

        static string Path(string key) => key == null ? "" : key.StartsWith("minecraft:", StringComparison.Ordinal) ? key.Substring(10) : key;

        public static bool HasPrecipitation(string biome) => !Dry.Contains(Path(biome));

        /// <summary>Biome.getHeightAdjustedTemperature: the base temperature, lower above sea level + 17 by 0.05 per 40 blocks
        /// plus the temperature noise (frozen-ocean patches are not modelled).</summary>
        public static float Temperature(float baseTemperature, int wx, int wy, int wz, int seaLevel = SeaLevel)
        {
            int snowLine = seaLevel + 17;
            if (wy <= snowLine) return baseTemperature;
            float noise = (float)TemperatureNoise.Get(wx / 8.0, wz / 8.0) * 8f;
            return baseTemperature - (noise + wy - snowLine) * 0.05f / 40f;
        }

        /// <summary>What falls at world block (wx, wy, wz) of a biome (key as the server names it, its climate if not vanilla).</summary>
        public static Precip At(string biome, double[] climate, int wx, int wy, int wz, int seaLevel = SeaLevel)
        {
            if (!HasPrecipitation(biome)) return Precip.None;
            float t = BiomeColors.Info(biome, climate).Temperature;
            return Temperature(t, wx, wy, wz, seaLevel) < SnowBelow ? Precip.Snow : Precip.Rain;
        }

        /// <summary>Vanilla's MOTION_BLOCKING heightmap test, by shape: anything solid or liquid stops a drop; air and plants
        /// without collision (crosses) let it through. Cached per palette index (Stops): the string tests are slow on the
        /// headset (they used to run for every drop every 5 seconds: a ~150 ms hitch at 256 wide in the rain).</summary>
        public static bool Blocks(WorldModel w, ushort id) => id < w.Palette.Count && Stops(w)[id] != 0;

        /// <summary>The name test behind Blocks (ordinal comparisons: culture-aware ones cost microseconds each under IL2CPP).</summary>
        public static bool StopsDrops(BlockShape s, string state)
        {
            if (s == BlockShape.Air || s == BlockShape.Cross) return false;
            string p = BlockRules.Path(state);
            bool E(string suffix) => p.EndsWith(suffix, StringComparison.Ordinal);
            return !(E("grass") && p != "grass_block") && !E("fern") && !p.Contains("flower") && !E("torch") && !E("sapling")
                   && !E("_rail") && p != "rail" && !E("button") && !E("pressure_plate") && !E("sign") && !E("banner")
                   && p != "redstone_wire" && p != "tripwire" && !E("vine") && !E("_bush") && p != "bush" && p != "dead_bush"
                   && !E("tulip") && p != "dandelion" && p != "poppy" && p != "allium" && p != "azure_bluet" && p != "oxeye_daisy" && p != "cornflower"
                   && p != "lily_of_the_valley" && p != "blue_orchid" && p != "wither_rose" && p != "sugar_cane" && p != "snow" && !E("_roots")
                   && p != "nether_sprouts" && p != "lever" && !E("_carpet") && p != "moss_carpet" && p != "glow_lichen" && p != "sculk_vein";
        }

        // Per palette index: 1 = stops drops. Rebuilt when the model, its snapshot, its palette size or the block shapes change
        // (main thread only, like WeatherRenderer).
        static WorldModel _stopsFor;
        static int _stopsSnapshot = int.MinValue, _stopsCount = -1, _stopsVersion = -1;
        static byte[] _stops = new byte[0];

        static byte[] Stops(WorldModel w)
        {
            int n = w.Palette.Count;
            if (_stopsFor == w && _stopsSnapshot == w.SnapshotId && _stopsCount == n && _stopsVersion == BlockRules.Version) return _stops;
            var s = new byte[n];
            for (int i = 0; i < n; i++) s[i] = StopsDrops(w.ShapeOf((ushort)i), w.Palette[i]) ? (byte)1 : (byte)0;
            _stops = s; _stopsFor = w; _stopsSnapshot = w.SnapshotId; _stopsCount = n; _stopsVersion = BlockRules.Version;
            return s;
        }

        /// <summary>Region-local y where drops of column (x, z) land: one above the highest motion-blocking cell; 0 when the
        /// column is open to the bottom of the region.</summary>
        public static int Ground(WorldModel w, int x, int z)
        {
            if (!w.InBounds(x, 0, z)) return 0;
            var stops = Stops(w);
            var grid = w.Grid;
            int layer = w.Dims.x * w.Dims.z;
            for (int y = w.Dims.y - 1, i = w.Index(x, y, z); y >= 0; y--, i -= layer)
            {
                ushort id = grid[i];
                if (id < stops.Length && stops[id] != 0) return y + 1;
            }
            return 0;
        }

        /// <summary>
        /// Up to count drops over the columns [x0, x1) x [z0, z1) of the region (fewer where nothing falls), deterministic for
        /// a seed: random columns, each told rain or snow by its biome at the landing height, falling from the region top.
        /// </summary>
        public static List<Drop> Layout(WorldModel w, int x0, int z0, int x1, int z1, int count, int seed, List<Drop> into = null)
        {
            var drops = into ?? new List<Drop>(count);
            drops.Clear();
            if (w == null || !w.Loaded || x1 <= x0 || z1 <= z0) return drops;
            var rnd = new System.Random(seed);
            int top = w.Dims.y;
            for (int i = 0; i < count; i++)
            {
                int x = rnd.Next(x0, x1), z = rnd.Next(z0, z1);
                float fx = (float)rnd.NextDouble(), fz = (float)rnd.NextDouble(), phase = (float)rnd.NextDouble(), speed = 0.8f + 0.4f * (float)rnd.NextDouble();
                if (!w.InBounds(x, 0, z)) continue;
                int ground = Ground(w, x, z);
                if (ground >= top) continue;
                var kind = KindAt(w, x, ground, z);
                if (kind == Precip.None) continue;
                drops.Add(new Drop { X = x + fx, Z = z + fz, Ground = ground, Top = top, Phase = phase, Kind = kind,
                                     Speed = (kind == Precip.Snow ? SnowSpeed : RainSpeed) * speed });
            }
            return drops;
        }

        /// <summary>Rain or snow at a region-local cell from the snapshot's biomes (rain without biome data).</summary>
        public static Precip KindAt(WorldModel w, int x, int y, int z)
        {
            var g = w.Biomes;
            if (g == null) return Precip.Rain;
            int wx = w.Origin.x + x, wy = w.Origin.y + y, wz = w.Origin.z + z;
            int i = g.At(wx, wy, wz);
            if (_biomesFor != g)
            {
                // Per biome palette index: base temperature, NaN without precipitation (the string lookups once per snapshot).
                var t = new float[g.Palette.Length];
                for (int k = 0; k < t.Length; k++)
                    t[k] = HasPrecipitation(g.Palette[k]) ? BiomeColors.Info(g.Palette[k], g.Climate?[k]).Temperature : float.NaN;
                _biomeTemps = t; _biomesFor = g;
            }
            if ((uint)i >= (uint)_biomeTemps.Length) return At(g.Palette[i], g.Climate?[i], wx, wy, wz);
            float bt = _biomeTemps[i];
            if (float.IsNaN(bt)) return Precip.None;
            return Temperature(bt, wx, wy, wz) < SnowBelow ? Precip.Snow : Precip.Rain;
        }

        static BiomeGrid _biomesFor;
        static float[] _biomeTemps = new float[0];
    }

    /// <summary>Biome.TEMPERATURE_NOISE: PerlinSimplexNoise(seed 1234, octave 0), one 2D simplex octave like SwampNoise
    /// (which is the same construction with seed 2345).</summary>
    public static class TemperatureNoise
    {
        static readonly SimplexNoise2 Noise = new SimplexNoise2(1234L);
        public static double Get(double x, double y) => Noise.Get(x, y);
    }

    /// <summary>The game's 2D SimplexNoise from a legacy random seed (permutation table after three nextDouble offsets).</summary>
    public sealed class SimplexNoise2
    {
        readonly byte[] _p;
        static readonly int[,] Grad = { { 1, 1 }, { -1, 1 }, { 1, -1 }, { -1, -1 }, { 1, 0 }, { -1, 0 }, { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 }, { 0, 1 }, { 0, -1 } };
        static readonly double F2 = 0.5 * (Math.Sqrt(3.0) - 1.0), G2 = (3.0 - Math.Sqrt(3.0)) / 6.0;

        public SimplexNoise2(long seed0)
        {
            long seed = (seed0 ^ 0x5DEECE66DL) & ((1L << 48) - 1);
            int Next(int bits) { seed = (seed * 0x5DEECE66DL + 0xBL) & ((1L << 48) - 1); return (int)(seed >> (48 - bits)); }
            int NextInt(int bound)
            {
                if ((bound & (bound - 1)) == 0) return (int)((bound * (long)Next(31)) >> 31);
                int bits, val;
                do { bits = Next(31); val = bits % bound; } while (bits - val + (bound - 1) < 0);
                return val;
            }
            for (int i = 0; i < 3; i++) { Next(26); Next(27); }   // xo, yo, zo
            _p = new byte[256];
            for (int i = 0; i < 256; i++) _p[i] = (byte)i;
            for (int i = 0; i < 256; i++) { int j = NextInt(256 - i); byte t = _p[i]; _p[i] = _p[i + j]; _p[i + j] = t; }
        }

        int Perm(int i) => _p[i & 255];

        static double Corner(int g, double x, double y)
        {
            double d = 0.5 - x * x - y * y;
            if (d < 0) return 0;
            d *= d;
            return d * d * (Grad[g, 0] * x + Grad[g, 1] * y);
        }

        public double Get(double x, double y)
        {
            double s = (x + y) * F2;
            int i = (int)Math.Floor(x + s), j = (int)Math.Floor(y + s);
            double t = (i + j) * G2;
            double x0 = x - (i - t), y0 = y - (j - t);
            int i1 = x0 > y0 ? 1 : 0, j1 = x0 > y0 ? 0 : 1;
            double x1 = x0 - i1 + G2, y1 = y0 - j1 + G2, x2 = x0 - 1.0 + 2.0 * G2, y2 = y0 - 1.0 + 2.0 * G2;
            int ii = i & 255, jj = j & 255;
            int g0 = Perm(ii + Perm(jj)) % 12, g1 = Perm(ii + i1 + Perm(jj + j1)) % 12, g2 = Perm(ii + 1 + Perm(jj + 1)) % 12;
            return 70.0 * (Corner(g0, x0, y0) + Corner(g1, x1, y1) + Corner(g2, x2, y2));
        }
    }
}
