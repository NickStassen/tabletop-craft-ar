// Sends head + hand poses at ~30 Hz in the protocol's tabletop frame (PROTOCOL.md section 3 and 4.7).
using System.Diagnostics;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class PoseSender : MonoBehaviour
    {
        public float Hz = 30f;
        public Transform RegionRoot;   // origin = region local (0,0,0) corner, unscaled, Unity axes
        public Transform Head, LeftHand, RightHand;
        public LinkClient Link;
        public int Sent;
        public System.Func<int> SnapshotId;   // [nav] current snapshot (pose.snapshot_id, PROTOCOL.md 4.7)

        float _next;
        static readonly Stopwatch Clock = Stopwatch.StartNew();

        void Update()
        {
            if (Link == null || !Link.Connected || RegionRoot == null || Head == null) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f / Hz;
            var head = ToTabletop(Head, true);
            TabletopPose? l = LeftHand ? ToTabletop(LeftHand, Tracked(OVRInput.Controller.LTouch, OVRInput.Controller.LHand)) : (TabletopPose?)null;
            TabletopPose? r = RightHand ? ToTabletop(RightHand, Tracked(OVRInput.Controller.RTouch, OVRInput.Controller.RHand)) : (TabletopPose?)null;
            long ts = Clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            var msg = Protocol.Pose(ts, head, l, r);
            // [nav] the snapshot whose origin RegionRoot is at, so the giant doesn't jump while a moved region is in flight.
            int snap = SnapshotId != null ? SnapshotId() : -1;
            if (snap >= 0) msg["snapshot_id"] = snap;
            Link.Send(msg);
            Sent++;
        }

        TabletopPose ToTabletop(Transform t, bool tracked)
        {
            Vector3 lp = RegionRoot.InverseTransformPoint(t.position);
            Quaternion lr = Quaternion.Inverse(RegionRoot.rotation) * t.rotation;
            return TabletopPose.FromUnityLocal(lp, lr, tracked);
        }

        static bool Tracked(OVRInput.Controller touch, OVRInput.Controller hand) =>
            OVRInput.GetControllerPositionTracked(touch) || OVRInput.GetControllerPositionTracked(hand);
    }
}
