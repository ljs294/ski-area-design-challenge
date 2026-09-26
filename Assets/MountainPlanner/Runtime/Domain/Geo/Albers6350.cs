using System;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// NAD83(2011) / CONUS Albers Equal Area (EPSG:6350) on the GRS80 ellipsoid: the native grid of
    /// USGS S1M tiles and of every resort package (0.3 §4.1). Also matches ESRI:102039 (BIGMAP).
    /// Formulas: Snyder, "Map Projections: A Working Manual" (USGS PP 1395), §14.
    /// </summary>
    public static class Albers6350
    {
        public const int Epsg = 6350;

        const double A = 6378137.0;
        const double F = 1 / 298.257222101;
        static readonly double E2 = 2 * F - F * F;
        static readonly double E = Math.Sqrt(E2);
        static readonly double Lon0 = ToRadians(-96.0);
        static readonly double N;
        static readonly double C;
        static readonly double Rho0;

        static Albers6350()
        {
            double p1 = ToRadians(29.5), p2 = ToRadians(45.5), p0 = ToRadians(23.0);
            double m1 = M(p1), m2 = M(p2);
            N = (m1 * m1 - m2 * m2) / (Q(p2) - Q(p1));
            C = m1 * m1 + N * Q(p1);
            Rho0 = A * Math.Sqrt(C - N * Q(p0)) / N;
        }

        /// <summary>Latitude and longitude to Albers metres.</summary>
        public static AlbersPoint Forward(GeoPoint p)
        {
            double rho = A * Math.Sqrt(C - N * Q(ToRadians(p.Latitude))) / N;
            double theta = N * (ToRadians(p.Longitude) - Lon0);
            return new AlbersPoint(rho * Math.Sin(theta), Rho0 - rho * Math.Cos(theta));
        }

        /// <summary>Albers metres to latitude and longitude (iterative; converges to below 1e-14 rad).</summary>
        public static GeoPoint Inverse(AlbersPoint p)
        {
            double dy = Rho0 - p.Y;
            double rho = Math.Sqrt(p.X * p.X + dy * dy);
            double theta = Math.Atan2(p.X, dy);
            double q = (C - rho * rho * N * N / (A * A)) / N;
            double lon = ToDegrees(Lon0 + theta / N);

            double phi = Math.Asin(Math.Max(-1, Math.Min(1, q / 2)));
            for (int i = 0; i < 20; i++)
            {
                double s = Math.Sin(phi), c = Math.Cos(phi), es2 = 1 - E2 * s * s;
                double dPhi = es2 * es2 / (2 * c) *
                    (q / (1 - E2) - s / es2 + 1 / (2 * E) * Math.Log((1 - E * s) / (1 + E * s)));
                phi += dPhi;
                if (Math.Abs(dPhi) < 1e-14) break;
            }
            return new GeoPoint(ToDegrees(phi), lon);
        }

        /// <summary>
        /// Scale factors at a latitude: k along the parallel and h along the meridian. The projection
        /// is equal-area, so h = 1 / k. One grid metre east–west is k true metres; north–south, h.
        /// Stored in the manifest for future measuring tools; rendering ignores them (0.3 §4.1).
        /// </summary>
        public static ScaleFactors ScaleAt(double latitude)
        {
            double phi = ToRadians(latitude);
            double rho = A * Math.Sqrt(C - N * Q(phi)) / N;
            double k = rho * N / (A * M(phi));
            return new ScaleFactors(k, 1 / k);
        }

        static double Q(double phi)
        {
            double s = Math.Sin(phi);
            return (1 - E2) * (s / (1 - E2 * s * s) - 1 / (2 * E) * Math.Log((1 - E * s) / (1 + E * s)));
        }

        static double M(double phi)
        {
            double s = Math.Sin(phi);
            return Math.Cos(phi) / Math.Sqrt(1 - E2 * s * s);
        }

        static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
        static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
    }

    /// <summary>Projection scale factors at a point: along the parallel and along the meridian.</summary>
    public readonly struct ScaleFactors
    {
        public readonly double Parallel;
        public readonly double Meridian;

        public ScaleFactors(double parallel, double meridian)
        {
            Parallel = parallel;
            Meridian = meridian;
        }

        /// <summary>The larger distortion of the two, as a fraction (0.01 = 1%).</summary>
        public double MaxDistortion => Math.Max(Math.Abs(Parallel - 1), Math.Abs(Meridian - 1));
    }
}
