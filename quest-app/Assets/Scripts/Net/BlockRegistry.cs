// block_registry (PROTOCOL.md 4.24): every block the server lets the headset place, by menu category, with its default
// state. Engine-free (Tests~). The block menu (XR/BlockMenu.cs, World/BlockMenuModel.cs) lists these; a placed state
// must be one of them or a variant of one (a wall torch is the torch's), so the client checks before sending.
using System;
using System.Collections.Generic;
using System.Text;

namespace TabletopCraft.Net
{
    public sealed class BlockEntry
    {
        public string State;     // default state, "minecraft:oak_log[axis=y]"
        public string Id;        // "minecraft:oak_log"
        public string Path;      // "oak_log"
        public string Item;      // item id for icons and names ("minecraft:redstone" for redstone_wire), = Id usually
        public int Category;     // index into BlockRegistry.Categories
        public int Order;        // position in the whole registry (menu order)
        public string Label;     // display name: the pack's en_us name, or "Oak Log" from the path
        public string SearchText;   // lower case label + path words, for search

        public override string ToString() => State;
    }

    public sealed class BlockRegistry
    {
        public const string Type = "block_registry";
        public readonly List<string> CategoryIds = new List<string>();
        public readonly List<string> CategoryLabels = new List<string>();
        public readonly List<List<BlockEntry>> ByCategory = new List<List<BlockEntry>>();
        public readonly List<BlockEntry> Entries = new List<BlockEntry>();
        public readonly Dictionary<string, BlockEntry> ById = new Dictionary<string, BlockEntry>();
        /// <summary>Block id -> registry block id for blocks placed with another block's item (wall torch -> torch).</summary>
        public readonly Dictionary<string, string> Variants = new Dictionary<string, string>();
        /// <summary>Bumped when names change (the pack's language file arrived).</summary>
        public int NamesVersion;

        public int Count => Entries.Count;

        /// <summary>Parses the message; throws FormatException when it is malformed.</summary>
        public static BlockRegistry From(Dictionary<string, object> m)
        {
            var r = new BlockRegistry();
            var cats = m.A("categories") ?? throw new FormatException("block_registry without categories");
            var items = m.O("items");
            foreach (var co in cats)
            {
                if (!(co is Dictionary<string, object> c) || c.S("id") == null) throw new FormatException("block_registry category without id");
                int ci = r.CategoryIds.Count;
                r.CategoryIds.Add(c.S("id"));
                r.CategoryLabels.Add(c.S("label") ?? DefaultLabel(c.S("id")));
                var list = new List<BlockEntry>();
                r.ByCategory.Add(list);
                foreach (var so in c.A("blocks") ?? new List<object>())
                {
                    if (!(so is string s) || s.Trim().Length == 0) throw new FormatException("block_registry block that isn't a state string");
                    string id = IdOf(s);
                    if (r.ById.ContainsKey(id)) continue;   // listed twice: keep the first
                    var e = new BlockEntry { State = s, Id = id, Path = PathOf(id), Category = ci, Order = r.Entries.Count };
                    e.Item = items?.S(id) ?? id;
                    e.Label = DefaultLabel(e.Path);
                    e.SearchText = SearchKey(e.Label, e.Path);
                    list.Add(e); r.Entries.Add(e); r.ById[id] = e;
                }
            }
            if (m.O("variants") is Dictionary<string, object> v)
                foreach (var kv in v) if (kv.Value is string home && r.ById.ContainsKey(home)) r.Variants[kv.Key] = home;
            return r;
        }

        /// <summary>The registry entry a state belongs to: its own block's, or the block a variant is placed with. Null if none.</summary>
        public BlockEntry Find(string stateOrId)
        {
            if (string.IsNullOrEmpty(stateOrId)) return null;
            string id = IdOf(stateOrId);
            if (ById.TryGetValue(id, out var e)) return e;
            return Variants.TryGetValue(id, out var home) && ById.TryGetValue(home, out e) ? e : null;
        }

        /// <summary>The server accepts a block_edit place of this state (PROTOCOL.md 4.24).</summary>
        public bool IsPlaceable(string state) => Find(state) != null;

        /// <summary>Display names from the pack's language file (assets/minecraft/lang/en_us.json): the item's name when the
        /// block is placed with a differently named item (Redstone Dust), else the block's ("Block of Diamond").</summary>
        public void ApplyNames(Func<string, string> lang)
        {
            if (lang == null) return;
            foreach (var e in Entries)
            {
                string name = null;
                if (e.Item != e.Id) name = lang(Key("item", e.Item));
                name = name ?? lang(Key("block", e.Id)) ?? lang(Key("item", e.Item));
                if (string.IsNullOrEmpty(name)) continue;
                e.Label = name;
                e.SearchText = SearchKey(name, e.Path);
            }
            NamesVersion++;
        }

        static string Key(string kind, string id)
        {
            int c = id.IndexOf(':');
            return kind + "." + (c < 0 ? "minecraft." + id : id.Substring(0, c) + "." + id.Substring(c + 1));
        }

        /// <summary>"minecraft:oak_log[axis=y]" -> "minecraft:oak_log"; ids without a namespace get "minecraft:".</summary>
        public static string IdOf(string state)
        {
            int b = state.IndexOf('[');
            string id = b < 0 ? state : state.Substring(0, b);
            return id.IndexOf(':') < 0 ? "minecraft:" + id : id;
        }

        public static string PathOf(string id) { int c = id.IndexOf(':'); return c < 0 ? id : id.Substring(c + 1); }

        /// <summary>"light_blue_stained_glass" -> "Light Blue Stained Glass".</summary>
        public static string DefaultLabel(string path)
        {
            var sb = new StringBuilder(path.Length);
            bool up = true;
            foreach (char ch in path)
            {
                if (ch == '_') { sb.Append(' '); up = true; continue; }
                sb.Append(up ? char.ToUpperInvariant(ch) : ch);
                up = false;
            }
            return sb.ToString();
        }

        /// <summary>Lower case label and path words, space separated ("block of diamond diamond block").</summary>
        public static string SearchKey(string label, string path) => (label.ToLowerInvariant() + " " + path.Replace('_', ' ')).Trim();
    }
}
