using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>
    /// A pending edit's changed contours (CF-08): the contour lines of the pending heights, only over the 1 m cells the
    /// edit changes, for the preview to draw in yellow. The pending edit is read only here (SH-02); the ground itself
    /// changes at Break ground (owner ruling P3-05 TE3). Marching squares on the edit lattice; same edits, same lines.
    /// </summary>
    public static class PendingContours
    {
        /// <summary>
        /// Segments as (x0, y0, x1, y1, elevation) quintuples in package metres, appended to <paramref name="into"/>
        /// (cleared first). <paramref name="tiles"/> are the edit tiles the pending edit wrote.
        /// </summary>
        public static void Build(EditedHeights current, EditedHeights pending, IReadOnlyList<EditTileKey> tiles, double interval, List<double> into)
        {
            if (current == null || pending == null || tiles == null || into == null) throw new ArgumentNullException(nameof(current));
            if (!(interval > 0)) throw new ArgumentOutOfRangeException(nameof(interval));
            into.Clear();
            const int n = EditTileKey.Side + 1;
            var h = new double[n * n];
            var changed = new bool[n * n];
            var lattice = pending.Lattice;
            foreach (var key in tiles)
            {
                bool any = false;
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        int c = key.FirstColumn + i, r = key.FirstRow + j;
                        bool differs = pending.Edits.DeltaMmAt(c, r) != current.Edits.DeltaMmAt(c, r);
                        changed[j * n + i] = differs;
                        any |= differs;
                        h[j * n + i] = differs || i == 0 || j == 0 || i == n - 1 || j == n - 1 ? pending.AtPoint(c, r) : double.NaN;
                    }
                if (!any) continue;
                for (int j = 0; j < n - 1; j++)
                    for (int i = 0; i < n - 1; i++)
                    {
                        int k = j * n + i;
                        if (!(changed[k] || changed[k + 1] || changed[k + n] || changed[k + n + 1])) continue;
                        int c = key.FirstColumn + i, r = key.FirstRow + j;
                        double a = H(h, k, pending, c, r), b = H(h, k + 1, pending, c + 1, r);
                        double d = H(h, k + n, pending, c, r + 1), e = H(h, k + n + 1, pending, c + 1, r + 1);
                        Cell(a, b, d, e, lattice.X(c), lattice.Y(r), interval, into);
                    }
            }
        }

        static double H(double[] h, int k, EditedHeights pending, int c, int r)
        {
            if (double.IsNaN(h[k])) h[k] = pending.AtPoint(c, r);
            return h[k];
        }

        /// <summary>One cell's lines: corners a (north-west), b (north-east), d (south-west), e (south-east).</summary>
        static void Cell(double a, double b, double d, double e, double x, double y, double interval, List<double> into)
        {
            double lo = Math.Min(Math.Min(a, b), Math.Min(d, e)), hi = Math.Max(Math.Max(a, b), Math.Max(d, e));
            Span<double> px = stackalloc double[4];
            Span<double> py = stackalloc double[4];
            for (double level = Math.Ceiling(lo / interval) * interval; level < hi; level += interval)
            {
                // Crossings on the four edges, in order north, east, south, west.
                int count = 0;
                Cross(a, b, level, x, y, x + 1, y, px, py, ref count);
                Cross(b, e, level, x + 1, y, x + 1, y - 1, px, py, ref count);
                Cross(e, d, level, x + 1, y - 1, x, y - 1, px, py, ref count);
                Cross(d, a, level, x, y - 1, x, y, px, py, ref count);
                for (int s = 0; s + 1 < count; s += 2)
                {
                    into.Add(px[s]); into.Add(py[s]); into.Add(px[s + 1]); into.Add(py[s + 1]); into.Add(level);
                }
            }
        }

        static void Cross(double h0, double h1, double level, double x0, double y0, double x1, double y1, Span<double> px, Span<double> py, ref int count)
        {
            if ((h0 < level) == (h1 < level)) return;
            double t = (level - h0) / (h1 - h0);
            px[count] = x0 + (x1 - x0) * t;
            py[count] = y0 + (y1 - y0) * t;
            count++;
        }
    }
}
