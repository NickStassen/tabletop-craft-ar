// Break and place blocks in the miniature from the headset, Minecraft style (docs/QUEST_SETUP.md "Editing blocks").
// A ray from the right controller (BlockRaycast, DDA in block space) targets a block; a thin outline shows it, like
// vanilla's selection box, and a short laser runs from the controller to the hit point (a short faded stub when the
// ray hits nothing). A faint ghost of the block the left trigger would place, oriented as it will be (BlockPlacement:
// wall torches, top slabs, door halves...), sits in the cell it fills, inside a second outline; both turn red when the
// placement would be refused. Bindings, active while area setup is off (it owns the trigger and B then):
//   Right trigger   break the targeted block (hold to repeat)
//   Left trigger    place the selected block (hold to repeat)
//   B (right)       pick the targeted block as the selected block (middle click)
// The selected block floats above the right controller as a small textured cube with its name; under it, the targeted
// block's name and world position ("oak stairs @ 105,64,-35"). Edits go out as block_edit (PROTOCOL.md 4.20) and are
// shown at once when the result is already in the palette (BlockEdits.cs reconciles them with the server's
// block_update / error). A short haptic tick confirms each edit.
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
        public NavState Nav;          // visible window inside the region (navigation); null = the whole region is shown
        public Action ModelChanged;   // an optimistic edit or its revert changed the grid (clip caps re-slice)
        public Action<string> Picked; // [menu] B picked this state (the block menu puts it into its hotbar)
        public float MaxRayM = 2f;
        public float RepeatSeconds = 0.25f, FirstRepeatSeconds = 0.35f;
        /// <summary>Length of the faded laser stub shown when the ray hits nothing (metres).</summary>
        public float MissLaserM = 0.12f;
        /// <summary>Opacity of the placement ghost.</summary>
        public float GhostAlpha = 0.4f;

        public readonly BlockEditor Editor = new BlockEditor();
        public BlockHit Target { get; private set; }
        /// <summary>What the left trigger would place now (cell, oriented state, or why not).</summary>
        public PlacePlan Placement { get; private set; }

        /// <summary>Status line for the HUD text.</summary>
        public string Status =>
            !Editor.ServerAllows ? "EDIT off" + (Editor.Result.Length > 0 ? ": " + Editor.Result : " (server)")
            : $"EDIT {BlockEditRules.Label(Editor.Selected)}  R-trig break  L-trig place  B pick" + (Editor.Result.Length > 0 ? "\n" + Editor.Result : "");

        LineRenderer _outline, _laser, _placeOutline;
        Transform _preview, _ghost;
        Mesh _previewMesh, _ghostMesh;
        Material _ghostMat;
        TextMesh _label, _target;
        string _previewState, _ghostKey, _targetText;
        float _breakAt = -1, _placeAt = -1, _hapticOff = -1, _planAt = -1;
        int _seenChanges;
        Vector3 _lo, _hi;   // the box targeting is limited to (region-local blocks)
        readonly Vector3[] _box = new Vector3[16], _placeBox = new Vector3[16];
        readonly List<Vector3> _verts = new List<Vector3>();
        readonly List<Color32> _colors = new List<Color32>();
        static readonly Color OutlineColor = new Color(0.02f, 0.02f, 0.02f, 0.85f);
        static readonly Color PlaceOkColor = new Color(1f, 1f, 1f, 0.35f), PlaceBadColor = new Color(1f, 0.15f, 0.1f, 0.8f);
        static readonly Color HitLaserStart = new Color(1f, 1f, 1f, 0.15f), HitLaserEnd = new Color(1f, 1f, 1f, 0.7f);
        static readonly Color MissLaserStart = new Color(1f, 1f, 1f, 0.3f), MissLaserEnd = new Color(1f, 1f, 1f, 0f);

        void Start()
        {
            var lineMat = new Material(Shader.Find("Sprites/Default"));
            _outline = NewLine("BlockOutline", lineMat, 16);
            _outline.startColor = _outline.endColor = OutlineColor;
            _placeOutline = NewLine("PlaceOutline", lineMat, 16);
            _laser = NewLine("EditLaser", lineMat, 2);
            _laser.startWidth = 0.0025f; _laser.endWidth = 0.0008f;

            _preview = new GameObject("SelectedBlock", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            if (RightHand) _preview.SetParent(RightHand, false);
            _preview.localPosition = new Vector3(0f, 0.045f, 0.03f);
            _preview.localScale = Vector3.one * 0.025f;
            _previewMesh = new Mesh();
            _preview.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
            _preview.GetComponent<MeshRenderer>().sharedMaterials = new[] { OpaqueMaterial, TransparentMaterial };
            _label = NewText("SelectedLabel", new Vector3(0f, 0.075f, 0.03f), 0.0022f, TextAnchor.LowerCenter, Color.white);
            _target = NewText("TargetLabel", new Vector3(0f, 0.028f, 0.03f), 0.0016f, TextAnchor.UpperCenter, new Color(1f, 1f, 1f, 0.75f));

            // The placement ghost: block-space mesh under Blocks (Z mirrored like the terrain), drawn see-through with
            // its own copy of the transparent block material.
            _ghost = new GameObject("PlaceGhost", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            if (Blocks) _ghost.SetParent(Blocks, false);
            _ghostMesh = new Mesh();
            _ghost.GetComponent<MeshFilter>().sharedMesh = _ghostMesh;
            _ghostMat = TransparentMaterial ? new Material(TransparentMaterial) : null;
            var gr = _ghost.GetComponent<MeshRenderer>();
            gr.sharedMaterial = _ghostMat;
            gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; gr.receiveShadows = false;
            _ghost.gameObject.SetActive(false);
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

        TextMesh NewText(string name, Vector3 local, float size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(RightHand ? RightHand : transform, false);
            go.transform.localPosition = local;
            var t = go.AddComponent<TextMesh>();
            TabletopApp.SetBuiltinFont(t);
            t.characterSize = size; t.fontSize = 48; t.anchor = anchor; t.color = color;
            return t;
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
            if (!active)
            {
                Target = default; Placement = default;
                _outline.gameObject.SetActive(false); _laser.gameObject.SetActive(false); _placeOutline.gameObject.SetActive(false);
                _ghost.gameObject.SetActive(false); SetTargetText("");
                _breakAt = _placeAt = -1;
                return;
            }

            // Only what's on the table can be targeted: the visible window when navigating (the margin is clipped away).
            _lo = Vector3.zero; _hi = w.Dims;
            if (Nav != null && Nav.Loaded && Nav.Origin == w.Origin) { _lo = Nav.WindowMin - (Vector3)w.Origin; _hi = _lo + (Vector3)Nav.Window; }
            // The controller ray in block space (InverseTransformVector includes the block scale: length in blocks).
            Vector3 o = BlockRaycast.FromBlocksLocal(Blocks.InverseTransformPoint(RightHand.position));
            Vector3 d = BlockRaycast.FromBlocksLocal(Blocks.InverseTransformVector(RightHand.forward * MaxRayM));
            var prev = Target;
            Target = BlockRaycast.Cast(w, o, d, d.magnitude, _lo, _hi);
            // What the left trigger would do: re-planned when the aim changes, and a few times a second for the rest
            // (look direction, neighbours, server updates).
            if (!Same(prev, Target) || now >= _planAt) { Placement = Editor.Plan(w, Target, d, Time.unscaledTimeAsDouble); _planAt = now + 0.1f; }
            Draw(Target);
            DrawPlacement(Placement, Target.Hit);
            SetTargetText(BlockEditor.Describe(w, Target));

            // Right trigger = break, left trigger = place; press acts at once, holding repeats.
            bool online = Online?.Invoke() ?? true;
            if (Held(OVRInput.Controller.RTouch, ref _breakAt, now) && Target.Hit)
            {
                if (online) Do(Editor.Break(w, Target, Time.unscaledTimeAsDouble), OVRInput.Controller.RTouch);
                else Editor.Result = "not connected";
                _planAt = -1;
            }
            if (Held(OVRInput.Controller.LTouch, ref _placeAt, now) && Target.Hit)
            {
                if (online) Do(Editor.Place(w, Target, d, Time.unscaledTimeAsDouble), OVRInput.Controller.LTouch);
                else Editor.Result = "not connected";
                _planAt = -1;
            }
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) && Editor.Pick(w, Target)) { Haptic(OVRInput.Controller.RTouch, 0.15f); Picked?.Invoke(Editor.Selected); _planAt = -1; }   // [menu] hotbar
        }

        static bool Same(BlockHit a, BlockHit b) =>
            a.Hit == b.Hit && a.Block == b.Block && a.Face == b.Face &&
            (a.Point.y - a.Block.y > 0.5f) == (b.Point.y - b.Block.y > 0.5f) && (a.Point.x - a.Block.x > 0.5f) == (b.Point.x - b.Block.x > 0.5f) &&
            (a.Point.z - a.Block.z > 0.5f) == (b.Point.z - b.Block.z > 0.5f);

        // After every Update (TabletopApp handles server errors there): grid changes from guesses and reverts.
        void LateUpdate()
        {
            if (Editor.Pending.Changes == _seenChanges) return;
            _seenChanges = Editor.Pending.Changes;
            _planAt = -1;
            ModelChanged?.Invoke();
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
            _laser.gameObject.SetActive(true);
            _laser.SetPosition(0, RightHand.position);
            if (!h.Hit)
            {
                // A miss: a short stub that fades out, so the aim is still visible.
                _laser.startColor = MissLaserStart; _laser.endColor = MissLaserEnd;
                _laser.SetPosition(1, RightHand.position + RightHand.forward * MissLaserM);
                return;
            }
            _laser.startColor = HitLaserStart; _laser.endColor = HitLaserEnd;
            _laser.SetPosition(1, Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(h.Point)));
            BoxLines(_outline, _box, h.Block, h.Block);
        }

        /// <summary>
        /// Selection box over cells <paramref name="a"/>..<paramref name="b"/> (inclusive): grown by 1/64 block so it
        /// doesn't z-fight the faces (vanilla grows it by 0.002), cut to the visible window like the terrain.
        /// </summary>
        void BoxLines(LineRenderer line, Vector3[] box, Vector3Int a0, Vector3Int b0)
        {
            const float e = 1f / 64;
            var lo = new Vector3Int(Math.Min(a0.x, b0.x), Math.Min(a0.y, b0.y), Math.Min(a0.z, b0.z));
            var hi = new Vector3Int(Math.Max(a0.x, b0.x), Math.Max(a0.y, b0.y), Math.Max(a0.z, b0.z));
            Vector3 a = new Vector3(Mathf.Max(lo.x, _lo.x) - e, Mathf.Max(lo.y, _lo.y) - e, Mathf.Max(lo.z, _lo.z) - e);
            Vector3 b = new Vector3(Mathf.Min(hi.x + 1, _hi.x) + e, Mathf.Min(hi.y + 1, _hi.y) + e, Mathf.Min(hi.z + 1, _hi.z) + e);
            int i = 0;
            void P(float x, float y, float z) => box[i++] = Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(new Vector3(x, y, z)));
            P(a.x, a.y, a.z); P(b.x, a.y, a.z); P(b.x, a.y, b.z); P(a.x, a.y, b.z); P(a.x, a.y, a.z);
            P(a.x, b.y, a.z); P(b.x, b.y, a.z); P(b.x, a.y, a.z); P(b.x, b.y, a.z); P(b.x, b.y, b.z); P(b.x, a.y, b.z);
            P(b.x, b.y, b.z); P(a.x, b.y, b.z); P(a.x, a.y, b.z); P(a.x, b.y, b.z); P(a.x, b.y, a.z);
            line.SetPositions(box);
            line.widthMultiplier = Mathf.Clamp(Blocks.lossyScale.x * 0.08f, 0.0006f, 0.003f);
        }

        /// <summary>The cell the left trigger fills: a faint outline and a ghost of the oriented block (red when refused).</summary>
        void DrawPlacement(PlacePlan p, bool hit)
        {
            var w = World?.Invoke();
            bool hasCell = hit && w != null && !p.Waiting && w.InBounds(p.Cell.x, p.Cell.y, p.Cell.z) && BlockRaycast.Overlaps(p.Cell, _lo, _hi) && (p.Ok || p.State != null || p.Why?.Length > 0);
            bool show = hasCell && (p.Ok || p.Why.StartsWith("can't place:"));   // not for "can't place fire" (a selection problem)
            _placeOutline.gameObject.SetActive(show);
            if (!show) { _ghost.gameObject.SetActive(false); return; }
            var color = p.Ok ? PlaceOkColor : PlaceBadColor;
            _placeOutline.startColor = _placeOutline.endColor = color;
            BoxLines(_placeOutline, _placeBox, p.Cell, p.HasPartner ? p.PartnerCell : p.Cell);
            if (!p.Ok || _ghostMat == null) { _ghost.gameObject.SetActive(false); return; }
            string key = p.State + "|" + p.PartnerState + "|" + (p.PartnerCell - p.Cell);
            if (key != _ghostKey) BuildGhost(p);
            _ghost.gameObject.SetActive(true);
        }

        /// <summary>The planned block(s) as a see-through mesh through the real mesher, placed at their cell.</summary>
        void BuildGhost(PlacePlan p)
        {
            _ghostKey = p.State + "|" + p.PartnerState + "|" + (p.PartnerCell - p.Cell);
            var one = BlockEditor.PreviewWorld(p, out var corner);
            _ghostMesh.Clear();
            if (one == null || Atlas == null) return;
            foreach (var s in one.Palette) for (int dir = 0; dir < 6; dir++) Atlas.Face(s, dir);   // main thread: new states join the atlas
            Atlas.Commit();
            if (Atlas.Array != null) { _ghostMat.SetTexture("_Blocks", Atlas.Array); _ghostMat.SetFloat("_HasTex", 1); }
            var data = new MeshData();
            IFaceSource faces = Atlas;
            GreedyMesher.Build(one, Vector3Int.zero, faces, data);
            GreedyMesher.BuildSpecials(one, Vector3Int.zero, faces, data);
            // Block space -> Blocks-local (mirror Z, flip winding); every face see-through at GhostAlpha.
            _verts.Clear(); _colors.Clear();
            foreach (var v in data.Verts) _verts.Add(new Vector3(v.x, v.y, -v.z));
            byte alpha = (byte)Mathf.Clamp(GhostAlpha * 255f, 0f, 255f);
            foreach (var c in data.Colors) _colors.Add(new Color32(c.r, c.g, c.b, (byte)(c.a * alpha / 255)));
            var tris = new List<int>(data.Opaque.Count + data.Transparent.Count);
            tris.AddRange(Flip(data.Opaque));
            tris.AddRange(Flip(data.Transparent));
            _ghostMesh.SetVertices(_verts);
            _ghostMesh.SetUVs(0, data.Uvs);
            _ghostMesh.SetColors(_colors);
            _ghostMesh.SetTriangles(tris, 0, false);
            _ghostMesh.RecalculateNormals();
            _ghostMesh.RecalculateBounds();
            _ghost.localPosition = BlockRaycast.ToBlocksLocal(corner);
            _ghost.localRotation = Quaternion.identity; _ghost.localScale = Vector3.one;
        }

        void SetTargetText(string s)
        {
            if (s == _targetText) return;
            _targetText = s;
            _target.text = s;
            _target.gameObject.SetActive(s.Length > 0);
        }

        void ShowPreview(bool active)
        {
            _preview.gameObject.SetActive(active);
            _label.gameObject.SetActive(active);
            if (!active) return;
            if (_previewState != Editor.Selected) BuildPreview(Editor.Selected);
            _preview.localRotation = Quaternion.Euler(20f, Time.unscaledTime * 30f % 360f, 0f);
            if (Head)
            {
                _label.transform.rotation = Quaternion.LookRotation(_label.transform.position - Head.position, Vector3.up);
                _target.transform.rotation = Quaternion.LookRotation(_target.transform.position - Head.position, Vector3.up);
            }
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

        void OnDestroy()
        {
            if (_previewMesh) Destroy(_previewMesh);
            if (_ghostMesh) Destroy(_ghostMesh);
            if (_ghostMat) Destroy(_ghostMat);
        }
    }
}
