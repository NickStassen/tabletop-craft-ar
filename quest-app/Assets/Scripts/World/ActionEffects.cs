// Block actions on the table (PROTOCOL.md 4.22): the pack's destroy_stage_0..9 cracks on blocks being dug (a slightly
// inflated cube, cut out), cleared on abort, break or after 20 s without news like the game; and block-break / place
// particles: small camera-facing squares cut from the broken block's texture (the atlas layer the renderer already
// uses, so one shared mesh and material for all of them), thrown out, falling with the game's gravity. Lives under the
// "Blocks" transform (block units, Z mirrored by hand like TabletopRenderer).
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class ActionEffects : MonoBehaviour
    {
        public WorldModel Model;
        public ResourcePackAtlas Atlas;
        public EntityTextures Textures;
        public Material BlocksMaterial;     // TabletopRenderer's opaque material (Texture2DArray + vertex colours)
        public const int MaxParticles = 1536;
        const float Gravity = 16f;          // blocks/s^2: the game's 0.04 blocks/tick^2
        const float CrackTimeout = 20f;     // seconds without an update before a crack goes (the game: 400 ticks)

        struct Particle { public Vector3 Pos, Vel; public float Age, Life, Size; public Vector2 Uv; public int Layer; public Color32 Color; }

        sealed class Crack { public GameObject Go; public MeshRenderer Renderer; public int Stage; public float Seen; }

        readonly Dictionary<Vector3Int, Crack> _cracks = new Dictionary<Vector3Int, Crack>();
        readonly List<Vector3Int> _expired = new List<Vector3Int>();
        readonly Particle[] _particles = new Particle[MaxParticles];
        int _count;
        Mesh _mesh;
        GameObject _meshGo;
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Vector3> _uvs = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<Vector3> _normals = new List<Vector3>();
        readonly List<int> _tris = new List<int>();
        uint _seed = 12345;
        public int Particles => _count;
        public int Cracks => _cracks.Count;

        float Rand() { _seed = _seed * 1664525u + 1013904223u; return (_seed >> 8) / 16777216f; }

        public void Apply(in GameAction a)
        {
            if (Model == null || !Model.Loaded) return;
            switch (a.Kind)
            {
                case ActionKind.Dig:
                    if (a.Stage < 0) RemoveCrack(a.Pos); else SetCrack(a.Pos, a.Stage);
                    break;
                case ActionKind.Break:
                    RemoveCrack(a.Pos);
                    Burst(a.Pos, a.Block, 4, 1f);   // ParticleEngine.destroy: a 4x4x4 grid
                    break;
                case ActionKind.Place:
                    Burst(a.Pos, a.Block, 2, 0.35f);
                    break;
            }
        }

        /// <summary>Region-local block centre in Blocks-transform units (Z mirrored).</summary>
        Vector3 Local(Vector3 world) { var l = world - (Vector3)Model.Origin; return new Vector3(l.x, l.y, -l.z); }

        void SetCrack(Vector3Int pos, int stage)
        {
            if (!_cracks.TryGetValue(pos, out var c))
            {
                c = new Crack { Go = new GameObject($"crack {pos.x},{pos.y},{pos.z}"), Stage = -1 };
                c.Go.transform.SetParent(transform, false);
                c.Go.AddComponent<MeshFilter>().sharedMesh = EntityMeshes.Unit;
                c.Renderer = c.Go.AddComponent<MeshRenderer>();
                EntityFigure.Quiet(c.Renderer);
                _cracks[pos] = c;
            }
            c.Seen = Time.unscaledTime;
            // Inflated a little so the cracks win the depth test against the block's own faces.
            c.Go.transform.localPosition = Local(new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f));
            c.Go.transform.localScale = Vector3.one * (1.004f / 16f);
            if (c.Stage == stage) return;
            c.Stage = stage;
            var tex = Textures.Get("block/destroy_stage_" + stage);
            c.Renderer.sharedMaterial = Textures.Material(tex, 2);
            c.Go.SetActive(tex != null);
        }

        void RemoveCrack(Vector3Int pos)
        {
            if (!_cracks.TryGetValue(pos, out var c)) return;
            Destroy(c.Go);
            _cracks.Remove(pos);
        }

        /// <summary>n^3 particles over the block (TerrainParticle-like): texture from the block's side face in the atlas.</summary>
        void Burst(Vector3Int pos, string state, int n, float speed)
        {
            int layer = -1;
            Color32 color = new Color32(255, 255, 255, 255);
            if (Atlas != null && !string.IsNullOrEmpty(state))
            {
                var f = Atlas.Face(state, 4);   // north side; main thread: the state was in the region's palette, so it's cached
                layer = f.Layer;
                color = f.Color;
                if (Atlas.Array == null || layer >= Atlas.Array.depth) layer = -1;
                if (layer < 0) color = f.Color;
            }
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    for (int k = 0; k < n; k++)
                    {
                        if (_count >= MaxParticles) return;
                        float fx = (i + 0.5f) / n, fy = (j + 0.5f) / n, fz = (k + 0.5f) / n;
                        var p = new Particle
                        {
                            Pos = new Vector3(pos.x + fx, pos.y + fy, pos.z + fz),
                            Vel = new Vector3((fx - 0.5f) * 2f + (Rand() - 0.5f) * 0.8f, (fy - 0.5f) * 2f + Rand() * 1.6f + 1f, (fz - 0.5f) * 2f + (Rand() - 0.5f) * 0.8f) * speed * 2f,
                            Life = 0.2f + Rand() * 0.8f,
                            Size = (Rand() * 0.5f + 0.5f) * 0.1f,
                            Uv = new Vector2(Mathf.Floor(Rand() * 4) / 4f, Mathf.Floor(Rand() * 4) / 4f),
                            Layer = layer, Color = color,
                        };
                        _particles[_count++] = p;
                    }
        }

        void Update()
        {
            float now = Time.unscaledTime;
            _expired.Clear();
            foreach (var kv in _cracks) if (now - kv.Value.Seen > CrackTimeout) _expired.Add(kv.Key);
            foreach (var k in _expired) RemoveCrack(k);

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            for (int i = 0; i < _count;)
            {
                ref var p = ref _particles[i];
                p.Age += dt;
                if (p.Age >= p.Life) { _particles[i] = _particles[--_count]; continue; }
                p.Vel.y -= Gravity * dt;
                p.Vel *= Mathf.Pow(0.98f, dt * 20f);
                p.Pos += p.Vel * dt;
                i++;
            }
            DrawParticles();
        }

        void DrawParticles()
        {
            if (_mesh == null)
            {
                _meshGo = new GameObject("particles", typeof(MeshFilter), typeof(MeshRenderer));
                _meshGo.transform.SetParent(transform, false);
                _mesh = new Mesh { name = "particles", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _mesh.MarkDynamic();
                _meshGo.GetComponent<MeshFilter>().sharedMesh = _mesh;
                var r = _meshGo.GetComponent<MeshRenderer>();
                r.sharedMaterial = BlocksMaterial;
                EntityFigure.Quiet(r);
            }
            _meshGo.SetActive(_count > 0);
            if (_count == 0 || Model == null || !Model.Loaded) return;
            var cam = Camera.main;
            if (cam == null) return;
            // Camera axes in the Blocks transform's space (rotation only; local units are blocks).
            Vector3 right = transform.InverseTransformDirection(cam.transform.right).normalized, up = transform.InverseTransformDirection(cam.transform.up).normalized;
            Vector3 normal = transform.InverseTransformDirection(Vector3.up).normalized;
            _verts.Clear(); _uvs.Clear(); _colors.Clear(); _normals.Clear(); _tris.Clear();
            for (int i = 0; i < _count; i++)
            {
                var p = _particles[i];
                var c = Local(p.Pos);
                Vector3 r = right * p.Size, u = up * p.Size;
                int b = _verts.Count;
                _verts.Add(c - r - u); _verts.Add(c - r + u); _verts.Add(c + r + u); _verts.Add(c + r - u);
                float u0 = p.Uv.x, v0 = p.Uv.y, s = 0.25f;
                _uvs.Add(new Vector3(u0, v0, p.Layer)); _uvs.Add(new Vector3(u0, v0 + s, p.Layer)); _uvs.Add(new Vector3(u0 + s, v0 + s, p.Layer)); _uvs.Add(new Vector3(u0 + s, v0, p.Layer));
                for (int k = 0; k < 4; k++) { _colors.Add(p.Color); _normals.Add(normal); }
                _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2); _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
                _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 1); _tris.Add(b); _tris.Add(b + 3); _tris.Add(b + 2);   // both sides
            }
            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetColors(_colors);
            _mesh.SetNormals(_normals);
            _mesh.SetTriangles(_tris, 0, false);
            _mesh.RecalculateBounds();
        }

        /// <summary>A new region (snapshot) moves block coordinates: drop the cracks; particles keep their world positions.</summary>
        public void OnRegionChanged()
        {
            foreach (var c in _cracks.Values) Destroy(c.Go);
            _cracks.Clear();
        }

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); }
    }
}
