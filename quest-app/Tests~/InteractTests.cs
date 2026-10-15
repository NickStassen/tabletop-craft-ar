// [hands] Engine-free tests for using blocks, items and mobs (PROTOCOL.md "Using blocks and items", "Acting on mobs"):
// the fixtures through Net/InteractProtocol.cs, the left trigger's right-click order (World/UseRules.cs), aiming at
// mobs (World/EntityRaycast.cs), the empty bucket's fluid ray (World/BlockRaycast.cs), the Use and Eggs tabs and the
// name tag field (World/BlockMenuModel.cs), interact messages from BlockEditor, and the info card (World/EntityCardModel.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class InteractTests
{
    static Dictionary<string, object> Parse(string s) => Json.Parse(s) as Dictionary<string, object>;
    const string U = "3c1e5f7a-2b4d-4e6f-8a9b-0c1d2e3f4a5b";
    static readonly Vector3 Zero = new Vector3(0, 0, 0), Right = new Vector3(1, 0, 0), Down = new Vector3(0, -1, 0);
    const string Door = "minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]";

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Fixtures(check, fixture);
        Rules(check);
        Raycast(check);
        Menu(check, fixture);
        Editor(check);
        Card(check, fixture);
    }

    static void Fixtures(Action<bool, string> check, Func<string, string> fixture)
    {
        var reg = ItemRegistry.From(Parse(File.ReadAllText(fixture("item_registry.json"))));
        check(reg.Use.Count == 18 && reg.Eggs.Count == 3 && reg.Count == 21, "item_registry: 18 usable items and 3 spawn eggs");
        var meal = reg.Find("item:minecraft:bone_meal");
        check(meal != null && meal.Label == "Bone Meal" && meal.Item == "minecraft:bone_meal" && BlockRegistry.IdOf(meal.State) == meal.State,
              "item_registry: items are menu entries \"item:<id>\" with their own icon item");
        var bolt = reg.Find(InteractProtocol.Selection(InteractProtocol.Lightning));
        check(bolt != null && bolt.Label == "Lightning" && bolt.Item == "minecraft:lightning_rod", "item_registry: the lightning wand looks like a lightning rod");
        check(reg.Eggs.All(e => e.State.EndsWith("_spawn_egg")) && reg.Find("item:minecraft:pig_spawn_egg").Label == "Pig Spawn Egg", "item_registry: spawn eggs");
        var lang = new Dictionary<string, string> { ["item.minecraft.bone_meal"] = "Bone Meal!", ["item.minecraft.pig_spawn_egg"] = "Pig Spawn Egg" };
        reg.ApplyNames(k => lang.TryGetValue(k, out var v) ? v : null);
        check(meal.Label == "Bone Meal!" && bolt.Label == "Lightning", "item_registry: names from the pack, the wand keeps its own");
        bool threw = false;
        try { ItemRegistry.From(Parse("{\"categories\":[{\"id\":\"use\",\"items\":[\"bone meal\"]}]}")); } catch (FormatException) { threw = true; }
        check(threw, "item_registry: an item without a namespace is rejected");

        var w = InteractWelcome.From(Parse(File.ReadAllText(fixture("welcome_interact.json"))));
        check(w.Allowed && w.IronDoors && w.Entities && !w.HarmPlayers && w.Lightning == "real" && w.AttackDamage == 5 && w.SpawnsPerMinute == 60, "welcome.interact parses");
        check(!InteractWelcome.From(Parse(File.ReadAllText(fixture("welcome.json")))).Allowed, "a welcome without interact_v1: not allowed");
        check(Parse(File.ReadAllText(fixture("hello_interact.json"))).A("caps").Contains(InteractProtocol.Cap), "hello_interact asks for interact_v1");
        check(Protocol.Hello("").A("caps").Contains(InteractProtocol.Cap), "the app's hello asks for interact_v1");

        var fx = Parse(File.ReadAllText(fixture("interact.json")));
        var built = InteractProtocol.Interact(new Vector3Int(105, 64, -35), "north", new Vector3(0.5f, 0.7f, 0f), null, false, new Vector3(0.1f, -0.5f, 0.86f));
        check(Json.Write(built["pos"]) == Json.Write(fx["pos"]) && (string)built["face"] == "north" && built["item"] == null && fx["item"] == null &&
              Json.Write(built["hit"]) == Json.Write(fx["hit"]), "interact builder matches the fixture (pos, face, hit, empty hand)");
        var d = (List<object>)built["dir"];
        check(Math.Abs(Math.Sqrt(d.Sum(x => (double)x * (double)x)) - 1) < 0.01, "interact dir is the normalised ray direction");
        var mv = Parse(File.ReadAllText(fixture("entity_op_move.json")));
        var op = InteractProtocol.EntityOp(U, "move", to: new Vector3(110.5f, 64f, -30.5f), yaw: 90f);
        check(Json.Write(op["to"]) == Json.Write(mv["to"]) && (double)op["yaw"] == 90 && (string)op["uuid"] == (string)mv["uuid"], "entity_op builder matches the move fixture");
        var nm = InteractProtocol.EntityOp(U, "name", text: "Rex");
        check((string)nm["text"] == Parse(File.ReadAllText(fixture("entity_op_name.json"))).S("text") && !nm.ContainsKey("to"), "entity_op name carries only the text");
        check(InteractProtocol.EggOf("minecraft:mooshroom") == "minecraft:mooshroom_spawn_egg" && InteractProtocol.ItemId("item:minecraft:shears") == "minecraft:shears"
              && InteractProtocol.ItemId("minecraft:stone") == null, "egg and item id helpers");

        var info = EntityInfoMessage.From(Parse(File.ReadAllText(fixture("entity_info.json"))));
        var lines = info.Lines();
        check(info.Uuid == U && info.EntityType == "minecraft:wolf" && info.Health == 14 && info.MaxHealth == 20 && info.Frozen == false && info.Ai == true,
              "entity_info parses");
        check(lines[0] == "Wolf \"Rex\"" && lines.Contains("Health 14 / 20") && lines.Contains("Owner Alex") && lines.Contains("Baby, grows up in 2:00") &&
              lines.Last() == "at 108.7, 64, -28", $"entity_info card lines ({string.Join(" | ", lines)})");
        var err = Parse(File.ReadAllText(fixture("error_not_found.json")));
        check(err.S("op") == "entity_op" && err.S("uuid") == U && HudText.ForError("not_found", "x").StartsWith("Gone"), "not_found error carries op and uuid");
        check(HudText.ForError("interact_denied", "the axe does nothing to stone") == "Can't: the axe does nothing to stone", "interact_denied toast");
    }

    static void Rules(Action<bool, string> check)
    {
        var table = new (string state, UseKind kind)[]
        {
            (Door, UseKind.Door), ("minecraft:iron_trapdoor[facing=north,half=bottom,open=false,powered=false,waterlogged=false]", UseKind.Trapdoor),
            ("minecraft:oak_fence_gate[facing=north,in_wall=false,open=false,powered=false]", UseKind.Gate), ("minecraft:lever[face=wall,facing=east,powered=false]", UseKind.Lever),
            ("minecraft:stone_button[face=floor,facing=north,powered=false]", UseKind.Button), ("minecraft:note_block[instrument=harp,note=0,powered=false]", UseKind.NoteBlock),
            ("minecraft:repeater[delay=1,facing=north,locked=false,powered=false]", UseKind.Repeater), ("minecraft:comparator[facing=north,mode=compare,powered=false]", UseKind.Comparator),
            ("minecraft:daylight_detector[inverted=false,power=0]", UseKind.Daylight), ("minecraft:bell[attachment=floor,facing=north,powered=false]", UseKind.Bell),
            ("minecraft:cake[bites=0]", UseKind.Cake), ("minecraft:red_candle_cake[lit=false]", UseKind.CandleCake), ("minecraft:composter[level=8]", UseKind.Composter),
            ("minecraft:composter[level=3]", UseKind.None), ("minecraft:jukebox[has_record=true]", UseKind.Jukebox), ("minecraft:jukebox[has_record=false]", UseKind.None),
            ("minecraft:candle[candles=2,lit=true,waterlogged=false]", UseKind.Candle), ("minecraft:blue_candle[candles=1,lit=false,waterlogged=false]", UseKind.None),
            ("minecraft:soul_campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]", UseKind.Campfire),
            ("minecraft:chest[facing=north,type=single,waterlogged=false]", UseKind.Container), ("minecraft:red_shulker_box[facing=up]", UseKind.Container),
            ("minecraft:oak_wall_sign[facing=north,waterlogged=false]", UseKind.Sign), ("minecraft:stone", UseKind.None), ("minecraft:crafting_table", UseKind.None),
        };
        var bad = table.Where(t => UseRules.Kind(t.state) != t.kind).Select(t => t.state + " -> " + UseRules.Kind(t.state)).ToList();
        check(bad.Count == 0, "UseRules.Kind matches the server's InteractRules.kind table" + (bad.Count > 0 ? ": " + string.Join("; ", bad) : ""));
        check(UseRules.Kind("minecraft:iron_door[facing=north,half=lower,hinge=left,open=false,powered=false]", ironDoors: false) == UseKind.None, "iron doors off: not usable");

        var w = new InteractWelcome { Allowed = true };
        const string stone = "minecraft:stone", meal = "item:minecraft:bone_meal";
        check(UseRules.Decide(false, Door, stone, false, TriggerMode.Use, w) == LeftAction.Interact, "right-click order: a door opens even with a block selected");
        check(UseRules.Decide(false, Door, stone, true, TriggerMode.Use, w) == LeftAction.Place, "grip + trigger (sneak) places against the door");
        check(UseRules.Decide(false, Door, meal, false, TriggerMode.Use, w) == LeftAction.Interact && UseRules.Decide(false, Door, meal, true, TriggerMode.Use, w) == LeftAction.Item,
              "with an item: the door first, sneak uses the item");
        check(UseRules.Decide(false, "minecraft:grass_block[snowy=false]", meal, false, TriggerMode.Use, w) == LeftAction.Item, "an item on a plain block: use it");
        check(UseRules.Decide(true, Door, stone, false, TriggerMode.Use, w) == LeftAction.Entity && UseRules.Decide(true, Door, stone, true, TriggerMode.Use, w) == LeftAction.Place,
              "a mob first; sneak skips it");
        check(UseRules.Decide(false, "minecraft:chest[facing=north,type=single,waterlogged=false]", stone, false, TriggerMode.Use, w) == LeftAction.Container &&
              UseRules.Decide(false, "minecraft:oak_sign[rotation=0,waterlogged=false]", stone, false, TriggerMode.Use, w) == LeftAction.Sign, "containers and signs go to their hooks");
        check(UseRules.Decide(false, Door, stone, false, TriggerMode.Place, w) == LeftAction.Place && UseRules.Decide(false, Door, stone, true, TriggerMode.Place, w) == LeftAction.Interact,
              "left_trigger place: the trigger places, the grip chord uses");
        check(UseRules.Decide(false, Door, stone, false, TriggerMode.Use, new InteractWelcome()) == LeftAction.Place &&
              UseRules.Decide(false, Door, meal, false, TriggerMode.Use, null) == LeftAction.None, "without interact_v1 the trigger only places");
        check(UseRules.Decide(true, null, stone, false, TriggerMode.Use, new InteractWelcome { Allowed = true, Entities = false }) == LeftAction.None, "mob actions off");
        check(UseRules.Decide(false, null, stone, false, TriggerMode.Use, w) == LeftAction.None, "a miss does nothing");
        check(UseRules.Outline(LeftAction.Entity) == OutlineKind.Entity && UseRules.Outline(LeftAction.Container) == OutlineKind.Interact &&
              UseRules.Outline(LeftAction.Item) == OutlineKind.Item && UseRules.Outline(LeftAction.Place) == OutlineKind.Place, "outline colours: yellow, cyan, orange, white");
        check(UseRules.Repeats(LeftAction.Item) && UseRules.Repeats(LeftAction.Place) && !UseRules.Repeats(LeftAction.Interact) && !UseRules.Repeats(LeftAction.Entity),
              "holding repeats placing and item uses, not doors or mobs");
        check(UseRules.FluidRay("item:minecraft:bucket") && !UseRules.FluidRay("item:minecraft:water_bucket") && !UseRules.FluidRay("minecraft:stone"), "only the empty bucket aims at fluids");
        check(UseRules.EntityOp(stone, "", out var i1, out _) == "info" && i1 == null && UseRules.EntityOp("item:minecraft:shears", "", out var i2, out _) == "use" && i2 == "minecraft:shears",
              "a mob: the info card with a block, use with an item");
        check(UseRules.EntityOp("item:minecraft:name_tag", "Rex", out var i3, out var t3) == "name" && i3 == null && t3 == "Rex", "the name tag names it");
        check(UseRules.LocalRefusal(LeftAction.Entity, "item:minecraft:name_tag", false, w, " ") != null && UseRules.LocalRefusal(LeftAction.Entity, "item:minecraft:name_tag", false, w, "Rex") == null,
              "an empty name tag is refused at once");
    }

    static WorldModel World(Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, World = "world", Origin = new Vector3Int(100, 60, -40), Dims = new Vector3Int(16, 16, 16) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[16 * 16 * 16];
        for (int y = 0; y < 16; y++) for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static void Raycast(Action<bool, string> check)
    {
        // A floor at y = 3, a pig standing on it at local (8, 4, 8), a player at (4, 4, 8), a wall at x = 12.
        var w = World((x, y, z) => y == 3 ? 1 : x == 12 && y < 10 ? 1 : 0, "minecraft:air", "minecraft:stone");
        var o = w.Origin;
        var pig = new EntityTarget { Uuid = "pig", Id = 7, Type = "minecraft:pig", Pos = new Vector3(o.x + 8.5f, o.y + 4, o.z + 8.5f), Width = 0.9f, Height = 0.9f };
        var alex = new EntityTarget { Uuid = "alex", Id = 3, Player = true, Name = "Alex", Pos = new Vector3(o.x + 4.5f, o.y + 4, o.z + 8.5f), Width = 0.6f, Height = 1.8f };
        var chick = new EntityTarget { Uuid = "chick", Type = "minecraft:chicken", Pos = new Vector3(o.x + 2.5f, o.y + 4, o.z + 2.5f), Width = 0.2f, Height = 0.3f };
        var all = new List<EntityTarget> { pig, alex, chick };
        Vector3 lo = Zero, hi = new Vector3(16, 16, 16);
        // Looking down the x axis at the pig's middle from x = 0: Alex is in front, then the pig.
        var from = new Vector3(0, 4.5f, 8.5f);
        var e = EntityRaycast.Cast(all, o, from, Right, 32, lo, hi);
        check(e.Hit && e.Target.Uuid == "alex" && Math.Abs(e.Distance - 4.2f) < 1e-3, $"entity ray: the nearest (Alex) wins ({e.Target.Uuid} at {e.Distance})");
        e = EntityRaycast.Cast(all, o, from, Right, 32, lo, hi, skip: "alex");
        check(e.Hit && e.Target.Uuid == "pig" && Math.Abs(e.Distance - 8.05f) < 1e-3, "entity ray: the carried one is skipped");
        var b = BlockRaycast.Cast(w, from, Right, 32, lo, hi);
        check(b.Hit && b.Block.x == 12 && EntityRaycast.EntityFirst(e, b), "the pig in front of the wall wins over the wall");
        var behind = EntityRaycast.Cast(new List<EntityTarget> { new EntityTarget { Uuid = "far", Pos = new Vector3(o.x + 14.5f, o.y + 4, o.z + 8.5f), Width = 1, Height = 1 } },
                                        o, from, Right, 32, lo, hi);
        check(behind.Hit && !EntityRaycast.EntityFirst(behind, b), "a mob behind the wall loses to the wall");
        e = EntityRaycast.Cast(all, o, new Vector3(2.5f, 10, 2.5f), Down, 32, lo, hi);
        check(e.Hit && e.Target.Uuid == "chick" && Math.Abs(e.Point.y - (4 + EntityRaycast.MinSize)) < 1e-3, "small mobs get a half-block box");
        e = EntityRaycast.Cast(all, o, from, Right, 32, new Vector3(6, 0, 0), hi);
        check(e.Hit && e.Target.Uuid == "pig", "outside the visible window doesn't count (Alex is cut off)");
        check(!EntityRaycast.Cast(all, o, from, Right, 3, lo, hi).Hit, "beyond the ray's length: no hit");
        var down = BlockRaycast.Cast(w, new Vector3(5.25f, 9, 5.75f), Down, 32, lo, hi);
        var drop = EntityRaycast.DropPoint(w, down);
        check(drop.HasValue && Math.Abs(drop.Value.x - 5.25f) < 1e-4 && drop.Value.y == 4 && Math.Abs(drop.Value.z - 5.75f) < 1e-4, "drop point: on top, where the ray points");
        var side = EntityRaycast.DropPoint(w, b);
        check(side.HasValue && side.Value.x == 11.5f && side.Value.y == 4 && side.Value.z == 8.5f, "drop point: in front of a wall, bottom centre of the cell");
        check(alex.Label == "Alex" && pig.Label == "Pig" && new EntityTarget { Type = "minecraft:wolf", Name = "Rex" }.Label == "Wolf \"Rex\"", "target labels");

        // The empty bucket stops at a water source, not at flowing water; other items look through both.
        var pond = World((x, y, z) => y == 3 ? 1 : y == 4 && x == 8 ? 2 : y == 4 && x == 9 ? 3 : 0, "minecraft:air", "minecraft:stone", "minecraft:water[level=0]", "minecraft:water[level=2]");
        var src = BlockRaycast.Cast(pond, new Vector3(8.5f, 9, 8.5f), Down, 32, lo, hi, fluidSources: true);
        var through = BlockRaycast.Cast(pond, new Vector3(8.5f, 9, 8.5f), Down, 32, lo, hi);
        var flowing = BlockRaycast.Cast(pond, new Vector3(9.5f, 9, 8.5f), Down, 32, lo, hi, fluidSources: true);
        check(src.Hit && src.Block.y == 4 && through.Hit && through.Block.y == 3 && flowing.Block.y == 3, "fluid ray: a source stops the empty bucket, flowing water doesn't");
    }

    static void Menu(Action<bool, string> check, Func<string, string> fixture)
    {
        var m = new BlockMenuModel();
        m.RestoreTab("eggs");   // prefs.json, before the server's lists
        m.SetRegistry(BlockRegistry.From(Parse(File.ReadAllText(fixture("block_registry.json")))));
        check(m.UseTab == -1 && m.SearchTab == 6 && m.InfoTab == 7, "menu: no item tabs before item_registry");
        var items = ItemRegistry.From(Parse(File.ReadAllText(fixture("item_registry.json"))));
        m.SetItems(items);
        check(m.UseTab == 6 && m.EggsTab == 7 && m.SearchTab == 8 && m.InfoTab == 9 && m.Tab == m.EggsTab, "menu: Use and Eggs after the categories; the saved Eggs tab is restored");
        check(m.TabCaption(m.UseTab) == "Use" && m.TabCaption(m.EggsTab) == "Eggs" && m.TabCaption(3) == "Func." && m.TabId(m.UseTab) == "use",
              "menu: tab captions (functional blocks are Func. now)");
        check(m.Items.Count == 3 && m.Items[1].State == "item:minecraft:pig_spawn_egg", "menu: the Eggs tab lists the spawn eggs");
        var L = m.Layout();
        check(L.Items.Count(i => i.Part == MenuPart.Tab) == 10 && L.Width == BlockMenuModel.Width, "menu: ten tabs fit the panel");
        m.SetTab(m.UseTab);
        m.Choose("item:minecraft:bone_meal");
        check(m.Selected == "item:minecraft:bone_meal" && m.Hotbar[0] == "item:minecraft:bone_meal" && m.LabelOf(m.Selected) == "Bone Meal", "menu: an item goes into the hotbar");
        check(!m.NameTagField && m.ActiveKeys == null, "menu: no name tag field for other items");
        m.Choose("item:minecraft:name_tag");
        check(m.NameTagField && m.ActiveKeys == m.NameTagKeys && m.GridRows == BlockMenuModel.SearchRows && m.Layout().NameTagField.W > 0, "menu: the name tag shows its text field and keyboard");
        foreach (char ch in "rex") m.Key(KeyboardModel.Letters.IndexOf(ch));
        var done = m.Key(BlockMenuModel.KeyEnter);
        check(m.NameTag == "rex" && m.Note.Contains("rex") && done.Kind == MenuActionKind.Typed, "menu: typing names the tag");
        check(m.Picked("item:minecraft:sheep_spawn_egg") && m.Selected == "item:minecraft:sheep_spawn_egg", "menu: B on a sheep picks its egg");
        check(!m.Picked("item:minecraft:wither_spawn_egg") && m.Note.Contains("Wither Spawn Egg"), "menu: an egg the server doesn't offer isn't picked");
        m.SetTab(m.SearchTab);
        foreach (char ch in "egg") m.Key(KeyboardModel.Letters.IndexOf(ch));
        check(m.Items.Count == 3 && m.Items.All(e => e.State.EndsWith("_spawn_egg")), "menu: search finds items too");
        var fresh = new BlockMenuModel();
        fresh.Restore(new List<string> { "item:minecraft:shears", "minecraft:stone" }, 0, "item:minecraft:shears");
        check(fresh.Selected == "item:minecraft:shears" && fresh.Hotbar.Count == 2, "prefs: a hotbar with items comes back");
    }

    static void Editor(Action<bool, string> check)
    {
        var w = World((x, y, z) => y == 3 ? 1 : 0, "minecraft:air", "minecraft:grass_block[snowy=false]");
        var ed = new BlockEditor { ServerAllows = true, Selected = "item:minecraft:bone_meal" };
        var h = BlockRaycast.Cast(w, new Vector3(5.25f, 9, 6.75f), Down, 32, Zero, new Vector3(16, 16, 16));
        var plan = ed.Plan(w, h, Down, 0);
        check(!plan.Ok && plan.State == null && plan.Why == "", "an item has no placement (no ghost)");
        var msg = ed.Interact(w, h, "minecraft:bone_meal", false, Down);
        var hit = (List<object>)msg["hit"];
        check(msg.S("type") == "interact" && Json.Write(msg["pos"]) == "[105,63,-34]" && msg.S("face") == "up" && msg.S("item") == "minecraft:bone_meal" &&
              Math.Abs((double)hit[0] - 0.25) < 1e-3 && Math.Abs((double)hit[1] - 1) < 1e-3 && Math.Abs((double)hit[2] - 0.75) < 1e-3,
              $"BlockEditor.Interact: world pos, face, hit point inside the block ({Json.Write(msg)})");
        check(ed.Result == "used bone meal on grass block" && w.State(w.Get(5, 3, 6)) == "minecraft:grass_block[snowy=false]", "no optimistic change for an interact");
        ed.OnError(w, Parse("{\"code\":\"interact_denied\",\"message\":\"bone meal does nothing to stone\",\"op\":\"interact\",\"pos\":[105,63,-34]}"));
        check(ed.Result == "bone meal does nothing to stone" && ed.Pending.Count == 0, "an interact refusal only reports");
    }

    /// <summary>
    /// --live-interact URL: the app's own classes against a running plugin on a flat world (grass at y = -61): moves the
    /// region to x/z 0..31 at the surface, places a door with block_edit, opens it with the left trigger's decision and
    /// BlockEditor.Interact, spawns a pig with its egg, aims at it with EntityRaycast, asks for its info card, and kills it
    /// from the card. The door is broken again at the end.
    /// </summary>
    public static void Live(string url, Action<bool, string> check)
    {
        var link = new LinkClient(url, "");
        link.Start();
        var w = new WorldModel();
        var ed = new BlockEditor();
        var store = new EntityStore();
        var asm = new SnapshotAssembler();
        var card = new EntityCardModel();
        InteractWelcome welcome = null;
        ItemRegistry items = null;
        EntityInfoMessage info = null;
        var errors = new List<string>();
        var t0 = DateTime.UtcNow;
        double Now() => (DateTime.UtcNow - t0).TotalSeconds;
        bool Wait(Func<bool> done, double seconds = 6)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                while (link.Inbound.TryDequeue(out var m))
                    switch (m.S("type"))
                    {
                        case "welcome": ed.ServerAllows = m.A("caps")?.Contains(Protocol.CapEdit) == true; welcome = InteractWelcome.From(m); break;
                        case ItemRegistry.Type: items = ItemRegistry.From(m); break;
                        case "chunk_snapshot":
                            asm.Offer(m, w, new HashSet<Vector3Int>());
                            if (!asm.Busy) break;
                            var res = asm.Wait();
                            if (res.Error == null) { w.ApplySnapshot(res.Snapshot); ed.OnSnapshot(w, res.Delta ? res.ListedKeys : null); }
                            break;
                        case "block_update": ed.OnBlockUpdate(w, m); w.ApplyBlockUpdate(m); break;
                        case "entities": store.Apply(m, null); break;
                        case "entity_info": info = EntityInfoMessage.From(m); card.Show(info, welcome, Now()); break;
                        case "error": errors.Add($"{m.S("code")}: {m.S("message")}"); ed.OnError(w, m); break;
                    }
                if (done()) return true;
                Thread.Sleep(5);
            }
            return false;
        }
        check(Wait(() => w.Loaded && link.Connected && welcome != null && items != null), "live interact: welcome, item_registry and a snapshot");
        check(welcome != null && welcome.Allowed && items?.Find("item:minecraft:pig_spawn_egg") != null, "live interact: interact_v1 granted, pig egg offered");
        if (welcome == null || !welcome.Allowed) { link.Dispose(); return; }
        var origin = new Vector3Int(0, -64, 0);
        link.Send(Protocol.RegionRequest(w.World, origin, new Vector3Int(32, 64, 32)));
        check(Wait(() => w.Loaded && w.Origin == origin), $"live interact: region on the surface ({w.Origin})");
        var down = new Vector3(0, -1, 0);
        var lo = new Vector3(0, 0, 0); var hi = (Vector3)w.Dims;
        BlockHit Ray(float x, float z) => BlockRaycast.Cast(w, new Vector3(x, w.Dims.y - 0.5f, z), down, w.Dims.y, lo, hi);
        var ground = Ray(10.5f, 10.5f);
        check(ground.Hit && ground.Block.y == 3 && w.State(w.Get(10, 3, 10)).StartsWith("minecraft:grass_block"), "live interact: grass at y=-61");
        var doorCell = new Vector3Int(10, 4, 10);
        link.Send(Protocol.BlockEdit("place", w.Origin + doorCell, Door, "up"));
        check(Wait(() => w.State(w.Get(10, 4, 10)).StartsWith("minecraft:oak_door") && w.State(w.Get(10, 5, 10)).Contains("half=upper")), "live interact: a door placed");
        var onDoor = Ray(10.5f, 10.5f);
        string doorState = w.State(w.Get(onDoor.Block.x, onDoor.Block.y, onDoor.Block.z));
        var act = UseRules.Decide(false, doorState, "minecraft:stone", false, TriggerMode.Use, welcome);
        check(act == LeftAction.Interact, $"live interact: the left trigger would use the door ({act})");
        link.Send(ed.Interact(w, onDoor, null, false, down));
        check(Wait(() => BlockEditRules.Prop(w.State(w.Get(10, 4, 10)), "open") == "true" && BlockEditRules.Prop(w.State(w.Get(10, 5, 10)), "open") == "true"),
              "live interact: the door opened (both halves)");
        // A pig from its egg on the grass a few blocks away, found with the app's entity store and ray.
        var known = new HashSet<int>(store.All.Keys);
        var grass = Ray(16.5f, 20.5f);
        link.Send(ed.Interact(w, grass, "minecraft:pig_spawn_egg", false, down));
        EntityInfo pig = null;
        check(Wait(() => (pig = store.All.Values.FirstOrDefault(e => e.Type == "minecraft:pig" && !known.Contains(e.Id))) != null), "live interact: the egg spawned a pig");
        if (pig != null)
        {
            var target = new EntityTarget { Uuid = pig.Uuid, Id = pig.Id, Type = pig.Type, Name = pig.Name, Pos = pig.Pos, Width = pig.Width, Height = pig.Height };
            var local = pig.Pos - (Vector3)w.Origin;
            var hitPig = EntityRaycast.Cast(new List<EntityTarget> { target }, w.Origin, new Vector3(local.x, w.Dims.y - 0.5f, local.z), down, w.Dims.y, lo, hi);
            var under = BlockRaycast.Cast(w, new Vector3(local.x, w.Dims.y - 0.5f, local.z), down, w.Dims.y, lo, hi);
            check(hitPig.Hit && EntityRaycast.EntityFirst(hitPig, under) && UseRules.Decide(true, null, "minecraft:stone", false, TriggerMode.Use, welcome) == LeftAction.Entity,
                  "live interact: the ray from above hits the pig before the grass");
            string op = UseRules.EntityOp("minecraft:stone", "", out var item, out var text);
            link.Send(InteractProtocol.EntityOp(pig.Uuid, op, item: item, text: text));
            check(Wait(() => info != null && info.Uuid == pig.Uuid) && card.Open && info.Lines()[0] == "Pig" && card.Buttons.Any(b => b.Op == "kill"),
                  $"live interact: the info card ({(info == null ? "none" : string.Join(" | ", info.Lines()))})");
            var r = card.ButtonRect(card.Buttons.FindIndex(b => b.Op == "kill"));
            card.Point(r.CX, r.CY, true);
            string kill = card.Click(Now());
            link.Send(InteractProtocol.EntityOp(pig.Uuid, kill));
            check(kill == "kill" && Wait(() => !store.All.ContainsKey(pig.Id)), "live interact: killed from the card");
        }
        check(errors.Count == 0, "live interact: no refusals" + (errors.Count > 0 ? ": " + string.Join("; ", errors) : ""));
        link.Send(Protocol.BlockEdit("break", w.Origin + doorCell, null, "up"));
        Wait(() => w.State(w.Get(10, 4, 10)) == "minecraft:air", 3);
        link.Dispose();
    }

    static void Card(Action<bool, string> check, Func<string, string> fixture)
    {
        var c = new EntityCardModel();
        var w = new InteractWelcome { Allowed = true };
        var wolf = EntityInfoMessage.From(Parse(File.ReadAllText(fixture("entity_info.json"))));
        c.Show(wolf, w, 0);
        check(c.Open && string.Join(",", c.Buttons.Select(b => b.Op)) == "kill,remove,freeze,glow,close", "card: a mob's actions");
        var r = c.ButtonRect(2);
        c.Point(r.CX, r.CY, true);
        check(c.CapturesRight && c.Hover == 2 && c.Click(1) == "freeze" && c.Open, "card: clicking a button gives its op");
        var player = EntityInfoMessage.From(Parse("{\"uuid\":\"" + U + "\",\"id\":3,\"entity_type\":\"minecraft:player\",\"name\":\"Alex\",\"pos\":[1,64,2],\"player\":true,\"glowing\":false}"));
        c.Show(player, w, 2);
        check(string.Join(",", c.Buttons.Select(b => b.Op)) == "glow,close" && c.Lines()[0] == "Alex", "card: players get glow only");
        c.Show(player, new InteractWelcome { Allowed = true, HarmPlayers = true }, 2);
        check(c.Buttons.Any(b => b.Op == "kill"), "card: kill for players with interact.harm-players");
        c.Point(c.ButtonRect(c.Buttons.Count - 1).CX, c.ButtonRect(c.Buttons.Count - 1).CY, true);
        check(c.Click(3) == "close" && !c.Open && !c.CapturesRight, "card: Close closes");
        c.Show(wolf, w, 10);
        c.Tick(20, true);
        bool stays = c.Open;
        c.Tick(20, false);
        check(stays && !c.Open, "card: closes when the mob is gone");
        c.Show(wolf, w, 10);
        c.Tick(10 + EntityCardModel.IdleSeconds + 1, true);
        check(!c.Open, "card: closes when idle");
    }
}
