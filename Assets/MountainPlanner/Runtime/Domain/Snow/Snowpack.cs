using System;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// The natural snowpack, version 0 (task 12b, owner 2026-10-02): a mid-winter snow depth worked out from the
    /// terrain alone, a stand-in until weather and the snow simulation arrive (Phase 4). It fills
    /// <see cref="SnowDepthField"/> when a mountain opens; the Snow depth info layer shows it, and thin snow lets
    /// the ground show through. Every factor is a smooth function of the terrain, so the same mountain always gets
    /// the same snow (no randomness, a fixed loop order, double maths).
    ///
    /// depth = base(elevation) × sun(aspect, slope) × wind(ridge or hollow) × canopy × shedding(slope)
    /// - Base: <see cref="ValleyMetres"/> on the site's low ground (its 5th elevation percentile) rising to
    ///   <see cref="SummitMetres"/> on its high ground (95th): more snow falls and less melts higher up.
    /// - Sun: south faces lose up to <see cref="SunLoss"/> of it, most on low ground; north faces keep
    ///   <see cref="ShadeGain"/> more. Directions are true north (the grid convergence is taken out).
    /// - Wind: ridges standing above their surroundings (about 56 m across) are scoured, hollows loaded, by up to
    ///   <see cref="WindShare"/>.
    /// - Canopy: dense forest holds back up to <see cref="CanopyLoss"/> in its branches.
    /// - Shedding: snow sluffs off slopes from <see cref="ShedStartDegrees"/> and is gone by
    ///   <see cref="ShedFullDegrees"/> (cliffs show rock, as A2 already draws).
    /// </summary>
    public static class Snowpack
    {
        public const double ValleyMetres = 0.3048;   // 12 in: iteration 1's depth, kept on the low ground
        public const double SummitMetres = 1.8;
        public const double SunLoss = 0.35, ShadeGain = 0.10;
        public const double WindShare = 0.3;
        public const int WindRadiusCells = 3;
        public const double CanopyLoss = 0.3;
        public const double ShedStartDegrees = 40, ShedFullDegrees = 60;

        /// <summary>
        /// Fills <paramref name="depth"/> (metres) from a grid of elevations (metres; NaN where there's no data) and
        /// canopy shares (0–1), both <paramref name="width"/> × <paramref name="height"/> cells of
        /// <paramref name="cellMetres"/>, west to east then south to north (as <see cref="SnowDepthField"/>).
        /// <paramref name="gridConvergenceDegrees"/> is the angle from grid north clockwise to true north.
        /// </summary>
        public static void Compute(int width, int height, double cellMetres, float[] elevation, float[] canopy,
                                   double gridConvergenceDegrees, float[] depth)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (!(cellMetres > 0)) throw new ArgumentOutOfRangeException(nameof(cellMetres));
            int n = width * height;
            if (elevation == null || elevation.Length != n) throw new ArgumentException("One elevation per cell.", nameof(elevation));
            if (canopy == null || canopy.Length != n) throw new ArgumentException("One canopy share per cell.", nameof(canopy));
            if (depth == null || depth.Length != n) throw new ArgumentException("One depth per cell.", nameof(depth));

            var (low, high) = Percentiles(elevation, 0.05, 0.95);
            double span = high - low;
            double[] sums = SummedArea(elevation, width, height, out int[] counts);
            double gamma = gridConvergenceDegrees * Math.PI / 180;

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int k = y * width + x;
                    double e = elevation[k];
                    if (double.IsNaN(e))
                    {
                        depth[k] = (float)ValleyMetres;
                        continue;
                    }
                    double t = span > 1 ? Clamp01((e - low) / span) : 0;
                    double baseDepth = ValleyMetres + (SummitMetres - ValleyMetres) * t;

                    // Slope and aspect from central differences (one-sided at the edges and beside missing data).
                    double east = Gradient(elevation, width, height, x, y, 1, 0) / cellMetres;
                    double north = Gradient(elevation, width, height, x, y, 0, 1) / cellMetres;
                    double slope = Math.Atan(Math.Sqrt(east * east + north * north)) * 180 / Math.PI;
                    // The way the slope faces: downhill, as a compass bearing from grid north, then from true north.
                    double bearing = Math.Atan2(-east, -north) - gamma;
                    double southness = -Math.Cos(bearing);   // 1 facing south, −1 facing north
                    double facing = SmoothStep(5, 30, slope);   // flat ground faces nowhere
                    double sun = 1 - SunLoss * Math.Max(0, southness) * facing * (1 - 0.5 * t)
                                   + ShadeGain * Math.Max(0, -southness) * facing;

                    double mean = WindowMean(sums, counts, width, height, x, y, WindRadiusCells);
                    double rise = e - mean;   // metres above (ridge) or below (hollow) the surroundings
                    double wind = 1 - WindShare * SmoothStep(1, 6, rise) + WindShare * SmoothStep(1, 6, -rise);

                    double trees = 1 - CanopyLoss * Clamp01(canopy[k]);
                    double shed = 1 - SmoothStep(ShedStartDegrees, ShedFullDegrees, slope);

                    depth[k] = (float)Math.Max(0, baseDepth * sun * wind * trees * shed);
                }
        }

        /// <summary>The p-th elevation percentiles (NaN cells skipped), from a 4,096-bin histogram: exact to a fraction of a metre.</summary>
        static (double Low, double High) Percentiles(float[] values, double pLow, double pHigh)
        {
            double min = double.MaxValue, max = double.MinValue;
            long count = 0;
            foreach (float v in values)
            {
                if (float.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                count++;
            }
            if (count == 0) return (0, 0);
            if (max - min < 1e-6) return (min, max);
            const int bins = 4096;
            var histogram = new long[bins];
            double scale = (bins - 1) / (max - min);
            foreach (float v in values)
                if (!float.IsNaN(v)) histogram[(int)((v - min) * scale)]++;
            double At(double p)
            {
                long target = (long)Math.Ceiling(p * count), seen = 0;
                for (int b = 0; b < bins; b++)
                {
                    seen += histogram[b];
                    if (seen >= target) return min + (b + 0.5) / scale;
                }
                return max;
            }
            return (At(pLow), At(pHigh));
        }

        /// <summary>Elevation difference across a cell (two cells apart, halved; one cell at an edge or beside missing data).</summary>
        static double Gradient(float[] e, int width, int height, int x, int y, int dx, int dy)
        {
            int x0 = x - dx, y0 = y - dy, x1 = x + dx, y1 = y + dy;
            double centre = e[y * width + x];
            bool hasBefore = x0 >= 0 && y0 >= 0 && !float.IsNaN(e[y0 * width + x0]);
            bool hasAfter = x1 < width && y1 < height && !float.IsNaN(e[y1 * width + x1]);
            if (hasBefore && hasAfter) return (e[y1 * width + x1] - e[y0 * width + x0]) * 0.5;
            if (hasAfter) return e[y1 * width + x1] - centre;
            if (hasBefore) return centre - e[y0 * width + x0];
            return 0;
        }

        /// <summary>Summed-area tables of elevation and of cells with data, (width + 1) × (height + 1).</summary>
        static double[] SummedArea(float[] e, int width, int height, out int[] counts)
        {
            int w = width + 1;
            var sums = new double[w * (height + 1)];
            counts = new int[w * (height + 1)];
            for (int y = 0; y < height; y++)
            {
                double row = 0;
                int rowCount = 0;
                for (int x = 0; x < width; x++)
                {
                    float v = e[y * width + x];
                    if (!float.IsNaN(v)) { row += v; rowCount++; }
                    sums[(y + 1) * w + x + 1] = sums[y * w + x + 1] + row;
                    counts[(y + 1) * w + x + 1] = counts[y * w + x + 1] + rowCount;
                }
            }
            return sums;
        }

        static double WindowMean(double[] sums, int[] counts, int width, int height, int x, int y, int r)
        {
            int w = width + 1;
            int x0 = Math.Max(0, x - r), y0 = Math.Max(0, y - r), x1 = Math.Min(width, x + r + 1), y1 = Math.Min(height, y + r + 1);
            double sum = sums[y1 * w + x1] - sums[y0 * w + x1] - sums[y1 * w + x0] + sums[y0 * w + x0];
            int count = counts[y1 * w + x1] - counts[y0 * w + x1] - counts[y1 * w + x0] + counts[y0 * w + x0];
            return count > 0 ? sum / count : 0;
        }

        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        static double SmoothStep(double a, double b, double v)
        {
            double t = Clamp01((v - a) / (b - a));
            return t * t * (3 - 2 * t);
        }
    }
}
