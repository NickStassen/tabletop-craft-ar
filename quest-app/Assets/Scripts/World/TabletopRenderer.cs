// Turns WorldModel sections into meshes under the "Blocks" transform (scaled to block_size_m, Z mirrored).
// Meshing runs on worker threads (GreedyMesher only reads the grid and the face cache); the main thread only
// prewarms the atlas for new palette entries (texture loading must stay on the main thread) and uploads finished
// meshes within a per-frame time budget, so a 128x96x128 re-snapshot (~380 sections) does not hitch the headset.
// New snapshots never empty the table (OnSnapshot): when the origin moved by whole sections (navigation) the meshes
// are re-keyed and only new or changed sections are meshed; otherwise the old meshes stay on screen until the new
// region is fully meshed. Section meshes are built relative to their own corner, so re-keying only moves them.
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
        /// <summary>View centre in region-local blocks: dirty sections closest to it are meshed first.</summary>
        public Vector3 Focus;
        /// <summary>True while a new region is meshed behind the old meshes (they swap when it is complete).</summary>
        public bool Staging => _staging;
        public int Refreshing => _lazy.Count;

        sealed class Job { public Vector3Int Section; public int SnapshotId; public MeshData Data; }

        readonly Dictionary<Vector3Int, (Mesh mesh, GameObject go, int quads)> _sections = new Dictionary<Vector3Int, (Mesh, GameObject, int)>();
        readonly ConcurrentQueue<Job> _done = new ConcurrentQueue<Job>();
        readonly ConcurrentBag<MeshData> _pool = new ConcurrentBag<MeshData>();
        readonly HashSet<Vector3Int> _busy = new HashSet<Vector3Int>();
        readonly List<Vector3> _tmp = new List<Vector3>();
        readonly List<Vector3Int> _pick = new List<Vector3Int>();
        readonly Stopwatch _sw = new Stopwatch();
        int _inFlight, _prewarmedSnapshot = -1, _prewarmedCount;
        // [nav] snapshot transitions
        readonly HashSet<Vector3Int> _lazy = new HashSet<Vector3Int>();   // kept sections to refresh when idle
        readonly List<(Mesh mesh, GameObject go)> _retired = new List<(Mesh, GameObject)>();
        readonly List<float> _pickDist = new List<float>();
        Transform _retiring;
        bool _staging; float _stagingSince;
        public float StagingTimeout = 20f;

        // Water faces seen from inside (MeshData.WaterBack) are a third submesh. A renderer draws only as many submeshes as
        // it has materials, so they are switched on by giving every section a third material while the eye is under water.
        Material[] _mats2, _mats3;
        bool _underwater;

        /// <summary>Draw the water's inside faces (EyeEffects sets this while the eye is in water).</summary>
        public bool Underwater
        {
            get => _underwater;
            set
            {
                if (_underwater == value) return;
                _underwater = value;
                foreach (var e in _sections.Values) e.go.GetComponent<MeshRenderer>().sharedMaterials = Materials();
                foreach (var e in _retired) if (e.go) e.go.GetComponent<MeshRenderer>().sharedMaterials = Materials();   // old region while staging
            }
        }

        Material[] Materials() => _underwater
            ? _mats3 ?? (_mats3 = new[] { OpaqueMaterial, TransparentMaterial, TransparentMaterial })
            : _mats2 ?? (_mats2 = new[] { OpaqueMaterial, TransparentMaterial });

        public void Clear()
        {
            foreach (var s in _sections.Values) { Destroy(s.mesh); Destroy(s.go); }
            _sections.Clear();
            Quads = 0;
            DropRetired();
            _lazy.Clear();
            _staging = false;
        }

        /// <summary>Old section keys whose mesh is out of date (dirty or being meshed): a SectionPlan must re-mesh them.</summary>
        public HashSet<Vector3Int> Unsettled()
        {
            var r = new HashSet<Vector3Int>(_busy);
            if (Model != null) r.UnionWith(Model.DirtySections);
            return r;
        }

        /// <summary>
        /// Call right after WorldModel.ApplySnapshot. Re-keys kept meshes (plan.Rekey) and marks only plan.Mesh dirty,
        /// or moves all meshes to a retiring group that stays on screen until the new region is fully meshed.
        /// </summary>
        public void OnSnapshot(SectionPlan plan, Vector3Int oldOrigin, Vector3Int newOrigin)
        {
            var n = Model.SectionCount;
            if (plan != null && plan.Rekey)
            {
                var sh = plan.ShiftSections;
                var moved = new Dictionary<Vector3Int, (Mesh, GameObject, int)>();
                int quads = 0;
                foreach (var kv in _sections)
                {
                    var k = new Vector3Int(kv.Key.x - sh.x, kv.Key.y - sh.y, kv.Key.z - sh.z);
                    var e = kv.Value;
                    if (k.x < 0 || k.y < 0 || k.z < 0 || k.x >= n.x || k.y >= n.y || k.z >= n.z) { Destroy(e.mesh); Destroy(e.go); continue; }
                    e.go.transform.localPosition = SectionPos(k);
                    moved[k] = e; quads += e.quads;
                }
                _sections.Clear();
                foreach (var kv in moved) _sections[kv.Key] = kv.Value;
                Quads = quads;
                var lazy = new List<Vector3Int>(_lazy);
                _lazy.Clear();
                foreach (var k in lazy)
                {
                    var nk = new Vector3Int(k.x - sh.x, k.y - sh.y, k.z - sh.z);
                    if (nk.x >= 0 && nk.y >= 0 && nk.z >= 0 && nk.x < n.x && nk.y < n.y && nk.z < n.z) _lazy.Add(nk);
                }
                _lazy.UnionWith(plan.Refresh);
                _lazy.ExceptWith(plan.Mesh);
                Model.DirtySections.Clear();
                Model.DirtySections.UnionWith(plan.Mesh);
            }
            else
            {
                if (_retiring == null) { _retiring = new GameObject("Retiring").transform; _retiring.SetParent(transform, false); }
                if (_staging) foreach (var e in _sections.Values) { Destroy(e.mesh); Destroy(e.go); }   // a newer region replaces a half-built one
                else if (_sections.Count > 0)
                {
                    // Keep what is on screen until the new region is complete. (The first load just appears progressively.)
                    foreach (var e in _sections.Values) { e.go.transform.SetParent(_retiring, false); _retired.Add((e.mesh, e.go)); }
                    _retiring.localPosition = Vector3.zero;
                    _staging = true; _stagingSince = Time.unscaledTime;
                }
                _sections.Clear();
                Quads = 0;
                _lazy.Clear();
            }
            // Retired meshes were built for an older origin: keep them where they are in the world.
            if (_retiring != null)
            {
                var d = new Vector3Int(oldOrigin.x - newOrigin.x, oldOrigin.y - newOrigin.y, oldOrigin.z - newOrigin.z);
                _retiring.localPosition += new Vector3(d.x, d.y, -d.z);
            }
        }

        static Vector3 SectionPos(Vector3Int s) => new Vector3(s.x * WorldModel.Section, s.y * WorldModel.Section, -s.z * WorldModel.Section);

        void DropRetired()
        {
            foreach (var r in _retired) { Destroy(r.mesh); Destroy(r.go); }
            _retired.Clear();
            if (_retiring != null) _retiring.localPosition = Vector3.zero;
        }

        void FinishStaging()
        {
            _staging = false;
            DropRetired();
            foreach (var e in _sections.Values) e.go.SetActive(e.quads > 0);
        }

        /// <summary>Fills _pick with up to `slots` sections from `from`, nearest to Focus first.</summary>
        void PickNearest(HashSet<Vector3Int> from, int slots)
        {
            _pick.Clear(); _pickDist.Clear();
            if (slots <= 0) return;
            float S = WorldModel.Section;
            foreach (var s in from)
            {
                if (_busy.Contains(s)) continue;
                float dx = (s.x + 0.5f) * S - Focus.x, dy = (s.y + 0.5f) * S - Focus.y, dz = (s.z + 0.5f) * S - Focus.z;
                float d = dx * dx + dy * dy * 0.25f + dz * dz;   // horizontal distance matters most
                int at = _pickDist.Count;
                while (at > 0 && _pickDist[at - 1] > d) at--;
                if (at >= slots) continue;
                _pick.Insert(at, s); _pickDist.Insert(at, d);
                if (_pick.Count > slots) { _pick.RemoveAt(slots); _pickDist.RemoveAt(slots); }
            }
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
            if (_staging && ((Model.DirtySections.Count == 0 && _inFlight == 0) || Time.unscaledTime - _stagingSince > StagingTimeout)) FinishStaging();
            if (_inFlight >= MaxJobsInFlight) return;
            // Urgent sections first (new terrain, nearest the view); kept ones are only refreshed when idle.
            bool lazy = Model.DirtySections.Count == 0;
            if (lazy && (_lazy.Count == 0 || _staging)) return;
            Prewarm();
            PickNearest(lazy ? _lazy : Model.DirtySections, MaxJobsInFlight - _inFlight);
            var w = Model; int snap = Model.SnapshotId;
            foreach (var s in _pick)
            {
                Model.DirtySections.Remove(s);
                _lazy.Remove(s);
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
                go.transform.localPosition = SectionPos(s);
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterials = Materials();
                entry = (mesh, go, 0);
            }
            int quads = data.Verts.Count / 4 - data.WaterBack.Count / 6;   // faces drawn from outside (inside copies not counted)
            Quads += quads - entry.quads;
            entry.quads = quads;
            _sections[s] = entry;
            // Minecraft (right-handed) -> Unity (left-handed): mirror Z, which also flips triangle winding.
            // Vertices are relative to the section corner (the GameObject sits there), so re-keying only moves it.
            _tmp.Clear();
            float bx = s.x * WorldModel.Section, by = s.y * WorldModel.Section, bz = s.z * WorldModel.Section;
            foreach (var v in data.Verts) _tmp.Add(new Vector3(v.x - bx, v.y - by, bz - v.z));
            var m = entry.mesh;
            m.Clear();
            m.SetVertices(_tmp);
            m.SetUVs(0, data.Uvs);
            m.SetColors(data.Colors);
            m.subMeshCount = 3;
            m.SetTriangles(Flip(data.Opaque), 0, false);
            m.SetTriangles(Flip(data.Transparent), 1, false);
            m.SetTriangles(Flip(data.WaterBack), 2, false);
            m.RecalculateNormals();
            m.RecalculateBounds();
            entry.go.SetActive(quads > 0 && !_staging);
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }
    }
}
