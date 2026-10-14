// Engine-free maths for area setup: fitting block_size_m to the table, and a face/vertex budget estimate used as
// a soft cap before asking the server for a region (docs/QUEST_SETUP.md "Setting up a play area").
using System;

namespace TabletopCraft.World
{
    public struct MeshEstimate
    {
        public long Quads;
        public long Vertices => Quads * 4;
        public long Triangles => Quads * 2;
        public override string ToString() => $"~{Quads / 1000}k faces / ~{Vertices / 1000}k verts";
    }

    public static class AreaMath
    {
        // Calibrated from greedy-mesher counts on real generated terrain (docs/TESTING.md "Real terrain").
        // Quads ~= SurfacePerColumn * dx*dz (the visible surface) + CavePerBlock * blocks below the surface band.
        public const double SurfacePerColumn = 3.5;
        public const double CavePerBlock = 0.035;
        public const int SurfaceBand = 96;
        /// <summary>
        /// Extra quads from per-vertex light (BlockShading): faces with different light/AO don't merge. Measured on real
        /// terrain (docs/TESTING.md "Lighting and biome colours"): x1.54-1.63 with smooth lighting, x1.21-1.28 flat.
        /// 1.0 = no light data. Set from welcome.caps (light_v1) by TabletopApp.
        /// </summary>
        public static double LightFactor = 1.0;
        public static double LightFactorFor(bool serverLight, bool smooth) => !serverLight ? 1.0 : smooth ? 1.6 : 1.25;

        /// <summary>Default soft cap (ESTIMATE for Quest 3 at 72 Hz with the unlit block shader): 250k quads = 1M vertices, 500k triangles.</summary>
        public const long DefaultQuadBudget = 250_000;

        public const float MinBlockM = 0.001f, MaxBlockM = 0.25f;

        /// <summary>block_size_m that fits dx x dz blocks on a tableW x tableD metre table: min(tableW/dx, tableD/dz), clamped.</summary>
        public static float FitBlockSize(float tableW, float tableD, int dx, int dz, float margin = 0.95f)
        {
            if (dx <= 0 || dz <= 0 || tableW <= 0 || tableD <= 0) return 0.01f;
            float bs = Math.Min(tableW / dx, tableD / dz) * margin;
            return Math.Max(MinBlockM, Math.Min(MaxBlockM, bs));
        }

        public static MeshEstimate Estimate(int dx, int dy, int dz)
        {
            double cols = (double)dx * dz;
            double deep = Math.Max(0, dy - SurfaceBand);
            return new MeshEstimate { Quads = (long)Math.Ceiling((cols * SurfacePerColumn * Math.Min(dy, SurfaceBand) / SurfaceBand * 1.0 + cols * deep * CavePerBlock) * LightFactor) };
        }

        /// <summary>True when the estimate exceeds the budget and the user has not chosen to override.</summary>
        public static bool OverBudget(MeshEstimate e, long budget, bool overridden) => !overridden && e.Quads > budget;

        /// <summary>Clamp requested dims to the server's advertised caps (welcome/region_info limits).</summary>
        public static (int, int, int) ClampToCaps(int dx, int dy, int dz, int maxX, int maxY, int maxZ, long maxBlocks)
        {
            dx = Math.Max(1, Math.Min(dx, maxX)); dy = Math.Max(1, Math.Min(dy, maxY)); dz = Math.Max(1, Math.Min(dz, maxZ));
            while ((long)dx * dy * dz > maxBlocks && dy > 1) dy--;
            return (dx, dy, dz);
        }
    }
}
