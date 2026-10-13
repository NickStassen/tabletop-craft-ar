// Classifies block-state strings for the v1 mesher and provides a no-assets flat colour table.
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum BlockShape : byte { Air, Cube, TransparentCube, Cross, Slab, Fluid }

    public static class BlockRules
    {
        // Filled from the meshing worker threads, so it must be concurrent (a plain Dictionary corrupted under
        // parallel inserts and threw NullReferenceException in the first meshing batch on the headset).
        static readonly ConcurrentDictionary<string, BlockShape> Cache = new ConcurrentDictionary<string, BlockShape>();

        public static string Name(string state) { int i = state.IndexOf('['); return i < 0 ? state : state.Substring(0, i); }
        public static string Path(string state) { var n = Name(state); int c = n.IndexOf(':'); return c < 0 ? n : n.Substring(c + 1); }

        public static BlockShape Shape(string state)
        {
            if (Cache.TryGetValue(state, out var s)) return s;
            string p = Path(state);
            if (p == "air" || p == "cave_air" || p == "void_air" || p == "barrier" || p == "light" || p == "structure_void") s = BlockShape.Air;
            else if (p == "water" || p == "lava" || p == "bubble_column") s = BlockShape.Fluid;
            else if (p.EndsWith("_slab") && !state.Contains("type=double")) s = BlockShape.Slab;
            else if (p.Contains("glass") || p.EndsWith("leaves") || p == "ice" || p.Contains("slime_block") || p.Contains("honey_block")) s = BlockShape.TransparentCube;
            else if (IsCross(p)) s = BlockShape.Cross;
            else if (IsNonCube(p)) s = BlockShape.Cross; // placeholder for stairs-free odd shapes (torches, rails, signs...)
            else s = BlockShape.Cube;
            Cache[state] = s;
            return s;
        }

        static bool IsCross(string p) =>
            p.Contains("sapling") || p.Contains("flower") || p == "short_grass" || p == "tall_grass" || p == "fern" || p == "large_fern" ||
            p.Contains("tulip") || p == "dandelion" || p == "poppy" || p.Contains("orchid") || p == "allium" || p == "azure_bluet" ||
            p.Contains("daisy") || p == "cornflower" || p.Contains("lily_of") || p.Contains("mushroom") && !p.Contains("block") ||
            p == "dead_bush" || p.Contains("wheat") || p.Contains("carrots") || p.Contains("potatoes") || p.Contains("beetroots") ||
            p == "sugar_cane" || p.Contains("vine") || p.Contains("roots") || p == "cobweb" || p.Contains("_bush") || p.Contains("seagrass") || p.Contains("kelp");

        static bool IsNonCube(string p) =>
            p.Contains("torch") || p.Contains("rail") || p.Contains("sign") || p.Contains("button") || p.Contains("lever") ||
            p.Contains("pressure_plate") || p.Contains("redstone_wire") || p.Contains("carpet") || p.Contains("ladder") || p.Contains("_pane") ||
            p.EndsWith("_bars") || p.Contains("fence") || p.EndsWith("_wall") || p.Contains("door") || p.Contains("banner") || p == "snow" || p.Contains("candle") || p.Contains("lantern") || p.Contains("chain");

        public static bool Opaque(BlockShape s) => s == BlockShape.Cube;

        /// <summary>Fallback colour when no resource pack is installed (zero Mojang assets).</summary>
        public static Color32 FlatColor(string state)
        {
            string p = Path(state);
            if (p == "grass_block") return new Color32(96, 160, 64, 255);
            if (p.Contains("leaves")) return new Color32(58, 120, 40, 255);
            if (p == "water") return new Color32(52, 92, 220, 170);
            if (p == "lava") return new Color32(230, 100, 20, 255);
            if (p.Contains("dirt") || p == "farmland" || p.Contains("mud")) return new Color32(134, 96, 67, 255);
            if (p.Contains("sand")) return new Color32(219, 207, 163, 255);
            if (p.Contains("log") || p.Contains("wood") || p.Contains("stem")) return new Color32(102, 81, 51, 255);
            if (p.Contains("planks") || p.Contains("stairs") && p.Contains("oak")) return new Color32(162, 130, 78, 255);
            if (p.Contains("glass")) return new Color32(200, 230, 240, 90);
            if (p == "bedrock") return new Color32(60, 60, 60, 255);
            if (p.Contains("snow")) return new Color32(245, 250, 250, 255);
            if (p.Contains("gold")) return new Color32(246, 208, 61, 255);
            if (p.Contains("diamond")) return new Color32(98, 219, 214, 255);
            if (p.Contains("iron")) return new Color32(216, 216, 216, 255);
            if (p.Contains("brick")) return new Color32(150, 97, 83, 255);
            if (p.Contains("torch")) return new Color32(255, 200, 80, 255);
            foreach (var c in Colors) if (p.Contains(c.Key)) return c.Value;
            if (p.Contains("stone") || p.Contains("ore") || p.Contains("andesite") || p.Contains("diorite") || p.Contains("granite") || p.Contains("deepslate") || p.Contains("cobble"))
                return new Color32(125, 125, 125, 255);
            // Stable pseudo-random colour so unknown blocks are still distinguishable.
            int h = p.GetHashCode();
            return new Color32((byte)(80 + (h & 0x7F)), (byte)(80 + ((h >> 8) & 0x7F)), (byte)(80 + ((h >> 16) & 0x7F)), 255);
        }

        static readonly KeyValuePair<string, Color32>[] Colors =
        {
            new KeyValuePair<string, Color32>("white", new Color32(234, 236, 237, 255)), new KeyValuePair<string, Color32>("orange", new Color32(240, 118, 19, 255)),
            new KeyValuePair<string, Color32>("magenta", new Color32(189, 68, 179, 255)), new KeyValuePair<string, Color32>("light_blue", new Color32(58, 175, 217, 255)),
            new KeyValuePair<string, Color32>("yellow", new Color32(248, 198, 39, 255)), new KeyValuePair<string, Color32>("lime", new Color32(112, 185, 25, 255)),
            new KeyValuePair<string, Color32>("pink", new Color32(237, 141, 172, 255)), new KeyValuePair<string, Color32>("light_gray", new Color32(142, 142, 134, 255)),
            new KeyValuePair<string, Color32>("gray", new Color32(62, 68, 71, 255)), new KeyValuePair<string, Color32>("cyan", new Color32(21, 137, 145, 255)),
            new KeyValuePair<string, Color32>("purple", new Color32(121, 42, 172, 255)), new KeyValuePair<string, Color32>("blue", new Color32(53, 57, 157, 255)),
            new KeyValuePair<string, Color32>("brown", new Color32(114, 71, 40, 255)), new KeyValuePair<string, Color32>("green", new Color32(84, 109, 27, 255)),
            new KeyValuePair<string, Color32>("red", new Color32(160, 39, 34, 255)), new KeyValuePair<string, Color32>("black", new Color32(20, 21, 25, 255)),
        };
    }
}
