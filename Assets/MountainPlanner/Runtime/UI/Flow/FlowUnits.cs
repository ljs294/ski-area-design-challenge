using System;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// The task 14 screens' view of the game's one display-units setting (<see cref="DisplayUnits"/>, task 12b.2:
    /// imperial by default, U or the menu switches it, remembered between sessions) and its formats
    /// (<see cref="UnitFormat"/>). Disk sizes stay in bytes and coordinates in degrees.
    /// </summary>
    public static class FlowUnits
    {
        public static bool Imperial => DisplayUnits.Imperial;

        /// <summary>Raised when the game's units change, so open screens redraw.</summary>
        public static event Action Changed
        {
            add => DisplayUnits.Changed += value;
            remove => DisplayUnits.Changed -= value;
        }

        /// <summary>Switches the whole game; <paramref name="remember"/> false for tests, so the player's choice is untouched.</summary>
        public static void Set(bool imperial, bool remember = true) => DisplayUnits.Set(imperial ? UnitSystem.Imperial : UnitSystem.Metric, remember);

        /// <summary>A site's size, in the game's format: "3.1 mi" or "5 km".</summary>
        public static string SiteSize(double km) => UnitFormat.SiteSize(km * 1000, DisplayUnits.Current);
    }
}
