// [setup] Pairing with a 6-digit code (PROTOCOL.md "Pairing", cap pair_v1), engine-free (Tests~):
//   - PairingStore: the device tokens this headset was given, one per server, in pairings.json (the app's private files
//     folder, next to tabletop.json). Keyed by the server's id (discovery reply id, welcome.server_id) and remembered
//     under the URL it was last reached at, so a server whose LAN address changed is still known when it is found again.
//   - PairCode / PairingWelcome: pair_code, paired and welcome.pairing.
//   - PluginSupport: what the connected plugin can't do for this app, as one line with what to update.
// The plugin side is PairingService.java; QUEST_SETUP.md "Store install" walks through it.
using System;
using System.Collections.Generic;
using System.Text;

namespace TabletopCraft.Net
{
    public sealed class PairingEntry
    {
        public string ServerId = "", Url = "", Token = "", Device = "", Name = "";
        public long PairedMs;
    }

    public sealed class PairingStore
    {
        public const string FileName = "pairings.json";
        public readonly List<PairingEntry> Entries = new List<PairingEntry>();

        public static PairingStore Parse(string json)
        {
            var s = new PairingStore();
            if (string.IsNullOrWhiteSpace(json)) return s;
            if (!(Json.Parse(json) is Dictionary<string, object> m)) throw new FormatException("pairings.json is not an object");
            foreach (var o in m.A("servers") ?? new List<object>())
            {
                if (!(o is Dictionary<string, object> e)) continue;
                var p = new PairingEntry
                {
                    ServerId = e.S("server_id") ?? "", Url = e.S("url") ?? "", Token = e.S("token") ?? "",
                    Device = e.S("device") ?? "", Name = e.S("name") ?? "", PairedMs = (long)e.D("paired_ms"),
                };
                if (p.Token.Length > 0 && (p.ServerId.Length > 0 || p.Url.Length > 0)) s.Entries.Add(p);
            }
            return s;
        }

        public string ToJson()
        {
            var list = new List<object>();
            foreach (var e in Entries)
                list.Add(new Dictionary<string, object>
                {
                    ["server_id"] = e.ServerId, ["url"] = e.Url, ["token"] = e.Token, ["device"] = e.Device, ["name"] = e.Name, ["paired_ms"] = e.PairedMs,
                });
            return Json.Write(new Dictionary<string, object> { ["v"] = 1, ["servers"] = list }) + "\n";
        }

        /// <summary>The entry for a server: by its id when known (from discovery or a welcome), else by the URL.</summary>
        public PairingEntry Find(string url, string serverId)
        {
            if (!string.IsNullOrEmpty(serverId)) foreach (var e in Entries) if (e.ServerId == serverId) return e;
            if (!string.IsNullOrEmpty(url)) foreach (var e in Entries) if (e.Url == url) return e;
            return null;
        }

        public string TokenFor(string url, string serverId) => Find(url, serverId)?.Token;

        /// <summary>paired: keep the token (one per server; a newer pairing replaces the old).</summary>
        public void Save(string serverId, string url, string token, string device, string name, long nowMs)
        {
            Forget(url, serverId);
            Entries.Add(new PairingEntry { ServerId = serverId ?? "", Url = url ?? "", Token = token, Device = device ?? "", Name = name ?? "", PairedMs = nowMs });
        }

        /// <summary>A welcome from this server at this URL: remember where it is now. True when something changed.</summary>
        public bool Seen(string url, string serverId)
        {
            var e = Find(null, serverId);
            if (e == null || string.IsNullOrEmpty(url) || e.Url == url) return false;
            e.Url = url;
            return true;
        }

        /// <summary>The server refused our token (unpaired, or its devices.yml lost it): drop it. True when one was dropped.</summary>
        public bool Forget(string url, string serverId) =>
            Entries.RemoveAll(e => (!string.IsNullOrEmpty(serverId) && e.ServerId == serverId) || (!string.IsNullOrEmpty(url) && e.Url == url)) > 0;

