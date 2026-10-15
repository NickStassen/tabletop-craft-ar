// Engine-free tests for moving around on the table: NavState (pan, margin clamp, shift requests, padding, speed),
// SectionPlan (re-keying section meshes when the origin moves by whole sections), sections_v1 / delta snapshots
// (PROTOCOL.md 4.18) and the clip-cap mesher.
using System;
using System.Collections.Generic;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class NavTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;

    sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

    public static int Run(Func<string, string> fixture)
    {
        NavStateTests();
        FastGlideTests();   // [nav] margins by width, lookahead, hysteresis, the lazy margin
        SectionPlanTests();
        SectionedTests(fixture);
        CapTests();
        return fails;
    }

    static void NavStateTests()
    {
        var n = new NavState();
        n.OnRegion("world", new Vector3Int(0, 0, 0), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 0);
        Check(n.Window == new Vector3Int(64, 32, 64) && Near(n.View.x, 48) && Near(n.View.y, 32), $"nav: window = dims - 2*margin, view at the centre ({n.Window})");
        var rr = n.RegionRootLocal(0.01f);
        Check(Near(rr.x, -0.48f) && Near(rr.y, -0.16f) && Near(rr.z, 0.48f), $"nav: RegionRoot local puts the window's bottom centre on the anchor ({rr})");

        Check(n.Move(new Vector3(3, 0, 0), 0.1f, 1f) == null && Near(n.View.x, 51), "nav: small pan, no request");
        var req = n.Move(new Vector3(2, 0, 0), 0.1f, 1.1f);
        Check(req.HasValue && req.Value.Origin == new Vector3Int(16, 0, 0) && req.Value.Dims == n.Dims && req.Value.Margin == n.Margin && n.Pending,
              "nav: past the trigger -> request the region shifted by one section");
        Check(n.Move(new Vector3(30, 0, 0), 0.1f, 1.2f) == null && Near(n.View.x, 64), $"nav: clamped to the margin while pending (view {n.View.x})");
        n.OnRegion("world", new Vector3Int(16, 0, 0), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 1.6f);
        Check(!n.Pending && Near(n.View.x, 64) && Near(n.Offset.x, 0), "nav: shifted snapshot keeps the view in the world (seamless)");
        // [nav] The round trip ends when the region is applied (0.5 s after the request); meshing is MeshCap's business.
        Check(n.LatencyEma > 0.49f && n.LatencyEma < 0.51f, $"nav: latency runs from the request until the region is applied ({n.LatencyEma:F2} s)");
        var back = n.Move(new Vector3(-5, 0, 0), 0.1f, 2f);
        Check(back.HasValue && back.Value.Origin.x == 0, "nav: reversing past the trigger requests the way back");
        n.OnRegion("world", new Vector3Int(0, 0, 0), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 2.2f);

        // Diagonal and vertical moves shift several axes at once.
        n.View = n.RegionCentre;
        var diag = n.Move(new Vector3(-6, 6, 6), 0.1f, 3f);
        Check(diag.HasValue && diag.Value.Origin == new Vector3Int(-16, 16, 16), $"nav: diagonal + up shift ({diag?.Origin})");
        n.OnRefused(3.1f, "test");
        Check(!n.Pending && n.Move(new Vector3(1, 0, 0), 0.1f, 3.5f) == null, "nav: refusal backs off");
        Check(n.Move(new Vector3(-1, 1, 1), 0.1f, 5.5f).HasValue, "nav: asks again after the back-off");
        Check(n.Move(new Vector3(0, 0, 0), 0.1f, 6f) == null && n.Pending, "nav: one request in flight at a time");
        Check(n.Move(new Vector3(-1, 0, 0), 0.1f, 5.5f + n.RequestTimeout + 0.5f).HasValue, "nav: a lost answer times out");

        // Another headset moved the shared region far away: re-centre.
        n.OnRegion("world", new Vector3Int(1000, 0, 0), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 13f);
        Check(Near(n.View.x, 1048), "nav: far jump by someone else re-centres the view");
        // Area setup changed the size: re-centre on the new region.
        n.OnRegion("world", new Vector3Int(0, 0, 0), new Vector3Int(160, 128, 160), new Vector3Int(16, 16, 16), 14f);
        Check(n.Window == new Vector3Int(128, 96, 128) && Near(n.View.x, 80), "nav: new dims re-centre");

        // Build height: never ask for a region below MinY.
        var b = new NavState { MinY = 0, MaxY = 320 };
        b.OnRegion("world", new Vector3Int(0, 0, 0), new Vector3Int(64, 64, 64), new Vector3Int(16, 16, 16), 0);
        Check(b.Move(new Vector3(0, -20, 0), 0.1f, 1f) == null && Near(b.View.y, 16), "nav: no request below the build height; view stops at the margin");

        // An old region without a margin: the first push pads it (same window), and with tight caps it steps instead.
        var old = new NavState();
        old.OnRegion("world", new Vector3Int(0, 60, 0), new Vector3Int(32, 64, 32), new Vector3Int(0, 0, 0), 0);
        Check(old.Move(new Vector3(0.5f, 0, 0), 0.05f, 1f) == null, "nav: stepped mode ignores a small push (jitter)");
        var oldStep = old.Move(new Vector3(2f, 0, 0), 0.05f, 1.3f);
        Check(oldStep.HasValue && oldStep.Value.Kind == "shift", "nav: a server without view_margin only gets section steps");
        var p = new NavState { CanPad = true };
        p.OnRegion("world", new Vector3Int(0, 60, 0), new Vector3Int(32, 64, 32), new Vector3Int(0, 0, 0), 0);
        var pad = p.Move(new Vector3(0.5f, 0, 0), 0.05f, 1f);
        Check(pad.HasValue && pad.Value.Kind == "pad" && pad.Value.Dims == new Vector3Int(64, 96, 64) && pad.Value.Margin == new Vector3Int(16, 16, 16) &&
              pad.Value.Origin == new Vector3Int(-16, 44, -16), $"nav: pad request keeps the window ({pad?.Origin} {pad?.Dims})");
        p.OnRegion("world", pad.Value.Origin, pad.Value.Dims, pad.Value.Margin, 1.3f);
        Check(p.Window == new Vector3Int(32, 64, 32) && Near(p.View.x, 16), "nav: after padding the window is unchanged");
        var tight = new NavState { CanPad = true, Caps = new RegionLimits { MaxX = 32, MaxY = 64, MaxZ = 32, MaxBlocks = 1 << 20 } };
        tight.OnRegion("world", new Vector3Int(0, 60, 0), new Vector3Int(32, 64, 32), default, 0);
        var step = tight.Move(new Vector3(2.5f, 0, 0), 0.05f, 1f);
        Check(step.HasValue && step.Value.Kind == "shift" && step.Value.Origin == new Vector3Int(16, 60, 0), "nav: no room to pad -> 16-block steps");

        // Hysteresis (headset log: z shifts -48 -> -32 -> -48 every 0.3 s while gliding). After a shift the view sits
        // 12 blocks behind the new centre; a little reverse motion must not ask for the old region back.
        var h = new NavState();
        h.OnRegion("world", new Vector3Int(0, 0, -64), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 0);   // centre z -16
        var go = h.Move(new Vector3(0, 0, -4.5f), 0.1f, 1f);
        Check(go.HasValue && go.Value.Origin.z == -80, "nav: gliding -z past the trigger shifts -16");
        h.OnRegion("world", go.Value.Origin, h.Dims, h.Margin, 1.3f);   // centre z -32, view z -20.5 -> 11.5 behind
        int zBack = 0; float t = 1.4f;
        for (int i = 0; i < 40; i++, t += 0.05f)   // glide along +x with z jitter of +-0.3 blocks per frame
        {
            var mv = h.Move(new Vector3(0.6f, 0, (i & 1) == 0 ? 0.3f : -0.25f), 0.05f, t);
            if (mv.HasValue && mv.Value.Origin.z != h.Origin.z) zBack++;
            if (mv.HasValue) h.OnRegion("world", mv.Value.Origin, h.Dims, h.Margin, t + 0.01f);   // x shifts arrive at once
        }
        Check(zBack == 0, $"nav: z jitter while gliding along x asks for no z shift ({zBack})");
        var zOnly = new NavState();
        zOnly.OnRegion("world", new Vector3Int(0, 0, -64), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 0);
        var first = zOnly.Move(new Vector3(0, 0, -4.5f), 0.1f, 1f);
        zOnly.OnRegion("world", first.Value.Origin, zOnly.Dims, zOnly.Margin, 1.3f);
        bool pingPong = false; t = 1.4f;
        for (int i = 0; i < 60; i++, t += 0.05f)
        {
            var mv = zOnly.Move(new Vector3(0, 0, (i & 1) == 0 ? 0.3f : -0.3f), 0.05f, t);   // hovering, z jitter only
            if (mv.HasValue) { pingPong = true; break; }
        }
        Check(!pingPong && Math.Abs(zOnly.TriggerFor(2, +1, 16) - 15f) < 1e-4f, "nav: hovering after a shift never asks for the old region back");
        var goBack = zOnly.Move(new Vector3(0, 0, 4f), 0.1f, 5f);   // a real move back, 4 blocks: 11.5 + 4 = 15.5 > 15
        Check(goBack.HasValue && goBack.Value.Origin.z == -64, $"nav: a real move back still shifts back ({goBack?.Origin})");
        var fwd = new NavState();
        fwd.OnRegion("world", new Vector3Int(0, 0, -64), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 0);
        var f1 = fwd.Move(new Vector3(0, 0, -4.5f), 0.1f, 1f);
        fwd.OnRegion("world", f1.Value.Origin, fwd.Dims, fwd.Margin, 1.3f);
        Check(fwd.Move(new Vector3(0, 0, -12f), 0.1f, 1.5f) == null, "nav: crossing the new centre needs no request yet");
        var f2 = fwd.Move(new Vector3(0, 0, -4.1f), 0.1f, 1.7f);   // view now 4.6 past the centre: next section
        Check(f2.HasValue && f2.Value.Origin.z == -96 && Math.Abs(fwd.TriggerFor(2, +1, 16) - 4f) < 1e-4f,
              "nav: gliding on keeps the normal trigger, and after crossing the centre reversing is normal again");

        // Speed: table metres per second, quadratic curve, ramp, and the streaming cap.
        var s = new NavState();
        Check(Near(s.Speed(0.01f, 1f, 0f, 0), 12f, 1e-3f) && Near(s.Speed(0.01f, 0.5f, 0f, 0), 3f, 1e-3f), "nav: speed = 0.12 m/s / block size, quadratic stick");
        Check(Near(s.Speed(0.01f, 1f, 5f, 0), 36f, 1e-3f), "nav: holding ramps to x3");
        Check(Near(s.Speed(0.01f, 1f, 5f, 16), s.StreamSpeedCap(16), 1e-3f) && s.StreamSpeedCap(16) < 36f, $"nav: capped so it can't outrun streaming ({s.StreamSpeedCap(16):F1} blocks/s)");
        Check(n.InWindow(new Vector3(80, 64, 80)) && !n.InWindow(new Vector3(10, 64, 80)), "nav: InWindow");

        // Area setup sizes the visible box and requests the margin on top (shrunk near the caps / build height).
        var a = new AreaSetupState { Limits = new RegionLimits { MaxX = 256, MaxY = 384, MaxZ = 256, MaxBlocks = 8 << 20 } };
        a.Begin("world", new Vector3Int(0, 32, 0), new Vector3Int(64, 96, 64));
        var rq = a.BuildRequest();
        Check(Json.Write(rq.A("size")) == "[96,128,96]" && (int)rq["y_origin"] == 16 && Json.Write(rq.A("view_margin")) == "[16,16,16]" &&
              a.Estimate.Quads == AreaMath.Estimate(96, 128, 96).Quads, $"area: request = window + 2*margin ({Json.Write(rq)})");
        a.Size = new Vector3Int(256, 96, 240);
        Check(a.RequestMargin == new Vector3Int(0, 16, 8), $"area: margin shrinks to fit the caps ({a.RequestMargin})");
        a.Size = new Vector3Int(64, 96, 64); a.MinY = 24;
        Check(a.RequestMargin.y == 8, "area: margin stops at the build height");
        // [area] Draw calls follow the mesh batches now (BatchRules.Columns^2 section columns each), not the sections.
        a.MinY = int.MinValue; a.Size = new Vector3Int(128, 96, 128); a.OverrideBudget = false;
        int cols = BatchRules.Columns; BatchRules.Columns = 2;
        Check(a.EstimatedMeshes == 36 && a.Blocker() == null, $"area: 128x96x128 window (+margin) is within the budgets ({a.EstimatedMeshes} batches, {a.Estimate})");
        a.Size = new Vector3Int(192, 96, 192);
        Check(a.EstimatedMeshes == 64 && a.Blocker() != null && a.Blocker().Contains("faces"), $"area: 192 window is fine for draw calls, warns about faces ({a.Blocker()})");
        a.SectionBudget = 50;
        Check(a.Blocker() != null && a.Blocker().Contains("mesh batches"), $"area: the batch budget still warns when set low ({a.Blocker()})");
        a.OverrideBudget = true;
        Check(a.Blocker() == null, "area: hold B overrides the batch budget too");
        BatchRules.Columns = cols; a.SectionBudget = AreaMath.DefaultSectionBudget;
    }

    // ---------- [nav] faster gliding at wide areas ----------

    static readonly RegionLimits NewCaps = new RegionLimits { MaxX = 352, MaxY = 384, MaxZ = 352, MaxBlocks = 24L << 20, MaxBytes = 32 << 20, MaxViewWidth = 256 };
    static readonly RegionLimits OldCaps = new RegionLimits { MaxX = 288, MaxY = 384, MaxZ = 288, MaxBlocks = 1 << 24, MaxBytes = 32 << 20, MaxViewWidth = 256 };

    sealed class GlideRun
    {
        public float Dist, Stalled, FirstRequestAt = -1;   // FirstRequestAt: blocks travelled when the first request went out
        public int Requests, Flips; public float MaxBehind;
        public readonly List<RegionMove> Moves = new List<RegionMove>();
        public readonly List<int> ShiftsX = new List<int>();   // x shift of each region that arrived (blocks)
    }

    /// <summary>
    /// Drives NavState like NavController at 72 Hz along dir at a fixed speed (blocks/s). Each request is answered after
    /// `latency` s with the region it asked for (no mesh cap: meshing keeps up). Flips counts how
    /// often the region's direction of travel reversed on an axis; MaxBehind the furthest the view sat behind the centre
    /// (in the direction of motion) right after a shift arrived.
    /// </summary>
    static GlideRun Glide(NavState n, Vector3 dir, float speed, float latency, float seconds, ref float t)
    {
        const float dt = 1f / 72f;
        var r = new GlideRun();
        var pending = new Queue<(float at, RegionMove m)>();
        var start = n.View; var lastSign = new int[3];
        for (float end = t + seconds; t < end; t += dt)
        {
            while (pending.Count > 0 && pending.Peek().at <= t) Arrive(n, r, pending.Dequeue().m, dir, lastSign, t);
            var before = n.View;
            var mv = n.Move(dir * (speed * dt), dt, t);
            var d = n.View - before;
            if (speed > 0 && Math.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z) < 0.5f * speed * dt) r.Stalled += dt;
            if (mv.HasValue)
            {
                r.Requests++; r.Moves.Add(mv.Value); pending.Enqueue((t + latency, mv.Value));
                if (r.FirstRequestAt < 0) { var sofar = n.View - start; r.FirstRequestAt = sofar.x * dir.x + sofar.y * dir.y + sofar.z * dir.z; }
            }
        }
        // Let the last answer land so the next run starts from a settled region.
        while (pending.Count > 0) { var (at, m) = pending.Dequeue(); t = Math.Max(t, at); Arrive(n, r, m, dir, lastSign, t); }
        var moved = n.View - start;
        r.Dist = moved.x * dir.x + moved.y * dir.y + moved.z * dir.z;
        return r;
    }

    static void Arrive(NavState n, GlideRun r, RegionMove m, Vector3 dir, int[] lastSign, float t)
    {
        var old = n.Origin;
        n.OnRegion("world", m.Origin, m.Dims, m.Margin, t);
        var off = n.Offset;
        r.ShiftsX.Add(m.Origin.x - old.x);
        for (int a = 0; a < 3; a++)
        {
            int s = Math.Sign(m.Origin[a] - old[a]);
            if (s == 0) continue;
            if (lastSign[a] != 0 && s != lastSign[a]) r.Flips++;
            lastSign[a] = s;
            if (dir[a] != 0) r.MaxBehind = Math.Max(r.MaxBehind, -off[a] * Math.Sign(dir[a]));
        }
    }

    static NavState Wide(int margin, RegionLimits caps, float latency = 0.3f)
    {
        var n = new NavState { CanPad = true, Caps = caps, LatencyEma = latency, MinY = -64, MaxY = 320 };
        int d = 256 + 2 * margin;
        n.OnRegion("world", new Vector3Int(-d / 2, 16, -d / 2), new Vector3Int(d, 128, d), new Vector3Int(margin, 16, margin), 0);
        return n;
    }

    static void FastGlideTests()
    {
        // Margins grow with the width; presets are still sized with the smallest margin.
        Check(AreaSizing.MarginFor(64) == 16 && AreaSizing.MarginFor(96) == 16 && AreaSizing.MarginFor(128) == 32 && AreaSizing.MarginFor(192) == 32 && AreaSizing.MarginFor(256) == 48,
              "glide: margin 16 up to 96 wide, 32 at 128-192, 48 at 256");
        var small = new NavState { CanPad = true };
        small.OnRegion("world", new Vector3Int(0, 16, 0), new Vector3Int(96, 128, 96), new Vector3Int(16, 16, 16), 0);
        bool sized = true;
        foreach (int w in AreaSizing.All)
        {
            var p = AreaSizing.Plan(small, w, NewCaps);
            int want = w == 64 ? 0 : w + 2 * AreaSizing.MarginFor(w);   // 64 is the current window: nothing to send
            if (w == 64) sized &= p == null; else sized &= p.HasValue && p.Value.Dims.x == want && p.Value.Margin.x == AreaSizing.MarginFor(w) && p.Value.Margin.y == 16;
        }
        Check(sized && AreaSizing.Presets(NewCaps, small.MarginPref.x).SequenceEqual(AreaSizing.All), "glide: each preset gets its margin on a 352 server (256 -> 352 wide region)");
        var oldPlan = AreaSizing.Plan(small, 256, OldCaps);
        Check(AreaSizing.Presets(OldCaps, 16).Contains(256) && oldPlan.HasValue && oldPlan.Value.Margin.x == 16 && oldPlan.Value.Dims.x == 288,
              "glide: a 0.3.0 server (288) keeps the 256 preset; the margin shrinks to fit");
        var tight = NewCaps; tight.MaxBlocks = 300L * 300 * 128;
        var tp = AreaSizing.Plan(small, 256, tight).Value;
        Check(tp.Margin.x == 16 && (long)tp.Dims.x * tp.Dims.y * tp.Dims.z <= tight.MaxBlocks, $"glide: the block cap shrinks the margin, not the width ({tp.Margin.x})");

        // A 256 window with the old 16-block margin grows its margin on the first push (a resize delta of the ring).
        var g = Wide(16, NewCaps);
        g.View = g.RegionCentre + new Vector3(3.3f, 0, -5.1f);
        var grow = g.Move(new Vector3(0.5f, 0, 0), 1 / 72f, 1f);
        bool aligned = grow.HasValue && (grow.Value.Origin.x - g.Origin.x) % 16 == 0 && (grow.Value.Origin.z - g.Origin.z) % 16 == 0 && grow.Value.Origin.y == g.Origin.y;
        float gcx = grow.HasValue ? grow.Value.Origin.x + grow.Value.Dims.x / 2f : 0, gcz = grow.HasValue ? grow.Value.Origin.z + grow.Value.Dims.z / 2f : 0;
        Check(grow.HasValue && grow.Value.Kind == "pad" && grow.Value.Dims == new Vector3Int(352, 128, 352) && grow.Value.Margin == new Vector3Int(48, 16, 48) && aligned &&
              Math.Abs(gcx - g.View.x) <= 8 && Math.Abs(gcz - g.View.z) <= 8, $"glide: first push grows the margin to 48, same window, whole sections ({grow?.Origin} {grow?.Dims})");
        var gv = g.View;
        g.OnRegion("world", grow.Value.Origin, grow.Value.Dims, grow.Value.Margin, 1.4f);
        Check(g.Window == new Vector3Int(256, 96, 256) && Near(g.View.x, gv.x) && Near(g.View.z, gv.z), "glide: the grown region keeps the window and the view");
        Check(Near(g.LatencyEma, 0.3f, 0.2f), "glide: a pad counts towards the glide latency");
        var g2 = Wide(16, OldCaps);
        Check(g2.GrowMargin() == null && g2.Move(new Vector3(0.5f, 0, 0), 1 / 72f, 1f) == null, "glide: on a 288 server there is no room to grow, so no request");

        // Runway per round trip and the speed cap.
        var s = new NavState { LatencyEma = 0.3f };
        Check(Near(s.Runway(16), 12f) && Near(s.Runway(32), 32 - (12 + 16) / 2f) && Near(s.Runway(48), 32f) && !s.CanLookahead(16) && s.CanLookahead(32),
              $"glide: runway 12 / 18 / 32 blocks per round trip at margin 16 / 32 / 48 ({s.Runway(16)}, {s.Runway(32)}, {s.Runway(48)})");
        Check(Near(s.StreamSpeedCap(48), 32f / 0.39f, 0.01f) && Near(s.StreamSpeedCap(16), 12f / 0.39f, 0.01f), $"glide: cap = runway / (1.3 x latency) ({s.StreamSpeedCap(48):F0} vs {s.StreamSpeedCap(16):F0} blocks/s)");

        // Steady motion with lookahead: no stalls at the cap, the view stays a reserve away from the trailing edge.
        float t = 0;
        var la = Wide(48, NewCaps);
        float cap48 = la.StreamSpeedCap(48);
        var run = Glide(la, new Vector3(1, 0, 0), cap48, 0.3f, 10f, ref t);
        Check(run.Stalled < 0.1f && run.Dist > cap48 * 9.8f && run.MaxBehind <= 48 - la.Reserve(48) + 0.01f && run.Flips == 0,
              $"glide: lookahead sustains the cap ({cap48:F0} blocks/s at 0.3 s): {run.Dist:F0} blocks in 10 s, stalled {run.Stalled:F2} s, {run.Requests} requests, at most {run.MaxBehind:F1} behind");
        Check(run.Moves.All(m => (m.Origin.x + 176) % 16 == 0 && m.Kind == "shift") && run.ShiftsX.Any(x => x >= 32), "glide: lookahead shifts are whole sections, several at once");
        var flat = Wide(16, NewCaps); flat.CanPad = false;   // the 0.3.0 margin, not grown
        float cap16 = flat.StreamSpeedCap(16);
        var r16 = Glide(flat, new Vector3(1, 0, 0), cap16, 0.3f, 10f, ref t);
        Check(r16.Stalled < 0.1f && cap48 > 2.5f * cap16, $"glide: the 16 margin sustains only {cap16:F0} blocks/s at the same latency ({r16.Dist:F0} blocks, stalled {r16.Stalled:F2} s)");
        var over = Wide(48, NewCaps);
        var ro = Glide(over, new Vector3(1, 0, 0), cap48 * 1.6f, 0.3f, 6f, ref t);
        Check(ro.Stalled > 0.2f, $"glide: well above the cap the view does stall at the margin ({ro.Stalled:F2} s)");

        // Stop: nothing more is asked for, and hovering with jitter never asks for the old region back.
        var stop = Glide(la, new Vector3(0, 0, 0), 0, 0.3f, 2f, ref t);
        int jitterReq = 0;
        for (int i = 0; i < 120; i++, t += 1 / 72f) if (la.Move(new Vector3((i & 1) == 0 ? 0.3f : -0.3f, 0, 0), 1 / 72f, t).HasValue) jitterReq++;
        Check(stop.Requests == 0 && jitterReq == 0, $"glide: stopping after lookahead asks for nothing, jitter neither ({stop.Requests}, {jitterReq})");

        // Reverse: the region turns round once the view is back past where the last shift left it, then looks ahead the
        // other way; it never goes back and forth.
        float behind = -la.Offset.x;
        var back = Glide(la, new Vector3(-1, 0, 0), 60f, 0.3f, 6f, ref t);
        Check(back.Flips == 0 && back.ShiftsX.All(x => x < 0) && back.FirstRequestAt >= la.Hysteresis - 0.5f && back.Stalled < 0.15f && back.Dist > 350,
              $"glide: reversing turns the region round after {back.FirstRequestAt:F1} blocks back (was {behind:F1} behind), then glides on ({back.Requests} requests, stalled {back.Stalled:F2} s)");
        var la2 = Wide(48, NewCaps);
        Glide(la2, new Vector3(1, 0, 0), 60f, 0.3f, 2f, ref t);
        float x0 = la2.View.x; var wiggle = new List<int>();
        for (int i = 0; i < 6; i++)   // back and forth by 6 blocks (less than SteadyBlocks): no lookahead, no ping-pong
        {
            wiggle.AddRange(Glide(la2, new Vector3(-1, 0, 0), 20f, 0.3f, 0.3f, ref t).ShiftsX);
            wiggle.AddRange(Glide(la2, new Vector3(1, 0, 0), 20f, 0.3f, 0.3f, ref t).ShiftsX);
        }
        int turns = 0;
        for (int i = 1; i < wiggle.Count; i++) if (Math.Sign(wiggle[i]) != Math.Sign(wiggle[i - 1])) turns++;
        Check(turns == 0 && wiggle.Count <= 1 && Math.Abs(la2.View.x - x0) < 10, $"glide: wiggling back and forth 6 blocks never ping-pongs ({string.Join(",", wiggle)})");

        // Diagonal: both axes look ahead.
        var dg = Wide(48, NewCaps);
        var rd = Glide(dg, new Vector3(0.7071f, 0, 0.7071f), 60f, 0.3f, 6f, ref t);
        Check(rd.Stalled < 0.1f && dg.Origin.x - (-176) >= 192 && dg.Origin.z - (-176) >= 192 && rd.Dist > 350 && rd.Flips == 0,
              $"glide: diagonal glide shifts x and z ahead ({rd.Requests} requests, {rd.Dist:F0} blocks, stalled {rd.Stalled:F2} s, origin {dg.Origin})");

        // Vertical: lookahead with a tall margin stops at the build height; a 16 y margin glides as before.
        var v = new NavState { CanPad = true, Caps = NewCaps, LatencyEma = 0.3f, MinY = -64, MaxY = 320 };
        v.OnRegion("world", new Vector3Int(0, 96, 0), new Vector3Int(96, 160, 96), new Vector3Int(16, 48, 16), 0);
        var up = Glide(v, new Vector3(0, 1, 0), 40f, 0.3f, 6f, ref t);
        Check(up.Moves.All(m => m.Origin.y + m.Dims.y <= 320 && (m.Origin.y - 96) % 16 == 0) && v.Origin.y + v.Dims.y == 320 && up.Flips == 0,
              $"glide: up with lookahead stops at the build height ({v.Origin.y}..{v.Origin.y + v.Dims.y}, {up.Requests} requests)");
        var down = Glide(v, new Vector3(0, -1, 0), 40f, 0.3f, 12f, ref t);
        Check(down.Moves.All(m => m.Origin.y >= -64) && v.Origin.y == -64, $"glide: and down to the bottom of the world ({v.Origin.y})");
        var v16 = Wide(48, NewCaps);
        var vu = Glide(v16, new Vector3(0, 1, 0), 20f, 0.3f, 3f, ref t);
        Check(!v16.CanLookahead(16) && vu.Requests >= 2 && vu.Moves.All(m => m.Kind == "shift") && vu.Stalled < 0.1f && v16.Origin.y > 16,
              $"glide: a 16 y margin still glides up a section at a time ({vu.Requests} requests)");

        // The latency the cap uses comes from glide requests, not from a resize.
        var lat = Wide(48, NewCaps, 0.3f);
        var rs = AreaSizing.Plan(lat, 128, NewCaps).Value;
        lat.TryIssue(rs, 10f);
        lat.OnRegion("world", rs.Origin, rs.Dims, rs.Margin, 12f);
        Check(Near(lat.LatencyEma, 0.3f), $"glide: a 2 s resize does not slow the glide down ({lat.LatencyEma:F2} s)");

        // The lazy margin: only sections near the window are meshed; a delta's new sections arrive outside that.
        var ms = Wide(48, NewCaps);   // region -176..176 on x/z (22 sections), window -128..128
        var nsec = SectionPlan.Sections(ms.Dims);
        var vis = MeshScope.Visible(ms, nsec); var act = MeshScope.Active(ms, nsec); var keep = MeshScope.Keep(ms, nsec);
        Check(vis.Min.x == 3 && vis.Max.x == 19 && act.Min.x == 2 && act.Max.x == 20 && keep.Min.x == 1 && keep.Max.x == 21 && vis.Min.y == 1 && vis.Max.y == 7 && act.Min.y == 0,
              $"scope: window sections {vis}, meshed {act}, kept {keep} of {nsec}");
        ms.Velocity = new Vector3(60, 0, 0);
        var ahead = MeshScope.Active(ms, nsec);
        Check(ahead.Max.x == 22 && ahead.Min.x == 2, $"scope: moving +x meshes further ahead ({ahead})");
        Check(MeshScope.Score(new Vector3Int(18, 2, 10), vis, ms.View - (Vector3)ms.Origin, ms.Velocity) < MeshScope.Score(new Vector3Int(19, 2, 10), vis, ms.View - (Vector3)ms.Origin, ms.Velocity) &&
              MeshScope.Score(new Vector3Int(19, 2, 10), vis, ms.View - (Vector3)ms.Origin, ms.Velocity) < MeshScope.Score(new Vector3Int(2, 2, 10), vis, ms.View - (Vector3)ms.Origin, ms.Velocity),
              "scope: on-screen sections first, then the ones ahead, then the rest");
        var noMargin = new NavState(); noMargin.OnRegion("world", new Vector3Int(0, 0, 0), new Vector3Int(64, 64, 64), default, 0);
        Check(MeshScope.Active(noMargin, new Vector3Int(4, 4, 4)).Count == 64, "scope: a region without a margin meshes everything");
        var lk = Wide(48, NewCaps);
        var pre = MeshScope.Active(lk, nsec);
        var lr = Glide(lk, new Vector3(1, 0, 0), 60f, 0.3f, 1.2f, ref t);
        var shiftX = lk.Origin.x - (-176);
        Check(shiftX >= 32 && lr.Moves.Count > 0, $"scope: a lookahead glide moved the region {shiftX} blocks");
        float gap = (nsec.x - 1) * 16 - (lk.View.x - lk.Origin.x + lk.Window.x / 2f);
        lk.Velocity = new Vector3(0, 0, 0);
        var post = MeshScope.Active(lk, nsec);
        Check(gap >= 32 && !post.Contains(new Vector3Int(nsec.x - 1, 2, 10)) && !post.Contains(new Vector3Int(nsec.x - 2, 2, 10)),
              $"scope: after a lookahead glide the newest sections are {gap:F0} blocks (over 0.5 s at 60 blocks/s) beyond the window, and not meshed at rest ({post})");

        // Meshing limits the speed: every 16 blocks along x bring one y-z slice of the meshed box (8 x 18 sections here).
        var box = MeshScope.Active(Wide(48, NewCaps), nsec);
        float mc = MeshScope.MeshSpeedCap(600f, box, new Vector3(1, 0, 0));
        Check(box.Max.y - box.Min.y == 8 && box.Max.z - box.Min.z == 18 && Near(mc, 600f * MeshScope.Utilisation * 16f / (8 * 18), 0.01f) &&
              MeshScope.MeshSpeedCap(0, box, new Vector3(1, 0, 0)) == float.MaxValue && MeshScope.MeshSpeedCap(600f, box, new Vector3(0, 0, 0)) == float.MaxValue,
              $"mesh cap: 600 sections/s keep up with {mc:F0} blocks/s along x at 256 wide; unknown rate or no motion = no cap");
        float diag = MeshScope.MeshSpeedCap(600f, box, new Vector3(1, 0, 1));
        Check(diag < mc && diag > mc / 2, $"mesh cap: a diagonal brings in two slices ({diag:F0} blocks/s)");
        var capped = Wide(48, NewCaps); capped.MeshCap = 20f;
        float sp = capped.Speed(0.0022f, 1f, 5f, 48);
        capped.MeshCap = 1f;
        Check(Near(sp, 20f) && Near(capped.Speed(0.0022f, 1f, 5f, 48), capped.MinMeshCap), $"mesh cap: Speed obeys it, but never below MinMeshCap ({sp:F0})");
        var meter = new MeshRateMeter();
        for (int i = 0; i < 80; i++) meter.Frame(1 / 72f, i % 2 == 0, 10);   // only the saturated half counts
        meter.Frame(1 / 72f, false, 1000);
        Check(Near(meter.Rate, 720f, 1f), $"mesh rate: sections per second of saturated time ({meter.Rate:F0})");
    }

    static WorldModel Terrain(Vector3Int origin, Vector3Int dims, string[] palette, Func<int, int, int, string> f, int id = 1)
    {
        var w = new WorldModel { SnapshotId = id, World = "world", Origin = origin, Dims = dims };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dims.x * dims.y * dims.z];
        for (int y = 0; y < dims.y; y++) for (int z = 0; z < dims.z; z++) for (int x = 0; x < dims.x; x++)
            w.Grid[w.Index(x, y, z)] = (ushort)Array.IndexOf(palette, f(origin.x + x, origin.y + y, origin.z + z));
        return w;
    }

    static ChunkSnapshot Snap(WorldModel w, int id) => new ChunkSnapshot { SnapshotId = id, World = w.World, Origin = w.Origin, Dims = w.Dims, Palette = w.Palette.ToArray(), Grid = w.Grid };

    static string Hills(int x, int y, int z) => y < 4 + ((x * 7 + z * 3) & 7) ? "minecraft:stone" : y == 4 + ((x * 7 + z * 3) & 7) ? "minecraft:grass_block" : "minecraft:air";

    static void SectionPlanTests()
    {
        var pal = new[] { "minecraft:air", "minecraft:stone", "minecraft:grass_block" };
        var dims = new Vector3Int(48, 16, 32);
        var old = Terrain(new Vector3Int(0, 0, 0), dims, pal, Hills);
        old.DirtySections.Clear();
        // Same world, shifted by one section in +X, palette in another order.
        var pal2 = new[] { "minecraft:grass_block", "minecraft:air", "minecraft:stone" };
        var neu = Terrain(new Vector3Int(16, 0, 0), dims, pal2, Hills, 2);
        var plan = SectionPlan.Compute(old, Snap(neu, 2), new HashSet<Vector3Int>(), null);
        Check(plan.Rekey && plan.ShiftSections == new Vector3Int(1, 0, 0), "plan: whole-section shift re-keys");
        Check(plan.Mesh.SetEquals(new[] { new Vector3Int(2, 0, 0), new Vector3Int(2, 0, 1) }) && plan.Kept == 4,
              $"plan: only the new column is meshed, 4 kept ({string.Join(" ", plan.Mesh)})");
        Check(plan.Refresh.SetEquals(new[] { new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 1) }), $"plan: neighbours refreshed later ({string.Join(" ", plan.Refresh)})");

        // A changed block inside the overlap, and a stale (dirty) old section.
        var changed = Terrain(new Vector3Int(16, 0, 0), dims, pal2, (x, y, z) => x == 20 && y == 15 && z == 3 ? "minecraft:stone" : Hills(x, y, z), 2);
        var stale = new HashSet<Vector3Int> { new Vector3Int(2, 0, 1) };   // old key (2,0,1) -> new key (1,0,1)
        var p2 = SectionPlan.Compute(old, Snap(changed, 2), stale, null);
        Check(p2.Mesh.Contains(new Vector3Int(0, 0, 0)) && p2.Mesh.Contains(new Vector3Int(1, 0, 1)) && p2.Kept == 2,
              $"plan: changed and stale sections are meshed again ({string.Join(" ", p2.Mesh)})");

        // Delta snapshots: listed sections only, no compare.
        var p3 = SectionPlan.Compute(old, Snap(neu, 2), null, new List<Vector3Int> { new Vector3Int(2, 0, 0), new Vector3Int(2, 0, 1) });
        Check(p3.Rekey && p3.Mesh.Count == 2 && p3.Compared == 0, "plan: delta uses the listed sections");

        // Not section aligned, other dims, other world: no re-key.
        var odd = Terrain(new Vector3Int(8, 0, 0), dims, pal, Hills, 2);
        Check(!SectionPlan.Compute(old, Snap(odd, 2), null, null).Rekey, "plan: 8-block shift -> full re-mesh");
        var big = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(64, 16, 32), pal, Hills, 2);
        var pb = SectionPlan.Compute(old, Snap(big, 2), null, null);
        Check(pb.Rekey && pb.Mesh.Count == 2 && pb.Kept == 6, $"plan: [area] grown dims on the same section grid re-key, only the new column is meshed ({pb.Mesh.Count} meshed, {pb.Kept} kept)");
        var far = Terrain(new Vector3Int(64, 0, 0), new Vector3Int(64, 16, 32), pal, Hills, 2);
        Check(!SectionPlan.Compute(old, Snap(far, 2), null, null).Rekey, "plan: new dims without overlap -> full re-mesh");
        // Unaligned dims: a partial old section can't stand in for a full new one.
        var w40 = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(40, 16, 16), pal, Hills);
        var w40b = Terrain(new Vector3Int(16, 0, 0), new Vector3Int(40, 16, 16), pal, Hills, 2);
        var p40 = SectionPlan.Compute(w40, Snap(w40b, 2), null, null);
        Check(p40.Rekey && p40.Mesh.Contains(new Vector3Int(1, 0, 0)) && p40.Mesh.Contains(new Vector3Int(2, 0, 0)) && !p40.Mesh.Contains(new Vector3Int(0, 0, 0)),
              $"plan: partial edge section is re-meshed ({string.Join(" ", p40.Mesh)})");
    }

    static Dictionary<string, object> Load(Func<string, string> fixture, string name) =>
        Json.Parse(System.IO.File.ReadAllText(fixture(name))) as Dictionary<string, object>;

    static void SectionedTests(Func<string, string> fixture)
    {
        var full = Load(fixture, "chunk_snapshot_sections.json");
        var s = SectionedSnapshot.Assemble(new[] { full }, null, null);
        var w = new WorldModel(); w.ApplySnapshot(s);
        Check(w.Dims == new Vector3Int(32, 16, 16) && w.State(w.Get(3, 7, 9)) == "minecraft:stone" && w.State(w.Get(3, 8, 9)).StartsWith("minecraft:grass_block") &&
              w.State(w.Get(3, 9, 9)) == "minecraft:air" && w.State(w.Get(20, 3, 15)) == "minecraft:stone" && w.State(w.Get(20, 4, 0)) == "minecraft:air",
              "sections_v1: full snapshot decodes");
        var delta = Load(fixture, "chunk_snapshot_delta.json");
        var listed = new List<Vector3Int>();
        var d = SectionedSnapshot.Assemble(new[] { delta }, w, listed);
        var w2 = new WorldModel(); w2.ApplySnapshot(d);
        Check(w2.Origin == new Vector3Int(16, 60, 0) && w2.State(w2.Get(4, 3, 15)) == "minecraft:stone" && w2.State(w2.Get(4, 4, 15)) == "minecraft:air" &&
              w2.State(w2.Get(16, 0, 0)).StartsWith("minecraft:oak_log") && w2.State(w2.Get(31, 0, 0)).StartsWith("minecraft:oak_log") &&
              w2.State(w2.Get(16, 0, 1)) == "minecraft:air" && listed.Count == 1, "sections_v1: delta copies the base shifted and applies listed sections");

        bool Throws(Action a) { try { a(); return false; } catch (FormatException) { return true; } }
        var wrongBase = new WorldModel(); wrongBase.ApplySnapshot(s); wrongBase.SnapshotId = 5;
        Check(Throws(() => SectionedSnapshot.Assemble(new[] { delta }, wrongBase, null)), "sections_v1: delta against another snapshot is rejected");
        var missing = Load(fixture, "chunk_snapshot_sections.json");
        missing["sections"] = new List<object> { missing.A("sections")[0] };
        Check(Throws(() => SectionedSnapshot.Assemble(new[] { missing }, null, null)), "sections_v1: a missing section in a full snapshot is rejected");
        var badRun = Load(fixture, "chunk_snapshot_sections.json");
        ((Dictionary<string, object>)badRun.A("sections")[1])["data"] = new List<object> { 1.0, 1000.0 };
        Check(Throws(() => SectionedSnapshot.Assemble(new[] { badRun }, null, null)), "sections_v1: wrong section run total is rejected");
        var gap = Load(fixture, "chunk_snapshot_delta.json");
        gap["sections"] = new List<object>();
        Check(Throws(() => SectionedSnapshot.Assemble(new[] { gap }, w, null)), "sections_v1: delta without the new section is rejected");

        // Parts: two messages, assembled after the last one, decode + plan off the main thread.
        var a = Load(fixture, "chunk_snapshot_sections.json"); var b2 = Load(fixture, "chunk_snapshot_sections.json");
        var secs = a.A("sections");
        a["parts"] = 2.0; b2["parts"] = 2.0; b2["part"] = 1.0;
        a["sections"] = new List<object> { secs[0] }; b2["sections"] = new List<object> { secs[1] };
        var asm = new SnapshotAssembler();
        asm.Offer(a, new WorldModel(), new HashSet<Vector3Int>());
        Check(!asm.Busy && asm.PartsWaiting == 1, "assembler: waits for the last part");
        asm.Offer(b2, new WorldModel(), new HashSet<Vector3Int>());
        var r = asm.Wait();
        Check(r != null && r.Error == null && r.Parts == 2 && r.Snapshot.Grid.Length == 32 * 16 * 16 && r.ViewMargin == new Vector3Int(8, 0, 0) && !r.Plan.Rekey,
              $"assembler: two parts -> one snapshot ({r?.Error})");
        asm.Offer(delta, w, new HashSet<Vector3Int>());
        var rd = asm.Wait();
        Check(rd.Error == null && rd.Delta && rd.Plan.Rekey && rd.Plan.Mesh.Count == 1 && rd.Plan.Kept == 1, $"assembler: delta -> re-key plan ({rd.Error})");
        var v1 = Load(fixture, "chunk_snapshot.json");
        asm.Offer(v1, w, new HashSet<Vector3Int>());
        var r1 = asm.Wait();
        Check(r1.Error == null && r1.Snapshot.Dims == new Vector3Int(32, 64, 32) && r1.ViewMargin == default, "assembler: v1 rle snapshot still works");
        // light_v1 + biomes_v1 inside sections: per-section light, biomes from part 0, deltas keep the base light.
        var lit = Load(fixture, "chunk_snapshot_sections_light.json");
        var ls = SectionedSnapshot.Assemble(new[] { lit }, null, null);
        Check(ls.Light != null && ls.Light[0] == 0 && ls.Light[256] == 240 && ls.Light[1023] == 254 && ls.Biomes != null && ls.Biomes.Palette[0] == "minecraft:plains",
              "sections_v1: per-section light and biomes decode");
        var lw = new WorldModel(); lw.ApplySnapshot(ls);
        var keep = Load(fixture, "chunk_snapshot_sections_light.json");
        keep["snapshot_id"] = 4.0; keep["sections"] = new List<object>(); keep["biomes"] = null;
        keep["base"] = new Dictionary<string, object> { ["snapshot_id"] = 3.0, ["shift"] = new List<object> { 0.0, 0.0, 0.0 } };
        var kd = SectionedSnapshot.Assemble(new[] { keep }, lw, null);
        Check(kd.Light != null && kd.Light[1023] == 254 && kd.Grid[5] == 1, "sections_v1: delta copies the base light");
        var noBaseLight = new WorldModel(); noBaseLight.ApplySnapshot(SectionedSnapshot.Assemble(new[] { Load(fixture, "chunk_snapshot_sections.json") }, null, null));
        var litDelta = Load(fixture, "chunk_snapshot_delta.json"); litDelta["light"] = new Dictionary<string, object> { ["encoding"] = "rle_v1", ["in_sections"] = true };
        Check(Throws(() => SectionedSnapshot.Assemble(new[] { litDelta }, noBaseLight, null)), "sections_v1: light delta against a base without light is rejected");
        // A section whose blocks are the same but whose light changed must be re-meshed (light is baked into the mesh).
        var a0 = Snap(Terrain(new Vector3Int(0, 0, 0), new Vector3Int(32, 16, 16), new[] { "minecraft:air", "minecraft:stone", "minecraft:grass_block" }, Hills), 1);
        a0.Light = new byte[a0.Grid.Length];
        var wa = new WorldModel(); wa.ApplySnapshot(a0); wa.DirtySections.Clear();
        var a1 = Snap(Terrain(new Vector3Int(0, 0, 0), new Vector3Int(32, 16, 16), new[] { "minecraft:air", "minecraft:stone", "minecraft:grass_block" }, Hills, 2), 2);
        a1.Light = new byte[a1.Grid.Length]; a1.Light[(15 * 16 + 3) * 32 + 20] = 0xFE;
        var lp = SectionPlan.Compute(wa, a1, new HashSet<Vector3Int>(), null);
        Check(lp.Rekey && lp.Mesh.Count == 1 && lp.Mesh.Contains(new Vector3Int(1, 0, 0)), $"plan: a light change re-meshes its section ({string.Join(" ", lp.Mesh)})");

        var req = Load(fixture, "region_request_nav.json");
        var built = Protocol.RegionRequest("world", new Vector3Int(16, 60, 0), new Vector3Int(32, 16, 16), new Vector3Int(8, 0, 0));
        Check(Json.Write(built.A("view_margin")) == Json.Write(req.A("view_margin")) && Json.Write(built.A("origin")) == Json.Write(req.A("origin")), "region_request with view_margin matches the fixture");
        Check(Protocol.Hello("").A("caps").Contains(SectionedSnapshot.Cap), "hello advertises sections_v1");
    }

    /// <summary>Faces merge only within one block's texture class (top / bottom / side), like a resource pack would.</summary>
    sealed class TexLike : IFaceSource
    {
        public FaceKey Face(string s, int d) =>
            new FaceKey { Layer = (BlockRules.Path(s) + (d == 3 ? "#top" : d == 2 ? "#bottom" : "#side")).GetHashCode() & 0x7fffffff, Color = new Color32(255, 255, 255, 255) };
    }

    /// <summary>
    /// dotnet run -- --measure-nav DIR: for each rle_palette_v1 snapshot in DIR (tools/nav_e2e.py --save, or
    /// measure_regions.py): decode time, quads with flat colours and with texture-like keys, non-empty sections (draw
    /// calls), single-thread mesh time, the re-key compare for a one-section shift, and the six clip caps.
    /// </summary>
    public static void MeasureNav(string dir)
    {
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var msg = Json.Parse(System.IO.File.ReadAllText(f)) as Dictionary<string, object>;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snap = ChunkSnapshot.From(msg);
            double decodeMs = sw.Elapsed.TotalMilliseconds;
            var w = new WorldModel(); w.ApplySnapshot(snap); w.DirtySections.Clear();
            var n = w.SectionCount;
            var mesh = new MeshData();
            long flat = 0, tex = 0, active = 0, maxSec = 0; double worst = 0;
            sw.Restart();
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                double t0 = sw.Elapsed.TotalMilliseconds;
                var k = new Vector3Int(x, y, z);
                GreedyMesher.Build(w, k, new Flat(), mesh); GreedyMesher.BuildSpecials(w, k, new Flat(), mesh);
                long q = mesh.Verts.Count / 4;
                flat += q; if (q > 0) active++; maxSec = Math.Max(maxSec, q);
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
            }
            double meshMs = sw.Elapsed.TotalMilliseconds;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                var k = new Vector3Int(x, y, z);
                GreedyMesher.Build(w, k, new TexLike(), mesh); GreedyMesher.BuildSpecials(w, k, new TexLike(), mesh);
                tex += mesh.Verts.Count / 4;
            }
            // Re-key compare for a one-section shift in +X (worst case: a full snapshot from a v1 server).
            var d = w.Dims;
            var shifted = new ushort[w.Grid.Length];
            for (int y = 0; y < d.y; y++) for (int z = 0; z < d.z; z++)
                Array.Copy(w.Grid, (y * d.z + z) * d.x + 16, shifted, (y * d.z + z) * d.x, d.x - 16);
            var next = new ChunkSnapshot { SnapshotId = w.SnapshotId + 1, World = w.World, Origin = new Vector3Int(w.Origin.x + 16, w.Origin.y, w.Origin.z), Dims = d, Palette = w.Palette.ToArray(), Grid = shifted };
            sw.Restart();
            var plan = SectionPlan.Compute(w, next, new HashSet<Vector3Int>(), null);
            double planMs = sw.Elapsed.TotalMilliseconds;
            // Clip caps for the window inside the view margin.
            var vm = msg.A("view_margin"); var m = vm != null ? Json.Ints(vm) : new[] { 0, 0, 0 };
            var pal = w.Palette; var keys = new FaceKey[pal.Count * 6]; var caps = new bool[pal.Count];
            for (int i = 0; i < pal.Count; i++)
            {
                var shape = BlockRules.Shape(pal[i]); caps[i] = CapMesher.Caps(shape);
                for (int fc = 0; fc < 6; fc++) { keys[i * 6 + fc] = new TexLike().Face(pal[i], fc); keys[i * 6 + fc].Transparent |= shape != BlockShape.Cube; }
            }
            sw.Restart();
            long capQuads = 0;
            for (int fc = 0; fc < 6; fc++)
            {
                int axis = fc / 2;
                CapMesher.Build(w, fc, CapMesher.Slice(fc, m[axis], d[axis] - m[axis]), keys, caps, mesh);
                capQuads += mesh.Verts.Count / 4;
            }
            double capMs = sw.Elapsed.TotalMilliseconds;
            var est = AreaMath.Estimate(d.x, d.y, d.z);
            Console.WriteLine($"MEASURE_NAV {d.x}x{d.y}x{d.z} margin={m[0]} sections={n.x * n.y * n.z} non_empty={active} quads_flat={flat} quads_texlike={tex} " +
                              $"verts_texlike={tex * 4} max_section_quads={maxSec} decode_ms={decodeMs:F0} mesh_ms_1thread={meshMs:F0} worst_section_ms={worst:F1} " +
                              $"rekey_plan_ms={planMs:F0} kept={plan.Kept} cap_quads={capQuads} caps_ms={capMs:F1} estimate={est.Quads}");
        }
    }

    static void CapTests()
    {
        var pal = new[] { "minecraft:air", "minecraft:stone", "minecraft:water", "minecraft:poppy" };
        var w = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(8, 6, 4), pal, (x, y, z) => y < 2 ? "minecraft:stone" : y == 2 && x < 4 ? "minecraft:water" : y == 3 ? "minecraft:poppy" : "minecraft:air");
        var keys = new FaceKey[pal.Length * 6]; var caps = new bool[pal.Length]; var flat = new Flat();
        for (int i = 0; i < pal.Length; i++)
        {
            var shape = BlockRules.Shape(pal[i]);
            caps[i] = CapMesher.Caps(shape);
            for (int f = 0; f < 6; f++) { keys[i * 6 + f] = flat.Face(pal[i], f); keys[i * 6 + f].Transparent |= shape != BlockShape.Cube; }
        }
        var m = new MeshData();
        CapMesher.Build(w, 0, 0, keys, caps, m);   // -X side, slice x=0: stone y<2 (2x4 cells) + water at y=2 (1x4)
        Check(m.Verts.Count == 8 && m.Opaque.Count == 6 && m.Transparent.Count == 6, $"caps: -X slice = 1 stone quad + 1 water quad (got {m.Verts.Count / 4})");
        Check(m.Verts.All(v => v.x == 0), "caps: plane axis at 0");
        CapMesher.Build(w, 1, 7, keys, caps, m);   // +X side at x=7: no water there
        Check(m.Verts.Count == 4 && m.Transparent.Count == 0, "caps: +X slice = 1 stone quad");
        CapMesher.Build(w, 3, 3, keys, caps, m);   // top cut through the poppies: crosses get no cap
        Check(m.Verts.Count == 0, "caps: no caps for non-full blocks");
        CapMesher.Build(w, 2, 0, keys, caps, m);   // bottom: 8x4 stone = one quad
        Check(m.Verts.Count == 4, "caps: bottom merges into one quad");
        // Caps use the same UV layout as the mesher's faces (vanilla orientation per side), so a cut looks like the side next to it.
        var one = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(1, 1, 1), new[] { "minecraft:air", "minecraft:stone" }, (x, y, z) => "minecraft:stone");
        var gm = new MeshData(); GreedyMesher.Build(one, new Vector3Int(0, 0, 0), flat, gm);
        bool uvSame = true;
        for (int f = 0; f < 6; f++)
        {
            var cm = new MeshData();
            CapMesher.Build(one, f, 0, keys, caps, cm);   // same palette order: 0 air, 1 stone
            var capUv = cm.Uvs.Select(u => (u.x, u.y)).OrderBy(t => t).ToList();
            // The greedy quad for this side: the 4 vertices whose position sits on that face's plane.
            int axis = f / 2; float plane = (f & 1) == 1 ? 1 : 0;
            var faceUv = Enumerable.Range(0, gm.Verts.Count / 4).Select(q => Enumerable.Range(q * 4, 4).ToList())
                .First(ix => ix.All(i => gm.Verts[i][axis] == plane)).Select(i => (gm.Uvs[i].x, gm.Uvs[i].y)).OrderBy(t => t).ToList();
            uvSame &= capUv.SequenceEqual(faceUv);
        }
        Check(uvSame, "caps: UVs match the mesher's faces on all six sides");
        Check(CapMesher.Slice(0, 2.3f, 9f) == 2 && CapMesher.Slice(1, 0f, 5f) == 4 && CapMesher.Slice(1, 0f, 5.2f) == 5 && CapMesher.Slice(0, 3f, 9f) == 3, "caps: slice under each plane");
    }
}
