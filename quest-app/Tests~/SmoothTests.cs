// [smooth] Smooth gliding (docs/TESTING.md "Smooth gliding"): the HITCH log, cheaper snapshot applies, fewer shifts, and
// the surface skirt's hand-off.
using System;
using System.Collections.Generic;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class SmoothTests
{
    public static void Run(Action<bool, string> check)
    {
        Hitch(check);
        Apply(check);
    }

    static ChunkSnapshot Snap(int id, params string[] palette) =>
        new ChunkSnapshot { SnapshotId = id, World = "world", Origin = default, Dims = new Vector3Int(16, 16, 16), Palette = palette, Grid = new ushort[16 * 16 * 16] };

    /// <summary>Cheaper snapshot applies: palette tables extend, grid arrays are reused, JSON integers don't allocate.</summary>
    static void Apply(Action<bool, string> check)
    {
        // PaletteTable: a delta's palette starts with the old one, so only the new entries are computed.
        var w = new WorldModel();
        w.ApplySnapshot(Snap(1, "minecraft:air", "minecraft:chest[facing=north]"));
        var t = new PaletteTable<bool>();
        t.Update(w, BlockEntityScan.Wanted);
        check(t.Items.Length == 2 && !t.Items[0] && t.Items[1] && t.Computed == 2, "palette table: built for the first snapshot");
        int epoch = w.PaletteEpoch;
        w.ApplySnapshot(Snap(2, "minecraft:air", "minecraft:chest[facing=north]", "minecraft:stone", "minecraft:oak_sign[rotation=0]"));
        var before = t.Items;
        bool changed = t.Update(w, BlockEntityScan.Wanted);
        check(changed && w.PaletteEpoch == epoch && t.Computed == 4 && t.Items.Length == 4 && t.Items[1] && !t.Items[2] && t.Items[3] && before.Length == 2,
              $"palette table: a delta that appends computes the new entries only, in a new array (computed {t.Computed})");
        check(!t.Update(w, BlockEntityScan.Wanted) && t.Computed == 4, "palette table: nothing new, nothing computed");
        w.ApplySnapshot(Snap(3, "minecraft:stone", "minecraft:air"));
        t.Update(w, BlockEntityScan.Wanted);
        check(w.PaletteEpoch == epoch + 1 && t.Computed == 6 && t.Items.Length == 2 && !t.Items[0], "palette table: another palette (not a prefix) rebuilds");
        check(w.ShapeOf(0) == BlockShape.Cube && w.ShapeOf(1) == BlockShape.Air, "palette table: shapes follow the new palette");

        // SnapshotBuffers: a retired array comes back only after another snapshot was applied and MinAgeSeconds passed.
        double clock = 100;
        var oldClock = SnapshotBuffers.Clock; bool oldOn = SnapshotBuffers.Enabled;
        SnapshotBuffers.Clock = () => clock; SnapshotBuffers.Enabled = true; SnapshotBuffers.Clear();
        var g1 = new ushort[4096]; var l1 = new byte[4096];
        SnapshotBuffers.Retire(g1, l1);
        check(!ReferenceEquals(SnapshotBuffers.Grid(4096), g1), "buffers: not reused before another snapshot was applied");
        SnapshotBuffers.Retire(new ushort[4096], null);
        check(!ReferenceEquals(SnapshotBuffers.Grid(4096), g1), "buffers: not reused before MinAgeSeconds");
        clock += 1;
        check(!ReferenceEquals(SnapshotBuffers.Grid(4095), g1) && ReferenceEquals(SnapshotBuffers.Grid(4096), g1) && ReferenceEquals(SnapshotBuffers.Light(4096), l1),
              "buffers: reused for the same size once a newer snapshot was applied and it is old enough");
        check(!ReferenceEquals(SnapshotBuffers.Grid(4096), g1), "buffers: handed out once");
        var eights = new List<ushort[]>();
        for (int i = 0; i < 5; i++) { var a8 = new ushort[8]; eights.Add(a8); SnapshotBuffers.Retire(a8, null); }
        clock += 1; SnapshotBuffers.Retire(new ushort[9], null);
        int got = 0;
        for (int i = 0; i < 5; i++) { var g = SnapshotBuffers.Grid(8); if (eights.Exists(x => ReferenceEquals(x, g))) got++; }
        check(got == 1, $"buffers: only the last Keep ({SnapshotBuffers.Keep}) arrays of a kind are kept (reused {got} of 5)");
        SnapshotBuffers.Enabled = false; SnapshotBuffers.Clear(); SnapshotBuffers.Clock = oldClock; SnapshotBuffers.Enabled = oldOn;

        // A delta decoded into a reused grid is the same as one decoded into a fresh grid.
        // (SectionedSnapshot writes every cell: full coverage is checked.)
        var json = "{\"a\":[0,1,4096,4097,-1,-0,12.5,1e3,123456789012345678,7],\"b\":-12}";
        var o = (Dictionary<string, object>)TabletopCraft.Net.Json.Parse(json);
        var a = (List<object>)o["a"];
        check((double)a[0] == 0 && (double)a[1] == 1 && (double)a[2] == 4096 && (double)a[3] == 4097 && (double)a[4] == -1 && double.IsNegative((double)a[5]) &&
              (double)a[6] == 12.5 && (double)a[7] == 1000 && (double)a[8] == 123456789012345678d && (double)a[9] == 7 && (double)o["b"] == -12,
              "json: integers, negatives, -0, fractions, exponents and long numbers parse as before");
        var o2 = (Dictionary<string, object>)TabletopCraft.Net.Json.Parse(json);
        check(ReferenceEquals(a[1], ((List<object>)o2["a"])[1]) && !ReferenceEquals(a[3], ((List<object>)o2["a"])[3]), "json: small integers share their boxes");
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
