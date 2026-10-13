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

    static string Fixture(string n)
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        { var p = System.IO.Path.Combine(d.FullName, "tools", "fixtures", n); if (System.IO.File.Exists(p)) return p; }
        throw new System.IO.FileNotFoundException(n);
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

        // Area setup maths
        Check(Math.Abs(AreaMath.FitBlockSize(1.2f, 0.8f, 64, 64, 1f) - 0.0125f) < 1e-6, "fit: min(tableW/dx, tableD/dz)");
        Check(Math.Abs(AreaMath.FitBlockSize(1.2f, 0.8f, 128, 32, 1f) - 1.2f / 128) < 1e-6, "fit: width-limited");
        Check(AreaMath.FitBlockSize(0.5f, 0.5f, 100000, 100000) == AreaMath.MinBlockM && AreaMath.FitBlockSize(10, 10, 1, 1) == AreaMath.MaxBlockM, "fit: clamped");
        Check(AreaMath.FitBlockSize(0, 1, 16, 16) == 0.01f, "fit: no table -> default");
        var e32 = AreaMath.Estimate(32, 96, 32); var e128 = AreaMath.Estimate(128, 384, 128);
        Check(e32.Quads == 3584 && e32.Vertices == 4 * e32.Quads && e32.Triangles == 2 * e32.Quads, $"estimate 32x96x32 = {e32}");
        Check(AreaMath.Estimate(64, 96, 64).Quads == 4 * e32.Quads, "estimate scales with columns");
        Check(AreaMath.Estimate(32, 384, 32).Quads > e32.Quads * 3, "estimate grows with depth (caves)");
        Check(!AreaMath.OverBudget(e128, AreaMath.DefaultQuadBudget, false), $"128x384x128 {e128} within default budget");
        Check(AreaMath.OverBudget(AreaMath.Estimate(512, 384, 512), AreaMath.DefaultQuadBudget, false), "512x384x512 over budget");
        Check(!AreaMath.OverBudget(AreaMath.Estimate(512, 384, 512), AreaMath.DefaultQuadBudget, true), "override lifts soft cap");
        Check(AreaMath.ClampToCaps(300, 384, 300, 128, 384, 128, 2097152) == (128, 128, 128), "clamp to server caps");

        // Area setup state + protocol builders vs fixtures
        var st = new AreaSetupState();
        st.Begin("world", new Vector3Int(0, -64, 0), new Vector3Int(32, 64, 32));
        Check(st.Center.x == 16 && st.Center.z == 16 && st.Mode == CenterMode.CurrentOrigin, "area: centre on current origin");
        st.Resize(0, 100); st.Resize(2, -100); st.Resize(1, 1);
        Check(st.Size.x == 128 && st.Size.z == 8 && st.Size.y == 80, $"area: resize clamps to caps/min (got {st.Size})");
        var info = RegionInfo.From(Json.Parse(System.IO.File.ReadAllText(Fixture("region_info.json"))) as Dictionary<string, object>);
        Check(info.Players.Count == 1 && info.Limits.MaxX == 128 && info.Dims.x == 32, "region_info fixture parses");
        st.SetInfo(info); st.NextPlayer(1);
        Check(st.Mode == CenterMode.Player && st.Center.x == 12 && st.Center.z == -41, $"area: centre on player (got {st.Center})");
        var rq = st.BuildRequest();
        var fx = Json.Parse(System.IO.File.ReadAllText(Fixture("region_request.json"))) as Dictionary<string, object>;
        Check(rq.S("type") == "region_request" && rq.ContainsKey("center") && rq.ContainsKey("size") && rq.ContainsKey("y_origin") && fx.ContainsKey("center") && fx.ContainsKey("y_origin"), "region_request matches fixture shape");
        st.Size = new Vector3Int(128, 384, 128); st.Limits = new RegionLimits { MaxX = 512, MaxY = 384, MaxZ = 512, MaxBlocks = long.MaxValue }; st.Resize(0, 48); st.Resize(2, 48);
        Check(st.OverBudget && st.Blocker() != null, $"area: soft cap warns ({st.Estimate})");
        st.OverrideBudget = true;
        Check(st.Blocker() == null, "area: override allows confirm");
        var err = RegionLimits.From(Json.Parse(System.IO.File.ReadAllText(Fixture("error_region_too_large.json"))) as Dictionary<string, object>, default);
        Check(err.MaxX == 128 && err.MaxBlocks == 2097152, "region_too_large limits parse");
        Check(Protocol.RegionQuery("Bob").S("player") == "Bob" && !Protocol.RegionQuery().ContainsKey("player"), "region_query builder");

        int measIdx = Array.IndexOf(args, "--measure");
        if (measIdx >= 0) { Measure(args[measIdx + 1]); return 0; }

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
        var area = new AreaSetupState(); bool requested = false, changed = false; string session = null; Vector3Int resized = default;
        int updates = 0, playerMsgs = 0, posesSent = 0; long rtt = -1; string avatarNote = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var mesh = new MeshData();
        while (DateTime.UtcNow < deadline)
        {
            while (link.Inbound.TryDequeue(out var m))
            {
                switch (m.S("type"))
                {
                    case "welcome": session = m.S("session"); area.Limits = RegionLimits.From(m.O("limits"), RegionLimits.Default); link.Send(Protocol.AvatarConfig(true, 0.01f, "", true, "Tabletop Giant")); link.Send(Protocol.Ping(1)); break;
                    case "chunk_snapshot":
                        world.ApplySnapshot(ChunkSnapshot.From(m));
                        if (!requested) { requested = true; area.Begin(world.World, world.Origin, world.Dims); link.Send(Protocol.RegionQuery()); }
                        else if (changed) resized = world.Dims;
                        break;
                    case "region_info":
                        area.SetInfo(RegionInfo.From(m));
                        area.Resize(0, 2); area.Resize(2, 2);   // +16 x/z via the same state the headset uses
                        link.Send(area.BuildRequest());
                        break;
                    case "region_changed": changed = m.S("by_session") == session; break;
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
        Check(changed && resized.x == area.Size.x && resized.z == area.Size.z, $"live: area setup resize -> region_changed + snapshot {resized}");
        Check(rtt >= 0, $"live: ping/pong rtt {rtt} ms");
        Check(avatarNote != null, $"live: avatar_state {avatarNote}");
        Console.WriteLine($"live: poses sent {posesSent}, player msgs {playerMsgs}, block updates {updates}");
        link.Dispose();
    }

    static void Measure(string dir)
    {
        foreach (var f in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            var w = new WorldModel();
            w.ApplySnapshot(ChunkSnapshot.From(Json.Parse(System.IO.File.ReadAllText(f)) as Dictionary<string, object>));
            var mesh = new MeshData();
            long quads = 0, specials = 0, maxSection = 0;
            var n = w.SectionCount;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double worst = 0;
            for (int x = 0; x < n.x; x++) for (int y = 0; y < n.y; y++) for (int z = 0; z < n.z; z++)
            {
                double t0 = sw.Elapsed.TotalMilliseconds;
                GreedyMesher.Build(w, new Vector3Int(x, y, z), new Flat(), mesh);
                long q = mesh.Verts.Count / 4;
                GreedyMesher.BuildSpecials(w, new Vector3Int(x, y, z), new Flat(), mesh);
                long s = mesh.Verts.Count / 4;
                quads += q; specials += s - q; maxSection = Math.Max(maxSection, s);
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - t0);
            }
            var est = AreaMath.Estimate(w.Dims.x, w.Dims.y, w.Dims.z);
            Console.WriteLine($"MEASURE {w.Dims.x}x{w.Dims.y}x{w.Dims.z} sections={n.x * n.y * n.z} quads={quads} specials={specials} verts={(quads + specials) * 4} " +
                              $"tris={(quads + specials) * 2} max_section_quads={maxSection} mesh_ms={sw.Elapsed.TotalMilliseconds:F0} worst_section_ms={worst:F1} estimate={est.Quads}");
        }
    }
}
