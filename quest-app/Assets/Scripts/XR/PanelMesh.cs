// [talk] Reused vertex lists for world-space panels drawn with Sprites/Default (the operator panel, chat bubbles): no
// uGUI, no per-frame garbage beyond the mesh upload. The same technique as XR/BlockMenu.cs's private builder.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class PanelMesh
    {
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Color32> _c = new List<Color32>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<int> _t = new List<int>();

        // Vertex colours reach Sprites/Default unconverted; the project renders in linear space and these are sRGB.
        static readonly bool Linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
        public static Color32 Lin(Color32 c) => Linear ? (Color32)((Color)c).linear : c;

        public void Clear() { _v.Clear(); _c.Clear(); _uv.Clear(); _t.Clear(); }

        /// <summary>a b c d clockwise from the top-left as the viewer sees them.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 col)
        {
            col = Lin(col);
            int n = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) { _c.Add(col); _uv.Add(new Vector2(0.5f, 0.5f)); }
            _t.Add(n); _t.Add(n + 1); _t.Add(n + 2); _t.Add(n); _t.Add(n + 2); _t.Add(n + 3);
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color32 col)
        {
            col = Lin(col);
            int n = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            for (int k = 0; k < 3; k++) { _c.Add(col); _uv.Add(new Vector2(0.5f, 0.5f)); }
            _t.Add(n); _t.Add(n + 1); _t.Add(n + 2);
        }

        public void Apply(Mesh m)
        {
            m.Clear();
            m.SetVertices(_v); m.SetColors(_c); m.SetUVs(0, _uv); m.SetTriangles(_t, 0, true);
        }

        /// <summary>A child object with an empty dynamic mesh and the material; returns the mesh.</summary>
        public static Mesh Child(Transform parent, string name, Material mat)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return mesh;
        }

        public static Material SpriteMat(int queue) => new Material(Shader.Find("Sprites/Default")) { renderQueue = queue, mainTexture = Texture2D.whiteTexture };
    }
}
