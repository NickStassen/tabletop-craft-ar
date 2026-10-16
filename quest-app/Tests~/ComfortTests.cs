// Engine-free tests for the comfort work (0.3.1): the reusable keyboard (World/KeyboardModel.cs) and the block menu's
// Info tab (Reset view, Server field, found servers), prefs.json (World/AppPrefs.cs), server addresses and LAN discovery
// (Net/LanDiscovery.cs), the link's inbound queue, dead-link detection, fatal closes and shutdown (Net/LinkClient.cs,
// against a small WebSocket server in this file), the session helpers (World/SessionState.cs), keyed toasts, and the
// orientation aids in NavState. --discover PORT probes a live plugin's discovery port on this machine and the LAN.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class ComfortTests
{
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string[] args)
    {
        Keyboard(check);
        MenuInfo(check, fixture);
        Prefs(check);
        Addresses(check);
        Discovery(check);
        Queue(check);
        Link(check);
        Session(check);
        Orientation(check);
        int i = Array.IndexOf(args, "--discover");
        if (i >= 0 && i + 1 < args.Length) LiveDiscover(check, int.Parse(args[i + 1]));
        i = Array.IndexOf(args, "--live-link");
        if (i >= 0 && i + 1 < args.Length) LiveLink(check, args[i + 1]);
        i = Array.IndexOf(args, "--live-token");
        if (i >= 0 && i + 1 < args.Length) LiveToken(check, args[i + 1]);
    }

    /// <summary>
    /// A real plugin goes silent (freeze it with kill -STOP after LINK_UP, resume with kill -CONT after ~10 s): the
    /// client must notice within DeadSeconds, keep retrying, and get a new welcome once the server is back.
    /// </summary>
    static void LiveLink(Action<bool, string> check, string url)
    {
        var c = new LinkClient(url, "");
        c.Start();
        bool up = Until(() => c.Connected && c.RttMs >= 0, 10000);
        Console.WriteLine($"LINK_UP {up} rtt {c.RttMs} ms");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool dead = Until(() => c.DeadAborts >= 1, 40000);
        double deadAt = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"LINK_DEAD {dead} after {deadAt:F1} s: {c.Status}; rtt {c.RttMs}");
        bool back = Until(() => c.Welcomes >= 2 && c.Connected, 60000);
        check(up && dead && c.RttMs >= -1 && back, $"live link: frozen server noticed ({c.DeadAborts} aborts), reconnected after it came back ({c.Welcomes} welcomes, {sw.Elapsed.TotalSeconds:F1} s)");
        c.Shutdown("test", 500, Protocol.AvatarConfig(false, 0.01f, "", true, "Tabletop Giant"));
    }

    /// <summary>A plugin with auth.token set and a client with the wrong one: close 4003, no retries.</summary>
    static void LiveToken(Action<bool, string> check, string url)
    {
        var c = new LinkClient(url, "definitely-wrong");
        c.Start();
        bool fatal = Until(() => c.FatalCode != 0, 8000);
        Thread.Sleep(3000);
        check(fatal && c.FatalCode == 4003 && c.Welcomes == 0, $"live token: wrong token -> {c.FatalCode} ({c.Status}), not retrying");
        int n = 0; while (c.Inbound.TryDequeue(out var m)) if (m.S("type") == "error" && m.S("code") == "auth_failed") n++;
        check(n == 1, "live token: exactly one auth_failed error queued (the toast)");
        c.Shutdown("test", 50);
    }

    // ---------- keyboard ----------

    static void Type(KeyboardModel k, string s)
    {
        foreach (char c in s)
        {
            int li = KeyboardModel.Letters.IndexOf(char.ToLowerInvariant(c));
            if (c == ' ') { k.Press(KeyboardModel.KeySpace); continue; }
            if (li >= 0)
            {
                if (k.SymbolsLayer) k.Press(KeyboardModel.KeyLayer);
                if (char.IsUpper(c) && k.Shift == ShiftState.Off) k.Press(KeyboardModel.KeyShift);
                k.Press(li);
                continue;
            }
            int si = KeyboardModel.Symbols.IndexOf(c);
            if (si < 0) throw new ArgumentException("no key for " + c);
            if (!k.SymbolsLayer) k.Press(KeyboardModel.KeyLayer);
            k.Press(KeyboardModel.KeySymbol0 + si);
        }
    }

    static void Keyboard(Action<bool, string> check)
    {
        var k = new KeyboardModel { MaxLength = 96 };
        string need = "/~^@[]=,.:_-!?\"'#+*{}0123456789";
        check(need.All(c => KeyboardModel.Symbols.IndexOf(c) >= 0), "keyboard: the ?123 layer has every digit and the symbols commands need");
        var L = k.Layout(0, 0, 0.3f, 0.024f);
        check(L.Count == 10 + 9 + 9 + 4 && L.Any(i => i.Index == KeyboardModel.KeyShift) && L.Any(i => i.Index == KeyboardModel.KeyEnter) && L.All(i => i.Part == MenuPart.Key),
              $"keyboard: letters layer = 3 letter rows with shift/del + ?123, space, clear, enter ({L.Count} keys)");
        check(L.All(i => i.R.X >= -1e-5f && i.R.X + i.R.W <= 0.3f + 1e-5f && i.R.Y + i.R.H <= 4 * 0.024f + 1e-5f), "keyboard: keys inside the given box");
        bool overlap = false;
        for (int a = 0; a < L.Count; a++) for (int b = a + 1; b < L.Count; b++)
        { var p = L[a].R; var q = L[b].R; if (p.X + 1e-5f < q.X + q.W && q.X + 1e-5f < p.X + p.W && p.Y + 1e-5f < q.Y + q.H && q.Y + 1e-5f < p.Y + p.H) overlap = true; }
        check(!overlap, "keyboard: keys don't overlap");
        check(L.First(i => i.Index == 0).Label == "q", "keyboard: lower-case labels until shift");
        k.Press(KeyboardModel.KeyShift);
        check(k.Shift == ShiftState.Once && k.Layout(0, 0, 0.3f, 0.024f).First(i => i.Index == 0).Label == "Q", "keyboard: shift shows upper case");
        k.Press(KeyboardModel.Letters.IndexOf('a')); k.Press(KeyboardModel.Letters.IndexOf('b'));
        check(k.Text == "Ab" && k.Shift == ShiftState.Off, "keyboard: shift is one-shot");
        k.Press(KeyboardModel.KeyShift); k.Press(KeyboardModel.KeyShift);
        k.Press(KeyboardModel.Letters.IndexOf('c')); k.Press(KeyboardModel.Letters.IndexOf('d'));
        check(k.Text == "AbCD" && k.Shift == ShiftState.Locked && k.Label(KeyboardModel.KeyShift) == "SHIFT", "keyboard: shift twice = caps lock");
        k.Press(KeyboardModel.KeyShift);
        check(k.Shift == ShiftState.Off, "keyboard: third shift turns it off");
        k.Press(KeyboardModel.KeyLayer);
        var S = k.Layout(0, 0, 0.3f, 0.024f);
        check(k.SymbolsLayer && S.Count == 10 + 10 + 12 + 4 && S.Any(i => i.Label == "{") && S.Any(i => i.Label == "\"") && k.Label(KeyboardModel.KeyLayer) == "ABC",
              $"keyboard: ?123 layer ({S.Count} keys), ABC back");
        k.SetText("");
        Type(k, "ws://192.168.1.50:8765/tabletop");
        check(k.Text == "ws://192.168.1.50:8765/tabletop", "keyboard: types a server URL");
        k.SetText("");
        Type(k, "/give @p minecraft:stone[axis=x]{} 64 ~ ^-1 \"a\",'b'!?#+*_");
        check(k.Text == "/give @p minecraft:stone[axis=x]{} 64 ~ ^-1 \"a\",'b'!?#+*_", "keyboard: types a command with every symbol");
        check(k.Press(KeyboardModel.KeyEnter) == KeyResult.Enter && k.Text.Length > 0, "keyboard: enter reports Enter and keeps the text");
        check(k.Press(KeyboardModel.KeyDelete) == KeyResult.Text && k.Text.EndsWith("*"), "keyboard: del removes the last character");
        check(k.Press(KeyboardModel.KeyClear) == KeyResult.Text && k.Text == "" && k.Press(KeyboardModel.KeyClear) == KeyResult.None && k.Press(KeyboardModel.KeyDelete) == KeyResult.None,
              "keyboard: clear empties; again does nothing");
        var s = new KeyboardModel { MaxLength = 5, CollapseSpaces = true };
        s.Press(KeyboardModel.KeySpace);
        Type(s, "a  b");
        check(s.Text == "a b", "keyboard: search collapses spaces (no leading or double space)");
        Type(s, "cdef");
        check(s.Text == "a bcd", "keyboard: max length");
        var d = new KeyboardModel { MaxLength = 200 };
        d.SetText("ws://192.168.100.200:8765/tabletop");
        check(d.Display(64) == "ws://192.168.100.200:8765/tabletop_" && d.Display(12) == "…5/tabletop_" && d.Display(12).Length == 12,
              "keyboard: field shows the tail with the cursor at the end");
    }

    // ---------- the block menu's Info tab ----------

    static MenuHit HitOf(BlockMenuModel m, MenuPart part, int index = 0)
    {
        var L = m.Layout();
        var it = L.Items.First(i => i.Part == part && i.Index == index);
        return BlockMenuModel.HitTest(L, it.R.CX, it.R.CY);
    }

    static bool NoOverlap(MenuLayout L)
    {
        var c = L.Items;
        for (int a = 0; a < c.Count; a++)
            for (int b = a + 1; b < c.Count; b++)
            {
                var p = c[a].R; var q = c[b].R;
                if (p.X + 1e-5f < q.X + q.W && q.X + 1e-5f < p.X + p.W && p.Y + 1e-5f < q.Y + q.H && q.Y + 1e-5f < p.Y + p.H) return false;
            }
        return c.All(i => i.R.X >= 0 && i.R.Y >= 0 && i.R.X + i.R.W <= L.Width + 1e-5f && i.R.Y + i.R.H <= L.Height + 1e-5f);
    }

    static void MenuInfo(Action<bool, string> check, Func<string, string> fixture)
    {
        var r = BlockRegistry.From(Parse(File.ReadAllText(fixture("block_registry.json"))));
        var m = new BlockMenuModel { ServerUrl = "ws://192.168.1.50:8765/tabletop" };
        m.SetRegistry(r);
        m.SetOpen(true);
        m.SetTab(m.InfoTab);
        var L = m.Layout();
        check(L.Items.Any(i => i.Part == MenuPart.HudToggle) && L.Items.Any(i => i.Part == MenuPart.ResetView && i.Label == "Reset view")
              && L.Items.Any(i => i.Part == MenuPart.ServerField && i.Label.Contains("192.168.1.50")) && L.Info.H > 0.08f && NoOverlap(L),
              "info: Show HUD, Reset view and the Server button below the status");
        m.Point(HitOf(m, MenuPart.ResetView), true);
        check(m.Click().Kind == MenuActionKind.ResetView, "info: Reset view button");
        m.Point(HitOf(m, MenuPart.ServerField), true);
        var a = m.Click();
        L = m.Layout();
        check(a.Kind == MenuActionKind.ServerEdit && m.ServerEditing && m.ServerKeys.Text == m.ServerUrl && m.ActiveKeys == m.ServerKeys
              && L.Items.Count(i => i.Part == MenuPart.Key) == 32 && L.Items.Any(i => i.Part == MenuPart.ServerFind) && L.Items.Any(i => i.Part == MenuPart.ServerConnect)
              && L.Items.Any(i => i.Part == MenuPart.ServerCancel) && L.ServerText.W > 0 && NoOverlap(L) && L.Height < 0.40f,
              $"info: the Server field opens with the keyboard, Find / Connect / Cancel ({L.Height:F3} m tall)");
        m.Point(HitOf(m, MenuPart.Key, KeyboardModel.KeyClear), true); m.Click();
        foreach (char ch in "192.168.1.77:8800")
        {
            int key = KeyboardModel.Symbols.IndexOf(ch);
            if (!m.ServerKeys.SymbolsLayer) { m.Point(HitOf(m, MenuPart.Key, KeyboardModel.KeyLayer), true); m.Click(); }
            m.Point(HitOf(m, MenuPart.Key, KeyboardModel.KeySymbol0 + key), true); m.Click();
        }
        check(m.ServerKeys.Text == "192.168.1.77:8800" && m.Query == "", "info: typing goes to the Server field, not the search");
        m.Point(HitOf(m, MenuPart.Key, KeyboardModel.KeyEnter), true);
        a = m.Click();
        check(a.Kind == MenuActionKind.ServerConnect && a.State == "ws://192.168.1.77:8800/tabletop" && !m.ServerEditing && m.ServerUrl == a.State,
              "info: enter connects to the normalised address and closes the field");
        m.Point(HitOf(m, MenuPart.ServerField), true); m.Click();
        m.SetFound(new[] { ("Survival (192.168.1.50)", "ws://192.168.1.50:8765/tabletop"), ("Creative (192.168.1.60)", "ws://192.168.1.60:8765/tabletop") }, "2 found");
        L = m.Layout();
        check(L.Items.Count(i => i.Part == MenuPart.FoundServer) == 2 && NoOverlap(L), "info: found servers are buttons");
        m.Point(HitOf(m, MenuPart.FoundServer, 1), true);
        check(m.NameLine() == "ws://192.168.1.60:8765/tabletop", "info: hovering a found server shows its URL");
        a = m.Click();
        check(a.Kind == MenuActionKind.ServerConnect && a.State == "ws://192.168.1.60:8765/tabletop" && !m.ServerEditing, "info: a found server connects in one click");
        m.Point(HitOf(m, MenuPart.ServerField), true); m.Click();
        m.ServerKeys.SetText("not an address");
        m.Point(HitOf(m, MenuPart.ServerConnect), true);
        check(m.Click().Kind == MenuActionKind.None && m.ServerEditing && m.NameLine().StartsWith("not a server address"), "info: a bad address is refused and the field stays open");
        m.Point(HitOf(m, MenuPart.ServerCancel), true);
        check(m.Click().Kind == MenuActionKind.ServerCancel && !m.ServerEditing && m.ServerUrl == "ws://192.168.1.60:8765/tabletop", "info: cancel keeps the old server");
        m.Point(HitOf(m, MenuPart.ServerField), true); m.Click();
        m.SetTab(0);
        check(!m.ServerEditing && m.ActiveKeys == null, "info: leaving the tab closes the Server field");
        m.SetTab(m.SearchTab);
        foreach (char ch in "oak") { m.Point(HitOf(m, MenuPart.Key, KeyboardModel.Letters.IndexOf(ch)), true); m.Click(); }
        m.Point(HitOf(m, MenuPart.Key, KeyboardModel.KeyEnter), true);
        a = m.Click();
        check(a.Kind == MenuActionKind.Select && a.State == m.Items[0].State && m.Selected == a.State && m.Hotbar[0] == a.State, "search: enter picks the first result");

        // Tab and hotbar restore (prefs.json).
        var fresh = new BlockMenuModel();
        fresh.RestoreTab("natural");
        fresh.Restore(new[] { "minecraft:oak_log[axis=x]", "", "minecraft:stone", "minecraft:oak_log[axis=y]", null, "minecraft:glass" }, 2, "minecraft:stone");
        check(fresh.Hotbar[0] == "minecraft:oak_log[axis=x]" && fresh.Hotbar[1] == "minecraft:stone" && fresh.Hotbar[2] == "minecraft:glass" && fresh.Hotbar.Count == 3
              && fresh.Hotbar.Selected == 1 && fresh.Selected == "minecraft:stone", "prefs: hotbar restored (empties skipped, one slot per block), selection kept");
        check(fresh.Tab == fresh.SearchTab, "prefs: a category tab waits for the block list");
        fresh.SetRegistry(r);
        check(fresh.TabId(fresh.Tab) == "natural", "prefs: the saved category tab opens once the block list arrives");
        fresh.RestoreTab("info");
        check(fresh.Tab == fresh.InfoTab && fresh.TabId(fresh.Tab) == "info", "prefs: Info tab restored");
        var none = new BlockMenuModel();
        none.Restore(null, 0, null);
        check(none.Selected == BlockEditRules.DefaultBlock && none.Hotbar.Count == 1, "prefs: nothing saved keeps stone");
    }

    // ---------- prefs.json ----------

    static void Prefs(Action<bool, string> check)
    {
        var p = new AppPrefs
        {
            BlockSizeM = 0.0042f, AreaWidth = 192, ShowHud = true, MenuTab = "natural", HotbarSelected = 1, Selected = "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]",
            Hotbar = new[] { "minecraft:stone", "minecraft:oak_stairs[facing=east,half=bottom,shape=straight,waterlogged=false]", null, null, null, null, null, null, null },
            TableAnchor = "8f2d1c3e-0000-4000-8000-000000000001", Table = new TablePose { X = 0.1f, Y = 0.02f, Z = -0.05f, Yaw = 30 },
        };
        p.SeeConfig(new Dictionary<string, object> { ["block_size_m"] = 0.01, ["show_hud"] = false, ["server"] = "ws://x" });
        string json = p.ToJson();
        var q = AppPrefs.Parse(json);
        check(Math.Abs(q.BlockSizeM.Value - 0.0042f) < 1e-6 && q.AreaWidth == 192 && q.ShowHud == true && q.MenuTab == "natural" && q.HotbarSelected == 1
              && q.Hotbar.Length == 9 && q.Hotbar[1] == p.Hotbar[1] && q.Hotbar[2] == null && q.Selected == p.Selected && q.TableAnchor == p.TableAnchor
              && Math.Abs(q.Table.Value.Yaw - 30) < 1e-3 && Math.Abs(q.Table.Value.Z + 0.05f) < 1e-5, "prefs: round trip");
        check(q.Seen.Count == 2 && (double)q.Seen["block_size_m"] == 0.01 && !q.Seen.ContainsKey("server") && json.Contains("\n  \"block_size_m\": "),
              "prefs: remembers the tabletop.json values it saw; one key per line");
        check(AppPrefs.Parse("").BlockSizeM == null && AppPrefs.Parse("{\"v\":1}").Table == null, "prefs: empty file / nothing saved");
        bool threw = false;
        try { AppPrefs.Parse("[1,2]"); } catch (FormatException) { threw = true; }
        check(threw, "prefs: a broken file is reported (the app starts fresh)");
        // tabletop.json vs prefs.json for the shared keys.
        check(Equals(AppPrefs.Pick(0.01, 0.004, null), 0.01), "prefs: a new tabletop.json value wins (never seen before)");
        check(Equals(AppPrefs.Pick(0.01, 0.004, 0.01), 0.004), "prefs: an unchanged tabletop.json value doesn't override the headset's choice");
        check(Equals(AppPrefs.Pick(0.02, 0.004, 0.01), 0.02), "prefs: an edited tabletop.json value wins at the next start");
        check(Equals(AppPrefs.Pick(null, 0.004, 0.01), 0.004) && AppPrefs.Pick(null, null, null) == null && Equals(AppPrefs.Pick(true, false, true), false),
              "prefs: no tabletop.json value -> prefs; bools too");
        // The table relative to the MRUK table anchor.
        var tableC = new Vector3(1.2f, 0.74f, -0.4f);
        var anchor = new Vector3(1.35f, 0.76f, -0.6f);
        var rel = TablePose.Relative(tableC, 40f, anchor, 100f);
        rel.Compose(tableC, 40f, out var back, out float yaw);
        check(Near(back, anchor) && Math.Abs(yaw - 100f) < 1e-3, "prefs: table pose relative to the table anchor round-trips");
        rel.Compose(new Vector3(0, 0.7f, 0), -50f, out var moved, out float yaw2);
        var d0 = anchor - tableC;
        var d1 = moved - new Vector3(0, 0.7f, 0);
        check(Math.Abs(Len(d0) - Len(d1)) < 1e-4 && Math.Abs(TablePose.Wrap(yaw2 - (-50f) - (100f - 40f))) < 1e-3,
              "prefs: a re-detected table (moved and turned) carries the miniature with it");
        var r90 = TablePose.RotY(new Vector3(0, 0, 1), 90);
        check(Near(r90, new Vector3(1, 0, 0)) && Math.Abs(TablePose.Wrap(370) - 10) < 1e-4 && Math.Abs(TablePose.Wrap(-190) - 170) < 1e-4, "prefs: yaw like Quaternion.Euler(0, yaw, 0)");
        // Debounced saving.
        var s = new PrefsSaver();
        s.Saved("A");
        check(s.Offer("A", 0) == null, "saver: nothing changed, nothing written");
        check(s.Offer("B", 1) == null && s.Offer("B", 2) == null && s.Offer("B", 2.6) == "B", "saver: written once stable for 1.5 s");
        s.Saved("B");
        check(s.Offer("B", 3) == null, "saver: not written twice");
        bool any = false;
        for (int i = 0; i < 9; i++) any |= s.Offer("Z" + i, 10 + i) != null;
        check(!any && s.Offer("Z9", 20.1) == "Z9", "saver: a long continuous change is written after 10 s");
        check(s.Flush("B2") == "B2" && s.Flush(null) == null, "saver: flush on pause");
        // tabletop.json written by the Server field.
        string cfg = ConfigFile.WithServer("{\"server\":\"ws://old:8765/tabletop\",\"token\":\"t\",\"block_size_m\":0.01}", "ws://192.168.1.77:8765/tabletop");
        var c = Parse(cfg);
        check(c.S("server") == "ws://192.168.1.77:8765/tabletop" && c.S("token") == "t" && c.D("block_size_m") == 0.01 && cfg.StartsWith("{\n  \"server\""),
              "config: the Server field sets server and keeps every other key");
        check(Parse(ConfigFile.WithServer(null, "ws://a:1/x")).S("server") == "ws://a:1/x", "config: a new tabletop.json");
        threw = false;
        try { ConfigFile.WithServer("{broken", "ws://a:1/x"); } catch (FormatException) { threw = true; }
        check(threw, "config: a broken tabletop.json isn't silently replaced");
    }

    static bool Near(Vector3 a, Vector3 b) => Math.Abs(a.x - b.x) < 1e-4 && Math.Abs(a.y - b.y) < 1e-4 && Math.Abs(a.z - b.z) < 1e-4;
    static float Len(Vector3 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

    // ---------- addresses and discovery ----------

    static void Addresses(Action<bool, string> check)
    {
        check(ServerAddress.Normalize("192.168.1.50") == "ws://192.168.1.50:8765/tabletop", "address: a bare IP gets ws://, :8765 and /tabletop");
        check(ServerAddress.Normalize(" 192.168.1.50:8800 ") == "ws://192.168.1.50:8800/tabletop", "address: a typed port is kept");
        check(ServerAddress.Normalize("192.168.1.50:80") == "ws://192.168.1.50:80/tabletop", "address: even port 80");
        check(ServerAddress.Normalize("ws://192.168.1.50:8765/tabletop") == "ws://192.168.1.50:8765/tabletop" && ServerAddress.Normalize("WS://Host:9/x") == "ws://host:9/x",
              "address: a full URL is kept (scheme and host lower-cased)");
        check(ServerAddress.Normalize("mc-pc") == "ws://mc-pc:8765/tabletop" && ServerAddress.Normalize("mc-pc/") == "ws://mc-pc:8765/tabletop", "address: a host name");
        check(ServerAddress.Normalize("") == null && ServerAddress.Normalize("a b") == null && ServerAddress.Normalize("http://x") == null
              && ServerAddress.Normalize("ws://") == null && ServerAddress.Normalize("1.2.3.4:99999") == null, "address: junk refused");
        check(ServerAddress.HostOf("ws://192.168.1.50:8765/tabletop") == "192.168.1.50", "address: host for labels");
    }

    static void Discovery(Action<bool, string> check)
    {
        var probe = Parse(Encoding.UTF8.GetString(LanDiscovery.Probe()));
        check(probe.S("type") == LanDiscovery.ProbeType && probe.I("v") == 1, "discovery: probe message");
        var fx = File.ReadAllText(FixturePath("discovery_reply.json"));
        var f = LanDiscovery.ParseReply(fx, "192.168.1.50");
        check(f != null && f.Url == "ws://192.168.1.50:8765/tabletop" && f.Name == "A Minecraft Server" && !f.TokenNeeded && f.Players == 2 && f.Label == "A Minecraft Server (192.168.1.50)",
              "discovery: reply fixture -> URL from the sender's address");
        check(LanDiscovery.ParseReply("{\"type\":\"tabletop_server\",\"v\":1,\"port\":0}", "1.2.3.4") == null && LanDiscovery.ParseReply("nope", "1.2.3.4") == null
              && LanDiscovery.ParseReply("{\"type\":\"other\",\"v\":1,\"port\":8765}", "1.2.3.4") == null, "discovery: other packets ignored");
        var t = LanDiscovery.Targets(IPAddress.Parse("192.168.7.23"));
        check(t.Count == 2 && t[0].Equals(IPAddress.Broadcast) && t[1].ToString() == "192.168.7.255", "discovery: limited + /24 broadcast");
        check(LanDiscovery.Subnet24(IPAddress.Parse("10.0.5.9")).Count == 254 && LanDiscovery.IsPrivate(IPAddress.Parse("172.20.1.1")) && !LanDiscovery.IsPrivate(IPAddress.Parse("8.8.8.8")),
              "discovery: /24 host list, private ranges");

        // A responder on loopback answers like the plugin (and a second identical reply is listed once).
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
        var responder = Task.Run(() =>
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var got = udp.Receive(ref from);
            var m = Parse(Encoding.UTF8.GetString(got));
            if (m.S("type") != LanDiscovery.ProbeType) return;
            var reply = Encoding.UTF8.GetBytes("{\"type\":\"tabletop_server\",\"v\":1,\"name\":\"Test\",\"port\":8802,\"path\":\"/tabletop\",\"token\":true,\"mc\":\"26.3\",\"version\":\"0.3.1\",\"players\":0}");
            udp.Send(reply, reply.Length, from); udp.Send(reply, reply.Length, from);
        });
        var found = LanDiscovery.ProbeUdp(new[] { IPAddress.Loopback }, port, 800);
        responder.Wait(1000);
        check(found.Count == 1 && found[0].Url == "ws://127.0.0.1:8802/tabletop" && found[0].TokenNeeded && found[0].Label.EndsWith("token"), "discovery: UDP probe and reply (loopback)");
        // TCP fallback: the /24 of 127.0.0.x has exactly one host listening on the port.
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int tport = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var open = LanDiscovery.ScanTcp(LanDiscovery.Subnet24(IPAddress.Loopback), tport, 300).Result;
        tcp.Stop();
        check(open.Count == 1 && open[0].Host == "127.0.0.1" && open[0].PortOnly && open[0].Url == $"ws://127.0.0.1:{tport}/tabletop" && sw.ElapsedMilliseconds < 5000,
              $"discovery: TCP fallback finds the open port ({sw.ElapsedMilliseconds} ms for 254 hosts)");
    }

    static string FixturePath(string n)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        { var p = Path.Combine(d.FullName, "tools", "fixtures", n); if (File.Exists(p)) return p; }
        throw new FileNotFoundException(n);
    }

    static void LiveDiscover(Action<bool, string> check, int port)
    {
        var targets = new List<IPAddress> { IPAddress.Loopback };
        targets.AddRange(LanDiscovery.Targets(LanDiscovery.LocalIPv4()));
        var found = LanDiscovery.ProbeUdp(targets, port, 1500);
        foreach (var f in found) Console.WriteLine($"  found {f.Label} -> {f.Url} mc {f.Mc} plugin {f.Version} players {f.Players}");
        check(found.Count > 0, $"live discovery on port {port}: {found.Count} server(s)");
    }

    // ---------- the inbound queue and the link ----------

    static void Queue(Action<bool, string> check)
    {
        var q = new InboundQueue();
        q.Enqueue(Parse("{\"type\":\"player_positions\",\"players\":[]}"));
        q.Enqueue(Parse("{\"type\":\"chunk_snapshot\",\"snapshot_id\":1,\"encoding\":\"sections_v1\",\"part\":0,\"parts\":2}"));
        q.Enqueue(Parse("{\"type\":\"chunk_snapshot\",\"snapshot_id\":1,\"encoding\":\"sections_v1\",\"part\":1,\"parts\":2}"));
        q.Enqueue(Parse("{\"type\":\"block_update\",\"snapshot_id\":1,\"changes\":[]}"));
        q.Enqueue(Parse("{\"type\":\"chunk_snapshot\",\"snapshot_id\":2,\"encoding\":\"sections_v1\",\"part\":0,\"parts\":1,\"base\":{\"snapshot_id\":1,\"shift\":[16,0,0]}}"));
        q.Enqueue(Parse("{\"type\":\"entities\",\"full\":false,\"entities\":[]}"));
        check(q.Count == 6 && q.Dropped == 0, "inbound: a delta doesn't drop anything");
        q.Enqueue(Parse("{\"type\":\"chunk_snapshot\",\"snapshot_id\":3,\"encoding\":\"rle_palette_v1\"}"));
        var types = new List<string>();
        while (q.TryDequeue(out var m)) types.Add(m.S("type") + (m.ContainsKey("snapshot_id") ? m.I("snapshot_id").ToString() : ""));
        check(string.Join(",", types) == "player_positions,entities,chunk_snapshot3" && q.Dropped == 4, $"inbound: a full snapshot drops the queued snapshots and updates ({string.Join(",", types)})");
        var b = new InboundQueue { Max = 3 };
        for (int i = 0; i < 3; i++) b.Enqueue(Parse("{\"type\":\"actions\"}"));
        check(b.Full && b.Count == 3, "inbound: bounded (the receive loop waits while full)");
    }

    /// <summary>A tiny WebSocket server on loopback (RFC 6455 handshake + WebSocket.CreateFromStream).</summary>
    internal sealed class FakeServer : IDisposable
    {
        readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
        readonly Func<WebSocket, int, Task> _session;
        public int Connections;
        public string Url => $"ws://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/tabletop";

        public FakeServer(Func<WebSocket, int, Task> session) { _session = session; _l.Start(); _ = Accept(); }

        async Task Accept()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                int n = Interlocked.Increment(ref Connections);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var s = c.GetStream();
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n") && await s.ReadAsync(one, 0, 1) == 1) head.Append((char)one[0]);
                        string key = head.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Substring(18).Trim();
                        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        var resp = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
                        await s.WriteAsync(resp, 0, resp.Length);
                        using var ws = WebSocket.CreateFromStream(s, true, null, TimeSpan.Zero);
                        await _session(ws, n);
                    }
                    catch (Exception) { }
                    finally { c.Dispose(); }
                });
            }
        }

        public static Task Send(WebSocket ws, string json) => ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);

        public void Dispose() => _l.Stop();
    }

    const string Welcome = "{\"v\":1,\"type\":\"welcome\",\"seq\":0,\"t\":0,\"session\":\"s\"}";

    internal static bool Until(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    static void Link(Action<bool, string> check)
    {
        // A server that answers pings: RTT measured inside the client, pongs not queued.
        var seen = new List<string>();
        using (var srv = new FakeServer(async (ws, n) =>
        {
            for (var m = await srvNext(ws); m != null; m = await srvNext(ws))
            {
                lock (seen) seen.Add(m.S("type"));
                if (m.S("type") == "hello") await FakeServer.Send(ws, Welcome);
                else if (m.S("type") == "ping") await FakeServer.Send(ws, $"{{\"v\":1,\"type\":\"pong\",\"seq\":1,\"t\":0,\"id\":{m.I("id")},\"echo_t\":{(long)m.D("t")}}}");
                else if (m.S("type") == "bye") { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); return; }
            }
        }))
        {
            var c = new LinkClient(srv.Url, "") { PingSeconds = 0.2f, DeadSeconds = 2f };
            c.Start();
            bool up = Until(() => c.Connected && c.RttMs >= 0, 3000);
            int queued = 0; while (c.Inbound.TryDequeue(out var m)) if (m.S("type") == "pong") queued = -100; else queued++;
            check(up && queued == 1, $"link: welcome queued, pongs measured inside the client (rtt {c.RttMs} ms)");
            c.Shutdown("headset off", 1000, Protocol.AvatarConfig(false, 0.01f, "", true, "Tabletop Giant"));
            List<string> got; lock (seen) got = seen.ToList();
            int ai = got.IndexOf("avatar_config"), bi = got.IndexOf("bye");
            check(ai >= 0 && bi > ai && !c.Connected && c.RttMs == -1, $"link: shutdown sends avatar_config then bye, clears RTT ({string.Join(",", got.Distinct())})");
            Thread.Sleep(700);
            check(srv.Connections == 1, "link: a shut-down client doesn't reconnect");
        }

        // A server that goes silent after welcome (no pongs): the client aborts after DeadSeconds and reconnects.
        using (var srv = new FakeServer(async (ws, n) =>
        {
            for (var m = await srvNext(ws); m != null; m = await srvNext(ws))
                if (m.S("type") == "hello") await FakeServer.Send(ws, Welcome);
        }))
        {
            var c = new LinkClient(srv.Url, "") { PingSeconds = 0.2f, DeadSeconds = 1f };
            c.Start();
            bool again = Until(() => c.Welcomes >= 2, 5000);
            check(again && c.DeadAborts >= 1 && srv.Connections >= 2, $"link: silent server detected after {c.DeadSeconds} s and reconnected ({c.DeadAborts} aborts, {srv.Connections} connections)");
            c.Shutdown("test", 100);
        }

        // Close 4003 after hello (wrong token): stop retrying.
        using (var srv = new FakeServer(async (ws, n) =>
        {
            var m = await srvNext(ws);
            await FakeServer.Send(ws, "{\"v\":1,\"type\":\"error\",\"seq\":0,\"t\":0,\"code\":\"auth_failed\",\"message\":\"bad or missing token\",\"fatal\":true}");
            await ws.CloseOutputAsync((WebSocketCloseStatus)4003, "auth_failed", CancellationToken.None);
            await Task.Delay(200);
        }))
        {
            var c = new LinkClient(srv.Url, "wrong");
            c.Start();
            bool fatal = Until(() => c.FatalCode != 0, 3000);
            Thread.Sleep(1500);
            check(fatal && c.FatalCode == 4003 && c.FatalReason.Contains("token") && srv.Connections == 1, $"link: close 4003 stops retrying ({c.Status})");
            c.Shutdown("test", 50);
        }
        var bad = new LinkClient("192.168.1.50:8765", "");
        bad.Start();
        check(bad.FatalCode == 1 && !bad.Connected, "link: a URL without ws:// doesn't start");

        // Connecting to a port nobody listens on: an error status and retries, never Connected.
        var free = new TcpListener(IPAddress.Loopback, 0); free.Start(); int fp = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
        var none = new LinkClient($"ws://127.0.0.1:{fp}/tabletop", "") { ConnectTimeoutSeconds = 1 };
        none.Start();
        check(Until(() => none.Status.StartsWith("error"), 3000) && !none.Connected && none.RttMs == -1, $"link: nobody listening -> {none.Status}");
        none.Shutdown("test", 50);
    }

    internal static async Task<Dictionary<string, object>> srvNext(WebSocket ws)
    {
        var buf = new byte[65536];
        try
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            var m = Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
            return m;
        }
        catch (Exception) { return null; }
    }

    // ---------- session helpers, toasts, orientation ----------

    static void Session(Action<bool, string> check)
    {
        var h = new LinkHealth();
        h.NewServer(0);
        h.Update(false, 1);
        check(!h.ShowCard(false, 4.9) && h.ShowCard(false, 5.1) && !h.Offline, "health: setup card after 5 s without a first connection");
        check(h.ShowCard(true, 0.1), "health: a fatal close shows the card at once");
        h.Update(true, 6);
        check(!h.ShowCard(false, 7) && !h.Offline && !h.HideFigures(7), "health: connected -> no card, no badge");
        h.Update(false, 10);
        check(h.Offline && !h.ShowCard(false, 30) && !h.HideFigures(11) && h.HideFigures(11.6), "health: a dropped link shows the badge (not the card) and hides figures after 1.5 s");
        h.Update(true, 12);
        check(!h.Offline && !h.HideFigures(13), "health: back online");
        h.NewServer(20);
        check(!h.Offline && h.ShowCard(false, 25.5), "health: a new server address can show the card again");

        string card = SetupCard.Text("ws://192.168.1.50:8765/tabletop", "error: Connection refused", 0, "", "/sdcard/Android/data/com.tabletopcraft.ar/files/tabletop.json");
        check(card.StartsWith(SetupCard.Title) && card.Contains("ws://192.168.1.50:8765/tabletop") && card.Contains("Connection refused") && card.Contains("tabletop.json")
              && card.Contains("Info > Server") && card.Contains("QUEST_SETUP.md Part D") && card.Contains("Retrying"), "setup card: URL, status, settings file, what to do");
        string tok = SetupCard.Text("ws://x:1/t", "closed 4003", 4003, "wrong token: ...", "/p/tabletop.json");
        check(tok.StartsWith("The server refused this headset") && tok.Contains("wrong token") && tok.Contains("Not retrying"), "setup card: 4003 says token, no retry");

        var lp = new LoadProgress();
        lp.Begin(10);
        check(!lp.Active && lp.Text(5) == null, "loading: small loads (a glide slice) show nothing");
        lp.Begin(200);
        check(lp.Text(200) == "Loading area 0 %" && lp.Text(120) == "Loading area 40 %" && lp.Text(0) == "Area loaded" && !lp.Active && lp.Text(0) == null, "loading: percent, then Area loaded once");

        var tq = new ToastQueue();
        tq.Push("Loading area 0 %", ToastKind.Info, 0, 3, "load");
        tq.Push("Link lost", ToastKind.Warn, 0.1);
        tq.Push("Loading area 50 %", ToastKind.Info, 0.5, 3, "load");
        var v = tq.Visible(0.6);
        check(v.Count == 2 && v.Count(t => t.Text.StartsWith("Loading")) == 1 && v.Last().Text == "Loading area 50 %", "toast: a keyed toast updates in place");
        tq.Push("No pack.zip", ToastKind.Warn, 1, 8);
        check(tq.Visible(8.5).Any(t => t.Text == "No pack.zip") && tq.Visible(9.1).All(t => t.Text != "No pack.zip"), "toast: custom life");

        var a = new HoldButton();
        check(a.Update(true, 0, false) == HoldButton.Event.Pressed && a.Update(true, 0.3f, false) == HoldButton.Event.None && Math.Abs(a.Progress(0.25f) - 0.5f) < 1e-4
              && a.Update(true, 0.51f, false) == HoldButton.Event.Fired && a.Update(true, 0.9f, false) == HoldButton.Event.None
              && a.Update(false, 1f, false) == HoldButton.Event.Released, "hold A: fires once after 0.5 s");
        check(a.Update(true, 2, false) == HoldButton.Event.Pressed && a.Update(false, 2.2f, false) == HoldButton.Event.Released, "hold A: a tap doesn't fire");
        a.Update(true, 3, false);
        bool fired = a.Update(true, 3.2f, true) == HoldButton.Event.Fired;
        fired |= a.Update(true, 3.8f, false) == HoldButton.Event.Fired;
        check(!fired && a.Update(false, 4, false) == HoldButton.Event.None, "hold A: blocked during the press (ray on the menu) -> never fires");
        check(a.Update(true, 5, true) == HoldButton.Event.None && a.Update(true, 6, false) == HoldButton.Event.None, "hold A: a press that starts on the menu never fires");
    }

    static void Orientation(Action<bool, string> check)
    {
        check(NavState.Facing(0, -1) == "N" && NavState.Facing(1, 0) == "E" && NavState.Facing(0, 1) == "S" && NavState.Facing(-1, 0) == "W"
              && NavState.Facing(1, -1) == "NE" && NavState.Facing(-0.3f, 1) == "S" && NavState.Facing(0, 0) == "-", "compass: north = -Z, east = +X");
        var n = new NavState();
        n.OnRegion("world", new Vector3Int(0, 0, 0), new Vector3Int(96, 64, 96), new Vector3Int(16, 16, 16), 0);
        n.Move(new Vector3(10, 0, -7), 0.1f, 0);
        check(Math.Abs(n.Offset.x - 10) < 1e-3 && n.Readout(0, -1) == "58 16 41  facing N", $"readout: block under the view centre and facing ({n.Readout(0, -1)})");
        n.Recenter();
        check(Math.Abs(n.Offset.x) < 1e-5 && Math.Abs(n.Offset.z) < 1e-5 && n.Readout(1, 0).EndsWith("facing E"), "reset view: the window back in the middle");
        var e = NavState.EdgePoint(0.4f, 0.3f, 0, -2);
        var e2 = NavState.EdgePoint(0.4f, 0.3f, 1, 1);
        check(Math.Abs(e.x) < 1e-5 && Math.Abs(e.z + 0.3f) < 1e-5 && Math.Abs(e2.x - 0.3f) < 1e-5 && Math.Abs(e2.z - 0.3f) < 1e-5, "badge: the table edge toward the head");
    }
}
