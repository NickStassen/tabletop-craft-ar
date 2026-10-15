// [area] Checks the batched mesh path against the old per-section one inside Unity, headless: real meshes from the
// mesher (cubes, water with its inside faces, plants, glass, slabs), once uploaded the old way (Vector3 lists,
// SetTriangles, RecalculateNormals) and once as a batch (SectionMesh -> BatchData -> TabletopRenderer.Fill with the
// 32-byte BatchVertex layout). Positions, colours, UVs, triangles per submesh, bounds and normals must match.
//   Unity -batchmode -nographics -quit -projectPath quest-app -buildTarget Android \
//     -executeMethod TabletopCraft.EditorTools.BatchMeshCheck.Run -logFile batch.log
//   grep BATCHCHECK batch.log
#if UNITY_EDITOR
using System.Collections.Generic;
using TabletopCraft.World;
using UnityEditor;
using UnityEngine;

namespace TabletopCraft.EditorTools
{
    public static class BatchMeshCheck
    {
        sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

        static string Block(int x, int y, int z)
        {
            int h = 6 + ((x * 7 + z * 3) & 3);
            if (y < h - 2) return "minecraft:stone";
            if (y < h) return "minecraft:dirt";
            if (y == h) return x % 5 == 0 ? "minecraft:glass" : z % 7 == 0 ? "minecraft:oak_slab[type=bottom,waterlogged=false]" : "minecraft:grass_block[snowy=false]";
            if (y < 9) return "minecraft:water[level=0]";
            if (y == h + 1 && (x + z) % 4 == 0) return "minecraft:poppy";
            return "minecraft:air";
        }

