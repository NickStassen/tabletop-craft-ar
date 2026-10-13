// Places the TabletopAnchor on the nearest MRUK TABLE surface after the scene loads (ARCHITECTURE.md section 4).
// Fallback: 0.75 m high, 0.5 m in front of the user. Right-hand A button re-places it at the right controller;
// both grips held = drag (move + yaw). Thumbstick Y on the right controller scales the miniature.
using System.Collections;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class TablePlacement : MonoBehaviour
    {
        public Transform Anchor;       // TabletopAnchor (+Y = table normal)
        public Transform Head;         // centre eye
        public Transform RightHand;
        public float FallbackAfterSeconds = 6f;
        public System.Action<float> OnScaleChanged; // multiplier applied to block size
        public string Status = "waiting for scene";
        /// <summary>Usable table top in metres (x = width along the anchor's X, y = depth along Z). Default when no TABLE.</summary>
        public Vector2 TableSize = new Vector2(0.8f, 0.6f);
        /// <summary>Set by AreaSetupMode: the controllers belong to area setup, so placement/scale input is ignored.</summary>
        public bool Suspended;

        bool _placed;
        Vector3 _grabOffset; float _grabYaw, _anchorYaw0; bool _grabbing;

        IEnumerator Start()
        {
            if (MRUK.Instance != null) MRUK.Instance.RegisterSceneLoadedCallback(OnSceneLoaded);
            else Status = "no MRUK in scene";
            float t = 0;
            while (!_placed && t < FallbackAfterSeconds) { t += Time.deltaTime; yield return null; }
            if (!_placed) PlaceInFront("no table found: placed in front of you (A = re-place)");
        }

        void OnSceneLoaded()
        {
            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null) { Status = "scene loaded, no room"; return; }
            MRUKAnchor best = null; float bestD = float.MaxValue;
            foreach (var a in room.Anchors)
            {
                if (!a.HasAnyLabel(MRUKAnchor.SceneLabels.TABLE)) continue;
                float d = Vector3.Distance(a.transform.position, Head.position);
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
            Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, up).normalized;
            Place(centre, Quaternion.LookRotation(fwd.sqrMagnitude > 0.01f ? fwd : Vector3.forward, up));
            Status = $"placed on table {bestD:F2} m away ({TableSize.x:F2} x {TableSize.y:F2} m)";
        }

        void PlaceInFront(string why)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            Vector3 p = Head.position + fwd * 0.5f; p.y = Mathf.Max(0.75f, Head.position.y - 0.6f);
            Place(p, Quaternion.LookRotation(fwd, Vector3.up));
            Status = why;
        }

        void Place(Vector3 p, Quaternion r) { Anchor.SetPositionAndRotation(p, r); _placed = true; }

        void Update()
        {
            if (Suspended) return;
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) && RightHand != null)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(Head.forward, Vector3.up).normalized;
                Place(RightHand.position, Quaternion.LookRotation(fwd.sqrMagnitude > 0.01f ? fwd : Vector3.forward, Vector3.up));
                Status = "placed at right controller";
            }
            bool both = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch) && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            if (both && RightHand != null)
            {
                float yaw = RightHand.eulerAngles.y;
                if (!_grabbing) { _grabbing = true; _grabOffset = Anchor.position - RightHand.position; _grabYaw = yaw; _anchorYaw0 = Anchor.eulerAngles.y; }
                Anchor.position = RightHand.position + _grabOffset;
                Anchor.rotation = Quaternion.Euler(0, _anchorYaw0 + Mathf.DeltaAngle(_grabYaw, yaw), 0);
            }
            else _grabbing = false;
            float stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).y;
            if (Mathf.Abs(stick) > 0.5f) OnScaleChanged?.Invoke(1f + Mathf.Sign(stick) * 0.5f * Time.deltaTime);
        }
    }
}
