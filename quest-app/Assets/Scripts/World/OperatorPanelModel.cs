// [talk] The operator panel (X on the left controller; docs/QUEST_SETUP.md Part L), engine-free (Tests~). Like the
// block menu (World/BlockMenuModel.cs) it owns the state, the layout in panel metres (origin top-left, y down), hit
// tests and every input rule; XR/OperatorPanel.cs draws it and feeds it the right controller's ray.
//
//   Chat     the chat log (scrolls), the input line and Send (chat_send)
//   Console  command output grouped per command, suggestion chips, history up/down, "@ pointer", quick buttons
//   Players  online players (health, food, game mode, ping); for the selected one: teleport to the pointer, game
//            modes, heal, feed, kick with a reason
//   World    time, weather, lightning at the pointer, tick freeze/step, difficulty, and the world's game rules as
//            toggles, by the names the server sent
//   Server   TPS, MSPT, memory, players, per-world chunks/entities/time/weather, tick state (server_status)
//
// Every button that runs a command is a template on top of the console path (World/OperatorCommands below); without
// command_v1 they are greyed and say "needs auth.token". Text fields are plain strings here; the panel's on-screen
// keyboard (KeyboardModel) types into the focused one.
using System;
using System.Collections.Generic;
using System.Text;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public enum OpTab { Chat, Console, Players, World, Server }

    public enum PanelPart { None, Panel, Tab, ScrollUp, ScrollDown, Send, Chip, Quick, HistoryUp, HistoryDown, AtToggle, Complete, PlayerRow, Button, Rule, Key, Cancel }

    public struct PanelItem
    {
        public PanelPart Part; public int Index; public MenuRect R; public string Label;
        public bool Enabled, Active;
    }

    public sealed class PanelLayout
    {
        public float Width, Height;
        public readonly List<PanelItem> Items = new List<PanelItem>();
        /// <summary>Text areas: the log/output/status, the input field, the reserved keyboard block, the note line.</summary>
        public MenuRect Text, Field, Keyboard, Note, Header;
        public int TextRows;
        public int TextChars;
    }

    public struct PanelHit
    {
        public PanelPart Part; public int Index;
        public PanelHit(PanelPart p, int i) { Part = p; Index = i; }
        public static readonly PanelHit Nothing = new PanelHit(PanelPart.None, 0);
        public bool Same(PanelHit o) => Part == o.Part && Index == o.Index;
        public override string ToString() => Part + (Part == PanelPart.None || Part == PanelPart.Panel ? "" : "#" + Index);
    }

    public enum PanelActionKind { None, Tab, Command, Chat, Scrolled, Selected, Toggled, Refused, Typed }

    public struct PanelAction
    {
        public PanelActionKind Kind;
        /// <summary>Command: the line; Chat: the text.</summary>
        public string Line;
        /// <summary>Command: run at the pointer (command "at", PROTOCOL.md 4.27).</summary>
        public bool AtPointer;
        public bool Haptic => Kind != PanelActionKind.None && Kind != PanelActionKind.Refused;
    }

    /// <summary>The command templates behind the panel's buttons (pure, tested).</summary>
    public static class OperatorCommands
    {
        public static readonly string[] Times = { "day", "noon", "night", "midnight" };
        public static readonly string[] Weathers = { "clear", "rain", "thunder" };
        public static readonly string[] GameModes = { "survival", "creative", "adventure", "spectator" };
        public static readonly string[] Difficulties = { "peaceful", "easy", "normal", "hard" };

        /// <summary>A player name that is safe to put into a command (Java names; Bedrock "." prefixes), else null.</summary>
        public static string SafeName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.Length > 32) return null;
            foreach (char c in n) if (!(char.IsLetterOrDigit(c) && c < 128) && c != '_' && c != '.') return null;
            return n;
        }

        public static string Time(string t) => "time set " + t;
        public static string Weather(string w) => "weather " + w;
        /// <summary>Run at the pointer: the bolt strikes the top of the aimed block.</summary>
        public const string Lightning = "summon minecraft:lightning_bolt ~ ~1 ~";
        public const string Freeze = "tick freeze", Unfreeze = "tick unfreeze", Step = "tick step 20";
        public static string Difficulty(string d) => "difficulty " + d;
        public static string GameRule(string name, object value) => "gamerule " + name + " " + Value(value);
        static string Value(object v) => v is bool b ? (b ? "true" : "false") : v is double d ? ((long)d == d ? ((long)d).ToString() : d.ToString(System.Globalization.CultureInfo.InvariantCulture)) : v?.ToString() ?? "";
        /// <summary>Run at the pointer: the player lands on top of the aimed block, in its dimension.</summary>
        public static string TeleportHere(string n) => SafeName(n) is string s ? "tp " + s + " ~ ~1 ~" : null;
        public static string GameMode(string mode, string n) => SafeName(n) is string s ? "gamemode " + mode + " " + s : null;
        public static string Heal(string n) => SafeName(n) is string s ? "effect give " + s + " minecraft:instant_health 1 10" : null;
        public static string Feed(string n) => SafeName(n) is string s ? "effect give " + s + " minecraft:saturation 1 10" : null;
        public static string Kick(string n, string reason)
        {
            if (!(SafeName(n) is string s)) return null;
            string r = (reason ?? "").Replace('\n', ' ').Trim();
            return "kick " + s + (r.Length > 0 ? " " + r : "");
        }
    }

    public sealed class OperatorPanelModel
    {
        // Layout (metres): as wide as the block menu, 24 mm buttons, 11.5 mm text rows.
        public const float Margin = 0.008f, Gap = 0.004f, TabH = 0.024f, LineH = 0.0115f, FieldH = 0.022f, ButtonH = 0.024f, KeyH = 0.024f,
                           NoteH = 0.016f, ScrollW = 0.022f, RowH = 0.022f, LabelW = 0.052f;
        public const int ChatRows = 11, ConsoleRows = 8, PlayerRows = 6, RuleRows = 6, ServerRows = 15, KeyboardRows = 4;
        public const double HoldSeconds = 0.6, QueryEvery = 1.0;
        public static readonly string[] TabNames = { "Chat", "Console", "Players", "World", "Server" };

        public bool Open { get; private set; }
        public OpTab Tab { get; private set; } = OpTab.Chat;
        public PanelHit Hover { get; private set; } = PanelHit.Nothing;
        public bool PointerOnPanel { get; private set; }
        public bool CapturesRight => Open && PointerOnPanel;
        public int Version { get; private set; }

        public readonly ChatLog Log = new ChatLog();
        public readonly ConsoleModel Console = new ConsoleModel();
        public ControlWelcome Welcome = new ControlWelcome();
        public ServerStatus Status;
        public WorldInfo World;
        public PlayersInfo Players;
        public bool Connected;
        /// <summary>"@ x y z" of the block the right controller last aimed at, or null (set by the app every frame).</summary>
        public string PointerLabel;
        /// <summary>A keyboard is drawn in the reserved block (after the shared KeyboardModel is wired).</summary>
        public bool HasKeyboard;

        public string ChatInput { get; private set; } = "";
        public string KickReason { get; private set; } = "";
        /// <summary>Kicking this player: the field and keyboard take the reason, Enter sends.</summary>
        public string KickTarget { get; private set; }
        public string SelectedPlayer { get; private set; }   // uuid
        /// <summary>Last thing that happened (the note line).</summary>
        public string Note = "";

        readonly Dictionary<OpTab, int> _scroll = new Dictionary<OpTab, int>();
        float _stickDir; double _stickNext;
        double _xDownAt = -1; bool _xWasOpen, _xHeld;
        double _nextQuery;

        public bool CommandsOn => Welcome.Commands && Connected;
        public bool ChatOn => Welcome.Chat && Welcome.ChatSend && Connected;
        public string CommandsOffWhy => !Connected ? "not connected" : "needs auth.token on the server";
        public int Scroll => _scroll.TryGetValue(Tab, out var v) ? v : 0;

        /// <summary>Which text field the keyboard types into.</summary>
        public enum Field { None, Chat, Console, Kick }
        public Field Focus => Tab == OpTab.Chat ? Field.Chat : Tab == OpTab.Console ? Field.Console : Tab == OpTab.Players && KickTarget != null ? Field.Kick : Field.None;
        public string FocusText => Focus == Field.Chat ? ChatInput : Focus == Field.Console ? Console.Input : Focus == Field.Kick ? KickReason : "";
        public int FocusMaxLen => Focus == Field.Chat ? Welcome.ChatMaxLen : Focus == Field.Console ? Welcome.CommandMaxLen : 200;

        public void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (!open) { PointerOnPanel = false; Hover = PanelHit.Nothing; KickTarget = null; }
            _nextQuery = 0;
            Version++;
        }

        public bool SetTab(OpTab t)
        {
            if (t == Tab) return false;
            Tab = t; KickTarget = null; _nextQuery = 0; Note = "";
            Version++;
            return true;
        }

        // ---------- X: tap toggles, hold 0.6 s opens Chat with the input focused ----------
        public void XDown(double now)
        {
            _xDownAt = now; _xWasOpen = Open; _xHeld = false;
            if (!Open) SetOpen(true);
        }

        public void XHeld(double now)
        {
            if (_xDownAt < 0 || _xHeld || now - _xDownAt < HoldSeconds) return;
            _xHeld = true;
            SetOpen(true);
            SetTab(OpTab.Chat);
            Version++;
        }

        public void XUp(double now)
        {
            if (_xDownAt >= 0 && !_xHeld && _xWasOpen && now - _xDownAt < HoldSeconds) SetOpen(false);
            _xDownAt = -1;
        }

        // ---------- typing (the keyboard calls these) ----------
        /// <summary>Replaces the focused field's text (the keyboard's text after a key press).</summary>
        public void Type(string text, double now)
        {
            text = text ?? "";
            int max = FocusMaxLen;
            if (text.Length > max) text = text.Substring(0, max);
            switch (Focus)
            {
                case Field.Chat: ChatInput = text; break;
                case Field.Console: Console.SetInput(text, now); break;
                case Field.Kick: KickReason = text; break;
                default: return;
            }
            Version++;
        }

        /// <summary>Enter on the keyboard (or Send): what to send for the focused field.</summary>
        public PanelAction Enter(double now)
        {
            switch (Focus)
            {
                case Field.Chat:
                {
                    string t = ChatInput.Trim();
                    if (t.Length == 0) return default;
                    if (!ChatOn) return Refuse(!Connected ? "not connected" : !Welcome.Chat ? "the server has no chat for headsets" : "sending chat is off on the server");
                    ChatInput = ""; _scroll[OpTab.Chat] = 0; Version++;
                    return new PanelAction { Kind = PanelActionKind.Chat, Line = t };
                }
                case Field.Console:
                {
                    if (Console.Input.Trim().TrimStart('/').Trim().Length == 0) return default;
                    if (!CommandsOn) return Refuse("commands " + CommandsOffWhy);
                    if (Console.AtPointer && PointerLabel == null) return Refuse("aim at a block first (or turn @ pointer off)");
                    string line = Console.Submit(now);
                    _scroll[OpTab.Console] = 0;
                    return new PanelAction { Kind = PanelActionKind.Command, Line = line, AtPointer = Console.AtPointer };
                }
                case Field.Kick:
                {
                    string line = OperatorCommands.Kick(KickTarget, KickReason);
                    if (line == null) return Refuse("can't kick that name");
                    if (!CommandsOn) return Refuse("commands " + CommandsOffWhy);
                    KickTarget = null; KickReason = ""; Version++;
                    return new PanelAction { Kind = PanelActionKind.Command, Line = line };
                }
            }
            return default;
        }

        PanelAction Refuse(string why) { Note = why; Version++; return new PanelAction { Kind = PanelActionKind.Refused, Line = why }; }

        // ---------- server data ----------
        public void OnWelcome(ControlWelcome w, IEnumerable<string> localQuick)
        {
            Welcome = w ?? new ControlWelcome();
            Console.MaxLen = Welcome.CommandMaxLen;
            Console.SetQuick(Welcome.Quick, localQuick);
            Version++;
        }

        public void OnStatus(ServerStatus s) { Status = s; if (Tab == OpTab.Server) Version++; }
        public void OnWorld(WorldInfo w) { World = w; if (Tab == OpTab.World) Version++; }

        public void OnPlayers(PlayersInfo p)
        {
            Players = p;
            if (SelectedPlayer != null && p.Players.FindIndex(x => x.Uuid == SelectedPlayer) < 0) SelectedPlayer = null;
            if (Tab == OpTab.Players) Version++;
        }

        /// <summary>The query to send now: world_query on the World tab, players_query on Players, once a second.</summary>
        public string QueryDue(double now)
        {
            if (!Open || !Connected || !Welcome.Status || now < _nextQuery) return null;
            string q = Tab == OpTab.World ? "world_query" : Tab == OpTab.Players ? "players_query" : null;
            if (q != null) _nextQuery = now + QueryEvery;
            return q;
        }

        /// <summary>A world command ran: ask for world_info again right away.</summary>
        public void RefreshSoon() => _nextQuery = 0;

        // ---------- pointer and clicks ----------
        public void Point(PanelHit hit, bool onPanel)
        {
            if (!Open) { onPanel = false; hit = PanelHit.Nothing; }
            if (onPanel == PointerOnPanel && hit.Same(Hover)) return;
            PointerOnPanel = onPanel; Hover = hit;
            Version++;
        }

        /// <summary>Right trigger on the panel: does what the hovered item does (layout from Layout()).</summary>
        public PanelAction Click(PanelLayout L, double now)
        {
            var none = new PanelAction();
            if (!CapturesRight || L == null) return none;
            PanelItem? found = null;
            foreach (var it in L.Items) if (it.Part == Hover.Part && it.Index == Hover.Index) { found = it; break; }
            if (found == null) return none;
            var item = found.Value;
            if (!item.Enabled) return Refuse(DisabledWhy(item));
            switch (item.Part)
            {
                case PanelPart.Tab: return SetTab((OpTab)item.Index) ? new PanelAction { Kind = PanelActionKind.Tab } : none;
                case PanelPart.ScrollUp: return ScrollBy(+Page(L)) ? new PanelAction { Kind = PanelActionKind.Scrolled } : none;
                case PanelPart.ScrollDown: return ScrollBy(-Page(L)) ? new PanelAction { Kind = PanelActionKind.Scrolled } : none;
                case PanelPart.Send: return Enter(now);
                case PanelPart.Chip: return Console.AcceptChip(item.Index, now) ? new PanelAction { Kind = PanelActionKind.Typed } : none;
                case PanelPart.Complete: return Console.AcceptChip(0, now) ? new PanelAction { Kind = PanelActionKind.Typed } : none;
                case PanelPart.HistoryUp: return Console.HistoryUp() ? new PanelAction { Kind = PanelActionKind.Typed } : none;
                case PanelPart.HistoryDown: return Console.HistoryDown() ? new PanelAction { Kind = PanelActionKind.Typed } : none;
                case PanelPart.AtToggle:
                    Console.AtPointer = !Console.AtPointer; Version++;
                    return new PanelAction { Kind = PanelActionKind.Toggled };
                case PanelPart.Quick:
                    if (item.Index >= Console.Quick.Count) return none;
                    return Run(Console.Quick[item.Index], false);
                case PanelPart.PlayerRow:
                {
                    var rows = VisiblePlayers();
                    if (item.Index >= rows.Count) return none;
                    SelectedPlayer = rows[item.Index].Uuid == SelectedPlayer ? null : rows[item.Index].Uuid;
                    KickTarget = null; Version++;
                    return new PanelAction { Kind = PanelActionKind.Selected };
                }
                case PanelPart.Cancel: KickTarget = null; KickReason = ""; Version++; return new PanelAction { Kind = PanelActionKind.Toggled };
                case PanelPart.Button: return Button(item.Index);
                case PanelPart.Rule:
                {
                    var rules = BoolRules();
                    int k = Scroll * 2 + item.Index;
                    if (k >= rules.Count) return none;
                    RefreshSoon();
                    return Run(OperatorCommands.GameRule(rules[k].Key, !(bool)rules[k].Value), false);
                }
            }
            return none;
        }

        /// <summary>Why a greyed item does nothing (the note line says it when it's clicked).</summary>
        public string DisabledWhy(PanelItem item)
        {
            switch (item.Part)
            {
                case PanelPart.Send: return !Connected ? "not connected" : !Welcome.Chat ? "the server has no chat for headsets" : "sending chat is off on the server";
                case PanelPart.Button: case PanelPart.Rule: case PanelPart.Quick:
                    if (!CommandsOn) return "commands " + CommandsOffWhy;
                    if (Tab == OpTab.Players && SelectedName() == null) return "select a player first";
                    if (item.Index == BtnTeleport || item.Index == BtnLightning) return "aim at a block first (right controller)";
                    if (item.Index == BtnStep) return "freeze the game first";
                    return "not available";
                case PanelPart.HistoryUp: case PanelPart.HistoryDown: return "no history yet";
                case PanelPart.Complete: return "no suggestions for this line";
                default: return "not available";
            }
        }

        PanelAction Run(string line, bool at)
        {
            if (line == null) return Refuse("no player selected");
            if (!CommandsOn) return Refuse("commands " + CommandsOffWhy);
            if (at && PointerLabel == null) return Refuse("aim at a block first (right controller)");
            Note = "/" + line + (at ? "  " + PointerLabel : "");
            Version++;
            return new PanelAction { Kind = PanelActionKind.Command, Line = line, AtPointer = at };
        }

        // Button ids: World 0-3 time, 10-12 weather, 13 lightning, 20 freeze, 21 unfreeze, 22 step, 30-33 difficulty;
        // Players 40 teleport here, 41-44 game modes, 45 heal, 46 feed, 47 kick.
        public const int BtnTime = 0, BtnWeather = 10, BtnLightning = 13, BtnFreeze = 20, BtnUnfreeze = 21, BtnStep = 22, BtnDifficulty = 30,
                         BtnTeleport = 40, BtnGameMode = 41, BtnHeal = 45, BtnFeed = 46, BtnKick = 47;

        PanelAction Button(int id)
        {
            string p = SelectedName();
            if (id >= BtnTime && id < BtnTime + 4) { RefreshSoon(); return Run(OperatorCommands.Time(OperatorCommands.Times[id - BtnTime]), false); }
            if (id >= BtnWeather && id < BtnWeather + 3) { RefreshSoon(); return Run(OperatorCommands.Weather(OperatorCommands.Weathers[id - BtnWeather]), false); }
            if (id == BtnLightning) return Run(OperatorCommands.Lightning, true);
            if (id == BtnFreeze) { RefreshSoon(); return Run(OperatorCommands.Freeze, false); }
            if (id == BtnUnfreeze) { RefreshSoon(); return Run(OperatorCommands.Unfreeze, false); }
            if (id == BtnStep) { RefreshSoon(); return Run(OperatorCommands.Step, false); }
            if (id >= BtnDifficulty && id < BtnDifficulty + 4) { RefreshSoon(); return Run(OperatorCommands.Difficulty(OperatorCommands.Difficulties[id - BtnDifficulty]), false); }
            if (id == BtnTeleport) return Run(OperatorCommands.TeleportHere(p), true);
            if (id >= BtnGameMode && id < BtnGameMode + 4) return Run(OperatorCommands.GameMode(OperatorCommands.GameModes[id - BtnGameMode], p), false);
            if (id == BtnHeal) return Run(OperatorCommands.Heal(p), false);
            if (id == BtnFeed) return Run(OperatorCommands.Feed(p), false);
            if (id == BtnKick)
            {
                if (OperatorCommands.SafeName(p) == null) return Refuse("no player selected");
                KickTarget = p; KickReason = ""; Note = "kick " + p + ": type a reason, enter kicks"; Version++;
                return new PanelAction { Kind = PanelActionKind.Toggled };
            }
            return default;
        }

        string SelectedName()
        {
            if (Players == null || SelectedPlayer == null) return null;
            foreach (var r in Players.Players) if (r.Uuid == SelectedPlayer) return r.Name;
            return null;
        }

        public List<PlayerRow> VisiblePlayers()
        {
            var l = new List<PlayerRow>();
            if (Players == null) return l;
            for (int i = Scroll; i < Players.Players.Count && l.Count < PlayerRows; i++) l.Add(Players.Players[i]);
            return l;
        }

        /// <summary>The boolean game rules (toggles), in the server's order.</summary>
        public List<KeyValuePair<string, object>> BoolRules()
        {
            var l = new List<KeyValuePair<string, object>>();
            if (World != null) foreach (var r in World.Rules) if (r.Value is bool) l.Add(r);
            return l;
        }

        // ---------- scrolling (right stick on the panel, or the arrows) ----------
        int Page(PanelLayout L) => Tab == OpTab.Players ? PlayerRows : Tab == OpTab.World ? RuleRows : Math.Max(1, L.TextRows - 1);

        /// <summary>Scroll steps: text tabs count lines up from the newest; lists count rows (rules: pairs) down.</summary>
        public int MaxScroll(PanelLayout L)
        {
            switch (Tab)
            {
                case OpTab.Players: return Math.Max(0, (Players?.Players.Count ?? 0) - PlayerRows);
                case OpTab.World: return Math.Max(0, (BoolRules().Count + 1) / 2 - RuleRows);
                case OpTab.Chat: return Math.Max(0, Log.Page(L.TextRows, L.TextChars, 0, _tmpChat) - L.TextRows);
                case OpTab.Console: return Math.Max(0, Console.Page(L.TextRows, L.TextChars, 0, _tmpConsole) - L.TextRows);
                default: return 0;
            }
        }
        readonly List<WristLine> _tmpChat = new List<WristLine>();
        readonly List<ConsoleLine> _tmpConsole = new List<ConsoleLine>();
        PanelLayout _lastLayout;

        /// <summary>Positive = up/back (older lines, earlier rows); text tabs scroll from the bottom.</summary>
        public bool ScrollBy(int steps)
        {
            var L = _lastLayout ?? Layout();
            bool text = Tab == OpTab.Chat || Tab == OpTab.Console;
            int s = Scroll + (text ? steps : -steps);
            s = Math.Max(0, Math.Min(MaxScroll(L), s));
            if (s == Scroll) return false;
            _scroll[Tab] = s; Version++;
            return true;
        }

        /// <summary>Right stick Y on the panel: push to scroll, hold to repeat.</summary>
        public bool Stick(float y, double now)
        {
            float dir = !CapturesRight ? 0 : y > 0.5f ? 1 : y < -0.5f ? -1 : 0;
            if (dir == 0) { _stickDir = 0; return false; }
            if (dir != _stickDir) { _stickDir = dir; _stickNext = now + 0.35; return ScrollBy(dir > 0 ? 1 : -1); }
            if (now < _stickNext) return false;
            _stickNext = now + 0.1;
            return ScrollBy(dir > 0 ? 1 : -1);
        }

        /// <summary>Right stick click on the panel: accept the first suggestion (Tab) in the Console.</summary>
        public bool StickClick(double now) => CapturesRight && Tab == OpTab.Console && Console.AcceptChip(0, now);

        // ---------- layout ----------
        public static float Width => BlockMenuModel.Width;

        public PanelLayout Layout()
        {
            var L = new PanelLayout { Width = Width };
            float inner = Width - 2 * Margin, y = Margin;
            float tw = inner / TabNames.Length;
            for (int t = 0; t < TabNames.Length; t++)
                L.Items.Add(new PanelItem { Part = PanelPart.Tab, Index = t, R = new MenuRect(Margin + t * tw, y, tw, TabH), Label = TabNames[t], Enabled = true, Active = t == (int)Tab });
            y += TabH + Gap;
            switch (Tab)
            {
                case OpTab.Chat:
                    y = TextArea(L, y, inner, ChatRows);
                    L.Field = new MenuRect(Margin, y, inner - 0.05f - Gap, FieldH);
                    Add(L, PanelPart.Send, 0, new MenuRect(Margin + inner - 0.05f, y, 0.05f, FieldH), "Send", ChatOn);
                    y += FieldH + Gap;
                    y = KeyboardBlock(L, y, inner);
                    break;
                case OpTab.Console:
                    y = TextArea(L, y, inner, ConsoleRows);
                    var chips = Console.Chips();
                    float cw = inner / ConsoleModel.ChipCount;
                    for (int i = 0; i < chips.Count; i++) Add(L, PanelPart.Chip, i, new MenuRect(Margin + i * cw, y, cw, FieldH), chips[i], true);
                    y += FieldH + Gap;
                    float hb = 0.024f, atw = 0.05f, tabw = 0.036f;
                    Add(L, PanelPart.HistoryUp, 0, new MenuRect(Margin, y, hb, FieldH), "^", Console.History.Count > 0);
                    Add(L, PanelPart.HistoryDown, 0, new MenuRect(Margin + hb, y, hb, FieldH), "v", Console.History.Count > 0);
                    L.Field = new MenuRect(Margin + 2 * hb + Gap, y, inner - 2 * hb - atw - tabw - 3 * Gap, FieldH);
                    Add(L, PanelPart.Complete, 0, new MenuRect(Margin + inner - atw - tabw - Gap, y, tabw, FieldH), "tab", chips.Count > 0);
                    var at = Add(L, PanelPart.AtToggle, 0, new MenuRect(Margin + inner - atw, y, atw, FieldH), "@ pointer", true);
                    at.Active = Console.AtPointer; L.Items[L.Items.Count - 1] = at;
                    y += FieldH + Gap;
                    int nq = Math.Min(8, Console.Quick.Count);
                    if (nq > 0)
                    {
                        int perRow = Math.Min(4, nq), rows = (nq + perRow - 1) / perRow;
                        float qw = inner / perRow;
                        for (int i = 0; i < nq; i++)
                            Add(L, PanelPart.Quick, i, new MenuRect(Margin + i % perRow * qw, y + i / perRow * ButtonH, qw, ButtonH), Console.Quick[i], CommandsOn);
                        y += rows * ButtonH + Gap;
                    }
                    y = KeyboardBlock(L, y, inner);
                    break;
                case OpTab.Players:
                {
                    var rows = VisiblePlayers();
                    float lw = inner - ScrollW - Gap;
                    for (int i = 0; i < PlayerRows; i++)
                    {
                        string label = i < rows.Count ? PlayerLine(rows[i]) : "";
                        var it = Add(L, PanelPart.PlayerRow, i, new MenuRect(Margin, y + i * RowH, lw, RowH), label, i < rows.Count);
                        it.Active = i < rows.Count && rows[i].Uuid == SelectedPlayer; L.Items[L.Items.Count - 1] = it;
                    }
                    Scrollers(L, y, PlayerRows * RowH);
                    L.Text = new MenuRect(Margin, y, lw, PlayerRows * RowH);
                    y += PlayerRows * RowH + Gap;
                    bool sel = SelectedName() != null, on = CommandsOn && sel;
                    y = ButtonRow(L, y, inner, sel ? SelectedName() : "Select", new[] { ("Here @", BtnTeleport, on && PointerLabel != null), ("Heal", BtnHeal, on), ("Feed", BtnFeed, on), ("Kick", BtnKick, on) });
                    y = ButtonRow(L, y, inner, "Mode", new[] { ("Survival", BtnGameMode, on), ("Creative", BtnGameMode + 1, on), ("Advent.", BtnGameMode + 2, on), ("Spect.", BtnGameMode + 3, on) });
                    if (KickTarget != null)
                    {
                        L.Field = new MenuRect(Margin, y, inner - 0.05f - Gap, FieldH);
                        Add(L, PanelPart.Cancel, 0, new MenuRect(Margin + inner - 0.05f, y, 0.05f, FieldH), "Cancel", true);
                        y += FieldH + Gap;
                        y = KeyboardBlock(L, y, inner);
                    }
                    break;
                }
                case OpTab.World:
                {
                    L.Header = new MenuRect(Margin, y, inner, 2 * LineH);
                    y += 2 * LineH + Gap;
                    bool on = CommandsOn;
                    y = ButtonRow(L, y, inner, "Time", new[] { ("Day", BtnTime, on), ("Noon", BtnTime + 1, on), ("Night", BtnTime + 2, on), ("Midnight", BtnTime + 3, on) });
                    y = ButtonRow(L, y, inner, "Weather", new[] { ("Clear", BtnWeather, on), ("Rain", BtnWeather + 1, on), ("Thunder", BtnWeather + 2, on), ("Bolt @", BtnLightning, on && PointerLabel != null) });
                    bool frozen = World != null && World.Tick.Frozen;
                    y = ButtonRow(L, y, inner, "Tick", new[] { (frozen ? "Unfreeze" : "Freeze", frozen ? BtnUnfreeze : BtnFreeze, on), ("Step 20", BtnStep, on && frozen) });
                    y = ButtonRow(L, y, inner, "Difficulty", new[] { ("Peaceful", BtnDifficulty, on), ("Easy", BtnDifficulty + 1, on), ("Normal", BtnDifficulty + 2, on), ("Hard", BtnDifficulty + 3, on) });
                    var rules = BoolRules();
                    float lw = inner - ScrollW - Gap, rw = lw / 2;
                    for (int i = 0; i < RuleRows * 2; i++)
                    {
                        int k = Scroll * 2 + i;
                        bool has = k < rules.Count;
                        var it = Add(L, PanelPart.Rule, i, new MenuRect(Margin + i % 2 * rw, y + i / 2 * RowH, rw, RowH), has ? rules[k].Key : "", has && on);
                        it.Active = has && (bool)rules[k].Value; L.Items[L.Items.Count - 1] = it;
                    }
                    Scrollers(L, y, RuleRows * RowH);
                    L.Text = new MenuRect(Margin, y, lw, RuleRows * RowH);
                    y += RuleRows * RowH + Gap;
                    break;
                }
                case OpTab.Server:
                    L.Text = new MenuRect(Margin, y, inner, ServerRows * LineH);
                    L.TextRows = ServerRows;
                    y += ServerRows * LineH + Gap;
                    break;
            }
            L.Note = new MenuRect(Margin, y, inner, NoteH);
            y += NoteH;
            L.Height = y + Margin;
            _lastLayout = L;
            return L;
        }

        static PanelItem Add(PanelLayout L, PanelPart part, int index, MenuRect r, string label, bool enabled)
        {
            var it = new PanelItem { Part = part, Index = index, R = r, Label = label, Enabled = enabled };
            L.Items.Add(it);
            return it;
        }

        float TextArea(PanelLayout L, float y, float inner, int rows)
        {
            float lw = inner - ScrollW - Gap;
            L.Text = new MenuRect(Margin, y, lw, rows * LineH);
            L.TextRows = rows;
            L.TextChars = CharsFor(lw);
            Scrollers(L, y, rows * LineH);
            return y + rows * LineH + Gap;
        }

        /// <summary>Characters per text row: the built-in font at the panel's text size is about 2.6 mm per character.</summary>
        public static int CharsFor(float width) => Math.Max(8, (int)(width / 0.0026f));

        static void Scrollers(PanelLayout L, float y, float h)
        {
            float sx = Margin + (Width - 2 * Margin) - ScrollW;
            Add(L, PanelPart.ScrollUp, 0, new MenuRect(sx, y, ScrollW, ScrollW), "", true);
            Add(L, PanelPart.ScrollDown, 0, new MenuRect(sx, y + h - ScrollW, ScrollW, ScrollW), "", true);
        }

        float ButtonRow(PanelLayout L, float y, float inner, string label, (string text, int id, bool on)[] buttons)
        {
            L.Items.Add(new PanelItem { Part = PanelPart.None, Index = -1, R = new MenuRect(Margin, y, LabelW, ButtonH), Label = label });
            float bw = (inner - LabelW) / 4;
            for (int i = 0; i < buttons.Length; i++)
                Add(L, PanelPart.Button, buttons[i].id, new MenuRect(Margin + LabelW + i * bw, y, bw, ButtonH), buttons[i].text, buttons[i].on);
            return y + ButtonH + Gap;
        }

        /// <summary>The keyboard's block: reserved below the field (drawn and hit-tested by the shared KeyboardModel).</summary>
        float KeyboardBlock(PanelLayout L, float y, float inner)
        {
            L.Keyboard = new MenuRect(Margin, y, inner, KeyboardRows * KeyH);
            return y + KeyboardRows * KeyH + Gap;
        }

        public static string PlayerLine(PlayerRow p) =>
            $"{p.Name}  {Short(p.GameMode)}  {p.Health:0.#}/{p.MaxHealth:0}hp  food {p.Food}  {p.Ping} ms{(p.Op ? "  op" : "")}";

        static string Short(string mode) => mode == null ? "?" : mode.Length > 9 ? mode.Substring(0, 9) : mode;

        /// <summary>What a point on the panel (metres from the top-left, y down) hits; Panel for the background, None outside.</summary>
        public static PanelHit HitTest(PanelLayout L, float x, float y)
        {
            if (x < 0 || y < 0 || x >= L.Width || y >= L.Height) return PanelHit.Nothing;
            foreach (var it in L.Items) if (it.Part != PanelPart.None && it.R.Contains(x, y)) return new PanelHit(it.Part, it.Index);
            return new PanelHit(PanelPart.Panel, 0);
        }

        // ---------- texts ----------
        /// <summary>The Server tab: TPS, MSPT, memory, players, tick state, one line per world.</summary>
        public static string StatusText(ServerStatus s)
        {
            if (s == null) return "waiting for server_status...";
            var sb = new StringBuilder();
            sb.Append($"TPS {s.Tps[0]:0.0} / {s.Tps[1]:0.0} / {s.Tps[2]:0.0}   MSPT {s.Mspt:0.0}\n");
            sb.Append($"Memory {s.UsedMb} / {s.MaxMb} MB   Players {s.Online} / {s.MaxPlayers}\n");
            sb.Append($"Tick {s.Tick.Label}\n");
            foreach (var w in s.Worlds)
                sb.Append($"{w.Name} ({w.Env}): {w.Chunks} chunks, {w.Entities} entities, {w.Players} players, {Clock(w.Time)} day {w.Day}, {w.Weather}\n");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The World tab's header.</summary>
        public static string WorldText(WorldInfo w)
        {
            if (w == null) return "waiting for world_info...";
            return $"{w.World} ({w.Env})   {Clock(w.Time)}, day {w.Day}   {w.Weather}   {w.Difficulty}\ntick {w.Tick.Label}   spawn {w.Spawn.x} {w.Spawn.y} {w.Spawn.z}   {w.Rules.Count} game rules";
        }

        /// <summary>Game time (0 = 6:00) as a clock: 6000 is 12:00.</summary>
        public static string Clock(long ticks)
        {
            long t = ((ticks % 24000) + 24000) % 24000;
            long minutes = (t * 60 / 1000 + 6 * 60) % (24 * 60);
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }
    }
}
