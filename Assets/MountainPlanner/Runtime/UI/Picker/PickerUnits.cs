using System;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;

namespace MountainPlanner.UI.Picker
{
    /// <summary>
    /// How the picker shows lengths: the game's one units switch (<see cref="DisplayUnits"/>, imperial by
    /// default, U or the menu) and its formatting (<see cref="UnitFormat.SiteSize"/>). Sizes take two decimals
    /// so each 0.1 km step on the slider reads differently ("1.37 mi", "1.43 mi"). Only the display converts;
    /// the square, its steps and EPSG:6350 stay metric.
    /// </summary>
    public static class PickerUnits
    {
        const int Decimals = 2;

        public static event Action Changed
        {
            add => DisplayUnits.Changed += value;
            remove => DisplayUnits.Changed -= value;
        }

        public static bool Imperial => DisplayUnits.Imperial;

        /// <summary>A site size: "2.49 mi" or "4 km".</summary>
        public static string Size(double km) => UnitFormat.SiteSize(km * 1000, DisplayUnits.Current, Decimals);

        /// <summary>A slider end: "1.24 mi" or "2 km".</summary>
        public static string SizeEnd(double km) => Size(km);
    }
}
