using System;

namespace MountainPlanner.Domain.Sky
{
    /// <summary>
    /// WX-60's sun for the snow (task 4.07): the unit vector toward the sun in integers only, so the snow's melt, which is
    /// hashed, is the same on every runtime (SI-08). <see cref="SolarPosition"/> is the float original the lighting reads;
    /// this one reads literal tables (<see cref="SunTables"/>: sines every 0.1°, declination and the equation of time at
    /// noon UTC each day of a reference year), and a test holds it within 0.2° of the float sun. Components are in
    /// millionths, x east, y north, z up, true axes; the caller turns them by the grid's convergence.
    /// </summary>
    public readonly struct SunVector
    {
        public const int One = 1_000_000;
        public readonly int East, North, Up;

        public SunVector(int east, int north, int up) { East = east; North = north; Up = up; }

        public bool AboveHorizon => Up > 0;

        /// <summary>
        /// The sun at a place (millionths of a degree, longitude negative west), a day of the year (1–366) and a local
        /// standard-time second of the day (the site's zone is longitude ÷ 15 rounded, as <see cref="SolarDay"/>'s).
        /// </summary>
        public static SunVector At(int latMicro, int lonMicro, int dayOfYear, int localSecond)
        {
            int d = dayOfYear < 1 ? 1 : dayOfYear > 366 ? 366 : dayOfYear;
            long zone = lonMicro >= 0 ? (lonMicro + 7_500_000L) / 15_000_000L : -((-lonMicro + 7_500_000L) / 15_000_000L);
            // The tables are at noon UTC: interpolate to the hour's UTC time (the declination moves 0.4° a day at the equinoxes).
            long utc = localSecond - zone * 3600 - 43_200;               // seconds from this day's noon UTC
            int i0 = d - 1 + (int)(utc >= 0 ? utc / 86_400 : (utc - 86_399) / 86_400);
            long frac = utc - (long)(i0 - (d - 1)) * 86_400;             // 0 … 86,399
            int a0 = Math.Max(0, Math.Min(365, i0)), a1 = Math.Max(0, Math.Min(365, i0 + 1));
            int decl = SunTables.DeclinationMilli[a0] + (int)((SunTables.DeclinationMilli[a1] - SunTables.DeclinationMilli[a0]) * frac / 86_400);
            int eot = SunTables.EquationOfTimeSeconds[a0] + (int)((SunTables.EquationOfTimeSeconds[a1] - SunTables.EquationOfTimeSeconds[a0]) * frac / 86_400);
            // True solar seconds: local standard time, to UTC, plus the equation of time and 4 min per degree of longitude.
            long solar = localSecond - zone * 3600 + eot + (lonMicro * 240L + (lonMicro >= 0 ? 500_000 : -500_000)) / 1_000_000;
            solar %= 86_400; if (solar < 0) solar += 86_400;
            int hourMilli = (int)(solar * 1000 / 240) - 180_000;        // hour angle, 0.001°, 0 at solar noon
            int lat = (int)(latMicro / 1000);                           // 0.001°

            long sLat = SinMilli(lat), cLat = CosMilli(lat), sDec = SinMilli(decl), cDec = CosMilli(decl);
            long sH = SinMilli(hourMilli), cH = CosMilli(hourMilli);
            long up = (sLat * sDec + cLat * cDec / One * cH) / One;
            long east = -(cDec * sH) / One;
            long north = (cLat * sDec - sLat * cDec / One * cH) / One;
            return new SunVector((int)east, (int)north, (int)up);
        }

        /// <summary>sin of an angle in 0.001°, in millionths: the 0.1° table, linear between (error under 4·10⁻⁷).</summary>
        public static int SinMilli(int milliDeg)
        {
            int a = milliDeg % 360_000; if (a < 0) a += 360_000;
            int sign = 1;
            if (a >= 180_000) { a -= 180_000; sign = -1; }
            if (a > 90_000) a = 180_000 - a;
            int i = a / 100, f = a % 100;
            int[] t = SunTables.SinMicro;
            int v = i >= t.Length - 1 ? t[t.Length - 1] : t[i] + (int)((long)(t[i + 1] - t[i]) * f / 100);
            return sign * v;
        }

        public static int CosMilli(int milliDeg) => SinMilli(milliDeg + 90_000);

        /// <summary>The sun turned from true axes to the grid's (convergence: degrees from grid north clockwise to true north, 0.001°).</summary>
        public SunVector ToGrid(int convergenceMilli)
        {
            long s = SinMilli(convergenceMilli), c = CosMilli(convergenceMilli);
            // A true azimuth a is grid azimuth a + convergence: rotate clockwise by the convergence.
            long east = (East * c + North * s) / One, north = (North * c - East * s) / One;
            return new SunVector((int)east, (int)north, Up);
        }
    }
}
