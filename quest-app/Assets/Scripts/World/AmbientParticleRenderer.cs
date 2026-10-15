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
            int n = slots.Count * 4;
            var verts = new List<Vector3>(n);
            var a = new List<Vector4>(n); var b = new List<Vector4>(n); var c = new List<Vector4>(n); var d = new List<Vector4>(n);
            var colors = new List<Color32>(n);
            var tris = new List<int>(slots.Count * 6);
            Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
            foreach (var s in slots)
            {
                var e = emitters[s.Emitter];
                var k = e.Kind;
                int v0 = verts.Count;
                var ub = new Vector4(s.Period, s.Chance, k.Life, k.Size);
                var uc = new Vector4(e.Spread.x, e.Spread.y, e.Spread.z, e.Reach);
                var ud = new Vector4(k.Sprite, k.Sprites, e.Light, k.Bright ? 1 : 0);
                for (int i = 0; i < 4; i++)
                {
                    verts.Add(e.Pos);
                    a.Add(new Vector4(i == 0 || i == 1 ? -0.5f : 0.5f, i == 1 || i == 2 ? 0.5f : -0.5f, (float)k.Motion, s.Seed));
                    b.Add(ub); c.Add(uc); d.Add(ud); colors.Add(e.Color);
                }
                tris.Add(v0); tris.Add(v0 + 1); tris.Add(v0 + 2); tris.Add(v0); tris.Add(v0 + 2); tris.Add(v0 + 3);
                lo = Vector3.Min(lo, e.Pos); hi = Vector3.Max(hi, e.Pos);
            }
            _mesh.Clear();
            if (slots.Count == 0) return;
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, a); _mesh.SetUVs(1, b); _mesh.SetUVs(2, c); _mesh.SetUVs(3, d);
            _mesh.SetColors(colors);
            _mesh.SetTriangles(tris, 0, false);
            // The shader moves particles: leaves and drips fall up to FallCap, smoke and fireflies rise and wander. Z mirrored.
            lo -= new Vector3(6, AmbientRules.FallCap + 2, 6); hi += new Vector3(6, 12, 6);
            _mesh.bounds = new Bounds(new Vector3((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, -(lo.z + hi.z) / 2), hi - lo);
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
            if (_mat != null) Destroy(_mat);
            if (_tex != null) Destroy(_tex);
        }
    }
}
