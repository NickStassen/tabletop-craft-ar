// [sound] Engine-free tests for spatial sound and asset delivery (PROTOCOL.md "Sound", "Asset delivery"): sounds.json
// parsing and vanilla's variant pick, block sound groups, the sounds of block state changes, what the director infers
// from actions, block updates, entities, players, rain and fluids, the voice limits and fall-off, sounds.zip, and the
// asset download (offer, frames, resume, checksum) against an in-memory server. With --sounds sounds.zip (the user's
// own, never committed) every hand-written sound id is checked against the real sounds.json; --live-assets URL TOKEN
// downloads from a running plugin.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class SoundTests
{
    static Action<bool, string> Check;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string[] args)
    {
        Check = check;
        Defs();
        Groups(fixture);
        Transitions();
        Director(fixture);
        Mixer();
        Pack();
        Assets(fixture);
        int si = Array.IndexOf(args, "--sounds");
        if (si >= 0 && si + 1 < args.Length) RealSounds(args[si + 1]);
        else Console.WriteLine("SKIP real sounds.json checks: pass --sounds PATH (your sounds.zip from tools/sounds/PackSounds.java)");
    }

    static Dictionary<string, object> J(string s) => (Dictionary<string, object>)Json.Parse(s);
    static Dictionary<string, object> Fx(Func<string, string> fixture, string n) => J(File.ReadAllText(fixture(n)));

    // ---------- sounds.json ----------

    const string Sample = @"{
      ""block.test.break"": {""sounds"": [""dig/a1"", ""dig/a2"", {""name"": ""dig/a3"", ""weight"": 2, ""volume"": 0.5, ""pitch"": 1.2}]},
      ""block.test.loud"": {""sounds"": [{""name"": ""block.test.break"", ""type"": ""event"", ""volume"": 0.8, ""pitch"": 0.5}]},
      ""music.test"": {""sounds"": [{""name"": ""music/game/x"", ""stream"": true, ""attenuation_distance"": 24}]},
      ""mod.thing"": {""sounds"": [""mymod:thing/one""]},
      ""minecraft:entity.test.ambient"": {""sounds"": [""mob/test/say""]},
      ""empty.one"": {""sounds"": []}
    }";

    static void Defs()
    {
        var d = SoundDefs.Parse(Sample);
        Check(d.Count == 6 && d.Has("block.test.break") && d.Has("minecraft:block.test.break") && d.Has("entity.test.ambient") && !d.Has("empty.one") && !d.Has("nope"),
              "sounds.json: events parsed, minecraft: namespace optional, empty events unplayable");
        Check(d.Weight("block.test.break") == 4 && d.Weight("block.test.loud") == 4, "sounds.json: weights (default 1; an event entry weighs its target's total)");
        Check(SoundDefs.FilePath("dig/a1") == "assets/minecraft/sounds/dig/a1.ogg" && SoundDefs.FilePath("mymod:thing/one") == "assets/mymod/sounds/thing/one.ogg",
              "sounds.json: file names map to resource-pack paths");
        var rng = new System.Random(7);
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < 8000; i++) { d.Pick("block.test.break", rng, out var c); counts[c.File] = counts.TryGetValue(c.File, out var n) ? n + 1 : 1; }
        int a3 = counts["assets/minecraft/sounds/dig/a3.ogg"], a1 = counts["assets/minecraft/sounds/dig/a1.ogg"];
        Check(Math.Abs(a3 / 8000.0 - 0.5) < 0.03 && Math.Abs(a1 / 8000.0 - 0.25) < 0.03, $"pick: chance follows weight (a3 {a3}, a1 {a1} of 8000)");
        bool sawA3 = false, ok = true;
        for (int i = 0; i < 200; i++)
        {
            d.Pick("block.test.loud", rng, out var c);
            if (c.File.EndsWith("a3.ogg")) { sawA3 = true; ok &= Near(c.Volume, 0.4f) && Near(c.Pitch, 0.6f); }
            else ok &= Near(c.Volume, 0.8f) && Near(c.Pitch, 0.5f);
        }
        Check(sawA3 && ok, "pick: an event entry multiplies the chosen sound's volume and pitch");
        d.Pick("music.test", rng, out var mu);
        Check(mu.Stream && mu.Attenuation == 24 && !d.Pick("nope", rng, out _), "pick: stream and attenuation_distance; unknown events give nothing");
        Check(d.Files("block.test.loud").Count == 3, "files of an event follow event entries (preload)");
    }

    static bool Near(float a, float b, float e = 1e-3f) => Math.Abs(a - b) <= e;

    // ---------- block sound groups ----------

    static SoundDefs Families(params string[] fams)
    {
        var sb = new StringBuilder("{");
        foreach (var f in fams)
            foreach (var w in new[] { "break", "step", "place", "hit", "fall" }) sb.Append($"\"block.{f}.{w}\":{{\"sounds\":[\"x/{f}_{w}\"]}},");
        sb.Length--; sb.Append('}');
        return SoundDefs.Parse(sb.ToString());
    }

    static void Groups(Func<string, string> fixture)
    {
        var g = BlockSounds.From(Fx(fixture, "sound_groups.json"));
        Check(g.Count == 7 && g.Groups == 4, $"sound_groups fixture: 7 blocks in 4 groups ({g.Count}, {g.Groups})");
        var log = g.Of("minecraft:oak_log[axis=y]", null);
        var anvil = g.Of("minecraft:anvil[facing=north]", null);
        Check(log.Step == "block.wood.step" && anvil.Volume == 0.3f && anvil.Break == "block.anvil.break", "sound_groups: a state finds its block's group (wood, a quiet anvil)");
        var brk = SoundRules.Break(anvil);
        Check(Near(brk.Volume, 0.65f) && Near(brk.Pitch, 0.8f) && Near(SoundRules.Step(g.Of("minecraft:stone", null)).Volume, 0.15f) && Near(SoundRules.Hit(log).Pitch, 0.5f),
              "vanilla formulas: break (v+1)/2 at 0.8 pitch, step v x 0.15, hit at half pitch");
        var defs = Families("stone", "wood", "glass", "gravel", "grass", "nether_wood", "copper", "wool", "deepslate_bricks", "sand", "lantern");
        Check(g.Of("minecraft:glass_pane", defs).Break == "block.glass.break" && g.Of("minecraft:spruce_planks", defs).Break == "block.wood.break"
              && g.Of("minecraft:crimson_door[half=lower]", defs).Break == "block.nether_wood.break" && g.Of("minecraft:waxed_oxidized_cut_copper", defs).Break == "block.copper.break"
              && g.Of("minecraft:coarse_dirt", defs).Break == "block.gravel.break" && g.Of("minecraft:red_wool", defs).Break == "block.wool.break"
              && g.Of("minecraft:deepslate_brick_wall", defs).Break == "block.deepslate_bricks.break" && g.Of("minecraft:lantern[hanging=false]", defs).Break == "block.lantern.break"
              && g.Of("minecraft:stone_brick_stairs", defs).Break == "block.stone.break" && g.Of("minecraft:mystery_block", defs).Break == "block.stone.break",
              "without the server's table: families guessed from the id, else stone");
        Check(g.Of("minecraft:water[level=0]", defs).Silent && g.Of("minecraft:air", defs).Silent, "water and air are silent");
    }

    // ---------- block state changes ----------

    static void Transitions()
    {
        var rng = new System.Random(1);
        string T(string a, string b) => SoundRules.Transition(a, b, rng)?.Event;
        Check(T("minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]", "minecraft:oak_door[facing=north,half=lower,hinge=left,open=true,powered=false]") == "block.wooden_door.open"
              && T("minecraft:oak_door[facing=north,half=upper,hinge=left,open=false,powered=false]", "minecraft:oak_door[facing=north,half=upper,hinge=left,open=true,powered=false]") == null,
              "door: opens with its family's sound, once (the lower half)");
        Check(T("minecraft:iron_door[half=lower,open=true]", "minecraft:iron_door[half=lower,open=false]") == "block.iron_door.close"
              && T("minecraft:waxed_copper_door[half=lower,open=false]", "minecraft:waxed_copper_door[half=lower,open=true]") == "block.copper_door.open",
              "door families: iron, copper");
        Check(T("minecraft:spruce_trapdoor[open=false]", "minecraft:spruce_trapdoor[open=true]") == "block.wooden_trapdoor.open"
              && T("minecraft:oak_fence_gate[open=false]", "minecraft:oak_fence_gate[open=true]") == "block.fence_gate.open"
              && T("minecraft:cherry_fence_gate[open=true]", "minecraft:cherry_fence_gate[open=false]") == "block.cherry_wood_fence_gate.close",
              "trapdoors and fence gates");
        var lever = SoundRules.Transition("minecraft:lever[powered=false]", "minecraft:lever[powered=true]", rng).Value;
        Check(lever.Event == "block.lever.click" && Near(lever.Pitch, 0.6f) && Near(lever.Volume, 0.3f), "lever: click at 0.6 pitch when it turns on");
        Check(T("minecraft:stone_button[powered=false]", "minecraft:stone_button[powered=true]") == "block.stone_button.click_on"
              && T("minecraft:birch_button[powered=true]", "minecraft:birch_button[powered=false]") == "block.wooden_button.click_off"
              && T("minecraft:heavy_weighted_pressure_plate[power=0]", "minecraft:heavy_weighted_pressure_plate[power=3]") == "block.metal_pressure_plate.click_on"
              && T("minecraft:heavy_weighted_pressure_plate[power=3]", "minecraft:heavy_weighted_pressure_plate[power=5]") == null,
              "buttons and pressure plates (a weighted plate clicks only on and off)");
        Check(T("minecraft:piston[extended=false,facing=up]", "minecraft:piston[extended=true,facing=up]") == "block.piston.extend"
              && T("minecraft:comparator[mode=compare]", "minecraft:comparator[mode=subtract]") == "block.comparator.click"
              && T("minecraft:candle[candles=1,lit=true]", "minecraft:candle[candles=1,lit=false]") == "block.candle.extinguish"
              && T("minecraft:campfire[lit=true]", "minecraft:campfire[lit=false]") == "entity.generic.extinguish_fire"
              && T("minecraft:composter[level=8]", "minecraft:composter[level=0]") == "block.composter.empty",
              "pistons, comparators, candles, campfires, composters");
        Check(T("minecraft:oak_log[axis=y]", "minecraft:stripped_oak_log[axis=y]") == "item.axe.strip"
              && T("minecraft:weathered_copper", "minecraft:exposed_copper") == "item.axe.scrape"
              && T("minecraft:exposed_copper", "minecraft:weathered_copper") == null
              && T("minecraft:cut_copper", "minecraft:waxed_cut_copper") == "item.honeycomb.wax_on"
              && T("minecraft:waxed_cut_copper", "minecraft:cut_copper") == "item.axe.wax_off"
              && T("minecraft:grass_block[snowy=false]", "minecraft:farmland[moisture=0]") == "item.hoe.till"
              && T("minecraft:dirt", "minecraft:dirt_path") == "item.shovel.flatten"
              && T("minecraft:pumpkin", "minecraft:carved_pumpkin[facing=north]") == "block.pumpkin.carve",
              "tools: strip, scrape (weathering is silent), wax on and off, till, path, carve");
        Check(T("minecraft:stone", "minecraft:air") == null && T("minecraft:air", "minecraft:stone") == null && T("minecraft:repeater[delay=1]", "minecraft:repeater[delay=2]") == null,
              "a plain break or place in a block_update makes no sound (they come from actions)");
    }

    // ---------- the director ----------

    static readonly string[] Needed =
    {
        "block.stone.break", "block.stone.step", "block.stone.place", "block.stone.hit", "block.grass.step", "block.grass.break", "block.wood.place", "block.wood.break",
        "block.note_block.harp", "block.chest.open", "entity.generic.explode", "music_disc.cat", "block.wooden_door.open", "entity.zombie.step", "entity.zombie.ambient",
        "entity.zombie.death", "entity.zombie.hurt", "entity.cow.ambient", "entity.cow.hurt", "entity.player.hurt", "entity.player.death", "entity.item.pickup",
        "entity.experience_orb.pickup", "entity.lightning_bolt.thunder", "entity.lightning_bolt.impact", "entity.tnt.primed", "weather.rain", "block.water.ambient",
        "block.lava.pop", "block.lava.ambient", "block.fire.ambient", "block.note_block.pling", "entity.generic.hurt", "entity.generic.death",
    };

    static SoundDefs NeededDefs()
    {
        var sb = new StringBuilder("{");
        foreach (var k in Needed) sb.Append($"\"{k}\":{{\"sounds\":[\"x/{k}\"]}},");
        sb.Length--; sb.Append('}');
        return SoundDefs.Parse(sb.ToString());
    }

    /// <summary>A 32 x 8 x 32 region at (100, 60, -40): grass at local y 0 (world 60), air above.</summary>
    static WorldModel Field(params string[] extra)
    {
        var w = new WorldModel { SnapshotId = 1, World = "world", Origin = new Vector3Int(100, 60, -40), Dims = new Vector3Int(32, 8, 32) };
        w.Palette.AddRange(new[] { "minecraft:air", "minecraft:grass_block[snowy=false]" });
        w.Palette.AddRange(extra);
        w.Grid = new ushort[32 * 8 * 32];
        for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) w.Grid[w.Index(x, 0, z)] = 1;
        return w;
    }

    static SoundDirector NewDirector(Func<string, string> fixture, int seed = 3)
    {
        var d = new SoundDirector { Defs = NeededDefs(), Groups = BlockSounds.From(Fx(fixture, "sound_groups.json")), Rng = new System.Random(seed) };
        return d;
    }

    static List<SoundRequest> Take(SoundDirector d) { var l = new List<SoundRequest>(d.Out); d.Out.Clear(); return l; }

    static Dictionary<string, object> Ents(bool full, string entries, string removed = null) =>
        J($"{{\"type\":\"entities\",\"full\":{(full ? "true" : "false")},\"entities\":[{entries}]{(removed != null ? ",\"removed\":[" + removed + "]" : "")}}}");

    static void Director(Func<string, string> fixture)
    {
        var w = Field();
        var d = NewDirector(fixture);
        var acts = new List<GameAction>();
        GameAction.Parse(Fx(fixture, "actions_sound.json"), acts);
        d.OnActions(acts, w, 10);
        var o = Take(d);
        var harp = o.FirstOrDefault(r => r.Event == "block.note_block.harp");
        var disc = o.FirstOrDefault(r => r.Event == "music_disc.cat");
        var brk = o.FirstOrDefault(r => r.Event == "block.stone.break");
        Check(o.Count == 6 && Near(harp.Volume, 3f) && Near(harp.Pitch, 1.189f) && harp.Pos.x == 105.5f && harp.Priority == SoundPriority.Event && harp.HasBlock && harp.Block == new Vector3Int(105, 64, -36)
              && disc.Stream && o.Any(r => r.Stop && r.Block == new Vector3Int(103, 64, -38)) && Near(brk.Volume, 1f) && Near(brk.Pitch, 0.8f) && brk.Pos.x == 106.5f,
              $"actions: the server's sounds (pos, volume, pitch, stream), a jukebox stop and a stone break ({string.Join("; ", o)})");
        d.OnActions(new List<GameAction> { new GameAction { Kind = ActionKind.Break, Id = -1, Pos = new Vector3Int(101, 60, -39), Block = "minecraft:stone" } }, w, 10);
        Check(Take(d).Count == 0, "an explosion's broken blocks add no break sounds");
        var empty = new SoundDirector { Groups = d.Groups };
        empty.OnActions(acts, w, 10);
        Check(empty.Out.Count == 0, "no sounds.zip: nothing is requested");

        // block_update transitions and the dedupe with the server's own sound for the headset's use
        var dw = Field("minecraft:oak_door[facing=north,half=lower,hinge=left,open=false,powered=false]", "minecraft:oak_door[facing=north,half=upper,hinge=left,open=false,powered=false]");
        dw.Grid[dw.Index(5, 1, 5)] = 2; dw.Grid[dw.Index(5, 2, 5)] = 3;
        var upd = J("{\"type\":\"block_update\",\"snapshot_id\":1,\"palette_add\":[\"minecraft:oak_door[facing=north,half=lower,hinge=left,open=true,powered=false]\",\"minecraft:oak_door[facing=north,half=upper,hinge=left,open=true,powered=false]\"],\"changes\":[[5,1,5,4],[5,2,5,5]]}");
        d.OnBlockUpdate(dw, upd, 20);
        o = Take(d);
        Check(o.Count == 1 && o[0].Event == "block.wooden_door.open" && o[0].Block == new Vector3Int(105, 61, -35), $"block_update: a door opening plays once at its lower half ({string.Join("; ", o)})");
        d.OnActions(new List<GameAction> { new GameAction { Kind = ActionKind.Sound, Sound = "block.wooden_door.open", At = new Vector3(105.5f, 61.5f, -34.5f), Pos = new Vector3Int(105, 61, -35), Volume = 1, Pitch = 1 } }, dw, 20.2);
        Check(Take(d).Count == 0 && d.Deduped == 1, "the same sound at the same block within a second plays once (server sound vs inferred)");
        d.OnBlockUpdate(dw, J("{\"snapshot_id\":9,\"changes\":[[5,1,5,4]]}"), 30);
        Check(Take(d).Count == 0, "a block_update for another snapshot is ignored");

        // the headset's own edits
        d.OnOwnEdit("place", new Vector3Int(110, 61, -30), "minecraft:oak_planks", 40);
        o = Take(d);
        Check(o.Count == 1 && o[0].Event == "block.wood.place" && o[0].Priority == SoundPriority.Own, "own edit: the place sound now, at the highest priority");

        // entities: a zombie walking (own step sound), a cow (block steps), ambient, hurt, death
        var e = new SoundDirector { Defs = d.Defs, Groups = d.Groups, Rng = new System.Random(5) };
        e.OnEntities(Ents(true, "{\"id\":1,\"type\":\"minecraft:zombie\",\"pos\":[105.5,61,-35.5],\"body_yaw\":0,\"on_ground\":true,\"size\":[0.6,1.95]}," +
                                "{\"id\":2,\"type\":\"minecraft:cow\",\"pos\":[110.5,61,-30.5],\"body_yaw\":0,\"on_ground\":true,\"size\":[0.9,1.4]}," +
                                "{\"id\":3,\"type\":\"minecraft:item\",\"pos\":[115.5,61,-25.5],\"item\":\"minecraft:dirt\",\"size\":[0.25,0.25]}"), w, 0);
        Check(Take(e).Count == 0, "entities: a full list (a new region) makes no sound by itself");
        int zSteps = 0, cSteps = 0;
        for (int i = 1; i <= 20; i++)
        {
            e.OnEntities(Ents(false, $"{{\"id\":1,\"pos\":[{105.5 + i * 0.2:F2},61,-35.5]}},{{\"id\":2,\"pos\":[{110.5 + i * 0.2:F2},61,-30.5]}}"), w, i * 0.1);
            foreach (var r in Take(e)) { if (r.Event == "entity.zombie.step") zSteps++; if (r.Event == "block.grass.step") cSteps++; }
        }
        Check(zSteps == 2 && cSteps == 2, $"steps: 4 blocks of walking = 2 steps (a step per 1/0.6 blocks); zombies use their own, cows the grass under them ({zSteps}, {cSteps})");
        e.OnEntities(Ents(false, "{\"id\":2,\"pos\":[115.5,61,-30.5],\"on_ground\":false}"), w, 3);
        Check(Take(e).Count == 0, "no steps in the air (or a teleport)");
        int ambient = 0;
        for (int t = 0; t < 600; t++) { e.Tick(4 + t * 0.05, w, new Vector3Int(0, 0, 0), new Vector3Int(32, 8, 32), 0); ambient += Take(e).Count(r => r.Event.EndsWith(".ambient")); }
        Check(ambient >= 4 && ambient <= 16, $"ambient: two mobs over 30 s say something every ~6 s each ({ambient})");
        e.OnActions(new List<GameAction> { new GameAction { Kind = ActionKind.Hurt, Id = 2 } }, w, 40);
        o = Take(e);
        Check(o.Count == 1 && o[0].Event == "entity.cow.hurt" && o[0].Pitch > 0.75f && o[0].Pitch < 1.25f, "hurt: the mob's own hurt sound and voice pitch");
        e.OnEntities(Ents(false, "{\"id\":1,\"pose\":\"dying\"}"), w, 41);
        o = Take(e);
        Check(o.Count == 1 && o[0].Event == "entity.zombie.death", "death: when the pose turns to dying");

        // pickups: an item gone next to a player is a pickup, one gone far from anyone isn't, and a full list never is
        e.OnPlayers(new List<PlayerInfo> { new PlayerInfo { Uuid = "p1", Id = 7, Pos = new Vector3(115.9f, 61, -25.2f) } }, w, 42);
        e.OnEntities(Ents(false, "{\"id\":4,\"type\":\"minecraft:experience_orb\",\"pos\":[125.5,61,-15.5],\"xp\":3,\"size\":[0.5,0.5]}"), w, 42);
        e.OnEntities(Ents(false, "", "3,4"), w, 43);
        o = Take(e);
        Check(o.Count == 1 && o[0].Event == "entity.item.pickup" && Near(o[0].Volume, 0.2f), $"pickup: the item next to the player, not the far orb ({string.Join("; ", o)})");
        e.OnEntities(Ents(false, "{\"id\":5,\"type\":\"minecraft:lightning_bolt\",\"pos\":[120,61,-20],\"size\":[0,0]},{\"id\":6,\"type\":\"minecraft:tnt\",\"pos\":[121,61,-20],\"size\":[0.98,0.98]}"), w, 44);
        o = Take(e);
        Check(o.Any(r => r.Event == "entity.lightning_bolt.thunder" && r.Volume >= 10000) && o.Any(r => r.Event == "entity.lightning_bolt.impact") && o.Any(r => r.Event == "entity.tnt.primed"),
              "a bolt thunders (heard everywhere) and strikes; primed TNT hisses");

        // players: footsteps on grass, none while riding; hurt by player id; death
        var p = new SoundDirector { Defs = d.Defs, Groups = d.Groups, Rng = new System.Random(9) };
        int steps = 0;
        for (int i = 0; i <= 10; i++) { p.OnPlayers(new List<PlayerInfo> { new PlayerInfo { Uuid = "a", Id = 7, Vehicle = -1, Pos = new Vector3(100.5f + i * 0.43f, 61, -30) } }, w, i * 0.1); steps += Take(p).Count(r => r.Event == "block.grass.step"); }
        int rideSteps = 0;
        for (int i = 0; i <= 10; i++) { p.OnPlayers(new List<PlayerInfo> { new PlayerInfo { Uuid = "a", Id = 7, Vehicle = 12, Pose = "riding", Pos = new Vector3(105f + i * 0.43f, 61, -30) } }, w, 2 + i * 0.1); rideSteps += Take(p).Count; }
        Check(steps == 2 && rideSteps == 0, $"players: about 2.6 steps a second walking on grass ({steps} in 1 s), none riding");
        p.OnActions(new List<GameAction> { new GameAction { Kind = ActionKind.Hurt, Id = 7 } }, w, 5);
        p.OnPlayers(new List<PlayerInfo> { new PlayerInfo { Uuid = "a", Id = 7, Vehicle = -1, Pose = "dying", Pos = new Vector3(110, 61, -30) } }, w, 5.1);
        o = Take(p);
        Check(o.Count == 2 && o[0].Event == "entity.player.hurt" && o[1].Event == "entity.player.death", "players: hurt (by entity id) and death");

        // digging: a hit every 4 ticks while the cracks keep coming, then nothing
        var dg = new SoundDirector { Defs = d.Defs, Groups = d.Groups };
        var sw = Field("minecraft:stone");
        sw.Grid[sw.Index(3, 1, 3)] = 2;
        dg.OnActions(new List<GameAction> { new GameAction { Kind = ActionKind.Dig, Stage = 2, Pos = new Vector3Int(103, 61, -37), Id = 7 } }, sw, 0);
        int hits = 0;
        for (int t = 0; t <= 60; t++) { dg.Tick(t * 0.05, sw, default, default, 0); hits += Take(dg).Count(r => r.Event == "block.stone.hit"); }
        Check(hits >= 5 && hits <= 6, $"digging: stone hits every 0.2 s for the second after the last crack update, then stop ({hits})");

        // rain on the field, flowing water and lava ambience
        var rn = new SoundDirector { Defs = d.Defs, Groups = d.Groups, Rng = new System.Random(4) };
        int rain = 0, dry = 0;
        for (int t = 0; t < 40; t++) { rn.Tick(t * 0.05, w, new Vector3Int(0, 0, 0), new Vector3Int(32, 8, 32), 1f); rain += Take(rn).Count(r => r.Event == "weather.rain" && r.Priority == SoundPriority.Ambient && r.Pos.y == 61); }
        for (int t = 40; t < 80; t++) { rn.Tick(t * 0.05, w, new Vector3Int(0, 0, 0), new Vector3Int(32, 8, 32), 0f); dry += Take(rn).Count(r => r.Event == "weather.rain"); }
        Check(rain >= 6 && rain <= 30 && dry == 0, $"rain: heard where it lands on the grass, a few times a second, not when dry ({rain} in 2 s)");
        var fl = Field("minecraft:water[level=3]", "minecraft:water[level=0]", "minecraft:lava[level=0]");
        for (int z = 0; z < 32; z++) for (int x = 0; x < 32; x++) fl.Grid[fl.Index(x, 1, z)] = (ushort)(x < 10 ? 2 : x < 20 ? 3 : 4);
        var fd = new SoundDirector { Defs = d.Defs, Groups = d.Groups, Rng = new System.Random(11) };
        int water = 0, lava = 0;
        for (int t = 0; t < 400; t++) { fd.Tick(t * 0.05, fl, new Vector3Int(0, 0, 0), new Vector3Int(32, 8, 32), 0); foreach (var r in Take(fd)) { if (r.Event == "block.water.ambient") water++; if (r.Event.StartsWith("block.lava")) lava++; } }
        Check(water > 3 && lava > 3, $"ambience: flowing water babbles and open lava pops (not still water) over 20 s ({water}, {lava})");

        // chat
        var ch = new SoundDirector { Defs = d.Defs };
        ch.OnChat(J("{\"kind\":\"chat\",\"text\":\"hi\"}"), 0); ch.OnChat(J("{\"kind\":\"operator\",\"text\":\"me\"}"), 0); ch.OnChat(J("{\"kind\":\"join\",\"text\":\"x\"}"), 0);
        o = Take(ch);
        ch.ChatPing = false; ch.OnChat(J("{\"kind\":\"whisper\",\"text\":\"psst\"}"), 1);
        Check(o.Count == 1 && !o[0].Positional && o[0].Event == "block.note_block.pling" && ch.Out.Count == 0, "chat: a ping in the head for others' lines only; chat_ping off silences it");
    }

    // ---------- voices and fall-off ----------

    static void Mixer()
    {
        float mpb = SoundMixer.MetresPerBlock(0.01f, 8f);
        float r1 = SoundMixer.Range(16, 1f, mpb), r4 = SoundMixer.Range(16, 4f, mpb);
        Check(Near(r1, 1.28f) && Near(r4, 5.12f) && Near(SoundMixer.Gain(1f, 0.64f, r1), 0.5f) && SoundMixer.Gain(1f, 2f, r1) == 0 && Near(SoundMixer.Gain(3f, 0, r1, 0.5f), 0.5f),
              "fall-off: 16 blocks x max(1, volume) of table range at 1 cm blocks x 8; loudness min(volume, 1) x master");
        Check(Near(SoundMixer.MetresPerBlock(0.003f, 8f), 0.6f / 16) && SoundMixer.MetresPerBlock(0.02f, 8f) > mpb, "fall-off: scales with the block size, never under 0.6 m for a 16-block sound");
        var m = new SoundMixer(8) { MaxAmbient = 3, MaxSteps = 2, MaxPerEvent = 3 };
        int amb = 0; for (int i = 0; i < 5; i++) if (m.Allocate("weather.rain", SoundPriority.Ambient, 0.5f, 0) >= 0) amb++;
        Check(amb == 3, $"voices: at most MaxPerEvent copies of one event ({amb})");
        Check(m.Allocate("block.water.ambient", SoundPriority.Ambient, 0.4f, 0) < 0, "voices: the ambience class is capped");
        for (int i = 0; i < 5; i++) m.Allocate("block.stone.break" + i, SoundPriority.Block, 0.5f, 0);
        Check(m.Busy == 8, "voices: all 8 busy");
        int stolen = m.Stolen, own = m.Allocate("block.wood.place", SoundPriority.Own, 0.2f, 1);
        Check(own >= 0 && m.Stolen == stolen + 1 && m.Voices[own].Event == "block.wood.place", "voices: the headset's own sound steals the least important voice");
        bool mobs = m.Allocate("entity.cow.ambient", SoundPriority.Mob, 0.9f, 1) >= 0 && m.Allocate("entity.pig.ambient", SoundPriority.Mob, 0.9f, 1) >= 0;
        Check(mobs && m.Voices.Count(v => v.Priority == SoundPriority.Ambient && v.Busy) == 0 && m.Allocate("block.grass.step", SoundPriority.Step, 1f, 1) < 0,
              "voices: mobs steal from ambience; a step can't steal from mobs or blocks");
        Check(m.Allocate("x", SoundPriority.Own, 0.005f, 1) < 0, "voices: too quiet to hear is dropped");
        m.Clear();
        int jb = m.Allocate("music_disc.cat", SoundPriority.Event, 1, 0, true, new Vector3Int(1, 2, 3));
        var stopped = new List<int>();
        m.StopBlock(new Vector3Int(1, 2, 3), stopped.Add);
        Check(jb >= 0 && stopped.SequenceEqual(new[] { jb }) && m.Busy == 0, "voices: sound_stop releases what plays at the block");
    }

    // ---------- sounds.zip ----------

    static string MakeZip(string dir, string name, string json, params (string path, byte[] data)[] files)
    {
        string p = Path.Combine(dir, name);
        if (File.Exists(p)) File.Delete(p);
        using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
        {
            using (var s = new StreamWriter(z.CreateEntry("assets/minecraft/sounds.json").Open())) s.Write(json);
            using (var s = new StreamWriter(z.CreateEntry("tabletop_sounds.json").Open())) s.Write("{\"minecraft\":\"26.3\"}");
            foreach (var f in files) using (var s = z.CreateEntry(f.path, CompressionLevel.NoCompression).Open()) s.Write(f.data, 0, f.data.Length);
        }
        return p;
    }

    static void Pack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tabletop_sound_test_" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            var ogg = Encoding.ASCII.GetBytes("OggS fake vorbis data");
            string zip = MakeZip(dir, "sounds.zip", Sample, ("assets/minecraft/sounds/dig/a1.ogg", ogg), ("assets/minecraft/sounds/dig/a2.ogg", ogg));
            string cache = Path.Combine(dir, "cache");
            Directory.CreateDirectory(Path.Combine(cache, "sounds_1_2"));   // a stale cache of another sounds.zip
            using (var p = SoundPack.Open(zip, cache))
            {
                Check(p.Loaded && p.Defs.Count == 6 && p.Files == 2 && p.Version == "26.3" && p.Status.Contains("Minecraft 26.3"), $"sounds.zip: definitions, files and version ({p.Status})");
                string f = p.Extract(SoundDefs.FilePath("dig/a1"));
                Check(f != null && File.ReadAllBytes(f).SequenceEqual(ogg) && f.StartsWith(p.CacheDir) && p.Extract(SoundDefs.FilePath("dig/a3")) == null && p.Extract("../../evil") == null,
                      "sounds.zip: a sound is copied out once into the cache; missing ones and paths outside are refused");
                Check(!Directory.Exists(Path.Combine(cache, "sounds_1_2")), "sounds.zip: caches of other sounds.zip files are deleted");
            }
            var none = SoundPack.Open(Path.Combine(dir, "missing.zip"), cache);
            Check(!none.Loaded && none.Status.StartsWith("no sounds.zip"), "no sounds.zip: silent, and the status says where to get one");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // ---------- asset delivery ----------

    sealed class FakeServer
    {
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        public int Requests;
        public Dictionary<string, object> Offer(int chunk, Func<string, string> shaOverride = null)
        {
            var files = new List<object>();
            foreach (var kv in Files)
                files.Add(new Dictionary<string, object> { ["name"] = kv.Key, ["size"] = (double)kv.Value.Length, ["sha256"] = shaOverride?.Invoke(kv.Key) ?? Sha(kv.Value) });
            files.Add(new Dictionary<string, object> { ["name"] = "evil.sh", ["size"] = 10.0, ["sha256"] = new string('a', 64) });
            return new Dictionary<string, object> { ["type"] = "assets", ["chunk"] = (double)chunk, ["files"] = files };
        }
        public byte[] Answer(Dictionary<string, object> get)
        {
            Requests++;
            var data = Files[get.S("name")];
            long off = Convert.ToInt64(get["offset"]); int len = Convert.ToInt32(get["length"]);
            var part = new byte[Math.Min(len, data.Length - off)];
            Array.Copy(data, off, part, 0, part.Length);
            return AssetProtocol.Frame(get.S("name"), off, part);
        }
    }

    static string Sha(byte[] b) { using (var s = System.Security.Cryptography.SHA256.Create()) return string.Concat(s.ComputeHash(b).Select(x => x.ToString("x2"))); }

    static bool Settle(AssetSync s, FakeServer srv, Func<bool> done, int maxRounds = 4000, int stopAfterChunks = -1)
    {
        int chunks = 0;
        for (int i = 0; i < maxRounds; i++)
        {
            foreach (var g in s.Pump())
            {
                if (stopAfterChunks >= 0 && chunks >= stopAfterChunks) return false;
                var f = srv.Answer(g); s.OnBinary(f, f.Length); chunks++;
            }
            if (done()) return true;
            Thread.Sleep(1);
        }
        return done();
    }

    static void Assets(Func<string, string> fixture)
    {
        var offer = AssetProtocol.ParseOffer(Fx(fixture, "assets.json"), out int chunk);
        Check(offer.Count == 2 && offer[0].Name == "entity_models.json" && offer[1].Size == 67012345 && chunk == 262144, "assets fixture: two files and the chunk size");
        var bin = File.ReadAllBytes(fixture("asset_chunk.bin"));
        Check(AssetProtocol.ParseChunk(bin, bin.Length, out var n, out var off, out var start) && n == "sounds.zip" && off == 262144
              && Encoding.ASCII.GetString(bin, start, bin.Length - start) == "tabletop asset chunk" && AssetProtocol.Frame(n, off, Encoding.ASCII.GetBytes("tabletop asset chunk")).SequenceEqual(bin),
              "asset_chunk.bin: the frame parses and the client builds it byte for byte");
        Check(!AssetProtocol.ParseChunk(Encoding.ASCII.GetBytes("XXXX\u0001a00000000z"), 15, out _, out _, out _), "a frame without the magic is ignored");
        var get = Fx(fixture, "asset_get.json");
        var mine = AssetProtocol.Get("sounds.zip", 262144, 262144);
        Check(get.S("name") == mine.S("name") && get.D("offset") == Convert.ToDouble(mine["offset"]) && get.D("length") == Convert.ToDouble(mine["length"]), "asset_get matches the fixture");

        string dir = Path.Combine(Path.GetTempPath(), "tabletop_assets_test_" + Environment.ProcessId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        try
        {
            var rnd = new System.Random(2);
            var srv = new FakeServer();
            var sounds = new byte[700_000]; rnd.NextBytes(sounds);
            var models = Encoding.UTF8.GetBytes("{\"models\":{}}");
            srv.Files["entity_models.json"] = models; srv.Files["sounds.zip"] = sounds;
            File.WriteAllBytes(Path.Combine(dir, "entity_models.json"), models);   // already up to date on the headset
            var s = new AssetSync(dir);
            var done = new List<string>();
            s.Completed = done.Add;
            s.OnOffer(srv.Offer(65536));
            bool ok = Settle(s, srv, () => done.Contains("sounds.zip") && !s.Busy);
            Check(ok && File.ReadAllBytes(Path.Combine(dir, "sounds.zip")).SequenceEqual(sounds) && done.SequenceEqual(new[] { "sounds.zip" })
                  && s.Items.First(i => i.Name == "entity_models.json").State == AssetSync.State.UpToDate && !s.Items.Any(i => i.Name == "evil.sh")
                  && !File.Exists(Path.Combine(dir, "sounds.zip.part")) && File.Exists(Path.Combine(dir, AssetSync.HashCache)),
                  $"assets: the missing file downloads in 64 KB chunks and checks out; the matching one is left alone; unknown names ignored ({s.Status})");
            int before = srv.Requests;
            var again = new AssetSync(dir) { Hash = _ => throw new Exception("should use the hash cache") };
            again.OnOffer(srv.Offer(65536));
            Settle(again, srv, () => !again.Busy);
            Check(srv.Requests == before && again.Items.All(i => i.State == AssetSync.State.UpToDate) && again.Status.Contains("up to date"),
                  $"assets: next time nothing is downloaded and nothing re-hashed (assets_hashes.json) ({again.Status})");

            // resume after a dropped link
            var sounds2 = new byte[600_000]; rnd.NextBytes(sounds2);
            srv.Files["sounds.zip"] = sounds2;
            var r = new AssetSync(dir) { InFlightMax = 2 };
            var rdone = new List<string>(); r.Completed = rdone.Add;
            r.OnOffer(srv.Offer(65536));
            Settle(r, srv, () => false, 50, 3);
            r.LinkLost();
            long partLen = new FileInfo(Path.Combine(dir, "sounds.zip.part")).Length;
            int reqBefore = srv.Requests;
            r.OnOffer(srv.Offer(65536));
            var first = new List<Dictionary<string, object>>();
            for (int i = 0; i < 100 && first.Count == 0; i++) { first = r.Pump(); Thread.Sleep(1); }
            long resumeAt = first.Count > 0 ? Convert.ToInt64(first[0]["offset"]) : -1;
            foreach (var g in first) { var f = srv.Answer(g); r.OnBinary(f, f.Length); }
            ok = Settle(r, srv, () => rdone.Contains("sounds.zip"));
            Check(partLen == 3 * 65536 && resumeAt == 3 * 65536 && ok && File.ReadAllBytes(Path.Combine(dir, "sounds.zip")).SequenceEqual(sounds2),
                  $"assets: a dropped link resumes from the part file at {resumeAt} (had {partLen})");

            // a wrong checksum never replaces the file
            var bad = new AssetSync(dir);
            File.WriteAllBytes(Path.Combine(dir, "pack.zip"), new byte[] { 1, 2, 3 });
            srv.Files.Clear(); srv.Files["pack.zip"] = new byte[100_000];
            bad.OnOffer(srv.Offer(65536, _ => new string('0', 64)));
            Settle(bad, srv, () => bad.Items.Count > 0 && bad.Items[0].State == AssetSync.State.Failed);
            Check(bad.Items[0].State == AssetSync.State.Failed && bad.Items[0].Note.Contains("checksum") && File.ReadAllBytes(Path.Combine(dir, "pack.zip")).Length == 3
                  && !File.Exists(Path.Combine(dir, "pack.zip.part")), "assets: a checksum mismatch keeps the old file and drops the download");
            var denied = new AssetSync(dir);
            denied.OnOffer(srv.Offer(65536));
            Settle(denied, srv, () => !denied.Items.Exists(i => i.State == AssetSync.State.Checking), 200, 0);
            Check(denied.OnError(Fx(fixture, "error_asset_denied.json")) && !denied.OnError(J("{\"code\":\"edit_denied\"}")) && denied.Items[0].State == AssetSync.State.Failed,
                  "assets: asset_denied stops the download; other errors aren't the downloader's");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    // ---------- the user's real sounds.zip ----------

    /// <summary>Every sound id the client and the server write by hand exists in the user's sounds.json.</summary>
    public static readonly string[] HandWritten =
    {
        // client: SoundDirector and SoundRules
        "entity.item.pickup", "entity.experience_orb.pickup", "entity.lightning_bolt.thunder", "entity.lightning_bolt.impact", "entity.tnt.primed",
        "entity.player.hurt", "entity.player.death", "entity.generic.hurt", "entity.generic.death", "weather.rain", "block.water.ambient", "block.lava.pop",
        "block.lava.ambient", "block.fire.ambient", "block.note_block.pling", "entity.cat.stray_ambient", "entity.wolf.growl",
        "block.wooden_door.open", "block.iron_door.close", "block.copper_door.open", "block.cherry_wood_door.open", "block.bamboo_wood_door.open", "block.nether_wood_door.open",
        "block.wooden_trapdoor.open", "block.iron_trapdoor.open", "block.copper_trapdoor.open", "block.fence_gate.open", "block.cherry_wood_fence_gate.close",
        "block.bamboo_wood_fence_gate.open", "block.nether_wood_fence_gate.open", "block.lever.click", "block.stone_button.click_on", "block.wooden_button.click_off",
        "block.cherry_wood_button.click_on", "block.bamboo_wood_button.click_on", "block.nether_wood_button.click_on", "block.stone_pressure_plate.click_on",
        "block.wooden_pressure_plate.click_off", "block.metal_pressure_plate.click_on", "block.cherry_wood_pressure_plate.click_on", "block.bamboo_wood_pressure_plate.click_on",
        "block.nether_wood_pressure_plate.click_on", "block.piston.extend", "block.piston.contract", "block.comparator.click", "block.candle.extinguish",
        "item.flintandsteel.use", "entity.generic.extinguish_fire", "block.composter.empty", "block.composter.ready", "block.composter.fill", "block.composter.fill_success",
        "item.axe.strip", "item.axe.scrape", "item.axe.wax_off", "item.honeycomb.wax_on", "item.hoe.till", "item.shovel.flatten", "block.pumpkin.carve",
        // server: SoundRules.java
        "block.chest.open", "block.chest.close", "block.ender_chest.open", "block.barrel.open", "block.barrel.close", "block.shulker_box.open", "block.copper_chest.open",
        "block.copper_chest_weathered.open", "block.copper_chest_oxidized.close", "entity.generic.explode", "entity.wind_charge.wind_burst", "entity.breeze.wind_burst",
        "item.bucket.fill", "item.bucket.fill_lava", "item.bucket.fill_powder_snow", "item.bucket.empty", "item.bucket.empty_lava", "item.bucket.empty_powder_snow",
        "item.bucket.empty_axolotl", "item.bucket.empty_tadpole", "item.bucket.empty_fish", "item.firecharge.use", "item.bone_meal.use", "block.bell.use", "block.note_block.harp",
    };

    static void RealSounds(string zip)
    {
        string cache = Path.Combine(Path.GetTempPath(), "tabletop_real_sounds_" + Environment.ProcessId);
        Directory.CreateDirectory(cache);
        try
        {
            using (var p = SoundPack.Open(zip, cache))
            {
                Check(p.Loaded && p.Defs.Count > 1000, $"real sounds.zip: {p.Status}");
                var missing = HandWritten.Where(k => !p.Defs.Has(k)).ToList();
                Check(missing.Count == 0, $"real sounds.json has every hand-written sound id ({HandWritten.Length}; missing: {string.Join(", ", missing)})");
                var rng = new System.Random(1);
                bool files = true; int checkedFiles = 0;
                foreach (var k in new[] { "block.stone.break", "block.grass.step", "entity.zombie.ambient", "weather.rain", "block.note_block.imitate.zombie", "entity.generic.explode" })
                {
                    if (!p.Defs.Pick(k, rng, out var c)) { files = false; continue; }
                    string f = p.Extract(c.File);
                    var head = f != null ? File.ReadAllBytes(f).Take(4).ToArray() : new byte[0];
                    files &= Encoding.ASCII.GetString(head) == "OggS";
                    checkedFiles++;
                }
                Check(files && checkedFiles == 6, "real sounds.zip: picked variants (also through an event reference) extract to Ogg files");
                var guess = new BlockSounds();
                Check(guess.Of("minecraft:oak_planks", p.Defs).Break == "block.wood.break" && guess.Of("minecraft:amethyst_block", p.Defs).Break == "block.amethyst_block.break",
                      "real sounds.json: the fallback guess finds wood and the block's own family");
            }
        }
        finally { try { Directory.Delete(cache, true); } catch (Exception) { } }
    }

    // ---------- live: asset delivery from a running plugin ----------

    /// <summary>--live-assets URL TOKEN: downloads every offered file through the app's LinkClient and AssetSync into a
    /// temp folder and compares it with the offer.</summary>
    public static void LiveAssets(string url, string token, Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tabletop_live_assets_" + Environment.ProcessId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var sync = new AssetSync(dir);
        var done = new List<string>();
        sync.Completed = done.Add;
        var link = new LinkClient(url, token);
        link.Binary = sync.OnBinary;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<AssetFile> offered = null;
        bool welcomeHasCap = false;
        link.Start();
        try
        {
            while (sw.Elapsed.TotalSeconds < 120)
            {
                while (link.Inbound.TryDequeue(out var m))
                {
                    if (m.S("type") == "welcome") welcomeHasCap = m.A("caps")?.Contains(AssetProtocol.Cap) == true;
                    if (m.S("type") == AssetProtocol.Type) { offered = AssetProtocol.ParseOffer(m, out _); sync.OnOffer(m); }
                    if (m.S("type") == "error") { Console.WriteLine("  server error: " + m.S("code") + " " + m.S("message")); sync.OnError(m); }
                }
                foreach (var g in sync.Pump()) link.Send(g);
                if (offered != null && offered.Count > 0 && !sync.Busy) break;
                Thread.Sleep(5);
            }
            double secs = sw.Elapsed.TotalSeconds;
            check(welcomeHasCap && offered != null && offered.Count > 0, $"live assets: welcome grants assets_v1 and the server offers {offered?.Count ?? 0} files");
            bool same = offered != null && offered.All(f => File.Exists(Path.Combine(dir, f.Name)) && AssetSync.Sha256File(Path.Combine(dir, f.Name)) == f.Sha256);
            long bytes = offered?.Sum(f => f.Size) ?? 0;
            check(same && done.Count == (offered?.Count ?? -1), $"live assets: {done.Count} files, {bytes / 1e6:F1} MB downloaded and verified in {secs:F1} s ({bytes / 1e6 / Math.Max(0.01, secs):F1} MB/s): {sync.Status}");
        }
        finally { link.Shutdown("test done"); try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
