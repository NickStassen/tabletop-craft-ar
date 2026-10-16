// [sound] Downloads the files the server offers (PROTOCOL.md "Asset delivery"): the operator's own pack.zip,
// entity_models.json and sounds.zip, into the app's files folder, so nothing has to be pushed with adb. Engine-free
// (Tests~ runs it against a fake server and, with --live-assets, the real plugin).
//
//   - An offered file whose local copy has the same size and SHA-256 is left alone (hashes are cached in
//     assets_hashes.json by size and date, so a 70 MB file isn't hashed at every start).
//   - Files download one at a time, in offer order (small first), with up to InFlightMax asset_get in flight. Chunks are
//     written to <name>.part as they arrive (the receive thread); <name>.part.sha remembers which version it is, so a
//     dropped link resumes where it stopped.
//   - When all bytes are there the part is hashed on a worker; only a matching SHA-256 replaces the file. Completed
//     then reports the name (Pump, main thread): the app reloads sounds and entity models, and restarts for a pack.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using TabletopCraft.Net;

namespace TabletopCraft.World
{
    public sealed class AssetSync
    {
        public enum State { Checking, UpToDate, Waiting, Downloading, Verifying, Done, Failed }

        public sealed class Item
        {
            public AssetFile Offer;
            public State State;
            public long Received, Requested;
            public int InFlight;
            public string Note = "";
            public double LastChunk, StartedAt;
            public long StartedFrom;
            public string Name => Offer.Name;
        }

        public const string HashCache = "assets_hashes.json";
        public readonly string Dir;
        public int InFlightMax = 4;
        public double StallSeconds = 15;
        /// <summary>SHA-256 of a file, lower-case hex (replaceable in tests).</summary>
        public Func<string, string> Hash = Sha256File;
        /// <summary>Seconds, for stall detection and speed (replaceable in tests).</summary>
        public Func<double> Clock = () => Watch.Elapsed.TotalSeconds;
        static readonly Stopwatch Watch = Stopwatch.StartNew();
        /// <summary>A file was replaced and verified (raised by Pump on the caller's thread).</summary>
        public Action<string> Completed;
        public bool Offered { get; private set; }
        /// <summary>[setup] The offer's optional state ("building", "ready", "missing", "failed"; "" from an older plugin) and
        /// note (what the server is missing and how to fix it), PROTOCOL.md "Asset delivery".</summary>
        public string ServerState { get; private set; } = "";
        public string ServerNote { get; private set; } = "";
        public int Downloaded { get; private set; }
        public long BytesReceived { get; private set; }

        readonly object _lock = new object();
        readonly List<Item> _items = new List<Item>();
        readonly Queue<string> _done = new Queue<string>();
        int _chunk = 262144;
        Item _current;
        FileStream _out;
        int _checking;

        public AssetSync(string dir) { Dir = dir; }

        public List<Item> Items { get { lock (_lock) return new List<Item>(_items); } }

        string PathOf(string name) => Path.Combine(Dir, name);

        // ---------- the offer ----------

        /// <summary>The server's assets message (main thread): what to fetch is worked out on a worker.</summary>
        public void OnOffer(Dictionary<string, object> m)
        {
            var offer = AssetProtocol.ParseOffer(m, out int chunk);
            var check = new List<Item>();
            lock (_lock)
            {
                Offered = true;
                ServerState = m.S("state") ?? "";
                ServerNote = m.S("note") ?? "";
                _chunk = chunk;
                var keep = new List<Item>();
                foreach (var f in offer)
                {
                    var old = _items.Find(i => i.Name == f.Name);
                    if (old != null && old.Offer.Sha256 == f.Sha256 && old.Offer.Size == f.Size && old.State != State.Failed)
                    {
                        if (old.State == State.Downloading) { CloseOut(); _current = null; old.State = State.Waiting; }   // resumes from the part
                        keep.Add(old);
                        continue;
                    }
                    if (old != null && old == _current) { CloseOut(); _current = null; }
                    var it = new Item { Offer = f, State = State.Checking };
                    keep.Add(it); check.Add(it);
                }
                if (_current != null && !keep.Contains(_current)) { CloseOut(); _current = null; }
                _items.Clear(); _items.AddRange(keep);
                _checking += check.Count;
            }
            if (check.Count > 0) Task.Run(() => CheckLocal(check));
        }

        void CheckLocal(List<Item> items)
        {
            var cache = LoadCache();
            bool dirty = false;
            foreach (var it in items)
            {
                string p = PathOf(it.Name), sha = null;
                try
                {
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length == it.Offer.Size)
                    {
                        string stamp = fi.Length + ":" + fi.LastWriteTimeUtc.Ticks;
                        if (cache.TryGetValue(it.Name, out var c) && c.Item1 == stamp) sha = c.Item2;
                        else { sha = Hash(p); cache[it.Name] = (stamp, sha); dirty = true; }
                    }
                }
                catch (Exception e) { it.Note = "can't read " + it.Name + ": " + e.Message; }
                lock (_lock)
                {
                    it.State = sha == it.Offer.Sha256 ? State.UpToDate : State.Waiting;
                    _checking--;
                }
            }
            if (dirty) SaveCache(cache);
        }

