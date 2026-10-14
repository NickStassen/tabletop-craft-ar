// The block menu's icon texture on the headset: a 1024x1024 atlas of 64 px icons (256 slots, least recently shown
// evicted). Icons come from World/BlockIcons.cs's worker (disk cache or CPU render from pack.zip); this class only
// uploads the ones the menu shows, a few per frame: each goes into a small staging texture and is copied into its slot
// on the GPU (Graphics.CopyTexture, 16 KB per icon), so the big atlas is never re-uploaded. Without CopyTexture support
// the atlas stays CPU-readable and is re-applied once per frame that changed it.
using System;
using System.Collections.Generic;
using System.IO;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class BlockIconAtlas : IDisposable
    {
        public const int Tile = IconRenderer.Size, PerRow = 16, Px = Tile * PerRow, Slots = PerRow * PerRow;
        public Texture2D Texture { get; private set; }
        /// <summary>Bumped whenever a slot gets a new icon (the panel re-meshes).</summary>
        public int Version { get; private set; }
        public readonly bool HasPack;
        public int Uploaded;

        readonly IconStore _store;
        readonly Dictionary<string, Color32[]> _pixels = new Dictionary<string, Color32[]>();
        readonly Dictionary<string, int> _slotOf = new Dictionary<string, int>();
        readonly string[] _slotState = new string[Slots];
        readonly int[] _slotFrame = new int[Slots];
        readonly List<(int prio, string state)> _wanted = new List<(int, string)>();
        readonly HashSet<string> _wantedSet = new HashSet<string>();
        readonly Texture2D[] _staging = new Texture2D[8];
        int _stagingNext;
        readonly bool _copy;
        bool _cpuDirty;

        public IconStore Store => _store;
        public string Status => $"icons {(HasPack ? "pack" : "flat")}: {_store.Rendered} rendered, {_store.FromDisk} cached, {_store.Pending} queued, {_slotOf.Count} shown";

        public BlockIconAtlas(string packPath, string cacheRoot)
        {
            HasPack = File.Exists(packPath);
            _store = new IconStore(HasPack ? packPath : null, IconDiskCache.For(cacheRoot, packPath));
            _copy = SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None;
            Texture = new Texture2D(Px, Px, TextureFormat.RGBA32, false) { name = "BlockIcons", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Texture.SetPixels32(new Color32[Px * Px]);
            Texture.Apply(false, _copy);   // GPU-only when slots are filled by CopyTexture
            if (_copy)
                for (int i = 0; i < _staging.Length; i++) _staging[i] = new Texture2D(Tile, Tile, TextureFormat.RGBA32, false) { name = "IconStaging" };
            for (int i = 0; i < Slots; i++) _slotFrame[i] = -1;
        }

        /// <summary>Main thread, every frame for every icon on screen: its UV rect when it's in the atlas. Otherwise it is
        /// asked for (lower priority = sooner) and false comes back (the caller draws a placeholder).</summary>
        public bool Get(string state, string item, int priority, out Rect uv)
        {
            if (_slotOf.TryGetValue(state, out int s)) { _slotFrame[s] = Time.frameCount; uv = UvOf(s); return true; }
            uv = default;
            if (_pixels.ContainsKey(state)) { if (_wantedSet.Add(state)) _wanted.Add((priority, state)); }
            else _store.Request(state, item, priority);
            return false;
        }

        /// <summary>Low-priority requests, so later pages, other tabs and search results are ready (and cached on disk).</summary>
        public void Prefetch(IEnumerable<BlockEntry> entries, int priority)
        {
            foreach (var e in entries) if (!_pixels.ContainsKey(e.State)) _store.Request(e.State, e.Item, priority);
        }

        /// <summary>Main thread, once per frame after the Get calls: collects finished icons, uploads up to maxUploads wanted ones.</summary>
        public void Update(int maxUploads)
        {
            while (_store.TryTake(out var st, out var px)) _pixels[st] = px;
            if (_wanted.Count > 0)
            {
                _wanted.Sort((a, b) => a.prio.CompareTo(b.prio));
                int frame = Time.frameCount, done = 0;
                foreach (var w in _wanted)
                {
                    if (done >= maxUploads) break;
                    int slot = FreeSlot(frame);
                    if (slot < 0) break;
                    if (_slotState[slot] != null) _slotOf.Remove(_slotState[slot]);
                    Upload(slot, _pixels[w.state]);
                    _slotState[slot] = w.state; _slotOf[w.state] = slot; _slotFrame[slot] = frame;
                    done++;
                }
                if (done > 0) { Version++; Uploaded += done; }
                _wanted.Clear(); _wantedSet.Clear();
            }
            if (_cpuDirty) { Texture.Apply(false); _cpuDirty = false; }
        }

        /// <summary>An empty slot, else the one shown longest ago (never one shown this frame); -1 if all are in use.</summary>
        int FreeSlot(int frame)
        {
            int best = -1;
            for (int i = 0; i < Slots; i++)
            {
                if (_slotState[i] == null) return i;
                if (_slotFrame[i] == frame) continue;
                if (best < 0 || _slotFrame[i] < _slotFrame[best]) best = i;
            }
            return best;
        }

        void Upload(int slot, Color32[] px)
        {
            int x = (slot % PerRow) * Tile, y = (slot / PerRow) * Tile;
            if (_copy)
            {
                var st = _staging[_stagingNext++ % _staging.Length];
                st.SetPixels32(px);
                st.Apply(false);
                Graphics.CopyTexture(st, 0, 0, 0, 0, Tile, Tile, Texture, 0, 0, x, y);
            }
            else { Texture.SetPixels32(x, y, Tile, Tile, px); _cpuDirty = true; }
        }

        static Rect UvOf(int slot)
        {
            float x = (slot % PerRow) * Tile, y = (slot / PerRow) * Tile;
            return new Rect((x + 0.5f) / Px, (y + 0.5f) / Px, (Tile - 1f) / Px, (Tile - 1f) / Px);
        }

        public void Dispose()
        {
            _store.Dispose();
            if (Texture) UnityEngine.Object.Destroy(Texture);
            foreach (var s in _staging) if (s) UnityEngine.Object.Destroy(s);
        }
    }
}
