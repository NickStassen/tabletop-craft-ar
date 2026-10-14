// Block targeting for editing from the headset (XR/BlockEditMode.cs): a voxel ray traversal (Amanatides & Woo DDA)
// over WorldModel that returns the first targetable cell and the face the ray entered it through, like vanilla's
// crosshair hit result. Engine-free (tested in Tests~).
//
// Frames: "block space" is the protocol's right-handed region-local frame (+X east, +Y up, +Z south, 1 unit = 1 block,
// cell (x,y,z) spans [x,x+1] x [y,y+1] x [z,z+1]). TabletopRenderer draws block-space vertex (x,y,z) at Blocks-local
// (x, y, -z) (Unity is left-handed, PROTOCOL.md section 3), so a point or direction in the Blocks transform's local
// space maps to block space by negating z, and back the same way (FromBlocksLocal is its own inverse).
//
// Air (incl. cave air, barriers, light) and fluids are skipped, so you aim through water at the seabed and the cell in
// front of the hit (Adjacent) is where a placed block goes, unless the hit block is replaceable (short grass, a snow
// layer: BlockEditor.Plan places into its cell then). Non-cube blocks (models, plants) are targeted by their cell.
// With navigation (NavState) only a window of the region is shown: Cast takes that box (region-local blocks, may be
// fractional) and never targets past it, so hidden margin blocks can't be hit or built on.
using System;
using UnityEngine;

namespace TabletopCraft.World
{
    public struct BlockHit
    {
        public bool Hit;
        public Vector3Int Block;     // region-local cell that was hit
        public int Face;             // 0=-X west 1=+X east 2=-Y down 3=+Y up 4=-Z north 5=+Z south (IFaceSource dirs)
        public Vector3Int Adjacent;  // Block + Normal(Face): the cell a placed block goes into (may be outside the region)
        public bool AdjacentInView;  // Adjacent is in the region and at least partly inside the box that was cast against
        public float Distance;       // along the ray to the entry point, in blocks
        public Vector3 Point;        // entry point on the face, block space
    }

    public static class BlockRaycast
    {
        /// <summary>Minecraft Direction names per face index (block_edit "face").</summary>
        public static readonly string[] FaceNames = { "west", "east", "down", "up", "north", "south" };

        public static Vector3Int Normal(int face)
        {
            var n = new Vector3Int();
            n[face / 2] = (face & 1) == 1 ? 1 : -1;
            return n;
        }

        /// <summary>Face index for the face on <paramref name="axis"/> (0=x 1=y 2=z) whose normal has the given sign.</summary>
        public static int FaceOf(int axis, int sign) => axis * 2 + (sign > 0 ? 1 : 0);

        /// <summary>Blocks-local (Unity, Z mirrored) to block space, and back (the mapping is its own inverse).</summary>
        public static Vector3 FromBlocksLocal(Vector3 p) => new Vector3(p.x, p.y, -p.z);
        public static Vector3 ToBlocksLocal(Vector3 p) => new Vector3(p.x, p.y, -p.z);

        /// <summary>What the ray stops at: everything except air-like blocks and fluids.</summary>
        public static bool Targetable(BlockShape s) => s != BlockShape.Air && s != BlockShape.Fluid;

        /// <summary>A placed block may go into air, a fluid or a replaceable block (short grass, a snow layer, vines...:
        /// BlockPlacement.Replaceable, vanilla's canBeReplaced); <paramref name="placing"/> is the block being placed.</summary>
        public static bool CanPlaceInto(WorldModel w, Vector3Int c, string placing = null)
        {
            if (!w.InBounds(c.x, c.y, c.z)) return false;
            ushort id = w.Grid[w.Index(c.x, c.y, c.z)];
            return !Targetable(w.ShapeOf(id)) || BlockPlacement.Replaceable(w.State(id), placing);
        }

        /// <summary>
        /// First targetable cell along the ray from <paramref name="origin"/> in direction <paramref name="dir"/> (block
        /// space, any length), up to <paramref name="maxDist"/> blocks. A ray that starts inside a targetable cell hits it,
        /// with the face that points back along the ray's main axis.
        /// </summary>
        public static BlockHit Cast(WorldModel w, Vector3 origin, Vector3 dir, float maxDist) =>
            w == null ? new BlockHit() : Cast(w, origin, dir, maxDist, new Vector3(0, 0, 0), w.Dims);

