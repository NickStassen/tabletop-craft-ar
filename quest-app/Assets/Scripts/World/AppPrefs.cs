// What the app remembers between sessions (docs/QUEST_SETUP.md "What the app remembers"), engine-free (Tests~).
//
// prefs.json lives next to tabletop.json (persistentDataPath) and is written by the app, debounced: the block size and
// the visible width it was chosen for, the table's position and turn relative to the MRUK TABLE anchor, the hotbar, the
// selected block, the menu tab and Show HUD. tabletop.json stays the hand-edited setup file (server, token, skin,
// lighting). Three keys can be in both (block_size_m, area_width, show_hud): a value in tabletop.json wins at the next
// start only when it differs from the one seen there last time (you edited the file); otherwise the headset's own
// choice from prefs.json stands. prefs.json records the tabletop.json values it saw under "tabletop_json".
using System;
using System.Collections.Generic;
using System.Text;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>The table anchor's pose relative to a reference frame (the MRUK table): offset in the frame's yaw-turned
    /// axes (metres) and yaw (degrees). Yaw is about +Y, Unity's convention (Euler(0, yaw, 0)).</summary>
    public struct TablePose
    {
        public float X, Y, Z, Yaw;

        public static TablePose Relative(Vector3 refPos, float refYaw, Vector3 pos, float yaw)
        {
            var d = RotY(pos - refPos, -refYaw);
            return new TablePose { X = d.x, Y = d.y, Z = d.z, Yaw = Wrap(yaw - refYaw) };
        }

        public void Compose(Vector3 refPos, float refYaw, out Vector3 pos, out float yaw)
        {
            pos = refPos + RotY(new Vector3(X, Y, Z), refYaw);
            yaw = Wrap(refYaw + Yaw);
        }

        /// <summary>v turned by deg about +Y as Unity's Quaternion.Euler(0, deg, 0) * v does.</summary>
        public static Vector3 RotY(Vector3 v, float deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return new Vector3((float)(v.x * c + v.z * s), v.y, (float)(-v.x * s + v.z * c));
        }

        /// <summary>-180 (exclusive) .. 180.</summary>
        public static float Wrap(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            if (deg <= -180f) deg += 360f;
            return deg;
        }
    }

    public sealed class AppPrefs
    {
        public const string FileName = "prefs.json";
        public const int Version = 1;
        /// <summary>The tabletop.json keys that prefs.json can override (see the file header).</summary>
        public static readonly string[] SharedKeys = { "block_size_m", "area_width", "show_hud", "volume", "sound" };   // [sound] volume 0..1, sound on/off

        public float? BlockSizeM;
        public int AreaWidth;              // visible width the block size was chosen for (0 = unknown)
        public bool? ShowHud;
        public float? Volume;              // [sound] master volume 0..1 (Info tab)
        public bool? Sound;                // [sound] sound on (Info tab mute)
        public string MenuTab;             // BlockMenuModel.TabId: a category id, "search" or "info"
        public string[] Hotbar;            // 9 slots, null = empty
        public int HotbarSelected;
        public string Selected;
        public string TableAnchor;         // MRUK TABLE anchor UUID the pose is relative to
        public TablePose? Table;
        /// <summary>tabletop.json's values of SharedKeys when these prefs were written (double or bool).</summary>
        public readonly Dictionary<string, object> Seen = new Dictionary<string, object>();

        public static AppPrefs Parse(string json)
        {
            var p = new AppPrefs();
            if (string.IsNullOrWhiteSpace(json)) return p;
            if (!(Json.Parse(json) is Dictionary<string, object> m)) throw new FormatException("prefs.json is not an object");
            if (m.TryGetValue("block_size_m", out var b) && b is double bs && bs > 0) p.BlockSizeM = (float)bs;
            p.AreaWidth = Math.Max(0, m.I("area_width"));
            if (m.TryGetValue("show_hud", out var h) && h is bool sh) p.ShowHud = sh;
            if (m.TryGetValue("volume", out var vo) && vo is double vd) p.Volume = (float)Math.Max(0, Math.Min(1, vd));   // [sound]
            if (m.TryGetValue("sound", out var so) && so is bool sb) p.Sound = sb;
            p.MenuTab = m.S("menu_tab");
            var hb = m.A("hotbar");
            if (hb != null) { p.Hotbar = new string[Math.Min(hb.Count, 9)]; for (int i = 0; i < p.Hotbar.Length; i++) p.Hotbar[i] = hb[i] as string; }
            p.HotbarSelected = m.I("hotbar_selected");
            p.Selected = m.S("selected");
            var t = m.O("table");
            var pos = t?.A("pos");
            if (t != null && pos != null && pos.Count == 3 && t.S("anchor") is string anchor && anchor.Length > 0)
            {
                var v = Json.Doubles(pos);
                p.TableAnchor = anchor;
                p.Table = new TablePose { X = (float)v[0], Y = (float)v[1], Z = (float)v[2], Yaw = (float)t.D("yaw") };
            }
            var seen = m.O("tabletop_json");
            if (seen != null) foreach (var k in SharedKeys) if (seen.TryGetValue(k, out var v) && (v is double || v is bool)) p.Seen[k] = v;
            return p;
        }

        public string ToJson()
        {
            var d = new Dictionary<string, object> { ["v"] = Version };
            if (BlockSizeM.HasValue) d["block_size_m"] = Math.Round(BlockSizeM.Value, 6);
            if (AreaWidth > 0) d["area_width"] = AreaWidth;
            if (ShowHud.HasValue) d["show_hud"] = ShowHud.Value;
            if (Volume.HasValue) d["volume"] = Math.Round(Volume.Value, 2);   // [sound]
            if (Sound.HasValue) d["sound"] = Sound.Value;
            if (!string.IsNullOrEmpty(MenuTab)) d["menu_tab"] = MenuTab;
            if (Hotbar != null) d["hotbar"] = new List<object>(Hotbar);
            if (Hotbar != null) d["hotbar_selected"] = HotbarSelected;
            if (!string.IsNullOrEmpty(Selected)) d["selected"] = Selected;
            if (Table.HasValue && !string.IsNullOrEmpty(TableAnchor))
            {
                var t = Table.Value;
                d["table"] = new Dictionary<string, object>
                {
                    ["anchor"] = TableAnchor,
                    ["pos"] = new List<object> { Math.Round(t.X, 4), Math.Round(t.Y, 4), Math.Round(t.Z, 4) },
                    ["yaw"] = Math.Round(t.Yaw, 2),
                };
            }
            if (Seen.Count > 0) d["tabletop_json"] = new Dictionary<string, object>(Seen);
            return ConfigFile.Pretty(d);
        }

        /// <summary>
        /// One shared key: tabletop.json's value wins when it is there and differs from the one seen last time (edited);
        /// otherwise the prefs value, else tabletop.json's. Values are double or bool (as Json parses them), or null.
        /// </summary>
        public static object Pick(object json, object prefs, object seen)
        {
            if (json != null && !Equals(json, seen)) return json;
            return prefs ?? json;
        }

        /// <summary>Remember tabletop.json's shared values (call after reading it, before saving).</summary>
        public void SeeConfig(Dictionary<string, object> tabletopJson)
        {
            Seen.Clear();
            if (tabletopJson == null) return;
            foreach (var k in SharedKeys) if (tabletopJson.TryGetValue(k, out var v) && (v is double || v is bool)) Seen[k] = v;
        }
    }

    /// <summary>Debounced writes: a changed snapshot is written once it has been stable for Quiet seconds, or after
    /// MaxDelay seconds of continuous change (a long zoom), never twice for the same text.</summary>
    public sealed class PrefsSaver
    {
        public double Quiet = 1.5, MaxDelay = 10;
        string _saved, _pending;
        double _since, _first;

        public void Saved(string json) { _saved = json; _pending = null; }

        /// <summary>The text to write now, or null. Call it about once a second with the current prefs; then write and call Saved.</summary>
        public string Offer(string json, double now)
        {
            if (json == null || json == _saved) { _pending = null; return null; }
            if (json != _pending)
            {
                if (_pending == null) _first = now;
                _pending = json; _since = now;
            }
            return now - _since >= Quiet || now - _first >= MaxDelay ? _pending : null;
        }

        /// <summary>On pause or quit: the text if it differs from what was written.</summary>
        public string Flush(string json) => json != null && json != _saved ? json : null;
    }

    /// <summary>Editing tabletop.json from the app (the Info tab's Server field).</summary>
    public static class ConfigFile
    {
        public const string FileName = "tabletop.json";

        /// <summary>tabletop.json with "server" set and every other key kept. null old text = a new file. Throws
        /// FormatException when the old text isn't a JSON object (the caller keeps a copy before replacing it).</summary>
        public static string WithServer(string oldJson, string url)
        {
            var d = string.IsNullOrWhiteSpace(oldJson) ? new Dictionary<string, object>()
                  : Json.Parse(oldJson) as Dictionary<string, object> ?? throw new FormatException("tabletop.json is not an object");
            d["server"] = url;
            return Pretty(d);
        }

        /// <summary>A JSON object with one top-level key per line (nested values stay compact), for adb pull and edit.</summary>
        public static string Pretty(Dictionary<string, object> d)
        {
            var sb = new StringBuilder("{\n");
            int i = 0;
            foreach (var kv in d)
            {
                sb.Append("  ").Append(Json.Write(kv.Key)).Append(": ").Append(Json.Write(kv.Value));
                sb.Append(++i < d.Count ? ",\n" : "\n");
            }
            return sb.Append("}\n").ToString();
        }
    }
}
