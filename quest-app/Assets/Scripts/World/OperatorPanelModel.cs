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
//   [operator] Marks  where the view is, the worlds (switch keeps the width, lands on the spawn), Spawn, jump to typed
//            coordinates, the shared bookmarks (go, add here, delete), and who is followed (Stop). Players also gets
//            Follow / Go to for the selected player (World/FollowState.cs).
//   [operator] Two modes replace the tab's content while they are open (the tabs stay; clicking one leaves the mode):
//            the container view (World/ContainerModel.cs) and the sign editor (World/SignEditorModel.cs), opened by the
//            left trigger on a container or a sign (BlockEditMode's hooks).
//
// Every button that runs a command is a template on top of the console path (World/OperatorCommands below); without
// command_v1 they are greyed and say "needs auth.token". The focused field (chat line, command line or kick reason)
// is typed on the shared on-panel keyboard (World/KeyboardModel.cs: shift, ?123, delete, clear, enter); Enter sends.
// [operator] Messages the Marks tab, the container view and the sign editor want sent go into Outbox (the XR panel sends
// them); jumps and follow requests come back as PanelActions for the app.
using System;
using System.Collections.Generic;
using System.Text;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum OpTab { Chat, Console, Players, World, Server, Marks }

    public enum PanelPart { None, Panel, Tab, ScrollUp, ScrollDown, Send, Chip, Quick, HistoryUp, HistoryDown, AtToggle, Complete, PlayerRow, Button, Rule, Key, Cancel,
                            Field, MarkRow, Slot, Swatch, SignLine }   // [operator]

    /// <summary>[operator] What the panel shows under the tabs: a tab, or the container view / sign editor.</summary>
    public enum PanelMode { Tabs, Container, Sign }

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

    public enum PanelActionKind { None, Tab, Command, Chat, Scrolled, Selected, Toggled, Refused, Typed,
                                  Jump, Follow, Sent }   // [operator]

    public struct PanelAction
    {
        public PanelActionKind Kind;
        /// <summary>Command: the line; Chat: the text; [operator] Jump: the world; Follow: "follow", "goto" or "stop".</summary>
        public string Line;
        /// <summary>Command: run at the pointer (command "at", PROTOCOL.md 4.27).</summary>
        public bool AtPointer;
        /// <summary>[operator] Jump: the target (world block coordinates; the block's centre).</summary>
        public Vector3 Pos;
        /// <summary>[operator] Follow: the player.</summary>
        public string Name, Uuid;
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
        public const int ChatRows = 11, ConsoleRows = 8, PlayerRows = 6, RuleRows = 6, ServerRows = 15, KeyboardRows = KeyboardModel.Rows;
        public const int MarkRows = 6, MarkRowsTyping = 2;   // [operator] bookmark rows (fewer while the keyboard shows)
        public const float SlotSize = 0.030f, SwatchSize = 0.016f;   // [operator] container slots (as the block menu's cells), sign colours
        public const double HoldSeconds = 0.6, QueryEvery = 1.0;
        public static readonly string[] TabNames = { "Chat", "Console", "Players", "World", "Server", "Marks" };

        public bool Open { get; private set; }
        public OpTab Tab { get; private set; } = OpTab.Chat;
        // [operator] Marks tab, container view, sign editor, and what they want sent
        public PanelMode Mode { get; private set; } = PanelMode.Tabs;
        public readonly MarksModel Marks = new MarksModel();
        public readonly ContainerModel Container = new ContainerModel();
        public readonly SignEditorModel Sign = new SignEditorModel();
        public readonly List<Dictionary<string, object>> Outbox = new List<Dictionary<string, object>>();
        /// <summary>The hotbar's selected block or item (Put in the container view), set by the app.</summary>
        public string HotbarSelection;
        public BlockRegistry Registry;
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
        /// <summary>The on-panel keyboards (World/KeyboardModel.cs), one per field so each keeps its shift/?123 mode.</summary>
        public readonly KeyboardModel ChatKeys = new KeyboardModel { MaxLength = ControlProtocol.MaxLen, EnterLabel = "send" };
        public readonly KeyboardModel ConsoleKeys = new KeyboardModel { MaxLength = ControlProtocol.MaxLen, EnterLabel = "run" };
        public readonly KeyboardModel KickKeys = new KeyboardModel { MaxLength = 100, EnterLabel = "kick" };
        /// <summary>The keyboard of the focused field, or null (no field on this tab).</summary>
        public KeyboardModel ActiveKeys => Focus == Field.Chat ? ChatKeys : Focus == Field.Console ? ConsoleKeys : Focus == Field.Kick ? KickKeys
                                         : Focus == Field.Marks ? Marks.ActiveKeys : Focus == Field.Sign ? Sign.Keys : null;

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
        public enum Field { None, Chat, Console, Kick, Marks, Sign }
        public Field Focus => Mode == PanelMode.Sign ? Field.Sign : Mode == PanelMode.Container ? Field.None
                            : Tab == OpTab.Chat ? Field.Chat : Tab == OpTab.Console ? Field.Console : Tab == OpTab.Players && KickTarget != null ? Field.Kick
                            : Tab == OpTab.Marks && Marks.Typing ? Field.Marks : Field.None;
        public string FocusText => Focus == Field.Chat ? ChatInput : Focus == Field.Console ? Console.Input : Focus == Field.Kick ? KickReason
                                 : Focus == Field.Marks ? Marks.FieldText : Focus == Field.Sign ? Sign.Current.Lines[Sign.Line] : "";
        public int FocusMaxLen => Focus == Field.Chat ? Welcome.ChatMaxLen : Focus == Field.Console ? Welcome.CommandMaxLen : Focus == Field.Sign ? SignEditorModel.MaxLine
                                : Focus == Field.Marks ? (Marks.Mode == MarksModel.FieldMode.Name ? 32 : 40) : 200;

        public void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (!open) { PointerOnPanel = false; Hover = PanelHit.Nothing; KickTarget = null; LeaveMode(); }
            _nextQuery = 0;
            Version++;
        }

        public bool SetTab(OpTab t)
        {
            bool left = LeaveMode();
            if (t == Tab && !left) return false;
            Tab = t; KickTarget = null; _nextQuery = 0; Note = "";
            ActiveKeys?.ResetMode();
            Version++;
            return true;
        }

        // ---------- [operator] the container view and the sign editor ----------

        /// <summary>The left trigger on a container: the panel opens on its view (container_query in the Outbox).</summary>
        public void OpenContainer(Vector3Int pos)
        {
            LeaveMode();
            SetOpen(true);
            Mode = PanelMode.Container; Note = "";
            Outbox.Add(Container.Begin(pos));
            Version++;
        }

        /// <summary>The left trigger on a sign: the panel opens on the sign editor, filled with the sign's text.</summary>
        public void OpenSign(Vector3Int pos, string state, BlockEntityData data)
        {
            LeaveMode();
            SetOpen(true);
            Mode = PanelMode.Sign; Note = "";
            Sign.Begin(pos, state, data);
            Version++;
        }

        /// <summary>Back to the tabs: an open container view ends (container_close in the Outbox). True when a mode was left.</summary>
        public bool LeaveMode()
        {
            if (Mode == PanelMode.Tabs) return false;
            if (Mode == PanelMode.Container) { var m = Container.Close(); if (m != null) Outbox.Add(m); }
            if (Mode == PanelMode.Sign) Sign.Close();
            Mode = PanelMode.Tabs;
            Version++;
            return true;
        }

        /// <summary>A container message: the open view updates; the view ending by itself returns to the tabs.</summary>
        public bool OnContainer(ContainerView v)
        {
            if (!Container.OnView(v)) return false;
            if (!Container.Open && Mode == PanelMode.Container) { Mode = PanelMode.Tabs; Note = Container.Note; }
            Version++;
            return true;
        }

        /// <summary>[operator] An error about a container, sign or bookmark message: the note line says it. True when it was ours.</summary>
        public bool OnOperatorError(string op, Vector3Int? pos, string message)
        {
            if (op == "bookmark") { Note = "bookmark: " + message; Version++; return true; }
            if (Container.OnError(op, pos, message))
            {
                if (!Container.Open && Mode == PanelMode.Container) Mode = PanelMode.Tabs;
                Note = message; Version++;
                return true;
            }
            if (Sign.OnError(op, pos, message)) { Note = message; Version++; return true; }
            return false;
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

        // ---------- typing ----------
        /// <summary>
        /// A key of the panel keyboard: the focused field's text goes through KeyboardModel.Press (shift, ?123, delete,
        /// clear), and enter sends what the field holds (Enter).
        /// </summary>
        public PanelAction Key(int key, double now)
        {
            if (Focus == Field.Sign) { Version++; return Sign.Key(key) == KeyResult.None ? default : new PanelAction { Kind = PanelActionKind.Typed }; }   // [operator]
            var kb = ActiveKeys;
            if (kb == null) return default;
            kb.MaxLength = FocusMaxLen;
            kb.SetText(FocusText);
            var r = kb.Press(key);
            switch (r)
            {
                case KeyResult.Enter: return Enter(now);
                case KeyResult.Text: Type(kb.Text, now); return new PanelAction { Kind = PanelActionKind.Typed };
                case KeyResult.Mode: Version++; return new PanelAction { Kind = PanelActionKind.Typed };
                default: return default;
            }
        }

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
                case Field.Marks: Marks.ActiveKeys?.SetText(text); break;   // [operator]
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
                case Field.Marks: return MarksEnter();   // [operator]
            }
            return default;
        }

        // ---------- [operator] Marks tab ----------

        /// <summary>Enter on the Marks field: jump to the typed coordinates, or save the new bookmark under the typed name.</summary>
        PanelAction MarksEnter()
        {
            if (!Marks.Loaded) return Refuse("no area yet");
            if (Marks.Mode == MarksModel.FieldMode.Name)
            {
                string name = Marks.NameKeys.Text.Trim();
                if (name.Length == 0) return Refuse("type a name for the bookmark");
                if (!Marks.Welcome.Marks) return Refuse("bookmarks are off on this server (marks_v1)");
                if (!Marks.Welcome.MarksEdit) return Refuse("bookmarks are read-only on this server (marks.edit)");
                Outbox.Add(OperatorProtocol.BookmarkAdd(name, Marks.World, Marks.Here));
                Marks.Selected = name; Marks.NameKeys.SetText(""); Marks.SetTyping(false, MarksModel.FieldMode.Coords);
                Note = $"bookmark \"{name}\" at {Marks.Here.x} {Marks.Here.y} {Marks.Here.z}"; Version++;
                return new PanelAction { Kind = PanelActionKind.Sent };
            }
            string why = Jumps.ParseCoords(Marks.CoordKeys.Text, Marks.View, out var pos, out bool hasY);
            if (why != null) return Refuse(why);
            Marks.SetTyping(false, MarksModel.FieldMode.Coords);
            return Jump(Marks.World, pos, $"{Math.Floor(pos.x)} {(hasY ? Math.Floor(pos.y) + " " : "")}{Math.Floor(pos.z)}");
        }

        PanelAction Jump(string world, Vector3 pos, string what)
        {
            if (!Connected) return Refuse("not connected");
            if (!Marks.Loaded) return Refuse("no area yet");
            Note = "jump to " + what; Version++;
            return new PanelAction { Kind = PanelActionKind.Jump, Line = world, Pos = pos };
        }

        /// <summary>The world buttons shown (at most 4: the first worlds the server lists).</summary>
        public List<WorldEntry> ShownWorlds()
        {
            var l = new List<WorldEntry>();
            foreach (var w in Marks.Worlds) { if (l.Count == 4) break; l.Add(w); }
            return l;
        }

        public List<Bookmark> VisibleMarks(int rows)
        {
            var l = new List<Bookmark>();
            for (int i = Scroll; i < Marks.Bookmarks.Marks.Count && l.Count < rows; i++) l.Add(Marks.Bookmarks.Marks[i]);
            return l;
        }

        int MarkRowCount => Marks.Typing ? MarkRowsTyping : MarkRows;

        PanelAction MarksButton(int id)
        {
            if (id >= BtnWorld && id < BtnWorld + 4)
            {
                var ws = ShownWorlds();
                if (id - BtnWorld >= ws.Count) return default;
                var w = ws[id - BtnWorld];
                return Jump(w.Name, new Vector3(w.Spawn.x + 0.5f, w.Spawn.y, w.Spawn.z + 0.5f), w.Label + " spawn");
            }
            switch (id)
            {
                case BtnGoCoords: return MarksEnter();
                case BtnSpawn:
                {
                    var w = Marks.WorldOf(Marks.World);
                    if (!w.HasValue) return Refuse("the server didn't list this world's spawn");
                    return Jump(w.Value.Name, new Vector3(w.Value.Spawn.x + 0.5f, w.Value.Spawn.y, w.Value.Spawn.z + 0.5f), "spawn");
                }
                case BtnAddMark:
                    if (!Marks.Welcome.Marks) return Refuse("bookmarks are off on this server (marks_v1)");
                    if (!Marks.Welcome.MarksEdit) return Refuse("bookmarks are read-only on this server (marks.edit)");
                    Marks.NameKeys.SetText(Marks.NextName());
                    Marks.SetTyping(true, MarksModel.FieldMode.Name);
                    Note = "name the bookmark, then save (enter)"; Version++;
                    return new PanelAction { Kind = PanelActionKind.Toggled };
                case BtnStopFollow:
                    if (!Marks.Following) return Refuse("not following anyone");
                    return new PanelAction { Kind = PanelActionKind.Follow, Line = "stop" };
                case BtnGoMark:
                case BtnDeleteMark:
                {
                    var b = Marks.Selected != null ? Marks.Find(Marks.Selected) : null;
                    if (!b.HasValue) return Refuse("select a bookmark first");
                    if (id == BtnGoMark)
                    {
                        if (!Marks.WorldOf(b.Value.World).HasValue && Marks.Worlds.Count > 0) return Refuse("the server has no world " + b.Value.World);
                        return Jump(b.Value.World, new Vector3(b.Value.Pos.x + 0.5f, b.Value.Pos.y + 0.5f, b.Value.Pos.z + 0.5f), "\"" + b.Value.Name + "\"");
                    }
                    if (!Marks.Welcome.MarksEdit) return Refuse("bookmarks are read-only on this server (marks.edit)");
                    Outbox.Add(OperatorProtocol.BookmarkRemove(b.Value.Name));
                    Note = "deleted \"" + b.Value.Name + "\""; Marks.Selected = null; Version++;
                    return new PanelAction { Kind = PanelActionKind.Sent };
                }
            }
            return default;
        }

        // ---------- [operator] the container view's and sign editor's buttons ----------

        PanelAction ContainerButton(int id)
        {
            Dictionary<string, object> m = null; string why = null;
            switch (id)
            {
                case BtnCount: case BtnCount + 1: case BtnCount + 2: Container.PickCount(ContainerModel.Counts[id - BtnCount]); return new PanelAction { Kind = PanelActionKind.Toggled };
                case BtnTake: m = Container.Take(out why); break;
                case BtnPut: m = Container.Put(ContainerModel.ItemOf(HotbarSelection, Registry), out why); break;
                case BtnClear: m = Container.Clear(out why); break;
                case BtnSort: m = Container.Sort(out why); break;
                case BtnClose: LeaveMode(); return new PanelAction { Kind = PanelActionKind.Tab };
                default: return default;
            }
            if (m == null) return Refuse(why ?? "not available");
            Outbox.Add(m); Note = Container.Note; Version++;
            return new PanelAction { Kind = PanelActionKind.Sent };
        }

        PanelAction SignButton(int id)
        {
            switch (id)
            {
                case BtnFront: Sign.SetSide(false); return new PanelAction { Kind = PanelActionKind.Toggled };
                case BtnBack: Sign.SetSide(true); return new PanelAction { Kind = PanelActionKind.Toggled };
                case BtnGlow: Sign.ToggleGlow(); return new PanelAction { Kind = PanelActionKind.Toggled };
                case BtnClose: LeaveMode(); return new PanelAction { Kind = PanelActionKind.Tab };
                case BtnSave:
                {
                    if (!Connected) return Refuse("not connected");
                    var l = Sign.Save();
                    if (l.Count == 0) return Refuse(Sign.Note);
                    Outbox.AddRange(l); Note = Sign.Note; Version++;
                    return new PanelAction { Kind = PanelActionKind.Sent };
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
            ChatKeys.MaxLength = Welcome.ChatMaxLen; ConsoleKeys.MaxLength = Welcome.CommandMaxLen;
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
                case PanelPart.Key: return Key(item.Index, now);
                case PanelPart.Cancel:
                    if (Tab == OpTab.Marks) { Marks.SetTyping(false, MarksModel.FieldMode.Coords); Version++; return new PanelAction { Kind = PanelActionKind.Toggled }; }   // [operator]
                    KickTarget = null; KickReason = ""; Version++; return new PanelAction { Kind = PanelActionKind.Toggled };
                // [operator] Marks: the field shows or hides the keyboard (coordinates), a row selects a bookmark
                case PanelPart.Field:
                    Marks.SetTyping(!(Marks.Typing && Marks.Mode == MarksModel.FieldMode.Coords), MarksModel.FieldMode.Coords); Version++;
                    return new PanelAction { Kind = PanelActionKind.Toggled };
                case PanelPart.MarkRow:
                {
                    var rows = VisibleMarks(MarkRowCount);
                    if (item.Index >= rows.Count) return none;
                    Marks.Selected = string.Equals(rows[item.Index].Name, Marks.Selected, StringComparison.OrdinalIgnoreCase) ? null : rows[item.Index].Name;
                    Version++;
                    return new PanelAction { Kind = PanelActionKind.Selected };
                }
                // [operator] the container view and the sign editor
                case PanelPart.Slot: Container.Select(item.Index); Note = ""; Version++; return new PanelAction { Kind = PanelActionKind.Selected };
                case PanelPart.Swatch: Sign.SetColor(OperatorProtocol.Dyes[item.Index]); Version++; return new PanelAction { Kind = PanelActionKind.Toggled };
                case PanelPart.SignLine: Sign.SelectLine(item.Index); Version++; return new PanelAction { Kind = PanelActionKind.Selected };
                case PanelPart.Button:
                    if (Mode == PanelMode.Container) return ContainerButton(item.Index);
                    if (Mode == PanelMode.Sign) return SignButton(item.Index);
                    if (Tab == OpTab.Marks) return MarksButton(item.Index);
                    return Button(item.Index);
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
            // [operator] the Marks tab, follow buttons, the container view and the sign editor say their own reasons
            if (item.Part == PanelPart.Button && (Mode != PanelMode.Tabs || Tab == OpTab.Marks || item.Index == BtnFollow || item.Index == BtnGoTo))
                return !Connected ? "not connected" : Mode == PanelMode.Container ? (Container.View == null ? "waiting for the server..." : !Container.CanEdit ? "containers are view-only on this server (interact.containers)" : "select a slot first")
                     : Tab == OpTab.Players ? "select a player first" : !Marks.Loaded ? "no area yet"
                     : item.Index == BtnAddMark ? (!Marks.Welcome.Marks ? "bookmarks are off on this server (marks_v1)" : "bookmarks are read-only on this server (marks.edit)")
                     : item.Index == BtnGoMark || item.Index == BtnDeleteMark ? (item.Index == BtnDeleteMark && !Marks.Welcome.MarksEdit ? "bookmarks are read-only on this server (marks.edit)" : "select a bookmark first")
                     : item.Index == BtnStopFollow ? "not following anyone" : "not available";
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
        // [operator] Players 48 follow / stop, 49 go to; Marks 60-63 worlds, 70 spawn, 71 add mark, 72 stop following, 73 go to
        // the bookmark, 74 delete it; container 80 take, 81 put, 82 clear, 83 sort, 84-86 count 1/16/64; sign 91 front,
        // 92 back, 93 glow, 94 save; 90 close (container and sign).
        public const int BtnFollow = 48, BtnGoTo = 49, BtnWorld = 60, BtnSpawn = 70, BtnAddMark = 71, BtnStopFollow = 72, BtnGoMark = 73, BtnDeleteMark = 74,
                         BtnTake = 80, BtnPut = 81, BtnClear = 82, BtnSort = 83, BtnCount = 84, BtnClose = 90, BtnFront = 91, BtnBack = 92, BtnGlow = 93, BtnSave = 94;

        PanelAction Button(int id)
        {
            string p = SelectedName();
            if (id == BtnFollow || id == BtnGoTo)   // [operator] client-side: needs no command_v1
            {
                if (p == null) return Refuse("select a player first");
                string uuid = SelectedPlayer;
                if (id == BtnFollow && Marks.FollowName == p) return new PanelAction { Kind = PanelActionKind.Follow, Line = "stop" };
                Note = (id == BtnFollow ? "following " : "going to ") + p; Version++;
                return new PanelAction { Kind = PanelActionKind.Follow, Line = id == BtnFollow ? "follow" : "goto", Name = p, Uuid = uuid };
            }
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
                KickTarget = p; KickReason = ""; KickKeys.ResetMode(); Note = "kick " + p + ": type a reason, enter kicks"; Version++;
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
        int Page(PanelLayout L) => Tab == OpTab.Players ? PlayerRows : Tab == OpTab.World ? RuleRows : Tab == OpTab.Marks ? MarkRowCount : Math.Max(1, L.TextRows - 1);

        /// <summary>Scroll steps: text tabs count lines up from the newest; lists count rows (rules: pairs) down.</summary>
        public int MaxScroll(PanelLayout L)
        {
            if (Mode != PanelMode.Tabs) return 0;   // [operator] the container view and the sign editor fit without scrolling
            switch (Tab)
            {
                case OpTab.Marks: return Math.Max(0, Marks.Bookmarks.Marks.Count - MarkRowCount);   // [operator]
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
                L.Items.Add(new PanelItem { Part = PanelPart.Tab, Index = t, R = new MenuRect(Margin + t * tw, y, tw, TabH), Label = TabNames[t], Enabled = true,
                                            Active = t == (int)Tab && Mode == PanelMode.Tabs });
            y += TabH + Gap;
            // [operator] the container view or the sign editor in place of the tab's content
            if (Mode == PanelMode.Container) y = ContainerLayout(L, y, inner);
            else if (Mode == PanelMode.Sign) y = SignLayout(L, y, inner);
            else switch (Tab)
            {
                case OpTab.Marks: y = MarksLayout(L, y, inner); break;   // [operator]
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
                    // [operator] the view follows the player, or jumps to them once (client-side: no command_v1 needed)
                    if (KickTarget == null)
                    {
                        bool view = sel && Connected;
                        bool following = sel && Marks.FollowName == SelectedName();
                        y = ButtonRow(L, y, inner, "View", new[] { (following ? "Stop" : "Follow", BtnFollow, view), ("Go to", BtnGoTo, view) });
                    }
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

        /// <summary>The focused field's keyboard below it (KeyboardModel's 4 rows as Key items).</summary>
        float KeyboardBlock(PanelLayout L, float y, float inner)
        {
            L.Keyboard = new MenuRect(Margin, y, inner, KeyboardRows * KeyH);
            var kb = ActiveKeys;
            if (kb != null)
                foreach (var k in kb.Layout(Margin, y, inner, KeyH))
                    L.Items.Add(new PanelItem { Part = PanelPart.Key, Index = k.Index, R = k.R, Label = k.Label, Enabled = true,
                                                Active = k.Index == KeyboardModel.KeyShift && kb.Shift != ShiftState.Off });
            return y + KeyboardRows * KeyH + Gap;
        }

        /// <summary>[operator] A label and up to 4 buttons, each with its own lit (Active) state.</summary>
        float Row(PanelLayout L, float y, float inner, string label, (string text, int id, bool on, bool active)[] buttons)
        {
            L.Items.Add(new PanelItem { Part = PanelPart.None, Index = -1, R = new MenuRect(Margin, y, LabelW, ButtonH), Label = label });
            float bw = (inner - LabelW) / 4;
            for (int i = 0; i < buttons.Length && i < 4; i++)
                L.Items.Add(new PanelItem { Part = PanelPart.Button, Index = buttons[i].id, R = new MenuRect(Margin + LabelW + i * bw, y, bw, ButtonH), Label = buttons[i].text,
                                            Enabled = buttons[i].on, Active = buttons[i].active });
            return y + ButtonH + Gap;
        }

        public const int BtnGoCoords = 75;

        /// <summary>[operator] The Marks tab: where the view is, worlds, spawn / add / stop, the coordinates field, bookmarks.</summary>
        float MarksLayout(PanelLayout L, float y, float inner)
        {
            L.Header = new MenuRect(Margin, y, inner, LineH);
            y += LineH + Gap;
            bool on = Connected && Marks.Loaded;
            var ws = ShownWorlds();
            if (ws.Count > 0)
            {
                var wb = new (string, int, bool, bool)[ws.Count];
                for (int i = 0; i < ws.Count; i++) wb[i] = (ws[i].Label, BtnWorld + i, on, ws[i].Name == Marks.World);
                y = Row(L, y, inner, "World", wb);
            }
            bool marks = Marks.Welcome.Marks && Marks.Welcome.MarksEdit;
            y = Row(L, y, inner, "Here", new[] { ("Spawn", BtnSpawn, on && Marks.WorldOf(Marks.World).HasValue, false), ("Add mark", BtnAddMark, on && marks, Marks.Typing && Marks.Mode == MarksModel.FieldMode.Name),
                                                  ("Stop follow", BtnStopFollow, Marks.Following, false) });
            float bw = 0.05f;
            var f = Add(L, PanelPart.Field, 0, new MenuRect(Margin, y, inner - 2 * bw - 2 * Gap, FieldH), MarksFieldLabel(), true);
            f.Active = Marks.Typing; L.Items[L.Items.Count - 1] = f;
            Add(L, PanelPart.Button, BtnGoCoords, new MenuRect(Margin + inner - 2 * bw - Gap, y, bw, FieldH), Marks.Mode == MarksModel.FieldMode.Name ? "Save" : "Go", on);
            Add(L, PanelPart.Cancel, 0, new MenuRect(Margin + inner - bw, y, bw, FieldH), "Cancel", Marks.Typing);
            y += FieldH + Gap;
            int rows = MarkRowCount;
            var vis = VisibleMarks(rows);
            float lw = inner - ScrollW - Gap;
            for (int i = 0; i < rows; i++)
            {
                string label = i < vis.Count ? Marks.Row(vis[i]) : "";
                var it = Add(L, PanelPart.MarkRow, i, new MenuRect(Margin, y + i * RowH, lw, RowH), label, i < vis.Count);
                it.Active = i < vis.Count && string.Equals(vis[i].Name, Marks.Selected, StringComparison.OrdinalIgnoreCase); L.Items[L.Items.Count - 1] = it;
            }
            Scrollers(L, y, rows * RowH);
            L.Text = new MenuRect(Margin, y, lw, rows * RowH);
            y += rows * RowH + Gap;
            bool sel = Marks.Selected != null && Marks.Find(Marks.Selected).HasValue;
            y = Row(L, y, inner, "Mark", new[] { ("Go", BtnGoMark, on && sel, false), ("Delete", BtnDeleteMark, on && sel && marks, false) });
            if (Marks.Typing) y = KeyboardBlock(L, y, inner);
            return y;
        }

        /// <summary>[operator] What the Marks field shows: the typed text with a cursor, or what to type.</summary>
        public string MarksFieldLabel()
        {
            string t = Marks.FieldText;
            if (Marks.Mode == MarksModel.FieldMode.Name) return "name: " + t + (Marks.Typing ? "_" : "");
            if (t.Length == 0 && !Marks.Typing) return "x z or x y z (click to type)";
            return "go to: " + t + (Marks.Typing ? "_" : "");
        }

        /// <summary>[operator] The container view: title, the slot grid, the count picker, Take / Put / Clear / Sort / Close.</summary>
        float ContainerLayout(PanelLayout L, float y, float inner)
        {
            L.Header = new MenuRect(Margin, y, inner, LineH);
            y += LineH + Gap;
            var v = Container.View;
            if (v == null)
            {
                L.Text = new MenuRect(Margin, y, inner, 3 * RowH);
                y += 3 * RowH + Gap;
            }
            else
            {
                var (cols, rows) = ContainerModel.Grid(v.Block, v.Size);
                float x0 = Margin + (inner - cols * SlotSize) / 2;
                for (int i = 0; i < v.Size && i < cols * rows; i++)
                {
                    var (item, n) = v.Slot(i);
                    var it = Add(L, PanelPart.Slot, i, new MenuRect(x0 + i % cols * SlotSize, y + i / cols * SlotSize, SlotSize, SlotSize), item == null ? "" : n > 1 ? n.ToString() : "", true);
                    it.Active = i == Container.Selected; L.Items[L.Items.Count - 1] = it;
                }
                L.Text = new MenuRect(x0, y, cols * SlotSize, rows * SlotSize);
                y += rows * SlotSize + Gap;
            }
            bool edit = Connected && Container.CanEdit, sel = edit && Container.Selected >= 0;
            var (selItem, _) = v != null && Container.Selected >= 0 ? v.Slot(Container.Selected) : (null, 0);
            y = Row(L, y, inner, "Count", new[] { ("1", BtnCount, edit, Container.Count == 1), ("16", BtnCount + 1, edit, Container.Count == 16), ("64", BtnCount + 2, edit, Container.Count == 64),
                                                   ("Sort", BtnSort, edit && v != null && ContainerModel.Sortable(v.Block), false) });
            y = Row(L, y, inner, "Slot", new[] { ("Take", BtnTake, sel && selItem != null, false), ("Put", BtnPut, sel && HotbarSelection != null, false), ("Clear", BtnClear, sel && selItem != null, false),
                                                  ("Close", BtnClose, true, false) });
            return y;
        }

        /// <summary>[operator] The sign editor: side, glow, save; four lines; sixteen dye swatches; the keyboard.</summary>
        float SignLayout(PanelLayout L, float y, float inner)
        {
            L.Header = new MenuRect(Margin, y, inner, LineH);
            y += LineH + Gap;
            bool on = Connected;
            y = Row(L, y, inner, Sign.Hanging ? "Hanging" : "Sign", new[] { ("Front", BtnFront, true, !Sign.OnBack), ("Back", BtnBack, true, Sign.OnBack),
                                                                           ("Glow", BtnGlow, true, Sign.Current.Glowing), ("Save", BtnSave, on && Sign.Changed, false) });
            for (int i = 0; i < 4; i++)
            {
                var it = Add(L, PanelPart.SignLine, i, new MenuRect(Margin, y + i * RowH, inner, RowH), Sign.Current.Lines[i] + (i == Sign.Line ? "_" : ""), true);
                it.Active = i == Sign.Line; L.Items[L.Items.Count - 1] = it;
            }
            y += 4 * RowH + Gap;
            float sw = inner / OperatorProtocol.Dyes.Length;
            for (int i = 0; i < OperatorProtocol.Dyes.Length; i++)
            {
                var it = Add(L, PanelPart.Swatch, i, new MenuRect(Margin + i * sw, y, sw, SwatchSize), OperatorProtocol.Dyes[i], true);
                it.Active = OperatorProtocol.Dyes[i] == Sign.Current.Color; L.Items[L.Items.Count - 1] = it;
            }
            y += SwatchSize + Gap;
            y = Row(L, y, inner, "", new[] { ("Close", BtnClose, true, false) });
            y = KeyboardBlock(L, y, inner);
            return y;
        }

        /// <summary>[operator] The header line of the container view / sign editor / Marks tab.</summary>
        public string ModeHeader()
        {
            if (Mode == PanelMode.Container)
                return (Container.Title.Length > 0 ? Container.Title : "Container") + $"  {Container.Pos.x} {Container.Pos.y} {Container.Pos.z}   " + Container.SlotLine();
            if (Mode == PanelMode.Sign)
                return $"{(Sign.OnBack ? "Back" : "Front")} of the sign at {Sign.Pos.x} {Sign.Pos.y} {Sign.Pos.z}   colour {Sign.Current.Color.Replace('_', ' ')}{(Sign.Current.Glowing ? ", glowing" : "")}";
            return Marks.Header();
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
