// Per-vertex colour = biome tint x light x ambient occlusion, the way vanilla's smooth lighting does it (26.x
// BlockModelLighter + lightmap.fsh). Engine-free: runs on the meshing workers and in Tests~. The shader still multiplies
// the fixed per-direction face shade (normals) and converts the sRGB vertex colour to linear.
//
//   light:  each vertex averages the packed (sky, block) light of the 4 cells around it in the layer the face points
//           into (vanilla smoothBlend: a dark neighbour inside an opaque block takes the centre's value), then looks it
//           up in the lightmap at noon (bilinear between levels, like the GPU sampling vanilla's 16x16 lightmap).
//           Defaults for AR: vanilla's "Bright" setting, and the lightmap rescaled so light level 0 shows at MinLight
//           (0.2) instead of near black, because dark faces read as holes over passthrough. Full light stays 1.
//   AO:     (side1 + side2 + corner + centre) / 4 with 0.2 for full-cube cells and 1.0 otherwise; the corner cell is
//           skipped (side1 reused) when neither side lets light through, as vanilla does.
//   no light data (old server, or no light_v1): white light and no AO, i.e. exactly the old look.
//   region edges: the miniature's sides are cutaways, so everything outside the region counts as open, sky-lit air
//           (sky 15, no AO occluder): a face whose front cell is outside is lit as daylight, and light/AO samples
//           outside the region add daylight and no occlusion, so caves cut by the edge brighten towards the cut.
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
        /// <summary>Vanilla "Brightness" option: 0 = Moody, 0.5 = the game's default, 1 = Bright (our default: AR).</summary>
        public static float Brightness { get => _brightness; set { _brightness = Mathf.Clamp01(value); BuildLightmap(); } }
        static float _brightness = 1f;

        /// <summary>
        /// Brightness of light level 0 (0..1). The lightmap is rescaled linearly so its level-0 value lands here and full
        /// light stays 1, keeping every step between them. Negative = vanilla's own curve (level 0 = 0.148 at Bright,
        /// 0.094 at the game's default). tabletop.json "min_light".
        /// </summary>
        public static float MinLight { get => _minLight; set { _minLight = Mathf.Min(value, 1f); BuildLightmap(); } }
        static float _minLight = 0.2f;
        /// <summary>Vanilla "Smooth Lighting". Off = one light value per face (the cell in front) and no AO: on real terrain
        /// about 26% fewer greedy quads (21% fewer in total) because more faces merge. Set before meshing (tabletop.json
        /// "smooth_lighting"); it applies to sections meshed afterwards.</summary>
        public static bool SmoothLighting = true;
        static readonly float[] LmR = new float[256], LmG = new float[256], LmB = new float[256]; // [sky * 16 + block]
        static readonly Vector3Int[] Dir = { new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1) };

        static BlockShading() { BuildLightmap(); }

        /// <summary>
        /// 26.x lightmap.fsh with the overworld at noon: ambient #0a0a0a, sky light colour white x factor 1, block light
        /// factor 1.4 (flicker averages out), block light tint #ffd88c fading to white at the extremes, then the
        /// Brightness setting's notGamma mix, then the MinLight rescale.
        /// </summary>
        static void BuildLightmap()
        {
            float floor = 0;
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
                    if (sky == 0 && block == 0) floor = g;   // ambient only: grey, the darkest entry
                    if (_minLight >= 0 && floor < 1)
                    {
                        float k = (1f - _minLight) / (1f - floor);
                        r = _minLight + (r - floor) * k; g = _minLight + (g - floor) * k; b = _minLight + (b - floor) * k;
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
        public static Quad4 FaceCorners(WorldModel w, FaceKey key, int x, int y, int z, int dir, bool cubic = true, int floor = 0) =>
            FaceCorners(null, w, key, x, y, z, dir, cubic, floor);

        /// <summary>[mesh] As above, reading light and shapes from a section's ShadeCache when it covers (x, y, z) (the
        /// meshers bind one per section): the same values without WorldModel's bounds checks and palette lookups for the
        /// up to 21 cells a face reads. cache null = straight from the world.</summary>
        public static Quad4 FaceCorners(ShadeCache cache, WorldModel w, FaceKey key, int x, int y, int z, int dir, bool cubic = true, int floor = 0)
        {
            Color32 tint = Tint(w, key, x, y, z);
            var light = w.Light;
            if (light == null) return new Quad4(tint);
            // Emissive rendering (magma, an active sculk sensor): LightCoordsUtil.FULL_BRIGHT, no AO.
            if (key.Shade == BakedModel.ShadeFullBright) return new Quad4(Shade(tint, Lightmap(15, 15), 1f));
            if (cache != null && cache.Covers(w, x, y, z)) { var cc = new CachedCells(cache); return Corners(ref cc, tint, key, x, y, z, dir, cubic, floor); }
            var wc = new WorldCells(w, light);
            return Corners(ref wc, tint, key, x, y, z, dir, cubic, floor);
        }

        // Cell flags for Corners: inside the region (WorldModel.InBounds), an AO occluder (AoOccluder), opaque (!Permeable).
        const int InRegion = 1, Occluder = 2, OpaqueCell = 4;
        static readonly int[] DX = { -1, 1, 0, 0, 0, 0 }, DY = { 0, 0, -1, 1, 0, 0 }, DZ = { 0, 0, 0, 0, -1, 1 };

        internal static int CellFlags(BlockShape s) => InRegion | (AoOccluder(s) ? Occluder : 0) | (Permeable(s) ? 0 : OpaqueCell);

        interface ICells
        {
            /// <summary>Light and flags of a region-local cell; outside the region open sky-lit air (sky 15, flags 0).</summary>
            void Cell(int x, int y, int z, out int sky, out int block, out int flags);
        }

        struct WorldCells : ICells
        {
            readonly WorldModel _w; readonly byte[] _light; readonly ushort[] _grid; readonly Vector3Int _d;
            public WorldCells(WorldModel w, byte[] light) { _w = w; _light = light; _grid = w.Grid; _d = w.Dims; }
            public void Cell(int x, int y, int z, out int sky, out int block, out int flags)
            {
                if (x < 0 || y < 0 || z < 0 || x >= _d.x || y >= _d.y || z >= _d.z) { sky = 15; block = 0; flags = 0; return; }
                int i = (y * _d.z + z) * _d.x + x;
                if ((uint)i >= (uint)_light.Length || (uint)i >= (uint)_grid.Length) { sky = 15; block = 0; flags = InRegion; return; }
                int v = _light[i];
                sky = v >> 4; block = v & 15;
                flags = CellFlags(_w.ShapeOf(_grid[i]));
            }
        }

        struct CachedCells : ICells
        {
            readonly byte[] _light, _flags; readonly int _x0, _y0, _z0;
            public CachedCells(ShadeCache c) { _light = c.LightCells; _flags = c.FlagCells; _x0 = c.X0; _y0 = c.Y0; _z0 = c.Z0; }
            public void Cell(int x, int y, int z, out int sky, out int block, out int flags)
            {
                int i = ((y - _y0) * ShadeCache.N + (z - _z0)) * ShadeCache.N + (x - _x0);
                int v = _light[i];
                sky = v >> 4; block = v & 15; flags = _flags[i];
            }
        }

        /// <summary>FaceCorners after the tint, no-light and full-bright cases, over either cell source (one code path).</summary>
        static Quad4 Corners<T>(ref T cells, Color32 tint, FaceKey key, int x, int y, int z, int dir, bool cubic, int floor) where T : struct, ICells
        {
            int nx = DX[dir], ny = DY[dir], nz = DZ[dir];
            int fx = x, fy = y, fz = z;
            if (cubic) { fx += nx; fy += ny; fz += nz; }
            cells.Cell(fx, fy, fz, out int cSky, out int cBlk, out int cFl);
            if ((cFl & InRegion) == 0) return new Quad4(Shade(tint, Lightmap(15, floor), 1f)); // cut face at the region edge
            int axis = dir >> 1, ua = (axis + 1) % 3, va = (axis + 2) % 3;
            int ux = ua == 0 ? 1 : 0, uy = ua == 1 ? 1 : 0, uz = ua == 2 ? 1 : 0;
            int vx = va == 0 ? 1 : 0, vy = va == 1 ? 1 : 0, vz = va == 2 ? 1 : 0;
            // "ambientocclusion": false (BlockModelLighter.prepareQuadFlat): one light, the cell in front for a face on
            // the block's side, else the block's own; no AO. floor: the face's light_emission (lightCoordsWithEmission).
            if (key.Shade == BakedModel.ShadeFlat) return new Quad4(Shade(tint, Lightmap(Mathf.Max(cSky, floor), Mathf.Max(cBlk, floor)), 1f));
            if (!cubic)
            {
                // Vanilla: a face inside the block takes the light of the cell in front unless that cell is opaque.
                cells.Cell(x + nx, y + ny, z + nz, out int fs, out int fb, out int fFl);
                if ((fFl & InRegion) != 0 && (fFl & OpaqueCell) == 0) { cSky = fs; cBlk = fb; }
            }
            if (!SmoothLighting) return new Quad4(Shade(tint, Lightmap(Mathf.Max(cSky, floor), Mathf.Max(cBlk, floor)), 1f));
            float cAo = (cFl & Occluder) != 0 ? 0.2f : 1f;
            var q = new Quad4();
            for (int c = 0; c < 4; c++)
            {
                int su = c == 1 || c == 2 ? 1 : -1, sv = c >= 2 ? 1 : -1;
                int ax = fx + ux * su, ay = fy + uy * su, az = fz + uz * su;
                int bx = fx + vx * sv, by = fy + vy * sv, bz = fz + vz * sv;
                cells.Cell(ax, ay, az, out int aSky, out int aBlk, out int aFl);
                cells.Cell(bx, by, bz, out int bSky, out int bBlk, out int bFl);
                cells.Cell(ax + nx, ay + ny, az + nz, out _, out _, out int aOut);
                cells.Cell(bx + nx, by + ny, bz + nz, out _, out _, out int bOut);
                int kSky = aSky, kBlk = aBlk, kFl = aFl;
                if ((aOut & OpaqueCell) == 0 || (bOut & OpaqueCell) == 0) cells.Cell(ax + vx * sv, ay + vy * sv, az + vz * sv, out kSky, out kBlk, out kFl);
                float ao = (((aFl & Occluder) != 0 ? 0.2f : 1f) + ((bFl & Occluder) != 0 ? 0.2f : 1f) + ((kFl & Occluder) != 0 ? 0.2f : 1f) + cAo) * 0.25f;
                SmoothBlend(aSky, aBlk, bSky, bBlk, kSky, kBlk, cSky, cBlk, out float sky, out float blk);
                var col = Shade(tint, Lightmap(Mathf.Max(sky, floor), Mathf.Max(blk, floor)), ao);
                if (c == 0) q.C0 = col; else if (c == 1) q.C1 = col; else if (c == 2) q.C2 = col; else q.C3 = col;
            }
            return q;
        }

        /// <summary>
        /// THE helper for any axis-aligned quad of block b facing dir (slab faces, model elements, fluid surfaces): smooth
        /// light and AO of the block's face, bilinearly interpolated at each vertex position (region-local block units),
        /// like vanilla's partial-face weights. p0..p3 in the caller's vertex order.
        /// </summary>
        public static Quad4 Quad(WorldModel w, FaceKey key, Vector3Int b, int dir, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int floor = 0)
        {
            int axis = dir >> 1, ua = (axis + 1) % 3, va = (axis + 2) % 3;
            float plane = p0[axis] - b[axis];
            bool cubic = (dir & 1) == 1 ? plane >= 0.999f : plane <= 0.001f;
            var c = FaceCorners(w, key, b.x, b.y, b.z, dir, cubic, floor);
            if (w.Light == null) return c;
            return new Quad4 { C0 = Bilerp(c, p0[ua] - b[ua], p0[va] - b[va]), C1 = Bilerp(c, p1[ua] - b[ua], p1[va] - b[va]),
                               C2 = Bilerp(c, p2[ua] - b[ua], p2[va] - b[va]), C3 = Bilerp(c, p3[ua] - b[ua], p3[va] - b[va]) };
        }

        /// <summary>Quads that aren't axis-aligned (cross plants, rotated elements): the block's own light, no AO (vanilla
        /// renders cross models with ambientocclusion=false).</summary>
        public static Color32 Flat(WorldModel w, FaceKey key, Vector3Int b, int floor = 0)
        {
            Color32 tint = Tint(w, key, b.x, b.y, b.z);
            var light = w.Light;
            if (light == null) return tint;
            if (key.Shade == BakedModel.ShadeFullBright) return Shade(tint, Lightmap(15, 15), 1f);
            Cell(w, light, b, out int sky, out int blk, out _);
            return Shade(tint, Lightmap(Mathf.Max(sky, floor), Mathf.Max(blk, floor)), 1f);
        }

        /// <summary>
        /// Baked model quads (BlockModelMesher): the biome tint when q.Tint is set; axis-aligned faces take Quad()
        /// (smooth light + AO of the block's face, bilinear for partial faces like vanilla's partial-face weights),
        /// rotated faces (q.Dir &lt; 0: crosses, 22.5/45 degree elements) the block's own light without AO. shade: the
        /// model's BakedModel.Shade (flat for "ambientocclusion": false, full bright for emissive blocks); a face with
        /// "light_emission" has at least that much sky and block light (eyeblossoms, firefly bushes glow).
        /// </summary>
        public static Quad4 Model(WorldModel w, int x, int y, int z, in BakedQuad q, byte shade = BakedModel.ShadeSmooth)
        {
            var key = new FaceKey { Color = q.Color, Tint = q.Tint, Shade = shade };
            var b = new Vector3Int(x, y, z);
            if (w.Light == null && (q.Tint == TintKind.None || w.Tints == null)) return new Quad4(q.Color);
            if (q.Dir < 0) return new Quad4(Flat(w, key, b, q.Emission));
            var o = new Vector3(x, y, z);
            return Quad(w, key, b, q.Dir, o + q.P0, o + q.P1, o + q.P2, o + q.P3, q.Emission);
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
            Cell(w, light, new Vector3Int(x, y + 1, z), out int s1, out int b1, out _);   // above the region: open sky
            return Shade(tint, Lightmap(Mathf.Max(s0, s1), Mathf.Max(b0, b1)), 1f);
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

        /// <summary>Light and shape of a cell; outside the region it is open sky-lit air (the cutaway sides).</summary>
        static void Cell(WorldModel w, byte[] light, Vector3Int p, out int sky, out int block, out BlockShape shape)
        {
            var d = w.Dims;
            int x = p.x, y = p.y, z = p.z;
            if (x < 0 || y < 0 || z < 0 || x >= d.x || y >= d.y || z >= d.z) { sky = 15; block = 0; shape = BlockShape.Air; return; }
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

    /// <summary>
    /// [mesh] Light and shape flags of one 16^3 section and a two-cell border (smooth lighting reads up to two cells past
    /// a face), copied from the world the first time a face of the section is lit and then read by index. A mesher
    /// binds one per section build (Bind) and passes it to BlockShading.FaceCorners/Quad; the colours are exactly those
    /// read from the world (Tests~ MeshTests). Not thread-safe: one per worker thread.
    /// </summary>
    public sealed class ShadeCache
    {
        public const int Border = 2, N = WorldModel.Section + 2 * Border;
        internal readonly byte[] LightCells = new byte[N * N * N];   // sky << 4 | block; outside the region 15 << 4
        internal readonly byte[] FlagCells = new byte[N * N * N];    // BlockShading's InRegion / Occluder / OpaqueCell
        internal int X0, Y0, Z0;                                     // region-local cell of index 0
        WorldModel _w;
        bool _filled;

        /// <summary>Starts a section: nothing is read until a face needs it.</summary>
        public void Bind(WorldModel w, Vector3Int sectionOrigin)
        {
            _w = w; _filled = false;
            X0 = sectionOrigin.x - Border; Y0 = sectionOrigin.y - Border; Z0 = sectionOrigin.z - Border;
        }

        public void Unbind() { _w = null; _filled = false; }

        /// <summary>True (and filled) when a face of cell (x, y, z) of world w can be lit from the cache: the cell is in
        /// the bound section, so every cell its corners read is in the border.</summary>
        internal bool Covers(WorldModel w, int x, int y, int z)
        {
            if (w != _w || w == null) return false;
            const int lo = Border, hi = N - Border;
            if (x - X0 < lo || y - Y0 < lo || z - Z0 < lo || x - X0 >= hi || y - Y0 >= hi || z - Z0 >= hi) return false;
            if (!_filled) Fill();
            return true;
        }

        void Fill()
        {
            _filled = true;
            var w = _w; var d = w.Dims; var light = w.Light; var grid = w.Grid;
            int i = 0;
            for (int py = 0; py < N; py++)
            {
                int y = Y0 + py;
                for (int pz = 0; pz < N; pz++)
                {
                    int z = Z0 + pz;
                    bool rowIn = y >= 0 && y < d.y && z >= 0 && z < d.z;
                    int row = (y * d.z + z) * d.x;
                    for (int px = 0; px < N; px++, i++)
                    {
                        int x = X0 + px;
                        if (!rowIn || x < 0 || x >= d.x) { LightCells[i] = 15 << 4; FlagCells[i] = 0; continue; }
                        int j = row + x;
                        if ((uint)j >= (uint)light.Length || (uint)j >= (uint)grid.Length) { LightCells[i] = 15 << 4; FlagCells[i] = 1; continue; }
                        LightCells[i] = light[j];
                        FlagCells[i] = (byte)BlockShading.CellFlags(w.ShapeOf(grid[j]));
                    }
                }
            }
        }
    }
}
