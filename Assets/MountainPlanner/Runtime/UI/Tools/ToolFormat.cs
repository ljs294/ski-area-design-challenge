using System;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;

namespace MountainPlanner.UI.Tools
{
    /// <summary>The tools' figures (SH-01): imperial first, metric on request; steepness always a grade in percent.</summary>
    public static class ToolFormat
    {
        static bool Imperial => DisplayUnits.Current == UnitSystem.Imperial;

        /// <summary>A whole length: "4,995 ft" or "1,523 m".</summary>
        public static string Length(double metres) =>
            Imperial ? $"{Math.Round(metres / UnitFormat.MetresPerFoot):#,0} ft" : $"{Math.Round(metres):#,0} m";

        /// <summary>A length in the display unit, rounded, for change checks (no string made).</summary>
        public static int LengthKey(double metres) => (int)Math.Round(Imperial ? metres / UnitFormat.MetresPerFoot : metres);

        /// <summary>"14%": rise over run.</summary>
        public static string Grade(double rise, double run) => run <= 0 ? "—" : $"{Math.Round(Math.Abs(rise) / run * 100):0}%";

        /// <summary>"2.3 acres" or "0.9 ha".</summary>
        public static string Area(double squareMetres) =>
            Imperial ? $"{squareMetres / 4046.8564224:0.0} acres" : $"{squareMetres / 10000:0.0} ha";

        /// <summary>"11:06" minutes and seconds.</summary>
        public static string Duration(double seconds)
        {
            int s = (int)Math.Round(seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        /// <summary>A brush or tower size in whole feet (or metres): "100 ft".</summary>
        /// <summary>A volume (GR-08): cubic yards, or cubic metres when metric; <paramref name="signed"/> shows + for a surplus.</summary>
        public static string Volume(double cubicMetres, bool signed = false)
        {
            double v = Imperial ? cubicMetres * 1.307950619 : cubicMetres;
            string n = Math.Abs(v).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            string sign = v < -0.5 ? "−" : signed && v > 0.5 ? "+" : "";
            return $"{sign}{n} {(Imperial ? "yd³" : "m³")}";
        }

        public static string Feet(int feet) => Imperial ? $"{feet} ft" : $"{Math.Round(feet * UnitFormat.MetresPerFoot):0} m";

        static string N(double v, string format = "N0") => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Task 4.14: stored water, "40 acre-ft" (a tenth under 10), or "49,300 m³".</summary>
        public static string Water(double cubicMetres)
        {
            if (!Imperial) return N(cubicMetres) + " m³";
            double af = cubicMetres / 1233.48183754752;
            return (af < 10 ? N(af, "N1") : N(af)) + " acre-ft";
        }

        /// <summary>Task 4.14: the same water in gallons, "13M gal" (or "49 ML").</summary>
        public static string Gallons(double cubicMetres)
        {
            if (!Imperial) return N(cubicMetres / 1000, cubicMetres < 10_000 ? "N1" : "N0") + " ML";
            double g = cubicMetres * 264.172052358148;
            return g >= 1e6 ? N(g / 1e6, g < 1e7 ? "N1" : "N0") + "M gal" : N(g) + " gal";
        }

        /// <summary>Task 4.14: a stream's flow, "6.6 cfs" (or "0.19 m³/s").</summary>
        public static string Flow(double cubicFeetPerSecond) =>
            Imperial ? N(cubicFeetPerSecond, cubicFeetPerSecond < 10 ? "N1" : "N0") + " cfs" : N(cubicFeetPerSecond * 0.028316846592, "N2") + " m³/s";

        /// <summary>Task 4.14: a height or depth to the foot, "23 ft" (or "7.0 m").</summary>
        public static string Depth(double metres) => Imperial ? N(metres / UnitFormat.MetresPerFoot) + " ft" : N(metres, "N1") + " m";

        /// <summary>Task 4.14: an elevation, "7,406 ft" (or "2,257.4 m").</summary>
        public static string Elevation(double metres) => Imperial ? N(metres / UnitFormat.MetresPerFoot) + " ft" : N(metres, "N1") + " m";
    }
}
