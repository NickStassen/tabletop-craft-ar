// [living] Engine-free tests for the living world (PROTOCOL.md "World state: time and weather", "Living entities"):
// the time-of-day lightmap against 26.3's lightmap.fsh and day.json, the weather layers, the per-vertex light codes and
// greedy merging, the world clock, and (further down) leads, the lightning bolt, rain and snow, display transforms,
// experience orbs and the mob gear catalog.
using System;
using System.Collections.Generic;
using System.Linq;
using TabletopCraft.Net;
using TabletopCraft.World;
using UnityEngine;

static partial class LivingTests
{
    static Action<bool, string> Check;

    public static void Run(Action<bool, string> check, Func<string, string> fixture, string jar)
    {
        Check = check;
        Lightmap();
        Timeline();
        Weather();
        Codes();
        Merging();
        Clock(fixture);
        More(fixture, jar);
    }

    /// <summary>The entity-side tests (LivingEntityTests.cs).</summary>
    static partial void More(Func<string, string> fixture, string jar);

    static bool Near(float a, float b, float eps = 1e-4f) => Math.Abs(a - b) <= eps;
    static bool Near(Vector3 a, Vector3 b, float eps = 1e-4f) => Near(a.x, b.x, eps) && Near(a.y, b.y, eps) && Near(a.z, b.z, eps);

    /// <summary>26.3 assets/minecraft/shaders/core/lightmap.fsh, written out again from the GLSL in doubles (no night
    /// vision, darkness or boss fog), then the Brightness mix; for comparing with BlockShading.RawLightmap.</summary>
    static double[] Vanilla(double skyFactor, double[] skyColor, double[] ambient, double blockFactor, double[] tint, int sky, int block, double brightness)
    {
        double Bright(double l) => l / (4.0 - 3.0 * l);
        double bl = block / 15.0, sl = sky / 15.0;
        double bb = Bright(bl) * blockFactor, sb = Bright(sl) * skyFactor;
        double par = (2.0 * bl - 1.0) * (2.0 * bl - 1.0);
        var c = new double[3];
        for (int i = 0; i < 3; i++)
        {
            double blockColor = tint[i] + (1.0 - tint[i]) * 0.9 * par;   // mix(BlockLightTint, vec3(1), 0.9 * parabolic)
            c[i] = Math.Clamp(ambient[i] + skyColor[i] * sb + blockColor * bb, 0, 1);
        }
        double max = Math.Max(c[0], Math.Max(c[1], c[2]));
        if (max > 0)
        {
            double inv = 1 - max, scaled = 1 - inv * inv * inv * inv;
            for (int i = 0; i < 3; i++) c[i] = c[i] + (c[i] * scaled / max - c[i]) * brightness;   // mix(color, notGamma, brightness)
        }
        return c;
    }

    static double[] D(Vector3 v) => new double[] { v.x, v.y, v.z };

