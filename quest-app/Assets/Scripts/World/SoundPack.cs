// [sound] The user's sounds.zip (tools/sounds/PackSounds.java; QUEST_SETUP.md Part E): sounds.json and the .ogg files,
// laid out like a resource pack (assets/minecraft/sounds.json, assets/minecraft/sounds/**.ogg). Unity can only decode
// an OGG from a file or URL, so a sound is copied out of the zip into a cache folder the first time it plays; the cache
// belongs to one sounds.zip (its size and date) and older caches are deleted. Thread-safe; engine-free (Tests~).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TabletopCraft.World
{
    public sealed class SoundPack : IDisposable
    {
        public const string FileName = "sounds.zip";
        public SoundDefs Defs { get; private set; } = new SoundDefs();
        public string Status { get; private set; } = "no " + FileName;
        /// <summary>The Minecraft version it was made from (tabletop_sounds.json), or "".</summary>
        public string Version { get; private set; } = "";
        public int Files { get; private set; }
        public bool Loaded => _zip != null && Defs.Count > 0;
        public string CacheDir { get; private set; }

        ZipArchive _zip;
        readonly object _lock = new object();
        readonly HashSet<string> _extracted = new HashSet<string>();

        /// <summary>Opens a sounds.zip; never throws (Status says what went wrong). cacheRoot: where sounds are copied out.</summary>
        public static SoundPack Open(string path, string cacheRoot)
        {
            var p = new SoundPack();
            try
            {
                if (!File.Exists(path)) { p.Status = "no " + FileName + " (QUEST_SETUP Part E)"; return p; }
                p._zip = ZipFile.OpenRead(path);
                var sj = p._zip.GetEntry("assets/minecraft/sounds.json") ?? p._zip.GetEntry("sounds.json");
                if (sj == null) { p.Status = FileName + " has no assets/minecraft/sounds.json"; p._zip.Dispose(); p._zip = null; return p; }
                using (var r = new StreamReader(sj.Open())) p.Defs = SoundDefs.Parse(r.ReadToEnd());
                foreach (var e in p._zip.Entries) if (e.FullName.EndsWith(".ogg")) p.Files++;
                var meta = p._zip.GetEntry("tabletop_sounds.json");
                if (meta != null)
                    using (var r = new StreamReader(meta.Open()))
                        if (TabletopCraft.Net.Json.Parse(r.ReadToEnd()) is Dictionary<string, object> m) p.Version = TabletopCraft.Net.Json.S(m, "minecraft") ?? "";
                var fi = new FileInfo(path);
                p.CacheDir = Path.Combine(cacheRoot, $"sounds_{fi.Length}_{fi.LastWriteTimeUtc.Ticks}");
                Directory.CreateDirectory(p.CacheDir);
                // Copies made for another sounds.zip are stale now.
                foreach (var d in Directory.GetDirectories(cacheRoot, "sounds_*"))
                    if (!string.Equals(Path.GetFullPath(d).TrimEnd('/', '\\'), Path.GetFullPath(p.CacheDir).TrimEnd('/', '\\'), StringComparison.Ordinal))
                        try { Directory.Delete(d, true); } catch (Exception) { }
                p.Status = $"{p.Defs.Count} events, {p.Files} sounds{(p.Version.Length > 0 ? " (Minecraft " + p.Version + ")" : "")}";
            }
            catch (Exception e) { p.Status = FileName + " error: " + e.Message; p._zip?.Dispose(); p._zip = null; p.Defs = new SoundDefs(); }
            return p;
        }

        public bool Has(string file) { lock (_lock) return _zip?.GetEntry(file) != null; }

        /// <summary>The file on disk for a sound in the zip (copied out the first time), or null when it isn't there.</summary>
        public string Extract(string file)
        {
            if (file == null || file.Contains("..")) return null;
            string dst = Path.Combine(CacheDir ?? "", file.Replace('/', Path.DirectorySeparatorChar));
            lock (_lock)
            {
                if (_extracted.Contains(file) || File.Exists(dst)) { _extracted.Add(file); return dst; }
                var e = _zip?.GetEntry(file);
                if (e == null) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                string tmp = dst + ".tmp";
                using (var s = e.Open()) using (var o = File.Create(tmp)) s.CopyTo(o);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                _extracted.Add(file);
                return dst;
            }
        }

        public void Dispose() { lock (_lock) { _zip?.Dispose(); _zip = null; } }
    }
}
