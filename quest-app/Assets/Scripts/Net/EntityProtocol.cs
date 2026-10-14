// Client side of the "entities" (PROTOCOL.md 4.21) and "actions" (4.22) messages: the entity set kept current from
// full lists and deltas, and parsed actions. Engine-free (Tests~ runs it against the fixtures).
using System.Collections.Generic;
using UnityEngine;

namespace TabletopCraft.Net
{
    /// <summary>One non-player entity as the client holds it. Fields not sent keep their defaults.</summary>
    public sealed class EntityInfo
    {
        public int Id;
        public string Type, Uuid;
        public Vector3 Pos;                 // world coordinates (feet / bottom centre)
        public float Yaw, Pitch, BodyYaw;   // yaw/pitch: head for living entities; BodyYaw = Yaw when not sent
        public bool HasBodyYaw, OnGround;
        public int Vehicle = -1;            // -1 = not riding
        public int[] Passengers = new int[0];
        public float Width, Height;
        public bool Baby, Small, Sheared, Invisible, Tamed, Angry;
        public string Variant, Color, Markings, Profession, Pose, Name, Item, Block, Facing;
        public int Count = 1, Rotation;
        public Dictionary<string, string> Equipment;
        /// <summary>Bumped when anything other than position/rotation changes: the visual is rebuilt.</summary>
        public int LooksVersion;

        public string Path => Type != null && Type.StartsWith("minecraft:") ? Type.Substring(10) : Type;
        public string Hand(string slot) => Equipment != null && Equipment.TryGetValue(slot, out var v) ? v : null;
    }

    /// <summary>The entities in the region. Apply() merges an entities message and reports what changed.</summary>
    public sealed class EntityStore
    {
        public readonly Dictionary<int, EntityInfo> All = new Dictionary<int, EntityInfo>();
        public int Messages, Fulls;
        readonly HashSet<int> _seen = new HashSet<int>();
        readonly List<int> _drop = new List<int>();

        public EntityInfo Get(int id) => All.TryGetValue(id, out var e) ? e : null;

        /// <summary>Merges one message. removed gets the ids that are gone (full lists drop everything not listed).</summary>
        public void Apply(Dictionary<string, object> m, List<int> removed)
        {
            Messages++;
            bool full = m.B("full");
            if (full) { Fulls++; _seen.Clear(); }
            var list = m.A("entities");
            if (list != null)
                foreach (var o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("id")) continue;
                    int id = d.I("id");
                    if (full) _seen.Add(id);
                    if (!All.TryGetValue(id, out var e))
                    {
                        if (d.S("type") == null) continue;   // a delta for an entity we never got: wait for a full entry
                        e = new EntityInfo { Id = id };
                        All[id] = e;
                    }
                    else if (full) Reset(e);                 // full entries leave out false/null fields: clear old ones
                    Merge(e, d);
                }
            if (full)
            {
                _drop.Clear();
                foreach (var id in All.Keys) if (!_seen.Contains(id)) _drop.Add(id);
                foreach (var id in _drop) { All.Remove(id); removed?.Add(id); }
            }
            var gone = m.A("removed");
            if (gone != null)
                foreach (var g in gone)
                {
                    int id = (int)(double)g;
                    if (All.Remove(id)) removed?.Add(id);
                }
        }

        static void Reset(EntityInfo e)
        {
            int v = e.LooksVersion;
            e.Vehicle = -1; e.Passengers = new int[0]; e.OnGround = false; e.HasBodyYaw = false;
            e.Baby = e.Small = e.Sheared = e.Invisible = e.Tamed = e.Angry = false;
            e.Variant = e.Color = e.Markings = e.Profession = e.Pose = e.Name = e.Item = e.Block = e.Facing = null;
            e.Count = 1; e.Rotation = 0; e.Equipment = null;
            e.LooksVersion = v + 1;
        }

