// Engine-free navigation state (docs/QUEST_SETUP.md "Moving around"): where the visible box (the "window") sits
// inside the streamed region, and when to ask the server for a region shifted by whole 16-block sections.
//
// The server streams a region; the client shows a window of size dims - 2 * view_margin centred on View. Panning
// moves View smoothly by fractional blocks inside [centre - margin, centre + margin], so terrain from the margin
// slides into the window and nothing ever shows past the loaded data. Moving towards a margin past Trigger blocks
// requests the region shifted by 16 blocks in that direction; when it arrives the view stays where it is in the
// world (seamless) and has room to move again. With no margin (an old region), the first push asks for a padded
// region (same window, margin added) and, if that isn't possible, moves in 16-block steps.
//
// [nav] Faster gliding at wide areas: the margin grows with the visible width (AreaSizing.MarginFor; a region with a
// smaller margin is grown on the first push), and while the view moves steadily along an axis the region is shifted
// further ahead ("lookahead"): as far as possible while keeping a reserve of loaded blocks behind the window, so most of
// the margin is runway in the direction of motion. A reverse shift needs the view to come back past where it landed
// (hysteresis), so stopping or turning round never makes the region ping-pong. The speed cap is the runway one round
// trip can sustain divided by the measured latency of glide requests (request until the visible window is meshed).
//
// All coordinates are Minecraft world block coordinates (right-handed, +Y up). Vectors are UnityEngine types so the
// file compiles in Unity and against Tests~/UnityStubs.cs.
using System;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class NavState
    {
        public const int Step = 16;                 // region shifts are whole sections, so section meshes can be re-keyed

        // Tuning (see docs/QUEST_SETUP.md).
        public Vector3Int MarginPref = new Vector3Int(16, 16, 16);   // margin per side requested when padding
        public float Trigger = 4f;                  // blocks past the region centre (in the direction of motion) before a shift request
        /// <summary>
        /// Extra blocks a shift back the other way needs. A shift leaves the view Step - Trigger blocks behind the new
        /// centre, so without this a little reverse motion (stick or head-yaw jitter) would ask for the old region again
        /// at once and the region would ping-pong. Until the view has come back across the new centre, a reverse
        /// shift on that axis needs Step - Trigger + Hysteresis blocks (capped at the margin edge).
        /// </summary>
        public float Hysteresis = 3f;
        /// <summary>Without a margin (stepped mode) a push has to add up to this many blocks before it asks for a step.</summary>
        public float StepPush = 2f;
        public float BaseSpeedM = 0.12f;            // table metres per second at full stick deflection
        public float RampSeconds = 2f;              // holding the stick ramps the speed up to x(1 + RampGain)
        public float RampGain = 2f;
        public float RequestTimeout = 6f;           // s without an answer before another request may go out
        public float MinRequestInterval = 0.2f;
        // [nav] Lookahead (see the top of the file).
        /// <summary>Blocks of loaded data kept behind the window when the region is shifted ahead (so a stop or a turn has room).</summary>
        public float LookaheadReserve = 16f;
        /// <summary>Blocks travelled in one direction along an axis, without stopping, before the motion counts as steady.</summary>
        public float SteadyBlocks = 8f;
        /// <summary>Slower than this (blocks per second) along an axis is not moving along it.</summary>
        public float SteadySpeed = 2f;
        /// <summary>s below SteadySpeed before the steady distance resets (a frame without input doesn't break a glide).</summary>
        public float SteadyGrace = 0.2f;

        // Region from the latest snapshot.
        public bool Loaded { get; private set; }
        public string World { get; private set; } = "world";
        public Vector3Int Origin { get; private set; }
        public Vector3Int Dims { get; private set; }
        public Vector3Int Margin { get; private set; }               // view_margin (blocks per side), clamped so the window is >= 1
        public Vector3Int Window => new Vector3Int(Dims.x - 2 * Margin.x, Dims.y - 2 * Margin.y, Dims.z - 2 * Margin.z);
        public Vector3 RegionCentre => new Vector3(Origin.x + Dims.x / 2f, Origin.y + Dims.y / 2f, Origin.z + Dims.z / 2f);

        /// <summary>Centre of the visible window in world blocks. The window spans View +- Window/2 on every axis.</summary>
        public Vector3 View;
        public Vector3 Offset => View - RegionCentre;
        public Vector3 WindowMin => new Vector3(View.x - Window.x / 2f, View.y - Window.y / 2f, View.z - Window.z / 2f);

        // Server limits (welcome / region_info).
        public RegionLimits Caps = RegionLimits.Default;
        public int MinY = int.MinValue, MaxY = int.MaxValue;         // build height [MinY, MaxY), when the server sends it
        /// <summary>The server keeps view_margin with the region (welcome.caps has "view_margin"). Without it a padded
        /// region would come back without its margin and look bigger, so navigation only steps by sections.</summary>
        public bool CanPad;

        // Request bookkeeping.
        public bool Pending { get; private set; }
        public int Requests { get; private set; }
        /// <summary>
        /// s from a glide request (shift or pad) to its region applied (seeded; measured as we go). [nav] Meshing is no
        /// longer part of it: the sections a shift brings in lie deep in the margin and are meshed as the window
        /// approaches (MeshScope), so meshing limits the speed through MeshCap instead. Resizes are left out: a width
        /// preset sends a whole ring and would hold the glide speed down for many shifts afterwards.
        /// </summary>
        public float LatencyEma = 0.5f;
        string _pendingKind = "";
        /// <summary>
        /// [nav] Fastest pan, in blocks per second, at which meshing keeps up with the sections entering the window
        /// (MeshScope.MeshSpeedCap from the renderer's measured rate; NavController sets it every frame). No cap until measured.
        /// </summary>
        public float MeshCap = float.MaxValue;
        /// <summary>[nav] The mesh cap never slows a glide below this (blocks per second): a bad estimate mustn't make it crawl.</summary>
        public float MinMeshCap = 12f;
        public string LastNote = "";
        float _pendingSince = -1, _lastRequestAt = -100;
        bool _padRefused;
        readonly int[] _lastDir = new int[3];       // direction the region last moved per axis, while the view is still behind its centre
        readonly float[] _landed = new float[3];    // [nav] blocks behind the centre the view was when that shift arrived
        readonly float[] _over = new float[3];      // push beyond the margin edge (clamped away), per axis
        readonly float[] _run = new float[3];       // [nav] blocks travelled in one direction (signed) without stopping, per axis
        readonly float[] _still = new float[3];     // [nav] s slower than SteadySpeed, per axis
        /// <summary>[nav] View velocity in world blocks per second (smoothed), for MeshScope's lead ahead of the window.</summary>
        public Vector3 Velocity;

        /// <summary>A new snapshot (or region_changed) defines the region. Keeps the view where it is in the world when it can.</summary>
        public void OnRegion(string world, Vector3Int origin, Vector3Int dims, Vector3Int margin, float now)
        {
            bool resized = Loaded && world == World && (dims != Dims || ClampMargin(margin, dims) != Margin);
            bool reset = !Loaded || world != World || resized;
            var moved = new Vector3Int(origin.x - Origin.x, origin.y - Origin.y, origin.z - Origin.z);
            var oldView = View;
            Loaded = true; World = world ?? World; Origin = origin; Dims = dims; Margin = ClampMargin(margin, dims);
            for (int a = 0; a < 3; a++) if (moved[a] != 0) _lastDir[a] = Math.Sign(moved[a]);
            // [nav] A glide request's round trip ends when its region is applied.
            if (Pending)
            {
                if (_pendingKind != "resize") LatencyEma = 0.7f * LatencyEma + 0.3f * Math.Max(0.05f, now - _pendingSince);
                Pending = false;
            }
            var c = RegionCentre;
            for (int a = 0; a < 3; a++) _over[a] = 0;
            if (reset)
            {
                View = c; _padRefused = false; for (int a = 0; a < 3; a++) _lastDir[a] = 0;
                // [area] A resize (width preset, padding) around where we look keeps the view where it is in the world
                // when the new window can show it (AreaSizing centres it to within 8 blocks); else (area setup moved the
                // area elsewhere) the view starts at the new centre.
                if (resized)
                {
                    var was = oldView - c;
                    bool near = Math.Abs(was.x) <= Margin.x + 0.5f && Math.Abs(was.y) <= Margin.y + 0.5f && Math.Abs(was.z) <= Margin.z + 0.5f;
                    if (near) View = c + Clamp(was, Margin);
                }
                return;
            }
            // Same size: a shift (ours or another headset's). Keep the world position; re-centre if it's far away.
            var off = View - c;
            if (Math.Abs(off.x) > Margin.x + Step || Math.Abs(off.y) > Margin.y + Step || Math.Abs(off.z) > Margin.z + Step) View = c;
            else View = c + Clamp(off, Margin);
            // [nav] How far behind the new centre the view landed: a shift back needs it to come back past that (TriggerFor).
            for (int a = 0; a < 3; a++) if (moved[a] != 0) _landed[a] = Math.Max(0f, -(View[a] - c[a]) * _lastDir[a]);
            ClearPassedDirections(View - c);
        }

        /// <summary>A view at or past the centre in the direction of the last shift has moved on: reverse shifts are normal again.</summary>
        void ClearPassedDirections(Vector3 offset)
        {
            for (int a = 0; a < 3; a++) if (_lastDir[a] != 0 && offset[a] * _lastDir[a] >= 0) _lastDir[a] = 0;
        }

        /// <summary>The server refused a request (region_too_large etc.): stop asking for a while.</summary>
        public void OnRefused(float now, string why)
        {
            if (Pending) _padRefused = true;
            Pending = false; _lastRequestAt = now + 2f; LastNote = "nav: server refused (" + why + ")";
        }

        static Vector3Int ClampMargin(Vector3Int m, Vector3Int d) =>
            new Vector3Int(Math.Max(0, Math.Min(m.x, (d.x - 1) / 2)), Math.Max(0, Math.Min(m.y, (d.y - 1) / 2)), Math.Max(0, Math.Min(m.z, (d.z - 1) / 2)));

        static Vector3 Clamp(Vector3 v, Vector3Int m) =>
            new Vector3(Math.Max(-m.x, Math.Min(m.x, v.x)), Math.Max(-m.y, Math.Min(m.y, v.y)), Math.Max(-m.z, Math.Min(m.z, v.z)));

        /// <summary>
        /// Pan speed in blocks per second: BaseSpeedM table metres per second (so it feels the same at any scale), a
        /// quadratic stick curve, a ramp while held, and a cap so the view can't outrun streaming (runway / latency)
        /// [nav] or meshing (MeshCap, at least MinMeshCap).
        /// </summary>
        public float Speed(float blockSizeM, float deflection, float heldSeconds, int axisMargin)
        {
            float d = Math.Min(1f, Math.Abs(deflection));
            float v = BaseSpeedM / Math.Max(1e-4f, blockSizeM) * d * d * (1f + RampGain * Math.Min(1f, heldSeconds / RampSeconds));
            v = Math.Max(0f, Math.Min(240f, v));
            if (axisMargin > Trigger) v = Math.Min(v, Math.Min(StreamSpeedCap(axisMargin), Math.Max(MinMeshCap, MeshCap)));
            return v;
        }

        /// <summary>
        /// Fastest sustainable pan: the blocks one round trip can cover without the view reaching the margin's edge, within
        /// ~1.3 latencies. Without lookahead a request goes out Trigger blocks past the centre and moves one section, so
        /// min(margin - Trigger, 16). [nav] With lookahead the request goes out as soon as a whole section fits behind the
        /// view and lands it near the reserve: margin - (reserve + 16) / 2 per round trip (32 blocks with a 48 margin).
        /// </summary>
        public float StreamSpeedCap(int axisMargin) => Math.Max(4f, Runway(axisMargin) / Math.Max(0.15f, LatencyEma * 1.3f));

        /// <summary>[nav] Blocks of travel per round trip the region can follow without stalling (see StreamSpeedCap).</summary>
        public float Runway(int margin) =>
            CanLookahead(margin) ? margin - (Reserve(margin) + Step) / 2f : Math.Min(Step, Math.Max(0f, margin - Trigger));

        /// <summary>[nav] Reserve kept behind the window by a lookahead shift: LookaheadReserve, less on a small margin.</summary>
        public float Reserve(int margin) => Math.Min(LookaheadReserve, margin - Step - Trigger);

        /// <summary>[nav] Lookahead needs room for a whole section plus a reserve of at least Trigger blocks (margin 24 and up).</summary>
        public bool CanLookahead(int margin) => Reserve(margin) >= Trigger;

        /// <summary>[nav] Moving steadily along an axis in direction dir (SteadyBlocks travelled that way without stopping).</summary>
        public bool Steady(int axis, int dir) => _run[axis] * dir >= SteadyBlocks;

        /// <summary>
        /// Move the view by delta world blocks over dt seconds. Returns a region_request to send (origin, dims,
        /// view_margin), or null. The view is clamped to the loaded margin, so it stalls at the edge rather than showing
        /// unloaded terrain while a request is in flight.
        /// </summary>
        public RegionMove? Move(Vector3 delta, float dt, float now)
        {
            if (!Loaded) return null;
            Track(delta, dt);
            var c = RegionCentre;
            var raw = View + delta - c;
            var clamped = Clamp(raw, Margin);
            View = c + clamped;
            ClearPassedDirections(clamped);
            // Where the view would be without the clamp: the offset plus any push piled up against the margin edge.
            var off = new Vector3();
            for (int a = 0; a < 3; a++)
            {
                float spill = raw[a] - clamped[a];
                _over[a] = spill != 0 && Math.Sign(spill) == Math.Sign(_over[a]) ? _over[a] + spill : spill;
                off[a] = clamped[a] + _over[a];
            }
            if (Pending && now - _pendingSince > RequestTimeout) { Pending = false; LastNote = "nav: request timed out"; }
            if (Pending || now - _lastRequestAt < MinRequestInterval) return null;
            if (delta.x == 0 && delta.y == 0 && delta.z == 0) return null;

            // No margin on a moving axis: pad the region once (same window, margins added), else step by sections.
            bool needPad = (delta.x != 0 && Margin.x == 0) || (delta.z != 0 && Margin.z == 0) || (delta.y != 0 && Margin.y == 0);
            if (needPad && CanPad && !_padRefused)
            {
                var pad = Pad();
                if (pad.HasValue) return Issue(pad.Value, now);
            }
            // [nav] A margin smaller than this width wants (AreaSizing.MarginFor): grow it once, keeping the window and the
            // section grid (a resize delta with just the new ring), then glide on with the longer runway.
            if (CanPad && !_padRefused && (delta.x != 0 || delta.z != 0) && Math.Min(Margin.x, Margin.z) > 0)
            {
                var grow = GrowMargin();
                if (grow.HasValue) return Issue(grow.Value, now);
            }
            var shift = new Vector3Int(AxisShift(0, off.x, delta.x, Margin.x), AxisShift(1, off.y, delta.y, Margin.y), AxisShift(2, off.z, delta.z, Margin.z));
            if (shift.y != 0)
            {
                // Never ask for a region below or above the build height (the server would clamp dims and the view would
                // jump): [nav] a lookahead shift of several sections stops at the last whole section that fits.
                int oy = Origin.y + shift.y;
                if (oy < MinY) shift.y = -Math.Max(0, (Origin.y - MinY) / Step) * Step;
                else if (MaxY != int.MaxValue && oy + Dims.y > MaxY) shift.y = Math.Max(0, (MaxY - Dims.y - Origin.y) / Step) * Step;
            }
            if (shift.x == 0 && shift.y == 0 && shift.z == 0) return null;
            for (int a = 0; a < 3; a++) if (shift[a] != 0) _over[a] = 0;
            return Issue(new RegionMove { Origin = new Vector3Int(Origin.x + shift.x, Origin.y + shift.y, Origin.z + shift.z), Dims = Dims, Margin = Margin, Kind = "shift" }, now);
        }

        /// <summary>
        /// Trigger for a shift on one axis in direction dir (+1/-1), given the last shift on that axis: a shift back needs
        /// the view Hysteresis blocks past where that shift left it ([nav] far behind the centre after a lookahead shift).
        /// </summary>
        public float TriggerFor(int axis, int dir, int margin)
        {
            if (margin <= 0) return StepPush;
            float trig = Math.Min(Trigger, margin / 2f);
            if (_lastDir[axis] != 0 && dir != _lastDir[axis])
                trig = Math.Min(Math.Max(Step - trig, _landed[axis]) + Hysteresis, margin - 0.5f);
            return trig;
        }

        int AxisShift(int axis, float off, float d, int margin)
        {
            if (d == 0) return 0;
            int dir = Math.Sign(d);
            float ahead = off * dir;   // how far the view is past the centre in the direction of motion
            // [nav] Lookahead: moving steadily this way, shift as many whole sections as fit while Reserve blocks stay
            // loaded behind the window. Not against a recent shift the other way until the view has come back past
            // where it landed (the reverse trigger), so a turn round first re-centres like a normal shift.
            if (margin > 0 && CanLookahead(margin) && Steady(axis, dir) && !(_lastDir[axis] != 0 && dir != _lastDir[axis] && ahead <= TriggerFor(axis, dir, margin)))
            {
                int nl = (int)Math.Floor((ahead + margin - Reserve(margin)) / Step);
                if (nl >= 1) return dir * nl * Step;
            }
            float trig = TriggerFor(axis, dir, margin);
            if (ahead <= trig) return 0;
            int n = margin <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling((ahead - Math.Min(Trigger, margin / 2f)) / Step));
            return dir * n * Step;
        }

        /// <summary>[nav] Distance travelled per axis in one direction (Steady) and the smoothed velocity.</summary>
        void Track(Vector3 delta, float dt)
        {
            if (dt <= 0) return;
            for (int a = 0; a < 3; a++)
            {
                float v = delta[a] / dt;
                if (Math.Abs(v) >= SteadySpeed)
                {
                    _still[a] = 0;
                    _run[a] = Math.Sign(v) == Math.Sign(_run[a]) ? _run[a] + delta[a] : delta[a];
                }
                else if ((_still[a] += dt) > SteadyGrace) _run[a] = 0;
            }
            float k = Math.Min(1f, dt / 0.15f);
            Velocity = Velocity + (delta * (1f / dt) - Velocity) * k;
        }

        /// <summary>
        /// [nav] The region with the x/z margin this width wants (AreaSizing.MarginFor), shrunk to the server's caps: the
        /// same window, the height and y margin kept, the origin moved by whole sections from the current one (centred on
        /// the view to within 8 blocks) so the server sends only the new ring. Null when the margin is already that big.
        /// </summary>
        public RegionMove? GrowMargin()
        {
            var w = Window; var m = Margin;
            int want = AreaSizing.MarginFor(Math.Max(w.x, w.z));
            int mx = Math.Max(m.x, Math.Min(want, (Caps.MaxX - w.x) / 2)), mz = Math.Max(m.z, Math.Min(want, (Caps.MaxZ - w.z) / 2));
            while ((mx > m.x || mz > m.z) && (long)(w.x + 2 * mx) * Dims.y * (w.z + 2 * mz) > Caps.MaxBlocks) { mx = Math.Max(m.x, mx - 8); mz = Math.Max(m.z, mz - 8); }
            if (mx <= m.x && mz <= m.z) return null;
            var dims = new Vector3Int(w.x + 2 * mx, Dims.y, w.z + 2 * mz);
            var o = new Vector3Int(AlignedTo(Origin.x, View.x - dims.x / 2f), Origin.y, AlignedTo(Origin.z, View.z - dims.z / 2f));
            return new RegionMove { Origin = o, Dims = dims, Margin = new Vector3Int(mx, m.y, mz), Kind = "pad" };
        }

        /// <summary>The origin closest to `ideal` that differs from `current` by whole sections.</summary>
        public static int AlignedTo(int current, float ideal) => current + (int)Math.Round((ideal - current) / Step, MidpointRounding.AwayFromZero) * Step;

        /// <summary>Region grown by MarginPref on each side (where caps and the build height allow), same window.</summary>
        public RegionMove? Pad()
        {
            var m = new Vector3Int(Math.Max(Margin.x, MarginPref.x), Math.Max(Margin.y, MarginPref.y), Math.Max(Margin.z, MarginPref.z));
            var w = Window;
            // Fit the caps: shrink the margin before giving up.
            for (int i = 0; i < 3; i++)
            {
                int cap = i == 0 ? Caps.MaxX : i == 1 ? Caps.MaxY : Caps.MaxZ;
                m[i] = Math.Max(0, Math.Min(m[i], (cap - w[i]) / 2));
            }
            int y0 = (int)Math.Floor(View.y - w.y / 2f) - m.y;
            if (y0 < MinY) { m.y = Math.Max(0, m.y - (MinY - y0)); y0 = (int)Math.Floor(View.y - w.y / 2f) - m.y; }
            if (MaxY != int.MaxValue && y0 + w.y + 2 * m.y > MaxY) m.y = Math.Max(0, (MaxY - y0 - w.y) / 2);
            var dims = new Vector3Int(w.x + 2 * m.x, w.y + 2 * m.y, w.z + 2 * m.z);
            while ((long)dims.x * dims.y * dims.z > Caps.MaxBlocks && (m.x > 0 || m.z > 0 || m.y > 0))
            {
                if (m.y > 0) m.y = Math.Max(0, m.y - Step); else { m.x = Math.Max(0, m.x - Step); m.z = Math.Max(0, m.z - Step); }
                dims = new Vector3Int(w.x + 2 * m.x, w.y + 2 * m.y, w.z + 2 * m.z);
            }
            if (m == Margin) return null;
            var o = new Vector3Int((int)Math.Floor(View.x - w.x / 2f) - m.x, (int)Math.Floor(View.y - w.y / 2f) - m.y, (int)Math.Floor(View.z - w.z / 2f) - m.z);
            return new RegionMove { Origin = o, Dims = dims, Margin = m, Kind = "pad" };
        }

        /// <summary>[area] Send a region change planned elsewhere (AreaSizing.Plan) through the same one-in-flight
        /// bookkeeping as the shifts. False while another request is pending (try again later).</summary>
        public bool TryIssue(RegionMove r, float now)
        {
            if (!Loaded) return false;
            if (Pending && now - _pendingSince > RequestTimeout) Pending = false;
            if (Pending) return false;
            Issue(r, now);
            return true;
        }

        RegionMove Issue(RegionMove r, float now)
        {
            Pending = true; _pendingSince = now; _lastRequestAt = now; Requests++; _pendingKind = r.Kind ?? "";
            LastNote = $"nav: {r.Kind} -> {r.Origin.x},{r.Origin.y},{r.Origin.z}";
            return r;
        }

        /// <summary>World block -> RegionRoot local Unity metres is (b - Origin) * bs with Z mirrored. This is RegionRoot's
        /// local position under the anchor, which sits at the window's bottom centre.</summary>
        public Vector3 RegionRootLocal(float bs)
        {
            var w = Window;
            return new Vector3((Origin.x - View.x) * bs, (Origin.y - (View.y - w.y / 2f)) * bs, -(Origin.z - View.z) * bs);
        }

        /// <summary>True if a world position is inside the visible window (players and other markers can use it).</summary>
        public bool InWindow(Vector3 p)
        {
            var a = WindowMin; var w = Window;
            return p.x >= a.x && p.x < a.x + w.x && p.y >= a.y - 1 && p.y < a.y + w.y + 1 && p.z >= a.z && p.z < a.z + w.z;
        }

        public string Describe() =>
            !Loaded ? "NAV -" :
            $"VIEW {Window.x}x{Window.y}x{Window.z} @ {View.x:F0},{View.y - Window.y / 2f:F0},{View.z:F0}  margin {Margin.x}/{Margin.y}" +
            (Pending ? "  loading..." : "") + $"  ({LatencyEma * 1000:F0} ms)";
    }

    /// <summary>A region_request the navigation wants to send (origin form plus view_margin).</summary>
    public struct RegionMove
    {
        public Vector3Int Origin, Dims, Margin;
        public string Kind;   // "shift" or "pad"
    }
}
