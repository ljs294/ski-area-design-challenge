#nullable enable
using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// Daymet V4's 1 km grid (WX-14): Lambert Conformal Conic on WGS84 (standard parallels 25° and 60° N, origin 42.5° N,
    /// central meridian 100° W), checked against the service's own "X &amp; Y on Lambert Conformal Conic" to the centimetre.
    /// Pixel edges, found by probing the service at 2026-11-08: x at 1000k + 250 m, y at 1000k + 500 m, so a pixel's centre
    /// is at x = 1000k + 750, y = 1000k. The lattice asks for pixel centres, so every point is a different pixel.
    /// </summary>
    public static class DaymetGrid
    {
        const double A = 6378137.0, Flattening = 1 / 298.257223563;
        static readonly double E2 = 2 * Flattening - Flattening * Flattening, E = Math.Sqrt(E2);
        static readonly double Lon0 = Rad(-100), N, F, Rho0;

        static DaymetGrid()
        {
            double p1 = Rad(25), p2 = Rad(60), p0 = Rad(42.5);
            N = (Math.Log(M(p1)) - Math.Log(M(p2))) / (Math.Log(T(p1)) - Math.Log(T(p2)));
            F = M(p1) / (N * Math.Pow(T(p1), N));
            Rho0 = A * F * Math.Pow(T(p0), N);
        }

        static double Rad(double d) => d * Math.PI / 180;
        static double Deg(double r) => r * 180 / Math.PI;
        static double M(double p) => Math.Cos(p) / Math.Sqrt(1 - E2 * Math.Sin(p) * Math.Sin(p));
        static double T(double p) => Math.Tan(Math.PI / 4 - p / 2) / Math.Pow((1 - E * Math.Sin(p)) / (1 + E * Math.Sin(p)), E / 2);

        public static (double X, double Y) Forward(GeoPoint g)
        {
            double rho = A * F * Math.Pow(T(Rad(g.Latitude)), N);
            double theta = N * (Rad(g.Longitude) - Lon0);
            return (rho * Math.Sin(theta), Rho0 - rho * Math.Cos(theta));
        }

        public static GeoPoint Inverse(double x, double y)
        {
            double rho = Math.Sign(N) * Math.Sqrt(x * x + (Rho0 - y) * (Rho0 - y));
            double t = Math.Pow(rho / (A * F), 1 / N);
            double theta = Math.Atan2(x, Rho0 - y);
            double phi = Math.PI / 2 - 2 * Math.Atan(t);
            for (int i = 0; i < 12; i++)
                phi = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - E * Math.Sin(phi)) / (1 + E * Math.Sin(phi)), E / 2));
            return new GeoPoint(Deg(phi), Deg(theta / N + Lon0));
        }

        /// <summary>The centre of the pixel holding (x, y), in grid metres.</summary>
        public static (double X, double Y) PixelCentre(double x, double y) =>
            (Math.Floor((x - 250) / 1000) * 1000 + 750, Math.Floor((y + 500) / 1000) * 1000);

        public static (double X, double Y) PixelCentre(GeoPoint g)
        {
            var (x, y) = Forward(g);
            return PixelCentre(x, y);
        }

        /// <summary>
        /// WX-14: the pixels whose centres lie in the Albers square of half-size <paramref name="halfMetres"/> around
        /// <paramref name="centre"/> (the box plus its surround), nearest the reference pixel first, the reference pixel itself
        /// first of all. Past <paramref name="cap"/> pixels every second row and column is kept (the reference pixel's parity).
        /// </summary>
        public static List<(double X, double Y)> Lattice(AlbersPoint centre, double halfMetres, int cap = 81)
        {
            var refCentre = PixelCentre(Albers6350.Inverse(centre));
            double reach = halfMetres * 1.5 + 1000;   // grid convergence: the Albers square is turned in the LCC frame
            var all = new List<(double X, double Y, int I, int J)>();
            int span = (int)Math.Ceiling(reach / 1000);
            for (int j = -span; j <= span; j++)
                for (int i = -span; i <= span; i++)
                {
                    double x = refCentre.X + i * 1000, y = refCentre.Y + j * 1000;
                    var a = Albers6350.Forward(Inverse(x, y));
                    if (Math.Abs(a.X - centre.X) > halfMetres || Math.Abs(a.Y - centre.Y) > halfMetres) continue;
                    all.Add((x, y, i, j));
                }
            if (all.Count > cap) all = all.FindAll(p => ((p.I & 1) | (p.J & 1)) == 0);
            all.Sort((p, q) =>
            {
                long dp = (long)p.I * p.I + (long)p.J * p.J, dq = (long)q.I * q.I + (long)q.J * q.J;
                if (dp != dq) return dp.CompareTo(dq);
                return p.J != q.J ? p.J.CompareTo(q.J) : p.I.CompareTo(q.I);
            });
            var result = new List<(double X, double Y)>(all.Count);
            foreach (var p in all) result.Add((p.X, p.Y));
            if (result.Count == 0 || result[0] != (refCentre.X, refCentre.Y)) result.Insert(0, refCentre);
            return result;
        }

        /// <summary>Albers points spread over a pixel's footprint (<paramref name="n"/> × <paramref name="n"/>), for the DEM check.</summary>
        public static IEnumerable<AlbersPoint> Footprint(double cx, double cy, int n = 20)
        {
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    yield return Albers6350.Forward(Inverse(cx - 500 + (i + 0.5) * 1000.0 / n, cy - 500 + (j + 0.5) * 1000.0 / n));
        }
    }
}
