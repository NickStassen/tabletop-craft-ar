// [operator] Jumping and following, engine-free (Tests~). docs/QUEST_SETUP.md "Marks, follow, containers and signs".
//
// A jump moves the shared region somewhere else at once (typed coordinates, the spawn, a bookmark, another world, a
// player far away): an ordinary region_request (PROTOCOL.md 4.13, origin form with view_margin) that keeps the visible
// width, the margins and (where the world allows it) the height, centred on the target. In the same world the origin
// moves by whole 16-block sections from the current one, so the server reads only the new sections and may send a delta;
// another world is always a full snapshot, and region_changed.limits brings its build height. When the region arrives,
// the view is put on the target itself (JumpFocus), not only near it.
//
// Following a player is client-side (the server's subscribe.follow is not supported): while the player is on the table
// the view glides after their eased position (the figure's), easing out over about a second, with a small dead zone and
// a vertical band so walking around doesn't bob the table; when they are far away or in another world (region_query
// once a second tells where), the view jumps. Any manual glide, area setup, Reset view, a jump from the Marks tab or the
// panel's Stop ends it; so does the player leaving the server.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class Jumps
    {
        /// <summary>
        /// The region_request of a jump to <paramref name="target"/> (world block coordinates) in <paramref name="world"/>
        /// with build height [minY, maxY) (minY == maxY: unknown, no clamp): the current x/z size and margins; the height
        /// and y margin shrunk to fit that world; the window centred on the target (y: the target in the middle of the
        /// window, clamped to the build height). The same world moves the origin by whole sections (an incremental read);
        /// another world takes the nearest origin. Null before the first region.
        /// </summary>
        public static RegionMove? Plan(NavState nav, string world, Vector3 target, int minY, int maxY)
        {
            if (nav == null || !nav.Loaded) return null;
            var dims = nav.Dims; var m = nav.Margin;
            int height = maxY > minY ? maxY - minY : dims.y;
            int dy = Math.Max(1, Math.Min(dims.y, height));
            int my = Math.Max(0, Math.Min(m.y, (dy - 1) / 2));
            int wy = dy - 2 * my;
            bool same = world == nav.World;
            float ix = target.x - dims.x / 2f, iz = target.z - dims.z / 2f, iy = target.y - wy / 2f - my;
            int ox = same ? NavState.AlignedTo(nav.Origin.x, ix) : (int)Math.Floor(ix);
            int oz = same ? NavState.AlignedTo(nav.Origin.z, iz) : (int)Math.Floor(iz);
            int oy = same && dy == dims.y ? NavState.AlignedTo(nav.Origin.y, iy) : (int)Math.Floor(iy);
            if (maxY > minY) oy = Math.Max(minY, Math.Min(maxY - dy, oy));
            return new RegionMove { Origin = new Vector3Int(ox, oy, oz), Dims = new Vector3Int(dims.x, dy, dims.z), Margin = new Vector3Int(m.x, my, m.z), Kind = "jump" };
        }

        /// <summary>
        /// Typed coordinates: "x z" or "x y z" (spaces or commas), each a whole number, or "~" / "~5" relative to the view
        /// centre. Returns null and the position (the block's centre; y = the view's centre when not typed), or why it
        /// can't be read.
        /// </summary>
        public static string ParseCoords(string text, Vector3 view, out Vector3 pos, out bool hasY)
        {
            pos = view; hasY = false;
            var parts = (text ?? "").Replace(',', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 && parts.Length != 3) return "type x z or x y z (whole numbers, ~ for here)";
            var v = new float[parts.Length];
            float[] here = parts.Length == 2 ? new[] { view.x, view.z } : new[] { view.x, view.y, view.z };
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                bool rel = p.StartsWith("~");
                if (rel) p = p.Substring(1);
                double d = 0;
                if (p.Length > 0 && !double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                    return "\"" + parts[i] + "\" isn't a number";
                if (!rel && p.Length == 0) return "\"" + parts[i] + "\" isn't a number";
                if (Math.Abs(d) > 30_000_000) return "that's outside the world (30 million blocks)";
                v[i] = rel ? here[i] + (float)d : (float)Math.Floor(d) + 0.5f;
            }
            hasY = parts.Length == 3;
            pos = hasY ? new Vector3(v[0], v[1], v[2]) : new Vector3(v[0], view.y, v[1]);
            return null;
        }
    }

    /// <summary>
    /// A jump in flight: when its region is applied, the view goes onto the target (clamped to the margin), not the
    /// region's centre (the section-aligned origin is up to 8 blocks off).
    /// </summary>
    public sealed class JumpFocus
    {
        public string World;
        public Vector3 Target;
        public Vector3Int Origin;
        public float Since = -1;
        public float Timeout = 15f;
        public bool Pending => Since >= 0;

        public void Set(string world, Vector3 target, Vector3Int origin, float now) { World = world; Target = target; Origin = origin; Since = now; }
        public void Clear() => Since = -1;

        /// <summary>After NavState.OnRegion: the jump's region (its world and x/z origin) arrived, so the view goes on the target. True when it did.</summary>
        public bool Apply(NavState nav, float now)
        {
            if (!Pending || nav == null || !nav.Loaded) return false;
            if (now - Since > Timeout) { Clear(); return false; }
            if (nav.World != World || nav.Origin.x != Origin.x || nav.Origin.z != Origin.z) return false;
            Clear();
            nav.Recenter();   // no lookahead or hysteresis left over from before the jump
            var c = nav.RegionCentre; var m = nav.Margin;
            var off = Target - c;
            nav.View = new Vector3(c.x + Mathf.Clamp(off.x, -m.x, m.x), c.y + Mathf.Clamp(off.y, -m.y, m.y), c.z + Mathf.Clamp(off.z, -m.z, m.z));
            return true;
        }
    }

    /// <summary>What following wants this frame: a glide delta (world blocks), or a jump (to send through NavState.TryIssue).</summary>
    public struct FollowStep
    {
        public Vector3 Delta;
        public RegionMove? Jump;
        public string JumpWorld;
        public Vector3 JumpTarget;
        /// <summary>Ask the server where the player is (region_query with the player's name).</summary>
        public bool Query;
    }

    public sealed class FollowState
    {
        // Tuning (QUEST_SETUP.md "Following a player").
        /// <summary>Fraction of the offset closed per second, exponentially (about 90% in a second).</summary>
        public float Ease = 2.3f;
        /// <summary>Blocks (x/z) the player may wander from the centre before the view moves.</summary>
        public float DeadZone = 0.75f;
        /// <summary>Fraction of the window height the player may be off-centre vertically before the view re-centres on them.</summary>
        public float VerticalBand = 0.25f;
        /// <summary>Blocks beyond the loaded region (the window plus margin) at which the view jumps instead of gliding.</summary>
        public float JumpBeyond = 16f;
        /// <summary>s between region_query asks while the player isn't on the table, and while they are (to notice a world change).</summary>
        public float QueryEvery = 1f, QueryWhileSeen = 5f;
        /// <summary>The figure counts as on the table while it was seen this recently (s).</summary>
        public float SeenFor = 0.5f;

        public bool Active { get; private set; }
        public string Name { get; private set; }
        public string Uuid { get; private set; }
        public string TargetWorld { get; private set; }
        public Vector3 Target { get; private set; }
        public bool HasTarget { get; private set; }
        /// <summary>Why following stopped, or what it does now (the panel's note).</summary>
        public string Note = "";
        public int JumpCount { get; private set; }
        public readonly JumpFocus Focus = new JumpFocus();
        public bool Once => _once;

        float _seenAt = -100, _lastQuery = -100, _jumpAt = -100;
        bool _recentering, _once;
        readonly HashSet<long> _queries = new HashSet<long>();

        /// <summary>Follow a player (by name for region_query, uuid for the figure on the table).</summary>
        public void Start(string name, string uuid, float now) => Begin(name, uuid, false);

        /// <summary>Jump to the player once (Players tab "Go to"), without following.</summary>
        public void GoTo(string name, string uuid, float now) => Begin(name, uuid, true);

        void Begin(string name, string uuid, bool once)
        {
            Active = true; _once = once; Name = name; Uuid = uuid; HasTarget = false; _recentering = false; JumpCount = 0;
            _seenAt = -100; _lastQuery = -100; _jumpAt = -100; Velocity = Vector3.zero;
            Note = (once ? "going to " : "following ") + name;
        }

        public void Stop(string why)
        {
            if (!Active) return;
            Active = false; _once = false; HasTarget = false; _queries.Clear();
            Note = why == null ? "" : "stopped following " + Name + ": " + why;
        }

        /// <summary>The followed player's figure on the table this frame (eased world position, from player_positions).</summary>
        public void OnSeen(string world, Vector3 pos, float now)
        {
            if (!Active) return;
            // [smooth] The player's velocity (x/z, smoothed over about 0.3 s) for NavState.Lead: lookahead from where the
            // player is going. A teleport, another world or a gap in sightings resets it.
            float dt = now - _seenAt;
            if (HasTarget && world == TargetWorld && dt > 1e-3f && dt < 0.5f)
            {
                var v = (pos - Target) / dt; v.y = 0;
                if (v.sqrMagnitude > 60f * 60f) Velocity = Vector3.zero;
                else Velocity += (v - Velocity) * Math.Min(1f, dt / 0.3f);
            }
            else Velocity = Vector3.zero;
            TargetWorld = world; Target = pos; HasTarget = true; _seenAt = now;
        }

        /// <summary>[smooth] The followed player's smoothed x/z velocity on the table (world blocks/s; zero when unknown).</summary>
        public Vector3 Velocity { get; private set; }

        public bool Seen(float now) => now - _seenAt <= SeenFor;

        /// <summary>A region_query this state sent (by the LinkClient seq), so its reply and errors come back here.</summary>
        public void Sent(long seq) { if (seq >= 0) { _queries.Add(seq); if (_queries.Count > 32) _queries.Clear(); } }
        public bool Owns(long refSeq) => refSeq >= 0 && _queries.Contains(refSeq);

        /// <summary>region_info answering our region_query: where the player is (unless the figure on the table says it better).</summary>
        public bool OnRegionInfo(RegionInfo info, long refSeq, float now)
        {
            if (!Owns(refSeq)) return false;
            _queries.Remove(refSeq);
            if (!Active) return true;
            foreach (var p in info.Players)
                if (p.Uuid == Uuid || string.Equals(p.Name, Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Seen(now) || p.World != TargetWorld) { TargetWorld = p.World; Target = p.Pos; HasTarget = true; }
                    return true;
                }
            Stop(Name + " isn't online");
            return true;
        }

        /// <summary>An error answering our region_query (the player left: "no online player named ..."). True when it was ours.</summary>
        public bool OnError(long refSeq, string message)
        {
            if (!Owns(refSeq)) return false;
            _queries.Remove(refSeq);
            if (Active) Stop(Name + " left the server");
            return true;
        }

        /// <summary>
        /// One frame of following: a glide delta for NavState.Move, or a jump (the caller sends it through
        /// NavState.TryIssue and then calls Jumped). <paramref name="worlds"/> gives another world's build height
        /// (welcome.worlds); the current world uses NavState's.
        /// </summary>
        public FollowStep Step(NavState nav, float dt, float now, Func<string, WorldEntry?> worlds)
        {
            var s = new FollowStep();
            if (!Active || nav == null || !nav.Loaded) return s;
            if (now - _lastQuery >= (Seen(now) ? QueryWhileSeen : QueryEvery)) { s.Query = true; _lastQuery = now; }
            // A jump is on its way. [smooth] A glide shift in flight no longer pauses the glide: NavState keeps the view inside
            // the loaded margin, and pausing stopped the view for every round trip (a stop-go follow that never got steady
            // enough for lookahead).
            if (Focus.Pending || !HasTarget) return s;
            bool otherWorld = TargetWorld != null && TargetWorld != nav.World;
            var off = Target - nav.View;
            bool far = Math.Abs(off.x) > nav.Window.x / 2f + nav.Margin.x + JumpBeyond || Math.Abs(off.z) > nav.Window.z / 2f + nav.Margin.z + JumpBeyond;
            if (otherWorld || far || (_once && JumpCount == 0))
            {
                if (nav.Pending || now - _jumpAt < 1f) return s;   // a request in flight, or the last jump's region hasn't settled yet
                int minY = nav.MinY, maxY = nav.MaxY;
                if (otherWorld)
                {
                    var w = worlds?.Invoke(TargetWorld);
                    if (w.HasValue) { minY = w.Value.MinY; maxY = w.Value.MaxY; } else { minY = 0; maxY = 0; }
                }
                else if (maxY == int.MaxValue) { minY = 0; maxY = 0; }
                string world = TargetWorld ?? nav.World;
                var j = Jumps.Plan(nav, world, Target, minY, maxY);
                if (j.HasValue) { s.Jump = j; s.JumpWorld = world; s.JumpTarget = Target; }
                return s;
            }
            float k = 1f - (float)Math.Exp(-Ease * Math.Max(0f, dt));
            var d = new Vector3();
            if (off.x * off.x + off.z * off.z > DeadZone * DeadZone) { d.x = off.x * k; d.z = off.z * k; }
            float band = Math.Max(2f, nav.Window.y * VerticalBand);
            if (Math.Abs(off.y) > band) _recentering = true;
            if (_recentering)
            {
                d.y = off.y * k;
                if (Math.Abs(off.y) < 1f) _recentering = false;
            }
            s.Delta = d;
            return s;
        }

        /// <summary>The jump Step asked for went out: the view goes onto the player when its region arrives.</summary>
        public void Jumped(RegionMove move, string world, Vector3 target, float now)
        {
            Focus.Set(world, target, move.Origin, now);
            JumpCount++; _jumpAt = now; _recentering = false;
            if (_once) { Active = false; _once = false; Note = "went to " + Name; }
        }

        /// <summary>The panel's line: who is followed, or why following stopped.</summary>
        public string Describe() => Active ? (_once ? "going to " : "following ") + Name : Note;
    }
}