    static void Lightmap()
    {
        float keepBright = BlockShading.Brightness, keepMin = BlockShading.MinLight;
        var envs = new (string name, LightEnv env)[]
        {
            ("noon", LightEnv.Noon), ("midnight", DayLight.Evaluate("overworld", 18000, 0, 0, false)),
            ("dusk", DayLight.Evaluate("overworld", 12500, 0, 0, false)), ("thunder", DayLight.Evaluate("overworld", 6000, 1, 1, false)),
            ("nether", DayLight.Evaluate("the_nether", 6000, 0, 0, false)), ("end", DayLight.Evaluate("the_end", 6000, 0, 0, false)),
        };
        foreach (float brightness in new[] { 0f, 0.5f, 1f })
        {
            BlockShading.Brightness = brightness;
            double worst = 0;
            foreach (var (name, env) in envs)
                for (int s = 0; s < 16; s++)
                    for (int b = 0; b < 16; b++)
                    {
                        var got = BlockShading.RawLightmap(env, s, b);
                        var want = Vanilla(env.SkyFactor, D(env.SkyColor), D(env.Ambient), env.BlockFactor, D(env.BlockTint), s, b, brightness);
                        worst = Math.Max(worst, Math.Max(Math.Abs(got.x - want[0]), Math.Max(Math.Abs(got.y - want[1]), Math.Abs(got.z - want[2]))));
                    }
            Check(worst < 1e-5, $"lightmap.fsh port matches the GLSL for noon, night, dusk, thunder, Nether, End at brightness {brightness} (worst {worst:E1})");
        }
        BlockShading.Brightness = 1f;
        // The noon environment reproduces the mesher's reference lightmap exactly (night falls by ratio from it).
        double refWorst = 0;
        for (int s = 0; s < 16; s++)
            for (int b = 0; b < 16; b++) refWorst = Math.Max(refWorst, (BlockShading.LightmapFor(LightEnv.Noon, s, b) - BlockShading.Lightmap(s, b)).magnitude);
        Check(refWorst < 1e-6, "LightmapFor(Noon) equals the baked reference lightmap");
        var night = DayLight.Evaluate("overworld", 18000, 0, 0, false);
        var open = BlockShading.LightmapFor(night, 15, 0); var torch = BlockShading.LightmapFor(night, 15, 14); var cave = BlockShading.LightmapFor(night, 0, 0);
        Check(open.y < 0.6f && open.z > open.x && torch.y > 0.9f && Near(cave, BlockShading.Lightmap(0, 0)),
              $"night: open ground dim and bluish ({open.x:F2},{open.y:F2},{open.z:F2}), a torch bright ({torch.y:F2}), caves as at noon");
        var nether = BlockShading.LightmapFor(DayLight.Evaluate("the_nether", 0, 0, 0, false), 0, 0);
        Check(nether.x > BlockShading.Lightmap(0, 0).x + 0.1f && nether.x > nether.z, $"Nether: warm ambient lifts the dark ({nether.x:F2},{nether.y:F2},{nether.z:F2})");
        BlockShading.MinLight = -1f;
        var vanillaNight = BlockShading.LightmapFor(night, 15, 0);
        var want15 = Vanilla(night.SkyFactor, D(night.SkyColor), D(night.Ambient), 1.4, D(night.BlockTint), 15, 0, 1);
        Check(Near(vanillaNight, new Vector3((float)want15[0], (float)want15[1], (float)want15[2]), 1e-5f), "min_light -1: the game's own night lightmap");
        BlockShading.MinLight = keepMin; BlockShading.Brightness = keepBright;
    }

    static void Timeline()
    {
        Check(Near(DayLight.SkyFactorAt(6000), 1f) && Near(DayLight.SkyFactorAt(18000), 0.24f), "day.json: sky factor 1 at noon, 0.24 at midnight");
        Check(Near(DayLight.SkyFactorAt(12205), 1f - 0.76f * (12205 - 11270) / 1870f), "dusk: linear between 11270 and 13140");
        Check(Near(DayLight.SkyFactorAt(0), 0.24f + 0.76f * (24000 - 22860) / 1870f) && Near(DayLight.SkyFactorAt(24000 + 100), 0.24f + 0.76f * 1240f / 1870f),
              "dawn wraps across tick 0 (22860 -> 730 next day)");
        var c = DayLight.SkyColorAt(18000);
        Check(Near(c, new Vector3(0x7a / 255f, 0x7a / 255f, 1f)) && Near(DayLight.SkyColorAt(6000), Vector3.one), "sky light colour: white by day, #7a7aff at night");
        Check(Near(DayLight.SkyFactorAt(-6000), DayLight.SkyFactorAt(18000)), "negative day time wraps");
    }

