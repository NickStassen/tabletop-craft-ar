// [hands] Using blocks, items and mobs (docs/PROTOCOL.md "Using blocks and items", "Acting on mobs"; cap interact_v1):
// message builders and typed views of item_registry, welcome.interact and entity_info, engine-free (Tests~ parses the
// golden fixtures with them).
//
// The hotbar and the block menu hold block states and items side by side: an item is written "item:<id>"
// ("item:minecraft:bone_meal"), so it can never be mistaken for a block state of the same name (the wheat item
// versus the wheat crop) and prefs.json keeps it as it is.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TabletopCraft.Net
{
    public static class InteractProtocol
    {
        public const string Cap = "interact_v1";
        public const string ItemPrefix = "item:";
        public const string Lightning = "tabletop:lightning";
        public const string NameTag = "minecraft:name_tag", Bucket = "minecraft:bucket";

        /// <summary>The selection is an item ("item:minecraft:bone_meal"), not a block state.</summary>
        public static bool IsItem(string selection) => selection != null && selection.StartsWith(ItemPrefix, StringComparison.Ordinal);
        /// <summary>"item:minecraft:bone_meal" -> "minecraft:bone_meal"; null for a block state.</summary>
        public static string ItemId(string selection) => IsItem(selection) ? selection.Substring(ItemPrefix.Length) : null;
        public static string Selection(string itemId) => ItemPrefix + itemId;

        /// <summary>The spawn egg of an entity type ("minecraft:pig" -> "minecraft:pig_spawn_egg").</summary>
        public static string EggOf(string entityType)
        {
            if (string.IsNullOrEmpty(entityType)) return null;
            int c = entityType.IndexOf(':');
            return (c < 0 ? "minecraft:" + entityType : entityType) + "_spawn_egg";
        }

        /// <summary>The item whose model is the icon: the lightning wand looks like a lightning rod.</summary>
        public static string IconItem(string itemId) => itemId == Lightning ? "minecraft:lightning_rod" : itemId;

        static List<object> V(Vector3 v, int digits) => new List<object> { Math.Round(v.x, digits), Math.Round(v.y, digits), Math.Round(v.z, digits) };

        /// <summary>The unit vector along v, or null for a (near) zero vector.</summary>
        static Vector3? Unit(Vector3 v)
        {
            double len = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            return len < 1e-6 ? (Vector3?)null : v * (float)(1 / len);
        }

        /// <summary>interact: the clicked block (world), its face, the hit point inside it (0..1), the item (null = empty hand),
        /// sneak (informational) and the ray direction (world axes).</summary>
        public static Dictionary<string, object> Interact(Vector3Int pos, string face, Vector3? hit, string item, bool sneak, Vector3? dir)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = "interact", ["pos"] = new List<object> { pos.x, pos.y, pos.z }, ["face"] = face, ["item"] = item, ["sneak"] = sneak,
            };
            if (hit.HasValue)
            {
                var h = hit.Value;
                d["hit"] = V(new Vector3(Mathf.Clamp01(h.x), Mathf.Clamp01(h.y), Mathf.Clamp01(h.z)), 3);
            }
            if (dir.HasValue && Unit(dir.Value) is Vector3 u) d["dir"] = V(u, 3);
            return d;
        }

        /// <summary>entity_op: op on the entity uuid; to (world, move/hold/drop), yaw, dir (attack knockback), item (use), text (name).</summary>
        public static Dictionary<string, object> EntityOp(string uuid, string op, Vector3? to = null, float? yaw = null, Vector3? dir = null, string item = null, string text = null)
        {
            var d = new Dictionary<string, object> { ["type"] = "entity_op", ["uuid"] = uuid, ["op"] = op };
            if (to.HasValue) d["to"] = V(to.Value, 2);
            if (yaw.HasValue) d["yaw"] = Math.Round(yaw.Value, 1);
            if (dir.HasValue && Unit(dir.Value) is Vector3 u) d["dir"] = V(u, 3);
            if (item != null) d["item"] = item;
            if (text != null) d["text"] = text;
            return d;
        }
    }

    /// <summary>welcome.interact: what the server lets the headset do (null fields: the cap wasn't granted).</summary>
    public sealed class InteractWelcome
    {
        public bool Allowed, IronDoors = true, Fire = true, Tnt = true, Fluids = true, Entities = true, HarmPlayers;
        public string Lightning = "real";
        public double AttackDamage = 5;
        public int SpawnsPerMinute = 60;

        public static InteractWelcome From(Dictionary<string, object> welcome)
        {
            var w = new InteractWelcome { Allowed = welcome?.A("caps")?.Contains(InteractProtocol.Cap) == true };
            var o = welcome?.O("interact");
            if (o == null) return w;
            w.IronDoors = o.B("iron_doors", true); w.Fire = o.B("fire", true); w.Tnt = o.B("tnt", true); w.Fluids = o.B("fluids", true);
            w.Entities = o.B("entities", true); w.HarmPlayers = o.B("harm_players");
            w.Lightning = o.S("lightning") ?? "real"; w.AttackDamage = o.D("attack_damage", 5); w.SpawnsPerMinute = o.I("max_spawns_per_minute", 60);
            return w;
        }
    }

    /// <summary>item_registry: the usable items and the spawn eggs, as menu entries (State = "item:&lt;id&gt;").</summary>
    public sealed class ItemRegistry
    {
        public const string Type = "item_registry";
        public readonly List<BlockEntry> Use = new List<BlockEntry>(), Eggs = new List<BlockEntry>();
        public readonly Dictionary<string, BlockEntry> BySelection = new Dictionary<string, BlockEntry>();
        public int Count => Use.Count + Eggs.Count;

        public static ItemRegistry From(Dictionary<string, object> m)
        {
            var r = new ItemRegistry();
            var cats = m.A("categories") ?? throw new FormatException("item_registry without categories");
            foreach (var co in cats)
            {
                if (!(co is Dictionary<string, object> c)) throw new FormatException("item_registry category is not an object");
                var list = c.S("id") == "spawn_eggs" ? r.Eggs : r.Use;
                foreach (var io in c.A("items") ?? new List<object>())
                {
                    if (!(io is string id) || id.IndexOf(':') < 0) throw new FormatException("item_registry item that isn't an item id");
                    string sel = InteractProtocol.Selection(id);
                    if (r.BySelection.ContainsKey(sel)) continue;
                    string path = BlockRegistry.PathOf(id);
                    var e = new BlockEntry
                    {
                        State = sel, Id = sel, Path = path, Item = InteractProtocol.IconItem(id), Category = list == r.Eggs ? 1 : 0, Order = r.BySelection.Count,
                        Label = id == InteractProtocol.Lightning ? "Lightning" : BlockRegistry.DefaultLabel(path),
                    };
                    e.SearchText = BlockRegistry.SearchKey(e.Label, path);
                    list.Add(e); r.BySelection[sel] = e;
                }
            }
            return r;
        }

        /// <summary>The entry of a selection ("item:minecraft:shears"), or null.</summary>
        public BlockEntry Find(string selection) => selection != null && BySelection.TryGetValue(selection, out var e) ? e : null;

        /// <summary>Names from the pack's language file ("item.minecraft.bone_meal" -> "Bone Meal").</summary>
        public void ApplyNames(Func<string, string> lang)
        {
            if (lang == null) return;
            foreach (var e in BySelection.Values)
            {
                string id = InteractProtocol.ItemId(e.State);
                if (id == InteractProtocol.Lightning) continue;
                int c = id.IndexOf(':');
                string name = lang("item." + id.Substring(0, c) + "." + id.Substring(c + 1));
                if (string.IsNullOrEmpty(name)) continue;
                e.Label = name;
                e.SearchText = BlockRegistry.SearchKey(name, e.Path);
            }
        }
    }

    /// <summary>entity_info: what the server says about one entity (the info card).</summary>
    public sealed class EntityInfoMessage
    {
        public string Uuid, EntityType, Name, Owner, Vehicle;
        public int Id, Passengers;
        public Vector3 Pos;
        public float Health = -1, MaxHealth = -1;
        public int? Age;
        public bool Baby, Glowing, Held, Leashed, Player;
        public bool? Ai, Frozen;

        public static EntityInfoMessage From(Dictionary<string, object> m)
        {
            var p = m.A("pos");
            var i = new EntityInfoMessage
            {
                Uuid = m.S("uuid"), EntityType = m.S("entity_type"), Name = m.S("name"), Owner = m.S("owner"), Vehicle = m.S("vehicle"),
                Id = m.I("id", -1), Passengers = m.I("passengers"), Baby = m.B("baby"), Glowing = m.B("glowing"), Held = m.B("held"),
                Leashed = m.B("leashed"), Player = m.B("player"),
                Pos = p != null && p.Count == 3 ? new Vector3((float)(double)p[0], (float)(double)p[1], (float)(double)p[2]) : new Vector3(0, 0, 0),
            };
            if (m.A("health") is List<object> h && h.Count == 2) { i.Health = (float)(double)h[0]; i.MaxHealth = (float)(double)h[1]; }
            if (m.ContainsKey("age")) i.Age = m.I("age");
            if (m.TryGetValue("ai", out var ai) && ai is bool a) i.Ai = a;
            if (m.TryGetValue("frozen", out var fz) && fz is bool f) i.Frozen = f;
            return i;
        }

        /// <summary>"Wolf" from "minecraft:wolf".</summary>
        public string TypeLabel => BlockRegistry.DefaultLabel(BlockRegistry.PathOf(EntityType ?? "?"));

        /// <summary>The card's text: the name, then health, owner, age, state.</summary>
        public List<string> Lines()
        {
            var l = new List<string>();
            l.Add(Player ? Name : string.IsNullOrEmpty(Name) ? TypeLabel : $"{TypeLabel} \"{Name}\"");
            if (Health >= 0) l.Add($"Health {Num(Health)} / {Num(MaxHealth)}");
            if (Owner != null) l.Add("Owner " + Owner);
            if (Age.HasValue && Age.Value < 0) l.Add($"Baby, grows up in {Clock(-Age.Value / 20)}");
            else if (Baby) l.Add("Baby");
            var state = new List<string>();
            if (Frozen == true) state.Add("frozen");
            else if (Ai == false) state.Add(Held ? "held" : "AI off");
            if (Glowing) state.Add("glowing");
            if (Leashed) state.Add("on a lead");
            if (Vehicle != null) state.Add("riding " + BlockRegistry.DefaultLabel(BlockRegistry.PathOf(Vehicle)).ToLowerInvariant());
            if (Passengers > 0) state.Add(Passengers == 1 ? "1 passenger" : Passengers + " passengers");
            if (state.Count > 0) l.Add(string.Join(", ", state));
            l.Add($"at {Num(Pos.x)}, {Num(Pos.y)}, {Num(Pos.z)}");
            return l;
        }

        static string Num(float v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
        static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
    }
}
