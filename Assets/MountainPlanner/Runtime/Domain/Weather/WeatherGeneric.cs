using System;
using MountainPlanner.Domain.Sky;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// Task 4.05 PR B1 (plan decision 3): the typical mountain's weather for an area with no weather history yet (no weather
    /// yet, or "No history" outside Daymet). Two frozen rows ship with the game, a western and an eastern site's; the area's
    /// own longitude picks one. The rows keep their own zref, so the lapse carries their climate to the area's height; only
    /// the sun moves (sunrise and sunset at the area, WX-29's nights), and the rows say they're limited.
    /// </summary>
    public static class WeatherGeneric
    {
        /// <summary>The western rows (Jackson Hole, Wyoming) and the eastern ones (Jackson, New Hampshire).</summary>
        public const string WestFile = "generic-west.rows", EastFile = "generic-east.rows";
        /// <summary>The 100th meridian: the Great Plains' edge, where the western mountains' dry continental winters give way.</summary>
        public const double EastOfLongitude = -100.0;

        public static bool East(double longitude) => longitude >= EastOfLongitude;

        public static string FileFor(double longitude) => East(longitude) ? EastFile : WestFile;

        /// <summary>A copy of <paramref name="template"/> with the sun of the area at (<paramref name="latitude"/>, <paramref name="longitude"/>).</summary>
        public static WeatherRows MoveTo(WeatherRows template, double latitude, double longitude)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var rows = template.Clone();
            SunTable(rows, latitude, longitude);
            rows.Limited = 1;
            return rows;
        }

        /// <summary>Calendar days before each month of a 365-day year, January first.</summary>
        static readonly int[] DaysBefore = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };

        /// <summary>WX-29: sunrise and sunset per label month on its 15th (face-clock minutes), as the calibration sets them.</summary>
        public static void SunTable(WeatherRows rows, double latitude, double longitude)
        {
            for (int m = 0; m < 12; m++)
            {
                int month = (m + 9) % 12 + 1;
                int year = m <= 2 ? 2022 : 2023;
                var day = new SolarDay(latitude, longitude, year, DaysBefore[month - 1] + 15);
                rows.SunriseMinute[m] = (int)Math.Round(day.Sunrise() / 60);
                rows.SunsetMinute[m] = (int)Math.Round(day.Sunset() / 60);
            }
        }
    }
}
