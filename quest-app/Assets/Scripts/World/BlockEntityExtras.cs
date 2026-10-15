// Engine-free helpers for the block entities' extras: banner flags composited from their pattern layers, beacon beam
// sections from the blocks above a beacon, and the scan that finds block entities in a WorldModel. Tests~ covers them.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class BannerImage
    {
        /// <summary>
        /// The flag texture as BannerRenderer layers it: entity/banner/banner_base (solid), then entity/banner/base tinted
        /// with the base colour, then each pattern (at most 16) tinted with its dye, each alpha-blended over the result.
        /// All images the same size (bottom-up pixels); a missing layer is skipped. Returns a new array.
        /// </summary>
        public static Color32[] Compose(Color32[] solid, int w, int h, IList<(Color32[] px, Color32 tint)> layers)
        {
            var o = new Color32[w * h];
            if (solid != null && solid.Length == o.Length) Array.Copy(solid, o, o.Length);
            foreach (var (px, tint) in layers)
            {
                if (px == null || px.Length != o.Length) continue;
                for (int i = 0; i < o.Length; i++)
                {
                    var s = px[i];
                    if (s.a == 0) continue;
                    float a = s.a / 255f, b = 1 - a;
                    var d = o[i];
                    o[i] = new Color32((byte)(s.r * tint.r / 255f * a + d.r * b + 0.5f), (byte)(s.g * tint.g / 255f * a + d.g * b + 0.5f),
                                       (byte)(s.b * tint.b / 255f * a + d.b * b + 0.5f), (byte)Math.Max(d.a, s.a));
                }
            }
            return o;
        }

        /// <summary>"minecraft:stripe_bottom" -> "entity/banner/stripe_bottom" (the pattern's asset id for vanilla ids).</summary>
        public static string PatternTexture(string pattern)
        {
            string p = pattern ?? "";
            int c = p.IndexOf(':');
            string ns = c >= 0 ? p.Substring(0, c) : "minecraft";
            p = c >= 0 ? p.Substring(c + 1) : p;
            return (ns == "minecraft" ? "" : ns + ":") + "entity/banner/" + p;
        }

        /// <summary>Parses a BlockEntityCatalog banner key back into base colour and (pattern, colour) layers.</summary>
        public static void ParseKey(string key, out string baseColor, List<BannerLayer> layers)
        {
            layers.Clear();
            var parts = key.Substring(BlockEntityCatalog.BannerPrefix.Length).Split('|');
            baseColor = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                int e = parts[i].LastIndexOf('=');
                if (e > 0) layers.Add(new BannerLayer(parts[i].Substring(0, e), parts[i].Substring(e + 1)));
            }
        }
    }

    /// <summary>A beacon beam section: from Y (relative to the beacon) up Height blocks, in one colour (sRGB).</summary>
    public struct BeamSection
    {
        public int Y, Height;
        public Color32 Color;
    }

    public static class BeaconBeam
    {
        /// <summary>
        /// BeaconBlockEntity's beam sections from the region grid, starting at the beacon itself (a white beam block):
        /// the first stained glass (or pane) above replaces the colour, later ones average with it, a different block
        /// that blocks light (an opaque cube here; bedrock excepted) stops the beam, anything else extends the section.
        /// The last section runs to the top of the region. Empty when the beam is blocked. dye(name) gives a dye's
        /// texture colour (white when unknown).
        /// </summary>
        public static List<BeamSection> Sections(WorldModel w, int x, int y, int z, Func<string, Color32> dye)
        {
            var list = new List<BeamSection>();
            if (w == null || !w.InBounds(x, y, z)) return list;
            Color32 white = dye("white");
            var cur = new BeamSection { Y = 0, Height = 1, Color = white };
            list.Add(cur);
            for (int ly = y + 1; ly < w.Dims.y; ly++)
            {
                string state = w.State(w.Get(x, ly, z));
                string path = BlockRules.Path(state);
                string glass = GlassDye(path);
                var last = list[list.Count - 1];
                if (glass != null)
                {
                    var c = dye(glass);
                    if (list.Count <= 1) list.Add(new BeamSection { Y = ly - y, Height = 1, Color = c });
                    else if (Same(c, last.Color)) { last.Height++; list[list.Count - 1] = last; }
                    else list.Add(new BeamSection { Y = ly - y, Height = 1, Color = Average(last.Color, c) });
                }
                else
                {
                    if (path != "bedrock" && w.ShapeOf(w.Get(x, ly, z)) == BlockShape.Cube) { list.Clear(); return list; }
                    last.Height++;
                    list[list.Count - 1] = last;
                }
            }
            return list;
        }

        /// <summary>The dye of a stained glass block or pane ("red_stained_glass_pane" -> "red"), else null.</summary>
        public static string GlassDye(string path)
        {
            int i = path.IndexOf("_stained_glass", StringComparison.Ordinal);
            return i > 0 && (path.EndsWith("_stained_glass") || path.EndsWith("_stained_glass_pane")) ? path.Substring(0, i) : null;
        }

        static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        /// <summary>ARGB.average: per channel (a + b) / 2.</summary>
        public static Color32 Average(Color32 a, Color32 b) => new Color32((byte)((a.r + b.r) / 2), (byte)((a.g + b.g) / 2), (byte)((a.b + b.b) / 2), 255);
    }

    /// <summary>
    /// One-block previews (the hand preview and the placement ghost, XR/BlockEditMode) run the terrain mesher, which
    /// leaves code-drawn block entities to BlockEntityRenderer: this adds their simple placeholder boxes (a chest box,
    /// a banner on its pole, a head, a bell's body under its modelled frame) so the preview isn't empty. Main thread.
    /// </summary>
    public static class BlockEntityPreview
    {
        static BlockModelBaker _baker;
        static IModelAssets _for;

        /// <summary>Appends the boxes of every code-drawn block in a small world (block units, cell corners). False when none.</summary>
        public static bool AddBoxes(WorldModel w, MeshData data, IModelAssets assets)
        {
            if (w == null || assets == null) return false;
            bool any = false;
            for (int y = 0; y < w.Dims.y; y++)
                for (int z = 0; z < w.Dims.z; z++)
                    for (int x = 0; x < w.Dims.x; x++)
                    {
                        string state = w.State(w.Get(x, y, z));
                        if (!BlockEntityCatalog.CodeDrawn(BlockRules.Path(state))) continue;
                        if (_baker == null || !ReferenceEquals(_for, assets)) { _baker = new BlockModelBaker(assets) { OnlyEntityBoxes = true }; _for = assets; }
                        var m = _baker.Bake(state);
                        if (m == null) continue;
                        foreach (var q in m.Quads) BlockModelMesher.AddQuad(data, new Vector3(x, y, z), q);
                        any |= m.Quads.Length > 0;
                    }
            return any;
        }
    }

    /// <summary>Finds the cells that hold block entities BlockEntityRenderer draws (any thread; reads the grid only).</summary>
    public static class BlockEntityScan
    {
        /// <summary>Per palette index: does the state need drawing by BlockEntityRenderer.</summary>
        public static bool[] Mask(IList<string> palette)
        {
            var m = new bool[palette.Count];
            for (int i = 0; i < m.Length; i++)
            {
                var k = BlockEntityCatalog.KindOfState(palette[i]);
                m[i] = k != BlockEntityKind.None && (k != BlockEntityKind.Lectern || palette[i].Contains("has_book=true"));
            }
            return m;
        }

        /// <summary>Region-local cells of the given 16^3 sections (all sections when null) whose palette index is in the mask.</summary>
        public static void Cells(ushort[] grid, Vector3Int dims, bool[] mask, IEnumerable<Vector3Int> sections, List<Vector3Int> into)
        {
            if (sections == null)
            {
                for (int i = 0; i < grid.Length; i++)
                {
                    int id = grid[i];
                    if (id < mask.Length && mask[id]) into.Add(new Vector3Int(i % dims.x, i / (dims.x * dims.z), (i / dims.x) % dims.z));
                }
                return;
            }
            const int S = WorldModel.Section;
            foreach (var s in sections)
            {
                int x0 = s.x * S, y0 = s.y * S, z0 = s.z * S;
                int x1 = Math.Min(x0 + S, dims.x), y1 = Math.Min(y0 + S, dims.y), z1 = Math.Min(z0 + S, dims.z);
                for (int y = y0; y < y1; y++)
                    for (int z = z0; z < z1; z++)
                    {
                        int row = (y * dims.z + z) * dims.x;
                        for (int x = x0; x < x1; x++)
                        {
                            int id = grid[row + x];
                            if (id < mask.Length && mask[id]) into.Add(new Vector3Int(x, y, z));
                        }
                    }
            }
        }
    }
}
