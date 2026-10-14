// What the headset's eye is in (air, water, lava, powder snow, inside an opaque block) and how the view should look
// there. Engine-free (tested in Tests~); EyeEffects.cs applies the result on the headset (fog globals in the block
// shaders, a tint over the passthrough, the water's inside faces).
// Like vanilla Camera.getFluidInCamera, only the eye position counts (no half-under-water split view):
//   - water: the eye cell holds water (water, waterlogged=true, kelp, seagrass, bubble column...) and the eye is below
//     its surface: y + 1 under more water, else y + amount/9 (FluidState.getHeight; a source is 8/9, so the top 1/9 of a
//     surface cell is air). Exactly at the surface is air (vanilla compares with <).
//   - lava: the same rule with lava. Powder snow: the whole cell.
//   - solid: an opaque full cube, or the solid half of a slab (vanilla shows the block's in-wall overlay there).
//   - outside the region: air (nothing is drawn there).
// EyeMediumFilter adds hysteresis (leave a medium only a few millimetres past its edge, after a short dwell) so the
// effect doesn't flicker while the head hovers at a surface, and fades looks in and out over ~0.2 s.
using System;
using UnityEngine;

namespace TabletopCraft.World
{
    public enum EyeMedium : byte { Air, Water, Lava, PowderSnow, Solid }

