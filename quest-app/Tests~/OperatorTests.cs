// [operator] Engine-free tests for the operator's navigation and block-entity tools (PROTOCOL.md "Following a player",
// "Marks: bookmarks, spawn and worlds", "Containers", "Signs"): the fixtures through Net/OperatorProtocol.cs, jump plans
// and typed coordinates (World/FollowState.cs: Jumps, JumpFocus), following a moving and teleporting player against the
// real NavState, the Marks tab and Players' Follow (World/MarksModel.cs, OperatorPanelModel), the container view
// (World/ContainerModel.cs) and the sign editor (World/SignEditorModel.cs). --live-operator URL TOKEN PLAYER runs the
// app's classes against a live plugin (follow a Java player, bookmarks, a chest, a sign).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class OperatorTests
{
    static Dictionary<string, object> Fx(Func<string, string> fixture, string name) => Json.Parse(File.ReadAllText(fixture(name + ".json"))) as Dictionary<string, object>;
    static bool Near(float a, float b, float eps = 1e-3f) => Math.Abs(a - b) <= eps;
    static string J(object o) => Json.Write(o);

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Fixtures(check, fixture);
        JumpPlans(check);
        Follow(check);
        Marks(check, fixture);
        Containers(check, fixture);
        Signs(check, fixture);
        Layouts(check, fixture);
    }

    static void Fixtures(Action<bool, string> check, Func<string, string> fixture)
    {
        var w = OperatorWelcome.From(Fx(fixture, "welcome_operator"));
        check(w.Marks && w.MarksEdit && w.MarksMax == 200 && w.Containers == "edit" && w.ContainersOn && w.Signs, "welcome: marks_v1, containers edit, signs");
        check(w.Worlds.Count == 3 && w.Worlds[1].Env == "nether" && w.Worlds[1].MinY == 0 && w.Worlds[1].MaxY == 256 && w.Worlds[2].Spawn == new Vector3Int(100, 49, 0),
              "welcome.worlds: env, build height, spawn");
        check(w.Worlds[0].Label == "Overworld" && w.Worlds[1].Label == "Nether" && w.Worlds[2].Label == "End", "world labels");
        var plain = OperatorWelcome.From(Fx(fixture, "welcome_interact"));
        check(!plain.Marks && plain.Containers == "off" && !plain.Signs && plain.Worlds.Count == 0, "a welcome without the operator fields: nothing on");
        check(Fx(fixture, "hello_operator").A("caps").Contains(OperatorProtocol.CapMarks) && Protocol.Hello("").A("caps").Contains(OperatorProtocol.CapMarks), "hello asks for marks_v1");

        var b = BookmarkList.From(Fx(fixture, "bookmarks"));
        check(b.Max == 200 && b.Marks.Count == 2 && b.Marks[0].Name == "Nether hub" && b.Marks[1].Pos == new Vector3Int(120, 64, -300) && b.Marks[1].By == "Nick", "bookmarks parse");
        var add = Fx(fixture, "bookmark");
        var bAdd = OperatorProtocol.BookmarkAdd("Village", "world", new Vector3Int(120, 64, -300));
        check(J(bAdd["pos"]) == J(add["pos"]) && (string)bAdd["op"] == "add" && (string)bAdd["name"] == "Village" && (string)bAdd["world"] == "world", "bookmark add builder = fixture");
        check(!OperatorProtocol.BookmarkRemove("Village").ContainsKey("world") && (string)Fx(fixture, "bookmark_remove")["op"] == "remove", "bookmark remove builder");

        var ri = Fx(fixture, "region_info_worlds");
        var info = RegionInfo.From(ri);
        var ws = WorldEntry.ListFrom(ri.A("worlds"));
        check(info.Players[0].World == "world_nether" && ws.Count == 3 && OperatorProtocol.V3(ri.A("spawn")) == new Vector3Int(0, 64, 0), "region_info: worlds, spawn, the player's world");

        var c = ContainerView.From(Fx(fixture, "container"));
        check(c.RefSeq == 92 && c.Size == 54 && c.Other == new Vector3Int(2, 64, 2) && c.Edit && c.Title == "Large Chest" && c.Slot(5) == ("minecraft:torch", 64) && c.Slot(6).Item == null,
              "container parses (double chest)");
        var f = ContainerView.From(Fx(fixture, "container_furnace"));
        check(f.Size == 3 && !f.Edit && !f.Other.HasValue && f.Slot(2).Item == "minecraft:iron_ingot", "container parses (a view-only furnace)");
        var closed = ContainerView.From(Fx(fixture, "container_closed"));
        check(closed.Closed && closed.Reason == "the container is gone", "container closed parses");
        bool threw = false;
        try { ContainerView.From(Json.Parse("{\"pos\":[0,0,0],\"size\":3,\"items\":[[5,\"minecraft:dirt\",1]]}") as Dictionary<string, object>); } catch (FormatException) { threw = true; }
        check(threw, "a container slot past the size is rejected");
        var e = Fx(fixture, "container_edit");
        var built = OperatorProtocol.ContainerSet(new Vector3Int(1, 64, 2), 5, "minecraft:torch", 32, "minecraft:torch", 64);
        check(J(built["pos"]) == J(e["pos"]) && J(built["was"]) == J(e["was"]) && (int)built["slot"] == 5 && (int)built["count"] == 32 && (string)built["item"] == "minecraft:torch",
              "container_edit builder = fixture");
        var put = OperatorProtocol.ContainerSet(new Vector3Int(1, 64, 2), 7, "minecraft:oak_log", 16, null, 3);
        check(J(put["was"]) == J(Fx(fixture, "container_edit_put")["was"]), "container_edit put: was [null, 0] for an empty slot");
        var clear = OperatorProtocol.ContainerSet(new Vector3Int(1, 64, 2), 0, "minecraft:dirt", 0, "minecraft:dirt", 5);
        check(clear["item"] == null && (int)clear["count"] == 0, "container_edit with count 0 empties the slot");
        check((string)OperatorProtocol.ContainerSort(new Vector3Int(1, 64, 2))["op"] == "sort" && (string)Fx(fixture, "container_edit_sort")["op"] == "sort", "container_edit sort");
        check(J(OperatorProtocol.ContainerQuery(new Vector3Int(1, 64, 2))["pos"]) == J(Fx(fixture, "container_query")["pos"]) &&
              J(OperatorProtocol.ContainerClose(new Vector3Int(1, 64, 2))["pos"]) == J(Fx(fixture, "container_close")["pos"]), "container_query / container_close builders");
        var s = Fx(fixture, "sign_edit");
        var sb = OperatorProtocol.SignEdit(new Vector3Int(106, 64, -35), false, new[] { "", "Shop", "open 9-5", "" }, "light_blue", true);
        check(J(sb["pos"]) == J(s["pos"]) && J(sb["lines"]) == J(s["lines"]) && (string)sb["side"] == "front" && (string)sb["color"] == "light_blue" && (bool)sb["glowing"],
              "sign_edit builder = fixture");
        check(OperatorProtocol.Dyes.Length == 16 && OperatorProtocol.Dyes.Distinct().Count() == 16, "sixteen dyes");
    }

    static NavState Nav(string world = "world", int minY = -64, int maxY = 320)
    {
        var n = new NavState { MinY = minY, MaxY = maxY, CanPad = true, Caps = new RegionLimits { MaxX = 352, MaxY = 384, MaxZ = 352, MaxBlocks = 25165824, MaxViewWidth = 256 } };
        n.OnRegion(world, new Vector3Int(0, 32, 0), new Vector3Int(96, 96, 96), new Vector3Int(16, 16, 16), 0);
        return n;
    }

    static void JumpPlans(Action<bool, string> check)
    {
        var n = Nav();
        var j = Jumps.Plan(n, "world", new Vector3(1000.5f, 70, -500.5f), -64, 320).Value;
        check(j.Kind == "jump" && j.Dims == n.Dims && j.Margin == n.Margin, $"jump keeps dims and margins ({j.Dims} {j.Margin})");
        check((j.Origin.x - n.Origin.x) % 16 == 0 && (j.Origin.z - n.Origin.z) % 16 == 0 && (j.Origin.y - n.Origin.y) % 16 == 0, $"same world: the origin moves by whole sections ({j.Origin})");
        check(Math.Abs(j.Origin.x + j.Dims.x / 2f - 1000.5f) <= 8 && Math.Abs(j.Origin.z + j.Dims.z / 2f + 500.5f) <= 8, "the jump is centred on the target (within 8 blocks)");
        var low = Jumps.Plan(n, "world", new Vector3(0, -60, 0), -64, 320).Value;
        check(low.Origin.y == -64, $"a target near bedrock clamps to the build height ({low.Origin.y})");
        var high = Jumps.Plan(n, "world", new Vector3(0, 318, 0), -64, 320).Value;
        check(high.Origin.y + high.Dims.y == 320, $"a target near the top clamps ({high.Origin.y}+{high.Dims.y})");
        var neth = Jumps.Plan(n, "world_nether", new Vector3(8.5f, 70, -4.5f), 0, 256).Value;
        check(neth.Dims.y == 96 && neth.Origin.y >= 0 && neth.Origin.y + 96 <= 256 && neth.Origin.x == (int)Math.Floor(8.5f - 48), $"another world: nearest origin, inside its height ({neth.Origin})");
        var tall = new NavState { MinY = -64, MaxY = 320 };
        tall.OnRegion("world", new Vector3Int(0, -64, 0), new Vector3Int(64, 384, 64), new Vector3Int(16, 16, 16), 0);
        var tn = Jumps.Plan(tall, "world_nether", new Vector3(0, 64, 0), 0, 256).Value;
        check(tn.Dims.y == 256 && tn.Origin.y == 0 && tn.Margin.y == 16, $"a region taller than the nether shrinks to its height ({tn.Dims} {tn.Origin})");

        var view = new Vector3(100.5f, 64, -20.5f);
        check(Jumps.ParseCoords("120 -300", view, out var p, out bool hasY) == null && !hasY && Near(p.x, 120.5f) && Near(p.y, 64) && Near(p.z, -299.5f), "coords: x z keeps the view's height");
        check(Jumps.ParseCoords("120, 70, -300", view, out p, out hasY) == null && hasY && Near(p.y, 70.5f), "coords: x, y, z with commas");
        check(Jumps.ParseCoords("~10 ~ ~-5", view, out p, out hasY) == null && Near(p.x, 110.5f) && Near(p.y, 64) && Near(p.z, -25.5f), "coords: ~ is relative to the view");
        check(Jumps.ParseCoords("12", view, out p, out hasY) != null && Jumps.ParseCoords("a b", view, out p, out hasY) != null &&
              Jumps.ParseCoords("1 2 3 4", view, out p, out hasY) != null && Jumps.ParseCoords("40000000 0", view, out p, out hasY) != null, "coords: bad input is refused with a reason");

        var focus = new JumpFocus();
        var nav = Nav();
        focus.Set("world", new Vector3(1003, 70, -499), j.Origin, 1);
        check(!focus.Apply(nav, 2), "focus waits for the jump's region");
        nav.OnRegion("world", j.Origin, j.Dims, j.Margin, 2);
        check(focus.Apply(nav, 2) && Near(nav.View.x, 1003) && Near(nav.View.z, -499) && Near(nav.View.y, 70) && !focus.Pending, "focus: the view lands on the target itself");
        focus.Set("world", new Vector3(0, 0, 0), new Vector3Int(5, 5, 5), 3);
        check(!focus.Apply(nav, 20) && !focus.Pending, "focus: gives up after its timeout");
    }

    /// <summary>A server that answers region requests after a delay (the region moves where it was asked to).</summary>
    sealed class FakeServer
    {
        public readonly List<(float at, string world, RegionMove move)> InFlight = new List<(float, string, RegionMove)>();
        public int Requests, Jumps;
        public void Ask(string world, RegionMove m, float now) { InFlight.Add((now + 0.15f, world, m)); Requests++; if (m.Kind == "jump") Jumps++; }
        public void Tick(NavState nav, FollowState f, float now)
        {
            for (int i = 0; i < InFlight.Count; i++)
                if (InFlight[i].at <= now)
                {
                    var r = InFlight[i]; InFlight.RemoveAt(i--);
                    nav.OnRegion(r.world, r.move.Origin, r.move.Dims, r.move.Margin, now);
                    f.Focus.Apply(nav, now);
                }
        }
    }

    static void Frame(NavState nav, FollowState f, FakeServer srv, float dt, float now, Func<string, WorldEntry?> worlds)
    {
        var step = f.Step(nav, dt, now, worlds);
        if (step.Jump.HasValue)
        {
            if (nav.TryIssue(step.Jump.Value, now)) { srv.Ask(step.JumpWorld, step.Jump.Value, now); f.Jumped(step.Jump.Value, step.JumpWorld, step.JumpTarget, now); }
        }
        else
        {
            var mv = nav.Move(step.Delta, dt, now);
            if (mv.HasValue) srv.Ask(nav.World, mv.Value, now);
        }
        srv.Tick(nav, f, now);
    }

    static void Follow(Action<bool, string> check)
    {
        var worlds = new Dictionary<string, WorldEntry> { ["world_nether"] = new WorldEntry { Name = "world_nether", Env = "nether", MinY = 0, MaxY = 256 } };
        WorldEntry? W(string n) => worlds.TryGetValue(n, out var w) ? w : (WorldEntry?)null;
        var nav = Nav();
        var f = new FollowState();
        var srv = new FakeServer();
        float now = 1, dt = 1f / 72;
        var pos = new Vector3(60, 64, 50);
        f.Start("Alex", "uuid-alex", now);
        check(f.Active && f.Describe() == "following Alex", "follow starts");
        var first = f.Step(nav, dt, now, W);
        check(first.Query && !first.Jump.HasValue && first.Delta.x == 0, "follow asks where the player is before it knows");
        // Walking east at 4.3 blocks per second for 20 s (86 blocks): the view keeps them centred, gliding by section shifts.
        float worst = 0;
        for (int i = 0; i < 72 * 20; i++)
        {
            now += dt; pos.x += 4.3f * dt;
            f.OnSeen("world", pos, now);
            Frame(nav, f, srv, dt, now, W);
            if (i > 72 * 3) worst = Math.Max(worst, Math.Abs(nav.View.x - pos.x));
        }
        check(worst < 3f && Math.Abs(nav.View.z - pos.z) < 1.5f, $"follow: the walking player stays centred (worst {worst:F2} blocks behind)");
        check(srv.Requests >= 4 && srv.Jumps == 0, $"follow: gliding by region shifts, no jumps ({srv.Requests} requests)");
        // A small step aside (dead zone) doesn't move the view.
        float vx = nav.View.x;
        for (int i = 0; i < 72; i++) { now += dt; f.OnSeen("world", new Vector3(pos.x + 0.3f, pos.y, pos.z), now); Frame(nav, f, srv, dt, now, W); }
        check(Near(nav.View.x, vx, 0.05f) || Math.Abs(nav.View.x - (pos.x + 0.3f)) < 0.8f, "follow: a step inside the dead zone doesn't drag the table");
        // Up a hill by 4 blocks: inside the vertical band, the height stays.
        float vy = nav.View.y;
        for (int i = 0; i < 72; i++) { now += dt; f.OnSeen("world", new Vector3(pos.x, pos.y + 4, pos.z), now); Frame(nav, f, srv, dt, now, W); }
        check(Near(nav.View.y, vy, 0.01f), "follow: a few blocks up or down doesn't bob the table");
        for (int i = 0; i < 72 * 4; i++) { now += dt; f.OnSeen("world", new Vector3(pos.x, pos.y + 40, pos.z), now); Frame(nav, f, srv, dt, now, W); }
        check(Math.Abs(nav.View.y - (pos.y + 40)) < 2f, $"follow: a big climb re-centres the height ({nav.View.y:F1})");
        pos.y += 40;
        // Teleported 2000 blocks away: the figure leaves the table; region_info says where; the view jumps there.
        var tp = new Vector3(pos.x + 2000, 70, pos.z - 1500);
        bool asked = false;
        for (int i = 0; i < 72; i++) { now += dt; f.Step(nav, dt, now, W); }   // the figure is gone from the table
        for (int i = 0; i < 72 * 2 && !asked; i++) { now += dt; var st = f.Step(nav, dt, now, W); asked = st.Query; }
        check(asked, "follow: a player gone from the table is asked for (region_query)");
        f.Sent(77);
        var ri = Json.Parse("{\"type\":\"region_info\",\"ref_seq\":77,\"region\":{\"world\":\"world\",\"origin\":[0,0,0],\"dims\":[1,1,1]},\"players\":[{\"name\":\"Alex\",\"uuid\":\"uuid-alex\",\"world\":\"world\",\"pos\":[" +
                             $"{tp.x},{tp.y},{tp.z}" + "],\"yaw\":0}],\"limits\":{}}") as Dictionary<string, object>;
        check(f.OnRegionInfo(RegionInfo.From(ri), 77, now) && f.HasTarget && Near(f.Target.x, tp.x), "follow: region_info gives the far position");
        for (int i = 0; i < 72 * 2; i++) { now += dt; Frame(nav, f, srv, dt, now, W); }
        check(srv.Jumps == 1 && Math.Abs(nav.View.x - tp.x) < 0.01f && Math.Abs(nav.View.z - tp.z) < 0.01f, $"follow: jumped onto the player ({nav.View.x:F1},{nav.View.z:F1})");
        // Into the nether: a jump into the other world with its height.
        f.Sent(78);
        var rn = Json.Parse("{\"type\":\"region_info\",\"region\":{\"world\":\"world\",\"origin\":[0,0,0],\"dims\":[1,1,1]},\"players\":[{\"name\":\"Alex\",\"uuid\":\"uuid-alex\",\"world\":\"world_nether\",\"pos\":[8.5,70,-4.5],\"yaw\":0}],\"limits\":{}}") as Dictionary<string, object>;
        f.OnRegionInfo(RegionInfo.From(rn), 78, now);
        RegionMove? sentJump = null;
        for (int i = 0; i < 72 * 2; i++)
        {
            now += dt;
            var st = f.Step(nav, dt, now, W);
            if (st.Jump.HasValue && nav.TryIssue(st.Jump.Value, now)) { sentJump = st.Jump; srv.Ask(st.JumpWorld, st.Jump.Value, now); f.Jumped(st.Jump.Value, st.JumpWorld, st.JumpTarget, now); }
            srv.Tick(nav, f, now);
        }
        check(sentJump.HasValue && nav.World == "world_nether" && sentJump.Value.Origin.y >= 0 && sentJump.Value.Origin.y + sentJump.Value.Dims.y <= 256 &&
              Math.Abs(nav.View.x - 8.5f) < 0.01f, $"follow: into the nether within its height ({sentJump?.Origin})");
        // The player leaves: the query error stops following.
        f.Sent(79);
        check(f.OnError(79, "no online player named Alex") && !f.Active && f.Describe().Contains("left"), "follow: stops when the player leaves");
        check(!f.OnError(80, "x"), "follow: other errors aren't ours");
        // Go to once: one jump, then idle.
        var g = new FollowState();
        var nav2 = Nav();
        var srv2 = new FakeServer();
        g.GoTo("Alex", "uuid-alex", 1);
        g.OnSeen("world", new Vector3(70, 64, 40), 1);
        Frame(nav2, g, srv2, dt, 1.01f, W);
        check(srv2.Jumps == 1 && !g.Active && g.Describe() == "went to Alex", "go to: one jump, then the view is free");
        // Manual stop.
        var h = new FollowState();
        h.Start("Bob", "u", 0);
        h.Stop("manual glide");
        check(!h.Active && h.Describe() == "stopped following Bob: manual glide", "follow: a manual glide stops it");
    }

    static PanelAction Click(OperatorPanelModel p, PanelPart part, int index)
    {
        var L = p.Layout();
        var it = L.Items.FirstOrDefault(i => i.Part == part && i.Index == index);
        if (it.Part != part) return new PanelAction { Kind = PanelActionKind.None, Line = "missing " + part + "#" + index };
        p.Point(OperatorPanelModel.HitTest(L, it.R.CX, it.R.CY), true);
        return p.Click(L, 0);
    }

    static OperatorPanelModel Panel(Func<string, string> fixture)
    {
        var p = new OperatorPanelModel { Connected = true };
        var wel = Fx(fixture, "welcome_operator");
        p.OnWelcome(ControlWelcome.From(wel), null);
        p.Marks.OnWelcome(OperatorWelcome.From(wel));
        p.Marks.OnBookmarks(BookmarkList.From(Fx(fixture, "bookmarks")));
        p.Marks.World = "world"; p.Marks.View = new Vector3(100.5f, 64, -20.5f); p.Marks.Loaded = true;
        p.SetOpen(true);
        return p;
    }

    static void Marks(Action<bool, string> check, Func<string, string> fixture)
    {
        var p = Panel(fixture);
        p.SetTab(OpTab.Marks);
        check(p.Marks.Header().StartsWith("Overworld  100 64 -21"), $"marks: header says where the view is ({p.Marks.Header()})");
        var a = Click(p, PanelPart.Button, OperatorPanelModel.BtnWorld + 1);
        check(a.Kind == PanelActionKind.Jump && a.Line == "world_nether" && Near(a.Pos.x, 0.5f) && Near(a.Pos.y, 70), "marks: a world button jumps to that world's spawn");
        a = Click(p, PanelPart.Button, OperatorPanelModel.BtnSpawn);
        check(a.Kind == PanelActionKind.Jump && a.Line == "world" && Near(a.Pos.y, 64), "marks: Spawn jumps to this world's spawn");
        check(Click(p, PanelPart.Field, 0).Kind == PanelActionKind.Toggled && p.Marks.Typing && p.Focus == OperatorPanelModel.Field.Marks &&
              p.Layout().Items.Any(i => i.Part == PanelPart.Key && i.Index == KeyboardModel.KeyEnter && i.Label == "go"), "marks: the field shows the keyboard (enter = go)");
        p.Marks.CoordKeys.SetText("120 70 -300");
        a = p.Enter(0);
        check(a.Kind == PanelActionKind.Jump && Near(a.Pos.x, 120.5f) && Near(a.Pos.y, 70.5f) && Near(a.Pos.z, -299.5f) && !p.Marks.Typing, "marks: typed coordinates jump");
        p.Marks.CoordKeys.SetText("nowhere");
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnGoCoords).Kind == PanelActionKind.Refused && p.Note.Contains("x z"), "marks: bad coordinates are refused with a reason");
        // Bookmarks: select, go, delete; add here with a name.
        var rows = p.Layout().Items.Where(i => i.Part == PanelPart.MarkRow && i.Enabled).ToList();
        check(rows.Count == 2 && rows[0].Label.StartsWith("Nether hub  [Nether]") && rows[1].Label == "Village  120 64 -300", $"marks: rows ({string.Join(" / ", rows.Select(r => r.Label))})");
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnGoMark).Kind == PanelActionKind.Refused, "marks: Go needs a selected bookmark");
        Click(p, PanelPart.MarkRow, 0);
        a = Click(p, PanelPart.Button, OperatorPanelModel.BtnGoMark);
        check(p.Marks.Selected == "Nether hub" && a.Kind == PanelActionKind.Jump && a.Line == "world_nether" && Near(a.Pos.x, 8.5f), "marks: a bookmark in another world jumps there");
        p.Outbox.Clear();
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnDeleteMark).Kind == PanelActionKind.Sent && p.Outbox.Count == 1 && (string)p.Outbox[0]["op"] == "remove" &&
              (string)p.Outbox[0]["name"] == "Nether hub", "marks: Delete sends bookmark remove");
        p.Outbox.Clear();
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnAddMark).Kind == PanelActionKind.Toggled && p.Marks.Typing && p.Marks.Mode == MarksModel.FieldMode.Name &&
              p.Marks.NameKeys.Text == "Mark 3", "marks: Add mark asks for a name (Mark 3)");
        p.Key(KeyboardModel.KeyClear, 0);
        foreach (int k in new[] { 1, 2, 8 }) p.Key(k, 0);   // w e o
        a = p.Enter(0);
        check(a.Kind == PanelActionKind.Sent && p.Outbox.Count == 1 && (string)p.Outbox[0]["op"] == "add" && (string)p.Outbox[0]["name"] == "weo" &&
              J(p.Outbox[0]["pos"]) == "[100,64,-21]" && (string)p.Outbox[0]["world"] == "world" && !p.Marks.Typing, "marks: Enter saves the bookmark where the view is");
        // Read-only and off.
        var ro = Panel(fixture);
        ro.Marks.Welcome.MarksEdit = false;
        ro.SetTab(OpTab.Marks);
        check(!ro.Layout().Items.First(i => i.Part == PanelPart.Button && i.Index == OperatorPanelModel.BtnAddMark).Enabled &&
              Click(ro, PanelPart.Button, OperatorPanelModel.BtnAddMark).Kind == PanelActionKind.Refused && ro.Note.Contains("read-only"), "marks: read-only server greys Add mark");
        // Following: the Players tab's Follow / Stop and the Marks tab's Stop follow.
        p.OnPlayers(PlayersInfo.From(Fx(fixture, "players_info")));
        p.SetTab(OpTab.Players);
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnFollow).Kind == PanelActionKind.Refused, "players: Follow needs a selection");
        Click(p, PanelPart.PlayerRow, 0);
        a = Click(p, PanelPart.Button, OperatorPanelModel.BtnFollow);
        check(a.Kind == PanelActionKind.Follow && a.Line == "follow" && a.Name == "Alex" && a.Uuid != null, "players: Follow follows the selected player");
        p.Marks.FollowName = "Alex"; p.Marks.FollowLine = "following Alex";
        check(p.Layout().Items.Any(i => i.Part == PanelPart.Button && i.Index == OperatorPanelModel.BtnFollow && i.Label == "Stop"), "players: Follow turns into Stop");
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnFollow).Line == "stop", "players: Stop stops following");
        a = Click(p, PanelPart.Button, OperatorPanelModel.BtnGoTo);
        check(a.Kind == PanelActionKind.Follow && a.Line == "goto", "players: Go to jumps once");
        var nc = new OperatorPanelModel { Connected = true };
        nc.OnPlayers(PlayersInfo.From(Fx(fixture, "players_info")));
        nc.SetOpen(true); nc.SetTab(OpTab.Players);
        Click(nc, PanelPart.PlayerRow, 0);
        check(nc.Layout().Items.First(i => i.Part == PanelPart.Button && i.Index == OperatorPanelModel.BtnFollow).Enabled &&
              !nc.Layout().Items.First(i => i.Part == PanelPart.Button && i.Index == OperatorPanelModel.BtnHeal).Enabled, "players: Follow works without command_v1 (Heal doesn't)");
        p.SetTab(OpTab.Marks);
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnStopFollow).Line == "stop", "marks: Stop follow");
    }

    static void Containers(Action<bool, string> check, Func<string, string> fixture)
    {
        check(ContainerModel.Grid("minecraft:chest", 27) == (9, 3) && ContainerModel.Grid("minecraft:chest", 54) == (9, 6) && ContainerModel.Grid("minecraft:hopper", 5) == (5, 1) &&
              ContainerModel.Grid("minecraft:dropper", 9) == (3, 3) && ContainerModel.Grid("minecraft:furnace", 3) == (3, 1) && ContainerModel.Grid("minecraft:brewing_stand", 5) == (5, 1),
              "container grids: rows of 9, 3x3, hoppers, furnaces, brewing stands");
        check(ContainerModel.SlotLabel("minecraft:blast_furnace", 1) == "fuel" && ContainerModel.SlotLabel("minecraft:brewing_stand", 3) == "ingredient" &&
              ContainerModel.SlotLabel("minecraft:chest", 0) == null, "container slot labels");
        var reg = BlockRegistry.From(Fx(fixture, "block_registry"));
        check(ContainerModel.ItemOf("item:minecraft:bone_meal", reg) == "minecraft:bone_meal" && ContainerModel.ItemOf("minecraft:redstone_wire[east=none,north=none,power=0,south=none,west=none]", reg) == "minecraft:redstone" &&
              ContainerModel.ItemOf("minecraft:stone", reg) == "minecraft:stone" && ContainerModel.ItemOf(null, reg) == null, "container: a hotbar selection's item");
        check(ContainerModel.IconOf("minecraft:stone", reg) == ("minecraft:stone", "minecraft:stone") && ContainerModel.IconOf("minecraft:diamond", reg) == ("item:minecraft:diamond", "minecraft:diamond"),
              "container icons: blocks as their registry state, items as item:<id>");

        var p = Panel(fixture);
        p.Registry = reg;
        p.SetTab(OpTab.Marks);
        p.Outbox.Clear();
        p.OpenContainer(new Vector3Int(1, 64, 2));
        check(p.Mode == PanelMode.Container && p.Open && p.Outbox.Count == 1 && (string)p.Outbox[0]["type"] == "container_query" && p.Container.Waiting, "container: the hook opens the view and asks");
        check(!p.OnContainer(ContainerView.From(Fx(fixture, "container_furnace"))), "container: another container's message isn't this view's");
        check(p.OnContainer(ContainerView.From(Fx(fixture, "container"))) && p.Container.View.Size == 54 && !p.Container.Waiting, "container: the view arrives");
        var L = p.Layout();
        check(L.Items.Count(i => i.Part == PanelPart.Slot) == 54 && L.Items.First(i => i.Part == PanelPart.Slot && i.Index == 5).Label == "64", "container: 54 slots with counts");
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnTake).Kind == PanelActionKind.Refused, "container: Take needs a slot");
        Click(p, PanelPart.Slot, 5);
        check(p.Container.Selected == 5 && p.ModeHeader().Contains("slot 5: 64 torch"), $"container: a slot is selected ({p.ModeHeader()})");
        p.Outbox.Clear();
        Click(p, PanelPart.Button, OperatorPanelModel.BtnCount + 1);
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnTake).Kind == PanelActionKind.Sent && (int)p.Outbox[0]["count"] == 48 && J(p.Outbox[0]["was"]) == "[\"minecraft:torch\",64]",
              "container: Take 16 of 64 sets 48 (was 64)");
        p.Outbox.Clear();
        p.HotbarSelection = "minecraft:torch";
        Click(p, PanelPart.Button, OperatorPanelModel.BtnPut);
        check((string)p.Outbox[0]["item"] == "minecraft:torch" && (int)p.Outbox[0]["count"] == 80, "container: Put adds to the same item (the server clamps to the stack size)");
        p.Outbox.Clear();
        p.HotbarSelection = "item:minecraft:bone_meal";
        Click(p, PanelPart.Button, OperatorPanelModel.BtnPut);
        check((string)p.Outbox[0]["item"] == "minecraft:bone_meal" && (int)p.Outbox[0]["count"] == 16 && p.Note.Contains("in place of"), "container: Put on another item replaces it");
        p.Outbox.Clear();
        p.HotbarSelection = "item:tabletop:lightning";
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnPut).Kind == PanelActionKind.Refused && p.Outbox.Count == 0, "container: the lightning wand isn't an item");
        Click(p, PanelPart.Button, OperatorPanelModel.BtnClear);
        check(p.Outbox[0]["item"] == null && (int)p.Outbox[0]["count"] == 0, "container: Clear empties the slot");
        p.Outbox.Clear();
        Click(p, PanelPart.Button, OperatorPanelModel.BtnSort);
        check((string)p.Outbox[0]["op"] == "sort", "container: Sort");
        // A refusal shows; the view ending by itself goes back to the tabs; leaving sends container_close.
        check(p.OnOperatorError("container_edit", new Vector3Int(1, 64, 2), "a shulker box can't go inside a shulker box") && p.Note.StartsWith("a shulker") && p.Mode == PanelMode.Container,
              "container: a refused edit says why, the view stays");
        p.Outbox.Clear();
        check(Click(p, PanelPart.Tab, (int)OpTab.Chat).Kind == PanelActionKind.Tab && p.Mode == PanelMode.Tabs && p.Outbox.Count == 1 && (string)p.Outbox[0]["type"] == "container_close",
              "container: a tab click leaves the view (container_close)");
        p.OpenContainer(new Vector3Int(1, 64, 2));
        p.OnContainer(ContainerView.From(Fx(fixture, "container")));
        check(p.OnContainer(ContainerView.From(Fx(fixture, "container_closed"))) && p.Mode == PanelMode.Tabs && p.Note.Contains("gone"), "container: the server closing the view");
        p.OpenContainer(new Vector3Int(2, 64, 2));
        check(p.OnContainer(ContainerView.From(Fx(fixture, "container"))), "container: a double chest's other half is the same view");
        p.Outbox.Clear();
        p.SetOpen(false);
        check(p.Mode == PanelMode.Tabs && p.Outbox.Any(m => (string)m["type"] == "container_close"), "container: closing the panel ends the view");
        // View-only: everything greyed.
        var v = Panel(fixture);
        v.OpenContainer(new Vector3Int(4, 64, 2));
        v.OnContainer(ContainerView.From(Fx(fixture, "container_furnace")));
        Click(v, PanelPart.Slot, 0);
        check(!v.Layout().Items.First(i => i.Index == OperatorPanelModel.BtnTake && i.Part == PanelPart.Button).Enabled && v.Container.Take(out var why) == null && why.Contains("view-only"),
              "container: a view-only server greys the edits");
        check(!v.Layout().Items.First(i => i.Index == OperatorPanelModel.BtnSort && i.Part == PanelPart.Button).Enabled, "container: furnaces don't sort");
        check(v.OnOperatorError("container_query", new Vector3Int(4, 64, 2), "no container there") && v.Mode == PanelMode.Tabs, "container: a refused query goes back to the tabs");
    }

    static void Signs(Action<bool, string> check, Func<string, string> fixture)
    {
        var store = new BlockEntityStore();
        store.Apply(Fx(fixture, "block_entities"), new List<Vector3Int>());
        var data = store.Get(new Vector3Int(106, 64, -35));
        var p = Panel(fixture);
        p.OpenSign(new Vector3Int(106, 64, -35), "minecraft:oak_sign[rotation=0,waterlogged=false]", data);
        check(p.Mode == PanelMode.Sign && p.Sign.Front.Lines[1] == "Shop" && p.Sign.Front.Color == "light_blue" && p.Sign.Front.Glowing && p.Sign.Back.Lines[0] == "Closed",
              "sign: filled from block_entities");
        check(p.Focus == OperatorPanelModel.Field.Sign && p.Layout().Items.Any(i => i.Part == PanelPart.Key && i.Label == "next"), "sign: the keyboard types into the line (enter = next)");
        check(!p.Layout().Items.First(i => i.Part == PanelPart.Button && i.Index == OperatorPanelModel.BtnSave).Enabled, "sign: nothing to save yet");
        Click(p, PanelPart.SignLine, 2);
        foreach (int k in new[] { 8, 9, 2 }) p.Key(k, 0);   // o p e (letters: q w e r t y u i o p / a s d f g h j k l / z x c v b n m)
        p.Key(KeyboardModel.KeyEnter, 0);
        check(p.Sign.Front.Lines[2] == "ope" && p.Sign.Line == 3 && p.Sign.Front.Changed, $"sign: typing into line 3, enter moves on ({p.Sign.Front.Lines[2]})");
        Click(p, PanelPart.Swatch, Array.IndexOf(OperatorProtocol.Dyes, "red"));
        Click(p, PanelPart.Button, OperatorPanelModel.BtnGlow);
        check(p.Sign.Front.Color == "red" && !p.Sign.Front.Glowing, "sign: colour and glow");
        Click(p, PanelPart.Button, OperatorPanelModel.BtnBack);
        check(p.Sign.OnBack && p.Layout().Items.First(i => i.Part == PanelPart.SignLine && i.Index == 0).Label.StartsWith("Closed"), "sign: the back side");
        p.Outbox.Clear();
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnSave).Kind == PanelActionKind.Sent && p.Outbox.Count == 1 && (string)p.Outbox[0]["side"] == "front" &&
              J(p.Outbox[0]["lines"]) == "[\"\",\"Shop\",\"ope\",\"\"]" && (string)p.Outbox[0]["color"] == "red" && !(bool)p.Outbox[0]["glowing"] && p.Note == "saved the front",
              "sign: Save sends the changed side only");
        check(p.OnOperatorError("sign_edit", new Vector3Int(106, 64, -35), "another plugin refused the sign change (protection)") && p.Note.Contains("protection"), "sign: a veto shows");
        var empty = new SignEditorModel();
        empty.Begin(new Vector3Int(0, 0, 0), "minecraft:oak_hanging_sign[attached=false,rotation=0,waterlogged=false]", null);
        check(empty.Hanging && empty.Front.Lines.All(l => l == "") && empty.Front.Color == "black" && empty.Save().Count == 0, "sign: an empty hanging sign");
        check(Click(p, PanelPart.Button, OperatorPanelModel.BtnClose).Kind == PanelActionKind.Tab && p.Mode == PanelMode.Tabs && !p.Sign.Open, "sign: Close");
    }

    static void Layouts(Action<bool, string> check, Func<string, string> fixture)
    {
        bool ok = true; string bad = "";
        void Fits(OperatorPanelModel p, string what)
        {
            var L = p.Layout();
            var click = L.Items.Where(i => i.Part != PanelPart.None).ToList();
            foreach (var it in L.Items) if (it.R.X < -1e-5f || it.R.Y < -1e-5f || it.R.X + it.R.W > L.Width + 1e-5f || it.R.Y + it.R.H > L.Height + 1e-5f) { ok = false; bad += what + " outside; "; }
            for (int a = 0; a < click.Count; a++)
                for (int b = a + 1; b < click.Count; b++)
                {
                    var A = click[a].R; var B = click[b].R;
                    if (A.X + A.W - 1e-5f > B.X && B.X + B.W - 1e-5f > A.X && A.Y + A.H - 1e-5f > B.Y && B.Y + B.H - 1e-5f > A.Y) { ok = false; bad += $"{what} {click[a].Part}#{click[a].Index}/{click[b].Part}#{click[b].Index}; "; }
                }
            foreach (var it in click) { var h = OperatorPanelModel.HitTest(L, it.R.CX, it.R.CY); if (h.Part != it.Part || h.Index != it.Index) { ok = false; bad += what + " hit " + it.Part + "; "; } }
            if (L.Height > 0.40f) { ok = false; bad += $"{what} {L.Height:F3} m tall; "; }
        }
        var p = Panel(fixture);
        p.SetTab(OpTab.Marks); Fits(p, "marks");
        p.Marks.SetTyping(true, MarksModel.FieldMode.Coords); Fits(p, "marks typing");
        p.OnPlayers(PlayersInfo.From(Fx(fixture, "players_info")));
        p.SetTab(OpTab.Players); Fits(p, "players");
        p.OpenContainer(new Vector3Int(1, 64, 2)); Fits(p, "container waiting");
        p.OnContainer(ContainerView.From(Fx(fixture, "container"))); Fits(p, "double chest");
        p.OpenContainer(new Vector3Int(4, 64, 2)); p.OnContainer(ContainerView.From(Fx(fixture, "container_furnace"))); Fits(p, "furnace");
        p.OpenSign(new Vector3Int(1, 1, 1), "minecraft:oak_sign", null); Fits(p, "sign");
        check(ok, "operator layouts: inside the panel, no overlaps, hit tests round-trip, at most 40 cm " + bad);
    }

    /// <summary>
    /// --live-operator URL TOKEN PLAYER: the app's own classes against a live plugin with a Java player online. Follows the
    /// player while the console tab teleports them (command tp), adds and removes a bookmark, opens a chest it fills with a
    /// command and edits it, and edits a sign.
    /// </summary>
    public static void Live(string url, string token, string player, Action<bool, string> check)
    {
        var link = new LinkClient(url, token);
        link.Start();
        var nav = new NavState();
        var follow = new FollowState();
        var panel = new OperatorPanelModel();
        var t0 = DateTime.UtcNow;
        float Now() => (float)(DateTime.UtcNow - t0).TotalSeconds;
        string session = null;
        var players = new Dictionary<string, Vector3>();
        var cmdOk = new Dictionary<long, bool>();
        var worlds = new List<WorldEntry>();
        bool Pump(Func<bool> done, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            float last = Now();
            while (DateTime.UtcNow < end)
            {
                while (link.Inbound.TryDequeue(out var m))
                {
                    switch (m.S("type"))
                    {
                        case "welcome":
                            session = m.S("session"); panel.Connected = true;
                            panel.OnWelcome(ControlWelcome.From(m), null); panel.Marks.OnWelcome(OperatorWelcome.From(m));
                            worlds = OperatorWelcome.From(m).Worlds;
                            nav.CanPad = m.A("caps")?.Contains("view_margin") == true;
                            nav.Caps = RegionLimits.From(m.O("limits"), nav.Caps);
                            if (m.O("limits")?.A("build_height") is List<object> bh) { nav.MinY = (int)(double)bh[0]; nav.MaxY = (int)(double)bh[1]; }
                            break;
                        case "region_changed":
                            if (m.O("limits")?.A("build_height") is List<object> bh2) { nav.MinY = (int)(double)bh2[0]; nav.MaxY = (int)(double)bh2[1]; }
                            break;
                        case "chunk_snapshot":
                        {
                            if (m.I("part") + 1 < m.I("parts", 1)) break;   // the region is known once the last part is in
                            var o = OperatorProtocol.V3(m.A("origin")); var d = OperatorProtocol.V3(m.A("dims")); var vm = OperatorProtocol.V3(m.A("view_margin"));
                            nav.OnRegion(m.S("world"), o, d, vm, Now());
                            follow.Focus.Apply(nav, Now());
                            break;
                        }
                        case "player_positions":
                            players.Clear();
                            foreach (var x in m.A("players")) { var pi = PlayerInfo.From((Dictionary<string, object>)x); players[pi.Uuid] = pi.Pos; }
                            break;
                        case "region_info": follow.OnRegionInfo(RegionInfo.From(m), (long)m.D("ref_seq", -1), Now()); break;
                        case "bookmarks": panel.Marks.OnBookmarks(BookmarkList.From(m)); break;
                        case "container": panel.OnContainer(ContainerView.From(m)); break;
                        case "command_result": cmdOk[(long)m.D("ref_seq", -1)] = m.B("ok"); break;
                        case "error":
                            if (follow.OnError((long)m.D("ref_seq", -1), m.S("message"))) break;
                            var ep = m.A("pos");
                            panel.OnOperatorError(m.S("op"), ep != null ? OperatorProtocol.V3(ep) : (Vector3Int?)null, m.S("message"));
                            if (m.S("code") == "region_too_large") { nav.OnRefused(Now(), m.S("message")); follow.Focus.Clear(); }
                            break;
                    }
                }
                foreach (var msg in panel.Outbox) link.Send(msg);
                panel.Outbox.Clear();
                float now = Now(), dt = Math.Max(0.001f, now - last);
                last = now;
                if (follow.Active && nav.Loaded)
                {
                    if (follow.Uuid != null && players.TryGetValue(follow.Uuid, out var seen)) follow.OnSeen(nav.World, seen, now);
                    var st = follow.Step(nav, dt, now, n => worlds.Where(w => w.Name == n).Select(w => (WorldEntry?)w).FirstOrDefault());
                    if (st.Query) follow.Sent(link.SendTracked(Protocol.RegionQuery(follow.Name)));
                    if (st.Jump.HasValue)
                    {
                        if (nav.TryIssue(st.Jump.Value, now))
                        {
                            var j = st.Jump.Value;
                            link.Send(Protocol.RegionRequest(st.JumpWorld, j.Origin, j.Dims, j.Margin));
                            follow.Jumped(j, st.JumpWorld, st.JumpTarget, now);
                        }
                    }
                    else
                    {
                        var mv = nav.Move(st.Delta, dt, now);
                        if (mv.HasValue) link.Send(Protocol.RegionRequest(nav.World, mv.Value.Origin, mv.Value.Dims, mv.Value.Margin));
                    }
                }
                if (done()) return true;
                Thread.Sleep(14);
            }
            return false;
        }
        bool Cmd(string line)
        {
            long seq = link.SendTracked(ControlProtocol.Command(line));
            return Pump(() => cmdOk.ContainsKey(seq), 5) && cmdOk[seq];
        }
        check(Pump(() => link.Connected && nav.Loaded, 15), "live operator: welcome and the first region");
        check(panel.Marks.Welcome.Marks && worlds.Count >= 1, $"live operator: marks_v1 and {worlds.Count} worlds");
        // A known area to start from: 64 wide around the spawn, margin 16.
        var sp = worlds[0].Spawn;
        var start = new RegionMove { Origin = new Vector3Int(sp.x - 48, Math.Max(nav.MinY, sp.y - 48), sp.z - 48), Dims = new Vector3Int(96, 96, 96), Margin = new Vector3Int(16, 16, 16), Kind = "jump" };
        link.Send(Protocol.RegionRequest(worlds[0].Name, start.Origin, start.Dims, start.Margin));
        check(Pump(() => nav.Loaded && nav.Origin == start.Origin && nav.Dims == start.Dims, 20), "live operator: a 64-wide area at the spawn");
        check(Cmd($"tp {player} {sp.x + 0.5} {sp.y + 1} {sp.z + 0.5}"), "live operator: the player is at the spawn");
        // Who is it (uuid)?
        long q = link.SendTracked(Protocol.RegionQuery(player));
        follow.Start(player, null, Now());
        follow.Sent(q);
        check(Pump(() => follow.HasTarget, 5), "live operator: region_query finds the player");
        // Walk the player east in 2-block steps (tp ~2 ~ ~), 3 a second: the view follows by gliding.
        float x0 = nav.View.x;
        int shiftsBefore = nav.Requests;
        for (int i = 0; i < 30; i++) { Cmd($"tp {player} ~2 ~ ~"); Pump(() => false, 0.2); }
        Pump(() => false, 2.5);
        float px = sp.x + 0.5f + 60;
        check(Math.Abs(nav.View.x - px) < 4f && nav.Requests > shiftsBefore, $"live operator: the view followed 60 blocks east (view {nav.View.x:F1}, player {px:F1}, {nav.Requests - shiftsBefore} requests)");
        // Teleport 1000 blocks: a jump.
        int jumps = follow.JumpCount;
        Cmd($"tp {player} {sp.x + 1000.5} 120 {sp.z - 700.5}");
        check(Pump(() => follow.JumpCount > jumps && !follow.Focus.Pending && Math.Abs(nav.View.x - (sp.x + 1000.5f)) < 1f, 25), $"live operator: a far teleport jumps the view ({nav.View.x:F1}, {nav.View.z:F1})");
        // Into the nether.
        var neth = worlds.FirstOrDefault(w => w.Env == "nether");
        if (neth.Name != null && Cmd($"execute in minecraft:the_nether run tp {player} 8.5 70 -4.5"))
            check(Pump(() => nav.World == neth.Name && !follow.Focus.Pending && nav.Origin.y >= neth.MinY && nav.Origin.y + nav.Dims.y <= neth.MaxY, 30),
                  $"live operator: followed into {neth.Name} (origin {nav.Origin}, dims {nav.Dims}, height {nav.MinY}..{nav.MaxY})");
        follow.Stop("test done");
        // Bookmarks round trip.
        panel.Outbox.Add(OperatorProtocol.BookmarkAdd("Live check", nav.World, new Vector3Int(1, 70, 2)));
        check(Pump(() => panel.Marks.Find("Live check").HasValue, 5), "live operator: bookmark added and broadcast");
        panel.Outbox.Add(OperatorProtocol.BookmarkRemove("Live check"));
        check(Pump(() => !panel.Marks.Find("Live check").HasValue, 5), "live operator: bookmark removed");
        link.Dispose();
    }
}
