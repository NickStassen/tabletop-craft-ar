// [operator] The sign editor, engine-free (Tests~): the left trigger on a sign opens it in the operator panel
// (OperatorPanelModel's sign mode), filled with what the block_entities stream says the sign holds (PROTOCOL.md 4.29).
// Front and back each have four lines, a dye colour and glowing ink; the right trigger picks a line and the panel
// keyboard types into it (enter moves to the next line), the swatches pick the colour, Glow toggles the ink. Save sends
// one sign_edit per side that changed (PROTOCOL.md "Signs"); the new text comes back through block_entities like any
// sign edit, so nothing is guessed here.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class SignEditorModel
    {
        /// <summary>Characters per line: vanilla signs show about 15 at the default size; the server keeps up to 90.</summary>
        public const int MaxLine = 40;

        public sealed class Side
        {
            public readonly string[] Lines = { "", "", "", "" };
            public string Color = "black";
            public bool Glowing;
            public bool Changed;
        }

        public Vector3Int Pos { get; private set; }
        public bool Open { get; private set; }
        public bool Hanging { get; private set; }
        public readonly Side Front = new Side(), Back = new Side();
        public bool OnBack { get; private set; }
        public Side Current => OnBack ? Back : Front;
        public int Line { get; private set; }
        public readonly KeyboardModel Keys = new KeyboardModel { MaxLength = MaxLine, EnterLabel = "next" };
        public string Note = "";
        public int Version { get; private set; }

        /// <summary>The left trigger on a sign: open, filled with the sign's current text (null: an empty sign).</summary>
        public void Begin(Vector3Int pos, string state, BlockEntityData data)
        {
            Pos = pos; Open = true; OnBack = false; Line = 0; Note = "";
            Hanging = BlockRegistry.PathOf(BlockRegistry.IdOf(state ?? "")).Contains("hanging_sign");
            Fill(Front, data?.Front); Fill(Back, data?.Back);
            Keys.SetText(Front.Lines[0]); Keys.ResetMode();
            Version++;
        }

        static void Fill(Side s, SignSideText t)
        {
            for (int i = 0; i < 4; i++) s.Lines[i] = t != null && i < t.Lines.Length && t.Lines[i] != null ? Trim(t.Lines[i]) : "";
            s.Color = t?.Color ?? "black"; s.Glowing = t != null && t.Glowing; s.Changed = false;
        }

        static string Trim(string s) => s.Length > MaxLine ? s.Substring(0, MaxLine) : s;

        public void Close() { if (Open) { Open = false; Version++; } }

        public void SetSide(bool back)
        {
            if (back == OnBack) return;
            OnBack = back; Line = 0; Keys.SetText(Current.Lines[0]); Version++;
        }

        public void SelectLine(int i)
        {
            if (i < 0 || i > 3) return;
            Line = i; Keys.SetText(Current.Lines[i]); Version++;
        }

        /// <summary>A key of the panel keyboard: typing goes into the selected line, enter moves to the next one.</summary>
        public KeyResult Key(int key)
        {
            Keys.SetText(Current.Lines[Line]);
            var r = Keys.Press(key);
            if (r == KeyResult.Text) { Current.Lines[Line] = Keys.Text; Current.Changed = true; }
            else if (r == KeyResult.Enter) SelectLine((Line + 1) % 4);
            Version++;
            return r;
        }

        public void SetColor(string dye)
        {
            if (dye == null || Current.Color == dye) return;
            Current.Color = dye; Current.Changed = true; Version++;
        }

        public void ToggleGlow() { Current.Glowing = !Current.Glowing; Current.Changed = true; Version++; }

        public bool Changed => Front.Changed || Back.Changed;

        /// <summary>Save: one sign_edit per side that changed (front first).</summary>
        public List<Dictionary<string, object>> Save()
        {
            var l = new List<Dictionary<string, object>>();
            bool f = Front.Changed, b = Back.Changed;
            if (f) l.Add(OperatorProtocol.SignEdit(Pos, false, Front.Lines, Front.Color, Front.Glowing));
            if (b) l.Add(OperatorProtocol.SignEdit(Pos, true, Back.Lines, Back.Color, Back.Glowing));
            Front.Changed = Back.Changed = false;
            Note = !f && !b ? "nothing changed" : f && b ? "saved both sides" : f ? "saved the front" : "saved the back";
            Version++;
            return l;
        }

        /// <summary>A refusal about a sign_edit at this sign.</summary>
        public bool OnError(string op, Vector3Int? pos, string message)
        {
            if (!Open || op != "sign_edit" || (pos.HasValue && pos.Value != Pos)) return false;
            Note = message ?? "refused";
            Version++;
            return true;
        }
    }
}
