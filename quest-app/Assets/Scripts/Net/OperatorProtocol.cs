// [operator] Marks (bookmarks, spawn, worlds; cap marks_v1), the container view and the sign editor (interact_v1)
// (docs/PROTOCOL.md "Following a player", "Marks: bookmarks, spawn and worlds", "Containers", "Signs"): message
// builders and typed views of the server's messages, engine-free (Tests~ parses the golden fixtures with them).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.Net
{
    public static class OperatorProtocol
    {
        public const string CapMarks = "marks_v1";

        static List<object> P(Vector3Int p) => new List<object> { p.x, p.y, p.z };

        /// <summary>bookmark add: a named world position every headset sees.</summary>
        public static Dictionary<string, object> BookmarkAdd(string name, string world, Vector3Int pos) =>
            new Dictionary<string, object> { ["type"] = "bookmark", ["op"] = "add", ["name"] = name, ["world"] = world, ["pos"] = P(pos) };

        public static Dictionary<string, object> BookmarkRemove(string name) =>
            new Dictionary<string, object> { ["type"] = "bookmark", ["op"] = "remove", ["name"] = name };

        public static Dictionary<string, object> ContainerQuery(Vector3Int pos) => new Dictionary<string, object> { ["type"] = "container_query", ["pos"] = P(pos) };
        public static Dictionary<string, object> ContainerClose(Vector3Int pos) => new Dictionary<string, object> { ["type"] = "container_close", ["pos"] = P(pos) };
        public static Dictionary<string, object> ContainerSort(Vector3Int pos) => new Dictionary<string, object> { ["type"] = "container_edit", ["pos"] = P(pos), ["op"] = "sort" };

        /// <summary>container_edit set: slot = count x item (null or 0 empties it), only if it still holds was (wasItem x wasCount).</summary>
        public static Dictionary<string, object> ContainerSet(Vector3Int pos, int slot, string item, int count, string wasItem, int wasCount) =>
            new Dictionary<string, object>
            {
                ["type"] = "container_edit", ["pos"] = P(pos), ["slot"] = slot, ["item"] = count > 0 ? item : null, ["count"] = item == null ? 0 : Math.Max(0, count),
                ["was"] = new List<object> { wasItem, wasItem == null ? 0 : wasCount },
            };

        /// <summary>sign_edit: one side's four lines, its dye colour and glowing ink.</summary>
        public static Dictionary<string, object> SignEdit(Vector3Int pos, bool back, IList<string> lines, string color, bool glowing)
        {
            var l = new List<object>();
            for (int i = 0; i < 4; i++) l.Add(i < lines.Count && lines[i] != null ? lines[i] : "");
            return new Dictionary<string, object>
            {
                ["type"] = "sign_edit", ["pos"] = P(pos), ["side"] = back ? "back" : "front", ["lines"] = l, ["color"] = color ?? "black", ["glowing"] = glowing,
            };
        }

        internal static Vector3Int V3(List<object> a) =>
            a != null && a.Count == 3 ? new Vector3Int((int)(double)a[0], (int)(double)a[1], (int)(double)a[2]) : new Vector3Int(0, 0, 0);

        public static readonly string[] Dyes = { "white", "light_gray", "gray", "black", "brown", "red", "orange", "yellow", "lime", "green", "cyan",
                                                 "light_blue", "blue", "purple", "magenta", "pink" };
    }

    /// <summary>A world the headset can switch to (welcome.worlds, region_info.worlds).</summary>
    public struct WorldEntry
    {
        public string Name, Env;
        public int MinY, MaxY;
        public Vector3Int Spawn;

        /// <summary>"Overworld", "Nether", "End" for the vanilla environments; else the world's name.</summary>
        public string Label => Env == "normal" ? (Name == "world" ? "Overworld" : Name) : Env == "nether" ? "Nether" : Env == "the_end" ? "End" : Name;

        public static List<WorldEntry> ListFrom(List<object> a)
        {
            var l = new List<WorldEntry>();
            if (a == null) return l;
            foreach (var o in a)
            {
                if (!(o is Dictionary<string, object> w) || w.S("name") == null) continue;
                l.Add(new WorldEntry { Name = w.S("name"), Env = w.S("env") ?? "normal", MinY = w.I("min_y", -64), MaxY = w.I("max_y", 320), Spawn = OperatorProtocol.V3(w.A("spawn")) });
            }
            return l;
        }
    }

    /// <summary>What welcome granted for the operator tools: bookmarks (marks_v1) and, with interact_v1, the container view and sign editor.</summary>
    public sealed class OperatorWelcome
    {
        public bool Marks, MarksEdit, Signs;
        public int MarksMax = 200;
        /// <summary>"edit", "view" or "off".</summary>
        public string Containers = "off";
        public List<WorldEntry> Worlds = new List<WorldEntry>();

        public bool ContainersOn => Containers == "edit" || Containers == "view";

        public static OperatorWelcome From(Dictionary<string, object> welcome)
        {
            var w = new OperatorWelcome();
            var caps = welcome?.A("caps");
            w.Marks = caps?.Contains(OperatorProtocol.CapMarks) == true;
            var m = welcome?.O("marks");
            if (m != null) { w.MarksMax = m.I("max", 200); w.MarksEdit = m.B("edit", true); }
            var i = welcome?.O("interact");
            if (caps?.Contains(InteractProtocol.Cap) == true && i != null)
            {
                w.Containers = i.S("containers") ?? "off";
                w.Signs = i.B("signs");
            }
            w.Worlds = WorldEntry.ListFrom(welcome?.A("worlds"));
            return w;
        }
    }

    /// <summary>A shared bookmark.</summary>
    public struct Bookmark
    {
        public string Name, World, By;
        public Vector3Int Pos;
    }

    /// <summary>bookmarks (S->C): every bookmark, sorted by name.</summary>
    public sealed class BookmarkList
    {
        public int Max = 200;
        public readonly List<Bookmark> Marks = new List<Bookmark>();

        public static BookmarkList From(Dictionary<string, object> m)
        {
            var b = new BookmarkList { Max = m.I("max", 200) };
            foreach (var o in m.A("marks") ?? new List<object>())
            {
                if (!(o is Dictionary<string, object> k) || k.S("name") == null || k.S("world") == null) continue;
                b.Marks.Add(new Bookmark { Name = k.S("name"), World = k.S("world"), By = k.S("by"), Pos = OperatorProtocol.V3(k.A("pos")) });
            }
            return b;
        }
    }

    /// <summary>container (S->C): a container's filled slots, or (Closed) the end of the view.</summary>
    public sealed class ContainerView
    {
        public long RefSeq = -1;
        public Vector3Int Pos;
        public Vector3Int? Other;
        public string Block, Title, Reason;
        public int Size;
        public bool Edit, Closed;
        public readonly Dictionary<int, (string Item, int Count)> Items = new Dictionary<int, (string, int)>();

        public (string Item, int Count) Slot(int i) => Items.TryGetValue(i, out var s) ? s : (null, 0);

        public static ContainerView From(Dictionary<string, object> m)
        {
            var v = new ContainerView
            {
                RefSeq = (long)m.D("ref_seq", -1), Pos = OperatorProtocol.V3(m.A("pos")), Block = m.S("block") ?? "", Title = m.S("title") ?? "",
                Size = m.I("size"), Edit = m.S("edit") == "edit", Closed = m.B("closed"), Reason = m.S("reason"),
            };
            if (m.A("other") is List<object> o && o.Count == 3) v.Other = OperatorProtocol.V3(o);
            foreach (var e in m.A("items") ?? new List<object>())
            {
                if (!(e is List<object> s) || s.Count != 3 || !(s[1] is string id)) throw new FormatException("container item must be [slot, item, count]");
                int slot = (int)(double)s[0], n = (int)(double)s[2];
                if (slot < 0 || slot >= Math.Max(1, v.Size) || n <= 0) throw new FormatException($"container slot {slot} x{n} outside the container");
                v.Items[slot] = (id, n);
            }
            return v;
        }
    }
}
