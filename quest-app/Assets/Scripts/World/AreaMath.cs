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
        // Calibrated from mesher counts on real generated terrain with the 26.3 pack: greedy cubes plus block models
        // (docs/TESTING.md "Block models from the resource pack").
        // Quads ~= SurfacePerColumn * dx*dz (the visible surface) + CavePerBlock * blocks below the surface band.
        public const double SurfacePerColumn = 5.5;
        public const double CavePerBlock = 0.04;
        public const int SurfaceBand = 96;
        /// <summary>
        /// Extra faces from per-vertex light (BlockShading): greedy faces with different light/AO don't merge (model
        /// and fluid faces barely change). Measured with the 26.3 pack on real terrain (docs/TESTING.md "Lighting with
        /// models and water"): x1.24-1.38 with smooth lighting, x1.04-1.17 flat. 1.0 = no light data. Set from
        /// welcome.caps (light_v1) by TabletopApp.
        /// </summary>
        public static double LightFactor = 1.0;
        public static double LightFactorFor(bool serverLight, bool smooth) => !serverLight ? 1.0 : smooth ? 1.35 : 1.15;

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

        // [nav] On real terrain (seed 12345, docs/TESTING.md "Navigation and bigger areas") 41-56% of a region's sections
        // are non-empty. [area] They no longer are a mesh (and one or two draw calls) each: the renderer merges them into
        // batches of BatchRules.Columns^2 section columns over the full height (SectionBatches.cs), so the draw calls
        // follow the batch count, about 100 renderers / 200 draw calls for a 256-wide window with its margin.
        public const double NonEmptyFraction = 0.5;
        /// <summary>Default soft cap on mesh renderers (batches; ESTIMATE: ~2 draw calls each, built-in pipeline on Quest 3).</summary>
        public const int DefaultSectionBudget = 300;

        /// <summary>Sections in a region, and the non-empty ones that get meshed (estimate).</summary>
        public static long Sections(int dx, int dy, int dz) => (long)((dx + 15) / 16) * ((dy + 15) / 16) * ((dz + 15) / 16);
        public static long EstimateSections(int dx, int dy, int dz) => (long)Math.Ceiling(Sections(dx, dy, dz) * NonEmptyFraction);
        /// <summary>[area] Mesh renderers (batches) for a region, worst-case alignment: one extra batch row and column.</summary>
        public static long EstimateMeshes(int dx, int dy, int dz)
        {
            int c = Math.Max(1, BatchRules.Columns);
            long bx = ((dx + 15) / 16 + c - 1) / c + 1, bz = ((dz + 15) / 16 + c - 1) / c + 1;
            return dy <= 0 ? 0 : bx * bz;
        }

        /// <summary>Clamp requested dims to the server's advertised caps (welcome/region_info limits).</summary>
        public static (int, int, int) ClampToCaps(int dx, int dy, int dz, int maxX, int maxY, int maxZ, long maxBlocks)
        {
            dx = Math.Max(1, Math.Min(dx, maxX)); dy = Math.Max(1, Math.Min(dy, maxY)); dz = Math.Max(1, Math.Min(dz, maxZ));
            while ((long)dx * dy * dz > maxBlocks && dy > 1) dy--;
            return (dx, dy, dz);
        }
    }
}
