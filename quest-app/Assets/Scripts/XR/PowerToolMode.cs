// [power] The power tools in the headset (docs/QUEST_SETUP.md "Power tools: undo, boxes and brushes"): the box wand and
// brushes on the left trigger, their cyan boxes on the table, and the Tools tab's buttons and B-hold undo sent to the
// server (PROTOCOL.md "Undo and redo", "Box edits", "Brushes"). The logic is engine-free in World/PowerTools.cs; this
// maps the controllers onto it and draws:
//
//   Wand on          left trigger = corner A at the aimed block, left grip + left trigger = corner B; a cyan wireframe
//                    shows the box (one block until B is set)
//   Paste (Clone)    an orange wireframe of the copy follows the aimed point (it stands on the face you aim at); the
//                    left trigger pastes it there, the Tools tab's Rotate turns it, B (a tap) or Clone again ends it
//   Brush on         holding the left trigger paints strokes at the aimed block; a faint cyan box shows the brush's reach
//   B held 0.6 s     undo (BlockEditMode calls Press(Undo)); the Tools tab has Undo and Redo with their counts
//
// The right trigger still breaks and B still picks (with a tool on, a B tap picks Replace's filter instead).
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class PowerToolMode : MonoBehaviour
    {
        public PowerTools Tools;
        public BlockEditMode Edit;                     // the ray's target (BlockHit) and the blocks' transform
        public Transform Blocks;
        public Func<WorldModel> World;
        public NavState Nav;
        public Func<Dictionary<string, object>, long> SendTracked;   // LinkClient.SendTracked: the envelope seq
        public Func<bool> Online, Blocked;
        public Func<string> Material;                  // the selected block (the menu's selection)

        static readonly Color BoxColor = new Color(0.3f, 1f, 1f, 0.95f), PasteColor = new Color(1f, 0.6f, 0.15f, 0.95f), BrushColor = new Color(0.3f, 1f, 1f, 0.45f);
        LineRenderer _box, _paste, _brush;
        readonly Vector3[] _boxPts = new Vector3[16], _pastePts = new Vector3[16], _brushPts = new Vector3[16];
        Vector3 _lo, _hi;
        float _hapticOff = -1;
        bool _stroking;

        void Start()
        {
            var mat = new Material(Shader.Find("Sprites/Default"));
            _box = NewLine("PowerBox", mat, BoxColor);
            _paste = NewLine("PowerPaste", mat, PasteColor);
            _brush = NewLine("PowerBrush", mat, BrushColor);
        }

        LineRenderer NewLine(string name, Material mat, Color c)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.useWorldSpace = true; l.positionCount = 16; l.loop = false; l.material = mat; l.numCapVertices = 0;
            l.startColor = l.endColor = c;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; l.receiveShadows = false;
            go.SetActive(false);
            return l;
        }

        /// <summary>A Tools tab button (or B held: Undo): runs it and sends what it makes, tracked for its result.</summary>
        public void Press(ToolButton b)
        {
            if (Tools == null) return;
            double now = Time.unscaledTimeAsDouble;
            var m = Tools.Press(b, Material?.Invoke(), now);
            if (m == null) return;
            if (!(Online?.Invoke() ?? true)) { Tools.Result = "not connected"; return; }
            Track(m, now);
        }

        void Track(Dictionary<string, object> m, double now)
        {
            string type = m.S("type"), op = type == "box_edit" ? m.S("op") : type;
            long seq = SendTracked != null ? SendTracked(m) : -1;
            Tools.Sent(seq, op, now);
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch); }
            var w = World?.Invoke();
            bool active = Tools != null && Tools.Mode != ToolMode.None && w != null && w.Loaded && Blocks && Edit && !(Blocked?.Invoke() ?? false);
            if (!active)
            {
                _box.gameObject.SetActive(false); _paste.gameObject.SetActive(false); _brush.gameObject.SetActive(false);
                _stroking = false;
                // the wand's box stays visible (without input) while the menu or a panel has the ray
                if (Tools != null && Tools.Mode == ToolMode.Wand && w != null && w.Loaded && Blocks) { Window(w); DrawBox(w); }
                return;
            }
            Window(w);
            var t = Edit.Target;
            bool online = Online?.Invoke() ?? true;
            bool leftDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool leftHeld = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            double dnow = Time.unscaledTimeAsDouble;
            if (Tools.Mode == ToolMode.Wand)
            {
                _brush.gameObject.SetActive(false);
                if (Tools.Pasting)
                {
                    bool aim = t.Hit && t.AdjacentInView;
                    _paste.gameObject.SetActive(aim);
                    if (aim)
                    {
                        var o = Tools.PasteOrigin(w.Origin + t.Adjacent) - w.Origin;
                        Lines(_paste, _pastePts, (Vector3)o, (Vector3)(o + Tools.PasteSize), 0.03f);
                        if (leftDown)
                        {
                            if (!online) Tools.Result = "not connected";
                            else { var m = Tools.Paste(w.Origin + t.Adjacent, dnow); if (m != null) { Track(m, dnow); Haptic(0.4f); } }
                        }
                    }
                }
                else
                {
                    _paste.gameObject.SetActive(false);
                    if (leftDown && t.Hit) { Tools.SetCorner(grip, w.Origin + t.Block); Haptic(grip ? 0.35f : 0.25f); }
                }
                DrawBox(w);
                return;
            }
            // brush
            _paste.gameObject.SetActive(false);
            if (!Tools.Pasting && Tools.HasBox) DrawBox(w); else _box.gameObject.SetActive(false);
            _brush.gameObject.SetActive(t.Hit);
            if (t.Hit)
            {
                int r = Tools.Radius;
                int y0 = Tools.Form == BrushForm.Sphere ? -r : -((Tools.Height - 1) / 2), y1 = Tools.Form == BrushForm.Sphere ? r : Tools.Height / 2;
                var c = (Vector3)t.Block;
                Lines(_brush, _brushPts, c + new Vector3(-r, y0, -r), c + new Vector3(r + 1, y1 + 1, r + 1), 0.02f);
            }
            if (leftDown) { Tools.BeginStroke(); _stroking = true; }
            if (!leftHeld) _stroking = false;
            if (_stroking && t.Hit)
            {
                if (!online) { Tools.Result = "not connected"; return; }
                var m = Tools.Stroke(w.Origin + t.Block, Material?.Invoke(), dnow);
                if (m != null) { SendTracked?.Invoke(m); Haptic(0.15f); }
            }
        }

        /// <summary>The visible window in region-local blocks (the margin around it is clipped away), as BlockEditMode.</summary>
        void Window(WorldModel w)
        {
            _lo = Vector3.zero; _hi = w.Dims;
            if (Nav != null && Nav.Loaded && Nav.Origin == w.Origin) { _lo = Nav.WindowMin - (Vector3)w.Origin; _hi = _lo + (Vector3)Nav.Window; }
        }

        /// <summary>The wand's box: cyan, cut to the visible window; hidden when none or wholly outside it.</summary>
        void DrawBox(WorldModel w)
        {
            if (!Tools.HasBox) { _box.gameObject.SetActive(false); return; }
            var lo = Tools.Min - w.Origin;
            var hi = Tools.Max - w.Origin + new Vector3Int(1, 1, 1);
            bool visible = hi.x > _lo.x && hi.y > _lo.y && hi.z > _lo.z && lo.x < _hi.x && lo.y < _hi.y && lo.z < _hi.z;
            _box.gameObject.SetActive(visible);
            if (visible) Lines(_box, _boxPts, (Vector3)lo, (Vector3)hi, 0.03f);
        }

        /// <summary>A box [a, b] in region-local blocks (grown by e, cut to the window) as 16 line points, like BlockEditMode's outline.</summary>
        void Lines(LineRenderer line, Vector3[] box, Vector3 a0, Vector3 b0, float e)
        {
            Vector3 a = new Vector3(Mathf.Max(a0.x, _lo.x) - e, Mathf.Max(a0.y, _lo.y) - e, Mathf.Max(a0.z, _lo.z) - e);
            Vector3 b = new Vector3(Mathf.Min(b0.x, _hi.x) + e, Mathf.Min(b0.y, _hi.y) + e, Mathf.Min(b0.z, _hi.z) + e);
            int i = 0;
            void P(float x, float y, float z) => box[i++] = Blocks.TransformPoint(BlockRaycast.ToBlocksLocal(new Vector3(x, y, z)));
            P(a.x, a.y, a.z); P(b.x, a.y, a.z); P(b.x, a.y, b.z); P(a.x, a.y, b.z); P(a.x, a.y, a.z);
            P(a.x, b.y, a.z); P(b.x, b.y, a.z); P(b.x, a.y, a.z); P(b.x, b.y, a.z); P(b.x, b.y, b.z); P(b.x, a.y, b.z);
            P(b.x, b.y, b.z); P(a.x, b.y, b.z); P(a.x, a.y, b.z); P(a.x, b.y, b.z); P(a.x, b.y, a.z);
            line.SetPositions(box);
            line.widthMultiplier = Mathf.Clamp(Blocks.lossyScale.x * 0.12f, 0.0008f, 0.004f);
        }

        void Haptic(float amplitude)
        {
            OVRInput.SetControllerVibration(0.6f, amplitude, OVRInput.Controller.LTouch);
            _hapticOff = Time.unscaledTime + 0.04f;
        }
    }
}
