// Places the TabletopAnchor on the nearest MRUK TABLE surface after the scene loads (ARCHITECTURE.md section 4).
// Fallback: 0.75 m high, 0.5 m in front of the user. Hold A (right, ~0.5 s, a tick when it starts and a buzz when it
// fires) to re-place it at the right controller; both grips held = drag (move + yaw). Thumbstick Y on the right
// controller scales the miniature, X turns it.
//
// [comfort] The placement is remembered relative to the MRUK table (prefs.json, World/AppPrefs.cs): the next session on
// the same table puts the miniature where it was, turned the same way. A scene that loads after you placed the table
// yourself (A, drag, turn, Reset view) doesn't move it. A is ignored while the right ray is on the block menu.
using System;
using System.Collections;
using Meta.XR.MRUtilityKit;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class TablePlacement : MonoBehaviour
    {
        public Transform Anchor;       // TabletopAnchor (+Y = table normal)
        public Transform Head;         // centre eye
        public Transform RightHand;
        public float FallbackAfterSeconds = 6f;
        public Action<float> OnScaleChanged; // multiplier applied to block size
        public string Status = "waiting for scene";
        /// <summary>Usable table top in metres (x = width along the anchor's X, y = depth along Z). Default when no TABLE.</summary>
        public Vector2 TableSize = new Vector2(0.8f, 0.6f);
        /// <summary>Set by AreaSetupMode: the controllers belong to area setup, so placement/scale input is ignored.</summary>
        public bool Suspended;
        /// <summary>[menu] True while the right ray is on the block menu: the right stick scrolls the menu instead.</summary>
        public Func<bool> StickBlocked;
        /// <summary>[comfort] True while A must not re-place the table (the right ray is on the block menu).</summary>
        public Func<bool> ABlocked;
        public float TurnDegPerSecond = 90f;   // right stick left/right at full deflection
        public readonly HoldButton HoldA = new HoldButton { HoldSeconds = 0.5f };

        public enum Source { None, Fallback, Table, Saved, Manual }
        /// <summary>Who placed the table last. Manual (A, drag, turn, Reset view) is never overridden by a late scene load.</summary>
        public Source PlacedBy { get; private set; }
        public bool Settled => PlacedBy != Source.None;
        /// <summary>The MRUK TABLE anchor the placement is remembered against: its UUID (null = none), centre and yaw.</summary>
        public string TableId { get; private set; }
        public Vector3 TableCentre { get; private set; }
        public float TableYaw { get; private set; }
        /// <summary>prefs.json: a pose saved against the table with this UUID, applied when the scene has that table.</summary>
        public string SavedTableId;
        public TablePose? SavedPose;

        Vector3 _grabOffset; float _grabYaw, _anchorYaw0; bool _grabbing;
        float _hapticOff = -1;
        bool _wasSuspended;

        IEnumerator Start()
        {
            if (MRUK.Instance != null) MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
            else Status = "no MRUK in scene";
            float t = 0;
            while (!Settled && t < FallbackAfterSeconds) { t += Time.deltaTime; yield return null; }
            if (!Settled) { PlaceInFront("no table found: placed in front of you (hold A to re-place)"); PlacedBy = Source.Fallback; }
        }

        void OnSceneLoaded()
        {
            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null) { Status = "scene loaded, no room"; return; }
            MRUKAnchor best = null; float bestD = float.MaxValue;
            foreach (var a in room.Anchors)
            {
                if (!a.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) continue;
                // The table the saved placement belongs to wins; otherwise the nearest one.
                float d = SavedTableId != null && a.Anchor.Uuid.ToString() == SavedTableId ? -1f : Vector3.Distance(a.transform.position, Head.position);
                if (d < bestD) { bestD = d; best = a; }
            }
            if (best == null) { Status = "scene has no TABLE (run Space Setup and mark a table)"; return; }
            // Plane anchors: the surface spans local X/Y and local +Z points out of it (up for a table).
            Vector3 centre = best.transform.position;
            if (best.PlaneRect.HasValue) centre = best.transform.TransformPoint(best.PlaneRect.Value.center);
            else if (best.VolumeBounds.HasValue) { var b = best.VolumeBounds.Value; centre = best.transform.TransformPoint(new Vector3(b.center.x, b.center.y, b.max.z)); }
            if (best.PlaneRect.HasValue) TableSize = best.PlaneRect.Value.size;
            else if (best.VolumeBounds.HasValue) TableSize = new Vector2(best.VolumeBounds.Value.size.x, best.VolumeBounds.Value.size.y);
            Vector3 up = best.PlaneRect.HasValue ? best.transform.forward : Vector3.up;
            if (Vector3.Dot(up, Vector3.up) < 0) up = -up;
            // [comfort] A yaw that belongs to the table itself (its local Y lies in the surface), stable across sessions.
            Vector3 along = Vector3.ProjectOnPlane(best.transform.up, Vector3.up);
            if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(best.transform.right, Vector3.up);
            TableId = best.Anchor.Uuid.ToString();
            TableCentre = centre;
            TableYaw = Quaternion.LookRotation(along.normalized, Vector3.up).eulerAngles.y;
            float dist = Vector3.Distance(best.transform.position, Head.position);
            if (PlacedBy == Source.Manual) { Status = $"table found {dist:F2} m away; keeping your placement (hold A to move it)"; return; }
            if (SavedPose.HasValue && SavedTableId == TableId)
            {
                SavedPose.Value.Compose(centre, TableYaw, out var p, out float yaw);
                Place(p, Quaternion.Euler(0, yaw, 0), Source.Saved);
                Status = $"placed where you left it on the table ({TableSize.x:F2} x {TableSize.y:F2} m)";
                return;
            }
            Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, up).normalized;
            Place(centre, Quaternion.LookRotation(fwd.sqrMagnitude > 0.01f ? fwd : Vector3.forward, up), Source.Table);
            Status = $"placed on table {dist:F2} m away ({TableSize.x:F2} x {TableSize.y:F2} m)";
        }

        void PlaceInFront(string why)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            Vector3 p = Head.position + fwd * 0.5f; p.y = Mathf.Max(0.75f, Head.position.y - 0.6f);
            Anchor.SetPositionAndRotation(p, Quaternion.LookRotation(fwd, Vector3.up));
            Status = why;
        }

        void Place(Vector3 p, Quaternion r, Source by) { Anchor.SetPositionAndRotation(p, r); PlacedBy = by; }

        /// <summary>[comfort] The table's pose relative to the MRUK table, for prefs.json; false without a table.</summary>
        public bool TryRelative(out string tableId, out TablePose rel)
        {
            tableId = TableId; rel = default;
            if (TableId == null || PlacedBy == Source.None || PlacedBy == Source.Fallback) return false;
            rel = TablePose.Relative(TableCentre, TableYaw, Anchor.position, Anchor.eulerAngles.y);
            return true;
        }

        /// <summary>[comfort] Reset view: turn the table about its centre so north (the far side) points away from you.</summary>
        public void NorthUp()
        {
            if (!Head || !Anchor) return;
            Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            Anchor.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            if (PlacedBy != Source.None) PlacedBy = Source.Manual;
        }

        void Haptic(float amplitude, float seconds)
        {
            OVRInput.SetControllerVibration(0.5f, amplitude, OVRInput.Controller.RTouch);
            _hapticOff = Time.unscaledTime + seconds;
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (_hapticOff >= 0 && now >= _hapticOff) { _hapticOff = -1; OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch); }
            bool aDown = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);
            // Area setup's A (confirm; setup may have ended earlier this frame) and A pressed on the menu never re-place
            // the table, even if still held afterwards.
            var ev = HoldA.Update(aDown, now, Suspended || _wasSuspended || (ABlocked != null && ABlocked()));
            _wasSuspended = Suspended;
            if (Suspended) return;
            if (ev == HoldButton.Event.Pressed) Haptic(0.12f, 0.03f);
            else if (ev == HoldButton.Event.Fired && RightHand != null)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
                Place(RightHand.position, Quaternion.LookRotation(fwd.sqrMagnitude > 0.01f ? fwd : Vector3.forward, Vector3.up), Source.Manual);
                Status = "placed at right controller";
                Haptic(0.6f, 0.08f);
            }
            bool both = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch) && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            if (both && RightHand != null)
            {
                float yaw = RightHand.eulerAngles.y;
                if (!_grabbing) { _grabbing = true; _grabOffset = Anchor.position - RightHand.position; _grabYaw = yaw; _anchorYaw0 = Anchor.eulerAngles.y; }
                Anchor.position = RightHand.position + _grabOffset;
                Anchor.rotation = Quaternion.Euler(0, _anchorYaw0 + Mathf.DeltaAngle(_grabYaw, yaw), 0);
                PlacedBy = Source.Manual;
            }
            else _grabbing = false;
            // Right stick: up/down scales, left/right turns the table about its centre (whichever axis dominates).
            if (StickBlocked != null && StickBlocked()) return;   // [menu] the stick scrolls the block menu
            Vector2 rs = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (Mathf.Abs(rs.y) > 0.5f && Mathf.Abs(rs.y) >= Mathf.Abs(rs.x)) OnScaleChanged?.Invoke(1f + Mathf.Sign(rs.y) * 0.5f * Time.deltaTime);
            else if (Mathf.Abs(rs.x) > 0.25f && !_grabbing)
            {
                // Push right = turn right: the world turns counter-clockwise seen from above (like a VR turn).
                float k = (Mathf.Abs(rs.x) - 0.25f) / 0.75f;
                Anchor.rotation = Quaternion.AngleAxis(-Mathf.Sign(rs.x) * TurnDegPerSecond * k * k * Time.deltaTime, Vector3.up) * Anchor.rotation;
                if (PlacedBy != Source.None) PlacedBy = Source.Manual;
            }
        }
    }
}
