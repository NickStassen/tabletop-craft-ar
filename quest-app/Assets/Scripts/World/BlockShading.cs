// Per-vertex colour = biome tint x light x ambient occlusion, the way vanilla's smooth lighting does it (26.x
// BlockModelLighter + lightmap.fsh). Engine-free: runs on the meshing workers and in Tests~. The shader still multiplies
// the fixed per-direction face shade (normals) and converts the sRGB vertex colour to linear.
//
//   light:  each vertex averages the packed (sky, block) light of the 4 cells around it in the layer the face points
//           into (vanilla smoothBlend: a dark neighbour inside an opaque block takes the centre's value), then looks it
//           up in the lightmap at noon (bilinear between levels, like the GPU sampling vanilla's 16x16 lightmap).
//   AO:     (side1 + side2 + corner + centre) / 4 with 0.2 for full-cube cells and 1.0 otherwise; the corner cell is
//           skipped (side1 reused) when neither side lets light through, as vanilla does.
//   no light data (old server, or no light_v1): white light and no AO, i.e. exactly the old look.
//   region edges: a face whose front cell is outside the region (the cut sides of the miniature) is lit as open sky;
//           other lookups outside the region clamp to the nearest cell inside it.
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>The 4 vertex colours of a quad, in the mesher's corner order (u,v), (u+1,v), (u+1,v+1), (u,v+1).</summary>
    public struct Quad4
    {
        public Color32 C0, C1, C2, C3;
        public Quad4(Color32 c) { C0 = C1 = C2 = C3 = c; }
        public Color32 this[int i] => i == 0 ? C0 : i == 1 ? C1 : i == 2 ? C2 : C3;
    }

    public static class BlockShading
    {
        /// <summary>Vanilla "Brightness" option: 0 = Moody, 0.5 = default, 1 = Bright.</summary>
        public static float Brightness { get => _brightness; set { _brightness = Mathf.Clamp01(value); BuildLightmap(); } }
        static float _brightness = 0.5f;
        /// <summary>Vanilla "Smooth Lighting". Off = one light value per face (the cell in front) and no AO: on real terrain
        /// about 26% fewer greedy quads (21% fewer in total) because more faces merge. Set before meshing (tabletop.json
        /// "smooth_lighting"); it applies to sections meshed afterwards.</summary>
        public static bool SmoothLighting = true;
        static readonly float[] LmR = new float[256], LmG = new float[256], LmB = new float[256]; // [sky * 16 + block]
        static readonly Vector3Int[] Dir = { new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1) };

        static BlockShading() { BuildLightmap(); }

        /// <summary>
        /// 26.x lightmap.fsh with the overworld at noon: ambient #0a0a0a, sky light colour white x factor 1, block light
        /// factor 1.4 (flicker averages out), block light tint #ffd88c fading to white at the extremes, brightness 0.5.
        /// </summary>
        static void BuildLightmap()
        {
            float amb = 10f / 255f;
            float tr = 1f, tg = 216f / 255f, tb = 140f / 255f;
            for (int sky = 0; sky < 16; sky++)
                for (int block = 0; block < 16; block++)
                {
                    float bl = block / 15f, sl = sky / 15f;
                    float bb = Curve(bl) * 1.4f, sb = Curve(sl) * 1f;
                    float mix = 0.9f * (2f * bl - 1f) * (2f * bl - 1f);
                    float r = amb + sb + Mathf.Lerp(tr, 1f, mix) * bb;
                    float g = amb + sb + Mathf.Lerp(tg, 1f, mix) * bb;
                    float b = amb + sb + Mathf.Lerp(tb, 1f, mix) * bb;
                    r = Mathf.Clamp01(r); g = Mathf.Clamp01(g); b = Mathf.Clamp01(b);
                    float max = Mathf.Max(r, Mathf.Max(g, b));
                    if (max > 0)
                    {
                        float inv = 1f - max, scaled = 1f - inv * inv * inv * inv, k = scaled / max; // notGamma
                        r = Mathf.Lerp(r, r * k, _brightness); g = Mathf.Lerp(g, g * k, _brightness); b = Mathf.Lerp(b, b * k, _brightness);
                    }
                    int i = sky * 16 + block;
                    LmR[i] = r; LmG[i] = g; LmB[i] = b;
                }
        }

        /// <summary>Vanilla light level curve: level / (4 - 3 level) for level in 0..1.</summary>
        public static float Curve(float level) => level / (4f - 3f * level);

        /// <summary>Lightmap colour for (possibly fractional, smooth-lit) sky and block levels 0..15.</summary>
        public static Vector3 Lightmap(float sky, float block)
        {
            sky = Mathf.Clamp(sky, 0, 15); block = Mathf.Clamp(block, 0, 15);
            int s0 = (int)sky, b0 = (int)block, s1 = Mathf.Min(s0 + 1, 15), b1 = Mathf.Min(b0 + 1, 15);
            float fs = sky - s0, fb = block - b0;
            int i00 = s0 * 16 + b0, i01 = s0 * 16 + b1, i10 = s1 * 16 + b0, i11 = s1 * 16 + b1;
            float w00 = (1 - fs) * (1 - fb), w01 = (1 - fs) * fb, w10 = fs * (1 - fb), w11 = fs * fb;
            return new Vector3(LmR[i00] * w00 + LmR[i01] * w01 + LmR[i10] * w10 + LmR[i11] * w11,
                               LmG[i00] * w00 + LmG[i01] * w01 + LmG[i10] * w10 + LmG[i11] * w11,
                               LmB[i00] * w00 + LmB[i01] * w01 + LmB[i10] * w10 + LmB[i11] * w11);
        }

        /// <summary>Full cubes (opaque or not) occlude for AO: vanilla's shade brightness 0.2 for full collision shapes.</summary>
        public static bool AoOccluder(BlockShape s) => s == BlockShape.Cube || s == BlockShape.TransparentCube;
        /// <summary>Light passes unless the cell is an opaque full cube (vanilla isLightPermeable).</summary>
        public static bool Permeable(BlockShape s) => s != BlockShape.Cube;

        /// <summary>The face's tint before lighting: the biome colour for tinted faces when the server sent biomes, else the key colour.</summary>
        public static Color32 Tint(WorldModel w, FaceKey key, int x, int y, int z)
        {
            if (key.Tint == TintKind.None) return key.Color;
            var tints = w.Tints;
            int c = tints == null ? -1 : tints.Tint(key.Tint, x, y, z);
            return c < 0 ? key.Color : new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, key.Color.a);
        }

        /// <summary>
        /// Colours for the 4 corners of the unit face of block (x,y,z) facing dir (0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z).
        /// cubic: the face lies on the block boundary (full cube faces); false for faces inside the block (a slab top),
        /// which vanilla lights from the block's own layer.
        /// </summary>
        public static Quad4 FaceCorners(WorldModel w, FaceKey key, int x, int y, int z, int dir, bool cubic = true)
        {
            Color32 tint = Tint(w, key, x, y, z);
            var light = w.Light;
            if (light == null) return new Quad4(tint);
            var n = Dir[dir];
            var p = new Vector3Int(x, y, z);
            var f = cubic ? p + n : p;
            if (!w.InBounds(f.x, f.y, f.z)) return new Quad4(Shade(tint, Lightmap(15, 0), 1f)); // cut face at the region edge
            int axis = dir >> 1, ua = (axis + 1) % 3, va = (axis + 2) % 3;
            var u = new Vector3Int(); u[ua] = 1;
            var v = new Vector3Int(); v[va] = 1;
            Cell(w, light, f, out int cSky, out int cBlk, out var cShape);
            if (!cubic)
            {
                // Vanilla: a face inside the block takes the light of the cell in front unless that cell is opaque.
                var front = p + n;
                Cell(w, light, front, out int fs, out int fb, out var fShape);
                if (w.InBounds(front.x, front.y, front.z) && Permeable(fShape)) { cSky = fs; cBlk = fb; }
            }
            if (!SmoothLighting) return new Quad4(Shade(tint, Lightmap(cSky, cBlk), 1f));
            float cAo = AoOccluder(cShape) ? 0.2f : 1f;
            var q = new Quad4();
            for (int c = 0; c < 4; c++)
            {
                int su = c == 1 || c == 2 ? 1 : -1, sv = c >= 2 ? 1 : -1;
                var a = f + u * su; var b = f + v * sv;
                Cell(w, light, a, out int aSky, out int aBlk, out var aShape);
                Cell(w, light, b, out int bSky, out int bBlk, out var bShape);
                Cell(w, light, a + n, out _, out _, out var aOut);
                Cell(w, light, b + n, out _, out _, out var bOut);
                int kSky = aSky, kBlk = aBlk; var kShape = aShape;
                if (Permeable(aOut) || Permeable(bOut)) Cell(w, light, a + v * sv, out kSky, out kBlk, out kShape);
                float ao = ((AoOccluder(aShape) ? 0.2f : 1f) + (AoOccluder(bShape) ? 0.2f : 1f) + (AoOccluder(kShape) ? 0.2f : 1f) + cAo) * 0.25f;
                SmoothBlend(aSky, aBlk, bSky, bBlk, kSky, kBlk, cSky, cBlk, out float sky, out float blk);
                var col = Shade(tint, Lightmap(sky, blk), ao);
                if (c == 0) q.C0 = col; else if (c == 1) q.C1 = col; else if (c == 2) q.C2 = col; else q.C3 = col;
            }
            return q;
        }

        /// <summary>
        /// THE helper for any axis-aligned quad of block b facing dir (slab faces, model elements, fluid surfaces): smooth
        /// light and AO of the block's face, bilinearly interpolated at each vertex position (region-local block units),
        /// like vanilla's partial-face weights. p0..p3 in the caller's vertex order.
        /// </summary>
        public static Quad4 Quad(WorldModel w, FaceKey key, Vector3Int b, int dir, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            int axis = dir >> 1, ua = (axis + 1) % 3, va = (axis + 2) % 3;
            float plane = p0[axis] - b[axis];
            bool cubic = (dir & 1) == 1 ? plane >= 0.999f : plane <= 0.001f;
            var c = FaceCorners(w, key, b.x, b.y, b.z, dir, cubic);
            if (w.Light == null) return c;
            return new Quad4 { C0 = Bilerp(c, p0[ua] - b[ua], p0[va] - b[va]), C1 = Bilerp(c, p1[ua] - b[ua], p1[va] - b[va]),
                               C2 = Bilerp(c, p2[ua] - b[ua], p2[va] - b[va]), C3 = Bilerp(c, p3[ua] - b[ua], p3[va] - b[va]) };
        }

        /// <summary>Quads that aren't axis-aligned (cross plants, rotated elements): the block's own light, no AO (vanilla
        /// renders cross models with ambientocclusion=false).</summary>
        public static Color32 Flat(WorldModel w, FaceKey key, Vector3Int b)
        {
            Color32 tint = Tint(w, key, b.x, b.y, b.z);
            var light = w.Light;
            if (light == null) return tint;
            Cell(w, light, b, out int sky, out int blk, out _);
            return Shade(tint, Lightmap(sky, blk), 1f);
        }

        /// <summary>
        /// Baked model quads (BlockModelMesher): the biome tint when q.Tint is set; axis-aligned faces take Quad()
        /// (smooth light + AO of the block's face, bilinear for partial faces like vanilla's partial-face weights),
        /// rotated faces (q.Dir &lt; 0: crosses, 22.5/45 degree elements) the block's own light without AO.
        /// </summary>
        public static Quad4 Model(WorldModel w, int x, int y, int z, in BakedQuad q)
        {
            var key = new FaceKey { Color = q.Color, Tint = q.Tint };
            var b = new Vector3Int(x, y, z);
            if (w.Light == null && (q.Tint == TintKind.None || w.Tints == null)) return new Quad4(q.Color);
            if (q.Dir < 0) return new Quad4(Flat(w, key, b));
            var o = new Vector3(x, y, z);
            return Quad(w, key, b, q.Dir, o + q.P0, o + q.P1, o + q.P2, o + q.P3);
        }

        /// <summary>
        /// Fluid faces (FluidMesher), as vanilla's LiquidBlockRenderer: one light per face, the brighter (per channel)
        /// of the fluid cell and the cell above, no AO. A face whose neighbour is outside the region (the cut sides
        /// and top of the miniature) is lit as open sky, like the cube faces there. tint already holds the
        /// (biome) colour and its alpha.
        /// </summary>
        public static Color32 Fluid(WorldModel w, Color32 tint, int x, int y, int z, int dir)
        {
            var light = w.Light;
            if (light == null) return tint;
            var n = Dir[dir];
            if (!w.InBounds(x + n.x, y + n.y, z + n.z)) return Shade(tint, Lightmap(15, 0), 1f);
            Cell(w, light, new Vector3Int(x, y, z), out int s0, out int b0, out _);
            if (w.InBounds(x, y + 1, z))
            {
                Cell(w, light, new Vector3Int(x, y + 1, z), out int s1, out int b1, out _);
                s0 = Mathf.Max(s0, s1); b0 = Mathf.Max(b0, b1);
            }
            return Shade(tint, Lightmap(s0, b0), 1f);
        }

        /// <summary>26.x LightCoordsUtil.smoothBlend on (sky, block) pairs; d is the centre.</summary>
        public static void SmoothBlend(int aS, int aB, int bS, int bB, int cS, int cB, int dS, int dB, out float sky, out float block)
        {
            if (dS > 2 || dB > 2)
            {
                if (aS == 0 && aB == 0) { aS = dS; aB = dB; } else if (aS == 0) aS = dS;
                if (bS == 0 && bB == 0) { bS = dS; bB = dB; } else if (bS == 0) bS = dS;
                if (cS == 0 && cB == 0) { cS = dS; cB = dB; } else if (cS == 0) cS = dS;
            }
            sky = (aS + bS + cS + dS) * 0.25f;
            block = (aB + bB + cB + dB) * 0.25f;
        }

        static void Cell(WorldModel w, byte[] light, Vector3Int p, out int sky, out int block, out BlockShape shape)
        {
            var d = w.Dims;
            int x = Mathf.Clamp(p.x, 0, d.x - 1), y = Mathf.Clamp(p.y, 0, d.y - 1), z = Mathf.Clamp(p.z, 0, d.z - 1);
            int i = (y * d.z + z) * d.x + x;
            var grid = w.Grid;
            if ((uint)i >= (uint)light.Length || (uint)i >= (uint)grid.Length) { sky = 15; block = 0; shape = BlockShape.Air; return; }
            int v = light[i];
            sky = v >> 4; block = v & 15;
            shape = w.ShapeOf(grid[i]);
        }

        static Color32 Shade(Color32 tint, Vector3 light, float ao) =>
            new Color32(ToByte(tint.r * light.x * ao), ToByte(tint.g * light.y * ao), ToByte(tint.b * light.z * ao), tint.a);

        static byte ToByte(float v) => (byte)Mathf.Clamp((int)(v + 0.5f), 0, 255);

        static Color32 Bilerp(Quad4 c, float fu, float fv)
        {
            fu = Mathf.Clamp01(fu); fv = Mathf.Clamp01(fv);
            float w0 = (1 - fu) * (1 - fv), w1 = fu * (1 - fv), w2 = fu * fv, w3 = (1 - fu) * fv;
            return new Color32(ToByte(c.C0.r * w0 + c.C1.r * w1 + c.C2.r * w2 + c.C3.r * w3), ToByte(c.C0.g * w0 + c.C1.g * w1 + c.C2.g * w2 + c.C3.g * w3),
                               ToByte(c.C0.b * w0 + c.C1.b * w1 + c.C2.b * w2 + c.C3.b * w3), c.C0.a);
        }
    }
}
