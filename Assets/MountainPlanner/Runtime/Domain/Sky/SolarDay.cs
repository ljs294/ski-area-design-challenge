using System;

namespace MountainPlanner.Domain.Sky
{
    /// <summary>
    /// A site's day as the game shows it (0.3 §4.7): clock times are local standard time, one hour per 15°
    /// of longitude (no daylight saving; the season is winter), and the sun comes from
    /// <see cref="SolarPosition"/> at the matching UTC moment. Also finds the day's sunrise, solar noon and
    /// sunset by a fixed scan and bisection, so the same inputs always give the same answer.
    /// </summary>
    public readonly struct SolarDay
    {
        public const int SecondsPerDay = 86400;
        /// <summary>The sun's centre this far below the horizon (geometric) is sunrise or sunset: refraction plus the half disc.</summary>
        public const double HorizonDegrees = -0.833;

        public readonly double Latitude, Longitude;
        public readonly int Year, DayOfYear;

        public SolarDay(double latitude, double longitude, int year, int dayOfYear)
        {
            if (!(latitude >= -90 && latitude <= 90)) throw new ArgumentOutOfRangeException(nameof(latitude));
            if (!(longitude >= -180 && longitude <= 180)) throw new ArgumentOutOfRangeException(nameof(longitude));
            if (year < 2 || year > 9998) throw new ArgumentOutOfRangeException(nameof(year));
            if (dayOfYear < 1 || dayOfYear > DaysInYear(year)) throw new ArgumentOutOfRangeException(nameof(dayOfYear));
            Latitude = latitude;
            Longitude = longitude;
            Year = year;
            DayOfYear = dayOfYear;
        }

        /// <summary>Hours east of UTC for the site's standard time zone: longitude / 15, rounded.</summary>
        public static int StandardOffsetHours(double longitude) => (int)Math.Round(longitude / 15.0, MidpointRounding.AwayFromZero);

        /// <summary>The UTC year, day of year and second of day for a local clock time on this day (it may fall on a neighbouring day).</summary>
        public (int Year, int DayOfYear, double SecondOfDay) ToUtc(double localSecond)
        {
            double s = localSecond - StandardOffsetHours(Longitude) * 3600.0;
            int y = Year, d = DayOfYear;
            while (s < 0)
            {
                s += SecondsPerDay;
                if (--d < 1) d = DaysInYear(--y);
            }
            while (s >= SecondsPerDay)
            {
                s -= SecondsPerDay;
                if (++d > DaysInYear(y)) { y++; d = 1; }
            }
            return (y, d, s);
        }

        /// <summary>The sun at a local clock time (seconds after local midnight, 0–86399).</summary>
        public SolarPosition SunAt(double localSecond)
        {
            var (y, d, s) = ToUtc(localSecond);
            return SolarPosition.At(Latitude, Longitude, y, d, s);
        }

        /// <summary>Local seconds of the sun's highest point (to within a second).</summary>
        public double SolarNoon()
        {
            // Scan every 10 minutes for the highest sample, then narrow by golden-section search.
            double best = 0, bestElevation = double.MinValue;
            for (int s = 0; s < SecondsPerDay; s += 600)
            {
                double e = SunAt(s).GeometricElevation;
                if (e > bestElevation) { bestElevation = e; best = s; }
            }
            double lo = Math.Max(0, best - 600), hi = Math.Min(SecondsPerDay - 1, best + 600);
            const double g = 0.6180339887498949;
            for (int i = 0; i < 40 && hi - lo > 0.5; i++)
            {
                double a = hi - g * (hi - lo), b = lo + g * (hi - lo);
                if (SunAt(a).GeometricElevation < SunAt(b).GeometricElevation) lo = a; else hi = b;
            }
            return (lo + hi) / 2;
        }

        /// <summary>Local seconds of sunrise, or NaN when the sun neither rises nor sets that day (polar day or night).</summary>
        public double Sunrise() => Crossing(rising: true);

        /// <summary>Local seconds of sunset, or NaN when the sun neither rises nor sets that day.</summary>
        public double Sunset() => Crossing(rising: false);

        double Crossing(bool rising)
        {
            double noon = SolarNoon();
            // Search the half-day on the right side of noon, every 10 minutes, then bisect the crossing.
            double from = rising ? Math.Max(0, noon - 43200) : noon, to = rising ? noon : Math.Min(SecondsPerDay - 1, noon + 43200);
            double previous = from;
            bool previousUp = Above(previous);
            for (double s = from + 600; ; s += 600)
            {
                if (s > to) s = to;
                bool up = Above(s);
                if (up != previousUp && up == rising)
                {
                    double lo = previous, hi = s;
                    for (int i = 0; i < 40 && hi - lo > 0.5; i++)
                    {
                        double mid = (lo + hi) / 2;
                        if (Above(mid) == rising) hi = mid; else lo = mid;
                    }
                    return (lo + hi) / 2;
                }
                if (s >= to) return double.NaN;
                previous = s;
                previousUp = up;
            }
        }

        bool Above(double localSecond) => SunAt(localSecond).GeometricElevation > HorizonDegrees;

        static int DaysInYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 366 : 365;
    }
}
