using System;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// TR-05's "any point on another trail": the nearest point on a trail's derived centreline (TR-03), read from the
    /// earthwork's last build of that trail. A trail the earthwork hasn't built yet has no line to snap to (its head and
    /// tail still snap). Engine-free, deterministic and allocation-free.
    /// </summary>
    public sealed class TrailLineSnap : ICentrelineSnap
    {
        readonly Func<DesignEarthwork> _earthwork;

        /// <param name="earthwork">The design's earthwork, read on each snap (it may start after the tool does).</param>
        public TrailLineSnap(Func<DesignEarthwork> earthwork) => _earthwork = earthwork;

        public bool Nearest(Trail trail, PointMm from, out PointMm on)
        {
            on = from;
            var earthwork = _earthwork?.Invoke();
            if (trail == null || earthwork == null) return false;
            if (!(earthwork.DetailOf(trail.Id.Value) is TrailBuild build) || build.Centreline == null || !build.Centreline.Found) return false;
            var site = earthwork.Site;
            site.PointOf(from, out double px, out double py);
            if (!NearestOnLine(build.Centreline.Points, px, py, out double x, out double y)) return false;
            on = PointMm.FromMetres(x - site.OriginX, y - site.OriginY);
            return true;
        }

        /// <summary>The nearest point to (px, py) on a polyline of x, y pairs; the first segment wins a tie.</summary>
        public static bool NearestOnLine(double[] points, double px, double py, out double x, out double y)
        {
            x = y = 0;
            if (points == null || points.Length < 4) return false;
            double best = double.MaxValue;
            for (int i = 0; i + 3 < points.Length; i += 2)
            {
                double ax = points[i], ay = points[i + 1], bx = points[i + 2], by = points[i + 3];
                double dx = bx - ax, dy = by - ay, len = dx * dx + dy * dy;
                double t = len > 0 ? ((px - ax) * dx + (py - ay) * dy) / len : 0;
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                double qx = ax + t * dx, qy = ay + t * dy;
                double d = (qx - px) * (qx - px) + (qy - py) * (qy - py);
                if (d < best) { best = d; x = qx; y = qy; }
            }
            return true;
        }
    }
}
