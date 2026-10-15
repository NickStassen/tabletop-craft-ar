// Bakes an entity_models.json layer into one static mesh for a block entity: every part placed by its pivot and
// rotation (the layer's default pose, with a renderer's setupAnim values on top: BlockEntityCatalog.Pose), optionally
// only one part and its children (a decorated pot side). Figure space like EntityModels (point-reflected game model
// space, 1 unit = 1 pixel), so the same cube/UV code builds it. Engine-free; Tests~ bakes the real layers.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class BlockEntityModels
    {
        /// <summary>The whole layer (or one part of it, only != null) in one figure-space mesh; null when empty.</summary>
        public static FigureMesh Bake(EModel m, PoseValue[] pose, string only)
        {
            if (m == null) return null;
            var fm = new FigureMesh();
            var tmp = new FigureMesh();
            Walk(m, m.Root, M4.Identity, only == null, pose, only, fm, tmp);
            return fm.VertexCount > 0 ? fm : null;
        }

        static void Walk(EModel m, EModelPart p, M4 parent, bool include, PoseValue[] pose, string only, FigureMesh into, FigureMesh tmp)
        {
            float px = p.Px, py = p.Py, pz = p.Pz, rx = p.Rx, ry = p.Ry, rz = p.Rz;
            string name = p.Parent == null ? "" : p.Name;
            if (pose != null)
                foreach (var v in pose)
                {
                    if (v.Part != name) continue;
                    switch (v.Field)
                    {
                        case 0: px = v.Value; break;
                        case 1: py = v.Value; break;
                        case 2: pz = v.Value; break;
                        case 3: rx = v.Value; break;
                        case 4: ry = v.Value; break;
                        case 5: rz = v.Value; break;
                    }
                }
            // Figure space: the pivot is point-reflected, rotations keep their numbers (EntityModels, EntityFigure).
            var local = parent * M4.T(-px, -py, -pz) * M4.RotZYX(rx, ry, rz) * M4.S(p.Sx, p.Sy, p.Sz);
            bool inc = include || (only != null && p.Name == only);
            if (inc && p.Cubes.Count > 0)
            {
                tmp.Clear();
                EntityModels.BuildPart(m, p, tmp);
                int start = into.VertexCount;
                for (int k = 0; k < tmp.VertexCount; k++)
                {
                    var pos = local.Point(new Vector3(tmp.Pos[3 * k], tmp.Pos[3 * k + 1], tmp.Pos[3 * k + 2]));
                    var n = local.Dir(new Vector3(tmp.Normal[3 * k], tmp.Normal[3 * k + 1], tmp.Normal[3 * k + 2]));
                    float len = (float)Math.Sqrt(n.x * n.x + n.y * n.y + n.z * n.z);
                    if (len > 1e-6f) n = n * (1f / len);
                    into.Pos.Add(pos.x); into.Pos.Add(pos.y); into.Pos.Add(pos.z);
                    into.Normal.Add(n.x); into.Normal.Add(n.y); into.Normal.Add(n.z);
                    into.Uv.Add(tmp.Uv[2 * k]); into.Uv.Add(tmp.Uv[2 * k + 1]);
                }
                foreach (var t in tmp.Tris) into.Tris.Add(start + t);
            }
            foreach (var c in p.Children) Walk(m, c, local, inc, pose, only, into, tmp);
        }

        /// <summary>Axis-aligned bounds of a mesh after a transform (tests, culling).</summary>
        public static void Bounds(FigureMesh fm, M4 m, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int k = 0; k < fm.VertexCount; k++)
            {
                var v = m.Point(new Vector3(fm.Pos[3 * k], fm.Pos[3 * k + 1], fm.Pos[3 * k + 2]));
                min = new Vector3(Math.Min(min.x, v.x), Math.Min(min.y, v.y), Math.Min(min.z, v.z));
                max = new Vector3(Math.Max(max.x, v.x), Math.Max(max.y, v.y), Math.Max(max.z, v.z));
            }
        }

        /// <summary>Block space of a part vertex: blockPos + M * S(-1/16) * figure vertex (see BlockEntityCatalog).</summary>
        public static M4 ToBlock(M4 vanilla) => vanilla * M4.S(-1f / 16);
    }
}
