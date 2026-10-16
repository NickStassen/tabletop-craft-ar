// [smooth] Closes World/FrameHitch every frame and logs the HITCH line for a slow frame (docs/TESTING.md "Smooth gliding").
// Its Update runs before every other script's (execution order), Application.onBeforeRender marks the end of the
// main thread's script work, so script_ms covers Update, LateUpdate and the coroutines in between.
using System;
using System.Diagnostics;
using TabletopCraft.World;
using UnityEngine;

namespace TabletopCraft.XR
{
    [DefaultExecutionOrder(-32000)]
    public sealed class HitchProbe : MonoBehaviour
    {
        long _start, _end;
        int _gc;

        void OnEnable() { Application.onBeforeRender += MarkEnd; _gc = GC.CollectionCount(0); }
        void OnDisable() => Application.onBeforeRender -= MarkEnd;
        void MarkEnd() => _end = Stopwatch.GetTimestamp();

        void Update()
        {
            long now = Stopwatch.GetTimestamp();
            if (_start != 0)
            {
                float frameMs = Time.unscaledDeltaTime * 1000f;
                float scriptMs = _end > _start ? (float)((_end - _start) * 1000.0 / Stopwatch.Frequency) : 0f;
                int gc = GC.CollectionCount(0);
                string line = FrameHitch.Close(frameMs, scriptMs, gc - _gc, frameMs > FrameHitch.ThresholdMs ? GC.GetTotalMemory(false) : 0, Time.unscaledTimeAsDouble);
                _gc = gc;
                if (line != null) UnityEngine.Debug.Log(line);
            }
            _start = now;
        }
    }
}