        // ---------- requests (main thread) ----------

        /// <summary>The asset_get messages to send now; also reports finished files through Completed.</summary>
        public List<Dictionary<string, object>> Pump()
        {
            var send = new List<Dictionary<string, object>>();
            List<string> finished = null;
            lock (_lock)
            {
                while (_done.Count > 0) (finished = finished ?? new List<string>()).Add(_done.Dequeue());
                double now = Clock();
                if (_checking == 0 && _current == null)
                {
                    var next = _items.Find(i => i.State == State.Waiting);
                    if (next != null) Start(next, now);
                }
                var cur = _current;
                if (cur != null && cur.State == State.Downloading)
                {
                    if (cur.InFlight > 0 && now - cur.LastChunk > StallSeconds) { cur.InFlight = 0; cur.Requested = cur.Received; cur.Note = "stalled, asking again"; }
                    while (cur.InFlight < InFlightMax && cur.Requested < cur.Offer.Size)
                    {
                        int len = (int)Math.Min(_chunk, cur.Offer.Size - cur.Requested);
                        send.Add(AssetProtocol.Get(cur.Name, cur.Requested, len));
                        if (cur.InFlight == 0) cur.LastChunk = now;
                        cur.Requested += len;
                        cur.InFlight++;
                    }
                }
            }
            if (finished != null) foreach (var n in finished) Completed?.Invoke(n);
            return send;
        }

        void Start(Item it, double now)
        {
            string part = PathOf(it.Name) + ".part", side = part + ".sha";
            long have = 0;
            try
            {
                if (File.Exists(part) && File.Exists(side) && File.ReadAllText(side).Trim() == it.Offer.Sha256)
                    have = Math.Min(new FileInfo(part).Length, it.Offer.Size) / _chunk * _chunk;   // whole chunks only
                else { if (File.Exists(part)) File.Delete(part); File.WriteAllText(side, it.Offer.Sha256); }
                _out = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                _out.SetLength(have);
            }
            catch (Exception e) { it.State = State.Failed; it.Note = "can't write " + part + ": " + e.Message; CloseOut(); return; }
            it.State = State.Downloading;
            it.Received = it.Requested = it.StartedFrom = have;
            it.InFlight = 0;
            it.StartedAt = it.LastChunk = now;
            it.Note = have > 0 ? $"resuming at {have / 1e6:F1} MB" : "";
            _current = it;
        }

        void CloseOut()
        {
            try { _out?.Dispose(); } catch (Exception) { }
            _out = null;
        }

        // ---------- chunks (receive thread) ----------

        /// <summary>A binary frame from the link (any thread). Chunks arrive in request order; anything else is ignored.</summary>
        public void OnBinary(byte[] buf, int len)
        {
            if (!AssetProtocol.ParseChunk(buf, len, out string name, out long offset, out int start)) return;
            Item verify = null;
            lock (_lock)
            {
                var cur = _current;
                if (cur == null || cur.State != State.Downloading || cur.Name != name || _out == null) return;
                cur.InFlight = Math.Max(0, cur.InFlight - 1);
                if (offset != cur.Received) return;   // a late copy of a chunk asked for again after a stall
                int n = (int)Math.Min(len - start, cur.Offer.Size - offset);
                try
                {
                    _out.Seek(offset, SeekOrigin.Begin);
                    _out.Write(buf, start, n);
                }
                catch (Exception e) { cur.State = State.Failed; cur.Note = "write failed: " + e.Message; CloseOut(); _current = null; return; }
                cur.Received += n;
                BytesReceived += n;
                cur.LastChunk = Clock();
                if (cur.Requested < cur.Received) cur.Requested = cur.Received;
                if (cur.Received >= cur.Offer.Size)
                {
                    CloseOut();
                    cur.State = State.Verifying;
                    verify = cur;
                }
            }
            if (verify != null) Task.Run(() => Verify(verify));
        }

