// Shared Unity meshes for entities: one per model part (EntityModels), the item sprite quad and item cube (item model
// space [0, 1] blocks, centred and point-reflected into figure pixels like the models: v -> -16 * (v - 0.5)), and a unit
// cube for fallback boxes and dig cracks. Built once on the main thread and reused by every entity (instancing-friendly).
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class EntityMeshes
    {
        static readonly Dictionary<(string, int), Mesh> Parts = new Dictionary<(string, int), Mesh>();
        static Mesh _sprite, _cube, _unit;

        /// <summary>The cubes of one model part (null when the part has none).</summary>
        public static Mesh Part(EModel m, EModelPart p)
        {
            if (p.Cubes.Count == 0) return null;
            if (Parts.TryGetValue((m.Name, p.Index), out var mesh) && mesh != null) return mesh;
            var fm = new FigureMesh();
            EntityModels.BuildPart(m, p, fm);
            mesh = Upload(fm, $"{m.Name}/{p.Name}");
            Parts[(m.Name, p.Index)] = mesh;
            return mesh;
        }

        public static Mesh Upload(FigureMesh fm, string name)
        {
            int n = fm.VertexCount;
            var pos = new Vector3[n]; var nrm = new Vector3[n]; var uv = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                pos[k] = new Vector3(fm.Pos[3 * k], fm.Pos[3 * k + 1], fm.Pos[3 * k + 2]);
                nrm[k] = new Vector3(fm.Normal[3 * k], fm.Normal[3 * k + 1], fm.Normal[3 * k + 2]);
                uv[k] = new Vector2(fm.Uv[2 * k], fm.Uv[2 * k + 1]);
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(pos); mesh.SetNormals(nrm); mesh.SetUVs(0, uv);
            mesh.SetTriangles(fm.Tris, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>A flat item sprite: the item model's 16x16 px square at z 0.5, both sides (the shader draws both).</summary>
        public static Mesh Sprite
        {
            get
            {
                if (_sprite != null) return _sprite;
                var fm = new FigureMesh();
                // Item space (x right, y up, z towards the viewer of a held item) -> figure: -16 * (v - 0.5).
                float[][] c = { new[] { 0f, 0, 0.5f }, new[] { 1f, 0, 0.5f }, new[] { 1f, 1, 0.5f }, new[] { 0f, 1, 0.5f } };
                float[][] t = { new[] { 0f, 0 }, new[] { 1f, 0 }, new[] { 1f, 1 }, new[] { 0f, 1 } };
                for (int k = 0; k < 4; k++)
                {
                    fm.Pos.Add(-16 * (c[k][0] - 0.5f)); fm.Pos.Add(-16 * (c[k][1] - 0.5f)); fm.Pos.Add(0);
                    fm.Normal.Add(0); fm.Normal.Add(0); fm.Normal.Add(-1);
                    fm.Uv.Add(t[k][0]); fm.Uv.Add(t[k][1]);
                }
                fm.Tris.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
                return _sprite = Upload(fm, "item sprite");
            }
        }

        /// <summary>A block-like item: the full cube, each face reading its slot of a 6-face strip (down, up, north, south, west, east).</summary>
        public static Mesh ItemCube => _cube != null ? _cube : (_cube = Upload(Cube(true), "item cube"));

        /// <summary>A unit cube (16 px, centred) with the whole texture on every face: fallback boxes, dig cracks.</summary>
        public static Mesh Unit => _unit != null ? _unit : (_unit = Upload(Cube(false), "unit cube"));

        static FigureMesh Cube(bool strip)
        {
            var fm = new FigureMesh();
            // Faces in item/block space: corners (x, y, z) in [0, 1], outward normal, in the strip order.
            var faces = new (Vector3 n, Vector3[] c)[]
            {
                (new Vector3(0, -1, 0), new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 0, 0) }),   // down
                (new Vector3(0, 1, 0), new[] { new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(0, 1, 1) }),    // up
                (new Vector3(0, 0, -1), new[] { new Vector3(1, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) }),   // north
                (new Vector3(0, 0, 1), new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1) }),    // south
                (new Vector3(-1, 0, 0), new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0) }),   // west
                (new Vector3(1, 0, 0), new[] { new Vector3(1, 0, 1), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1) }),    // east
            };
            float[] cu = { 0, 1, 1, 0 }, cv = { 0, 0, 1, 1 };
            for (int f = 0; f < 6; f++)
            {
                int start = fm.VertexCount;
                for (int k = 0; k < 4; k++)
                {
                    var p = faces[f].c[k];
                    fm.Pos.Add(-16 * (p.x - 0.5f)); fm.Pos.Add(-16 * (p.y - 0.5f)); fm.Pos.Add(-16 * (p.z - 0.5f));
                    fm.Normal.Add(-faces[f].n.x); fm.Normal.Add(-faces[f].n.y); fm.Normal.Add(-faces[f].n.z);
                    fm.Uv.Add(strip ? (f + cu[k]) / 6f : cu[k]); fm.Uv.Add(cv[k]);
                }
                // Wind so cross(b - a, c - a) points along the (point-reflected) outward normal, as EntityModels does.
                Vector3 a = V(fm, start), b = V(fm, start + 1), c = V(fm, start + 2), n = -faces[f].n;
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) > 0) fm.Tris.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                else fm.Tris.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }
            return fm;
        }

        static Vector3 V(FigureMesh fm, int i) => new Vector3(fm.Pos[3 * i], fm.Pos[3 * i + 1], fm.Pos[3 * i + 2]);
    }
}