        /// <summary>The name the server lists this headset under: "Quest 3", "Quest Pro"... from the device model.</summary>
        public static string DeviceName(string model)
        {
            string m = (model ?? "").Trim();
            if (m.IndexOf("Quest 3S", StringComparison.OrdinalIgnoreCase) >= 0) return "Quest 3S";
            if (m.IndexOf("Quest 3", StringComparison.OrdinalIgnoreCase) >= 0) return "Quest 3";
            if (m.IndexOf("Quest Pro", StringComparison.OrdinalIgnoreCase) >= 0) return "Quest Pro";
            if (m.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0) return "Quest";
            if (m.Length == 0) return "Headset";
            return m.Length > 32 ? m.Substring(0, 32) : m;
        }
    }

    /// <summary>pair_code: a code to show until it expires (or the headset is paired).</summary>
    public sealed class PairCode
    {
        public string Code = "";
        public double ExpiresAt;   // app clock (seconds)
        public bool Required, TokenRejected;

        public static PairCode From(Dictionary<string, object> m, double now)
        {
            string code = m.S("code") ?? "";
            if (code.Length != 6) return null;
            foreach (char c in code) if (c < '0' || c > '9') return null;
            int secs = Math.Max(1, m.I("expires_s", 300));
            return new PairCode { Code = code, ExpiresAt = now + secs, Required = m.B("required"), TokenRejected = m.B("token_rejected") };
        }

        public bool Live(double now) => now < ExpiresAt;
        public int SecondsLeft(double now) => Math.Max(0, (int)Math.Ceiling(ExpiresAt - now));
        /// <summary>"482 913": easier to read across a room.</summary>
        public string Spaced => Code.Length == 6 ? Code.Substring(0, 3) + " " + Code.Substring(3) : Code;
    }

    /// <summary>welcome.pairing (only to pair_v1 sessions of a 1.1+ plugin), and welcome.server_id.</summary>
    public sealed class PairingWelcome
    {
        public bool Supported, Paired, Required, TokenRejected;
        public string ServerId = "", Device = "", Name = "", ServerVersion = "";

        public static PairingWelcome From(Dictionary<string, object> welcome)
        {
            var w = new PairingWelcome { ServerId = welcome.S("server_id") ?? "", ServerVersion = welcome.S("server_version") ?? "" };
            var p = welcome.O("pairing");
            if (p == null) return w;
            w.Supported = true;
            w.Paired = p.B("paired");
            w.Required = p.B("required");
            w.TokenRejected = p.B("token_rejected");
            w.Device = p.S("device") ?? "";
            w.Name = p.S("name") ?? "";
            return w;
        }
    }

    /// <summary>
    /// What an older plugin can't do for this app (semantic versions: same major works, features come by capability).
    /// The app keeps working with what the plugin offers and says, once, what an update would add.
    /// </summary>
    public static class PluginSupport
    {
        public const string Releases = "github.com/NickStassen/tabletop-craft-ar/releases";
        public const string PairingSince = "1.1.0";

        /// <summary>One line for a toast and the Info tab, or null when nothing the app needs is missing.</summary>
        public static string Note(PairingWelcome w, bool hasPack)
        {
            if (w.Supported) return null;
            string v = string.IsNullOrEmpty(w.ServerVersion) ? "an older version" : Named(w.ServerVersion);
            return $"Server plugin {v} can't pair headsets{(hasPack ? "" : " or send the game's textures")}: " +
                   $"update it to {Named(PairingSince)} or newer ({Releases})";
        }

        /// <summary>[v1.2.1] A version as the docs write it: "1.1.0" -> "v1.1.0".</summary>
        public static string Named(string version) => string.IsNullOrEmpty(version) || version[0] == 'v' ? version : "v" + version;

        /// <summary>Compares "1.0.2" and "1.1.0" by number (missing parts are 0; anything after '-' is ignored).</summary>
        public static int Compare(string a, string b)
        {
            int[] pa = Parts(a), pb = Parts(b);
            for (int i = 0; i < 3; i++) if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
            return 0;
        }

        static int[] Parts(string v)
        {
            var r = new int[3];
            var s = (v ?? "").Split('-')[0].Split('.');
            for (int i = 0; i < 3 && i < s.Length; i++) int.TryParse(s[i], out r[i]);
            return r;
        }
    }

    public static class PairingProtocol
    {
        public const string Cap = "pair_v1";
        public static Dictionary<string, object> Request(string deviceName) => new Dictionary<string, object>
        {
            ["type"] = "pair_request", ["device_name"] = deviceName ?? "Headset",
        };
    }
}
