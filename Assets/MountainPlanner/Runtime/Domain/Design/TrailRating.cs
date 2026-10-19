using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail's difficulty (TD-01 to TD-03, TD-06) and the profile the tools draw (TF-04). Slopes are grades in percent,
    /// the fall-line slope of the 1 m cells, as the Slope angle layer shows them.
    /// </summary>
    public static class TrailRating
    {
        /// <summary>TD-02: the steepest stretch is three stations (75 ft) averaged.</summary>
        public const int WindowStations = 3;

        public sealed class Result
        {
            /// <summary>Each station's median cross-section slope, percent (TD-01).</summary>
            public double[] StationSlope { get; internal set; }
            /// <summary>The graded surface at each station on the centreline (the profile's solid line).</summary>
            public double[] Surface { get; internal set; }
            public double MeanPercent { get; internal set; }
            public double MaxPercent { get; internal set; }
            /// <summary>The first station of the steepest 75 ft window.</summary>
            public int SteepestStation { get; internal set; }
            /// <summary>TD-02 v1: (3 × mean + max) / 4.</summary>
            public double ValuePercent { get; internal set; }
            public SlopeBands.Band Band => SlopeBands.Of(ValuePercent);
            public double DropMetres { get; internal set; }
        }

        /// <summary>The fall-line slope of cell (column, row), percent, from its four corners on <paramref name="surface"/>.</summary>
        public static double CellSlope(IHeightSource surface, EditLattice lattice, int column, int row)
        {
            double x0 = lattice.X(column), x1 = lattice.X(column + 1), y0 = lattice.Y(row), y1 = lattice.Y(row + 1);
            double a = surface.At(x0, y0), b = surface.At(x1, y0), c = surface.At(x0, y1), d = surface.At(x1, y1);
            double gx = ((b + d) - (a + c)) / 2, gy = ((a + b) - (c + d)) / 2;
            return Math.Sqrt(gx * gx + gy * gy) * 100;
        }

        /// <summary>
        /// TD-01, TD-02: rates a graded (or natural) trail on <paramref name="surface"/>, the ground with its grade.
        /// </summary>
        public static Result Rate(IHeightSource surface, TrailFootprint fp, TrailGrader.Graded grade)
        {
            var st = grade.Stations;
            int n = st.Length / 2;
            var slope = new double[n];
            var surf = new double[n];
            var cells = new List<double>();
            var seen = new HashSet<long>();
            for (int k = 0; k < n; k++)
            {
                double cx = st[2 * k], cy = st[2 * k + 1];
                surf[k] = surface.At(cx, cy);
                int a = Math.Max(0, k - 1), b = Math.Min(n - 1, k + 1);
                double ex = st[2 * b] - st[2 * a], ey = st[2 * b + 1] - st[2 * a + 1], len = Math.Sqrt(ex * ex + ey * ey);
                if (len <= 0) { slope[k] = k > 0 ? slope[k - 1] : 0; continue; }
                double nx = -ey / len, ny = ex / len;
                cells.Clear();
                seen.Clear();
                foreach (int side in new[] { 1, -1 })
                    for (double d = side > 0 ? 0 : 1; ; d += 1)
                    {
                        double x = cx + nx * d * side, y = cy + ny * d * side;
                        fp.Lattice.CellAt(x, y, out int c, out int r);
                        if (!fp.Contains(c, r)) break;
                        if (seen.Add(((long)c << 32) ^ (uint)r)) cells.Add(CellSlope(surface, fp.Lattice, c, r));
                    }
                slope[k] = cells.Count > 0 ? Median(cells) : k > 0 ? slope[k - 1] : 0;
            }

            // Length-weighted mean: each station stands for half the way to each neighbour.
            double sum = 0, weight = 0;
            var along = grade.Along;
            for (int k = 0; k < n; k++)
            {
                double w = (k + 1 < n ? along[k + 1] - along[k] : 0) / 2 + (k > 0 ? along[k] - along[k - 1] : 0) / 2;
                if (n == 1) w = 1;
                sum += slope[k] * w;
                weight += w;
            }
            double mean = weight > 0 ? sum / weight : 0;
            double max = 0;
            int steepest = 0;
            int win = Math.Min(WindowStations, n);
            for (int k = 0; k + win <= n; k++)
            {
                double s = 0;
                for (int t = 0; t < win; t++) s += slope[k + t];
                s /= win;
                if (s > max) { max = s; steepest = k; }
            }
            return new Result
            {
                StationSlope = slope,
                Surface = surf,
                MeanPercent = mean,
                MaxPercent = max,
                SteepestStation = steepest,
                ValuePercent = (3 * mean + max) / 4,
                DropMetres = n > 0 ? surf[0] - surf[n - 1] : 0,
            };
        }

        /// <summary>
        /// TD-06 v1, the estimate while painting: (3 × the mean + the 90th percentile) / 4 of every painted cell's slope on
        /// the natural ground. NaN for an empty footprint.
        /// </summary>
        public static double Estimate(IHeightSource ground, TrailFootprint fp)
        {
            if (fp.IsEmpty) return double.NaN;
            var all = new double[fp.Cells];
            int i = 0;
            double sum = 0;
            for (int r = fp.Row0; r < fp.Row0 + fp.Height; r++)
                for (int c = fp.Column0; c < fp.Column0 + fp.Width; c++)
                {
                    if (!fp.Contains(c, r)) continue;
                    double s = CellSlope(ground, fp.Lattice, c, r);
                    all[i++] = s;
                    sum += s;
                }
            Array.Sort(all);
            double p90 = all[Math.Min(all.Length - 1, (int)Math.Floor(0.9 * (all.Length - 1) + 0.5))];
            return (3 * (sum / all.Length) + p90) / 4;
        }

        static double Median(List<double> v)
        {
            v.Sort();
            int m = v.Count / 2;
            return v.Count % 2 == 1 ? v[m] : (v[m - 1] + v[m]) / 2;
        }
    }
}