    static void Weather()
    {
        var rain = DayLight.Evaluate("overworld", 6000, 1, 0, false);
        var thunder = DayLight.Evaluate("overworld", 6000, 1, 1, false);
        Check(Near(rain.SkyFactor, 1f + (0.24f - 1f) * 0.3125f) && Near(thunder.SkyFactor, 1f + (0.24f - 1f) * 0.52734375f),
              $"WeatherAttributes: rain {rain.SkyFactor:F4}, thunder {thunder.SkyFactor:F4} at noon");
        float afterRain = Mid(1f, 0.7625f, 0.5f), halfThunder = Mid(afterRain, Mid(afterRain, 0.24f, 0.52734375f), 0.5f);
        Check(Near(DayLight.Evaluate("overworld", 6000, 1, 0.5f, false).SkyFactor, halfThunder),
              $"rain 1, thunder 0.5: the rain layer at 0.5, then the thunder layer at 0.5 ({halfThunder:F4})");
        Check(Near(DayLight.Evaluate("overworld", 18000, 1, 1, false).SkyFactor, 0.24f), "a storm at night stays at the night factor");
        Check(Near(DayLight.Evaluate("overworld", 18000, 1, 1, true).SkyFactor, 1f), "lightning flash lights the sky fully");
        Check(Near(DayLight.Evaluate("overworld", 6000, 0, 1, false).SkyFactor, 1f), "thunder without rain counts as nothing");
        var end = DayLight.Evaluate("the_end", 18000, 0, 0, false);
        Check(end.SkyFactor == 0 && Near(end.Ambient, DayLight.Rgb(0x3f473f)), "the End: no sky light, its own ambient");
        var ratios = new float[768];
        DayLight.Ratios(LightEnv.Noon, ratios);
        Check(ratios.All(r => Near(r, 1f, 1e-5f)), "noon: every ratio is 1 (the day looks exactly as before)");
        DayLight.Ratios(DayLight.Evaluate("overworld", 18000, 0, 0, false), ratios);
        float R(int s, int b, int ch) => ratios[(s * 16 + b) * 3 + ch];
        Check(R(15, 0, 1) < 0.6f && R(15, 14, 1) > 0.9f && Near(R(0, 0, 1), 1f) && Near(R(0, 15, 1), 1f),
              $"midnight ratios: open sky {R(15, 0, 1):F2}, torch-lit {R(15, 14, 1):F2}, caves unchanged");
    }

    static float Mid(float a, float b, float t) => a + (b - a) * t;

    static void Codes()
    {
        Check(LightCode.Of(14.6f, 0.4f) == 15 * 16 && LightCode.Of(-1, 20) == 15 && LightCode.Of(7.5f, 7.49f) == 8 * 16 + 7, "LightCode rounds and clamps");
        int bad = 0;
        for (int c = 0; c < 256; c++)
        {
            byte back = LightCode.FromSnorm(LightCode.ToSnorm((byte)c));
            if (back != c && !(c == 241 && back == 242)) bad++;
        }
        Check(bad == 0, "light code survives the snorm8 normal w (only 241 reads as 242)");
        Check(LightCode.FromSnorm(127) == LightCode.OpenSky, "a mesh without light codes (w = 1) reads as open sky");
        var q = new Quad4 { L0 = LightCode.Of(15, 0), L1 = LightCode.Of(15, 0), L2 = LightCode.Of(13, 8), L3 = LightCode.Of(13, 8) };
        Check(LightCode.Bilerp(q, 0.5f, 0.5f) == LightCode.Of(14, 4), "light code bilerp inside a face");
    }

    static WorldModel World(int dx, int dy, int dz, Func<int, int, int, int> grid, Func<int, int, int, int> light, params string[] palette)
    {
        var w = new WorldModel { SnapshotId = 1, Dims = new Vector3Int(dx, dy, dz) };
        w.Palette.AddRange(palette);
        w.Grid = new ushort[dx * dy * dz];
        w.Light = new byte[dx * dy * dz];
        for (int y = 0; y < dy; y++) for (int z = 0; z < dz; z++) for (int x = 0; x < dx; x++) { w.Grid[w.Index(x, y, z)] = (ushort)grid(x, y, z); w.Light[w.Index(x, y, z)] = (byte)light(x, y, z); }
        return w;
    }

    sealed class Flat : IFaceSource { public FaceKey Face(string s, int d) => new FaceKey { Layer = -1, Color = BlockRules.FlatColor(s) }; }

