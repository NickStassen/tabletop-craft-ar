// [mesh] The low-detail surface skirt on screen (World/SurfaceSkirt.cs; ARCHITECTURE.md "Surface skirt"): tiles for the
// section columns of the visible window whose sections aren't on screen yet, built on a worker, drawn with the opaque
// block material under "Blocks", dropped when their surface sections are on screen. tabletop.json "surface_skirt".
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabletopCraft.World
{
    public sealed partial class TabletopRenderer
    {
        /// <summary>The window's bottom and top (region-local block y, top exclusive; NavController sets them with the
        /// scopes): the skirt shows the first block below the top, as the clip box would.</summary>
        public int SkirtBottom = int.MinValue, SkirtTop = int.MaxValue;
        /// <summary>Most skirt tiles one worker job builds (one job at a time).</summary>
        public int SkirtTilesPerJob = 32;
        /// <summary>Skirt tiles planned or on screen (GLIDE log).</summary>
        public int SkirtTiles => _skirt.Tiles.Count;

        sealed class SkirtTile { public GameObject Go; public Mesh Mesh; public MeshRenderer Renderer; public Vector3Int WorldCorner; }
        readonly SkirtPlanner _skirt = new SkirtPlanner();
        readonly SkirtPlanner.Pending _pending = new SkirtPlanner.Pending();
        readonly List<Vector2Int> _skirtBuild = new List<Vector2Int>();
        readonly ConcurrentQueue<(Vector3Int corner, int minTop, int maxTop, BatchData data)> _skirtDone = new ConcurrentQueue<(Vector3Int, int, int, BatchData)>();
        volatile bool _skirtBusy;
        Material[] _skirtMats;
        // Sections whose new mesh isn't uploaded yet (world block of the section corner -> stamp; a batch upload clears
        // the stamps it was assembled with), so the skirt knows what is really on screen.
        readonly Dictionary<Vector3Int, int> _fresh = new Dictionary<Vector3Int, int>();
        readonly List<Vector3Int> _freshStale = new List<Vector3Int>();
        int _freshStamp;

        Vector3Int CornerOf(Vector3Int s) => new Vector3Int(Model.Origin.x + s.x * WorldModel.Section, Model.Origin.y + s.y * WorldModel.Section, Model.Origin.z + s.z * WorldModel.Section);


        void MarkFresh(Vector3Int s) { if (SurfaceSkirt.Enabled) _fresh[CornerOf(s)] = ++_freshStamp; }

        /// <summary>The fresh stamps of a batch's sections when its assembly starts (cleared again when it is uploaded).</summary>
        List<(Vector3Int, int)> FreshOf(Vector3Int key)
        {
            if (_fresh.Count == 0) return null;
            List<(Vector3Int, int)> r = null;
            foreach (var s in _layout.SectionsOf(key))
            {
                var c = CornerOf(s);
                if (_fresh.TryGetValue(c, out int st)) (r ?? (r = new List<(Vector3Int, int)>())).Add((c, st));
            }
            return r;
        }

        void Uploaded(List<(Vector3Int corner, int stamp)> fresh)
        {
            if (fresh == null) return;
            foreach (var (c, st) in fresh) if (_fresh.TryGetValue(c, out int now) && now == st) _fresh.Remove(c);
        }

        /// <summary>Once per frame after the batches: plans, builds, uploads and drops skirt tiles.</summary>
        void UpdateSkirt()
        {
            while (_skirtDone.TryDequeue(out var r)) ApplySkirtTile(r.corner, r.minTop, r.maxTop, r.data);
            bool on = SurfaceSkirt.Enabled && !_staging && Model != null && Model.Loaded && !Scope.Equals(SectionBox.All);
            if (!on) { if (_skirt.Tiles.Count > 0) { _skirt.Clear(); FreeDropped(); } return; }
            // Only while the view moves or tiles are up: at rest the window fills in from meshing within frames.
            if (_skirt.Tiles.Count == 0 && Motion.sqrMagnitude < 0.01f) return;
            // What isn't on screen yet, from the few pending sets (not by asking every section of the window).
            _pending.Reset(Scope, Model.SectionCount);   // the active box: tiles are ready before their columns show
            foreach (var s in Model.DirtySections) _pending.Mark(s);
            foreach (var s in _busy) _pending.Mark(s);
            if (_fresh.Count > 0)
            {
                var o = Model.Origin; int S = WorldModel.Section;
                foreach (var c in _fresh.Keys) _pending.Mark(new Vector3Int((c.x - o.x) / S, (c.y - o.y) / S, (c.z - o.z) / S));
            }
            _skirt.Frame(_pending, _skirtBusy ? 0 : SkirtTilesPerJob, _skirtBuild);
            FreeDropped();
            if (_skirtBuild.Count == 0) return;
            var w = Model; IFaceSource faces = Atlas;
            int top = Mathf.Min(SkirtTop, w.Dims.y), bottom = Mathf.Max(SkirtBottom, 0);
            var jobs = new List<(Vector2Int c, Vector3Int corner)>();
            foreach (var c in _skirtBuild) jobs.Add((c, CornerOf(new Vector3Int(c.x, 0, c.y))));
            int snap = w.SnapshotId;
            _skirtBusy = true;
            Task.Run(() =>
            {
                var md = new MeshData();
                foreach (var (c, corner) in jobs)
                {
                    int minTop = -1, maxTop = -1; BatchData data = null;
                    try
                    {
                        if (w.SnapshotId == snap)
                        {
                            SurfaceSkirt.Build(w, c.x, c.y, bottom, top, faces, md, out minTop, out maxTop);
                            var sm = SectionMesh.From(md, new Vector3Int(c.x, 0, c.y));
                            if (sm != null) data = BatchData.Assemble(new[] { (sm, Vector3.zero) });
                        }
                    }
                    catch (System.Exception e) { UnityEngine.Debug.LogWarning($"[Tabletop] skirt {c}: {e.Message}"); }
                    _skirtDone.Enqueue((corner, minTop, maxTop, data));
                }
                _skirtBusy = false;
            });
        }

        /// <summary>A finished tile: uploaded if its column still wants one (the region may have moved meanwhile).</summary>
        void ApplySkirtTile(Vector3Int corner, int minTop, int maxTop, BatchData data)
        {
            int S = WorldModel.Section, ox = corner.x - Model.Origin.x, oz = corner.z - Model.Origin.z, dy = corner.y - Model.Origin.y;
            var key = new Vector2Int(Mathf.FloorToInt(ox / (float)S), Mathf.FloorToInt(oz / (float)S));
            bool aligned = ox % S == 0 && oz % S == 0 && dy % S == 0;
            var t = aligned ? _skirt.Built(key, minTop < 0 ? -1 : minTop + dy, maxTop < 0 ? -1 : maxTop + dy) : null;
            if (t == null || data == null) { data?.Release(); return; }   // not wanted any more, or no surface (dropped next frame)
            var tile = _skirtPool.Count > 0 ? _skirtPool.Pop() : NewSkirtTile();
            tile.WorldCorner = corner;
            Fill(tile.Mesh, data);
            data.Release();
            (_skirtMpb ?? (_skirtMpb = new MaterialPropertyBlock())).SetVector(Cell0Id, new Vector4(corner.x, corner.y, corner.z, 0));   // [random] world block of the mesh origin
            tile.Renderer.SetPropertyBlock(_skirtMpb);
            PlaceSkirt(tile);
            tile.Go.SetActive(true);
            t.Handle = tile;
        }

        // Tiles come and go a few per frame while gliding: their objects and meshes are pooled.
        readonly Stack<SkirtTile> _skirtPool = new Stack<SkirtTile>();
        MaterialPropertyBlock _skirtMpb;   // created on first use (not allowed in a MonoBehaviour field initializer)

        SkirtTile NewSkirtTile()
        {
            var tile = new SkirtTile();
            tile.Go = new GameObject("skirt", typeof(MeshFilter), typeof(MeshRenderer));
            tile.Go.transform.SetParent(transform, false);
            tile.Mesh = new Mesh { name = "skirt", indexFormat = IndexFormat.UInt32 };
            tile.Mesh.MarkDynamic();
            tile.Go.GetComponent<MeshFilter>().sharedMesh = tile.Mesh;
            var r = tile.Renderer = tile.Go.GetComponent<MeshRenderer>();
            r.sharedMaterials = _skirtMats ?? (_skirtMats = new[] { OpaqueMaterial });
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return tile;
        }

        void PlaceSkirt(SkirtTile t)
        {
            var o = Model.Origin;
            t.Go.transform.localPosition = new Vector3(t.WorldCorner.x - o.x, t.WorldCorner.y - o.y, -(t.WorldCorner.z - o.z));
        }

        void FreeDropped()
        {
            foreach (var t in _skirt.Dropped)
                if (t.Handle is SkirtTile st)
                {
                    if (st.Go) { st.Go.SetActive(false); _skirtPool.Push(st); }
                    t.Handle = null;
                }
            _skirt.Dropped.Clear();
        }

        /// <summary>After a snapshot: tiles of a whole-section move keep their place in the world, else all go.</summary>
        void SkirtOnSnapshot(SectionPlan plan)
        {
            if (plan == null || !plan.Rekey) { _skirt.Clear(); FreeDropped(); _fresh.Clear(); return; }
            _skirt.Shift(plan.ShiftSections, Model.SectionCount);
            FreeDropped();
            foreach (var t in _skirt.Tiles.Values) if (t.Handle is SkirtTile st) PlaceSkirt(st);
            if (_fresh.Count == 0) return;
            var o = Model.Origin; var d = Model.Dims;
            _freshStale.Clear();
            foreach (var c in _fresh.Keys) if (c.x < o.x || c.y < o.y || c.z < o.z || c.x >= o.x + d.x || c.y >= o.y + d.y || c.z >= o.z + d.z) _freshStale.Add(c);
            foreach (var c in _freshStale) _fresh.Remove(c);
        }

        void ClearSkirt() { _skirt.Clear(); FreeDropped(); _fresh.Clear(); }
    }
}
