// Turns WorldModel sections into meshes under the "Blocks" transform (scaled to block_size_m, Z mirrored).
// Meshing runs on worker threads (GreedyMesher only reads the grid and the face cache); the main thread only
// prewarms the atlas for new palette entries (texture loading must stay on the main thread) and uploads finished
// meshes within a per-frame time budget, so a 128x96x128 re-snapshot (~380 sections) does not hitch the headset.
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class TabletopRenderer : MonoBehaviour
    {
        public int MaxJobsInFlight = 3;          // worker threads meshing at once
        public float UploadBudgetMs = 3f;        // main-thread mesh upload budget per frame
        public Material OpaqueMaterial, TransparentMaterial;
        public WorldModel Model;
        public ResourcePackAtlas Atlas;

        /// <summary>Sections still waiting to be meshed or uploaded (shown in the status line while loading).</summary>
        public int Pending => (Model?.DirtySections.Count ?? 0) + _inFlight;
        public int Quads { get; private set; }

        sealed class Job { public Vector3Int Section; public int SnapshotId; public MeshData Data; }

        readonly Dictionary<Vector3Int, (Mesh mesh, GameObject go, int quads)> _sections = new Dictionary<Vector3Int, (Mesh, GameObject, int)>();
        readonly ConcurrentQueue<Job> _done = new ConcurrentQueue<Job>();
        readonly ConcurrentBag<MeshData> _pool = new ConcurrentBag<MeshData>();
        readonly HashSet<Vector3Int> _busy = new HashSet<Vector3Int>();
        readonly List<Vector3> _tmp = new List<Vector3>();
        readonly List<Vector3Int> _pick = new List<Vector3Int>();
        readonly Stopwatch _sw = new Stopwatch();
        int _inFlight, _prewarmedSnapshot = -1, _prewarmedCount;

        public void Clear()
        {
            foreach (var s in _sections.Values) { Destroy(s.mesh); Destroy(s.go); }
            _sections.Clear();
            Quads = 0;
        }

        void Prewarm()
        {
            if (_prewarmedSnapshot != Model.SnapshotId) { _prewarmedSnapshot = Model.SnapshotId; _prewarmedCount = 0; }
            if (Atlas == null) { _prewarmedCount = Model.Palette.Count; return; }
            // FluidMesher meshes water/lava (incl. waterlogged blocks, kelp, seagrass) with these two states' faces. Always
            // ready, so a worker never resolves them, even for a block_update that lands mid-mesh (cached after the first call).
            for (int d = 0; d < 6; d++) { Atlas.Face(FluidRules.WaterState, d); Atlas.Face(FluidRules.LavaState, d); }
            for (; _prewarmedCount < Model.Palette.Count; _prewarmedCount++)
                for (int d = 0; d < 6; d++) Atlas.Face(Model.Palette[_prewarmedCount], d);
            Atlas.Commit();
            if (Atlas.Array != null)
            {
                OpaqueMaterial.SetTexture("_Blocks", Atlas.Array); OpaqueMaterial.SetFloat("_HasTex", 1);
                TransparentMaterial.SetTexture("_Blocks", Atlas.Array); TransparentMaterial.SetFloat("_HasTex", 1);
            }
        }

        void Update()
        {
            Atlas?.Animate(Time.unscaledTimeAsDouble);   // animated textures: 20 game ticks/s, one layer copy per frame change
            if (Model == null || !Model.Loaded) return;
            Upload();
            if (Model.DirtySections.Count == 0 || _inFlight >= MaxJobsInFlight) return;
            Prewarm();
            _pick.Clear();
            foreach (var s in Model.DirtySections)
            {
                if (_inFlight + _pick.Count >= MaxJobsInFlight) break;
                if (!_busy.Contains(s)) _pick.Add(s);
            }
            var w = Model; int snap = Model.SnapshotId;
            foreach (var s in _pick)
            {
                Model.DirtySections.Remove(s);
                _busy.Add(s);
                Interlocked.Increment(ref _inFlight);
                IFaceSource faces = Atlas;
                Task.Run(() =>
                {
                    var data = _pool.TryTake(out var d) ? d : new MeshData();
                    try
                    {
                        var n = w.SectionCount;
                        if (s.x < n.x && s.y < n.y && s.z < n.z && w.SnapshotId == snap)
                        {
                            GreedyMesher.Build(w, s, faces, data);
                            GreedyMesher.BuildSpecials(w, s, faces, data);
                        }
                        else data.Clear();
                    }
                    catch (System.Exception e) { data.Clear(); UnityEngine.Debug.LogWarning($"[Tabletop] mesh {s}: {e.Message}"); }
                    _done.Enqueue(new Job { Section = s, SnapshotId = snap, Data = data });
                });
            }
        }

        void Upload()
        {
            _sw.Restart();
            while (_sw.Elapsed.TotalMilliseconds < UploadBudgetMs && _done.TryDequeue(out var j))
            {
                Interlocked.Decrement(ref _inFlight);
                _busy.Remove(j.Section);
                // Stale (a new snapshot arrived while meshing): its sections are already queued again.
                if (j.SnapshotId == Model.SnapshotId) Apply(j.Section, j.Data);
                j.Data.Clear();
                _pool.Add(j.Data);
            }
        }

        void Apply(Vector3Int s, MeshData data)
        {
            if (!_sections.TryGetValue(s, out var entry))
            {
                var go = new GameObject($"section {s.x},{s.y},{s.z}", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterials = new[] { OpaqueMaterial, TransparentMaterial };
                entry = (mesh, go, 0);
            }
            int quads = data.Verts.Count / 4;
            Quads += quads - entry.quads;
            entry.quads = quads;
            _sections[s] = entry;
            // Minecraft (right-handed) -> Unity (left-handed): mirror Z, which also flips triangle winding.
            _tmp.Clear();
            foreach (var v in data.Verts) _tmp.Add(new Vector3(v.x, v.y, -v.z));
            var m = entry.mesh;
            m.Clear();
            m.SetVertices(_tmp);
            m.SetUVs(0, data.Uvs);
            m.SetColors(data.Colors);
            m.subMeshCount = 2;
            m.SetTriangles(Flip(data.Opaque), 0, false);
            m.SetTriangles(Flip(data.Transparent), 1, false);
            m.RecalculateNormals();
            m.RecalculateBounds();
            entry.go.SetActive(quads > 0);
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }
    }
}
