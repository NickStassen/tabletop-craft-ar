// Test-side stand-ins for ResourcePackAtlas: IModelAssets over a client jar / resource pack zip (one fake layer
// per texture, alpha class read from the PNG), and an IFaceSource/IModelSource that prepares states the way the
// atlas does when the renderer prewarms a palette.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

sealed class ZipAssets : IModelAssets, IDisposable
{
    public static readonly Color32 Tint = new Color32(124, 189, 107, 255);
    readonly ZipArchive _zip;
    readonly Dictionary<string, int> _layers = new Dictionary<string, int>();
    readonly List<int> _alpha = new List<int>();
    public readonly List<string> Names = new List<string>();

    public ZipAssets(string path) { _zip = ZipFile.OpenRead(path); }

    public Dictionary<string, object> ReadJson(string path)
    {
        var e = _zip.GetEntry(path);
        if (e == null) return null;
        using (var r = new StreamReader(e.Open())) return Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
    }

    public int LayerOf(string texture)
    {
        string ns = "minecraft", p = texture;
        int c = texture.IndexOf(':'); if (c >= 0) { ns = texture.Substring(0, c); p = texture.Substring(c + 1); }
        string key = ns + ":" + p;
        if (!_layers.TryGetValue(key, out int l))
        {
            var e = _zip.GetEntry($"assets/{ns}/textures/{p}.png");
            l = -1;
            if (e != null) { l = Names.Count; Names.Add(key); _alpha.Add(AlphaOf(e)); }
            _layers[key] = l;
        }
        return l;
    }

    public FaceKey Texture(string texture, bool tinted, string state)
    {
        int l = LayerOf(texture);
        return new FaceKey { Layer = l, Color = l < 0 ? BlockRules.FlatColor(state) : tinted ? Tint : new Color32(255, 255, 255, 255),
                             Tint = tinted && l >= 0 ? BiomeColors.BlockTint(state, out _) : TintKind.None };   // as ResourcePackAtlas.Key
    }

    public int AlphaClass(int layer) => layer >= 0 && layer < _alpha.Count ? _alpha[layer] : 0;

    public void Dispose() => _zip.Dispose();

    static int BE32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];

    /// <summary>0 opaque, 1 cutout, 2 translucent, over the first frame (top square) like the atlas.</summary>
    static int AlphaOf(ZipArchiveEntry e)
    {
        byte[] data;
        using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
        int pos = 8, w = 0, h = 0, depth = 8, ctype = 6;
        byte[] trns = null;
        var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = BE32(data, pos), d = pos + 8;
            string type = Encoding.ASCII.GetString(data, pos + 4, 4);
            if (type == "IHDR") { w = BE32(data, d); h = BE32(data, d + 4); depth = data[d + 8]; ctype = data[d + 9]; }
            else if (type == "tRNS") { trns = new byte[len]; Array.Copy(data, d, trns, 0, len); }
            else if (type == "IDAT") idat.Write(data, d, len);
            else if (type == "IEND") break;
            pos = d + len + 4;
        }
        if (ctype == 0 || ctype == 2) return trns != null ? 1 : 0;
        int ch = ctype == 6 ? 4 : ctype == 4 ? 2 : 1;
        int bitsPerPixel = ch * depth, bpp = Math.Max(1, bitsPerPixel / 8), stride = (w * bitsPerPixel + 7) / 8;
        int rows = Math.Min(w, h);
        idat.Position = 0;
        var raw = new byte[(stride + 1) * h];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int n = 0, r;
            while (n < raw.Length && (r = z.Read(raw, n, raw.Length - n)) > 0) n += r;
        }
        var prev = new byte[stride];
        var cur = new byte[stride];
        bool cut = false;
        for (int y = 0; y < rows; y++)
        {
            int f = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0, x = cur[i];
                switch (f)
                {
                    case 1: x += a; break;
                    case 2: x += b; break;
                    case 3: x += (a + b) / 2; break;
                    case 4: { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); x += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; break; }
                }
                cur[i] = (byte)x;
            }
            for (int px = 0; px < w; px++)
            {
                int alpha;
                if (ctype == 3)
                {
                    int bit = px * depth, idx = (cur[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                    alpha = trns != null && idx < trns.Length ? trns[idx] : 255;
                }
                else alpha = cur[px * bpp + (ch - 1) * (depth / 8)];
                if (alpha != 255 && alpha != 0) return 2;
                if (alpha == 0) cut = true;
            }
            var t = prev; prev = cur; cur = t;
        }
        return cut ? 1 : 0;
    }
}

/// <summary>Like ResourcePackAtlas: Face() prepares a state's model the first time it is seen.</summary>
sealed class PackFaces : IFaceSource, IModelSource
{
    readonly BlockModels _models;
    readonly Dictionary<string, FaceKey[]> _faces = new Dictionary<string, FaceKey[]>();

    public PackFaces(IModelAssets assets, bool entityBoxes = true) { _models = new BlockModels(assets) { EntityBoxes = entityBoxes }; }

    public FaceKey Face(string state, int dir)
    {
        if (!_faces.TryGetValue(state, out var f))
        {
            f = new FaceKey[6];
            var flat = BlockRules.FlatColor(state);
            for (int i = 0; i < 6; i++) f[i] = new FaceKey { Layer = -1, Color = flat };
            _models.Prepare(state, f);
            _faces[state] = f;
        }
        return f[dir];
    }

    public BakedModel Model(string state) => _models.Model(state);

    /// <summary>What TabletopRenderer.Prewarm does on the main thread before meshing.</summary>
    public void Prewarm(WorldModel w) { foreach (var s in w.Palette) Face(s, 0); }
}
