// Cross-section caps for the clipped tabletop box (World/ClipCaps). The block shaders discard everything outside
// the visible window, which would leave the cut sides hollow (faces between two solid blocks are never meshed).
// For each side of the window, this meshes the slice of blocks the plane cuts through as one textured quad per
// greedy rectangle, facing outwards, so the box looks solid: strata, ores and caves show in the cut like in
// Minecraft. Engine-free (runs on a worker thread); output is in the same right-handed region-local block frame as
// GreedyMesher, with the plane's own axis at 0 (ClipCaps moves the plane), and uses the mesher's UV layout.
using System;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class CapMesher
    {
        /// <summary>Shapes that fill their whole block and therefore get a cap where the window cuts them.</summary>
        public static bool Caps(BlockShape s) => s == BlockShape.Cube || s == BlockShape.TransparentCube || s == BlockShape.Fluid;

        /// <summary>[water] The state a palette state is cut as, or null for no cap. Cells holding water or lava that aren't
        /// cubes (waterlogged models, kelp, seagrass, bubble columns, the fluids themselves) are cut as that fluid, as
        /// FluidMesher draws them, so the water cut has no holes around plants.</summary>
        public static string CapState(string state, BlockShape shape) =>
            shape == BlockShape.Cube || shape == BlockShape.TransparentCube ? state : FluidRules.FaceState(state) ?? (Caps(shape) ? state : null);

        [ThreadStatic] static FaceKey[] _mask;
        [ThreadStatic] static bool[] _has;

        /// <summary>
        /// face: 0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z (outward normal of the window side). slice: region-local block index
        /// along that axis. keys: palette index * 6 + face -> FaceKey; caps: palette index -> gets a cap.
        /// </summary>
        public static void Build(WorldModel w, int face, int slice, FaceKey[] keys, bool[] caps, MeshData m)
        {
            m.Clear();
            int axis = face / 2, ua = (axis + 1) % 3, va = (axis + 2) % 3;
            var d = w.Dims;
            if (slice < 0 || slice >= d[axis]) return;
            int nu = d[ua], nv = d[va], cells = nu * nv;
            if (_mask == null || _mask.Length < cells) { _mask = new FaceKey[cells]; _has = new bool[cells]; }
            var mask = _mask; var has = _has; var grid = w.Grid;
            int pal = caps.Length;
            var p = new Vector3Int();
            p[axis] = slice;
            for (int v = 0; v < nv; v++)
                for (int u = 0; u < nu; u++)
                {
                    p[ua] = u; p[va] = v;
                    int k = u + v * nu;
                    int id = grid[(p.y * d.z + p.z) * d.x + p.x];
                    has[k] = id < pal && caps[id];
                    if (!has[k]) continue;
                    var key = keys[id * 6 + face];
                    // Biome tint per cell (BlockShading.Tint), folded into the key so merging respects it. No light or
                    // AO: the cut reads like a daylight cross-section, like the region's own open-sky edges.
                    // [water] Water keeps its tint kind (colour already folded in): Emit gives it inside faces.
                    if (key.Tint != TintKind.None) { key.Color = BlockShading.Tint(w, key, p.x, p.y, p.z); if (key.Tint != TintKind.Water) key.Tint = TintKind.None; }
                    mask[k] = key;
                }
            for (int v = 0; v < nv; v++)
                for (int u = 0; u < nu;)
                {
                    int k = u + v * nu;
                    if (!has[k]) { u++; continue; }
                    var key = mask[k];
                    int wdt = 1;
                    while (u + wdt < nu && has[k + wdt] && mask[k + wdt].Equals(key)) wdt++;
                    int hgt = 1;
                    for (bool ok = true; v + hgt < nv && ok;)
                    {
                        for (int i = 0; i < wdt; i++) { int kk = u + i + (v + hgt) * nu; if (!has[kk] || !mask[kk].Equals(key)) { ok = false; break; } }
                        if (ok) hgt++;
                    }
                    Emit(m, face, axis, ua, va, (face & 1) == 1, u, v, wdt, hgt, key);
                    for (int j = 0; j < hgt; j++) for (int i = 0; i < wdt; i++) has[u + i + (v + j) * nu] = false;
                    u += wdt;
                }
        }

        static void Emit(MeshData m, int face, int axis, int ua, int va, bool positive, int u, int v, int w, int h, FaceKey key)
        {
            Vector3 Corner(float cu, float cv) { var c = new Vector3(); c[ua] = cu; c[va] = cv; return c; }
            int start = m.Verts.Count;
            m.Verts.Add(Corner(u, v)); m.Verts.Add(Corner(u + w, v)); m.Verts.Add(Corner(u + w, v + h)); m.Verts.Add(Corner(u, v + h));
            // Same UVs as GreedyMesher's faces (vanilla orientation per side, tiled per block, the model's UvMap).
            for (int i = start; i < start + 4; i++) m.Uvs.Add(CubeUv.Greedy(face, m.Verts[i], key));
            for (int i = 0; i < 4; i++) m.Colors.Add(key.Color);
            var tris = key.Transparent ? m.Transparent : m.Opaque;
            // Same winding rule as GreedyMesher.EmitQuad (right-handed, CCW seen from outside); the uploader flips it after mirroring Z.
            if (positive) { tris.Add(start); tris.Add(start + 1); tris.Add(start + 2); tris.Add(start); tris.Add(start + 2); tris.Add(start + 3); }
            else { tris.Add(start); tris.Add(start + 2); tris.Add(start + 1); tris.Add(start); tris.Add(start + 3); tris.Add(start + 2); }
            // [water] A water cut seen from inside the water (an aquarium wall): reversed copy with own vertices in
            // MeshData.WaterBack, drawn only while the eye is under water (ClipCaps.Underwater), as FluidMesher does.
            if (key.Tint == TintKind.Water)
            {
                int b = m.Verts.Count;
                for (int i = 0; i < 4; i++) { m.Verts.Add(m.Verts[start + i]); m.Uvs.Add(m.Uvs[start + i]); m.Colors.Add(m.Colors[start + i]); }
                var wb = m.WaterBack;
                if (positive) { wb.Add(b); wb.Add(b + 2); wb.Add(b + 1); wb.Add(b); wb.Add(b + 3); wb.Add(b + 2); }
                else { wb.Add(b); wb.Add(b + 1); wb.Add(b + 2); wb.Add(b); wb.Add(b + 2); wb.Add(b + 3); }
            }
        }

        /// <summary>
        /// Region-local block slice cut by a window side. lo/hi: the window's extent on that axis in region-local
        /// blocks (fractional). Negative faces cut the block at floor(lo), positive faces the block at ceil(hi) - 1.
        /// </summary>
        public static int Slice(int face, float lo, float hi) => (face & 1) == 0 ? (int)Math.Floor(lo) : (int)Math.Ceiling(hi) - 1;
    }
}
