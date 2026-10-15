// [motion] Ambient particles (AmbientParticles.cs): vanilla's spawn points and neighbour rules, the slot plan (rates,
// cap, density by width).
using System;
using System.Collections.Generic;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static class MotionTests
{
    static WorldModel World(int dx, int dy, int dz, Func<int, int, int, int> f, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) w.Grid[w.Index(x, y, z)] = (ushort)f(x, y, z);
        return w;
    }

    static List<Emitter> Scan(WorldModel w)
    {
        var l = new List<Emitter>();
        var pal = w.Palette.ToArray();
        AmbientRules.Scan(w, AmbientRules.Table(pal), pal, null, new Vector3Int(0, 0, 0), w.Dims, l);
        return l;
    }

    static bool Near(Vector3 a, Vector3 b) => Math.Abs(a.x - b.x) < 1e-4 && Math.Abs(a.y - b.y) < 1e-4 && Math.Abs(a.z - b.z) < 1e-4;
    static bool Near(float a, float b, float eps = 1e-3f) => Math.Abs(a - b) < eps;

    public static void Run(Action<bool, string> check, Func<string, string> fixture)
    {
        Particles(check);
        Curves(check);
        Protocol(check, fixture);
    }

    // Animation curves against vanilla 26.3's numbers (MotionCurves.cs names the classes).
    static void Curves(Action<bool, string> check)
    {
        // ChestLidController: 0.1 a tick, so 10 ticks to open; ChestRenderer eases 1 - (1 - o)^3; the lid turns -pi/2 at most.
        float o = 0; for (int i = 0; i < 10; i++) o = MotionCurves.StepLid(o, true, 1);
        check(Near(o, 1f) && MotionCurves.StepLid(1, true, 1) == 1 && Near(MotionCurves.StepLid(1, false, 3), 0.7f) && MotionCurves.StepLid(0.05f, false, 1) == 0,
            "lid: 0.1 a tick toward open or closed, clamped");
        check(Near(MotionCurves.ChestLidRad(0), 0) && Near(MotionCurves.ChestLidRad(1), -Mathf.PI / 2) && Near(MotionCurves.ChestLidRad(0.5f), -0.875f * Mathf.PI / 2),
            "chest lid: -(1 - (1 - o)^3) * pi/2 (half open = 87.5% of the turn)");
        MotionCurves.ShulkerLid(1, out float y, out float yr);
        MotionCurves.ShulkerLid(0, out float y0, out float yr0);
        check(Near(y, 16) && Near(yr, 1.5f * Mathf.PI) && Near(y0, 24) && yr0 == 0, "shulker box lid: rises 8 px and turns 270 degrees");
        check(MotionCurves.BellSwing(0) == 0 && Near(MotionCurves.BellSwing(5), Mathf.Sin(5 / Mathf.PI) / (4 + 5 / 3f)) && MotionCurves.BellSwing(50) == 0 && MotionCurves.BellSwing(-1) == 0,
            "bell: sin(t / pi) / (4 + t / 3) for 50 ticks");
        MotionCurves.BellAxes("north", 0.2f, out float bx, out float bz);
        MotionCurves.BellAxes("west", 0.2f, out float wx, out float wz);
        check(bx == -0.2f && bz == 0 && wx == 0 && wz == 0.2f, "bell: north and south swing about x, east and west about z");
        check(MotionCurves.DeathTiltDeg(0) == 0 && MotionCurves.DeathTiltDeg(1) == 0 && Near(MotionCurves.DeathTiltDeg(6), Mathf.Sqrt(5f / 20f * 1.6f) * 90f) &&
              MotionCurves.DeathTiltDeg(14) == 90 && MotionCurves.DeathTiltDeg(20, 180) == 180, "death: sqrt((t - 1) / 20 * 1.6) of 90 degrees (spiders 180), flat after 13.5 ticks");
        check(MotionCurves.TntScale(40) == 1 && MotionCurves.TntScale(10) == 1 && Near(MotionCurves.TntScale(0), 1.3f) && Near(MotionCurves.TntScale(5), 1 + 0.0625f * 0.3f),
            "tnt: swells 30% over the last 10 ticks ((1 - fuse / 10)^4)");
        check(MotionCurves.TntWhite(80) && !MotionCurves.TntWhite(77) && MotionCurves.TntWhite(72) && !MotionCurves.TntWhite(5) && MotionCurves.TntWhite(3), "tnt: white while fuse / 5 is even");
        MotionCurves.CreeperScale(0, out float c0, out float u0);
        MotionCurves.CreeperScale(1, out float c1, out float u1);
        float h = 1 + Mathf.Sin(100) * 0.01f;
        check(c0 == 1 && u0 == 1 && Near(c1, 1.4f * h) && Near(u1, 1.1f / h) && MotionCurves.CreeperWhite(0.05f) == 0 && MotionCurves.CreeperWhite(0.15f) == 0.5f && MotionCurves.CreeperWhite(0.95f) == 0.95f,
            "creeper: (1 + g 0.4) h wide, (1 + g 0.1) / h high, white every other tenth of the swell");
        check(MotionCurves.BedFacing("minecraft:red_bed[facing=east,occupied=true,part=head]", out int fx, out int fz) && fx == 1 && fz == 0 && !MotionCurves.BedFacing("minecraft:stone", out _, out _),
            "sleeping: the bed's facing");
        // Quantised pose keys and the poses they bake (BlockEntityCatalog.Pose).
        var lid = BlockEntityCatalog.Pose(MotionCurves.ChestPoseKey(1));
        var bell = BlockEntityCatalog.Pose(MotionCurves.BellPoseKey("east", 5));
        var shk = BlockEntityCatalog.Pose(MotionCurves.ShulkerPoseKey(0.5f));
        check(MotionCurves.ChestPoseKey(0.01f) == null && lid != null && lid.Length == 2 && lid[0].Part == "lid" && lid[1].Part == "lock" && Near(lid[0].Value, -Mathf.PI / 2) &&
              bell != null && bell[0].Part == "bell_body" && bell[0].Value == 0 && Near(bell[1].Value, -Mathf.Round(MotionCurves.BellSwing(5) * 100) / 100f) &&
              shk != null && Near(shk[0].Value, 20) && Near(shk[1].Value, 0.75f * Mathf.PI) && MotionCurves.BellPoseKey("east", 60) == null,
            "poses: lid and lock, the bell body, the shulker lid, nothing at rest");
    }

    static void Protocol(Action<bool, string> check, Func<string, string> fixture)
    {
        var acts = new List<TabletopCraft.Net.GameAction>();
        TabletopCraft.Net.GameAction.Parse(Json.Parse(System.IO.File.ReadAllText(fixture("actions_motion.json"))) as Dictionary<string, object>, acts);
        check(acts.Count == 4 && acts[0].Kind == TabletopCraft.Net.ActionKind.Lid && acts[0].Open && !acts[1].Open && acts[0].Pos == new Vector3Int(107, 64, -35) &&
              acts[2].Kind == TabletopCraft.Net.ActionKind.Bell && acts[2].Dir == "east" && acts[3].Kind == TabletopCraft.Net.ActionKind.Explode && acts[3].Power == 4 && Near(acts[3].At.x, 110.2f),
            "actions_motion: lid open/close, bell from the east, explode with power 4");
        var store = new TabletopCraft.Net.EntityStore();
        store.Apply(Json.Parse(System.IO.File.ReadAllText(fixture("entities_motion.json"))) as Dictionary<string, object>, null);
        var e = store.All;
        check(e[401].Fuse == 37 && Near(e[402].Swell, 0.5f) && e[403].Pose == "dying" && e[404].Pose == "sitting" && e[405].Pose == "sleeping" && e[403].Fuse == 0,
            "entities_motion: tnt fuse, creeper swell, dying, sitting and sleeping poses");
    }

    static void Particles(Action<bool, string> check)
    {
        // Spawn points and rates per animateTick call (TorchBlock, WallTorchBlock, CandleBlock offsets, campfire, fire).
        var t = AmbientRules.Templates("minecraft:torch");
        check(t.Length == 2 && t.All(e => Near(e.Offset, new Vector3(0.5f, 0.7f, 0.5f)) && e.PerCall == 1) && t.Any(e => e.Kind.Motion == ParticleMotion.Flame) && t.Any(e => e.Kind.Motion == ParticleMotion.Smoke),
            "particles: a torch spawns smoke and a flame at (0.5, 0.7, 0.5) every call");
        t = AmbientRules.Templates("minecraft:wall_torch[facing=east]");
        check(t.Length == 2 && t.All(e => Near(e.Offset, new Vector3(0.23f, 0.92f, 0.5f))), "particles: a wall torch facing east burns 0.27 toward the wall, 0.22 higher");
        t = AmbientRules.Templates("minecraft:soul_wall_torch[facing=north]");
        check(t.Any(e => e.Kind.Sprite == ParticleSprites.SoulFlame && Near(e.Offset, new Vector3(0.5f, 0.92f, 0.77f))), "particles: soul wall torch: soul flame, facing north");
        t = AmbientRules.Templates("minecraft:red_candle[candles=3,lit=true,waterlogged=false]");
        var flames = t.Where(e => e.Kind.Motion == ParticleMotion.Flame).ToArray();
        check(flames.Length == 3 && Near(flames[0].Offset, new Vector3(0.5f, 0.313f, 0.625f)) && flames.All(e => e.Kind.Size < ParticleKind.Flame.Size) &&
              t.Count(e => e.Kind.Motion == ParticleMotion.Smoke && Near(e.PerCall, 0.3f)) == 3, "particles: three lit candles: three small flames at vanilla's offsets, smoke 30% of calls");
        check(AmbientRules.Templates("minecraft:candle[candles=2,lit=false,waterlogged=false]").Length == 0, "particles: an unlit candle spawns nothing");
        t = AmbientRules.Templates("minecraft:cyan_candle_cake[lit=true]");
        check(t.Length == 2 && t.All(e => Near(e.Offset, new Vector3(0.5f, 1f, 0.5f))), "particles: a lit candle cake's flame sits on top of the cake");
        check(AmbientRules.Templates("minecraft:campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]").Select(e => e.Kind.Motion).OrderBy(m => m).SequenceEqual(new[] { ParticleMotion.Campfire, ParticleMotion.LavaPop }) &&
              AmbientRules.Templates("minecraft:soul_campfire[facing=north,lit=true,signal_fire=false,waterlogged=false]").Single().Kind.Motion == ParticleMotion.Campfire &&
              AmbientRules.Templates("minecraft:campfire[facing=north,lit=false,signal_fire=false,waterlogged=false]").Length == 0,
            "particles: a lit campfire smokes and sparks, a soul campfire only smokes, an unlit one nothing");
        check(AmbientRules.Templates("minecraft:fire[age=3]").Single().PerCall == 3 && AmbientRules.Templates("minecraft:nether_portal[axis=x]").Single().PerCall == 4,
            "particles: fire 3 large smokes a call, portal 4 swirls a call");
        check(AmbientRules.Templates("minecraft:cherry_leaves[distance=1,persistent=false,waterlogged=false]").Single().PerCall == 0.1f &&
              AmbientRules.Templates("minecraft:oak_leaves[distance=1,persistent=false,waterlogged=false]").Single().Tint == 1 &&
              AmbientRules.Templates("minecraft:pale_oak_leaves[distance=1,persistent=false,waterlogged=false]").Single().PerCall == 0.02f,
            "particles: leaf chances (cherry 0.1, pale oak 0.02, others 0.01, tinted by the biome)");

        // Neighbour rules in the grid.
        const string air = "minecraft:air", stone = "minecraft:stone", lava = "minecraft:lava[level=0]", water = "minecraft:water[level=0]";
        var e1 = Scan(World(1, 3, 1, (x, y, z) => y == 0 ? 2 : y == 1 ? 1 : 0, air, lava, stone));
        var e2 = Scan(World(1, 2, 1, (x, y, z) => y == 0 ? 1 : 2, air, lava, stone));
        check(e1.Count(e => e.Kind.Motion == ParticleMotion.LavaPop) == 1 && e1[0].Pos.y == 2f && e2.Count == 0, "particles: lava pops only under air, from its top");

        // Oak leaves at y 4 over three open cells and stone at y 0: a leaf falls 2.95 blocks (from 0.05 under the block).
        var leaves = Scan(World(1, 5, 1, (x, y, z) => y == 4 ? 1 : y == 0 ? 2 : 0, air, "minecraft:oak_leaves[distance=1,persistent=false,waterlogged=false]", stone));
        check(leaves.Count == 1 && Near(leaves[0].Reach, 2.95f) && Near(leaves[0].Pos.y, 3.95f), $"particles: a leaf falls from under the leaves to the floor ({(leaves.Count > 0 ? leaves[0].Reach : -1)})");
        check(Scan(World(1, 2, 1, (x, y, z) => y == 1 ? 1 : 2, air, "minecraft:oak_leaves[distance=1,persistent=false,waterlogged=false]", stone)).Count == 0, "particles: no leaf over a solid block");

        // Water over stone over two open cells (floor at y 0): a drip hangs under the stone and falls 1.95.
        var drip = Scan(World(1, 5, 1, (x, y, z) => y == 4 ? 1 : y == 3 ? 2 : y == 0 ? 2 : 0, air, water, stone));
        check(drip.Count == 1 && drip[0].Kind.Motion == ParticleMotion.Drip && Near(drip[0].Pos.y, 2.95f) && Near(drip[0].Reach, 1.95f) && Near(drip[0].Rate, 0.1f * AmbientRules.CallsPerSecond),
            $"particles: water drips through a stone ceiling 1 in 10 calls ({(drip.Count > 0 ? drip[0].Reach : -1)})");
        check(Scan(World(1, 5, 1, (x, y, z) => y == 4 ? 1 : y == 3 ? 2 : y == 0 ? 3 : 0, air, water, "minecraft:glass", stone)).Count == 0, "particles: glass is impermeable: no drip");

        // A bubble column from y 1 to 3 under water: bubbles rise to the column's top (y 4).
        var col = Scan(World(1, 6, 1, (x, y, z) => y == 0 ? 3 : y <= 3 ? 1 : y == 4 ? 2 : 0, air, "minecraft:bubble_column[drag=false]", water, "minecraft:soul_sand"));
        var bottom = col.Where(e => e.Kind.Motion == ParticleMotion.BubbleUp && Near(e.Pos.y, 1f)).ToList();
        check(col.Count == 6 && bottom.Count == 1 && Near(bottom[0].Reach, 3f), $"particles: a bubble column's bubbles rise to its top ({col.Count} emitters)");
        var portal = Scan(World(2, 1, 1, (x, y, z) => x + 1, air, "minecraft:nether_portal[axis=x]", "minecraft:nether_portal[axis=z]"));
        check(portal.Count == 2 && portal[0].Reach == 2 && portal[1].Reach == 0, "particles: the portal swirl crosses the portal's plane");

        // Slots: a rate of r per second is kept (slots x chance / period), a long-lived busy emitter caps its slots.
        bool ok = true;
        foreach (var (rate, life) in new[] { (2f, 2.2f), (0.02f, 4f), (0.2f, 15f), (6f, 5f), (1f, 7f) })
        {
            int k = AmbientRules.Slots(rate, life, out float period, out float chance);
            ok &= k >= 1 && chance > 0 && chance <= 1 && period > life && (k == AmbientRules.MaxSlotsPerEmitter || Near(k * chance / period, rate));
        }
        check(ok && AmbientRules.Slots(0, 2, out _, out _) == 0 && AmbientRules.Slots(100, 5, out _, out float full) == AmbientRules.MaxSlotsPerEmitter && full == 1,
            "particles: slots keep the spawn rate (k * chance / period), capped per emitter");
        check(Near(AmbientRules.Density(48, 1), 1) && Near(AmbientRules.Density(128, 1), 0.25f) && Near(AmbientRules.Density(512, 1), 0.1f) && Near(AmbientRules.Density(64, 0.5f), 0.5f),
            "particles: density 1 up to 64 wide, then by area (0.25 at 128, at least 0.1), times particle_density");

        // The plan honours the cap: 2,000 torches (12 slots each at full density) into 3,000 slots.
        var many = new List<Emitter>();
        for (int i = 0; i < 2000; i++) foreach (var tt in AmbientRules.Templates("minecraft:torch")) many.Add(new Emitter { Pos = new Vector3(i % 50, i / 50, 0) + tt.Offset, Rate = tt.PerCall * AmbientRules.CallsPerSecond, Kind = tt.Kind });
        var few = AmbientRules.Plan(many.Take(20).ToList(), 1f, 3000);
        var capped = AmbientRules.Plan(many, 1f, 3000);
        int expect = many.Take(20).Sum(e => AmbientRules.Slots(e.Rate, e.Kind.Life, out _, out _));
        check(few.Count == expect && capped.Count <= 3000 && capped.Count > 2500 && capped.Select(s => s.Emitter).Distinct().Count() > 1000,
            $"particles: under the cap every slot is kept ({few.Count}), over it the density drops to fit ({capped.Count} slots, {capped.Select(s => s.Emitter).Distinct().Count()} emitters)");
        check(ParticleSprites.Names.Length == ParticleSprites.Count && ParticleSprites.Names[ParticleSprites.Generic] == "generic_7" && ParticleSprites.Names[ParticleSprites.DripFall] == "drip_fall" && ParticleSprites.Names.All(n => n != null),
            "particles: sprite layers named like the pack's textures/particle files (smoke from generic_7)");
    }
}
