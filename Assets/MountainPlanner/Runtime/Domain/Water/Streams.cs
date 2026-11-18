using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Domain.Water
{
    /// <summary>
    /// One OpenStreetMap waterway line (task 4.14, WS-08): what a dam crosses and what feeds it. The package keeps them
    /// (water.json) beside the rasterised water. Data © OpenStreetMap contributors, ODbL.
    /// </summary>
    public sealed class StreamLine
    {
        /// <summary>The OSM way's ID (a dam records it, so its card can name the stream).</summary>
        public ulong Way;
        /// <summary>The OSM waterway class: "stream", "river" or "canal".</summary>
        public string Class = "";
        public string Name = "";
        /// <summary>The width, metres: OSM's `width` when it gave one, else <see cref="StreamFlow.DefaultWidthMetres"/>.</summary>
        public double WidthMetres;
        /// <summary>Whether <see cref="WidthMetres"/> came from OSM's `width` tag.</summary>
        public bool WidthFromOsm;
        /// <summary>The line in Albers metres (EPSG:6350), in OSM's order (downstream, by OSM's convention).</summary>
        public List<AlbersPoint> Points = new List<AlbersPoint>();
    }

    /// <summary>
    /// A named OpenStreetMap water area (task 4.14, LK-10): its name and a point inside it, which binds the name to the lake
    /// body under it when the area opens. Kept in water.json beside the stream lines. Data © OpenStreetMap contributors, ODbL.
    /// </summary>
    public sealed class WaterName
    {
        public string Name = "";
        /// <summary>A point inside the water, Albers metres (the middle of the widest span on a scan line).</summary>
        public AlbersPoint Inside;

        /// <summary>
        /// A point inside a polygon given as rings (even-odd, as the raster fills it): the middle of the widest inside span on
        /// the scan line through the middle of the box, else at a quarter or three quarters. Null for a degenerate shape.
        /// </summary>
        public static AlbersPoint? InteriorPoint(IReadOnlyList<List<AlbersPoint>> rings)
        {
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var r in rings) foreach (var p in r) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            if (!(maxY > minY)) return null;
            var xs = new List<double>();
            foreach (double f in new[] { 0.5, 0.25, 0.75, 0.375, 0.625 })
            {
                double y = minY + f * (maxY - minY);
                xs.Clear();
                foreach (var r in rings)
                    for (int i = 0; i < r.Count; i++)
                    {
                        var a = r[i];
                        var b = r[(i + 1) % r.Count];
                        if ((a.Y <= y) == (b.Y <= y)) continue;   // half-open: a vertex on the line counts once
                        xs.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }
                xs.Sort();
                double best = 0, mid = 0;
                for (int k = 0; k + 1 < xs.Count; k += 2)
                    if (xs[k + 1] - xs[k] > best) { best = xs[k + 1] - xs[k]; mid = (xs[k] + xs[k + 1]) / 2; }
                if (best > 0) return new AlbersPoint(mid, y);
            }
            return null;
        }
    }

    /// <summary>WS-03 v1: a dammed stream's inflow from its width (MapLibre's table, kept) and the season's factor.</summary>
    public static class StreamFlow
    {
        /// <summary>WS-03's defaults when OSM gives no width: a stream 8 ft, a river 46 ft (a canal as a river).</summary>
        public static double DefaultWidthMetres(string waterwayClass) => waterwayClass == "stream" ? 8 * 0.3048 : 46 * 0.3048;

        // WS-03: width (ft) → cfs, linear between rows; wider than 200 ft: 2,650 cfs.
        static readonly double[] WidthFt = { 3, 6.5, 13, 26, 50, 100, 200 };
        static readonly double[] Cfs = { 1, 3.5, 11, 35, 106, 353, 1060 };
        const double WiderCfs = 2650;

        /// <summary>WS-03: the base inflow, cubic feet a second, for a stream this wide (feet).</summary>
        public static double BaseCfs(double widthFeet)
        {
            if (widthFeet <= WidthFt[0]) return Cfs[0];
            if (widthFeet > WidthFt[WidthFt.Length - 1]) return WiderCfs;
            for (int i = 1; i < WidthFt.Length; i++)
                if (widthFeet <= WidthFt[i])
                    return Cfs[i - 1] + (Cfs[i] - Cfs[i - 1]) * (widthFeet - WidthFt[i - 1]) / (WidthFt[i] - WidthFt[i - 1]);
            return WiderCfs;
        }

        /// <summary>
        /// WS-03's season factor by calendar month (1–12): Dec–Feb 0.6, Mar 1.0, Apr 1.5; Oct–Nov 0.8 (owner guess 5, tuned in
        /// PR B against the record); May–Sep 1.0 (no snowmaking then).
        /// </summary>
        public static double SeasonFactor(int month) => month switch
        {
            12 or 1 or 2 => 0.6,
            3 => 1.0,
            4 => 1.5,
            10 or 11 => 0.8,
            _ => 1.0,
        };
    }
}
