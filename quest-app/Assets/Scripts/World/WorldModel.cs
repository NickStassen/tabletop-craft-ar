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
        public readonly HashSet<Vector3Int> DirtySections = new HashSet<Vector3Int>();
        public bool Loaded => SnapshotId >= 0;
        public Vector3Int SectionCount => new Vector3Int((Dims.x + Section - 1) / Section, (Dims.y + Section - 1) / Section, (Dims.z + Section - 1) / Section);

        public int Index(int x, int y, int z) => (y * Dims.z + z) * Dims.x + x;
        public bool InBounds(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < Dims.x && y < Dims.y && z < Dims.z;
        public ushort Get(int x, int y, int z) => InBounds(x, y, z) ? Grid[Index(x, y, z)] : (ushort)0xFFFF;
        public string State(ushort id) => id < Palette.Count ? Palette[id] : "minecraft:air";

        public void ApplySnapshot(ChunkSnapshot s)
        {
            SnapshotId = s.SnapshotId; World = s.World; Origin = s.Origin; Dims = s.Dims;
            Palette.Clear(); Palette.AddRange(s.Palette);
            Grid = s.Grid;
            DirtySections.Clear();
            var n = SectionCount;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++) DirtySections.Add(new Vector3Int(x, y, z));
        }

        /// <summary>Returns false if the update is for an older snapshot (dropped per spec).</summary>
        public bool ApplyBlockUpdate(Dictionary<string, object> m)
        {
            if (m.I("snapshot_id", -2) != SnapshotId) return false;
            var add = m.A("palette_add");
            if (add != null) foreach (var s in add) Palette.Add((string)s);
            foreach (var c in m.A("changes"))
            {
                var a = (List<object>)c;
                int x = (int)(double)a[0], y = (int)(double)a[1], z = (int)(double)a[2], p = (int)(double)a[3];
                if (!InBounds(x, y, z) || p >= Palette.Count) continue;
                Grid[Index(x, y, z)] = (ushort)p;
                MarkDirty(x, y, z);
            }
            return true;
        }

        void MarkDirty(int x, int y, int z)
        {
            var s = new Vector3Int(x / Section, y / Section, z / Section);
            DirtySections.Add(s);
            // Neighbouring sections when the block sits on a border (their faces may now be visible).
            if (x % Section == 0 && s.x > 0) DirtySections.Add(s + Vector3Int.left);
            if (x % Section == Section - 1) DirtySections.Add(s + Vector3Int.right);
            if (y % Section == 0 && s.y > 0) DirtySections.Add(s + Vector3Int.down);
            if (y % Section == Section - 1) DirtySections.Add(s + Vector3Int.up);
            if (z % Section == 0 && s.z > 0) DirtySections.Add(s + new Vector3Int(0, 0, -1));
            if (z % Section == Section - 1) DirtySections.Add(s + new Vector3Int(0, 0, 1));
        }
    }
}
