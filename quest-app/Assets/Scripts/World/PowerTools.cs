// [power] The headset's power tools, engine-free (Tests~): undo/redo, the box wand and brushes (docs/QUEST_SETUP.md
// "Power tools", docs/PROTOCOL.md "Undo and redo", "Box edits", "Brushes"; cap power_v1). XR/PowerToolMode.cs feeds it
// the controllers and draws the boxes; the block menu's Tools tab (World/BlockMenuModel.cs) shows its buttons.
//
//   Groups      every trigger press starts an edit group (EditGroups); the edits of one hold share it, so one undo
//               takes back one hold. A box edit or a brush hold is one group too.
//   B           a tap picks (or, with a tool on, picks the filter); holding it 0.6 s undoes, and keeps undoing every
//               0.4 s while held (TapHoldButton).
//   Wand        left trigger = corner A, left grip + left trigger = corner B (one corner alone is a one-block box);
//               the Tools tab's Fill / Replace / Clear / Walls / Hollow send box_edit with the selected block (and the
//               filter for Replace); Clone turns paste mode on: the copy follows the aimed point, Rotate turns it a
//               quarter, the left trigger pastes (again and again) until Clone or B ends it.
//   Brush       sphere, cylinder or disc of radius 1..8: while the left trigger is held, a stroke goes out every
//               StrokeSeconds at the aimed block (paint, replace the filter, erase, smooth), kept under the server's
//               brush-blocks-per-second.
// Nothing changes locally: results come back as block updates, history and box_progress.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum ButtonEvent { None, Tap, Hold }

    /// <summary>A button with a tap and a hold meaning: Tap when released before HoldSeconds; Hold at HoldSeconds and
    /// again every RepeatSeconds while it stays down (no Tap after a hold). A press that began while disabled is
    /// ignored until it is released.</summary>
    public sealed class TapHoldButton
    {
        public double HoldSeconds = 0.6, RepeatSeconds = 0.4;
        double _down = -1, _next;
        bool _held, _ignore;

        public bool Down => _down >= 0;

        /// <summary>0..1 towards the first hold (for a ring or a buzz), 0 when up.</summary>
        public float Progress(double now) => _down < 0 || _held ? 0f : (float)Math.Min(1, (now - _down) / HoldSeconds);

        public ButtonEvent Update(bool pressed, double now, bool enabled = true)
        {
            if (!enabled) { _down = -1; _ignore = pressed; return ButtonEvent.None; }
            if (_ignore) { if (!pressed) _ignore = false; return ButtonEvent.None; }
            if (pressed)
            {
                if (_down < 0) { _down = now; _held = false; _next = now + HoldSeconds; return ButtonEvent.None; }
                if (now < _next) return ButtonEvent.None;
                _held = true; _next = now + RepeatSeconds;
                return ButtonEvent.Hold;
            }
            if (_down < 0) return ButtonEvent.None;
            bool tap = !_held;
            _down = -1;
            return tap ? ButtonEvent.Tap : ButtonEvent.None;
        }
    }

    /// <summary>Edit group ids for this connection: one per trigger press or tool action (positive, increasing).</summary>
    public sealed class EditGroups
    {
        int _next = 1;
        public int Last { get; private set; }
        public int Begin() => Last = _next++;
    }

    public enum ToolMode { None, Wand, Brush }
    public enum BrushForm { Sphere, Cylinder, Disc }
    public enum ToolButton { Wand, Brush, Fill, Replace, Clear, Walls, Hollow, Clone, Rotate, ClearCorners, Paint, BrushReplace, Erase, Smooth,
                             RadiusDown, RadiusUp, Undo, Redo }

    public sealed class PowerTools
    {
        public const double StrokeSeconds = 0.15, JobTimeoutSeconds = 90;
        public PowerLimits Limits = new PowerLimits();
        public PowerHistory History = new PowerHistory();
        /// <summary>The last box_progress (a job running, or the last one finished).</summary>
        public BoxProgress Progress;
        public readonly EditGroups Groups;
        public ToolMode Mode { get; private set; }
        public Vector3Int? A { get; private set; }
        public Vector3Int? B { get; private set; }
        /// <summary>The block Replace looks for (box and brush): picked with B while a tool is on.</summary>
        public string Filter { get; private set; }
        public bool Pasting { get; private set; }
        public int Rotate { get; private set; }
        public BrushForm Form = BrushForm.Sphere;
        public string BrushOp = "paint";
        public int Radius = 3;
        /// <summary>Last thing that happened (the Tools tab's status line).</summary>
        public string Result = "";
        /// <summary>Lines the app shows as toasts (taken with TakeToasts).</summary>
        readonly List<(string Text, bool Warn)> _toasts = new List<(string, bool)>();
        /// <summary>Jobs sent and not answered yet: envelope seq -> what and when.</summary>
        readonly Dictionary<long, (string Op, double At)> _pending = new Dictionary<long, (string, double)>();
        double _lastStroke = -1, _tokens, _tokensAt = -1;
        Vector3Int _lastCenter;
        public int Version { get; private set; }

        public PowerTools(EditGroups groups = null) { Groups = groups ?? new EditGroups(); }

        // ---------- the server ----------

        public void OnWelcome(Dictionary<string, object> welcome)
        {
            Limits = PowerLimits.From(welcome);
            History = new PowerHistory { Depth = Limits.UndoDepth };
            Progress = null;
            _pending.Clear();
            if (!Limits.Box && Mode == ToolMode.Wand || !Limits.Brush && Mode == ToolMode.Brush || !Limits.Allowed) SetMode(ToolMode.None);
            Radius = Math.Max(1, Math.Min(Radius, Math.Max(1, Limits.BrushMaxRadius)));
            Version++;
        }

        public void OnHistory(Dictionary<string, object> m) { History = PowerHistory.From(m); Version++; }

        public void OnProgress(Dictionary<string, object> m)
        {
            Progress = BoxProgress.From(m);
            if (Progress.Finished)
            {
                _pending.Remove(Progress.RefSeq);
                Result = Progress.Text();
                _toasts.Add((Result, Progress.Skipped > 0 || Progress.Phase == "failed"));
            }
            Version++;
        }

        /// <summary>An error about a power message (op box_edit, undo, redo, brush): true when it was ours. The app toasts
        /// it as it does every error.</summary>
        public bool OnError(Dictionary<string, object> m)
        {
            string op = m.S("op");
            if (op != "box_edit" && op != "undo" && op != "redo" && op != "brush") return false;
            if (m.ContainsKey("ref_seq")) _pending.Remove((long)m.D("ref_seq"));
            Result = (op == "box_edit" ? "box: " : op + ": ") + m.S("message");
            Version++;
            return true;
        }

        /// <summary>The app sent a job (undo, redo, box_edit) with this envelope seq (LinkClient.SendTracked).</summary>
        public void Sent(long seq, string op, double now) { if (seq >= 0) _pending[seq] = (op, now); }

        /// <summary>A job is still on its way or running (the server takes one at a time per headset).</summary>
        public bool Busy(double now)
        {
            if (_pending.Count == 0) return false;
            var stale = new List<long>();
            foreach (var kv in _pending) if (now - kv.Value.At > JobTimeoutSeconds) stale.Add(kv.Key);
            foreach (var k in stale) _pending.Remove(k);
            return _pending.Count > 0;
        }

        public List<(string Text, bool Warn)> TakeToasts()
        {
            var l = new List<(string, bool)>(_toasts);
            _toasts.Clear();
            return l;
        }

        // ---------- modes, corners, filter ----------

        public void SetMode(ToolMode m)
        {
            if (m == Mode) return;
            Mode = m;
            if (m != ToolMode.Wand) Pasting = false;
            Version++;
        }

        /// <summary>Left trigger with the wand: corner A (or B with the left grip). A new A without B is a one-block box.</summary>
        public void SetCorner(bool second, Vector3Int world)
        {
            if (second && !A.HasValue) second = false;
            if (second) B = world; else { A = world; B = null; }
            Pasting = false;
            Result = second ? $"corner B {Fmt(world)}: box {Size.x}x{Size.y}x{Size.z} = {Volume} blocks" : $"corner A {Fmt(world)} (left grip + left trigger sets B)";
            if (second && Limits.BoxMaxBlocks > 0 && Volume > Limits.BoxMaxBlocks) Result += $", more than {Limits.BoxMaxBlocks}";
            Version++;
        }

        public void ClearCorners() { A = B = null; Pasting = false; Result = "corners cleared"; Version++; }

        public bool HasBox => A.HasValue;
        public Vector3Int Min => !A.HasValue ? default : B.HasValue ? Lo(A.Value, B.Value) : A.Value;
        public Vector3Int Max => !A.HasValue ? default : B.HasValue ? Hi(A.Value, B.Value) : A.Value;
        static Vector3Int Lo(Vector3Int a, Vector3Int b) => new Vector3Int(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Min(a.z, b.z));
        static Vector3Int Hi(Vector3Int a, Vector3Int b) => new Vector3Int(Math.Max(a.x, b.x), Math.Max(a.y, b.y), Math.Max(a.z, b.z));
        public Vector3Int Size => HasBox ? Max - Min + new Vector3Int(1, 1, 1) : default;
        public long Volume => HasBox ? (long)Size.x * Size.y * Size.z : 0;

        /// <summary>B with a tool on: the aimed block becomes Replace's filter (the block's id, any state). True if taken.</summary>
        public bool PickFilter(string state)
        {
            if (Mode == ToolMode.None || string.IsNullOrEmpty(state)) return false;
            if (Pasting) { Pasting = false; Result = "paste ended"; Version++; return true; }
            Filter = BlockRegistry.IdOf(state);
            Result = "replace: " + BlockEditRules.Label(Filter);
            Version++;
            return true;
        }

        // ---------- buttons ----------

        /// <summary>A Tools tab button (or B held: Undo). Returns the message to send, or null (Result says why).</summary>
        public Dictionary<string, object> Press(ToolButton b, string material, double now)
        {
            Version++;
            switch (b)
            {
                case ToolButton.Wand:
                    if (!Limits.Box) { Result = Off("box tools", "edit.box-max-blocks"); return null; }
                    SetMode(Mode == ToolMode.Wand ? ToolMode.None : ToolMode.Wand);
                    Result = Mode == ToolMode.Wand ? "wand on: left trigger = corner A, left grip + trigger = corner B" : "wand off";
                    return null;
                case ToolButton.Brush:
                    if (!Limits.Brush) { Result = Off("brushes", "edit.brush-blocks-per-second"); return null; }
                    if (Mode != ToolMode.Brush) { SetMode(ToolMode.Brush); Form = BrushForm.Sphere; }
                    else if (Form == BrushForm.Disc) SetMode(ToolMode.None);
                    else Form = Form + 1;
                    Result = Mode == ToolMode.Brush ? $"brush: {Form.ToString().ToLowerInvariant()} {BrushOp}, hold the left trigger" : "brush off";
                    return null;
                case ToolButton.Fill: return Box("fill", material, now);
                case ToolButton.Replace: return Box("replace", material, now);
                case ToolButton.Clear: return Box("clear", null, now);
                case ToolButton.Walls: return Box("walls", material, now);
                case ToolButton.Hollow: return Box("hollow", material, now);
                case ToolButton.Clone:
                    if (Pasting) { Pasting = false; Result = "paste ended"; return null; }
                    if (!Limits.Box) { Result = Off("box tools", "edit.box-max-blocks"); return null; }
                    if (!HasBox) { Result = "can't clone: set corner A (and B) with the wand first"; return null; }
                    if (Volume > Limits.BoxMaxBlocks) { Result = $"can't clone: the box is {Volume} blocks, more than {Limits.BoxMaxBlocks}"; return null; }
                    SetMode(ToolMode.Wand);
                    Pasting = true; Rotate = 0;
                    Result = "paste: aim where the copy goes, left trigger pastes, Rotate turns it, B ends";
                    return null;
                case ToolButton.Rotate:
                    Rotate = (Rotate + 1) & 3;
                    Result = $"copy turned {Rotate * 90}°";
                    return null;
                case ToolButton.ClearCorners: ClearCorners(); return null;
                case ToolButton.Paint: case ToolButton.BrushReplace: case ToolButton.Erase: case ToolButton.Smooth:
                    if (!Limits.Brush) { Result = Off("brushes", "edit.brush-blocks-per-second"); return null; }
                    BrushOp = b == ToolButton.Paint ? "paint" : b == ToolButton.BrushReplace ? "replace" : b == ToolButton.Erase ? "erase" : "smooth";
                    SetMode(ToolMode.Brush);
                    Result = $"brush: {Form.ToString().ToLowerInvariant()} {BrushOp}";
                    return null;
                case ToolButton.RadiusDown: case ToolButton.RadiusUp:
                    Radius = Math.Max(1, Math.Min(Math.Max(1, Limits.BrushMaxRadius), Radius + (b == ToolButton.RadiusUp ? 1 : -1)));
                    Result = $"brush radius {Radius} ({Cells(Form, Radius)} blocks)";
                    return null;
                case ToolButton.Undo: case ToolButton.Redo:
                {
                    bool redo = b == ToolButton.Redo;
                    if (!Limits.Undo) { Result = Limits.Allowed ? Off("undo", "edit.undo-depth") : "undo needs a server with power_v1 (update the plugin)"; return null; }
                    if (Busy(now)) { Result = "wait: " + (Progress != null && !Progress.Finished ? Progress.Text() : "the last job is still running"); return null; }
                    if ((redo ? History.Redo : History.Undo) == 0) { Result = redo ? "nothing to redo" : "nothing to undo"; return null; }
                    Result = redo ? "redo..." : "undo...";
                    return redo ? PowerProtocol.Redo() : PowerProtocol.Undo();
                }
            }
            return null;
        }

        static string Off(string what, string key) => $"{what} are off on this server ({key})";

        /// <summary>box_edit over the wand's box; null with a reason in Result when it can't go.</summary>
        public Dictionary<string, object> Box(string op, string material, double now)
        {
            if (!Limits.Box) { Result = Off("box tools", "edit.box-max-blocks"); return null; }
            if (!HasBox) { Result = $"can't {op}: set corner A with the wand's left trigger first (Tools tab: Wand)"; return null; }
            if (Volume > Limits.BoxMaxBlocks) { Result = $"can't {op}: the box is {Volume} blocks, more than {Limits.BoxMaxBlocks} (edit.box-max-blocks)"; return null; }
            if (Busy(now)) { Result = "wait: " + (Progress != null && !Progress.Finished ? Progress.Text() : "the last job is still running"); return null; }
            bool needsState = op != "clear";
            if (needsState && (string.IsNullOrEmpty(material) || InteractProtocol.IsItem(material))) { Result = $"can't {op}: select a block (not an item) first"; return null; }
            if (op == "replace" && Filter == null) { Result = "can't replace: pick the block to replace with B first (a tap, while the wand is on)"; return null; }
            Result = $"{op} {Size.x}x{Size.y}x{Size.z} = {Volume} blocks" + (Limits.UndoDepth > 0 && Volume > Limits.UndoDepth ? " (bigger than undo's depth: undo it before your next edit)" : "");
            if (Limits.UndoDepth > 0 && Volume > Limits.UndoDepth) _toasts.Add(($"{Volume} blocks: undo works until your next edit", false));
            return PowerProtocol.BoxEdit(op, A.Value, B ?? A.Value, needsState ? material : null, op == "replace" ? Filter : null, null, 0, Groups.Begin());
        }

        // ---------- clone ----------

        /// <summary>The copy's size after its quarter turns.</summary>
        public Vector3Int PasteSize { get { var s = Size; return (Rotate & 1) == 1 ? new Vector3Int(s.z, s.y, s.x) : s; } }

        /// <summary>Where the copy's min corner goes for an aimed cell (the cell in front of the aimed face): the copy
        /// stands on it, centred in X and Z.</summary>
        public Vector3Int PasteOrigin(Vector3Int aimCell) { var s = PasteSize; return aimCell - new Vector3Int(s.x / 2, 0, s.z / 2); }

        public Dictionary<string, object> Paste(Vector3Int aimCell, double now)
        {
            if (!Pasting || !HasBox) return null;
            if (Busy(now)) { Result = "wait: the last job is still running"; return null; }
            var dest = PasteOrigin(aimCell);
            Result = $"clone {Size.x}x{Size.y}x{Size.z} to {Fmt(dest)}" + (Rotate != 0 ? $", turned {Rotate * 90}°" : "");
            return PowerProtocol.BoxEdit("clone", A.Value, B ?? A.Value, null, null, dest, Rotate, Groups.Begin());
        }

        // ---------- brushes ----------

        public string Shape => Form == BrushForm.Sphere ? "sphere" : "cylinder";
        public int Height => Form == BrushForm.Disc ? 1 : 2 * Radius + 1;

        /// <summary>Cells in a brush (what a stroke costs from brush-blocks-per-second), as the server counts them.</summary>
        public static int Cells(BrushForm form, int r)
        {
            int h = form == BrushForm.Disc ? 1 : 2 * r + 1, n = 0;
            long rr = (long)r * r + r;
            int y0 = form == BrushForm.Sphere ? -r : -((h - 1) / 2), y1 = form == BrushForm.Sphere ? r : h / 2;
            for (int dy = y0; dy <= y1; dy++)
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                        if (form == BrushForm.Sphere ? dx * dx + dy * dy + dz * dz <= rr : dx * dx + dz * dz <= rr) n++;
            return n;
        }

        /// <summary>The trigger went down with a brush: a new group for this hold, and the first stroke goes at once.</summary>
        public void BeginStroke() { Groups.Begin(); _lastStroke = -1; }

        /// <summary>
        /// A stroke at center while the trigger is held, or null: at most one per StrokeSeconds, none while the aim rests
        /// on the same block (after the first), none that would go over the server's blocks per second.
        /// </summary>
        public Dictionary<string, object> Stroke(Vector3Int center, string material, double now)
        {
            if (Mode != ToolMode.Brush || !Limits.Brush) return null;
            if (_lastStroke >= 0 && (now - _lastStroke < StrokeSeconds || (center == _lastCenter && now - _lastStroke < 0.5))) return null;
            bool needsState = BrushOp == "paint" || BrushOp == "replace";
            if (needsState && (string.IsNullOrEmpty(material) || InteractProtocol.IsItem(material))) { Result = "can't paint: select a block (not an item) first"; return null; }
            if (BrushOp == "replace" && Filter == null) { Result = "can't replace: pick the block to replace with B first"; return null; }
            int cost = Cells(Form, Radius);
            if (_tokensAt < 0) { _tokens = Limits.BrushBlocksPerSecond; _tokensAt = now; }
            _tokens = Math.Min(Limits.BrushBlocksPerSecond, _tokens + (now - _tokensAt) * Limits.BrushBlocksPerSecond);
            _tokensAt = now;
            if (_tokens < cost) return null;
            _tokens -= cost;
            _lastStroke = now; _lastCenter = center;
            return PowerProtocol.Brush(BrushOp, Shape, center, Radius, Form == BrushForm.Sphere ? (int?)null : Height, needsState ? material : null,
                                       BrushOp == "replace" ? Filter : null, Groups.Last);
        }

        // ---------- the panel ----------

        /// <summary>Label of a Tools tab button.</summary>
        public string Label(ToolButton b)
        {
            switch (b)
            {
                case ToolButton.Wand: return Mode == ToolMode.Wand ? "Wand: on" : "Wand: off";
                case ToolButton.Brush: return Mode == ToolMode.Brush ? "Brush: " + Form.ToString().ToLowerInvariant() : "Brush: off";
                case ToolButton.Clone: return Pasting ? "End paste" : "Clone";
                case ToolButton.Rotate: return $"Rotate ({Rotate * 90}°)";
                case ToolButton.ClearCorners: return "Clear corners";
                case ToolButton.BrushReplace: return "Replace";
                case ToolButton.RadiusDown: return "Radius -";
                case ToolButton.RadiusUp: return "Radius +";
                case ToolButton.Undo: return $"Undo ({History.Undo})";
                case ToolButton.Redo: return $"Redo ({History.Redo})";
                default: return b.ToString();
            }
        }

        /// <summary>A button that shows a current choice (lit).</summary>
        public bool Active(ToolButton b) =>
            b == ToolButton.Wand ? Mode == ToolMode.Wand : b == ToolButton.Brush ? Mode == ToolMode.Brush : b == ToolButton.Clone ? Pasting
            : Mode == ToolMode.Brush && (b == ToolButton.Paint && BrushOp == "paint" || b == ToolButton.BrushReplace && BrushOp == "replace"
                                         || b == ToolButton.Erase && BrushOp == "erase" || b == ToolButton.Smooth && BrushOp == "smooth");

        /// <summary>The Tools tab's rows of buttons.</summary>
        public static readonly ToolButton[][] Rows =
        {
            new[] { ToolButton.Wand, ToolButton.Brush },
            new[] { ToolButton.Fill, ToolButton.Replace, ToolButton.Clear },
            new[] { ToolButton.Walls, ToolButton.Hollow, ToolButton.Clone },
            new[] { ToolButton.Rotate, ToolButton.ClearCorners },
            new[] { ToolButton.Paint, ToolButton.BrushReplace, ToolButton.Erase, ToolButton.Smooth },
            new[] { ToolButton.RadiusDown, ToolButton.RadiusUp },
            new[] { ToolButton.Undo, ToolButton.Redo },
        };

        /// <summary>The Tools tab's text: the box, the material and filter, the brush, history, the running job.</summary>
        public List<string> Lines(string material, Func<string, string> label, double now)
        {
            var l = new List<string>();
            l.Add(HasBox ? $"Box {Size.x}x{Size.y}x{Size.z} = {Volume} blocks (max {Limits.BoxMaxBlocks})" + (Pasting ? $", copy {PasteSize.x}x{PasteSize.y}x{PasteSize.z}" : "")
                         : Mode == ToolMode.Wand ? "Box: aim and pull the left trigger for corner A" : "Box: none (turn the wand on)");
            l.Add($"Block: {(string.IsNullOrEmpty(material) ? "-" : label(material))}   Replace: {(Filter == null ? "- (B picks it)" : label(Filter))}");
            l.Add($"Brush: {Form.ToString().ToLowerInvariant()} {BrushOp}, radius {Radius} ({Cells(Form, Radius)} blocks)");
            l.Add($"Undo {History.Undo} ({History.UndoBlocks} blocks)  Redo {History.Redo} ({History.RedoBlocks} blocks)  hold B = undo");
            l.Add(Progress != null && !Progress.Finished ? Progress.Text() : Result);
            return l;
        }

        /// <summary>One line for the Info tab.</summary>
        public string Status =>
            !Limits.Allowed ? "off (the server has no power_v1)"
            : $"undo {History.Undo} ({History.UndoBlocks} blocks), redo {History.Redo}  tool {(Mode == ToolMode.Wand ? (Pasting ? "paste" : "wand") : Mode == ToolMode.Brush ? "brush" : "none")}" +
              (HasBox ? $"  box {Size.x}x{Size.y}x{Size.z}" : "") + (Progress != null && !Progress.Finished ? "  " + Progress.Text() : "");

        static string Fmt(Vector3Int v) => $"{v.x},{v.y},{v.z}";
    }
}
