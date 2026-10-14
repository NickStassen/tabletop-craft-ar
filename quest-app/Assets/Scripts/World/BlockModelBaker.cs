// Bakes Java-edition block models from the user's resource pack into quads (ARCHITECTURE.md section 4,
// "Block models"). Engine-free, so Tests~ runs it against a real client jar. It reads JSON and resolves textures,
// so it runs on the main thread only (BlockModels.Prepare); worker threads only read the finished BakedModel.
//
// Supported: blockstate "variants" (property match; a weighted list takes its first entry; x/y rotation; uvlock)
// and "multipart" (every part whose "when" matches, including OR/AND and "a|b" alternatives); model "parent"
// chains with "#texture" variables (26.x {"sprite": ..., "force_translucent": ...} objects too); elements with
// from/to, per-face uv (default from the element bounds), texture, cullface, rotation and tintindex; element
// rotation as axis/angle/origin/rescale or as 26.x x/y/z angles. Block entities (chests, banners, heads, shulker
// boxes, pots...) have no model geometry in vanilla, so they get simple boxes (BlockEntityBoxes).
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>One baked face. Positions are block-local in block units (0..1 for most models). T0..T3 are
    /// (u, v, layer) with u/v in 0..1 inside the Texture2DArray layer (Unity orientation, v up).</summary>
    public struct BakedQuad
    {
        public Vector3 P0, P1, P2, P3;
        public Vector3 T0, T1, T2, T3;
        public Color32 Color;     // base colour: tint for tinted faces, white for plain textures, flat colour without a texture
        public sbyte Cull;        // neighbour direction (IFaceSource order 0..5) that hides this face, -1 = never culled
        public sbyte Dir;         // axis-aligned facing (0..5), -1 for rotated faces
        public bool Tinted;       // tintindex present (grass, foliage, water...)
        public TintKind Tint;     // biome colour of a tinted face (BlockShading resolves it per block), None otherwise
        public bool Translucent;  // alpha-blended submesh; otherwise the alpha-tested opaque submesh
    }

    public sealed class BakedModel
    {
        public BakedQuad[] Quads = new BakedQuad[0];
        /// <summary>Six full faces on the block boundary and nothing else: the block stays in the greedy path.</summary>
        public bool FullCube;
        /// <summary>FullCube whose six base faces have opaque textures (hides neighbour faces like stone does).</summary>
        public bool Opaque;
        /// <summary>FullCube only: the first face per direction as a greedy FaceKey (texture, tint, UV rotation), with a
        /// second whole face on that side as its Overlay (the grass side overlay).</summary>
        public FaceKey[] CubeFaces;
        /// <summary>Per direction, 4 x 64 bits: which 1/16 cells of that side of the block are covered by opaque
        /// boundary faces. A neighbour's face with a cullface towards this block is hidden if its cells are covered.</summary>
        public ulong[] Cover = new ulong[24];
        /// <summary>Per quad, 4 x 64 bits: the 1/16 cells its cullface side would need covered (rounded outwards).</summary>
        public ulong[] CullCells = new ulong[0];

        public bool Covers(int dir, ulong[] cells, int quad)
        {
            int c = dir * 4, q = quad * 4;
            return (cells[q] & ~Cover[c]) == 0 && (cells[q + 1] & ~Cover[c + 1]) == 0 && (cells[q + 2] & ~Cover[c + 2]) == 0 && (cells[q + 3] & ~Cover[c + 3]) == 0;
        }
    }

    /// <summary>What the baker needs from the resource pack. ResourcePackAtlas implements it on the main thread;
    /// Tests~ implements it over a zip.</summary>
    public interface IModelAssets
    {
        /// <summary>Parsed JSON at a zip path such as assets/minecraft/models/block/stone.json, or null.</summary>
        Dictionary<string, object> ReadJson(string path);
        /// <summary>Texture2DArray layer (-1 if missing) plus the face colour (tint, white, or flat colour).</summary>
        FaceKey Texture(string texture, bool tinted, string state);
        /// <summary>0 = opaque, 1 = cutout (alpha 0 or 255 only), 2 = translucent (some partial alpha).</summary>
        int AlphaClass(int layer);
    }

    /// <summary>Baked block models by state string. Thread-safe read; null when the state was never prepared.</summary>
    public interface IModelSource
    {
        BakedModel Model(string state);
    }

    /// <summary>UV orientation for greedy cube faces: vanilla's default per-face mapping, in block units from the
    /// section-local corner position (Repeat wrap tiles merged quads exactly like single blocks), then the face's
    /// rotation/mirror from its block model (FaceKey.UvMap: bit0 swap u/v, bit1 negate u, bit2 negate v).</summary>
    public static class CubeUv
    {
        /// <summary>(u, v) in block units, Unity orientation (v up), for a point on a face pointing in dir.</summary>
        public static void Canon(int dir, Vector3 p, out float u, out float v)
        {
            switch (dir)
            {
                case 0: u = p.z; v = p.y; break;   // west
                case 1: u = -p.z; v = p.y; break;  // east
                case 2: u = p.x; v = p.z; break;   // down
                case 3: u = p.x; v = -p.z; break;  // up
                case 4: u = -p.x; v = p.y; break;  // north
                default: u = p.x; v = p.y; break;  // south
            }
        }

        public static Vector3 Greedy(int dir, Vector3 p, FaceKey key)
        {
            Canon(dir, p, out float u, out float v);
            int m = key.UvMap;
            if ((m & 1) != 0) { float t = u; u = v; v = t; }
            if ((m & 2) != 0) u = -u;
            if ((m & 4) != 0) v = -v;
            return new Vector3(u, v, key.Layer);
        }

        /// <summary>The UvMap that turns the canonical mapping into this axis-aligned quad's own UVs.</summary>
        public static byte MapOf(in BakedQuad q, int dir)
        {
            Canon(dir, q.P0, out float u0, out float v0);
            Canon(dir, q.P1, out float u1, out float v1);
            Canon(dir, q.P3, out float u3, out float v3);
            float cu1 = u1 - u0, cv1 = v1 - v0, cu2 = u3 - u0, cv2 = v3 - v0;
            float bu1 = q.T1.x - q.T0.x, bv1 = q.T1.y - q.T0.y, bu2 = q.T3.x - q.T0.x, bv2 = q.T3.y - q.T0.y;
            float det = cu1 * cv2 - cu2 * cv1;
            if (Math.Abs(det) < 1e-6f) return 0;
            float m00 = (bu1 * cv2 - bu2 * cv1) / det, m01 = (bu2 * cu1 - bu1 * cu2) / det;
            float m10 = (bv1 * cv2 - bv2 * cv1) / det, m11 = (bv2 * cu1 - bv1 * cu2) / det;
            if (Math.Abs(m00) >= Math.Abs(m01)) return (byte)((m00 < 0 ? 2 : 0) | (m11 < 0 ? 4 : 0));
            return (byte)(1 | (m01 < 0 ? 2 : 0) | (m10 < 0 ? 4 : 0));
        }
    }

    public sealed class BlockModelBaker
    {
        public static readonly string[] DirNames = { "west", "east", "down", "up", "north", "south" };
        static readonly Vector3[] DirVec = { new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1), new Vector3(0, 0, 1) };
        // Vanilla FaceInfo: per direction, 4 corners as (x, y, z) picks, 1 = max, 0 = min. Counter-clockwise seen
        // from outside; corner i takes UV corner i of (u0,v0) (u0,v1) (u1,v1) (u1,v0), shifted by the face rotation.
        static readonly int[][] Corners =
        {
            new[] { 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1 }, // west
            new[] { 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0 }, // east
            new[] { 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 0, 1 }, // down
            new[] { 0, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0 }, // up
            new[] { 1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0 }, // north
            new[] { 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1 }, // south
        };

        readonly IModelAssets _assets;
        readonly Dictionary<string, Dictionary<string, object>> _blockstates = new Dictionary<string, Dictionary<string, object>>();
        readonly Dictionary<string, Resolved> _resolved = new Dictionary<string, Resolved>();
        bool _hasBlockColor;
        TintKind _tintKind;   // which biome colour this state's tinted faces take (BiomeColors.BlockTint)
        Color32 _blockColor;

        public BlockModelBaker(IModelAssets assets) { _assets = assets; }

        struct TexRef { public string Value; public bool Force; }

        sealed class Resolved
        {
            public readonly Dictionary<string, TexRef> Textures = new Dictionary<string, TexRef>();
            public List<object> Elements;
        }

        /// <summary>Model rotation from a blockstate entry (x/y in degrees) about the block centre.</summary>
        struct M3
        {
            public float a, b, c, d, e, f, g, h, i;
            public static M3 Identity => new M3 { a = 1, e = 1, i = 1 };
            static float Snap(double x) => Math.Abs(x) < 1e-6 ? 0f : Math.Abs(x - 1) < 1e-6 ? 1f : Math.Abs(x + 1) < 1e-6 ? -1f : (float)x;
            public static M3 X(double deg) { double r = deg * Math.PI / 180; float co = Snap(Math.Cos(r)), si = Snap(Math.Sin(r)); return new M3 { a = 1, e = co, f = -si, h = si, i = co }; }
            public static M3 Y(double deg) { double r = deg * Math.PI / 180; float co = Snap(Math.Cos(r)), si = Snap(Math.Sin(r)); return new M3 { a = co, c = si, e = 1, g = -si, i = co }; }
            public static M3 Z(double deg) { double r = deg * Math.PI / 180; float co = Snap(Math.Cos(r)), si = Snap(Math.Sin(r)); return new M3 { a = co, b = -si, d = si, e = co, i = 1 }; }
            public static M3 operator *(M3 p, M3 q) => new M3
            {
                a = p.a * q.a + p.b * q.d + p.c * q.g, b = p.a * q.b + p.b * q.e + p.c * q.h, c = p.a * q.c + p.b * q.f + p.c * q.i,
                d = p.d * q.a + p.e * q.d + p.f * q.g, e = p.d * q.b + p.e * q.e + p.f * q.h, f = p.d * q.c + p.e * q.f + p.f * q.i,
                g = p.g * q.a + p.h * q.d + p.i * q.g, h = p.g * q.b + p.h * q.e + p.i * q.h, i = p.g * q.c + p.h * q.f + p.i * q.i,
            };
            public Vector3 Mul(Vector3 v) => new Vector3(a * v.x + b * v.y + c * v.z, d * v.x + e * v.y + f * v.z, g * v.x + h * v.y + i * v.z);
            public bool IsIdentity => a == 1 && e == 1 && i == 1 && b == 0 && c == 0 && d == 0 && f == 0 && g == 0 && h == 0;
            /// <summary>Vanilla BlockModelRotation: rotate by -x about X first, then by -y about Y.</summary>
            public static M3 Model(double x, double y) => Y(-y) * X(-x);
        }

        /// <summary>Element rotation: rotate about Origin (in 1/16), then scale the off-axis components (rescale).</summary>
        struct ElementRot { public bool On; public M3 R; public Vector3 Origin, Scale; }

        public static Dictionary<string, string> Props(string state)
        {
            var d = new Dictionary<string, string>();
            int i = state.IndexOf('[');
            if (i < 0) return d;
            foreach (var kv in state.Substring(i + 1).TrimEnd(']').Split(','))
            {
                int e = kv.IndexOf('=');
                if (e > 0) d[kv.Substring(0, e)] = kv.Substring(e + 1);
            }
            return d;
        }

        /// <summary>Bakes every model the state selects, or returns null when the pack has no blockstate for it.</summary>
        public BakedModel Bake(string state)
        {
            string name = BlockRules.Name(state);
            string ns = name.Contains(":") ? name.Substring(0, name.IndexOf(':')) : "minecraft";
            string path = BlockRules.Path(state);
            string key = ns + ":" + path;
            if (!_blockstates.TryGetValue(key, out var bs)) { bs = _assets.ReadJson($"assets/{ns}/blockstates/{path}.json"); _blockstates[key] = bs; }
            if (bs == null) return null;
            var props = Props(state);
            var applies = new List<Dictionary<string, object>>();
            var variants = bs.O("variants");
            if (variants != null)
            {
                object pick = null, fallback = null;
                foreach (var kv in variants)
                {
                    if (fallback == null) fallback = kv.Value;
                    if (VariantMatches(kv.Key, props)) { pick = kv.Value; break; }
                }
                if (pick == null) pick = fallback;   // properties from another game version: better the default look than nothing
                var a = First(pick);
                if (a != null) applies.Add(a);
            }
            var multipart = bs.A("multipart");
            if (multipart != null)
                foreach (var p in multipart)
                    if (p is Dictionary<string, object> part && (!part.TryGetValue("when", out var w) || When(w, props)))
                    {
                        var a = First(part.TryGetValue("apply", out var ap) ? ap : null);
                        if (a != null) applies.Add(a);
                    }

            _hasBlockColor = BlockColor(path, props, out _blockColor);
            _tintKind = BiomeColors.BlockTint(state, out _);
            var quads = new List<BakedQuad>();
            Resolved firstModel = null;
            foreach (var a in applies)
            {
                var model = a.S("model");
                var r = model == null ? null : Resolve(model);
                if (r == null) continue;
                if (firstModel == null) firstModel = r;
                var rot = M3.Model(a.D("x"), a.D("y"));
                bool uvlock = a.B("uvlock");
                if (r.Elements != null) foreach (var el in r.Elements) if (el is Dictionary<string, object> e) BakeElement(e, r, rot, uvlock, state, quads);
            }
            BlockEntityBoxes.Add(this, path, props, firstModel, state, quads);
            return Finish(quads);
        }

        /// <summary>[menu] Bakes one model by id with no blockstate rotation, e.g. an item's inventory model
        /// ("minecraft:block/oak_fence_inventory") for the block menu's icons. The state picks tints and block colours as
        /// in Bake. Null when the model is missing or has no elements (builtin/entity items).</summary>
        public BakedModel BakeModel(string model, string state)
        {
            var r = Resolve(model);
            if (r?.Elements == null || r.Elements.Count == 0) return null;
            _hasBlockColor = BlockColor(BlockRules.Path(state), Props(state), out _blockColor);
            _tintKind = BiomeColors.BlockTint(state, out _);
            var quads = new List<BakedQuad>();
            foreach (var el in r.Elements) if (el is Dictionary<string, object> e) BakeElement(e, r, M3.Identity, false, state, quads);
            return Finish(quads);
        }

        static Dictionary<string, object> First(object v)
        {
            if (v is List<object> l) v = l.Count > 0 ? l[0] : null;   // weighted random list: vanilla picks per position; we take the first
            return v as Dictionary<string, object>;
        }

        static bool VariantMatches(string key, Dictionary<string, string> props)
        {
            if (key.Length == 0) return true;
            foreach (var cond in key.Split(','))
            {
                int e = cond.IndexOf('=');
                if (e < 0) continue;
                if (!props.TryGetValue(cond.Substring(0, e), out var have) || have != cond.Substring(e + 1)) return false;
            }
            return true;
        }

        static string JsonValue(object v) => v is bool b ? (b ? "true" : "false") : v is double d ? ((long)d).ToString() : v as string;

        /// <summary>Multipart "when": {"k": "a|b"} (all keys must match), {"OR": [...]}, {"AND": [...]}; "!a" negates.</summary>
        public static bool When(object cond, Dictionary<string, string> props)
        {
            if (!(cond is Dictionary<string, object> c)) return true;
            if (c.TryGetValue("OR", out var or) && or is List<object> ol) { foreach (var x in ol) if (When(x, props)) return true; return false; }
            if (c.TryGetValue("AND", out var and) && and is List<object> al) { foreach (var x in al) if (!When(x, props)) return false; return true; }
            foreach (var kv in c)
            {
                string want = JsonValue(kv.Value);
                if (want == null) return false;
                bool neg = want.StartsWith("!");
                if (neg) want = want.Substring(1);
                props.TryGetValue(kv.Key, out var have);
                bool any = false;
                foreach (var alt in want.Split('|')) if (alt == have) { any = true; break; }
                if (any == neg) return false;
            }
            return true;
        }

        /// <summary>Vanilla block colours that depend on the state, not the biome (BlockColors): redstone power, stem age.</summary>
        public static bool BlockColor(string path, Dictionary<string, string> p, out Color32 c)
        {
            c = default;
            if (path == "redstone_wire")
            {
                float f = (p.TryGetValue("power", out var s) && int.TryParse(s, out int n) ? n : 0) / 15f;
                float r = f * 0.6f + (f > 0 ? 0.4f : 0.3f), g = Math.Max(0, f * f * 0.7f - 0.5f), b = Math.Max(0, f * f * 0.6f - 0.7f);
                c = new Color32((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), 255);
                return true;
            }
            if (path == "pumpkin_stem" || path == "melon_stem")
            {
                int age = p.TryGetValue("age", out var s) && int.TryParse(s, out int n) ? n : 0;
                c = new Color32((byte)(age * 32), (byte)(255 - age * 8), (byte)(age * 4), 255);
                return true;
            }
            if (path == "attached_pumpkin_stem" || path == "attached_melon_stem") { c = new Color32(224, 199, 28, 255); return true; }
            return false;
        }

        Resolved Resolve(string model)
        {
            if (_resolved.TryGetValue(model, out var done)) return done;
            var r = new Resolved();
            string cur = model;
            for (int depth = 0; depth < 16 && cur != null && !cur.StartsWith("builtin/"); depth++)
            {
                string ns = "minecraft", p = cur;
                int c = cur.IndexOf(':'); if (c >= 0) { ns = cur.Substring(0, c); p = cur.Substring(c + 1); }
                var mj = _assets.ReadJson($"assets/{ns}/models/{p}.json");
                if (mj == null) { if (depth == 0) r = null; break; }
                if (mj.O("textures") is Dictionary<string, object> t)
                    foreach (var kv in t)
                        if (!r.Textures.ContainsKey(kv.Key))
                        {
                            if (kv.Value is string s) r.Textures[kv.Key] = new TexRef { Value = s };
                            else if (kv.Value is Dictionary<string, object> o) r.Textures[kv.Key] = new TexRef { Value = o.S("sprite"), Force = o.B("force_translucent") };
                        }
                if (r.Elements == null && mj.A("elements") is List<object> el) r.Elements = el;
                cur = mj.S("parent");
            }
            _resolved[model] = r;
            return r;
        }

        /// <summary>Follows "#var" references; null when unresolved (vanilla would show the missing texture).</summary>
        static bool Lookup(Resolved r, string reference, out string texture, out bool force)
        {
            texture = reference; force = false;
            for (int i = 0; i < 16 && texture != null && texture.StartsWith("#"); i++)
            {
                if (!r.Textures.TryGetValue(texture.Substring(1), out var t)) { texture = null; break; }
                texture = t.Value; force |= t.Force;
            }
            return texture != null && !texture.StartsWith("#");
        }

        internal bool ParticleOf(object model, string state, out FaceKey key)
        {
            key = default;
            if (!(model is Resolved r) || !Lookup(r, "#particle", out var tex, out _)) return false;
            key = _assets.Texture(tex, false, state);
            return key.Layer >= 0;
        }

        internal FaceKey Texture(string texture, string state) => _assets.Texture(texture, false, state);

        static Vector3 V(List<object> l, Vector3 def) => l != null && l.Count >= 3 && l[0] is double x && l[1] is double y && l[2] is double z ? new Vector3((float)x, (float)y, (float)z) : def;

        void BakeElement(Dictionary<string, object> el, Resolved r, M3 rot, bool uvlock, string state, List<BakedQuad> outQ)
        {
            var from = V(el.A("from"), new Vector3(0, 0, 0));
            var to = V(el.A("to"), new Vector3(16, 16, 16));
            var er = ElementRotation(el.O("rotation"));
            var faces = el.O("faces");
            if (faces == null) return;
            // Inside-out elements (from > to) are faces meant to be seen from inside the block (the inner cage of a
            // spawner, the underside of a snow layer). Keep them only for see-through textures.
            bool inverted = from.x > to.x || from.y > to.y || from.z > to.z;
            for (int d = 0; d < 6; d++)
            {
                var f = faces.O(DirNames[d]);
                if (f == null || !Lookup(r, f.S("texture"), out var tex, out bool force)) continue;
                // Only vanilla's tinted blocks (BlockColors) honour a tintindex; for the rest it is ignored (untinted).
                bool tinted = f.ContainsKey("tintindex") && BiomeColors.Tinted(state, f.I("tintindex", -1));
                var key = _assets.Texture(tex, tinted && !_hasBlockColor, state);
                if (tinted && _hasBlockColor) { if (key.Layer >= 0) key.Color = _blockColor; tinted = false; }   // not a biome tint
                if (inverted && key.Layer >= 0 && _assets.AlphaClass(key.Layer) == 0) continue;
                float[] uv = DefaultUv(d, from, to);
                if (f.A("uv") is List<object> u && u.Count >= 4) for (int k = 0; k < 4; k++) uv[k] = u[k] is double x ? (float)x : uv[k];
                int cull = CullIndex(f.S("cullface"));
                bool translucent = force || (key.Layer >= 0 && _assets.AlphaClass(key.Layer) == 2);
                Emit(from, to, d, uv, f.I("rotation"), key, tinted, translucent, cull, er, rot, uvlock, outQ);
            }
        }

        static int CullIndex(string s)
        {
            if (s == null) return -1;
            if (s == "bottom") return 2;
            return Array.IndexOf(DirNames, s);
        }

        static ElementRot ElementRotation(Dictionary<string, object> r)
        {
            var e = new ElementRot { Scale = new Vector3(1, 1, 1) };
            if (r == null) return e;
            e.Origin = V(r.A("origin"), new Vector3(8, 8, 8));
            string axis = r.S("axis");
            if (axis != null)
            {
                double angle = r.D("angle");
                if (Math.Abs(angle) < 1e-9) return e;
                e.R = axis == "x" ? M3.X(angle) : axis == "y" ? M3.Y(angle) : M3.Z(angle);
                if (r.B("rescale"))
                {
                    float s = (float)(1 / Math.Cos(angle * Math.PI / 180));
                    e.Scale = axis == "x" ? new Vector3(1, s, s) : axis == "y" ? new Vector3(s, 1, s) : new Vector3(s, s, 1);
                }
            }
            else
            {
                // 26.x: "x"/"y"/"z" angles. Applied X, then Y, then Z.
                double x = r.D("x"), y = r.D("y"), z = r.D("z");
                if (Math.Abs(x) + Math.Abs(y) + Math.Abs(z) < 1e-9) return e;
                e.R = M3.Z(z) * M3.Y(y) * M3.X(x);
            }
            e.On = true;
            return e;
        }

        /// <summary>Vanilla BlockElement.uvsByDefault: u0, v0, u1, v1 in 1/16 from the element bounds.</summary>
        static float[] DefaultUv(int d, Vector3 f, Vector3 t)
        {
            switch (d)
            {
                case 0: return new[] { f.z, 16 - t.y, t.z, 16 - f.y };            // west
                case 1: return new[] { 16 - t.z, 16 - t.y, 16 - f.z, 16 - f.y };  // east
                case 2: return new[] { f.x, 16 - t.z, t.x, 16 - f.z };            // down
                case 3: return new[] { f.x, f.z, t.x, t.z };                      // up
                case 4: return new[] { 16 - t.x, 16 - t.y, 16 - f.x, 16 - f.y };  // north
                default: return new[] { f.x, 16 - t.y, t.x, 16 - f.y };           // south
            }
        }

        static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        static int Snap(Vector3 v, float min)
        {
            float len = (float)Math.Sqrt(Dot(v, v));
            if (len < 1e-9f) return -1;
            for (int d = 0; d < 6; d++) if (Dot(v, DirVec[d]) / len > min) return d;
            return -1;
        }

        /// <summary>One face: corners from the (unordered) bounds, element rotation, model rotation, UVs, cullface.</summary>
        void Emit(Vector3 from, Vector3 to, int d, float[] uv, int faceRot, FaceKey key, bool tinted, bool translucent, int cull,
                  ElementRot er, M3 rot, bool uvlock, List<BakedQuad> outQ)
        {
            var c = Corners[d];
            var p = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                // Like vanilla, "from" is always the min side and "to" the max side, even when from > to.
                var v = new Vector3(c[i * 3] == 1 ? to.x : from.x, c[i * 3 + 1] == 1 ? to.y : from.y, c[i * 3 + 2] == 1 ? to.z : from.z);
                if (er.On)
                {
                    var q = er.R.Mul(v - er.Origin);
                    v = new Vector3(q.x * er.Scale.x, q.y * er.Scale.y, q.z * er.Scale.z) + er.Origin;
                }
                if (!rot.IsIdentity) v = rot.Mul(v - new Vector3(8, 8, 8)) + new Vector3(8, 8, 8);
                p[i] = v;
            }
            var n = Cross(p[1] - p[0], p[2] - p[0]);
            if (Dot(n, n) < 1e-8f)
            {
                n = Cross(p[2] - p[0], p[3] - p[0]);
                if (Dot(n, n) < 1e-8f) return;   // zero-area face
            }
            int dir = Snap(n, 0.999f);
            if (cull >= 0 && !rot.IsIdentity) cull = Snap(rot.Mul(DirVec[cull]), 0.5f);

            var t = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                float u, v;
                if (uvlock && !rot.IsIdentity && dir >= 0)
                {
                    // uvlock: the texture stays aligned to the world, i.e. the default mapping of the final face.
                    var pp = p[i];
                    switch (dir)
                    {
                        case 0: u = pp.z; v = 16 - pp.y; break;
                        case 1: u = 16 - pp.z; v = 16 - pp.y; break;
                        case 2: u = pp.x; v = 16 - pp.z; break;
                        case 3: u = pp.x; v = pp.z; break;
                        case 4: u = 16 - pp.x; v = 16 - pp.y; break;
                        default: u = pp.x; v = 16 - pp.y; break;
                    }
                }
                else
                {
                    int s = (i + ((faceRot / 90) % 4 + 4)) % 4;
                    u = s == 0 || s == 1 ? uv[0] : uv[2];
                    v = s == 0 || s == 3 ? uv[1] : uv[3];
                }
                t[i] = new Vector3(u / 16f, 1f - v / 16f, key.Layer);
            }
            const float k = 1f / 16;
            outQ.Add(new BakedQuad
            {
                P0 = p[0] * k, P1 = p[1] * k, P2 = p[2] * k, P3 = p[3] * k,
                T0 = t[0], T1 = t[1], T2 = t[2], T3 = t[3],
                Color = key.Color, Cull = (sbyte)cull, Dir = (sbyte)dir, Tinted = tinted, Translucent = translucent,
                Tint = tinted ? _tintKind : TintKind.None,
            });
        }

        /// <summary>A box with the same texture on every face (block entities). from/to in 1/16; yaw in degrees
        /// clockwise seen from above (blockstate "y" convention); faces on the block boundary get a cullface.</summary>
        internal void Box(Vector3 from, Vector3 to, FaceKey key, double yaw, List<BakedQuad> outQ)
        {
            var rot = M3.Model(0, yaw);
            bool rightAngle = Math.Abs(yaw % 90) < 1e-6;
            for (int d = 0; d < 6; d++)
            {
                float plane = d % 2 == 0 ? from[d / 2] : to[d / 2];
                int cull = rightAngle && (plane == 0 || plane == 16) ? d : -1;
                Emit(from, to, d, DefaultUv(d, from, to), 0, key, false, false, cull, default, rot, false, outQ);
            }
        }

        static bool OnBoundary(in BakedQuad q, int dir)
        {
            int axis = dir / 2;
            float plane = dir % 2 == 0 ? 0 : 1;
            return Math.Abs(q.P0[axis] - plane) < 1e-4f && Math.Abs(q.P1[axis] - plane) < 1e-4f && Math.Abs(q.P2[axis] - plane) < 1e-4f && Math.Abs(q.P3[axis] - plane) < 1e-4f;
        }

        static void Bounds2(in BakedQuad q, int dir, out float a0, out float b0, out float a1, out float b1)
        {
            int ua = dir / 2 == 0 ? 2 : 0, va = dir / 2 == 1 ? 2 : 1;   // x faces: (z, y); y faces: (x, z); z faces: (x, y)
            a0 = Math.Min(Math.Min(q.P0[ua], q.P1[ua]), Math.Min(q.P2[ua], q.P3[ua])); a1 = Math.Max(Math.Max(q.P0[ua], q.P1[ua]), Math.Max(q.P2[ua], q.P3[ua]));
            b0 = Math.Min(Math.Min(q.P0[va], q.P1[va]), Math.Min(q.P2[va], q.P3[va])); b1 = Math.Max(Math.Max(q.P0[va], q.P1[va]), Math.Max(q.P2[va], q.P3[va]));
        }

        static void Fill(ulong[] bits, int at, int a0, int b0, int a1, int b1)
        {
            for (int b = Math.Max(0, b0); b < Math.Min(16, b1); b++)
                for (int a = Math.Max(0, a0); a < Math.Min(16, a1); a++) { int i = b * 16 + a; bits[at + (i >> 6)] |= 1UL << (i & 63); }
        }

        bool IsOpaque(in BakedQuad q)
        {
            int layer = (int)q.T0.z;
            return !q.Translucent && (layer >= 0 ? _assets.AlphaClass(layer) == 0 : q.Color.a == 255);
        }

        BakedModel Finish(List<BakedQuad> quads)
        {
            var m = new BakedModel { Quads = quads.ToArray(), CullCells = new ulong[quads.Count * 4] };
            var first = new int[] { -1, -1, -1, -1, -1, -1 };
            var second = new int[] { -1, -1, -1, -1, -1, -1 };   // a whole face over the first one: an overlay (grass sides)
            bool onlyShell = true, inside = true;
            for (int i = 0; i < m.Quads.Length; i++)
            {
                var q = m.Quads[i];
                int d = q.Dir;
                bool boundary = d >= 0 && OnBoundary(q, d);
                float a0 = 0, b0 = 0, a1 = 0, b1 = 0;
                if (d >= 0) Bounds2(q, d, out a0, out b0, out a1, out b1);
                bool whole = boundary && a0 < 1e-4f && b0 < 1e-4f && a1 > 1 - 1e-4f && b1 > 1 - 1e-4f;
                if (!(whole && q.Cull == d)) onlyShell = false;
                if (whole && q.Cull == d && first[d] < 0) first[d] = i;
                else if (whole && q.Cull == d && second[d] < 0) second[d] = i;
                for (int a = 0; a < 3; a++)
                    if (Math.Min(Math.Min(q.P0[a], q.P1[a]), Math.Min(q.P2[a], q.P3[a])) < -1e-4f || Math.Max(Math.Max(q.P0[a], q.P1[a]), Math.Max(q.P2[a], q.P3[a])) > 1 + 1e-4f) inside = false;
                if (boundary && IsOpaque(q))
                    Fill(m.Cover, d * 4, (int)Math.Ceiling(a0 * 16 - 1e-3), (int)Math.Ceiling(b0 * 16 - 1e-3), (int)Math.Floor(a1 * 16 + 1e-3), (int)Math.Floor(b1 * 16 + 1e-3));
                if (q.Cull >= 0)
                {
                    // Rounded outwards and at least one cell, so an edge-on face is never "covered" by nothing.
                    Bounds2(q, q.Cull, out a0, out b0, out a1, out b1);
                    int ia0 = Math.Min(15, (int)Math.Floor(a0 * 16 + 1e-3)), ib0 = Math.Min(15, (int)Math.Floor(b0 * 16 + 1e-3));
                    int ia1 = Math.Max(ia0 + 1, (int)Math.Ceiling(a1 * 16 - 1e-3)), ib1 = Math.Max(ib0 + 1, (int)Math.Ceiling(b1 * 16 - 1e-3));
                    Fill(m.CullCells, i * 4, ia0, ib0, ia1, ib1);
                }
            }
            bool shell = true, opaqueShell = true;
            for (int d = 0; d < 6; d++)
            {
                if (first[d] < 0) { shell = false; break; }
                if (!IsOpaque(m.Quads[first[d]])) opaqueShell = false;
            }
            // A full cube: only whole boundary faces (overlays like grass sides included), or an opaque shell with
            // more faces hidden inside it (powder snow's inner faces).
            if (shell && (onlyShell || (opaqueShell && inside)))
            {
                m.FullCube = true;
                m.Opaque = opaqueShell;
                m.CubeFaces = new FaceKey[6];
                for (int d = 0; d < 6; d++)
                {
                    var q = m.Quads[first[d]];
                    m.CubeFaces[d] = new FaceKey { Layer = (int)q.T0.z, Color = q.Color, UvMap = CubeUv.MapOf(q, d), Tint = q.Tint };
                    // A second whole face on the same side is drawn over the first (grass_block_side_overlay, tinted):
                    // the greedy mesher emits it as a coplanar second quad (FaceKey.Overlay).
                    if (second[d] >= 0 && (int)m.Quads[second[d]].T0.z >= 0)
                    {
                        var o = m.Quads[second[d]];
                        m.CubeFaces[d].Overlay = (int)o.T0.z + 1;
                        m.CubeFaces[d].OverlayColor = o.Color; m.CubeFaces[d].OverlayTint = o.Tint; m.CubeFaces[d].OverlayUvMap = CubeUv.MapOf(o, d);
                    }
                }
            }
            return m;
        }
    }

    /// <summary>Block entities are drawn by code in vanilla; their block models only name a particle texture.
    /// Simple boxes keep them from being invisible. Bells get a body under their (modelled) frame.</summary>
    static class BlockEntityBoxes
    {
        static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        static double Facing(Dictionary<string, string> p)
        {
            p.TryGetValue("facing", out var f);
            return f == "east" ? 90 : f == "south" ? 180 : f == "west" ? 270 : 0;
        }

        static double Rotation16(Dictionary<string, string> p) => p.TryGetValue("rotation", out var r) && int.TryParse(r, out int n) ? n * 22.5 : 0;

        static Color32 HeadColor(string path)
        {
            if (path.StartsWith("wither_skeleton")) return new Color32(52, 52, 52, 255);
            if (path.StartsWith("skeleton")) return new Color32(205, 205, 195, 255);
            if (path.StartsWith("zombie")) return new Color32(84, 136, 74, 255);
            if (path.StartsWith("creeper")) return new Color32(96, 170, 84, 255);
            if (path.StartsWith("piglin")) return new Color32(226, 160, 146, 255);
            if (path.StartsWith("dragon")) return new Color32(36, 36, 40, 255);
            return new Color32(190, 140, 110, 255);   // player head
        }

        public static void Add(BlockModelBaker b, string path, Dictionary<string, string> p, object model, string state, List<BakedQuad> q)
        {
            if (path == "bell")
            {
                if (b.ParticleOf(model, state, out var bell)) { b.Box(V(4, 4, 4), V(12, 6, 12), bell, 0, q); b.Box(V(5, 6, 5), V(11, 13, 11), bell, 0, q); }
                return;
            }
            if (q.Count > 0) return;
            bool hasParticle = b.ParticleOf(model, state, out var particle);
            if (path.EndsWith("chest"))
            {
                if (!hasParticle) return;
                float x0 = 1, x1 = 15;
                p.TryGetValue("type", out var type);
                if (type == "left") x1 = 16; else if (type == "right") x0 = 0;   // the other half is clockwise / counter-clockwise of facing
                b.Box(V(x0, 0, 1), V(x1, 14, 15), particle, Facing(p), q);
            }
            else if (path.EndsWith("shulker_box")) { if (hasParticle) b.Box(V(0, 0, 0), V(16, 16, 16), particle, 0, q); }
            else if (path.EndsWith("_skull") || path.EndsWith("_head"))
            {
                var key = new FaceKey { Layer = -1, Color = HeadColor(path) };
                if (path.Contains("_wall_")) b.Box(V(4, 4, 8), V(12, 12, 16), key, Facing(p), q);
                else b.Box(V(4, 0, 4), V(12, 8, 12), key, Rotation16(p), q);
            }
            else if (path.EndsWith("_banner"))
            {
                int cut = path.IndexOf(path.EndsWith("_wall_banner") ? "_wall_banner" : "_banner", StringComparison.Ordinal);
                var cloth = b.Texture("block/" + path.Substring(0, cut) + "_wool", state);
                if (cloth.Layer < 0) cloth = new FaceKey { Layer = -1, Color = BlockRules.FlatColor(path) };
                if (path.EndsWith("_wall_banner"))
                {
                    if (hasParticle) b.Box(V(1, 14, 13.5f), V(15, 16, 15.5f), particle, Facing(p), q);
                    b.Box(V(1, -12, 14), V(15, 14, 15), cloth, Facing(p), q);
                }
                else
                {
                    if (hasParticle) { b.Box(V(7, 0, 7), V(9, 28, 9), particle, Rotation16(p), q); b.Box(V(1, 26, 9), V(15, 28, 11), particle, Rotation16(p), q); }
                    b.Box(V(1, 4, 9.5f), V(15, 26, 10.5f), cloth, Rotation16(p), q);
                }
            }
            else if (path == "decorated_pot") { if (hasParticle) { b.Box(V(1, 0, 1), V(15, 13, 15), particle, 0, q); b.Box(V(4, 13, 4), V(12, 16, 12), particle, 0, q); } }
            else if (path == "conduit") { if (hasParticle) b.Box(V(5, 5, 5), V(11, 11, 11), particle, 0, q); }
            else if (path.EndsWith("copper_golem_statue")) { if (hasParticle) b.Box(V(3, 0, 4), V(13, 15, 12), particle, Facing(p), q); }
            else if (path == "end_portal") b.Box(V(0, 0, 0), V(16, 12, 16), new FaceKey { Layer = -1, Color = new Color32(14, 18, 34, 255) }, 0, q);
            else if (path == "end_gateway") b.Box(V(0, 0, 0), V(16, 16, 16), new FaceKey { Layer = -1, Color = new Color32(14, 18, 34, 255) }, 0, q);
            // Packs before 26.x have no geometry for beds and signs either.
            else if (!hasParticle) return;
            else if (path.EndsWith("_bed")) b.Box(V(0, 0, 0), V(16, 9, 16), particle, 0, q);
            else if (path.EndsWith("_wall_hanging_sign")) b.Box(V(1, 0, 7), V(15, 10, 9), particle, Facing(p), q);
            else if (path.EndsWith("_hanging_sign")) b.Box(V(1, 0, 7), V(15, 10, 9), particle, Rotation16(p), q);
            else if (path.EndsWith("_wall_sign")) b.Box(V(0, 4.5f, 14), V(16, 12.5f, 16), particle, Facing(p), q);
            else if (path.EndsWith("_sign")) { b.Box(V(7, 0, 7), V(9, 9, 9), particle, Rotation16(p), q); b.Box(V(0, 8, 7), V(16, 16, 9), particle, Rotation16(p), q); }
        }
    }
}
