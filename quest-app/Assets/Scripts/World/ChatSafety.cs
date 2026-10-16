// [safety] Mute, block and report on the headset (docs/PROTOCOL.md "Chat safety: mute, block, report", docs/QUEST_SETUP.md
// Part L), engine-free (Tests~):
//   ChatSafety    who this headset mutes or blocks, per server (welcome.server_id, else the server's address), by uuid;
//                 what that hides: a muted player's chat, /say and /me lines and their bubbles; a blocked player's also
//                 their whispers (/tabletop tell). Kept in prefs.json under "chat_safety".
//   ReportReasons the reasons the report picker offers (the codes PROTOCOL.md lists).
//   ReportDraft   the report being written: the player, the reason, an optional note typed on the panel keyboard.
// Muting and blocking happen only on this headset: the server and the player aren't told. A report goes to the server's
// operators (report_v1); the headset also blocks the player it reports.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public enum SafetyLevel { None, Muted, Blocked }

    public sealed class SafetyEntry
    {
        public string Uuid, Name;
        public SafetyLevel Level;
        /// <summary>The reason of this headset's last report about them, or null.</summary>
        public string Reported;
        public long ReportedAt;   // Unix ms
    }

    public sealed class ChatSafety
    {
        /// <summary>Most players remembered per server (the oldest additions go first).</summary>
        public const int MaxPerServer = 500;
        readonly Dictionary<string, List<SafetyEntry>> _servers = new Dictionary<string, List<SafetyEntry>>();
        public string ServerKey { get; private set; } = "";
        /// <summary>Bumped on every change (and when the server changes).</summary>
        public int Version { get; private set; }

        List<SafetyEntry> Current => _servers.TryGetValue(ServerKey, out var l) ? l : null;

        /// <summary>The key a server's lists are kept under: its id (welcome.server_id, 1.1.0), else "url:" and its address.</summary>
        public static string KeyFor(string serverId, string url)
        {
            if (!string.IsNullOrEmpty(serverId)) return serverId;
            return string.IsNullOrEmpty(url) ? "" : "url:" + url.Trim().ToLowerInvariant();
        }

        /// <summary>At welcome: the lists of this server apply. A server first seen by its address (a plugin before 1.1.0)
        /// keeps its lists when it starts sending a server id.</summary>
        public void UseServer(string serverId, string url)
        {
            string key = KeyFor(serverId, url), old = KeyFor(null, url);
            if (!string.IsNullOrEmpty(serverId) && old.Length > 0 && _servers.TryGetValue(old, out var byUrl))
            {
                _servers.Remove(old);
                if (_servers.TryGetValue(key, out var byId)) { foreach (var e in byUrl) if (byId.FindIndex(x => x.Uuid == e.Uuid) < 0) byId.Add(e); }
                else _servers[key] = byUrl;
            }
            ServerKey = key;
            Version++;
        }

        public SafetyEntry Find(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;
            var l = Current;
            if (l == null) return null;
            foreach (var e in l) if (string.Equals(e.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        public SafetyLevel LevelOf(string uuid) => Find(uuid)?.Level ?? SafetyLevel.None;
        public bool IsMuted(string uuid) => LevelOf(uuid) != SafetyLevel.None;   // blocking also mutes
        public bool IsBlocked(string uuid) => LevelOf(uuid) == SafetyLevel.Blocked;
        public int Count => Current?.Count ?? 0;

        /// <summary>This server's muted and blocked players, by name.</summary>
        public List<SafetyEntry> List()
        {
            var l = new List<SafetyEntry>(Current ?? new List<SafetyEntry>());
            l.Sort((a, b) => { int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.CompareOrdinal(a.Uuid, b.Uuid); });
            return l;
        }

        /// <summary>Mutes, blocks or clears a player on this server; None forgets them. True when something changed.</summary>
        public bool Set(string uuid, string name, SafetyLevel level)
        {
            if (string.IsNullOrEmpty(uuid)) return false;
            var e = Find(uuid);
            if (level == SafetyLevel.None)
            {
                if (e == null) return false;
                Current.Remove(e);
                Version++;
                return true;
            }
            if (e == null)
            {
                if (!_servers.TryGetValue(ServerKey, out var l)) _servers[ServerKey] = l = new List<SafetyEntry>();
                l.Add(e = new SafetyEntry { Uuid = uuid, Name = string.IsNullOrEmpty(name) ? "?" : name });
                if (l.Count > MaxPerServer) l.RemoveRange(0, l.Count - MaxPerServer);
            }
            else if (!string.IsNullOrEmpty(name)) e.Name = name;
            if (e.Level == level) return false;
            e.Level = level;
            Version++;
            return true;
        }

        /// <summary>This headset reported them: they are blocked too, and the report is remembered.</summary>
        public void Reported(string uuid, string name, string reason, long nowMs)
        {
            Set(uuid, name, SafetyLevel.Blocked);
            var e = Find(uuid);
            if (e == null) return;
            e.Reported = reason; e.ReportedAt = nowMs;
            Version++;
        }

        /// <summary>
        /// True when this headset doesn't show the message: a muted player's chat, say and me lines; a blocked player's
        /// whispers too. Joins, quits, deaths and advancements stay (they say who is on the server), and so do lines
        /// without a uuid (the console, plugins, other headsets).
        /// </summary>
        public bool Hides(ChatMessage m)
        {
            if (m == null || string.IsNullOrEmpty(m.Uuid)) return false;
            var lv = LevelOf(m.Uuid);
            if (lv == SafetyLevel.None) return false;
            switch (m.Kind)
            {
                case "chat": case "say": case "me": return true;
                case "whisper": return lv == SafetyLevel.Blocked;
                default: return false;
            }
        }

        /// <summary>A row of the list: "Alex  blocked, reported (harassment)".</summary>
        public static string Describe(SafetyEntry e) =>
            e.Name + "  " + (e.Level == SafetyLevel.Blocked ? "blocked" : "muted") + (e.Reported != null ? ", reported (" + ReportReasons.Label(e.Reported).ToLowerInvariant() + ")" : "");

        // ---------- prefs.json "chat_safety": {"<server key>": [{"uuid","name","level","reported","reported_at"}]} ----------

        /// <summary>Every server's lists, or null when there are none.</summary>
        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in _servers)
            {
                if (kv.Value.Count == 0) continue;
                var l = new List<object>();
                foreach (var e in kv.Value)
                {
                    var o = new Dictionary<string, object> { ["uuid"] = e.Uuid, ["name"] = e.Name, ["level"] = e.Level == SafetyLevel.Blocked ? "blocked" : "muted" };
                    if (e.Reported != null) { o["reported"] = e.Reported; o["reported_at"] = e.ReportedAt; }
                    l.Add(o);
                }
                d[kv.Key] = l;
            }
            return d.Count > 0 ? d : null;
        }

        /// <summary>Replaces every server's lists with the saved ones (bad entries are skipped).</summary>
        public void Load(Dictionary<string, object> saved)
        {
            _servers.Clear();
            if (saved != null)
                foreach (var kv in saved)
                {
                    if (!(kv.Value is List<object> arr)) continue;
                    var l = new List<SafetyEntry>();
                    foreach (var x in arr)
                    {
                        if (!(x is Dictionary<string, object> o) || string.IsNullOrEmpty(o.S("uuid"))) continue;
                        string lv = o.S("level");
                        if (lv != "muted" && lv != "blocked") continue;
                        if (l.FindIndex(e => e.Uuid == o.S("uuid")) >= 0) continue;
                        l.Add(new SafetyEntry
                        {
                            Uuid = o.S("uuid"), Name = o.S("name") ?? "?", Level = lv == "blocked" ? SafetyLevel.Blocked : SafetyLevel.Muted,
                            Reported = o.S("reported"), ReportedAt = (long)o.D("reported_at"),
                        });
                    }
                    if (l.Count > 0) _servers[kv.Key] = l;
                }
            Version++;
        }
    }

    /// <summary>The reasons the report picker offers: codes for the wire (PROTOCOL.md "Chat safety"), labels for the buttons.</summary>
    public static class ReportReasons
    {
        public static readonly string[] Codes = { "harassment", "hate", "sexual", "threat", "spam", "cheating", "other" };
        public static readonly string[] Labels = { "Harassment", "Hate speech", "Sexual", "Threats", "Spam", "Cheating", "Other" };

        public static string Label(string code)
        {
            int i = Array.IndexOf(Codes, code);
            return i >= 0 ? Labels[i] : code ?? "";
        }
    }

    /// <summary>The report being written in the panel: who, why, and an optional note for the operators.</summary>
    public sealed class ReportDraft
    {
        public const int MaxNote = ControlProtocol.MaxReportNote;
        public string Uuid { get; private set; }
        public string Name { get; private set; }
        public string Reason;
        public readonly KeyboardModel Keys = new KeyboardModel { MaxLength = MaxNote, EnterLabel = "report" };
        public bool Open => Uuid != null;
        public string Note => Keys.Text;

        public void Begin(string uuid, string name)
        {
            Uuid = uuid; Name = string.IsNullOrEmpty(name) ? "?" : name; Reason = null;
            Keys.SetText(""); Keys.ResetMode();
        }

        public void Close() { Uuid = null; Name = null; Reason = null; Keys.SetText(""); }

        /// <summary>The report message (blocked: the headset blocks them too, which it always does).</summary>
        public Dictionary<string, object> Message() => ControlProtocol.Report(Uuid, Name, Reason, Note, true);
    }
}
