// Greedy mesher for one 16^3 section (ARCHITECTURE.md section 4). Works in Minecraft region-local block
// coordinates; TabletopRenderer converts to Unity (z mirrored). Faces merge only with an equal FaceKey
// (texture layer + tint + colour), and UVs are in block units so a Texture2DArray with Repeat wrap tiles them.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public struct FaceKey
    {
        public int Layer;        // Texture2DArray layer, -1 = flat colour only
        public Color32 Color;    // tint (biome) or flat colour
        public bool Transparent;
        public bool Equals(FaceKey o) => Layer == o.Layer && Color.r == o.Color.r && Color.g == o.Color.g && Color.b == o.Color.b && Color.a == o.Color.a && Transparent == o.Transparent;
    }

    public interface IFaceSource
    {
        /// <summary>dir: 0=-X 1=+X 2=-Y 3=+Y 4=-Z(north) 5=+Z(south)</summary>
        FaceKey Face(string state, int dir);
    }

    public sealed class MeshData
    {
        public readonly List<Vector3> Verts = new List<Vector3>();
        public readonly List<Vector3> Uvs = new List<Vector3>();   // u, v (block units), layer
        public readonly List<Color32> Colors = new List<Color32>();
        public readonly List<int> Opaque = new List<int>();
        public readonly List<int> Transparent = new List<int>();
        public void Clear() { Verts.Clear(); Uvs.Clear(); Colors.Clear(); Opaque.Clear(); Transparent.Clear(); }
    }

    public static class GreedyMesher
    {
        static readonly Vector3Int[] Dirs = { new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1) };

        public static void Build(WorldModel w, Vector3Int section, IFaceSource faces, MeshData m)
        {
            m.Clear();
            int S = WorldModel.Section;
            Vector3Int b0 = section * S;
            Vector3Int size = new Vector3Int(Mathf.Min(S, w.Dims.x - b0.x), Mathf.Min(S, w.Dims.y - b0.y), Mathf.Min(S, w.Dims.z - b0.z));
            var mask = new FaceKey[S * S];
            var has = new bool[S * S];

            for (int dir = 0; dir < 6; dir++)
            {
                int axis = dir / 2;              // 0=x 1=y 2=z
                int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                Vector3Int n = Dirs[dir];
                for (int d = 0; d < size[axis]; d++)
                {
                    // Build the mask for this slice.
                    for (int v = 0; v < size[va]; v++)
                        for (int u = 0; u < size[ua]; u++)
                        {
                            var p = new Vector3Int(); p[axis] = d; p[ua] = u; p[va] = v; p += b0;
                            int k = u + v * S;
                            has[k] = false;
                            ushort id = w.Get(p.x, p.y, p.z);
                            string st = w.State(id);
                            var shape = BlockRules.Shape(st);
                            if (shape != BlockShape.Cube && shape != BlockShape.TransparentCube && shape != BlockShape.Fluid) continue;
                            var q = p + n;
                            ushort nid = w.Get(q.x, q.y, q.z);
                            if (nid != 0xFFFF)
                            {
                                var ns = BlockRules.Shape(w.State(nid));
                                if (ns == BlockShape.Cube) continue;                 // hidden by an opaque neighbour
                                if (nid == id && shape != BlockShape.Cube) continue; // glass next to same glass, water next to water
                            }
                            mask[k] = faces.Face(st, dir);
                            mask[k].Transparent |= shape != BlockShape.Cube;
                            has[k] = true;
                        }
                    // Greedy merge.
                    for (int v = 0; v < size[va]; v++)
                        for (int u = 0; u < size[ua];)
                        {
                            int k = u + v * S;
                            if (!has[k]) { u++; continue; }
                            var key = mask[k];
                            int wdt = 1;
                            while (u + wdt < size[ua] && has[k + wdt] && mask[k + wdt].Equals(key)) wdt++;
                            int hgt = 1;
                            bool ok = true;
                            while (v + hgt < size[va] && ok)
                            {
                                for (int i = 0; i < wdt; i++) { int kk = u + i + (v + hgt) * S; if (!has[kk] || !mask[kk].Equals(key)) { ok = false; break; } }
                                if (ok) hgt++;
                            }
                            EmitQuad(m, b0, axis, ua, va, dir, d, u, v, wdt, hgt, key);
                            for (int j = 0; j < hgt; j++) for (int i = 0; i < wdt; i++) has[u + i + (v + j) * S] = false;
                            u += wdt;
                        }
                }
            }
        }

        static void EmitQuad(MeshData m, Vector3Int b0, int axis, int ua, int va, int dir, int d, int u, int v, int w, int h, FaceKey key)
        {
            bool positive = (dir & 1) == 1;
            float plane = d + (positive ? 1 : 0);
            Vector3 Corner(float cu, float cv) { var p = new Vector3(); p[axis] = plane; p[ua] = cu; p[va] = cv; return p + (Vector3)b0; }
            int start = m.Verts.Count;
            m.Verts.Add(Corner(u, v)); m.Verts.Add(Corner(u + w, v)); m.Verts.Add(Corner(u + w, v + h)); m.Verts.Add(Corner(u, v + h));
            float layer = key.Layer;
            m.Uvs.Add(new Vector3(0, 0, layer)); m.Uvs.Add(new Vector3(w, 0, layer)); m.Uvs.Add(new Vector3(w, h, layer)); m.Uvs.Add(new Vector3(0, h, layer));
            for (int i = 0; i < 4; i++) m.Colors.Add(key.Color);
            var tris = key.Transparent ? m.Transparent : m.Opaque;
            // Winding for a right-handed frame, CCW seen from outside. The renderer flips it after mirroring Z.
            if (positive) { tris.Add(start); tris.Add(start + 1); tris.Add(start + 2); tris.Add(start); tris.Add(start + 2); tris.Add(start + 3); }
            else { tris.Add(start); tris.Add(start + 2); tris.Add(start + 1); tris.Add(start); tris.Add(start + 3); tris.Add(start + 2); }
        }

        /// <summary>Simple placeholders for non-cube shapes: crossed quads, half-height slabs.</summary>
        public static void BuildSpecials(WorldModel w, Vector3Int section, IFaceSource faces, MeshData m)
        {
            int S = WorldModel.Section;
            Vector3Int b0 = section * S;
            for (int y = b0.y; y < Mathf.Min(b0.y + S, w.Dims.y); y++)
                for (int z = b0.z; z < Mathf.Min(b0.z + S, w.Dims.z); z++)
                    for (int x = b0.x; x < Mathf.Min(b0.x + S, w.Dims.x); x++)
                    {
                        string st = w.State(w.Get(x, y, z));
                        var shape = BlockRules.Shape(st);
                        if (shape == BlockShape.Cross) Cross(m, x, y, z, faces.Face(st, 4));
                        else if (shape == BlockShape.Slab) Box(m, new Vector3(x, y + (st.Contains("type=top") ? 0.5f : 0), z), new Vector3(1, 0.5f, 1), faces, st);
                    }
        }

        static void Cross(MeshData m, int x, int y, int z, FaceKey key)
        {
            key.Transparent = true;
            AddQuad(m, new Vector3(x, y, z), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y + 1, z + 1), new Vector3(x, y + 1, z), key, true);
            AddQuad(m, new Vector3(x + 1, y, z), new Vector3(x, y, z + 1), new Vector3(x, y + 1, z + 1), new Vector3(x + 1, y + 1, z), key, true);
        }

        static void Box(MeshData m, Vector3 o, Vector3 s, IFaceSource faces, string st)
        {
            Vector3 a = o, b = o + s;
            AddQuad(m, new Vector3(a.x, b.y, a.z), new Vector3(a.x, b.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z), faces.Face(st, 3), true);
            AddQuad(m, new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z), faces.Face(st, 2), true);
            AddQuad(m, new Vector3(a.x, a.y, a.z), new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, a.y, a.z), faces.Face(st, 4), true);
            AddQuad(m, new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, a.y, b.z), faces.Face(st, 5), true);
            AddQuad(m, new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, b.y, a.z), new Vector3(a.x, a.y, a.z), faces.Face(st, 0), true);
            AddQuad(m, new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, a.y, b.z), faces.Face(st, 1), true);
        }

        static void AddQuad(MeshData m, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, FaceKey key, bool doubleSided)
        {
            int s = m.Verts.Count;
            m.Verts.Add(p0); m.Verts.Add(p1); m.Verts.Add(p2); m.Verts.Add(p3);
            float l = key.Layer;
            m.Uvs.Add(new Vector3(0, 0, l)); m.Uvs.Add(new Vector3(1, 0, l)); m.Uvs.Add(new Vector3(1, 1, l)); m.Uvs.Add(new Vector3(0, 1, l));
            for (int i = 0; i < 4; i++) m.Colors.Add(key.Color);
            var t = key.Transparent ? m.Transparent : m.Opaque;
            t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
            if (doubleSided) { t.Add(s); t.Add(s + 2); t.Add(s + 1); t.Add(s); t.Add(s + 3); t.Add(s + 2); }
        }
    }
}
