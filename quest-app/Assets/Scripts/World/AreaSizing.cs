// [area] Visible-width presets (docs/QUEST_SETUP.md "Area size presets", PROTOCOL.md 4.25). Engine-free.
//
// The user picks how wide the visible window is (x and z, in blocks) from a few presets; the height stays what it
// is. A resize asks for the region that puts the new window around where the view is now, with the same 16-block
// view margin, and with the origin moved by whole 16-block sections from the current one: the server can then send
// a delta (only the new ring when growing, nothing when shrinking) and the renderer keeps every section mesh it
// already has (SectionPlan re-keys them). The block size is scaled by oldWidth / newWidth when the new window
// arrives, so the miniature keeps its footprint on the table and the user's zoom stays what they chose.
using System;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public static class AreaSizing
    {
        /// <summary>Visible widths (x and z) the left-stick click steps through.</summary>
        public static readonly int[] All = { 64, 96, 128, 192, 256 };
        public const int MinWidth = 16;

        /// <summary>
        /// [nav] View margin per side (x and z) for a visible width: a wider window needs more runway to glide fast (the
        /// region round trip grows with its size; TESTING.md "Faster gliding"). Pairs of (from width, margin), ascending.
        /// The margin is shrunk to fit the server's caps; presets are always sized with the minimum (NavState.MarginPref).
        /// </summary>
        /// [smooth] 32 from the smallest width (it was 16 up to 96 wide): the margin is what makes lookahead and the
        /// fewer, bigger shifts possible (NavState.LookaheadSections); at 96 wide a shift every 16 blocks hitched.
        public static (int width, int margin)[] MarginByWidth = { (0, 32), (256, 48) };

        /// <summary>[nav] The margin this width wants (MarginByWidth), before the server's caps.</summary>
        public static int MarginFor(int width)
        {
            int m = MarginByWidth.Length > 0 ? MarginByWidth[0].margin : 16;
            foreach (var (w, mm) in MarginByWidth) if (width >= w) m = mm;
            return m;
        }

        /// <summary>Largest window the server accepts: limits.max_view_width, and the region with margin m on both
        /// sides must fit max_dims.x/z.</summary>
        public static int MaxWidth(RegionLimits caps, int margin)
        {
            int byDims = Math.Min(caps.MaxX, caps.MaxZ) - 2 * Math.Max(0, margin);
            int w = caps.MaxViewWidth > 0 ? Math.Min(caps.MaxViewWidth, byDims) : byDims;
            return Math.Max(MinWidth, w);
        }

        /// <summary>The presets this server allows (never empty: at least the smallest one, capped to the max).</summary>
        public static int[] Presets(RegionLimits caps, int margin)
        {
            int max = MaxWidth(caps, margin);
            int n = 0;
            foreach (int p in All) if (p <= max) n++;
            if (n == 0) return new[] { Math.Min(All[0], max) };
            var r = new int[n];
            for (int i = 0, j = 0; i < All.Length; i++) if (All[i] <= max) r[j++] = All[i];
            return r;
        }

        /// <summary>Current visible width: the window's larger horizontal side (0 before the first snapshot).</summary>
        public static int Current(NavState nav) => nav != null && nav.Loaded ? Math.Max(nav.Window.x, nav.Window.z) : 0;

        /// <summary>The preset after `current` (the first one bigger than it), wrapping to the smallest.</summary>
        public static int Next(int[] presets, int current)
        {
            foreach (int p in presets) if (p > current) return p;
            return presets[0];
        }

        /// <summary>Snap a requested width (a slider may send anything) to whole sections within [MinWidth, max].</summary>
        public static int Snap(int width, int max)
        {
            int w = (int)Math.Round(width / 16.0) * 16;
            return Math.Max(MinWidth, Math.Min(Math.Max(MinWidth, max / 16 * 16), w));
        }

        /// <summary>
        /// The region_request for a window `width` wide on x and z around the current view (nav.View), keeping the
        /// height, the y margin and the section grid: the origin differs from the current one by whole sections on
        /// every axis (centred to within 8 blocks), so the reply re-keys. [nav] The width is limited with the minimum
        /// margin (NavState.MarginPref), so a bigger margin never takes a preset away; the x/z margin is MarginFor(width),
        /// shrunk to fit the server's dims and block caps. Null when nothing would change or the region can't fit.
        /// </summary>
        public static RegionMove? Plan(NavState nav, int width, RegionLimits caps)
        {
            if (nav == null || !nav.Loaded) return null;
            int m0 = Math.Max(0, Math.Min(Math.Max(nav.MarginPref.x, nav.MarginPref.z), (Math.Min(caps.MaxX, caps.MaxZ) - MinWidth) / 2));
            int w = Snap(width, MaxWidth(caps, m0));
            int m = Math.Max(0, Math.Min(Math.Max(m0, MarginFor(w)), (Math.Min(caps.MaxX, caps.MaxZ) - w) / 2));
            long h = nav.Dims.y;
            while (m > 0 && (long)(w + 2 * m) * (w + 2 * m) * h > caps.MaxBlocks) m = Math.Max(0, m - 8);
            if ((long)(w + 2 * m) * (w + 2 * m) * h > caps.MaxBlocks) return null;
            var dims = new Vector3Int(w + 2 * m, nav.Dims.y, w + 2 * m);
            var margin = new Vector3Int(m, nav.Margin.y, m);
            if (dims == nav.Dims && margin == nav.Margin) return null;
            var o = new Vector3Int(NavState.AlignedTo(nav.Origin.x, nav.View.x - dims.x / 2f), nav.Origin.y, NavState.AlignedTo(nav.Origin.z, nav.View.z - dims.z / 2f));
            return new RegionMove { Origin = o, Dims = dims, Margin = margin, Kind = "resize" };
        }

        /// <summary>Block size that keeps the footprint on the table: bs * oldWidth / newWidth, within the protocol's range.</summary>
        public static float ScaleBlockSize(float bs, int oldWidth, int newWidth)
        {
            if (oldWidth <= 0 || newWidth <= 0 || oldWidth == newWidth) return bs;
            return Math.Max(MinBlockM, Math.Min(MaxBlockM, bs * oldWidth / newWidth));
        }
        public const float MinBlockM = 0.002f, MaxBlockM = 0.1f;   // avatar_config.block_size_m range (PROTOCOL.md 4.8)

        /// <summary>Status text: current width, the presets and the server's max.</summary>
        public static string Describe(int current, int[] presets, int serverMax, int pending) =>
            $"AREA {(current > 0 ? current.ToString() : "-")} wide" + (pending > 0 ? $" -> {pending}..." : "") +
            $"  presets {string.Join("/", presets)}  server max {serverMax}  (L-stick click = next)";
    }

    /// <summary>
    /// [area] Frame-time samples for the periodic PERF log line (TabletopApp/AreaSizeControl): percentiles of
    /// Time.unscaledDeltaTime over the last few seconds. Engine-free.
    /// </summary>
    public sealed class FrameStats
    {
        readonly float[] _ms;
        int _n, _at;
        public FrameStats(int capacity = 1024) { _ms = new float[capacity]; }
        public int Count => _n;
        public void Add(float ms) { _ms[_at] = ms; _at = (_at + 1) % _ms.Length; if (_n < _ms.Length) _n++; }
        public void Clear() { _n = 0; _at = 0; }

        /// <summary>p in [0, 1]: nearest-rank percentile of the samples (0 when empty).</summary>
        public float Percentile(float p)
        {
            if (_n == 0) return 0;
            var s = new float[_n];
            Array.Copy(_ms, s, _n);
            Array.Sort(s);
            int i = (int)Math.Ceiling(p * _n) - 1;
            return s[Math.Max(0, Math.Min(_n - 1, i))];
        }

        public float Max() { float m = 0; for (int i = 0; i < _n; i++) m = Math.Max(m, _ms[i]); return m; }
        public float Mean() { if (_n == 0) return 0; double t = 0; for (int i = 0; i < _n; i++) t += _ms[i]; return (float)(t / _n); }
        /// <summary>Frames over a budget (e.g. 1000/72 ms plus a little slack): dropped or late frames.</summary>
        public int Over(float ms) { int c = 0; for (int i = 0; i < _n; i++) if (_ms[i] > ms) c++; return c; }
    }
}
