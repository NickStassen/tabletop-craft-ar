// [sound] Asset delivery (PROTOCOL.md "Asset delivery", cap assets_v1): the server operator's own pack.zip,
// entity_models.json and sounds.zip offered by their own server and pulled by the headset in binary frames. Messages
// and the frame format; the download itself is World/AssetSync.cs. Engine-free (Tests~).
using System;
using System.Collections.Generic;
using System.Text;

namespace TabletopCraft.Net
{
    public sealed class AssetFile
    {
        public string Name, Sha256;
        public long Size;
    }

    public static class AssetProtocol
    {
        public const string Cap = "assets_v1", Type = "assets";
        /// <summary>The only files the app accepts (and the server serves), all into the app's files folder.</summary>
        public static readonly string[] Names = { "entity_models.json", "sounds.zip", "pack.zip" };
        public const int MaxChunk = 1 << 20;
        public const long MaxSize = int.MaxValue;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("TTA1");

        /// <summary>The offer: known names with a size and a SHA-256 (others are ignored); chunk = the largest length to ask for.</summary>
        public static List<AssetFile> ParseOffer(Dictionary<string, object> m, out int chunk)
        {
            chunk = Math.Max(1024, Math.Min(MaxChunk, m.I("chunk", 262144)));
            var list = new List<AssetFile>();
            foreach (var o in m.A("files") ?? new List<object>())
            {
                if (!(o is Dictionary<string, object> f)) continue;
                string name = f.S("name"), sha = f.S("sha256");
                long size = (long)f.D("size", -1);
                if (Array.IndexOf(Names, name) < 0 || sha == null || sha.Length != 64 || size <= 0 || size > MaxSize) continue;
                list.Add(new AssetFile { Name = name, Size = size, Sha256 = sha.ToLowerInvariant() });
            }
            return list;
        }

        public static Dictionary<string, object> Get(string name, long offset, int length) => new Dictionary<string, object>
        {
            ["type"] = "asset_get", ["name"] = name, ["offset"] = offset, ["length"] = length,
        };

        /// <summary>
        /// A binary frame: "TTA1", the name's length (1..64) and ASCII name, the offset (8 bytes, big-endian), then the data
        /// from dataStart to len. False for anything else.
        /// </summary>
        public static bool ParseChunk(byte[] buf, int len, out string name, out long offset, out int dataStart)
        {
            name = null; offset = 0; dataStart = 0;
            if (buf == null || len < 15 || len > buf.Length) return false;
            for (int i = 0; i < 4; i++) if (buf[i] != Magic[i]) return false;
            int n = buf[4];
            if (n < 1 || n > 64 || len <= 13 + n) return false;   // at least one byte of data
            name = Encoding.ASCII.GetString(buf, 5, n);
            long off = 0;
            for (int i = 0; i < 8; i++) off = (off << 8) | buf[5 + n + i];
            offset = off;
            dataStart = 13 + n;
            return offset >= 0;
        }

        /// <summary>A frame as the server builds it (tests).</summary>
        public static byte[] Frame(string name, long offset, byte[] data)
        {
            var n = Encoding.ASCII.GetBytes(name);
            var b = new byte[13 + n.Length + data.Length];
            Array.Copy(Magic, b, 4);
            b[4] = (byte)n.Length;
            Array.Copy(n, 0, b, 5, n.Length);
            for (int i = 0; i < 8; i++) b[5 + n.Length + i] = (byte)(offset >> (56 - 8 * i));
            Array.Copy(data, 0, b, 13 + n.Length, data.Length);
            return b;
        }
    }
}
