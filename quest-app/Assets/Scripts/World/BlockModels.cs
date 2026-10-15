// Baked block models by state (main thread prepares, mesher workers read) and the mesher path for every block
// whose model is not a full cube (BlockShape.Model). Engine-free; see BlockModelBaker.cs for the baking itself.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
                    RandomUv.Assign(m);   // [random] rotated/mirrored variants of greedy cubes: per-block in the shader
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

    /// <summary>
    /// [random] Per-block texture rotation of greedy cubes. Vanilla turns or mirrors stone, dirt, grass tops, sand,
    /// netherrack, deepslate... per position (weighted blockstate lists), but a greedy quad spans many blocks, so the
    /// block shaders pick the variant per block cell (Shaders/TabletopRandom.cginc) and greedy merging is unchanged.
    /// A table lists, per variant, how that variant's UVs on one side differ from variant 0's: a 3-bit code (bit0 swap
    /// u/v, bit1 negate u, bit2 negate v, as FaceKey.UvMap) applied to the in-block texture coordinate. FaceKey.Random
    /// is the table id (0 = none); the mesher encodes it in uv.z as layer + LayerStride * id. The shader's pick is a
    /// stable hash of the world block, not the Java client's exact choice (that needs 64-bit maths per pixel); model
    /// blocks use the exact vanilla pick (BlockRandom). Main thread only (BlockModels.Prepare and the renderer).
    /// </summary>
    public static class RandomUv
    {
        public const int MaxTables = 63, MaxEntries = 16, LayerStride = 4096;
        static readonly List<byte[]> Tables = new List<byte[]>();
        /// <summary>Bumped when a table is added: the renderer re-uploads Packed().</summary>
        public static int Version { get; private set; }
        public static int Count => Tables.Count;

        public static void Reset() { Tables.Clear(); Version++; }

        /// <summary>Sets m.CubeFaces[d].Random for a full cube whose random variants are all full cubes with the same
        /// texture per side and only differ in UV rotation/mirroring.</summary>
        public static void Assign(BakedModel m)
        {
            var v = m.Variants;
            if (v == null || v.Length < 2 || v.Length > MaxEntries) return;
            foreach (var k in v) if (!k.FullCube || k.CubeFaces == null) return;
            for (int d = 0; d < 6; d++)
            {
                var codes = new byte[v.Length];
                bool same = true, any = false;
                for (int k = 0; k < v.Length && same; k++)
                {
                    var a = v[0].CubeFaces[d]; var b = v[k].CubeFaces[d];
                    same = a.Layer == b.Layer && a.Overlay == b.Overlay && a.Tint == b.Tint;
                    codes[k] = Relative(a.UvMap, b.UvMap);
                    any |= codes[k] != 0;
                }
                if (same && any && m.CubeFaces[d].Layer >= 0) m.CubeFaces[d].Random = Register(codes);
            }
        }

        /// <summary>The code R with UvMap(to) = R * UvMap(from) (2x2 signed permutations, see CubeUv.Greedy).</summary>
        public static byte Relative(byte from, byte to)
        {
            Matrix(from, out int a00, out int a01, out int a10, out int a11);
            Matrix(to, out int b00, out int b01, out int b10, out int b11);
            // Inverse of a signed permutation = its transpose.
            int r00 = b00 * a00 + b01 * a01, r01 = b00 * a10 + b01 * a11, r10 = b10 * a00 + b11 * a01, r11 = b10 * a10 + b11 * a11;
            if (r00 != 0) return (byte)((r00 < 0 ? 2 : 0) | (r11 < 0 ? 4 : 0));
            return (byte)(1 | (r01 < 0 ? 2 : 0) | (r10 < 0 ? 4 : 0));
        }

        /// <summary>A UvMap code as a matrix: swap first (bit0), then negate u (bit1) and v (bit2).</summary>
        public static void Matrix(byte code, out int m00, out int m01, out int m10, out int m11)
        {
            int su = (code & 2) != 0 ? -1 : 1, sv = (code & 4) != 0 ? -1 : 1;
            if ((code & 1) == 0) { m00 = su; m01 = 0; m10 = 0; m11 = sv; }
            else { m00 = 0; m01 = su; m10 = sv; m11 = 0; }
        }

        /// <summary>Table id for these codes (1..MaxTables), shared by equal tables; 0 when the tables are full.</summary>
        public static byte Register(byte[] codes)
        {
            for (int i = 0; i < Tables.Count; i++)
                if (Tables[i].Length == codes.Length && System.Linq.Enumerable.SequenceEqual(Tables[i], codes)) return (byte)(i + 1);
            if (Tables.Count >= MaxTables) return 0;
            Tables.Add((byte[])codes.Clone());
            Version++;
            return (byte)Tables.Count;
        }

        /// <summary>The shader table: float4 per id (index 0 unused): x = entries, y = codes of entries 0-7 (3 bits each),
        /// z = codes of entries 8-15. Exact in floats (24 bits).</summary>
        public static float[] Packed()
        {
            var r = new float[(MaxTables + 1) * 4];
            for (int t = 0; t < Tables.Count; t++)
            {
                var c = Tables[t];
                int lo = 0, hi = 0;
                for (int k = 0; k < c.Length; k++) { if (k < 8) lo |= c[k] << (3 * k); else hi |= c[k] << (3 * (k - 8)); }
                r[(t + 1) * 4] = c.Length; r[(t + 1) * 4 + 1] = lo; r[(t + 1) * 4 + 2] = hi;
            }
            return r;
        }

        public static byte[] Table(int id) => id >= 1 && id <= Tables.Count ? Tables[id - 1] : null;

        /// <summary>The in-block texture coordinate (0..1) variant `code` samples instead of (u, v): what the shader does.</summary>
        public static void Apply(byte code, ref float u, ref float v)
        {
            float tu = u - 0.5f, tv = v - 0.5f;
            if ((code & 1) != 0) { float t = tu; tu = tv; tv = t; }
            if ((code & 2) != 0) tu = -tu;
            if ((code & 4) != 0) tv = -tv;
            u = tu + 0.5f; v = tv + 0.5f;
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
            // [random] Vanilla's per-position picks (weighted variants) and model offsets (flowers, grass, bamboo...), from
            // the world position like the Java client, so the same block looks the same on the table.
            int wx = w.Origin.x + x, wy = w.Origin.y + y, wz = w.Origin.z + z;
            var off = BlockRandom.Offset(model, wx, wz);
            if (model.Variants != null) model = model.Pick(wx, wy, wz);
            var quads = model.Quads;
            var o = new Vector3(x, y, z);
            var at = o + off;
            for (int i = 0; i < quads.Length; i++)
            {
                int c = quads[i].Cull;
                if (c >= 0 && Hidden(w, x + DX[c], y + DY[c], z + DZ[c], c, state, model, i, models)) continue;
                AddQuad(m, at, quads[i], BlockShading.Model(w, x, y, z, quads[i], model.Shade));   // lit at the block's own cell, as vanilla
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
            m.AddColors(col);   // [living] colours and light codes
            var t = q.Translucent ? m.Transparent : m.Opaque;
            t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
        }
    }
}
