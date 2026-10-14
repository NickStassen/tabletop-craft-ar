// Block-menu icons rendered on the CPU from the user's own pack.zip (engine-free; Tests~ renders them from a client jar).
// Like the game's inventory: blocks whose item model is flat (flowers, torches, rails, doors, panes...) show the item
// sprite (item/generated: layer0, layer1, tint); everything else is the item's 3D model (or, for builtin/entity items
// like chests and beds, the block's simple boxes from BlockModelBaker) drawn isometrically with the model's "gui"
// display transform (vanilla block/block.json: rotation [30, 225, 0], scale 0.625) and fixed inventory shading.
//
// All of it runs on one worker thread (IconStore): its own read-only handle on pack.zip, its own model baker and its
// own PNG decoding, so the main thread never bakes, decodes or loads textures for icons and ResourcePackAtlas (used by
// the mesher) is never touched. Finished icons are cached on disk (IconDiskCache), keyed by the pack's identity, so
// the next start reads them back instead of rendering. Nothing generated is ever committed.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>IModelAssets over a pack zip for one thread: JSON, textures (first animation frame, full resolution) and lang.</summary>
    public sealed class IconAssets : IModelAssets, IDisposable
    {
        static readonly Color32 GrassTint = new Color32(124, 189, 107, 255), FoliageTint = new Color32(72, 181, 24, 255), WaterTint = new Color32(63, 118, 228, 255);
        readonly ZipArchive _zip;
        readonly Dictionary<string, Dictionary<string, object>> _json = new Dictionary<string, Dictionary<string, object>>();
        readonly Dictionary<string, int> _layers = new Dictionary<string, int>();
        readonly List<Color32[]> _pixels = new List<Color32[]>();
        readonly List<int> _sizes = new List<int>();
        readonly List<sbyte> _alpha = new List<sbyte>();

        public IconAssets(string zipPath) { _zip = ZipFile.OpenRead(zipPath); }

        public byte[] ReadBytes(string path)
        {
            var e = _zip.GetEntry(path);
            if (e == null) return null;
            using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        public Dictionary<string, object> ReadJson(string path)
        {
            if (_json.TryGetValue(path, out var j)) return j;
            var b = ReadBytes(path);
            j = b == null ? null : Json.Parse(System.Text.Encoding.UTF8.GetString(b)) as Dictionary<string, object>;
            _json[path] = j;
            return j;
        }

        /// <summary>Layer index of a texture ("minecraft:block/stone", "block/stone"), -1 when the pack lacks it.</summary>
        public int LayerOf(string texture)
        {
            if (texture == null) return -1;
            if (_layers.TryGetValue(texture, out int l)) return l;
            l = -1;
            var px = PngDecoder.Decode(ReadBytes(ItemModels.TexturePath(texture)), out int w, out int h);
            if (px != null && w > 0)
            {
                // Animated textures are frames stacked vertically: keep the top square (stored bottom-up, so the last rows).
                int n = Math.Min(w, h);
                var sq = new Color32[n * n];
                for (int y = 0; y < n; y++) Array.Copy(px, (h - n + y) * w, sq, y * n, n);
                l = _pixels.Count;
                _pixels.Add(sq); _sizes.Add(n); _alpha.Add(-1);
            }
            _layers[texture] = l;
            return l;
        }

        public Color32[] Pixels(int layer, out int size)
        {
            size = layer >= 0 && layer < _sizes.Count ? _sizes[layer] : 0;
            return size > 0 ? _pixels[layer] : null;
        }

        /// <summary>As ResourcePackAtlas.Key: tinted faces take the block's fixed tint or the default grass/foliage/water colour.</summary>
        public FaceKey Texture(string texture, bool tinted, string state)
        {
            int layer = LayerOf(texture);
            var c = new Color32(255, 255, 255, 255);
            string p = BlockRules.Path(state);
            if (tinted || p == "water" || p == "bubble_column")
            {
                var kind = BiomeColors.BlockTint(state, out var fixedTint);
                c = fixedTint ?? (kind == TintKind.Water ? WaterTint : kind == TintKind.Foliage ? FoliageTint : GrassTint);
            }
            return new FaceKey { Layer = layer, Color = layer < 0 ? BlockRules.FlatColor(state) : c };
        }

        public int AlphaClass(int layer)
        {
            if (layer < 0 || layer >= _pixels.Count) return 0;
            if (_alpha[layer] < 0)
            {
                sbyte k = 0;
                foreach (var p in _pixels[layer]) { if (p.a == 255) continue; if (p.a != 0) { k = 2; break; } k = 1; }
                _alpha[layer] = k;
            }
            return _alpha[layer];
        }

        /// <summary>assets/minecraft/lang/en_us.json as key -> name, or null when the pack has none.</summary>
        public Dictionary<string, string> Language()
        {
            var j = ReadJson("assets/minecraft/lang/en_us.json");
            if (j == null) return null;
            var d = new Dictionary<string, string>(j.Count);
            foreach (var kv in j) if (kv.Value is string s && (kv.Key.StartsWith("block.") || kv.Key.StartsWith("item."))) d[kv.Key] = s;
            return d;
        }

        public void Dispose() => _zip.Dispose();
    }

    /// <summary>Renders one icon (Size x Size RGBA, bottom-up rows). One instance per thread.</summary>
    public sealed class IconRenderer
    {
        public const int Size = 64, Supersample = 2;
        /// <summary>Bump when the look changes: it is part of the disk cache key.</summary>
        public const int Version = 1;
        public static readonly ItemDisplay BlockGui = new ItemDisplay { Rx = 30, Ry = 225, Sx = 0.625f, Sy = 0.625f, Sz = 0.625f, Set = true };

        readonly IconAssets _assets;
        readonly BlockModelBaker _baker;

        /// <summary>assets null = no pack: every block is a cube in its flat colour.</summary>
        public IconRenderer(IconAssets assets)
        {
            _assets = assets;
            if (assets != null) _baker = new BlockModelBaker(assets);
        }

        public Color32[] Render(string state, string item)
        {
            if (_assets == null) return Cube(BlockRules.FlatColor(state));
            ItemVisual vis = null;
            try { vis = ItemModels.Resolve(item ?? BlockRegistry.IdOf(state), _assets.ReadJson); } catch (Exception) { }
            if (vis != null && vis.Kind == ItemVisualKind.Sprite)
            {
                var px = Sprite(vis);
                if (px != null) return px;
            }
            BakedModel model = null;
            try
            {
                if (vis != null && vis.Kind == ItemVisualKind.Cube && !vis.BuiltinEntity && vis.Model != null) model = _baker.BakeModel(vis.Model, state);
                if (model == null || model.Quads.Length == 0) model = _baker.Bake(state);   // block entities: their simple boxes
            }
            catch (Exception) { model = null; }
            if (model == null || model.Quads.Length == 0) return Cube(BlockRules.FlatColor(state));
            var gui = vis != null && vis.Gui.Set ? vis.Gui : BlockGui;
            return IconRaster.Draw(model.Quads, gui, l => _assets.Pixels(l, out int s) is Color32[] p ? (p, s) : (null, 0), Size, Supersample);
        }

        /// <summary>A flat item: layer0 (tinted) with layer1 over it, nearest-neighbour scaled to the icon.</summary>
        Color32[] Sprite(ItemVisual v)
        {
            var l0 = _assets.Pixels(_assets.LayerOf(v.Sprite), out int s0);
            if (l0 == null) return null;
            var l1 = v.Overlay != null ? _assets.Pixels(_assets.LayerOf(v.Overlay), out int s1) : null;
            int n1 = l1 != null ? (int)Math.Sqrt(l1.Length) : 0;
            var outPx = new Color32[Size * Size];
            var t = v.SpriteTint;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    var c = l0[(y * s0 / Size) * s0 + x * s0 / Size];
                    c = new Color32((byte)(c.r * t.r / 255), (byte)(c.g * t.g / 255), (byte)(c.b * t.b / 255), c.a);
                    if (l1 != null)
                    {
                        var o = l1[(y * n1 / Size) * n1 + x * n1 / Size];
                        c = Over(o, c);
                    }
                    outPx[y * Size + x] = c;
                }
            return outPx;
        }

        static Color32 Over(Color32 top, Color32 bottom)
        {
            float a = top.a / 255f, b = bottom.a / 255f * (1 - a), oa = a + b;
            if (oa <= 0) return new Color32(0, 0, 0, 0);
            return new Color32((byte)((top.r * a + bottom.r * b) / oa), (byte)((top.g * a + bottom.g * b) / oa), (byte)((top.b * a + bottom.b * b) / oa), (byte)(oa * 255));
        }

        /// <summary>A unit cube in one flat colour (no pack, or a block without a model).</summary>
        public static Color32[] Cube(Color32 c)
        {
            if (c.a < 40) c.a = 160;
            var quads = new List<BakedQuad>();
            for (int d = 0; d < 6; d++)
            {
                var k = CubeCorners[d];
                quads.Add(new BakedQuad
                {
                    P0 = new Vector3(k[0], k[1], k[2]), P1 = new Vector3(k[3], k[4], k[5]), P2 = new Vector3(k[6], k[7], k[8]), P3 = new Vector3(k[9], k[10], k[11]),
                    T0 = new Vector3(0, 0, -1), T1 = new Vector3(0, 0, -1), T2 = new Vector3(0, 0, -1), T3 = new Vector3(0, 0, -1),
                    Color = c, Dir = (sbyte)d, Cull = -1, Translucent = c.a < 255,
                });
            }
            return IconRaster.Draw(quads, BlockGui, l => (null, 0), Size, Supersample);
        }

        // Vanilla FaceInfo corners per direction (west, east, down, up, north, south), counter-clockwise from outside.
        static readonly float[][] CubeCorners =
        {
            new float[] { 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1 }, new float[] { 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0 },
            new float[] { 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 1 }, new float[] { 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0 },
            new float[] { 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0 }, new float[] { 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1 },
        };
    }

    /// <summary>
    /// Orthographic software rasteriser for one icon. The GUI view: vertex v (block units) goes to T/16 + R (S (v - 0.5))
    /// with R = Rx * Ry * Rz (degrees from the model's "gui" entry, applied Z, Y, X), x right, y up, the viewer on +z.
    /// Faces turned away are culled; the rest are shaded like the game's inventory (top 1.0, faces turned left 0.8,
    /// right 0.6), depth-tested (later coplanar faces win, so grass overlays sit on their base), cut-out textures
    /// alpha-tested, translucent faces blended back to front, then the supersampled image is averaged down.
    /// </summary>
    public static class IconRaster
    {
        public static Color32[] Draw(IList<BakedQuad> quads, ItemDisplay gui, Func<int, (Color32[] px, int size)> layer, int size, int ssaa)
        {
            int n = size * ssaa;
            var col = new float[n * n * 4];
            var depth = new float[n * n];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;
            var R = Rot(gui.Rx, gui.Ry, gui.Rz);
            var S = new Vector3(gui.Sx == 0 ? 1 : gui.Sx, gui.Sy == 0 ? 1 : gui.Sy, gui.Sz == 0 ? 1 : gui.Sz);
            var T = new Vector3(gui.Tx / 16f, gui.Ty / 16f, gui.Tz / 16f);
            var translucent = new List<(float z, int i)>();
            for (int q = 0; q < quads.Count; q++)
            {
                if (quads[q].Translucent) translucent.Add((AvgZ(quads[q], R, S, T), q));
                else DrawQuad(quads[q], R, S, T, layer, n, col, depth, false);
            }
            translucent.Sort((a, b) => a.z.CompareTo(b.z));   // far (small z) first
            foreach (var t in translucent) DrawQuad(quads[t.i], R, S, T, layer, n, col, depth, true);
            // Average down (premultiplied).
            var outPx = new Color32[size * size];
            float k = 1f / (ssaa * ssaa);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < ssaa; sy++)
                        for (int sx = 0; sx < ssaa; sx++)
                        {
                            int i = ((y * ssaa + sy) * n + x * ssaa + sx) * 4;
                            r += col[i]; g += col[i + 1]; b += col[i + 2]; a += col[i + 3];
                        }
                    a *= k;
                    outPx[y * size + x] = a <= 1e-4f ? new Color32(0, 0, 0, 0)
                        : new Color32(B(r * k / a), B(g * k / a), B(b * k / a), B(a));
                }
            return outPx;
        }

        static byte B(float v) => (byte)Math.Max(0, Math.Min(255, (int)(v * 255 + 0.5f)));

        static float[] Rot(float rx, float ry, float rz)
        {
            double a = rx * Math.PI / 180, b = ry * Math.PI / 180, c = rz * Math.PI / 180;
            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a), cb = (float)Math.Cos(b), sb = (float)Math.Sin(b), cc = (float)Math.Cos(c), sc = (float)Math.Sin(c);
            float[] X = { 1, 0, 0, 0, ca, -sa, 0, sa, ca }, Y = { cb, 0, sb, 0, 1, 0, -sb, 0, cb }, Z = { cc, -sc, 0, sc, cc, 0, 0, 0, 1 };
            return Mul(Mul(X, Y), Z);
        }

        static float[] Mul(float[] p, float[] q)
        {
            var r = new float[9];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i * 3 + j] = p[i * 3] * q[j] + p[i * 3 + 1] * q[3 + j] + p[i * 3 + 2] * q[6 + j];
            return r;
        }

        static Vector3 Apply(float[] m, Vector3 v) => new Vector3(m[0] * v.x + m[1] * v.y + m[2] * v.z, m[3] * v.x + m[4] * v.y + m[5] * v.z, m[6] * v.x + m[7] * v.y + m[8] * v.z);

        static Vector3 Xf(Vector3 p, float[] R, Vector3 S, Vector3 T)
        {
            var c = new Vector3((p.x - 0.5f) * S.x, (p.y - 0.5f) * S.y, (p.z - 0.5f) * S.z);
            return Apply(R, c) + T;
        }

        static float AvgZ(BakedQuad q, float[] R, Vector3 S, Vector3 T) =>
            (Xf(q.P0, R, S, T).z + Xf(q.P1, R, S, T).z + Xf(q.P2, R, S, T).z + Xf(q.P3, R, S, T).z) / 4;

        static void DrawQuad(BakedQuad q, float[] R, Vector3 S, Vector3 T, Func<int, (Color32[] px, int size)> layer, int n,
                             float[] col, float[] depth, bool blend)
        {
            var p = new[] { Xf(q.P0, R, S, T), Xf(q.P1, R, S, T), Xf(q.P2, R, S, T), Xf(q.P3, R, S, T) };
            var e1 = p[1] - p[0]; var e2 = p[3] - p[0];
            var nrm = new Vector3(e1.y * e2.z - e1.z * e2.y, e1.z * e2.x - e1.x * e2.z, e1.x * e2.y - e1.y * e2.x);
            if (Math.Abs(nrm.x) + Math.Abs(nrm.y) + Math.Abs(nrm.z) < 1e-9f) { e1 = p[2] - p[0]; nrm = new Vector3(e1.y * e2.z - e1.z * e2.y, e1.z * e2.x - e1.x * e2.z, e1.x * e2.y - e1.y * e2.x); }
            float len = (float)Math.Sqrt(nrm.x * nrm.x + nrm.y * nrm.y + nrm.z * nrm.z);
            if (len < 1e-9f || nrm.z / len <= 1e-4f) return;   // facing away (or edge-on)
            nrm = nrm * (1f / len);
            float wy = Math.Max(0, nrm.y), wl = Math.Max(0, -nrm.x), wr = Math.Max(0, nrm.x), sum = wy + wl + wr;
            float shade = sum < 0.05f ? 0.8f : (wy * 1f + wl * 0.8f + wr * 0.6f) / sum;
            int li = (int)Math.Round(q.T0.z);
            var tex = li >= 0 ? layer(li) : (null, 0);
            var uv = new[] { q.T0, q.T1, q.T2, q.T3 };
            Tri(p[0], p[1], p[2], uv[0], uv[1], uv[2], q.Color, shade, tex.px, tex.size, n, col, depth, blend);
            Tri(p[0], p[2], p[3], uv[0], uv[2], uv[3], q.Color, shade, tex.px, tex.size, n, col, depth, blend);
        }

        static void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 ta, Vector3 tb, Vector3 tc, Color32 tint, float shade,
                        Color32[] tex, int ts, int n, float[] col, float[] depth, bool blend)
        {
            float ax = (a.x + 0.5f) * n, ay = (a.y + 0.5f) * n, bx = (b.x + 0.5f) * n, by = (b.y + 0.5f) * n, cx = (c.x + 0.5f) * n, cy = (c.y + 0.5f) * n;
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-6f) return;
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(n - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            float tr = tint.r / 255f * shade, tg = tint.g / 255f * shade, tbl = tint.b / 255f * shade, talpha = tint.a / 255f;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                    float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    float z = w0 * a.z + w1 * b.z + w2 * c.z;
                    int i = y * n + x;
                    if (z < depth[i] - 1e-4f) continue;
                    float r = tr, g = tg, bl = tbl, al = talpha;
                    if (tex != null)
                    {
                        float u = w0 * ta.x + w1 * tb.x + w2 * tc.x, v = w0 * ta.y + w1 * tb.y + w2 * tc.y;
                        int tx = Math.Max(0, Math.Min(ts - 1, (int)Math.Floor(u * ts))), ty = Math.Max(0, Math.Min(ts - 1, (int)Math.Floor(v * ts)));
                        var t = tex[ty * ts + tx];
                        r *= t.r / 255f; g *= t.g / 255f; bl *= t.b / 255f; al *= t.a / 255f;
                    }
                    int k = i * 4;
                    if (!blend)
                    {
                        if (al < 0.1f) continue;   // cut-out
                        col[k] = r; col[k + 1] = g; col[k + 2] = bl; col[k + 3] = 1;
                        depth[i] = z;
                    }
                    else
                    {
                        if (al <= 0.004f) continue;
                        float da = col[k + 3], oa = al + da * (1 - al);
                        col[k] = (r * al + col[k] * da * (1 - al)) / oa; col[k + 1] = (g * al + col[k + 1] * da * (1 - al)) / oa;
                        col[k + 2] = (bl * al + col[k + 2] * da * (1 - al)) / oa; col[k + 3] = oa;
                    }
                }
        }
    }

    /// <summary>Finished icons on disk: persistentDataPath/icons/&lt;pack id&gt;-v&lt;version&gt;/&lt;hash&gt;.icon (deflated RGBA).</summary>
    public sealed class IconDiskCache
    {
        public readonly string Dir;
        int Bytes => IconRenderer.Size * IconRenderer.Size * 4;

        public IconDiskCache(string dir) { Dir = dir; }

        /// <summary>The cache folder for a pack under root, named by the pack's size and modification time (a new pack.zip
        /// gets a new folder); folders of other packs or older icon versions are deleted.</summary>
        public static IconDiskCache For(string root, string packPath)
        {
            string id = "nopack";
            try { var f = new FileInfo(packPath); if (f.Exists) id = f.Length.ToString("x") + "-" + f.LastWriteTimeUtc.Ticks.ToString("x"); } catch (Exception) { }
            string name = $"{id}-v{IconRenderer.Version}-{IconRenderer.Size}";
            try
            {
                if (Directory.Exists(root))
                    foreach (var d in Directory.GetDirectories(root)) if (Path.GetFileName(d) != name) Directory.Delete(d, true);
                Directory.CreateDirectory(Path.Combine(root, name));
            }
            catch (Exception) { }
            return new IconDiskCache(Path.Combine(root, name));
        }

        public string FileOf(string key)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in key) { h ^= c; h *= 1099511628211UL; }
            return Path.Combine(Dir, h.ToString("x16") + ".icon");
        }

        public Color32[] Load(string key)
        {
            try
            {
                string f = FileOf(key);
                if (!File.Exists(f)) return null;
                byte[] raw;
                using (var fs = File.OpenRead(f)) using (var z = new DeflateStream(fs, CompressionMode.Decompress)) using (var ms = new MemoryStream()) { z.CopyTo(ms); raw = ms.ToArray(); }
                if (raw.Length != Bytes) return null;
                var px = new Color32[raw.Length / 4];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(raw[i * 4], raw[i * 4 + 1], raw[i * 4 + 2], raw[i * 4 + 3]);
                return px;
            }
            catch (Exception) { return null; }
        }

        public void Save(string key, Color32[] px)
        {
            try
            {
                var raw = new byte[px.Length * 4];
                for (int i = 0; i < px.Length; i++) { raw[i * 4] = px[i].r; raw[i * 4 + 1] = px[i].g; raw[i * 4 + 2] = px[i].b; raw[i * 4 + 3] = px[i].a; }
                string f = FileOf(key), tmp = f + ".tmp";
                using (var fs = File.Create(tmp)) using (var z = new DeflateStream(fs, CompressionLevel.Fastest)) z.Write(raw, 0, raw.Length);
                if (File.Exists(f)) File.Delete(f);
                File.Move(tmp, f);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// The icon worker: one background thread that reads icons from the disk cache or renders them, highest priority
    /// (lowest number) first. The main thread asks with Request and collects finished icons with TryTake. The worker
    /// opens the pack itself and loads the pack's language file first (Names).
    /// </summary>
    public sealed class IconStore : IDisposable
    {
        sealed class Job { public string State, Item; public int Prio; public long Seq; }
        readonly object _lock = new object();
        readonly Dictionary<string, Job> _queued = new Dictionary<string, Job>();
        readonly HashSet<string> _done = new HashSet<string>();
        readonly ConcurrentQueue<(string state, Color32[] px)> _out = new ConcurrentQueue<(string, Color32[])>();
        readonly string _packPath;
        readonly IconDiskCache _cache;
        readonly Thread _thread;
        long _seq;
        volatile bool _stop;
        /// <summary>The pack's English names (block.* / item.* keys), once the worker has read them; null without a pack.</summary>
        public volatile Dictionary<string, string> Names;
        public volatile bool NamesReady;
        public volatile string Status = "starting";
        public int Rendered, FromDisk;
        public double RenderMs;

        /// <summary>packPath null or missing: flat-colour cubes. cache null: no disk cache.</summary>
        public IconStore(string packPath, IconDiskCache cache)
        {
            _packPath = packPath; _cache = cache;
            _thread = new Thread(Run) { IsBackground = true, Name = "tabletop-icons", Priority = System.Threading.ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public int Pending { get { lock (_lock) return _queued.Count; } }

        /// <summary>Main thread: asks for an icon (again with a lower number = sooner). Ignored once delivered.</summary>
        public void Request(string state, string item, int priority)
        {
            lock (_lock)
            {
                if (_done.Contains(state)) return;
                if (_queued.TryGetValue(state, out var j)) { if (priority < j.Prio) j.Prio = priority; return; }
                _queued[state] = new Job { State = state, Item = item, Prio = priority, Seq = _seq++ };
                Monitor.Pulse(_lock);
            }
        }

        /// <summary>Main thread: a finished icon (Size x Size, bottom-up), if any.</summary>
        public bool TryTake(out string state, out Color32[] px)
        {
            if (_out.TryDequeue(out var r)) { state = r.state; px = r.px; return true; }
            state = null; px = null;
            return false;
        }

        void Run()
        {
            IconAssets assets = null;
            try
            {
                if (_packPath != null && File.Exists(_packPath)) assets = new IconAssets(_packPath);
                Names = assets?.Language();
                Status = assets == null ? "no pack: flat colours" : "pack icons";
            }
            catch (Exception e) { Status = "pack error: " + e.Message; assets = null; }
            NamesReady = true;
            var renderer = new IconRenderer(assets);
            var sw = new System.Diagnostics.Stopwatch();
            while (!_stop)
            {
                Job job = null;
                lock (_lock)
                {
                    while (!_stop && _queued.Count == 0) Monitor.Wait(_lock, 500);
                    if (_stop) break;
                    foreach (var j in _queued.Values) if (job == null || j.Prio < job.Prio || (j.Prio == job.Prio && j.Seq < job.Seq)) job = j;
                    _queued.Remove(job.State);
                    _done.Add(job.State);
                }
                string key = job.Item + "|" + job.State;
                var px = _cache?.Load(key);
                if (px != null) FromDisk++;
                else
                {
                    sw.Restart();
                    try { px = renderer.Render(job.State, job.Item); } catch (Exception) { px = IconRenderer.Cube(BlockRules.FlatColor(job.State)); }
                    RenderMs += sw.Elapsed.TotalMilliseconds;
                    Rendered++;
                    _cache?.Save(key, px);
                }
                _out.Enqueue((job.State, px));
            }
            assets?.Dispose();
        }

        public void Dispose()
        {
            _stop = true;
            lock (_lock) Monitor.PulseAll(_lock);
        }
    }
}
