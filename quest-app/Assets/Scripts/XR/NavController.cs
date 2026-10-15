// Moving around the world on the table (docs/QUEST_SETUP.md "Moving around"). Drives the engine-free NavState from
// the controllers, keeps RegionRoot placed so the visible window sits on the anchor, clips the block shaders to
// that window (global _TabletopClip, see Shaders/TabletopClip.cginc), keeps the cut sides solid (ClipCaps), and
// sends region_request when the view needs the region shifted.
//
//   Left stick                 glide across the world, relative to where you look (forward = away from you)
//   Left grip + left stick     up / down (stick forward = up), sideways still works
//   (Right stick X turns the table and right stick Y scales it: TablePlacement. Both grips = drag the table, so the
//   left stick is ignored while both are held.)
// Locomotion stays on the sticks and grips: the index triggers and B belong to block editing.
// Area setup (Y) owns the sticks while it is active.
// [operator] Jumps (the operator panel's Marks tab: a world, the spawn, a bookmark, typed coordinates) and following a
// player (Players: Follow / Go to) are driven from here too (World/FollowState.cs): the follow's glide goes through the
// same NavState.Move as the stick, its jumps and the Marks jumps through NavState.TryIssue, and the view lands on the
// target when the jump's region is applied. Any stick glide, area setup or Reset view stops following.
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
        public string Status => State.Describe() + (State.LastNote.Length > 0 ? "  " + State.LastNote : "") + (Follow.Active ? "  " + Follow.Describe() : "");
        // [operator] following a player, jumps
        public readonly FollowState Follow = new FollowState();
        public Func<string, WorldEntry?> WorldInfo;                    // another world's build height (welcome.worlds)
        public delegate bool ShownFn(string uuid, out Vector3 worldPos);
        public ShownFn PlayerShown;                                    // a player's eased figure on the table (PlayerMarkers)
        public Func<Dictionary<string, object>, long> SendTracked;     // region_query (its reply comes back by ref_seq)
        public Func<bool> FollowBlocked;                               // area setup: following stops
        public Action<string> FollowNote;                              // why following stopped (a toast)
        string _jumpWorld; Vector3 _jumpTarget; float _jumpUntil = -1;   // a jump waiting for the one-in-flight slot

        static readonly int ClipId = Shader.PropertyToID("_TabletopClip");
        float _held;
        float _logT, _logDist, _logWant, _logHoles, _logStream;   // [nav] GLIDE log line over 5 s of stick time

        /// <summary>[nav] logcat: how fast the glide really went over the last 5 s of stick time, and what limited it.</summary>
        void LogGlide(float bs)
        {
            float v = _logDist / _logT;
            Debug.Log($"[Tabletop] GLIDE width={Mathf.Max(State.Window.x, State.Window.z)} margin={State.Margin.x} blocks_s={v:F1} table_cm_s={v * bs * 100:F1} " +
                      $"asked_blocks_s={_logWant / _logT:F1} stream_cap={_logStream:F0} mesh_cap={(State.MeshCap > 1e6f ? "-" : State.MeshCap.ToString("F0"))} " +
                      $"mesh_rate_s={(Renderer ? Renderer.MeshRate : 0):F0} latency_ms={State.LatencyEma * 1000:F0} holes_s={_logHoles:F1} " +
                      $"pending={(Renderer ? Renderer.Pending : 0)} requests={State.Requests} " +
                      $"mesh_ms={(Renderer ? Renderer.MsPerSection : 0):F2} per_job={(Renderer ? Renderer.LastPerJob : 0)} skirt_tiles={(Renderer ? Renderer.SkirtTiles : 0)}");   // [mesh] worker time per section, job size, skirt
            _logT = _logDist = _logWant = _logHoles = 0;
        }

        void OnDisable() => Shader.SetGlobalMatrix(ClipId, Matrix4x4.zero);

        /// <summary>A snapshot was applied: adopt the region (keeps the view where it is in the world when possible).</summary>
        public void OnSnapshot(string world, Vector3Int origin, Vector3Int dims, Vector3Int viewMargin)
        {
            State.OnRegion(world, origin, dims, viewMargin, Time.unscaledTime);
            if (Follow.Focus.Apply(State, Time.unscaledTime))   // [operator] a jump's region: the view lands on the target
                Debug.Log($"[Tabletop] JUMP landed {world} {State.View.x:F1},{State.View.y:F1},{State.View.z:F1}");
            Place();
        }

        /// <summary>
        /// [operator] Jump the view to a world position (the Marks tab): a region_request that keeps the width, centred on the
        /// target, in its world (another world: its build height from welcome.worlds). Stops following. Waits up to 5 s
        /// for a glide request in flight. False when there is no area yet.
        /// </summary>
        public bool JumpTo(string world, Vector3 target)
        {
            if (!State.Loaded) return false;
            if (Follow.Active) StopFollow("jumped elsewhere");
            _jumpWorld = string.IsNullOrEmpty(world) ? State.World : world; _jumpTarget = target; _jumpUntil = Time.unscaledTime + 5f;
            TryJump(Time.unscaledTime);
            return true;
        }

        void TryJump(float now)
        {
            if (_jumpUntil < 0) return;
            if (now > _jumpUntil) { _jumpUntil = -1; FollowNote?.Invoke("Jump didn't go out (the area was busy)"); return; }
            int minY = State.MinY, maxY = State.MaxY;
            if (_jumpWorld != State.World) { var w = WorldInfo?.Invoke(_jumpWorld); if (w.HasValue) { minY = w.Value.MinY; maxY = w.Value.MaxY; } else { minY = 0; maxY = 0; } }
            else if (maxY == int.MaxValue) { minY = 0; maxY = 0; }
            var mv = Jumps.Plan(State, _jumpWorld, _jumpTarget, minY, maxY);
            if (!mv.HasValue || !State.TryIssue(mv.Value, now)) return;
            _jumpUntil = -1;
            SendJump(_jumpWorld, mv.Value, _jumpTarget, now);
        }

        void SendJump(string world, RegionMove mv, Vector3 target, float now)
        {
            Send?.Invoke(Protocol.RegionRequest(world, mv.Origin, mv.Dims, mv.Margin));
            Follow.Focus.Set(world, target, mv.Origin, now);
            Debug.Log($"[Tabletop] JUMP {world} {target.x:F1},{target.y:F1},{target.z:F1} -> origin {mv.Origin.x},{mv.Origin.y},{mv.Origin.z} dims {mv.Dims.x}x{mv.Dims.y}x{mv.Dims.z}");
        }

        /// <summary>[operator] Follow a player (Players tab), jump to them once, or stop.</summary>
        public void FollowCommand(string op, string name, string uuid)
        {
            if (op == "stop") { StopFollow("stopped from the panel"); return; }
            if (string.IsNullOrEmpty(name)) return;
            _jumpUntil = -1;
            if (op == "goto") Follow.GoTo(name, uuid, Time.unscaledTime); else Follow.Start(name, uuid, Time.unscaledTime);
            Debug.Log($"[Tabletop] FOLLOW {op} {name}");
        }

        public void StopFollow(string why)
        {
            if (!Follow.Active) return;
            Follow.Stop(why);
            Debug.Log("[Tabletop] FOLLOW " + Follow.Note);
            FollowNote?.Invoke(char.ToUpperInvariant(Follow.Note[0]) + Follow.Note.Substring(1));
        }

        /// <summary>[operator] Our region_query's answer or error (by ref_seq); true when it was the follow's.</summary>
        public bool OnFollowReply(Dictionary<string, object> m)
        {
            long refSeq = (long)m.D("ref_seq", -1);
            if (!Follow.Owns(refSeq)) return false;
            bool was = Follow.Active;
            if (m.S("type") == "region_info") Follow.OnRegionInfo(RegionInfo.From(m), refSeq, Time.unscaledTime);
            else Follow.OnError(refSeq, m.S("message"));
            if (was && !Follow.Active && !Follow.Note.StartsWith("went")) { Debug.Log("[Tabletop] FOLLOW " + Follow.Note); FollowNote?.Invoke(char.ToUpperInvariant(Follow.Note[0]) + Follow.Note.Substring(1)); }
            return true;
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

        public void OnRegionError(string message)
        {
            State.OnRefused(Time.unscaledTime, message);
            Follow.Focus.Clear();   // [operator] a refused jump lands nowhere
        }

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
                bool leftGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
                bool dragging = leftGrip && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
                if (mag > DeadZone && !dragging)
                {
                    if (Follow.Active) StopFollow("manual glide");   // [operator] the stick takes over
                    _held += dt;
                    bool vertical = leftGrip;
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
                    // [nav] No faster than meshing can fill the window in this direction (world axes: Z mirrored).
                    State.MeshCap = Renderer ? MeshScope.MeshSpeedCap(Renderer.MeshRate, Renderer.Scope, new Vector3(dir.x, dir.y, -dir.z)) : float.MaxValue;
                    float speed = State.Speed(bs, rescaled, _held, m);
                    Vector3 u = dir * speed * dt;                    // anchor-local Unity axes, in blocks
                    delta = new Vector3(u.x, u.y, -u.z);              // -> world blocks (Z mirrored)
                    _logT += dt; _logWant += speed * dt; _logStream = State.StreamSpeedCap(m);
                }
                else _held = 0;
            }
            // [operator] following a player: its glide (when the stick is idle), its queries and jumps; a Marks jump waiting
            float now = Time.unscaledTime;
            if (Follow.Active && (FollowBlocked?.Invoke() ?? false)) StopFollow("area setup");
            if (Follow.Active && delta == Vector3.zero)
            {
                if (Follow.Uuid != null && PlayerShown != null && PlayerShown(Follow.Uuid, out var seen)) Follow.OnSeen(State.World, seen, now);
                var st = Follow.Step(State, dt, now, WorldInfo);
                if (st.Query && SendTracked != null) Follow.Sent(SendTracked(Protocol.RegionQuery(Follow.Name)));
                if (st.Jump.HasValue)
                {
                    if (State.TryIssue(st.Jump.Value, now))
                    {
                        SendJump(st.JumpWorld, st.Jump.Value, st.JumpTarget, now);
                        Follow.Jumped(st.Jump.Value, st.JumpWorld, st.JumpTarget, now);
                    }
                }
                else delta = st.Delta;
            }
            TryJump(now);
            var before = State.View;
            var move = State.Move(delta, dt, Time.unscaledTime);
            if (delta != Vector3.zero && _held > 0)   // [operator] the stick's glide only (not following)
            {
                _logDist += (State.View - before).magnitude;
                if (Renderer && Renderer.PendingVisible > 0) _logHoles += dt;   // sections on screen not meshed yet
            }
            if (_logT >= 5f) LogGlide(bs);
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
            if (Renderer)
            {
                Renderer.Focus = State.View - (Vector3)State.Origin;
                // [nav] Mesh only near the window (and further ahead of the motion); the rest of the margin waits.
                var n = Renderer.Model != null && Renderer.Model.Loaded ? Renderer.Model.SectionCount : SectionPlan.Sections(State.Dims);
                Renderer.Scope = MeshScope.Active(State, n);
                Renderer.KeepScope = MeshScope.Keep(State, n);
                Renderer.VisibleScope = MeshScope.Visible(State, n);
                Renderer.Motion = State.Velocity;
                var (wlo, whi) = MeshScope.Window(State);   // [mesh] the skirt shows the first block below the window top
                Renderer.SkirtBottom = Mathf.FloorToInt(wlo.y); Renderer.SkirtTop = Mathf.CeilToInt(whi.y);
            }
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
            BlockKeywords.Set(BlockKeywords.ClipPlanes, BlockKeywords.UseClipPlanes);   // [gpu] block shaders: hardware clip planes
            if (Caps)
            {
                // While a new region is meshed behind the old one, the old terrain shows as it is (no caps for the new box).
                Vector3 lo = State.WindowMin - (Vector3)State.Origin;
                Caps.UpdateCaps(lo, lo + (Vector3)w, !(Renderer && Renderer.Staging));
            }
        }
    }
}
