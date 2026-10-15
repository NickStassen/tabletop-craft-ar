// [nav] Which sections the renderer meshes (docs/ARCHITECTURE.md "Moving around"). Engine-free.
//
// The view margin is runway for gliding, not something to look at: the clip shader hides it. So only sections near
// the visible window are meshed (Active: the window plus Lead blocks on every side, plus how far the view travels in
// LeadSeconds in the direction of motion), and meshes further out than KeepExtra beyond that are dropped (Keep).
// Sections outside Active stay dirty in WorldModel until they come close. A delta from the server then costs no
// meshing at all when it arrives (its new sections are deep in the margin); they are meshed as the window approaches,
// the ones about to show first (Score).
using System;
using UnityEngine;

namespace TabletopCraft.World
{
    /// <summary>A box of section keys [Min, Max) in region-local section coordinates.</summary>
    public struct SectionBox
    {
        public Vector3Int Min, Max;
        public static readonly SectionBox All = new SectionBox { Min = new Vector3Int(int.MinValue, int.MinValue, int.MinValue), Max = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue) };
        public bool Contains(Vector3Int k) => k.x >= Min.x && k.y >= Min.y && k.z >= Min.z && k.x < Max.x && k.y < Max.y && k.z < Max.z;
        public int Count => Math.Max(0, Max.x - Min.x) * Math.Max(0, Max.y - Min.y) * Math.Max(0, Max.z - Min.z);
        public bool Equals(SectionBox o) => Min == o.Min && Max == o.Max;
        public override string ToString() => $"[{Min} .. {Max})";
    }

    public static class MeshScope
    {
        /// <summary>Blocks around the window that are meshed before they can show (turning, a small move).</summary>
        public static float Lead = 8f;
        /// <summary>Plus the distance the view travels in this many seconds, on the side it moves towards.</summary>
        public static float LeadSeconds = 0.6f;
        /// <summary>Most extra lead ahead, in blocks.</summary>
        public static float MaxLead = 48f;
        /// <summary>Meshes further than this beyond the active box are dropped (and meshed again if the window comes back).</summary>
        public static float KeepExtra = 16f;

        /// <summary>Sections that intersect the box [lo, hi) of region-local blocks, clamped to the region's n sections.</summary>
        public static SectionBox Covering(Vector3 lo, Vector3 hi, Vector3Int n)
        {
            var b = new SectionBox();
            for (int a = 0; a < 3; a++)
            {
                int s0 = (int)Math.Floor(lo[a] / WorldModel.Section), s1 = (int)Math.Ceiling(hi[a] / WorldModel.Section);
                b.Min[a] = Math.Max(0, Math.Min(n[a], s0));
                b.Max[a] = Math.Max(b.Min[a], Math.Min(n[a], s1));
            }
            return b;
        }

        /// <summary>Region-local block box of the visible window.</summary>
        public static (Vector3 lo, Vector3 hi) Window(NavState nav)
        {
            var lo = nav.WindowMin - (Vector3)nav.Origin;
            return (lo, lo + (Vector3)nav.Window);
        }

        /// <summary>Sections intersecting the visible window (holes here are on screen).</summary>
        public static SectionBox Visible(NavState nav, Vector3Int n)
        {
            var (lo, hi) = Window(nav);
            return Covering(lo, hi, n);
        }

        /// <summary>Sections to keep meshed: the window grown by Lead, and by the travel of LeadSeconds towards the motion.</summary>
        public static SectionBox Active(NavState nav, Vector3Int n) => Grown(nav, n, 0f);

        /// <summary>Sections whose meshes are kept: Active grown by KeepExtra. Meshes outside are dropped.</summary>
        public static SectionBox Keep(NavState nav, Vector3Int n) => Grown(nav, n, KeepExtra);

        static SectionBox Grown(NavState nav, Vector3Int n, float extra)
        {
            // An old region without a margin shows everything it has.
            if (nav.Margin.x <= 0 && nav.Margin.y <= 0 && nav.Margin.z <= 0) return Covering(new Vector3(0, 0, 0), nav.Dims, n);
            var (lo, hi) = Window(nav);
            var v = nav.Velocity;
            for (int a = 0; a < 3; a++)
            {
                float ahead = Math.Min(MaxLead, Math.Abs(v[a]) * LeadSeconds);
                lo[a] -= Lead + extra + (v[a] < 0 ? ahead : 0);
                hi[a] += Lead + extra + (v[a] > 0 ? ahead : 0);
            }
            return Covering(lo, hi, n);
        }

        /// <summary>
        /// Meshing order, lower first: sections on screen (nearest the focus first), then the ones ahead in the direction
        /// of motion, then the rest of the active box by distance.
        /// </summary>
        public static float Score(Vector3Int k, SectionBox visible, Vector3 focus, Vector3 velocity)
        {
            float S = WorldModel.Section;
            float dx = (k.x + 0.5f) * S - focus.x, dy = (k.y + 0.5f) * S - focus.y, dz = (k.z + 0.5f) * S - focus.z;
            float d = dx * dx + dy * dy * 0.25f + dz * dz;   // horizontal distance matters most
            if (visible.Contains(k)) return d;
            float vlen = (float)Math.Sqrt(velocity.x * velocity.x + velocity.y * velocity.y + velocity.z * velocity.z);
            float along = vlen > 1e-3f ? (dx * velocity.x + dy * velocity.y + dz * velocity.z) / vlen : 0f;
            return 1e7f + d - (along > 0 ? 1e6f : 0f);
        }

        /// <summary>Share of the measured meshing rate a glide may use (the rest is for edits, refreshes and slack).</summary>
        public static float Utilisation = 0.8f;

        /// <summary>
        /// Fastest pan (blocks per second) along dir at which meshing keeps up: every 16 blocks of travel along an axis
        /// bring a new slice of the active box (its cross-section, in sections) into it, so the speed is
        /// rate * Utilisation * 16 / cross-section. sectionsPerSecond is MeshRateMeter.Rate; 0 (unknown) = no cap.
        /// </summary>
        public static float MeshSpeedCap(float sectionsPerSecond, SectionBox active, Vector3 dir)
        {
            if (sectionsPerSecond <= 0 || active.Count == 0) return float.MaxValue;
            float len = (float)Math.Sqrt(dir.x * dir.x + dir.y * dir.y + dir.z * dir.z);
            if (len < 1e-6f) return float.MaxValue;
            int ex = active.Max.x - active.Min.x, ey = active.Max.y - active.Min.y, ez = active.Max.z - active.Min.z;
            float perBlock = (Math.Abs(dir.x) * ey * ez + Math.Abs(dir.y) * ex * ez + Math.Abs(dir.z) * ex * ey) / len / WorldModel.Section;
            return perBlock <= 0 ? float.MaxValue : sectionsPerSecond * Utilisation / perBlock;
        }
    }

    /// <summary>
    /// [nav] Sections meshed per second while every worker was busy: what meshing can sustain on this device (Quest or PC).
    /// Fed once per frame by the renderer; an EMA over 0.5 s windows of saturated time. 0 until measured.
    /// </summary>
    public sealed class MeshRateMeter
    {
        public float Rate;
        float _t; int _n;
        /// <summary>dt: the frame time; saturated: all workers were busy during it; meshed: sections finished during it.</summary>
        public void Frame(float dt, bool saturated, int meshed)
        {
            if (!saturated || dt <= 0) return;
            _t += dt; _n += meshed;
            if (_t < 0.5f) return;
            float s = _n / _t;
            Rate = Rate <= 0 ? s : 0.7f * Rate + 0.3f * s;
            _t = 0; _n = 0;
        }
    }
}
