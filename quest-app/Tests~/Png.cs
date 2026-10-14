// Test-only PNG reader (8-bit grey/RGB/RGBA/palette, not interlaced) so the colormap tests can read the real client
// jar without Unity's Texture2D.LoadImage. Returns 0xRRGGBB pixels, row-major from the top-left.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

static class Png
{
    public static int[] DecodeRgb(byte[] png, out int width, out int height)
    {
        int pos = 8, bitDepth = 0, colorType = 0, interlace = 0;
        width = height = 0;
        byte[] palette = null;
        var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int data = pos + 8;
            if (type == "IHDR")
            {
                width = (png[data] << 24) | (png[data + 1] << 16) | (png[data + 2] << 8) | png[data + 3];
                height = (png[data + 4] << 24) | (png[data + 5] << 16) | (png[data + 6] << 8) | png[data + 7];
                bitDepth = png[data + 8]; colorType = png[data + 9]; interlace = png[data + 12];
            }
            else if (type == "PLTE") { palette = new byte[len]; Array.Copy(png, data, palette, 0, len); }
            else if (type == "IDAT") idat.Write(png, data, len);
            else if (type == "IEND") break;
            pos = data + len + 4;
        }
        if (bitDepth != 8 || interlace != 0) throw new NotSupportedException($"png depth {bitDepth} interlace {interlace}");
        int bpp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException("png colour type " + colorType) };
        byte[] raw;
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) using (var o = new MemoryStream()) { z.CopyTo(o); raw = o.ToArray(); }
        int stride = width * bpp;
        var cur = new byte[stride]; var prev = new byte[stride];
        var outPx = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int add = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c), _ => throw new FormatException("png filter " + f) };
                cur[i] = (byte)(cur[i] + add);
            }
            for (int x = 0; x < width; x++)
            {
                int r, g, bl;
                switch (colorType)
                {
                    case 0: case 4: r = g = bl = cur[x * bpp]; break;
                    case 3: int p = cur[x] * 3; r = palette[p]; g = palette[p + 1]; bl = palette[p + 2]; break;
                    default: r = cur[x * bpp]; g = cur[x * bpp + 1]; bl = cur[x * bpp + 2]; break;
                }
                outPx[y * width + x] = (r << 16) | (g << 8) | bl;
            }
            (prev, cur) = (cur, prev);
        }
        return outPx;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>RGBA PNG (8-bit, filter 0) of bottom-up Color32 pixels, for looking at rendered icons (--icons DIR).</summary>
    public static byte[] EncodeRgba(UnityEngine.Color32[] px, int width, int height)
    {
        var raw = new MemoryStream();
        for (int y = height - 1; y >= 0; y--)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++) { var c = px[y * width + x]; raw.WriteByte(c.r); raw.WriteByte(c.g); raw.WriteByte(c.b); raw.WriteByte(c.a); }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) raw.WriteTo(zs);
        var o = new MemoryStream();
        o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, width); BE(ihdr, 4, height); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(o, "IHDR", ihdr); Chunk(o, "IDAT", z.ToArray()); Chunk(o, "IEND", new byte[0]);
        return o.ToArray();
    }

    static void BE(byte[] b, int i, int v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }

    static void Chunk(Stream o, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); o.Write(len);
        var td = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(td, 0); data.CopyTo(td, 4);
        o.Write(td);
        uint crc = 0xFFFFFFFF;
        foreach (byte bt in td) { crc ^= bt; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1)); }
        var c = new byte[4]; BE(c, 0, (int)~crc); o.Write(c);
    }

    /// <summary>Reads one entry of a zip (e.g. the client jar) and decodes it; null if the entry is missing.</summary>
    public static int[] FromZip(string zipPath, string entry)
    {
        using var z = ZipFile.OpenRead(zipPath);
        var e = z.GetEntry(entry);
        if (e == null) return null;
        using var s = e.Open(); using var ms = new MemoryStream();
        s.CopyTo(ms);
        var px = DecodeRgb(ms.ToArray(), out int w, out int h);
        return w == 256 && h == 256 ? px : null;
    }
}
