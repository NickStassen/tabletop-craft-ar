// Server addresses and LAN discovery (docs/PROTOCOL.md section 7, docs/QUEST_SETUP.md Part D). Engine-free (Tests~).
//
// The headset broadcasts a small UDP probe to port 8765 (the plugin's discovery.port) and every plugin on the LAN
// answers the sender directly with its name, WebSocket port and path. Sending a broadcast and receiving a unicast reply
// need no extra Android permission (receiving broadcasts would need a WifiManager.MulticastLock and the
// CHANGE_WIFI_MULTICAST_STATE permission). If nothing answers within a second (a firewall that only lets TCP 8765
// through, say), the local /24 is scanned for an open TCP port 8765 instead; those hosts are listed as "port open".
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TabletopCraft.Net
{
    public static class ServerAddress
    {
        public const int DefaultPort = 8765;
        public const string DefaultPath = "/tabletop";

        /// <summary>
        /// A typed address as a WebSocket URL: "192.168.1.50" -> ws://192.168.1.50:8765/tabletop, "host:8800" ->
        /// ws://host:8800/tabletop, a full ws:// or wss:// URL is kept (its path defaults to /tabletop). Null when it
        /// isn't an address.
        /// </summary>
        public static string Normalize(string text)
        {
            string t = (text ?? "").Trim();
            if (t.Length == 0 || t.IndexOf(' ') >= 0) return null;
            bool scheme = t.Contains("://");
            if (scheme && !t.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) return null;
            // Without a scheme the port defaults to the plugin's 8765, not ws's 80.
            bool typedPort = !scheme && t.Split('/')[0].Contains(":");
            if (!scheme) t = "ws://" + t;
            if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
            if (Uri.CheckHostName(u.Host) == UriHostNameType.Unknown) return null;
            int port = scheme || typedPort ? u.Port : DefaultPort;
            if (port <= 0 || port > 65535) return null;
            string path = u.AbsolutePath == "/" || u.AbsolutePath.Length == 0 ? DefaultPath : u.AbsolutePath;
            return $"{u.Scheme.ToLowerInvariant()}://{u.Host}:{port}{path}";
        }

        /// <summary>Host of a URL for short labels ("192.168.1.50"), or the text itself.</summary>
        public static string HostOf(string url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host : url ?? "";
    }

    /// <summary>A server that answered the discovery probe (or a host with the port open).</summary>
    public sealed class FoundServer
    {
        public string Name = "", Url = "", Host = "", Mc = "", Version = "";
        public int Players;
        public bool TokenNeeded, PortOnly;
        /// <summary>Button text: "Name (host)", with "token" / "port open" notes.</summary>
        public string Label
        {
            get
            {
                string s = PortOnly ? $"{Host} (port open)" : $"{(Name.Length > 0 ? Name : "Server")} ({Host})";
                if (TokenNeeded) s += " token";
                return s.Length > 40 ? s.Substring(0, 39) + "…" : s;
            }
        }
    }

    public static class LanDiscovery
    {
        public const string ProbeType = "tabletop_discover", ReplyType = "tabletop_server";
        public const int MaxReplyBytes = 2048;

        public static byte[] Probe() => Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object> { ["type"] = ProbeType, ["v"] = Protocol.Version }));

        /// <summary>A discovery reply from senderHost, or null if it isn't one.</summary>
        public static FoundServer ParseReply(string json, string senderHost)
        {
            Dictionary<string, object> m;
            try { m = Json.Parse(json) as Dictionary<string, object>; } catch (FormatException) { return null; }
            if (m == null || m.S("type") != ReplyType || m.I("v") != Protocol.Version) return null;
            int port = m.I("port");
            string path = m.S("path") ?? ServerAddress.DefaultPath;
            if (port <= 0 || port > 65535 || !path.StartsWith("/")) return null;
            string url = ServerAddress.Normalize($"ws://{senderHost}:{port}{path}");
            if (url == null) return null;
            string name = m.S("name") ?? "";
            if (name.Length > 48) name = name.Substring(0, 48);
            return new FoundServer
            {
                Name = name, Url = url, Host = senderHost, Mc = m.S("mc") ?? "", Version = m.S("version") ?? "",
                Players = Math.Max(0, m.I("players")), TokenNeeded = m.B("token"),
            };
        }

        /// <summary>This device's LAN IPv4 address (no packet is sent), or null.</summary>
        public static IPAddress LocalIPv4()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(new IPEndPoint(IPAddress.Parse("10.254.254.254"), 9));   // a route lookup only
                if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address) && !ep.Address.Equals(IPAddress.Any)) return ep.Address;
            }
            catch (SocketException) { }
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address)) return a.Address;
                }
            }
            catch (Exception) { }   // Android may refuse interface lists
            return null;
        }

        /// <summary>10/8, 172.16/12, 192.168/16, 169.254/16.</summary>
        public static bool IsPrivate(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        /// <summary>Where the probe goes: the limited broadcast, plus the /24 broadcast of the local address.</summary>
        public static List<IPAddress> Targets(IPAddress local)
        {
            var l = new List<IPAddress> { IPAddress.Broadcast };
            if (local != null && local.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = local.GetAddressBytes();
                l.Add(new IPAddress(new byte[] { b[0], b[1], b[2], 255 }));
            }
            return l;
        }

        /// <summary>The other hosts of local's /24 (1..254), for the TCP fallback.</summary>
        public static List<IPAddress> Subnet24(IPAddress local)
        {
            var l = new List<IPAddress>();
            if (local == null || local.AddressFamily != AddressFamily.InterNetwork) return l;
            var b = local.GetAddressBytes();
            for (int i = 1; i < 255; i++) l.Add(new IPAddress(new byte[] { b[0], b[1], b[2], (byte)i }));
            return l;
        }

        /// <summary>
        /// Sends the probe to every target and collects replies for timeoutMs (blocking: run it on a worker). Replies
        /// with the same URL are listed once.
        /// </summary>
        public static List<FoundServer> ProbeUdp(IEnumerable<IPAddress> targets, int port, int timeoutMs)
        {
            var found = new List<FoundServer>();
            var urls = new HashSet<string>();
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            var probe = Probe();
            int sent = 0;
            foreach (var t in targets)
            {
                try { s.SendTo(probe, new IPEndPoint(t, port)); sent++; }
                catch (SocketException) { }   // no route to that broadcast address
            }
            if (sent == 0) return found;
            var buf = new byte[MaxReplyBytes];
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) break;
                s.ReceiveTimeout = left;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = s.ReceiveFrom(buf, ref from); }
                catch (SocketException) { break; }   // timed out
                var f = ParseReply(Encoding.UTF8.GetString(buf, 0, n), ((IPEndPoint)from).Address.ToString());
                if (f != null && urls.Add(f.Url)) found.Add(f);
            }
            return found;
        }

        /// <summary>Hosts with the TCP port open (connect within timeoutMs, `parallel` at a time).</summary>
        public static async Task<List<FoundServer>> ScanTcp(IEnumerable<IPAddress> hosts, int port, int timeoutMs, int parallel = 48)
        {
            var found = new List<FoundServer>();
            var gate = new SemaphoreSlim(parallel);
            var tasks = new List<Task>();
            foreach (var h in hosts)
            {
                await gate.WaitAsync();
                var host = h;
                tasks.Add(Task.Run(async () =>
                {
                    var c = new TcpClient();
                    try
                    {
                        var t = c.ConnectAsync(host, port);
                        _ = t.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        if (await Task.WhenAny(t, Task.Delay(timeoutMs)) == t && c.Connected)
                            lock (found) found.Add(new FoundServer { Host = host.ToString(), Url = $"ws://{host}:{port}{ServerAddress.DefaultPath}", PortOnly = true });
                    }
                    catch (Exception) { }
                    finally { c.Dispose(); gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks);
            found.Sort((a, b) => string.CompareOrdinal(a.Host, b.Host));
            return found;
        }

        /// <summary>
        /// The whole search for the Info tab's "Find servers": the UDP probe (udpMs), then, if nothing answered, a TCP
        /// scan of the local /24 for the port (tcpMs per host). Run it on a worker.
        /// </summary>
        public static async Task<List<FoundServer>> Find(int port = ServerAddress.DefaultPort, int udpMs = 1200, int tcpMs = 400)
        {
            var local = LocalIPv4();
            var found = await Task.Run(() => ProbeUdp(Targets(local), port, udpMs));
            if (found.Count > 0 || local == null) return found;
            return await ScanTcp(Subnet24(local), port, tcpMs);
        }
    }
}
