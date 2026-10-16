// [mesh] Low-detail surface skirt (PLAN.md roadmap 7; ARCHITECTURE.md "Surface skirt"). Engine-free: built on the meshing
// workers, tested in Tests~ (MeshTests, SmoothTests).
//
// While gliding faster than the headset meshes, the leading strip of the visible window has sections that aren't
// meshed yet: holes. The skirt fills them from data the client already has (the region snapshot): per 16x16 column
// tile, the top block of every column (the first full block or fluid below the window top) as one textured top face,
// greedy-merged, plus a side face wherever the next column is lower, lit like a flat face (the cell above;
// BlockShading.Flat, biome tint, light code for the time of day). A tile costs about 1/30 of meshing its sections. It
// is drawn with the block materials (so the nav clip box, fog and day light apply) a hair inside the real surface
// (Inset).
// [smooth] It never draws where there is air: leaves, glass, plants, slabs and model blocks are see-through for it (it
// shows the ground or trunk under a canopy, never a face at the canopy), a fluid's top sits at the fluid's height, and a
// side face only covers the column's own solid run from its top down (an overhang's side is the overhang, not a wall
// down to the next column). SkirtPlanner keeps a tile only while none of its column's sections in the window is on
// screen, so detail and skirt are never drawn together. Off by default (tabletop.json "surface_skirt").
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class SurfaceSkirt
    {
        /// <summary>tabletop.json "surface_skirt" ([smooth] off by default until it is checked on the headset again).</summary>
        public static bool Enabled = false;
        /// <summary>[smooth] Height of a fluid's top face in its block (still water's surface sits at 8/9).</summary>
        public const float FluidTop = 0.875f;
        /// <summary>tabletop.json "skirt_speed": with the skirt on, a glide may go this much faster than meshing keeps up
        /// (MeshScope.SkirtFactor): the leading strip shows the skirt and detail fills in behind. 1 = as fast as detail.</summary>
        public static float SpeedFactor = 2f;
        /// <summary>Blocks the skirt sits inside the real surface (top faces lower, side faces inward).</summary>
        public const float Inset = 0.03f;
        const int S = WorldModel.Section, Outside = int.MinValue, None = -1;

        [ThreadStatic] static int[] _tops;
        [ThreadStatic] static ushort[] _ids;

        /// <summary>
        /// Appends the skirt of tile (sx, sz) (region-local section column) to m, in region-local block coordinates like
        /// the section meshers (faces relative to the tile corner b0 = (16 sx, 0, 16 sz) for UVs). yTop: the window top
        /// (exclusive), yBottom: the window bottom, both region-local blocks. minTop/maxTop: the lowest and highest top
        /// block found (region-local y), or -1 when the tile has no surface.
        /// </summary>
        public static void Build(WorldModel w, int sx, int sz, int yBottom, int yTop, IFaceSource faces, MeshData m, out int minTop, out int maxTop)
        {
            m.Clear();
            minTop = maxTop = -1;
            var d = w.Dims;
            yTop = Math.Min(yTop, d.y); yBottom = Math.Max(0, yBottom);
            if (yTop <= yBottom) return;
            var tops = _tops ?? (_tops = new int[(S + 2) * (S + 2)]);
            var ids = _ids ?? (_ids = new ushort[(S + 2) * (S + 2)]);
            var runs = _runs ?? (_runs = new int[(S + 2) * (S + 2)]);
            int x0 = sx * S, z0 = sz * S;
            var grid = w.Grid;
            int layer = d.x * d.z;
            // Heights of the tile's columns and a one-column border (for the side faces).
            for (int cz = -1; cz <= S; cz++)
                for (int cx = -1; cx <= S; cx++)
                {
                    int x = x0 + cx, z = z0 + cz, k = (cz + 1) * (S + 2) + cx + 1;
                    if (x < 0 || z < 0 || x >= d.x || z >= d.z) { tops[k] = Outside; continue; }
                    int top = None, run = None; ushort id = 0;
                    for (int y = yTop - 1, i = (y * d.z + z) * d.x + x; y >= yBottom; y--, i -= layer)
                    {
                        if ((uint)i >= (uint)grid.Length) continue;
                        id = grid[i];
                        if (Surface(w.ShapeOf(id))) { top = y; break; }
                    }
                    // [smooth] The column's solid run from its top down (side faces stop where air or a canopy starts).
                    if (top >= 0)
                    {
                        run = top;
                        for (int y = top - 1, i = (y * d.z + z) * d.x + x; y >= yBottom && (uint)i < (uint)grid.Length && Surface(w.ShapeOf(grid[i])); y--, i -= layer) run = y;
                    }
                    tops[k] = top; ids[k] = id; runs[k] = run;
                }
            var b0 = new Vector3Int(x0, 0, z0);
            int nx = Math.Min(S, d.x - x0), nz = Math.Min(S, d.z - z0);
            // Top faces, greedy-merged over equal height, face key and colour.
            var done = new bool[S * S];
            for (int cx = 0; cx < nx; cx++)
                for (int cz = 0; cz < nz;)
                {
                    int k = (cz + 1) * (S + 2) + cx + 1, top = tops[k];
                    if (top < 0 || done[cz * S + cx]) { cz++; continue; }
                    var key = TopKey(w, faces, ids[k]);
                    var col = Light(w, key, x0 + cx, top + 1 < d.y ? top + 1 : top, z0 + cz, out byte code);
                    // Grow along z, then along x while every cell of the next row matches.
                    int len = 1;
                    while (cz + len < nz && Same(w, faces, tops, ids, done, cx, cz + len, top, key, col, code, x0, z0, d)) len++;
                    int wid = 1;
                    for (bool ok = true; cx + wid < nx && ok;)
                    {
                        for (int j = 0; j < len && ok; j++) ok = Same(w, faces, tops, ids, done, cx + wid, cz + j, top, key, col, code, x0, z0, d);
                        if (ok) wid++;
                    }
                    for (int a = 0; a < wid; a++) for (int j = 0; j < len; j++) done[(cz + j) * S + cx + a] = true;
                    // Up: plane y, u along z, v along x (GreedyMesher's axes for dir 3). [smooth] A fluid's top at its height.
                    Face(m, b0, 3, top + Height(w, ids[k]) - Inset, cz, cx, cz + len, cx + wid, key, new Quad4(col, code));
                    minTop = minTop < 0 ? top : Math.Min(minTop, top); maxTop = Math.Max(maxTop, top);
                    cz += len;
                }
            // Steps: a side face wherever the next column's surface is lower (not toward columns outside the region).
            for (int cz = 0; cz < nz; cz++)
                for (int cx = 0; cx < nx; cx++)
                {
                    int k = (cz + 1) * (S + 2) + cx + 1, top = tops[k];
                    if (top < 0) continue;
                    for (int dir = 0; dir < 6; dir++)
                    {
                        if (dir == 2 || dir == 3) continue;
                        int ox = dir == 0 ? -1 : dir == 1 ? 1 : 0, oz = dir == 4 ? -1 : dir == 5 ? 1 : 0;
                        int hn = tops[k + oz * (S + 2) + ox];
                        if (hn == Outside || hn >= top) continue;
                        // [smooth] Down to the neighbour's top or the end of this column's solid run, whichever is higher:
                        // never a wall under an overhang or a canopy.
                        float lo = Math.Max(Math.Max(hn + 1, runs[k]), yBottom), hi = top + Height(w, ids[k]) - Inset;
                        if (hi <= lo) continue;
                        var key = SideKey(w, faces, ids[k], dir);
                        int fx = x0 + cx + ox, fz = z0 + cz + oz;
                        var col = Light(w, key, fx, top, fz, out byte code);
                        var q = new Quad4(col, code);
                        int x = x0 + cx - b0.x, z = z0 + cz - b0.z;
                        // x faces: plane x, u along y, v along z; z faces: plane z, u along x, v along y.
                        if (dir == 0) Face(m, b0, 0, x + Inset, lo, z, hi, z + 1, key, q);
                        else if (dir == 1) Face(m, b0, 1, x + 1 - Inset, lo, z, hi, z + 1, key, q);
                        else if (dir == 4) Face(m, b0, 4, z + Inset, x, lo, x + 1, hi, key, q);
                        else Face(m, b0, 5, z + 1 - Inset, x, lo, x + 1, hi, key, q);
                    }
                }
        }

        /// <summary>The lowest and highest top block (region-local y) of tile (sx, sz) below yTop, -1 when none: the
        /// sections a viewer from above sees there (Tests~ glide harness: holes that show).</summary>
        public static void SurfaceRange(WorldModel w, int sx, int sz, int yBottom, int yTop, out int minTop, out int maxTop)
        {
            minTop = maxTop = -1;
            var d = w.Dims; var grid = w.Grid;
            yTop = Math.Min(yTop, d.y); yBottom = Math.Max(0, yBottom);
            int layer = d.x * d.z;
            for (int z = sz * S; z < Math.Min(d.z, sz * S + S); z++)
                for (int x = sx * S; x < Math.Min(d.x, sx * S + S); x++)
                    for (int y = yTop - 1, i = (y * d.z + z) * d.x + x; y >= yBottom; y--, i -= layer)
                    {
                        if ((uint)i >= (uint)grid.Length || !Surface(w.ShapeOf(grid[i]))) continue;
                        minTop = minTop < 0 ? y : Math.Min(minTop, y); maxTop = Math.Max(maxTop, y);
                        break;
                    }
        }

        /// <summary>A block that makes the skirt's surface: [smooth] a full opaque block or a fluid. Air, plants, leaves and
        /// glass (see-through), slabs and model blocks (not a full block) are skipped, so the skirt shows what is under them
        /// instead of a face where there is air (the phantom blocks under canopies).</summary>
        public static bool Surface(BlockShape s) => s == BlockShape.Cube || s == BlockShape.Fluid;

        /// <summary>[smooth] Where a column's top face sits in its block: 1, or FluidTop for water and lava.</summary>
        static float Height(WorldModel w, ushort id) => w.ShapeOf(id) == BlockShape.Fluid ? FluidTop : 1f;

        [ThreadStatic] static int[] _runs;

        static bool Same(WorldModel w, IFaceSource faces, int[] tops, ushort[] ids, bool[] done, int cx, int cz, int top, FaceKey key, Color32 col, byte code, int x0, int z0, Vector3Int d)
        {
            int k = (cz + 1) * (S + 2) + cx + 1;
            if (tops[k] != top || done[cz * S + cx]) return false;
            var kk = TopKey(w, faces, ids[k]);
            if (!kk.Equals(key)) return false;
            var c = Light(w, kk, x0 + cx, top + 1 < d.y ? top + 1 : top, z0 + cz, out byte cc);
            return cc == code && c.r == col.r && c.g == col.g && c.b == col.b && c.a == col.a;
        }

        static FaceKey TopKey(WorldModel w, IFaceSource faces, ushort id)
        {
            var key = faces.Face(w.State(id), 3);
            key.Overlay = 0; key.Transparent = false;
            return key;
        }

        static FaceKey SideKey(WorldModel w, IFaceSource faces, ushort id, int dir)
        {
            var key = faces.Face(w.State(id), dir);
            key.Overlay = 0; key.Transparent = false;
            return key;
        }

        /// <summary>The face's colour (biome tint x light of the cell, no AO) and light code, like a flat-lit face.</summary>
        static Color32 Light(WorldModel w, FaceKey key, int x, int y, int z, out byte code)
        {
            var c = new Vector3Int(Mathf.Clamp(x, 0, w.Dims.x - 1), Mathf.Clamp(y, 0, w.Dims.y - 1), Mathf.Clamp(z, 0, w.Dims.z - 1));
            return BlockShading.Flat(w, key, c, out code);
        }

        /// <summary>
        /// One quad in the plane `plane` (section-local, along dir's axis) spanning (u0, v0)..(u1, v1) on the axes
        /// GreedyMesher uses for that direction (u = axis + 1, v = axis + 2), with GreedyMesher's corner order, block-unit
        /// UVs (CubeUv.Greedy) and winding (right-handed, counter-clockwise from outside; the renderer flips it).
        /// </summary>
        static void Face(MeshData m, Vector3Int b0, int dir, float plane, float u0, float v0, float u1, float v1, FaceKey key, Quad4 col)
        {
            int axis = dir >> 1, ua = (axis + 1) % 3, va = (axis + 2) % 3;
            Vector3 Corner(float cu, float cv) { var p = new Vector3(); p[axis] = plane; p[ua] = cu; p[va] = cv; return p; }
            int s = m.Verts.Count;
            var c0 = Corner(u0, v0); var c1 = Corner(u1, v0); var c2 = Corner(u1, v1); var c3 = Corner(u0, v1);
            m.Verts.Add(c0 + b0); m.Verts.Add(c1 + b0); m.Verts.Add(c2 + b0); m.Verts.Add(c3 + b0);
            m.Uvs.Add(CubeUv.Greedy(dir, c0, key)); m.Uvs.Add(CubeUv.Greedy(dir, c1, key)); m.Uvs.Add(CubeUv.Greedy(dir, c2, key)); m.Uvs.Add(CubeUv.Greedy(dir, c3, key));
            m.AddColors(col);
            var t = m.Opaque;   // one opaque pass (water and leaves too): a stand-in, no sorting
            if ((dir & 1) == 1) { t.Add(s); t.Add(s + 1); t.Add(s + 2); t.Add(s); t.Add(s + 2); t.Add(s + 3); }
            else { t.Add(s); t.Add(s + 2); t.Add(s + 1); t.Add(s); t.Add(s + 3); t.Add(s + 2); }
        }
    }

    /// <summary>
    /// [mesh] Which skirt tiles exist (engine-free; TabletopRenderer and the Tests~ glide harness drive it). Per frame,
    /// [smooth] one rule both ways, so tiles don't toggle as detail arrives: a section column of the box has a tile while
    /// it has a pending section (dirty, being meshed, or newly meshed and its batch not uploaded) and none of its sections
    /// in the window is on screen (Pending.MarkShown). The first batch upload that puts any of them on screen drops the
    /// tile in that frame, so detail and skirt are never drawn together (no z-fighting, no skirt faces through cutout
    /// leaves). (It used to stay until the column's surface rows were on screen and was planned again from any pending
    /// section, so it flickered while deeper sections were meshed.) Keys are region-local section columns (x, z); Shift
    /// re-keys them when the region moves by whole sections.
    /// </summary>
    public sealed class SkirtPlanner
    {
        public sealed class Tile { public int MinSec = -1, MaxSec = -1; public bool Built; public object Handle; }

        public readonly Dictionary<Vector2Int, Tile> Tiles = new Dictionary<Vector2Int, Tile>();
        /// <summary>Tiles the last Frame or Shift removed (their handles are the caller's to free).</summary>
        public readonly List<Tile> Dropped = new List<Tile>();
        readonly List<Vector2Int> _gone = new List<Vector2Int>();

        /// <summary>
        /// Sections of the visible box that aren't on screen in their current form (dirty, being meshed, or meshed and
        /// their batch not uploaded yet). The caller marks them from its few pending sets each frame (Reset, Mark), which
        /// is much cheaper than asking every section of the box.
        /// </summary>
        public sealed class Pending
        {
            bool[] _p = new bool[0], _shown = new bool[0];
            int _x0, _y0, _z0, _nx, _ny, _nz, _sy0, _sy1;
            public SectionBox Box { get; private set; }

            /// <summary>box: the columns and rows to plan for; shownY0..shownY1 (section rows, end exclusive): the window's
            /// rows, where a section on screen means its column needs no tile (default: the box's rows).</summary>
            public void Reset(SectionBox visible, Vector3Int sections, int shownY0 = int.MinValue, int shownY1 = int.MaxValue)
            {
                _x0 = Math.Max(0, visible.Min.x); _y0 = Math.Max(0, visible.Min.y); _z0 = Math.Max(0, visible.Min.z);
                _nx = Math.Max(0, Math.Min(sections.x, visible.Max.x) - _x0); _ny = Math.Max(0, Math.Min(sections.y, visible.Max.y) - _y0);
                _nz = Math.Max(0, Math.Min(sections.z, visible.Max.z) - _z0);
                int n = _nx * _ny * _nz;
                if (_p.Length < n) _p = new bool[n]; else Array.Clear(_p, 0, n);
                if (_shown.Length < _nx * _nz) _shown = new bool[_nx * _nz]; else Array.Clear(_shown, 0, _nx * _nz);
                _sy0 = shownY0 == int.MinValue ? _y0 : shownY0; _sy1 = shownY1 == int.MaxValue ? _y0 + _ny : shownY1;
                Box = new SectionBox { Min = new Vector3Int(_x0, _y0, _z0), Max = new Vector3Int(_x0 + _nx, _y0 + _ny, _z0 + _nz) };
            }

            public void Mark(Vector3Int s)
            {
                int x = s.x - _x0, y = s.y - _y0, z = s.z - _z0;
                if ((uint)x < (uint)_nx && (uint)y < (uint)_ny && (uint)z < (uint)_nz) _p[(y * _nz + z) * _nx + x] = true;
            }

            /// <summary>[smooth] Section s has geometry on screen (meshed, its batch uploaded; an old mesh being redone counts).</summary>
            public void MarkShown(Vector3Int s)
            {
                int x = s.x - _x0, z = s.z - _z0;
                if ((uint)x < (uint)_nx && (uint)z < (uint)_nz && s.y >= _sy0 && s.y < _sy1) _shown[z * _nx + x] = true;
            }

            /// <summary>[smooth] Any section of column (x, z) in the window is on screen.</summary>
            public bool Shown(int x, int z)
            {
                x -= _x0; z -= _z0;
                return (uint)x < (uint)_nx && (uint)z < (uint)_nz && _shown[z * _nx + x];
            }

            /// <summary>Any section of column (x, z) in the box is pending.</summary>
            public bool AnyPending(int x, int z)
            {
                x -= _x0; z -= _z0;
                if ((uint)x >= (uint)_nx || (uint)z >= (uint)_nz) return false;
                for (int y = 0; y < _ny; y++) if (_p[(y * _nz + z) * _nx + x]) return true;
                return false;
            }

            public bool Get(int x, int y, int z)
            {
                x -= _x0; y -= _y0; z -= _z0;
                return (uint)x < (uint)_nx && (uint)y < (uint)_ny && (uint)z < (uint)_nz && _p[(y * _nz + z) * _nx + x];
            }
        }

        /// <summary>Plans one frame from the pending sections of the visible box (Pending.Reset with that box first).
        /// Fills build with new tiles to build (up to maxBuild); removed tiles go to Dropped.</summary>
        public void Frame(Pending pending, int maxBuild, List<Vector2Int> build)
        {
            build.Clear(); Dropped.Clear();
            var box = pending.Box;
            int x0 = box.Min.x, x1 = box.Max.x, y0 = box.Min.y, y1 = box.Max.y, z0 = box.Min.z, z1 = box.Max.z;
            _gone.Clear();
            foreach (var kv in Tiles)
            {
                var c = kv.Key;
                if (c.x < x0 || c.x >= x1 || c.y < z0 || c.y >= z1) { _gone.Add(c); continue; }
                if (!Wanted(pending, c.x, c.y)) _gone.Add(c);   // [smooth] detail on screen (or nothing left to wait for)
            }
            foreach (var c in _gone) { Dropped.Add(Tiles[c]); Tiles.Remove(c); }
            if (maxBuild <= 0 || x1 <= x0 || y1 <= y0 || z1 <= z0) return;
            for (int x = x0; x < x1 && build.Count < maxBuild; x++)
                for (int z = z0; z < z1 && build.Count < maxBuild; z++)
                {
                    var c = new Vector2Int(x, z);
                    if (Tiles.ContainsKey(c) || !Wanted(pending, x, z)) continue;
                    Tiles[c] = new Tile();
                    build.Add(c);
                }
        }

        /// <summary>[smooth] A column wants a tile while something in it is pending and nothing of it in the window is on screen.</summary>
        static bool Wanted(Pending p, int x, int z) => !p.Shown(x, z) && p.AnyPending(x, z);

        /// <summary>A tile's build finished: its surface section rows (minTopBlock -1: it has no surface).</summary>
        public Tile Built(Vector2Int c, int minTopBlock, int maxTopBlock)
        {
            if (!Tiles.TryGetValue(c, out var t) || t.Built) return null;
            t.Built = true;
            t.MinSec = minTopBlock < 0 ? -1 : FloorDiv(minTopBlock, WorldModel.Section);
            t.MaxSec = maxTopBlock < 0 ? -1 : FloorDiv(maxTopBlock + 1, WorldModel.Section);   // the section above holds what stands on it
            return t;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>The region moved by whole sections: tiles keep their place in the world (new key = old - shift);
        /// tiles that left the region go to Dropped.</summary>
        public void Shift(Vector3Int shiftSections, Vector3Int sections)
        {
            Dropped.Clear();
            if (shiftSections.x == 0 && shiftSections.z == 0 && shiftSections.y == 0) return;
            var old = new List<KeyValuePair<Vector2Int, Tile>>(Tiles);
            Tiles.Clear();
            foreach (var kv in old)
            {
                var n = new Vector2Int(kv.Key.x - shiftSections.x, kv.Key.y - shiftSections.z);
                var t = kv.Value;
                if (n.x < 0 || n.y < 0 || n.x >= sections.x || n.y >= sections.z) { Dropped.Add(t); continue; }
                if (t.MinSec >= 0) { t.MinSec -= shiftSections.y; t.MaxSec -= shiftSections.y; }
                Tiles[n] = t;
            }
        }

        /// <summary>Removes every tile (they go to Dropped).</summary>
        public void Clear() { Dropped.Clear(); Dropped.AddRange(Tiles.Values); Tiles.Clear(); }
    }
}
