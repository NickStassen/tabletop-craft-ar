// Tabletop link protocol v1 (docs/PROTOCOL.md): message builders and typed views of server messages.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.Net
{
    public static class Protocol
    {
        public const int Version = 1;
        public const string ClientName = "quest-app";
        /// <summary>The app's version (TabletopApp sets it from Application.version, the repo's VERSION).</summary>
        public static string ClientVersion = "1.0.0";

        /// <summary>
        /// hello. [setup] deviceToken: the token this server gave the headset when it was paired (PROTOCOL.md "Pairing"),
        /// deviceName: what the server lists it as. pair_v1 is always asked for: a server that pairs then shows a code
        /// instead of refusing a headset without a credential.
        /// </summary>
        public static Dictionary<string, object> Hello(string token, string deviceToken = null, string deviceName = null)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = "hello", ["client"] = ClientName, ["client_version"] = ClientVersion,
                ["protocols"] = new List<object> { Version }, ["token"] = token ?? "",
                ["caps"] = new List<object> { "rle_palette_v1", "avatar", CapLight, CapBiomes, SectionedSnapshot.Cap, CapEdit, CapEntities, CapActions,
                                              ControlProtocol.CapChat, ControlProtocol.CapCommand, ControlProtocol.CapStatus, CapBlockEntities,   // [talk], [blocks]
                                              InteractProtocol.Cap, CapWorld, OperatorProtocol.CapMarks, PowerProtocol.Cap },   // [hands], [living], [operator], [power]
            };
            if (!string.IsNullOrEmpty(OperatorName)) d["operator_name"] = OperatorName;   // [talk] the name in chat (PROTOCOL.md 4.26)
            var caps = (List<object>)d["caps"];
            if (WantSound) caps.Add(CapSound);                 // [sound] sound_groups and sound actions
            if (WantAssets) caps.Add(AssetProtocol.Cap);       // [sound] the operator's own files, if the server offers them
            caps.Add(PairingProtocol.Cap);                     // [setup] pairing with a 6-digit code
            if (!string.IsNullOrEmpty(deviceToken)) d["device_token"] = deviceToken;
            if (!string.IsNullOrEmpty(deviceName)) d["device_name"] = deviceName;
            return d;
        }

        /// <summary>[sound] Spatial sound (PROTOCOL.md "Sound"): block sound groups and the sounds the server names.</summary>
        public const string CapSound = "sound_v1";
        /// <summary>[sound] tabletop.json "sound" (default true) and "download_assets" (default true).</summary>
        public static bool WantSound = true, WantAssets = true;

        /// <summary>[talk] tabletop.json operator_name, else skin_player: the name Java players see in chat (the server sanitises it).</summary>
        public static string OperatorName;

        /// <summary>[living] Time of day and weather (world_state, PROTOCOL.md "World state: time and weather"; World/DayLight.cs).</summary>
        public const string CapWorld = "world_v1";

        /// <summary>Optional snapshot extras (PROTOCOL.md 4.4.1): per-block light and 4x4x4 biomes.</summary>
        public const string CapLight = "light_v1", CapBiomes = "biomes_v1";
        /// <summary>Non-player entities (PROTOCOL.md 4.21) and swings/digging/breaks/damage (4.22); EntityProtocol.cs.</summary>
        public const string CapEntities = "entities_v1", CapActions = "actions_v1";
        /// <summary>Block editing from the headset (PROTOCOL.md 4.20); granted in welcome.caps when the server allows it.</summary>
        public const string CapEdit = "edit_v1";
        /// <summary>[blocks] Block entity data: sign text, banner patterns, heads, sherds, spawners... (PROTOCOL.md 4.29).</summary>
        public const string CapBlockEntities = "block_entities_v1";

        /// <summary>block_edit (PROTOCOL.md 4.20): op "break" or "place", world block position, state (place only), clicked face.</summary>
        public static Dictionary<string, object> BlockEdit(string op, Vector3Int pos, string state, string face)
        {
            var d = new Dictionary<string, object> { ["type"] = "block_edit", ["op"] = op, ["pos"] = new List<object> { pos.x, pos.y, pos.z } };
            if (state != null) d["state"] = state;
            if (face != null) d["face"] = face;
            return d;
        }

        public static Dictionary<string, object> Subscribe(string world, Vector3Int origin, Vector3Int dims) => new Dictionary<string, object>
        {
            ["type"] = "subscribe", ["world"] = world,
            ["origin"] = new List<object> { origin.x, origin.y, origin.z }, ["dims"] = new List<object> { dims.x, dims.y, dims.z },
        };

        public static Dictionary<string, object> AvatarConfig(bool enabled, float blockSizeM, string skinPlayer, bool showHands, string nameTag) => new Dictionary<string, object>
        {
            ["type"] = "avatar_config", ["enabled"] = enabled, ["block_size_m"] = blockSizeM, ["max_scale"] = 16.0,
            ["skin_player"] = string.IsNullOrEmpty(skinPlayer) ? null : skinPlayer, ["show_hands"] = showHands, ["name_tag"] = nameTag,
        };

        /// <summary>region_request, centre form (PROTOCOL.md 4.13). origin = centre - size/2; y_origin pins the bottom.</summary>
        public static Dictionary<string, object> RegionRequest(string world, Vector3 center, Vector3Int size, int? yOrigin)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = "region_request", ["world"] = world,
                ["center"] = new List<object> { Math.Round(center.x, 2), Math.Round(center.y, 2), Math.Round(center.z, 2) },
                ["size"] = new List<object> { size.x, size.y, size.z },
            };
            if (yOrigin.HasValue) d["y_origin"] = yOrigin.Value;
            return d;
        }

        /// <summary>region_request, origin form.</summary>
        public static Dictionary<string, object> RegionRequest(string world, Vector3Int origin, Vector3Int dims) => new Dictionary<string, object>
        {
            ["type"] = "region_request", ["world"] = world,
            ["origin"] = new List<object> { origin.x, origin.y, origin.z }, ["dims"] = new List<object> { dims.x, dims.y, dims.z },
        };

        /// <summary>region_request, origin form, with the view margin kept around the visible box (PROTOCOL.md 4.17).</summary>
        public static Dictionary<string, object> RegionRequest(string world, Vector3Int origin, Vector3Int dims, Vector3Int viewMargin)
        {
            var d = RegionRequest(world, origin, dims);
            d["view_margin"] = new List<object> { viewMargin.x, viewMargin.y, viewMargin.z };
            return d;
        }

        public static Dictionary<string, object> RegionQuery(string player = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "region_query" };
            if (!string.IsNullOrEmpty(player)) d["player"] = player;
            return d;
        }

        public static Dictionary<string, object> Ping(int id) => new Dictionary<string, object> { ["type"] = "ping", ["id"] = id };
        public static Dictionary<string, object> Bye(string reason) => new Dictionary<string, object> { ["type"] = "bye", ["reason"] = reason };

        /// <summary>Pose payload. Inputs are TabletopPose values already converted to the tabletop frame.</summary>
        public static Dictionary<string, object> Pose(long tsMicros, TabletopPose head, TabletopPose? left, TabletopPose? right)
        {
            var d = new Dictionary<string, object> { ["type"] = "pose", ["ts"] = tsMicros, ["head"] = head.ToJson() };
            d["left"] = left.HasValue ? left.Value.ToJson() : null;
            d["right"] = right.HasValue ? right.Value.ToJson() : null;
            return d;
        }
    }

    /// <summary>A pose in the protocol's tabletop frame (metres, right-handed, +X east, +Y up, +Z south).</summary>
    public struct TabletopPose
    {
        public Vector3 p;     // tabletop frame
        public Quaternion q;  // tabletop frame, [x,y,z,w]
        public bool tracked;

        /// <summary>PROTOCOL.md section 3: Unity anchor-local (left-handed) -> tabletop frame: p=[ux,uy,-uz], q=[-qx,-qy,qz,qw].</summary>
        public static TabletopPose FromUnityLocal(Vector3 u, Quaternion r, bool tracked = true) =>
            new TabletopPose { p = new Vector3(u.x, u.y, -u.z), q = new Quaternion(-r.x, -r.y, r.z, r.w), tracked = tracked };

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["p"] = new List<object> { Math.Round(p.x, 4), Math.Round(p.y, 4), Math.Round(p.z, 4) },
            ["q"] = new List<object> { Math.Round(q.x, 5), Math.Round(q.y, 5), Math.Round(q.z, 5), Math.Round(q.w, 5) },
            ["tracked"] = tracked,
        };
    }

    public sealed class ChunkSnapshot
    {
        public int SnapshotId;
        public string World;
        public Vector3Int Origin, Dims;
        public string[] Palette;
        public ushort[] Grid; // yzx
        public byte[] Light;  // optional light_v1: sky << 4 | block per cell, yzx (null = not sent)
        public TabletopCraft.World.BiomeGrid Biomes; // optional biomes_v1 (null = not sent)

        public static ChunkSnapshot From(Dictionary<string, object> m)
        {
            if (m.S("order") != "yzx" || m.S("encoding") != "rle_palette_v1") throw new FormatException("unsupported order/encoding");
            var o = Json.Ints(m.A("origin")); var d = Json.Ints(m.A("dims"));
            var pal = m.A("palette"); var names = new string[pal.Count];
            for (int i = 0; i < names.Length; i++) names[i] = (string)pal[i];
            var s = new ChunkSnapshot
            {
                SnapshotId = m.I("snapshot_id"), World = m.S("world"),
                Origin = new Vector3Int(o[0], o[1], o[2]), Dims = new Vector3Int(d[0], d[1], d[2]), Palette = names,
            };
            s.Grid = DecodeRle(m.A("data"), d[0] * d[1] * d[2], names.Length);
            s.Light = DecodeLight(m.O("light"), s.Grid.Length);
            s.Biomes = TabletopCraft.World.BiomeGrid.From(m.O("biomes"));
            return s;
        }

        /// <summary>chunk_snapshot.light (rle_v1 pairs of packed values over the yzx grid); null when absent.</summary>
        public static byte[] DecodeLight(Dictionary<string, object> o, int volume)
        {
            if (o == null) return null;
            if (o.S("encoding") != "rle_v1") throw new FormatException("unsupported light encoding " + o.S("encoding"));
            var data = o.A("data");
            if (data == null || data.Count % 2 != 0) throw new FormatException("bad light data");
            var b = new byte[volume];
            int p = 0;
            for (int k = 0; k < data.Count; k += 2)
            {
                int v = (int)(double)data[k], n = (int)(double)data[k + 1];
                if (n < 1 || p + n > volume || v < 0 || v > 255) throw new FormatException("bad light run at " + k);
                if (v != 0) for (int j = 0; j < n; j++) b[p + j] = (byte)v;
                p += n;
            }
            if (p != volume) throw new FormatException($"light runs cover {p} of {volume}");
            return b;
        }

        /// <summary>rle_palette_v1: pairs [paletteIndex, runLength]. Throws on a wrong total (caller sends bad_snapshot).</summary>
        public static ushort[] DecodeRle(List<object> data, int volume, int paletteSize)
        {
            if (data.Count % 2 != 0) throw new FormatException("odd rle length");
            var g = new ushort[volume];
            int p = 0;
            for (int k = 0; k < data.Count; k += 2)
            {
                int v = (int)(double)data[k], n = (int)(double)data[k + 1];
                if (n < 1 || p + n > volume || v < 0 || v >= paletteSize) throw new FormatException("bad run at " + k);
                for (int j = 0; j < n; j++) g[p + j] = (ushort)v;
                p += n;
            }
            if (p != volume) throw new FormatException($"runs cover {p} of {volume}");
            return g;
        }
    }

    public struct PlayerInfo
    {
        public string Uuid, Name, Skin;
        public string SkinModel; // "wide", "slim" or null (PROTOCOL.md 4.6, optional)
        public bool HasSkin;     // the message carried "skin" (sent once per UUID per connection, may be null)
        public Vector3 Pos; // world coords (feet)
        public float Yaw, Pitch;
        public bool Sneaking;
        // Additive (PROTOCOL.md 4.6, entities work): entity id, vehicle id (-1 = none), pose (null = standing), equipment.
        public int Id, Vehicle;
        public string Pose;
        public Dictionary<string, string> Equipment;

        /// <summary>One entry of player_positions.players.</summary>
        public static PlayerInfo From(Dictionary<string, object> p)
        {
            var pos = Json.Doubles(p.A("pos"));
            return new PlayerInfo
            {
                Uuid = p.S("uuid"), Name = p.S("name"), Pos = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]),
                Yaw = (float)p.D("yaw"), Pitch = (float)p.D("pitch"), Sneaking = p.B("sneaking"),
                HasSkin = p.ContainsKey("skin"), Skin = p.S("skin"), SkinModel = p.S("skin_model"),
                Id = p.I("id", -1), Vehicle = p.I("vehicle", -1), Pose = p.S("pose"), Equipment = EntityStore.Equipment(p.O("equipment")),
            };
        }
    }

    /// <summary>Server limits from welcome.limits, region_info.limits or a region_too_large error.</summary>
    public struct RegionLimits
    {
        public int MaxX, MaxY, MaxZ; public long MaxBlocks, MaxBytes;
        /// <summary>[area] limits.max_view_width (PROTOCOL.md 4.25): widest visible window; 0 = not sent.</summary>
        public int MaxViewWidth;
        public static RegionLimits Default => new RegionLimits { MaxX = 128, MaxY = 384, MaxZ = 128, MaxBlocks = 2097152, MaxBytes = 4194304 };
        public static RegionLimits From(Dictionary<string, object> o, RegionLimits fallback)
        {
            if (o == null) return fallback;
            var r = fallback;
            if (o.A("max_dims") is List<object> md && md.Count == 3) { var v = Json.Doubles(md); r.MaxX = (int)v[0]; r.MaxY = (int)v[1]; r.MaxZ = (int)v[2]; }
            if (o.ContainsKey("max_blocks")) r.MaxBlocks = (long)o.D("max_blocks");
            if (o.ContainsKey("max_bytes")) r.MaxBytes = (long)o.D("max_bytes");
            if (o.ContainsKey("max_view_width")) r.MaxViewWidth = (int)o.D("max_view_width");   // [area]
            return r;
        }
    }

    public struct RegionPlayer { public string Name, Uuid, World; public Vector3 Pos; public float Yaw; }

    /// <summary>region_info (PROTOCOL.md 4.15).</summary>
    public sealed class RegionInfo
    {
        public string World; public Vector3Int Origin, Dims; public RegionLimits Limits;
        public readonly List<RegionPlayer> Players = new List<RegionPlayer>();
        public static RegionInfo From(Dictionary<string, object> m)
        {
            var r = new RegionInfo();
            var reg = m.O("region");
            r.World = reg.S("world");
            var o = Json.Doubles(reg.A("origin")); var d = Json.Doubles(reg.A("dims"));
            r.Origin = new Vector3Int((int)o[0], (int)o[1], (int)o[2]); r.Dims = new Vector3Int((int)d[0], (int)d[1], (int)d[2]);
            r.Limits = RegionLimits.From(m.O("limits"), RegionLimits.Default);
            foreach (var x in m.A("players") ?? new List<object>())
            {
                var p = (Dictionary<string, object>)x; var pos = Json.Doubles(p.A("pos"));
                r.Players.Add(new RegionPlayer { Name = p.S("name"), Uuid = p.S("uuid"), World = p.S("world"), Pos = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]), Yaw = (float)p.D("yaw") });
            }
            return r;
        }
    }
}