        static void Merge(EntityInfo e, Dictionary<string, object> d)
        {
            bool looks = false;
            foreach (var kv in d)
            {
                object v = kv.Value;
                switch (kv.Key)
                {
                    case "type": if (e.Type != (string)v) { e.Type = (string)v; looks = true; } break;
                    case "uuid": e.Uuid = v as string; break;
                    case "pos": var p = (List<object>)v; e.Pos = new Vector3((float)(double)p[0], (float)(double)p[1], (float)(double)p[2]); break;
                    case "yaw": e.Yaw = (float)(double)v; if (!e.HasBodyYaw) e.BodyYaw = e.Yaw; break;
                    case "pitch": e.Pitch = (float)(double)v; break;
                    case "body_yaw": e.BodyYaw = (float)(double)v; e.HasBodyYaw = true; break;
                    case "on_ground": e.OnGround = v is bool og && og; break;
                    case "vehicle": e.Vehicle = v is double vd ? (int)vd : -1; break;
                    case "passengers": e.Passengers = v is List<object> pl ? Json.Ints(pl) : new int[0]; break;
                    case "size": var s = (List<object>)v; e.Width = (float)(double)s[0]; e.Height = (float)(double)s[1]; looks = true; break;
                    case "baby": e.Baby = Flag(v); looks = true; break;
                    case "small": e.Small = Flag(v); looks = true; break;
                    case "sheared": e.Sheared = Flag(v); looks = true; break;
                    case "invisible": e.Invisible = Flag(v); looks = true; break;
                    case "tamed": e.Tamed = Flag(v); looks = true; break;
                    case "angry": e.Angry = Flag(v); looks = true; break;
                    case "variant": e.Variant = v as string; looks = true; break;
                    case "color": e.Color = v as string; looks = true; break;
                    case "markings": e.Markings = v as string; looks = true; break;
                    case "profession": e.Profession = v as string; looks = true; break;
                    case "pose": e.Pose = v as string; looks = true; break;
                    case "name": e.Name = v as string; looks = true; break;
                    case "item": e.Item = v as string; looks = true; break;
                    case "block": e.Block = v as string; looks = true; break;
                    case "facing": e.Facing = v as string; looks = true; break;
                    case "count": e.Count = v is double c ? (int)c : 1; looks = true; break;
                    case "rotation": e.Rotation = v is double r ? (int)r : 0; looks = true; break;
                    case "equipment": e.Equipment = Equipment(v as Dictionary<string, object>); looks = true; break;
                }
            }
            if (looks) e.LooksVersion++;
        }

        static bool Flag(object v) => v is bool b && b;

        public static Dictionary<string, string> Equipment(Dictionary<string, object> o)
        {
            if (o == null || o.Count == 0) return null;
            var r = new Dictionary<string, string>();
            foreach (var kv in o) if (kv.Value is string s) r[kv.Key] = s;
            return r;
        }
    }

    public enum ActionKind { Swing, Dig, Break, Place, Hurt }

    /// <summary>One entry of an "actions" message (PROTOCOL.md 4.22).</summary>
    public struct GameAction
    {
        public ActionKind Kind;
        public int Id;              // entity id (swing, hurt; who for dig/break/place, -1 if not sent)
        public bool OffHand, Crit;
        public Vector3Int Pos;      // world block (dig, break, place)
        public int Stage;           // dig: 0..9, -1 = cleared
        public string Block;        // break, place: block state

        /// <summary>Parses the actions of a message into list (unknown kinds are skipped).</summary>
        public static void Parse(Dictionary<string, object> m, List<GameAction> list)
        {
            var a = m.A("actions");
            if (a == null) return;
            foreach (var o in a)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var g = new GameAction { Id = d.I("id", -1), Stage = d.I("stage", -1), Block = d.S("block"), Crit = d.B("crit"), OffHand = d.S("hand") == "off" };
                if (d.A("pos") is List<object> p && p.Count == 3) g.Pos = new Vector3Int((int)(double)p[0], (int)(double)p[1], (int)(double)p[2]);
                switch (d.S("kind"))
                {
                    case "swing": g.Kind = ActionKind.Swing; break;
                    case "dig": g.Kind = ActionKind.Dig; break;
                    case "break": g.Kind = ActionKind.Break; break;
                    case "place": g.Kind = ActionKind.Place; break;
                    case "hurt": g.Kind = ActionKind.Hurt; break;
                    default: continue;
                }
                list.Add(g);
            }
        }
    }
}
