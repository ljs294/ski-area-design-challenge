using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.14's ring geometry (PD-01, PD-02): a closed ring of points in metres, the signed distance to it (inside
    /// negative), whether a point is inside (even-odd), its area and whether it is simple. Plain doubles in a fixed order,
    /// so the same ring and point always give the same answer.
    /// </summary>
    public sealed class RingDistance
    {
        readonly double[] _x, _y;
        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public int Count => _x.Length;

        public RingDistance(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            int n = xs.Count;
            _x = new double[n];
            _y = new double[n];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                _x[i] = xs[i];
                _y[i] = ys[i];
                minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]);
                minY = Math.Min(minY, ys[i]); maxY = Math.Max(maxY, ys[i]);
            }
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        /// <summary>A ring of design points (TE5's local frame, metres).</summary>
        public static RingDistance Of(IReadOnlyList<PointMm> ring)
        {
            var xs = new double[ring.Count];
            var ys = new double[ring.Count];
            for (int i = 0; i < ring.Count; i++) { xs[i] = ring[i].XMetres; ys[i] = ring[i].YMetres; }
            return new RingDistance(xs, ys);
        }

        /// <summary>A ring of design points placed on the earthwork lattice's frame (<see cref="EarthworkSite.PointOf"/>).</summary>
        public static RingDistance Of(IReadOnlyList<PointMm> ring, EarthworkSite site)
        {
            var xs = new double[ring.Count];
            var ys = new double[ring.Count];
            for (int i = 0; i < ring.Count; i++) site.PointOf(ring[i], out xs[i], out ys[i]);
            return new RingDistance(xs, ys);
        }

        /// <summary>Even-odd: whether (x, y) is inside the ring.</summary>
        public bool Inside(double x, double y)
        {
            bool inside = false;
            int n = _x.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((_y[i] > y) != (_y[j] > y) && x < (_x[j] - _x[i]) * (y - _y[i]) / (_y[j] - _y[i]) + _x[i]) inside = !inside;
            }
            return inside;
        }

        /// <summary>The distance from (x, y) to the nearest edge, metres.</summary>
        public double Unsigned(double x, double y)
        {
            double best = double.MaxValue;
            int n = _x.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double d = SegmentDistanceSquared(x, y, _x[j], _y[j], _x[i], _y[i]);
                if (d < best) best = d;
            }
            return Math.Sqrt(best);
        }

        /// <summary>PD-01's signed distance: negative inside the ring, positive outside, metres.</summary>
        public double Signed(double x, double y)
        {
            double d = Unsigned(x, y);
            return Inside(x, y) ? -d : d;
        }

        /// <summary>The ring's area, square metres (shoelace, always positive).</summary>
        public double Area()
        {
            double a = 0;
            int n = _x.Length;
            for (int i = 0, j = n - 1; i < n; j = i++) a += (_x[j] - _x[i]) * (_y[j] + _y[i]);
            return Math.Abs(a) / 2;
        }

        /// <summary>Whether no two edges cross or touch, other than neighbours at their shared point.</summary>
        public bool Simple()
        {
            int n = _x.Length;
            if (n < 3) return false;
            for (int i = 0; i < n; i++)
            {
                int i2 = (i + 1) % n;
                if (_x[i] == _x[i2] && _y[i] == _y[i2]) return false;
                for (int k = i + 1; k < n; k++)
                {
                    int k2 = (k + 1) % n;
                    if (k == i2 || k2 == i) continue;   // neighbours share a point
                    if (SegmentsMeet(_x[i], _y[i], _x[i2], _y[i2], _x[k], _y[k], _x[k2], _y[k2])) return false;
                }
            }
            return true;
        }

        /// <summary>Whether a segment crosses or touches any edge of the ring.</summary>
        public bool Crosses(double ax, double ay, double bx, double by)
        {
            int n = _x.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
                if (SegmentsMeet(ax, ay, bx, by, _x[j], _y[j], _x[i], _y[i])) return true;
            return false;
        }

        /// <summary>Whether this ring and another overlap: an edge crosses, or one holds a point of the other.</summary>
        public bool Overlaps(RingDistance o)
        {
            int n = _x.Length;
            for (int i = 0, j = n - 1; i < n; j = i++) if (o.Crosses(_x[j], _y[j], _x[i], _y[i])) return true;
            return o.Inside(_x[0], _y[0]) || Inside(o._x[0], o._y[0]);
        }

        /// <summary>The squared distance from (px, py) to the segment (ax, ay)–(bx, by).</summary>
        public static double SegmentDistanceSquared(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double ex = ax + t * dx - px, ey = ay + t * dy - py;
            return ex * ex + ey * ey;
        }

        static double Cross(double ax, double ay, double bx, double by, double cx, double cy) => (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

        /// <summary>Whether segments AB and CD cross or touch.</summary>
        public static bool SegmentsMeet(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double d1 = Cross(cx, cy, dx, dy, ax, ay), d2 = Cross(cx, cy, dx, dy, bx, by);
            double d3 = Cross(ax, ay, bx, by, cx, cy), d4 = Cross(ax, ay, bx, by, dx, dy);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            return d1 == 0 && OnSegment(cx, cy, dx, dy, ax, ay) || d2 == 0 && OnSegment(cx, cy, dx, dy, bx, by)
                || d3 == 0 && OnSegment(ax, ay, bx, by, cx, cy) || d4 == 0 && OnSegment(ax, ay, bx, by, dx, dy);
        }

        static bool OnSegment(double ax, double ay, double bx, double by, double px, double py) =>
            Math.Min(ax, bx) <= px && px <= Math.Max(ax, bx) && Math.Min(ay, by) <= py && py <= Math.Max(ay, by);
    }
}
