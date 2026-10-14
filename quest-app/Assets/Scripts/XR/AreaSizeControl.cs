// [area] Visible-width presets in the headset (docs/QUEST_SETUP.md "Area size presets", PROTOCOL.md 4.25).
//
//   Left thumbstick click     next width preset (64 -> 96 -> 128 -> 192 -> 256 -> 64), not while area setup (Y) is open
//
// The three operations a menu can call (the coordinator wires Func<int[]> AreaPresets, Func<int> AreaCurrent and
// Action<int> AreaRequest to them): Presets(), Current(), Request(width). The resize itself is engine-free
// (World/AreaSizing.cs): the new window is centred where the view is now, the origin moves by whole sections so the
// renderer re-keys every section mesh it has (growing meshes only the new ring, shrinking drops the outer sections),
// and the server answers with a delta. When the new window arrives the block size is scaled by old/new width, so
// the miniature keeps its footprint on the table; the right stick still zooms afterwards.
//
// Measurement hooks (logcat, "[Tabletop]"):
//   tabletop.json "area_width": 192   start at that preset, with the block size fitted to the table (like area setup's
//                                     auto-fit), so every preset covers the same part of the table
//   AREA_LOADED ...                   once a new area is fully meshed and uploaded: batches, draw calls, vertices
//   PERF ...                          every 5 s: width, batches, vertices, frame time percentiles (unscaledDeltaTime)
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    public sealed class AreaSizeControl : MonoBehaviour
    {
        public NavState Nav;
        public TabletopRenderer Renderer;
        public Func<bool> Blocked;                 // true while area setup owns the controllers
        public Action<Dictionary<string, object>> Send;
        public Func<float> BlockSize;
        public Action<float> SetBlockSize;
        public Func<Vector2> TableSize;            // usable table top in metres (TablePlacement)
        public int StartWidth;                     // tabletop.json "area_width" (0 = keep the server's area)
        public float PerfLogSeconds = 5f;
        public RegionLimits Limits = RegionLimits.Default;

        public string Status { get; private set; } = "";
        string _note = "";

        // ---- the menu's three operations ----
        /// <summary>Widths this server allows, smallest first (AreaPresets).</summary>
        public int[] Presets() => AreaSizing.Presets(Limits, Margin);
        /// <summary>Visible width now, or the one being loaded (AreaCurrent).</summary>
        public int Current() => _awaiting ? _sentWidth : AreaSizing.Current(Nav);
        /// <summary>Ask for a visible width (AreaRequest); snapped to whole sections and the server's max, sent when no other region request is in flight.</summary>
        public void Request(int width) { if (width > 0) _wanted = width; }

        int Margin => Nav != null ? Math.Max(Nav.MarginPref.x, Nav.MarginPref.z) : 16;
        int _wanted, _sentWidth;
        bool _awaiting, _started, _fitOnArrival, _loading;
        float _sentAt, _loadFrom, _perfFrom = -1;
        readonly FrameStats _frames = new FrameStats(2048);

        public void OnLimits(Dictionary<string, object> limits) { if (limits != null) Limits = RegionLimits.From(limits, Limits); }

        public void OnError(Dictionary<string, object> m)
        {
            if (!_awaiting || m.S("code") != "region_too_large") return;
            _awaiting = false; _fitOnArrival = false;
            OnLimits(m);
            _note = "server refused: " + m.S("message");
        }

        /// <summary>A snapshot was applied (TabletopApp.ApplySnapshot, after NavController.OnSnapshot). oldWidth = the visible width before it.</summary>
        public void OnRegionApplied(int oldWidth)
        {
            int now = AreaSizing.Current(Nav);
            if (now != oldWidth) { _loading = true; _loadFrom = Time.unscaledTime; }
            if (!_awaiting || Nav.Pending) return;
            _awaiting = false;
            if (_fitOnArrival)
            {
                _fitOnArrival = false;
                FitToTable(now);
            }
            else if (now != oldWidth && oldWidth > 0) SetBlockSize?.Invoke(AreaSizing.ScaleBlockSize(BlockSize(), oldWidth, now));
            _note = now == _sentWidth ? "" : $"server set width {now} (asked {_sentWidth})";
            Debug.Log($"[Tabletop] AREA_RESIZED width {oldWidth} -> {now} (asked {_sentWidth}) block {BlockSize() * 1000:F2} mm in {(Time.unscaledTime - _sentAt) * 1000:F0} ms");
        }

        void FitToTable(int width)
        {
            var t = TableSize != null ? TableSize() : new Vector2(0.8f, 0.6f);
            float bs = AreaMath.FitBlockSize(t.x, t.y, width, width);
            SetBlockSize?.Invoke(Mathf.Clamp(bs, AreaSizing.MinBlockM, AreaSizing.MaxBlockM));
        }

        void Update()
        {
            float now = Time.unscaledTime;
            Perf(now);
            if (Nav == null || !Nav.Loaded) { Status = ""; return; }
            if (!_started)
            {
                _started = true;
                if (StartWidth > 0)
                {
                    int w = AreaSizing.Snap(StartWidth, AreaSizing.MaxWidth(Limits, Margin));
                    if (w != AreaSizing.Current(Nav)) { _wanted = w; _fitOnArrival = true; }
                    else FitToTable(w);
                }
            }
            bool blocked = Blocked != null && Blocked();
            if (!blocked && OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch))
                Request(AreaSizing.Next(Presets(), _wanted > 0 ? _wanted : Current()));
            if (_wanted > 0 && !blocked)
            {
                var plan = AreaSizing.Plan(Nav, _wanted, Limits);
                if (plan == null) { _note = $"width {_wanted}: nothing to change"; _wanted = 0; _fitOnArrival = false; }
                else if (Nav.TryIssue(plan.Value, now))
                {
                    var p = plan.Value;
                    Send?.Invoke(Protocol.RegionRequest(Nav.World, p.Origin, p.Dims, p.Margin));
                    _sentWidth = Math.Max(p.Dims.x - 2 * p.Margin.x, p.Dims.z - 2 * p.Margin.z);
                    _awaiting = true; _sentAt = now; _wanted = 0; _note = "";
                    Debug.Log($"[Tabletop] AREA_REQUEST width {AreaSizing.Current(Nav)} -> {_sentWidth}: origin {p.Origin} dims {p.Dims.x}x{p.Dims.y}x{p.Dims.z} margin {p.Margin.x}/{p.Margin.y}");
                }
            }
            if (_awaiting && now - _sentAt > Nav.RequestTimeout + 1f) { _awaiting = false; _fitOnArrival = false; _note = "resize timed out"; }
            if (_loading && Renderer && Renderer.Idle)
            {
                _loading = false;
                Debug.Log($"[Tabletop] AREA_LOADED width={AreaSizing.Current(Nav)} region={Nav.Dims.x}x{Nav.Dims.y}x{Nav.Dims.z} load_s={now - _loadFrom:F1} faces={Renderer.Quads} {Renderer.Stats()} batch_columns={BatchRules.Columns}");
            }
            Status = AreaSizing.Describe(AreaSizing.Current(Nav), Presets(), AreaSizing.MaxWidth(Limits, Margin), _awaiting ? _sentWidth : _wanted) +
                     (_note.Length > 0 ? "  " + _note : "");
        }

        /// <summary>Frame times every frame; a PERF line every PerfLogSeconds (read next to VrApi's FPS lines in logcat).</summary>
        void Perf(float now)
        {
            _frames.Add(Time.unscaledDeltaTime * 1000f);
            if (_perfFrom < 0) { _perfFrom = now; return; }
            if (now - _perfFrom < PerfLogSeconds) return;
            float secs = now - _perfFrom;
            var st = Renderer ? Renderer.Stats() : default;
            Debug.Log($"[Tabletop] PERF width={AreaSizing.Current(Nav)} frames={_frames.Count} fps={_frames.Count / secs:F1} frame_ms p50={_frames.Percentile(0.5f):F1} " +
                      $"p90={_frames.Percentile(0.9f):F1} p99={_frames.Percentile(0.99f):F1} max={_frames.Max():F1} over15ms={_frames.Over(15f)} " +
                      $"faces={(Renderer ? Renderer.Quads : 0)} pending={(Renderer ? Renderer.Pending : 0)} {st}");
            _frames.Clear();
            _perfFrom = now;
        }
    }
}
