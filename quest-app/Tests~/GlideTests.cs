// [nav] dotnet run -- --live-glide ws://host:port/tabletop [--widths 64,128,256] [--glide-seconds 8] [--no-mesh]
//       [--mesh-slowdown 3] [--jar client.jar] [--skirt]
//
// Glides across a live server the way the headset does, to measure the sustainable pan speed per width preset: for each
// width it resizes the area (AreaSizing.Plan), fits the block size to a 0.8 x 0.6 m table, then holds the left stick at
// full tilt along +x for the given time and back along -x. Every frame (72 Hz) runs NavState as NavController does
// (Speed with the stream and mesh caps, Move, one region_request in flight), decodes snapshots on a worker
// (SnapshotAssembler) and applies them, and meshes like TabletopRenderer (3 jobs of up to 4 sections, MeshScope's lazy
// margin and order, the measured mesh rate; the snapshot's light). x86 meshing is faster than the Quest's:
// --mesh-slowdown F holds each worker F times as long per section. Prints one GLIDE line per width and direction:
// achieved blocks/s and table cm/s, the caps, stalled time, latencies, and frames with unmeshed sections on screen.
// [mesh] Workers mesh exactly as TabletopRenderer's do (Build + BuildSpecials + SectionMesh.From), with the client jar's
// models and textures with --jar (prewarmed on the main thread like the atlas), else stable flat colours cached per
// state (the atlas without a pack). The old harness used BlockRules.FlatColor per face, string work the app never does.
// Batches (BatchRules.Columns^2 columns) are tracked like the renderer's: rebuilds/s and MB/s re-uploaded while gliding.
// --skirt also builds the low-detail surface (SurfaceSkirt) for the frames with holes on screen and reports its cost.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class GlideTests
{

    /// <summary>
    /// TabletopRenderer's meshing schedule without Unity: dirty sections, 3 jobs in flight, the same order (MeshScope.Score)
    /// and the same lazy margin (MeshScope: only sections near the window are meshed, meshes outside Keep are dropped).
    /// </summary>
    sealed class SimMesher
    {
        public WorldModel W;
        public int MaxJobs = 3;
        public SectionBox Scope = SectionBox.All, Keep = SectionBox.All, Visible = SectionBox.All;
        public Vector3 Motion;
        int _inFlight;
        readonly HashSet<Vector3Int> _busy = new HashSet<Vector3Int>();
        readonly ConcurrentBag<MeshData> _pool = new ConcurrentBag<MeshData>();
        readonly HashSet<Vector3Int> _meshes = new HashSet<Vector3Int>();
        readonly ConcurrentQueue<(Vector3Int s, int snap, SectionMesh mesh)> _done = new ConcurrentQueue<(Vector3Int, int, SectionMesh)>();
        public IFaceSource Faces = new MeshTests.CachedFlat();
        // [mesh] Batches as TabletopRenderer keeps them: a section's mesh per key, dirty batches rebuilt (re-uploaded)
        // once none of their sections is pending in scope, or after BatchMaxWait.
        readonly Dictionary<Vector3Int, SectionMesh> _secMesh = new Dictionary<Vector3Int, SectionMesh>();
        readonly Dictionary<Vector3Int, float> _dirtyBatches = new Dictionary<Vector3Int, float>();
        BatchLayout _layout;
        public long Rebuilds, RebuildBytes;
        float _clock;
        public long Meshed, Dropped, MeshTicks;
        public double MeshMsPerSection => Meshed == 0 ? 0 : MeshTicks * 1000.0 / Stopwatch.Frequency / Meshed;
        public int Pending => W.DirtySections.Count(Scope.Contains) + _inFlight;
        public int PendingVisible => W.DirtySections.Count(Visible.Contains) + _busy.Count(Visible.Contains);

        // [mesh] The surface skirt as TabletopRenderer runs it (SkirtPlanner, SurfaceSkirt.Build on a worker).
        public bool Skirt;
        public int SkirtBottom = int.MinValue, SkirtTop = int.MaxValue;
        readonly SkirtPlanner _planner = new SkirtPlanner();
        readonly SkirtPlanner.Pending _pend = new SkirtPlanner.Pending();
        readonly HashSet<Vector3Int> _fresh = new HashSet<Vector3Int>();   // meshed, batch not rebuilt yet
        readonly ConcurrentQueue<(Vector3Int corner, bool built, int minTop, int maxTop)> _skirtDone = new ConcurrentQueue<(Vector3Int, bool, int, int)>();
        readonly List<Vector2Int> _skirtBuild = new List<Vector2Int>();
        volatile bool _skirtBusy;
        public long TilesBuilt, SkirtTicks;

        /// <summary>
        /// Holes that show from above: visible section columns whose surface sections (the rows holding the top blocks
        /// below the window top, SurfaceSkirt.SurfaceRange) aren't on screen (dirty, being meshed, or batch not rebuilt).
        /// uncovered: the same without a skirt tile up there. Pending sections under the surface or in the air don't show.
        /// </summary>
        public (bool hole, bool uncovered) SurfaceHoles()
        {
            if (Visible.Equals(SectionBox.All)) return (false, false);
            _holes.Reset(Visible, W.SectionCount);
            foreach (var s in W.DirtySections) _holes.Mark(s);
            foreach (var s in _busy) _holes.Mark(s);
            foreach (var s in _fresh) _holes.Mark(s);
            var box = _holes.Box; bool hole = false, uncovered = false;
            int S = WorldModel.Section, top = Math.Min(SkirtTop, W.Dims.y), bottom = Math.Max(SkirtBottom, 0);
            for (int x = box.Min.x; x < box.Max.x && !uncovered; x++)
                for (int z = box.Min.z; z < box.Max.z && !uncovered; z++)
                {
                    var key = (W.Origin.x + x * S, W.Origin.z + z * S, W.Origin.y + top, W.Origin.y + bottom);
                    if (!_surface.TryGetValue(key, out var r))
                    {
                        SurfaceSkirt.SurfaceRange(W, x, z, bottom, top, out int lo, out int hi);
                        r = lo < 0 ? (-1, -1) : (W.Origin.y + lo, W.Origin.y + hi + 1);   // world y; the section above holds what stands on it
                        _surface[key] = r;
                    }
                    if (r.Item1 < 0) continue;
                    bool pend = false;
                    for (int y = Math.Max(box.Min.y, FloorDiv(r.Item1 - W.Origin.y, S)); y <= Math.Min(box.Max.y - 1, FloorDiv(r.Item2 - W.Origin.y, S)) && !pend; y++) pend = _holes.Get(x, y, z);
                    if (!pend) continue;
                    hole = true;
                    if (!(Skirt && _planner.Tiles.TryGetValue(new Vector2Int(x, z), out var t) && t.Built)) uncovered = true;
                }
            return (hole, uncovered);
        }
        readonly SkirtPlanner.Pending _holes = new SkirtPlanner.Pending();
        readonly Dictionary<(int, int, int, int), (int, int)> _surface = new Dictionary<(int, int, int, int), (int, int)>();
        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        void UpdateSkirt()
        {
            while (_skirtDone.TryDequeue(out var r))   // as TabletopRenderer.ApplySkirtTile: keys from the world corner
            {
                int S = WorldModel.Section, ox = r.corner.x - W.Origin.x, oz = r.corner.z - W.Origin.z, dy = r.corner.y - W.Origin.y;
                if (ox % S != 0 || oz % S != 0 || dy % S != 0) continue;
                var key = new Vector2Int(FloorDiv(ox, S), FloorDiv(oz, S));
                if (!r.built) { if (_planner.Tiles.TryGetValue(key, out var stale) && !stale.Built) _planner.Tiles.Remove(key); continue; }
                _planner.Built(key, r.minTop < 0 ? -1 : r.minTop + dy, r.maxTop < 0 ? -1 : r.maxTop + dy);
            }
            if (!Skirt || Scope.Equals(SectionBox.All)) return;
            if (_planner.Tiles.Count == 0 && Motion.sqrMagnitude < 0.01f) return;
            _pend.Reset(Scope, W.SectionCount);   // as TabletopRenderer: the active box
            foreach (var s in W.DirtySections) _pend.Mark(s);
            foreach (var s in _busy) _pend.Mark(s);
            foreach (var s in _fresh) _pend.Mark(s);
            _planner.Frame(_pend, _skirtBusy ? 0 : 32, _skirtBuild);
            _planner.Dropped.Clear();
            if (_skirtBuild.Count == 0) return;
            var w = W; var faces = Faces; int snap = W.SnapshotId; var o = W.Origin;
            var list = _skirtBuild.Select(c => (c, corner: new Vector3Int(o.x + c.x * WorldModel.Section, o.y, o.z + c.y * WorldModel.Section))).ToList();
            int top = Math.Min(SkirtTop, w.Dims.y), bottom = Math.Max(SkirtBottom, 0); float slow = Slowdown;
            _skirtBusy = true;
            Task.Run(() =>
            {
                var md = new MeshData();
                foreach (var (c, corner) in list)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    int minTop = -1, maxTop = -1; bool built = false;
                    try { if (w.SnapshotId == snap) { SurfaceSkirt.Build(w, c.x, c.y, bottom, top, faces, md, out minTop, out maxTop); SectionMesh.From(md, new Vector3Int(c.x, 0, c.y)); built = true; } }
                    catch (Exception) { }
                    long took = Stopwatch.GetTimestamp() - t0;
                    Interlocked.Add(ref SkirtTicks, took); Interlocked.Increment(ref TilesBuilt);
                    if (slow > 1f) { long until = Stopwatch.GetTimestamp() + (long)(took * (slow - 1f)); while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(20); }
                    _skirtDone.Enqueue((corner, built, minTop, maxTop));
                }
                _skirtBusy = false;
            });
        }

        public HashSet<Vector3Int> Unsettled() { var r = new HashSet<Vector3Int>(_busy); r.UnionWith(W.DirtySections); return r; }

        public void OnSnapshot(SectionPlan plan)
        {
            var n = W.SectionCount;
            Vector3Int Sh(Vector3Int k) => new Vector3Int(k.x - plan.ShiftSections.x, k.y - plan.ShiftSections.y, k.z - plan.ShiftSections.z);
            bool In(Vector3Int k) => k.x >= 0 && k.y >= 0 && k.z >= 0 && k.x < n.x && k.y < n.y && k.z < n.z;
            var layout = new BatchLayout(W.Origin, W.Dims, BatchRules.Columns);
            if (plan != null && plan.Rekey)
            {
                W.DirtySections.Clear(); W.DirtySections.UnionWith(plan.Mesh);
                W.DirtySections.UnionWith(_dropped.Select(Sh).Where(In));   // dropped after the plan was made (as TabletopRenderer)
                var moved = _meshes.Select(Sh).Where(In).ToList();
                _meshes.Clear(); _meshes.UnionWith(moved); _meshes.ExceptWith(plan.Mesh);
                var (dirty, gone) = BatchRules.AfterRekey(_layout, layout, plan.ShiftSections, _secMesh.Keys, _dirtyBatches.Keys.Concat(_secMesh.Keys.Select(_layout.KeyOf)).Distinct());
                var sm = _secMesh.Where(kv => In(Sh(kv.Key))).ToDictionary(kv => Sh(kv.Key), kv => kv.Value);
                _secMesh.Clear(); foreach (var kv in sm) _secMesh[kv.Key] = kv.Value;
                foreach (var g in gone) { _dirtyBatches.Remove(g); _trail.Remove(g); }
                foreach (var k in dirty) if (!_dirtyBatches.ContainsKey(k)) _dirtyBatches[k] = _clock;
            }
            else { _meshes.Clear(); _secMesh.Clear(); _dirtyBatches.Clear(); _trail.Clear(); }
            _layout = layout;
            _dropped.Clear();
            if (plan != null && plan.Rekey)
            {
                var f = _fresh.Select(Sh).Where(In).ToList(); _fresh.Clear(); _fresh.UnionWith(f);
                _planner.Shift(plan.ShiftSections, n); _planner.Dropped.Clear();
            }
            else { _fresh.Clear(); _planner.Clear(); _planner.Dropped.Clear(); }
        }
        readonly HashSet<Vector3Int> _dropped = new HashSet<Vector3Int>();

        public readonly MeshRateMeter Meter = new MeshRateMeter();
        bool _saturated;
        /// <summary>Slower meshing than this PC (e.g. 3 for a guess at the Quest): each section holds its worker that much longer.</summary>
        public float Slowdown = 1f;

        public void Update(Vector3 focus, float dt)
        {
            _clock += dt;
            int done = 0;
            while (_done.TryDequeue(out var j))
            {
                _inFlight--; _busy.Remove(j.s); Meshed++; done++;
                if (j.snap != W.SnapshotId) continue;
                if (Keep.Contains(j.s)) { _meshes.Add(j.s); _dropped.Remove(j.s); SetMesh(j.s, j.mesh); }
                else { W.DirtySections.Add(j.s); _dropped.Add(j.s); SetMesh(j.s, null); }
            }
            Meter.Frame(dt, _saturated, done);
            Work.Frame();
            foreach (var k in _meshes.Where(k => !Keep.Contains(k)).ToList()) { _meshes.Remove(k); W.DirtySections.Add(k); _dropped.Add(k); Dropped++; SetMesh(k, null); }
            if (!OldBatchRules) FlushTrail(!W.DirtySections.Any(Scope.Contains) && _inFlight == 0 && Motion.sqrMagnitude < 0.01f);
            RebuildBatches();
            Dispatch(focus, dt);
            UpdateSkirt();
            _saturated = _jobs >= MaxJobs;
        }

        /// <summary>0.4.0's batch rules (every drop re-uploads its batch; no off-screen wait), for before/after numbers.</summary>
        public bool OldBatchRules;
        readonly Dictionary<Vector3Int, float> _trail = new Dictionary<Vector3Int, float>();
        public long EmptyRebuilds;

        void SetMesh(Vector3Int s, SectionMesh m)
        {
            bool had = _secMesh.ContainsKey(s);
            if (m == null && !had) return;
            if (m == null) _secMesh.Remove(s); else _secMesh[s] = m;
            if (m != null) _fresh.Add(s);
            var key = _layout.KeyOf(s);
            if (m == null && !OldBatchRules) { if (!_dirtyBatches.ContainsKey(key) && !_trail.ContainsKey(key)) _trail[key] = _clock; return; }   // TabletopRenderer.Drop
            if (!_dirtyBatches.ContainsKey(key)) _dirtyBatches[key] = _clock;
            _trail.Remove(key);
        }

        /// <summary>TabletopRenderer.FlushTrail: trail batches rebuilt when empty, after 4 s, or when meshing is idle.</summary>
        void FlushTrail(bool idle)
        {
            foreach (var kv in _trail.ToList())
                if (idle || _clock - kv.Value >= 4f || !_layout.SectionsOf(kv.Key).Any(_secMesh.ContainsKey))
                {
                    _trail.Remove(kv.Key);
                    if (!_dirtyBatches.ContainsKey(kv.Key)) _dirtyBatches[kv.Key] = _clock;
                }
        }

        /// <summary>TabletopRenderer.UpdateBatches without Unity: what would be re-uploaded (sizes of the batches rebuilt).</summary>
        void RebuildBatches()
        {
            foreach (var key in _dirtyBatches.Keys.ToList())
            {
                bool off = !OldBatchRules && BatchRules.OffScreen(_layout, key, Visible);   // TabletopRenderer.SectionsPending
                bool pending = _layout.SectionsOf(key).Any(s => _busy.Contains(s) || (W.DirtySections.Contains(s) && (Scope.Contains(s) || (off && Keep.Contains(s)))));
                if (!BatchRules.Ready(pending, _dirtyBatches[key], _clock, 0.4f, false)) continue;
                _dirtyBatches.Remove(key);
                long bytes = 0;
                foreach (var s in _layout.SectionsOf(key)) if (_secMesh.TryGetValue(s, out var m)) bytes += m.Bytes;
                Rebuilds++; RebuildBytes += bytes;
                foreach (var s in _layout.SectionsOf(key)) _fresh.Remove(s);
                if (bytes == 0) EmptyRebuilds++;
            }
        }

        void Dispatch(Vector3 focus, float dt)
        {
            int slots = MaxJobs - _jobs;
            if (slots <= 0 || W.DirtySections.Count == 0) return;
            var vis = Visible; var motion = Motion;
            var pick = W.DirtySections.Where(s => !_busy.Contains(s) && Scope.Contains(s))
                .OrderBy(s => MeshScope.Score(s, vis, focus, motion))
                .Take(slots * (LastPerJob = MeshScope.SectionsPerJob(Work.MsPerSection, dt * 1000f, PerJob))).ToList();
            var w = W; int snap = W.SnapshotId;
            int jobs = Math.Min(slots, pick.Count);
            for (int j = 0; j < jobs; j++)
            {
                var list = new List<Vector3Int>();
                for (int i = j; i < pick.Count; i += jobs) list.Add(pick[i]);
                foreach (var s in list) { W.DirtySections.Remove(s); _busy.Add(s); _inFlight++; }
                Interlocked.Increment(ref _jobs);
                var faces = Faces;
                Task.Run(() =>
                {
                    var data = _pool.TryTake(out var pooled) ? pooled : new MeshData();   // pooled like TabletopRenderer's
                    foreach (var s in list)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        SectionMesh mesh = null;
                        try
                        {
                            var n = w.SectionCount;
                            if (w.SnapshotId == snap && s.x < n.x && s.y < n.y && s.z < n.z)
                            {
                                GreedyMesher.Build(w, s, faces, data); GreedyMesher.BuildSpecials(w, s, faces, data);
                                mesh = SectionMesh.From(data, s);
                            }
                        }
                        catch (Exception) { }
                        data.Clear();
                        long took = Stopwatch.GetTimestamp() - t0;
                        Interlocked.Add(ref MeshTicks, took);   // the real work (the slowdown below is a stand-in)
                        if (mesh != null) Work.Add((long)(took * Math.Max(1f, Slowdown)));   // the job size sees the slower device
                        if (Slowdown > 1f)   // spin, like a slower core would be busy (Thread.Sleep rounds sub-ms waits to 0 or 1 ms)
                        {
                            long until = Stopwatch.GetTimestamp() + (long)(took * (Slowdown - 1f));
                            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(20);
                        }
                        _done.Enqueue((s, snap, mesh));
                    }
                    _pool.Add(data);
                    Interlocked.Decrement(ref _jobs);
                });
            }
        }
        int _jobs;
        public int PerJob = 4, LastPerJob;
        public readonly MeshWorkMeter Work = new MeshWorkMeter();
    }

    /// <summary>[smooth] The palette-derived tables the app rebuilds on the main thread when a snapshot is applied: the
    /// block-entity mask (BlockEntityRenderer), the clip caps' face keys (ClipCaps.RefreshKeys), the sound ambience
    /// table (SoundDirector) and the atlas prewarm (TabletopRenderer.Prewarm).</summary>
    static void FullPalettePasses(WorldModel w, IFaceSource faces)
    {
        var p = w.Palette;
        BlockEntityScan.Mask(p);
        for (int i = 0; i < p.Count; i++) PaletteEntry(p[i], faces);
    }

    static byte PaletteEntry(string st, IFaceSource faces)
    {
        var shape = BlockRules.Shape(st);
        string cut = CapMesher.CapState(st, shape);
        if (cut != null && faces != null) for (int f = 0; f < 6; f++) faces.Face(cut, f);
        string path = BlockRules.Path(st);
        if (path == "water") BlockEditRules.Prop(st, "level");
        if (faces != null) for (int d = 0; d < 6; d++) faces.Face(st, d);
        return 0;
    }

    /// <summary>[smooth] The same passes as the app does them now: only the entries a delta appended (PaletteTable).</summary>
    sealed class AppPalettePasses
    {
        readonly PaletteTable<bool> _mask = new PaletteTable<bool>();
        readonly PaletteTable<byte> _rest = new PaletteTable<byte>();
        public int Computed => _rest.Computed;
        public void Update(WorldModel w, IFaceSource faces)
        {
            _mask.Update(w, BlockEntityScan.Wanted);
            _rest.Update(w, s => PaletteEntry(s, faces));
        }
    }

    /// <summary>[smooth] --old-apply: the 0.8.1 snapshot apply (whole-palette passes every snapshot, no buffer reuse).</summary>
    public static bool OldApply;
    /// <summary>[smooth] --height H: the region height to glide with (0: the server's).</summary>
    public static int Height;

    public static void LiveGlide(string url, int[] widths, float seconds, bool mesh, float slowdown, Action<bool, string> check, string jar = null, bool skirt = false, bool oldBatches = false)
    {
        var link = new LinkClient(url, "");
        link.Start();
        var world = new WorldModel(); var nav = new NavState(); var asm = new SnapshotAssembler();
        var sim = new SimMesher { W = world, Slowdown = slowdown, OldBatchRules = oldBatches, Skirt = skirt };
        using var assets = jar != null ? new ZipAssets(jar) : null;
        var pack = assets != null ? new PackFaces(assets) : null;
        if (pack != null) sim.Faces = pack;
        int prewarmed = 0, prewarmSnap = -1;
        // The atlas prewarm (TabletopRenderer.Prewarm): new palette entries are baked on the main thread before meshing.
        void Prewarm()
        {
            if (pack == null) return;
            int key = OldApply ? world.SnapshotId : world.PaletteEpoch;   // [smooth] the app now prewarms only appended entries
            if (prewarmSnap != key) { prewarmSnap = key; prewarmed = 0; }
            for (int d = 0; d < 6; d++) { pack.Face(FluidRules.WaterState, d); pack.Face(FluidRules.LavaState, d); }
            for (; prewarmed < world.Palette.Count; prewarmed++) pack.Face(world.Palette[prewarmed], 0);
        }
        var limits = RegionLimits.Default;
        var held = new Queue<Dictionary<string, object>>();
        var clock = Stopwatch.StartNew();
        float Now() => (float)clock.Elapsed.TotalSeconds;
        float requestAt = -1; var latencies = new List<float>(); int listed = 0, requests = 0, parts0 = 0, holeFrames = 0, uncoveredFrames = 0, surfaceFrames = 0;
        bool gliding = false; float lastFrame = -1;
        // [smooth] main-thread time per frame (inbound, apply, meshing schedule, nav; not the sleep), apply time per
        // snapshot, the decode worker's time, and GC while gliding.
        var mainMs = new List<float>(); var applyMs = new List<float>(); var decodeMs = new List<float>();
        var passes = new AppPalettePasses();
        void PalettePasses() { if (OldApply) FullPalettePasses(world, pack); else passes.Update(world, pack); }
        SnapshotBuffers.Enabled = !OldApply; SnapshotBuffers.Clear();

        // One frame of the headset: inbound messages, a finished decode, meshing, then the caller moves.
        void Frame()
        {
            if (asm.TryTake(out var r))
            {
                if (r.Snapshot == null) Console.WriteLine("GLIDE snapshot error " + r.Error);
                else
                {
                    long a0 = Stopwatch.GetTimestamp();
                    var oldGrid = world.Grid; var oldLight = world.Light;
                    world.ApplySnapshot(r.Snapshot);
                    Prewarm();
                    sim.OnSnapshot(r.Plan);
                    PalettePasses();   // [smooth] the app's palette-derived tables (block entities, caps, sound)
                    if (!ReferenceEquals(oldGrid, world.Grid)) SnapshotBuffers.Retire(oldGrid, ReferenceEquals(oldLight, world.Light) ? null : oldLight);   // as TabletopApp
                    if (gliding) { applyMs.Add((float)((Stopwatch.GetTimestamp() - a0) * 1000.0 / Stopwatch.Frequency)); decodeMs.Add((float)r.Ms); }
                    nav.OnRegion(world.World, world.Origin, world.Dims, r.ViewMargin, Now());
                    if (requestAt >= 0) { latencies.Add(Now() - requestAt); requestAt = -1; }
                    listed += Math.Max(0, r.Listed);
                }
            }
            for (int budget = 50; budget > 0; budget--)
            {
                Dictionary<string, object> m;
                bool fromHeld = !asm.Busy && held.Count > 0;
                if (fromHeld) m = held.Dequeue();
                else if (!link.Inbound.TryDequeue(out m)) break;
                string type = m.S("type");
                if (!fromHeld && (asm.Busy || held.Count > 0) && (type == "block_update" || type == "chunk_snapshot")) { held.Enqueue(m); continue; }
                switch (type)
                {
                    case "welcome":
                        limits = RegionLimits.From(m.O("limits"), limits); nav.CanPad = m.A("caps")?.Contains("view_margin") == true; nav.Caps = limits;
                        var bh = m.O("limits")?.A("build_height");
                        if (bh != null && bh.Count == 2) { nav.MinY = (int)(double)bh[0]; nav.MaxY = (int)(double)bh[1]; }
                        break;
                    case "block_update": world.ApplyBlockUpdate(m); Prewarm(); break;
                    case "chunk_snapshot":
                        if (m.I("part") == 0) parts0++;
                        asm.Offer(m, world, mesh ? sim.Unsettled() : new HashSet<Vector3Int>()); break;
                    case "error": Console.WriteLine("GLIDE server error " + m.S("code") + " " + m.S("message")); if (m.S("code") == "region_too_large") nav.OnRefused(Now(), m.S("message")); break;
                }
            }
            if (world.Loaded && nav.Loaded)
            {
                var n = world.SectionCount;
                sim.Scope = MeshScope.Active(nav, n); sim.Keep = MeshScope.Keep(nav, n); sim.Visible = MeshScope.Visible(nav, n); sim.Motion = nav.Velocity;
                var (wlo, whi) = MeshScope.Window(nav);   // as NavController
                sim.SkirtBottom = (int)Math.Floor(wlo.y); sim.SkirtTop = (int)Math.Ceiling(whi.y);
            }
            float fnow = Now();
            if (mesh) sim.Update(nav.View - (Vector3)world.Origin, lastFrame < 0 ? 0 : fnow - lastFrame);
            else world.DirtySections.Clear();
            lastFrame = fnow;
            if (sim.PendingVisible > 0 && gliding) holeFrames++;   // sections on screen not meshed yet
            if (gliding) { var (h, u) = sim.SurfaceHoles(); if (h) surfaceFrames++; if (u) uncoveredFrames++; }   // [mesh] holes that show; not covered by the skirt
        }

        void Send(RegionMove mv) { link.Send(Protocol.RegionRequest(nav.World, mv.Origin, mv.Dims, mv.Margin)); requestAt = Now(); requests++; }

        bool WaitIdle(float timeout)
        {
            float end = Now() + timeout;
            while (Now() < end)
            {
                Frame();
                if (world.Loaded && !nav.Pending && !asm.Busy && held.Count == 0 && sim.Pending == 0) return true;
                Thread.Sleep(5);
            }
            return false;
        }

        if (!WaitIdle(120)) { check(false, "live-glide: no first snapshot"); link.Dispose(); return; }
        if (Height > 0 && nav.Dims.y != Height)   // [smooth] --height: a taller area, like the headset's 186-block one
        {
            var mv = new RegionMove { Origin = new Vector3Int(nav.Origin.x, Math.Max(nav.MinY == int.MinValue ? -64 : nav.MinY, (int)nav.View.y - Height / 2) / 16 * 16, nav.Origin.z), Dims = new Vector3Int(nav.Dims.x, Height, nav.Dims.z), Margin = new Vector3Int(nav.Margin.x, 0, nav.Margin.z), Kind = "resize" };
            if (nav.TryIssue(mv, Now())) Send(mv);
            if (!WaitIdle(120)) { check(false, "live-glide: no taller area"); link.Dispose(); return; }
        }
        foreach (int width in widths)
        {
            var plan = AreaSizing.Plan(nav, width, limits);
            if (plan != null && nav.TryIssue(plan.Value, Now())) Send(plan.Value);
            bool ok = WaitIdle(120);
            latencies.Clear();
            int w = AreaSizing.Current(nav);
            float bs = AreaMath.FitBlockSize(0.8f, 0.6f, w, w);
            check(ok && w == Math.Min(width, AreaSizing.MaxWidth(limits, 16)), $"live-glide: area {w} wide, region {nav.Dims.x}x{nav.Dims.y}x{nav.Dims.z}, margin {nav.Margin.x}/{nav.Margin.y}, block {bs * 1000:F2} mm");
            foreach (int dir in new[] { +1, -1 })
            {
                latencies.Clear(); listed = 0; requests = 0; sim.Meshed = 0; sim.Dropped = 0; sim.MeshTicks = 0; holeFrames = 0; uncoveredFrames = 0; surfaceFrames = 0; sim.TilesBuilt = 0; sim.SkirtTicks = 0; gliding = true;
                sim.Rebuilds = 0; sim.RebuildBytes = 0; sim.EmptyRebuilds = 0;
                float held0 = 0, t0 = Now(), last = t0, x0 = nav.View.x, stalled = 0, capSum = 0, meshCapSum = 0; int frames = 0;
                mainMs.Clear(); applyMs.Clear(); decodeMs.Clear();
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                long alloc0 = GC.GetTotalAllocatedBytes(false);
                while (Now() - t0 < seconds)
                {
                    float now = Now(), dt = Math.Min(0.1f, now - last); last = now;
                    long f0 = Stopwatch.GetTimestamp();
                    Frame();
                    held0 += dt;
                    nav.MeshCap = MeshScope.MeshSpeedCap(sim.Meter.Rate, sim.Scope, new Vector3(dir, 0, 0));   // as NavController
                    meshCapSum += Math.Min(1000f, nav.MeshCap);
                    float speed = nav.Speed(bs, 1f, held0, Math.Min(nav.Margin.x, nav.Margin.z));
                    float want = nav.BaseSpeedM / bs * (1f + nav.RampGain * Math.Min(1f, held0 / nav.RampSeconds));
                    capSum += speed; frames++;
                    float before = nav.View.x;
                    var mv = nav.Move(new Vector3(dir * speed * dt, 0, 0), dt, now);
                    if (Math.Abs(nav.View.x - before) < 0.5f * speed * dt) stalled += dt;
                    if (mv.HasValue) Send(mv.Value);
                    mainMs.Add((float)((Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency));
                    int sleep = (int)((1f / 72f - (Now() - now)) * 1000);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
                gliding = false;
                double allocMb = (GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0;
                int gcs0 = GC.CollectionCount(0) - gc0, gcs1 = GC.CollectionCount(1) - gc1, gcs2 = GC.CollectionCount(2) - gc2;
                float secs = Now() - t0, dist = (nav.View.x - x0) * dir;
                var lat = latencies.OrderBy(x => x).ToList();
                string P(float p) => lat.Count == 0 ? "-" : (lat[Math.Min(lat.Count - 1, (int)(p * lat.Count))] * 1000).ToString("F0");
                Console.WriteLine($"GLIDE width={w} dir={(dir > 0 ? "+x" : "-x")} region={nav.Dims.x}x{nav.Dims.y}x{nav.Dims.z} margin={nav.Margin.x} block_mm={bs * 1000:F2} " +
                                  $"achieved_blocks_s={dist / secs:F1} table_cm_s={dist / secs * bs * 100:F2} wanted_blocks_s={nav.BaseSpeedM / bs * (1 + nav.RampGain):F0} " +
                                  $"mean_speed_setting={capSum / Math.Max(1, frames):F1} stalled_s={stalled:F2}/{secs:F1} requests={requests} latency_ms p50={P(0.5f)} p90={P(0.9f)} max={P(0.999f)} " +
                                  $"latency_ema_ms={nav.LatencyEma * 1000:F0} stream_cap={nav.StreamSpeedCap(Math.Min(nav.Margin.x, nav.Margin.z)):F0} mesh_rate_s={sim.Meter.Rate:F0} " +
                                  $"mean_mesh_cap={meshCapSum / Math.Max(1, frames):F0} sections_listed={listed} meshed={sim.Meshed} mesh_ms_per_section={sim.MeshMsPerSection:F2} per_job={sim.LastPerJob} " +
                                  $"dropped={sim.Dropped} hole_frames={holeFrames}/{frames} surface_hole_frames={surfaceFrames} uncovered_frames={uncoveredFrames} faces={(pack != null ? "pack" : "flat")} slowdown={slowdown:F1} " +
                                  $"skirt={(skirt ? "on" : "off")} skirt_factor={MeshScope.SkirtFactor:F1} skirt_tiles_s={sim.TilesBuilt / secs:F0} skirt_ms_per_tile={(sim.TilesBuilt == 0 ? 0 : sim.SkirtTicks * 1000.0 / Stopwatch.Frequency / sim.TilesBuilt):F3} " +
                                  $"batch_rebuilds_s={sim.Rebuilds / secs:F1} (empty {sim.EmptyRebuilds / secs:F1}) batch_upload_mb_s={sim.RebuildBytes / secs / 1048576.0:F1}");
                // [smooth] what the shifts cost the main thread and the GC (TESTING.md "Smooth gliding")
                string Pc(List<float> l, float p) { if (l.Count == 0) return "-"; var o = l.OrderBy(x => x).ToList(); return o[Math.Min(o.Count - 1, (int)(p * o.Count))].ToString("F2"); }
                Console.WriteLine($"SMOOTH width={w} dir={(dir > 0 ? "+x" : "-x")} region={nav.Dims.x}x{nav.Dims.y}x{nav.Dims.z} margin={nav.Margin.x} shifts={applyMs.Count} shifts_s={applyMs.Count / secs:F2} " +
                                  $"blocks_per_shift={(applyMs.Count == 0 ? 0 : dist / applyMs.Count):F1} main_ms p50={Pc(mainMs, 0.5f)} p99={Pc(mainMs, 0.99f)} max={(mainMs.Count == 0 ? 0 : mainMs.Max()):F2} " +
                                  $"over4ms={mainMs.Count(x => x > 4f)}/{mainMs.Count} apply_ms p50={Pc(applyMs, 0.5f)} max={(applyMs.Count == 0 ? 0 : applyMs.Max()):F2} " +
                                  $"decode_worker_ms p50={Pc(decodeMs, 0.5f)} max={(decodeMs.Count == 0 ? 0 : decodeMs.Max()):F1} palette={world.Palette.Count} " +
                                  $"alloc_mb={allocMb:F0} alloc_mb_per_shift={(applyMs.Count == 0 ? 0 : allocMb / applyMs.Count):F1} gc={gcs0}/{gcs1}/{gcs2} buffers_reused={SnapshotBuffers.Reused} old_apply={OldApply}");
                WaitIdle(30);
            }
        }
        link.Dispose();
    }
}
