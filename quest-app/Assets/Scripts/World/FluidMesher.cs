// Fluids (water, lava) and waterlogging for the section mesher. A cell holds water when its state is water, has
// waterlogged=true, or is a block that always sits in water (kelp, kelp_plant, seagrass, tall_seagrass,
// bubble_column: the LiquidBlockContainer/BubbleColumn blocks of the 26.3 client). The fluid volume is drawn in
// addition to the block itself; faces between two cells holding the same fluid are culled, so there are no internal
// faces and no air gaps around plants or waterlogged blocks. Surface heights follow the vanilla
// LiquidBlockRenderer: a source is 8/9 high, flowing water amount/9 (level 1..7 -> 7/9..1/9), each top corner is the
// weighted average of the cell and its neighbours, and a cell under more of the same fluid is full height.
// Flat faces (ocean tops, full-height walls at the region edge, bottoms) are greedy-merged like GreedyMesher.Build;
// sloped ones are emitted per cell. Water goes to the transparent submesh, lava to the opaque one.
// Engine-free and run on meshing worker threads: it only reads the grid and IFaceSource. Fluids take their faces
// from the canonical "minecraft:water"/"minecraft:lava" states, which TabletopRenderer.Prewarm resolves on the main
// thread (see FluidRules.FaceState).
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum FluidKind : byte { None = 0, Water = 1, Lava = 2 }

    public static class FluidRules
    {
        public const string WaterState = "minecraft:water", LavaState = "minecraft:lava";

        // Packed info: kind in bits 0-1, amount (vanilla FluidState.getAmount, 1..8) in bits 2-5; 0 = no fluid.
        // Concurrent: filled from the meshing worker threads.
        static readonly ConcurrentDictionary<string, byte> Cache = new ConcurrentDictionary<string, byte>();

        public static byte Info(string state)
        {
            if (Cache.TryGetValue(state, out var b)) return b;
            b = Compute(state);
            Cache[state] = b;
            return b;
        }

        public static FluidKind Kind(byte info) => (FluidKind)(info & 3);
        public static FluidKind Kind(string state) => Kind(Info(state));
        public static int Amount(byte info) => info >> 2;
        /// <summary>Own surface height in blocks (vanilla getOwnHeight = amount / 9; a source is 8/9).</summary>
        public static float Height(byte info) => (info >> 2) / 9f;

        /// <summary>The state whose faces (texture, tint) this state's fluid uses, or null when it holds no fluid.</summary>
        public static string FaceState(string state)
        {
            var k = Kind(state);
            return k == FluidKind.Water ? WaterState : k == FluidKind.Lava ? LavaState : null;
        }

        static byte Compute(string state)
        {
            string p = BlockRules.Path(state);
            if (p == "water" || p == "lava")
            {
                int level = IntProp(state, "level");   // 0 source, 1..7 flowing (amount 8 - level), 8..15 falling (amount 8)
                int amount = level <= 0 || level >= 8 ? 8 : 8 - level;
                return Pack(p == "water" ? FluidKind.Water : FluidKind.Lava, amount);
            }
            if (p == "kelp" || p == "kelp_plant" || p == "seagrass" || p == "tall_seagrass" || p == "bubble_column" ||
                state.Contains("waterlogged=true"))
                return Pack(FluidKind.Water, 8);
            return 0;
        }

        static byte Pack(FluidKind k, int amount) => (byte)((int)k | amount << 2);

        /// <summary>Integer block-state property ("level=3" in "minecraft:water[level=3]"), 0 when absent.</summary>
        public static int IntProp(string state, string key)
        {
            int open = state.IndexOf('[');
            if (open < 0) return 0;
            string k = key + "=";
            int j = state.IndexOf(k, open, StringComparison.Ordinal);
            while (j > 0 && state[j - 1] != '[' && state[j - 1] != ',') j = state.IndexOf(k, j + 1, StringComparison.Ordinal);
            if (j < 0) return 0;
            int v = 0;
            for (j += k.Length; j < state.Length && state[j] >= '0' && state[j] <= '9'; j++) v = v * 10 + (state[j] - '0');
            return v;
        }

        /// <summary>Faces of a block that are full and opaque (bit d for direction d, IFaceSource order): they hide
        /// a fluid face on that side, in the same cell (vanilla isFaceOccludedBySelf) or the neighbour's.
        /// HOOK(shapes): refine from real block models; today opaque cubes, slabs and straight stairs.</summary>
        public static byte FullFaces(string state, BlockShape shape)
        {
            if (shape == BlockShape.Cube) return 0x3F;
            string p = BlockRules.Path(state);
            bool top = state.Contains("type=top") || state.Contains("half=top");
            if (shape == BlockShape.Slab || p.EndsWith("_slab")) return state.Contains("type=double") ? (byte)0x3F : (byte)(top ? 1 << 3 : 1 << 2);
            if (p.EndsWith("_stairs"))
            {
                int b = top ? 1 << 3 : 1 << 2;
                if (state.Contains("shape=straight"))
                {
                    if (state.Contains("facing=west")) b |= 1 << 0;
                    else if (state.Contains("facing=east")) b |= 1 << 1;
                    else if (state.Contains("facing=north")) b |= 1 << 4;
                    else if (state.Contains("facing=south")) b |= 1 << 5;
                }
                return (byte)b;
            }
            return 0;
        }
    }

    public static class FluidMesher
    {
        const int S = WorldModel.Section, P = S + 2;
        const byte InBounds = 1, Solid = 2;

        static readonly Vector3Int[] Dirs = { new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, -1, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1) };

        // One scratch per worker thread, fetched once per section (thread-static access is slow under IL2CPP).
        [ThreadStatic] static Pass _pass;

        /// <summary>Tint of the water at region-local (x, y, z): the blended biome water colour when the server sent
        /// biomes (BlockShading/BiomeColors), else the atlas default (ResourcePackAtlas.WaterTint, or the flat colour).
        /// The alpha (water opacity) is always the atlas one. Called on worker threads.</summary>
        public static Color32 WaterColor(WorldModel w, int x, int y, int z, Color32 atlasTint) =>
            BlockShading.Tint(w, new FaceKey { Color = atlasTint, Tint = TintKind.Water }, x, y, z);

        /// <summary>Appends the fluid geometry of one section. Call after the block geometry (GreedyMesher.BuildSpecials
        /// does) so water is drawn after the plants inside it in the transparent submesh.</summary>
        public static void Build(WorldModel w, Vector3Int section, IFaceSource faces, MeshData m) => (_pass ?? (_pass = new Pass())).Build(w, section, faces, m);

        static int Pad(int x, int y, int z) => ((y + 1) * P + (z + 1)) * P + (x + 1);   // section-local -1..S
        static int Cell(int x, int y, int z) => ((y * S + z) * S + x) * 4;

        sealed class Pass
        {
            // The (S+2)^3 neighbourhood: fluid info, full-face bits and flags per cell.
            readonly byte[] _fluid = new byte[P * P * P], _full = new byte[P * P * P], _flags = new byte[P * P * P];
            readonly float[] _corners = new float[S * S * S * 4];   // 4 top-corner heights per section cell: NW, SW, SE, NE
            readonly FaceKey[] _mask = new FaceKey[S * S], _keys = new FaceKey[12];
            readonly float[] _maskH = new float[S * S];
            readonly bool[] _has = new bool[S * S];
            int[] _pal = new int[256];   // palette id -> packed (fluid | full << 8 | flags << 16) + 1, 0 = not looked up

            public void Build(WorldModel w, Vector3Int section, IFaceSource faces, MeshData m)
            {
                Vector3Int b0 = section * S;
                var size = new Vector3Int(Mathf.Min(S, w.Dims.x - b0.x), Mathf.Min(S, w.Dims.y - b0.y), Mathf.Min(S, w.Dims.z - b0.z));
                if (size.x <= 0 || size.y <= 0 || size.z <= 0) return;
                int kinds = Fill(w, b0);
                if (kinds == 0) return;

                // Face keys of the fluids present. TabletopRenderer.Prewarm resolves both states on the main thread.
                for (int k = 1; k <= 2; k++)
                {
                    if ((kinds & 1 << k) == 0) continue;
                    string st = k == (int)FluidKind.Water ? FluidRules.WaterState : FluidRules.LavaState;
                    for (int d = 0; d < 6; d++)
                    {
                        var key = faces.Face(st, d);
                        if (k == (int)FluidKind.Water) key.Transparent = true;
                        _keys[(k - 1) * 6 + d] = key;
                    }
                }

                Corners(size);

                for (int dir = 0; dir < 6; dir++)
                {
                    int axis = dir / 2, ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    int selfBit = 1 << dir, nbBit = 1 << (dir ^ 1);
                    Vector3Int n = Dirs[dir];
                    for (int d = 0; d < size[axis]; d++)
                    {
                        for (int v = 0; v < size[va]; v++)
                            for (int u = 0; u < size[ua]; u++)
                            {
                                int k = u + v * S;
                                _has[k] = false;
                                var p = new Vector3Int(); p[axis] = d; p[ua] = u; p[va] = v;
                                int i = Pad(p.x, p.y, p.z);
                                int f = _fluid[i], kind = f & 3;
                                if (kind == 0 || _full[i] == 0x3F || (_full[i] & selfBit) != 0) continue;   // no fluid, or covered by its own block
                                int ni = Pad(p.x + n.x, p.y + n.y, p.z + n.z);
                                if ((_fluid[ni] & 3) == kind) continue;                                       // same fluid next door: no face
                                bool nbFull = (_full[ni] & nbBit) != 0;
                                int c = Cell(p.x, p.y, p.z);
                                float nw = _corners[c], sw = _corners[c + 1], se = _corners[c + 2], ne = _corners[c + 3];
                                var key = Key(w, kind, dir, b0 + p);
                                if (dir == 3)
                                {
                                    // A lowered surface is never hidden by the block above (vanilla checks min height < 1).
                                    if (nbFull && nw >= 1f && sw >= 1f && se >= 1f && ne >= 1f) continue;
                                    if (nw == sw && sw == se && se == ne) { _mask[k] = key; _maskH[k] = nw; _has[k] = true; }
                                    else Top(m, b0, p.x, p.x + 1, p.z, p.z + 1, p.y, nw, sw, se, ne, key);
                                    continue;
                                }
                                if (nbFull) continue;
                                if (dir == 2) { _mask[k] = key; _maskH[k] = 0; _has[k] = true; continue; }
                                float h0, h1;   // top of the side at its t0 / t1 end (see Side)
                                if (dir == 0) { h0 = nw; h1 = sw; } else if (dir == 1) { h0 = ne; h1 = se; } else if (dir == 4) { h0 = nw; h1 = ne; } else { h0 = sw; h1 = se; }
                                if (h0 == h1) { _mask[k] = key; _maskH[k] = h0; _has[k] = true; }
                                else if (axis == 0) Side(m, b0, dir, p.x + (dir & 1), p.z, p.z + 1, p.y, p.y + h0, p.y + h1, key);
                                else Side(m, b0, dir, p.z + (dir & 1), p.x, p.x + 1, p.y, p.y + h0, p.y + h1, key);
                            }
                        Merge(m, b0, dir, axis, d, size[ua], size[va]);
                    }
                }
            }

            FaceKey Key(WorldModel w, int kind, int dir, Vector3Int pos)
            {
                var key = _keys[(kind - 1) * 6 + dir];
                if (kind == (int)FluidKind.Water) key.Color = WaterColor(w, pos.x, pos.y, pos.z, key.Color);
                // Light (vanilla LiquidBlockRenderer: one value per face), folded into the key so the greedy merge of
                // flat fluid faces only joins equally lit cells.
                key.Color = BlockShading.Fluid(w, key.Color, pos.x, pos.y, pos.z, dir, out key.Light);   // [living] and its light code
                return key;
            }

            /// <summary>Greedy merge of the slice mask (equal key and equal top height), as in GreedyMesher.Build.</summary>
            void Merge(MeshData m, Vector3Int b0, int dir, int axis, int d, int nu, int nv)
            {
                for (int v = 0; v < nv; v++)
                    for (int u = 0; u < nu;)
                    {
                        int k = u + v * S;
                        if (!_has[k]) { u++; continue; }
                        var key = _mask[k];
                        float h = _maskH[k];
                        int wdt = 1;
                        while (u + wdt < nu && _has[k + wdt] && _maskH[k + wdt] == h && _mask[k + wdt].Equals(key)) wdt++;
                        int hgt = 1;
                        for (bool ok = true; v + hgt < nv && ok;)
                        {
                            for (int i = 0; i < wdt; i++) { int kk = k + i + hgt * S; if (!_has[kk] || _maskH[kk] != h || !_mask[kk].Equals(key)) { ok = false; break; } }
                            if (ok) hgt++;
                        }
                        // Mask axes: y faces (u=z, v=x), x faces (u=y, v=z), z faces (u=x, v=y). A side's top row ends at
                        // its surface height h (1 for full cells; lowered rows never stack, the cell above isn't fluid).
                        if (dir == 2) Bottom(m, b0, v, v + hgt, u, u + wdt, d, key);
                        else if (dir == 3) Top(m, b0, v, v + hgt, u, u + wdt, d, h, h, h, h, key);
                        else if (axis == 0) Side(m, b0, dir, d + (dir & 1), v, v + hgt, u, u + wdt - 1 + h, u + wdt - 1 + h, key);
                        else Side(m, b0, dir, d + (dir & 1), u, u + wdt, v, v + hgt - 1 + h, v + hgt - 1 + h, key);
                        for (int j = 0; j < hgt; j++) for (int i = 0; i < wdt; i++) _has[k + i + j * S] = false;
                        u += wdt;
                    }
            }

            /// <summary>Loads the section and a one-cell border. Returns a bit mask of the fluid kinds (bit 1 water,
            /// bit 2 lava) visible inside the section; 0 = nothing to do.</summary>
            int Fill(WorldModel w, Vector3Int b0)
            {
                int pc = w.Palette.Count;
                if (_pal.Length < pc) _pal = new int[pc * 2];
                else Array.Clear(_pal, 0, pc);
                int kinds = 0;
                // [mesh] The section itself first: without a visible fluid in it nothing is drawn and the border isn't read.
                for (int y = 0; y < S; y++)
                    for (int z = 0; z < S; z++)
                        for (int x = 0; x < S; x++)
                        {
                            int info = Load(w, b0, x, y, z, pc);
                            if ((info & 3) != 0 && (byte)(info >> 8) != 0x3F) kinds |= 1 << (info & 3);
                        }
                if (kinds == 0) return 0;
                for (int y = -1; y <= S; y++)
                    for (int z = -1; z <= S; z++)
                        for (int x = -1; x <= S; x++)
                            if (x < 0 || y < 0 || z < 0 || x == S || y == S || z == S) Load(w, b0, x, y, z, pc);
                return kinds;
            }

            /// <summary>Loads one cell of the neighbourhood (section-local -1..S) and returns its packed info.</summary>
            int Load(WorldModel w, Vector3Int b0, int x, int y, int z, int pc)
            {
                ushort id = w.Get(b0.x + x, b0.y + y, b0.z + z);
                int info;
                if (id == 0xFFFF) info = 0;                 // outside the region
                else if (id < pc) { info = _pal[id] - 1; if (info < 0) { info = Lookup(w.State(id)); _pal[id] = info + 1; } }
                else info = Lookup(w.State(id));            // palette grew while meshing
                int i = Pad(x, y, z);
                _fluid[i] = (byte)info; _full[i] = (byte)(info >> 8); _flags[i] = (byte)(info >> 16);
                return info;
            }

            /// <summary>Top-corner heights of every fluid cell (vanilla LiquidBlockRenderer.tesselate).</summary>
            void Corners(Vector3Int size)
            {
                for (int y = 0; y < size.y; y++)
                    for (int z = 0; z < size.z; z++)
                        for (int x = 0; x < size.x; x++)
                        {
                            int i = Pad(x, y, z), kind = _fluid[i] & 3;
                            if (kind == 0) continue;
                            int c = Cell(x, y, z);
                            if ((_fluid[Pad(x, y + 1, z)] & 3) == kind) { _corners[c] = _corners[c + 1] = _corners[c + 2] = _corners[c + 3] = 1f; continue; }
                            float cur = FluidRules.Height(_fluid[i]);
                            float hn = H(x, y, z - 1, kind), hs = H(x, y, z + 1, kind), he = H(x + 1, y, z, kind), hw = H(x - 1, y, z, kind);
                            _corners[c] = Q(Avg(cur, hn, hw, x - 1, y, z - 1, kind));       // NW
                            _corners[c + 1] = Q(Avg(cur, hs, hw, x - 1, y, z + 1, kind));   // SW
                            _corners[c + 2] = Q(Avg(cur, hs, he, x + 1, y, z + 1, kind));   // SE
                            _corners[c + 3] = Q(Avg(cur, hn, he, x + 1, y, z - 1, kind));   // NE
                        }
            }

            /// <summary>Vanilla getHeight of a neighbour: its fluid height (1 under more fluid), 0 for open non-solid
            /// space (lowers the corner), -1 for solid blocks and outside the region (ignored).</summary>
            float H(int x, int y, int z, int kind)
            {
                int i = Pad(x, y, z);
                if ((_fluid[i] & 3) == kind) return (_fluid[Pad(x, y + 1, z)] & 3) == kind ? 1f : FluidRules.Height(_fluid[i]);
                int g = _flags[i];
                return (g & InBounds) == 0 || (g & Solid) != 0 ? -1f : 0f;
            }

            /// <summary>Vanilla calculateAverageHeight: sources and near-full neighbours weigh 10, the rest 1.</summary>
            float Avg(float cur, float a, float b, int dx, int dy, int dz, int kind)
            {
                if (a >= 1f || b >= 1f) return 1f;
                float sum = 0, wt = 0;
                if (a > 0f || b > 0f)
                {
                    float diag = H(dx, dy, dz, kind);
                    if (diag >= 1f) return 1f;
                    Add(ref sum, ref wt, diag);
                }
                Add(ref sum, ref wt, cur); Add(ref sum, ref wt, b); Add(ref sum, ref wt, a);
                return sum / wt;
            }
        }

        static int Lookup(string state)
        {
            var shape = BlockRules.Shape(state);
            int flags = InBounds | (shape != BlockShape.Air && shape != BlockShape.Cross && shape != BlockShape.Fluid ? Solid : 0);
            return FluidRules.Info(state) | FluidRules.FullFaces(state, shape) << 8 | flags << 16;
        }

        // Quantised to 1/576 (a multiple of 1/9) so equal heights compare equal and flat faces merge.
        static float Q(float h) => h >= 1f ? 1f : (float)Math.Round(h * 576.0) / 576f;

        static void Add(ref float sum, ref float wt, float h)
        {
            if (h >= 0.8f) { sum += h * 10f; wt += 10f; }
            else if (h >= 0f) { sum += h; wt += 1f; }
        }

        // Geometry, section-local coordinates. Corners are counter-clockwise seen from outside (right-handed frame), as
        // GreedyMesher emits them; TabletopRenderer mirrors Z and flips the winding.
        static void Top(MeshData m, Vector3Int b0, float x0, float x1, float z0, float z1, float y, float nw, float sw, float se, float ne, FaceKey key) =>
            Quad(m, b0, 3, new Vector3(x0, y + nw, z0), new Vector3(x0, y + sw, z1), new Vector3(x1, y + se, z1), new Vector3(x1, y + ne, z0), key);

        static void Bottom(MeshData m, Vector3Int b0, float x0, float x1, float z0, float z1, float y, FaceKey key) =>
            Quad(m, b0, 2, new Vector3(x0, y, z0), new Vector3(x1, y, z0), new Vector3(x1, y, z1), new Vector3(x0, y, z1), key);

        /// <summary>Side face in the plane x = plane (dir 0/1) or z = plane (dir 4/5), spanning t0..t1 along the other
        /// horizontal axis, from y up to top0 (at t0) and top1 (at t1).</summary>
        static void Side(MeshData m, Vector3Int b0, int dir, float plane, float t0, float t1, float y, float top0, float top1, FaceKey key)
        {
            switch (dir)
            {
                case 0: Quad(m, b0, dir, new Vector3(plane, y, t0), new Vector3(plane, y, t1), new Vector3(plane, top1, t1), new Vector3(plane, top0, t0), key); break;
                case 1: Quad(m, b0, dir, new Vector3(plane, y, t1), new Vector3(plane, y, t0), new Vector3(plane, top0, t0), new Vector3(plane, top1, t1), key); break;
                case 4: Quad(m, b0, dir, new Vector3(t1, y, plane), new Vector3(t0, y, plane), new Vector3(t0, top0, plane), new Vector3(t1, top1, plane), key); break;
                default: Quad(m, b0, dir, new Vector3(t0, y, plane), new Vector3(t1, y, plane), new Vector3(t1, top1, plane), new Vector3(t0, top0, plane), key); break;
            }
        }

        static void Quad(MeshData m, Vector3Int b0, int dir, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, FaceKey key)
        {
            int s = m.Verts.Count;
            m.Verts.Add(p0 + b0); m.Verts.Add(p1 + b0); m.Verts.Add(p2 + b0); m.Verts.Add(p3 + b0);
            m.Uvs.Add(Uv(dir, p0, key.Layer)); m.Uvs.Add(Uv(dir, p1, key.Layer)); m.Uvs.Add(Uv(dir, p2, key.Layer)); m.Uvs.Add(Uv(dir, p3, key.Layer));
            m.AddColors(new Quad4(key.Color, key.Light));   // tint x light and the light code, set in Key() (BlockShading.Fluid)
            var t = key.Transparent ? m.Transparent : m.Opaque;
            t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3);
            // The same face seen from inside the water (looking up at the surface, the region's cut walls as an aquarium
            // from inside). Own vertices so the outside face keeps its normal (face shading), reversed winding, and its own
            // index list (a third submesh) that is only drawn while the eye is under water: from outside nothing blends
            // twice. Water only: lava is opaque and nearly blind from inside anyway.
            if (key.Transparent)
            {
                int b = m.Verts.Count;
                for (int i = 0; i < 4; i++) { m.Verts.Add(m.Verts[s + i]); m.Uvs.Add(m.Uvs[s + i]); m.CopyColor(s + i); }
                var wb = m.WaterBack;
                wb.Add(b); wb.Add(b + 2); wb.Add(b + 1); wb.Add(b); wb.Add(b + 3); wb.Add(b + 2);
            }
        }

        /// <summary>UVs in block units from the section-local position, so merged and lowered faces tile and crop the
        /// texture instead of stretching it: u runs to the viewer's right, v up (north on top faces).</summary>
        static Vector3 Uv(int dir, Vector3 p, float layer)
        {
            switch (dir)
            {
                case 0: return new Vector3(p.z, p.y, layer);
                case 1: return new Vector3(-p.z, p.y, layer);
                case 2: return new Vector3(p.x, p.z, layer);
                case 3: return new Vector3(p.x, -p.z, layer);
                case 4: return new Vector3(-p.x, p.y, layer);
                default: return new Vector3(p.x, p.y, layer);
            }
        }
    }
}
