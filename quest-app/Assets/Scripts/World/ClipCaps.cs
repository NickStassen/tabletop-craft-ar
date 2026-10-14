// Solid sides for the clipped tabletop box. The block shaders discard geometry outside the visible window
// (NavController / TabletopClip.cginc), which would leave the cut hollow. ClipCaps keeps six cap meshes, one per
// window side, made by CapMesher from the slice of blocks each side cuts through, and moves them with the view so
// the miniature always looks like a solid block of terrain. Lives under "Blocks" (region-local block units, Z
// mirrored) and uses the same two block materials, so caps look like the faces next to them.
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class ClipCaps : MonoBehaviour
    {
        public WorldModel Model;
        public IFaceSource Faces;
        public Material OpaqueMaterial, TransparentMaterial;
        /// <summary>Bumped by the app on every snapshot and block_update, so caps re-slice when the blocks change.</summary>
        public int ModelVersion;

        sealed class Cap
        {
            public GameObject Go; public Mesh Mesh;
            public int Slice = int.MinValue, Version = -1, KeysVersion = -1;
            public Vector3Int BuiltOrigin;                // region origin the current mesh was built for
            public Task<(MeshData data, int slice, int version, Vector3Int origin, int keys)> Job;
        }

        readonly Cap[] _caps = new Cap[6];
        readonly List<Vector3> _tmp = new List<Vector3>();
        readonly List<int> _tri = new List<int>();
        FaceKey[] _keys = new FaceKey[0]; bool[] _capsTable = new bool[0];
        int _keysVersion, _keysSnapshot = -1, _keysCount;
        bool _visible;

        void Awake()
        {
            for (int f = 0; f < 6; f++)
            {
                var go = new GameObject("cap " + "-X+X-Y+Y-Z+Z".Substring(f * 2, 2), typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                go.SetActive(false);
                _caps[f] = new Cap { Go = go, Mesh = mesh };
            }
        }

        /// <summary>
        /// Place and (if needed) rebuild the caps for the window [lo, hi) in region-local blocks. Pass show = false to
        /// hide them (no window, or the window is the whole region and its border faces are already meshed).
        /// </summary>
        public void UpdateCaps(Vector3 lo, Vector3 hi, bool show)
        {
            if (Model == null || !Model.Loaded || !show)
            {
                if (_visible) { foreach (var c in _caps) c.Go.SetActive(false); _visible = false; }
                return;
            }
            _visible = true;
            RefreshKeys();
            var d = Model.Dims;
            for (int f = 0; f < 6; f++)
            {
                var c = _caps[f];
                int axis = f / 2;
                int slice = CapMesher.Slice(f, lo[axis], hi[axis]);
                // At the region's own border the mesher already emits the faces (no neighbour): no cap there.
                bool atBorder = (f & 1) == 0 ? lo[axis] <= 0.01f : hi[axis] >= d[axis] - 0.01f;
                bool inRange = slice >= 0 && slice < d[axis] && !atBorder;
                // A finished job is always uploaded: it is positioned for the origin it was built for, so a slice that
                // is a frame or two old still sits at the right place in the world.
                if (c.Job != null && c.Job.IsCompleted)
                {
                    if (!c.Job.IsFaulted)
                    {
                        var r = c.Job.Result;
                        Upload(c, r.data); c.Slice = r.slice; c.Version = r.version; c.BuiltOrigin = r.origin; c.KeysVersion = r.keys;
                    }
                    c.Job = null;
                }
                if (c.Job == null && inRange && (slice != c.Slice || c.Version != ModelVersion || c.BuiltOrigin != Model.Origin || c.KeysVersion != _keysVersion))
                {
                    // Grid and dims are captured together: a snapshot applied meanwhile can't mix them.
                    var w = new WorldModel { Dims = Model.Dims, Grid = Model.Grid };
                    var keys = _keys; var table = _capsTable; int ver = ModelVersion, kv = _keysVersion; var org = Model.Origin; int face = f, sl = slice;
                    c.Job = Task.Run(() =>
                    {
                        var m = new MeshData();
                        CapMesher.Build(w, face, sl, keys, table, m);
                        return (m, sl, ver, org, kv);
                    });
                }
                // Plane position: the window side, in region-local blocks for the CURRENT origin; the other two axes
                // carry the offset between the origin the mesh was built for and the current one.
                var off = new Vector3(c.BuiltOrigin.x - Model.Origin.x, c.BuiltOrigin.y - Model.Origin.y, c.BuiltOrigin.z - Model.Origin.z);
                float plane = (f & 1) == 0 ? lo[axis] : hi[axis];
                off[axis] = plane;
                c.Go.transform.localPosition = new Vector3(off.x, off.y, -off.z);
                c.Go.SetActive(inRange && c.Mesh.vertexCount > 0);
            }
        }

        /// <summary>palette index * 6 + face -> FaceKey, built on the main thread (the atlas loads textures here).</summary>
        void RefreshKeys()
        {
            if (_keysSnapshot == Model.SnapshotId && _keysCount == Model.Palette.Count) return;
            int n = Model.Palette.Count;
            var keys = new FaceKey[n * 6]; var table = new bool[n];
            for (int i = 0; i < n; i++)
            {
                string st = Model.Palette[i];
                var shape = BlockRules.Shape(st);
                table[i] = CapMesher.Caps(shape);
                if (!table[i]) continue;
                for (int f = 0; f < 6; f++)
                {
                    var k = Faces != null ? Faces.Face(st, f) : new FaceKey { Layer = -1, Color = BlockRules.FlatColor(st) };
                    k.Transparent |= shape != BlockShape.Cube;
                    keys[i * 6 + f] = k;
                }
            }
            // Normally the renderer has already resolved these states; if not, publish new layers like its Prewarm does.
            if (Faces is ResourcePackAtlas atlas)
            {
                atlas.Commit();
                if (atlas.Array != null)
                {
                    OpaqueMaterial.SetTexture("_Blocks", atlas.Array); OpaqueMaterial.SetFloat("_HasTex", 1);
                    TransparentMaterial.SetTexture("_Blocks", atlas.Array); TransparentMaterial.SetFloat("_HasTex", 1);
                }
            }
            _keys = keys; _capsTable = table; _keysSnapshot = Model.SnapshotId; _keysCount = n; _keysVersion++;
        }

        void Upload(Cap c, MeshData data)
        {
            var go = c.Go;
            var r = go.GetComponent<MeshRenderer>();
            if (r.sharedMaterials.Length != 2) r.sharedMaterials = new[] { OpaqueMaterial, TransparentMaterial };
            _tmp.Clear();
            foreach (var v in data.Verts) _tmp.Add(new Vector3(v.x, v.y, -v.z));   // mirror Z like TabletopRenderer
            var m = c.Mesh;
            m.Clear();
            m.SetVertices(_tmp);
            m.SetUVs(0, data.Uvs);
            m.SetColors(data.Colors);
            m.subMeshCount = 2;
            m.SetTriangles(Flip(data.Opaque), 0, false);
            m.SetTriangles(Flip(data.Transparent), 1, false);
            m.RecalculateNormals();
            m.RecalculateBounds();
        }

        List<int> Flip(List<int> t)
        {
            _tri.Clear();
            for (int i = 0; i < t.Count; i += 3) { _tri.Add(t[i]); _tri.Add(t[i + 2]); _tri.Add(t[i + 1]); }
            return _tri;
        }

        void OnDestroy() { foreach (var c in _caps) if (c?.Mesh != null) Destroy(c.Mesh); }
    }
}
