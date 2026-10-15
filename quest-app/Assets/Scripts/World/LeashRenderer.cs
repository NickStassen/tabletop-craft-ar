// [living] Lead ropes and fishing lines on the table (PROTOCOL.md "Living entities": leash, owner; Leash.cs), rebuilt
// every frame from where the entities and players are shown, so a rope follows a walking mob and its holder smoothly.
// One dynamic mesh, vertex coloured (vanilla's brown two-tone rope, lit by the light at both ends; black lines), drawn
// with TabletopCraft/Lines, which hides it outside the visible window like everything else.
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class LeashRenderer : MonoBehaviour
    {
        public EntityRenderer Entities;
        public PlayerMarkers Players;
        public EntityTextures Textures;
        /// <summary>Thinnest rope and line on the table in metres (vanilla's 0.05-block rope is a hair at 256 blocks wide).</summary>
        public float MinRopeM = 0.0012f, MinLineM = 0.0006f;

        Mesh _mesh;
        MeshRenderer _renderer;
        readonly List<Vector3> _world = new List<Vector3>(), _verts = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        readonly List<int> _tris = new List<int>();
        public int Ropes { get; private set; }
        public int Lines { get; private set; }

        void Start()
        {
            var go = new GameObject("leads and lines");
            go.transform.SetParent(transform, false);
            _mesh = new Mesh { name = "leads" };
            _mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = Textures.LinesOpaque;
            EntityFigure.Quiet(_renderer);
        }

        void LateUpdate()
        {
            if (Entities == null || _mesh == null) return;
            _world.Clear(); _colors.Clear(); _tris.Clear();
            Ropes = Lines = 0;
            float bs = Mathf.Max(Entities.BlockSizeM, 1e-4f);
            float thick = Mathf.Max(1f, MinRopeM / (Leash.Width * bs));
            foreach (var e in Entities.ShownInfos)
            {
                if (e.Leash >= 0) AddLeash(e, thick);
                if (e.Owner >= 0 && e.Path == "fishing_bobber") AddLine(e, Mathf.Max(0.02f, MinLineM / bs));
            }
            _verts.Clear();
            foreach (var p in _world) _verts.Add(Entities.ToLocal(p));
            _mesh.Clear();
            if (_verts.Count == 0) { _renderer.enabled = false; return; }
            _renderer.enabled = true;
            _mesh.SetVertices(_verts);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0, true);
        }

        void AddLeash(EntityInfo e, float thick)
        {
            if (!Entities.Pose(e.Id, out var pos, out float body, out _)) return;
            float eye = e.Eye > 0 ? e.Eye : 0.85f * e.Height;
            Vector3 end; EntityInfo holder = null; Vector3 hpos = default; float hbody = 0;
            if (Entities.Pose(e.Leash, out hpos, out hbody, out holder))
            {
                float heye = holder.Eye > 0 ? holder.Eye : 0.85f * holder.Height;
                if (Leash.QuadHolder(holder.Type) && Leash.QuadLeashed(e.Type))
                {
                    // A happy ghast towing a boat or a camel: 4 taut ropes between the corners (EntityRenderer quad leash).
                    var from = Leash.QuadLeashedOffsets(e.Type, e.Width, e.Height);
                    var to = Leash.QuadHolderOffsets(holder.Width, holder.Height);
                    for (int i = 0; i < 4; i++)
                        Rope(pos + Leash.Turn(from[i], body), hpos + Leash.Turn(to[i], hbody), false, pos + Vector3.up * eye, hpos + Vector3.up * heye, thick);
                    return;
                }
                end = Leash.Hold(holder.Type, hpos, heye, holder.Height, hbody);
                Rope(pos + Leash.Turn(Leash.Offset(e.Type, eye, e.Width, e.Height), body), end, true, pos + Vector3.up * eye, hpos + Vector3.up * heye, thick);
            }
            else if (Players != null && Players.Hold(e.Leash, false, out end))
                Rope(pos + Leash.Turn(Leash.Offset(e.Type, eye, e.Width, e.Height), body), end, true, pos + Vector3.up * eye, end, thick);
        }

        void Rope(Vector3 start, Vector3 end, bool slack, Vector3 lightFrom, Vector3 lightTo, float thick)
        {
            Entities.LightLevels(lightFrom, out int s0, out int b0);
            Entities.LightLevels(lightTo, out int s1, out int b1);
            int first = _world.Count;
            Leash.Rope(start, end, slack, t => BlockShading.LightmapLive((int)Mathf.Lerp(s0, s1, t), (int)Mathf.Lerp(b0, b1, t)), _world, _colors, _tris, thick);
            if (_world.Count > first) Ropes++;
        }

        void AddLine(EntityInfo bobber, float width)
        {
            if (Players == null || !Entities.Pose(bobber.Id, out var pos, out _, out _) || !Players.Hold(bobber.Owner, true, out var hand)) return;
            var pts = Leash.FishingLine(pos, hand);
            var black = new Color32(0, 0, 0, 255);
            for (int k = 0; k + 1 < pts.Length; k++)
            {
                // Two crossed ribbons per segment, like the lead, so the line reads from any side.
                var a = pts[k]; var b = pts[k + 1];
                var dir = (b - a).normalized;
                var side = Vector3.Cross(dir, Vector3.up);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
                side = side.normalized * (width * 0.5f);
                var up = Vector3.Cross(side, dir).normalized * (width * 0.5f);
                Quad(a - side, a + side, b + side, b - side, black);
                Quad(a - up, a + up, b + up, b - up, black);
            }
            Lines++;
        }

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 col)
        {
            int s = _world.Count;
            _world.Add(a); _world.Add(b); _world.Add(c); _world.Add(d);
            for (int i = 0; i < 4; i++) _colors.Add(col);
            _tris.Add(s); _tris.Add(s + 1); _tris.Add(s + 2); _tris.Add(s); _tris.Add(s + 2); _tris.Add(s + 3);
        }

        void OnDestroy() { if (_mesh != null) Destroy(_mesh); }
    }
}
