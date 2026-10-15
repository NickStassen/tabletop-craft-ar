// [talk] Engine-free tests for chat, commands and server status on the headset (docs/PROTOCOL.md 4.26-4.28): the golden
// fixtures through Net/ControlProtocol.cs, World/ChatLog.cs (formatting, wrist lines, wrapping, bubbles by uuid),
// World/ConsoleModel.cs (history and its JSON, completion debounce and stale replies, chips, output grouping) and
// World/OperatorPanelModel.cs (layout, hit tests, tabs, command templates, greyed buttons without command_v1, the
// pointer, kick with a reason, hold X). --live-control URL TOKEN runs a session against a real server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class ControlTests
{
    static Dictionary<string, object> Fx(Func<string, string> fixture, string name) => Json.Parse(File.ReadAllText(fixture(name + ".json"))) as Dictionary<string, object>;

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Fixtures(check, fixture);
        Chat(check, fixture);
        ConsoleTests(check, fixture);
        Panel(check, fixture);
        Keys(check, fixture);
    }

    /// <summary>Types text on the panel's keyboard the way a user would: letters, shift for capitals, ?123 for digits and symbols.</summary>
    static PanelAction TypeKeys(OperatorPanelModel p, string text, double now)
    {
        var last = default(PanelAction);
        foreach (char ch in text)
        {
            var kb = p.ActiveKeys;
            if (ch == ' ') { last = p.Key(KeyboardModel.KeySpace, now); continue; }
            int letter = KeyboardModel.Letters.IndexOf(char.ToLowerInvariant(ch));
            int symbol = KeyboardModel.Symbols.IndexOf(ch);
            if (letter >= 0)
            {
                if (kb.SymbolsLayer) p.Key(KeyboardModel.KeyLayer, now);
                if (char.IsUpper(ch) && kb.Shift == ShiftState.Off) p.Key(KeyboardModel.KeyShift, now);
                last = p.Key(letter, now);
            }
            else if (symbol >= 0)
            {
                if (!kb.SymbolsLayer) p.Key(KeyboardModel.KeyLayer, now);
                last = p.Key(KeyboardModel.KeySymbol0 + symbol, now);
            }
            else throw new ArgumentException("no key for " + ch);
        }
        return last;
    }

    static void Keys(Action<bool, string> check, Func<string, string> fixture)
    {
        var p = new OperatorPanelModel { Connected = true };
        p.OnWelcome(ControlWelcome.From(Fx(fixture, "welcome_control")), null);
        p.SetOpen(true);
        p.OnPlayers(PlayersInfo.From(Fx(fixture, "players_info")));
        p.SetTab(OpTab.Chat);
        var L = p.Layout();
        var keys = L.Items.Where(i => i.Part == PanelPart.Key).ToList();
        check(keys.Count == 26 + 6 && keys.All(k => k.R.Y >= L.Keyboard.Y - 1e-5f && k.R.Y + k.R.H <= L.Keyboard.Y + L.Keyboard.H + 1e-5f) &&
              keys.First(k => k.Index == KeyboardModel.KeyEnter).Label == "send", "keys: the Chat tab lays out the shared keyboard (enter = send)");
        TypeKeys(p, "Hi giant", 0);
        check(p.ChatInput == "Hi giant", $"keys: shift makes one capital, then lower case ({p.ChatInput})");
        p.Key(KeyboardModel.KeyDelete, 0);
        check(p.ChatInput == "Hi gian", "keys: delete");
        TypeKeys(p, "t!", 0);
        var sent = p.Key(KeyboardModel.KeyEnter, 0);
        check(sent.Kind == PanelActionKind.Chat && sent.Line == "Hi giant!" && p.ChatInput == "", "keys: enter sends the chat line");

        p.SetTab(OpTab.Console);
        check(p.Layout().Items.Any(i => i.Part == PanelPart.Key && i.Index == KeyboardModel.KeyEnter && i.Label == "run"), "keys: the Console's enter says run");
        TypeKeys(p, "tp @a ~ ~1 ~", 1);
        check(p.Console.Input == "tp @a ~ ~1 ~" && p.ConsoleKeys.SymbolsLayer, $"keys: ?123 types @ ~ and digits ({p.Console.Input})");
        check(p.Console.CompletionDue(1.2) == "tp @a ~ ~1 ~", "keys: typing asks for completions after 150 ms");
        p.Key(KeyboardModel.KeyClear, 2);
        TypeKeys(p, "time set day", 2);
        var run = p.Key(KeyboardModel.KeyEnter, 2);
        check(run.Kind == PanelActionKind.Command && run.Line == "time set day" && p.Console.Input == "" && p.Console.History.Last() == "time set day",
              "keys: enter runs the command and remembers it");
        check(p.Console.HistoryUp() && p.FocusText == "time set day", "keys: history fills the field the keyboard types into");
        p.Key(KeyboardModel.KeyDelete, 3);
        check(p.Console.Input == "time set da", "keys: delete edits a recalled line");

        // Clicking keys goes through Click like any other item.
        var L2 = p.Layout();
        p.Point(new PanelHit(PanelPart.Key, KeyboardModel.KeyClear), true);
        p.Click(L2, 4);
        check(p.Console.Input == "", "keys: a clicked key (clear) acts");

        p.SetTab(OpTab.Players);
        check(!p.Layout().Items.Any(i => i.Part == PanelPart.Key), "keys: no keyboard on Players until kick asks for a reason");
        var L3 = p.Layout();
        p.Point(new PanelHit(PanelPart.PlayerRow, 0), true); p.Click(L3, 5);
        L3 = p.Layout();
        p.Point(new PanelHit(PanelPart.Button, OperatorPanelModel.BtnKick), true); p.Click(L3, 5);
        check(p.Layout().Items.Any(i => i.Part == PanelPart.Key && i.Index == KeyboardModel.KeyEnter && i.Label == "kick"), "keys: kick shows the keyboard (enter = kick)");
        TypeKeys(p, "spam", 6);
        var kick = p.Key(KeyboardModel.KeyEnter, 6);
        check(kick.Kind == PanelActionKind.Command && kick.Line == "kick Alex spam", $"keys: enter kicks with the typed reason ({kick.Line})");
        p.SetTab(OpTab.Server);
        check(p.ActiveKeys == null && !p.Layout().Items.Any(i => i.Part == PanelPart.Key), "keys: no keyboard on Server");
        var max = new OperatorPanelModel { Connected = true };
        max.OnWelcome(new ControlWelcome { Chat = true, ChatSend = true, ChatMaxLen = 5, Commands = true, Status = true }, null);
        max.SetOpen(true);
        TypeKeys(max, "abcdefgh", 0);
        check(max.ChatInput == "abcde", "keys: max_len from welcome caps the field");
    }

    static void Fixtures(Action<bool, string> check, Func<string, string> fixture)
    {
        var w = ControlWelcome.From(Fx(fixture, "welcome_control"));
        check(w.Chat && w.ChatSend && w.Commands && w.Status && w.ChatName == "Nick" && w.ChatMaxLen == 256 && w.Quick.Count == 4 && w.Quick[0] == "time set day",
              "control: welcome caps, chat name, quick buttons");
        var none = ControlWelcome.From(Fx(fixture, "welcome"));
        check(!none.Chat && !none.Commands && !none.Status && none.Quick.Count == 0, "control: an older welcome grants nothing");
        var hello = Protocol.Hello("t");
        var caps = (List<object>)hello["caps"];
        check(caps.Contains(ControlProtocol.CapChat) && caps.Contains(ControlProtocol.CapCommand) && caps.Contains(ControlProtocol.CapStatus) && !hello.ContainsKey("operator_name"),
              "control: hello asks for chat_v1, command_v1, status_v1");
        Protocol.OperatorName = "Nick";
        check(Protocol.Hello("t").S("operator_name") == "Nick", "control: hello carries operator_name");
        Protocol.OperatorName = null;

        var c = ChatMessage.From(Fx(fixture, "chat"));
        check(c.Kind == "chat" && c.Name == "Alex" && c.Id == 7 && c.Uuid.StartsWith("0f1e") && c.Text == "hi giant", "control: chat fixture");
        var op = ChatMessage.From(Fx(fixture, "chat_operator"));
        check(op.Kind == "operator" && op.Session == "6f1c" && op.Line == "[Giant] Nick: hello from the table", "control: operator echo fixture");
        var r = CommandResult.From(Fx(fixture, "command_result"));
        check(r.RefSeq == 80 && r.Ok && !r.AsChat && Math.Abs(r.Ms - 0.42) < 1e-9 && r.Lines.Single() == "Summoned new Lightning Bolt", "control: command_result fixture");
        var o = CommandOutput.From(Fx(fixture, "command_output"));
        check(o.RefSeq == 81 && o.Lines.Count == 1, "control: command_output fixture");
        var s = CommandSuggestions.From(Fx(fixture, "command_suggestions"));
        check(s.RefSeq == 82 && s.Start == 9 && s.Items.SequenceEqual(new[] { "advance_time", "advance_weather" }), "control: command_suggestions fixture");
        var st = ServerStatus.From(Fx(fixture, "server_status"));
        check(st.Tps[1] == 19.98 && st.Mspt == 3.21 && st.UsedMb == 812 && st.Online == 1 && st.Worlds.Count == 2 && st.Worlds[1].Env == "nether" && !st.Tick.Frozen,
              "control: server_status fixture");
        var wi = WorldInfo.From(Fx(fixture, "world_info"));
        check(wi.RefSeq == 84 && wi.Weather == "rain" && wi.Tick.Frozen && wi.Spawn.y == 64 && wi.Rules.Count == 6 &&
              wi.Rules.First(x => x.Key == "advance_time").Value is bool && wi.Rules.First(x => x.Key == "random_tick_speed").Value is double,
              "control: world_info fixture (bools and numbers)");
        var pi = PlayersInfo.From(Fx(fixture, "players_info"));
        check(pi.Players.Count == 2 && pi.Players[0].Name == "Alex" && pi.Players[0].Health == 18.5f && pi.Players[1].Op && pi.Players[1].World == "world_nether",
              "control: players_info fixture");

        // Builders match the fixtures' shapes.
        var cmd = ControlProtocol.Command("summon minecraft:lightning_bolt ~ ~1 ~", "world", new Vector3Int(105, 64, -35));
        var fcmd = Fx(fixture, "command");
        check(Json.Write(cmd["at"]) == Json.Write(fcmd["at"]) && cmd.S("line") == fcmd.S("line"), "control: command builder with at matches the fixture");
        check(!ControlProtocol.Command("list").ContainsKey("at"), "control: command without a pointer has no at");
        check(ControlProtocol.ChatSend("x").S("type") == "chat_send" && ControlProtocol.CommandComplete("gamerule adv").S("line") == "gamerule adv" &&
              ControlProtocol.WorldQuery().S("type") == "world_query" && ControlProtocol.PlayersQuery().S("type") == "players_query", "control: builders");
    }

    static ChatMessage Msg(string kind, string name, string text, string uuid = null, string line = null, string session = null) =>
        new ChatMessage { Kind = kind, Name = name, Text = text, Uuid = uuid, Line = line, Session = session };

    static void Chat(Action<bool, string> check, Func<string, string> fixture)
    {
        check(ChatLog.Format(Msg("chat", "Alex", "hi")) == "<Alex> hi" && ChatLog.Format(Msg("say", "Server", "restart soon")) == "[Server] restart soon" &&
              ChatLog.Format(Msg("me", "Alex", "waves")) == "* Alex waves" && ChatLog.Format(Msg("whisper", "Alex", "psst")) == "Alex whispers: psst" &&
              ChatLog.Format(Msg("death", "Alex", "Alex was slain by Zombie")) == "Alex was slain by Zombie" &&
              ChatLog.Format(Msg("operator", "Nick", "yo", line: "[Giant] Nick: yo")) == "[Giant] Nick: yo", "chat: lines formatted like the game");
        check(ChatLog.ToneOf("death") == ChatTone.Death && ChatLog.ToneOf("operator") == ChatTone.Operator && ChatLog.Toast("join") && ChatLog.Toast("death") && !ChatLog.Toast("chat"),
              "chat: tones and toast kinds");
        var log = new ChatLog();
        for (int i = 0; i < 250; i++) log.Add(Msg("chat", "A", "m" + i), i * 0.1);
        check(log.Count == ChatLog.Capacity && log.Entries[0].Text == "m50", "chat: ring of 200");
        var mine = log.Add(ChatMessage.From(Fx(fixture, "chat_operator")), 100, "6f1c");
        var other = log.Add(ChatMessage.From(Fx(fixture, "chat_operator")), 100, "zzzz");
        check(mine.Mine && !other.Mine, "chat: the echo of our own line is marked by session");

        var wl = new ChatLog();
        for (int i = 0; i < 7; i++) wl.Add(Msg("chat", "A", "w" + i), i);
        var wrist = wl.Wrist(7);
        check(wrist.Count == 5 && wrist[0].Text == "<A> w2" && wrist[4].Alpha == 1, "chat: wrist shows the last 5");
        var fading = wl.Wrist(2 + ChatLog.WristSeconds - 0.75);
        check(fading[0].Text == "<A> w2" && Math.Abs(fading[0].Alpha - 0.5f) < 0.01f, "chat: wrist lines fade over the last 1.5 s of 10");
        check(wl.Wrist(100).Count == 0, "chat: wrist empty after 10 s");

        var wrap = ChatLog.Wrap("the quick brown fox jumps over the lazy dog", 12);
        check(wrap.SequenceEqual(new[] { "the quick", "brown fox", "jumps over", "the lazy dog" }), "chat: word wrap");
        check(ChatLog.Wrap(new string('x', 25), 10).SequenceEqual(new[] { "xxxxxxxxxx", "xxxxxxxxxx", "xxxxx" }) && ChatLog.Wrap("", 10).Count == 1, "chat: long words cut, empty is one line");
        var page = new List<WristLine>();
        int total = wl.Page(3, 40, 0, page);
        check(total == 7 && page.Count == 3 && page[2].Text == "<A> w6", "chat: page shows the newest at the bottom");
        wl.Page(3, 40, 2, page);
        check(page[2].Text == "<A> w4", "chat: scrolled up two lines");

        var b = new ChatBubbles();
        string u = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
        b.Add(Msg("join", "Alex", "Alex joined the game", u), 0);
        b.Add(Msg("chat", "Alex", "no uuid"), 0);
        check(!b.Players.Any(), "bubbles: only chat/say/me with a uuid");
        for (int i = 0; i < 5; i++) b.Add(Msg("chat", "Alex", "line " + i, u), i);
        check(b.For(u).Count == ChatBubbles.MaxPerPlayer && b.For(u)[0].Text == "line 2", "bubbles: 3 stacked per player");
        b.Add(Msg("me", "Alex", "waves", u), 5);
        check(b.Text(u).EndsWith("* waves"), "bubbles: me as * text");
        check(Math.Abs(ChatBubbles.Life("12345") - 6.25) < 1e-9, "bubbles: 6 s + 0.05 s per character");
        check(!b.Prune(5) && b.Prune(30) && !b.Players.Any() && b.For(u).Count == 0, "bubbles: expire");
        check(b.For("someone-else").Count == 0 && b.For(null).Count == 0, "bubbles: matched by uuid only");
    }

    static void ConsoleTests(Action<bool, string> check, Func<string, string> fixture)
    {
        var c = new ConsoleModel();
        c.SetInput("/time set day", 0);
        string line = c.Submit(0.1);
        check(line == "time set day" && c.Input == "" && c.History.Single() == "time set day", "console: submit strips the slash and remembers");
        c.SetInput("time set day", 1); c.Submit(1);
        c.SetInput("   ", 2);
        check(c.Submit(2) == null && c.History.Count == 1, "console: empty lines and repeats aren't remembered twice");
        c.SetInput("weather clear", 3); c.Submit(3);
        c.SetInput("draft", 4);
        check(c.HistoryUp() && c.Input == "weather clear" && c.HistoryUp() && c.Input == "time set day" && !c.HistoryUp(), "console: history up");
        check(c.HistoryDown() && c.Input == "weather clear" && c.HistoryDown() && c.Input == "draft" && !c.HistoryDown(), "console: history down back to the draft");
        string saved = c.SaveHistory();
        var d = new ConsoleModel();
        d.LoadHistory(saved);
        check(d.History.SequenceEqual(c.History), "console: history survives JSON (files/history.json)");
        d.LoadHistory("not json"); d.LoadHistory("{\"a\":1}");
        check(d.History.Count == 2, "console: a broken history file is ignored");
        var big = new ConsoleModel();
        for (int i = 0; i < 150; i++) { big.SetInput("say " + i, i); big.Submit(i); }
        check(big.History.Count == ConsoleModel.HistoryMax && big.History[0] == "say 50", "console: history keeps the last 100");

        // Results and output, matched by seq.
        var e = c.Sent(80, "summon minecraft:lightning_bolt ~ ~1 ~", "@ 105 64 -35");
        check(c.Output().Last().Kind == ConsoleLineKind.Pending, "console: pending until the result");
        check(c.OnResult(CommandResult.From(Fx(fixture, "command_result"))) && e.State == ConsoleState.Ok && e.Lines[0] == "Summoned new Lightning Bolt", "console: result by ref_seq");
        check(!c.OnResult(new CommandResult { RefSeq = 999 }), "console: a result for an unknown seq is ignored");
        var f = c.Sent(81, "backup");
        c.OnResult(new CommandResult { RefSeq = 81, Ok = true });
        check(c.OnOutput(CommandOutput.From(Fx(fixture, "command_output"))) && f.Lines.Single() == "Backup finished in 1.2 s", "console: late output joins its command");
        var g = c.Sent(83, "stop");
        check(c.OnError(83, "command_denied", "/stop is in commands.deny") && g.State == ConsoleState.Refused && g.Lines[0].Contains("commands.deny"), "console: refused commands");
        c.Sent(84, "time sett");
        c.OnResult(new CommandResult { RefSeq = 84, Ok = false, Lines = new List<string> { "Incorrect argument for command at position 5: time <--[HERE]" } });
        var outp = c.Output();
        check(outp.Any(x => x.Text == "> /summon minecraft:lightning_bolt ~ ~1 ~  @ 105 64 -35" && x.Kind == ConsoleLineKind.Command) &&
              outp.Last().Kind == ConsoleLineKind.Error, "console: output grouped per command, failures as errors");
        c.Refused("list", "not connected");
        check(c.Entries.Last().State == ConsoleState.Refused, "console: offline lines are refused at once");

        // Completion: debounce, stale replies, chips, accepting.
        var k = new ConsoleModel();
        k.SetInput("gamerule adv", 10.0);
        check(k.CompletionDue(10.1) == null && k.CompletionDue(10.16) == "gamerule adv" && k.CompletionDue(10.3) == null, "complete: asked 150 ms after the last edit, once");
        k.CompletionSent(82, "gamerule adv");
        var sug = CommandSuggestions.From(Fx(fixture, "command_suggestions"));
        check(k.OnSuggestions(sug) && k.Chips().SequenceEqual(new[] { "advance_time", "advance_weather" }), "complete: chips from the reply");
        check(!k.OnSuggestions(new CommandSuggestions { RefSeq = 70, Start = 0, Items = new List<string> { "x" } }), "complete: a reply to an older request is dropped");
        check(k.AcceptChip(0, 11) && k.Input == "gamerule advance_time" && k.Chips().Count == 0, "complete: a chip replaces from start");
        check(k.CompletionDue(11) == "gamerule advance_time", "complete: the next argument is asked at once");
        k.CompletionSent(90, "gamerule advance_time");
        k.SetInput("gamerule advance_time ", 11.2);
        check(!k.OnSuggestions(new CommandSuggestions { RefSeq = 90, Start = 9, Items = new List<string> { "y" } }), "complete: suggestions for a line that changed meanwhile are dropped");
        var many = new CommandSuggestions { RefSeq = 91, Start = 22, Items = Enumerable.Range(0, 20).Select(i => "s" + i).ToList() };
        k.CompletionSent(91, k.Input);
        check(k.OnSuggestions(many) && k.Chips().Count == ConsoleModel.ChipCount, "complete: at most 6 chips");
        k.SetInput("", 12);
        check(k.CompletionDue(13) == null, "complete: nothing asked for an empty line");
        k.SetQuick(new[] { "time set day", "/weather clear" }, new[] { "weather clear", "tp @a ~ ~ ~", "" });
        check(k.Quick.SequenceEqual(new[] { "time set day", "weather clear", "tp @a ~ ~ ~" }), "console: quick buttons from the server and tabletop.json, deduplicated");
    }

    static void Panel(Action<bool, string> check, Func<string, string> fixture)
    {
        // Templates.
        check(OperatorCommands.TeleportHere("Alex") == "tp Alex ~ ~1 ~" && OperatorCommands.GameMode("creative", "Alex") == "gamemode creative Alex" &&
              OperatorCommands.Heal("Alex") == "effect give Alex minecraft:instant_health 1 10" && OperatorCommands.Feed(".BedrockGuy") == "effect give .BedrockGuy minecraft:saturation 1 10" &&
              OperatorCommands.Kick("Alex", " griefing\n") == "kick Alex griefing" && OperatorCommands.Kick("Alex", "") == "kick Alex", "templates: player commands");
        check(OperatorCommands.TeleportHere("Al ex") == null && OperatorCommands.Kick("@a", "x") == null && OperatorCommands.SafeName("Steve_1") == "Steve_1", "templates: unsafe names refused");
        check(OperatorCommands.TeleportHere(".Steve") == "tp .Steve ~ ~1 ~" && OperatorCommands.Kick(".Steve", "") == "kick .Steve", "templates: Floodgate (Bedrock) names with the . prefix");
        check(OperatorCommands.GameRule("advance_time", false) == "gamerule advance_time false" && OperatorCommands.GameRule("random_tick_speed", 3.0) == "gamerule random_tick_speed 3" &&
              OperatorCommands.Time("midnight") == "time set midnight" && OperatorCommands.Weather("thunder") == "weather thunder" && OperatorCommands.Difficulty("hard") == "difficulty hard",
              "templates: world commands");
        check(OperatorPanelModel.Clock(0) == "06:00" && OperatorPanelModel.Clock(6000) == "12:00" && OperatorPanelModel.Clock(18000) == "00:00" && OperatorPanelModel.Clock(23999) == "05:59",
              "templates: game time as a clock");

        var p = new OperatorPanelModel { Connected = true };
        p.OnWelcome(ControlWelcome.From(Fx(fixture, "welcome_control")), new[] { "tick step 100" });
        check(p.Console.Quick.Count == 5 && p.Console.Quick[4] == "tick step 100", "panel: quick buttons merged");
        // Every tab: items inside the panel, no overlapping clickable items, hit tests find each one.
        bool inside = true, noOverlap = true, hits = true;
        p.SetOpen(true);
        p.OnPlayers(PlayersInfo.From(Fx(fixture, "players_info")));
        p.OnWorld(WorldInfo.From(Fx(fixture, "world_info")));
        foreach (OpTab t in Enum.GetValues(typeof(OpTab)))
        {
            p.SetTab(t);
            var L = p.Layout();
            var click = L.Items.Where(i => i.Part != PanelPart.None).ToList();
            foreach (var it in L.Items) if (it.R.X < 0 || it.R.Y < 0 || it.R.X + it.R.W > L.Width + 1e-5f || it.R.Y + it.R.H > L.Height + 1e-5f) inside = false;
            for (int a = 0; a < click.Count; a++)
                for (int bb = a + 1; bb < click.Count; bb++)
                {
                    var A = click[a].R; var B = click[bb].R;
                    if (A.X + A.W - 1e-5f > B.X && B.X + B.W - 1e-5f > A.X && A.Y + A.H - 1e-5f > B.Y && B.Y + B.H - 1e-5f > A.Y) noOverlap = false;
                }
            foreach (var it in click) { var h = OperatorPanelModel.HitTest(L, it.R.CX, it.R.CY); if (h.Part != it.Part || h.Index != it.Index) hits = false; }
            if (L.Height > 0.40f) inside = false;   // fits beside the hand
        }
        check(inside && noOverlap && hits, "panel: every tab lays out inside the panel, no overlaps, hit tests round-trip");
        p.SetTab(OpTab.Chat);
        var LC = p.Layout();
        check(LC.Keyboard.H > 0 && LC.Field.W > 0 && LC.TextRows == OperatorPanelModel.ChatRows && OperatorPanelModel.HitTest(LC, -0.01f, 0).Part == PanelPart.None,
              "panel: Chat has the log, a field and the keyboard block");

        // Clicking: tabs, chat, console enter with the pointer, buttons and their templates.
        PanelAction Click(PanelPart part, int index)
        {
            var L = p.Layout();
            p.Point(new PanelHit(part, index), true);
            return p.Click(L, 0);
        }
        check(Click(PanelPart.Tab, (int)OpTab.World).Kind == PanelActionKind.Tab && p.Tab == OpTab.World, "panel: tab click");
        var a1 = Click(PanelPart.Button, OperatorPanelModel.BtnTime + 3);
        check(a1.Kind == PanelActionKind.Command && a1.Line == "time set midnight" && !a1.AtPointer, "panel: World time button runs time set midnight");
        var noPtr = Click(PanelPart.Button, OperatorPanelModel.BtnLightning);
        check(noPtr.Kind == PanelActionKind.Refused && p.Note.Contains("aim at a block"), "panel: lightning needs the pointer");
        p.PointerLabel = "@ 105 64 -35";
        var bolt = Click(PanelPart.Button, OperatorPanelModel.BtnLightning);
        check(bolt.Kind == PanelActionKind.Command && bolt.AtPointer && bolt.Line == OperatorCommands.Lightning, "panel: lightning at the pointer");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnUnfreeze).Line == "tick unfreeze", "panel: tick button follows the frozen state");
        var rule = Click(PanelPart.Rule, 0);
        check(rule.Kind == PanelActionKind.Command && rule.Line == "gamerule advance_time false", "panel: a gamerule toggle flips the server's value");
        check(p.QueryDue(0) == "world_query" && p.QueryDue(0.5) == null && p.QueryDue(1.1) == "world_query", "panel: World tab polls world_info every second");

        p.SetTab(OpTab.Players);
        check(p.QueryDue(2) == "players_query", "panel: Players tab polls players_info");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnHeal).Kind == PanelActionKind.Refused && p.Note == "select a player first", "panel: player buttons need a selection");
        check(Click(PanelPart.PlayerRow, 1).Kind == PanelActionKind.Selected && p.SelectedPlayer == "1a2b3c4d-5e6f-4a1b-9c2d-3e4f5a6b7c8d", "panel: select a player row");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnGameMode + 1).Line == "gamemode creative Steve" && Click(PanelPart.Button, OperatorPanelModel.BtnTeleport).Line == "tp Steve ~ ~1 ~",
              "panel: game mode and teleport-here for the selected player");
        check(Click(PanelPart.Button, OperatorPanelModel.BtnKick).Kind == PanelActionKind.Toggled && p.Focus == OperatorPanelModel.Field.Kick && p.Layout().Keyboard.H > 0, "panel: kick asks for a reason");
        p.Type("bye for now", 3);
        var kick = p.Enter(3);
        check(kick.Kind == PanelActionKind.Command && kick.Line == "kick Steve bye for now" && p.KickTarget == null, "panel: enter kicks with the reason");

        p.SetTab(OpTab.Console);
        p.Type("weather rain", 4);
        var en = p.Enter(4);
        check(en.Kind == PanelActionKind.Command && en.Line == "weather rain" && !en.AtPointer && p.Console.Input == "", "panel: console enter sends the line");
        Click(PanelPart.AtToggle, 0);
        p.PointerLabel = null;
        p.Type("setblock ~ ~ ~ stone", 5);
        check(p.Enter(5).Kind == PanelActionKind.Refused && p.Console.Input == "setblock ~ ~ ~ stone", "panel: @ pointer without an aimed block keeps the line");
        p.PointerLabel = "@ 1 2 3";
        var atl = p.Enter(5);
        check(atl.Kind == PanelActionKind.Command && atl.AtPointer, "panel: @ pointer sends at");
        var q = Click(PanelPart.Quick, 0);
        check(q.Kind == PanelActionKind.Command && q.Line == "time set day", "panel: quick button");

        p.SetTab(OpTab.Chat);
        p.Type("hello table", 6);
        var ch = p.Enter(6);
        check(ch.Kind == PanelActionKind.Chat && ch.Line == "hello table" && p.ChatInput == "", "panel: chat enter sends chat_send");

        // Without command_v1 (no auth.token on the server) every command button is greyed and says why.
        var n = new OperatorPanelModel { Connected = true };
        n.OnWelcome(ControlWelcome.From(Fx(fixture, "welcome")), null);
        n.SetOpen(true);
        n.SetTab(OpTab.World);
        var LW = n.Layout();
        check(LW.Items.Where(i => i.Part == PanelPart.Button).All(i => !i.Enabled), "panel: no command_v1 -> command buttons greyed");
        n.Point(new PanelHit(PanelPart.Button, OperatorPanelModel.BtnTime), true);
        check(n.Click(LW, 0).Kind == PanelActionKind.Refused && n.Note.Contains("needs auth.token"), "panel: a greyed button says needs auth.token");
        n.SetTab(OpTab.Console);
        n.Type("list", 0);
        check(n.Enter(0).Kind == PanelActionKind.Refused, "panel: console refuses without command_v1");
        n.SetTab(OpTab.Chat);
        check(!n.Layout().Items.First(i => i.Part == PanelPart.Send).Enabled, "panel: no chat_v1 -> Send greyed");

        // X: tap toggles, hold opens Chat.
        var x = new OperatorPanelModel();
        x.XDown(0); x.XHeld(0.1); x.XUp(0.15);
        check(x.Open, "X: a tap opens");
        x.SetTab(OpTab.World);
        x.XDown(1); x.XUp(1.2);
        check(!x.Open, "X: a tap on the open panel closes it");
        x.XDown(2); x.XHeld(2.3); x.XHeld(2.65); x.XUp(3);
        check(x.Open && x.Tab == OpTab.Chat && x.Focus == OperatorPanelModel.Field.Chat, "X: holding 0.6 s opens Chat with the input focused");
        x.SetTab(OpTab.Server);
        x.XDown(4); x.XHeld(4.7); x.XUp(4.8);
        check(x.Open && x.Tab == OpTab.Chat, "X: holding on the open panel switches to Chat instead of closing");

        // Scrolling and the server text.
        var sc = new OperatorPanelModel { Connected = true };
        sc.SetOpen(true);
        for (int i = 0; i < 40; i++) sc.Log.Add(Msg("chat", "A", "line " + i), i);
        var LS = sc.Layout();
        sc.Point(new PanelHit(PanelPart.Panel, 0), true);
        check(sc.MaxScroll(LS) == 40 - OperatorPanelModel.ChatRows && sc.Stick(1, 0) && sc.Scroll == 1 && !sc.Stick(1, 0.1) && sc.Stick(1, 0.5) && sc.Scroll == 2,
              "panel: right stick scrolls the chat back, with repeat");
        check(OperatorPanelModel.StatusText(ServerStatus.From(Fx(fixture, "server_status"))).Contains("TPS 20.0 / 20.0 / 20.0") &&
              OperatorPanelModel.WorldText(WorldInfo.From(Fx(fixture, "world_info"))).Contains("12:00, day 12"), "panel: server and world texts");
    }

    /// <summary>--live-control URL TOKEN: the app's own classes against a real plugin.</summary>
    public static void Live(string url, string token, Action<bool, string> check)
    {
        Protocol.OperatorName = "Live Check";
        var link = new LinkClient(url, token);
        link.Start();
        var panel = new OperatorPanelModel();
        string session = null;
        var t0 = DateTime.UtcNow;
        double Now() => (DateTime.UtcNow - t0).TotalSeconds;
        var seen = new List<string>();
        bool Wait(Func<bool> done, double seconds = 6)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                while (link.Inbound.TryDequeue(out var m))
                {
                    string type = m.S("type");
                    seen.Add(type);
                    switch (type)
                    {
                        case "welcome": session = m.S("session"); panel.Connected = true; panel.OnWelcome(ControlWelcome.From(m), null); break;
                        case "chat": panel.Log.Add(ChatMessage.From(m), Now(), session); break;
                        case "command_result": panel.Console.OnResult(CommandResult.From(m)); break;
                        case "command_output": panel.Console.OnOutput(CommandOutput.From(m)); break;
                        case "command_suggestions": panel.Console.OnSuggestions(CommandSuggestions.From(m)); break;
                        case "server_status": panel.OnStatus(ServerStatus.From(m)); break;
                        case "world_info": panel.OnWorld(WorldInfo.From(m)); break;
                        case "players_info": panel.OnPlayers(PlayersInfo.From(m)); break;
                        case "error": panel.Console.OnError((long)m.D("ref_seq", -1), m.S("code"), m.S("message")); break;
                    }
                }
                if (done()) return true;
                Thread.Sleep(5);
            }
            return false;
        }
        check(Wait(() => panel.Connected && link.Connected), "live control: welcome");
        check(panel.Welcome.Commands && panel.Welcome.Chat && panel.Welcome.Status && panel.Welcome.ChatName == "LiveCheck", "live control: caps granted, name sanitised");
        long seq = link.SendTracked(ControlProtocol.Command("time set day"));
        var e = panel.Console.Sent(seq, "time set day");
        check(seq >= 0 && Wait(() => e.State != ConsoleState.Pending) && e.State == ConsoleState.Ok && e.Lines.Count > 0, $"live control: command result matched by seq ({string.Join(" | ", e.Lines)})");
        long bad = link.SendTracked(ControlProtocol.Command("stop"));
        var be = panel.Console.Sent(bad, "stop");
        check(Wait(() => be.State != ConsoleState.Pending) && be.State == ConsoleState.Refused, "live control: denied root refused");
        panel.Console.SetInput("gamerule adv", Now());
        Thread.Sleep(200);
        string due = panel.Console.CompletionDue(Now());
        panel.Console.CompletionSent(link.SendTracked(ControlProtocol.CommandComplete(due)), due);
        check(Wait(() => panel.Console.Chips().Count > 0) && panel.Console.Chips().Contains("advance_time"), $"live control: chips {string.Join(",", panel.Console.Chips())}");
        link.SendTracked(ControlProtocol.WorldQuery());
        link.SendTracked(ControlProtocol.PlayersQuery());
        check(Wait(() => panel.World != null && panel.Players != null && panel.Status != null), "live control: world_info, players_info, server_status");
        check(panel.World != null && panel.World.Rules.Any(r => r.Key == "advance_time"), $"live control: {panel.World?.Rules.Count} game rules");
        link.SendTracked(ControlProtocol.ChatSend("hello from the C# test"));
        check(Wait(() => panel.Log.Entries.Any(x => x.Mine)) && panel.Log.Entries.Last(x => x.Mine).Display.StartsWith("[Giant] LiveCheck:"), "live control: chat echo marked as ours");
        System.Console.WriteLine("live control: " + OperatorPanelModel.StatusText(panel.Status).Replace("\n", " | "));
        link.Dispose();
        Protocol.OperatorName = null;
    }
}
