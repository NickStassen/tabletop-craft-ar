// Engine-free tests for the Quest scripts: JSON, RLE, the greedy mesher, the pose frame conversion, and
// (with --live ws://...) a real session against the Paper plugin using the same LinkClient the headset uses.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class Program
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

    static WorldModel Box(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static int Main(string[] args)
    {
        // JSON round trip
        var o = Json.Parse("{\"a\":[1,2.5,-3e2],\"b\":\"x\\\"y\\u00e9\",\"c\":null,\"d\":true}") as Dictionary<string, object>;
        Check(o.A("a").Count == 3 && (double)o.A("a")[2] == -300 && o.S("b") == "x\"yé" && o["c"] == null && o.B("d"), "json parse");
        Check(Json.Write(new Dictionary<string, object> { ["k"] = new List<object> { 1, 0.5, "s" } }) == "{\"k\":[1,0.5,\"s\"]}", "json write");

        // RLE (pairs index,run)
        var g = ChunkSnapshot.DecodeRle(new List<object> { 1.0, 2.0, 0.0, 1.0 }, 3, 2);
        Check(g.SequenceEqual(new ushort[] { 1, 1, 0 }), "rle decode");
        try { ChunkSnapshot.DecodeRle(new List<object> { 0.0, 2.0 }, 3, 1); Check(false, "rle short total rejected"); } catch (FormatException) { Check(true, "rle short total rejected"); }

        // Mesher: a solid 4x4x4 stone cube = 6 merged quads.
        var m = new MeshData();
        var cube = Box(4, 4, 4, (x, y, z) => 1, "minecraft:air", "minecraft:stone");
        GreedyMesher.Build(cube, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count == 24 && m.Opaque.Count == 36, $"solid cube -> 6 quads (got {m.Verts.Count / 4})");
        // Checkerboard of two block types on a floor: no merging across different keys.
        var chk = Box(4, 1, 4, (x, y, z) => 1 + ((x + z) & 1), "minecraft:air", "minecraft:stone", "minecraft:dirt");
        GreedyMesher.Build(chk, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 16 + 16 + 8 + 8, $"checkerboard floor quads (got {m.Verts.Count / 4})");
        // Hidden faces: two cubes touching produce 10 faces merged into 6 quads (1x2 rectangles).
        var two = Box(2, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:stone");
        GreedyMesher.Build(two, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 6, $"two touching cubes -> 6 quads (got {m.Verts.Count / 4})");
        // Glass next to glass shares no face; air pocket inside stone is skipped (faces inward).
        var glass = Box(2, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:glass");
        GreedyMesher.Build(glass, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Verts.Count / 4 == 6 && m.Transparent.Count == 36, "glass pair -> 6 transparent quads");
        // Cross placeholder
        var flower = Box(1, 1, 1, (x, y, z) => 1, "minecraft:air", "minecraft:poppy");
        GreedyMesher.Build(flower, new Vector3Int(0, 0, 0), new Flat(), m);
        int before = m.Verts.Count;
        GreedyMesher.BuildSpecials(flower, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(before == 0 && m.Verts.Count == 8, "poppy -> cross quads only");

        // Pose conversion (PROTOCOL.md section 3): Unity identity looks +Z (Unity forward) -> tabletop -Z (north).
        var tp = TabletopPose.FromUnityLocal(new Vector3(0.1f, 0.5f, 0.2f), new Quaternion(0.1f, 0.2f, 0.3f, 0.927f));
        Check(tp.p.x == 0.1f && tp.p.z == -0.2f && tp.q.x == -0.1f && tp.q.y == -0.2f && tp.q.z == 0.3f, "unity -> tabletop frame mapping");

        // Block update with palette_add, and stale snapshot drop
        var wm = Box(2, 2, 2, (x, y, z) => 0, "minecraft:air");
        var upd = Json.Parse("{\"snapshot_id\":1,\"palette_add\":[\"minecraft:torch\"],\"changes\":[[1,1,1,1]]}") as Dictionary<string, object>;
        Check(wm.ApplyBlockUpdate(upd) && wm.State(wm.Get(1, 1, 1)) == "minecraft:torch", "block_update palette_add");
        Check(!wm.ApplyBlockUpdate(Json.Parse("{\"snapshot_id\":9,\"changes\":[]}") as Dictionary<string, object>), "stale block_update dropped");

        int liveIdx = Array.IndexOf(args, "--live");
        if (liveIdx >= 0) Live(args[liveIdx + 1]);
        Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
        return fails == 0 ? 0 : 1;
    }

    static void Live(string url)
    {
        var link = new LinkClient(url, "");
        link.Start();
        var world = new WorldModel();
        int updates = 0, playerMsgs = 0, posesSent = 0; long rtt = -1; string avatarNote = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var mesh = new MeshData();
        while (DateTime.UtcNow < deadline)
        {
            while (link.Inbound.TryDequeue(out var m))
            {
                switch (m.S("type"))
                {
                    case "welcome": link.Send(Protocol.AvatarConfig(true, 0.01f, "", true, "Tabletop Giant")); link.Send(Protocol.Ping(1)); break;
                    case "chunk_snapshot": world.ApplySnapshot(ChunkSnapshot.From(m)); break;
                    case "block_update": if (world.ApplyBlockUpdate(m)) updates++; break;
                    case "player_positions": playerMsgs++; break;
                    case "pong": rtt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)m.D("echo_t"); break;
                    case "avatar_state": avatarNote = $"enabled={m.B("enabled")} scale={m.D("scale")}"; break;
                    case "error": Console.WriteLine("server error " + m.S("code") + " " + m.S("message")); fails++; break;
                }
            }
            if (link.Connected)
            {
                double t = posesSent / 30.0;
                var head = TabletopPose.FromUnityLocal(new Vector3(0.16f + 0.1f * (float)Math.Cos(t), 0.45f, -(0.16f + 0.1f * (float)Math.Sin(t))), new Quaternion(0, 0, 0, 1));
                link.Send(Protocol.Pose(posesSent, head, head, head));
                posesSent++;
            }
            Thread.Sleep(33);
        }
        Check(world.Loaded, $"live: snapshot received dims={world.Dims} palette={world.Palette.Count}");
        int quads = 0;
        var n = world.SectionCount;
        for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++) { GreedyMesher.Build(world, new Vector3Int(x, y, z), new Flat(), mesh); quads += mesh.Verts.Count / 4; }
        Check(quads > 0, $"live: meshed {n.x * n.y * n.z} sections into {quads} quads");
        Check(rtt >= 0, $"live: ping/pong rtt {rtt} ms");
        Check(avatarNote != null, $"live: avatar_state {avatarNote}");
        Console.WriteLine($"live: poses sent {posesSent}, player msgs {playerMsgs}, block updates {updates}");
        link.Dispose();
    }
}
