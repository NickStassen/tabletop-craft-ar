// Engine-free tests for the block menu: block_registry parsing (Net/BlockRegistry.cs), the menu model's layout, hit
// tests, selection state machine, search, scrolling and hotbar (World/BlockMenuModel.cs), the registry check in
// BlockEditor, the HUD's toasts and link watch (World/HudState.cs), and, with --jar, the icon pipeline
// (World/PngDecoder.cs, World/BlockIcons.cs) against a real client jar. --icons DIR [--registry FILE] writes a contact
// sheet of every registry block's icon (FILE: a block_registry message saved from a server) for a visual check.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class MenuTests
{
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string jar, string[] args)
    {
        var fx = Parse(File.ReadAllText(fixture("block_registry.json")));
        Registry(check, fx);
        Model(check, fx);
        Editor(check, fx);
        Hud(check);
        if (jar != null) Icons(check, jar, args);
        else Console.WriteLine("SKIP icon tests: set MC_CLIENT_JAR or pass --jar PATH");
    }

    static void Registry(Action<bool, string> check, Dictionary<string, object> fx)
    {
        var r = BlockRegistry.From(fx);
        check(r.Count == 13 && r.CategoryIds.Count == 6 && r.CategoryIds[0] == "building" && r.CategoryLabels[4] == "Redstone Blocks", "registry: 13 blocks in 6 categories");
        check(r.ByCategory[0].Count == 4 && r.ByCategory[5].Count == 0 && r.Entries[0].Order == 0 && r.Entries[12].Order == 12, "registry: per-category lists in menu order");
        check(r.Find("minecraft:oak_log[axis=x]")?.State == "minecraft:oak_log[axis=y]", "registry: any state finds its block's entry");
        check(r.Find("minecraft:wall_torch[facing=north]")?.Id == "minecraft:torch" && r.IsPlaceable("minecraft:wall_torch[facing=east]"), "registry: variant (wall torch) belongs to the torch");
        check(!r.IsPlaceable("minecraft:fire[age=0]") && !r.IsPlaceable("minecraft:air") && r.IsPlaceable("stone"), "registry: fire/air not placeable, ids without namespace work");
        check(r.ById["minecraft:redstone_wire"].Item == "minecraft:redstone" && r.ById["minecraft:stone"].Item == "minecraft:stone", "registry: item ids for icons");
        check(r.ById["minecraft:oak_stairs"].Label == "Oak Stairs" && BlockRegistry.DefaultLabel("light_blue_stained_glass") == "Light Blue Stained Glass", "registry: default labels");
        var lang = new Dictionary<string, string> { ["block.minecraft.diamond_block"] = "Block of Diamond", ["item.minecraft.redstone"] = "Redstone Dust", ["block.minecraft.redstone_wire"] = "Redstone Wire" };
        r.ApplyNames(k => lang.TryGetValue(k, out var v) ? v : null);
        check(r.ById["minecraft:diamond_block"].Label == "Block of Diamond" && r.ById["minecraft:redstone_wire"].Label == "Redstone Dust" && r.NamesVersion == 1,
              "registry: names from the pack's language file (item name for redstone)");
        check(r.ById["minecraft:diamond_block"].SearchText.Contains("block of diamond") && r.ById["minecraft:diamond_block"].SearchText.Contains("diamond block"), "registry: search text has name and id words");
        bool threw = false;
        try { BlockRegistry.From(Parse("{\"type\":\"block_registry\",\"count\":0}")); } catch (FormatException) { threw = true; }
        check(threw, "registry: no categories -> FormatException");
        var dup = BlockRegistry.From(Parse("{\"categories\":[{\"id\":\"a\",\"blocks\":[\"minecraft:stone\",\"minecraft:stone[x=1]\"]}],\"variants\":{\"minecraft:x\":\"minecraft:nope\"}}"));
        check(dup.Count == 1 && dup.Variants.Count == 0 && dup.CategoryLabels[0] == "A", "registry: duplicates and dangling variants dropped");
    }

    /// <summary>The fixture plus 60 extra building blocks (so the grid scrolls) and some oak/dark oak names for search.</summary>
    static BlockRegistry Big(Dictionary<string, object> fx)
    {
        var cats = (List<object>)fx["categories"];
        var building = new List<object>((List<object>)((Dictionary<string, object>)cats[0])["blocks"]);
        building.Add("minecraft:dark_oak_planks"); building.Add("minecraft:oak_planks");
        for (int i = 0; i < 60; i++) building.Add($"minecraft:test_block_{i:D2}");
        var c0 = new Dictionary<string, object>((Dictionary<string, object>)cats[0]) { ["blocks"] = building };
        var m = new Dictionary<string, object>(fx) { ["categories"] = new List<object>(cats) { [0] = c0 } };
        return BlockRegistry.From(m);
    }

    static MenuHit HitOf(BlockMenuModel m, MenuPart part, int index)
    {
        var L = m.Layout();
        var it = L.Items.First(i => i.Part == part && i.Index == index);
        return BlockMenuModel.HitTest(L, it.R.CX, it.R.CY);
    }

    static void Model(Action<bool, string> check, Dictionary<string, object> fx)
    {
        var r = Big(fx);
        var m = new BlockMenuModel();
        m.SetRegistry(r);
        check(m.Selected == "minecraft:stone" && m.Hotbar[0] == "minecraft:stone" && !m.Open, "menu: starts closed with stone selected and in the hotbar");

        // Closed: the ray never belongs to the menu.
        m.Point(new MenuHit(MenuPart.Cell, 0), true);
        check(!m.CapturesRight && m.Click().Kind == MenuActionKind.None, "menu: closed -> pointer ignored, trigger does nothing");

        m.Toggle();
        var L = m.Layout();
        check(L.Width > 0.29f && L.Width < 0.33f && L.Height > 0.2f && L.Height < 0.32f, $"menu: panel {L.Width:F3} x {L.Height:F3} m");
        check(L.Items.All(i => i.R.X >= 0 && i.R.Y >= 0 && i.R.X + i.R.W <= L.Width + 1e-5f && i.R.Y + i.R.H <= L.Height + 1e-5f), "menu: every box inside the panel");
        var clickable = L.Items.ToList();
        bool overlap = false;
        for (int a = 0; a < clickable.Count; a++)
            for (int b = a + 1; b < clickable.Count; b++)
            {
                var p = clickable[a].R; var q = clickable[b].R;
                if (p.X + 1e-5f < q.X + q.W && q.X + 1e-5f < p.X + p.W && p.Y + 1e-5f < q.Y + q.H && q.Y + 1e-5f < p.Y + p.H) overlap = true;
            }
        check(!overlap, "menu: clickable boxes don't overlap");
        check(L.Items.Count(i => i.Part == MenuPart.Tab) == 8 && L.Items.Count(i => i.Part == MenuPart.Cell) == 45 && L.Items.Count(i => i.Part == MenuPart.Hotbar) == 9
              && !L.Items.Any(i => i.Part == MenuPart.AreaStep), "menu: 8 tabs, 9x5 grid, 9 hotbar slots, no area row without hooks");
        check(BlockMenuModel.HitTest(L, -0.01f, 0.1f).Part == MenuPart.None && BlockMenuModel.HitTest(L, 0.001f, 0.001f).Part == MenuPart.Panel, "menu: outside = None, margin = Panel");

        // Hover + select.
        var cell3 = HitOf(m, MenuPart.Cell, 1);
        m.Point(cell3, true);
        var e3 = m.CellEntry(1);
        check(m.CapturesRight && cell3.Part == MenuPart.Cell && cell3.Index == 1 && m.NameLine() == e3.Label, "menu: hover a cell shows its name");
        int v0 = m.Version;
        var act = m.Click();
        check(act.Kind == MenuActionKind.Select && act.Haptic && m.Selected == e3.State && m.Hotbar[0] == e3.State && m.Hotbar[1] == "minecraft:stone" && m.Version > v0,
              "menu: trigger on a cell selects it and pushes it into the hotbar");
        m.Point(MenuHit.Nothing, false);
        check(!m.CapturesRight && m.Click().Kind == MenuActionKind.None && m.NameLine().Contains(e3.Label), "menu: ray off the panel -> bindings back, no click");

        // Scrolling: 75 building entries = 9 rows, 5 visible.
        check(m.Items.Count == 66 && m.RowCount == 8 && m.MaxScroll == 3, $"menu: building tab rows {m.RowCount}, max scroll {m.MaxScroll}");
        check(!m.Stick(-1, 0), "menu: stick ignored while the ray is off the panel");
        m.Point(cell3, true);
        check(!m.Stick(1, 0) && m.Scroll == 0, "menu: stick up at the top does nothing");
        check(m.Stick(-1, 1.0) && m.Scroll == 1, "menu: stick down scrolls a row");
        check(!m.Stick(-1, 1.2) && m.Scroll == 1, "menu: held stick waits before repeating");
        check(m.Stick(-1, 1.36) && m.Scroll == 2 && m.Stick(-1, 1.49) && m.Scroll == 3 && !m.Stick(-1, 1.62), "menu: then repeats every 0.12 s up to the end");
        m.Stick(0, 1.7);
        check(m.CellEntry(0) == m.Items[27], "menu: cell 0 shows the first visible row");
        m.Point(HitOf(m, MenuPart.ScrollUp, 0), true);
        check(m.Click().Kind == MenuActionKind.Scrolled && m.Scroll == 0, "menu: up arrow pages up (clamped)");
        m.Point(HitOf(m, MenuPart.Cell, 44), true);
        check(m.CellEntry(44) != null && m.Click().Kind == MenuActionKind.Select, "menu: last visible cell selectable");

        // Tabs keep their own scroll.
        m.ScrollBy(2);
        m.Point(HitOf(m, MenuPart.Tab, 2), true);
        check(m.NameLine() == "Natural Blocks", "menu: hovering a tab names the category");
        check(m.Click().Kind == MenuActionKind.Tab && m.Tab == 2 && m.Items.Count == 3 && m.Scroll == 0, "menu: tab switches category");
        m.SetTab(0);
        check(m.Scroll == 2, "menu: back to a tab restores its scroll");
        m.Point(HitOf(m, MenuPart.Cell, 44), true);
        m.SetTab(2);
        check(m.CellEntry(44) == null && m.Click().Kind == MenuActionKind.None, "menu: empty cell does nothing");

        // Search.
        m.SetTab(m.SearchTab);
        L = m.Layout();
        check(m.Items.Count == r.Count && L.Items.Count(i => i.Part == MenuPart.Key) == 29 && L.Items.Count(i => i.Part == MenuPart.Cell) == 27, "search: empty query lists all, keyboard + 3 rows");
        foreach (char ch in "oak") { m.Point(HitOf(m, MenuPart.Key, "qwertyuiopasdfghjklzxcvbnm".IndexOf(ch)), true); m.Click(); }
        check(m.Query == "oak" && m.Items.Count == 4 && m.Items[0].Id == "minecraft:oak_log" && m.Items[1].Id == "minecraft:oak_stairs"
              && m.Items[2].Id == "minecraft:oak_planks" && m.Items[3].Id == "minecraft:dark_oak_planks", "search: names starting with the query first, then word matches");
        m.Point(HitOf(m, MenuPart.Key, BlockMenuModel.KeySpace), true); m.Click();
        foreach (char ch in "st") { m.Point(HitOf(m, MenuPart.Key, "qwertyuiopasdfghjklzxcvbnm".IndexOf(ch)), true); m.Click(); }
        check(m.Query == "oak st" && m.Items.Count == 1 && m.Items[0].Id == "minecraft:oak_stairs", "search: every word must match");
        m.Point(HitOf(m, MenuPart.Key, BlockMenuModel.KeyDelete), true); m.Click();
        check(m.Query == "oak s", "search: delete removes the last character");
        m.Point(HitOf(m, MenuPart.Key, BlockMenuModel.KeyClear), true);
        check(m.Click().Kind == MenuActionKind.Typed && m.Query == "" && m.Click().Kind == MenuActionKind.None, "search: clear empties; clear again does nothing");
        check(BlockMenuModel.Search(r, "PLANKS").Count == 2 && BlockMenuModel.Search(r, "zzz").Count == 0, "search: case-insensitive, no match = empty");

        // Hotbar and its slots.
        var hb = new Hotbar();
        foreach (var s in new[] { "minecraft:a", "minecraft:b", "minecraft:c" }) hb.Push(s);
        check(hb[0] == "minecraft:c" && hb[1] == "minecraft:b" && hb[2] == "minecraft:a" && hb.Count == 3, "hotbar: most recent first");
        hb.Push("minecraft:b[facing=east]");
        check(hb[0] == "minecraft:b[facing=east]" && hb[1] == "minecraft:c" && hb[2] == "minecraft:a" && hb.Count == 3, "hotbar: one slot per block (re-pick moves it to the front)");
        for (int i = 0; i < 10; i++) hb.Push("minecraft:x" + i);
        check(hb.Count == 9 && hb[0] == "minecraft:x9" && hb[8] == "minecraft:x1", "hotbar: 9 slots, oldest dropped");
        var hb2 = new Hotbar();
        check(hb2.Cycle() == null, "hotbar: cycling an empty hotbar gives nothing");
        hb2.Push("minecraft:a"); hb2.Push("minecraft:b");
        check(hb2.Cycle() == "minecraft:a" && hb2.Selected == 1 && hb2.Cycle() == "minecraft:b" && hb2.Selected == 0 && hb2.Cycle(-1) == "minecraft:a", "hotbar: cycle skips empty slots and wraps");
        check(hb2.Choose(5) == null && hb2.Choose(0) == "minecraft:b", "hotbar: choosing an empty slot does nothing");

        m.SetTab(0);
        var before = m.Hotbar[1];
        m.Point(HitOf(m, MenuPart.Hotbar, 1), true);
        check(m.NameLine() == m.LabelOf(before), "menu: hovering a hotbar slot names its block");
        act = m.Click();
        check(act.Kind == MenuActionKind.HotbarSlot && m.Selected == before && m.Hotbar[1] == before && m.Hotbar.Selected == 1, "menu: clicking a hotbar slot selects it without reordering");

        // B pick and the right stick click (menu closed).
        m.Point(MenuHit.Nothing, false);
        check(!m.Picked("minecraft:fire[age=3]") && m.Selected == before && m.NameLine().Contains("can't place"), "pick: a block the server won't place isn't selected");
        check(m.Picked("minecraft:wall_torch[facing=east]") && m.Selected == "minecraft:wall_torch[facing=east]" && m.Hotbar[0] == m.Selected, "pick: a variant is selected as picked and goes into the hotbar");
        check(m.Picked("minecraft:oak_log[axis=z]") && m.Hotbar[0] == "minecraft:oak_log[axis=z]" && m.Hotbar[1] == "minecraft:wall_torch[facing=east]", "pick: MRU order");
        m.SetOpen(false);
        check(!m.Open && !m.PointerOnPanel && m.Hover.Part == MenuPart.None, "menu: closing releases the pointer");
        string next = m.CycleHotbar();
        check(next == "minecraft:wall_torch[facing=east]" && m.Selected == next, "hotbar: stick click cycles to the next slot and selects it");

        // Area row hooks and the Info tab.
        m.SetOpen(true);
        m.AreaPresets = new[] { 32, 64, 96, 128, 192, 256 };
        L = m.Layout();
        check(L.Items.Count(i => i.Part == MenuPart.AreaStep) == 5 && L.AreaLabel.W > 0, "area row: 5 steps when the hooks are set");
        m.Point(HitOf(m, MenuPart.AreaStep, 2), true);
        act = m.Click();
        check(act.Kind == MenuActionKind.Area && act.Value == 96, "area row: a step requests that size");
        m.SetTab(m.InfoTab);
        L = m.Layout();
        check(L.Info.H > 0.08f && L.Items.Any(i => i.Part == MenuPart.HudToggle && i.Label == "Show HUD: off") && !L.Items.Any(i => i.Part == MenuPart.Cell), "info tab: status area and Show HUD toggle");
        m.Point(HitOf(m, MenuPart.HudToggle, 0), true);
        act = m.Click();
        check(act.Kind == MenuActionKind.HudToggle && act.Value == 1 && m.ShowHud && m.Layout().Items.Any(i => i.Label == "Show HUD: on"), "info tab: toggle turns the HUD on");
    }

    static void Editor(Action<bool, string> check, Dictionary<string, object> fx)
    {
        var r = BlockRegistry.From(fx);
        var w = new WorldModel { SnapshotId = 1, World = "world", Dims = new Vector3Int(2, 2, 1) };
        w.Palette.AddRange(new[] { "minecraft:air", "minecraft:fire[age=0]", "minecraft:stone" });
        w.Grid = new ushort[] { 2, 2, 1, 0 };
        var ed = new BlockEditor { ServerAllows = true, Placeable = s => r.IsPlaceable(s) };
        var hitFire = new BlockHit { Hit = true, Block = new Vector3Int(0, 1, 0), Face = 3, Adjacent = new Vector3Int(0, 2, 0) };
        check(!ed.Pick(w, hitFire) && ed.Selected == BlockEditRules.DefaultBlock && ed.Result.Contains("can't place"), "editor: picking fire keeps the selection");
        ed.Selected = "minecraft:fire[age=0]";
        var hitStone = new BlockHit { Hit = true, Block = new Vector3Int(0, 0, 0), Face = 3, Adjacent = new Vector3Int(0, 1, 0), AdjacentInView = true };
        w.Grid[w.Index(0, 1, 0)] = 0;
        check(ed.Place(w, hitStone, new Vector3(0, -1, 0), 0) == null && ed.Result.Contains("can't place"), "editor: a non-registry selection isn't sent");
        ed.Selected = "minecraft:stone";
        check(ed.Place(w, hitStone, new Vector3(0, -1, 0), 0) != null, "editor: a registry block is sent");
        var open = new BlockEditor { ServerAllows = true };
        check(open.Pick(w, hitFire), "editor: without a registry (older server) everything can be picked");
    }

    static void Hud(Action<bool, string> check)
    {
        var q = new ToastQueue();
        q.Push("link lost", ToastKind.Warn, 0);
        var v = q.Visible(0.05);
        check(v.Count == 1 && v[0].Alpha > 0.2f && v[0].Alpha < 0.5f, "toast: fades in");
        check(Math.Abs(q.Visible(1.0)[0].Alpha - 1) < 1e-4f && Math.Abs(q.Visible(2.25)[0].Alpha - 0.5f) < 1e-3f, "toast: holds, then fades out");
        check(q.Visible(2.6).Count == 0 && q.Count == 0, "toast: gone after 2.5 s");
        q.Push("a", ToastKind.Info, 10); q.Push("b", ToastKind.Info, 10.1); q.Push("a", ToastKind.Info, 11);
        v = q.Visible(11.05);
        check(v.Count == 2 && v[1].Text == "a" && v[0].Text == "b", "toast: the same text restarts instead of stacking");
        check(q.Visible(13.2).Count == 1 && q.Visible(13.2)[0].Text == "a", "toast: restarted one outlives the other");
        q.Clear();
        q.Push("e1", ToastKind.Error, 20); q.Push("i1", ToastKind.Info, 20); q.Push("i2", ToastKind.Info, 20); q.Push("i3", ToastKind.Info, 20);
        v = q.Visible(20.5);
        check(v.Count == 3 && v[0].Text == "i1", "toast: at most 3, oldest dropped");
        q.Clear(); q.Push("e", ToastKind.Error, 0);
        check(q.Visible(2.8).Count == 1 && q.Visible(3.01).Count == 0, "toast: errors last 3 s");
        q.Push(new string('x', 200), ToastKind.Info, 0);
        check(q.Visible(0.5).Last().Text.Length == ToastQueue.MaxChars && q.Visible(0.5).Last().Text.EndsWith("..."), "toast: long text shortened");

        var lw = new LinkWatch();
        check(lw.Update(false) == null && lw.Update(true) == null && lw.Update(true) == null, "link watch: first connection is quiet");
        check(lw.Update(false)?.StartsWith("Link lost") == true && lw.Update(false) == null && lw.Update(true) == "Link restored", "link watch: lost and restored once each");
        check(HudText.ForError("edit_denied", "cell is occupied").StartsWith("Edit refused") && HudText.ForError("region_too_large", "x").StartsWith("Area too large")
              && HudText.ForError("bad_state", "fire").StartsWith("Can't place") && HudText.ForError(null, "x") == null, "hud text: short error toasts");
    }

    static double Coverage(Color32[] px) => px.Count(c => c.a > 128) / (double)px.Length;
    static float Lum(Color32 c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;

    static void Icons(Action<bool, string> check, string jar, string[] args)
    {
        // PNG decoder against the test reader (RGB, top-down) on a few textures of different PNG kinds.
        using (var z = ZipFile.OpenRead(jar))
        {
            int ok = 0, n = 0;
            foreach (var path in new[] { "assets/minecraft/textures/block/stone.png", "assets/minecraft/textures/block/oak_leaves.png", "assets/minecraft/textures/block/water_still.png",
                                         "assets/minecraft/textures/item/diamond.png", "assets/minecraft/textures/block/torch.png", "assets/minecraft/textures/colormap/grass.png" })
            {
                var e = z.GetEntry(path);
                if (e == null) continue;
                byte[] b; using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); b = ms.ToArray(); }
                n++;
                var mine = PngDecoder.Decode(b, out int w, out int h);
                int[] theirs;
                try { theirs = Png.DecodeRgb(b, out int w2, out int h2); } catch (NotSupportedException) { ok++; continue; }
                bool same = mine != null && mine.Length == theirs.Length;
                for (int y = 0; same && y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var c = mine[(h - 1 - y) * w + x]; int t = theirs[y * w + x];
                        if (c.a > 0 && (c.r != (t >> 16 & 255) || c.g != (t >> 8 & 255) || c.b != (t & 255))) { same = false; break; }
                    }
                if (same) ok++;
            }
            check(n >= 5 && ok == n, $"png: decoder matches the reference on {ok}/{n} pack textures");
        }

        using var assets = new IconAssets(jar);
        var rr = new IconRenderer(assets);
        var stone = rr.Render("minecraft:stone", "minecraft:stone");
        int S = IconRenderer.Size;
        Color32 At(Color32[] p, float fx, float fy) => p[(int)(fy * S) * S + (int)(fx * S)];
        check(Coverage(stone) > 0.45 && Coverage(stone) < 0.8 && At(stone, 0.5f, 0.5f).a == 255, $"icon: stone is an isometric cube ({Coverage(stone):P0} covered)");
        check(Lum(At(stone, 0.5f, 0.78f)) > Lum(At(stone, 0.3f, 0.35f)) && Lum(At(stone, 0.3f, 0.35f)) > Lum(At(stone, 0.7f, 0.35f)), "icon: top lighter than the left side, left lighter than the right");
        var grass = rr.Render("minecraft:grass_block[snowy=false]", "minecraft:grass_block");
        var gt = At(grass, 0.5f, 0.78f);
        check(gt.g > gt.r + 20 && gt.g > gt.b + 20, "icon: grass top is tinted green");
        var torch = rr.Render("minecraft:torch", "minecraft:torch");
        var tex = assets.Pixels(assets.LayerOf("minecraft:block/torch"), out int ts);
        bool flat = tex != null && Enumerable.Range(0, S * S).All(i => (torch[i].a > 0) == (tex[(i / S * ts / S) * ts + (i % S) * ts / S].a > 0));
        check(flat, "icon: torch is the flat item sprite");
        var dust = rr.Render("minecraft:redstone_wire[east=none,north=none,power=0,south=none,west=none]", "minecraft:redstone");
        check(Coverage(dust) > 0.05 && Coverage(dust) < 0.6, "icon: redstone wire shows the redstone dust item");
        var fence = rr.Render("minecraft:oak_fence[east=false,north=false,south=false,waterlogged=false,west=false]", "minecraft:oak_fence");
        check(Coverage(fence) > 0.08 && Coverage(fence) < 0.5, "icon: fence uses its inventory model");
        var chest = rr.Render("minecraft:chest[facing=north,type=single,waterlogged=false]", "minecraft:chest");
        check(Coverage(chest) > 0.3, "icon: chest (builtin item) drawn from the block's box");
        var glass = rr.Render("minecraft:red_stained_glass", "minecraft:red_stained_glass");
        var gc = At(glass, 0.5f, 0.5f);
        check(gc.a > 40 && gc.a < 250 && gc.r > gc.b, "icon: stained glass is translucent red");
        var flatCube = new IconRenderer(null).Render("minecraft:grass_block", null);
        check(Coverage(flatCube) > 0.45 && At(flatCube, 0.5f, 0.5f).g > At(flatCube, 0.5f, 0.5f).r, "icon: without a pack a flat-colour cube");

        // Disk cache and the worker.
        string dir = Path.Combine(Path.GetTempPath(), "tabletop-icon-test-" + Environment.ProcessId);
        try
        {
            var cache = IconDiskCache.For(dir, jar);
            cache.Save("k", stone);
            var back = cache.Load("k");
            check(back != null && back.SequenceEqual(stone) && cache.Load("missing") == null, "icon cache: round trip");
            Directory.CreateDirectory(Path.Combine(dir, "old-pack"));
            cache = IconDiskCache.For(dir, jar);
            check(!Directory.Exists(Path.Combine(dir, "old-pack")) && Directory.Exists(cache.Dir), "icon cache: other packs' folders are removed");
            var states = new[] { "minecraft:stone", "minecraft:oak_log[axis=y]", "minecraft:poppy" };
            var got = TakeAll(new IconStore(jar, cache), states, out var store1);
            check(got == 3 && store1.Rendered >= 2 && store1.NamesReady && store1.Names != null && store1.Names.TryGetValue("block.minecraft.diamond_block", out var dn) && dn.Length > 0,
                  $"icon store: worker renders on request and reads the pack's names ({store1.Rendered} rendered)");
            got = TakeAll(new IconStore(jar, cache), states, out var store2);
            check(got == 3 && store2.Rendered == 0 && store2.FromDisk == 3, "icon store: next start reads them from disk");
            store1.Dispose(); store2.Dispose();
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }

        int idx = Array.IndexOf(args, "--icons");
        if (idx >= 0 && idx + 1 < args.Length) Sheet(jar, args[idx + 1], args, assets);
    }

    static int TakeAll(IconStore s, string[] states, out IconStore store)
    {
        store = s;
        foreach (var st in states) s.Request(st, BlockRegistry.IdOf(st), 0);
        var got = new HashSet<string>();
        var until = DateTime.UtcNow.AddSeconds(20);
        while (got.Count < states.Length && DateTime.UtcNow < until)
        {
            while (s.TryTake(out var st, out var px)) if (px != null && px.Length == IconRenderer.Size * IconRenderer.Size) got.Add(st);
            Thread.Sleep(5);
        }
        return got.Count;
    }

    /// <summary>--icons DIR: every block's icon on one sheet (sheet.png) plus timings, for looking at them.</summary>
    static void Sheet(string jar, string dir, string[] args, IconAssets assets)
    {
        Directory.CreateDirectory(dir);
        int ri = Array.IndexOf(args, "--registry");
        List<(string state, string item)> blocks;
        if (ri >= 0 && ri + 1 < args.Length)
        {
            var reg = BlockRegistry.From(Parse(File.ReadAllText(args[ri + 1])));
            blocks = reg.Entries.Select(e => (e.State, e.Item)).ToList();
        }
        else blocks = new List<(string, string)> { ("minecraft:stone", "minecraft:stone"), ("minecraft:grass_block[snowy=false]", "minecraft:grass_block"), ("minecraft:torch", "minecraft:torch") };
        var rr = new IconRenderer(assets);
        int S = IconRenderer.Size, cols = 32, rows = (blocks.Count + cols - 1) / cols;
        var sheet = new Color32[cols * S * rows * S];
        for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(139, 139, 139, 255);   // inventory grey
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int b = 0; b < blocks.Count; b++)
        {
            var px = rr.Render(blocks[b].state, blocks[b].item);
            int ox = (b % cols) * S, oy = (rows - 1 - b / cols) * S;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    var c = px[y * S + x]; ref var d = ref sheet[(oy + y) * cols * S + ox + x];
                    float a = c.a / 255f;
                    d = new Color32((byte)(c.r * a + d.r * (1 - a)), (byte)(c.g * a + d.g * (1 - a)), (byte)(c.b * a + d.b * (1 - a)), 255);
                }
        }
        double ms = sw.Elapsed.TotalMilliseconds;
        File.WriteAllBytes(Path.Combine(dir, "sheet.png"), Png.EncodeRgba(sheet, cols * S, rows * S));
        File.WriteAllLines(Path.Combine(dir, "sheet.txt"), blocks.Select((x, i) => $"{i / cols},{i % cols} {x.state}"));
        Console.WriteLine($"ICONS {blocks.Count} rendered in {ms:F0} ms ({ms / Math.Max(1, blocks.Count):F2} ms each) -> {dir}/sheet.png");
    }
}
