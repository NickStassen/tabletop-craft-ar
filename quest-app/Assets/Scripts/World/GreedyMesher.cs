// Greedy mesher for one 16^3 section (ARCHITECTURE.md section 4). Works in Minecraft region-local block
// coordinates; TabletopRenderer converts to Unity (z mirrored). Faces merge only with an equal FaceKey
// (texture layer + tint + colour), and UVs are in block units so a Texture2DArray with Repeat wrap tiles them.
//
// VERTEX COLOURS (biome tint x light x AO) come from ONE place, BlockShading (World/BlockShading.cs). Every geometry
// path must take its colours from it so new geometry gets biome colours and lighting for free:
//   - full cube faces (Build):          BlockShading.FaceCorners(w, key, x, y, z, dir)   -> Quad4 (smooth light + AO)
//   - any axis-aligned quad (slabs, model elements, fluid surfaces):
//                                       BlockShading.Quad(w, key, block, dir, p0, p1, p2, p3) -> Quad4
//   - quads that aren't axis-aligned (cross plants, rotated elements):
//                                       BlockShading.Flat(w, key, block)                  -> Color32 (own cell, no AO)
//   - baked block models (BlockModelMesher.Emit):  BlockShading.Model(w, x, y, z, quad)  -> Quad4 (Quad or Flat)
//   - fluids (FluidMesher, folded into the face key so merging respects it): BlockShading.Fluid(w, tint, x, y, z, dir)
// then pass the Quad4 to AddQuad / EmitQuad. key.Tint (set by the atlas from the model's tintindex) picks the biome
// colour (grass/foliage/dry foliage/water); without server light/biome data the colours equal the old fixed look.
// Greedy merging only joins faces whose 4 corner colours are equal and constant along the merge direction.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public struct FaceKey
    {
        public int Layer;        // Texture2DArray layer, -1 = flat colour only
        public Color32 Color;    // fixed tint or flat colour; the fallback when no biome data (Tint != None)
        public bool Transparent;
        public TintKind Tint;    // biome tint resolved per block by BlockShading (None = use Color)
        public byte UvMap;       // texture rotation/mirror from the block model (CubeUv in BlockModelBaker.cs), 0 = vanilla default
        // A second, coplanar layer drawn over this face (the grass block's biome-tinted side overlay): its layer + 1
        // (0 = none), colour/tint and UV map like the fields above. The mesher emits it as a second quad on exactly
        // the same vertices right after the base quad, so it wins the depth test (LEqual) without z-fighting.
        public int Overlay;
        public Color32 OverlayColor;
        public TintKind OverlayTint;
        public byte OverlayUvMap;
        public byte Random;      // [random] per-block texture rotation table (RandomUv in BlockModels.cs), 0 = none
        public byte Shade;       // BakedModel.Shade: 0 smooth light + AO, 1 flat (no AO), 2 full bright (magma)
        public bool Equals(FaceKey o) => Layer == o.Layer && Color.r == o.Color.r && Color.g == o.Color.g && Color.b == o.Color.b && Color.a == o.Color.a && Transparent == o.Transparent && Tint == o.Tint && UvMap == o.UvMap && Random == o.Random && Shade == o.Shade
            && Overlay == o.Overlay && (Overlay == 0 || (OverlayTint == o.OverlayTint && OverlayUvMap == o.OverlayUvMap && OverlayColor.r == o.OverlayColor.r && OverlayColor.g == o.OverlayColor.g && OverlayColor.b == o.OverlayColor.b && OverlayColor.a == o.OverlayColor.a));

        /// <summary>The overlay layer as a face key of its own (for colours and UVs), or default when there is none.</summary>
        public FaceKey OverlayKey() => Overlay == 0 ? default : new FaceKey { Layer = Overlay - 1, Color = OverlayColor, Tint = OverlayTint, UvMap = OverlayUvMap, Transparent = Transparent, Shade = Shade };
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
        /// <summary>Water faces seen from inside the water (FluidMesher): drawn only while the eye is under water (EyeEffects).</summary>
        public readonly List<int> WaterBack = new List<int>();
        public void Clear() { Verts.Clear(); Uvs.Clear(); Colors.Clear(); Opaque.Clear(); Transparent.Clear(); WaterBack.Clear(); }
    }

    public static class GreedyMesher
    {
        static readonly Vector3Int[] Dirs = { new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1) };

        /// <summary>Vanilla's default leaves (cutoutLeaves, LeavesBlock.skipRendering): faces between two leaves blocks are
        /// drawn even when the blocks are identical, so canopies look full from every side. False culls them like glass
        /// (fewer faces). tabletop.json "leaves_inner".</summary>
        public static bool LeavesInner = true;

        public static void Build(WorldModel w, Vector3Int section, IFaceSource faces, MeshData m)
        {
            m.Clear();
            int S = WorldModel.Section;
            bool[] keepSame = LeavesInner ? LeavesOf(w) : null;   // palette ids whose faces against themselves are drawn
            Vector3Int b0 = section * S;
            Vector3Int size = new Vector3Int(Mathf.Min(S, w.Dims.x - b0.x), Mathf.Min(S, w.Dims.y - b0.y), Mathf.Min(S, w.Dims.z - b0.z));
            var mask = new FaceKey[S * S];
            var has = new bool[S * S];
            var corners = new Quad4[S * S];  // per-face vertex colours from BlockShading
            var ocorners = new Quad4[S * S]; // the same for the face's overlay layer (FaceKey.Overlay), when it has one

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
                            if (shape != BlockShape.Cube && shape != BlockShape.TransparentCube) continue;   // fluids: FluidMesher
                            var q = p + n;
                            ushort nid = w.Get(q.x, q.y, q.z);
                            if (nid != 0xFFFF)
                            {
                                var ns = BlockRules.Shape(w.State(nid));
                                if (ns == BlockShape.Cube) continue;                 // hidden by an opaque neighbour
                                // glass next to same glass, water next to water; not leaves (vanilla draws those, LeavesInner)
                                if (nid == id && shape != BlockShape.Cube && !(keepSame != null && id < keepSame.Length && keepSame[id])) continue;
                            }
                            mask[k] = faces.Face(st, dir);
                            mask[k].Transparent |= shape != BlockShape.Cube;
                            corners[k] = BlockShading.FaceCorners(w, mask[k], p.x, p.y, p.z, dir);
                            if (mask[k].Overlay != 0) ocorners[k] = BlockShading.FaceCorners(w, mask[k].OverlayKey(), p.x, p.y, p.z, dir);
                            has[k] = true;
                        }
                    // Greedy merge: same key and same corner colours; along u only if the colours don't change along u
                    // (c0 == c1, c3 == c2), along v only if they don't change along v (c0 == c3, c1 == c2).
                    for (int v = 0; v < size[va]; v++)
                        for (int u = 0; u < size[ua];)
                        {
                            int k = u + v * S;
                            if (!has[k]) { u++; continue; }
                            var key = mask[k];
                            var col = corners[k];
                            var ocol = ocorners[k];
                            bool ov = key.Overlay != 0;
                            int wdt = 1;
                            if (Eq(col.C0, col.C1) && Eq(col.C3, col.C2) && (!ov || (Eq(ocol.C0, ocol.C1) && Eq(ocol.C3, ocol.C2))))
                                while (u + wdt < size[ua] && has[k + wdt] && mask[k + wdt].Equals(key) && Same(corners[k + wdt], col) && (!ov || Same(ocorners[k + wdt], ocol))) wdt++;
                            int hgt = 1;
                            bool ok = Eq(col.C0, col.C3) && Eq(col.C1, col.C2) && (!ov || (Eq(ocol.C0, ocol.C3) && Eq(ocol.C1, ocol.C2)));
                            while (v + hgt < size[va] && ok)
                            {
                                for (int i = 0; i < wdt; i++)
                                {
                                    int kk = u + i + (v + hgt) * S;
                                    if (!has[kk] || !mask[kk].Equals(key) || !Same(corners[kk], col) || (ov && !Same(ocorners[kk], ocol))) { ok = false; break; }
                                }
                                if (ok) hgt++;
                            }
                            EmitQuad(m, b0, axis, ua, va, dir, d, u, v, wdt, hgt, key, col, ocol);
                            for (int j = 0; j < hgt; j++) for (int i = 0; i < wdt; i++) has[u + i + (v + j) * S] = false;
                            u += wdt;
                        }
                }
            }
        }

        /// <summary>Per palette index: a leaves block (every *_leaves; vanilla's LeavesBlock subclasses).</summary>
        static bool[] LeavesOf(WorldModel w)
        {
            var pal = w.Palette;
            int n = pal.Count;
            var r = new bool[n];
            for (int i = 0; i < n && i < pal.Count; i++) r[i] = BlockRules.Path(pal[i]).EndsWith("_leaves", System.StringComparison.Ordinal);
            return r;
        }

        static bool Eq(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
        static bool Same(Quad4 a, Quad4 b) => Eq(a.C0, b.C0) && Eq(a.C1, b.C1) && Eq(a.C2, b.C2) && Eq(a.C3, b.C3);
        static int Luma(Color32 c) => c.r * 2 + c.g * 5 + c.b;

        static void EmitQuad(MeshData m, Vector3Int b0, int axis, int ua, int va, int dir, int d, int u, int v, int w, int h, FaceKey key, Quad4 col, Quad4 ocol)
        {
            bool positive = (dir & 1) == 1;
            float plane = d + (positive ? 1 : 0);
            Vector3 Corner(float cu, float cv) { var p = new Vector3(); p[axis] = plane; p[ua] = cu; p[va] = cv; return p + (Vector3)b0; }
            // Split along the brighter diagonal so a dark AO corner stays a corner instead of a streak across the quad.
            bool flip = Luma(col.C1) + Luma(col.C3) > Luma(col.C0) + Luma(col.C2);
            var tris = key.Transparent ? m.Transparent : m.Opaque;
            int start = m.Verts.Count;
            m.Verts.Add(Corner(u, v)); m.Verts.Add(Corner(u + w, v)); m.Verts.Add(Corner(u + w, v + h)); m.Verts.Add(Corner(u, v + h));
            // Vanilla face orientation (grass fringe up on every side, logs on their side rotated), tiled per block.
            for (int i = start; i < start + 4; i++) m.Uvs.Add(CubeUv.Greedy(dir, m.Verts[i] - (Vector3)b0, key));
            m.Colors.Add(col.C0); m.Colors.Add(col.C1); m.Colors.Add(col.C2); m.Colors.Add(col.C3);
            Tris(tris, start, positive, flip);
            if (key.Overlay == 0) return;
            // The overlay layer (grass side): the same corners, its own UVs and tinted colours, right after the base quad
            // in the same submesh and with the same split, so it draws over the base at exactly equal depth.
            var over = key.OverlayKey();
            int o = m.Verts.Count;
            for (int i = 0; i < 4; i++) m.Verts.Add(m.Verts[start + i]);
            for (int i = o; i < o + 4; i++) m.Uvs.Add(CubeUv.Greedy(dir, m.Verts[i] - (Vector3)b0, over));
            m.Colors.Add(ocol.C0); m.Colors.Add(ocol.C1); m.Colors.Add(ocol.C2); m.Colors.Add(ocol.C3);
            Tris(tris, o, positive, flip);
        }

        /// <summary>The two triangles of a mesher quad (corners start..start+3): winding for a right-handed frame, CCW
        /// seen from outside (the renderer flips it after mirroring Z), split along the diagonal that flip picks.</summary>
        static void Tris(List<int> tris, int start, bool positive, bool flip)
        {
            if (!flip)
            {
                if (positive) { tris.Add(start); tris.Add(start + 1); tris.Add(start + 2); tris.Add(start); tris.Add(start + 2); tris.Add(start + 3); }
                else { tris.Add(start); tris.Add(start + 2); tris.Add(start + 1); tris.Add(start); tris.Add(start + 3); tris.Add(start + 2); }
            }
            else
            {
                if (positive) { tris.Add(start); tris.Add(start + 1); tris.Add(start + 3); tris.Add(start + 1); tris.Add(start + 2); tris.Add(start + 3); }
                else { tris.Add(start); tris.Add(start + 3); tris.Add(start + 1); tris.Add(start + 1); tris.Add(start + 3); tris.Add(start + 2); }
            }
        }

        /// <summary>Non-cube shapes: baked pack models (BlockShape.Model); without a pack, placeholder crossed quads and slabs.</summary>
        public static void BuildSpecials(WorldModel w, Vector3Int section, IFaceSource faces, MeshData m)
        {
            int S = WorldModel.Section;
            Vector3Int b0 = section * S;
            var models = faces as IModelSource;   // baked resource-pack models (BlockModels.cs), null without a pack
            bool moving = !w.MovingShown.IsEmpty;  // [piston] moving_piston cells draw what moves through them (PistonGhosts)
            for (int y = b0.y; y < Mathf.Min(b0.y + S, w.Dims.y); y++)
                for (int z = b0.z; z < Mathf.Min(b0.z + S, w.Dims.z); z++)
                    for (int x = b0.x; x < Mathf.Min(b0.x + S, w.Dims.x); x++)
                    {
                        string st = w.State(w.Get(x, y, z));
                        var shape = BlockRules.Shape(st);
                        if (shape == BlockShape.Cross) Cross(m, w, x, y, z, faces.Face(st, 4));
                        else if (shape == BlockShape.Slab) Box(m, w, new Vector3Int(x, y, z), new Vector3(x, y + (st.Contains("type=top") ? 0.5f : 0), z), new Vector3(1, 0.5f, 1), faces, st);
                        else if (shape == BlockShape.Model)
                        {
                            if (moving && w.MovingShown.TryGetValue(w.Index(x, y, z), out var shown)) st = shown;
                            BlockModelMesher.Emit(m, w, x, y, z, st, models?.Model(st), models);
                        }
                    }
            // Water/lava, including waterlogged blocks and kelp/seagrass cells. Last, so water blends over the plants
            // and models inside it in the transparent submesh.
            FluidMesher.Build(w, section, faces, m);
        }

        static void Cross(MeshData m, WorldModel w, int x, int y, int z, FaceKey key)
        {
            key.Transparent = true;
            var c = new Quad4(BlockShading.Flat(w, key, new Vector3Int(x, y, z)));
            AddQuad(m, new Vector3(x, y, z), new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y + 1, z + 1), new Vector3(x, y + 1, z), key, true, c);
            AddQuad(m, new Vector3(x + 1, y, z), new Vector3(x, y, z + 1), new Vector3(x, y + 1, z + 1), new Vector3(x + 1, y + 1, z), key, true, c);
        }

        static void Box(MeshData m, WorldModel w, Vector3Int blk, Vector3 o, Vector3 s, IFaceSource faces, string st)
        {
            Vector3 a = o, b = o + s;
            BoxFace(m, w, blk, 3, new Vector3(a.x, b.y, a.z), new Vector3(a.x, b.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z), faces.Face(st, 3));
            BoxFace(m, w, blk, 2, new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(a.x, a.y, b.z), faces.Face(st, 2));
            BoxFace(m, w, blk, 4, new Vector3(a.x, a.y, a.z), new Vector3(a.x, b.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, a.y, a.z), faces.Face(st, 4));
            BoxFace(m, w, blk, 5, new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, a.y, b.z), faces.Face(st, 5));
            BoxFace(m, w, blk, 0, new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, b.y, a.z), new Vector3(a.x, a.y, a.z), faces.Face(st, 0));
            BoxFace(m, w, blk, 1, new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, a.y, b.z), faces.Face(st, 1));
        }

        static void BoxFace(MeshData m, WorldModel w, Vector3Int blk, int dir, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, FaceKey key) =>
            AddQuad(m, p0, p1, p2, p3, key, true, BlockShading.Quad(w, key, blk, dir, p0, p1, p2, p3));

        static void AddQuad(MeshData m, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, FaceKey key, bool doubleSided, Quad4 col)
        {
            int s = m.Verts.Count;
            m.Verts.Add(p0); m.Verts.Add(p1); m.Verts.Add(p2); m.Verts.Add(p3);
            float l = key.Layer;
            m.Uvs.Add(new Vector3(0, 0, l)); m.Uvs.Add(new Vector3(1, 0, l)); m.Uvs.Add(new Vector3(1, 1, l)); m.Uvs.Add(new Vector3(0, 1, l));
            m.Colors.Add(col.C0); m.Colors.Add(col.C1); m.Colors.Add(col.C2); m.Colors.Add(col.C3);
            var t = key.Transparent ? m.Transparent : m.Opaque;
            t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
            if (doubleSided) { t.Add(s); t.Add(s + 2); t.Add(s + 1); t.Add(s); t.Add(s + 3); t.Add(s + 2); }
        }
    }
}
