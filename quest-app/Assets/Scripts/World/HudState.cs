// What reaches the user without the head-locked status text (engine-free, tested in Tests~). The full status lives in
// the block menu's Info tab; "Show HUD" (or tabletop.json "show_hud": true) brings back a small head-locked copy.
// Important events become short toasts low in the view (XR/Hud.cs): link lost / restored, server errors (refused
// edits, region_too_large...), "area set". Each lasts a few seconds and fades; the same text again restarts it instead
// of stacking a copy.
using System;
using System.Collections.Generic;

namespace TabletopCraft.World
{
    public enum ToastKind { Info, Warn, Error }

    public struct ToastView { public string Text; public ToastKind Kind; public float Alpha; }

    public sealed class ToastQueue
    {
        public const double Life = 2.5, ErrorLife = 3.0, FadeIn = 0.15, FadeOut = 0.5;
        public const int MaxVisible = 3, MaxChars = 72;

        sealed class Toast { public string Text; public ToastKind Kind; public double Start, End; }
        readonly List<Toast> _list = new List<Toast>();
        /// <summary>Bumped when a toast is added, restarted or expires.</summary>
        public int Version { get; private set; }
        public int Count => _list.Count;

        public void Push(string text, ToastKind kind, double now)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.Length > MaxChars) text = text.Substring(0, MaxChars - 3) + "...";
            double end = now + (kind == ToastKind.Error ? ErrorLife : Life);
            var same = _list.Find(t => t.Text == text);
            if (same != null) { _list.Remove(same); same.End = end; same.Kind = kind; _list.Add(same); }   // restart, move to newest
            else _list.Add(new Toast { Text = text, Kind = kind, Start = now, End = end });
            while (_list.Count > MaxVisible) _list.RemoveAt(0);
            Version++;
        }

        /// <summary>Live toasts, oldest first, with their fade (0..1). Drops expired ones.</summary>
        public List<ToastView> Visible(double now)
        {
            if (_list.RemoveAll(t => now >= t.End) > 0) Version++;
            var v = new List<ToastView>(_list.Count);
            foreach (var t in _list)
            {
                double a = Math.Min(1, Math.Min((now - t.Start) / FadeIn, (t.End - now) / FadeOut));
                v.Add(new ToastView { Text = t.Text, Kind = t.Kind, Alpha = (float)Math.Max(0, a) });
            }
            return v;
        }

        public void Clear() { _list.Clear(); Version++; }
    }

    /// <summary>Turns the link state into "link lost" / "link restored" events (none before the first connection).</summary>
    public sealed class LinkWatch
    {
        bool _up, _everUp;

        /// <summary>Call every frame; returns a toast text on a transition, else null.</summary>
        public string Update(bool connected)
        {
            if (connected == _up) return null;
            _up = connected;
            if (connected)
            {
                bool again = _everUp;
                _everUp = true;
                return again ? "Link restored" : null;
            }
            return _everUp ? "Link lost: reconnecting..." : null;
        }
    }

    public static class HudText
    {
        /// <summary>A short toast for a server error, or null for errors the user doesn't need to see.</summary>
        public static string ForError(string code, string message)
        {
            message = message ?? "";
            switch (code)
            {
                case null: return null;
                case "edit_denied": return "Edit refused: " + message;
                case "bad_state": return "Can't place: " + message;
                case "out_of_region": return "Can't edit outside the area";
                case "rate_limited": return "Too many edits, slow down";
                case "region_too_large": return "Area too large: " + message;
                case "world_not_found": return "World not found: " + message;
                case "skin_failed": return "Skin lookup failed: " + message;
                case "auth_failed": return "Wrong token (tabletop.json)";
                case "version_mismatch": return "Server speaks another protocol version";
                default: return code + ": " + message;
            }
        }
    }
}
