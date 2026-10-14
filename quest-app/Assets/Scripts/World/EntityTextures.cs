// Textures and materials for entities, items and dig cracks, read from the user's pack.zip (ResourcePackAtlas.ReadBytes)
// on the main thread and cached: entity skins (with overlays such as villager type/profession or horse markings
// composited in), item sprites (first animation frame, tinted layer0 + layer1), and 6-face strips for block-like items.
// One material per texture (TabletopEntity.mat's shader, GPU instancing on), so objects sharing a mesh and texture
// batch; per-object colours go through MaterialPropertyBlocks.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class EntityTextures : IDisposable
    {
        public ResourcePackAtlas Atlas;
        public static readonly int ColorId = Shader.PropertyToID("_Color"), OverlayId = Shader.PropertyToID("_Overlay"), MainTexId = Shader.PropertyToID("_MainTex");
        // Fixed biome-ish tints for tinted item faces (grass blocks, leaves) like the atlas uses without biome data.
        static readonly Color32 GrassTint = new Color32(124, 189, 107, 255);

        readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        readonly Dictionary<(Texture, int), Material> _mats = new Dictionary<(Texture, int), Material>();
        readonly Dictionary<string, ItemVisual> _items = new Dictionary<string, ItemVisual>();
        Material _base;
        Texture2D _white;

        public Material Base
        {
            get
            {
                if (_base != null) return _base;
                _base = Resources.Load<Material>("TabletopEntity");
                if (_base == null)
                {
                    Debug.LogWarning("[Tabletop] Resources/TabletopEntity material missing; entities fall back to Unlit/Texture");
                    _base = new Material(Shader.Find("Unlit/Texture"));
                }
                return _base;
            }
        }

        public Texture2D White
        {
            get
            {
                if (_white == null)
                {
                    _white = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "entity white", filterMode = FilterMode.Point };
                    _white.SetPixel(0, 0, Color.white);
                    _white.Apply(false, true);
                }
                return _white;
            }
        }

        /// <summary>A material for a texture: flags 1 = translucent (blended, no depth write), 2 = flat (no entity shading).</summary>
        public Material Material(Texture tex, int flags = 0)
        {
            if (tex == null) tex = White;
            if (_mats.TryGetValue((tex, flags), out var m)) return m;
            m = new Material(Base) { name = "entity " + tex.name, mainTexture = tex, enableInstancing = true };
            if ((flags & 1) != 0)
            {
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0);
                m.SetFloat("_Cutoff", 0.01f);
                m.renderQueue = 3000;
            }
            if ((flags & 2) != 0) m.SetFloat("_Shade", 0);
            _mats[(tex, flags)] = m;
            return m;
        }

        /// <summary>The pack texture "entity/cow/cow_temperate" (or "minecraft:item/x") with overlays drawn over it; null if missing.</summary>
        public Texture2D Get(string texture, List<string> overlays = null, Color32? tint = null)
        {
            if (string.IsNullOrEmpty(texture)) return null;
            string key = texture + (overlays == null ? "" : "+" + string.Join("+", overlays)) + (tint.HasValue ? "#" + tint.Value.r + "," + tint.Value.g + "," + tint.Value.b : "");
            if (_tex.TryGetValue(key, out var t)) return t;
            var px = Load(texture, out int w, out int h, false);
            if (px != null)
            {
                if (tint.HasValue) Tint(px, tint.Value);
                if (overlays != null)
                    foreach (var o in overlays)
                    {
                        var op = Load(o, out int ow, out int oh, false);
                        if (op != null) Over(px, w, h, op, ow, oh);
                    }
                t = Make(px, w, h, key);
            }
            _tex[key] = t;   // null is cached too: a missing texture is looked up once
            return t;
        }

        /// <summary>An item sprite: layer0 (tinted) with layer1 over it, first animation frame; null if missing.</summary>
        public Texture2D Sprite(ItemVisual v)
        {
            string key = v.Key;
            if (_tex.TryGetValue(key, out var t)) return t;
            var px = Load(v.Sprite, out int w, out int h, true);
            if (px != null)
            {
                Tint(px, v.SpriteTint);
                if (v.Overlay != null) { var op = Load(v.Overlay, out int ow, out int oh, true); if (op != null) Over(px, w, h, op, ow, oh); }
                t = Make(px, w, h, key);
            }
            _tex[key] = t;
            return t;
        }

        /// <summary>A block-like item's six faces side by side (down, up, north, south, west, east), each N x N.</summary>
        public Texture2D Strip(ItemVisual v)
        {
            string key = v.Key;
            if (_tex.TryGetValue(key, out var t)) return t;
            const int n = 16;
            var strip = new Color32[6 * n * n];
            bool any = false;
            for (int f = 0; f < 6; f++)
            {
                var px = Load(v.Faces[f], out int w, out int h, true);
                if (px == null) continue;
                any = true;
                if (v.Tinted[f]) Tint(px, GrassTint);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        strip[y * 6 * n + f * n + x] = px[(y * h / n) * w + x * w / n];
            }
            t = any ? Make(strip, 6 * n, n, key) : null;
            _tex[key] = t;
            return t;
        }

        readonly Dictionary<string, Color32?> _averages = new Dictionary<string, Color32?>();

        /// <summary>The average colour of a pack texture's opaque pixels (for entities drawn as boxes); null if missing.</summary>
        public Color32? Average(string texture)
        {
            if (string.IsNullOrEmpty(texture)) return null;
            if (_averages.TryGetValue(texture, out var c)) return c;
            var px = Load(texture, out _, out _, false);
            long r = 0, g = 0, b = 0, n = 0;
            if (px != null)
                foreach (var p in px)
                {
                    if (p.a < 128) continue;
                    r += p.r; g += p.g; b += p.b; n++;
                }
            c = n > 0 ? new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255) : (Color32?)null;
            _averages[texture] = c;
            return c;
        }

        /// <summary>Item visuals by id, resolved from the pack once (main thread).</summary>
        public ItemVisual Item(string id)
        {
            if (string.IsNullOrEmpty(id)) return new ItemVisual();
            if (_items.TryGetValue(id, out var v)) return v;
            var assets = Atlas as IModelAssets;
            v = Atlas != null && Atlas.HasPack ? ItemModels.Resolve(id, assets.ReadJson) : new ItemVisual { Item = id };
            _items[id] = v;
            return v;
        }

        /// <summary>Pixels (Unity rows, bottom-up) of a pack texture; square sprites keep only the top frame of an animation.</summary>
        Color32[] Load(string texture, out int w, out int h, bool firstFrame)
        {
            w = h = 0;
            if (Atlas == null || !Atlas.HasPack || string.IsNullOrEmpty(texture)) return null;
            byte[] png;
            try { png = Atlas.ReadBytes(ItemModels.TexturePath(texture)); } catch (Exception) { return null; }
            if (png == null) return null;
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!src.LoadImage(png)) return null;
                w = src.width; h = src.height;
                var px = src.GetPixels32();
                if (firstFrame && h > w)
                {
                    // Animated (frames stacked downwards): the top frame is the last w rows in Unity's bottom-up order.
                    var f = new Color32[w * w];
                    Array.Copy(px, (h - w) * w, f, 0, w * w);
                    h = w;
                    return f;
                }
                return px;
            }
            finally { UnityEngine.Object.Destroy(src); }
        }

        static void Tint(Color32[] px, Color32 c)
        {
            if (c.r == 255 && c.g == 255 && c.b == 255) return;
            for (int i = 0; i < px.Length; i++)
            {
                var p = px[i];
                px[i] = new Color32((byte)(p.r * c.r / 255), (byte)(p.g * c.g / 255), (byte)(p.b * c.b / 255), p.a);
            }
        }

        /// <summary>Alpha-blends an overlay over the base (nearest sample when the sizes differ).</summary>
        static void Over(Color32[] b, int w, int h, Color32[] o, int ow, int oh)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var s = o[(y * oh / h) * ow + x * ow / w];
                    if (s.a == 0) continue;
                    int i = y * w + x;
                    var d = b[i];
                    float a = s.a / 255f;
                    b[i] = new Color32((byte)(s.r * a + d.r * (1 - a)), (byte)(s.g * a + d.g * (1 - a)), (byte)(s.b * a + d.b * (1 - a)), (byte)Math.Max(d.a, s.a));
                }
        }

        static Texture2D Make(Color32[] px, int w, int h, string name)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        public void Dispose()
        {
            foreach (var t in _tex.Values) if (t != null) UnityEngine.Object.Destroy(t);
            foreach (var m in _mats.Values) if (m != null) UnityEngine.Object.Destroy(m);
            if (_white != null) UnityEngine.Object.Destroy(_white);
            _tex.Clear(); _mats.Clear();
        }
    }
}
