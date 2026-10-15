// [living] Rain and snow over the miniature (PROTOCOL.md "World state: time and weather"): while it rains in the
// world_state (faded by WorldClock's rain level) one static mesh of drops covers the visible window, each drop a column
// that the TabletopCraft/Weather shader moves from the region top down to the column's surface (Precipitation.cs: rain,
// snow by biome temperature and height, nothing in deserts and savannas). The mesh is rebuilt when the snapshot changes,
// the view glides more than 8 blocks, or at most every 5 seconds while blocks change (drops land on new blocks). A rebuild
// is about a millisecond at 256 wide (cached per-block tests, reused buffers); it used to redo slow string tests for every
// drop every 5 seconds, a ~150 ms hitch on the headset. tabletop.json "weather_fx".
// Cost: up to 2,500 small alpha-blended quads, one draw call; nothing at all in clear weather.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class WeatherRenderer : MonoBehaviour
    {
        public WorldModel Model;
        public WorldLight Light;
        public EntityTextures Textures;
        public NavState Nav;
        public Transform Head;
        public float BlockSizeM = 0.01f;
        /// <summary>Drops per block of window area and the cap (the drop count scales with the window, not the region).</summary>
        public float DropsPerBlock = 1f / 20f;
        public int MaxDrops = 2500;

        static readonly int StrengthId = Shader.PropertyToID("_Strength"), TimeId = Shader.PropertyToID("_WeatherTime"),
                            LightId = Shader.PropertyToID("_Light"), RainSizeId = Shader.PropertyToID("_RainSize"), SnowSizeId = Shader.PropertyToID("_SnowSize");

        Mesh _mesh;
        MeshRenderer _renderer;
        Material _mat;
        int _builtSnapshot = -1, _builtEdits = -1;
        Vector3 _builtView;
        readonly List<Drop> _drops = new List<Drop>();
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Vector2> _corners = new List<Vector2>();
        readonly List<Vector4> _data = new List<Vector4>();
        readonly List<int> _tris = new List<int>();
        float _nextRebuild;
        public int Drops { get; private set; }

        void Start()
        {
            var go = new GameObject("drops");
            go.transform.SetParent(transform, false);
            _mesh = new Mesh { name = "weather drops" };
            _mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            var baseMat = Resources.Load<Material>("TabletopWeather");
            if (baseMat == null) Debug.LogWarning("[Tabletop] Resources/TabletopWeather material missing: no rain or snow");
            _mat = baseMat != null ? new Material(baseMat) : null;
            _renderer.sharedMaterial = _mat;
            EntityFigure.Quiet(_renderer);
            _renderer.enabled = false;
        }

        void Update()
        {
            if (_mat == null || Model == null || Light == null) return;
            var clock = Light.Clock;
            bool on = WorldLight.WeatherFx && clock.Known && clock.Rain > 0.001f && Model.Loaded && clock.Dimension != "the_nether" && clock.Dimension != "the_end";
            if (_renderer.enabled != on) _renderer.enabled = on;
            if (!on) return;
            // The mesh is in region-local blocks (Z mirrored), like Blocks: scale by the block size.
            transform.localScale = Vector3.one * BlockSizeM;
            var view = Nav != null && Nav.Loaded ? Nav.View : Vector3.zero;
            if (Model.SnapshotId != _builtSnapshot || (Model.Edits != _builtEdits && Time.unscaledTime >= _nextRebuild) || (view - _builtView).sqrMagnitude > 64f) Rebuild(view);
            _mat.SetFloat(StrengthId, clock.Rain);
            _mat.SetFloat(TimeId, Time.timeSinceLevelLoad);
            var l = BlockShading.LightmapLive(15, 0);   // drops are lit like open sky (vanilla: the light at the column)
            _mat.SetVector(LightId, new Vector4(l.x, l.y, l.z, 1));
            float bs = Mathf.Max(BlockSizeM, 1e-4f);
            _mat.SetVector(RainSizeId, new Vector4(Mathf.Max(0.08f, 0.0012f / bs), Mathf.Max(1.2f, 0.012f / bs), 0, 0));   // ~1.2 x 12 mm
            _mat.SetFloat(SnowSizeId, Mathf.Max(0.25f, 0.003f / bs));                                                    // ~3 mm flakes
        }

        void Rebuild(Vector3 view)
        {
            _builtSnapshot = Model.SnapshotId; _builtEdits = Model.Edits;
            _builtView = view;
            _nextRebuild = Time.unscaledTime + 5f;
            // Columns of the visible window and 16 blocks around it (gliding), inside the region.
            int x0 = 0, z0 = 0, x1 = Model.Dims.x, z1 = Model.Dims.z;
            if (Nav != null && Nav.Loaded)
            {
                var min = Nav.WindowMin - (Vector3)Model.Origin;
                x0 = Mathf.Max(0, Mathf.FloorToInt(min.x) - 16); z0 = Mathf.Max(0, Mathf.FloorToInt(min.z) - 16);
                x1 = Mathf.Min(Model.Dims.x, Mathf.FloorToInt(min.x) + Nav.Window.x + 16); z1 = Mathf.Min(Model.Dims.z, Mathf.FloorToInt(min.z) + Nav.Window.z + 16);
            }
            float area = Nav != null && Nav.Loaded ? Nav.Window.x * Nav.Window.z : (x1 - x0) * (z1 - z0);
            int count = Mathf.Clamp((int)(area * DropsPerBlock), 64, MaxDrops);
            var drops = Precipitation.Layout(Model, x0, z0, x1, z1, count, Model.Origin.x * 31 + Model.Origin.z * 17 + Model.SnapshotId, _drops);
            Drops = drops.Count;
            var verts = _verts; var corners = _corners; var data = _data; var tris = _tris;
            verts.Clear(); corners.Clear(); data.Clear(); tris.Clear();
            foreach (var d in drops)
            {
                int s = verts.Count;
                var basePos = new Vector3(d.X, d.Ground, -d.Z);
                var info = new Vector4(d.Top, d.Phase, d.Speed, d.Kind == Precip.Snow ? 1 : 0);
                for (int k = 0; k < 4; k++) { verts.Add(basePos); data.Add(info); }
                corners.Add(new Vector2(-0.5f, 0)); corners.Add(new Vector2(0.5f, 0)); corners.Add(new Vector2(0.5f, 1)); corners.Add(new Vector2(-0.5f, 1));
                tris.Add(s); tris.Add(s + 1); tris.Add(s + 2); tris.Add(s); tris.Add(s + 2); tris.Add(s + 3);
            }
            _mesh.Clear();
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, corners);
            _mesh.SetUVs(1, data);
            _mesh.SetTriangles(tris, 0, false);
            // The shader moves the drops up to the region top: bounds over the whole column range.
            _mesh.bounds = new Bounds(new Vector3((x0 + x1) / 2f, Model.Dims.y / 2f, -(z0 + z1) / 2f), new Vector3(x1 - x0 + 4, Model.Dims.y + 4, z1 - z0 + 4));
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_mat != null) Destroy(_mat);
        }
    }
}
