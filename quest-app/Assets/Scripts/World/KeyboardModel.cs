// A reusable on-panel keyboard, engine-free (Tests~): the text being typed, the shift and ?123 layers, and the key
// layout as MenuItems in panel space. A panel shows it by drawing MenuPart.Key items (XR/BlockMenu.cs does) and calls
// Press with the hovered key's Index. Used by the block menu's Search tab and the Info tab's Server field; meant to be
// reused for chat and commands later.
//
//   Letters (abc):  q w e r t y u i o p / a s d f g h j k l / shift z x c v b n m del / ?123 space clear enter
//   Symbols (?123): 1 2 3 4 5 6 7 8 9 0 / / : . , - _ @ # ~ ^ / [ ] { } = + * ! ? " ' del / ABC space clear enter
//
// Shift is one-shot (the next letter is upper case); shift again = caps lock; once more = off. The cursor is always at
// the end of the text; Display(n) shows its tail when it's longer than the field.
using System;
using System.Collections.Generic;

namespace TabletopCraft.World
{
    public enum ShiftState { Off, Once, Locked }

    public enum KeyResult { None, Text, Mode, Enter }

    public sealed class KeyboardModel
    {
        public const string Letters = "qwertyuiopasdfghjklzxcvbnm";
        /// <summary>The ?123 layer, in layout order: digits, then the symbols commands and addresses need.</summary>
        public const string Symbols = "1234567890/:.,-_@#~^[]{}=+*!?\"'";
        public const int KeySpace = 26, KeyDelete = 27, KeyClear = 28, KeyShift = 29, KeyLayer = 30, KeyEnter = 31, KeySymbol0 = 32;
        public const int Rows = 4;

        public string Text { get; private set; } = "";
        public int MaxLength = 64;
        /// <summary>No leading space and no two spaces in a row (search words).</summary>
        public bool CollapseSpaces;
        /// <summary>Caption of the enter key (null = "enter").</summary>
        public string EnterLabel = "enter";
        public ShiftState Shift { get; private set; }
        public bool SymbolsLayer { get; private set; }
        /// <summary>Bumped on every change (text, shift, layer).</summary>
        public int Version { get; private set; }

        public void SetText(string t)
        {
            t = t ?? "";
            if (t.Length > MaxLength) t = t.Substring(0, MaxLength);
            if (t == Text) return;
            Text = t; Version++;
        }

        /// <summary>Back to lower-case letters (when the field opens).</summary>
        public void ResetMode()
        {
            if (Shift == ShiftState.Off && !SymbolsLayer) return;
            Shift = ShiftState.Off; SymbolsLayer = false; Version++;
        }

        /// <summary>The character a key types in the current mode, or '\0' for the special keys.</summary>
        public char CharOf(int key)
        {
            if (key >= 0 && key < Letters.Length) return Shift != ShiftState.Off ? char.ToUpperInvariant(Letters[key]) : Letters[key];
            if (key == KeySpace) return ' ';
            if (key >= KeySymbol0 && key < KeySymbol0 + Symbols.Length) return Symbols[key - KeySymbol0];
            return '\0';
        }

        public KeyResult Press(int key)
        {
            switch (key)
            {
                case KeyShift:
                    Shift = Shift == ShiftState.Off ? ShiftState.Once : Shift == ShiftState.Once ? ShiftState.Locked : ShiftState.Off;
                    Version++;
                    return KeyResult.Mode;
                case KeyLayer:
                    SymbolsLayer = !SymbolsLayer; Version++;
                    return KeyResult.Mode;
                case KeyEnter:
                    return KeyResult.Enter;
                case KeyDelete:
                    if (Text.Length == 0) return KeyResult.None;
                    Text = Text.Substring(0, Text.Length - 1); Version++;
                    return KeyResult.Text;
                case KeyClear:
                    if (Text.Length == 0) return KeyResult.None;
                    Text = ""; Version++;
                    return KeyResult.Text;
            }
            char c = CharOf(key);
            if (c == '\0' || Text.Length >= MaxLength) return KeyResult.None;
            if (c == ' ' && CollapseSpaces && (Text.Length == 0 || Text.EndsWith(" "))) return KeyResult.None;
            Text += c;
            if (Shift == ShiftState.Once && key < Letters.Length) Shift = ShiftState.Off;
            Version++;
            return KeyResult.Text;
        }

        /// <summary>The text with the cursor at its end, shortened to its last maxChars characters ("…" + tail).</summary>
        public string Display(int maxChars, bool cursor = true)
        {
            string s = Text + (cursor ? "_" : "");
            return s.Length <= maxChars || maxChars < 2 ? s : "…" + s.Substring(s.Length - (maxChars - 1));
        }

        /// <summary>Caption of a key in the current mode.</summary>
        public string Label(int key)
        {
            switch (key)
            {
                case KeySpace: return "space";
                case KeyDelete: return "del";
                case KeyClear: return "clear";
                case KeyShift: return Shift == ShiftState.Locked ? "SHIFT" : "shift";
                case KeyLayer: return SymbolsLayer ? "ABC" : "?123";
                case KeyEnter: return EnterLabel ?? "enter";
            }
            char c = CharOf(key);
            return c == '\0' ? "" : c.ToString();
        }

        /// <summary>
        /// The keys of the current layer as clickable boxes (Part Key, Index = key id), 4 rows of keyH from (x0, y0),
        /// width wide (panel metres, y down). Rows are 10 key units wide.
        /// </summary>
        public List<MenuItem> Layout(float x0, float y0, float width, float keyH)
        {
            var items = new List<MenuItem>();
            float u = width / 10;
            void Row(int r, float startUnits, IEnumerable<(int key, float units)> keys)
            {
                float x = x0 + startUnits * u;
                foreach (var (key, units) in keys)
                {
                    items.Add(new MenuItem { Part = MenuPart.Key, Index = key, R = new MenuRect(x, y0 + r * keyH, units * u, keyH), Label = Label(key) });
                    x += units * u;
                }
            }
            IEnumerable<(int, float)> Span(int from, int count, float units, int baseKey)
            {
                for (int i = 0; i < count; i++) yield return (baseKey + from + i, units);
            }
            IEnumerable<(int, float)> Concat(params IEnumerable<(int, float)>[] parts)
            {
                foreach (var p in parts) foreach (var k in p) yield return k;
            }
            if (!SymbolsLayer)
            {
                Row(0, 0, Span(0, 10, 1, 0));
                Row(1, 0.5f, Span(10, 9, 1, 0));
                Row(2, 0, Concat(new[] { (KeyShift, 1.5f) }, Span(19, 7, 1, 0), new[] { (KeyDelete, 1.5f) }));
            }
            else
            {
                Row(0, 0, Span(0, 10, 1, KeySymbol0));
                Row(1, 0, Span(10, 10, 1, KeySymbol0));
                Row(2, 0, Concat(Span(20, 11, 0.75f, KeySymbol0), new[] { (KeyDelete, 1.75f) }));
            }
            Row(3, 0, new[] { (KeyLayer, 1.5f), (KeySpace, 4.5f), (KeyClear, 1.5f), (KeyEnter, 2.5f) });
            return items;
        }
    }
}
