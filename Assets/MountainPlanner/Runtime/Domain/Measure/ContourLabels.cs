using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Measure
{
    /// <summary>One elevation label on a contour line (task 12b.2), in the map's local frame.</summary>
    public readonly struct ContourLabel
    {
        /// <summary>Local position (metres east and north) and the line's elevation (metres): the label sits on the line.</summary>
        public readonly float X, Z, Elevation;
        /// <summary>The line's direction on the ground: radians from east toward north (either way along it).</summary>
        public readonly float Angle;
        /// <summary>The ground's rise per metre across the line, so the view can tell how tightly lines are packed.</summary>
        public readonly float Rise;
        /// <summary>On a heavy (every fifth labelled) line: kept when zoomed out.</summary>
        public readonly bool Major;
        /// <summary>The elevation as shown, e.g. "8200".</summary>
        public readonly string Text;

        public ContourLabel(float x, float z, float elevation, float angle, float rise, bool major, string text)
        {
            X = x; Z = z; Elevation = elevation; Angle = angle; Rise = rise; Major = major; Text = text;
        }
    }

    /// <summary>
    /// Where contour labels go (task 12b.2): on the labelled lines (every 200 ft, or 50 m), at most one per line in
    /// each square of <c>spacingMetres</c>, at the crossing nearest the square's centre, so labels fall roughly that far
    /// apart along every line. Lines crossing near-flat ground (they wander) or very steep ground (they bunch up) get
    /// none. Deterministic: a fixed scan order, sorted output.
    /// </summary>
    public static class ContourLabels
    {
        public const double MinRise = 0.03, MaxRise = 1.5;   // 3% to 150%

        /// <summary>
        /// Labels for an elevation grid (metres, NaN where there's no data) of <paramref name="width"/> × <paramref name="height"/>
        /// cells of <paramref name="cellMetres"/>, west to east then south to north, whose first cell's corner is at
        /// local (<paramref name="originX"/>, <paramref name="originZ"/>).
        /// </summary>
        public static ContourLabel[] Place(int width, int height, double cellMetres, double originX, double originZ, float[] elevation,
                                           UnitSystem units, double spacingMetres = 600)
        {
            if (elevation == null || elevation.Length != width * height) throw new ArgumentException("One elevation per cell.", nameof(elevation));
            double interval = UnitFormat.ContourIntervalMetres(units) * UnitFormat.IndexEvery;
            int majorEvery = UnitFormat.MajorEvery / UnitFormat.IndexEvery;
            int square = Math.Max(2, (int)Math.Round(spacingMetres / cellMetres));
            int squaresX = (width + square - 1) / square;
            var best = new Dictionary<long, (double Distance, double X, double Y, int Level)>();

            void Consider(double fx, double fy, int level)
            {
                int sx = (int)(fx / square), sy = (int)(fy / square);
                double cx = (sx + 0.5) * square, cy = (sy + 0.5) * square;
                double d = (fx - cx) * (fx - cx) + (fy - cy) * (fy - cy);
                long key = ((long)(sy * squaresX + sx) << 20) + (level + (1 << 19));
                if (!best.TryGetValue(key, out var b) || d < b.Distance) best[key] = (d, fx, fy, level);
            }

            void Edge(int i0, int j0, int i1, int j1)
            {
                float a = elevation[j0 * width + i0], b = elevation[j1 * width + i1];
                if (float.IsNaN(a) || float.IsNaN(b) || a == b) return;
                int la = (int)Math.Floor(a / interval), lb = (int)Math.Floor(b / interval);
                for (int k = Math.Min(la, lb) + 1; k <= Math.Max(la, lb); k++)
                {
                    double t = (k * interval - a) / (b - a);
                    Consider(i0 + (i1 - i0) * t, j0 + (j1 - j0) * t, k);
                }
            }

            for (int j = 0; j < height; j++)
                for (int i = 0; i < width; i++)
                {
                    if (i + 1 < width) Edge(i, j, i + 1, j);
                    if (j + 1 < height) Edge(i, j, i, j + 1);
                }

            var keys = new List<long>(best.Keys);
            keys.Sort();
            var labels = new List<ContourLabel>(keys.Count);
            foreach (long key in keys)
            {
                var c = best[key];
                int i = Math.Min(width - 1, (int)Math.Round(c.X)), j = Math.Min(height - 1, (int)Math.Round(c.Y));
                double east = Difference(elevation, width, height, i, j, 1, 0) / cellMetres;
                double north = Difference(elevation, width, height, i, j, 0, 1) / cellMetres;
                double rise = Math.Sqrt(east * east + north * north);
                if (rise < MinRise || rise > MaxRise) continue;
                double level = c.Level * interval;
                labels.Add(new ContourLabel(
                    (float)(originX + (c.X + 0.5) * cellMetres), (float)(originZ + (c.Y + 0.5) * cellMetres), (float)level,
                    (float)Math.Atan2(east, -north),   // along the line: perpendicular to the way uphill
                    (float)rise, c.Level % majorEvery == 0, UnitFormat.ContourLabel(level, units)));
            }
            return labels.ToArray();
        }

        static double Difference(float[] e, int width, int height, int x, int y, int dx, int dy)
        {
            int x0 = Math.Max(0, x - dx), y0 = Math.Max(0, y - dy), x1 = Math.Min(width - 1, x + dx), y1 = Math.Min(height - 1, y + dy);
            float a = e[y0 * width + x0], b = e[y1 * width + x1];
            if (float.IsNaN(a) || float.IsNaN(b) || (x1 - x0) + (y1 - y0) == 0) return 0;
            return (b - a) / ((x1 - x0) + (y1 - y0));
        }
    }
}
