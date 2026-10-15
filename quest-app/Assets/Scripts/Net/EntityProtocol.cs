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
        /// <summary>Armour stands: arms and base plate shown; StandPose = 18 degrees (head, body, left_arm, right_arm,
        /// left_leg, right_leg: x, y, z each), null for vanilla's default pose.</summary>
        public bool Arms, BasePlate;
        public float[] StandPose;
        public static readonly string[] StandParts = { "head", "body", "left_arm", "right_arm", "left_leg", "right_leg" };
        /// <summary>ArmorStand.DEFAULT_*_POSE (26.3), degrees, StandParts order.</summary>
        public static readonly float[] DefaultStandPose = { 0, 0, 0, 0, 0, 0, -10, 0, -10, -15, 0, 10, -1, 0, -1, 1, 0, 1 };
        public string Variant, Color, Markings, Profession, Pose, Name, Item, Block, Facing;
        public int Count = 1, Rotation;
        public Dictionary<string, string> Equipment;
        // [living] PROTOCOL.md "Living entities": the lead holder's id (-1 = none), eye height, chests, a name shown always,
        // experience orb value, bobber owner (-1), enderman block, shulker lid and attach face, display entity state.
        public int Leash = -1, Owner = -1, Xp;
        public float Eye, Peek;
        // [motion] primed TNT: fuse ticks left when last sent (the renderer counts down from there); creeper: swelling 0..1
        public int Fuse;
        public float Swell;
        public bool Chested, NameVisible;
        public string Carried, Attach;
        public EntityDisplay Display;
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
            e.Baby = e.Small = e.Sheared = e.Invisible = e.Tamed = e.Angry = e.Arms = e.BasePlate = false;
            e.StandPose = null;
            e.Variant = e.Color = e.Markings = e.Profession = e.Pose = e.Name = e.Item = e.Block = e.Facing = null;
            e.Count = 1; e.Rotation = 0; e.Equipment = null;
            e.Leash = e.Owner = -1; e.Xp = 0; e.Eye = e.Peek = 0; e.Chested = e.NameVisible = false; e.Carried = e.Attach = null; e.Display = null;   // [living]
            e.Fuse = 0; e.Swell = 0;   // [motion]
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
                    case "arms": e.Arms = Flag(v); looks = true; break;
                    case "base_plate": e.BasePlate = Flag(v); looks = true; break;
                    case "stand_pose": e.StandPose = StandPose(v as Dictionary<string, object>); looks = true; break;
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
                    // [living] leads and lines follow every frame (no rebuild); the rest changes the look
                    case "leash": e.Leash = v is double ld ? (int)ld : -1; break;
                    case "owner": e.Owner = v is double od ? (int)od : -1; break;
                    case "eye": e.Eye = v is double ed ? (float)ed : 0; break;
                    case "peek": e.Peek = v is double pd ? (float)pd : 0; break;
                    case "fuse": e.Fuse = v is double fd ? (int)fd : 0; break;          // [motion]
                    case "swell": e.Swell = v is double sd ? (float)sd : 0; break;
                    case "display": e.Display = EntityDisplay.From(v as Dictionary<string, object>); if (e.Display == null || e.Display.Text != null) looks = true; break;
                    case "chested": e.Chested = Flag(v); looks = true; break;
                    case "name_visible": e.NameVisible = Flag(v); looks = true; break;
                    case "xp": e.Xp = v is double xd ? (int)xd : 0; looks = true; break;
                    case "carried": e.Carried = v as string; looks = true; break;
                    case "attach": e.Attach = v as string; looks = true; break;
                }
            }
            if (looks) e.LooksVersion++;
        }

        static bool Flag(object v) => v is bool b && b;

        /// <summary>stand_pose {"head": [x, y, z], ...} -> 18 degrees in EntityInfo.StandParts order; null for null.
        /// A part that is missing keeps vanilla's default (EntityInfo.DefaultStandPose).</summary>
        public static float[] StandPose(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var p = (float[])EntityInfo.DefaultStandPose.Clone();
            for (int i = 0; i < EntityInfo.StandParts.Length; i++)
                if (o.TryGetValue(EntityInfo.StandParts[i], out var a) && a is List<object> l && l.Count >= 3)
                    for (int k = 0; k < 3; k++) p[i * 3 + k] = l[k] is double d ? (float)d : p[i * 3 + k];
            return p;
        }

        public static Dictionary<string, string> Equipment(Dictionary<string, object> o)
        {
            if (o == null || o.Count == 0) return null;
            var r = new Dictionary<string, string>();
            foreach (var kv in o) if (kv.Value is string s) r[kv.Key] = s;
            return r;
        }
    }

    /// <summary>[living] A display entity's render state (PROTOCOL.md "Display entities"): Display#getTransformation as
    /// translation, left rotation, scale and right rotation (game frame, quaternions x, y, z, w), the billboard, a light
    /// override, the item display transform and the text display's text and look.</summary>
    public sealed class EntityDisplay
    {
        public Vector3 Translation, Scale = new Vector3(1, 1, 1);
        public float[] Left = { 0, 0, 0, 1 }, Right = { 0, 0, 0, 1 };
        public string Billboard = "fixed", ItemTransform = "none", Text, Align = "center";
        public int BlockLight = -1, SkyLight = -1;   // -1 = the world's light
        public int LineWidth = 200, Background = 0x40000000, TextOpacity = 255;
        public bool Shadow, SeeThrough, DefaultBackground;

        public static EntityDisplay From(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var d = new EntityDisplay();
            if (o.A("translation") is List<object> t && t.Count == 3) d.Translation = new Vector3(F(t[0]), F(t[1]), F(t[2]));
            if (o.A("scale") is List<object> s && s.Count == 3) d.Scale = new Vector3(F(s[0]), F(s[1]), F(s[2]));
            if (o.A("left_rotation") is List<object> l && l.Count == 4) d.Left = new[] { F(l[0]), F(l[1]), F(l[2]), F(l[3]) };
            if (o.A("right_rotation") is List<object> r && r.Count == 4) d.Right = new[] { F(r[0]), F(r[1]), F(r[2]), F(r[3]) };
            d.Billboard = o.S("billboard") ?? "fixed";
            if (o.A("brightness") is List<object> b && b.Count == 2) { d.BlockLight = (int)F(b[0]); d.SkyLight = (int)F(b[1]); }
            d.ItemTransform = o.S("item_transform") ?? "none";
            d.Text = o.S("text");
            d.LineWidth = o.I("line_width", 200);
            d.Background = o.TryGetValue("background", out var bg) && bg is double bd ? unchecked((int)(long)bd) : 0x40000000;
            d.TextOpacity = o.I("text_opacity", 255);
            d.Shadow = o.B("shadow"); d.SeeThrough = o.B("see_through"); d.DefaultBackground = o.B("default_background");
            d.Align = o.S("align") ?? "center";
            return d;
        }

        static float F(object v) => v is double x ? (float)x : 0f;
    }

    public enum ActionKind { Swing, Dig, Break, Place, Hurt, Sound, SoundStop, Lid, Bell, Explode }

    /// <summary>One entry of an "actions" message (PROTOCOL.md 4.22; [sound] sound and sound_stop, "Sound").</summary>
    public struct GameAction
    {
        public ActionKind Kind;
        public int Id;              // entity id (swing, hurt; who for dig/break/place, -1 if not sent)
        public bool OffHand, Crit;
        public Vector3Int Pos;      // world block (dig, break, place, sound_stop; a sound's block)
        public int Stage;           // dig: 0..9, -1 = cleared
        public string Block;        // break, place: block state
        // [sound] sound: the event id, where (world, not rounded to the block), vanilla volume and pitch, streamed
        public string Sound;
        public Vector3 At;
        public float Volume, Pitch;
        public bool Stream;
        // [motion] lid: opening or closing; bell: the side it was rung from; explode: the explosion's power (at At)
        public bool Open;
        public string Dir;
        public float Power;

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
                if (d.A("pos") is List<object> p && p.Count == 3)
                {
                    float px = (float)(double)p[0], py = (float)(double)p[1], pz = (float)(double)p[2];
                    g.At = new Vector3(px, py, pz);
                    g.Pos = new Vector3Int(Mathf.FloorToInt(px), Mathf.FloorToInt(py), Mathf.FloorToInt(pz));
                }
                switch (d.S("kind"))
                {
                    case "swing": g.Kind = ActionKind.Swing; break;
                    case "dig": g.Kind = ActionKind.Dig; break;
                    case "break": g.Kind = ActionKind.Break; break;
                    case "place": g.Kind = ActionKind.Place; break;
                    case "hurt": g.Kind = ActionKind.Hurt; break;
                    case "sound":   // [sound]
                        g.Kind = ActionKind.Sound; g.Sound = d.S("sound"); g.Volume = (float)d.D("volume", 1); g.Pitch = (float)d.D("pitch", 1); g.Stream = d.B("stream");
                        if (g.Sound == null) continue;
                        break;
                    case "sound_stop": g.Kind = ActionKind.SoundStop; break;
                    case "lid": g.Kind = ActionKind.Lid; g.Open = d.B("open"); break;   // [motion] PROTOCOL.md "Block animations"
                    case "bell": g.Kind = ActionKind.Bell; g.Dir = d.S("dir"); if (g.Dir == null) continue; break;
                    case "explode": g.Kind = ActionKind.Explode; g.Power = (float)d.D("power", 4); break;
                    default: continue;
                }
                list.Add(g);
            }
        }
    }
}
