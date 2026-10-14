// Break and place blocks in the miniature from the headset, Minecraft style (docs/QUEST_SETUP.md "Editing blocks").
// A ray from the right controller (BlockRaycast, DDA in block space) targets a block; a thin outline shows it, like
// vanilla's selection box, and a short laser runs from the controller to the hit point. Bindings, active while area
// setup is off (it owns the trigger and B then):
//   Right trigger   break the targeted block (hold to repeat)
//   Left trigger    place the selected block against the targeted face (hold to repeat)
//   B (right)       pick the targeted block as the selected block (middle click)
// The selected block floats above the right controller as a small textured cube with its name. Edits go out as
// block_edit (PROTOCOL.md 4.17) and are shown at once when the result is already in the palette (BlockEdits.cs
// reconciles them with the server's block_update / error). A short haptic tick confirms each edit.
using System;
using System.Collections.Generic;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class BlockEditMode : MonoBehaviour
    {
        public Transform Blocks;      // TabletopRenderer's transform: block space, scaled to block_size_m, Z mirrored
        public Transform RightHand, Head;
        public Func<WorldModel> World;
        public ResourcePackAtlas Atlas;
        public Material OpaqueMaterial, TransparentMaterial;
        public Action<Dictionary<string, object>> Send;
        public Func<bool> Online;     // link up (edits are dropped otherwise)
        public Func<bool> Blocked;    // another mode owns the controllers (area setup)
        public float MaxRayM = 2f;
        public float RepeatSeconds = 0.25f, FirstRepeatSeconds = 0.35f;

        public readonly BlockEditor Editor = new BlockEditor();
        public BlockHit Target { get; private set; }

        /// <summary>Status line for the HUD text.</summary>
        public string Status =>
            !Editor.ServerAllows ? "EDIT off" + (Editor.Result.Length > 0 ? ": " + Editor.Result : " (server)")
            : $"EDIT {BlockEditRules.Label(Editor.Selected)}  R-trig break  L-trig place  B pick" + (Editor.Result.Length > 0 ? "\n" + Editor.Result : "");

        LineRenderer _outline, _laser;
        Transform _preview;
        Mesh _previewMesh;
        TextMesh _label;
        string _previewState;
        float _breakAt = -1, _placeAt = -1, _hapticOff = -1;
        readonly Vector3[] _box = new Vector3[16];
        readonly List<Vector3> _verts = new List<Vector3>();
        static readonly Color OutlineColor = new Color(0.02f, 0.02f, 0.02f, 0.85f);

        void Start()
        {
            var lineMat = new Material(Shader.Find("Sprites/Default"));
            _outline = NewLine("BlockOutline", lineMat, 16);
            _outline.startColor = _outline.endColor = OutlineColor;
            _laser = NewLine("EditLaser", lineMat, 2);
            _laser.startColor = new Color(1f, 1f, 1f, 0.15f); _laser.endColor = new Color(1f, 1f, 1f, 0.7f);
            _laser.startWidth = 0.0025f; _laser.endWidth = 0.0008f;

            _preview = new GameObject("SelectedBlock", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            if (RightHand) _preview.SetParent(RightHand, false);
            _preview.localPosition = new Vector3(0f, 0.045f, 0.03f);
            _preview.localScale = Vector3.one * 0.025f;
            _previewMesh = new Mesh();
            _preview.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
            _preview.GetComponent<MeshRenderer>().sharedMaterials = new[] { OpaqueMaterial, TransparentMaterial };
            var label = new GameObject("SelectedLabel");
            label.transform.SetParent(RightHand ? RightHand : transform, false);
            label.transform.localPosition = new Vector3(0f, 0.075f, 0.03f);
            _label = label.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(_label);
            _label.characterSize = 0.0022f; _label.fontSize = 48; _label.anchor = TextAnchor.LowerCenter; _label.color = Color.white;
        }

        LineRenderer NewLine(string name, Material mat, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true; l.positionCount = points; l.loop = false; l.material = mat;
            l.numCapVertices = 0; l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; l.receiveShadows = false;
            go.SetActive(false);
            return l;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch); OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch); }
            var w = World?.Invoke();
            Editor.Tick(w, Time.unscaledTimeAsDouble);
            // Nothing to aim with until the server grants edit_v1 (the status line says why), or while area setup runs.
            bool active = Editor.ServerAllows && w != null && w.Loaded && RightHand && Blocks && !(Blocked?.Invoke() ?? false);
            ShowPreview(active);
            if (!active) { Target = default; _outline.gameObject.SetActive(false); _laser.gameObject.SetActive(false); _breakAt = _placeAt = -1; return; }

            // The controller ray in block space (InverseTransformVector includes the block scale: length in blocks).
            Vector3 o = BlockRaycast.FromBlocksLocal(Blocks.InverseTransformPoint(RightHand.position));
            Vector3 d = BlockRaycast.FromBlocksLocal(Blocks.InverseTransformVector(RightHand.forward * MaxRayM));
            Target = BlockRaycast.Cast(w, o, d, d.magnitude);
            Draw(Target);

            // Right trigger = break, left trigger = place; press acts at once, holding repeats.
            bool online = Online?.Invoke() ?? true;
            if (Held(OVRInput.Controller.RTouch, ref _breakAt, now) && Target.Hit)
            {
                if (online) Do(Editor.Break(w, Target, Time.unscaledTimeAsDouble), OVRInput.Controller.RTouch);
                else Editor.Result = "not connected";
            }
            if (Held(OVRInput.Controller.LTouch, ref _placeAt, now) && Target.Hit)
            {
                if (online) Do(Editor.Place(w, Target, d, Time.unscaledTimeAsDouble), OVRInput.Controller.LTouch);
                else Editor.Result = "not connected";
            }
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) && Editor.Pick(w, Target)) Haptic(OVRInput.Controller.RTouch, 0.15f);
        }

        /// <summary>True on the trigger press, then every RepeatSeconds while it stays held.</summary>
        bool Held(OVRInput.Controller c, ref float next, float now)
        {
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, c)) { next = now + FirstRepeatSeconds; return true; }
            if (next < 0 || !OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, c)) { next = -1; return false; }
            if (now < next) return false;
            next = now + RepeatSeconds;
            return true;
        }

        void Do(Dictionary<string, object> msg, OVRInput.Controller c)
        {
            if (msg == null) return;   // refused locally (Editor.Result says why)
            Send?.Invoke(msg);
            Haptic(c, 0.3f);
        }

        void Haptic(OVRInput.Controller c, float amplitude)
        {
            OVRInput.SetControllerVibration(0.6f, amplitude, c);
            _hapticOff = Time.unscaledTime + 0.04f;
        }

        void Draw(BlockHit h)
        {
            _outline.gameObject.SetActive(h.Hit);
            _laser.gameObject.SetActive(h.Hit);
            if (!h.Hit) return;
            // Selection box: the cell grown by 1/64 block so it doesn't z-fight the faces (vanilla grows it by 0.002).
            const float e = 1f / 64;
            Vector3 a = new Vector3(h.Block.x - e, h.Block.y - e, h.Block.z - e), b = new Vector3(h.Block.x + 1 + e, h.Block.y + 1 + e, h.Block.z + 1 + e);
            int i = 0;
            void P(float x, float y, float z) => _box[i++] = Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(new Vector3(x, y, z)));
            P(a.x, a.y, a.z); P(b.x, a.y, a.z); P(b.x, a.y, b.z); P(a.x, a.y, b.z); P(a.x, a.y, a.z);
            P(a.x, b.y, a.z); P(b.x, b.y, a.z); P(b.x, a.y, a.z); P(b.x, b.y, a.z); P(b.x, b.y, b.z); P(b.x, a.y, b.z);
            P(b.x, b.y, b.z); P(a.x, b.y, b.z); P(a.x, a.y, b.z); P(a.x, b.y, b.z); P(a.x, b.y, a.z);
            _outline.SetPositions(_box);
            float bs = Blocks.lossyScale.x;
            _outline.widthMultiplier = Mathf.Clamp(bs * 0.08f, 0.0006f, 0.003f);
            _laser.SetPosition(0, RightHand.position);
            _laser.SetPosition(1, Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(h.Point)));
        }

        void ShowPreview(bool active)
        {
            _preview.gameObject.SetActive(active);
            _label.gameObject.SetActive(active);
            if (!active) return;
            if (_previewState != Editor.Selected) BuildPreview(Editor.Selected);
            _preview.localRotation = Quaternion.Euler(20f, Time.unscaledTime * 30f % 360f, 0f);
            if (Head) _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - Head.position, Vector3.up);
        }

        /// <summary>The selected block as a one-block world through the real mesher: textures and models like the miniature.</summary>
        void BuildPreview(string state)
        {
            _previewState = state;
            _label.text = BlockEditRules.Label(state);
            if (Atlas != null)
            {
                // Main thread: resolve the state's textures/model (it may be new to the atlas), as TabletopRenderer.Prewarm does.
                for (int dir = 0; dir < 6; dir++) Atlas.Face(state, dir);
                Atlas.Commit();
                if (Atlas.Array != null)
                    foreach (var m in new[] { OpaqueMaterial, TransparentMaterial }) { m.SetTexture("_Blocks", Atlas.Array); m.SetFloat("_HasTex", 1); }
            }
            var one = new WorldModel { SnapshotId = 0, World = "preview", Dims = new Vector3Int(1, 1, 1) };
            one.Palette.Add(BlockEditRules.Air); one.Palette.Add(state);
            one.Grid = new ushort[] { 1 };
            IFaceSource faces = Atlas;
            var data = new MeshData();
            if (faces != null) { GreedyMesher.Build(one, Vector3Int.zero, faces, data); GreedyMesher.BuildSpecials(one, Vector3Int.zero, faces, data); }
            // Block space -> Unity (mirror Z, flip winding), centred on the cell.
            _verts.Clear();
            foreach (var v in data.Verts) _verts.Add(new Vector3(v.x - 0.5f, v.y - 0.5f, 0.5f - v.z));
            _previewMesh.Clear();
            _previewMesh.SetVertices(_verts);
            _previewMesh.SetUVs(0, data.Uvs);
            _previewMesh.SetColors(data.Colors);
            _previewMesh.subMeshCount = 2;
            _previewMesh.SetTriangles(Flip(data.Opaque), 0, false);
            _previewMesh.SetTriangles(Flip(data.Transparent), 1, false);
            _previewMesh.RecalculateNormals();
            _previewMesh.RecalculateBounds();
        }

        static List<int> Flip(List<int> t)
        {
            var r = new List<int>(t.Count);
            for (int i = 0; i + 2 < t.Count; i += 3) { r.Add(t[i]); r.Add(t[i + 2]); r.Add(t[i + 1]); }
            return r;
        }

        void OnDestroy() { if (_previewMesh) Destroy(_previewMesh); }
    }
}
