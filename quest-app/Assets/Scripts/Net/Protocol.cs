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
        public const string ClientVersion = "0.1.0";

        public static Dictionary<string, object> Hello(string token) => new Dictionary<string, object>
        {
            ["type"] = "hello", ["client"] = ClientName, ["client_version"] = ClientVersion,
            ["protocols"] = new List<object> { Version }, ["token"] = token ?? "",
            ["caps"] = new List<object> { "rle_palette_v1", "avatar" },
        };

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
            return s;
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
        public Vector3 Pos; // world coords (feet)
        public float Yaw, Pitch;
        public bool Sneaking;
    }
}
