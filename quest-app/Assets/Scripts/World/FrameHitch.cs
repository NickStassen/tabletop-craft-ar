// [smooth] Per-frame counters for the HITCH log line (engine-free; tested in Tests~ GlideTests.RunHitch). The main thread
// adds the time of the few things that can stall a frame (applying a snapshot, re-keying meshes, uploading batches,
// the atlas prewarm, the skirt, the clip caps) and a few counts; XR/HitchProbe closes every frame and, for a frame over
// ThresholdMs, logs one line:
//   [Tabletop] HITCH ms=45.1 script_ms=31.0 apply_ms=12.0 rekey_ms=0.8 upload_ms=3.1 uploads=2 upload_kb=1800 prewarm_ms=0.1
//     skirt_ms=0.0 caps_ms=0.3 mesh_done=12 snap=1 gc=1 heap_mb=312 skipped=0
// ms: the frame's time (unscaledDeltaTime); script_ms: main-thread scripts from the first Update to onBeforeRender, so
// a frame much longer than its script time stalled elsewhere (render thread, GPU, GC on another thread, a descheduled
// main thread). apply_ms includes rekey_ms. gc: garbage collections during the frame. Main thread only (no locks).
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TabletopCraft.World
{
    public static class FrameHitch
    {
        public enum Part { Apply, Rekey, Upload, Prewarm, Skirt, Caps, Count }

        /// <summary>Frames longer than this (ms) are logged (tabletop.json "hitch_ms"; 0 = off).</summary>
        public static float ThresholdMs = 30f;
        /// <summary>At most this many lines per second (a GPU-bound area would otherwise log every frame).</summary>
        public static int MaxLinesPerSecond = 4;

        static readonly long[] Ticks = new long[(int)Part.Count];
        static int _uploads, _meshDone, _snaps, _skipped, _lines;
        static long _uploadBytes;
        static double _windowStart = -1;

        public static long Begin() => Stopwatch.GetTimestamp();
        public static void End(Part p, long t0) => Ticks[(int)p] += Stopwatch.GetTimestamp() - t0;
        public static void Uploaded(long bytes) { _uploads++; _uploadBytes += bytes; }
        public static void MeshDone() => _meshDone++;
        public static void Snapshot() => _snaps++;

        public static double Ms(Part p) => Ticks[(int)p] * 1000.0 / Stopwatch.Frequency;
        public static int Uploads => _uploads;

        /// <summary>
        /// Ends a frame: returns the HITCH line when frameMs is over the threshold (and the per-second cap allows it), else
        /// null, and starts the next frame's counters. now: seconds (for the cap).
        /// </summary>
        public static string Close(float frameMs, float scriptMs, int gcs, long heapBytes, double now)
        {
            string line = null;
            if (ThresholdMs > 0 && frameMs > ThresholdMs)
            {
                if (_windowStart < 0 || now - _windowStart >= 1.0) { _windowStart = now; _lines = 0; }
                if (_lines < MaxLinesPerSecond)
                {
                    _lines++;
                    var c = CultureInfo.InvariantCulture;
                    var sb = new StringBuilder(256);
                    sb.Append("[Tabletop] HITCH ms=").Append(frameMs.ToString("F1", c))
                      .Append(" script_ms=").Append(scriptMs.ToString("F1", c))
                      .Append(" apply_ms=").Append(Ms(Part.Apply).ToString("F1", c))
                      .Append(" rekey_ms=").Append(Ms(Part.Rekey).ToString("F1", c))
                      .Append(" upload_ms=").Append(Ms(Part.Upload).ToString("F1", c))
                      .Append(" uploads=").Append(_uploads)
                      .Append(" upload_kb=").Append(_uploadBytes / 1024)
                      .Append(" prewarm_ms=").Append(Ms(Part.Prewarm).ToString("F1", c))
                      .Append(" skirt_ms=").Append(Ms(Part.Skirt).ToString("F1", c))
                      .Append(" caps_ms=").Append(Ms(Part.Caps).ToString("F1", c))
                      .Append(" mesh_done=").Append(_meshDone)
                      .Append(" snap=").Append(_snaps)
                      .Append(" gc=").Append(gcs)
                      .Append(" heap_mb=").Append(heapBytes / (1024 * 1024))
                      .Append(" skipped=").Append(_skipped);
                    line = sb.ToString();
                    _skipped = 0;
                }
                else _skipped++;
            }
            for (int i = 0; i < Ticks.Length; i++) Ticks[i] = 0;
            _uploads = _meshDone = _snaps = 0; _uploadBytes = 0;
            return line;
        }
    }
}
