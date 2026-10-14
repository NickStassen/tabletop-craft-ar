// Skin textures for the player figures, cached per URL / pack path. Downloads run through UnityWebRequestTexture
// (asynchronous, never blocks a frame); until one finishes, a figure wears the default skin the game would show
// for its UUID, read from the user's pack.zip, or a plain-colour skin when there is no pack (no Mojang assets ship
// with the app). Every texture is converted like the game does (SkinImage.Process), point-filtered, no mipmaps.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace TabletopCraft.World
{
    public sealed class PlayerSkins : IDisposable
    {
        public ResourcePackAtlas Atlas;

        readonly Dictionary<string, Texture2D> _tex = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, List<Action<Texture2D>>> _waiting = new Dictionary<string, List<Action<Texture2D>>>();
        readonly HashSet<string> _failed = new HashSet<string>();
        bool _disposed;

        /// <summary>The skin the game shows for a player without textures (by UUID), from the pack, else plain colours.</summary>
        public Texture2D DefaultFor(string uuid, out bool slim)
        {
            string path = SkinImage.DefaultSkinPath(uuid, out slim);
            var t = FromPack(path);
            if (t == null && path != SkinImage.SteveWidePath) { slim = false; t = FromPack(SkinImage.SteveWidePath); }
            if (t != null) return t;
            slim = false;
            int variant = SkinImage.PlainVariant(uuid);
            string key = "plain:" + variant;
            if (!_tex.TryGetValue(key, out t)) { t = ToTexture(SkinImage.PlainColours(variant), "plain skin " + variant); _tex[key] = t; }
            return t;
        }

        Texture2D FromPack(string path)
        {
            string key = "pack:" + path;
            if (_tex.TryGetValue(key, out var t)) return t;
            if (_failed.Contains(key) || Atlas == null || !Atlas.HasPack) return null;
            byte[] png = Atlas.ReadBytes(path);
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            t = png != null && src.LoadImage(png) ? Convert(src, path) : null;
            UnityEngine.Object.Destroy(src);
            if (t == null) { _failed.Add(key); return null; }
            _tex[key] = t;
            return t;
        }

        /// <summary>
        /// Calls done with the processed skin from a player_positions URL: now if cached, later when downloaded, never
        /// if the URL isn't a textures.minecraft.net skin or the download fails (the figure keeps its default skin).
        /// </summary>
        public void Fetch(string url, Action<Texture2D> done)
        {
            string u = SkinImage.NormalizeUrl(url);
            if (u == null || _failed.Contains(u)) return;
            if (_tex.TryGetValue(u, out var t)) { done(t); return; }
            if (_waiting.TryGetValue(u, out var list)) { list.Add(done); return; }
            _waiting[u] = new List<Action<Texture2D>> { done };
            var req = UnityWebRequestTexture.GetTexture(u, false);
            req.timeout = 20;
            req.SendWebRequest().completed += _ => OnDownloaded(u, req);
        }

        void OnDownloaded(string url, UnityWebRequest req)
        {
            Texture2D t = null;
            try
            {
                if (!_disposed && req.result == UnityWebRequest.Result.Success)
                {
                    var src = DownloadHandlerTexture.GetContent(req);
                    t = Convert(src, url);
                    UnityEngine.Object.Destroy(src);
                }
                else Debug.LogWarning($"[Tabletop] skin {url}: {req.error}");
            }
            catch (Exception e) { Debug.LogWarning($"[Tabletop] skin {url}: {e.Message}"); }
            finally { req.Dispose(); }
            _waiting.TryGetValue(url, out var waiters);
            _waiting.Remove(url);
            if (_disposed) { if (t != null) UnityEngine.Object.Destroy(t); return; }
            if (t == null) { _failed.Add(url); return; }
            _tex[url] = t;
            Debug.Log($"[Tabletop] skin loaded {t.name}");
            if (waiters != null) foreach (var w in waiters) w(t);
        }

        /// <summary>Readable source texture -> processed 64x64 skin texture, or null if it isn't a skin.</summary>
        static Texture2D Convert(Texture2D src, string what)
        {
            int w = src.width, h = src.height;
            var px = src.GetPixels32();
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[(h - 1 - y) * w + x]; // Unity rows are bottom-up
                    int i = (y * w + x) * 4;
                    rgba[i] = c.r; rgba[i + 1] = c.g; rgba[i + 2] = c.b; rgba[i + 3] = c.a;
                }
            var img = SkinImage.Process(rgba, w, h);
            if (img == null) { Debug.LogWarning($"[Tabletop] skin {what}: unsupported size {w}x{h}"); return null; }
            return ToTexture(img, h == 32 ? what + " (legacy 64x32)" : what);
        }

        static Texture2D ToTexture(byte[] topDown, string what)
        {
            const int n = SkinImage.Size;
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = (y * n + x) * 4;
                    px[(n - 1 - y) * n + x] = new Color32(topDown[i], topDown[i + 1], topDown[i + 2], topDown[i + 3]);
                }
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = what, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (var t in _tex.Values) if (t != null) UnityEngine.Object.Destroy(t);
            _tex.Clear();
            _waiting.Clear();
        }
    }
}
