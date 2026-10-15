// [sound] The game's sound definitions (sounds.json from the user's own sounds.zip, tools/sounds/PackSounds.java): sound
// events, their variants and vanilla's way of picking one. Engine-free (Tests~).
//
// sounds.json maps an event id ("block.stone.break") to {"sounds": [...]} where each entry is a file name ("dig/stone1")
// or an object with name, volume, pitch, weight, stream, attenuation_distance, preload and type ("file" or "event": the
// name is another event, picked from in turn). Like the game's WeighedSoundEvents: an entry's chance is its weight (an
// event entry weighs the referenced event's total), and an event entry's volume and pitch multiply the chosen one's.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public sealed class SoundEntry
    {
        public string Name;                    // file "dig/stone1" (namespace optional) or, for IsEvent, an event id
        public float Volume = 1f, Pitch = 1f;
        public int Weight = 1;
        public bool Stream, Preload, IsEvent;
        public int Attenuation = 16;           // blocks at volume <= 1 (linear fall-off, like the game)
    }

    /// <summary>One played sound: the file inside sounds.zip and the event's volume and pitch factors.</summary>
    public struct SoundChoice
    {
        public string Event, File;
        public float Volume, Pitch;
        public bool Stream;
        public int Attenuation;
    }

    public sealed class SoundDefs
    {
        readonly Dictionary<string, List<SoundEntry>> _events = new Dictionary<string, List<SoundEntry>>();
        public int Count => _events.Count;
        public IEnumerable<string> Keys => _events.Keys;

        /// <summary>"minecraft:block.stone.break" and "block.stone.break" are the same event.</summary>
        public static string Key(string id) => id != null && id.StartsWith("minecraft:") ? id.Substring(10) : id;

        /// <summary>A file name as sounds.zip (a resource pack) stores it: dig/stone1 -> assets/minecraft/sounds/dig/stone1.ogg.</summary>
        public static string FilePath(string name)
        {
            int c = name.IndexOf(':');
            string ns = c >= 0 ? name.Substring(0, c) : "minecraft", path = c >= 0 ? name.Substring(c + 1) : name;
            return "assets/" + ns + "/sounds/" + path + ".ogg";
        }

        public static SoundDefs Parse(string json)
        {
            var d = new SoundDefs();
            if (!(Json.Parse(json) is Dictionary<string, object> root)) throw new FormatException("sounds.json is not an object");
            foreach (var kv in root)
            {
                if (!(kv.Value is Dictionary<string, object> ev) || !(ev.A("sounds") is List<object> list)) continue;
                var entries = new List<SoundEntry>(list.Count);
                foreach (var o in list)
                {
                    if (o is string s) { entries.Add(new SoundEntry { Name = s }); continue; }
                    if (!(o is Dictionary<string, object> e) || e.S("name") == null) continue;
                    entries.Add(new SoundEntry
                    {
                        Name = e.S("name"), Volume = (float)e.D("volume", 1), Pitch = (float)e.D("pitch", 1), Weight = Math.Max(0, e.I("weight", 1)),
                        Stream = e.B("stream"), Preload = e.B("preload"), IsEvent = e.S("type") == "event",
                        Attenuation = e.I("attenuation_distance", 16),
                    });
                }
                d._events[Key(kv.Key)] = entries;
            }
            return d;
        }

        public bool Has(string key) => key != null && _events.TryGetValue(Key(key), out var l) && l.Count > 0;

        /// <summary>The event's total weight (an event entry counts the referenced event's total); 0 for unknown ids.</summary>
        public int Weight(string key, int depth = 0)
        {
            if (depth > 8 || key == null || !_events.TryGetValue(Key(key), out var l)) return 0;
            int w = 0;
            foreach (var e in l) w += e.IsEvent ? Weight(e.Name, depth + 1) : e.Weight;
            return w;
        }

        /// <summary>Picks a variant like the game does; false for an unknown or empty event.</summary>
        public bool Pick(string key, Random rng, out SoundChoice c) => Pick(Key(key), rng, out c, 0);

        bool Pick(string key, Random rng, out SoundChoice c, int depth)
        {
            c = default;
            if (depth > 8 || key == null || !_events.TryGetValue(key, out var l) || l.Count == 0) return false;
            int total = Weight(key, depth);
            if (total <= 0) return false;
            int r = rng.Next(total);
            foreach (var e in l)
            {
                int w = e.IsEvent ? Weight(e.Name, depth + 1) : e.Weight;
                if (r >= w) { r -= w; continue; }
                if (e.IsEvent)
                {
                    if (!Pick(Key(e.Name), rng, out c, depth + 1)) return false;
                    c.Volume *= e.Volume; c.Pitch *= e.Pitch; c.Stream |= e.Stream; c.Event = key;
                    return true;
                }
                c = new SoundChoice { Event = key, File = FilePath(e.Name), Volume = e.Volume, Pitch = e.Pitch, Stream = e.Stream, Attenuation = e.Attenuation };
                return true;
            }
            return false;
        }

        /// <summary>Every file an event can play (for preloading).</summary>
        public List<string> Files(string key, List<string> into = null, int depth = 0)
        {
            into = into ?? new List<string>();
            if (depth > 8 || key == null || !_events.TryGetValue(Key(key), out var l)) return into;
            foreach (var e in l)
            {
                if (e.IsEvent) Files(e.Name, into, depth + 1);
                else if (!into.Contains(FilePath(e.Name))) into.Add(FilePath(e.Name));
            }
            return into;
        }
    }
}
