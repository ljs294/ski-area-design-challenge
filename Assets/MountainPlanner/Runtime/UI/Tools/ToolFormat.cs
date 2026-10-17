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
        public static string Feet(int feet) => Imperial ? $"{feet} ft" : $"{Math.Round(feet * UnitFormat.MetresPerFoot):0} m";
    }
}
