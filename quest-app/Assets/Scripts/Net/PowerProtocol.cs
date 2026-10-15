// [power] Undo/redo, box edits and brushes (docs/PROTOCOL.md "Undo and redo", "Box edits", "Brushes"; cap power_v1):
// message builders and typed views of welcome.limits, history and box_progress, engine-free (Tests~ parses the golden
// fixtures with them).
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TabletopCraft.Net
{
    public static class PowerProtocol
    {
        public const string Cap = "power_v1";

        static List<object> V(Vector3Int v) => new List<object> { v.x, v.y, v.z };

        public static Dictionary<string, object> Undo() => new Dictionary<string, object> { ["type"] = "undo" };
        public static Dictionary<string, object> Redo() => new Dictionary<string, object> { ["type"] = "redo" };

        /// <summary>box_edit: op fill, replace, clear, walls, hollow or clone over the box a..b (world, inclusive, any
        /// order); state for all but clear and clone, filter for replace, dest (the copy's min corner) and quarter turns
        /// for clone.</summary>
        public static Dictionary<string, object> BoxEdit(string op, Vector3Int a, Vector3Int b, string state, string filter, Vector3Int? dest, int rotate, int group)
        {
            var d = new Dictionary<string, object> { ["type"] = "box_edit", ["op"] = op, ["from"] = V(a), ["to"] = V(b) };
            if (state != null) d["state"] = state;
            if (filter != null) d["filter"] = filter;
            if (dest.HasValue) { d["dest"] = V(dest.Value); d["rotate"] = rotate & 3; }
            if (group > 0) d["group"] = group;
            return d;
        }

        /// <summary>brush: one stroke of a sphere or an upright cylinder (height layers; 1 = a flat disc) at center.</summary>
        public static Dictionary<string, object> Brush(string op, string shape, Vector3Int center, int radius, int? height, string state, string filter, int group)
        {
            var d = new Dictionary<string, object> { ["type"] = "brush", ["op"] = op, ["shape"] = shape, ["center"] = V(center), ["radius"] = radius };
            if (height.HasValue && shape == "cylinder") d["height"] = height.Value;
            if (state != null) d["state"] = state;
            if (filter != null) d["filter"] = filter;
            if (group > 0) d["group"] = group;
            return d;
        }
    }

    /// <summary>welcome.caps holds power_v1, and welcome.limits says how far it goes (0 = that tool is off).</summary>
    public sealed class PowerLimits
    {
        public bool Allowed;
        public int UndoDepth, BoxMaxBlocks, BrushMaxRadius, BrushBlocksPerSecond;
        public bool Undo => Allowed && UndoDepth > 0;
        public bool Box => Allowed && BoxMaxBlocks > 0;
        public bool Brush => Allowed && BrushBlocksPerSecond > 0 && BrushMaxRadius > 0;

        public static PowerLimits From(Dictionary<string, object> welcome)
        {
            var l = new PowerLimits { Allowed = welcome?.A("caps")?.Contains(PowerProtocol.Cap) == true };
            var lim = welcome?.O("limits");
            if (!l.Allowed || lim == null) return l;
            l.UndoDepth = lim.I("undo_depth"); l.BoxMaxBlocks = lim.I("box_max_blocks");
            l.BrushMaxRadius = lim.I("brush_max_radius"); l.BrushBlocksPerSecond = lim.I("brush_blocks_per_second");
            return l;
        }
    }

    /// <summary>history: how many groups (and blocks) the server can undo and redo for this headset.</summary>
    public sealed class PowerHistory
    {
        public int Undo, Redo, UndoBlocks, RedoBlocks, Depth;

        public static PowerHistory From(Dictionary<string, object> m) => new PowerHistory
        {
            Undo = m.I("undo"), Redo = m.I("redo"), UndoBlocks = m.I("undo_blocks"), RedoBlocks = m.I("redo_blocks"), Depth = m.I("depth"),
        };
    }

    /// <summary>box_progress: a box edit, undo or redo running (or finished, with what it changed and skipped).</summary>
    public sealed class BoxProgress
    {
        public string Op, Phase;
        public int Group = -1;
        public long RefSeq = -1;
        public long Processed, Total, Changed, Skipped, Outside;
        public bool Finished;
        public readonly List<Vector3Int> SkippedPos = new List<Vector3Int>();
        public double Ms;
        public int Ticks;

        public static BoxProgress From(Dictionary<string, object> m)
        {
            var p = new BoxProgress
            {
                Op = m.S("op"), Phase = m.S("phase"), Group = m.I("group", -1), RefSeq = m.ContainsKey("ref_seq") ? (long)m.D("ref_seq") : -1,
                Processed = (long)m.D("processed"), Total = (long)m.D("total"), Changed = (long)m.D("changed"), Skipped = (long)m.D("skipped"),
                Outside = (long)m.D("outside"), Finished = m.B("finished"), Ms = m.D("ms"), Ticks = m.I("ticks"),
            };
            foreach (var o in m.A("skipped_pos") ?? new List<object>())
                if (o is List<object> a && a.Count == 3) p.SkippedPos.Add(new Vector3Int((int)(double)a[0], (int)(double)a[1], (int)(double)a[2]));
            return p;
        }

        public int Percent => Total <= 0 ? 0 : (int)(Processed * 100 / Total);

        static string Verb(string op, bool done)
        {
            switch (op)
            {
                case "fill": return done ? "Filled" : "Filling";
                case "replace": return done ? "Replaced" : "Replacing";
                case "clear": return done ? "Cleared" : "Clearing";
                case "walls": return done ? "Walled" : "Building walls";
                case "hollow": return done ? "Hollowed" : "Hollowing";
                case "clone": return done ? "Cloned" : "Cloning";
                case "undo": return done ? "Undone" : "Undoing";
                case "redo": return done ? "Redone" : "Redoing";
                default: return op ?? "?";
            }
        }

        /// <summary>"Filling 12000 / 32768 (36 %)" while it runs; "Undone 12 blocks; 2 changed since, left as they are" when done.</summary>
        public string Text()
        {
            if (!Finished) return $"{Verb(Op, false)} {Processed} / {Total} ({Percent} %)";
            string s = $"{Verb(Op, true)} {Changed} block{(Changed == 1 ? "" : "s")}";
            if (Ms >= 1000) s += " in " + (Ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            if (Skipped > 0) s += $"; {Skipped} changed since, left as {(Skipped == 1 ? "it is" : "they are")}";
            if (Outside > 0) s += $"; {Outside} outside the area now";
            if (Phase == "failed") s += " (stopped: server error)";
            return s;
        }
    }
}
