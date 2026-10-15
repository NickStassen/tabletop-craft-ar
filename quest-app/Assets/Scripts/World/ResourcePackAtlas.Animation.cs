// Animated block textures (water, lava, kelp, seagrass, fire, prismarine, magma, sea lantern, nether portal, ...).
// Layer() keeps every frame of a texture whose .png.mcmeta has an animation section (TextureAnimation parses it).
// Commit() packs those frames into a second Texture2DArray, the frame store, which shaders never sample. Animate()
// advances the animations at 20 game ticks per second. When a texture's frame changes, it GPU-copies that one
// 16x16 frame from the store into the texture's atlas layer (Graphics.CopyTexture, about 1 KB). Interpolated
// animations (prismarine, magma, command blocks, sculk, ...) are blended on the CPU into a one-layer staging array,
// uploaded (1 KB) and copied the same way. Nothing re-uploads the atlas per tick, and meshes and shaders don't
// change (the layer index stays the same). Main thread only.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabletopCraft.World
{
    public sealed partial class ResourcePackAtlas
    {
        sealed class Anim
        {
            public int Layer, Store = -1;      // atlas layer; first element in the frame store (-1 = not stored)
            public TextureAnimation Meta;
            public Color32[][] Frames;         // per image frame, TileSize x TileSize, Unity row order
            public Color32[][][] FrameMips;    // per image frame: its mip chain (TextureMips), when the atlas has mips
            public int Shown = -1, ShownNext = -1, ShownBlend;   // what the atlas layer holds now
            public Texture2DArray Staging;     // interpolated animations only
            public Color32[][] Mix;            // per mip level
        }

        readonly List<Anim> _anims = new List<Anim>();
        Texture2DArray _frameStore;
        bool _framesDirty;
        long _tick = -1;
        int _canCopy = -1;   // -1 not checked yet, 0 no (first frames only), 1 yes

        /// <summary>Animated textures loaded, and layer copies issued so far (profiling / status).</summary>
        public int AnimatedTextures => _anims.Count;
        public long FrameCopies { get; private set; }

        /// <summary>From Layer() for each new texture: if it is animated, keeps all its frames and returns the pixels
        /// for its atlas layer (the first frame shown); otherwise null. part 0-3: only that quarter of each frame
        /// (top-left, top-right, bottom-left, bottom-right; [flow] QuadLayer), -1 = the whole frame.</summary>
        Color32[] LoadAnimation(string pngEntry, Texture2D tex, int layer, int part = -1)
        {
            try
            {
                var meta = _zip.GetEntry(pngEntry + ".mcmeta");
                if (meta == null) return null;
                string json;
                using (var r = new System.IO.StreamReader(meta.Open())) json = r.ReadToEnd();
                var a = TextureAnimation.Parse(json, tex.width, tex.height);
                if (a == null) return null;
                var src = tex.GetPixels32();
                var frames = new Color32[a.ImageFrames][];
                for (int i = 0; i < frames.Length; i++)
                {
                    a.Origin(i, out int fx, out int fy);
                    int fw = a.FrameWidth, fh = a.FrameHeight;
                    if (part >= 0) { fw /= 2; fh /= 2; fx += (part & 1) * fw; fy += (part >> 1) * fh; }
                    frames[i] = Crop(src, tex.width, tex.height, fx, fy, fw, fh, TileSize);
                }
                Color32[][][] mips = null;
                if (Mipmaps) { mips = new Color32[frames.Length][][]; for (int i = 0; i < frames.Length; i++) mips[i] = TextureMips.Chain(frames[i], TileSize); }
                _anims.Add(new Anim { Layer = layer, Meta = a, Frames = frames, FrameMips = mips });
                _framesDirty = true;
                return frames[a.Frames[0].Index];
            }
            catch (System.Exception e) { Debug.LogWarning($"[Tabletop] animation {pngEntry}: {e.Message}"); return null; }
        }

        /// <summary>Nearest-neighbour resample of one frame (image coordinates, y down) to size x size, Unity rows (bottom-up).</summary>
        static Color32[] Crop(Color32[] src, int w, int h, int fx, int fy, int fw, int fh, int size)
        {
            var dst = new Color32[size * size];
            int bottom = h - fy - fh;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    dst[y * size + x] = src[(bottom + y * fh / size) * w + fx + x * fw / size];
            return dst;
        }

        /// <summary>From Commit() after the atlas was (re)built with first frames: (re)builds the frame store if
        /// textures were added and shows the current frames again.</summary>
        void CommitAnimations()
        {
            if (_anims.Count == 0) return;
            if (_canCopy < 0)
            {
                _canCopy = (SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) != 0 ? 1 : 0;
                if (_canCopy == 0) Debug.LogWarning("[Tabletop] ANIM off: Graphics.CopyTexture unsupported, showing first frames");
            }
            if (_canCopy == 0) return;
            if (_framesDirty)
            {
                int total = 0, max = SystemInfo.maxTextureArraySlices;
                foreach (var a in _anims)
                {
                    if (total + a.Frames.Length > max) { a.Store = -1; continue; }   // over the GPU's array limit: stays static
                    a.Store = total;
                    total += a.Frames.Length;
                }
                if (_frameStore != null) Object.Destroy(_frameStore);
                _frameStore = null;
                if (total > 0)
                {
                    _frameStore = new Texture2DArray(TileSize, TileSize, total, TextureFormat.RGBA32, Mipmaps) { filterMode = FilterMode.Point, anisoLevel = 0 };
                    foreach (var a in _anims)
                        if (a.Store >= 0)
                            for (int i = 0; i < a.Frames.Length; i++)
                            {
                                if (a.FrameMips == null) { _frameStore.SetPixels32(a.Frames[i], a.Store + i); continue; }
                                for (int k = 0; k < a.FrameMips[i].Length && k < _frameStore.mipmapCount; k++) _frameStore.SetPixels32(a.FrameMips[i][k], a.Store + i, k);
                            }
                    _frameStore.Apply(false, true);
                }
                _framesDirty = false;
                Debug.Log($"[Tabletop] ANIM {_anims.Count} animated textures, {total} frames ({(long)total * TextureMips.Bytes(TileSize, Mipmaps) / 1024} KB frame store)");
            }
            foreach (var a in _anims) a.Shown = -1;   // the new atlas holds first frames
            if (_tick >= 0) Step(_tick);
        }

        /// <summary>Main thread, once per rendered frame: advances the animations to the game tick at this time.</summary>
        public void Animate(double seconds)
        {
            long tick = (long)(seconds * TextureAnimation.TicksPerSecond);
            if (tick == _tick) return;
            _tick = tick;
            Step(tick);
        }

        void Step(long tick)
        {
            if (Array == null || _frameStore == null || _canCopy != 1) return;
            foreach (var a in _anims)
            {
                if (a.Store < 0 || a.Layer >= Array.depth) continue;
                a.Meta.At(tick, out int f, out int next, out float blend);
                int b = (int)(blend * 255f);   // 1/255 steps: as fine as RGBA32 can show
                // Every mip level of the frame (TextureMips chains in the store), so distant animated blocks animate too.
                int mips = Mathf.Min(_frameStore.mipmapCount, Array.mipmapCount);
                if (b == 0)
                {
                    if (a.Shown == f && a.ShownBlend == 0) continue;
                    for (int k = 0; k < mips; k++) Graphics.CopyTexture(_frameStore, a.Store + f, k, Array, a.Layer, k);
                }
                else
                {
                    if (a.Shown == f && a.ShownNext == next && a.ShownBlend == b) continue;
                    Blend(a, f, next, b);
                    for (int k = 0; k < Mathf.Min(mips, a.Staging.mipmapCount); k++) Graphics.CopyTexture(a.Staging, 0, k, Array, a.Layer, k);
                }
                a.Shown = f; a.ShownNext = next; a.ShownBlend = b;
                FrameCopies++;
            }
        }

        /// <summary>Vanilla interpolation: RGB mixed toward the next frame, alpha of the current one (every mip level:
        /// the levels of the two frames' chains are mixed the same way).</summary>
        void Blend(Anim a, int f, int next, int b)
        {
            bool mips = a.FrameMips != null;
            int levels = mips ? a.FrameMips[f].Length : 1;
            if (a.Staging == null)
            {
                a.Staging = new Texture2DArray(TileSize, TileSize, 1, TextureFormat.RGBA32, mips) { filterMode = FilterMode.Point, anisoLevel = 0 };
                levels = Mathf.Min(levels, a.Staging.mipmapCount);
                a.Mix = new Color32[levels][];
                for (int k = 0; k < levels; k++) a.Mix[k] = new Color32[(mips ? a.FrameMips[f][k] : a.Frames[f]).Length];
            }
            levels = a.Mix.Length;
            int ib = 255 - b;
            for (int k = 0; k < levels; k++)
            {
                Color32[] c = mips ? a.FrameMips[f][k] : a.Frames[f], d = mips ? a.FrameMips[next][k] : a.Frames[next], mix = a.Mix[k];
                for (int i = 0; i < mix.Length; i++)
                    mix[i] = new Color32((byte)((c[i].r * ib + d[i].r * b) / 255), (byte)((c[i].g * ib + d[i].g * b) / 255), (byte)((c[i].b * ib + d[i].b * b) / 255), c[i].a);
                a.Staging.SetPixels32(mix, 0, k);
            }
            a.Staging.Apply(false, false);
        }

        void DisposeAnimations()
        {
            if (_frameStore != null) Object.Destroy(_frameStore);
            foreach (var a in _anims) if (a.Staging != null) Object.Destroy(a.Staging);
            _anims.Clear();
            _frameStore = null;
        }
    }
}
