// chunk_snapshot with encoding "sections_v1" (PROTOCOL.md 4.18): the region as 16^3 sections, split over several
// messages ("part"/"parts"), optionally as a delta against the client's current snapshot ("base": unlisted
// sections are copied from it, shifted). Engine-free; runs on a worker thread (World/SnapshotAssembler).
using System;
using System.Collections.Generic;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.Net
{
    public static class SectionedSnapshot
    {
        public const string Encoding = "sections_v1";
        public const string Cap = "sections_v1";

        /// <summary>
        /// Builds the full grid from all parts (in order). current = the model the base refers to (read only; the caller
        /// holds back block_updates until this returns). listed receives the section keys present in the parts.
        /// Throws FormatException on anything inconsistent (the caller replies bad_snapshot).
        /// </summary>
        public static ChunkSnapshot Assemble(IList<Dictionary<string, object>> parts, WorldModel current, List<Vector3Int> listed)
        {
            if (parts == null || parts.Count == 0) throw new FormatException("no parts");
            var m0 = parts[0];
            if (m0.S("order") != "yzx" || m0.S("encoding") != Encoding) throw new FormatException("unsupported order/encoding");
            var o = Json.Ints(m0.A("origin")); var d = Json.Ints(m0.A("dims"));
            var pal = m0.A("palette"); var names = new string[pal.Count];
            for (int i = 0; i < names.Length; i++) names[i] = (string)pal[i];
            var s = new ChunkSnapshot
            {
                SnapshotId = m0.I("snapshot_id"), World = m0.S("world"),
                Origin = new Vector3Int(o[0], o[1], o[2]), Dims = new Vector3Int(d[0], d[1], d[2]), Palette = names,
            };
            long volume = (long)d[0] * d[1] * d[2];
            if (d[0] < 1 || d[1] < 1 || d[2] < 1 || volume > int.MaxValue) throw new FormatException("bad dims");
            var grid = new ushort[volume];
            var n = SectionPlan.Sections(s.Dims);
            var covered = new bool[n.x * n.y * n.z];
            int total = m0.I("parts", 1);
            if (parts.Count != total) throw new FormatException($"have {parts.Count} of {total} parts");

            // light_v1 (PROTOCOL.md 4.5.1): the top-level "light" says light travels inside the sections.
            var lightObj = m0.O("light");
            if (lightObj != null && lightObj.S("encoding") != "rle_v1") throw new FormatException("unsupported light encoding " + lightObj.S("encoding"));
            var light = lightObj != null ? new byte[volume] : null;

            var bas = m0.O("base");
            Vector3Int shift = default;
            if (bas != null) shift = CopyBase(bas, s, current, grid, light);
            var keys = new HashSet<Vector3Int>();

            for (int pi = 0; pi < parts.Count; pi++)
            {
                var m = parts[pi];
                if (m.I("snapshot_id") != s.SnapshotId || m.I("part", -1) != pi) throw new FormatException("parts out of order");
                var secs = m.A("sections");
                if (secs == null) continue;
                foreach (var e in secs)
                {
                    var sec = e as Dictionary<string, object> ?? throw new FormatException("section is not an object");
                    var k = Json.Ints(sec.A("s"));
                    if (k.Length != 3 || k[0] < 0 || k[1] < 0 || k[2] < 0 || k[0] >= n.x || k[1] >= n.y || k[2] >= n.z) throw new FormatException("section key out of range");
                    int ci = (k[1] * n.z + k[2]) * n.x + k[0];
                    if (covered[ci]) throw new FormatException("section listed twice");
                    covered[ci] = true;
                    DecodeSection(sec.A("data"), s.Dims, k[0], k[1], k[2], names.Length, grid);
                    if (light != null) DecodeSectionLight(sec.A("light") ?? throw new FormatException("section without light"), s.Dims, k[0], k[1], k[2], light);
                    keys.Add(new Vector3Int(k[0], k[1], k[2]));
                    listed?.Add(new Vector3Int(k[0], k[1], k[2]));
                }
            }
            if (bas == null)
                for (int i = 0; i < covered.Length; i++) if (!covered[i]) throw new FormatException("section missing from a full sectioned snapshot");
            if (bas != null) CheckDeltaCoverage(s.Dims, current.Dims, shift, keys);
            s.Grid = grid;
            s.Light = light;
            s.Biomes = BiomeGrid.From(m0.O("biomes"));   // biomes_v1: the whole region, on part 0
            return s;
        }

        /// <summary>
        /// Delta: copy everything that overlaps the base region; listed sections overwrite their part afterwards. [area]
        /// The base may have other dims (a section-aligned resize, PROTOCOL.md 4.25): new local p = old local p + shift.
        /// </summary>
        static Vector3Int CopyBase(Dictionary<string, object> bas, ChunkSnapshot s, WorldModel cur, ushort[] grid, byte[] light)
        {
            if (light != null && (cur?.Light == null || cur.Light.Length != cur.Grid.Length)) throw new FormatException("delta with light against a snapshot without light");
            int baseId = bas.I("snapshot_id", -1);
            var sh = Json.Ints(bas.A("shift"));
            if (cur == null || !cur.Loaded || cur.SnapshotId != baseId) throw new FormatException($"base snapshot {baseId} is not the current one ({cur?.SnapshotId})");
            if (cur.World != s.World) throw new FormatException("base has a different world");
            if (sh.Length != 3 || sh[0] % WorldModel.Section != 0 || sh[1] % WorldModel.Section != 0 || sh[2] % WorldModel.Section != 0) throw new FormatException("shift must be whole sections");
            if (cur.Origin.x + sh[0] != s.Origin.x || cur.Origin.y + sh[1] != s.Origin.y || cur.Origin.z + sh[2] != s.Origin.z) throw new FormatException("shift does not match the origins");
            // The base palette must be a prefix of the new one, so base indices stay valid.
            if (cur.Palette.Count > s.Palette.Length) throw new FormatException("palette shrank");
            for (int i = 0; i < cur.Palette.Count; i++) if (cur.Palette[i] != s.Palette[i]) throw new FormatException("palette prefix differs at " + i);
            var d = s.Dims; var od = cur.Dims; var old = cur.Grid;
            var shift = new Vector3Int(sh[0], sh[1], sh[2]);
            int x0 = Math.Max(0, -sh[0]), x1 = Math.Min(d.x, od.x - sh[0]);
            if (x1 <= x0) return shift;
            for (int y = 0; y < d.y; y++)
            {
                int oy = y + sh[1];
                if (oy < 0 || oy >= od.y) continue;
                for (int z = 0; z < d.z; z++)
                {
                    int oz = z + sh[2];
                    if (oz < 0 || oz >= od.z) continue;
                    Array.Copy(old, (oy * od.z + oz) * od.x + x0 + sh[0], grid, (y * d.z + z) * d.x + x0, x1 - x0);
                    if (light != null) Array.Copy(cur.Light, (oy * od.z + oz) * od.x + x0 + sh[0], light, (y * d.z + z) * d.x + x0, x1 - x0);
                }
            }
            return shift;
        }

        /// <summary>A section's packed light (sky &lt;&lt; 4 | block): RLE pairs over its box in yzx order.</summary>
        static void DecodeSectionLight(List<object> data, Vector3Int dims, int kx, int ky, int kz, byte[] light)
        {
            if (data.Count % 2 != 0) throw new FormatException("section light must be [value, run] pairs");
            int S = WorldModel.Section;
            int bx = kx * S, by = ky * S, bz = kz * S;
            int sx = Math.Min(S, dims.x - bx), sy = Math.Min(S, dims.y - by), sz = Math.Min(S, dims.z - bz);
            int vol = sx * sy * sz, p = 0, x = 0, y = 0, z = 0;
            for (int i = 0; i < data.Count; i += 2)
            {
                int v = (int)(double)data[i], run = (int)(double)data[i + 1];
                if (run < 1 || p + run > vol || v < 0 || v > 255) throw new FormatException($"bad light run in section {kx},{ky},{kz}");
                for (int j = 0; j < run; j++)
                {
                    light[((by + y) * dims.z + bz + z) * dims.x + bx + x] = (byte)v;
                    if (++x == sx) { x = 0; if (++z == sz) { z = 0; y++; } }
                }
                p += run;
            }
            if (p != vol) throw new FormatException($"section {kx},{ky},{kz} light covers {p} of {vol}");
        }

        /// <summary>RLE pairs over one section's box (clamped to dims) in yzx order.</summary>
        static void DecodeSection(List<object> data, Vector3Int dims, int kx, int ky, int kz, int paletteSize, ushort[] grid)
        {
            if (data == null || data.Count % 2 != 0) throw new FormatException("section data must be [index, run] pairs");
            int S = WorldModel.Section;
            int bx = kx * S, by = ky * S, bz = kz * S;
            int sx = Math.Min(S, dims.x - bx), sy = Math.Min(S, dims.y - by), sz = Math.Min(S, dims.z - bz);
            int vol = sx * sy * sz, p = 0;
            int x = 0, y = 0, z = 0;
            for (int i = 0; i < data.Count; i += 2)
            {
                int v = (int)(double)data[i], run = (int)(double)data[i + 1];
                if (run < 1 || p + run > vol || v < 0 || v >= paletteSize) throw new FormatException($"bad run in section {kx},{ky},{kz}");
                for (int j = 0; j < run; j++)
                {
                    grid[((by + y) * dims.z + bz + z) * dims.x + bx + x] = (ushort)v;
                    if (++x == sx) { x = 0; if (++z == sz) { z = 0; y++; } }
                }
                p += run;
            }
            if (p != vol) throw new FormatException($"section {kx},{ky},{kz} runs cover {p} of {vol}");
        }

        /// <summary>Unlisted sections of a delta must lie fully inside the base region (else data would be missing).</summary>
        static void CheckDeltaCoverage(Vector3Int dims, Vector3Int baseDims, Vector3Int sh, HashSet<Vector3Int> listed)
        {
            var n = SectionPlan.Sections(dims); int S = WorldModel.Section;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                var k = new Vector3Int(x, y, z);
                if (listed.Contains(k)) continue;
                for (int a = 0; a < 3; a++)
                {
                    int lo = k[a] * S + sh[a], hi = Math.Min(dims[a], k[a] * S + S) + sh[a];
                    if (lo < 0 || hi > baseDims[a]) throw new FormatException($"section {x},{y},{z} is neither listed nor inside the base");
                }
            }
        }
    }
}
