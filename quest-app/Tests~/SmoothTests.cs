// [smooth] Smooth gliding (docs/TESTING.md "Smooth gliding"): the HITCH log, cheaper snapshot applies, fewer shifts, and
// the surface skirt's hand-off.
using System;
using System.Collections.Generic;
using System.Threading;
using TabletopCraft.World;
using UnityEngine;

static class SmoothTests
{
    public static void Run(Action<bool, string> check)
    {
        Hitch(check);
    }

    static void Hitch(Action<bool, string> check)
    {
        float old = FrameHitch.ThresholdMs; int oldCap = FrameHitch.MaxLinesPerSecond;
        FrameHitch.ThresholdMs = 30f; FrameHitch.MaxLinesPerSecond = 2;
        FrameHitch.Close(0, 0, 0, 0, 0);   // start clean
        long t0 = FrameHitch.Begin(); Thread.Sleep(2); FrameHitch.End(FrameHitch.Part.Apply, t0);
        FrameHitch.Uploaded(2048 * 1024); FrameHitch.Uploaded(1024); FrameHitch.MeshDone(); FrameHitch.Snapshot();
        check(FrameHitch.Close(14f, 3f, 0, 0, 10.0) == null, "hitch: a 14 ms frame logs nothing");
        check(FrameHitch.Uploads == 0 && FrameHitch.Ms(FrameHitch.Part.Apply) == 0, "hitch: every frame starts its counters from zero");
        t0 = FrameHitch.Begin(); Thread.Sleep(2); FrameHitch.End(FrameHitch.Part.Apply, t0);
        FrameHitch.Uploaded(2048 * 1024); FrameHitch.MeshDone(); FrameHitch.MeshDone(); FrameHitch.Snapshot();
        string line = FrameHitch.Close(45.5f, 31f, 1, 300L << 20, 10.1);
        check(line != null && line.StartsWith("[Tabletop] HITCH ms=45.5 script_ms=31.0 apply_ms=") && line.Contains(" uploads=1 upload_kb=2048 ") &&
              line.Contains(" mesh_done=2 snap=1 gc=1 heap_mb=300 skipped=0") && !line.Contains(","), $"hitch: one line with the frame's counters ({line})");
        check(FrameHitch.Close(40f, 1f, 0, 0, 10.2) != null && FrameHitch.Close(40f, 1f, 0, 0, 10.3) == null, "hitch: at most MaxLinesPerSecond lines a second");
        string next = FrameHitch.Close(40f, 1f, 0, 0, 11.3);
        check(next != null && next.EndsWith("skipped=1"), "hitch: the next second's first line counts the skipped ones");
        FrameHitch.ThresholdMs = 0;
        check(FrameHitch.Close(500f, 1f, 0, 0, 20) == null, "hitch: hitch_ms 0 turns it off");
        FrameHitch.ThresholdMs = old; FrameHitch.MaxLinesPerSecond = oldCap;
    }
}
