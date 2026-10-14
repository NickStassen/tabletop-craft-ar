// Moving around the world on the table (docs/QUEST_SETUP.md "Moving around"). Drives the engine-free NavState from
// the controllers, keeps RegionRoot placed so the visible window sits on the anchor, clips the block shaders to
// that window (global _TabletopClip, see Shaders/TabletopClip.cginc), keeps the cut sides solid (ClipCaps), and
// sends region_request when the view needs the region shifted.
//
//   Left stick                 glide across the world, relative to where you look (forward = away from you)
//   Left trigger + left stick  up / down (stick forward = up), sideways still works
//   (Right stick X turns the table and right stick Y scales it: TablePlacement.)
// Area setup (Y) owns the sticks while it is active.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class NavController : MonoBehaviour
    {
        public Transform Anchor, RegionRoot, Head;
        public TabletopRenderer Renderer;
        public ClipCaps Caps;
        public Func<float> BlockSize;
        public Func<bool> Suspended;                       // true while area setup owns the controllers
        public Action<Dictionary<string, object>> Send;
        public float DeadZone = 0.15f;

        public readonly NavState State = new NavState();
        public string Status => State.Describe() + (State.LastNote.Length > 0 ? "  " + State.LastNote : "");

        static readonly int ClipId = Shader.PropertyToID("_TabletopClip");
        float _held;

        void OnDisable() => Shader.SetGlobalMatrix(ClipId, Matrix4x4.zero);

        /// <summary>A snapshot was applied: adopt the region (keeps the view where it is in the world when possible).</summary>
        public void OnSnapshot(string world, Vector3Int origin, Vector3Int dims, Vector3Int viewMargin)
        {
            State.OnRegion(world, origin, dims, viewMargin, Time.unscaledTime);
            Place();
        }

        /// <summary>welcome: server capabilities. Padding a region needs a server that keeps view_margin (else it would grow forever).</summary>
        public void OnWelcome(Dictionary<string, object> welcome)
        {
            var caps = welcome.A("caps");
            State.CanPad = caps != null && caps.Contains("view_margin");
            OnLimits(welcome.O("limits"));
        }

        public void OnLimits(Dictionary<string, object> limits)
        {
            if (limits == null) return;
            State.Caps = RegionLimits.From(limits, State.Caps);
            var bh = limits.A("build_height");
            if (bh != null && bh.Count == 2) { State.MinY = (int)(double)bh[0]; State.MaxY = (int)(double)bh[1]; }
        }

        public void OnRegionError(string message) => State.OnRefused(Time.unscaledTime, message);

        void Update()
        {
            if (!State.Loaded || Anchor == null) return;
            float bs = Mathf.Max(1e-4f, BlockSize());
            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            Vector3 delta = Vector3.zero;
            if (Suspended == null || !Suspended())
            {
                Vector2 ls = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
                float mag = Mathf.Min(1f, ls.magnitude);
                if (mag > DeadZone)
                {
                    _held += dt;
                    bool vertical = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
                    // Head yaw in the anchor's frame, projected onto the table.
                    Vector3 f = Head ? Anchor.InverseTransformDirection(Head.forward) : Vector3.forward;
                    f.y = 0;
                    if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                    f.Normalize();
                    var r = new Vector3(f.z, 0, -f.x);
                    Vector3 dir = vertical ? r * ls.x + Vector3.up * ls.y : r * ls.x + f * ls.y;
                    if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
                    float rescaled = (mag - DeadZone) / (1f - DeadZone);
                    int m = vertical && Mathf.Abs(ls.y) > Mathf.Abs(ls.x) ? State.Margin.y : Mathf.Min(State.Margin.x, State.Margin.z);
                    float speed = State.Speed(bs, rescaled, _held, m);
                    Vector3 u = dir * speed * dt;                    // anchor-local Unity axes, in blocks
                    delta = new Vector3(u.x, u.y, -u.z);              // -> world blocks (Z mirrored)
                }
                else _held = 0;
            }
            var move = State.Move(delta, dt, Time.unscaledTime);
            if (move.HasValue)
            {
                var mv = move.Value;
                Send?.Invoke(Protocol.RegionRequest(State.World, mv.Origin, mv.Dims, mv.Margin));
                Debug.Log($"[Tabletop] {State.LastNote} dims {mv.Dims.x}x{mv.Dims.y}x{mv.Dims.z} margin {mv.Margin.x}/{mv.Margin.y}");
            }
            Place();
        }

        /// <summary>Puts RegionRoot so the window's bottom centre is on the anchor, and tells the renderer where to mesh first.</summary>
        public void Place()
        {
            if (!State.Loaded || RegionRoot == null) return;
            float bs = Mathf.Max(1e-4f, BlockSize());
            RegionRoot.localPosition = State.RegionRootLocal(bs);
            if (Renderer) Renderer.Focus = State.View - (Vector3)State.Origin;
        }

        // After TablePlacement moved the anchor this frame.
        void LateUpdate()
        {
            if (!State.Loaded || Anchor == null) { Shader.SetGlobalMatrix(ClipId, Matrix4x4.zero); return; }
            float bs = Mathf.Max(1e-4f, BlockSize());
            var w = State.Window;
            float e = 0.02f * bs;   // a hair outside the window so caps sitting exactly on it survive the clip
            var half = new Vector3(w.x * bs / 2f + e, w.y * bs / 2f + e, w.z * bs / 2f + e);
            var box = Matrix4x4.Scale(new Vector3(1f / half.x, 1f / half.y, 1f / half.z)) * Matrix4x4.Translate(new Vector3(0, -w.y * bs / 2f, 0));
            Shader.SetGlobalMatrix(ClipId, box * Anchor.worldToLocalMatrix);
            if (Caps)
            {
                // While a new region is meshed behind the old one, the old terrain shows as it is (no caps for the new box).
                Vector3 lo = State.WindowMin - (Vector3)State.Origin;
                Caps.UpdateCaps(lo, lo + (Vector3)w, !(Renderer && Renderer.Staging));
            }
        }
    }
}