    static void Merging()
    {
        // A sunlit stone floor where one cell gets torch light too. At noon on "Bright" both are full white, so they used
        // to merge into one quad; at night only the torch-lit part stays bright, so they mustn't.
        var w = World(4, 2, 4, (x, y, z) => y == 0 ? 1 : 0, (x, y, z) => y == 0 ? 0 : (x == 1 && z == 1 ? 0xFE : 0xF0), "minecraft:air", "minecraft:stone");
        var m = new MeshData();
        GreedyMesher.Build(w, new Vector3Int(0, 0, 0), new Flat(), m);
        int tops = 0; bool sameColours = true; Color32? first = null;
        for (int i = 0; i < m.Verts.Count; i += 4)
            if (Near(m.Verts[i].y, 1f) && Near(m.Verts[i + 2].y, 1f))
            {
                tops++;
                for (int k = 0; k < 4; k++) { var c = m.Colors[i + k]; first ??= c; if (c.r != first.Value.r) sameColours = false; }
            }
        Check(m.Light.Count == m.Colors.Count, "every block vertex has a light code");
        Check(tops > 1 && sameColours, $"noon-identical faces with different light don't merge ({tops} top quads, colours equal at noon: {sameColours})");
        var lit = Enumerable.Range(0, m.Verts.Count).Where(i => Near(m.Verts[i].y, 1f)).Select(i => m.Light[i]).Distinct().ToList();
        Check(lit.Contains(LightCode.Of(15, 0)) && lit.Any(c => LightCode.Block(c) > 0), "the floor's codes carry the torch light");
        var sm = SectionMesh.From(m, new Vector3Int(0, 0, 0));
        bool nw = true;
        for (int i = 0; i < m.Verts.Count; i++) if (LightCode.FromSnorm(sm.Verts[i].Nw) != m.Light[i] && m.Light[i] != 241) nw = false;
        Check(nw, "SectionMesh carries the light code in the normal's w");
        // Without light data: everything is open sky (night darkens all of it), and faces still merge as before.
        var dark = new WorldModel { SnapshotId = 1, Dims = w.Dims, Grid = w.Grid };
        dark.Palette.AddRange(w.Palette);
        GreedyMesher.Build(dark, new Vector3Int(0, 0, 0), new Flat(), m);
        Check(m.Light.All(c => c == LightCode.OpenSky) && m.Verts.Count == 6 * 4, $"no light data: open sky codes, a single box ({m.Verts.Count / 4} quads)");
    }

    static void Clock(Func<string, string> fixture)
    {
        var msg = Json.Parse(System.IO.File.ReadAllText(fixture("world_state.json"))) as Dictionary<string, object>;
        var c = new WorldClock();
        Check(!c.Known && c.Env.SameAs(LightEnv.Noon), "before world_state: noon");
        c.Apply(msg);
        Check(c.Known && c.DayTime == 18000 && c.Rain == 1 && c.Thunder == 1 && c.Dimension == "overworld", "world_state fixture: midnight thunderstorm, weather snaps on the first message");
        c.Step(1f);
        Check(Near((float)c.DayTime, 18020f) && Near((float)c.Time, 114020f), "the clock runs 20 ticks a second between messages");
        msg["rain"] = 0.0; msg["thunder"] = 0.0;
        c.Apply(msg);
        c.Step(2.5f);
        Check(Near(c.Rain, 0.5f, 1e-3f) && Near(c.Thunder, 0.5f, 1e-3f), $"rain fades 0.01 a tick like vanilla (rain {c.Rain:F3} after 50 ticks)");
        c.Step(10f);
        Check(c.Rain == 0 && c.Thunder == 0, "and stops");
        msg["fixed_time"] = true; msg["day_time"] = 6000.0;
        c.Apply(msg);
        c.Step(1f);
        Check(c.DayTime == 6000, "fixed_time: the clock stands still");
        c.Flash();
        Check(c.Flashing && Near(c.Env.SkyFactor, 1f), "lightning flash");
        c.Step(0.2f);
        Check(!c.Flashing, "the flash lasts 2 ticks");
        msg["dimension"] = "the_nether"; msg["world"] = "world_nether"; msg["rain"] = 1.0;
        c.Apply(msg);
        Check(c.Rain == 1 && c.Env.SkyFactor == 0, "another world snaps its weather; the Nether has no sky light");
        Check(new WorldClock { Known = true, Time = 24000 * 4 + 5 }.MoonPhase == 4, "moon phase: one a day, new moon on day 4");
    }
}
