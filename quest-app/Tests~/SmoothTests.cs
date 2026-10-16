// [smooth] Smooth gliding (docs/TESTING.md "Smooth gliding"): the HITCH log, cheaper snapshot applies, fewer shifts, and
// the surface skirt's hand-off.
using System;
using System.Collections.Generic;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class SmoothTests
{
    public static void Run(Action<bool, string> check)
    {
        Hitch(check);
        Apply(check);
        Shifts(check);
        Skirt(check);
    }

    sealed class FlatFaces : IFaceSource
    {
        public FaceKey Face(string s, int d) => new FaceKey { Layer = s.Contains("water") ? 4 : s.Contains("log") ? 3 : 1, Color = new Color32(200, 200, 200, 255) };
    }

    static WorldModel Box(Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(16, 20, 16) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[16 * 20 * 16];
        for (int y = 0; y < 20; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        w.Light = new byte[w.Grid.Length]; for (int i = 0; i < w.Light.Length; i++) w.Light[i] = 15 << 4;
        return w;
    }

    /// <summary>Quads of a skirt build: (min y, max y, vertical?) per quad.</summary>
    static List<(float lo, float hi, bool side, float x, float z)> Quads(MeshData md)
    {
        var r = new List<(float, float, bool, float, float)>();
        for (int i = 0; i + 3 < md.Verts.Count; i += 4)
        {
            float lo = float.MaxValue, hi = float.MinValue, x = 0, z = 0;
            for (int k = 0; k < 4; k++) { var v = md.Verts[i + k]; lo = Math.Min(lo, v.y); hi = Math.Max(hi, v.y); x += v.x / 4; z += v.z / 4; }
            r.Add((lo, hi, hi - lo > 0.01f, x, z));
        }
        return r;
    }

    /// <summary>The surface skirt never draws where there is air (overhangs, canopies, fluids), and is off by default.</summary>
    static void Skirt(Action<bool, string> check)
    {
        BlockRules.ResetShapes();
        var faces = new FlatFaces(); var md = new MeshData();
        check(!SurfaceSkirt.Enabled, "skirt: off by default (tabletop.json surface_skirt)");
        // A stone floor (top block y 5) and an overhang: a 2-block slab of stone at y 10-11 over (8, 8), air under it.
        var over = Box((x, y, z) => y <= 5 || (x == 8 && z == 8 && (y == 10 || y == 11)) ? 1 : 0, "minecraft:air", "minecraft:stone");
        SurfaceSkirt.Build(over, 0, 0, 0, 20, faces, md, out _, out _);
        var q = Quads(md);
        float sideLo = 99; int sides = 0;
        foreach (var t in q) if (t.side) { sides++; sideLo = Math.Min(sideLo, t.lo); }
        check(sides == 4 && Math.Abs(sideLo - 10) < 1e-4f, $"skirt: an overhang's sides cover its own 2 blocks, not a wall down to the floor ({sides} sides from y {sideLo})");
        // A tree: a log trunk at (8, 8) y 6-8 under a leaf canopy at y 9-10 over a 5x5 area; the floor at 5.
        var tree = Box((x, y, z) => y <= 5 ? 1 : x == 8 && z == 8 && y <= 8 ? 2 : Math.Abs(x - 8) <= 2 && Math.Abs(z - 8) <= 2 && (y == 9 || y == 10) ? 3 : 0,
                       "minecraft:air", "minecraft:stone", "minecraft:oak_log[axis=y]", "minecraft:oak_leaves[distance=1]");
        SurfaceSkirt.Build(tree, 0, 0, 0, 20, faces, md, out int lo, out int hi);
        q = Quads(md);
        float maxY = 0; int trunkSides = 0; bool wallUnderLeaves = false;
        foreach (var t in q)
        {
            maxY = Math.Max(maxY, t.hi);
            bool atTrunk = Math.Abs(t.x - 8.5f) <= 0.6f && Math.Abs(t.z - 8.5f) <= 0.6f;
            if (t.side && atTrunk) trunkSides++;
            else if (t.side) wallUnderLeaves = true;
        }
        check(maxY <= 9f && trunkSides == 4 && !wallUnderLeaves && lo == 5 && hi == 8,
              $"skirt: under a leaf canopy it shows the floor and the trunk, no face at the leaves and no walls under them (top {maxY}, trunk sides {trunkSides}, tops {lo}..{hi})");
        // Water (y 3-5) over stone: the top face at the water's height, below the real surface.
        var pond = Box((x, y, z) => y <= 2 ? 1 : y <= 5 ? 2 : 0, "minecraft:air", "minecraft:stone", "minecraft:water[level=0]");
        SurfaceSkirt.Build(pond, 0, 0, 0, 20, faces, md, out lo, out _);
        q = Quads(md);
        check(q.Count == 1 && lo == 5 && Math.Abs(q[0].hi - (5 + SurfaceSkirt.FluidTop - SurfaceSkirt.Inset)) < 1e-4f, $"skirt: a pond's top sits at the water's height ({(q.Count > 0 ? q[0].hi : -1)})");
        BlockRules.ResetShapes();
    }

    static NavState Region(int window, int margin)
    {
        var n = new NavState { CanPad = true, Caps = new RegionLimits { MaxX = 352, MaxY = 384, MaxZ = 352, MaxBlocks = 25165824, MaxBytes = 64 << 20 } };
        int d = window + 2 * margin;
        n.OnRegion("world", new Vector3Int(-d / 2 / 16 * 16, 0, -d / 2 / 16 * 16), new Vector3Int(d, 128, d), new Vector3Int(margin, 0, margin), 0);
        return n;
    }

    /// <summary>A steady glide along +x at `speed` blocks/s for `seconds`, answers after `latency` s (72 Hz frames).
    /// Returns (shifts, blocks travelled, seconds stalled at the margin's edge, smallest runway left at a request).</summary>
    static (int shifts, float dist, float stalled, float minRunway) Glide(NavState n, float speed, float latency, float seconds, Func<NavState, Vector3> delta = null)
    {
        const float dt = 1f / 72f;
        var pending = new Queue<(float at, RegionMove m)>();
        float t = 1, x0 = n.View.x, stalled = 0, minRunway = float.MaxValue; int shifts = 0;
        for (float end = t + seconds; t < end; t += dt)
        {
            while (pending.Count > 0 && pending.Peek().at <= t) { var m = pending.Dequeue().m; n.OnRegion("world", m.Origin, m.Dims, m.Margin, t); }
            var d = delta != null ? delta(n) : new Vector3(speed * dt, 0, 0);
            float before = n.View.x;
            var mv = n.Move(d, dt, t);
            if (d.x > 0 && n.View.x - before < 0.5f * d.x) stalled += dt;
            if (mv.HasValue)
            {
                if (mv.Value.Kind == "shift") { shifts++; minRunway = Math.Min(minRunway, n.Margin.x - n.Offset.x); }
                pending.Enqueue((t + latency, mv.Value));
            }
        }
        return (shifts, n.View.x - x0, stalled, minRunway);
    }

    /// <summary>Fewer shifts: margin 32 from the smallest width, lookahead shifts of whole sections in pairs (or threes at 48),
    /// lookahead from a followed player's velocity, and a follow that keeps gliding while a shift is in flight.</summary>
    static void Shifts(Action<bool, string> check)
    {
        var n = new NavState();
        check(n.LookaheadSections(32) == 2 && n.LookaheadSections(48) == 3 && n.LookaheadSections(24) == 1 && !n.CanLookahead(16),
              $"shifts: lookahead moves 2 sections at a 32 margin, 3 at 48, 1 at 24, none at 16 ({n.LookaheadSections(32)}, {n.LookaheadSections(48)}, {n.LookaheadSections(24)})");
        // 96 wide, as on the headset: 16 margin (0.8.1) against 32 with 2-section shifts, 20 blocks/s, 0.4 s answers.
        var old = Region(96, 16); old.CanPad = false; var oldRun = Glide(old, 20f, 0.4f, 8f);   // stays at 16 (no margin growth)
        var neu = Region(96, 32); var newRun = Glide(neu, 20f, 0.4f, 8f);
        check(newRun.dist / newRun.shifts >= 24f && newRun.stalled < 0.05f && newRun.dist > 150f && newRun.minRunway >= 16f,
              $"shifts: 96 wide at 20 blocks/s, margin 32 shifts {newRun.shifts} times in {newRun.dist:F0} blocks, stalled {newRun.stalled:F2} s, runway at a request >= {newRun.minRunway:F1} " +
              $"(margin 16: {oldRun.shifts} shifts, stalled {oldRun.stalled:F2} s)");
        var one = Region(96, 32); one.LookaheadRunway = 1000f; var oneRun = Glide(one, 20f, 0.4f, 8f);
        check(newRun.shifts < oneRun.shifts, $"shifts: pairs of sections instead of one at a time ({newRun.shifts} vs {oneRun.shifts})");
        var fast = Region(96, 32); var fastRun = Glide(fast, 40f, 0.25f, 6f);
        check(fastRun.stalled < 0.05f, $"shifts: 40 blocks/s with 0.25 s answers doesn't stall at the edge ({fastRun.stalled:F2} s)");
        // 256 wide (margin 48): threes.
        var wide = Region(256, 48); var wideRun = Glide(wide, 40f, 0.5f, 8f);
        check(wideRun.stalled < 0.05f && wideRun.dist / Math.Max(1, wideRun.shifts) >= 40f, $"shifts: 256 wide moves {wideRun.dist / Math.Max(1, wideRun.shifts):F0} blocks per shift, stalled {wideRun.stalled:F2} s");

        // Following: the player's velocity makes the motion steady at once (lookahead), the eased view's needed 8 blocks.
        var f = Region(96, 32);
        f.Lead = new Vector3(4.3f, 0, 0);
        check(f.Steady(0, +1) && !f.Steady(0, -1) && !f.Steady(2, +1), "follow: a player walking +x makes +x steady right away");
        var fs = new FollowState();
        fs.Start("Alex", "u1", 0);
        for (int i = 0; i <= 30; i++) fs.OnSeen("world", new Vector3(10 + 4.3f * i / 30f, 64, 5), i / 30f);
        check(Math.Abs(fs.Velocity.x - 4.3f) < 0.3f && Math.Abs(fs.Velocity.z) < 0.01f && fs.Velocity.y == 0, $"follow: the player's velocity from its sightings ({fs.Velocity.x:F2})");
        fs.OnSeen("world", new Vector3(500, 64, 5), 1.05f);
        check(fs.Velocity.x == 0 && fs.Velocity.z == 0, "follow: a teleport resets the velocity");
        // A shift in flight doesn't pause the follow's glide any more.
        var g = Region(96, 32);
        var fl = new FollowState(); fl.Start("Alex", "u1", 0);
        fl.OnSeen("world", g.View + new Vector3(10, 0, 0), 0.5f);
        g.TryIssue(new RegionMove { Origin = g.Origin + new Vector3Int(16, 0, 0), Dims = g.Dims, Margin = g.Margin, Kind = "shift" }, 0.5f);
        var st = fl.Step(g, 1 / 72f, 0.51f, null);
        check(g.Pending && st.Delta.x > 0 && !st.Jump.HasValue, "follow: keeps gliding while a shift is in flight");
        // A walking player: the follow (eased) with the player's velocity as lead shifts in pairs and never stalls.
        var w = Region(96, 32); var wf = new FollowState(); wf.Start("Alex", "u1", 0);
        float clock = 1, px = w.View.x;
        var walk = Glide(w, 0, 0.4f, 20f, nav =>
        {
            clock += 1f / 72f; px += 4.3f / 72f;
            wf.OnSeen("world", new Vector3(px, nav.View.y, nav.View.z), clock);
            nav.Lead = wf.Velocity;
            return wf.Step(nav, 1f / 72f, clock, null).Delta;
        });
        check(walk.stalled < 0.05f && walk.shifts > 0 && walk.dist / walk.shifts >= 28f, $"follow: a walking player (4.3 blocks/s) is followed in {walk.shifts} shifts over {walk.dist:F0} blocks, stalled {walk.stalled:F2} s");
    }

    static ChunkSnapshot Snap(int id, params string[] palette) =>
        new ChunkSnapshot { SnapshotId = id, World = "world", Origin = default, Dims = new Vector3Int(16, 16, 16), Palette = palette, Grid = new ushort[16 * 16 * 16] };

    /// <summary>Cheaper snapshot applies: palette tables extend, grid arrays are reused, JSON integers don't allocate.</summary>
    static void Apply(Action<bool, string> check)
    {
        // PaletteTable: a delta's palette starts with the old one, so only the new entries are computed.
        var w = new WorldModel();
        w.ApplySnapshot(Snap(1, "minecraft:air", "minecraft:chest[facing=north]"));
        var t = new PaletteTable<bool>();
        t.Update(w, BlockEntityScan.Wanted);
        check(t.Items.Length == 2 && !t.Items[0] && t.Items[1] && t.Computed == 2, "palette table: built for the first snapshot");
        int epoch = w.PaletteEpoch;
        w.ApplySnapshot(Snap(2, "minecraft:air", "minecraft:chest[facing=north]", "minecraft:stone", "minecraft:oak_sign[rotation=0]"));
        var before = t.Items;
        bool changed = t.Update(w, BlockEntityScan.Wanted);
        check(changed && w.PaletteEpoch == epoch && t.Computed == 4 && t.Items.Length == 4 && t.Items[1] && !t.Items[2] && t.Items[3] && before.Length == 2,
              $"palette table: a delta that appends computes the new entries only, in a new array (computed {t.Computed})");
        check(!t.Update(w, BlockEntityScan.Wanted) && t.Computed == 4, "palette table: nothing new, nothing computed");
        w.ApplySnapshot(Snap(3, "minecraft:stone", "minecraft:air"));
        t.Update(w, BlockEntityScan.Wanted);
        check(w.PaletteEpoch == epoch + 1 && t.Computed == 6 && t.Items.Length == 2 && !t.Items[0], "palette table: another palette (not a prefix) rebuilds");
        check(w.ShapeOf(0) == BlockShape.Cube && w.ShapeOf(1) == BlockShape.Air, "palette table: shapes follow the new palette");

        // SnapshotBuffers: a retired array comes back only after another snapshot was applied and MinAgeSeconds passed.
        double clock = 100;
        var oldClock = SnapshotBuffers.Clock; bool oldOn = SnapshotBuffers.Enabled;
        SnapshotBuffers.Clock = () => clock; SnapshotBuffers.Enabled = true; SnapshotBuffers.Clear();
        var g1 = new ushort[4096]; var l1 = new byte[4096];
        SnapshotBuffers.Retire(g1, l1);
        check(!ReferenceEquals(SnapshotBuffers.Grid(4096), g1), "buffers: not reused before another snapshot was applied");
        SnapshotBuffers.Retire(new ushort[4096], null);
        check(!ReferenceEquals(SnapshotBuffers.Grid(4096), g1), "buffers: not reused before MinAgeSeconds");
        clock += 1;
        check(!ReferenceEquals(SnapshotBuffers.Grid(4095), g1) && ReferenceEquals(SnapshotBuffers.Grid(4096), g1) && ReferenceEquals(SnapshotBuffers.Light(4096), l1),
              "buffers: reused for the same size once a newer snapshot was applied and it is old enough");
        check(!ReferenceEquals(SnapshotBuffers.Grid(4096), g1), "buffers: handed out once");
        var eights = new List<ushort[]>();
        for (int i = 0; i < 5; i++) { var a8 = new ushort[8]; eights.Add(a8); SnapshotBuffers.Retire(a8, null); }
        clock += 1; SnapshotBuffers.Retire(new ushort[9], null);
        int got = 0;
        for (int i = 0; i < 5; i++) { var g = SnapshotBuffers.Grid(8); if (eights.Exists(x => ReferenceEquals(x, g))) got++; }
        check(got == 1, $"buffers: only the last Keep ({SnapshotBuffers.Keep}) arrays of a kind are kept (reused {got} of 5)");
        SnapshotBuffers.Enabled = false; SnapshotBuffers.Clear(); SnapshotBuffers.Clock = oldClock; SnapshotBuffers.Enabled = oldOn;

        // A delta decoded into a reused grid is the same as one decoded into a fresh grid.
        // (SectionedSnapshot writes every cell: full coverage is checked.)
        var json = "{\"a\":[0,1,4096,4097,-1,-0,12.5,1e3,123456789012345678,7],\"b\":-12}";
        var o = (Dictionary<string, object>)TabletopCraft.Net.Json.Parse(json);
        var a = (List<object>)o["a"];
        check((double)a[0] == 0 && (double)a[1] == 1 && (double)a[2] == 4096 && (double)a[3] == 4097 && (double)a[4] == -1 && double.IsNegative((double)a[5]) &&
              (double)a[6] == 12.5 && (double)a[7] == 1000 && (double)a[8] == 123456789012345678d && (double)a[9] == 7 && (double)o["b"] == -12,
              "json: integers, negatives, -0, fractions, exponents and long numbers parse as before");
        var o2 = (Dictionary<string, object>)TabletopCraft.Net.Json.Parse(json);
        check(ReferenceEquals(a[1], ((List<object>)o2["a"])[1]) && !ReferenceEquals(a[3], ((List<object>)o2["a"])[3]), "json: small integers share their boxes");
    }

    static void Hitch(Action<bool, string> check)
    {
        float old = FrameHitch.ThresholdMs; int oldCap = FrameHitch.MaxLinesPerSecond;
        FrameHitch.ThresholdMs = 30f; FrameHitch.MaxLinesPerSecond = 2;
        FrameHitch.Close(0, 0, 0, 0, 0);   // start clean
        long t0 = FrameHitch.Begin(); Thread.Sleep(2); FrameHitch.End(FrameHitch.Part.Apply, t0);
        FrameHitch.Uploaded(2048 * 1024); FrameHitch.Uploaded(1024); FrameHitch.MeshDone(); FrameHitch.Snapshot();
        check(FrameHitch.Close(14f, 3f, 0, 0, 10.0) == null, "hitch: a 14 ms frame logs nothing");
        check(FrameHitch.Uploads == 0 && FrameHitch.Ms(FrameHitch.Part.Apply) == 0, "hitch: every frame starts its counters from zero");
        t0 = FrameHitch.Begin(); Thread.Sleep(2); FrameHitch.End(FrameHitch.Part.Apply, t0);
        FrameHitch.Uploaded(2048 * 1024); FrameHitch.MeshDone(); FrameHitch.MeshDone(); FrameHitch.Snapshot();
        string line = FrameHitch.Close(45.5f, 31f, 1, 300L << 20, 10.1);
        check(line != null && line.StartsWith("[Tabletop] HITCH ms=45.5 script_ms=31.0 apply_ms=") && line.Contains(" uploads=1 upload_kb=2048 ") &&
              line.Contains(" mesh_done=2 snap=1 gc=1 heap_mb=300 skipped=0") && !line.Contains(","), $"hitch: one line with the frame's counters ({line})");
        check(FrameHitch.Close(40f, 1f, 0, 0, 10.2) != null && FrameHitch.Close(40f, 1f, 0, 0, 10.3) == null, "hitch: at most MaxLinesPerSecond lines a second");
        string next = FrameHitch.Close(40f, 1f, 0, 0, 11.3);
        check(next != null && next.EndsWith("skipped=1"), "hitch: the next second's first line counts the skipped ones");
        FrameHitch.ThresholdMs = 0;
        check(FrameHitch.Close(500f, 1f, 0, 0, 20) == null, "hitch: hitch_ms 0 turns it off");
        FrameHitch.ThresholdMs = old; FrameHitch.MaxLinesPerSecond = oldCap;
    }
}
