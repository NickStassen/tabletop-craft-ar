// [living] The lightning bolt's geometry, as 26.3's LightningBoltRenderer builds it from the bolt's seed: a main bolt of
// 8 segments 16 blocks tall that zig-zags (nextInt(11) - 5 per segment) down to the entity position, plus 2 branches of
// 3 segments that wander further (nextInt(31) - 15), each drawn in 4 layers of growing width (0.1 + 0.2 * layer,
// tapering towards the ground on the main bolt), every segment as 4 quads around its column. Colour (0.45, 0.45, 0.5)
// at alpha 0.3, blended additively (RenderTypes.lightning). Engine-free (Tests~); EntityFigure turns it into a mesh.
// The server doesn't expose the bolt's seed, so the app uses the entity id: the shape is vanilla's, not that exact bolt.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>java.util.Random / the game's LegacyRandomSource (same LCG), for porting the game's seeded shapes.</summary>
    public sealed class JavaRandom
    {
        long _seed;
        public JavaRandom(long seed) { _seed = (seed ^ 0x5DEECE66DL) & ((1L << 48) - 1); }

        int Next(int bits) { _seed = (_seed * 0x5DEECE66DL + 0xBL) & ((1L << 48) - 1); return (int)((ulong)_seed >> (48 - bits)); }

        public int NextInt(int bound)
        {
            if ((bound & -bound) == bound) return (int)((bound * (long)Next(31)) >> 31);
            int bits, val;
            do { bits = Next(31); val = bits % bound; } while (bits - val + (bound - 1) < 0);
            return val;
        }

        public long NextLong() => ((long)Next(32) << 32) + Next(32);
        public float NextFloat() => Next(24) / (float)(1 << 24);
    }

    public static class LightningBolt
    {
        public static readonly Color32 Color = new Color32((byte)(0.45f * 255 + 0.5f), (byte)(0.45f * 255 + 0.5f), (byte)(0.5f * 255 + 0.5f), (byte)(0.3f * 255 + 0.5f));
        public const int Layers = 4, Branches = 3, Segments = 8;

        /// <summary>The bolt's quads in blocks, relative to the strike point (y up; game x and z, not mirrored), 4 corners
        /// each in vanilla's order. 4 layers x (8 main + 2 x 3 branch) segments x 4 quads = 224 quads.</summary>
        public static List<Vector3> Quads(long seed)
        {
            var quads = new List<Vector3>(224 * 4);
            var xs = new float[8]; var zs = new float[8];
            float x = 0, z = 0;
            var r = new JavaRandom(seed);
            for (int i = 7; i >= 0; i--)
            {
                xs[i] = x; zs[i] = z;
                x += r.NextInt(11) - 5;
                z += r.NextInt(11) - 5;
            }
            float endX = x, endZ = z;
            for (int layer = 0; layer < Layers; layer++)
            {
                var r2 = new JavaRandom(seed);
                for (int branch = 0; branch < Branches; branch++)
                {
                    bool main = branch == 0;
                    int from = 7 - branch, to = main ? 0 : from - 2;
                    float px = xs[from] - endX, pz = zs[from] - endZ;
                    for (int seg = from; seg >= to; seg--)
                    {
                        float ox = px, oz = pz;
                        if (main) { px += r2.NextInt(11) - 5; pz += r2.NextInt(11) - 5; }
                        else { px += r2.NextInt(31) - 15; pz += r2.NextInt(31) - 15; }
                        float w1 = 0.1f + layer * 0.2f, w2 = w1;
                        if (main) { w1 *= seg * 0.1f + 1f; w2 *= (seg - 1) * 0.1f + 1f; }
                        Quad(quads, px, pz, ox, oz, seg, w1, w2, false, false, true, false);
                        Quad(quads, px, pz, ox, oz, seg, w1, w2, true, false, true, true);
                        Quad(quads, px, pz, ox, oz, seg, w1, w2, true, true, false, true);
                        Quad(quads, px, pz, ox, oz, seg, w1, w2, false, true, false, false);
                    }
                }
            }
            return quads;
        }

        /// <summary>LightningBoltRenderer.quad: the segment from (x0, z0) at the top (y = (seg + 1) * 16) to (x1, z1) at the
        /// bottom (y = seg * 16), one side of its square column.</summary>
        static void Quad(List<Vector3> q, float x1, float z1, float x0, float z0, int seg, float wTop, float wBottom, bool a, bool b, bool c, bool d)
        {
            q.Add(new Vector3(x1 + (a ? wBottom : -wBottom), seg * 16, z1 + (b ? wBottom : -wBottom)));
            q.Add(new Vector3(x0 + (a ? wTop : -wTop), (seg + 1) * 16, z0 + (b ? wTop : -wTop)));
            q.Add(new Vector3(x0 + (c ? wTop : -wTop), (seg + 1) * 16, z0 + (d ? wTop : -wTop)));
            q.Add(new Vector3(x1 + (c ? wBottom : -wBottom), seg * 16, z1 + (d ? wBottom : -wBottom)));
        }
    }
}
