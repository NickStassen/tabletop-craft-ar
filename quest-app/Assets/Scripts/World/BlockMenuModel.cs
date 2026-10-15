// The block menu's state, layout and input rules, engine-free (Tests~). XR/BlockMenu.cs draws it on a panel beside
// the left hand and feeds it what the right controller's ray hits; this file decides what a hover, a trigger pull, a
// stick push, the right stick click or a B pick does.
//
//   Tabs: the server's categories (block_registry), [hands] Use and Eggs (item_registry: usable items and spawn eggs,
//         when the server grants interact_v1; with the name tag selected the Use tab shows its text field and keyboard,
//         like naming one at an anvil), Search (on-panel keyboard, World/KeyboardModel.cs; blocks and items), Info
//         (status, "Show HUD", "Reset view", and the Server field: type an address or pick a server found on the LAN).
//   Grid: 9 columns, scrolled by rows (right stick or the arrows while the ray is on the panel).
//   Hotbar: 9 recent picks, most recent first, one per block (picking oak stairs again moves them to the front).
//   Area row (optional): 5 area sizes, shown only when the app wires the area hooks.
//
// Layout units are metres in panel space: origin at the top-left corner, x to the right, y down.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public enum MenuPart { None, Panel, Tab, Cell, ScrollUp, ScrollDown, Hotbar, Key, AreaStep, HudToggle,
                           ResetView, ServerField, ServerFind, ServerConnect, ServerCancel, FoundServer }

    public struct MenuHit
    {
        public MenuPart Part; public int Index;
        public MenuHit(MenuPart p, int i) { Part = p; Index = i; }
        public static readonly MenuHit Nothing = new MenuHit(MenuPart.None, 0);
        public bool Same(MenuHit o) => Part == o.Part && Index == o.Index;
        public override string ToString() => Part + (Part == MenuPart.None || Part == MenuPart.Panel ? "" : "#" + Index);
    }

    public struct MenuRect
    {
        public float X, Y, W, H;
        public MenuRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
        public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;
        public float CX => X + W / 2; public float CY => Y + H / 2;
    }

    /// <summary>One clickable or labelled box of the panel.</summary>
    public struct MenuItem { public MenuPart Part; public int Index; public MenuRect R; public string Label; }

    public enum MenuActionKind { None, Select, Tab, Typed, Scrolled, HotbarSlot, Area, HudToggle,
                                 ResetView, ServerEdit, ServerFind, ServerConnect, ServerCancel }

    public struct MenuAction
    {
        public MenuActionKind Kind; public string State; public int Value;
        public bool Haptic => Kind != MenuActionKind.None;
    }

    /// <summary>The 9 recent blocks, most recently picked first, one slot per block id.</summary>
    public sealed class Hotbar
    {
        public const int Size = 9;
        readonly string[] _slots = new string[Size];
        public int Selected { get; private set; }
        public string this[int i] => i >= 0 && i < Size ? _slots[i] : null;
        public int Count { get { int n = 0; foreach (var s in _slots) if (s != null) n++; return n; } }
        public string Current => _slots[Selected];

        /// <summary>Moves (or adds) the block to slot 0 and selects it; the same block in another state is replaced.</summary>
        public void Push(string state)
        {
            if (string.IsNullOrEmpty(state)) return;
            string id = BlockRegistry.IdOf(state);
            int at = Array.FindIndex(_slots, s => s != null && BlockRegistry.IdOf(s) == id);
            int last = at >= 0 ? at : Size - 1;
            for (int i = last; i > 0; i--) _slots[i] = _slots[i - 1];
            _slots[0] = state;
            Selected = 0;
        }

        /// <summary>Selects the next filled slot (wrapping) and returns its state, or null when the hotbar is empty.</summary>
        public string Cycle(int dir = 1)
        {
            for (int k = 1; k <= Size; k++)
            {
                int i = ((Selected + dir * k) % Size + Size) % Size;
                if (_slots[i] != null) { Selected = i; return _slots[i]; }
            }
            return null;
        }

        /// <summary>Selects slot i if it holds a block; returns its state or null.</summary>
        public string Choose(int i)
        {
            if (this[i] == null) return null;
            Selected = i;
            return _slots[i];
        }

        /// <summary>A copy of the 9 slots (null = empty), for the app's prefs.json.</summary>
        public string[] Slots() => (string[])_slots.Clone();

        /// <summary>Restores saved slots (prefs.json): empty entries are skipped, one slot per block id is kept.</summary>
        public void Restore(IList<string> slots, int selected)
        {
            if (slots == null) return;
            Array.Clear(_slots, 0, Size);
            var ids = new HashSet<string>();
            int k = 0, sel = 0;
            for (int i = 0; i < slots.Count && k < Size; i++)
            {
                if (string.IsNullOrEmpty(slots[i]) || !ids.Add(BlockRegistry.IdOf(slots[i]))) continue;
                if (i == selected) sel = k;
                _slots[k++] = slots[i];
            }
            Selected = sel;
        }
    }

    public sealed class BlockMenuModel
    {
        // Layout (metres). Readable at arm's length: 30 mm cells, 24 mm tabs and keys.
        public const int Columns = 9, Rows = 5, SearchRows = 3;
        public const float Margin = 0.008f, Gap = 0.004f, TabH = 0.024f, Cell = 0.030f, ScrollW = 0.022f, NameH = 0.018f,
                           HotH = 0.034f, AreaH = 0.024f, KeyH = 0.024f, SearchH = 0.022f, AreaLabelW = 0.05f, ButtonH = 0.024f;
        public const int MaxQuery = 32, MaxServer = 96, MaxFound = 4;
        public const int KeySpace = KeyboardModel.KeySpace, KeyDelete = KeyboardModel.KeyDelete, KeyClear = KeyboardModel.KeyClear,
                         KeyEnter = KeyboardModel.KeyEnter;

        public BlockRegistry Registry { get; private set; }
        /// <summary>[hands] The server's item_registry (null without interact_v1): the Use and Eggs tabs.</summary>
        public ItemRegistry UseItems { get; private set; }
        /// <summary>[hands] The name tag's text (typed on the Use tab while the name tag is selected).</summary>
        public readonly KeyboardModel NameTagKeys = new KeyboardModel { MaxLength = 64, CollapseSpaces = true, EnterLabel = "done" };
        public string NameTag => NameTagKeys.Text.Trim();
        public bool Open { get; private set; }
        public int Tab { get; private set; }
        /// <summary>The Search tab's keyboard; its text is the query. Enter picks the first result.</summary>
        public readonly KeyboardModel SearchKeys = new KeyboardModel { MaxLength = MaxQuery, CollapseSpaces = true, EnterLabel = "pick" };
        public string Query => SearchKeys.Text;
        /// <summary>The Info tab's Server field: the address being typed. Enter connects.</summary>
        public readonly KeyboardModel ServerKeys = new KeyboardModel { MaxLength = MaxServer, EnterLabel = "connect" };
        /// <summary>The server the app is set to (shown on the Info tab's Server button).</summary>
        public string ServerUrl = "";
        /// <summary>The Server field is open on the Info tab (keyboard, found servers, Find / Connect / Cancel).</summary>
        public bool ServerEditing { get; private set; }
        /// <summary>Servers found on the LAN (button label, URL), and a line about the search ("searching...", "2 found").</summary>
        public readonly List<(string Label, string Url)> Found = new List<(string, string)>();
        public string FoundNote = "";
        public int Scroll { get; private set; }
        public readonly Hotbar Hotbar = new Hotbar();
        /// <summary>The block the left trigger places (BlockEditor.Selected follows it).</summary>
        public string Selected { get; private set; } = BlockEditRules.DefaultBlock;
        public MenuHit Hover { get; private set; } = MenuHit.Nothing;
        /// <summary>The right controller's ray is on the open panel: it owns the trigger and the right stick.</summary>
        public bool PointerOnPanel { get; private set; }
        public bool ShowHud;
        /// <summary>Area size presets in blocks (null = no area row) and the current size, from the app's hooks.</summary>
        public int[] AreaPresets;
        public int AreaCurrent = int.MinValue;
        /// <summary>Bumped on every visible change (the panel re-meshes when it differs).</summary>
        public int Version { get; private set; }
        /// <summary>Last thing that happened, for the name line ("can't place fire").</summary>
        public string Note = "";

        List<BlockEntry> _items = new List<BlockEntry>();
        readonly Dictionary<int, int> _scrollByTab = new Dictionary<int, int>();
        float _stickDir; double _stickNext;
        string _wantTab;   // a category tab restored from prefs.json before the server's block list arrived

        public BlockMenuModel() { Hotbar.Push(Selected); }

        public int CategoryCount => Registry?.CategoryIds.Count ?? 0;
        /// <summary>[hands] The Use and Eggs tabs follow the categories when the server sent an item_registry (else -1).</summary>
        public int ItemTabCount => UseItems != null ? 2 : 0;
        public int UseTab => UseItems != null ? CategoryCount : -1;
        public int EggsTab => UseItems != null ? CategoryCount + 1 : -1;
        public int SearchTab => CategoryCount + ItemTabCount;
        public int InfoTab => SearchTab + 1;
        public bool CapturesRight => Open && PointerOnPanel;
        public IReadOnlyList<BlockEntry> Items => _items;
        /// <summary>[hands] The Use tab with the name tag selected: its text field and keyboard under a shorter grid.</summary>
        public bool NameTagField => Tab == UseTab && InteractProtocol.ItemId(Selected) == InteractProtocol.NameTag;
        public int GridRows => Tab == InfoTab ? 0 : Tab == SearchTab || NameTagField ? SearchRows : Rows;
        public int RowCount => (_items.Count + Columns - 1) / Columns;
        public int MaxScroll => Math.Max(0, RowCount - GridRows);

        public void SetRegistry(BlockRegistry r)
        {
            string cur = TabId(Tab);   // Search, Info, Use and Eggs move when the category count changes
            Registry = r;
            int t = cur != null ? TabOfId(cur) : -1;
            Tab = t >= 0 ? t : Tab > InfoTab ? 0 : Tab;
            if (_wantTab != null) { int want = TabOfId(_wantTab); if (want >= 0) { Tab = want; _wantTab = null; } else if (UseItems != null) _wantTab = null; }
            _scrollByTab.Clear();
            Refresh(true);
        }

        /// <summary>[hands] item_registry arrived (or null: the next server has none): the Use and Eggs tabs come or go.</summary>
        public void SetItems(ItemRegistry items)
        {
            string cur = TabId(Tab);
            UseItems = items;
            int t = cur != null ? TabOfId(cur) : -1;
            Tab = t >= 0 ? t : Math.Min(Tab, InfoTab);
            if (_wantTab != null) { int want = TabOfId(_wantTab); if (want >= 0) { Tab = want; _wantTab = null; } }
            _scrollByTab.Clear();
            Refresh(true);
        }

        /// <summary>A stable name for a tab (prefs.json): the category id, "use", "eggs", "search" or "info".</summary>
        public string TabId(int tab) => tab == SearchTab ? "search" : tab == InfoTab ? "info" : tab == UseTab ? "use" : tab == EggsTab ? "eggs"
            : Registry != null && tab >= 0 && tab < CategoryCount ? Registry.CategoryIds[tab] : null;

        int TabOfId(string id) => id == "search" ? SearchTab : id == "info" ? InfoTab : id == "use" ? UseTab : id == "eggs" ? EggsTab
            : Registry != null ? Registry.CategoryIds.IndexOf(id) : -1;

        /// <summary>Restores the tab from prefs.json. A category tab waits for the server's block list (SetRegistry), Use
        /// and Eggs for its item list (SetItems).</summary>
        public void RestoreTab(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            int t = TabOfId(id);
            if (t >= 0) SetTab(t);
            else if (Registry == null || (UseItems == null && (id == "use" || id == "eggs"))) _wantTab = id;
        }

        /// <summary>Restores the hotbar and the selected block from prefs.json.</summary>
        public void Restore(IList<string> hotbar, int hotbarSelected, string selected)
        {
            Hotbar.Restore(hotbar, hotbarSelected);
            if (!string.IsNullOrEmpty(selected)) Selected = selected;
            else if (Hotbar.Current != null) Selected = Hotbar.Current;
            if (Hotbar.Count == 0) Hotbar.Push(Selected);
            Note = "";
            Version++;
        }

        public void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (!open) { PointerOnPanel = false; Hover = MenuHit.Nothing; ServerEditing = false; }
            Version++;
        }

        public void Toggle() => SetOpen(!Open);

        /// <summary>The entry shown in grid cell i (row-major from the top-left visible row), or null.</summary>
        public BlockEntry CellEntry(int i)
        {
            if (i < 0 || i >= GridRows * Columns) return null;
            int k = Scroll * Columns + i;
            return k < _items.Count ? _items[k] : null;
        }

        /// <summary>The ray's target on the panel this frame (onPanel false: the ray missed the panel).</summary>
        public void Point(MenuHit hit, bool onPanel)
        {
            if (!Open) { onPanel = false; hit = MenuHit.Nothing; }
            if (onPanel == PointerOnPanel && hit.Same(Hover)) return;
            PointerOnPanel = onPanel;
            Hover = hit;
            Version++;
        }

        /// <summary>Right trigger while the ray is on the panel: does what the hovered part does.</summary>
        public MenuAction Click()
        {
            var none = new MenuAction();
            if (!CapturesRight) return none;
            switch (Hover.Part)
            {
                case MenuPart.Tab: return SetTab(Hover.Index) ? new MenuAction { Kind = MenuActionKind.Tab, Value = Hover.Index } : none;
                case MenuPart.Cell:
                    var e = CellEntry(Hover.Index);
                    if (e == null) return none;
                    Choose(e.State);
                    return new MenuAction { Kind = MenuActionKind.Select, State = e.State };
                case MenuPart.Hotbar:
                    var s = Hotbar.Choose(Hover.Index);
                    if (s == null) return none;
                    Selected = s; Note = ""; Version++;
                    return new MenuAction { Kind = MenuActionKind.HotbarSlot, State = s, Value = Hover.Index };
                case MenuPart.ScrollUp: return ScrollBy(-GridRows) ? new MenuAction { Kind = MenuActionKind.Scrolled, Value = Scroll } : none;
                case MenuPart.ScrollDown: return ScrollBy(GridRows) ? new MenuAction { Kind = MenuActionKind.Scrolled, Value = Scroll } : none;
                case MenuPart.Key: return Key(Hover.Index);
                case MenuPart.AreaStep:
                    if (AreaPresets == null || Hover.Index >= AreaPresets.Length) return none;
                    return new MenuAction { Kind = MenuActionKind.Area, Value = AreaPresets[Hover.Index] };
                case MenuPart.HudToggle:
                    ShowHud = !ShowHud; Version++;
                    return new MenuAction { Kind = MenuActionKind.HudToggle, Value = ShowHud ? 1 : 0 };
                case MenuPart.ResetView: return new MenuAction { Kind = MenuActionKind.ResetView };
                case MenuPart.ServerField:
                    ServerEditing = true; Note = "";
                    ServerKeys.SetText(ServerUrl); ServerKeys.ResetMode();
                    Version++;
                    return new MenuAction { Kind = MenuActionKind.ServerEdit };
                case MenuPart.ServerFind: return new MenuAction { Kind = MenuActionKind.ServerFind };
                case MenuPart.FoundServer:
                    if (Hover.Index >= Found.Count) return none;
                    ServerKeys.SetText(Found[Hover.Index].Url);
                    return Connect(Found[Hover.Index].Url);
                case MenuPart.ServerConnect: return Connect(ServerKeys.Text);
                case MenuPart.ServerCancel:
                    ServerEditing = false; Note = ""; Version++;
                    return new MenuAction { Kind = MenuActionKind.ServerCancel };
            }
            return none;
        }

        /// <summary>The keyboard on the current tab: Search's, the Server field's while it's open, [hands] the name tag's
        /// on the Use tab while the name tag is selected, else null.</summary>
        public KeyboardModel ActiveKeys => Tab == SearchTab ? SearchKeys : Tab == InfoTab && ServerEditing ? ServerKeys : NameTagField ? NameTagKeys : null;

        /// <summary>Connect to a typed or found address: normalised (ws://host:8765/tabletop by default) or refused.</summary>
        MenuAction Connect(string text)
        {
            string url = ServerAddress.Normalize(text);
            if (url == null) { Note = "not a server address: " + text; Version++; return new MenuAction(); }
            ServerEditing = false; ServerUrl = url; Note = ""; Version++;
            return new MenuAction { Kind = MenuActionKind.ServerConnect, State = url };
        }

        /// <summary>Servers found on the LAN (or a note while searching / when none answered).</summary>
        public void SetFound(IEnumerable<(string Label, string Url)> found, string note)
        {
            Found.Clear();
            if (found != null) foreach (var f in found) if (Found.Count < MaxFound) Found.Add(f);
            FoundNote = note ?? "";
            Version++;
        }

        /// <summary>Right stick Y while the ray is on the panel: push to scroll one row, hold to repeat. True when it scrolled.</summary>
        public bool Stick(float y, double now)
        {
            float dir = !CapturesRight ? 0 : y > 0.5f ? 1 : y < -0.5f ? -1 : 0;
            if (dir == 0) { _stickDir = 0; return false; }
            if (dir != _stickDir) { _stickDir = dir; _stickNext = now + 0.35; return ScrollBy(dir > 0 ? -1 : 1); }
            if (now < _stickNext) return false;
            _stickNext = now + 0.12;
            return ScrollBy(dir > 0 ? -1 : 1);
        }

        public bool ScrollBy(int rows)
        {
            int s = Math.Max(0, Math.Min(MaxScroll, Scroll + rows));
            if (s == Scroll) return false;
            Scroll = s; _scrollByTab[Tab] = s; Version++;
            return true;
        }

        public bool SetTab(int tab)
        {
            if (tab < 0 || tab > InfoTab || tab == Tab) return false;
            Tab = tab;
            if (tab != InfoTab) ServerEditing = false;
            Refresh(false);
            return true;
        }

        /// <summary>A block chosen in the grid: it becomes the selection and goes to the front of the hotbar.</summary>
        public void Choose(string state)
        {
            Selected = state; Note = "";
            Hotbar.Push(state);
            Version++;
        }

        /// <summary>B pick from the world. A block the server won't place (fire, a potted plant...) isn't selected.</summary>
        public bool Picked(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            if (InteractProtocol.IsItem(state))   // [hands] a mob's spawn egg (B on a mob)
            {
                if (UseItems?.Find(state) == null) { Note = "no " + LabelOf(state) + " on this server"; Version++; return false; }
            }
            else if (Registry != null && !Registry.IsPlaceable(state)) { Note = "can't place " + LabelOf(state); Version++; return false; }
            Choose(state);
            return true;
        }

        /// <summary>Right stick click with the menu closed: the next hotbar slot becomes the selection. Null if empty.</summary>
        public string CycleHotbar()
        {
            var s = Hotbar.Cycle();
            if (s != null) { Selected = s; Note = ""; Version++; }
            return s;
        }

        /// <summary>
        /// An on-panel key of the tab's keyboard (World/KeyboardModel.cs). Search: typing filters the list, enter picks
        /// the first result. Server field: enter connects to the typed address.
        /// </summary>
        public MenuAction Key(int key)
        {
            var none = new MenuAction();
            var kb = ActiveKeys;
            if (kb == null) return none;
            var r = kb.Press(key);
            if (r == KeyResult.Enter)
            {
                if (kb == ServerKeys) return Connect(kb.Text);
                if (kb == NameTagKeys) { Note = NameTag.Length > 0 ? "Name tag: " + NameTag + " (left trigger on a mob)" : "Name tag: empty"; Version++; return new MenuAction { Kind = MenuActionKind.Typed, Value = key }; }
                if (_items.Count == 0) return none;
                Choose(_items[0].State);
                return new MenuAction { Kind = MenuActionKind.Select, State = _items[0].State };
            }
            if (r == KeyResult.None) return none;
            if (kb == SearchKeys && r == KeyResult.Text) { _scrollByTab[Tab] = 0; Refresh(false); }
            Version++;
            return new MenuAction { Kind = MenuActionKind.Typed, Value = key };
        }

        /// <summary>Name line text: what the ray is on, else the selected block (and the last note).</summary>
        public string NameLine()
        {
            if (Hover.Part == MenuPart.Cell && CellEntry(Hover.Index) is BlockEntry e) return e.Label;
            if (Hover.Part == MenuPart.Hotbar && Hotbar[Hover.Index] is string hs) return LabelOf(hs);
            if (Hover.Part == MenuPart.Tab) return TabLabel(Hover.Index);
            if (Hover.Part == MenuPart.FoundServer && Hover.Index < Found.Count) return Found[Hover.Index].Url;
            if (Hover.Part == MenuPart.ServerField) return "Change the server: " + ServerUrl;
            if (Hover.Part == MenuPart.ResetView) return "North up, fit to the table, view in the middle of the area";
            if (Note.Length > 0) return Note;
            return "Selected: " + LabelOf(Selected);
        }

        public string LabelOf(string state) =>
            InteractProtocol.IsItem(state) ? UseItems?.Find(state)?.Label ?? BlockRegistry.DefaultLabel(BlockRegistry.PathOf(InteractProtocol.ItemId(state)))   // [hands]
            : Registry?.Find(state) is BlockEntry e && BlockRegistry.IdOf(state) == e.Id ? e.Label : BlockRegistry.DefaultLabel(BlockRegistry.PathOf(BlockRegistry.IdOf(state)));

        public string TabLabel(int tab) => tab == SearchTab ? "Search" : tab == InfoTab ? "Info" : tab == UseTab ? "Use items" : tab == EggsTab ? "Spawn eggs"
            : Registry != null && tab < CategoryCount ? Registry.CategoryLabels[tab] : "";

        /// <summary>Short tab captions that fit 10 tabs on the panel.</summary>
        public string TabCaption(int tab)
        {
            if (tab == SearchTab) return "Search";
            if (tab == InfoTab) return "Info";
            if (tab == UseTab) return "Use";     // [hands]
            if (tab == EggsTab) return "Eggs";
            string id = Registry != null && tab < CategoryCount ? Registry.CategoryIds[tab] : "";
            switch (id)
            {
                case "building": return "Build";
                case "colored": return "Color";
                case "natural": return "Nature";
                case "functional": return "Func.";   // [hands] "Use" is the items tab now
                case "redstone": return "Redst.";
                case "other": return "Other";
                default: return BlockRegistry.DefaultLabel(id.Length > 6 ? id.Substring(0, 6) : id);
            }
        }

        void Refresh(bool keepScroll)
        {
            if (Tab == UseTab) _items = UseItems.Use;          // [hands]
            else if (Tab == EggsTab) _items = UseItems.Eggs;
            else if (Registry == null) _items = new List<BlockEntry>();
            else if (Tab == SearchTab) { _items = Search(Registry, Query); if (UseItems != null) _items.AddRange(Search(UseItems, Query)); }
            else if (Tab == InfoTab) _items = new List<BlockEntry>();
            else _items = Registry.ByCategory[Tab];
            int s = _scrollByTab.TryGetValue(Tab, out var v) ? v : 0;
            Scroll = Math.Max(0, Math.Min(MaxScroll, keepScroll ? Scroll : s));
            Version++;
        }

        /// <summary>
        /// Every entry whose name or id contains all the query's words (case-insensitive); names that start with the
        /// query first, then entries with a word starting with the first query word, then the rest, each in menu order.
        /// An empty query lists everything.
        /// </summary>
        public static List<BlockEntry> Search(BlockRegistry r, string query) => Search(r.Entries, query);

        /// <summary>[hands] The same over the usable items and spawn eggs (listed after the blocks).</summary>
        public static List<BlockEntry> Search(ItemRegistry r, string query)
        {
            var all = new List<BlockEntry>(r.Use);
            all.AddRange(r.Eggs);
            return Search(all, query);
        }

        static List<BlockEntry> Search(List<BlockEntry> entries, string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return new List<BlockEntry>(entries);
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<(int score, BlockEntry e)>();
            foreach (var e in entries)
            {
                bool all = true;
                foreach (var w in words) if (e.SearchText.IndexOf(w, StringComparison.Ordinal) < 0) { all = false; break; }
                if (!all) continue;
                int score = e.SearchText.StartsWith(q, StringComparison.Ordinal) ? 0
                          : (" " + e.SearchText).IndexOf(" " + words[0], StringComparison.Ordinal) >= 0 ? 1 : 2;
                hits.Add((score, e));
            }
            hits.Sort((a, b) => a.score != b.score ? a.score.CompareTo(b.score) : a.e.Order.CompareTo(b.e.Order));
            var list = new List<BlockEntry>(hits.Count);
            foreach (var h in hits) list.Add(h.e);
            return list;
        }

        // ---------- layout ----------

        public static float Width => 2 * Margin + Columns * Cell + Gap + ScrollW;

        /// <summary>Every box on the panel for the current tab, plus the panel size and the text areas.</summary>
        public MenuLayout Layout()
        {
            var L = new MenuLayout { Width = Width };
            float inner = Width - 2 * Margin, y = Margin;
            int tabs = InfoTab + 1;
            float tw = inner / tabs;
            for (int t = 0; t < tabs; t++) L.Items.Add(new MenuItem { Part = MenuPart.Tab, Index = t, R = new MenuRect(Margin + t * tw, y, tw, TabH), Label = TabCaption(t) });
            y += TabH + Gap;
            if (Tab == SearchTab) { L.SearchField = new MenuRect(Margin, y, inner, SearchH); y += SearchH + Gap; }
            if (Tab == InfoTab && ServerEditing) y = ServerLayout(L, y, inner);
            else
            {
                int rows = Tab == InfoTab ? Rows : GridRows;
                L.Grid = new MenuRect(Margin, y, Columns * Cell, rows * Cell);
                if (Tab == InfoTab)
                {
                    // Status text, then [Show HUD] [Reset view] and the Server button (opens the Server field).
                    float bh = ButtonH, bw = (inner - Gap) / 2, y1 = y + rows * Cell - 2 * bh - Gap;
                    L.Info = new MenuRect(Margin, y, inner, y1 - Gap - y);
                    L.Items.Add(new MenuItem { Part = MenuPart.HudToggle, Index = 0, R = new MenuRect(Margin, y1, bw, bh), Label = ShowHud ? "Show HUD: on" : "Show HUD: off" });
                    L.Items.Add(new MenuItem { Part = MenuPart.ResetView, Index = 0, R = new MenuRect(Margin + bw + Gap, y1, bw, bh), Label = "Reset view" });
                    L.Items.Add(new MenuItem { Part = MenuPart.ServerField, Index = 0, R = new MenuRect(Margin, y1 + bh + Gap, inner, bh),
                                               Label = "Server: " + (ServerUrl.Length > 0 ? ServerUrl : "-") + "  (change)" });
                }
                else
                {
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < Columns; c++)
                            L.Items.Add(new MenuItem { Part = MenuPart.Cell, Index = r * Columns + c, R = new MenuRect(Margin + c * Cell, y + r * Cell, Cell, Cell) });
                    float sx = Margin + Columns * Cell + Gap;
                    L.Items.Add(new MenuItem { Part = MenuPart.ScrollUp, R = new MenuRect(sx, y, ScrollW, ScrollW) });
                    L.Items.Add(new MenuItem { Part = MenuPart.ScrollDown, R = new MenuRect(sx, y + rows * Cell - ScrollW, ScrollW, ScrollW) });
                    L.ScrollTrack = new MenuRect(sx + ScrollW * 0.35f, y + ScrollW + Gap, ScrollW * 0.3f, rows * Cell - 2 * ScrollW - 2 * Gap);
                }
                y += rows * Cell + Gap;
                if (Tab == SearchTab) { L.Items.AddRange(SearchKeys.Layout(Margin, y, inner, KeyH)); y += KeyboardModel.Rows * KeyH + Gap; }
                else if (NameTagField)   // [hands] the name tag's text under the grid, then its keyboard
                {
                    L.NameTagField = new MenuRect(Margin, y, inner, SearchH); y += SearchH + Gap;
                    L.Items.AddRange(NameTagKeys.Layout(Margin, y, inner, KeyH)); y += KeyboardModel.Rows * KeyH + Gap;
                }
            }
            L.Name = new MenuRect(Margin, y, inner, NameH);
            y += NameH;
            for (int i = 0; i < Hotbar.Size; i++)
                L.Items.Add(new MenuItem { Part = MenuPart.Hotbar, Index = i, R = new MenuRect(Margin + i * Cell, y + (HotH - Cell) / 2, Cell, Cell) });
            y += HotH + Gap;
            if (AreaPresets != null && AreaPresets.Length > 0)
            {
                int n = Math.Min(5, AreaPresets.Length);
                L.AreaLabel = new MenuRect(Margin, y, AreaLabelW, AreaH);
                float bw = (inner - AreaLabelW) / n;
                for (int i = 0; i < n; i++)
                    L.Items.Add(new MenuItem { Part = MenuPart.AreaStep, Index = i, R = new MenuRect(Margin + AreaLabelW + i * bw, y, bw, AreaH), Label = AreaPresets[i].ToString() });
                y += AreaH + Gap;
            }
            L.Height = y - Gap + Margin;
            return L;
        }

        /// <summary>The Info tab while the Server field is open: the address, found servers (2 x 2), Find / Connect /
        /// Cancel, and the keyboard.</summary>
        float ServerLayout(MenuLayout L, float y, float inner)
        {
            L.ServerText = new MenuRect(Margin, y, inner, SearchH);
            y += SearchH + Gap;
            float bw = (inner - Gap) / 2;
            L.FoundNote = new MenuRect(Margin, y, inner, 2 * ButtonH + Gap);
            for (int i = 0; i < Found.Count && i < MaxFound; i++)
                L.Items.Add(new MenuItem { Part = MenuPart.FoundServer, Index = i, R = new MenuRect(Margin + (i % 2) * (bw + Gap), y + (i / 2) * (ButtonH + Gap), bw, ButtonH), Label = Found[i].Label });
            y += 2 * (ButtonH + Gap);
            float w3 = (inner - 2 * Gap) / 3;
            L.Items.Add(new MenuItem { Part = MenuPart.ServerFind, R = new MenuRect(Margin, y, w3, ButtonH), Label = "Find servers" });
            L.Items.Add(new MenuItem { Part = MenuPart.ServerConnect, R = new MenuRect(Margin + w3 + Gap, y, w3, ButtonH), Label = "Connect" });
            L.Items.Add(new MenuItem { Part = MenuPart.ServerCancel, R = new MenuRect(Margin + 2 * (w3 + Gap), y, w3, ButtonH), Label = "Cancel" });
            y += ButtonH + Gap;
            L.Items.AddRange(ServerKeys.Layout(Margin, y, inner, KeyH));
            return y + KeyboardModel.Rows * KeyH + Gap;
        }

        /// <summary>What a point on the panel (metres from the top-left, y down) hits; Panel for the background, None outside.</summary>
        public static MenuHit HitTest(MenuLayout L, float x, float y)
        {
            if (x < 0 || y < 0 || x >= L.Width || y >= L.Height) return MenuHit.Nothing;
            foreach (var it in L.Items) if (it.R.Contains(x, y)) return new MenuHit(it.Part, it.Index);
            return new MenuHit(MenuPart.Panel, 0);
        }
    }

    public sealed class MenuLayout
    {
        public float Width, Height;
        public readonly List<MenuItem> Items = new List<MenuItem>();
        public MenuRect Grid, Name, SearchField, Info, ScrollTrack, AreaLabel, ServerText, FoundNote;
        public MenuRect NameTagField;   // [hands] the name tag's text (Use tab)
    }
}
