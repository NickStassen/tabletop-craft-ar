// [area] Mesh batching for big areas (ARCHITECTURE.md "Batching"; PLAN.md roadmap item 7). Engine-free: the
// TabletopRenderer uploads what this builds.
//
// Every non-empty 16^3 section used to be its own mesh and renderer, one or two draw calls each: about 330 at a
// 128-wide window, 1,200-1,800 at 256. Sections are now merged into batches of Columns x Columns sections
// horizontally over the region's full height. Batches are aligned to WORLD section coordinates (not to the region
// corner), so when the region shifts by whole sections, or grows or shrinks around the view, a batch keeps the same
// sections and only moves; the ones at the edges lose or gain a column.
//
//   SectionMesh  a section's mesh in batch form: Unity space (Z mirrored, winding flipped), relative to the section
//                corner, flat normals computed here (Unity's RecalculateNormals is too slow on the main thread for
//                100k-vertex batches). Built on the meshing worker, immutable, kept per section so a batch can be
//                rebuilt when one of its sections changes (edits, block_update, light) without re-meshing the rest.
//   BatchLayout  which batch a section belongs to, where a batch sits under "Blocks", and a section's offset in it.
//   BatchData    one batch's vertices and indices (opaque, transparent, water-back submeshes), assembled on a worker
//                from its sections' SectionMeshes with pooled arrays; the main thread only copies it into a Mesh.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>One batch vertex, in Unity's attribute order: position (float3), normal (snorm8 x4; [living] w = the
    /// vertex's LightCode for the time of day, DayLight.cs), colour (unorm8 x4), uv0 (float3: u, v, layer). 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BatchVertex
    {
        public Vector3 Pos;
        public sbyte Nx, Ny, Nz, Nw;
        public Color32 Color;
        public Vector3 Uv;
        public const int Size = 32;
    }

    public sealed class SectionMesh
    {
        public readonly BatchVertex[] Verts;
        /// <summary>Section-local indices: Opaque, then Transparent, then WaterBack (winding already flipped for Unity).</summary>
        public readonly int[] Indices;
        public readonly int Opaque, Transparent, WaterBack;
        /// <summary>Faces seen from outside (the water's inside copies are not counted), as the status line shows.</summary>
        public readonly int Quads;

        SectionMesh(BatchVertex[] v, int[] i, int o, int t, int w, int q) { Verts = v; Indices = i; Opaque = o; Transparent = t; WaterBack = w; Quads = q; }

        /// <summary>
        /// Converts a mesher result (Minecraft region-local block coordinates) into batch form: relative to the section
        /// corner, Z mirrored (Minecraft is right-handed, Unity left-handed), triangle winding flipped to match, and a
        /// normal per vertex like Mesh.RecalculateNormals: the normalised sum of the unit normals of the triangles that
        /// use it, or zero when they cancel (double-sided quads sharing vertices; the shader leaves those unshaded).
        /// Returns null for an empty section.
        /// </summary>
        public static SectionMesh From(MeshData d, Vector3Int section)
        {
            int n = d.Verts.Count;
            if (n == 0) return null;
            int S = WorldModel.Section;
            float bx = section.x * S, by = section.y * S, bz = section.z * S;
            var v = new BatchVertex[n];
            var acc = new float[n * 3];
            bool lit = d.Light.Count == n;   // [living] every emitter filled the light codes (else: open sky)
            for (int i = 0; i < n; i++)
            {
                var p = d.Verts[i];
                v[i].Pos = new Vector3(p.x - bx, p.y - by, bz - p.z);
                v[i].Color = d.Colors[i];
                v[i].Uv = d.Uvs[i];
                v[i].Nw = LightCode.ToSnorm(lit ? d.Light[i] : LightCode.OpenSky);   // [living] the time-of-day lookup (DayLight.cs)
            }
            int o = d.Opaque.Count, t = d.Transparent.Count, w = d.WaterBack.Count;
            var idx = new int[o + t + w];
            int at = 0;
            at = Flip(d.Opaque, idx, at, v, acc);
            at = Flip(d.Transparent, idx, at, v, acc);
            Flip(d.WaterBack, idx, at, v, acc);
            for (int i = 0; i < n; i++)
            {
                float x = acc[i * 3], y = acc[i * 3 + 1], z = acc[i * 3 + 2];
                float len = (float)Math.Sqrt(x * x + y * y + z * z);
                if (len < 1e-3f) continue;   // cancelled out: zero normal
                v[i].Nx = Snorm(x / len); v[i].Ny = Snorm(y / len); v[i].Nz = Snorm(z / len);
            }
            return new SectionMesh(v, idx, o, t, w, n / 4 - w / 6);
        }

        static sbyte Snorm(float f) => (sbyte)Math.Round(Math.Max(-1f, Math.Min(1f, f)) * 127f);

        /// <summary>Copies a triangle list with (a, c, b) winding and adds each triangle's unit normal to its vertices.</summary>
        static int Flip(List<int> tris, int[] dst, int at, BatchVertex[] v, float[] acc)
        {
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 2], c = tris[i + 1];
                dst[at++] = a; dst[at++] = b; dst[at++] = c;
                var p0 = v[a].Pos; var p1 = v[b].Pos; var p2 = v[c].Pos;
                float ux = p1.x - p0.x, uy = p1.y - p0.y, uz = p1.z - p0.z, wx = p2.x - p0.x, wy = p2.y - p0.y, wz = p2.z - p0.z;
                float nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;   // Unity's Vector3.Cross(p1 - p0, p2 - p0)
                float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-12f) continue;   // degenerate triangle
                nx /= len; ny /= len; nz /= len;
                acc[a * 3] += nx; acc[a * 3 + 1] += ny; acc[a * 3 + 2] += nz;
                acc[b * 3] += nx; acc[b * 3 + 1] += ny; acc[b * 3 + 2] += nz;
                acc[c * 3] += nx; acc[c * 3 + 1] += ny; acc[c * 3 + 2] += nz;
            }
            return at;
        }

        public int Bytes => Verts.Length * BatchVertex.Size + Indices.Length * 4;
    }

    /// <summary>
    /// Batch geometry for a region: batch keys are world-aligned groups of Columns x Columns sections on x and z (y = 0:
    /// a batch spans the region's full height). Keys stay the same when the region moves by whole sections.
    /// </summary>
    public readonly struct BatchLayout
    {
        public readonly Vector3Int Origin, Sections;
        public readonly int Columns;

        public BatchLayout(Vector3Int origin, Vector3Int dims, int columns)
        {
            Origin = origin; Sections = SectionPlan.Sections(dims); Columns = Math.Max(1, columns);
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
        /// <summary>World section index of the region's first section on x and z (sections start at origin + 16k).</summary>
        int Sx => FloorDiv(Origin.x, WorldModel.Section);
        int Sz => FloorDiv(Origin.z, WorldModel.Section);
        /// <summary>World section index of the region's first section on y.</summary>
        public int Sy => FloorDiv(Origin.y, WorldModel.Section);

        public Vector3Int KeyOf(Vector3Int section) => new Vector3Int(FloorDiv(Sx + section.x, Columns), 0, FloorDiv(Sz + section.z, Columns));

        /// <summary>Region-relative section key of the batch's first column (can be outside the region).</summary>
        public Vector3Int First(Vector3Int key) => new Vector3Int(key.x * Columns - Sx, 0, key.z * Columns - Sz);

        /// <summary>Region sections that belong to batch `key` (x/z clipped to the region, every y layer).</summary>
        public IEnumerable<Vector3Int> SectionsOf(Vector3Int key)
        {
            var f = First(key);
            for (int x = Math.Max(0, f.x); x < Math.Min(Sections.x, f.x + Columns); x++)
                for (int z = Math.Max(0, f.z); z < Math.Min(Sections.z, f.z + Columns); z++)
                    for (int y = 0; y < Sections.y; y++) yield return new Vector3Int(x, y, z);
        }

        /// <summary>True when batch `key` has at least one section in the region.</summary>
        public bool Covers(Vector3Int key)
        {
            var f = First(key);
            return Sections.y > 0 && f.x < Sections.x && f.x + Columns > 0 && f.z < Sections.z && f.z + Columns > 0;
        }

        /// <summary>Every batch key that has sections in this region.</summary>
        public List<Vector3Int> AllKeys()
        {
            var r = new List<Vector3Int>();
            if (Sections.x <= 0 || Sections.z <= 0) return r;
            var a = KeyOf(new Vector3Int(0, 0, 0)); var b = KeyOf(new Vector3Int(Sections.x - 1, 0, Sections.z - 1));
            for (int x = a.x; x <= b.x; x++) for (int z = a.z; z <= b.z; z++) r.Add(new Vector3Int(x, 0, z));
            return r;
        }

        /// <summary>
        /// Batch position under "Blocks" (block units, Z mirrored). y0 is the world section index the batch's mesh was
        /// built against (its BatchY), so a mesh built before a vertical shift stays where it is in the world.
        /// </summary>
        public Vector3 Position(Vector3Int key, int y0)
        {
            var f = First(key); int S = WorldModel.Section;
            return new Vector3(f.x * S, (y0 - Sy) * S, -f.z * S);
        }

        /// <summary>A section's corner inside its batch (Unity block units, Z mirrored), for a batch built against y0.</summary>
        public Vector3 Offset(Vector3Int section, Vector3Int key, int y0)
        {
            var f = First(key); int S = WorldModel.Section;
            return new Vector3((section.x - f.x) * S, (Sy + section.y - y0) * S, -(section.z - f.z) * S);
        }
    }

    /// <summary>One assembled batch: vertices, indices (opaque, transparent, water-back ranges) and bounds. Arrays are
    /// rented from ArrayPool; Release returns them once the mesh has been uploaded.</summary>
    public sealed class BatchData
    {
        public BatchVertex[] Verts = Array.Empty<BatchVertex>();
        public int[] Indices = Array.Empty<int>();
        public int VertCount, IndexCount, Opaque, Transparent, WaterBack, Quads, Sections;
        public Vector3 Min, Max;
        public double Ms;

        /// <summary>Concatenates sections (each with its offset in the batch). Runs on a worker; SectionMeshes are immutable.</summary>
        public static BatchData Assemble(IReadOnlyList<(SectionMesh mesh, Vector3 offset)> parts)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var b = new BatchData();
            int nv = 0, ni = 0;
            foreach (var p in parts) { nv += p.mesh.Verts.Length; ni += p.mesh.Indices.Length; b.Opaque += p.mesh.Opaque; b.Transparent += p.mesh.Transparent; b.WaterBack += p.mesh.WaterBack; b.Quads += p.mesh.Quads; }
            b.VertCount = nv; b.IndexCount = ni; b.Sections = parts.Count;
            if (nv == 0) { b.Ms = sw.Elapsed.TotalMilliseconds; return b; }
            b.Verts = ArrayPool<BatchVertex>.Shared.Rent(nv);
            b.Indices = ArrayPool<int>.Shared.Rent(ni);
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int vAt = 0;
            int oAt = 0, tAt = b.Opaque, wAt = b.Opaque + b.Transparent;
            foreach (var (m, off) in parts)
            {
                var src = m.Verts;
                for (int i = 0; i < src.Length; i++)
                {
                    var v = src[i];
                    v.Pos = new Vector3(v.Pos.x + off.x, v.Pos.y + off.y, v.Pos.z + off.z);
                    if (v.Pos.x < minX) minX = v.Pos.x; if (v.Pos.x > maxX) maxX = v.Pos.x;
                    if (v.Pos.y < minY) minY = v.Pos.y; if (v.Pos.y > maxY) maxY = v.Pos.y;
                    if (v.Pos.z < minZ) minZ = v.Pos.z; if (v.Pos.z > maxZ) maxZ = v.Pos.z;
                    b.Verts[vAt + i] = v;
                }
                var idx = m.Indices;
                for (int i = 0; i < m.Opaque; i++) b.Indices[oAt++] = idx[i] + vAt;
                for (int i = m.Opaque; i < m.Opaque + m.Transparent; i++) b.Indices[tAt++] = idx[i] + vAt;
                for (int i = m.Opaque + m.Transparent; i < idx.Length; i++) b.Indices[wAt++] = idx[i] + vAt;
                vAt += src.Length;
            }
            b.Min = new Vector3(minX, minY, minZ); b.Max = new Vector3(maxX, maxY, maxZ);
            b.Ms = sw.Elapsed.TotalMilliseconds;
            return b;
        }

        public void Release()
        {
            if (Verts.Length > 0) ArrayPool<BatchVertex>.Shared.Return(Verts);
            if (Indices.Length > 0) ArrayPool<int>.Shared.Return(Indices);
            Verts = Array.Empty<BatchVertex>(); Indices = Array.Empty<int>();
        }

        /// <summary>Draw calls this batch costs per eye pass: one per non-empty submesh that has a material.</summary>
        public int DrawCalls(bool underwater) => (Opaque > 0 ? 1 : 0) + (Transparent > 0 ? 1 : 0) + (underwater && WaterBack > 0 ? 1 : 0);
        public long Bytes => (long)VertCount * BatchVertex.Size + (long)IndexCount * 4;
    }

    /// <summary>[area] Engine-free rules for when batches are rebuilt (TabletopRenderer); tested in Tests~/AreaTests.cs.</summary>
    public static class BatchRules
    {
        /// <summary>
        /// Sections per batch side (x and z; a batch spans the full height). Chosen by measurement (TESTING.md "Area size
        /// presets and batching", seed 12345 with the 26.3 pack and light): 2 gives 36 renderers / 72 draw calls at a
        /// 128-wide window and 100 / 200 at 256 (per-section meshes: 328 / 605 and 1,223 / 2,058), while a batch stays
        /// under ~90k vertices (3 MB), so an edit re-uploads about 1.3 MB. 4 halves the draw calls again but an edit
        /// re-uploads 5-9 MB. tabletop.json "batch_columns" overrides it for headset comparisons.
        /// </summary>
        public static int Columns = 2;

        /// <summary>
        /// Rebuild a dirty batch now? Right away when none of its sections is waiting to be meshed. While some are, wait
        /// up to maxWait seconds after it first got dirty, so a big area still fills in progressively instead of
        /// re-uploading a batch for every section that lands. While staging (the old region stays up until the new one is
        /// complete) nobody sees the batch, so it waits for its sections.
        /// </summary>
        public static bool Ready(bool sectionsPending, float dirtySince, float now, float maxWait, bool staging) =>
            !sectionsPending || (!staging && now - dirtySince >= maxWait);

        /// <summary>[mesh] True when none of the batch's section columns is in the visible box (x and z; a batch spans the
        /// full height). Such a batch can wait for more of its sections before it is uploaded (TabletopRenderer).</summary>
        public static bool OffScreen(BatchLayout layout, Vector3Int key, SectionBox visible)
        {
            var f = layout.First(key);
            return (long)f.x + layout.Columns <= visible.Min.x || f.x >= visible.Max.x || (long)f.z + layout.Columns <= visible.Min.z || f.z >= visible.Max.z;
        }

        /// <summary>
        /// After a re-keyed snapshot (a shift or resize by whole sections): batches that lost sections (dropped by the
        /// new region) must be rebuilt; batches that no longer cover any section of the region go away. Batches that
        /// only gain sections get dirty when those are meshed. `held` = old section keys that had a mesh.
        /// </summary>
        public static (HashSet<Vector3Int> dirty, HashSet<Vector3Int> gone) AfterRekey(BatchLayout before, BatchLayout after, Vector3Int shiftSections,
                                                                                      IEnumerable<Vector3Int> held, IEnumerable<Vector3Int> batches)
        {
            var dirty = new HashSet<Vector3Int>(); var gone = new HashSet<Vector3Int>();
            var n = after.Sections;
            foreach (var k in held)
            {
                var nk = new Vector3Int(k.x - shiftSections.x, k.y - shiftSections.y, k.z - shiftSections.z);
                if (nk.x < 0 || nk.y < 0 || nk.z < 0 || nk.x >= n.x || nk.y >= n.y || nk.z >= n.z) dirty.Add(before.KeyOf(k));
            }
            foreach (var b in batches) if (!after.Covers(b)) gone.Add(b);
            dirty.ExceptWith(gone);
            return (dirty, gone);
        }
    }
}
