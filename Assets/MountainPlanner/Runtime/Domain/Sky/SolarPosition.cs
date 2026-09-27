using System;

namespace MountainPlanner.Domain.Sky
{
    /// <summary>
    /// Where the sun is, from the NOAA Solar Calculator algorithm (after Meeus, "Astronomical
    /// Algorithms"). Accurate to about 0.01° for 1800–2100, well inside the 0.1° the lighting needs
    /// (0.7 task 11). Deterministic and engine-free: it takes the time as plain numbers, never a clock.
    /// </summary>
    public readonly struct SolarPosition
    {
        /// <summary>Degrees above the horizon, corrected for atmospheric refraction (what the eye sees).</summary>
        public readonly double Elevation;

        /// <summary>Degrees above the horizon, geometric (no refraction).</summary>
        public readonly double GeometricElevation;

        /// <summary>Degrees clockwise from true north.</summary>
        public readonly double Azimuth;

        SolarPosition(double elevation, double geometricElevation, double azimuth)
        {
            Elevation = elevation;
            GeometricElevation = geometricElevation;
            Azimuth = azimuth;
        }

        public bool IsAboveHorizon => Elevation > 0;

        /// <summary>
        /// The sun at a place and a UTC time. <paramref name="dayOfYear"/> is 1-based;
        /// <paramref name="secondOfDayUtc"/> is 0–86399. Longitude is negative west.
        /// </summary>
        public static SolarPosition At(double latitude, double longitude, int year, int dayOfYear, double secondOfDayUtc)
        {
            if (!(latitude >= -90 && latitude <= 90)) throw new ArgumentOutOfRangeException(nameof(latitude));
            if (!(longitude >= -180 && longitude <= 180)) throw new ArgumentOutOfRangeException(nameof(longitude));
            if (dayOfYear < 1 || dayOfYear > DaysInYear(year)) throw new ArgumentOutOfRangeException(nameof(dayOfYear));
            if (!(secondOfDayUtc >= 0 && secondOfDayUtc < 86400)) throw new ArgumentOutOfRangeException(nameof(secondOfDayUtc));

            double jd = JulianDayOfJanuaryFirst(year) + (dayOfYear - 1) + secondOfDayUtc / 86400.0;
            double t = (jd - 2451545.0) / 36525.0;

            double l0 = Mod360(280.46646 + t * (36000.76983 + t * 0.0003032));
            double m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
            double e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);
            double mr = Rad(m);
            double centre = Math.Sin(mr) * (1.914602 - t * (0.004817 + 0.000014 * t))
                          + Math.Sin(2 * mr) * (0.019993 - 0.000101 * t)
                          + Math.Sin(3 * mr) * 0.000289;
            double omega = 125.04 - 1934.136 * t;
            double apparentLongitude = l0 + centre - 0.00569 - 0.00478 * Math.Sin(Rad(omega));
            double meanObliquity = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
            double obliquity = meanObliquity + 0.00256 * Math.Cos(Rad(omega));

            double declination = Math.Asin(Math.Sin(Rad(obliquity)) * Math.Sin(Rad(apparentLongitude)));
            double y = Math.Pow(Math.Tan(Rad(obliquity) / 2), 2);
            double l0r = Rad(l0);
            double equationOfTimeMinutes = 4 * Deg(
                y * Math.Sin(2 * l0r) - 2 * e * Math.Sin(mr) + 4 * e * y * Math.Sin(mr) * Math.Cos(2 * l0r)
                - 0.5 * y * y * Math.Sin(4 * l0r) - 1.25 * e * e * Math.Sin(2 * mr));

            double trueSolarMinutes = Mod(secondOfDayUtc / 60.0 + equationOfTimeMinutes + 4 * longitude, 1440);
            double hourAngle = trueSolarMinutes / 4 < 0 ? trueSolarMinutes / 4 + 180 : trueSolarMinutes / 4 - 180;

            double lat = Rad(latitude);
            double cosZenith = Math.Sin(lat) * Math.Sin(declination) + Math.Cos(lat) * Math.Cos(declination) * Math.Cos(Rad(hourAngle));
            double zenith = Math.Acos(Math.Max(-1, Math.Min(1, cosZenith)));
            double geometric = 90 - Deg(zenith);

            double azimuth;
            double denominator = Math.Cos(lat) * Math.Sin(zenith);
            if (Math.Abs(denominator) > 1e-9)
            {
                double cosAz = (Math.Sin(lat) * Math.Cos(zenith) - Math.Sin(declination)) / denominator;
                double a = Deg(Math.Acos(Math.Max(-1, Math.Min(1, cosAz))));
                azimuth = hourAngle > 0 ? Mod360(a + 180) : Mod360(540 - a);
            }
            else
            {
                azimuth = latitude > 0 ? 180 : 0;
            }

            return new SolarPosition(geometric + Refraction(geometric), geometric, azimuth);
        }

        /// <summary>
        /// The sun's azimuth on the resort's Albers grid. Grid north differs from true north by the
        /// meridian convergence (about 9° at Jackson Hole, 15° at Crystal Mountain), so shadows would
        /// point the wrong way without this. <paramref name="gridConvergence"/> is the angle, in
        /// degrees, from grid north clockwise to true north (0.3 §4.1; Albers6350 supplies it).
        /// </summary>
        public double GridAzimuth(double gridConvergence) => Mod360(Azimuth + gridConvergence);

        /// <summary>
        /// A unit vector toward the sun in the resort's local frame: x east, y up, z north (grid axes).
        /// </summary>
        public (double x, double y, double z) DirectionInFrame(double gridConvergence)
        {
            double az = Rad(GridAzimuth(gridConvergence)), el = Rad(Elevation);
            return (Math.Sin(az) * Math.Cos(el), Math.Sin(el), Math.Cos(az) * Math.Cos(el));
        }

        // NOAA's refraction approximation, in degrees, for a geometric elevation in degrees.
        static double Refraction(double elevation)
        {
            if (elevation > 85) return 0;
            double te = Math.Tan(Rad(elevation));
            double arcSeconds;
            if (elevation > 5) arcSeconds = 58.1 / te - 0.07 / (te * te * te) + 0.000086 / Math.Pow(te, 5);
            else if (elevation > -0.575) arcSeconds = 1735 + elevation * (-518.2 + elevation * (103.4 + elevation * (-12.79 + elevation * 0.711)));
            else arcSeconds = -20.772 / te;
            return arcSeconds / 3600;
        }

        // Meeus ch. 7, for 1 January 0h UTC (Gregorian calendar).
        static double JulianDayOfJanuaryFirst(int year)
        {
            int y = year - 1;
            int a = y / 100;
            int b = 2 - a + a / 4;
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * 14) + 1 + b - 1524.5;
        }

        static int DaysInYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 366 : 365;
        static double Rad(double degrees) => degrees * Math.PI / 180;
        static double Deg(double radians) => radians * 180 / Math.PI;
        static double Mod(double v, double m) => v - m * Math.Floor(v / m);
        static double Mod360(double v) => Mod(v, 360);
    }
}
