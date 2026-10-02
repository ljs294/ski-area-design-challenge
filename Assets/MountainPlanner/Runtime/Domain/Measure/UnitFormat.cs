using System;
using System.Globalization;

namespace MountainPlanner.Domain.Measure
{
    /// <summary>How figures are shown: imperial by default (owner, 2026-10-02), metric on request.</summary>
    public enum UnitSystem { Imperial = 0, Metric = 1 }

    /// <summary>
    /// Every distance, elevation and depth the player reads, in either unit system (task 12b.2). The game works in
    /// metres; only what's shown changes. Each formatter has an integer key so a readout rebuilds its text only
    /// when the shown value changes. Slope is a grade in percent in both systems (<see cref="SlopeBands"/>).
    /// </summary>
    public static class UnitFormat
    {
        public const double MetresPerFoot = 0.3048, MetresPerInch = 0.0254, FeetPerMile = 5280;
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>Elevation to the nearest foot or metre, e.g. "8,640 ft" or "2,634 m".</summary>
        public static string Elevation(double metres, UnitSystem units) =>
            ElevationKey(metres, units).ToString("N0", Invariant) + (units == UnitSystem.Imperial ? " ft" : " m");

        public static int ElevationKey(double metres, UnitSystem units) =>
            (int)Math.Round(units == UnitSystem.Imperial ? metres / MetresPerFoot : metres);

        /// <summary>Snow depth: "8 in", "2 ft 4 in", "3 ft"; or "71 cm", "1.4 m".</summary>
        public static string SnowDepth(double metres, UnitSystem units)
        {
            int key = SnowDepthKey(metres, units);
            if (units == UnitSystem.Imperial)
            {
                if (key < 12) return key.ToString(Invariant) + " in";
                int feet = key / 12, inches = key % 12;
                return inches == 0 ? feet.ToString(Invariant) + " ft" : feet.ToString(Invariant) + " ft " + inches.ToString(Invariant) + " in";
            }
            return key < 100 ? key.ToString(Invariant) + " cm" : (key / 100.0).ToString("0.##", Invariant) + " m";
        }

        /// <summary>Whole inches, or whole centimetres.</summary>
        public static int SnowDepthKey(double metres, UnitSystem units) =>
            (int)Math.Round(Math.Max(0, metres) / (units == UnitSystem.Imperial ? MetresPerInch : 0.01));

        /// <summary>The snow-depth legend's stops, in metres: 0, 6 in, 18 in, 3 ft, 6 ft, 10 ft; or 0, 15 cm, 50 cm, 1, 2, 3 m.</summary>
        public static double[] SnowDepthStops(UnitSystem units) => units == UnitSystem.Imperial
            ? new[] { 0, 6 * MetresPerInch, 18 * MetresPerInch, 3 * MetresPerFoot, 6 * MetresPerFoot, 10 * MetresPerFoot }
            : new[] { 0, 0.15, 0.5, 1, 2, 3 };

        /// <summary>Contours: a line every 40 ft (labelled every 200 ft, heavy every 1,000 ft, USGS mountain maps), or 10 / 50 / 250 m.</summary>
        public static double ContourIntervalMetres(UnitSystem units) => units == UnitSystem.Imperial ? 40 * MetresPerFoot : 10;
        public const int IndexEvery = 5, MajorEvery = 25;

        /// <summary>A contour's label: the elevation alone, as on a topographic map ("8200"; units come from the legend).</summary>
        public static string ContourLabel(double metres, UnitSystem units) =>
            ElevationKey(metres, units).ToString(Invariant);

        /// <summary>Scale-bar lengths, metres, and their labels: 20 ft … 5 mi, or 5 m … 10 km.</summary>
        public static (double Metres, string Label)[] ScaleLengths(UnitSystem units)
        {
            if (units == UnitSystem.Metric)
            {
                int[] m = { 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
                var metric = new (double, string)[m.Length];
                for (int i = 0; i < m.Length; i++) metric[i] = (m[i], m[i] >= 1000 ? (m[i] / 1000).ToString(Invariant) + " km" : m[i].ToString(Invariant) + " m");
                return metric;
            }
            int[] ft = { 20, 50, 100, 200, 500, 1000, 2000 };
            double[] mi = { 0.5, 1, 2, 5 };
            var imperial = new (double, string)[ft.Length + mi.Length];
            for (int i = 0; i < ft.Length; i++) imperial[i] = (ft[i] * MetresPerFoot, ft[i].ToString("N0", Invariant) + " ft");
            for (int i = 0; i < mi.Length; i++) imperial[ft.Length + i] = (mi[i] * FeetPerMile * MetresPerFoot, (mi[i] == 0.5 ? "½" : mi[i].ToString(Invariant)) + " mi");
            return imperial;
        }

        /// <summary>
        /// A site's size, e.g. "3.1 mi" or "5 km", to at most <paramref name="decimals"/> places (the site picker's
        /// 0.1 km slider asks for 2, so neighbouring steps read differently in miles: "1.37 mi", "1.43 mi").
        /// </summary>
        public static string SiteSize(double metres, UnitSystem units, int decimals = 1)
        {
            string format = decimals <= 0 ? "0" : "0." + new string('#', decimals);
            return units == UnitSystem.Imperial
                ? (metres / MetresPerFoot / FeetPerMile).ToString(format, Invariant) + " mi"
                : (metres / 1000).ToString(format, Invariant) + " km";
        }
    }
}
