// [safety] Engine-free tests for mute, block and report on the headset (docs/PROTOCOL.md "Chat safety: mute, block,
// report"): the report fixtures through Net/ControlProtocol.cs, World/ChatSafety.cs (what is hidden, per server, the
// prefs.json round trip), ChatLog/ChatBubbles removal, and the operator panel (a chat line picks its player, Mute /
// Block / Report on the Chat and Players tabs, the muted/blocked list, the report picker, acks and errors).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;

static class SafetyTests
{
    const string Alex = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", Steve = "11111111-2222-3333-4444-555555555555";

    static Dictionary<string, object> Fx(Func<string, string> fixture, string name) => Json.Parse(File.ReadAllText(fixture(name + ".json"))) as Dictionary<string, object>;

    static ChatMessage Msg(string kind, string uuid, string name, string text) =>
        new ChatMessage { Kind = kind, Uuid = uuid, Name = name, Text = text };

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Fixtures(check, fixture);
        Lists(check);
        Panel(check, fixture);
    }

    static void Fixtures(Action<bool, string> check, Func<string, string> fixture)
    {
        check(Fx(fixture, "hello_report").A("caps").Contains(ControlProtocol.CapReport) && Protocol.Hello("").A("caps").Contains(ControlProtocol.CapReport),
              "safety: hello asks for report_v1");
        check(ControlWelcome.From(Fx(fixture, "welcome_report")).Reports && !ControlWelcome.From(Fx(fixture, "welcome_control")).Reports,
              "safety: welcome.caps says whether the server takes reports");
        var fx = Fx(fixture, "report");
        var built = ControlProtocol.Report(Alex, "Alex", "harassment", "  kept insulting me in chat ", true);
        check(built.O("player").S("uuid") == fx.O("player").S("uuid") && built.O("player").S("name") == fx.O("player").S("name")
              && built.S("reason") == fx.S("reason") && built.S("details") == fx.S("details")
              && built.B("blocked") == fx.B("blocked") && built.S("type") == "report", "safety: the report builder matches report.json");
        var bare = ControlProtocol.Report(Alex, null, "spam", "   ", true);
        check(!bare.ContainsKey("details") && !bare.O("player").ContainsKey("name"), "safety: no note and no name leave the fields out");
        check(ControlProtocol.Report(Alex, "Alex", "spam", new string('x', 300), true).S("details").Length == ControlProtocol.MaxReportNote, "safety: the note is cut to 200 characters");
        check(ReportReasons.Codes.Contains(fx.S("reason")) && ReportReasons.Codes.Length == ReportReasons.Labels.Length, "safety: the picker's reasons are the protocol's codes");
        var ack = ReportAck.From(Fx(fixture, "report_ack"));
        check(ack.RefSeq == 70 && ack.Id == "5e1a9c03" && ack.Operators == 1, "safety: report_ack parses");
        var chat = ChatMessage.From(Fx(fixture, "chat"));
        check(chat.Uuid == fx.O("player").S("uuid"), "safety: a player is reported by the uuid their chat came with");
    }

    static void Lists(Action<bool, string> check)
    {
        var s = new ChatSafety();
        s.UseServer("3f9c2a7e1b4d5c6a", "ws://192.168.1.50:8765/tabletop");
        check(!s.Hides(Msg("chat", Alex, "Alex", "hi")) && s.Count == 0, "safety: nobody is hidden at first");
        check(s.Set(Alex, "Alex", SafetyLevel.Muted) && s.IsMuted(Alex) && !s.IsBlocked(Alex), "safety: mute");
        check(s.Hides(Msg("chat", Alex, "Alex", "hi")) && s.Hides(Msg("say", Alex, "Alex", "hi")) && s.Hides(Msg("me", Alex, "Alex", "waves"))
              && !s.Hides(Msg("whisper", Alex, "Alex", "psst")) && !s.Hides(Msg("join", Alex, "Alex", "Alex joined the game"))
              && !s.Hides(Msg("death", Alex, "Alex", "Alex fell")), "safety: mute hides chat, say and me; not whispers, joins or deaths");
        check(!s.Hides(Msg("chat", Steve, "Steve", "hi")) && !s.Hides(Msg("operator", null, "Nick", "hi")) && !s.Hides(null), "safety: others and lines without a uuid stay");
        check(s.Hides(Msg("chat", Alex.ToUpperInvariant(), "Alex", "hi")), "safety: uuids match without regard to case");
        s.Set(Alex, "Alex", SafetyLevel.Blocked);
        check(s.IsBlocked(Alex) && s.IsMuted(Alex) && s.Hides(Msg("whisper", Alex, "Alex", "psst")), "safety: block also hides whispers");
        check(!s.Set(Alex, "Alex", SafetyLevel.Blocked), "safety: setting the same level changes nothing");
        s.Set(Steve, "Steve", SafetyLevel.Muted);
        var l = s.List();
        check(l.Count == 2 && l[0].Name == "Alex" && l[1].Name == "Steve" && ChatSafety.Describe(l[0]) == "Alex  blocked" && ChatSafety.Describe(l[1]) == "Steve  muted",
              "safety: the list, by name");
        s.Reported(Steve, "Steve", "spam", 1760370009000);
        check(s.IsBlocked(Steve) && s.Find(Steve).Reported == "spam" && ChatSafety.Describe(s.Find(Steve)) == "Steve  blocked, reported (spam)", "safety: a report blocks and is remembered");
        check(s.Set(Alex, null, SafetyLevel.None) && s.Count == 1 && !s.IsMuted(Alex), "safety: unblock forgets them");

        // Per server: another server's lists are its own; a server first known by its address keeps them when it sends an id.
        int v = s.Version;
        s.UseServer("aaaaaaaaaaaaaaaa", "ws://192.168.1.60:8765/tabletop");
        check(s.Version != v && s.Count == 0 && !s.IsMuted(Steve), "safety: lists are per server");
        s.UseServer(null, "ws://192.168.1.70:8765/tabletop");
        s.Set(Alex, "Alex", SafetyLevel.Muted);
        check(s.ServerKey == "url:ws://192.168.1.70:8765/tabletop", "safety: a server without an id is keyed by its address");
        s.UseServer("bbbbbbbbbbbbbbbb", "WS://192.168.1.70:8765/tabletop");
        check(s.ServerKey == "bbbbbbbbbbbbbbbb" && s.IsMuted(Alex), "safety: the address's lists move to the server id");

        // prefs.json round trip (through AppPrefs, as the app saves it).
        var prefs = new AppPrefs { ChatSafety = s.ToJson() };
        var back = AppPrefs.Parse(prefs.ToJson());
        var t = new ChatSafety();
        t.Load(back.ChatSafety);
        t.UseServer("3f9c2a7e1b4d5c6a", null);
        check(t.IsBlocked(Steve) && t.Find(Steve).Reported == "spam" && t.Find(Steve).ReportedAt == 1760370009000 && t.Count == 1, "safety: prefs.json keeps each server's list");
        t.UseServer("bbbbbbbbbbbbbbbb", null);
        check(t.LevelOf(Alex) == SafetyLevel.Muted, "safety: and the other server's");
        check(new ChatSafety().ToJson() == null && AppPrefs.Parse(new AppPrefs().ToJson()).ChatSafety == null, "safety: nothing saved when nobody is muted");
        var bad = new ChatSafety();
        bad.Load(Json.Parse("{\"x\":[{\"uuid\":\"u1\",\"level\":\"loud\"},{\"name\":\"n\"},3,{\"uuid\":\"u2\",\"level\":\"muted\"},{\"uuid\":\"u2\",\"level\":\"blocked\"}],\"y\":5}") as Dictionary<string, object>);
        bad.UseServer("x", null);
        check(bad.Count == 1 && bad.LevelOf("u2") == SafetyLevel.Muted && bad.Find("u2").Name == "?", "safety: bad saved entries are skipped");

        // ChatLog and bubbles: what a mute takes away at once.
        var log = new ChatLog();
        log.Add(Msg("chat", Alex, "Alex", "hi"), 0); log.Add(Msg("whisper", Alex, "Alex", "psst"), 0); log.Add(Msg("join", Alex, "Alex", "Alex joined the game"), 0);
        log.Add(Msg("chat", Steve, "Steve", "yo"), 0);
        check(log.RemoveFrom(Alex, false) == 1 && log.Count == 3 && log.RemoveFrom(Alex, true) == 1 && log.Count == 2, "safety: mute removes chat lines, block also whispers; joins stay");
        var page = new List<WristLine>();
        log.Page(5, 40, 0, page);
        check(page.Count == 2 && page[1].Entry.Uuid == Steve && page[0].Entry.Kind == "join", "safety: chat rows know the line they come from");
        var bubbles = new ChatBubbles();
        bubbles.Add(Msg("chat", Alex, "Alex", "hi"), 0); bubbles.Add(Msg("chat", Steve, "Steve", "yo"), 0);
        check(bubbles.RemoveWhere(u => u == Alex) && bubbles.For(Alex).Count == 0 && bubbles.For(Steve).Count == 1 && !bubbles.RemoveWhere(u => u == Alex),
              "safety: a muted player's bubbles go");
    }

    static void Panel(Action<bool, string> check, Func<string, string> fixture)
    {
        var p = new OperatorPanelModel { Connected = true };
        p.OnWelcome(ControlWelcome.From(Fx(fixture, "welcome_report")), null);
        p.Safety.UseServer("3f9c2a7e1b4d5c6a", null);
        p.SetOpen(true);
        p.Log.Add(Msg("chat", Alex, "Alex", "you are terrible"), 0);
        p.Log.Add(Msg("join", Steve, "Steve", "Steve joined the game"), 0);
        p.Log.Add(new ChatMessage { Kind = "operator", Name = "Nick", Session = "6f1c", Text = "hello" }, 0, "6f1c");
        PanelAction Click(PanelPart part, int index)
        {
            var L = p.Layout();
            p.Point(new PanelHit(part, index), true);
            return p.Click(L, 0);
        }
        int RowOf(string uuid)   // the Chat tab row showing a line of this player (last one), or -1
        {
            var L = p.Layout();
            var page = new List<WristLine>();
            p.Log.Page(L.TextRows, L.TextChars, p.Scroll, page);
            return page.FindLastIndex(w => w.Entry != null && w.Entry.Uuid == uuid);
        }
        bool Has(PanelPart part, int index, out PanelItem item)
        {
            foreach (var it in p.Layout().Items) if (it.Part == part && it.Index == index) { item = it; return true; }
            item = default; return false;
        }

        // Chat tab: player lines are clickable rows, the headset's own line isn't.
        p.SetTab(OpTab.Chat);
        var L0 = p.Layout();
        var lines = L0.Items.Where(i => i.Part == PanelPart.ChatLine).Select(i => i.Index).ToList();
        check(lines.SequenceEqual(new[] { 0, 1 }), "safety: chat lines with a player can be clicked (not the headset's own)");
        check(!Has(PanelPart.Button, OperatorPanelModel.BtnMute, out _), "safety: no Mute row until a line is picked");
        check(Click(PanelPart.ChatLine, 0).Kind == PanelActionKind.Selected && p.ChatPickUuid == Alex && p.ChatPickName == "Alex", "safety: clicking Alex's line picks Alex");
        check(Has(PanelPart.ChatLine, 0, out var picked) && picked.Active && Has(PanelPart.Button, OperatorPanelModel.BtnMute, out var mb) && mb.Label == "Mute" && mb.Enabled,
              "safety: the picked line is lit and Mute / Block / Report appear");
        check(p.Layout().TextRows == OperatorPanelModel.ChatRows - 2, "safety: the log gives two rows to the buttons");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnMute).Kind == PanelActionKind.Toggled && p.Safety.LevelOf(Alex) == SafetyLevel.Muted
              && p.Log.Entries.All(e => e.Uuid != Alex || e.Kind != "chat") && p.Note.StartsWith("muted Alex"), "safety: Mute hides Alex's lines at once");
        check(Has(PanelPart.Button, OperatorPanelModel.BtnMute, out var um) && um.Label == "Unmute" && um.Active, "safety: the button now says Unmute");
        Click(PanelPart.Button, OperatorPanelModel.BtnBlock);
        check(p.Safety.IsBlocked(Alex) && Has(PanelPart.Button, OperatorPanelModel.BtnMute, out var mb2) && !mb2.Enabled
              && Has(PanelPart.Button, OperatorPanelModel.BtnBlock, out var bb) && bb.Label == "Unblock", "safety: Block; Mute is greyed while blocked");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnMute).Kind == PanelActionKind.Refused && p.Note.Contains("Unblock first"), "safety: and says why");
        Click(PanelPart.Button, OperatorPanelModel.BtnBlock);
        check(!p.Safety.IsMuted(Alex) && p.Note == "unblocked Alex", "safety: Unblock");
        Click(PanelPart.Button, OperatorPanelModel.BtnPickClose);
        check(p.ChatPickUuid == null && p.Layout().TextRows == OperatorPanelModel.ChatRows, "safety: Close drops the picked line");

        // Report from a chat line: the picker, a reason, a note typed on the keyboard, enter sends; the player is blocked.
        check(RowOf(Alex) < 0, "safety: Alex's chat line stayed hidden after the unblock (a mute removes lines for good)");
        Click(PanelPart.ChatLine, RowOf(Steve));   // Steve's join line
        check(p.ChatPickUuid == Steve, "safety: any line with a player picks them (a join too)");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnReport).Kind == PanelActionKind.Toggled && p.Mode == PanelMode.Report && p.Report.Name == "Steve"
              && p.Focus == OperatorPanelModel.Field.Report && p.ModeHeader().StartsWith("Report Steve"), "safety: Report opens the picker");
        check(Has(PanelPart.Button, OperatorPanelModel.BtnReportSend, out var send) && !send.Enabled, "safety: Send report waits for a reason");
        check(p.Enter(0).Kind == PanelActionKind.Refused && p.Note == "pick a reason first", "safety: enter without a reason says so");
        Click(PanelPart.Button, OperatorPanelModel.BtnReason + 4);
        check(p.Report.Reason == "spam" && Has(PanelPart.Button, OperatorPanelModel.BtnReason + 4, out var rb) && rb.Active, "safety: a reason lights up");
        foreach (char c in "spam links") p.Key(c == ' ' ? KeyboardModel.KeySpace : KeyboardModel.Letters.IndexOf(c), 0);
        check(p.FocusText == "spam links" && p.Report.Note == "spam links", "safety: the note is typed on the panel keyboard");
        p.Outbox.Clear();
        var sent = p.Enter(0);
        check(sent.Kind == PanelActionKind.Sent && p.Outbox.Count == 1 && p.Outbox[0].S("type") == "report" && p.Outbox[0].O("player").S("uuid") == Steve
              && p.Outbox[0].S("reason") == "spam" && p.Outbox[0].S("details") == "spam links" && p.Outbox[0].B("blocked"), "safety: enter sends the report");
        check(p.Mode == PanelMode.Tabs && p.Tab == OpTab.Chat && p.Safety.IsBlocked(Steve) && p.Safety.Find(Steve).Reported == "spam" && p.ChatPickUuid == null,
              "safety: back to the chat; Steve is blocked and marked reported");
        string ackNote = p.OnReportAck(ReportAck.From(Fx(fixture, "report_ack")));
        check(ackNote.Contains("reported Steve") && ackNote.Contains("5e1a9c03") && ackNote.Contains("1 operator online"), "safety: the ack says who saw it");
        check(p.OnReportAck(new ReportAck { Id = "00000001", Operators = 0 }).Contains("none online"), "safety: or that it waits for the operators");
        check(p.OnReportError("more than 3 reports a minute").StartsWith("report not sent: more than 3"), "safety: a refused report says why");

        // Cancel leaves without reporting.
        p.Log.Add(Msg("chat", Alex, "Alex", "sorry"), 0);
        check(RowOf(Steve) >= 0, "safety: a blocked player's join line stays");
        Click(PanelPart.ChatLine, RowOf(Alex));
        Click(PanelPart.Button, OperatorPanelModel.BtnReport);
        p.Outbox.Clear();
        Click(PanelPart.Button, OperatorPanelModel.BtnClose);
        check(p.Mode == PanelMode.Tabs && p.Outbox.Count == 0 && !p.Safety.IsMuted(Alex), "safety: Cancel reports nothing and blocks nobody");

        // Players tab: the Chat row acts on the selected online player; the list shows who is muted or blocked.
        p.OnPlayers(PlayersInfo.From(Fx(fixture, "players_info")));
        p.SetTab(OpTab.Players);
        check(Has(PanelPart.Button, OperatorPanelModel.BtnMute, out var pm) && !pm.Enabled && Has(PanelPart.Button, OperatorPanelModel.BtnSafetyList, out var lb) && lb.Label == "Mute list (1)",
              "safety: Players has Mute / Block / Report (greyed until a player is selected) and the list button");
        Click(PanelPart.PlayerRow, 0);
        string selUuid = p.SelectedPlayer, selName = p.Players.Players[0].Name;
        Click(PanelPart.Button, OperatorPanelModel.BtnMute);
        check(p.Safety.LevelOf(selUuid) == SafetyLevel.Muted && p.Note.StartsWith("muted " + selName), "safety: Mute the selected online player");
        Click(PanelPart.Button, OperatorPanelModel.BtnSafetyList);
        var LL = p.Layout();
        var rows = LL.Items.Where(i => i.Part == PanelPart.SafetyRow && i.Enabled).ToList();
        check(p.SafetyList && rows.Count == 2 && rows.Any(r => r.Label.StartsWith(selName + "  muted")) && rows.Any(r => r.Label == "Steve  blocked, reported (spam)")
              && !LL.Items.Any(i => i.Part == PanelPart.PlayerRow) && Has(PanelPart.Button, OperatorPanelModel.BtnSafetyList, out var ob) && ob.Label == "Online",
              "safety: the list shows muted and blocked players with Online to go back");
        int steveRow = rows.First(r => r.Label.StartsWith("Steve")).Index;
        Click(PanelPart.SafetyRow, steveRow);
        check(p.SafetyTarget().uuid == Steve && Has(PanelPart.Button, OperatorPanelModel.BtnBlock, out var ub) && ub.Label == "Unblock", "safety: select a listed player");
        Click(PanelPart.Button, OperatorPanelModel.BtnBlock);
        check(!p.Safety.IsMuted(Steve) && p.SafetySelected == null && p.Layout().Items.Count(i => i.Part == PanelPart.SafetyRow && i.Enabled) == 1, "safety: Unblock takes them off the list");
        Click(PanelPart.SafetyRow, 0);
        Click(PanelPart.Button, OperatorPanelModel.BtnMute);
        check(p.Safety.Count == 0, "safety: Unmute empties the list");
        Click(PanelPart.Button, OperatorPanelModel.BtnSafetyList);
        check(!p.SafetyList && p.Layout().Items.Any(i => i.Part == PanelPart.PlayerRow), "safety: Online goes back to the players");

        // A server without report_v1 (an older plugin): Mute and Block work, Report is greyed and says why.
        var old = new OperatorPanelModel { Connected = true };
        old.OnWelcome(ControlWelcome.From(Fx(fixture, "welcome_control")), null);
        old.SetOpen(true);
        old.Log.Add(Msg("chat", Alex, "Alex", "hi"), 0);
        old.SetTab(OpTab.Chat);
        var Lo = old.Layout(); old.Point(new PanelHit(PanelPart.ChatLine, 0), true); old.Click(Lo, 0);
        Lo = old.Layout();
        var rep = Lo.Items.First(i => i.Part == PanelPart.Button && i.Index == OperatorPanelModel.BtnReport);
        old.Point(new PanelHit(PanelPart.Button, OperatorPanelModel.BtnReport), true);
        check(!rep.Enabled && old.Click(Lo, 0).Kind == PanelActionKind.Refused && old.Note.Contains("older than 1.2.0"), "safety: an older plugin: Report greyed, with the reason");
        Lo = old.Layout(); old.Point(new PanelHit(PanelPart.Button, OperatorPanelModel.BtnBlock), true); old.Click(Lo, 0);
        check(old.Safety.IsBlocked(Alex), "safety: blocking works on any server");
        check(old.OpenReport(Alex, "Alex").Kind == PanelActionKind.Refused && old.Mode == PanelMode.Tabs, "safety: no picker without report_v1");

        // Every mode and view still fits beside the hand.
        bool fits = true;
        p.SetTab(OpTab.Chat); p.Log.Add(Msg("chat", Alex, "Alex", "x"), 0); p.Layout(); p.Point(new PanelHit(PanelPart.ChatLine, 0), true); p.Click(p.Layout(), 0);
        fits &= p.Layout().Height <= 0.40f;
        p.OpenReport(Alex, "Alex"); fits &= p.Layout().Height <= 0.40f && p.Layout().Field.W > 0 && p.Layout().Keyboard.H > 0;
        p.SetTab(OpTab.Players); fits &= p.Layout().Height <= 0.40f;
        check(fits, "safety: the chat row, the report picker and the Players rows fit beside the hand");
    }
}
