// [area] Engine-free tests for the visible-width presets (World/AreaSizing.cs), resize re-keying (SectionPlan,
// sections_v1 resize deltas, PROTOCOL.md 4.25) and mesh batching (World/SectionBatches.cs), plus two tools:
//   --measure-batches DIR   draw calls, renderers and vertices per batch size for snapshots saved by tools/area_e2e.py
//   --live-resize URL       resize a live session 64 -> 256 -> 96 and check each delta-assembled region against a full resend
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class AreaTests
{
    static Action<bool, string> Check;
    static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;

    sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

    static readonly RegionLimits Server = new RegionLimits { MaxX = 288, MaxY = 384, MaxZ = 288, MaxBlocks = 1 << 24, MaxBytes = 32 << 20, MaxViewWidth = 256 };

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Check = check;
        PresetTests();
        PlanTests();
        ResizeRekeyTests(fixture);
        LayoutTests();
        SectionMeshTests();
        BatchTests();
        SolidSplitTests();
        PackedVertexTests();
    }

    /// <summary>[gpu] PackedVertex (20 bytes) against the classic BatchVertex: positions exact in 1/64 steps, uv in 1/1024,
    /// light code, face direction and shade (multiplied into the colour as the classic shader multiplies it), layer and
    /// table out of uv.z; a batch whose coordinates don't fit stays classic.</summary>
    static void PackedVertexTests()
    {
        var md = new MeshData();
        // An up face with random table 5 and a light code, a south face on layer 7 (alpha 128), a flat-colour east face,
        // and a 45 degree quad (no face shade, no direction).
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float z, Color32 col, byte light)
        {
            int s = md.Verts.Count;
            md.Verts.Add(a); md.Verts.Add(b); md.Verts.Add(c); md.Verts.Add(d);
            md.Uvs.Add(new Vector3(a.x + 0.0625f, a.z, z)); md.Uvs.Add(new Vector3(b.x, b.z, z)); md.Uvs.Add(new Vector3(c.x, c.z - 3.5f, z)); md.Uvs.Add(new Vector3(d.x, -d.z, z));
            for (int k = 0; k < 4; k++) { md.Colors.Add(col); md.Light.Add(light); }
            md.Opaque.AddRange(new[] { s, s + 2, s + 1, s, s + 3, s + 2 });
        }
        Quad(new Vector3(1, 5, 2), new Vector3(4, 5, 2), new Vector3(4, 5, 6), new Vector3(1, 5, 6), 3 + RandomUv.LayerStride * 5, new Color32(200, 100, 50, 255), 0xE7);
        Quad(new Vector3(0, 0, 9), new Vector3(16, 0, 9), new Vector3(16, 15.4375f, 9), new Vector3(0, 15.4375f, 9), 7, new Color32(255, 255, 255, 128), 0x0F);
        Quad(new Vector3(3, 1, 1), new Vector3(3, 1, 2), new Vector3(3, 2, 2), new Vector3(3, 2, 1), -1, new Color32(90, 90, 90, 255), 0xF0);
        Quad(new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 0), 2, new Color32(10, 20, 30, 255), 0x55);
        var sm = SectionMesh.From(md, new Vector3Int(1, 2, 3));
        var off = new Vector3(32, 48, -32);
        var classic = BatchData.Assemble(new List<(SectionMesh, Vector3)> { (sm, off) });
        var packed = BatchData.Assemble(new List<(SectionMesh, Vector3)> { (sm, off) }, true);
        bool ok = packed.Packed != null && packed.Verts.Length == 0 && packed.Bytes == (long)packed.VertCount * 20 + packed.IndexCount * 4
                  && (packed.Min - classic.Min).sqrMagnitude == 0 && (packed.Max - classic.Max).sqrMagnitude == 0 && packed.Indices.Take(packed.IndexCount).SequenceEqual(classic.Indices.Take(classic.IndexCount));
        string bad = ok ? "" : $" (header: packed {packed.Packed != null}, verts {packed.Verts.Length}, bytes {packed.Bytes}, min {packed.Min} {classic.Min})";
        for (int i = 0; ok && i < classic.VertCount; i++)
        {
            var c = classic.Verts[i]; var p = packed.Packed[i];
            float shade = PackedVertex.Shade(c.Nx, c.Ny, c.Nz, out int dir);
            int z = (int)Math.Round(c.Uv.z);
            bool v = (p.Position - c.Pos).sqrMagnitude < 1e-10f && Math.Abs(p.Uv.x - c.Uv.x) < 1e-6f && Math.Abs(p.Uv.y - c.Uv.y) < 1e-6f
                     && p.Light == LightCode.FromSnorm(c.Nw) && p.Dir == dir && p.Layer == (z < 0 ? -1 : z % RandomUv.LayerStride) && p.Table == (z < 0 ? 0 : z / RandomUv.LayerStride)
                     && p.Color.r == (byte)Math.Round(c.Color.r * shade) && p.Color.a == c.Color.a;
            if (!v) { ok = false; bad = $" vertex {i}: {p.Position} {p.Uv} light {p.Light} dir {p.Dir} layer {p.Layer}/{p.Table} {p.Color} vs {c.Pos} {c.Uv} {c.Color} shade {shade}"; }
        }
        var dirs = packed.Packed.Take(packed.VertCount).Select(p => p.Dir).Distinct().OrderBy(d => d).ToArray();
        var shades = packed.Packed.Take(packed.VertCount).Select(p => p.Color.r).Distinct().OrderBy(r => r).ToArray();
        Check(ok && dirs.SequenceEqual(new[] { 0, 2, 4, 6 }) && shades.SequenceEqual(new byte[] { 10, 54, 200, 204 }),
              $"packed vertices: 20 bytes, positions/uv exact, light codes, face directions {string.Join(",", dirs)} and shades {string.Join(",", shades)} as the classic shader{bad}");
        var far = BatchData.Assemble(new List<(SectionMesh, Vector3)> { (sm, new Vector3(0, 600, 0)) }, true);
        Check(far.Packed == null && far.Verts.Length >= far.VertCount && far.Max.y > 512, $"a batch out of the packed range (600 blocks up) stays classic (packed {far.Packed != null}, verts {far.Verts.Length}/{far.VertCount}, max y {far.Max.y})");
        classic.Release(); packed.Release(); far.Release();
    }

    /// <summary>[gpu] Opaque faces on fully opaque layers (and flat colours) with opaque vertex colours go to the solid range
    /// (drawn without alpha test), first and in mesher order; the rest stay alpha-tested. Batches keep the split.</summary>
    static void SolidSplitTests()
    {
        SolidLayers.Clear();
        SolidLayers.Set(0, true); SolidLayers.Set(1, false); SolidLayers.Set(2, true); SolidLayers.Set(3, true); SolidLayers.Set(5, true);
        // quad layer (uv.z), vertex alpha, expected solid: layer 0; layer 1 (holes); flat colour; layer 0 with alpha 200;
        // layer 0 with random table 3; [flow] table 64 over layers 0..3 (1 has holes); table 64 over 2..5 (4 unknown);
        // a layer the atlas never published.
        var quads = new (float z, byte a, bool solid)[]
        {
            (0, 255, true), (1, 255, false), (-1, 255, true), (0, 200, false), (0 + RandomUv.LayerStride * 3, 255, true),
            (0 + RandomUv.LayerStride * FluidRules.QuadTable, 255, false), (2 + RandomUv.LayerStride * FluidRules.QuadTable, 255, false), (40, 255, false),
        };
        var md = new MeshData();
        for (int q = 0; q < quads.Length; q++)
        {
            int s = md.Verts.Count;
            md.Verts.Add(new Vector3(q, 1, 0)); md.Verts.Add(new Vector3(q + 1, 1, 0)); md.Verts.Add(new Vector3(q + 1, 1, 1)); md.Verts.Add(new Vector3(q, 1, 1));
            for (int k = 0; k < 4; k++) { md.Uvs.Add(new Vector3(k & 1, k >> 1, quads[q].z)); md.Colors.Add(new Color32(200, 200, 200, quads[q].a)); }
            md.Opaque.AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
        }
        var sm = SectionMesh.From(md, new Vector3Int(0, 0, 0));
        int wantSolid = quads.Count(q => q.solid) * 6;
        int Quad(int index) => index / 4;
        var order = Enumerable.Range(0, sm.Opaque / 6).Select(t => Quad(sm.Indices[t * 6])).ToList();
        var want = Enumerable.Range(0, quads.Length).Where(q => quads[q].solid).Concat(Enumerable.Range(0, quads.Length).Where(q => !quads[q].solid)).ToList();
        Check(sm.Solid == wantSolid && sm.Opaque == quads.Length * 6 && order.SequenceEqual(want),
              $"solid split: {sm.Solid / 6} of {quads.Length} quads solid (flat colours, opaque layers, random tables), first and in order (got {sm.Solid / 6}: {string.Join(",", order)})");
        var b = BatchData.Assemble(new List<(SectionMesh, Vector3)> { (sm, Vector3.zero), (sm, new Vector3(16, 0, 0)) });
        bool firstSolid = Enumerable.Range(0, b.Solid).All(i => quads[Quad(b.Indices[i] % (quads.Length * 4))].solid);
        bool restCut = Enumerable.Range(b.Solid, b.Opaque - b.Solid).All(i => !quads[Quad(b.Indices[i] % (quads.Length * 4))].solid);
        Check(b.Solid == 2 * wantSolid && firstSolid && restCut && b.DrawCalls(false) == 2, "a batch keeps every section's solid faces first (one solid and one alpha-tested draw)");
        SolidLayers.Enabled = false;
        var off = SectionMesh.From(md, new Vector3Int(0, 0, 0));
        SolidLayers.Enabled = true;
        Check(off.Solid == 0 && off.Opaque == quads.Length * 6, "solid_pass false: every opaque face alpha-tested, as before");
        SolidLayers.Clear();
    }

    static NavState Nav(Vector3Int origin, Vector3Int dims, Vector3Int margin)
    {
        var n = new NavState { CanPad = true };
        n.OnRegion("world", origin, dims, margin, 0);
        return n;
    }

    static void PresetTests()
    {
        Check(AreaSizing.Presets(Server, 16).SequenceEqual(new[] { 64, 96, 128, 192, 256 }), "area size: all presets on a default server (max view 256, max dims 288)");
        var small = Server; small.MaxViewWidth = 96;
        Check(AreaSizing.Presets(small, 16).SequenceEqual(new[] { 64, 96 }) && AreaSizing.MaxWidth(small, 16) == 96, "area size: max_view_width 96 -> presets 64, 96");
        var old = RegionLimits.Default;   // no max_view_width: max_dims 128 minus the margins
        Check(AreaSizing.MaxWidth(old, 16) == 96 && AreaSizing.Presets(old, 16).SequenceEqual(new[] { 64, 96 }), "area size: without max_view_width the max is max_dims - 2 * margin");
        var tiny = Server; tiny.MaxViewWidth = 40;
        Check(AreaSizing.Presets(tiny, 16).SequenceEqual(new[] { 40 }), "area size: a server max below every preset leaves one preset at the max");
        var p = AreaSizing.Presets(Server, 16);
        Check(AreaSizing.Next(p, 64) == 96 && AreaSizing.Next(p, 192) == 256 && AreaSizing.Next(p, 256) == 64 && AreaSizing.Next(p, 100) == 128 && AreaSizing.Next(p, 0) == 64,
              "area size: next preset steps up and wraps");
        Check(AreaSizing.Snap(100, 256) == 96 && AreaSizing.Snap(300, 256) == 256 && AreaSizing.Snap(3, 256) == 16 && AreaSizing.Snap(250, 200) == 192, "area size: slider values snap to whole sections and the max");
        Check(Near(AreaSizing.ScaleBlockSize(0.01f, 64, 256), 0.0025f) && Near(AreaSizing.ScaleBlockSize(0.004f, 256, 64), 0.016f), "area size: block size scales by old/new width (footprint kept)");
        Check(Near(AreaSizing.ScaleBlockSize(0.003f, 64, 256), 0.002f) && Near(AreaSizing.ScaleBlockSize(0.05f, 256, 64), 0.1f), "area size: block size stays in 0.002-0.1 m");
        Check(AreaSizing.Current(new NavState()) == 0 && AreaSizing.Current(Nav(new Vector3Int(0, 0, 0), new Vector3Int(96, 128, 160), new Vector3Int(16, 16, 16))) == 128,
              "area size: current width = the window's larger side");
        var st = AreaSizing.Describe(128, p, 256, 0);
        Check(st.Contains("128") && st.Contains("max 256") && st.Contains("64/96/128/192/256"), $"area size: status shows width, presets, server max ({st})");

        var f = new FrameStats(8);
        foreach (var ms in new[] { 14f, 13f, 30f, 12f, 14f, 15f, 13f, 14f, 13f, 100f }) f.Add(ms);   // ring of 8 keeps the last 8
        Check(f.Count == 8 && f.Percentile(0.5f) == 14f && f.Percentile(1f) == 100f && f.Max() == 100f && f.Over(15f) == 2, $"frame stats: percentiles over the last samples (p50 {f.Percentile(0.5f)})");
    }

    static void PlanTests()
    {
        // A 64-wide window (96 region with 16 margin) at the origin, view in the middle.
        var n = Nav(new Vector3Int(0, 16, 0), new Vector3Int(96, 128, 96), new Vector3Int(16, 16, 16));
        var grow = AreaSizing.Plan(n, 256, Server).Value;
        Check(grow.Kind == "resize" && grow.Dims == new Vector3Int(288, 128, 288) && grow.Margin == new Vector3Int(16, 16, 16) && grow.Origin == new Vector3Int(-96, 16, -96),
              $"plan: 64 -> 256 keeps height and margin, centred ({grow.Origin} {grow.Dims})");
        // Off-centre view: the origin still moves by whole sections, and the view stays inside the new margin.
        for (float vx = 32; vx <= 64; vx += 3.7f)
            for (float vz = 33; vz <= 63; vz += 5.3f)
            {
                n.View = new Vector3(vx, n.View.y, vz);
                var r = AreaSizing.Plan(n, 192, Server).Value;
                float cx = r.Origin.x + r.Dims.x / 2f, cz = r.Origin.z + r.Dims.z / 2f;
                if ((r.Origin.x - n.Origin.x) % 16 != 0 || (r.Origin.z - n.Origin.z) % 16 != 0 || Math.Abs(cx - vx) > 8 || Math.Abs(cz - vz) > 8)
                { Check(false, $"plan: view {vx},{vz} -> origin {r.Origin} centre {cx},{cz}"); return; }
            }
        Check(true, "plan: any view -> section-aligned origin, window centred to within 8 blocks");
        n.View = n.RegionCentre;
        var same = AreaSizing.Plan(n, 64, Server);   // [smooth] same width: only the margin grows to MarginFor(64) = 32
        Check(same.HasValue && same.Value.Dims == new Vector3Int(128, 128, 128) && same.Value.Margin == new Vector3Int(32, 16, 32), "plan: same width -> only the margin grows");
        var n32 = Nav(new Vector3Int(-16, 16, -16), new Vector3Int(128, 128, 128), new Vector3Int(32, 16, 32));
        Check(AreaSizing.Plan(n32, 64, Server) == null, "plan: same width and margin -> nothing to send");
        var capped = Server; capped.MaxViewWidth = 192;
        Check(AreaSizing.Plan(n, 256, capped).Value.Dims.x == 192 + 2 * 32, "plan: the server's max caps the width (192 + its 32-block glide margins)");
        var fewBlocks = Server; fewBlocks.MaxBlocks = 288L * 288 * 128 - 1;
        var fb = AreaSizing.Plan(n, 256, fewBlocks).Value;
        Check(fb.Dims.x == 256 + 2 * fb.Margin.x && fb.Margin.x < 16 && (long)fb.Dims.x * fb.Dims.y * fb.Dims.z <= fewBlocks.MaxBlocks, $"plan: the block cap shrinks the margin first ({fb.Margin.x})");
        var noRoom = Server; noRoom.MaxBlocks = 100_000;
        Check(AreaSizing.Plan(n, 256, noRoom) == null, "plan: a region that can't fit the block cap is not sent");
        // An old region without a margin gets one (the glide needs it).
        var bare = Nav(new Vector3Int(0, 60, 0), new Vector3Int(32, 64, 32), new Vector3Int(0, 0, 0));
        var pad = AreaSizing.Plan(bare, 64, Server).Value;
        Check(pad.Dims == new Vector3Int(128, 64, 128) && pad.Margin == new Vector3Int(32, 0, 32) && pad.Origin == new Vector3Int(-48, 60, -48), $"plan: a region without margin gets a 32-block margin ({pad.Origin} {pad.Dims} {pad.Margin})");

        // NavState: one request in flight; the answer keeps the view where it is in the world.
        n.View = new Vector3(53, n.View.y, 41);
        var up = AreaSizing.Plan(n, 256, Server).Value;
        Check(n.TryIssue(up, 1f) && n.Pending && !n.TryIssue(up, 1.5f), "nav: a resize goes through the one-in-flight bookkeeping");
        Check(n.Move(new Vector3(1, 0, 0), 0.1f, 1.6f) == null, "nav: no shift request while the resize is pending");
        var before = n.View;
        n.OnRegion("world", up.Origin, up.Dims, up.Margin, 2f);
        Check(!n.Pending && Near(n.View.x, before.x) && Near(n.View.z, before.z) && n.Window == new Vector3Int(256, 96, 256), $"nav: resized region keeps the view in the world ({n.View})");
        var down = AreaSizing.Plan(n, 96, Server).Value;
        n.TryIssue(down, 3f);
        n.OnRegion("world", down.Origin, down.Dims, down.Margin, 3.5f);
        Check(Near(n.View.x, before.x) && Near(n.View.z, before.z) && n.Window.x == 96, "nav: shrinking keeps the view too");
        // Area setup somewhere else: the view starts at the new centre.
        n.OnRegion("world", new Vector3Int(5000, 16, 5000), new Vector3Int(160, 128, 160), new Vector3Int(16, 16, 16), 4f);
        Check(Near(n.View.x, 5080) && Near(n.View.z, 5080), "nav: a region elsewhere re-centres the view");
        Check(n.TryIssue(down, 4.1f) && n.TryIssue(down, 4.1f + n.RequestTimeout + 0.1f), "nav: a lost resize answer times out");
    }

    static WorldModel Terrain(Vector3Int origin, Vector3Int dims, Func<int, int, int, string> f, int id = 1)
    {
        var pal = new List<string> { "minecraft:air" };
        var w = new WorldModel { SnapshotId = id, World = "world", Origin = origin, Dims = dims };
        w.Grid = new ushort[dims.x * dims.y * dims.z];
        for (int y = 0; y < dims.y; y++) for (int z = 0; z < dims.z; z++) for (int x = 0; x < dims.x; x++)
        {
            string s = f(origin.x + x, origin.y + y, origin.z + z);
            int i = pal.IndexOf(s);
            if (i < 0) { i = pal.Count; pal.Add(s); }
            w.Grid[w.Index(x, y, z)] = (ushort)i;
        }
        w.Palette.AddRange(pal);
        return w;
    }

    static ChunkSnapshot Snap(WorldModel w, int id) => new ChunkSnapshot { SnapshotId = id, World = w.World, Origin = w.Origin, Dims = w.Dims, Palette = w.Palette.ToArray(), Grid = w.Grid };

    static string Hills(int x, int y, int z)
    {
        int h = 20 + ((x * 7 + z * 3) & 7) + (int)(6 * Math.Sin(x * 0.05) * Math.Cos(z * 0.04));
        return y < h - 3 ? "minecraft:stone" : y < h ? "minecraft:dirt" : y == h ? "minecraft:grass_block[snowy=false]" : y < 22 ? "minecraft:water[level=0]" :
               y == h + 1 && ((x * 31 + z * 17) % 23 == 0) ? "minecraft:poppy" : "minecraft:air";
    }

    static void ResizeRekeyTests(Func<string, string> fixture)
    {
        // Grow 64 -> 128 window around the same centre (96 -> 160 region): every old section is kept, only the ring is meshed.
        var old = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(96, 32, 96), Hills);
        old.DirtySections.Clear();
        var big = Terrain(new Vector3Int(-32, 0, -32), new Vector3Int(160, 32, 160), Hills, 2);
        var g = SectionPlan.Compute(old, Snap(big, 2), new HashSet<Vector3Int>(), null);
        Check(g.Rekey && g.ShiftSections == new Vector3Int(-2, 0, -2) && g.Kept == 6 * 6 * 2 && g.Mesh.Count == (10 * 10 - 6 * 6) * 2,
              $"resize plan: growing keeps all {g.Kept} sections, meshes only the ring ({g.Mesh.Count})");
        Check(g.Mesh.All(k => k.x < 2 || k.x >= 8 || k.z < 2 || k.z >= 8), "resize plan: the meshed sections are exactly the new ring");
        // Shrink 128 -> 64: nothing to mesh; the new border sections are refreshed (their neighbours left).
        big.DirtySections.Clear();
        var s = SectionPlan.Compute(big, Snap(old, 3), new HashSet<Vector3Int>(), null);
        Check(s.Rekey && s.Mesh.Count == 0 && s.Kept == 6 * 6 * 2, $"resize plan: shrinking re-meshes nothing ({s.Mesh.Count}), keeps {s.Kept}");
        Check(s.Refresh.Count == (36 - 16) * 2 && s.Refresh.All(k => k.x == 0 || k.x == 5 || k.z == 0 || k.z == 5), $"resize plan: the outer ring of the smaller region is refreshed later ({s.Refresh.Count})");
        // A delta's listed sections stand in for the compare.
        var d = SectionPlan.Compute(old, Snap(big, 2), null, g.Mesh.ToList());
        Check(d.Rekey && d.Mesh.SetEquals(g.Mesh) && d.Compared == 0, "resize plan: with a delta, the listed sections are meshed");

        // The resize fixtures (PROTOCOL.md 4.25) assemble against snapshot 8.
        var full = Json.Parse(System.IO.File.ReadAllText(fixture("chunk_snapshot_sections.json"))) as Dictionary<string, object>;
        foreach (var name in new[] { "chunk_snapshot_grow.json", "chunk_snapshot_shrink.json" })
        {
            var w = new WorldModel();
            w.ApplySnapshot(SectionedSnapshot.Assemble(new[] { full }, null, null));
            var before = Enumerable.Range(0, 32).SelectMany(x => Enumerable.Range(0, 16).Select(y => (x, y))).ToDictionary(p => p, p => w.State(w.Get(p.x, p.y, 3)));
            var m = Json.Parse(System.IO.File.ReadAllText(fixture(name))) as Dictionary<string, object>;
            var listed = new List<Vector3Int>();
            var snap = SectionedSnapshot.Assemble(new[] { m }, w, listed);
            var plan = SectionPlan.Compute(w, snap, new HashSet<Vector3Int>(), listed);
            var nw = new WorldModel(); nw.ApplySnapshot(snap);
            bool copied = before.All(kv => { int nx = kv.Key.x - (snap.Origin.x - w.Origin.x); return nx < 0 || nx >= snap.Dims.x || nw.State(nw.Get(nx, kv.Key.y, 3)) == kv.Value; });
            Check(copied && plan.Rekey && plan.Mesh.Count == listed.Count, $"{name}: copies the base, re-keys, meshes the {listed.Count} listed sections");
        }
        var grow = Json.Parse(System.IO.File.ReadAllText(fixture("chunk_snapshot_grow.json"))) as Dictionary<string, object>;
        var w8 = new WorldModel(); w8.ApplySnapshot(SectionedSnapshot.Assemble(new[] { full }, null, null));
        var gs = SectionedSnapshot.Assemble(new[] { grow }, w8, null);
        var gw = new WorldModel(); gw.ApplySnapshot(gs);
        Check(gw.State(gw.Get(0, 7, 0)) == "minecraft:stone" && gw.State(gw.Get(0, 8, 0)).StartsWith("minecraft:grass_block") && gw.State(gw.Get(63, 3, 15)) == "minecraft:stone"
              && gw.State(gw.Get(63, 4, 15)) == "minecraft:air", "grow fixture: the new sections decode at both ends");
        grow["sections"] = new List<object> { ((List<object>)grow["sections"])[0] };
        try { SectionedSnapshot.Assemble(new[] { grow }, w8, null); Check(false, "grow fixture without a ring section is refused"); }
        catch (FormatException) { Check(true, "grow fixture without a ring section is refused"); }
        var limits = RegionLimits.From((Json.Parse(System.IO.File.ReadAllText(fixture("welcome.json"))) as Dictionary<string, object>).O("limits"), RegionLimits.Default);
        Check(limits.MaxViewWidth == 96, "welcome fixture: limits.max_view_width parses");
    }

    static void LayoutTests()
    {
        int S = WorldModel.Section;
        var a = new BatchLayout(new Vector3Int(-96, 16, -96), new Vector3Int(288, 128, 288), 4);
        var b = new BatchLayout(new Vector3Int(-80, 32, -96), new Vector3Int(288, 128, 288), 4);   // shifted one section east and up
        Check(a.KeyOf(new Vector3Int(1, 0, 0)) == b.KeyOf(new Vector3Int(0, 0, 0)) && a.KeyOf(new Vector3Int(5, 3, 7)) == b.KeyOf(new Vector3Int(4, 2, 7)),
              "batch: keys are world-aligned (a shifted region keeps them)");
        Check(a.KeyOf(new Vector3Int(0, 0, 0)) == new Vector3Int(-2, 0, -2) && a.KeyOf(new Vector3Int(4, 7, 3)) == new Vector3Int(-1, 0, -1) && a.KeyOf(new Vector3Int(1, 0, 1)) == new Vector3Int(-2, 0, -2),
              "batch: 4 sections per side, full height");
        Check(a.AllKeys().Count == 25 && new BatchLayout(new Vector3Int(-88, 16, -88), new Vector3Int(288, 128, 288), 4).AllKeys().Count == 25,
              "batch: 18 sections per side make 5 x 5 batches here");
        int covered = a.AllKeys().Sum(k => a.SectionsOf(k).Count());
        Check(covered == 18 * 18 * 8, $"batch: every section is in exactly one batch ({covered})");
        // Position + offset = the section's corner (block units, Z mirrored), also for a batch built before a y shift.
        bool ok = true;
        foreach (var lay in new[] { a, b })
            foreach (var s in new[] { new Vector3Int(0, 0, 0), new Vector3Int(5, 3, 7), new Vector3Int(17, 7, 17) })
                foreach (int y0 in new[] { lay.Sy, lay.Sy - 1, lay.Sy + 2 })
                {
                    var k = lay.KeyOf(s);
                    var p = lay.Position(k, y0) + lay.Offset(s, k, y0);
                    ok &= Near(p.x, s.x * S) && Near(p.y, s.y * S) && Near(p.z, -s.z * S);
                }
        Check(ok, "batch: position + offset = section corner (any y anchor)");
        // A batch built before the vertical shift stays put in the world.
        var k0 = a.KeyOf(new Vector3Int(5, 3, 7));
        var worldBefore = a.Position(k0, a.Sy) + a.Offset(new Vector3Int(5, 3, 7), k0, a.Sy) + new Vector3(a.Origin.x, a.Origin.y, -a.Origin.z);
        var worldAfter = b.Position(k0, a.Sy) + b.Offset(new Vector3Int(4, 2, 7), k0, a.Sy) + new Vector3(b.Origin.x, b.Origin.y, -b.Origin.z);
        Check(Near(worldBefore.x, worldAfter.x) && Near(worldBefore.y, worldAfter.y) && Near(worldBefore.z, worldAfter.z), "batch: a shifted region shows a kept batch at the same world place");

        // Rebuild planning after a re-key.
        var held = new List<Vector3Int>();
        for (int x = 0; x < 18; x++) for (int y = 0; y < 8; y++) for (int z = 0; z < 18; z++) held.Add(new Vector3Int(x, y, z));
        var east = new BatchLayout(new Vector3Int(-80, 16, -96), new Vector3Int(288, 128, 288), 4);
        var (dirty, gone) = BatchRules.AfterRekey(a, east, new Vector3Int(1, 0, 0), held, a.AllKeys());
        Check(dirty.SetEquals(Enumerable.Range(-2, 5).Select(z => new Vector3Int(-2, 0, z))) && gone.Count == 0,
              $"batch rekey: a one-section shift east rebuilds the west column of batches only ({string.Join(" ", dirty)})");
        var up = new BatchLayout(new Vector3Int(-96, 32, -96), new Vector3Int(288, 128, 288), 4);
        Check(BatchRules.AfterRekey(a, up, new Vector3Int(0, 1, 0), held, a.AllKeys()).dirty.Count == 25, "batch rekey: a vertical shift rebuilds every batch (each lost a layer)");
        var grown = new BatchLayout(new Vector3Int(-160, 16, -160), new Vector3Int(416, 128, 416), 4);
        var gr = BatchRules.AfterRekey(a, grown, new Vector3Int(-4, 0, -4), held, a.AllKeys());
        Check(gr.dirty.Count == 0 && gr.gone.Count == 0, "batch rekey: growing keeps every batch as it is (new ones fill in as their sections mesh)");
        var shrunk = new BatchLayout(new Vector3Int(-32, 16, -32), new Vector3Int(96, 128, 96), 4);
        var sr = BatchRules.AfterRekey(a, shrunk, new Vector3Int(4, 0, 4), held, a.AllKeys());
        var keep = shrunk.AllKeys();
        Check(sr.gone.SetEquals(a.AllKeys().Except(keep)) && sr.dirty.SetEquals(keep.Where(k => k != new Vector3Int(0, 0, 0))),
              $"batch rekey: shrinking drops the outer batches ({sr.gone.Count}) and rebuilds the cut ones ({sr.dirty.Count}; batch 0,0 lies fully inside)");

        Check(BatchRules.Ready(false, 0, 0, 0.4f, false) && !BatchRules.Ready(true, 0, 0.1f, 0.4f, false) && BatchRules.Ready(true, 0, 0.5f, 0.4f, false) && !BatchRules.Ready(true, 0, 5f, 0.4f, true),
              "batch: rebuild when the sections are done, or after the wait (not while staging)");
    }

    static void SectionMeshTests()
    {
        var w = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(2, 1, 1), (x, y, z) => "minecraft:stone");
        var md = new MeshData();
        GreedyMesher.Build(w, new Vector3Int(0, 0, 0), new Flat(), md);
        var sm = SectionMesh.From(md, new Vector3Int(0, 0, 0));
        Check(sm.Verts.Length == 24 && sm.Opaque == 36 && sm.Quads == 6, "section mesh: two stones -> 6 quads");
        // Normals per face: up, down and the sides, with Unity's Z mirrored.
        int upN = 0, downN = 0, zN = 0, xN = 0;
        foreach (var v in sm.Verts)
        {
            if (v.Ny == 127) upN++; else if (v.Ny == -127) downN++;
            else if (Math.Abs(v.Nz) == 127) zN++; else if (Math.Abs(v.Nx) == 127) xN++;
        }
        Check(upN == 4 && downN == 4 && zN == 8 && xN == 8, $"section mesh: flat normals per face (up {upN} down {downN} z {zN} x {xN})");
        // The top face's vertices point up and its triangles wind clockwise from above (Unity front face).
        bool outward = true;
        for (int i = 0; i < sm.Opaque; i += 3)
        {
            var p0 = sm.Verts[sm.Indices[i]].Pos; var p1 = sm.Verts[sm.Indices[i + 1]].Pos; var p2 = sm.Verts[sm.Indices[i + 2]].Pos;
            float nx = (p1.y - p0.y) * (p2.z - p0.z) - (p1.z - p0.z) * (p2.y - p0.y), ny = (p1.z - p0.z) * (p2.x - p0.x) - (p1.x - p0.x) * (p2.z - p0.z), nz = (p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x);
            var c = new Vector3((p0.x + p1.x + p2.x) / 3 - 1f, (p0.y + p1.y + p2.y) / 3 - 0.5f, (p0.z + p1.z + p2.z) / 3 + 0.5f);   // from the box centre (1, 0.5, -0.5)
            outward &= nx * c.x + ny * c.y + nz * c.z > 0;
        }
        Check(outward, "section mesh: flipped winding faces outward in Unity space (Z mirrored)");
        Check(sm.Verts.All(v => v.Pos.z <= 0 && v.Pos.z >= -1), "section mesh: Z mirrored (Minecraft +z -> Unity -z)");
        // Relative to the section corner.
        var w2 = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(32, 32, 32), (x, y, z) => x == 20 && y == 17 && z == 30 ? "minecraft:stone" : "minecraft:air");
        GreedyMesher.Build(w2, new Vector3Int(1, 1, 1), new Flat(), md);
        var s2 = SectionMesh.From(md, new Vector3Int(1, 1, 1));
        Check(s2.Verts.Min(v => v.Pos.x) == 4 && s2.Verts.Min(v => v.Pos.y) == 1 && s2.Verts.Max(v => v.Pos.z) == -14 && s2.Verts.Min(v => v.Pos.z) == -15,
              $"section mesh: vertices relative to the section corner ({s2.Verts.Min(v => v.Pos.x)},{s2.Verts.Min(v => v.Pos.y)},{s2.Verts.Min(v => v.Pos.z)}..{s2.Verts.Max(v => v.Pos.z)})");
        // A double-sided quad sharing its vertices cancels to a zero normal (unshaded, like RecalculateNormals).
        var ds = new MeshData();
        foreach (var p in new[] { new Vector3(0, 0, 0.5f), new Vector3(1, 0, 0.5f), new Vector3(1, 1, 0.5f), new Vector3(0, 1, 0.5f) }) { ds.Verts.Add(p); ds.Uvs.Add(new Vector3(0, 0, -1)); ds.Colors.Add(new Color32(255, 255, 255, 255)); }
        ds.Transparent.AddRange(new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 });
        var dsm = SectionMesh.From(ds, new Vector3Int(0, 0, 0));
        Check(dsm.Verts.All(v => v.Nx == 0 && v.Ny == 0 && v.Nz == 0) && dsm.Transparent == 12, "section mesh: double-sided shared quad -> zero normal");
        Check(SectionMesh.From(new MeshData(), new Vector3Int(0, 0, 0)) == null, "section mesh: empty section -> null");
    }

    static void BatchTests()
    {
        // A real-ish terrain: batches merge sections, keep submesh ranges and offsets.
        var w = Terrain(new Vector3Int(-40, 0, -24), new Vector3Int(128, 48, 96), Hills);
        var n = w.SectionCount;
        var secs = new Dictionary<Vector3Int, SectionMesh>();
        var md = new MeshData();
        int drawCallsBefore = 0, verts = 0;
        for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
        {
            var k = new Vector3Int(x, y, z);
            GreedyMesher.Build(w, k, new Flat(), md); GreedyMesher.BuildSpecials(w, k, new Flat(), md);
            var sm = SectionMesh.From(md, k);
            if (sm == null) continue;
            secs[k] = sm; verts += sm.Verts.Length;
            drawCallsBefore += (sm.Opaque > 0 ? 1 : 0) + (sm.Transparent > 0 ? 1 : 0);
        }
        var lay = new BatchLayout(w.Origin, w.Dims, 4);
        int drawCalls = 0, batchVerts = 0, renderers = 0; bool rangesOk = true, boundsOk = true;
        foreach (var key in lay.AllKeys())
        {
            var parts = lay.SectionsOf(key).Where(secs.ContainsKey).Select(s => (secs[s], lay.Offset(s, key, lay.Sy))).ToList();
            var b = BatchData.Assemble(parts);
            if (b.VertCount == 0) continue;
            renderers++; drawCalls += b.DrawCalls(false); batchVerts += b.VertCount;
            rangesOk &= b.Opaque + b.Transparent + b.WaterBack == b.IndexCount && b.Opaque == parts.Sum(p => p.Item1.Opaque) && b.Transparent == parts.Sum(p => p.Item1.Transparent);
            for (int i = 0; i < b.IndexCount; i++) rangesOk &= b.Indices[i] >= 0 && b.Indices[i] < b.VertCount;
            // Each section's vertices land at its offset in the batch.
            var first = parts[0];
            var v0 = first.Item1.Verts[0].Pos + first.Item2;
            boundsOk &= v0.x >= b.Min.x && v0.y >= b.Min.y && v0.z >= b.Min.z && v0.x <= b.Max.x && v0.y <= b.Max.y && v0.z <= b.Max.z && Near(b.Verts[0].Pos.x, v0.x) && Near(b.Verts[0].Pos.z, v0.z);
            b.Release();
        }
        Check(batchVerts == verts && rangesOk && boundsOk, $"batch: assembly keeps every vertex ({batchVerts}), index ranges and bounds");
        Check(renderers <= lay.AllKeys().Count && drawCalls < drawCallsBefore / 3, $"batch: {secs.Count} section meshes ({drawCallsBefore} draw calls) -> {renderers} batches ({drawCalls} draw calls)");
        // The water's inside faces stay a separate range (only drawn while the eye is under water).
        var water = Terrain(new Vector3Int(0, 0, 0), new Vector3Int(16, 16, 16), (x, y, z) => y < 3 ? "minecraft:stone" : y < 6 ? "minecraft:water[level=0]" : "minecraft:air");
        GreedyMesher.Build(water, new Vector3Int(0, 0, 0), new Flat(), md); GreedyMesher.BuildSpecials(water, new Vector3Int(0, 0, 0), new Flat(), md);
        var wm = SectionMesh.From(md, new Vector3Int(0, 0, 0));
        var wb = BatchData.Assemble(new[] { (wm, new Vector3(16, 0, -32)), (wm, new Vector3(0, 0, 0)) });
        Check(wm.WaterBack > 0 && wb.WaterBack == 2 * wm.WaterBack && wb.DrawCalls(true) == wb.DrawCalls(false) + 1 && wb.Indices[wb.Opaque + wb.Transparent] >= 0,
              $"batch: water-back faces are their own submesh ({wb.WaterBack / 3} triangles)");
        wb.Release();
    }

    // ---------- --measure-batches DIR ----------

    /// <summary>Loads a snapshot saved by tools/area_e2e.py (a JSON array of sections_v1 parts) or a single rle_palette_v1 message.</summary>
    static ChunkSnapshot LoadSnapshot(string path)
    {
        var j = Json.Parse(System.IO.File.ReadAllText(path));
        if (j is List<object> parts) return SectionedSnapshot.Assemble(parts.Cast<Dictionary<string, object>>().ToList(), null, null);
        return ChunkSnapshot.From((Dictionary<string, object>)j);
    }

    /// <summary>[gpu] Rain layout time per area snapshot (WeatherRenderer.Rebuild's work): 2,500 drops, cold and warm.</summary>
    public static void MeasureWeather(string dir)
    {
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(p => int.TryParse(System.IO.Path.GetFileNameWithoutExtension(p), out var v) ? v : 0))
        {
            var w = new WorldModel();
            w.ApplySnapshot(LoadSnapshot(f));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = Precipitation.Layout(w, 0, 0, w.Dims.x, w.Dims.z, 2500, 1).Count;
            double cold = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            for (int r = 0; r < 10; r++) Precipitation.Layout(w, 0, 0, w.Dims.x, w.Dims.z, 2500, 2 + r);
            Console.WriteLine($"MEASURE_WEATHER {System.IO.Path.GetFileName(f)} region={w.Dims.x}x{w.Dims.y}x{w.Dims.z} drops={n} cold_ms={cold:F1} warm_ms={sw.Elapsed.TotalMilliseconds / 10:F2}");
        }
    }

    public static void MeasureBatches(string dir, string jar)
    {
        using var assets = jar != null ? new ZipAssets(jar) : null;
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(p => int.TryParse(System.IO.Path.GetFileNameWithoutExtension(p), out var v) ? v : 0))
        {
            var snap = LoadSnapshot(f);
            var w = new WorldModel();
            w.ApplySnapshot(snap);
            w.DirtySections.Clear();
            BlockRules.ResetShapes();
            IFaceSource faces = new Flat();
            if (assets != null) { var pf = new PackFaces(assets); pf.Prewarm(w); faces = pf; }
            var n = w.SectionCount;
            var secs = new Dictionary<Vector3Int, SectionMesh>();
            var md = new MeshData();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                var k = new Vector3Int(x, y, z);
                GreedyMesher.Build(w, k, faces, md); GreedyMesher.BuildSpecials(w, k, faces, md);
                var sm = SectionMesh.From(md, k);
                if (sm != null) secs[k] = sm;
            }
            double meshMs = sw.Elapsed.TotalMilliseconds;
            long verts = secs.Values.Sum(s => (long)s.Verts.Length), tris = secs.Values.Sum(s => (long)(s.Opaque + s.Transparent) / 3);
            int sectionDraws = secs.Values.Sum(s => (s.Opaque > 0 ? 1 : 0) + (s.Transparent > 0 ? 1 : 0));
            int window = w.Dims.x - 32;
            Console.WriteLine($"MEASURE_BATCH {System.IO.Path.GetFileName(f)} window={window} region={w.Dims.x}x{w.Dims.y}x{w.Dims.z} light={(w.Light != null ? "yes" : "no")} pack={(assets != null ? "yes" : "no")} " +
                              $"sections={n.x * n.y * n.z} non_empty={secs.Count} verts={verts} tris={tris} faces={secs.Values.Sum(s => (long)s.Quads)} mesh_ms_1thread={meshMs:F0} " +
                              $"opaque_tris={secs.Values.Sum(s => (long)s.Opaque / 3)} solid_tris={secs.Values.Sum(s => (long)s.Solid / 3)} transparent_tris={secs.Values.Sum(s => (long)s.Transparent / 3)}");
            {
                // [gpu] upload size of the area's batches with 32-byte BatchVertex vs 20-byte PackedVertex (batches that don't fit stay classic)
                var lay = new BatchLayout(w.Origin, w.Dims, BatchRules.Columns);
                long classicBytes = 0, packedBytes = 0; int fallbacks = 0;
                foreach (var key in lay.AllKeys())
                {
                    var parts = lay.SectionsOf(key).Where(secs.ContainsKey).Select(s => (secs[s], lay.Offset(s, key, lay.Sy))).ToList();
                    var pb = BatchData.Assemble(parts, true);
                    if (pb.VertCount == 0) continue;
                    if (pb.Packed == null) fallbacks++;
                    packedBytes += pb.Bytes; classicBytes += (long)pb.VertCount * BatchVertex.Size + pb.IndexCount * 4L;
                    pb.Release();
                }
                Console.WriteLine($"MEASURE_PACKED {System.IO.Path.GetFileName(f)} verts={verts} vertex_bytes {BatchVertex.Size} -> {PackedVertex.Size}, vertex+index MB {classicBytes / 1048576.0:F1} -> {packedBytes / 1048576.0:F1}, batches left classic {fallbacks}");
            }
            Console.WriteLine($"  per-section meshes (before): renderers={secs.Count} draw_calls={sectionDraws}");
            foreach (int cols in new[] { 1, 2, 3, 4, 6, 8 })
            {
                var lay = new BatchLayout(w.Origin, w.Dims, cols);
                int renderers = 0, draws = 0; long maxBytes = 0, maxVerts = 0, totalBytes = 0; double maxMs = 0, sumMs = 0;
                var bytesOf = new Dictionary<Vector3Int, long>();
                foreach (var key in lay.AllKeys())
                {
                    var parts = lay.SectionsOf(key).Where(secs.ContainsKey).Select(s => (secs[s], lay.Offset(s, key, lay.Sy))).ToList();
                    var b = BatchData.Assemble(parts);
                    if (b.VertCount > 0) { renderers++; draws += b.DrawCalls(false); }
                    bytesOf[key] = b.Bytes; totalBytes += b.Bytes;
                    maxBytes = Math.Max(maxBytes, b.Bytes); maxVerts = Math.Max(maxVerts, b.VertCount); maxMs = Math.Max(maxMs, b.Ms); sumMs += b.Ms;
                    b.Release();
                }
                // An edit in the middle rebuilds one batch; a one-section shift east rebuilds the batches that lose or gain a column.
                var mid = lay.KeyOf(new Vector3Int(n.x / 2, n.y / 2, n.z / 2));
                var held = secs.Keys.ToList();
                var east = new BatchLayout(new Vector3Int(w.Origin.x + 16, w.Origin.y, w.Origin.z), w.Dims, cols);
                var (dirty, gone) = BatchRules.AfterRekey(lay, east, new Vector3Int(1, 0, 0), held, lay.AllKeys());
                var gaining = new HashSet<Vector3Int>(Enumerable.Range(0, n.y).SelectMany(y => Enumerable.Range(0, n.z).Select(z => east.KeyOf(new Vector3Int(n.x - 1, y, z)))));
                dirty.UnionWith(gaining);
                long shiftBytes = dirty.Sum(k => bytesOf.TryGetValue(k, out var v) ? v : maxBytes);
                Console.WriteLine($"  columns={cols} batches={lay.AllKeys().Count} renderers={renderers} draw_calls={draws} max_batch_verts={maxVerts} max_batch_kb={maxBytes / 1024} " +
                                  $"total_mb={totalBytes / 1048576.0:F1} max_assemble_ms={maxMs:F1} all_assemble_ms={sumMs:F0} edit_rebuild_kb={bytesOf[mid] / 1024} " +
                                  $"shift_rebuilds={dirty.Count} shift_rebuild_mb={shiftBytes / 1048576.0:F1}");
            }
        }
        BlockRules.ResetShapes();
    }

    // ---------- --live-resize URL ----------

    /// <summary>
    /// A live session resizes the window 64 -> 256 -> 96 the way the headset does (AreaSizing.Plan + NavState), applies each
    /// resize delta through SnapshotAssembler (worker decode + SectionPlan), then asks for a full resend (bad_snapshot) and
    /// compares it with the delta-assembled region, blocks and light.
    /// </summary>
    public static void LiveResize(string url, Action<bool, string> check)
    {
        Check = check;
        var link = new LinkClient(url, "");
        link.Start();
        var world = new WorldModel(); var nav = new NavState(); var asm = new SnapshotAssembler();
        var limits = RegionLimits.Default;
        var widths = new Queue<int>(new[] { 64, 256, 96 });
        int phase = 0;   // 0 = wait for a snapshot to resize from, 1 = resize sent, 2 = resend asked
        int asked = 0; Vector3 viewBefore = default; string[] mine = null; byte[] mineLight = null;
        SnapshotAssembler.Result resized = null; var t0 = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddSeconds(150); var nextPing = DateTime.UtcNow;
        DateTime askResendAt = DateTime.MaxValue, lastResend = DateTime.MinValue;
        float Now() => (float)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        while (DateTime.UtcNow < deadline && (widths.Count > 0 || phase != 0))
        {
            while (link.Inbound.TryDequeue(out var m))
            {
                switch (m.S("type"))
                {
                    case "welcome": limits = RegionLimits.From(m.O("limits"), limits); nav.CanPad = true; break;
                    case "block_update": world.ApplyBlockUpdate(m); break;
                    case "error": Console.WriteLine("live-resize: server error " + m.S("code") + " " + m.S("message")); break;
                    case "chunk_snapshot":
                        if (phase == 2 && m.I("part") == 0)
                        {
                            mine = world.Grid.Select(id => world.State(id)).ToArray();
                            mineLight = world.Light == null ? null : (byte[])world.Light.Clone();
                        }
                        asm.Offer(m, world, new HashSet<Vector3Int>());
                        if (!asm.Busy) break;
                        var r = asm.Wait();
                        if (r.Error != null) { Check(false, "live-resize: snapshot " + r.Error); phase = 0; widths.Clear(); break; }
                        int oldWidth = AreaSizing.Current(nav);
                        world.ApplySnapshot(r.Snapshot);
                        nav.OnRegion(world.World, world.Origin, world.Dims, r.ViewMargin, Now());
                        if (phase == 1)
                        {
                            resized = r; phase = 2;
                            int got = AreaSizing.Current(nav);
                            bool kept = Near(nav.View.x, viewBefore.x, 0.01f) && Near(nav.View.z, viewBefore.z, 0.01f);
                            int expect = Math.Min(asked, AreaSizing.MaxWidth(limits, 16));
                            Check(got == expect && r.Delta && r.Plan.Rekey && kept,
                                  $"live-resize: {oldWidth} -> {got} (asked {asked}) as a delta: {r.Listed} sections sent, {r.Plan.Mesh.Count} to mesh, {r.Plan.Kept} kept, view kept {kept}, {(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms");
                            if (got > oldWidth) Check(r.Plan.Kept > 0 && r.Plan.Mesh.Count >= r.Listed, "live-resize: growing keeps every old section mesh, meshes the new ring");
                            else Check(r.Plan.Mesh.Count == r.Listed && r.Listed < 8, $"live-resize: shrinking meshes only changed sections ({r.Listed})");
                            askResendAt = lastResend.AddSeconds(2.2);   // the server resends at most every 2 s per session
                        }
                        else if (phase == 2)
                        {
                            phase = 0;
                            var now = world.Grid.Select(id => world.State(id)).ToArray();
                            int blockDiff = mine == null || mine.Length != now.Length ? -1 : now.Where((st, i) => st != mine[i]).Count();
                            int lightDiff = world.Light == null || mineLight == null ? 0 : world.Light.Where((v, i) => v != mineLight[i]).Count();
                            Check(!r.Delta && blockDiff == 0 && lightDiff == 0 && (world.Light == null) == (mineLight == null),
                                  $"live-resize: the full resend matches the delta-assembled {world.Dims.x}x{world.Dims.y}x{world.Dims.z} region ({blockDiff} blocks, {lightDiff} light cells differ)");
                        }
                        break;
                }
            }
            if (phase == 2 && askResendAt != DateTime.MaxValue && DateTime.UtcNow >= askResendAt)
            {
                askResendAt = DateTime.MaxValue; lastResend = DateTime.UtcNow;
                link.Send(new Dictionary<string, object> { ["type"] = "error", ["code"] = "bad_snapshot", ["message"] = "live resize check", ["snapshot_id"] = world.SnapshotId, ["fatal"] = false });
            }
            if (phase == 0 && world.Loaded && widths.Count > 0)
            {
                asked = widths.Dequeue();
                var plan = AreaSizing.Plan(nav, asked, limits);
                if (plan == null) { Console.WriteLine($"live-resize: already {asked} wide, skipped"); continue; }
                viewBefore = nav.View; t0 = DateTime.UtcNow;
                nav.TryIssue(plan.Value, Now());
                link.Send(Protocol.RegionRequest(world.World, plan.Value.Origin, plan.Value.Dims, plan.Value.Margin));
                phase = 1;
            }
            if (link.Connected && DateTime.UtcNow > nextPing) { link.Send(Protocol.Ping(1)); nextPing = DateTime.UtcNow.AddSeconds(2); }
            Thread.Sleep(30);
        }
        Check(widths.Count == 0 && phase == 0, "live-resize: every resize answered in time");
        link.Dispose();
    }
}
