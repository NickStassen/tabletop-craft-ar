// Mip chains for the block texture array (ResourcePackAtlas), the way vanilla 26.3 builds them
// (MipmapGenerator.generateMipLevels with the AUTO strategy): a texture with fully transparent pixels (leaves, glass,
// plants, rails...) is CUTOUT: its transparent pixels first take the colour of the nearest opaque one
// (TextureUtil.solidify, so mips don't darken towards the holes), and every smaller level's alpha is scaled until it
// covers as much as level 0 does at the alpha cutoff (alphaTestCoverage / scaleAlphaToCoverage), so leaves and grass
// don't thin out and vanish in the distance. Every other texture is MEAN. Both average 2x2 pixels, RGB in linear
// light (ARGB.meanLinear). Engine-free (Tests~); pixels are Unity's rows (bottom-up), which none of this depends on.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class TextureMips
    {
        /// <summary>The alpha cutoff the levels keep their coverage at: the block shader's _Cutoff.</summary>
        public const float Cutoff = 0.3f;

        /// <summary>Mip levels of a size x size texture down to 1 x 1 (16 -> 5).</summary>
        public static int Levels(int size) { int n = 1; while (size > 1) { size >>= 1; n++; } return n; }

        /// <summary>Vanilla's CUTOUT test (Transparency.hasTransparent): any fully transparent pixel.</summary>
        public static bool HasTransparent(Color32[] px) { foreach (var p in px) if (p.a == 0) return true; return false; }

        /// <summary>Levels 0..Levels(size)-1; level 0 is the input (solidified when it is a cutout texture).</summary>
        public static Color32[][] Chain(Color32[] level0, int size)
        {
            var levels = new Color32[Levels(size)][];
            bool cutout = HasTransparent(level0);
            levels[0] = cutout ? Solidify(level0, size) : level0;
            float target = cutout ? Coverage(levels[0], size, Cutoff, 1f) : 0;
            int s = size;
            for (int k = 1; k < levels.Length; k++)
            {
                levels[k] = Mean(levels[k - 1], s);
                s = Math.Max(1, s >> 1);
                if (cutout) ScaleToCoverage(levels[k], s, target, Cutoff);
            }
            return levels;
        }

        /// <summary>TextureUtil.solidify: transparent pixels take the colour of the nearest non-transparent one (breadth
        /// first), alpha unchanged.</summary>
        public static Color32[] Solidify(Color32[] src, int size)
        {
            var dst = (Color32[])src.Clone();
            var dist = new int[dst.Length];
            var queue = new Queue<int>();
            for (int i = 0; i < dst.Length; i++)
            {
                if (dst[i].a != 0) { queue.Enqueue(i); dist[i] = 0; }
                else dist[i] = int.MaxValue;
            }
            if (queue.Count == 0) return dst;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(), x = i % size, y = i / size;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                    int n = ny * size + nx;
                    if (dist[n] != int.MaxValue) continue;
                    dist[n] = dist[i] + 1;
                    dst[n] = new Color32(dst[i].r, dst[i].g, dst[i].b, 0);
                    queue.Enqueue(n);
                }
            }
            return dst;
        }

        static float ToLinear(byte c) { float v = c / 255f; return v <= 0.04045f ? v / 12.92f : (float)Math.Pow((v + 0.055f) / 1.055f, 2.4f); }
        static byte ToSrgb(float v) { v = v <= 0.0031308f ? v * 12.92f : 1.055f * (float)Math.Pow(v, 1 / 2.4f) - 0.055f; return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v * 255))); }

        /// <summary>ARGB.meanLinear over 2x2 blocks: alpha averaged as is, RGB in linear light.</summary>
        public static Color32[] Mean(Color32[] src, int size)
        {
            int n = Math.Max(1, size >> 1);
            var dst = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int x0 = Math.Min(2 * x, size - 1), x1 = Math.Min(2 * x + 1, size - 1), y0 = Math.Min(2 * y, size - 1), y1 = Math.Min(2 * y + 1, size - 1);
                    Color32 a = src[y0 * size + x0], b = src[y0 * size + x1], c = src[y1 * size + x0], d = src[y1 * size + x1];
                    dst[y * n + x] = new Color32(
                        ToSrgb((ToLinear(a.r) + ToLinear(b.r) + ToLinear(c.r) + ToLinear(d.r)) / 4),
                        ToSrgb((ToLinear(a.g) + ToLinear(b.g) + ToLinear(c.g) + ToLinear(d.g)) / 4),
                        ToSrgb((ToLinear(a.b) + ToLinear(b.b) + ToLinear(c.b) + ToLinear(d.b)) / 4),
                        (byte)((a.a + b.a + c.a + d.a) / 4));
                }
            return dst;
        }

        /// <summary>MipmapGenerator.alphaTestCoverage: the share of 4x4 bilinear sub-samples of every 2x2 pixel
        /// neighbourhood whose alpha (times scale, clamped) is over the cutoff. NaN for a 1x1 level, like vanilla.</summary>
        public static float Coverage(Color32[] px, int size, float cutoff, float scale)
        {
            float total = 0;
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    float a00 = A(px[y * size + x]), a10 = A(px[y * size + x + 1]), a01 = A(px[(y + 1) * size + x]), a11 = A(px[(y + 1) * size + x + 1]);
                    int count = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        float fy = (j + 0.5f) / 4f;
                        for (int i = 0; i < 4; i++)
                        {
                            float fx = (i + 0.5f) / 4f;
                            float v = a00 * (1 - fx) * (1 - fy) + a10 * fx * (1 - fy) + a01 * (1 - fx) * fy + a11 * fx * fy;
                            if (v > cutoff) count++;
                        }
                    }
                    total += count / 16f;
                }
            return total / ((size - 1) * (size - 1));
            float A(Color32 c) => Math.Max(0f, Math.Min(1f, c.a / 255f * scale));
        }

        /// <summary>MipmapGenerator.scaleAlphaToCoverage: five bisection steps for the alpha scale (0..4) whose coverage is
        /// closest to the target, then alpha = clamp(alpha * scale + 0.025).</summary>
        public static void ScaleToCoverage(Color32[] px, int size, float target, float cutoff)
        {
            float lo = 0, hi = 4, scale = 1, best = 1, bestErr = float.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                float cov = Coverage(px, size, cutoff, scale);
                float err = Math.Abs(cov - target);
                if (err < bestErr) { bestErr = err; best = scale; }
                if (cov < target) lo = scale;
                else if (cov > target) hi = scale;
                else break;   // equal, or NaN (1x1)
                scale = (lo + hi) * 0.5f;
            }
            for (int i = 0; i < px.Length; i++)
            {
                float a = Math.Max(0f, Math.Min(1f, px[i].a / 255f * best + 0.025f));
                px[i].a = (byte)Math.Round(a * 255f);
            }
        }

        /// <summary>Bytes of one RGBA32 layer with its mips (16 px: 1,364 = 1,024 x 4/3).</summary>
        public static int Bytes(int size, bool mips)
        {
            int total = 0;
            for (int s = size; ; s = Math.Max(1, s >> 1)) { total += s * s * 4; if (!mips || s == 1) break; }
            return total;
        }
    }
}