        public static void Run()
        {
            int errors = 0, checkedVerts = 0, zeroNormals = 0;
            var w = new WorldModel { SnapshotId = 1, World = "world", Origin = new Vector3Int(0, 0, 0), Dims = new Vector3Int(32, 16, 32) };
            var pal = new List<string>();
            w.Grid = new ushort[32 * 16 * 32];
            for (int y = 0; y < 16; y++) for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++)
            {
                string s = Block(x, y, z);
                int i = pal.IndexOf(s);
                if (i < 0) { i = pal.Count; pal.Add(s); }
                w.Grid[w.Index(x, y, z)] = (ushort)i;
            }
            w.Palette.AddRange(pal);
            var faces = new Flat();
            var parts = new List<(SectionMesh, Vector3)>();
            var md = new MeshData();
            var refVerts = new List<Vector3>(); var refNormals = new List<Vector3>(); var refColors = new List<Color32>(); var refUvs = new List<Vector3>();
            var refTris = new[] { new List<int>(), new List<int>(), new List<int>() };
            var lay = new BatchLayout(w.Origin, w.Dims, 4);
            var key = lay.KeyOf(new Vector3Int(0, 0, 0));
            for (int sx = 0; sx < 2; sx++) for (int sz = 0; sz < 2; sz++)
            {
                var s = new Vector3Int(sx, 0, sz);
                GreedyMesher.Build(w, s, faces, md);
                GreedyMesher.BuildSpecials(w, s, faces, md);
                var sm = SectionMesh.From(md, s);
                if (sm == null) continue;
                var off = lay.Offset(s, key, lay.Sy);
                parts.Add((sm, off));
                // The old path: one Mesh per section, RecalculateNormals.
                var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                var v = new List<Vector3>();
                float bx = sx * 16, bz = sz * 16;
                foreach (var p in md.Verts) v.Add(new Vector3(p.x - bx, p.y, bz - p.z));
                m.SetVertices(v); m.SetUVs(0, md.Uvs); m.SetColors(md.Colors);
                m.subMeshCount = 3;
                m.SetTriangles(Flip(md.Opaque), 0, false); m.SetTriangles(Flip(md.Transparent), 1, false); m.SetTriangles(Flip(md.WaterBack), 2, false);
                m.RecalculateNormals();
                int baseV = refVerts.Count;
                foreach (var p in v) refVerts.Add(p + off);
                refNormals.AddRange(m.normals); refColors.AddRange(md.Colors); refUvs.AddRange(md.Uvs);
                for (int sub = 0; sub < 3; sub++) foreach (int t in m.GetTriangles(sub)) refTris[sub].Add(t + baseV);
                Object.DestroyImmediate(m);
            }
            var data = BatchData.Assemble(parts);
            var batch = new Mesh();
            TabletopRenderer.Fill(batch, data);
            var bv = batch.vertices; var bn = batch.normals; var bc = batch.colors32; var bu = new List<Vector3>(); batch.GetUVs(0, bu);
            if (bv.Length != refVerts.Count) { errors++; Debug.LogError($"BATCHCHECK vertex count {bv.Length} vs {refVerts.Count}"); }
            for (int i = 0; i < Mathf.Min(bv.Length, refVerts.Count); i++)
            {
                checkedVerts++;
                if ((bv[i] - refVerts[i]).sqrMagnitude > 1e-8f) { if (errors++ < 10) Debug.LogError($"BATCHCHECK position {i}: {bv[i]} vs {refVerts[i]}"); }
                if (!bc[i].Equals(refColors[i])) { if (errors++ < 10) Debug.LogError($"BATCHCHECK colour {i}: {bc[i]} vs {refColors[i]}"); }
                if ((bu[i] - refUvs[i]).sqrMagnitude > 1e-8f) { if (errors++ < 10) Debug.LogError($"BATCHCHECK uv {i}: {bu[i]} vs {refUvs[i]}"); }
                var rn = refNormals[i];
                if (rn.sqrMagnitude < 1e-4f) zeroNormals++;
                // SNorm8 normals: within 1/127 per component of the float normal (zero stays zero).
                if (Mathf.Abs(bn[i].x - rn.x) > 0.02f || Mathf.Abs(bn[i].y - rn.y) > 0.02f || Mathf.Abs(bn[i].z - rn.z) > 0.02f)
                { if (errors++ < 10) Debug.LogError($"BATCHCHECK normal {i}: {bn[i]} vs RecalculateNormals {rn}"); }
            }
            for (int sub = 0; sub < 3; sub++)
            {
                var bt = batch.GetTriangles(sub);
                bool same = bt.Length == refTris[sub].Count;
                for (int i = 0; same && i < bt.Length; i++) same = bt[i] == refTris[sub][i];
                if (!same) { errors++; Debug.LogError($"BATCHCHECK submesh {sub}: {bt.Length} vs {refTris[sub].Count} indices differ"); }
            }
            var b = batch.bounds;
            var lo = refVerts[0]; var hi = refVerts[0];
            foreach (var p in refVerts) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            if ((b.min - lo).sqrMagnitude > 1e-6f || (b.max - hi).sqrMagnitude > 1e-6f) { errors++; Debug.LogError($"BATCHCHECK bounds {b.min}..{b.max} vs {lo}..{hi}"); }
            Debug.Log($"BATCHCHECK done: {parts.Count} sections, {checkedVerts} vertices ({zeroNormals} with zero normals), submeshes {data.Opaque / 3}/{data.Transparent / 3}/{data.WaterBack / 3} triangles, {errors} errors");
            data.Release();
            Object.DestroyImmediate(batch);
            if (Application.isBatchMode) EditorApplication.Exit(errors == 0 ? 0 : 1);
        }

        // ---------- the renderer itself, driven frame by frame ----------
        //   -executeMethod TabletopCraft.EditorTools.BatchMeshCheck.RunRenderer   (grep BATCHRENDER)

        static WorldModel Terrain(Vector3Int origin, Vector3Int dims, int id)
        {
            var w = new WorldModel { SnapshotId = id, World = "world", Origin = origin, Dims = dims };
            var pal = new List<string>();
            w.Grid = new ushort[dims.x * dims.y * dims.z];
            for (int y = 0; y < dims.y; y++) for (int z = 0; z < dims.z; z++) for (int x = 0; x < dims.x; x++)
            {
                string s = Block(origin.x + x, origin.y + y, origin.z + z);
                int i = pal.IndexOf(s);
                if (i < 0) { i = pal.Count; pal.Add(s); }
                w.Grid[w.Index(x, y, z)] = (ushort)i;
            }
            w.Palette.AddRange(pal);
            return w;
        }

        static Net.ChunkSnapshot Snap(WorldModel w) => new Net.ChunkSnapshot { SnapshotId = w.SnapshotId, World = w.World, Origin = w.Origin, Dims = w.Dims, Palette = w.Palette.ToArray(), Grid = w.Grid };

