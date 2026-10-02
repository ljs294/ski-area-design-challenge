using System;
using System.Globalization;

namespace MountainPlanner.UI.Picker
{
    /// <summary>
    /// How the picker shows lengths: imperial by default (owner, 2026-10-02), metric when switched. Only the
    /// display converts; the square, its 0.1 km steps and EPSG:6350 stay metric underneath.
    /// The one switch point for the shared display-units setting (task 12b.2): when it lands, the picker
    /// sets <see cref="Imperial"/> from it and listens for its Changed event.
    /// </summary>
    public static class PickerUnits
    {
        public const double KmPerMile = 1.609344;

        static bool _imperial = true;

        public static event Action Changed;

        public static bool Imperial
        {
            get => _imperial;
            set
            {
                if (_imperial == value) return;
                _imperial = value;
                Changed?.Invoke();
            }
        }

        /// <summary>A site size: "4.0 km", or "2.49 mi" (two decimals, so each 0.1 km step still reads differently).</summary>
        public static string Size(double km) => _imperial
            ? (km / KmPerMile).ToString("0.00", CultureInfo.InvariantCulture) + " mi"
            : km.ToString("0.0", CultureInfo.InvariantCulture) + " km";

        /// <summary>A slider end: "2 km", or "1.24 mi".</summary>
        public static string SizeEnd(double km) => _imperial
            ? (km / KmPerMile).ToString("0.00", CultureInfo.InvariantCulture) + " mi"
            : km.ToString("0", CultureInfo.InvariantCulture) + " km";
    }
}
