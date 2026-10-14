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
        n.OnRegion("world", new Vector3Int(16, 0, 0), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 1.5f);
        Check(!n.Pending && Near(n.View.x, 64) && Near(n.Offset.x, 0), "nav: shifted snapshot keeps the view in the world (seamless)");
        n.OnSettled(1.6f);   // the renderer finished meshing the new sections 0.5 s after the request
        Check(n.LatencyEma > 0.49f && n.LatencyEma < 0.51f, $"nav: latency runs until the new terrain is meshed ({n.LatencyEma:F2} s)");
        n.OnSettled(5f);
        Check(n.LatencyEma < 0.51f, "nav: settling twice doesn't count twice");
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
        Check(n.Move(new Vector3(-1, 0, 0), 0.1f, 12f).HasValue, "nav: a lost answer times out");

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
