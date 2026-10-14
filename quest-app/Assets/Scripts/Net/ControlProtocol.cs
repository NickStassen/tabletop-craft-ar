// [talk] Chat, commands and server status (docs/PROTOCOL.md 4.26-4.28): message builders and typed views of the
// server's messages, engine-free (Tests~ parses the golden fixtures with them).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.Net
{
    public static class ControlProtocol
    {
        public const string CapChat = "chat_v1", CapCommand = "command_v1", CapStatus = "status_v1";
        public const int MaxLen = 256;

        public static Dictionary<string, object> ChatSend(string text) => new Dictionary<string, object> { ["type"] = "chat_send", ["text"] = text };

        /// <summary>command; with a pointer (world block coordinates) the server runs it at that block (execute positioned).</summary>
        public static Dictionary<string, object> Command(string line, string atWorld = null, Vector3Int? atPos = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "command", ["line"] = line };
            if (atPos.HasValue)
            {
                var at = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(atWorld)) at["world"] = atWorld;
                at["pos"] = new List<object> { atPos.Value.x, atPos.Value.y, atPos.Value.z };
                d["at"] = at;
            }
            return d;
        }

        public static Dictionary<string, object> CommandComplete(string line) => new Dictionary<string, object> { ["type"] = "command_complete", ["line"] = line ?? "" };
        public static Dictionary<string, object> WorldQuery(string world = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "world_query" };
            if (!string.IsNullOrEmpty(world)) d["world"] = world;
            return d;
        }
        public static Dictionary<string, object> PlayersQuery() => new Dictionary<string, object> { ["type"] = "players_query" };

        internal static List<string> Strings(List<object> a)
        {
            var l = new List<string>();
            if (a != null) foreach (var x in a) if (x is string s) l.Add(s);
            return l;
        }
    }

    /// <summary>What welcome granted: chat (and whether the headset may talk, under which name), commands, status.</summary>
    public sealed class ControlWelcome
    {
        public bool Chat, ChatSend, Commands, Status;
        public string ChatName = "";
        public int ChatMaxLen = ControlProtocol.MaxLen, CommandMaxLen = ControlProtocol.MaxLen;
        public List<string> Quick = new List<string>();

        public static ControlWelcome From(Dictionary<string, object> welcome)
        {
            var w = new ControlWelcome();
            var caps = welcome?.A("caps");
            w.Chat = caps?.Contains(ControlProtocol.CapChat) == true;
            w.Commands = caps?.Contains(ControlProtocol.CapCommand) == true;
            w.Status = caps?.Contains(ControlProtocol.CapStatus) == true;
            var c = welcome?.O("chat");
            if (c != null) { w.ChatSend = c.B("send"); w.ChatName = c.S("name") ?? ""; w.ChatMaxLen = c.I("max_len", ControlProtocol.MaxLen); }
            var k = welcome?.O("commands");
            if (k != null) { w.Quick = ControlProtocol.Strings(k.A("quick")); w.CommandMaxLen = k.I("max_len", ControlProtocol.MaxLen); }
            return w;
        }
    }

    /// <summary>chat (S->C): kind (chat, join, quit, death, advancement, broadcast, say, me, operator, whisper).</summary>
    public sealed class ChatMessage
    {
        public string Kind, Name, Uuid, Session, Text, Line;
        public int Id = -1;

        public static ChatMessage From(Dictionary<string, object> m)
        {
            var f = m.O("from");
            return new ChatMessage
            {
                Kind = m.S("kind") ?? "chat", Text = m.S("text") ?? "", Line = m.S("line"),
                Name = f?.S("name"), Uuid = f?.S("uuid"), Session = f?.S("session"), Id = f?.I("id", -1) ?? -1,
            };
        }
    }

    public sealed class CommandResult
    {
        public long RefSeq; public bool Ok, AsChat; public double Ms;
        public List<string> Lines = new List<string>();

        public static CommandResult From(Dictionary<string, object> m) => new CommandResult
        {
            RefSeq = (long)m.D("ref_seq", -1), Ok = m.B("ok"), AsChat = m.B("as_chat"), Ms = m.D("ms"), Lines = ControlProtocol.Strings(m.A("lines")),
        };
    }

    /// <summary>command_output: late feedback for an earlier command (same ref_seq as its result).</summary>
    public sealed class CommandOutput
    {
        public long RefSeq; public List<string> Lines = new List<string>();
        public static CommandOutput From(Dictionary<string, object> m) => new CommandOutput { RefSeq = (long)m.D("ref_seq", -1), Lines = ControlProtocol.Strings(m.A("lines")) };
    }

    /// <summary>command_suggestions: items replace the line from Start.</summary>
    public sealed class CommandSuggestions
    {
        public long RefSeq; public int Start; public List<string> Items = new List<string>();
        public static CommandSuggestions From(Dictionary<string, object> m) =>
            new CommandSuggestions { RefSeq = (long)m.D("ref_seq", -1), Start = m.I("start"), Items = ControlProtocol.Strings(m.A("items")) };
    }

    public struct TickInfo
    {
        public float Rate; public bool Frozen, Stepping, Sprinting;
        public static TickInfo From(Dictionary<string, object> o) =>
            o == null ? new TickInfo { Rate = 20 } : new TickInfo { Rate = (float)o.D("rate", 20), Frozen = o.B("frozen"), Stepping = o.B("stepping"), Sprinting = o.B("sprinting") };
        public string Label => Frozen ? (Stepping ? "frozen, stepping" : "frozen") : Sprinting ? "sprinting" : Math.Abs(Rate - 20) > 0.01f ? $"{Rate:0.#} tps" : "normal";
    }

    public struct WorldStatus { public string Name, Env, Weather; public int Chunks, Entities, Players; public long Time, Day; }

    /// <summary>server_status (every 2 s with status_v1).</summary>
    public sealed class ServerStatus
    {
        public double[] Tps = new double[3];
        public double Mspt; public long UsedMb, MaxMb; public int Online, MaxPlayers;
        public TickInfo Tick;
        public List<WorldStatus> Worlds = new List<WorldStatus>();

        public static ServerStatus From(Dictionary<string, object> m)
        {
            var s = new ServerStatus { Mspt = m.D("mspt"), Tick = TickInfo.From(m.O("tick")) };
            var t = m.A("tps") is List<object> tl ? Json.Doubles(tl) : new double[0];
            for (int i = 0; i < 3 && i < t.Length; i++) s.Tps[i] = t[i];
            var mem = m.O("memory"); if (mem != null) { s.UsedMb = (long)mem.D("used_mb"); s.MaxMb = (long)mem.D("max_mb"); }
            var pl = m.O("players"); if (pl != null) { s.Online = pl.I("online"); s.MaxPlayers = pl.I("max"); }
            foreach (var x in m.A("worlds") ?? new List<object>())
            {
                var w = (Dictionary<string, object>)x;
                s.Worlds.Add(new WorldStatus
                {
                    Name = w.S("name"), Env = w.S("env"), Weather = w.S("weather"), Chunks = w.I("chunks"), Entities = w.I("entities"),
                    Players = w.I("players"), Time = (long)w.D("time"), Day = (long)w.D("day"),
                });
            }
            return s;
        }
    }

    /// <summary>world_info: time, weather, difficulty and the world's game rules by the names the server's gamerule takes.</summary>
    public sealed class WorldInfo
    {
        public long RefSeq = -1;
        public string World, Env, Weather, Difficulty;
        public long Time, Day;
        public Vector3Int Spawn;
        public TickInfo Tick;
        /// <summary>Rules in the server's order (sorted by name): bool, double (numbers) or string values.</summary>
        public List<KeyValuePair<string, object>> Rules = new List<KeyValuePair<string, object>>();

        public static WorldInfo From(Dictionary<string, object> m)
        {
            var w = new WorldInfo
            {
                RefSeq = (long)m.D("ref_seq", -1), World = m.S("world"), Env = m.S("env"), Weather = m.S("weather"), Difficulty = m.S("difficulty"),
                Time = (long)m.D("time"), Day = (long)m.D("day"), Tick = TickInfo.From(m.O("tick")),
            };
            var sp = m.A("spawn") is List<object> sl ? Json.Ints(sl) : new int[0];
            if (sp.Length == 3) w.Spawn = new Vector3Int(sp[0], sp[1], sp[2]);
            var g = m.O("gamerules");
            if (g != null) foreach (var kv in g) w.Rules.Add(new KeyValuePair<string, object>(kv.Key, kv.Value));
            return w;
        }
    }

    public struct PlayerRow
    {
        public string Name, Uuid, World, GameMode;
        public int Id, Food, Ping;
        public Vector3 Pos;
        public float Health, MaxHealth;
        public bool Op;
    }

    /// <summary>players_info: every online player (any world).</summary>
    public sealed class PlayersInfo
    {
        public long RefSeq = -1;
        public List<PlayerRow> Players = new List<PlayerRow>();

        public static PlayersInfo From(Dictionary<string, object> m)
        {
            var r = new PlayersInfo { RefSeq = (long)m.D("ref_seq", -1) };
            foreach (var x in m.A("players") ?? new List<object>())
            {
                var p = (Dictionary<string, object>)x;
                var pos = p.A("pos") is List<object> pl ? Json.Doubles(pl) : new double[0];
                r.Players.Add(new PlayerRow
                {
                    Name = p.S("name"), Uuid = p.S("uuid"), World = p.S("world"), GameMode = p.S("gamemode"), Id = p.I("id", -1),
                    Food = p.I("food"), Ping = p.I("ping"), Health = (float)p.D("health"), MaxHealth = (float)p.D("max_health", 20), Op = p.B("op"),
                    Pos = pos.Length == 3 ? new Vector3((float)pos[0], (float)pos[1], (float)pos[2]) : new Vector3(0, 0, 0),
                });
            }
            return r;
        }
    }
}
