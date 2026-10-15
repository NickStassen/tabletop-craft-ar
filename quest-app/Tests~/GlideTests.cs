// [nav] dotnet run -- --live-glide ws://host:port/tabletop [--widths 64,128,256] [--glide-seconds 8] [--no-mesh]
//       [--mesh-slowdown 3]
//
// Glides across a live server the way the headset does, to measure the sustainable pan speed per width preset: for each
// width it resizes the area (AreaSizing.Plan), fits the block size to a 0.8 x 0.6 m table, then holds the left stick at
// full tilt along +x for the given time and back along -x. Every frame (72 Hz) runs NavState as NavController does
// (Speed with the stream and mesh caps, Move, one region_request in flight), decodes snapshots on a worker
// (SnapshotAssembler) and applies them, and meshes like TabletopRenderer (3 jobs of up to 4 sections, MeshScope's lazy
// margin and order, the measured mesh rate; flat colours, the snapshot's light). x86 meshing is faster than the Quest's:
// --mesh-slowdown F holds each worker F times as long per section. Prints one GLIDE line per width and direction:
// achieved blocks/s and table cm/s, the caps, stalled time, latencies, and frames with unmeshed sections on screen.
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
    sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

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
        readonly HashSet<Vector3Int> _meshes = new HashSet<Vector3Int>();
        readonly ConcurrentQueue<(Vector3Int s, int snap)> _done = new ConcurrentQueue<(Vector3Int, int)>();
        readonly Flat _faces = new Flat();
        public long Meshed, Dropped, MeshTicks;
        public double MeshMsPerSection => Meshed == 0 ? 0 : MeshTicks * 1000.0 / Stopwatch.Frequency / Meshed;
        public int Pending => W.DirtySections.Count(Scope.Contains) + _inFlight;
        public int PendingVisible => W.DirtySections.Count(Visible.Contains) + _busy.Count(Visible.Contains);

        public HashSet<Vector3Int> Unsettled() { var r = new HashSet<Vector3Int>(_busy); r.UnionWith(W.DirtySections); return r; }

        public void OnSnapshot(SectionPlan plan)
        {
            var n = W.SectionCount;
            Vector3Int Sh(Vector3Int k) => new Vector3Int(k.x - plan.ShiftSections.x, k.y - plan.ShiftSections.y, k.z - plan.ShiftSections.z);
            bool In(Vector3Int k) => k.x >= 0 && k.y >= 0 && k.z >= 0 && k.x < n.x && k.y < n.y && k.z < n.z;
            if (plan != null && plan.Rekey)
            {
                W.DirtySections.Clear(); W.DirtySections.UnionWith(plan.Mesh);
                W.DirtySections.UnionWith(_dropped.Select(Sh).Where(In));   // dropped after the plan was made (as TabletopRenderer)
                var moved = _meshes.Select(Sh).Where(In).ToList();
                _meshes.Clear(); _meshes.UnionWith(moved); _meshes.ExceptWith(plan.Mesh);
            }
            else _meshes.Clear();
            _dropped.Clear();
        }
        readonly HashSet<Vector3Int> _dropped = new HashSet<Vector3Int>();

        public readonly MeshRateMeter Meter = new MeshRateMeter();
        bool _saturated;
        /// <summary>Slower meshing than this PC (e.g. 3 for a guess at the Quest): each section holds its worker that much longer.</summary>
        public float Slowdown = 1f;

        public void Update(Vector3 focus, float dt)
        {
            int done = 0;
            while (_done.TryDequeue(out var j))
            {
                _inFlight--; _busy.Remove(j.s); Meshed++; done++;
                if (j.snap != W.SnapshotId) continue;
                if (Keep.Contains(j.s)) _meshes.Add(j.s); else { W.DirtySections.Add(j.s); _dropped.Add(j.s); }
            }
            Meter.Frame(dt, _saturated, done);
            foreach (var k in _meshes.Where(k => !Keep.Contains(k)).ToList()) { _meshes.Remove(k); W.DirtySections.Add(k); _dropped.Add(k); Dropped++; }
            Dispatch(focus);
            _saturated = _jobs >= MaxJobs;
        }

        void Dispatch(Vector3 focus)
        {
            int slots = MaxJobs - _jobs;
            if (slots <= 0 || W.DirtySections.Count == 0) return;
            var vis = Visible; var motion = Motion;
            var pick = W.DirtySections.Where(s => !_busy.Contains(s) && Scope.Contains(s))
                .OrderBy(s => MeshScope.Score(s, vis, focus, motion))
                .Take(slots * PerJob).ToList();
            var w = W; int snap = W.SnapshotId;
            int jobs = Math.Min(slots, pick.Count);
            for (int j = 0; j < jobs; j++)
            {
                var list = new List<Vector3Int>();
                for (int i = j; i < pick.Count; i += jobs) list.Add(pick[i]);
                foreach (var s in list) { W.DirtySections.Remove(s); _busy.Add(s); _inFlight++; }
                Interlocked.Increment(ref _jobs);
                Task.Run(() =>
                {
                    var data = new MeshData();
                    foreach (var s in list)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        try { if (w.SnapshotId == snap) { GreedyMesher.Build(w, s, _faces, data); GreedyMesher.BuildSpecials(w, s, _faces, data); } }
                        catch (Exception) { }
                        data.Clear();
                        long took = Stopwatch.GetTimestamp() - t0;
                        if (Slowdown > 1f) Thread.Sleep(TimeSpan.FromSeconds(took * (Slowdown - 1f) / Stopwatch.Frequency));
                        Interlocked.Add(ref MeshTicks, Stopwatch.GetTimestamp() - t0);
                        _done.Enqueue((s, snap));
                    }
                    Interlocked.Decrement(ref _jobs);
                });
            }
        }
        int _jobs;
        public int PerJob = 4;
    }

    public static void LiveGlide(string url, int[] widths, float seconds, bool mesh, float slowdown, Action<bool, string> check)
    {
        var link = new LinkClient(url, "");
        link.Start();
        var world = new WorldModel(); var nav = new NavState(); var asm = new SnapshotAssembler();
        var sim = new SimMesher { W = world, Slowdown = slowdown };
        var limits = RegionLimits.Default;
        var held = new Queue<Dictionary<string, object>>();
        var clock = Stopwatch.StartNew();
        float Now() => (float)clock.Elapsed.TotalSeconds;
        float requestAt = -1; var latencies = new List<float>(); int listed = 0, requests = 0, parts0 = 0, holeFrames = 0;
        bool gliding = false; float lastFrame = -1;

        // One frame of the headset: inbound messages, a finished decode, meshing, then the caller moves.
        void Frame()
        {
            if (asm.TryTake(out var r))
            {
                if (r.Snapshot == null) Console.WriteLine("GLIDE snapshot error " + r.Error);
                else
                {
                    world.ApplySnapshot(r.Snapshot);
                    sim.OnSnapshot(r.Plan);
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
                    case "block_update": world.ApplyBlockUpdate(m); break;
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
            }
            float fnow = Now();
            if (mesh) sim.Update(nav.View - (Vector3)world.Origin, lastFrame < 0 ? 0 : fnow - lastFrame);
            else world.DirtySections.Clear();
            lastFrame = fnow;
            if (sim.PendingVisible > 0 && gliding) holeFrames++;   // sections on screen not meshed yet
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
                latencies.Clear(); listed = 0; requests = 0; sim.Meshed = 0; sim.Dropped = 0; sim.MeshTicks = 0; holeFrames = 0; gliding = true;
                float held0 = 0, t0 = Now(), last = t0, x0 = nav.View.x, stalled = 0, capSum = 0, meshCapSum = 0; int frames = 0;
                while (Now() - t0 < seconds)
                {
                    float now = Now(), dt = Math.Min(0.1f, now - last); last = now;
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
                    int sleep = (int)((1f / 72f - (Now() - now)) * 1000);
                    if (sleep > 0) Thread.Sleep(sleep);
                }
                gliding = false;
                float secs = Now() - t0, dist = (nav.View.x - x0) * dir;
                var lat = latencies.OrderBy(x => x).ToList();
                string P(float p) => lat.Count == 0 ? "-" : (lat[Math.Min(lat.Count - 1, (int)(p * lat.Count))] * 1000).ToString("F0");
                Console.WriteLine($"GLIDE width={w} dir={(dir > 0 ? "+x" : "-x")} region={nav.Dims.x}x{nav.Dims.y}x{nav.Dims.z} margin={nav.Margin.x} block_mm={bs * 1000:F2} " +
                                  $"achieved_blocks_s={dist / secs:F1} table_cm_s={dist / secs * bs * 100:F2} wanted_blocks_s={nav.BaseSpeedM / bs * (1 + nav.RampGain):F0} " +
                                  $"mean_speed_setting={capSum / Math.Max(1, frames):F1} stalled_s={stalled:F2}/{secs:F1} requests={requests} latency_ms p50={P(0.5f)} p90={P(0.9f)} max={P(0.999f)} " +
                                  $"latency_ema_ms={nav.LatencyEma * 1000:F0} stream_cap={nav.StreamSpeedCap(Math.Min(nav.Margin.x, nav.Margin.z)):F0} mesh_rate_s={sim.Meter.Rate:F0} " +
                                  $"mean_mesh_cap={meshCapSum / Math.Max(1, frames):F0} sections_listed={listed} meshed={sim.Meshed} mesh_ms_per_section={sim.MeshMsPerSection:F1} " +
                                  $"dropped={sim.Dropped} hole_frames={holeFrames}/{frames}");
                WaitIdle(30);
            }
        }
        link.Dispose();
    }
}
