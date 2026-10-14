// Minimal PNG decoder (engine-free, any thread): the block-menu icons are rendered on a worker thread, where Unity's
// Texture2D.LoadImage can't run. Greyscale, RGB, palette (with tRNS), grey+alpha and RGBA, bit depths 1-16, no
// interlacing (resource-pack textures never use it). Pixels come back bottom-up, like Texture2D.GetPixels32.
using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class PngDecoder
    {
        /// <summary>Decoded pixels (row 0 = bottom), or null when the data isn't a PNG this decoder supports.</summary>
        public static Color32[] Decode(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png == null || png.Length < 33 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') return null;
            int pos = 8, depth = 8, type = 6, interlace = 0;
            byte[] palette = null, trns = null;
            var idat = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                if (len < 0 || pos + 12 + len > png.Length) break;
                int d = pos + 8;
                char a = (char)png[pos + 4], b = (char)png[pos + 5], c = (char)png[pos + 6], e = (char)png[pos + 7];
                if (a == 'I' && b == 'H' && c == 'D' && e == 'R')
                {
                    width = (png[d] << 24) | (png[d + 1] << 16) | (png[d + 2] << 8) | png[d + 3];
                    height = (png[d + 4] << 24) | (png[d + 5] << 16) | (png[d + 6] << 8) | png[d + 7];
                    depth = png[d + 8]; type = png[d + 9]; interlace = png[d + 12];
                }
                else if (a == 'P' && b == 'L' && c == 'T' && e == 'E') { palette = new byte[len]; Array.Copy(png, d, palette, 0, len); }
                else if (a == 't' && b == 'R' && c == 'N' && e == 'S') { trns = new byte[len]; Array.Copy(png, d, trns, 0, len); }
                else if (a == 'I' && b == 'D' && c == 'A' && e == 'T') idat.Write(png, d, len);
                else if (a == 'I' && b == 'E' && c == 'N' && e == 'D') break;
                pos = d + len + 4;
            }
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || interlace != 0 || idat.Length < 3) return null;
            int channels = type == 0 ? 1 : type == 2 ? 3 : type == 3 ? 1 : type == 4 ? 2 : type == 6 ? 4 : 0;
            if (channels == 0 || (depth != 1 && depth != 2 && depth != 4 && depth != 8 && depth != 16)) return null;
            int bitsPerPixel = channels * depth, bpp = Math.Max(1, bitsPerPixel / 8), stride = (width * bitsPerPixel + 7) / 8;
            byte[] raw;
            try
            {
                idat.Position = 2;   // zlib header; DeflateStream reads the raw deflate data and ignores the adler32 after it
                using (var z = new DeflateStream(idat, CompressionMode.Decompress)) using (var o = new MemoryStream()) { z.CopyTo(o); raw = o.ToArray(); }
            }
            catch (InvalidDataException) { return null; }
            if (raw.Length < (stride + 1) * height) return null;
            var cur = new byte[stride]; var prev = new byte[stride];
            var px = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int f = raw[y * (stride + 1)];
                Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= bpp ? cur[i - bpp] : 0, up = prev[i], ul = i >= bpp ? prev[i - bpp] : 0;
                    switch (f)
                    {
                        case 1: cur[i] = (byte)(cur[i] + left); break;
                        case 2: cur[i] = (byte)(cur[i] + up); break;
                        case 3: cur[i] = (byte)(cur[i] + ((left + up) >> 1)); break;
                        case 4:
                            int p = left + up - ul, pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - ul);
                            cur[i] = (byte)(cur[i] + (pa <= pb && pa <= pc ? left : pb <= pc ? up : ul));
                            break;
                    }
                }
                int row = (height - 1 - y) * width;
                for (int x = 0; x < width; x++) px[row + x] = Pixel(cur, x, type, depth, palette, trns);
                var t = prev; prev = cur; cur = t;
            }
            return px;
        }

        static int Sample(byte[] row, int index, int depth)
        {
            if (depth == 8) return row[index];
            if (depth == 16) return row[index * 2];   // high byte
            int perByte = 8 / depth, b = row[index / perByte], shift = 8 - depth * (index % perByte + 1);
            return (b >> shift) & ((1 << depth) - 1);
        }

        static byte Scale(int v, int depth) => depth >= 8 ? (byte)v : (byte)(v * 255 / ((1 << depth) - 1));

        static Color32 Pixel(byte[] row, int x, int type, int depth, byte[] palette, byte[] trns)
        {
            switch (type)
            {
                case 0:
                {
                    int raw = Sample(row, x, depth);
                    byte g = Scale(raw, depth);
                    byte alpha = trns != null && trns.Length >= 2 && ((trns[0] << 8) | trns[1]) == (depth == 16 ? (row[x * 2] << 8 | row[x * 2 + 1]) : raw) ? (byte)0 : (byte)255;
                    return new Color32(g, g, g, alpha);
                }
                case 3:
                {
                    int i = Sample(row, x, depth);
                    if (palette == null || i * 3 + 2 >= palette.Length) return new Color32(0, 0, 0, 0);
                    byte alpha = trns != null && i < trns.Length ? trns[i] : (byte)255;
                    return new Color32(palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2], alpha);
                }
                case 2:
                {
                    int s = depth == 16 ? 2 : 1, o = x * 3 * s;
                    var c = new Color32(row[o], row[o + s], row[o + 2 * s], 255);
                    if (trns != null && trns.Length >= 6 && depth == 8 && trns[1] == c.r && trns[3] == c.g && trns[5] == c.b) c.a = 0;
                    return c;
                }
                case 4: { int s = depth == 16 ? 2 : 1, o = x * 2 * s; return new Color32(row[o], row[o], row[o], row[o + s]); }
                default: { int s = depth == 16 ? 2 : 1, o = x * 4 * s; return new Color32(row[o], row[o + s], row[o + 2 * s], row[o + 3 * s]); }
            }
        }
    }
}