        /// <summary>Vertex count, triangle count and the sum of vertex positions under "Blocks" (block units, Z mirrored).</summary>
        static (long verts, long tris, double sx, double sy, double sz) OnScreen(Transform blocks)
        {
            long v = 0, t = 0; double sx = 0, sy = 0, sz = 0;
            foreach (var mf in blocks.GetComponentsInChildren<MeshFilter>(false))
            {
                var m = mf.sharedMesh; if (m == null || m.vertexCount == 0) continue;
                var p = mf.transform.localPosition;
                foreach (var q in m.vertices) { sx += q.x + p.x; sy += q.y + p.y; sz += q.z + p.z; }
                v += m.vertexCount; t += (m.GetIndexCount(0) + m.GetIndexCount(1)) / 3;
            }
            return (v, t, sx, sy, sz);
        }

        /// <summary>Vertex/triangle counts and position sums of one section's mesh (region-local Unity block units).</summary>
        struct Stat
        {
            public long V, T; public double X, Y, Z;
            public static Stat Of(MeshData md)
            {
                var s = new Stat { V = md.Verts.Count, T = (md.Opaque.Count + md.Transparent.Count) / 3 };
                foreach (var p in md.Verts) { s.X += p.x; s.Y += p.y; s.Z += -p.z; }
                return s;
            }
            /// <summary>The same mesh after the region origin moved by `shift` blocks (local = old local - shift, Z mirrored).</summary>
            public Stat Moved(Vector3Int shift) => new Stat { V = V, T = T, X = X - V * (double)shift.x, Y = Y - V * (double)shift.y, Z = Z + V * (double)shift.z };
        }

        static Stat MeshStat(WorldModel w, Vector3Int k, IFaceSource faces, MeshData md)
        {
            GreedyMesher.Build(w, k, faces, md); GreedyMesher.BuildSpecials(w, k, faces, md);
            return Stat.Of(md);
        }

