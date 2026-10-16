// Engine-free tests for the easy-setup work (1.1.0): the asset offer's state and note (World/AssetSync.cs), pairing
// (Net/Pairing.cs: the token store, codes, welcome.pairing, older plugins; the LinkClient's pairing state and an unpaired
// token against a fake server), the discovery reply's id, and the first run without a server (World/SessionState.cs).
// --live-pair URL CONSOLE_FIFO pairs the app's own classes with a real plugin (tools/pair_check.py does the same in Python).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TabletopCraft.Net;
using TabletopCraft.World;

static class SetupTests
{
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string[] args)
    {
        string Fx(string n) => File.ReadAllText(fixture(n + ".json"));
        AssetOfferNote(check, Fx);
        Hello(check);
        Store(check);
        Messages(check, Fx);
        Discovery(check, Fx);
        Cards(check);
        LinkPairing(check);
    }

    static void AssetOfferNote(Action<bool, string> check, Func<string, string> fx)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tabletop-setup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            var s = new AssetSync(dir);
            s.OnOffer(Parse(fx("assets_building")));
            check(s.ServerState == "building" && s.ServerNote.Contains("26.3"), "setup: assets offer keeps state and note");
            check(s.Status.StartsWith("the server is preparing the game files") && s.Status.Contains("26.3"), "setup: building offer status: " + s.Status);
            var missing = Parse(fx("assets_building"));
            missing["state"] = "missing"; missing["note"] = "No Minecraft 26.3 on the server's PC";
            s.OnOffer(missing);
            check(s.Status == "the server offers nothing: No Minecraft 26.3 on the server's PC", "setup: missing offer status: " + s.Status);
            var old = new AssetSync(dir);
            old.OnOffer(Parse("{\"type\":\"assets\",\"chunk\":262144,\"files\":[]}"));
            check(old.ServerState == "" && old.Status == "the server offers nothing", "setup: a 1.0 offer (no state) still reads as before");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void Hello(Action<bool, string> check)
    {
        var h = Protocol.Hello("", "tok123", "Quest 3");
        var caps = (List<object>)h["caps"];
        check(caps.Contains(PairingProtocol.Cap) && (string)h["device_token"] == "tok123" && (string)h["device_name"] == "Quest 3", "pair: hello asks for pair_v1 with the device token and name");
        var plain = Protocol.Hello("");
        check(((List<object>)plain["caps"]).Contains("pair_v1") && !plain.ContainsKey("device_token"), "pair: no token, no device_token field (still pair_v1)");
        check(PairingStore.DeviceName("Oculus Quest 3") == "Quest 3" && PairingStore.DeviceName("Meta Quest 3S") == "Quest 3S" && PairingStore.DeviceName("") == "Headset"
              && PairingStore.DeviceName("Quest Pro") == "Quest Pro", "pair: device names from the model");
    }

    static void Store(Action<bool, string> check)
    {
        var s = new PairingStore();
        s.Save("3f9c2a7e1b4d5c6a", "ws://10.0.0.5:8765/tabletop", "tokA", "a1b2c3d4", "Quest 3", 1);
        s.Save("0000aaaa", "ws://10.0.0.9:8765/tabletop", "tokB", "b1", "Quest 3", 2);
        check(s.TokenFor("ws://10.0.0.5:8765/tabletop", null) == "tokA" && s.TokenFor("ws://elsewhere/t", "3f9c2a7e1b4d5c6a") == "tokA", "pair: token by URL or by server id");
        check(s.Seen("ws://10.0.0.6:8765/tabletop", "3f9c2a7e1b4d5c6a") && s.TokenFor("ws://10.0.0.6:8765/tabletop", null) == "tokA", "pair: a server that moved is found at its new address");
        s.Save("3f9c2a7e1b4d5c6a", "ws://10.0.0.6:8765/tabletop", "tokC", "c1", "Quest 3", 3);
        check(s.Entries.Count == 2 && s.TokenFor(null, "3f9c2a7e1b4d5c6a") == "tokC", "pair: pairing again replaces the old token");
        var back = PairingStore.Parse(s.ToJson());
        check(back.Entries.Count == 2 && back.TokenFor("ws://10.0.0.9:8765/tabletop", null) == "tokB" && back.Find(null, "3f9c2a7e1b4d5c6a").Device == "c1", "pair: pairings.json round trip");
        check(back.Forget("ws://10.0.0.9:8765/tabletop", null) && back.TokenFor("ws://10.0.0.9:8765/tabletop", null) == null && !back.Forget("ws://nope", "nope"), "pair: forget an unpaired server's token");
        bool threw = false;
        try { PairingStore.Parse("[1]"); } catch (FormatException) { threw = true; }
        check(threw && PairingStore.Parse("").Entries.Count == 0, "pair: a broken pairings.json is reported, an empty one is fine");
    }

    static void Messages(Action<bool, string> check, Func<string, string> fx)
    {
        var code = PairCode.From(Parse(fx("pair_code")), 100);
        check(code != null && code.Code == "482913" && code.Spaced == "482 913" && code.Required && code.SecondsLeft(100) == 300 && code.Live(399) && !code.Live(400.1),
              "pair: pair_code fixture (code, spacing, five minutes)");
        var bad = Parse(fx("pair_code")); bad["code"] = "48a913";
        check(PairCode.From(bad, 0) == null, "pair: a code that isn't 6 digits is ignored");
        var w = PairingWelcome.From(Parse(fx("welcome_pair")));
        check(w.Supported && w.Paired && w.Device == "a1b2c3d4" && w.ServerId == "3f9c2a7e1b4d5c6a" && !w.Required && PluginSupport.Note(w, false) == null, "pair: welcome_pair fixture");
        var old = PairingWelcome.From(Parse(fx("welcome")));
        string note = PluginSupport.Note(old, false);
        check(!old.Supported && note != null && note.Contains("update it to 1.1.0") && note.Contains("textures") && note.Contains("github.com/NickStassen/tabletop-craft-ar/releases"),
              "pair: a 1.0 plugin gets an update note: " + note);
        check(!PluginSupport.Note(old, true).Contains("textures"), "pair: the note leaves out textures when the headset has a pack");
        check(PluginSupport.Compare("1.0.0", "1.1.0") < 0 && PluginSupport.Compare("1.10.0", "1.9.3") > 0 && PluginSupport.Compare("1.1", "1.1.0") == 0
              && PluginSupport.Compare("1.1.0-rc1", "1.1.0") == 0, "pair: version compare");
        var paired = Parse(fx("paired"));
        check(paired.S("token") == Parse(fx("hello_pair")).S("device_token") && paired.S("server_id") == w.ServerId, "pair: paired fixture matches hello_pair and welcome_pair");
        check(PairingProtocol.Request("Quest 3").S("type") == "pair_request" && Parse(fx("pair_request")).S("device_name") == "Quest 3", "pair: pair_request");
    }

    static void Discovery(Action<bool, string> check, Func<string, string> fx)
    {
        var f = LanDiscovery.ParseReply(fx("discovery_reply_pair"), "10.0.0.5");
        check(f != null && f.Id == "3f9c2a7e1b4d5c6a" && f.Pair && f.Url == "ws://10.0.0.5:8765/tabletop", "pair: discovery reply id and pair");
        var t = Parse(fx("discovery_reply_pair")); t["token"] = true;
        var ft = LanDiscovery.ParseReply(Json.Write(t), "10.0.0.5");
        check(!ft.Label.Contains("token"), "pair: a server that pairs needs no typed token (no 'token' note): " + ft.Label);
        var old = LanDiscovery.ParseReply(fx("discovery_reply"), "10.0.0.5");
        check(old.Id == "" && !old.Pair, "pair: a 1.0 reply has no id and doesn't pair");
    }

    static void Cards(Action<bool, string> check)
    {
        var code = PairCode.From(Parse("{\"code\":\"482913\",\"expires_s\":300,\"required\":false}"), 0);
        string t = SetupCard.PairText(code, 61, "10.0.0.5");
        check(t.Contains("482 913") && t.Contains("tabletop pair 482913") && t.Contains("3:59") && t.Contains("textures"), "pair: the pairing card");
        var run = new FirstRun { Active = true };
        check(run.ShouldSearch(0, false) && !run.ShouldSearch(0, true), "first run: searches at once");
        check(SetupCard.FirstRunText(run).Contains("Looking for your Minecraft server") && SetupCard.FirstRunText(run).Contains(SetupCard.Disclaimer), "first run: card while searching, with the disclaimer");
        string pick = run.Searched(new List<FoundServer>(), 1);
        check(pick == null && !run.ShouldSearch(5, false) && run.ShouldSearch(9.1, false) && SetupCard.FirstRunText(run).Contains("No server found"), "first run: none found, search again in 8 s");
        var a = new FoundServer { Url = "ws://10.0.0.5:8765/tabletop" };
        var portOnly = new FoundServer { Url = "ws://10.0.0.7:8765/tabletop", PortOnly = true };
        check(run.Searched(new List<FoundServer> { a, portOnly }, 10) == a.Url, "first run: the only plugin that answered is picked (a host with just the port open isn't)");
        check(run.Searched(new List<FoundServer> { a, new FoundServer { Url = "ws://10.0.0.6:8765/tabletop" } }, 20) == null
              && SetupCard.FirstRunText(run).Contains("Found 2 servers"), "first run: several servers: the user picks");
        run.Done();
        check(!run.ShouldSearch(100, false) && run.Searched(new List<FoundServer> { a }, 100) == null, "first run: done once a server is chosen");
        check(SetupCard.Disclaimer == "NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.", "setup: the exact Minecraft disclaimer");
    }

    /// <summary>
    /// --live-pair URL CONSOLE_FIFO: the app's own classes against a real plugin. Pairs (the code typed into the server
    /// console FIFO), keeps the token in a PairingStore, reconnects with it and downloads every offered game file with
    /// AssetSync into a temp folder (checked by SHA-256 there), like a store install with no files on the headset.
    /// </summary>
    public static void LivePair(string url, string fifo, Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tabletop-livepair-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        var store = new PairingStore();
        var c = new LinkClient(url, "") { DeviceName = "live-pair" };
        try
        {
            c.Start();
            Dictionary<string, object> m = null;
            PairCode code = null;
            bool welcomed = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10000 && code == null)
            {
                while (c.Inbound.TryDequeue(out m))
                {
                    if (m.S("type") == "pair_code") code = PairCode.From(m, 0);
                    else if (m.S("type") == "welcome") { welcomed = true; if (PairingWelcome.From(m).Supported) c.Send(PairingProtocol.Request("live-pair")); }
                }
                Thread.Sleep(50);
            }
            check(code != null, $"live pair: a code ({(welcomed ? "open server, after pair_request" : "the server waits for it")})");
            if (code == null) return;
            File.AppendAllText(fifo, $"tabletop pair {code.Code}\n");
            string token = null, sid = null;
            ComfortTests.Until(() =>
            {
                while (c.Inbound.TryDequeue(out var x)) if (x.S("type") == "paired") { token = x.S("token"); sid = x.S("server_id"); }
                return token != null;
            }, 10000);
            check(token != null && sid != null, "live pair: paired (token kept for server " + sid + ")");
            if (token == null) return;
            store.Save(sid, url, token, "", "live-pair", 0);
            c.Shutdown("test", 200);

            // A new link as after a restart: the stored token, a trusted welcome, the game files.
            c = new LinkClient(url, "") { DeviceName = "live-pair", DeviceToken = PairingStore.Parse(store.ToJson()).TokenFor(url, null) };
            var sync = new AssetSync(dir);
            c.Binary = sync.OnBinary;
            c.Start();
            PairingWelcome pw = null;
            var done = new List<string>();
            sync.Completed = n => done.Add(n);
            sw.Restart();
            while (sw.ElapsedMilliseconds < 120000 && (pw == null || sync.Busy || !sync.Offered || done.Count < sync.Items.Count))
            {
                while (c.Inbound.TryDequeue(out var x))
                {
                    if (x.S("type") == "welcome") pw = PairingWelcome.From(x);
                    else if (x.S("type") == "assets") sync.OnOffer(x);
                }
                if (c.Connected) foreach (var g in sync.Pump()) c.Send(g); else sync.Pump();
                Thread.Sleep(20);
            }
            check(pw != null && pw.Paired && pw.ServerId == sid, "live pair: the stored token gives a paired welcome");
            var names = sync.Items.Select(i => i.Name).OrderBy(n => n).ToList();
            bool sums = names.All(n => File.Exists(Path.Combine(dir, n)) && AssetSync.Sha256File(Path.Combine(dir, n)) == sync.Items.First(i => i.Name == n).Offer.Sha256);
            long mb = names.Sum(n => new FileInfo(Path.Combine(dir, n)).Length) / 1000000;
            check(names.Count == 3 && done.Count == 3 && sums, $"live pair: every game file downloaded and verified ({string.Join(", ", names)}, {mb} MB in {sw.Elapsed.TotalSeconds:F0} s; {sync.Status})");
        }
        finally
        {
            c.Shutdown("test", 200);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>LinkClient against a fake plugin: pairing before welcome (pings, pair_request), and an unpaired token.</summary>
    static void LinkPairing(Action<bool, string> check)
    {
        // The server waits for pairing: a code, then (after a pair_request and a few pings) paired and welcome.
        var seen = new List<string>();
        string helloToken = "?";
        using (var srv = new ComfortTests.FakeServer(async (ws, n) =>
        {
            for (var m = await ComfortTests.srvNext(ws); m != null; m = await ComfortTests.srvNext(ws))
            {
                lock (seen) seen.Add(m.S("type"));
                if (m.S("type") == "hello")
                {
                    helloToken = m.S("device_token") ?? "";
                    await ComfortTests.FakeServer.Send(ws, "{\"v\":1,\"type\":\"pair_code\",\"seq\":0,\"t\":0,\"code\":\"123456\",\"expires_s\":300,\"required\":true}");
                }
                else if (m.S("type") == "ping") await ComfortTests.FakeServer.Send(ws, $"{{\"v\":1,\"type\":\"pong\",\"seq\":1,\"t\":0,\"id\":{m.I("id")},\"echo_t\":{(long)m.D("t")}}}");
                else if (m.S("type") == "pair_request")
                {
                    await ComfortTests.FakeServer.Send(ws, "{\"v\":1,\"type\":\"paired\",\"seq\":2,\"t\":0,\"token\":\"q1mTq3z9yZQ0nI5c1o0m8m3rT9JdY0cB7v1hJ3zK2xA\",\"device\":\"a1b2c3d4\",\"server_id\":\"3f9c2a7e1b4d5c6a\"}");
                    await ComfortTests.FakeServer.Send(ws, "{\"v\":1,\"type\":\"welcome\",\"seq\":3,\"t\":0,\"session\":\"s\"}");
                }
            }
        }))
        {
            var c = new LinkClient(srv.Url, "") { PingSeconds = 0.2f, DeadSeconds = 1.5f, DeviceName = "Quest 3" };
            c.Start();
            bool pairing = ComfortTests.Until(() => c.Pairing, 3000);
            Thread.Sleep(2000);   // longer than DeadSeconds: pings keep the waiting link alive
            int pings; lock (seen) pings = seen.Count(x => x == "ping");
            check(pairing && !c.Connected && pings >= 3 && c.DeadAborts == 0 && srv.Connections == 1, $"pair link: waiting for pairing pings and stays up ({pings} pings)");
            c.Send(new Dictionary<string, object> { ["type"] = "chat_send", ["text"] = "x" });
            c.SendPairing(PairingProtocol.Request("Quest 3"));
            bool welcomed = ComfortTests.Until(() => c.Connected, 3000);
            var types = new List<string>(); while (c.Inbound.TryDequeue(out var m)) types.Add(m.S("type"));
            List<string> got; lock (seen) got = seen.ToList();
            check(welcomed && !c.Pairing && types.SequenceEqual(new[] { "pair_code", "paired", "welcome" }) && !got.Contains("chat_send") && helloToken == "",
                  $"pair link: pair_request goes out while waiting, other messages don't; paired then welcome ({string.Join(",", types)})");
            c.Shutdown("test", 100);
        }

        // An unpaired token: close 4003 -> the client forgets it and connects again without it (no fatal stop).
        var tokens = new List<string>();
        using (var srv = new ComfortTests.FakeServer(async (ws, n) =>
        {
            var m = await ComfortTests.srvNext(ws);
            lock (tokens) tokens.Add(m?.S("device_token") ?? "");
            if (n == 1)
            {
                await ComfortTests.FakeServer.Send(ws, "{\"v\":1,\"type\":\"error\",\"seq\":0,\"t\":0,\"code\":\"auth_failed\",\"message\":\"this headset was unpaired on the server\",\"fatal\":true}");
                await ws.CloseOutputAsync((WebSocketCloseStatus)4003, "unpaired", CancellationToken.None);
                await Task.Delay(200);
                return;
            }
            await ComfortTests.FakeServer.Send(ws, "{\"v\":1,\"type\":\"welcome\",\"seq\":0,\"t\":0,\"session\":\"s\"}");
            while (await ComfortTests.srvNext(ws) != null) { }
        }))
        {
            var c = new LinkClient(srv.Url, "") { DeviceToken = "oldtoken", DeviceName = "Quest 3" };
            c.Start();
            bool back = ComfortTests.Until(() => c.Connected, 5000);
            List<string> t; lock (tokens) t = tokens.ToList();
            check(back && c.DeviceTokenRejected && c.DeviceToken == null && c.FatalCode == 0 && t.Count == 2 && t[0] == "oldtoken" && t[1] == "",
                  $"pair link: an unpaired token is dropped and the link comes back without it ({string.Join(",", t)})");
            c.Shutdown("test", 100);
        }
    }
}
