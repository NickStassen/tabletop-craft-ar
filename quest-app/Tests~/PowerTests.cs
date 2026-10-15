// [power] Engine-free tests for undo/redo, box edits and brushes (PROTOCOL.md "Undo and redo", "Box edits", "Brushes"):
// the fixtures through Net/PowerProtocol.cs, B's tap and hold (TapHoldButton), edit groups on block_edit and interact
// (BlockEditor), the wand's corners, boxes, clone/paste and brushes (World/PowerTools.cs), the Tools tab
// (World/BlockMenuModel.cs), and with --live-power URL the app's own classes against a server.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class PowerTests
{
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;
    static Dictionary<string, object> Fx(Func<string, string> fixture, string n) => Parse(File.ReadAllText(fixture(n + ".json")));
    /// <summary>A number from a built message (ints there, doubles in parsed JSON), -1 when absent.</summary>
    static int N(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : -1;

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Fixtures(check, fixture);
        Buttons(check);
        Groups(check);
        Wand(check, fixture);
        Brushes(check, fixture);
        History(check, fixture);
        Menu(check, fixture);
    }

    static void Fixtures(Action<bool, string> check, Func<string, string> fixture)
    {
        var lim = PowerLimits.From(Fx(fixture, "welcome_power"));
        check(lim.Allowed && lim.UndoDepth == 10000 && lim.BoxMaxBlocks == 32768 && lim.BrushMaxRadius == 8 && lim.BrushBlocksPerSecond == 20000
              && lim.Undo && lim.Box && lim.Brush, "welcome_power: power_v1 and its limits");
        check(!PowerLimits.From(Fx(fixture, "welcome")).Allowed && !PowerLimits.From(Fx(fixture, "welcome_interact")).Undo, "a welcome without power_v1: no tools");
        check(Fx(fixture, "hello_power").A("caps").Contains(PowerProtocol.Cap) && Protocol.Hello("").A("caps").Contains(PowerProtocol.Cap), "the app's hello asks for power_v1");
        var h = PowerHistory.From(Fx(fixture, "history"));
        check(h.Undo == 3 && h.Redo == 1 && h.UndoBlocks == 45 && h.RedoBlocks == 2 && h.Depth == 10000, "history parses");
        var run = BoxProgress.From(Fx(fixture, "box_progress"));
        check(!run.Finished && run.Op == "fill" && run.Group == 9 && run.RefSeq == 130 && run.Percent == 36 && run.Text() == "Filling 12000 / 32768 (36 %)",
              $"box_progress parses while running ({run.Text()})");
        var done = BoxProgress.From(Fx(fixture, "box_progress_done"));
        check(done.Finished && done.Op == "undo" && done.SkippedPos.Count == 2 && done.SkippedPos[0] == new Vector3Int(106, 64, -35)
              && done.Text() == "Undone 12 blocks; 2 changed since, left as they are", $"box_progress done: skipped cells and the toast ({done.Text()})");

        string W(object o) => Json.Write(o);
        var fill = Fx(fixture, "box_edit_fill");
        var b = PowerProtocol.BoxEdit("fill", new Vector3Int(110, 64, -30), new Vector3Int(101, 60, -21), "minecraft:stone_bricks", null, null, 0, 9);
        check(W(b["from"]) == W(fill["from"]) && W(b["to"]) == W(fill["to"]) && (string)b["state"] == (string)fill["state"] && (int)b["group"] == 9
              && !b.ContainsKey("dest") && !b.ContainsKey("filter"), "box_edit builder matches the fill fixture");
        var clone = Fx(fixture, "box_edit_clone");
        b = PowerProtocol.BoxEdit("clone", new Vector3Int(101, 60, -30), new Vector3Int(104, 62, -29), null, null, new Vector3Int(112, 60, -30), 1, 11);
        check(W(b["dest"]) == W(clone["dest"]) && (int)b["rotate"] == 1 && !b.ContainsKey("state"), "box_edit builder matches the clone fixture");
        var br = Fx(fixture, "brush_replace");
        b = PowerProtocol.Brush("replace", "cylinder", new Vector3Int(105, 63, -35), 4, 1, "minecraft:dirt_path", "minecraft:grass_block", 13);
        check(W(b["center"]) == W(br["center"]) && (int)b["height"] == 1 && (string)b["filter"] == (string)br["filter"] && (int)b["radius"] == 4,
              "brush builder matches the fixture (a disc: cylinder of height 1)");
        b = PowerProtocol.Brush("paint", "sphere", new Vector3Int(1, 2, 3), 3, 7, "minecraft:moss_block", null, 12);
        check(!b.ContainsKey("height") && !b.ContainsKey("filter"), "a sphere sends no height");
        check(PowerProtocol.Undo().S("type") == "undo" && PowerProtocol.Redo().S("type") == "redo" && Fx(fixture, "undo").S("type") == "undo", "undo and redo");
        check(Fx(fixture, "block_edit_group").I("group") == 7 && Fx(fixture, "interact_group").I("group") == 8, "group fixtures");
    }

    static void Buttons(Action<bool, string> check)
    {
        var b = new TapHoldButton();
        check(b.Update(true, 0) == ButtonEvent.None && b.Update(true, 0.3) == ButtonEvent.None && b.Update(false, 0.35) == ButtonEvent.Tap, "B: a short press is a tap (on release)");
        var events = new List<ButtonEvent>();
        for (double t = 1; t <= 2.5; t += 0.05) events.Add(b.Update(true, t));
        events.Add(b.Update(false, 2.55));
        int holds = events.Count(e => e == ButtonEvent.Hold);
        check(holds == 3 && !events.Contains(ButtonEvent.Tap), $"B: held 1.5 s = undo at 0.6 s, 1.0 s and 1.4 s, no tap after ({holds} holds)");
        check(Math.Abs(b.Progress(0) - 0) < 1e-6, "B: no progress while up");
        b.Update(true, 3);
        check(Math.Abs(b.Progress(3.3) - 0.5f) < 1e-3, "B: halfway to a hold at 0.3 s");
        b.Update(false, 3.31);
        // a press that began while disabled (area setup had B) is ignored until released
        check(b.Update(true, 4, false) == ButtonEvent.None && b.Update(true, 4.1) == ButtonEvent.None && b.Update(true, 5) == ButtonEvent.None
              && b.Update(false, 5.1) == ButtonEvent.None && b.Update(true, 6) == ButtonEvent.None && b.Update(false, 6.1) == ButtonEvent.Tap,
              "B: a press from while it was disabled does nothing; the next one taps");
    }

    static WorldModel Flat()
    {
        var w = new WorldModel { SnapshotId = 1, World = "world", Origin = new Vector3Int(100, -64, 200), Dims = new Vector3Int(16, 16, 16) };
        w.Palette.Add("minecraft:air"); w.Palette.Add("minecraft:stone");
        w.Grid = new ushort[16 * 16 * 16];
        for (int y = 0; y < 4; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) w.Grid[w.Index(x, y, z)] = 1;
        return w;
    }

    static void Groups(Action<bool, string> check)
    {
        var g = new EditGroups();
        int a = g.Begin(), b = g.Begin();
        check(a == 1 && b == 2 && g.Last == 2, "edit groups count up from 1");
        var w = Flat();
        var ed = new BlockEditor { ServerAllows = true, Selected = "minecraft:stone" };
        var hit = BlockRaycast.Cast(w, new Vector3(5.5f, 12, 5.5f), new Vector3(0, -1, 0), 32, new Vector3(0, 0, 0), new Vector3(16, 16, 16));
        var none = ed.Place(w, hit, new Vector3(0, -1, 0), 0);
        check(none != null && !none.ContainsKey("group"), "no group before the first press");
        ed.Group = 5;
        var place = ed.Place(w, BlockRaycast.Cast(w, new Vector3(7.5f, 12, 7.5f), new Vector3(0, -1, 0), 32, new Vector3(0, 0, 0), new Vector3(16, 16, 16)), new Vector3(0, -1, 0), 0);
        var brk = ed.Break(w, BlockRaycast.Cast(w, new Vector3(9.5f, 12, 9.5f), new Vector3(0, -1, 0), 32, new Vector3(0, 0, 0), new Vector3(16, 16, 16)), 0);
        var use = ed.Interact(w, hit, "minecraft:bone_meal", false, new Vector3(0, -1, 0));
        check(place.N("group") == 5 && brk.N("group") == 5 && use.N("group") == 5, "block_edit and interact carry the trigger's group");
    }

    static PowerTools Granted(Func<string, string> fixture)
    {
        var t = new PowerTools();
        t.OnWelcome(Fx(fixture, "welcome_power"));
        return t;
    }

    static void Wand(Action<bool, string> check, Func<string, string> fixture)
    {
        var t = new PowerTools();
        check(t.Press(ToolButton.Wand, "minecraft:stone", 0) == null && t.Mode == ToolMode.None && t.Result.Contains("off on this server"), "no power_v1: the wand stays off");
        t = Granted(fixture);
        t.Press(ToolButton.Wand, "minecraft:stone", 0);
        check(t.Mode == ToolMode.Wand, "the Wand button turns it on");
        check(t.Box("fill", "minecraft:stone", 0) == null && t.Result.Contains("corner A"), "fill without a corner: refused locally");
        t.SetCorner(true, new Vector3Int(1, 2, 3));
        check(t.A == new Vector3Int(1, 2, 3) && !t.B.HasValue && t.Volume == 1, "grip + trigger before A sets A (a one-block box)");
        t.SetCorner(true, new Vector3Int(10, 6, -2));
        check(t.Min == new Vector3Int(1, 2, -2) && t.Max == new Vector3Int(10, 6, 3) && t.Size == new Vector3Int(10, 5, 6) && t.Volume == 300, "corner B: a 10x5x6 box");
        var fill = t.Box("fill", "minecraft:stone_bricks", 0);
        check(fill != null && fill.S("op") == "fill" && fill.S("state") == "minecraft:stone_bricks" && fill.N("group") > 0 && Json.Write(fill["from"]) == "[1,2,3]"
              && Json.Write(fill["to"]) == "[10,6,-2]", "fill sends the box, the selected block and a new group");
        check(t.Box("fill", "item:minecraft:bone_meal", 0) == null && t.Result.Contains("not an item"), "an item can't fill a box");
        check(t.Box("replace", "minecraft:glass", 0) == null && t.Result.Contains("pick the block to replace"), "replace needs a filter first");
        check(t.PickFilter("minecraft:stone_bricks") && t.Filter == "minecraft:stone_bricks", "B with the wand on picks the filter");
        var rep = t.Box("replace", "minecraft:glass", 0);
        check(rep != null && rep.S("filter") == "minecraft:stone_bricks" && rep.N("group") > fill.N("group"), "replace sends the filter, each box its own group");
        var clear = t.Box("clear", null, 0);
        check(clear != null && !clear.ContainsKey("state"), "clear needs no block");
        t.SetCorner(false, new Vector3Int(0, 0, 0)); t.SetCorner(true, new Vector3Int(39, 20, 39));
        check(t.Volume == 40 * 21 * 40 && t.Box("fill", "minecraft:stone", 0) == null && t.Result.Contains("more than 32768"), "a box over box_max_blocks is refused locally");
        t.SetCorner(false, new Vector3Int(0, 0, 0)); t.SetCorner(true, new Vector3Int(99, 1, 99));
        t.Box("fill", "minecraft:stone", 0);
        check(t.TakeToasts().Any(x => x.Text.Contains("until your next edit")), "a box bigger than undo_depth warns that undo lasts until the next edit");

        // clone: a 3 (x) by 1 by 2 (z) box, turned a quarter = 2 by 3, standing on the aimed cell
        t.SetCorner(false, new Vector3Int(0, 0, 0)); t.SetCorner(true, new Vector3Int(2, 0, 1));
        t.Press(ToolButton.Clone, null, 0);
        check(t.Pasting && t.PasteSize == new Vector3Int(3, 1, 2), "Clone starts paste mode");
        t.Press(ToolButton.Rotate, null, 0);
        check(t.Rotate == 1 && t.PasteSize == new Vector3Int(2, 1, 3) && t.Label(ToolButton.Rotate) == "Rotate (90°)", "Rotate turns the copy a quarter");
        check(t.PasteOrigin(new Vector3Int(10, 5, 10)) == new Vector3Int(9, 5, 9), "the copy stands on the aimed cell, centred");
        var paste = t.Paste(new Vector3Int(10, 5, 10), 0);
        check(paste != null && paste.S("op") == "clone" && Json.Write(paste["dest"]) == "[9,5,9]" && paste.N("rotate") == 1 && t.Pasting, "the left trigger pastes (and stays in paste mode)");
        check(t.PickFilter("minecraft:dirt") && !t.Pasting && t.Filter == "minecraft:stone_bricks", "B ends paste mode (the filter stays)");
        t.ClearCorners();
        check(!t.HasBox && t.Press(ToolButton.Clone, null, 0) == null && !t.Pasting, "Clone needs a box");
        t.Press(ToolButton.Wand, null, 0);
        check(t.Mode == ToolMode.None && !t.PickFilter("minecraft:dirt"), "wand off: B picks blocks again");
    }

    static void Brushes(Action<bool, string> check, Func<string, string> fixture)
    {
        check(PowerTools.Cells(BrushForm.Sphere, 2) == 81 && PowerTools.Cells(BrushForm.Sphere, 8) == 2553 && PowerTools.Cells(BrushForm.Sphere, 1) == 19,
              "brush cells match the server's count (81 for radius 2, 2553 for 8)");
        check(PowerTools.Cells(BrushForm.Cylinder, 3) == PowerTools.Cells(BrushForm.Disc, 3) * 7, "a cylinder is its disc times 2r+1");
        var t = Granted(fixture);
        t.Press(ToolButton.Brush, null, 0);
        check(t.Mode == ToolMode.Brush && t.Form == BrushForm.Sphere && t.Shape == "sphere", "Brush: sphere first");
        t.Press(ToolButton.Brush, null, 0); var cyl = t.Form;
        t.Press(ToolButton.Brush, null, 0); var disc = t.Form;
        t.Press(ToolButton.Brush, null, 0);
        check(cyl == BrushForm.Cylinder && disc == BrushForm.Disc && t.Mode == ToolMode.None, "Brush cycles sphere, cylinder, disc, off");
        t.Press(ToolButton.Erase, null, 0);
        check(t.Mode == ToolMode.Brush && t.BrushOp == "erase" && t.Active(ToolButton.Erase) && !t.Active(ToolButton.Paint), "an op button turns the brush on");
        for (int i = 0; i < 20; i++) t.Press(ToolButton.RadiusUp, null, 0);
        check(t.Radius == 8, "the radius stops at brush_max_radius");
        t.Press(ToolButton.Paint, null, 0);
        check(t.Form == BrushForm.Disc, "the brush remembers its shape when an op button turns it back on");
        t.Form = BrushForm.Sphere;
        t.BeginStroke();
        int group = t.Groups.Last;
        var s1 = t.Stroke(new Vector3Int(5, 5, 5), "minecraft:moss_block", 10.0);
        check(s1 != null && s1.S("op") == "paint" && s1.N("radius") == 8 && s1.N("group") == group && s1.S("state") == "minecraft:moss_block", "the first stroke goes at once");
        check(t.Stroke(new Vector3Int(6, 5, 5), "minecraft:moss_block", 10.05) == null, "strokes come at most every 0.15 s");
        check(t.Stroke(new Vector3Int(5, 5, 5), "minecraft:moss_block", 10.2) == null, "resting on the same block doesn't repeat at once");
        var s2 = t.Stroke(new Vector3Int(7, 5, 5), "minecraft:moss_block", 10.2);
        check(s2 != null && s2.N("group") == group, "a held stroke keeps its group");
        // 20000 blocks a second refills 3200 per stroke interval: radius 8 (2553) strokes all go
        int sent = 0;
        for (int i = 0; i < 10; i++) if (t.Stroke(new Vector3Int(20 + i, 5, 5), "minecraft:moss_block", 10.4 + i * 0.16) != null) sent++;
        check(sent == 10, $"radius 8 strokes every 0.16 s fit 20000 blocks a second ({sent} of 10)");
        // a server allowing 5000 a second: only about one stroke in three goes
        t.Limits.BrushBlocksPerSecond = 5000;
        sent = 0;
        for (int i = 0; i < 10; i++) if (t.Stroke(new Vector3Int(40 + i, 5, 5), "minecraft:moss_block", 20 + i * 0.16) != null) sent++;
        check(sent >= 2 && sent <= 5, $"strokes stay under a lower brush_blocks_per_second ({sent} of 10 in 1.6 s at 5000/s)");
        t.Limits.BrushBlocksPerSecond = 20000;
        t.Press(ToolButton.BrushReplace, null, 0);
        t.BeginStroke();
        check(t.Stroke(new Vector3Int(50, 5, 5), "minecraft:moss_block", 30) == null && t.Result.Contains("pick the block"), "brush replace needs the filter");
        t.PickFilter("minecraft:grass_block[snowy=false]");
        var rs = t.Stroke(new Vector3Int(50, 5, 5), "minecraft:moss_block", 31);
        check(rs != null && rs.S("filter") == "minecraft:grass_block", "the filter is the block's id (any state)");
    }

    static void History(Action<bool, string> check, Func<string, string> fixture)
    {
        var t = Granted(fixture);
        check(t.Press(ToolButton.Undo, null, 0) == null && t.Result == "nothing to undo", "nothing to undo before any edit");
        t.OnHistory(Fx(fixture, "history"));
        check(t.Label(ToolButton.Undo) == "Undo (3)" && t.Label(ToolButton.Redo) == "Redo (1)", "the buttons show the counts");
        var u = t.Press(ToolButton.Undo, null, 1);
        check(u != null && u.S("type") == "undo", "undo goes out");
        t.Sent(120, "undo", 1);
        check(t.Busy(1.5) && t.Press(ToolButton.Redo, null, 1.5) == null && t.Result.StartsWith("wait"), "one job at a time: redo waits for the undo");
        t.OnProgress(Fx(fixture, "box_progress_done"));
        var toasts = t.TakeToasts();
        check(!t.Busy(2) && toasts.Count == 1 && toasts[0].Warn && toasts[0].Text.Contains("2 changed since"), "the finished undo frees the slot and toasts what it skipped");
        t.Sent(130, "fill", 3);
        check(t.OnError(Fx(fixture, "error_box_denied")) && !t.Busy(3) && t.Result.Contains("more than 32768"), "a refused box frees the slot");
        check(!t.OnError(Parse("{\"type\":\"error\",\"code\":\"edit_denied\",\"op\":\"place\",\"message\":\"x\"}")), "other errors aren't the tools'");
        t.Sent(140, "undo", 4);
        check(!t.Busy(4 + PowerTools.JobTimeoutSeconds + 1), "a job never answered stops blocking after a while");
        var none = new PowerTools();
        check(none.Press(ToolButton.Undo, null, 0) == null && none.Result.Contains("power_v1"), "undo on a server without power_v1 says why");
        check(t.Status.StartsWith("undo 3") && none.Status.StartsWith("off"), "the Info tab line");
    }

    static void Menu(Action<bool, string> check, Func<string, string> fixture)
    {
        var m = new BlockMenuModel();
        m.RestoreTab("tools");   // prefs.json, before the server's lists
        m.SetRegistry(BlockRegistry.From(Fx(fixture, "block_registry")));
        var tools = new PowerTools();
        m.SetTools(tools);
        check(m.ToolsTab == -1 && m.SearchTab == 6, "menu: no Tools tab before power_v1");
        m.SetItems(ItemRegistry.From(Fx(fixture, "item_registry")));
        tools.OnWelcome(Fx(fixture, "welcome_power"));
        m.SetTools(tools);
        check(m.ToolsTab == 8 && m.SearchTab == 9 && m.InfoTab == 10 && m.Tab == m.ToolsTab && m.TabId(m.ToolsTab) == "tools" && m.TabCaption(m.ToolsTab) == "Tools",
              "menu: Tools after Use and Eggs; the saved Tools tab is restored");
        var L = m.Layout();
        var tl = L.Items.Where(i => i.Part == MenuPart.Tool).ToList();
        check(tl.Count == PowerTools.Rows.Sum(r => r.Length) && L.ToolsInfo.H > 0 && L.Items.Count(i => i.Part == MenuPart.Tab) == 11 && m.GridRows == 0,
              $"menu: the Tools tab has {tl.Count} buttons and a status area; eleven tabs");
        check(tl.All(i => i.R.X >= 0 && i.R.X + i.R.W <= L.Width + 1e-6), "menu: the buttons fit the panel");
        var undo = tl.First(i => i.Index == (int)ToolButton.Undo);
        m.Point(new MenuHit(MenuPart.Tool, undo.Index), true);
        m.SetOpen(true);
        m.Point(new MenuHit(MenuPart.Tool, undo.Index), true);
        var a = m.Click();
        check(a.Kind == MenuActionKind.Tool && a.Value == (int)ToolButton.Undo && m.NameLine().Contains("Undo the last"), "menu: a Tools button click goes to the app, the name line says what it does");
        check(undo.Label == "Undo (0)", "menu: the Undo button shows the count");
        tools.OnWelcome(Fx(fixture, "welcome_interact"));
        m.SetTools(tools);
        check(m.ToolsTab == -1 && m.SearchTab == 8 && m.Tab <= m.InfoTab, "menu: a server without power_v1 takes the tab away");
    }

    /// <summary>--live-power URL [TOKEN]: the app's own classes against a server with power_v1 (a flat world, like tools/power_check.py).</summary>
    public static void Live(string url, string token, Action<bool, string> check)
    {
        var link = new LinkClient(url, token);
        link.Start();
        var w = new WorldModel();
        var ed = new BlockEditor();
        var asm = new SnapshotAssembler();
        var tools = new PowerTools();
        var errors = new List<string>();
        bool Wait(Func<bool> done, double seconds = 8)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                while (link.Inbound.TryDequeue(out var m))
                    switch (m.S("type"))
                    {
                        case "welcome": ed.ServerAllows = m.A("caps")?.Contains(Protocol.CapEdit) == true; tools.OnWelcome(m); break;
                        case "chunk_snapshot":
                            asm.Offer(m, w, new HashSet<Vector3Int>());
                            if (!asm.Busy) break;
                            var res = asm.Wait();
                            if (res.Error == null) { w.ApplySnapshot(res.Snapshot); ed.OnSnapshot(w, res.Delta ? res.ListedKeys : null); }
                            break;
                        case "block_update": ed.OnBlockUpdate(w, m); w.ApplyBlockUpdate(m); break;
                        case "history": tools.OnHistory(m); break;
                        case "box_progress": tools.OnProgress(m); break;
                        case "error": errors.Add($"{m.S("code")}: {m.S("message")}"); tools.OnError(m); ed.OnError(w, m); break;
                    }
                if (done()) return true;
                Thread.Sleep(5);
            }
            return false;
        }
        double Now() => DateTime.UtcNow.Ticks / 1e7;
        check(Wait(() => w.Loaded && link.Connected && tools.Limits.Allowed), "live power: welcome grants power_v1 and a snapshot arrives");
        if (!tools.Limits.Allowed) { link.Dispose(); return; }
        var origin = new Vector3Int(0, -64, 0);
        link.Send(Protocol.RegionRequest(w.World, origin, new Vector3Int(32, 64, 32)));
        check(Wait(() => w.Loaded && w.Origin == origin), $"live power: region on the surface ({w.Origin})");
        string At(int x, int y, int z) => w.State(w.Get(x, y, z));
        var down = new Vector3(0, -1, 0);
        BlockHit Ray(float x, float z) => BlockRaycast.Cast(w, new Vector3(x, w.Dims.y - 0.5f, z), down, w.Dims.y, new Vector3(0, 0, 0), (Vector3)w.Dims);
        // a trigger hold of three places = one group
        ed.Selected = "minecraft:gold_block";
        ed.Group = tools.Groups.Begin();
        foreach (int x in new[] { 3, 4, 5 }) { link.Send(ed.Place(w, Ray(x + 0.5f, 20.5f), down, Now())); Thread.Sleep(80); }
        check(Wait(() => At(3, 4, 20) == "minecraft:gold_block" && At(5, 4, 20) == "minecraft:gold_block" && tools.History.UndoBlocks >= 3),
              $"live power: three places in one group (history {tools.History.Undo} groups, {tools.History.UndoBlocks} blocks)");
        var u = tools.Press(ToolButton.Undo, null, Now());
        tools.Sent(link.SendTracked(u), "undo", Now());
        check(Wait(() => At(3, 4, 20) == "minecraft:air" && At(5, 4, 20) == "minecraft:air" && !tools.Busy(Now())), "live power: one undo takes the hold back");
        // the wand: a 4x2x4 box of stone bricks, then cleared
        tools.Press(ToolButton.Wand, null, Now());
        tools.SetCorner(false, origin + new Vector3Int(8, 4, 8)); tools.SetCorner(true, origin + new Vector3Int(11, 5, 11));
        var fill = tools.Box("fill", "minecraft:stone_bricks", Now());
        tools.Sent(link.SendTracked(fill), "fill", Now());
        check(Wait(() => At(8, 4, 8) == "minecraft:stone_bricks" && At(11, 5, 11) == "minecraft:stone_bricks" && !tools.Busy(Now()) && tools.Progress?.Changed == 32),
              $"live power: the wand fills its box ({tools.Progress?.Text()})");
        var clear = tools.Box("clear", null, Now());
        tools.Sent(link.SendTracked(clear), "clear", Now());
        check(Wait(() => At(8, 4, 8) == "minecraft:air" && !tools.Busy(Now())), "live power: and clears it");
        if (errors.Count > 0) Console.WriteLine("live power errors: " + string.Join("; ", errors));
        link.Dispose();
    }
}