        void Verify(Item it)
        {
            string part = PathOf(it.Name) + ".part", dst = PathOf(it.Name);
            string sha = null, why = null;
            try { sha = Hash(part); } catch (Exception e) { why = e.Message; }
            lock (_lock)
            {
                if (_current == it) _current = null;
                if (sha != it.Offer.Sha256)
                {
                    it.State = State.Failed;
                    it.Note = why ?? "checksum mismatch: the download was damaged or the file changed (reconnect to try again)";
                    TryDelete(part); TryDelete(part + ".sha");
                    return;
                }
                try
                {
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(part, dst);
                    TryDelete(part + ".sha");
                }
                catch (Exception e) { it.State = State.Failed; it.Note = "can't replace " + it.Name + ": " + e.Message; return; }
                it.State = State.Done;
                it.Note = "";
                Downloaded++;
                _done.Enqueue(it.Name);
            }
            var cache = LoadCache();
            var fi = new FileInfo(dst);
            cache[it.Name] = (fi.Length + ":" + fi.LastWriteTimeUtc.Ticks, it.Offer.Sha256);
            SaveCache(cache);
        }

        static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { } }

        // ---------- errors and the link ----------

        /// <summary>An error about an asset_get (op "asset_get"); false for other errors.</summary>
        public bool OnError(Dictionary<string, object> m)
        {
            if (m.S("op") != "asset_get") return false;
            string name = m.S("name"), code = m.S("code");
            lock (_lock)
            {
                foreach (var it in _items)
                {
                    if (it.Name != name && code != "asset_denied") continue;
                    if (code == "rate_limited" && it == _current) { it.InFlight = 0; it.Requested = it.Received; it.Note = "the server asked to slow down"; continue; }
                    if (it == _current) { CloseOut(); _current = null; }
                    if (it.State == State.Downloading || it.State == State.Waiting) { it.State = State.Failed; it.Note = code + ": " + m.S("message"); }
                }
            }
            return true;
        }

        /// <summary>The link dropped: the current download waits for the next offer and resumes from its part file.</summary>
        public void LinkLost()
        {
            lock (_lock)
            {
                Offered = false;
                if (_current != null) { CloseOut(); _current.State = State.Waiting; _current.InFlight = 0; _current = null; }
            }
        }

        public bool Busy { get { lock (_lock) return _checking > 0 || _items.Exists(i => i.State == State.Waiting || i.State == State.Downloading || i.State == State.Verifying); } }

        /// <summary>One line for the Info tab.</summary>
        public string Status
        {
            get
            {
                lock (_lock)
                {
                    if (!Offered) return _items.Count == 0 ? "not offered by this server" : "waiting for the server";
                    if (_items.Count == 0) return (ServerState == "building" ? "the server is preparing the game files" : "the server offers nothing")
                                                  + (ServerNote.Length > 0 ? ": " + ServerNote : "");
                    if (_checking > 0) return "checking local copies...";
                    var cur = _current;
                    if (cur != null) return Progress(cur);
                    var parts = new List<string>();
                    foreach (var it in _items)
                        parts.Add(it.Name + " " + (it.State == State.UpToDate ? "up to date" : it.State == State.Done ? "downloaded" : it.State == State.Verifying ? "checking" :
                                                   it.State == State.Failed ? "failed (" + it.Note + ")" : it.State.ToString().ToLowerInvariant()));
                    return string.Join(", ", parts);
                }
            }
        }

        /// <summary>The progress toast while a file downloads, else null.</summary>
        public string Toast() { lock (_lock) return _current != null && _current.State == State.Downloading ? "Downloading " + Progress(_current) : null; }

        string Progress(Item it)
        {
            double secs = Math.Max(0.001, Clock() - it.StartedAt), mbs = (it.Received - it.StartedFrom) / 1e6 / secs;
            return $"{it.Name} {100.0 * it.Received / it.Offer.Size:F0}% ({it.Received / 1e6:F1}/{it.Offer.Size / 1e6:F1} MB, {mbs:F1} MB/s){(it.Note.Length > 0 ? " " + it.Note : "")}";
        }

        // ---------- hashes ----------

        public static string Sha256File(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
            {
                var h = sha.ComputeHash(s);
                var sb = new System.Text.StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        Dictionary<string, (string, string)> LoadCache()
        {
            var d = new Dictionary<string, (string, string)>();
            try
            {
                string p = PathOf(HashCache);
                if (!File.Exists(p)) return d;
                if (Json.Parse(File.ReadAllText(p)) is Dictionary<string, object> m)
                    foreach (var kv in m) if (kv.Value is Dictionary<string, object> e && e.S("stamp") != null && e.S("sha256") != null) d[kv.Key] = (e.S("stamp"), e.S("sha256"));
            }
            catch (Exception) { }
            return d;
        }

        readonly object _cacheLock = new object();

        void SaveCache(Dictionary<string, (string, string)> cache)
        {
            lock (_cacheLock)
            {
                try
                {
                    var m = new Dictionary<string, object>();
                    foreach (var kv in cache) m[kv.Key] = new Dictionary<string, object> { ["stamp"] = kv.Value.Item1, ["sha256"] = kv.Value.Item2 };
                    File.WriteAllText(PathOf(HashCache), Json.Write(m));
                }
                catch (Exception) { }
            }
        }
    }
}