        /// <summary>Like Cast, limited to the box [boxMin, boxMax] (region-local blocks, e.g. the visible window).</summary>
        public static BlockHit Cast(WorldModel w, Vector3 origin, Vector3 dir, float maxDist, Vector3 boxMin, Vector3 boxMax)
        {
            var none = new BlockHit();
            if (w == null || !w.Loaded || maxDist <= 0) return none;
            var lo = new Vector3(Math.Max(0f, boxMin.x), Math.Max(0f, boxMin.y), Math.Max(0f, boxMin.z));
            var hi = new Vector3(Math.Min(w.Dims.x, boxMax.x), Math.Min(w.Dims.y, boxMax.y), Math.Min(w.Dims.z, boxMax.z));
            if (hi.x <= lo.x || hi.y <= lo.y || hi.z <= lo.z) return none;
            float len = MathF.Sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
            if (len < 1e-9f) return none;
            var d = dir * (1f / len);

            // Clip the ray to the box (slab method); remember the axis it enters through.
            float t0 = 0, t1 = maxDist;
            int enterAxis = -1;
            for (int a = 0; a < 3; a++)
            {
                if (MathF.Abs(d[a]) < 1e-12f)
                {
                    if (origin[a] < lo[a] || origin[a] >= hi[a]) return none;   // parallel to this slab and outside it
                    continue;
                }
                float ta = (lo[a] - origin[a]) / d[a], tb = (hi[a] - origin[a]) / d[a];
                if (ta > tb) { float s = ta; ta = tb; tb = s; }
                if (ta > t0) { t0 = ta; enterAxis = a; }
                if (tb < t1) t1 = tb;
                if (t0 > t1) return none;
            }

            var p = origin + d * t0;
            var cell = new Vector3Int();
            for (int a = 0; a < 3; a++)
                cell[a] = Math.Min(Math.Max((int)MathF.Floor(p[a]), (int)MathF.Floor(lo[a])), (int)MathF.Ceiling(hi[a]) - 1);
            int face;
            if (enterAxis >= 0)
            {
                // Entered through the box face: the first cell on that axis is fixed by the direction (no rounding).
                cell[enterAxis] = d[enterAxis] > 0 ? (int)MathF.Floor(lo[enterAxis]) : (int)MathF.Ceiling(hi[enterAxis]) - 1;
                face = FaceOf(enterAxis, d[enterAxis] > 0 ? -1 : 1);
            }
            else
            {
                int main = 0;
                for (int a = 1; a < 3; a++) if (MathF.Abs(d[a]) > MathF.Abs(d[main])) main = a;
                face = FaceOf(main, d[main] > 0 ? -1 : 1);
            }

            int sx = Sign(d.x), sy = Sign(d.y), sz = Sign(d.z);
            float mx = Next(origin.x, d.x, cell.x), my = Next(origin.y, d.y, cell.y), mz = Next(origin.z, d.z, cell.z);
            float dxT = sx != 0 ? MathF.Abs(1f / d.x) : float.PositiveInfinity;
            float dyT = sy != 0 ? MathF.Abs(1f / d.y) : float.PositiveInfinity;
            float dzT = sz != 0 ? MathF.Abs(1f / d.z) : float.PositiveInfinity;
            float t = t0;
            int guard = w.Dims.x + w.Dims.y + w.Dims.z + 3;
            while (guard-- > 0)
            {
                if (Targetable(w.ShapeOf(w.Grid[w.Index(cell.x, cell.y, cell.z)])))
                {
                    var adj = cell + Normal(face);
                    return new BlockHit { Hit = true, Block = cell, Face = face, Adjacent = adj, Distance = t, Point = origin + d * t,
                                          AdjacentInView = w.InBounds(adj.x, adj.y, adj.z) && Overlaps(adj, lo, hi) };
                }
                int axis = mx <= my ? (mx <= mz ? 0 : 2) : (my <= mz ? 1 : 2);
                float tn = axis == 0 ? mx : axis == 1 ? my : mz;
                if (tn > t1) return none;   // leaves the region or goes past maxDist
                t = tn;
                if (axis == 0) { cell.x += sx; mx += dxT; face = FaceOf(0, -sx); }
                else if (axis == 1) { cell.y += sy; my += dyT; face = FaceOf(1, -sy); }
                else { cell.z += sz; mz += dzT; face = FaceOf(2, -sz); }
                if (!w.InBounds(cell.x, cell.y, cell.z) || !Overlaps(cell, lo, hi)) return none;   // rounding at the box exit
            }
            return none;
        }

        static int Sign(float v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        /// <summary>Cell c overlaps the open box (lo, hi) on every axis.</summary>
        public static bool Overlaps(Vector3Int c, Vector3 lo, Vector3 hi) =>
            c.x + 1 > lo.x + 1e-4f && c.x < hi.x - 1e-4f && c.y + 1 > lo.y + 1e-4f && c.y < hi.y - 1e-4f && c.z + 1 > lo.z + 1e-4f && c.z < hi.z - 1e-4f;

        /// <summary>Ray parameter where the ray crosses the next cell boundary on one axis (infinity if parallel).</summary>
        static float Next(float o, float d, int cell) =>
            d > 0 ? (cell + 1 - o) / d : d < 0 ? (cell - o) / d : float.PositiveInfinity;
    }
}
