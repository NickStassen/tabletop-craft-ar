// [living] Time of day, weather and dimension light like vanilla 26.3 (PROTOCOL.md "World state: time and weather").
// Engine-free (Tests~ checks it against the game's formulas).
//
//   LightEnv    the inputs of 26.x's lightmap.fsh (LightmapRenderState): sky factor and colour, ambient colour, block
//               factor and tint. BlockShading.RawLightmap evaluates the shader for one entry.
//   DayLight    vanilla's environment attributes for the lightmap: the overworld day timeline (data/minecraft/timeline/
//               day.json, sky_light_factor and sky_light_color tracks, linear between keyframes, wrapping at 24000),
//               the weather layers (WeatherAttributes: rain lerps the sky factor toward 0.24 by 0.3125 and the sky colour
//               toward the night colour; thunder by 0.52734375), the lightning flash (ClientLevel: factor 1 while the sky
//               flashes) and the Nether's and End's own ambient and sky values (dimension_type/*.json).
//   WorldClock  the latest world_state plus the client's prediction between messages: the clock runs at 20 ticks a second
//               and rain and thunder fade by 0.01 a tick like ServerLevel's rain and thunder levels.
//   LightCode   per-vertex light of the block meshes: the meshers bake vertex colours with the noon lightmap and keep the
//               (sky, block) light each corner was lit with; the block shaders multiply by Ratio(env) for that code, so
//               night falls without re-meshing. Carried in the batch vertex normal's spare w byte.
using System;
using System.Collections.Generic;
using TabletopCraft.Net;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>The lightmap inputs (26.x LightmapRenderState): colours are 0..1 sRGB values as the game uses them.</summary>
    public struct LightEnv
    {
        public float SkyFactor, BlockFactor;
        public Vector3 SkyColor, Ambient, BlockTint;

        /// <summary>The overworld at noon in clear weather: the reference the meshers bake vertex colours with.</summary>
        public static LightEnv Noon => new LightEnv
        {
            SkyFactor = 1f, BlockFactor = 1.4f, SkyColor = new Vector3(1, 1, 1), Ambient = DayLight.Rgb(0x0a0a0a), BlockTint = DayLight.Rgb(0xffd88c),
        };

        public bool SameAs(in LightEnv o) =>
            Mathf.Abs(SkyFactor - o.SkyFactor) < 1e-4f && Mathf.Abs(BlockFactor - o.BlockFactor) < 1e-4f
            && (SkyColor - o.SkyColor).sqrMagnitude < 1e-8f && (Ambient - o.Ambient).sqrMagnitude < 1e-8f && (BlockTint - o.BlockTint).sqrMagnitude < 1e-8f;

        public override string ToString() => $"sky {SkyFactor:F3} ({SkyColor.x:F2},{SkyColor.y:F2},{SkyColor.z:F2}) ambient ({Ambient.x:F3},{Ambient.y:F3},{Ambient.z:F3})";
    }

    /// <summary>
    /// The light a block mesh vertex was lit with: rounded sky * 16 + block (0..255). Stored in the batch vertex's normal
    /// w byte (SNorm8) shifted so that a mesh without it (float3 normals read as w = 1) means open sky: sbyte = ((code +
    /// 15) &amp; 255) - 128. Byte -128 reads like -127 on the GPU, so code 241 (sky 15, block 1) comes back as 242.
    /// </summary>
    public static class LightCode
    {
        public const byte OpenSky = 240, FullBright = 255;

        public static byte Of(float sky, float block)
        {
            int s = Mathf.Clamp((int)(sky + 0.5f), 0, 15), b = Mathf.Clamp((int)(block + 0.5f), 0, 15);
            return (byte)(s * 16 + b);
        }

        public static int Sky(byte code) => code >> 4;
        public static int Block(byte code) => code & 15;

        /// <summary>The light at (fu, fv) inside a face from its 4 corners (as BlockShading's colour Bilerp).</summary>
        public static byte Bilerp(in Quad4 c, float fu, float fv)
        {
            fu = Mathf.Clamp01(fu); fv = Mathf.Clamp01(fv);
            float w0 = (1 - fu) * (1 - fv), w1 = fu * (1 - fv), w2 = fu * fv, w3 = (1 - fu) * fv;
            float s = Sky(c.L0) * w0 + Sky(c.L1) * w1 + Sky(c.L2) * w2 + Sky(c.L3) * w3;
            float b = Block(c.L0) * w0 + Block(c.L1) * w1 + Block(c.L2) * w2 + Block(c.L3) * w3;
            return Of(s, b);
        }

        public static sbyte ToSnorm(byte code) => (sbyte)(((code + 15) & 255) - 128);

        /// <summary>What the shader decodes from the normal's w (TabletopDayLight.cginc), for tests.</summary>
        public static byte FromSnorm(sbyte s)
        {
            float w = Mathf.Max(-1f, s / 127f);
            int c = (int)Math.Round(w * 127f) + 128;
            return (byte)((c - 15) & 255);
        }
    }

    public static class DayLight
    {
        public const int Day = 24000;
        public const float NightSkyFactor = 0.24f, RainAlpha = 0.3125f, ThunderAlpha = 0.52734375f;
        public static readonly Vector3 NightSkyColor = Rgb(0x7a7aff);
        // data/minecraft/timeline/day.json: minecraft:visual/sky_light_factor and sky_light_color ("modifier": "multiply").
        static readonly int[] Keys = { 730, 11270, 13140, 22860 };
        static readonly float[] FactorKeys = { 1f, 1f, NightSkyFactor, NightSkyFactor };
        static readonly Vector3[] ColorKeys = { new Vector3(1, 1, 1), new Vector3(1, 1, 1), NightSkyColor, NightSkyColor };

        public static Vector3 Rgb(int c) => new Vector3(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);

        /// <summary>A keyframe track's value at a time of day: linear between keyframes, wrapping at the 24000-tick day.</summary>
        public static float Track(int[] ticks, float[] values, double t)
        {
            Segment(ticks, t, out int a, out int b, out float f);
            return values[a] + (values[b] - values[a]) * f;
        }

        public static Vector3 Track(int[] ticks, Vector3[] values, double t)
        {
            Segment(ticks, t, out int a, out int b, out float f);
            return values[a] + (values[b] - values[a]) * f;
        }

        static void Segment(int[] ticks, double t, out int a, out int b, out float f)
        {
            t = ((t % Day) + Day) % Day;
            int n = ticks.Length;
            a = n - 1;
            for (int i = 0; i < n; i++) if (ticks[i] <= t) a = i;
            b = (a + 1) % n;
            double t0 = ticks[a], t1 = ticks[b];
            if (t1 <= t0) t1 += Day;
            double tt = t < t0 ? t + Day : t;
            f = t1 > t0 ? (float)((tt - t0) / (t1 - t0)) : 0f;
        }

        /// <summary>Sky light factor and colour of the overworld timeline at a time of day (clear weather, no flash).</summary>
        public static float SkyFactorAt(double dayTime) => Track(Keys, FactorKeys, dayTime);
        public static Vector3 SkyColorAt(double dayTime) => Track(Keys, ColorKeys, dayTime);

        /// <summary>
        /// The lightmap inputs for a dimension at a time of day with rain and thunder levels (0..1, thunder only counts while
        /// raining) and the lightning flash. dimension: world_state.dimension.
        /// </summary>
        public static LightEnv Evaluate(string dimension, double dayTime, float rain, float thunder, bool flash)
        {
            var env = LightEnv.Noon;
            switch (dimension)
            {
                case "the_nether":
                    env.Ambient = Rgb(0x302821); env.SkyFactor = 0f; env.SkyColor = NightSkyColor;
                    return env;
                case "the_end":
                    env.Ambient = Rgb(0x3f473f); env.SkyFactor = 0f; env.SkyColor = Rgb(0xac60cd);
                    return env;
            }
            env.SkyFactor = SkyFactorAt(dayTime);
            env.SkyColor = SkyColorAt(dayTime);
            // WeatherAttributes: rain without thunder, then thunder, each lerping toward its modified value.
            thunder = Mathf.Clamp01(Mathf.Min(thunder, rain));
            float rainOnly = Mathf.Clamp01(rain) - thunder;
            if (rainOnly > 0) Weather(ref env, rainOnly, RainAlpha);
            if (thunder > 0) Weather(ref env, thunder, ThunderAlpha);
            if (flash) env.SkyFactor = 1f;   // ClientLevel: a lightning flash lights the sky fully
            return env;
        }

        static void Weather(ref LightEnv env, float level, float alpha)
        {
            float f = env.SkyFactor + (NightSkyFactor - env.SkyFactor) * alpha;   // FloatModifier.ALPHA_BLEND
            Vector3 c = env.SkyColor + (NightSkyColor - env.SkyColor) * alpha;     // ColorModifier.ALPHA_BLEND_RGB
            env.SkyFactor += (f - env.SkyFactor) * level;
            env.SkyColor += (c - env.SkyColor) * level;
        }

        /// <summary>
        /// The per-code ratio the block shaders multiply noon-lit vertex colours by: LightmapFor(env) / the noon lightmap,
        /// per channel, for every (sky, block) code. rgb gets 256 * 3 floats (index code * 3 + channel).
        /// </summary>
        public static void Ratios(in LightEnv env, float[] rgb)
        {
            for (int code = 0; code < 256; code++)
            {
                int s = code >> 4, b = code & 15;
                var now = BlockShading.LightmapFor(env, s, b);
                var noon = BlockShading.Lightmap(s, b);
                rgb[code * 3] = now.x / Mathf.Max(noon.x, 1e-4f);
                rgb[code * 3 + 1] = now.y / Mathf.Max(noon.y, 1e-4f);
                rgb[code * 3 + 2] = now.z / Mathf.Max(noon.z, 1e-4f);
            }
        }
    }

    /// <summary>The world_state the server last sent, predicted forward between messages (main thread).</summary>
    public sealed class WorldClock
    {
        public bool Known;
        public string World, Dimension = "overworld";
        public bool Skylight = true, FixedTime, Ticking = true;
        public double Time, DayTime;
        /// <summary>What the server says (0/1) and the shown levels, fading by 0.01 a tick like vanilla's rain levels.</summary>
        public float RainTarget, ThunderTarget, Rain, Thunder;
        float _flash;

        /// <summary>Takes a world_state message. A new world or dimension snaps the weather instead of fading it.</summary>
        public void Apply(Dictionary<string, object> m)
        {
            string world = m.S("world"), dim = m.S("dimension") ?? "overworld";
            bool snap = !Known || world != World || dim != Dimension;
            World = world; Dimension = dim;
            Skylight = !m.ContainsKey("skylight") || m.B("skylight");
            Time = m.D("time", 0); DayTime = m.D("day_time", 0);
            FixedTime = m.B("fixed_time");
            Ticking = !m.ContainsKey("ticking") || m.B("ticking");
            RainTarget = (float)m.D("rain", 0); ThunderTarget = (float)m.D("thunder", 0);
            if (snap) { Rain = RainTarget; Thunder = ThunderTarget; }
            Known = true;
        }

        /// <summary>Advances the clock and the weather fades by dt seconds.</summary>
        public void Step(float dt)
        {
            if (!Known) return;
            float ticks = dt * 20f;
            if (Ticking && !FixedTime) { Time += ticks; DayTime = (DayTime + ticks) % DayLight.Day; }
            Rain = Mathf.MoveTowards(Rain, RainTarget, 0.01f * ticks);
            Thunder = Mathf.MoveTowards(Thunder, ThunderTarget, 0.01f * ticks);
            if (_flash > 0) _flash -= dt;
        }

        /// <summary>A lightning bolt: the sky flashes for vanilla's 2 ticks.</summary>
        public void Flash() => _flash = 0.1f;
        public bool Flashing => _flash > 0;

        /// <summary>The lightmap inputs now (noon before the first world_state).</summary>
        public LightEnv Env => Known ? DayLight.Evaluate(Dimension, DayTime, Rain, Thunder, Flashing) : LightEnv.Noon;

        /// <summary>Moon phase 0..7 (full moon first), timeline/moon.json: one phase a day.</summary>
        public int MoonPhase => (int)(((long)Math.Floor(Time / DayLight.Day) % 8 + 8) % 8);
    }
}
