using System;
using System.Globalization;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Every length the task 14 screens show goes through here, so the display-units setting (imperial by
    /// default, U or the menu switches it; task 12's shared setting) changes them all at once. Disk sizes stay
    /// in bytes and coordinates in degrees.
    /// </summary>
    public static class FlowUnits
    {
        const double MetresPerMile = 1609.344;

        /// <summary>Imperial (miles) or metric (km). The owner's default is imperial.</summary>
        public static bool Imperial = true;

        /// <summary>Raised when <see cref="Imperial"/> changes through <see cref="Set"/>, so open screens redraw.</summary>
        public static event Action Changed;

        public static void Set(bool imperial)
        {
            if (Imperial == imperial) return;
            Imperial = imperial;
            Changed?.Invoke();
        }

        /// <summary>A site's size: "2.0 km" or "1.24 mi" (the size steps are 0.1 km, so miles keep two decimals).</summary>
        public static string SiteSize(double km) => Imperial
            ? (km * 1000 / MetresPerMile).ToString("0.00", CultureInfo.InvariantCulture) + " mi"
            : km.ToString("0.0", CultureInfo.InvariantCulture) + " km";
    }
}
