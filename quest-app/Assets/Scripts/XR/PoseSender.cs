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
            Link.Send(Protocol.Pose(ts, head, l, r));
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
