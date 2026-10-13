// Builds a Texture2DArray from a USER-SUPPLIED resource pack zip (or the user's own client .jar renamed to .zip),
// copied to Application.persistentDataPath/pack.zip. Nothing from Mojang ships with this app.
// Resolution: blockstates/<name>.json -> first/matching variant model -> models/... parent chain -> element face
// textures ("#var" resolved recursively) -> textures/<path>.png. Animated strips use the first frame.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class ResourcePackAtlas : IFaceSource, IDisposable
    {
        public Texture2DArray Array { get; private set; }
        public int TileSize { get; private set; } = 16;
        public bool HasPack => _zip != null;
        public string Status = "no pack";

        ZipArchive _zip;
        readonly Dictionary<string, int> _layers = new Dictionary<string, int>();       // texture path -> layer
        readonly List<Color32[]> _pixels = new List<Color32[]>();
        readonly Dictionary<string, FaceKey[]> _faceCache = new Dictionary<string, FaceKey[]>();
        readonly Dictionary<string, Dictionary<string, object>> _models = new Dictionary<string, Dictionary<string, object>>();
        bool _arrayDirty;
        static readonly string[] DirNames = { "west", "east", "down", "up", "north", "south" };
        static readonly Color32 GrassTint = new Color32(124, 189, 107, 255), FoliageTint = new Color32(89, 174, 48, 255), WaterTint = new Color32(63, 118, 228, 200);

        public static string DefaultPath => Path.Combine(Application.persistentDataPath, "pack.zip");

        public bool TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) { Status = "no pack at " + path + " (flat colours)"; return false; }
                _zip = ZipFile.OpenRead(path);
                Status = "pack loaded: " + Path.GetFileName(path);
                return true;
            }
            catch (Exception e) { Status = "pack error: " + e.Message; _zip = null; return false; }
        }

        public FaceKey Face(string state, int dir)
        {
            if (!_faceCache.TryGetValue(state, out var faces))
            {
                faces = new FaceKey[6];
                Color32 flat = BlockRules.FlatColor(state);
                for (int i = 0; i < 6; i++) faces[i] = new FaceKey { Layer = -1, Color = flat };
                if (_zip != null) { try { Resolve(state, faces); } catch (Exception e) { Debug.LogWarning($"[Tabletop] texture for {state}: {e.Message}"); } }
                _faceCache[state] = faces;
            }
            return faces[dir];
        }

        void Resolve(string state, FaceKey[] faces)
        {
            string name = BlockRules.Name(state);
            string ns = name.Contains(":") ? name.Substring(0, name.IndexOf(':')) : "minecraft";
            string path = BlockRules.Path(state);
            var bs = ReadJson($"assets/{ns}/blockstates/{path}.json");
            if (bs == null) return;
            string model = null;
            var variants = bs.O("variants");
            if (variants != null)
            {
                string props = state.Contains("[") ? state.Substring(state.IndexOf('[') + 1).TrimEnd(']') : "";
                object pick = null;
                foreach (var kv in variants) { if (pick == null || Matches(props, kv.Key)) { pick = kv.Value; if (Matches(props, kv.Key)) break; } }
                model = ModelName(pick);
            }
            else if (bs.A("multipart") is List<object> mp && mp.Count > 0 && mp[0] is Dictionary<string, object> part && part.TryGetValue("apply", out var apply)) model = ModelName(apply);
            if (model == null) return;

            var textures = new Dictionary<string, string>();
            List<object> elements = null;
            string cur = model;
            for (int depth = 0; depth < 12 && cur != null; depth++)
            {
                var mj = Model(cur);
                if (mj == null) break;
                if (mj.O("textures") is Dictionary<string, object> t) foreach (var kv in t) if (!textures.ContainsKey(kv.Key)) textures[kv.Key] = kv.Value as string;
                if (elements == null && mj.A("elements") is List<object> el) elements = el;
                cur = mj.S("parent");
            }
            if (elements == null || elements.Count == 0)
            {
                // Item-like/cross models (flowers): use "cross"/"texture"/"all" for every face.
                string tex = Lookup(textures, "#cross") ?? Lookup(textures, "#all") ?? Lookup(textures, "#texture");
                if (tex != null) for (int i = 0; i < 6; i++) faces[i] = Key(tex, false, state);
                return;
            }
            var faceDefs = ((Dictionary<string, object>)elements[0]).O("faces");
            if (faceDefs == null) return;
            for (int i = 0; i < 6; i++)
            {
                var f = faceDefs.O(DirNames[i]);
                if (f == null) continue;
                string tex = Lookup(textures, f.S("texture"));
                if (tex != null) faces[i] = Key(tex, f.ContainsKey("tintindex"), state);
            }
        }

        static bool Matches(string props, string key)
        {
            if (key == "") return true;
            foreach (var cond in key.Split(',')) if (!("," + props + ",").Contains("," + cond + ",")) return false;
            return true;
        }

        static string ModelName(object variant)
        {
            if (variant is List<object> l && l.Count > 0) variant = l[0];
            return (variant as Dictionary<string, object>)?.S("model");
        }

        Dictionary<string, object> Model(string id)
        {
            if (id.StartsWith("builtin/")) return null;
            string ns = "minecraft", p = id;
            int c = id.IndexOf(':'); if (c >= 0) { ns = id.Substring(0, c); p = id.Substring(c + 1); }
            string key = ns + ":" + p;
            if (!_models.TryGetValue(key, out var m)) { m = ReadJson($"assets/{ns}/models/{p}.json"); _models[key] = m; }
            return m;
        }

        static string Lookup(Dictionary<string, string> t, string r)
        {
            for (int i = 0; i < 8 && r != null && r.StartsWith("#"); i++) t.TryGetValue(r.Substring(1), out r);
            return r != null && r.StartsWith("#") ? null : r;
        }

        FaceKey Key(string texture, bool tinted, string state)
        {
            int layer = Layer(texture);
            Color32 tint = new Color32(255, 255, 255, 255);
            string p = BlockRules.Path(state);
            if (tinted) tint = p.Contains("leaves") || p.Contains("vine") ? FoliageTint : GrassTint;
            if (p == "water") tint = WaterTint;
            return new FaceKey { Layer = layer, Color = layer < 0 ? BlockRules.FlatColor(state) : tint };
        }

        int Layer(string texture)
        {
            if (_layers.TryGetValue(texture, out int l)) return l;
            string ns = "minecraft", p = texture;
            int c = texture.IndexOf(':'); if (c >= 0) { ns = texture.Substring(0, c); p = texture.Substring(c + 1); }
            var entry = _zip.GetEntry($"assets/{ns}/textures/{p}.png");
            if (entry == null) { _layers[texture] = -1; return -1; }
            byte[] bytes;
            using (var s = entry.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) { UnityEngine.Object.Destroy(tex); _layers[texture] = -1; return -1; }
            if (_pixels.Count == 0) TileSize = tex.width;
            _pixels.Add(FirstFrame(tex, TileSize));
            UnityEngine.Object.Destroy(tex);
            l = _pixels.Count - 1;
            _layers[texture] = l;
            _arrayDirty = true;
            return l;
        }

        static Color32[] FirstFrame(Texture2D t, int size)
        {
            // Nearest-neighbour resample of the top square (first animation frame) to size x size.
            int w = t.width, frame = Mathf.Min(t.width, t.height);
            var src = t.GetPixels32();
            var dst = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int sx = x * frame / size, sy = (t.height - frame) + y * frame / size; // Unity rows are bottom-up
                    dst[y * size + x] = src[sy * w + sx];
                }
            return dst;
        }

        /// <summary>Call after meshing on the main thread: (re)creates the array if new textures were added.</summary>
        public void Commit()
        {
            if (!_arrayDirty || _pixels.Count == 0) return;
            if (Array != null) UnityEngine.Object.Destroy(Array);
            Array = new Texture2DArray(TileSize, TileSize, _pixels.Count, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, anisoLevel = 0 };
            for (int i = 0; i < _pixels.Count; i++) Array.SetPixels32(_pixels[i], i);
            Array.Apply(false, true);
            _arrayDirty = false;
        }

        Dictionary<string, object> ReadJson(string path)
        {
            var e = _zip?.GetEntry(path);
            if (e == null) return null;
            using (var r = new StreamReader(e.Open())) return Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
        }

        public void Dispose() { _zip?.Dispose(); if (Array != null) UnityEngine.Object.Destroy(Array); }
    }
}
