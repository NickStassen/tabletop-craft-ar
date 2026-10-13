// Headset area setup (docs/QUEST_SETUP.md "Setting up a play area"). Left Y toggles the mode. While active, a
// wireframe box shows the requested area at the current block scale relative to the region on the table, with
// grab handles on its +X, +Z and top faces. Confirm sends region_request; the server answers with region_changed
// and a new snapshot (to every shared headset), or region_too_large with its limits.
//
//   Left stick           width (X) / depth (Z), 8-block steps
//   Right stick up/down  height, 16-block steps
//   Right stick L/R      previous / next player (centre on player)
//   X (left)             cycle centre: current origin -> player -> coordinates
//   Right grip + move    nudge the centre in whole blocks (coordinates mode)
//   Right trigger        grab a handle sphere and drag that face
//   Right stick click    toggle block-size auto-fit (fit the area to the table) vs keep the manual scale
//   A                    confirm      B tap  cancel      B hold 1 s  toggle the face-budget override
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class AreaSetupMode : MonoBehaviour
    {
        public Transform RegionRoot;           // RegionRoot under the anchor (block units after Blocks scale)
        public Transform RightHand;
        public TablePlacement Placement;
        public Func<float> BlockSize;          // current block_size_m
        public Func<WorldModel> World;
        public Action<Dictionary<string, object>> Send;
        public Action<float> ApplyBlockSize;   // called with the fitted block size after a confirmed resize
        public bool AutoFit = true;

        public bool Active { get; private set; }
        public readonly AreaSetupState State = new AreaSetupState();
        public string Status = "";
        public float FittedBlockSize => AreaMath.FitBlockSize(Placement ? Placement.TableSize.x : 0.8f, Placement ? Placement.TableSize.y : 0.6f, State.Size.x, State.Size.z);

        LineRenderer _box;
        readonly Transform[] _handles = new Transform[3];   // +X, +Z, top
        float _repeatAt, _bDownAt = -1; bool _bLong;
        int _dragHandle = -1; Vector3 _dragStart; Vector3Int _dragSize0;
        bool _nudging; Vector3 _nudgeStart, _nudgeCenter0; int _nudgeY0;
        bool _awaitingResult; float _requestedBlockSize;

        void Start()
        {
            var go = new GameObject("AreaBox");
            go.transform.SetParent(transform, false);
            _box = go.AddComponent<LineRenderer>();
            _box.useWorldSpace = true; _box.widthMultiplier = 0.003f; _box.positionCount = 16; _box.loop = false;
            _box.material = new Material(Shader.Find("Sprites/Default")); _box.startColor = _box.endColor = new Color(0.3f, 1f, 1f);
            for (int i = 0; i < 3; i++)
            {
                var h = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(h.GetComponent<Collider>());
                h.transform.SetParent(go.transform, false);
                h.transform.localScale = Vector3.one * 0.025f;
                h.GetComponent<Renderer>().material.color = new Color(1f, 0.8f, 0.2f);
                _handles[i] = h.transform;
            }
            go.SetActive(false);
        }

        public void Toggle()
        {
            if (Active) { Exit("area setup cancelled"); return; }
            var w = World();
            if (w == null || !w.Loaded) { Status = "area setup: waiting for the first snapshot"; return; }
            State.Begin(w.World, w.Origin, w.Dims);
            Active = true;
            if (Placement) Placement.Suspended = true;
            _box.gameObject.SetActive(true);
            Send(Protocol.RegionQuery());
        }

        void Exit(string why)
        {
            Active = false; _dragHandle = -1; _nudging = false;
            if (Placement) Placement.Suspended = false;
            _box.gameObject.SetActive(false);
            Status = why;
        }

        public void OnRegionInfo(RegionInfo info) { State.SetInfo(info); }

        public void OnRegionChanged(Dictionary<string, object> m, bool mine)
        {
            if (mine && _awaitingResult && AutoFit) ApplyBlockSize?.Invoke(_requestedBlockSize);
            _awaitingResult = false;
            var d = m.A("dims");
            Status = mine ? "area set" : $"area changed by another headset to {(d != null ? $"{d[0]}x{d[1]}x{d[2]}" : "?")}";
        }

        public void OnError(Dictionary<string, object> m)
        {
            if (m.S("code") != "region_too_large") return;
            _awaitingResult = false;
            State.Limits = RegionLimits.From(m, State.Limits);
            Status = "server refused: " + m.S("message");
        }

        void Update()
        {
            if (OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch)) Toggle();
            if (!Active) return;
            float now = Time.unscaledTime;

            Vector2 ls = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            Vector2 rs = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (now >= _repeatAt)
            {
                bool acted = true;
                if (Mathf.Abs(ls.x) > 0.6f) State.Resize(0, ls.x > 0 ? 1 : -1);
                else if (Mathf.Abs(ls.y) > 0.6f) State.Resize(2, ls.y > 0 ? 1 : -1);
                else if (Mathf.Abs(rs.y) > 0.6f) State.Resize(1, rs.y > 0 ? 1 : -1);
                else if (Mathf.Abs(rs.x) > 0.7f) State.NextPlayer(rs.x > 0 ? 1 : -1);
                else acted = false;
                if (acted) _repeatAt = now + 0.18f;
            }
            if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch)) State.CycleMode();
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch)) AutoFit = !AutoFit;

            float bs = Mathf.Max(1e-4f, BlockSize());
            HandleDrag(bs);
            HandleNudge(bs);

            // B: tap = cancel, hold = override toggle.
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) { _bDownAt = now; _bLong = false; }
            if (_bDownAt >= 0 && !_bLong && OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch) && now - _bDownAt > 1f) { _bLong = true; State.OverrideBudget = !State.OverrideBudget; }
            if (OVRInput.GetUp(OVRInput.Button.Two, OVRInput.Controller.RTouch)) { if (!_bLong && _bDownAt >= 0) { Exit("area setup cancelled"); return; } _bDownAt = -1; }

            string blocker = State.Blocker();
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            {
                if (blocker != null) Status = "cannot confirm: " + blocker;
                else
                {
                    _requestedBlockSize = FittedBlockSize; _awaitingResult = true;
                    Send(State.BuildRequest());
                    Exit($"requested {State.Size.x}x{State.Size.y}x{State.Size.z}...");
                    return;
                }
            }
            DrawBox(bs);
            Status = State.Describe() + $"\nblock {(AutoFit ? $"auto-fit {FittedBlockSize * 1000:F1} mm" : $"manual {bs * 1000:F1} mm")}" +
                     (blocker != null ? $"\nWARNING {blocker}" : "\nA confirm  B cancel");
            _box.startColor = _box.endColor = blocker != null ? new Color(1f, 0.4f, 0.3f) : new Color(0.3f, 1f, 1f);
        }

        // Box corners in RegionRoot-local metres. RegionRoot local = block coords relative to the current origin * bs, Z mirrored.
        Vector3 Local(float bx, float by, float bz, float bs)
        {
            var w = World();
            return new Vector3((bx - w.Origin.x) * bs, (by - w.Origin.y) * bs, -(bz - w.Origin.z) * bs);
        }

        void Corners(float bs, out Vector3 min, out Vector3 max)
        {
            float x0 = State.Center.x - State.Size.x / 2f, z0 = State.Center.z - State.Size.z / 2f;
            min = new Vector3(x0, State.YOrigin, z0); max = new Vector3(x0 + State.Size.x, State.YOrigin + State.Size.y, z0 + State.Size.z);
        }

        void DrawBox(float bs)
        {
            Corners(bs, out var a, out var b);
            Vector3 P(float x, float y, float z) => RegionRoot.TransformPoint(Local(x, y, z, bs));
            var pts = new[]
            {
                P(a.x, a.y, a.z), P(b.x, a.y, a.z), P(b.x, a.y, b.z), P(a.x, a.y, b.z), P(a.x, a.y, a.z),
                P(a.x, b.y, a.z), P(b.x, b.y, a.z), P(b.x, a.y, a.z), P(b.x, b.y, a.z), P(b.x, b.y, b.z), P(b.x, a.y, b.z),
                P(b.x, b.y, b.z), P(a.x, b.y, b.z), P(a.x, a.y, b.z), P(a.x, b.y, b.z), P(a.x, b.y, a.z),
            };
            _box.SetPositions(pts);
            float cx = (a.x + b.x) / 2, cy = (a.y + b.y) / 2, cz = (a.z + b.z) / 2;
            _handles[0].position = P(b.x, cy, cz);
            _handles[1].position = P(cx, cy, b.z);
            _handles[2].position = P(cx, b.y, cz);
        }

        void HandleDrag(float bs)
        {
            if (!RightHand) return;
            bool trig = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            if (trig && _dragHandle < 0 && OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
                for (int i = 0; i < 3; i++)
                    if (Vector3.Distance(_handles[i].position, RightHand.position) < 0.06f) { _dragHandle = i; _dragStart = RegionRoot.InverseTransformPoint(RightHand.position); _dragSize0 = State.Size; }
            }
            if (!trig) { _dragHandle = -1; return; }
            if (_dragHandle < 0) return;
            Vector3 d = (RegionRoot.InverseTransformPoint(RightHand.position) - _dragStart) / bs;   // blocks; Z mirrored
            int axis = _dragHandle == 0 ? 0 : _dragHandle == 1 ? 2 : 1;
            float delta = axis == 0 ? d.x : axis == 2 ? -d.z : d.y;
            // Dragging one face grows the box symmetrically about the centre (the centre stays put), x2 per block moved.
            int step = axis == 1 ? 16 : AreaSetupState.Step;
            int target = Mathf.RoundToInt((_dragSize0[axis] + (axis == 1 ? delta : 2 * delta)) / step);
            State.Resize(axis, target - State.Size[axis] / step);
        }

        void HandleNudge(float bs)
        {
            if (!RightHand) return;
            bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch) && !OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            if (!grip) { _nudging = false; return; }
            Vector3 p = RegionRoot.InverseTransformPoint(RightHand.position);
            if (!_nudging) { _nudging = true; _nudgeStart = p; _nudgeCenter0 = State.Center; _nudgeY0 = State.YOrigin; return; }
            Vector3 d = (p - _nudgeStart) / bs;
            int dx = Mathf.RoundToInt(_nudgeCenter0.x + d.x - State.Center.x);
            int dz = Mathf.RoundToInt(_nudgeCenter0.z - d.z - State.Center.z);
            int dy = Mathf.RoundToInt(_nudgeY0 + d.y - State.YOrigin);
            if (dx != 0 || dy != 0 || dz != 0) State.Nudge(dx, dy, dz);
        }
    }
}
