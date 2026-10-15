// [motion] Ambient particles on the table (AmbientParticles.cs has the rules): torch and candle flames, smoke, campfire
// smoke, lava pops, the portal swirl, falling leaves, fireflies, bubble columns and drips. A worker scans the visible
// window (and 4 blocks around it) for emitters when the snapshot changes, blocks change (at most twice a second) or the
// view glides more than 8 blocks; the main thread turns the emitters into one static mesh of looping slots, capped at
// MaxParticles and thinned with the window's width (AmbientRules.Density), that the TabletopCraft/Particles shader
// animates. Sprites: the pack's textures/particle/*.png in a 16x16 texture array (nothing without a pack).
// tabletop.json "particles" (on) and "particle_density" (1). Cost: one draw call of up to 3,000 small quads.
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class AmbientParticleRenderer : MonoBehaviour
    {
        public WorldModel Model;
        public ResourcePackAtlas Atlas;
        public WorldLight Light;
        public NavState Nav;
        public float BlockSizeM = 0.01f;
        public int MaxParticles = 3000;
        /// <summary>tabletop.json "particles": ambient particles on the table (default on).</summary>
        public static bool Enabled = true;
        /// <summary>tabletop.json "particle_density": 1 = vanilla's rate near the player, 0.5 half as many...</summary>
        public static float DensityScale = 1f;
        public int Particles { get; private set; }
        public int Emitters { get; private set; }

        static readonly int TimeId = Shader.PropertyToID("_ParticleTime"), FirefliesId = Shader.PropertyToID("_Fireflies"), TexId = Shader.PropertyToID("_ParticleTex");
        Mesh _mesh;
        MeshRenderer _renderer;
        Material _mat;
        Texture2DArray _tex;
        bool _texTried;
        int _builtSnapshot = -1, _builtEdits = -1, _scanSnapshot, _scanEdits;
        Vector3 _builtView, _scanView;
        float _nextCheck, _scanWidth;
        Task<List<Emitter>> _scan;

        void Start()
        {
            var go = new GameObject("particles");
            go.transform.SetParent(transform, false);
            _mesh = new Mesh { name = "ambient particles", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            var baseMat = Resources.Load<Material>("TabletopParticles");
            if (baseMat == null) Debug.LogWarning("[Tabletop] Resources/TabletopParticles material missing: no ambient particles");
            _mat = baseMat != null ? new Material(baseMat) : null;
            _renderer.sharedMaterial = _mat;
            EntityFigure.Quiet(_renderer);
            _renderer.enabled = false;
        }

        void Update()
        {
            bool on = Enabled && _mat != null && Model != null && Model.Loaded && Atlas != null && Atlas.HasPack && Sprites();
            _renderer.enabled = on && Particles > 0;
            if (!on) return;
            transform.localScale = Vector3.one * BlockSizeM;   // region-local blocks (Z mirrored in the shader), like Weather
            _mat.SetFloat(TimeId, Time.timeSinceLevelLoad);
            // Fireflies need the bush's light at most 13 (FireflyBushBlock): dusk and night.
            float sky = Light != null && Light.Clock.Known ? Light.Current.SkyFactor : 1f;
            _mat.SetFloat(FirefliesId, Mathf.Clamp01((0.88f - sky) * 8f));
            UpdateBursts();   // [motion] explosions

            if (_scan != null && _scan.IsCompleted)
            {
                var list = _scan.Status == TaskStatus.RanToCompletion ? _scan.Result : null;
                if (_scan.IsFaulted) Debug.LogWarning("[Tabletop] particles: scan failed: " + _scan.Exception?.GetBaseException().Message);
                _scan = null;
                if (list != null && _scanSnapshot == Model.SnapshotId) Build(list);
                else _builtSnapshot = -1;   // stale (a new snapshot came meanwhile): scan again
            }
            if (_scan == null && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 0.5f;   // block edits batch up for half a second
                var view = Nav != null && Nav.Loaded ? Nav.View : Vector3.zero;
                if (Model.SnapshotId != _builtSnapshot || Model.Edits != _builtEdits || (view - _builtView).sqrMagnitude > 64f) StartScan(view);
            }
        }

        void StartScan(Vector3 view)
        {
            Vector3Int min = Vector3Int.zero, max = Model.Dims;
            float width = Mathf.Max(Model.Dims.x, Model.Dims.z);
            if (Nav != null && Nav.Loaded)
            {
                var wmin = Nav.WindowMin - (Vector3)Model.Origin;
                var win = Nav.Window;
                min = new Vector3Int(Mathf.FloorToInt(wmin.x) - 4, Mathf.FloorToInt(wmin.y) - 4, Mathf.FloorToInt(wmin.z) - 4);
                max = min + win + new Vector3Int(8, 8, 8);
                width = Mathf.Max(win.x, win.z);
            }
            // Main thread: the palette's table (string work, the template cache); the worker only reads arrays.
            var palette = Model.Palette.ToArray();
            var table = AmbientRules.Table(palette);
            var w = Model;
            IFaceSource faces = Atlas;
            _scanSnapshot = Model.SnapshotId; _scanEdits = Model.Edits; _scanView = view; _scanWidth = width;
            _scan = Task.Run(() =>
            {
                var list = new List<Emitter>();
                AmbientRules.Scan(w, table, palette, faces, min, max, list);
                return list;
            });
        }

        void Build(List<Emitter> emitters)
        {
            _builtSnapshot = _scanSnapshot; _builtEdits = _scanEdits; _builtView = _scanView;
            var slots = AmbientRules.Plan(emitters, AmbientRules.Density(_scanWidth, DensityScale), MaxParticles);
            Emitters = emitters.Count;
            Particles = slots.Count;
            var q = new Quads(slots.Count);
            foreach (var s in slots)
            {
                var e = emitters[s.Emitter];
                var k = e.Kind;
                q.Add(e.Pos, k.Motion, s.Seed, new Vector4(s.Period, s.Chance, k.Life, k.Size), new Vector4(e.Spread.x, e.Spread.y, e.Spread.z, e.Reach),
                      new Vector4(k.Sprite, k.Sprites, e.Light, k.Bright ? 1 : 0), e.Color);
            }
            // The shader moves particles: leaves and drips fall up to FallCap, smoke and fireflies rise and wander.
            q.Apply(_mesh, new Vector3(6, AmbientRules.FallCap + 2, 6), new Vector3(6, 12, 6));
        }

        /// <summary>Quads for the particle shader (every corner of a slot holds the same data), and their mesh.</summary>
        sealed class Quads
        {
            readonly List<Vector3> _v; readonly List<Vector4> _a, _b, _c, _d; readonly List<Color32> _col; readonly List<int> _t;
            Vector3 _lo = Vector3.one * float.MaxValue, _hi = Vector3.one * float.MinValue;
            public Quads(int n)
            {
                _v = new List<Vector3>(n * 4); _a = new List<Vector4>(n * 4); _b = new List<Vector4>(n * 4); _c = new List<Vector4>(n * 4); _d = new List<Vector4>(n * 4);
                _col = new List<Color32>(n * 4); _t = new List<int>(n * 6);
            }

            public void Add(Vector3 pos, ParticleMotion motion, float seed, Vector4 b, Vector4 c, Vector4 d, Color32 color)
            {
                int v0 = _v.Count;
                for (int i = 0; i < 4; i++)
                {
                    _v.Add(pos);
                    _a.Add(new Vector4(i == 0 || i == 1 ? -0.5f : 0.5f, i == 1 || i == 2 ? 0.5f : -0.5f, (float)motion, seed));
                    _b.Add(b); _c.Add(c); _d.Add(d); _col.Add(color);
                }
                _t.Add(v0); _t.Add(v0 + 1); _t.Add(v0 + 2); _t.Add(v0); _t.Add(v0 + 2); _t.Add(v0 + 3);
                _lo = Vector3.Min(_lo, pos); _hi = Vector3.Max(_hi, pos);
            }

            /// <summary>Fills the mesh; bounds around the spawn points grown by how far particles travel (Z mirrored).</summary>
            public void Apply(Mesh m, Vector3 down, Vector3 up)
            {
                m.Clear();
                if (_v.Count == 0) return;
                m.SetVertices(_v);
                m.SetUVs(0, _a); m.SetUVs(1, _b); m.SetUVs(2, _c); m.SetUVs(3, _d);
                m.SetColors(_col);
                m.SetTriangles(_t, 0, false);
                Vector3 lo = _lo - down, hi = _hi + up;
                m.bounds = new Bounds(new Vector3((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, -(lo.z + hi.z) / 2), hi - lo);
            }
        }

        // ---------- [motion] explosions (the explode action): one-shot clouds and a flash in a second, small mesh ----------

        struct Burst { public Vector3 Pos; public float Power, Start; }
        readonly List<Burst> _bursts = new List<Burst>();
        Mesh _burstMesh;
        MeshRenderer _burstRenderer;
        bool _burstDirty;
        public const int MaxBursts = 8;

        /// <summary>An explosion at a world position (PROTOCOL.md "Block animations").</summary>
        public void Explode(Vector3 world, float power)
        {
            if (!Enabled || Model == null || !Model.Loaded) return;
            if (_bursts.Count >= MaxBursts) _bursts.RemoveAt(0);
            _bursts.Add(new Burst { Pos = world - (Vector3)Model.Origin, Power = power, Start = Time.timeSinceLevelLoad });
            _burstDirty = true;
        }

        void UpdateBursts()
        {
            if (_burstMesh == null)
            {
                var go = new GameObject("explosions");
                go.transform.SetParent(transform, false);
                _burstMesh = new Mesh { name = "explosions" };
                _burstMesh.MarkDynamic();
                go.AddComponent<MeshFilter>().sharedMesh = _burstMesh;
                _burstRenderer = go.AddComponent<MeshRenderer>();
                _burstRenderer.sharedMaterial = _mat;
                EntityFigure.Quiet(_burstRenderer);
            }
            float now = Time.timeSinceLevelLoad;
            for (int i = _bursts.Count - 1; i >= 0; i--) if (now - _bursts[i].Start > 1.5f) { _bursts.RemoveAt(i); _burstDirty = true; }
            _burstRenderer.enabled = _bursts.Count > 0;
            if (!_burstDirty) return;
            _burstDirty = false;
            var q = new Quads(_bursts.Count * 49);
            var white = new Color32(255, 255, 255, 255);
            foreach (var bu in _bursts)
            {
                var k = ParticleKind.Explosion;
                int n = ParticleKind.ExplosionClouds(bu.Power);
                float spread = ParticleKind.ExplosionSpread(bu.Power), size = k.Size * Mathf.Clamp(bu.Power / 4f, 0.5f, 1.5f);
                for (int i = 0; i < n; i++)   // HugeExplosionSeedParticle: 6 a tick for 8 ticks
                    q.Add(bu.Pos, k.Motion, AmbientRules.Hash01(bu.Pos + new Vector3(i * 0.37f, bu.Start, i * 0.11f)), new Vector4(bu.Start + (i / 6) / 20f, 1, k.Life, size),
                          new Vector4(spread, spread, spread, 0), new Vector4(k.Sprite, k.Sprites, LightCode.FullBright, 1), white);
                var f = ParticleKind.Flash;
                q.Add(bu.Pos, f.Motion, AmbientRules.Hash01(bu.Pos + new Vector3(0, bu.Start, 0)), new Vector4(bu.Start, 1, f.Life, f.Size * Mathf.Clamp(bu.Power / 4f, 0.5f, 1.5f)),
                      Vector4.zero, new Vector4(f.Sprite, f.Sprites, LightCode.FullBright, 1), white);
            }
            q.Apply(_burstMesh, Vector3.one * 8f, Vector3.one * 8f);
        }

        /// <summary>The sprite array from the pack (once). False when there's nothing to draw with.</summary>
        bool Sprites()
        {
            if (_tex != null) return true;
            if (_texTried) return false;
            _texTried = true;
            const int size = 16;
            var tex = new Texture2DArray(size, size, ParticleSprites.Count, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "particle sprites" };
            var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            int found = 0;
            for (int l = 0; l < ParticleSprites.Count; l++)
            {
                System.Array.Clear(px, 0, px.Length);
                var bytes = Atlas.ReadBytes($"assets/minecraft/textures/particle/{ParticleSprites.Names[l]}.png");
                if (bytes != null && tmp.LoadImage(bytes))
                {
                    var src = tmp.GetPixels32();
                    int w = tmp.width, h = Mathf.Min(tmp.width, tmp.height), top = tmp.height - h;   // the first square (bottom-up rows)
                    for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) px[y * size + x] = src[(top + y * h / size) * w + x * w / size];
                    found++;
                }
                tex.SetPixels32(px, l);
            }
            Destroy(tmp);
            if (found == 0) { Destroy(tex); return false; }
            tex.Apply(false, true);
            _tex = tex;
            _mat.SetTexture(TexId, _tex);
            Debug.Log($"[Tabletop] PARTICLES {found}/{ParticleSprites.Count} sprites");
            return true;
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_burstMesh != null) Destroy(_burstMesh);
            if (_mat != null) Destroy(_mat);
            if (_tex != null) Destroy(_tex);
        }
    }
}
