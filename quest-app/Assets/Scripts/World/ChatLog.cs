// [talk] The headset's chat (docs/PROTOCOL.md 4.26, docs/QUEST_SETUP.md Part L), engine-free (Tests~):
//   ChatLog      the last 200 lines from the server's "chat" messages, each formatted and coloured by kind; the wrist
//                overlay's last lines (fading after 10 s) and word wrapping for the panel's Chat tab;
//   ChatBubbles  speech bubbles over player figures, matched by uuid (never by name): up to 3 per player, each shown
//                6 s + 0.05 s per character.
using System;
using System.Collections.Generic;
using System.Text;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum ChatTone { Chat, Operator, Whisper, System, Death, Advancement, Server }

    public sealed class ChatEntry
    {
        public string Kind, Name, Uuid, Text, Display;
        public ChatTone Tone;
        public double Time;
        /// <summary>This headset's own line (the server's echo of its chat_send).</summary>
        public bool Mine;
    }

    /// <summary>A line to draw; [safety] Entry: the chat line it comes from (Page fills it, so a row can be picked).</summary>
    public struct WristLine { public string Text; public ChatTone Tone; public float Alpha; public ChatEntry Entry; }

    public sealed class ChatLog
    {
        public const int Capacity = 200, WristCount = 5;
        public const double WristSeconds = 10, FadeSeconds = 1.5;

        readonly List<ChatEntry> _entries = new List<ChatEntry>();
        /// <summary>Bumped on every new line.</summary>
        public int Version { get; private set; }
        public IReadOnlyList<ChatEntry> Entries => _entries;
        public int Count => _entries.Count;

        /// <summary>Adds a chat message; mySession marks the server's echo of this headset's own line.</summary>
        public ChatEntry Add(ChatMessage m, double now, string mySession = null)
        {
            var e = new ChatEntry
            {
                Kind = m.Kind, Name = m.Name, Uuid = m.Uuid, Text = m.Text, Display = Format(m), Tone = ToneOf(m.Kind), Time = now,
                Mine = m.Kind == "operator" && m.Session != null && m.Session == mySession,
            };
            _entries.Add(e);
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
            Version++;
            return e;
        }

        public void Clear() { _entries.Clear(); Version++; }

        /// <summary>[safety] Takes a muted player's chat, say and me lines out (and whispers when blocked); how many went.</summary>
        public int RemoveFrom(string uuid, bool whispers)
        {
            if (string.IsNullOrEmpty(uuid)) return 0;
            int n = _entries.RemoveAll(e => e.Uuid == uuid && (e.Kind == "chat" || e.Kind == "say" || e.Kind == "me" || (whispers && e.Kind == "whisper")));
            if (n > 0) Version++;
            return n;
        }

        /// <summary>One line as the game shows it: "&lt;Alex&gt; hi", "[Alex] hi" (say), "* Alex waves" (me), the full line otherwise.</summary>
        public static string Format(ChatMessage m)
        {
            string n = string.IsNullOrEmpty(m.Name) ? "?" : m.Name, t = m.Text ?? "";
            switch (m.Kind)
            {
                case "chat": return "<" + n + "> " + t;
                case "say": return "[" + n + "] " + t;
                case "me": return "* " + n + " " + t;
                case "whisper": return n + " whispers: " + t;
                case "operator": return !string.IsNullOrEmpty(m.Line) ? m.Line : "[Giant] " + n + ": " + t;
                default: return t;   // join, quit, death, advancement, broadcast: the server sends the whole line
            }
        }

        public static ChatTone ToneOf(string kind)
        {
            switch (kind)
            {
                case "operator": return ChatTone.Operator;
                case "whisper": return ChatTone.Whisper;
                case "join": case "quit": return ChatTone.System;
                case "death": return ChatTone.Death;
                case "advancement": return ChatTone.Advancement;
                case "broadcast": case "say": return ChatTone.Server;
                default: return ChatTone.Chat;
            }
        }

        /// <summary>Colour of a tone (sRGB): white chat, gold for the giant, pink whispers, yellow joins, red deaths...</summary>
        public static Color32 ColorOf(ChatTone t)
        {
            switch (t)
            {
                case ChatTone.Operator: return new Color32(255, 196, 64, 255);
                case ChatTone.Whisper: return new Color32(255, 140, 220, 255);
                case ChatTone.System: return new Color32(255, 255, 110, 255);
                case ChatTone.Death: return new Color32(255, 120, 100, 255);
                case ChatTone.Advancement: return new Color32(120, 255, 120, 255);
                case ChatTone.Server: return new Color32(150, 210, 255, 255);
                default: return new Color32(240, 244, 250, 255);
            }
        }

        /// <summary>Joins and deaths also show as toasts (World/HudState.cs).</summary>
        public static bool Toast(string kind) => kind == "join" || kind == "death";

        /// <summary>The wrist overlay: the last 5 lines younger than 10 s, oldest first, fading out over the last 1.5 s.</summary>
        public List<WristLine> Wrist(double now)
        {
            var l = new List<WristLine>();
            for (int i = Math.Max(0, _entries.Count - WristCount); i < _entries.Count; i++)
            {
                var e = _entries[i];
                double age = now - e.Time;
                if (age >= WristSeconds || age < 0) continue;
                float a = (float)Math.Min(1, (WristSeconds - age) / FadeSeconds);
                l.Add(new WristLine { Text = e.Display, Tone = e.Tone, Alpha = a });
            }
            return l;
        }

        /// <summary>
        /// Wrapped lines for the Chat tab: the last `rows` lines `scroll` lines up from the bottom (0 = newest at the
        /// bottom), each at most `width` characters. Returns how many wrapped lines exist in total.
        /// </summary>
        public int Page(int rows, int width, int scroll, List<WristLine> into)
        {
            into.Clear();
            var all = new List<WristLine>();
            foreach (var e in _entries) foreach (var w in Wrap(e.Display, width)) all.Add(new WristLine { Text = w, Tone = e.Tone, Alpha = 1, Entry = e });
            int end = Math.Max(0, all.Count - Math.Max(0, scroll)), start = Math.Max(0, end - rows);
            for (int i = start; i < end; i++) into.Add(all[i]);
            return all.Count;
        }

        /// <summary>Word wrap at width characters; words longer than a line are cut. Never returns an empty list.</summary>
        public static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            width = Math.Max(4, width);
            var sb = new StringBuilder();
            foreach (var raw in (text ?? "").Split(' '))
            {
                string w = raw;
                while (w.Length > width)
                {
                    if (sb.Length > 0) { lines.Add(sb.ToString()); sb.Clear(); }
                    lines.Add(w.Substring(0, width));
                    w = w.Substring(width);
                }
                if (sb.Length > 0 && sb.Length + 1 + w.Length > width) { lines.Add(sb.ToString()); sb.Clear(); }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w);
            }
            if (sb.Length > 0 || lines.Count == 0) lines.Add(sb.ToString());
            return lines;
        }
    }

    public struct Bubble { public string Text; public double Until; }

    /// <summary>Speech bubbles over player figures, by uuid: chat, say and me lines, up to 3 stacked per player.</summary>
    public sealed class ChatBubbles
    {
        public const int MaxPerPlayer = 3, Width = 28;
        public const double BaseSeconds = 6, PerChar = 0.05;
        readonly Dictionary<string, List<Bubble>> _by = new Dictionary<string, List<Bubble>>();
        public int Version { get; private set; }

        public static double Life(string text) => BaseSeconds + PerChar * (text?.Length ?? 0);

        /// <summary>True when the message makes a bubble (a player's chat, say or me with a uuid).</summary>
        public static bool Wants(ChatMessage m) => !string.IsNullOrEmpty(m.Uuid) && (m.Kind == "chat" || m.Kind == "say" || m.Kind == "me");

        public void Add(ChatMessage m, double now)
        {
            if (!Wants(m)) return;
            if (!_by.TryGetValue(m.Uuid, out var l)) _by[m.Uuid] = l = new List<Bubble>();
            string text = m.Kind == "me" ? "* " + m.Text : m.Text;
            l.Add(new Bubble { Text = text, Until = now + Life(text) });
            if (l.Count > MaxPerPlayer) l.RemoveRange(0, l.Count - MaxPerPlayer);
            Version++;
        }

        /// <summary>[safety] Drops the bubbles of the players matching (muted on this headset); true when some went away.</summary>
        public bool RemoveWhere(Func<string, bool> gone)
        {
            var drop = new List<string>();
            foreach (var k in _by.Keys) if (gone(k)) drop.Add(k);
            foreach (var k in drop) _by.Remove(k);
            if (drop.Count > 0) Version++;
            return drop.Count > 0;
        }

        /// <summary>Drops expired bubbles; true when something went away.</summary>
        public bool Prune(double now)
        {
            bool changed = false;
            var empty = new List<string>();
            foreach (var kv in _by)
            {
                if (kv.Value.RemoveAll(b => now >= b.Until) > 0) changed = true;
                if (kv.Value.Count == 0) empty.Add(kv.Key);
            }
            foreach (var k in empty) _by.Remove(k);
            if (changed) Version++;
            return changed;
        }

        /// <summary>Live bubbles of a player, oldest first (drawn bottom-up newest lowest).</summary>
        public IReadOnlyList<Bubble> For(string uuid) => uuid != null && _by.TryGetValue(uuid, out var l) ? l : (IReadOnlyList<Bubble>)Array.Empty<Bubble>();

        public IEnumerable<string> Players => _by.Keys;

        /// <summary>The text a player's bubble stack shows: each bubble wrapped, newest last.</summary>
        public string Text(string uuid)
        {
            var sb = new StringBuilder();
            foreach (var b in For(uuid)) foreach (var line in ChatLog.Wrap(b.Text, Width)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(line); }
            return sb.ToString();
        }
    }
}