    /// <summary>Engine-free sRGB colour, 0..1 per channel.</summary>
    public struct Rgb
    {
        public float R, G, B;
        public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
        public static Rgb Hex(int c) => new Rgb(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
        public static Rgb operator *(Rgb a, float k) => new Rgb(a.R * k, a.G * k, a.B * k);
        public static Rgb Lerp(Rgb a, Rgb b, float t) => new Rgb(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        public override string ToString() => $"({R:F2}, {G:F2}, {B:F2})";
    }

    /// <summary>The view in a medium. Fog: colour lerp toward Fog by Strength x saturate((d - Start) / (End - Start)), d =
    /// distance from the eye in BLOCKS (EyeEffects scales by the block size). Tint: drawn over the passthrough, behind
    /// the miniature, with opacity TintAlpha. Air = Strength 0 and TintAlpha 0.</summary>
    public struct EyeLook
    {
        public Rgb Fog, Tint;
        public float Start, End, Strength, TintAlpha;
        public bool Visible => Strength > 0f || TintAlpha > 0f;

        public static EyeLook Lerp(EyeLook a, EyeLook b, float t) => new EyeLook
        {
            Fog = Rgb.Lerp(a.Fog, b.Fog, t), Tint = Rgb.Lerp(a.Tint, b.Tint, t),
            Start = a.Start + (b.Start - a.Start) * t, End = a.End + (b.End - a.End) * t,
            Strength = a.Strength + (b.Strength - a.Strength) * t, TintAlpha = a.TintAlpha + (b.TintAlpha - a.TintAlpha) * t,
        };

        /// <summary>The same colours and distances, switched off: fading toward this changes nothing but the amount.</summary>
        public EyeLook Off() { var o = this; o.Strength = 0; o.TintAlpha = 0; return o; }
    }

    public static class EyeMediumRules
    {
        public const int DefaultWaterColor = 0x3F76E4;   // vanilla default water colour (BiomeInfo.Water)

        /// <summary>The medium at p, region-local block coordinates in the Minecraft frame (x east, y up, z south).</summary>
        public static EyeMedium At(WorldModel w, Vector3 p)
        {
            if (w == null || !w.Loaded) return EyeMedium.Air;
            int x = (int)Math.Floor(p.x), y = (int)Math.Floor(p.y), z = (int)Math.Floor(p.z);
            if (!w.InBounds(x, y, z)) return EyeMedium.Air;
            ushort id = w.Get(x, y, z);
            string st = w.State(id);
            int kindOf = Describe(st);
            if (kindOf == PowderSnow) return EyeMedium.PowderSnow;
            if (w.ShapeOf(id) == BlockShape.Cube || kindOf == SlabDouble) return EyeMedium.Solid;
            if (kindOf == SlabBottom && p.y - y < 0.5f || kindOf == SlabTop && p.y - y >= 0.5f) return EyeMedium.Solid;
            byte info = FluidRules.Info(st);
            var kind = FluidRules.Kind(info);
            if (kind == FluidKind.None || !(p.y < y + SurfaceHeight(w, x, y, z, info))) return EyeMedium.Air;
            return kind == FluidKind.Water ? EyeMedium.Water : EyeMedium.Lava;
        }

        // Per state, cached (the per-frame lookup must not allocate): 0 other, or one of these.
        const int PowderSnow = 1, SlabBottom = 2, SlabTop = 3, SlabDouble = 4;
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Kinds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        static int Describe(string st)
        {
            if (Kinds.TryGetValue(st, out int k)) return k;
            string path = BlockRules.Path(st);
            k = path == "powder_snow" ? PowderSnow
              : !path.EndsWith("_slab") ? 0
              : st.Contains("type=double") ? SlabDouble : st.Contains("type=top") ? SlabTop : SlabBottom;
            Kinds[st] = k;
            return k;
        }

        /// <summary>Vanilla FluidState.getHeight: 1 when the cell above holds the same fluid, else amount / 9.</summary>
        public static float SurfaceHeight(WorldModel w, int x, int y, int z, byte info)
        {
            ushort above = w.Get(x, y + 1, z);
            if (above != 0xFFFF && FluidRules.Kind(FluidRules.Info(w.State(above))) == FluidRules.Kind(info)) return 1f;
            return FluidRules.Height(info);
        }

        /// <summary>True if the medium m is found within margin blocks of p along any axis (or at p).</summary>
        public static bool Near(WorldModel w, Vector3 p, EyeMedium m, float margin)
        {
            if (At(w, p) == m) return true;
            for (int a = 0; a < 3; a++)
                for (int s = -1; s <= 1; s += 2)
                {
                    var q = p; q[a] += s * margin;
                    if (At(w, q) == m) return true;
                }
            return false;
        }

        /// <summary>Biome water colour at the eye (the light work's blended biome tints), else the vanilla default.</summary>
        public static Rgb WaterColor(WorldModel w, Vector3 p)
        {
            int c = w?.Tints?.Tint(TintKind.Water, (int)Math.Floor(p.x), (int)Math.Floor(p.y), (int)Math.Floor(p.z)) ?? -1;
            return Rgb.Hex(c < 0 ? DefaultWaterColor : c);
        }

        /// <summary>Vanilla LocalPlayer.getWaterVision for the ticks spent under water (0..600): eyes adjust over 30 s.</summary>
        public static float WaterVision(float ticks)
        {
            if (ticks >= 600f) return 1f;
            float a = Math.Min(1f, Math.Max(0f, ticks / 100f)), b = ticks < 100f ? 0f : (ticks - 100f) / 500f;
            return a * 0.6f + b * 0.4f;
        }

        /// <summary>
        /// The look of a medium (distances in blocks). Water follows vanilla's water fog: from 8 blocks behind the eye to
        /// 96 x max(0.25, vision) blocks (24 blocks on entering, 96 after 30 s), toward the biome water colour, darker
        /// until the eyes adjust; the passthrough gets the same colour at 45%. Lava: vanilla 0.25..1 block of
        /// (0.6, 0.1, 0) fog, a strong orange tint. Powder snow: vanilla 0..2 blocks, frosted white. Solid (head inside
        /// a block): dark fog within 1.5 blocks and a 70% dark tint, dim but no blackout.
        /// </summary>
        public static EyeLook Look(EyeMedium m, Rgb water, float vision)
        {
            switch (m)
            {
                case EyeMedium.Water:
                {
                    var fog = water * (0.6f + 0.4f * vision);
                    return new EyeLook { Fog = fog, Start = 0f, End = 96f * Math.Max(0.5f, vision), Strength = 0.7f, Tint = fog, TintAlpha = 0.45f };   // lighter than vanilla (0.95, from 24 blocks): user feedback
                }
                case EyeMedium.Lava:
                    return new EyeLook { Fog = new Rgb(0.6f, 0.1f, 0f), Start = 0.25f, End = 1f, Strength = 1f, Tint = new Rgb(0.85f, 0.3f, 0.05f), TintAlpha = 0.8f };
                case EyeMedium.PowderSnow:
                    return new EyeLook { Fog = new Rgb(0.623f, 0.734f, 0.785f), Start = 0f, End = 2f, Strength = 1f, Tint = new Rgb(0.92f, 0.95f, 0.98f), TintAlpha = 0.7f };
                case EyeMedium.Solid:
                    return new EyeLook { Fog = new Rgb(0.03f, 0.03f, 0.03f), Start = 0f, End = 1.5f, Strength = 0.9f, Tint = new Rgb(0f, 0f, 0f), TintAlpha = 0.7f };
                default:
                    return default;
            }
        }
    }

    /// <summary>Per-frame medium tracking with hysteresis, the water-vision ramp and a ~0.2 s fade between looks.</summary>
    public sealed class EyeMediumFilter
    {
        /// <summary>Leaving a medium needs the eye this far (metres) past its edge; clamped to 0.05..1 block.</summary>
        public float MarginM = 0.004f;
        /// <summary>A new medium must hold this long before it is taken (one-frame blips are ignored).</summary>
        public float DwellSeconds = 0.05f;
        public float FadeSeconds = 0.2f;

        public EyeMedium Medium { get; private set; } = EyeMedium.Air;
        /// <summary>True on the update that switched Medium.</summary>
        public bool Changed { get; private set; }
        public EyeLook Look { get; private set; }
        public float VisionTicks { get; private set; }

        EyeMedium _candidate = EyeMedium.Air;
        float _candidateTime, _fadeT = 1f;
        EyeLook _from;

        /// <summary>eye: region-local block coordinates (Minecraft frame); blockSizeM: metres per block; dt: seconds.</summary>
        public EyeLook Update(WorldModel w, Vector3 eye, float blockSizeM, float dt)
        {
            Changed = false;
            var raw = EyeMediumRules.At(w, eye);
            if (raw != Medium && Medium != EyeMedium.Air)
            {
                float margin = Math.Min(1f, Math.Max(0.05f, MarginM / Math.Max(1e-4f, blockSizeM)));
                if (EyeMediumRules.Near(w, eye, Medium, margin)) raw = Medium;
            }
            if (raw == Medium) _candidateTime = 0f;
            else
            {
                if (raw != _candidate) { _candidate = raw; _candidateTime = 0f; }
                _candidateTime += dt;
                if (_candidateTime >= DwellSeconds)
                {
                    _from = Look;                 // cross-fade from whatever is showing now
                    Medium = raw; Changed = true; _fadeT = 0f; _candidateTime = 0f;
                }
            }

            // Vanilla waterVisionTime: +1 per tick under water (max 600), -10 per tick out of it.
            VisionTicks = Medium == EyeMedium.Water ? Math.Min(600f, VisionTicks + dt * 20f) : Math.Max(0f, VisionTicks - dt * 200f);

            var target = Medium == EyeMedium.Air ? _from.Off()
                : EyeMediumRules.Look(Medium, EyeMediumRules.WaterColor(w, eye), EyeMediumRules.WaterVision(VisionTicks));
            var from = _from;
            if (!from.Visible) from = target.Off();   // fading in from air: no colour shift, only the amount
            _fadeT = FadeSeconds <= 0f ? 1f : Math.Min(1f, _fadeT + dt / FadeSeconds);
            float s = _fadeT * _fadeT * (3f - 2f * _fadeT);
            Look = EyeLook.Lerp(from, target, s);
            if (_fadeT >= 1f) _from = Look;
            return Look;
        }
    }
}
