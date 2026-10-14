// Turns WorldModel sections into meshes under the "Blocks" transform (scaled to block_size_m, Z mirrored).
// Meshing runs on worker threads (GreedyMesher only reads the grid and the face cache); the main thread only
// prewarms the atlas for new palette entries (texture loading must stay on the main thread) and uploads finished
// meshes within a per-frame time budget, so a 128x96x128 re-snapshot (~380 sections) does not hitch the headset.
// New snapshots never empty the table (OnSnapshot): when the origin moved by whole sections (navigation, or a resize
// that keeps the section grid) the meshes are re-keyed and only new or changed sections are meshed; otherwise the old
// meshes stay on screen until the new region is fully meshed.
//
// [area] Batching (SectionBatches.cs, ARCHITECTURE.md "Batching"): sections are not drawn one by one. Each meshed
// section is kept as a CPU SectionMesh, and sections are merged into world-aligned batches of BatchRules.Columns^2
// section columns over the full height, one renderer (two or three draw calls) per batch. A batch is reassembled on a
// worker when one of its sections changes and uploaded within the same per-frame budget; while many of its sections
// are still being meshed it waits (BatchRules.Ready), so a big area fills in batch by batch. Batches keep their key
// when the region moves, so a shift only moves them and rebuilds the ones at the edges.
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabletopCraft.World
{
    public sealed class TabletopRenderer : MonoBehaviour
    {
        public int MaxJobsInFlight = 3;          // worker threads meshing at once
        public float UploadBudgetMs = 3f;        // main-thread mesh upload budget per frame
        public Material OpaqueMaterial, TransparentMaterial;
        public WorldModel Model;
        public ResourcePackAtlas Atlas;
        // [area] batching
        public float BatchMaxWait = 0.4f;        // s a dirty batch waits for its sections still being meshed
        public int MaxAssembliesInFlight = 2;    // batches being assembled on workers at once

        /// <summary>Sections still waiting to be meshed or uploaded, plus batches waiting to be rebuilt (status line, nav latency).</summary>
        public int Pending => (Model?.DirtySections.Count ?? 0) + _inFlight + _dirty.Count + _assembling;
        public int Quads { get; private set; }
        /// <summary>View centre in region-local blocks: dirty sections closest to it are meshed first.</summary>
        public Vector3 Focus;
        /// <summary>True while a new region is meshed behind the old meshes (they swap when it is complete).</summary>
        public bool Staging => _staging;
        public int Refreshing => _lazy.Count;

        sealed class Job { public Vector3Int Section; public int SnapshotId; public SectionMesh Mesh; }

        /// <summary>One batch renderer: world-aligned key, the world section index its mesh's y offsets refer to.</summary>
        sealed class Batch
        {
            public Vector3Int Key; public GameObject Go; public Mesh Mesh; public MeshRenderer Renderer;
            public int Y0; public bool Assembling, Dead;
            public int Verts, Indices, Opaque, Transparent, WaterBack, Sections;
            public long Bytes;
        }

        readonly Dictionary<Vector3Int, SectionMesh> _secs = new Dictionary<Vector3Int, SectionMesh>();   // [area] CPU mesh per section
        readonly Dictionary<Vector3Int, Batch> _batches = new Dictionary<Vector3Int, Batch>();
        readonly Dictionary<Vector3Int, float> _dirty = new Dictionary<Vector3Int, float>();             // batch key -> dirty since
        readonly ConcurrentQueue<(Batch batch, BatchData data)> _assembled = new ConcurrentQueue<(Batch, BatchData)>();
        readonly List<(SectionMesh, Vector3)> _parts = new List<(SectionMesh, Vector3)>();
        readonly List<Vector3Int> _order = new List<Vector3Int>();
        BatchLayout _layout;
        int _assembling;
        readonly ConcurrentQueue<Job> _done = new ConcurrentQueue<Job>();
        readonly ConcurrentBag<MeshData> _pool = new ConcurrentBag<MeshData>();
        readonly HashSet<Vector3Int> _busy = new HashSet<Vector3Int>();
        readonly List<Vector3Int> _pick = new List<Vector3Int>();
        readonly Stopwatch _sw = new Stopwatch();
        int _inFlight, _prewarmedSnapshot = -1, _prewarmedCount;
        // [nav] snapshot transitions
        readonly HashSet<Vector3Int> _lazy = new HashSet<Vector3Int>();   // kept sections to refresh when idle
        readonly List<Batch> _retired = new List<Batch>();
        readonly List<float> _pickDist = new List<float>();
        Transform _retiring;
        bool _staging; float _stagingSince;
        public float StagingTimeout = 20f;

        // Water faces seen from inside (MeshData.WaterBack) are a third submesh. A renderer draws only as many submeshes as
        // it has materials, so they are switched on by giving every batch a third material while the eye is under water.
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
                foreach (var b in _batches.Values) b.Renderer.sharedMaterials = Materials();
                foreach (var b in _retired) if (b.Go) b.Renderer.sharedMaterials = Materials();   // old region while staging
            }
        }

        Material[] Materials() => _underwater
            ? _mats3 ?? (_mats3 = new[] { OpaqueMaterial, TransparentMaterial, TransparentMaterial })
            : _mats2 ?? (_mats2 = new[] { OpaqueMaterial, TransparentMaterial });

        public void Clear()
        {
            foreach (var b in _batches.Values) Kill(b);
            _batches.Clear(); _dirty.Clear(); _secs.Clear();
            Quads = 0;
            DropRetired();
            _lazy.Clear();
            _staging = false;
        }

        static void Kill(Batch b) { b.Dead = true; Gone(b.Mesh); Gone(b.Go); }
        static void Gone(Object o) { if (!o) return; if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }   // edit mode: Editor/BatchMeshCheck

        /// <summary>Old section keys whose mesh is out of date (dirty or being meshed): a SectionPlan must re-mesh them.</summary>
        public HashSet<Vector3Int> Unsettled()
        {
            var r = new HashSet<Vector3Int>(_busy);
            if (Model != null) r.UnionWith(Model.DirtySections);
            return r;
        }

        /// <summary>
        /// Call right after WorldModel.ApplySnapshot. Re-keys kept meshes (plan.Rekey) and marks only plan.Mesh dirty,
        /// or moves all batches to a retiring group that stays on screen until the new region is fully meshed.
        /// </summary>
        public void OnSnapshot(SectionPlan plan, Vector3Int oldOrigin, Vector3Int newOrigin)
        {
            var n = Model.SectionCount;
            var layout = new BatchLayout(Model.Origin, Model.Dims, BatchRules.Columns);
            if (plan != null && plan.Rekey)
            {
                var sh = plan.ShiftSections;
                // [area] Batches are keyed in world sections: they keep their meshes and only move. The ones that lost
                // sections are rebuilt; the ones that gain sections are rebuilt when those are meshed.
                var (dirtyBatches, gone) = BatchRules.AfterRekey(_layout, layout, sh, _secs.Keys, _batches.Keys);
                var moved = new Dictionary<Vector3Int, SectionMesh>();
                int quads = 0;
                foreach (var kv in _secs)
                {
                    var k = new Vector3Int(kv.Key.x - sh.x, kv.Key.y - sh.y, kv.Key.z - sh.z);
                    if (k.x < 0 || k.y < 0 || k.z < 0 || k.x >= n.x || k.y >= n.y || k.z >= n.z) continue;
                    moved[k] = kv.Value; quads += kv.Value.Quads;
                }
                _secs.Clear();
                foreach (var kv in moved) _secs[kv.Key] = kv.Value;
                Quads = quads;
                foreach (var g in gone) { if (_batches.TryGetValue(g, out var b)) Kill(b); _batches.Remove(g); _dirty.Remove(g); }
                _layout = layout;
                foreach (var b in _batches.Values) b.Go.transform.localPosition = _layout.Position(b.Key, b.Y0);
                foreach (var k in dirtyBatches) MarkBatch(k);
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
                if (_staging) foreach (var b in _batches.Values) Kill(b);   // a newer region replaces a half-built one
                else if (_batches.Count > 0)
                {
                    // Keep what is on screen until the new region is complete. (The first load just appears progressively.)
                    foreach (var b in _batches.Values) { b.Go.transform.SetParent(_retiring, false); b.Dead = true; _retired.Add(b); }
                    _retiring.localPosition = Vector3.zero;
                    _staging = true; _stagingSince = Time.unscaledTime;
                }
                _batches.Clear(); _dirty.Clear(); _secs.Clear();
                _layout = layout;
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

        void DropRetired()
        {
            foreach (var b in _retired) Kill(b);
            _retired.Clear();
            if (_retiring != null) _retiring.localPosition = Vector3.zero;
        }

        void FinishStaging()
        {
            _staging = false;
            DropRetired();
            foreach (var b in _batches.Values) b.Go.SetActive(b.Verts > 0);
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
            // [piston] what moving_piston cells draw instead (PistonGhosts): piston heads may not be in the palette.
            if (!Model.MovingShown.IsEmpty) foreach (var s in Model.MovingShown.Values) Atlas.Face(s, 0);
            Atlas.Commit();
            if (Atlas.Array != null)
            {
                OpaqueMaterial.SetTexture("_Blocks", Atlas.Array); OpaqueMaterial.SetFloat("_HasTex", 1);
                TransparentMaterial.SetTexture("_Blocks", Atlas.Array); TransparentMaterial.SetFloat("_HasTex", 1);
            }
            if (_randomVersion != RandomUv.Version) UploadRandomTables();
        }

        // [random] Per-block texture rotation of greedy cubes (RandomUv, Shaders/TabletopRandom.cginc): the tables are a
        // global array (fixed size: Unity can't grow a shader array after its first upload), and every batch carries the
        // world block of its mesh origin so the shader can hash world positions (stable when the region moves).
        static readonly int RandomUvId = Shader.PropertyToID("_TabletopRandomUv"), Cell0Id = Shader.PropertyToID("_TabletopCell0");
        int _randomVersion = -1;
        Vector4[] _randomTables;

        void UploadRandomTables()
        {
            _randomVersion = RandomUv.Version;
            var p = RandomUv.Packed();
            if (_randomTables == null) _randomTables = new Vector4[RandomUv.MaxTables + 1];
            for (int i = 0; i < _randomTables.Length; i++) _randomTables[i] = new Vector4(p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3]);
            Shader.SetGlobalVectorArray(RandomUvId, _randomTables);
        }

        void Update()
        {
            Atlas?.Animate(Time.unscaledTimeAsDouble);   // animated textures: 20 game ticks/s, one layer copy per frame change
            if (Model == null || !Model.Loaded) return;
            _sw.Restart();
            Upload();
            UpdateBatches();   // [area]
            if (_staging && ((Model.DirtySections.Count == 0 && _inFlight == 0 && _dirty.Count == 0 && _assembling == 0) || Time.unscaledTime - _stagingSince > StagingTimeout)) FinishStaging();
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
                    SectionMesh mesh = null;
                    try
                    {
                        var n = w.SectionCount;
                        if (s.x < n.x && s.y < n.y && s.z < n.z && w.SnapshotId == snap)
                        {
                            GreedyMesher.Build(w, s, faces, data);
                            GreedyMesher.BuildSpecials(w, s, faces, data);
                            mesh = SectionMesh.From(data, s);   // [area] batch form (Unity space, normals) on the worker
                        }
                    }
                    catch (System.Exception e) { mesh = null; UnityEngine.Debug.LogWarning($"[Tabletop] mesh {s}: {e.Message}"); }
                    data.Clear();
                    _pool.Add(data);
                    _done.Enqueue(new Job { Section = s, SnapshotId = snap, Mesh = mesh });
                });
            }
        }

        /// <summary>Finished sections: keep their CPU mesh and mark their batch dirty (cheap; the batches upload later).</summary>
        void Upload()
        {
            while (_sw.Elapsed.TotalMilliseconds < UploadBudgetMs && _done.TryDequeue(out var j))
            {
                Interlocked.Decrement(ref _inFlight);
                _busy.Remove(j.Section);
                // Stale (a new snapshot arrived while meshing): its sections are already queued again.
                if (j.SnapshotId == Model.SnapshotId) Apply(j.Section, j.Mesh);
            }
        }

        void Apply(Vector3Int s, SectionMesh mesh)
        {
            _secs.TryGetValue(s, out var old);
            if (old == null && mesh == null) return;   // still empty: nothing changes on screen
            Quads += (mesh?.Quads ?? 0) - (old?.Quads ?? 0);
            if (mesh == null) _secs.Remove(s); else _secs[s] = mesh;
            MarkBatch(_layout.KeyOf(s));
        }

        void MarkBatch(Vector3Int key)
        {
            if (!_dirty.ContainsKey(key)) _dirty[key] = Time.unscaledTime;
        }

        // ---------- [area] batches ----------

        /// <summary>Uploads assembled batches within the frame budget (at least one per frame), then starts new assemblies.</summary>
        void UpdateBatches()
        {
            bool first = true;
            while ((first || _sw.Elapsed.TotalMilliseconds < UploadBudgetMs) && _assembled.TryDequeue(out var r))
            {
                first = false;
                _assembling--;
                r.batch.Assembling = false;
                if (!r.batch.Dead) UploadBatch(r.batch, r.data);
                r.data.Release();
            }
            if (_dirty.Count == 0 || _assembling >= MaxAssembliesInFlight) return;
            // Nearest to the view first.
            _order.Clear(); _order.AddRange(_dirty.Keys);
            Vector2 F(Vector3Int k) { var p = _layout.First(k); return new Vector2((p.x + BatchRules.Columns / 2f) * WorldModel.Section - Focus.x, (p.z + BatchRules.Columns / 2f) * WorldModel.Section - Focus.z); }
            _order.Sort((a, b) => F(a).sqrMagnitude.CompareTo(F(b).sqrMagnitude));
            float now = Time.unscaledTime;
            foreach (var key in _order)
            {
                if (_assembling >= MaxAssembliesInFlight) break;
                if (_batches.TryGetValue(key, out var existing) && existing.Assembling) continue;
                bool waited = !_staging && now - _dirty[key] >= BatchMaxWait;   // ready anyway: skip the section lookups
                if (!BatchRules.Ready(!waited && SectionsPending(key), _dirty[key], now, BatchMaxWait, _staging)) continue;
                _dirty.Remove(key);
                if (!_layout.Covers(key)) { if (existing != null) Kill(existing); _batches.Remove(key); continue; }
                var b = existing ?? NewBatch(key);
                _parts.Clear();
                foreach (var s in _layout.SectionsOf(key))
                    if (_secs.TryGetValue(s, out var m)) _parts.Add((m, _layout.Offset(s, key, b.Y0)));
                var parts = _parts.ToArray();
                b.Assembling = true; _assembling++;
                Task.Run(() =>
                {
                    BatchData d;
                    try { d = BatchData.Assemble(parts); }
                    catch (System.Exception e) { d = new BatchData(); UnityEngine.Debug.LogWarning($"[Tabletop] batch {b.Key}: {e.Message}"); }
                    _assembled.Enqueue((b, d));
                });
            }
        }

        bool SectionsPending(Vector3Int key)
        {
            foreach (var s in _layout.SectionsOf(key)) if (_busy.Contains(s) || Model.DirtySections.Contains(s)) return true;
            return false;
        }

        Batch NewBatch(Vector3Int key)
        {
            var go = new GameObject($"batch {key.x},{key.z}", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            var b = new Batch { Key = key, Go = go, Y0 = _layout.Sy, Renderer = go.GetComponent<MeshRenderer>() };
            go.transform.localPosition = _layout.Position(key, b.Y0);
            b.Mesh = new Mesh { name = go.name, indexFormat = IndexFormat.UInt32 };
            b.Mesh.MarkDynamic();
            go.GetComponent<MeshFilter>().sharedMesh = b.Mesh;
            b.Renderer.sharedMaterials = Materials();
            b.Renderer.shadowCastingMode = ShadowCastingMode.Off; b.Renderer.receiveShadows = false;
            b.Renderer.lightProbeUsage = LightProbeUsage.Off; b.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            // [random] World block of the mesh origin: Blocks-local (x, y, -z) is region-local, plus the region origin. Batch
            // keys are world-aligned, so this stays true when the region moves and the batch is only repositioned.
            var lp = go.transform.localPosition;
            var mpb = new MaterialPropertyBlock();
            mpb.SetVector(Cell0Id, new Vector4(_layout.Origin.x + Mathf.Round(lp.x), _layout.Origin.y + Mathf.Round(lp.y), _layout.Origin.z - Mathf.Round(lp.z), 0));
            b.Renderer.SetPropertyBlock(mpb);
            go.SetActive(false);
            _batches[key] = b;
            return b;
        }

        static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3),
        };

        /// <summary>Copies an assembled batch into a Mesh: one vertex buffer (BatchVertex layout), 32-bit indices, three
        /// submeshes (opaque, transparent, water seen from inside), bounds from the assembly. Public for Editor checks.</summary>
        public static void Fill(Mesh m, BatchData d)
        {
            const MeshUpdateFlags F = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds;
            m.Clear();
            if (d.VertCount == 0) return;
            m.SetVertexBufferParams(d.VertCount, VertexLayout);
            m.SetVertexBufferData(d.Verts, 0, 0, d.VertCount, 0, F);
            m.SetIndexBufferParams(d.IndexCount, IndexFormat.UInt32);
            m.SetIndexBufferData(d.Indices, 0, 0, d.IndexCount, F);
            var bounds = new Bounds((d.Min + d.Max) * 0.5f, d.Max - d.Min);
            m.subMeshCount = 3;
            m.SetSubMesh(0, new SubMeshDescriptor(0, d.Opaque) { bounds = bounds, firstVertex = 0, vertexCount = d.VertCount }, F);
            m.SetSubMesh(1, new SubMeshDescriptor(d.Opaque, d.Transparent) { bounds = bounds, firstVertex = 0, vertexCount = d.VertCount }, F);
            m.SetSubMesh(2, new SubMeshDescriptor(d.Opaque + d.Transparent, d.WaterBack) { bounds = bounds, firstVertex = 0, vertexCount = d.VertCount }, F);
            m.bounds = bounds;
        }

        void UploadBatch(Batch b, BatchData d)
        {
            Fill(b.Mesh, d);
            b.Verts = d.VertCount; b.Indices = d.IndexCount; b.Opaque = d.Opaque; b.Transparent = d.Transparent; b.WaterBack = d.WaterBack;
            b.Sections = d.Sections; b.Bytes = d.Bytes;
            b.Go.SetActive(d.VertCount > 0 && !_staging);
        }

        /// <summary>[area] What is on screen now (logs: AREA_LOADED, PERF).</summary>
        public struct RenderStats
        {
            public int Batches, Renderers, Sections, MeshedSections, Vertices, Triangles, DrawCalls;
            public long Bytes;
            public override string ToString() =>
                $"batches={Batches} renderers={Renderers} draw_calls~{DrawCalls} sections={MeshedSections}/{Sections} verts={Vertices} tris={Triangles} gpu_kb={Bytes / 1024}";
        }

        public RenderStats Stats()
        {
            var s = new RenderStats { Batches = _batches.Count, MeshedSections = _secs.Count };
            if (Model != null && Model.Loaded) { var n = Model.SectionCount; s.Sections = n.x * n.y * n.z; }
            foreach (var b in _batches.Values)
            {
                if (b.Verts == 0) continue;
                s.Renderers++; s.Vertices += b.Verts; s.Triangles += (b.Opaque + b.Transparent) / 3; s.Bytes += b.Bytes;
                s.DrawCalls += (b.Opaque > 0 ? 1 : 0) + (b.Transparent > 0 ? 1 : 0) + (_underwater && b.WaterBack > 0 ? 1 : 0);
            }
            return s;
        }

        /// <summary>Nothing left to mesh, assemble or upload, and not staging (the area on screen is complete).</summary>
        public bool Idle => Model != null && Model.Loaded && Pending == 0 && !_staging;
    }
}
