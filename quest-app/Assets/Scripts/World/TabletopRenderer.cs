// Turns WorldModel sections into meshes under the "Blocks" transform (scaled to block_size_m, Z mirrored),
// rebuilding at most a few dirty sections per frame.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class TabletopRenderer : MonoBehaviour
    {
        public int SectionsPerFrame = 2;
        public Material OpaqueMaterial, TransparentMaterial;
        public WorldModel Model;
        public ResourcePackAtlas Atlas;

        readonly Dictionary<Vector3Int, (Mesh mesh, GameObject go)> _sections = new Dictionary<Vector3Int, (Mesh, GameObject)>();
        readonly MeshData _data = new MeshData();
        readonly List<Vector3> _tmp = new List<Vector3>();

        public void Clear()
        {
            foreach (var s in _sections.Values) { Destroy(s.mesh); Destroy(s.go); }
            _sections.Clear();
        }

        void Update()
        {
            if (Model == null || !Model.Loaded || Model.DirtySections.Count == 0) return;
            int budget = SectionsPerFrame;
            var done = new List<Vector3Int>();
            foreach (var s in Model.DirtySections) { if (budget-- <= 0) break; done.Add(s); }
            foreach (var s in done) { Model.DirtySections.Remove(s); Rebuild(s); }
            Atlas?.Commit();
            if (Atlas?.Array != null)
            {
                OpaqueMaterial.SetTexture("_Blocks", Atlas.Array); OpaqueMaterial.SetFloat("_HasTex", 1);
                TransparentMaterial.SetTexture("_Blocks", Atlas.Array); TransparentMaterial.SetFloat("_HasTex", 1);
            }
        }

        void Rebuild(Vector3Int s)
        {
            var n = Model.SectionCount;
            if (s.x < 0 || s.y < 0 || s.z < 0 || s.x >= n.x || s.y >= n.y || s.z >= n.z) return;
            GreedyMesher.Build(Model, s, Atlas, _data);
            GreedyMesher.BuildSpecials(Model, s, Atlas, _data);
            if (!_sections.TryGetValue(s, out var entry))
            {
                var go = new GameObject($"section {s.x},{s.y},{s.z}", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.GetComponent<MeshRenderer>().sharedMaterials = new[] { OpaqueMaterial, TransparentMaterial };
                entry = (mesh, go);
                _sections[s] = entry;
            }
            // Minecraft (right-handed) -> Unity (left-handed): mirror Z, which also flips triangle winding.
            _tmp.Clear();
            foreach (var v in _data.Verts) _tmp.Add(new Vector3(v.x, v.y, -v.z));
            var m = entry.mesh;
            m.Clear();
            m.SetVertices(_tmp);
            m.SetUVs(0, _data.Uvs);
            m.SetColors(_data.Colors);
            m.subMeshCount = 2;
            m.SetTriangles(Flip(_data.Opaque), 0, false);
            m.SetTriangles(Flip(_data.Transparent), 1, false);
            m.RecalculateNormals();
            m.RecalculateBounds();
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }
    }
}
