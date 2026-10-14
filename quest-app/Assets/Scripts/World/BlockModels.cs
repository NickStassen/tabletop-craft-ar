// Baked block models by state (main thread prepares, mesher workers read) and the mesher path for every block
// whose model is not a full cube (BlockShape.Model). Engine-free; see BlockModelBaker.cs for the baking itself.
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class BlockModels : IModelSource
    {
        readonly ConcurrentDictionary<string, BakedModel> _models = new ConcurrentDictionary<string, BakedModel>();
        readonly BlockModelBaker _baker;

        public BlockModels(IModelAssets assets) { _baker = new BlockModelBaker(assets); }

        /// <summary>Any thread. Null until Prepare ran for the state (or when the pack has no blockstate for it).</summary>
        public BakedModel Model(string state) => _models.TryGetValue(state, out var m) ? m : null;

        /// <summary>MAIN THREAD, before any worker meshes the state. Bakes its model and tells the mesher how to draw
        /// it: a full cube stays in the greedy path (its six faces, with x/y rotation and UV rotation, go into
        /// cubeFaces), anything else becomes BlockShape.Model. Air and fluids keep their name-based shape.</summary>
        public BakedModel Prepare(string state, FaceKey[] cubeFaces)
        {
            if (_models.TryGetValue(state, out var done)) return done;
            BakedModel m = null;
            try { m = _baker.Bake(state); }
            catch (Exception e) { Debug.LogWarning($"[Tabletop] model for {state}: {e.Message}"); }
            var byName = BlockRules.Classify(state);
            if (m != null && byName != BlockShape.Air && byName != BlockShape.Fluid)
            {
                if (m.FullCube)
                {
                    if (cubeFaces != null) for (int d = 0; d < 6; d++) cubeFaces[d] = m.CubeFaces[d];
                    // Cut-out or translucent cubes (glass, leaves, grates) must not hide their neighbours' faces.
                    BlockRules.SetShape(state, byName == BlockShape.TransparentCube || !m.Opaque ? BlockShape.TransparentCube : BlockShape.Cube);
                }
                else BlockRules.SetShape(state, BlockShape.Model);
            }
            _models[state] = m;
            return m;
        }
    }

    public static class BlockModelMesher
    {
        static readonly int[] DX = { -1, 1, 0, 0, 0, 0 }, DY = { 0, 0, -1, 1, 0, 0 }, DZ = { 0, 0, 0, 0, -1, 1 };

        /// <summary>Emits the baked quads of the block at region-local (x, y, z). A face with a cullface is dropped
        /// when the neighbour on that side is an opaque cube, a block model whose boundary covers it, or (for
        /// translucent faces) the same block, like glass panes joining up.</summary>
        public static void Emit(MeshData m, WorldModel w, int x, int y, int z, string state, BakedModel model, IModelSource models)
        {
            if (model == null) return;
            var quads = model.Quads;
            var o = new Vector3(x, y, z);
            for (int i = 0; i < quads.Length; i++)
            {
                int c = quads[i].Cull;
                if (c >= 0 && Hidden(w, x + DX[c], y + DY[c], z + DZ[c], c, state, model, i, models)) continue;
                AddQuad(m, o, quads[i], BlockShading.Model(w, x, y, z, quads[i]));
            }
        }

        static bool Hidden(WorldModel w, int nx, int ny, int nz, int c, string state, BakedModel model, int quad, IModelSource models)
        {
            ushort nid = w.Get(nx, ny, nz);
            if (nid == 0xFFFF) return false;   // region border: drawn, like the greedy path does
            string ns = w.State(nid);
            var shape = BlockRules.Shape(ns);
            if (shape == BlockShape.Cube) return true;
            if (shape != BlockShape.Model || models == null) return false;
            if (model.Quads[quad].Translucent && SameBlock(state, ns)) return true;
            var nm = models.Model(ns);
            return nm != null && nm.Covers(c ^ 1, model.CullCells, quad);
        }

        static bool SameBlock(string a, string b)
        {
            int ia = a.IndexOf('['), ib = b.IndexOf('[');
            int la = ia < 0 ? a.Length : ia, lb = ib < 0 ? b.Length : ib;
            return la == lb && string.CompareOrdinal(a, 0, b, 0, la) == 0;
        }

        /// <summary>Appends one model face at block origin o (region-local block units, Minecraft frame; the renderer
        /// mirrors Z). Same layout as GreedyMesher.AddQuad but with the model's own UVs.</summary>
        public static void AddQuad(MeshData m, Vector3 o, in BakedQuad q) => AddQuad(m, o, q, new Quad4(q.Color));

        /// <summary>As above with per-vertex colours: BlockShading.Model gives the biome tint (q.Tint), the smooth light
        /// and AO of axis-aligned faces, and the block's own light for rotated ones.</summary>
        public static void AddQuad(MeshData m, Vector3 o, in BakedQuad q, Quad4 col)
        {
            int s = m.Verts.Count;
            m.Verts.Add(o + q.P0); m.Verts.Add(o + q.P1); m.Verts.Add(o + q.P2); m.Verts.Add(o + q.P3);
            m.Uvs.Add(q.T0); m.Uvs.Add(q.T1); m.Uvs.Add(q.T2); m.Uvs.Add(q.T3);
            m.Colors.Add(col.C0); m.Colors.Add(col.C1); m.Colors.Add(col.C2); m.Colors.Add(col.C3);
            var t = q.Translucent ? m.Transparent : m.Opaque;
            t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
        }
    }
}
