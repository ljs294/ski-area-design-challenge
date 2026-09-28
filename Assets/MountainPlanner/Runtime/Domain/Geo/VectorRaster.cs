using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// Anti-aliased rasterization of vector shapes onto a <see cref="GridSpec"/>, as 0–255 coverage per
    /// cell (0.3 §4.4: OpenStreetMap water, roads and buildings at 1 m; later, drawn trails).
    /// Deterministic: the same shapes always give the same bytes. Overlapping shapes combine by max.
    /// </summary>
    public static class VectorRaster
    {
        /// <summary>Sub-rows per cell row for polygon coverage (exact horizontally, 8 samples vertically).</summary>
        public const int SubRows = 8;

        /// <summary>
        /// Fills polygons with the even-odd rule. Rings may be given in any order and direction; holes are
        /// just more rings. Open rings are closed implicitly.
        /// </summary>
        public static void FillPolygon(byte[] target, GridSpec grid, IReadOnlyList<IReadOnlyList<AlbersPoint>> rings)
        {
            if (target.Length != grid.CellCount) throw new ArgumentException("The target doesn't match the grid.");
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            var edges = new List<(double X0, double Y0, double X1, double Y1)>();
            foreach (var ring in rings)
            {
                if (ring.Count < 3) continue;
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (a.Y == b.Y) continue;
                    // Grid space: x in cells from the west edge, y in cells from the north edge (down).
                    double ax = (a.X - grid.West) / grid.CellSize, ay = (grid.North - a.Y) / grid.CellSize;
                    double bx = (b.X - grid.West) / grid.CellSize, by = (grid.North - b.Y) / grid.CellSize;
                    edges.Add((ax, ay, bx, by));
                    minX = Math.Min(minX, Math.Min(ax, bx)); maxX = Math.Max(maxX, Math.Max(ax, bx));
                    minY = Math.Min(minY, Math.Min(ay, by)); maxY = Math.Max(maxY, Math.Max(ay, by));
                }
            }
            if (edges.Count == 0) return;
            int r0 = Math.Max(0, (int)Math.Floor(minY)), r1 = Math.Min(grid.Rows - 1, (int)Math.Ceiling(maxY));
            int c0 = Math.Max(0, (int)Math.Floor(minX)), c1 = Math.Min(grid.Columns - 1, (int)Math.Ceiling(maxX));
            if (r0 > r1 || c0 > c1) return;

            var coverage = new double[c1 - c0 + 1];
            var crossings = new List<double>();
            for (int r = r0; r <= r1; r++)
            {
                Array.Clear(coverage, 0, coverage.Length);
                bool any = false;
                for (int s = 0; s < SubRows; s++)
                {
                    double y = r + (s + 0.5) / SubRows;
                    crossings.Clear();
                    foreach (var e in edges)
                    {
                        // Half-open in y, so a vertex exactly on the scanline counts once.
                        if ((e.Y0 <= y && e.Y1 > y) || (e.Y1 <= y && e.Y0 > y))
                            crossings.Add(e.X0 + (y - e.Y0) / (e.Y1 - e.Y0) * (e.X1 - e.X0));
                    }
                    if (crossings.Count < 2) continue;
                    crossings.Sort();
                    for (int k = 0; k + 1 < crossings.Count; k += 2)
                    {
                        double x0 = Math.Max(c0, crossings[k]), x1 = Math.Min(c1 + 1, crossings[k + 1]);
                        if (x1 <= x0) continue;
                        any = true;
                        int first = (int)Math.Floor(x0), last = Math.Min(c1, (int)Math.Ceiling(x1) - 1);
                        for (int c = first; c <= last; c++)
                        {
                            double span = Math.Min(x1, c + 1) - Math.Max(x0, c);
                            if (span > 0) coverage[c - c0] += span / SubRows;
                        }
                    }
                }
                if (!any) continue;
                long row = (long)r * grid.Columns;
                for (int c = c0; c <= c1; c++)
                {
                    byte v = ToByte(coverage[c - c0]);
                    if (v > target[row + c]) target[row + c] = v;
                }
            }
        }

        /// <summary>
        /// Draws a polyline as a band of the given width (round joins and caps), with a one-cell soft edge.
        /// </summary>
        public static void StrokeLine(byte[] target, GridSpec grid, IReadOnlyList<AlbersPoint> line, double widthMetres)
        {
            if (target.Length != grid.CellCount) throw new ArgumentException("The target doesn't match the grid.");
            double half = widthMetres / 2, reach = half + grid.CellSize;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var a = line[i];
                var b = line[i + 1];
                double minX = Math.Min(a.X, b.X) - reach, maxX = Math.Max(a.X, b.X) + reach;
                double minY = Math.Min(a.Y, b.Y) - reach, maxY = Math.Max(a.Y, b.Y) + reach;
                int c0 = Math.Max(0, (int)Math.Floor((minX - grid.West) / grid.CellSize));
                int c1 = Math.Min(grid.Columns - 1, (int)Math.Floor((maxX - grid.West) / grid.CellSize));
                int r0 = Math.Max(0, (int)Math.Floor((grid.North - maxY) / grid.CellSize));
                int r1 = Math.Min(grid.Rows - 1, (int)Math.Floor((grid.North - minY) / grid.CellSize));
                double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                for (int r = r0; r <= r1; r++)
                {
                    double py = grid.North - (r + 0.5) * grid.CellSize;
                    long row = (long)r * grid.Columns;
                    for (int c = c0; c <= c1; c++)
                    {
                        double px = grid.West + (c + 0.5) * grid.CellSize;
                        double t = len2 > 0 ? ((px - a.X) * dx + (py - a.Y) * dy) / len2 : 0;
                        t = t < 0 ? 0 : t > 1 ? 1 : t;
                        double ex = px - (a.X + t * dx), ey = py - (a.Y + t * dy);
                        double d = Math.Sqrt(ex * ex + ey * ey);
                        byte v = ToByte((half - d) / grid.CellSize + 0.5);
                        if (v > target[row + c]) target[row + c] = v;
                    }
                }
            }
        }

        static byte ToByte(double coverage)
        {
            if (coverage <= 0) return 0;
            if (coverage >= 1) return 255;
            return (byte)Math.Round(coverage * 255, MidpointRounding.AwayFromZero);
        }
    }
}
