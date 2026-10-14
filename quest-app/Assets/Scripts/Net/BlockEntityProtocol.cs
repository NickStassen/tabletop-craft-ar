// block_entities (PROTOCOL.md 4.26): what the server knows about block entities in the region that the block state
// doesn't say: sign text, banner patterns, player head skins, pot sherds, spawned mobs, beacon tiers, campfire, shelf
// and vault items, conduit activity. Engine-free: BlockEntityStore keeps the latest entry per world position;
// BlockEntityRenderer draws from it, Tests~ parses the fixtures.
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.Net
{
    public sealed class SignSideText
    {
        public string[] Lines = { "", "", "", "" };
        public string Color = "black";
        public bool Glowing;

        public static SignSideText From(Dictionary<string, object> o)
        {
            var s = new SignSideText();
            if (o == null) return s;
            if (o.A("lines") is List<object> l)
                for (int i = 0; i < 4 && i < l.Count; i++) s.Lines[i] = l[i] as string ?? "";
            s.Color = o.S("color") ?? "black";
            s.Glowing = o.B("glowing");
            return s;
        }

        public bool Empty { get { foreach (var l in Lines) if (!string.IsNullOrEmpty(l)) return false; return true; } }
    }

    public struct BannerLayer
    {
        public string Pattern, Color;
        public BannerLayer(string pattern, string color) { Pattern = pattern; Color = color; }
    }

    /// <summary>One block_entities entry. Only the fields of its kind are set.</summary>
    public sealed class BlockEntityData
    {
        public Vector3Int Pos;            // world block coordinates
        public string Kind;               // "sign", "banner", "skull", "decorated_pot", "spawner", "trial_spawner", "beacon", "campfire", "shelf", "vault", "conduit"
        public SignSideText Front, Back;  // signs and hanging signs
        public List<BannerLayer> Patterns;
        public string Skin, SkinName, SkinUuid;   // player heads: skin URL (textures.minecraft.net), profile name and UUID
        public string[] Sherds;           // decorated pots: back, left, right, front (item ids; brick = plain side)
        public string Entity;             // spawners: the spawned entity type
        public float EntityWidth, EntityHeight;
        public bool Ominous;              // trial spawners
        public int Tier;                  // beacons: pyramid levels 0-4
        public string[] Items;            // campfires (4) and shelves (3): item ids or null
        public string Item;               // vaults: the displayed item
        public bool Active;               // conduits

        public static BlockEntityData From(Dictionary<string, object> o)
        {
            var p = o.A("pos");
            if (p == null || p.Count != 3) return null;
            var d = new BlockEntityData { Pos = new Vector3Int((int)(double)p[0], (int)(double)p[1], (int)(double)p[2]), Kind = o.S("kind") ?? "" };
            switch (d.Kind)
            {
                case "sign":
                    d.Front = SignSideText.From(o.O("front"));
                    d.Back = SignSideText.From(o.O("back"));
                    break;
                case "banner":
                    d.Patterns = new List<BannerLayer>();
                    if (o.A("patterns") is List<object> pl)
                        foreach (var e in pl)
                            if (e is Dictionary<string, object> l && l.S("pattern") != null) d.Patterns.Add(new BannerLayer(l.S("pattern"), l.S("color") ?? "white"));
                    break;
                case "skull":
                    d.Skin = o.S("skin"); d.SkinName = o.S("name"); d.SkinUuid = o.S("uuid");
                    break;
                case "decorated_pot":
                    d.Sherds = Strings(o.A("sherds"), 4);
                    break;
                case "spawner":
                case "trial_spawner":
                    d.Entity = o.S("entity");
                    if (o.A("size") is List<object> sz && sz.Count == 2) { d.EntityWidth = (float)(double)sz[0]; d.EntityHeight = (float)(double)sz[1]; }
                    d.Ominous = o.B("ominous");
                    break;
                case "beacon": d.Tier = o.I("tier"); break;
                case "campfire": d.Items = Strings(o.A("items"), 4); break;
                case "shelf": d.Items = Strings(o.A("items"), 3); break;
                case "vault": d.Item = o.S("item"); break;
                case "conduit": d.Active = o.B("active"); break;
            }
            return d;
        }

        static string[] Strings(List<object> l, int n)
        {
            var a = new string[n];
            if (l != null) for (int i = 0; i < n && i < l.Count; i++) a[i] = l[i] as string;
            return a;
        }
    }

    /// <summary>The latest block_entities entry per world position (main thread).</summary>
    public sealed class BlockEntityStore
    {
        public readonly Dictionary<Vector3Int, BlockEntityData> All = new Dictionary<Vector3Int, BlockEntityData>();

        public BlockEntityData Get(Vector3Int world) => All.TryGetValue(world, out var d) ? d : null;

        /// <summary>Applies one message; changed gets every position that was set or removed.</summary>
        public void Apply(Dictionary<string, object> m, List<Vector3Int> changed)
        {
            if (m.A("entities") is List<object> list)
                foreach (var e in list)
                {
                    if (!(e is Dictionary<string, object> o)) continue;
                    var d = BlockEntityData.From(o);
                    if (d == null) continue;
                    All[d.Pos] = d;
                    changed?.Add(d.Pos);
                }
            if (m.A("removed") is List<object> rem)
                foreach (var r in rem)
                    if (r is List<object> p && p.Count == 3)
                    {
                        var k = new Vector3Int((int)(double)p[0], (int)(double)p[1], (int)(double)p[2]);
                        if (All.Remove(k)) changed?.Add(k);
                    }
        }

        /// <summary>Drops entries outside a region (world origin, dims): the server forgets what it sent there too.</summary>
        public void Prune(Vector3Int origin, Vector3Int dims)
        {
            List<Vector3Int> gone = null;
            foreach (var k in All.Keys)
                if (k.x < origin.x || k.y < origin.y || k.z < origin.z || k.x >= origin.x + dims.x || k.y >= origin.y + dims.y || k.z >= origin.z + dims.z)
                    (gone ??= new List<Vector3Int>()).Add(k);
            if (gone != null) foreach (var k in gone) All.Remove(k);
        }
    }
}
