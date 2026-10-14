// The block menu's state, layout and input rules, engine-free (Tests~). XR/BlockMenu.cs draws it on a panel beside
// the left hand and feeds it what the right controller's ray hits; this file decides what a hover, a trigger pull, a
// stick push, the right stick click or a B pick does.
//
//   Tabs: the server's categories (block_registry), Search (on-panel QWERTY keyboard), Info (status, "Show HUD").
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
    public enum MenuPart { None, Panel, Tab, Cell, ScrollUp, ScrollDown, Hotbar, Key, AreaStep, HudToggle }

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

    public enum MenuActionKind { None, Select, Tab, Typed, Scrolled, HotbarSlot, Area, HudToggle }

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
    }

    public sealed class BlockMenuModel
    {
        // Layout (metres). Readable at arm's length: 30 mm cells, 24 mm tabs and keys.
        public const int Columns = 9, Rows = 5, SearchRows = 3;
        public const float Margin = 0.008f, Gap = 0.004f, TabH = 0.024f, Cell = 0.030f, ScrollW = 0.022f, NameH = 0.018f,
                           HotH = 0.034f, AreaH = 0.024f, KeyH = 0.024f, SearchH = 0.022f, AreaLabelW = 0.05f;
        public const int MaxQuery = 24;
        public const string KeyRows = "qwertyuiop|asdfghjkl|zxcvbnm";
        public const int KeySpace = 26, KeyDelete = 27, KeyClear = 28;

        public BlockRegistry Registry { get; private set; }
        public bool Open { get; private set; }
        public int Tab { get; private set; }
        public string Query { get; private set; } = "";
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

        public BlockMenuModel() { Hotbar.Push(Selected); }

        public int CategoryCount => Registry?.CategoryIds.Count ?? 0;
        public int SearchTab => CategoryCount;
        public int InfoTab => CategoryCount + 1;
        public bool CapturesRight => Open && PointerOnPanel;
        public IReadOnlyList<BlockEntry> Items => _items;
        public int GridRows => Tab == InfoTab ? 0 : Tab == SearchTab ? SearchRows : Rows;
        public int RowCount => (_items.Count + Columns - 1) / Columns;
        public int MaxScroll => Math.Max(0, RowCount - GridRows);

        public void SetRegistry(BlockRegistry r)
        {
            Registry = r;
            if (Tab > InfoTab) Tab = 0;
            _scrollByTab.Clear();
            Refresh(true);
        }

        public void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (!open) { PointerOnPanel = false; Hover = MenuHit.Nothing; }
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
                case MenuPart.Key: return Key(Hover.Index) ? new MenuAction { Kind = MenuActionKind.Typed, Value = Hover.Index } : none;
                case MenuPart.AreaStep:
                    if (AreaPresets == null || Hover.Index >= AreaPresets.Length) return none;
                    return new MenuAction { Kind = MenuActionKind.Area, Value = AreaPresets[Hover.Index] };
                case MenuPart.HudToggle:
                    ShowHud = !ShowHud; Version++;
                    return new MenuAction { Kind = MenuActionKind.HudToggle, Value = ShowHud ? 1 : 0 };
            }
            return none;
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
            if (Registry != null && !Registry.IsPlaceable(state)) { Note = "can't place " + LabelOf(state); Version++; return false; }
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

        /// <summary>An on-panel key: letters and space append to the query, delete removes the last character, clear empties it.</summary>
        public bool Key(int key)
        {
            string q = Query;
            if (key >= 0 && key < 26) { if (q.Length < MaxQuery) q += KeyChar(key); }
            else if (key == KeySpace) { if (q.Length > 0 && q.Length < MaxQuery && !q.EndsWith(" ")) q += " "; }
            else if (key == KeyDelete) { if (q.Length > 0) q = q.Substring(0, q.Length - 1); }
            else if (key == KeyClear) q = "";
            if (q == Query) return false;
            Query = q;
            _scrollByTab[Tab] = 0;
            Refresh(false);
            return true;
        }

        public static char KeyChar(int key) => KeyRows.Replace("|", "")[key];

        /// <summary>Name line text: what the ray is on, else the selected block (and the last note).</summary>
        public string NameLine()
        {
            if (Hover.Part == MenuPart.Cell && CellEntry(Hover.Index) is BlockEntry e) return e.Label;
            if (Hover.Part == MenuPart.Hotbar && Hotbar[Hover.Index] is string hs) return LabelOf(hs);
            if (Hover.Part == MenuPart.Tab) return TabLabel(Hover.Index);
            if (Note.Length > 0) return Note;
            return "Selected: " + LabelOf(Selected);
        }

        public string LabelOf(string state) => Registry?.Find(state) is BlockEntry e && BlockRegistry.IdOf(state) == e.Id ? e.Label : BlockRegistry.DefaultLabel(BlockRegistry.PathOf(BlockRegistry.IdOf(state)));

        public string TabLabel(int tab) => tab == SearchTab ? "Search" : tab == InfoTab ? "Info" : Registry != null && tab < CategoryCount ? Registry.CategoryLabels[tab] : "";

        /// <summary>Short tab captions that fit 8 tabs on the panel.</summary>
        public string TabCaption(int tab)
        {
            if (tab == SearchTab) return "Search";
            if (tab == InfoTab) return "Info";
            string id = Registry != null && tab < CategoryCount ? Registry.CategoryIds[tab] : "";
            switch (id)
            {
                case "building": return "Build";
                case "colored": return "Color";
                case "natural": return "Nature";
                case "functional": return "Use";
                case "redstone": return "Redst.";
                case "other": return "Other";
                default: return BlockRegistry.DefaultLabel(id.Length > 6 ? id.Substring(0, 6) : id);
            }
        }

        void Refresh(bool keepScroll)
        {
            if (Registry == null) _items = new List<BlockEntry>();
            else if (Tab == SearchTab) _items = Search(Registry, Query);
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
        public static List<BlockEntry> Search(BlockRegistry r, string query)
        {
            string q = (query ?? "").Trim().ToLowerInvariant();
            if (q.Length == 0) return new List<BlockEntry>(r.Entries);
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<(int score, BlockEntry e)>();
            foreach (var e in r.Entries)
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
            int rows = Tab == InfoTab ? Rows : GridRows;
            L.Grid = new MenuRect(Margin, y, Columns * Cell, rows * Cell);
            if (Tab == InfoTab)
            {
                float bh = 0.024f, bw = 0.09f;
                L.Info = new MenuRect(Margin, y, inner, rows * Cell - bh - Gap);
                L.Items.Add(new MenuItem { Part = MenuPart.HudToggle, Index = 0, R = new MenuRect(Margin, y + rows * Cell - bh, bw, bh), Label = ShowHud ? "Show HUD: on" : "Show HUD: off" });
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
            if (Tab == SearchTab)
            {
                float kw = inner / 10;
                var rowsOfKeys = KeyRows.Split('|');
                int k = 0;
                for (int r = 0; r < rowsOfKeys.Length; r++)
                {
                    float x0 = Margin + (10 - rowsOfKeys[r].Length) * kw / 2;
                    for (int i = 0; i < rowsOfKeys[r].Length; i++, k++)
                        L.Items.Add(new MenuItem { Part = MenuPart.Key, Index = k, R = new MenuRect(x0 + i * kw, y + r * KeyH, kw, KeyH), Label = char.ToUpperInvariant(rowsOfKeys[r][i]).ToString() });
                }
                float yb = y + 3 * KeyH;
                L.Items.Add(new MenuItem { Part = MenuPart.Key, Index = KeySpace, R = new MenuRect(Margin + 1 * kw, yb, 4 * kw, KeyH), Label = "space" });
                L.Items.Add(new MenuItem { Part = MenuPart.Key, Index = KeyDelete, R = new MenuRect(Margin + 5 * kw, yb, 2 * kw, KeyH), Label = "delete" });
                L.Items.Add(new MenuItem { Part = MenuPart.Key, Index = KeyClear, R = new MenuRect(Margin + 7 * kw, yb, 2 * kw, KeyH), Label = "clear" });
                y += 4 * KeyH + Gap;
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
        public MenuRect Grid, Name, SearchField, Info, ScrollTrack, AreaLabel;
    }
}
