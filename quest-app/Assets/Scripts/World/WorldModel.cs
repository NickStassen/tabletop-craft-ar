// Client copy of the subscribed region (chunk_snapshot + block_update), with per-16^3-section dirty flags.
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class WorldModel
    {
        public const int Section = 16;
        public int SnapshotId = -1;
        public string World;
        public Vector3Int Origin, Dims;
        public readonly List<string> Palette = new List<string>();
        public ushort[] Grid = new ushort[0];
        /// <summary>Packed light per cell (sky &lt;&lt; 4 | block, yzx) when the server sent light_v1 data, else null (old look).</summary>
        public byte[] Light;
        /// <summary>4x4x4 biomes (biomes_v1) and the blended tints derived from them, else null (fixed tints).</summary>
        public BiomeGrid Biomes;
        public BiomeTints Tints;
        BlockShape[] _shapes = new BlockShape[0];   // per palette index; swapped whole, read by workers
        volatile int _shapesVersion = -1;           // BlockRules.Version the cache was built for
        public readonly HashSet<Vector3Int> DirtySections = new HashSet<Vector3Int>();
        /// <summary>[blocks] A cell's block or light changed (block_update, local edits): region-local x, y, z. Main thread.</summary>
        public System.Action<int, int, int> CellChanged;
        public bool Loaded => SnapshotId >= 0;
        public Vector3Int SectionCount => new Vector3Int((Dims.x + Section - 1) / Section, (Dims.y + Section - 1) / Section, (Dims.z + Section - 1) / Section);

        public int Index(int x, int y, int z) => (y * Dims.z + z) * Dims.x + x;
        public bool InBounds(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < Dims.x && y < Dims.y && z < Dims.z;
        public ushort Get(int x, int y, int z) => InBounds(x, y, z) ? Grid[Index(x, y, z)] : (ushort)0xFFFF;
        public string State(ushort id) => id < Palette.Count ? Palette[id] : "minecraft:air";

        /// <summary>BlockRules shape of a palette index without a string lookup (hot path of the lighting code).</summary>
        public BlockShape ShapeOf(ushort id)
        {
            // BlockModels.Prepare reclassifies states from their pack models (BlockRules.SetShape) after the snapshot
            // arrived; the version tells us to re-read. Rebuilding on a worker is fine: the array is swapped whole.
            if (_shapesVersion != BlockRules.Version) RebuildShapes();
            var s = _shapes;
            return id < s.Length ? s[id] : BlockRules.Shape(State(id));
        }

        void RebuildShapes()
        {
            int v = BlockRules.Version, n = Palette.Count;
            var s = new BlockShape[n];
            for (int i = 0; i < n; i++) s[i] = BlockRules.Shape(Palette[i]);
            _shapes = s;
            _shapesVersion = v;
        }

        public void ApplySnapshot(ChunkSnapshot s)
        {
            SnapshotId = s.SnapshotId; World = s.World; Origin = s.Origin; Dims = s.Dims;
            Palette.Clear(); Palette.AddRange(s.Palette);
            Grid = s.Grid;
            Light = s.Light;
            Biomes = s.Biomes;
            Tints = s.Biomes != null ? new BiomeTints(s.Biomes, s.Origin, s.Dims) : null;
            RebuildShapes();
            DirtySections.Clear();
            var n = SectionCount;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++) DirtySections.Add(new Vector3Int(x, y, z));
        }

        /// <summary>Returns false if the update is for an older snapshot (dropped per spec).</summary>
        public bool ApplyBlockUpdate(Dictionary<string, object> m)
        {
            if (m.I("snapshot_id", -2) != SnapshotId) return false;
            var add = m.A("palette_add");
            if (add != null) { foreach (var s in add) Palette.Add((string)s); RebuildShapes(); }
            foreach (var c in m.A("changes") ?? new List<object>())
            {
                var a = (List<object>)c;
                int x = (int)(double)a[0], y = (int)(double)a[1], z = (int)(double)a[2], p = (int)(double)a[3];
                if (!InBounds(x, y, z) || p >= Palette.Count) continue;
                Grid[Index(x, y, z)] = (ushort)p;
                MarkDirty(x, y, z); CellChanged?.Invoke(x, y, z);
            }
            // Optional light changes (PROTOCOL.md 4.4.1): [x, y, z, sky << 4 | block], also sent without block changes.
            var light = Light;
            if (light != null && m.A("light") is List<object> lc)
                foreach (var c in lc)
                {
                    var a = (List<object>)c;
                    int x = (int)(double)a[0], y = (int)(double)a[1], z = (int)(double)a[2], v = (int)(double)a[3];
                    if (!InBounds(x, y, z) || (uint)Index(x, y, z) >= (uint)light.Length) continue;
                    light[Index(x, y, z)] = (byte)v;
                    MarkDirty(x, y, z); CellChanged?.Invoke(x, y, z);
                }
            return true;
        }

        /// <summary>A local (optimistic) change of one cell, from block editing (BlockEdits.cs); re-meshes like a block_update.</summary>
        public void SetLocal(int x, int y, int z, ushort id)
        {
            if (!InBounds(x, y, z) || id >= Palette.Count) return;
            Grid[Index(x, y, z)] = id;
            MarkDirty(x, y, z); CellChanged?.Invoke(x, y, z);
        }

        void MarkDirty(int x, int y, int z)
        {
            // The block's section, plus every neighbouring section it touches when it sits on a border: their faces
            // may now be visible, and smooth light / AO read the 3x3x3 cells around a face (so edges and corners too).
            var s = new Vector3Int(x / Section, y / Section, z / Section);
            var n = SectionCount;
            int x0 = x % Section == 0 && s.x > 0 ? s.x - 1 : s.x, x1 = x % Section == Section - 1 && s.x + 1 < n.x ? s.x + 1 : s.x;
            int y0 = y % Section == 0 && s.y > 0 ? s.y - 1 : s.y, y1 = y % Section == Section - 1 && s.y + 1 < n.y ? s.y + 1 : s.y;
            int z0 = z % Section == 0 && s.z > 0 ? s.z - 1 : s.z, z1 = z % Section == Section - 1 && s.z + 1 < n.z ? s.z + 1 : s.z;
            for (int sx = x0; sx <= x1; sx++)
                for (int sy = y0; sy <= y1; sy++)
                    for (int sz = z0; sz <= z1; sz++) DirtySections.Add(new Vector3Int(sx, sy, sz));
        }
    }
}
