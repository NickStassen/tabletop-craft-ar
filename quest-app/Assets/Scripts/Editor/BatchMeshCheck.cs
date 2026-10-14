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

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }
    }
}
#endif
