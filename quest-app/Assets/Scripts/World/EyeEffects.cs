// Applies "eye in a fluid" on the headset: each frame it maps the centre eye into block space (the Blocks transform,
// Z mirrored back to the Minecraft frame), lets EyeMediumFilter decide the medium (air, water, lava, powder snow,
// inside an opaque block) and then:
//   - sets the fog globals read by TabletopFog.cginc in the block and player shaders (distances in blocks x the
//     block size in metres, so the fog scales with the miniature),
//   - tints the passthrough with a box around the head drawn behind the miniature (EyeOverlay.shader),
//   - shows the water's inside faces (TabletopRenderer.Underwater) while the eye is in water, so the surface and the
//     region's cut walls are visible from below/inside.
// Logs "[Tabletop] EYE <medium>" when the medium changes.
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class EyeEffects : MonoBehaviour
    {
        public Transform Head, Blocks;
        public WorldModel Model;
        public TabletopRenderer BlockRenderer;
        public ClipCaps Caps;   // nav's cut sides of the visible window (their water gets inside faces too)

        public EyeMedium Medium => _filter.Medium;

        readonly EyeMediumFilter _filter = new EyeMediumFilter();
        MeshRenderer _overlay;
        Material _overlayMat;
        static readonly int FogColorId = Shader.PropertyToID("_TabletopFogColor"), FogParamsId = Shader.PropertyToID("_TabletopFogParams"),
                            FogEyeId = Shader.PropertyToID("_TabletopFogEye"), TintId = Shader.PropertyToID("_Tint"),
                            ClipId = Shader.PropertyToID("_TabletopClip");

        void Start()
        {
            var mat = Resources.Load<Material>("TabletopEyeOverlay");
            if (mat == null) { Debug.LogWarning("[Tabletop] EYE overlay material missing; fog only"); return; }
            _overlayMat = new Material(mat);
            var go = new GameObject("EyeOverlay", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(Head, false);
            go.GetComponent<MeshFilter>().sharedMesh = HeadBox(0.5f);
            _overlay = go.GetComponent<MeshRenderer>();
            _overlay.sharedMaterial = _overlayMat;
            _overlay.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _overlay.receiveShadows = false;
            _overlay.enabled = false;
        }

        void LateUpdate()
        {
            if (!Head || !Blocks || Model == null) return;
            float mpb = Mathf.Abs(Blocks.lossyScale.x);          // metres per block
            var l = Blocks.InverseTransformPoint(Head.position);  // Unity block space, Z mirrored
            // Blocks outside nav's visible window are clipped away (TabletopClip.cginc; a zero matrix means no clip): an eye
            // there is in air, whatever the loaded region holds.
            var c = Shader.GetGlobalMatrix(ClipId).MultiplyPoint3x4(Head.position);
            bool visible = Mathf.Max(Mathf.Abs(c.x), Mathf.Max(Mathf.Abs(c.y), Mathf.Abs(c.z))) <= 1f;
            var look = _filter.Update(visible ? Model : null, new Vector3(l.x, l.y, -l.z), mpb, Time.unscaledDeltaTime);
            if (_filter.Changed) Debug.Log($"[Tabletop] EYE {_filter.Medium}");
            bool under = _filter.Medium == EyeMedium.Water;
            if (BlockRenderer) BlockRenderer.Underwater = under;
            if (Caps) Caps.Underwater = under;

            float start = look.Start * mpb, end = Mathf.Max(look.End * mpb, start + 1e-4f);
            Shader.SetGlobalVector(FogColorId, Render(look.Fog, 1f));
            Shader.SetGlobalVector(FogParamsId, new Vector4(start, 1f / (end - start), look.Strength, 0f));
            Shader.SetGlobalVector(FogEyeId, Head.position);
            BlockKeywords.Set(BlockKeywords.Fog, look.Strength > 0f);   // [gpu] the block shaders' fog only while there is some
            if (_overlay)
            {
                bool on = look.TintAlpha > 0.002f;
                _overlay.enabled = on;
                if (on) _overlayMat.SetVector(TintId, Render(look.Tint, look.TintAlpha));
            }
        }

        /// <summary>sRGB colour to the render colour space (vectors aren't converted by Unity, colours would be).</summary>
        static Vector4 Render(Rgb c, float a)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                return new Vector4(Mathf.GammaToLinearSpace(c.R), Mathf.GammaToLinearSpace(c.G), Mathf.GammaToLinearSpace(c.B), a);
            return new Vector4(c.R, c.G, c.B, a);
        }

        /// <summary>A closed box around the head (half size h, metres); drawn from inside (the shader has Cull Off).</summary>
        static Mesh HeadBox(float h)
        {
            var m = new Mesh { name = "EyeOverlayBox" };
            m.vertices = new[]
            {
                new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h),
                new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h),
            };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * (4 * h));   // generous: never culled while it surrounds the eye
            return m;
        }

        void OnDestroy()
        {
            Shader.SetGlobalVector(FogParamsId, Vector4.zero);
            BlockKeywords.Set(BlockKeywords.Fog, false);
            if (_overlayMat) Destroy(_overlayMat);
        }
    }
}
