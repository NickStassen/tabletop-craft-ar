// Engine-free plan for applying a new snapshot without emptying the table (TabletopRenderer.OnSnapshot).
// When the world and dims are the same and the origin moved by whole 16-block sections (navigation), every section
// that is still inside the region keeps its mesh under a new key (old key k -> new key k - ShiftSections). Only
// sections that are new, changed, or were still waiting to be meshed are meshed again; kept sections that touch
// re-meshed ones are refreshed in the background (their border faces, and any neighbour-dependent shading, may
// differ). Otherwise the renderer keeps the old meshes on screen until the new region is fully meshed.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class SectionPlan
    {
        public bool Rekey;                  // keep meshes and re-key them
        public Vector3Int ShiftBlocks;      // new origin - old origin
        public Vector3Int ShiftSections;    // ShiftBlocks / 16
        public readonly HashSet<Vector3Int> Mesh = new HashSet<Vector3Int>();      // new keys to mesh now
        public readonly HashSet<Vector3Int> Refresh = new HashSet<Vector3Int>();   // kept but next to re-meshed sections
        public int Kept;                    // sections whose mesh carries over unchanged
        public long Compared;               // blocks compared (diagnostics)

        public static Vector3Int Sections(Vector3Int dims) =>
            new Vector3Int((dims.x + WorldModel.Section - 1) / WorldModel.Section, (dims.y + WorldModel.Section - 1) / WorldModel.Section, (dims.z + WorldModel.Section - 1) / WorldModel.Section);

        public static bool CanRekey(string oldWorld, Vector3Int oldOrigin, Vector3Int oldDims, string newWorld, Vector3Int newOrigin, Vector3Int newDims)
        {
            if (oldWorld != newWorld || oldDims.x != newDims.x || oldDims.y != newDims.y || oldDims.z != newDims.z) return false;
            int S = WorldModel.Section;
            for (int a = 0; a < 3; a++)
            {
                int d = newOrigin[a] - oldOrigin[a];
                if (d % S != 0 || Math.Abs(d) >= newDims[a]) return false;   // not section-aligned, or no overlap on this axis
            }
            return true;
        }

        /// <summary>
        /// old: the model as it is now (not yet replaced). stale: old keys whose mesh is out of date (dirty or being
        /// meshed). listed: for a delta snapshot, the new keys the server sent (everything else is unchanged); null to
        /// compare the overlapping sections block by block (any full snapshot, including from a v1 server).
        /// </summary>
        public static SectionPlan Compute(WorldModel old, ChunkSnapshot neu, ICollection<Vector3Int> stale, ICollection<Vector3Int> listed)
        {
            var p = new SectionPlan();
            var n = Sections(neu.Dims);
            if (!old.Loaded || old.Grid == null || !CanRekey(old.World, old.Origin, old.Dims, neu.World, neu.Origin, neu.Dims))
            {
                for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++) p.Mesh.Add(new Vector3Int(x, y, z));
                return p;
            }
            p.Rekey = true;
            int S = WorldModel.Section;
            p.ShiftBlocks = new Vector3Int(neu.Origin.x - old.Origin.x, neu.Origin.y - old.Origin.y, neu.Origin.z - old.Origin.z);
            p.ShiftSections = new Vector3Int(p.ShiftBlocks.x / S, p.ShiftBlocks.y / S, p.ShiftBlocks.z / S);
            int[] remap = listed == null ? Remap(old.Palette, neu.Palette) : null;
            for (int x = 0; x < n.x; x++)
                for (int y = 0; y < n.y; y++)
                    for (int z = 0; z < n.z; z++)
                    {
                        var k = new Vector3Int(x, y, z);
                        var ko = new Vector3Int(x + p.ShiftSections.x, y + p.ShiftSections.y, z + p.ShiftSections.z);
                        bool inOld = ko.x >= 0 && ko.y >= 0 && ko.z >= 0 && ko.x < n.x && ko.y < n.y && ko.z < n.z;
                        bool changed = !inOld || (stale != null && stale.Contains(ko));
                        if (!changed) changed = listed != null ? listed.Contains(k) : !Same(old, ko, neu, k, remap, ref p.Compared);
                        if (changed) p.Mesh.Add(k); else p.Kept++;
                    }
            // Kept sections next to re-meshed ones (26-neighbourhood): faces on the shared border, and anything that
            // samples neighbours (AO, fluids, connected shapes), may differ. Refresh them after the urgent work.
            foreach (var k in p.Mesh)
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                {
                    var b = new Vector3Int(k.x + dx, k.y + dy, k.z + dz);
                    if (b.x < 0 || b.y < 0 || b.z < 0 || b.x >= n.x || b.y >= n.y || b.z >= n.z) continue;
                    if (!p.Mesh.Contains(b)) p.Refresh.Add(b);
                }
            return p;
        }

        /// <summary>Old palette index -> new palette index (-1 if the state is not in the new palette).</summary>
        static int[] Remap(List<string> oldPal, string[] newPal)
        {
            var idx = new Dictionary<string, int>(newPal.Length);
            for (int i = 0; i < newPal.Length; i++) if (!idx.ContainsKey(newPal[i])) idx[newPal[i]] = i;
            var r = new int[oldPal.Count];
            for (int i = 0; i < r.Length; i++) r[i] = idx.TryGetValue(oldPal[i], out var j) ? j : -1;
            return r;
        }

        /// <summary>Same blocks in old section ko and new section k (same dims, so same section sizes)?</summary>
        static bool Same(WorldModel old, Vector3Int ko, ChunkSnapshot neu, Vector3Int k, int[] remap, ref long compared)
        {
            int S = WorldModel.Section;
            var d = neu.Dims;
            int ox = ko.x * S, oy = ko.y * S, oz = ko.z * S, nx = k.x * S, ny = k.y * S, nz = k.z * S;
            int sx = Math.Min(S, d.x - nx), sy = Math.Min(S, d.y - ny), sz = Math.Min(S, d.z - nz);
            if (sx != Math.Min(S, d.x - ox) || sy != Math.Min(S, d.y - oy) || sz != Math.Min(S, d.z - oz)) return false;
            var og = old.Grid; var ng = neu.Grid;
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                {
                    int oi = ((oy + y) * d.z + oz + z) * d.x + ox, ni = ((ny + y) * d.z + nz + z) * d.x + nx;
                    for (int x = 0; x < sx; x++)
                    {
                        int ov = og[oi + x];
                        if (ov >= remap.Length || remap[ov] != ng[ni + x]) { compared += x + 1; return false; }
                    }
                    compared += sx;
                }
            return true;
        }
    }
}
