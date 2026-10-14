// Entity box models (boats, minecarts, mobs, armour) from entity_models.json (which the user generates from the game's
// model layer definitions in their own client jar), built for the Quest's figure space exactly like PlayerModel: game
// model space point-reflected,
// (x, y, z) -> (-x, -y, -z) per part, so rotations keep their numeric values; the caller puts the model root at
// (0, 24, 0) for living entities (the game's 1.501-block offset) and turns it by 180 + body yaw. Engine-free (Tests~
// builds and checks the meshes); EntityFigure turns them into shared Unity meshes.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    /// <summary>One cube of a model part (game model space: Y down, front -Z), as ModelPart.Cube builds it.</summary>
    public sealed class EModelCube
    {
        public float U, V, X, Y, Z, W, H, D, Gx, Gy, Gz;
        public bool Mirror;
        public int Faces = 63;   // 1 down, 2 up, 4 north, 8 south, 16 west, 32 east
    }

    /// <summary>A model part: pivot and default rotation (radians, applied Z * Y * X like the game), scale, cubes, children.</summary>
    public sealed class EModelPart
    {
        public string Name;
        public float Px, Py, Pz, Rx, Ry, Rz, Sx = 1, Sy = 1, Sz = 1;
        public readonly List<EModelCube> Cubes = new List<EModelCube>();
        public readonly List<EModelPart> Children = new List<EModelPart>();
        public EModelPart Parent;
        public int Index;   // depth-first position in EModel.Parts (root = 0)
    }

    public sealed class EModel
    {
        public string Name;
        public int TexW, TexH;
        public EModelPart Root;
        public readonly List<EModelPart> Parts = new List<EModelPart>();   // depth-first, root first
        readonly Dictionary<string, EModelPart> _byName = new Dictionary<string, EModelPart>();

        public EModelPart Find(string name) => name != null && _byName.TryGetValue(name, out var p) ? p : null;

        internal void Index(EModelPart p)
        {
            p.Index = Parts.Count;
            Parts.Add(p);
            if (!string.IsNullOrEmpty(p.Name) && !_byName.ContainsKey(p.Name)) _byName[p.Name] = p;
            foreach (var c in p.Children) { c.Parent = p; Index(c); }
        }

        /// <summary>Total cube count (for stats and tests).</summary>
        public int CubeCount { get { int n = 0; foreach (var p in Parts) n += p.Cubes.Count; return n; } }
    }

    /// <summary>
    /// The model data: entity_models.json, which the user generates from their own client jar with
    /// tools/entity_models/DumpEntityModels.java and copies next to pack.zip (nothing derived from the game ships with
    /// the app). Until it is loaded, or without it, Get() returns null and entities are drawn as boxes of their size.
    /// </summary>
    public static class EntityModels
    {
        public const string FileName = "entity_models.json";
        static Dictionary<string, object> _raw;                  // "models" of the loaded file, or null
        static Dictionary<string, int> _dyes = new Dictionary<string, int>();
        static readonly Dictionary<string, EModel> Cache = new Dictionary<string, EModel>();
        static readonly object Lock = new object();
        static volatile int _generation;

        /// <summary>Bumped when model data is loaded (or dropped): figures built before it are rebuilt.</summary>
        public static int Generation => _generation;
        public static bool Loaded { get { lock (Lock) return _raw != null; } }
        /// <summary>For the status line and logs: what was loaded, or why not.</summary>
        public static string Status { get; private set; } = "no " + FileName;

        /// <summary>Names (layers) in the loaded data, aliases included (empty when nothing is loaded).</summary>
        public static IEnumerable<string> Names { get { lock (Lock) return _raw != null ? new List<string>(_raw.Keys) : new List<string>(); } }

        /// <summary>Loads entity_models.json text (any thread). False, with Status saying why, when it isn't usable.</summary>
        public static bool LoadJson(string json)
        {
            Dictionary<string, object> models, dyes, textDyes;
            string mc;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                models = root?.O("models");
                dyes = root?.O("dyes");
                textDyes = root?.O("text_dyes");   // [blocks] sign text colours (DyeColor.getTextColor), files from 0.4.0 on
                mc = root?.S("minecraft");
                if (models == null || root.I("format") != 1) throw new FormatException("not an entity_models.json (format 1) file");
            }
            catch (Exception e) { Status = FileName + ": " + e.Message; return false; }
            var d = new Dictionary<string, int>();
            if (dyes != null) foreach (var kv in dyes) if (kv.Value is double v) d[kv.Key] = (int)v;
            var td = new Dictionary<string, int>();
            if (textDyes != null) foreach (var kv in textDyes) if (kv.Value is double v) td[kv.Key] = (int)v;
            lock (Lock)
            {
                _raw = models;
                _dyes = d;
                _textDyes = td;
                Cache.Clear();
                _generation++;
            }
            Status = $"{FileName}: {models.Count} models (Minecraft {mc})";
            return true;
        }

        /// <summary>Loads the file if it exists (any thread; the Quest calls it on a worker at start).</summary>
        public static bool LoadFile(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) { Status = "no " + path + " (entities are boxes)"; return false; }
                return LoadJson(System.IO.File.ReadAllText(path));
            }
            catch (Exception e) { Status = FileName + ": " + e.Message; return false; }
        }

        /// <summary>Forgets the loaded data (tests).</summary>
        public static void Unload()
        {
            lock (Lock) { _raw = null; _dyes = new Dictionary<string, int>(); _textDyes = new Dictionary<string, int>(); Cache.Clear(); _generation++; }
            Status = "no " + FileName;
        }

        /// <summary>A dye's texture colour (DyeColor.getTextureDiffuseColor) from the loaded file, as 0xRRGGBB.</summary>
        public static bool Dye(string name, out int rgb)
        {
            lock (Lock) { rgb = 0; return name != null && _dyes.TryGetValue(name, out rgb); }
        }

        static Dictionary<string, int> _textDyes = new Dictionary<string, int>();

        /// <summary>[blocks] A dye's sign text colour (DyeColor.getTextColor), as 0xRRGGBB; false for files without it.</summary>
        public static bool TextDye(string name, out int rgb)
        {
            lock (Lock) { rgb = 0; return name != null && _textDyes.TryGetValue(name, out rgb); }
        }

        /// <summary>The model for a layer name ("cow", "sheep#wool", "boat/oak"), or null if unknown or no data. Thread-safe, cached.</summary>
        public static EModel Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (Lock)
            {
                if (_raw == null) return null;
                if (Cache.TryGetValue(name, out var m)) return m;
                object v = null;
                string key = name;
                for (int i = 0; i < 4 && _raw.TryGetValue(key, out v) && v is string alias; i++) key = alias;   // identical models are aliases
                try { m = v is Dictionary<string, object> d ? Parse(name, d) : null; }
                catch (Exception) { m = null; }   // a damaged entry: that type falls back to a box
                Cache[name] = m;
                return m;
            }
        }

        static EModel Parse(string name, Dictionary<string, object> d)
        {
            var t = Json.Ints(d.A("t"));
            var m = new EModel { Name = name, TexW = t[0], TexH = t[1], Root = Part(d.A("r")) };
            m.Index(m.Root);
            return m;
        }

        static float F(object o) => (float)(double)o;

        static EModelPart Part(List<object> a)
        {
            var p = new EModelPart { Name = (string)a[0] };
            var pose = (List<object>)a[1];
            p.Px = F(pose[0]); p.Py = F(pose[1]); p.Pz = F(pose[2]); p.Rx = F(pose[3]); p.Ry = F(pose[4]); p.Rz = F(pose[5]);
            if (pose.Count >= 9) { p.Sx = F(pose[6]); p.Sy = F(pose[7]); p.Sz = F(pose[8]); }
            foreach (List<object> c in (List<object>)a[2])
            {
                var cube = new EModelCube { U = F(c[0]), V = F(c[1]), X = F(c[2]), Y = F(c[3]), Z = F(c[4]), W = F(c[5]), H = F(c[6]), D = F(c[7]) };
                if (c.Count > 8)
                {
                    if (c[8] is List<object> g) { cube.Gx = F(g[0]); cube.Gy = F(g[1]); cube.Gz = F(g[2]); }
                    else cube.Gx = cube.Gy = cube.Gz = F(c[8]);
                }
                if (c.Count > 9) cube.Mirror = F(c[9]) != 0;
                if (c.Count > 10) cube.Faces = (int)F(c[10]);
                p.Cubes.Add(cube);
            }
            foreach (List<object> ch in (List<object>)a[3]) p.Children.Add(Part(ch));
            return p;
        }

        /// <summary>A part's own cubes in its local figure space (pivot at the origin), UVs normalised to the texture size.</summary>
        public static void BuildPart(EModel m, EModelPart p, FigureMesh mesh)
        {
            foreach (var c in p.Cubes) AppendCube(c, m.TexW, m.TexH, mesh);
        }

        // Corner order and per-face UV rectangles follow ModelPart.Cube exactly (see PlayerModel.AppendBox).
        // Corners: 0=(x0,y0,z0) 1=(x1,y0,z0) 2=(x1,y1,z0) 3=(x0,y1,z0) 4=(x0,y0,z1) 5=(x1,y0,z1) 6=(x1,y1,z1) 7=(x0,y1,z1).
        static readonly int[][] FaceCorners =
        {
            new[] { 5, 4, 0, 1 }, // DOWN
            new[] { 2, 3, 7, 6 }, // UP
            new[] { 0, 4, 7, 3 }, // WEST
            new[] { 1, 0, 3, 2 }, // NORTH
            new[] { 5, 1, 2, 6 }, // EAST
            new[] { 4, 5, 6, 7 }, // SOUTH
        };
        static readonly int[] FaceBit = { 1, 2, 16, 4, 32, 8 };
        // Outward direction of each face in game space (DOWN is -Y; the game's Y points down the model).
        static readonly float[][] GameDir = { new[] { 0f, -1, 0 }, new[] { 0f, 1, 0 }, new[] { -1f, 0, 0 }, new[] { 0f, 0, -1 }, new[] { 1f, 0, 0 }, new[] { 0f, 0, 1 } };

        /// <summary>
        /// One cube: grown by its deformation, mirrored like the game (x bounds swapped, so the west skin area lands on
        /// the east side), point-reflected into figure space. Triangles are wound clockwise seen from outside (Unity).
        /// </summary>
        public static void AppendCube(EModelCube b, int texW, int texH, FigureMesh m)
        {
            float x0 = b.X - b.Gx, y0 = b.Y - b.Gy, z0 = b.Z - b.Gz;
            float x1 = b.X + b.W + b.Gx, y1 = b.Y + b.H + b.Gy, z1 = b.Z + b.D + b.Gz;
            if (b.Mirror) { float t = x0; x0 = x1; x1 = t; }
            float[,] c =
            {
                { x0, y0, z0 }, { x1, y0, z0 }, { x1, y1, z0 }, { x0, y1, z0 },
                { x0, y0, z1 }, { x1, y0, z1 }, { x1, y1, z1 }, { x0, y1, z1 },
            };
            float u = b.U, v = b.V, w = b.W, h = b.H, d = b.D;
            float[][] uv =
            {
                new[] { u + d, v, u + d + w, v + d },
                new[] { u + d + w, v + d, u + d + w + w, v },
                new[] { u, v + d, u + d, v + d + h },
                new[] { u + d, v + d, u + d + w, v + d + h },
                new[] { u + d + w, v + d, u + d + w + d, v + d + h },
                new[] { u + d + w + d, v + d, u + d + w + d + w, v + d + h },
            };
            for (int f = 0; f < 6; f++)
            {
                if ((b.Faces & FaceBit[f]) == 0) continue;
                int start = m.VertexCount;
                var q = uv[f];
                float[] cu = { q[2], q[0], q[0], q[2] }, cv = { q[1], q[1], q[3], q[3] };
                // Outward normal in figure space: the face's game direction (x flipped for mirrored west/east faces),
                // point-reflected.
                float nx = -GameDir[f][0] * (b.Mirror ? -1 : 1), ny = -GameDir[f][1], nz = -GameDir[f][2];
                for (int k = 0; k < 4; k++)
                {
                    int ci = FaceCorners[f][k];
                    m.Pos.Add(-c[ci, 0]); m.Pos.Add(-c[ci, 1]); m.Pos.Add(-c[ci, 2]);
                    m.Normal.Add(nx); m.Normal.Add(ny); m.Normal.Add(nz);
                    m.Uv.Add(cu[k] / texW); m.Uv.Add(1f - cv[k] / texH);
                }
                // Wind so that cross(b - a, c - a) points along the outward normal (front face for Unity). Zero-area faces
                // (flat cubes) keep the default order; the entity shader draws both sides anyway.
                int p0 = start * 3, p1 = (start + 1) * 3, p2 = (start + 2) * 3;
                float ax = m.Pos[p1] - m.Pos[p0], ay = m.Pos[p1 + 1] - m.Pos[p0 + 1], az = m.Pos[p1 + 2] - m.Pos[p0 + 2];
                float bx = m.Pos[p2] - m.Pos[p0], by = m.Pos[p2 + 1] - m.Pos[p0 + 1], bz = m.Pos[p2 + 2] - m.Pos[p0 + 2];
                float dot = (ay * bz - az * by) * nx + (az * bx - ax * bz) * ny + (ax * by - ay * bx) * nz;
                if (dot > 0) { m.Tris.Add(start); m.Tris.Add(start + 1); m.Tris.Add(start + 2); m.Tris.Add(start); m.Tris.Add(start + 2); m.Tris.Add(start + 3); }
                else { m.Tris.Add(start); m.Tris.Add(start + 2); m.Tris.Add(start + 1); m.Tris.Add(start); m.Tris.Add(start + 3); m.Tris.Add(start + 2); }
            }
        }

        /// <summary>Figure-space local position of a part relative to its parent: the point-reflected pivot.</summary>
        public static void Pivot(EModelPart p, out float x, out float y, out float z) { x = -p.Px; y = -p.Py; z = -p.Pz; }
    }
}
