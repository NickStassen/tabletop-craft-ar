// [hands] The mob info card, engine-free (Tests~): the left trigger on a mob (with a block, not an item, selected) asks
// the server for entity_info; the card shows it above the mob on the table with a few actions the right ray clicks:
// Kill, Remove, Freeze / Unfreeze, Glow, and Close. Players get Glow (and Kill only when the server lets the headset harm
// players). XR/EntityCard.cs draws it and sends the entity_op; the card asks again after each action so it stays current.
//
// Layout units are metres in card space: origin at the top-left corner, x to the right, y down.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public sealed class EntityCardModel
    {
        public const float Width = 0.12f, Margin = 0.005f, LineH = 0.0095f, ButtonH = 0.015f, Gap = 0.0025f;
        public const int ButtonsPerRow = 3;
        /// <summary>The card closes by itself this long after it last changed (seconds).</summary>
        public const double IdleSeconds = 30;

        public EntityInfoMessage Info { get; private set; }
        public bool Open => Info != null;
        public string Uuid => Info?.Uuid;
        public int Hover { get; private set; } = -1;
        public bool PointerOn { get; private set; }
        public bool CapturesRight => Open && PointerOn;
        public int Version { get; private set; }
        public readonly List<(string Op, string Label)> Buttons = new List<(string, string)>();
        double _changed;

        /// <summary>An entity_info arrived: open the card on it (or refresh it).</summary>
        public void Show(EntityInfoMessage info, InteractWelcome w, double now)
        {
            Info = info; _changed = now;
            Buttons.Clear();
            if (info.Player)
            {
                Buttons.Add(("glow", "Glow"));
                if (w != null && w.HarmPlayers) Buttons.Add(("kill", "Kill"));
            }
            else
            {
                Buttons.Add(("kill", "Kill"));
                Buttons.Add(("remove", "Remove"));
                if (info.Frozen.HasValue) Buttons.Add(info.Frozen.Value ? ("unfreeze", "Unfreeze") : ("freeze", "Freeze"));
                Buttons.Add(("glow", info.Glowing ? "Unglow" : "Glow"));
            }
            Buttons.Add(("close", "Close"));
            Version++;
        }

        public void Close()
        {
            if (Info == null) return;
            Info = null; Hover = -1; PointerOn = false; Version++;
        }

        /// <summary>Closes after IdleSeconds, or when the entity is gone (not among the shown targets).</summary>
        public void Tick(double now, bool entityShown)
        {
            if (Open && (!entityShown || now - _changed > IdleSeconds)) Close();
        }

        public List<string> Lines() => Info?.Lines() ?? new List<string>();

        public float Height
        {
            get
            {
                int rows = (Buttons.Count + ButtonsPerRow - 1) / ButtonsPerRow;
                return 2 * Margin + Lines().Count * LineH + Gap + rows * (ButtonH + Gap) - Gap;
            }
        }

        /// <summary>Button i's box (card space).</summary>
        public MenuRect ButtonRect(int i)
        {
            float bw = (Width - 2 * Margin - (ButtonsPerRow - 1) * Gap) / ButtonsPerRow;
            float y0 = Margin + Lines().Count * LineH + Gap;
            int r = i / ButtonsPerRow, c = i % ButtonsPerRow;
            return new MenuRect(Margin + c * (bw + Gap), y0 + r * (ButtonH + Gap), bw, ButtonH);
        }

        /// <summary>The button at a card point (-1: none); onCard false when the ray missed the card.</summary>
        public void Point(float x, float y, bool onCard)
        {
            int h = -1;
            if (Open && onCard) for (int i = 0; i < Buttons.Count; i++) if (ButtonRect(i).Contains(x, y)) { h = i; break; }
            bool on = Open && onCard;
            if (h == Hover && on == PointerOn) return;
            Hover = h; PointerOn = on; Version++;
        }

        /// <summary>The right trigger on the card: the hovered button's op ("close" closes it here), or null.</summary>
        public string Click(double now)
        {
            if (!CapturesRight || Hover < 0 || Hover >= Buttons.Count) return null;
            string op = Buttons[Hover].Op;
            if (op == "close") { Close(); return op; }
            _changed = now;
            return op;
        }
    }
}
