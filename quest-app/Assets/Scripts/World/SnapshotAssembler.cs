// Turns chunk_snapshot messages into a decoded ChunkSnapshot plus a SectionPlan on a worker thread, so a large
// region never decodes (or gets compared with the old one) on the main thread. Collects the parts of a sectioned
// snapshot first. While Busy, the caller must hold back block_update and chunk_snapshot messages (the worker reads
// the current model) and replay them in order after TryTake.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    public sealed class SnapshotAssembler
    {
        public sealed class Result
        {
            public int SnapshotId;
            public ChunkSnapshot Snapshot;      // null on error
            public SectionPlan Plan;
            public Vector3Int ViewMargin;       // chunk_snapshot.view_margin (PROTOCOL.md 4.17), 0 if absent
            public string Error;
            public bool Delta;
            public int Parts = 1, Listed = -1;  // Listed: sections sent (sectioned snapshots)
            public HashSet<Vector3Int> ListedKeys;   // [edit] a delta's sent section keys (pending block edits elsewhere carry over)
            public double Ms;                   // worker time
        }

        readonly List<Dictionary<string, object>> _parts = new List<Dictionary<string, object>>();
        int _partsId = int.MinValue, _partsTotal;
        Task<Result> _task;

        /// <summary>True while a decode is running.</summary>
        public bool Busy => _task != null;
        /// <summary>Parts received for a sectioned snapshot that isn't complete yet.</summary>
        public int PartsWaiting => _parts.Count;

        /// <summary>Feed a chunk_snapshot. stale: old section keys whose mesh is out of date (copied by the caller).</summary>
        public void Offer(Dictionary<string, object> m, WorldModel current, HashSet<Vector3Int> stale)
        {
            if (_task != null) throw new InvalidOperationException("snapshot decode already running; hold messages while Busy");
            var margin = Margin(m);
            if (m.S("encoding") == SectionedSnapshot.Encoding)
            {
                int id = m.I("snapshot_id"), part = m.I("part"), total = Math.Max(1, m.I("parts", 1));
                if (id != _partsId || part == 0) { _parts.Clear(); _partsId = id; _partsTotal = total; }
                _parts.Add(m);
                if (_parts.Count < _partsTotal) return;
                var parts = _parts.ToArray();
                _parts.Clear(); _partsId = int.MinValue;
                _task = Task.Run(() => Run(id, margin, current, stale, listed =>
                {
                    var s = SectionedSnapshot.Assemble(parts, current, listed);
                    return (s, parts[0].O("base") != null);
                }, parts.Length));
            }
            else
            {
                int id = m.I("snapshot_id");
                _task = Task.Run(() => Run(id, margin, current, stale, listed => (ChunkSnapshot.From(m), false), 1));
            }
        }

        static Vector3Int Margin(Dictionary<string, object> m)
        {
            var a = m.A("view_margin");
            if (a == null || a.Count != 3) return default;
            var v = Json.Ints(a);
            return new Vector3Int(Math.Max(0, v[0]), Math.Max(0, v[1]), Math.Max(0, v[2]));
        }

        static Result Run(int id, Vector3Int margin, WorldModel current, HashSet<Vector3Int> stale,
                          Func<List<Vector3Int>, (ChunkSnapshot, bool)> decode, int parts)
        {
            var sw = Stopwatch.StartNew();
            var r = new Result { SnapshotId = id, ViewMargin = margin, Parts = parts };
            try
            {
                var listed = new List<Vector3Int>();
                var (s, delta) = decode(listed);
                r.Snapshot = s; r.Delta = delta;
                r.Listed = parts > 1 || delta || listed.Count > 0 ? listed.Count : -1;
                if (delta) r.ListedKeys = new HashSet<Vector3Int>(listed);   // [edit]
                r.Plan = SectionPlan.Compute(current, s, stale, delta ? listed : null);
            }
            catch (Exception e) { r.Error = e.Message; r.Snapshot = null; }
            r.Ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        /// <summary>Returns the finished decode, if any (main thread).</summary>
        public bool TryTake(out Result r)
        {
            r = null;
            if (_task == null || !_task.IsCompleted) return false;
            r = _task.IsFaulted ? new Result { Error = _task.Exception?.GetBaseException().Message } : _task.Result;
            _task = null;
            return true;
        }

        /// <summary>Blocking variant for tests and tools.</summary>
        public Result Wait()
        {
            _task?.Wait();
            TryTake(out var r);
            return r;
        }
    }
}
