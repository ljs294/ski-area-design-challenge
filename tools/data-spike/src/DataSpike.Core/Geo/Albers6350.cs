using System;

namespace MountainPlanner.DataSpike.Geo
{
    /// <summary>
    /// NAD83(2011) / CONUS Albers Equal Area (EPSG:6350) on the GRS80 ellipsoid: the native grid of
    /// USGS S1M tiles (0.3 §4.1). Also matches ESRI:102039, which the BIGMAP service uses.
    /// Formulas from Snyder, "Map Projections: A Working Manual" (USGS PP 1395), §14.
    /// </summary>
    public static class Albers6350
    {
        const double A = 6378137.0;
        const double F = 1 / 298.257222101;
        static readonly double E2 = 2 * F - F * F;
        static readonly double E = Math.Sqrt(E2);
        static readonly double Lon0 = ToRad(-96.0);
        static readonly double N;
        static readonly double C;
        static readonly double Rho0;

        static Albers6350()
        {
            double p1 = ToRad(29.5), p2 = ToRad(45.5), p0 = ToRad(23.0);
            double m1 = M(p1), m2 = M(p2);
            N = (m1 * m1 - m2 * m2) / (Q(p2) - Q(p1));
            C = m1 * m1 + N * Q(p1);
            Rho0 = A * Math.Sqrt(C - N * Q(p0)) / N;
        }

        /// <summary>Latitude/longitude (degrees) to Albers metres.</summary>
        public static void Forward(double latDeg, double lonDeg, out double x, out double y)
        {
            double rho = A * Math.Sqrt(C - N * Q(ToRad(latDeg))) / N;
            double theta = N * (ToRad(lonDeg) - Lon0);
            x = rho * Math.Sin(theta);
            y = Rho0 - rho * Math.Cos(theta);
        }

        /// <summary>Albers metres to latitude/longitude (degrees).</summary>
        public static void Inverse(double x, double y, out double latDeg, out double lonDeg)
        {
            double dy = Rho0 - y;
            double rho = Math.Sqrt(x * x + dy * dy);
            double theta = Math.Atan2(x, dy);
            double q = (C - rho * rho * N * N / (A * A)) / N;
            lonDeg = ToDeg(Lon0 + theta / N);

            double phi = Math.Asin(q / 2);
            for (int i = 0; i < 15; i++)
            {
                double s = Math.Sin(phi), c = Math.Cos(phi), es2 = 1 - E2 * s * s;
                double dPhi = es2 * es2 / (2 * c) *
                    (q / (1 - E2) - s / es2 + 1 / (2 * E) * Math.Log((1 - E * s) / (1 + E * s)));
                phi += dPhi;
                if (Math.Abs(dPhi) < 1e-14) break;
            }
            latDeg = ToDeg(phi);
        }

        /// <summary>
        /// Scale factor along the parallel (k) at a latitude; along the meridian it is 1/k.
        /// A 1 m grid step is k true metres east–west and 1/k true metres north–south.
        /// </summary>
        public static double ParallelScale(double latDeg)
        {
            double phi = ToRad(latDeg);
            double rho = A * Math.Sqrt(C - N * Q(phi)) / N;
            return rho * N / (A * M(phi));
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

        static double ToRad(double d) => d * Math.PI / 180.0;
        static double ToDeg(double r) => r * 180.0 / Math.PI;
    }
}