        /// <summary>
        /// What the screen should show per section, following the renderer's contract (SectionPlan): a re-keyed snapshot
        /// keeps the old meshes of kept sections (moved) and re-meshes plan.Mesh and plan.Refresh from the new model; anything
        /// else meshes everything from the model. (Kept sections at the trailing edge of a shift keep their old border faces,
        /// as before batching: they lie in the hidden view margin.)
        /// </summary>
        static Dictionary<Vector3Int, Stat> Expect(WorldModel w, Dictionary<Vector3Int, Stat> before, SectionPlan plan, IFaceSource faces)
        {
            var r = new Dictionary<Vector3Int, Stat>(); var md = new MeshData(); var n = w.SectionCount;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                var k = new Vector3Int(x, y, z);
                var ko = plan != null && plan.Rekey ? new Vector3Int(x + plan.ShiftSections.x, y + plan.ShiftSections.y, z + plan.ShiftSections.z) : k;
                bool keep = plan != null && plan.Rekey && !plan.Mesh.Contains(k) && !plan.Refresh.Contains(k) && before.ContainsKey(ko);
                r[k] = keep ? before[ko].Moved(plan.ShiftBlocks) : MeshStat(w, k, faces, md);
            }
            return r;
        }

        static Stat Sum(Dictionary<Vector3Int, Stat> d)
        {
            var s = new Stat();
            foreach (var v in d.Values) { s.V += v.V; s.T += v.T; s.X += v.X; s.Y += v.Y; s.Z += v.Z; }
            return s;
        }

        static bool Same((long verts, long tris, double sx, double sy, double sz) got, Stat want) =>
            got.verts == want.V && got.tris == want.T && System.Math.Abs(got.sx - want.X) < 1e-3 * (1 + System.Math.Abs(want.X))
            && System.Math.Abs(got.sy - want.Y) < 1e-3 * (1 + System.Math.Abs(want.Y)) && System.Math.Abs(got.sz - want.Z) < 1e-3 * (1 + System.Math.Abs(want.Z));

        public static void RunRenderer()
        {
            int errors = 0;
            var go = new GameObject("Blocks");
            var r = go.AddComponent<TabletopRenderer>();
            r.OpaqueMaterial = new Material(Shader.Find("TabletopCraft/Blocks"));
            r.TransparentMaterial = new Material(Shader.Find("TabletopCraft/BlocksTransparent"));
            var atlas = new ResourcePackAtlas();   // no pack: flat colours, like the headset without pack.zip
            r.Atlas = atlas;
            var model = new WorldModel(); r.Model = model;
            var update = typeof(TabletopRenderer).GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var expect = new Dictionary<Vector3Int, Stat>();
            int id = 0;

            int Pump(int timeoutMs)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); int frames = 0;
                while ((!r.Idle || r.Refreshing > 0) && sw.ElapsedMilliseconds < timeoutMs) { update.Invoke(r, null); frames++; System.Threading.Thread.Sleep(1); }
                return frames;
            }

            void Report(string what, string how, int frames, long ms)
            {
                var got = OnScreen(go.transform); var want = Sum(expect);
                bool ok = r.Idle && Same(got, want) && (go.transform.Find("Retiring")?.childCount ?? 0) == 0;
                if (!ok) errors++;
                Debug.Log($"BATCHRENDER {(ok ? "PASS" : "FAIL")} {what}: {how} in {frames} frames {ms} ms; on screen {got.verts} verts {got.tris} tris, expected {want.V}/{want.T}; {r.Stats()}");
            }

            void Step(string what, WorldModel next, bool rekeyExpected)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var plan = model.Loaded ? SectionPlan.Compute(model, Snap(next), r.Unsettled(), null) : null;
                var oldOrigin = model.Loaded ? model.Origin : next.Origin;
                model.ApplySnapshot(Snap(next));
                r.OnSnapshot(plan, oldOrigin, next.Origin);
                int frames = Pump(60000);
                expect = Expect(model, expect, plan, atlas);
                if (plan != null && plan.Rekey != rekeyExpected) { errors++; Debug.LogError($"BATCHRENDER {what}: re-key {plan.Rekey}, expected {rekeyExpected}"); }
                Report(what, plan == null ? "first load" : plan.Rekey ? $"re-keyed by {plan.ShiftBlocks}, {plan.Mesh.Count} meshed, {plan.Refresh.Count} refreshed, {plan.Kept} kept" : "staged",
                       frames, sw.ElapsedMilliseconds);
            }

            var dims = new Vector3Int(96, 48, 96);
            Step("load 96x48x96", Terrain(new Vector3Int(-48, 0, -48), dims, ++id), false);
            Step("shift east one section", Terrain(new Vector3Int(-32, 0, -48), dims, ++id), true);
            {
                // An edit in the middle of section (2,1,2): only that section is re-meshed, and its batch rebuilt.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                model.SetLocal(40, 20, 40, (ushort)model.Palette.IndexOf("minecraft:stone"));
                int frames = Pump(20000);
                expect[new Vector3Int(2, 1, 2)] = MeshStat(model, new Vector3Int(2, 1, 2), atlas, new MeshData());
                Report("block edit", "1 section", frames, sw.ElapsedMilliseconds);
            }
            var edited = Terrain(new Vector3Int(-32, 0, -48), dims, ++id);
            edited.Grid = (ushort[])model.Grid.Clone(); edited.Palette.Clear(); edited.Palette.AddRange(model.Palette);
            Step("grow to 160x48x160 around the view", Shifted(edited, new Vector3Int(-64, 0, -80), new Vector3Int(160, 48, 160), ++id), true);
            Step("shrink to 64x48x64", Shifted(model, new Vector3Int(-16, 0, -32), new Vector3Int(64, 48, 64), ++id), true);
            Step("move down one section", Terrain(new Vector3Int(-16, -16, -32), new Vector3Int(64, 48, 64), ++id), true);
            Step("jump far away (staged)", Terrain(new Vector3Int(4000, 0, 4000), new Vector3Int(64, 48, 64), ++id), false);
            r.Underwater = true;
            int drawsUnder = r.Stats().DrawCalls;
            r.Underwater = false;
            bool water = drawsUnder > r.Stats().DrawCalls;
            if (!water) errors++;
            Debug.Log($"BATCHRENDER {(water ? "PASS" : "FAIL")} underwater adds the inside-water submesh draws ({r.Stats().DrawCalls} -> {drawsUnder})");
            {
                // [nav] Lazy margin (MeshScope): meshes outside KeepScope are dropped, and meshed again when the window returns.
                var all = expect;
                var box = new SectionBox { Min = new Vector3Int(1, 0, 1), Max = new Vector3Int(3, 3, 3) };
                r.Scope = r.KeepScope = r.VisibleScope = box;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                update.Invoke(r, null);   // a frame sees the new boxes (Pump only runs while something is pending)
                int frames = 1 + Pump(20000);
                expect = new Dictionary<Vector3Int, Stat>();
                foreach (var kv in all) if (box.Contains(kv.Key)) expect[kv.Key] = kv.Value;
                Report("lazy margin drops meshes outside the kept box", $"{all.Count - expect.Count} sections dropped", frames, sw.ElapsedMilliseconds);
                r.Scope = r.KeepScope = r.VisibleScope = SectionBox.All;
                sw.Restart();
                update.Invoke(r, null);
                frames = 1 + Pump(20000);
                expect = all;
                Report("lazy margin meshes them again when the window comes back", "re-meshed", frames, sw.ElapsedMilliseconds);
            }
            {
                // [mesh] Surface skirt: while the view moves, the visible columns whose new sections aren't on screen yet get a
                // skirt tile (World/SurfaceSkirt.cs); when everything is meshed and uploaded the tiles go and what is on screen
                // is the terrain alone again.
                bool skirtWas = SurfaceSkirt.Enabled; SurfaceSkirt.Enabled = true;
                var n = model.SectionCount;
                var box = new SectionBox { Min = new Vector3Int(0, 0, 0), Max = n };
                r.Scope = r.KeepScope = r.VisibleScope = box;
                r.Motion = new Vector3(20, 0, 0);
                r.SkirtBottom = 0; r.SkirtTop = model.Dims.y;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var next = Terrain(model.Origin + new Vector3Int(16, 0, 0), model.Dims, ++id);
                var plan = SectionPlan.Compute(model, Snap(next), r.Unsettled(), null);
                var oldOrigin = model.Origin;
                model.ApplySnapshot(Snap(next));
                r.OnSnapshot(plan, oldOrigin, next.Origin);
                int frames = 0, most = 0, shown = 0;
                for (; frames < 20000 && (frames < 3 || !r.Idle || r.Refreshing > 0 || r.SkirtTiles > 0); frames++)
                {
                    update.Invoke(r, null);
                    most = Mathf.Max(most, r.SkirtTiles);
                    foreach (var mf in go.GetComponentsInChildren<MeshFilter>(false)) if (mf.name == "skirt" && mf.sharedMesh && mf.sharedMesh.vertexCount > 0) { shown++; break; }
                    System.Threading.Thread.Sleep(1);
                }
                bool cleared = r.SkirtTiles == 0;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(false)) if (mf.name == "skirt") cleared = false;
                if (most == 0 || shown == 0 || !cleared) { errors++; Debug.LogError($"BATCHRENDER skirt: most {most} tiles, shown in {shown} frames, cleared {cleared}"); }
                expect = Expect(model, expect, plan, atlas);
                Report("skirt while gliding east", $"up to {most} skirt tiles, on screen in {shown} frames, all gone after", frames, sw.ElapsedMilliseconds);
                r.Motion = Vector3.zero;
                r.Scope = r.KeepScope = r.VisibleScope = SectionBox.All;
                SurfaceSkirt.Enabled = skirtWas;
            }
            Debug.Log($"BATCHRENDER done: {errors} errors");
            Object.DestroyImmediate(go);
            if (Application.isBatchMode) EditorApplication.Exit(errors == 0 ? 0 : 1);
        }


        /// <summary>A region with other origin/dims: blocks from `from` where they overlap (keeps edits), else generated.</summary>
        static WorldModel Shifted(WorldModel from, Vector3Int origin, Vector3Int dims, int id)
        {
            var w = Terrain(origin, dims, id);
            for (int y = 0; y < dims.y; y++) for (int z = 0; z < dims.z; z++) for (int x = 0; x < dims.x; x++)
            {
                int fx = origin.x + x - from.Origin.x, fy = origin.y + y - from.Origin.y, fz = origin.z + z - from.Origin.z;
                if (!from.InBounds(fx, fy, fz)) continue;
                string s = from.State(from.Get(fx, fy, fz));
                int i = w.Palette.IndexOf(s);
                if (i < 0) { i = w.Palette.Count; w.Palette.Add(s); }
                w.Grid[w.Index(x, y, z)] = (ushort)i;
            }
            return w;
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }
    }
}
#endif
