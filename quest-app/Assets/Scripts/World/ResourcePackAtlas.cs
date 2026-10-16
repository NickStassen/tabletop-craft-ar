// Builds a Texture2DArray from a USER-SUPPLIED resource pack zip (or the user's own client .jar renamed to .zip),
// copied to Application.persistentDataPath/pack.zip. Nothing from Mojang ships with this app.
// Resolution: blockstates/<name>.json -> first/matching variant model -> models/... parent chain -> element face
// textures ("#var" resolved recursively) -> textures/<path>.png. Animated textures keep all their frames and are
// advanced by Animate() (ResourcePackAtlas.Animation.cs).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed partial class ResourcePackAtlas : IFaceSource, IDisposable
    {
        public Texture2DArray Array { get; private set; }
        public int TileSize { get; private set; } = 16;
        public bool HasPack => _zip != null;
        public string Status = "no pack";

        ZipArchive _zip;
        readonly Dictionary<string, int> _layers = new Dictionary<string, int>();       // texture path -> layer
        readonly List<Color32[]> _pixels = new List<Color32[]>();
        // Workers read it while the main thread prewarms new palette entries (only the main thread adds).
        readonly ConcurrentDictionary<string, FaceKey[]> _faceCache = new ConcurrentDictionary<string, FaceKey[]>();
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
                SolidLayers.Clear();   // [gpu] this atlas numbers its layers from 0
                Status = "pack loaded: " + Path.GetFileName(path);
                BiomeColormapLoader.Load(_zip); // grass/foliage/dry_foliage colour maps for biome tints
                return true;
            }
            catch (Exception e) { Status = "pack error: " + e.Message; _zip = null; return false; }
        }

        /// <summary>Raw bytes of a file in the pack, or null. Main thread only (used by PlayerSkins for default skins).</summary>
        public byte[] ReadBytes(string path)
        {
            var e = _zip?.GetEntry(path);
            if (e == null) return null;
            using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        public FaceKey Face(string state, int dir)
        {
            if (!_faceCache.TryGetValue(state, out var faces))
            {
                if (OffMainThread) return Unresolved(state);   // see ResourcePackAtlas.Models.cs
                faces = new FaceKey[6];
                Color32 flat = BlockRules.FlatColor(state);
                for (int i = 0; i < 6; i++) faces[i] = new FaceKey { Layer = -1, Color = flat };
                if (_zip != null) { try { Resolve(state, faces); } catch (Exception e) { Debug.LogWarning($"[Tabletop] texture for {state}: {e.Message}"); } }
                PrepareModel(state, faces);   // bake the block model; full cubes get rotated faces (ResourcePackAtlas.Models.cs)
                _faceCache[state] = faces;
            }
            return faces[dir];
        }

        void Resolve(string state, FaceKey[] faces)
        {
            string name = BlockRules.Name(state);
            string ns = name.Contains(":") ? name.Substring(0, name.IndexOf(':')) : "minecraft";
            string path = BlockRules.Path(state);
            if ((path == "water" || path == "lava") && state.Contains("tabletop="))
            {
                // [flow] FluidRules.WaterFlowState/LavaFlowState/OverlayState: textures vanilla's FluidStateModelSet names in code.
                bool overlay = state.Contains("tabletop=overlay");
                string tex = $"{ns}:block/{path}_{(overlay ? "overlay" : "flow")}";
                int quad = overlay ? -1 : QuadLayer(tex);
                var k = quad >= 0 ? Key(tex, false, state, quad) : Key(tex, false, state);
                if (quad >= 0) k.Random = FluidRules.QuadTable;
                for (int i = 0; i < 6; i++) faces[i] = k;
                return;
            }
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
                if (mj.O("textures") is Dictionary<string, object> t) foreach (var kv in t) if (!textures.ContainsKey(kv.Key)) textures[kv.Key] = TextureRef(kv.Value);
                if (elements == null && mj.A("elements") is List<object> el) elements = el;
                cur = mj.S("parent");
            }
            if (elements == null || elements.Count == 0)
            {
                // Item-like/cross models (flowers): use "cross"/"texture"/"all" for every face. Fluid models only name
                // their still texture as "particle".
                string tex = Lookup(textures, "#cross") ?? Lookup(textures, "#all") ?? Lookup(textures, "#texture")
                             ?? (path == "water" || path == "lava" ? Lookup(textures, "#particle") : null);
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
                if (tex != null) faces[i] = Key(tex, f.ContainsKey("tintindex") && BiomeColors.Tinted(state, f.I("tintindex", -1)), state);
            }
        }

        /// <summary>A model texture is a path string, or (26.x) an object like {"sprite": "block/glass", "force_translucent": true}.</summary>
        static string TextureRef(object v) => v as string ?? (v as Dictionary<string, object>)?.S("sprite");

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

        FaceKey Key(string texture, bool tinted, string state, int layer = int.MinValue)
        {
            if (layer == int.MinValue) layer = Layer(texture);
            Color32 tint = new Color32(255, 255, 255, 255);
            string p = BlockRules.Path(state);
            // Tinted faces (model "tintindex") take a biome colour per block in the mesher (BlockShading); Color stays
            // the fixed tint used when the server sends no biomes. Which colour map applies is vanilla's BlockColors.
            var kind = TintKind.None;
            if (tinted || p == "water" || p == "bubble_column")
            {
                kind = BiomeColors.BlockTint(state, out var fixedTint);
                tint = fixedTint ?? (kind == TintKind.Water ? WaterTint : kind == TintKind.Foliage ? FoliageTint : kind == TintKind.None ? tint : GrassTint);
            }
            return new FaceKey { Layer = layer, Color = layer < 0 ? BlockRules.FlatColor(state) : tint, Tint = layer < 0 ? TintKind.None : kind };
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
            _pixels.Add(LoadAnimation(entry.FullName, tex, _pixels.Count) ?? FirstFrame(tex, TileSize));   // animated: all frames kept
            UnityEngine.Object.Destroy(tex);
            l = _pixels.Count - 1;
            PublishSolid(l);   // [gpu]
            _layers[texture] = l;
            _arrayDirty = true;
            return l;
        }

        readonly Dictionary<string, int> _quadLayers = new Dictionary<string, int>();   // [flow] texture -> first of 4 layers, -1 = not split

        /// <summary>[flow] A texture whose frames are twice the tile size (vanilla's 32 px water_flow and lava_flow, of
        /// which a block shows a 16 px part) as four tile-size layers in a row: top-left, top-right, bottom-left,
        /// bottom-right, each animated like the whole. The block shader picks one per 2-block cell (FluidRules.QuadTable),
        /// so the sprite keeps its full resolution. -1 when the texture is missing or not twice the tile size.</summary>
        int QuadLayer(string texture)
        {
            if (_quadLayers.TryGetValue(texture, out int l)) return l;
            l = -1;
            string ns = "minecraft", p = texture;
            int c = texture.IndexOf(':'); if (c >= 0) { ns = texture.Substring(0, c); p = texture.Substring(c + 1); }
            var entry = _pixels.Count == 0 ? null : _zip.GetEntry($"assets/{ns}/textures/{p}.png");
            if (entry != null)
            {
                byte[] bytes;
                using (var s = entry.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(bytes) && tex.width == 2 * TileSize && tex.height >= tex.width)
                {
                    l = _pixels.Count;
                    for (int q = 0; q < 4; q++)
                    {
                        _pixels.Add(LoadAnimation(entry.FullName, tex, _pixels.Count, q) ?? Crop(tex.GetPixels32(), tex.width, tex.height, (q & 1) * TileSize, (q >> 1) * TileSize, TileSize, TileSize, TileSize));
                        PublishSolid(_pixels.Count - 1);   // [gpu]
                    }
                    _arrayDirty = true;
                }
                UnityEngine.Object.Destroy(tex);
            }
            _quadLayers[texture] = l;
            return l;
        }

        /// <summary>[gpu] Tells the meshers (SolidLayers) whether a new layer is opaque in every texel of every animation frame:
        /// its faces then skip the alpha test.</summary>
        void PublishSolid(int layer)
        {
            bool solid = Opaque(_pixels[layer]);
            if (solid) foreach (var f in AnimationFrames(layer)) if (!Opaque(f)) { solid = false; break; }
            SolidLayers.Set(layer, solid);
        }

        static bool Opaque(Color32[] px)
        {
            foreach (var p in px) if (p.a != 255) return false;
            return true;
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

        /// <summary>Mip-mapped, trilinear block textures (TextureMips: vanilla's mip chains, coverage-kept for cut-out
        /// textures); the block shaders still draw texels crisp up close. tabletop.json "mipmaps": false = the old
        /// single level with point sampling. Set before the first Commit.</summary>
        public static bool Mipmaps = true;
        readonly List<Color32[][]> _mips = new List<Color32[][]>();   // per layer: its mip chain (level 0 solidified)
        static readonly int BlocksSizeId = Shader.PropertyToID("_TabletopBlocksSize");

        /// <summary>Bytes of the texture array on the GPU (status / logs).</summary>
        public long ArrayBytes => Array == null ? 0 : (long)Array.depth * TextureMips.Bytes(TileSize, Array.mipmapCount > 1);

        /// <summary>Call after meshing on the main thread: (re)creates the array if new textures were added.</summary>
        public void Commit()
        {
            if (!_arrayDirty || _pixels.Count == 0) return;
            if (_canCopy < 0) _canCopy = (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) != 0 ? 1 : 0;
            // [smooth] New textures while gliding into new terrain: the array has spare layers, so only the new ones are
            // copied in (a staging texture and GPU copies). Re-creating the whole array (every layer and mip set again and
            // uploaded) cost the main thread milliseconds whenever a shift brought a block with a texture not seen yet.
            if (Array != null && _canCopy == 1 && _committed > 0 && _pixels.Count <= Array.depth)
            {
                for (int i = _committed; i < _pixels.Count; i++) CopyLayer(i);
                _committed = _pixels.Count;
                _arrayDirty = false;
                CommitAnimations();
                return;
            }
            if (Array != null) UnityEngine.Object.Destroy(Array);
            int cap = Mathf.Max(_pixels.Count, Mathf.Min(SystemInfo.maxTextureArraySlices, _pixels.Count + Mathf.Max(32, _pixels.Count / 4)));   // spare layers
            Array = new Texture2DArray(TileSize, TileSize, cap, TextureFormat.RGBA32, Mipmaps)
            { filterMode = Mipmaps ? FilterMode.Trilinear : FilterMode.Point, wrapMode = TextureWrapMode.Repeat, anisoLevel = 0 };
            for (int i = 0; i < _pixels.Count; i++)
            {
                if (!Mipmaps) { Array.SetPixels32(_pixels[i], i); continue; }
                for (int k = 0; k < MipsOf(i).Length && k < Array.mipmapCount; k++) Array.SetPixels32(_mips[i][k], i, k);
            }
            Array.Apply(false, true);
            _committed = _pixels.Count;
            Shader.SetGlobalFloat(BlocksSizeId, TileSize);   // texels per block: the shaders keep them crisp when magnified
            _arrayDirty = false;
            Debug.Log($"[Tabletop] ATLAS {_pixels.Count} layers {TileSize}px mips={Array.mipmapCount} {ArrayBytes / 1024} KB");
            CommitAnimations();   // frame store + current frames into the new array
        }

        int _committed;      // [smooth] layers already in Array
        Texture2D _stage;    // one layer, uploaded and GPU-copied into the array (the copies run in order on the render thread)

        Color32[][] MipsOf(int i)
        {
            while (_mips.Count <= i) _mips.Add(null);
            return _mips[i] ?? (_mips[i] = TextureMips.Chain(_pixels[i], TileSize));
        }

        void CopyLayer(int i)
        {
            if (_stage == null) _stage = new Texture2D(TileSize, TileSize, TextureFormat.RGBA32, Mipmaps) { filterMode = FilterMode.Point };
            if (!Mipmaps) { _stage.SetPixels32(_pixels[i]); _stage.Apply(false, false); Graphics.CopyTexture(_stage, 0, 0, Array, i, 0); return; }
            var mips = MipsOf(i);
            int n = Mathf.Min(mips.Length, Mathf.Min(Array.mipmapCount, _stage.mipmapCount));
            for (int k = 0; k < n; k++) _stage.SetPixels32(mips[k], k);
            _stage.Apply(false, false);
            for (int k = 0; k < n; k++) Graphics.CopyTexture(_stage, 0, k, Array, i, k);
        }

        Dictionary<string, object> ReadJson(string path)
        {
            var e = _zip?.GetEntry(path);
            if (e == null) return null;
            using (var r = new StreamReader(e.Open())) return Json.Parse(r.ReadToEnd()) as Dictionary<string, object>;
        }

        public void Dispose() { DisposeAnimations(); _zip?.Dispose(); if (Array != null) UnityEngine.Object.Destroy(Array); if (_stage != null) UnityEngine.Object.Destroy(_stage); }
    }
}
