using System.Collections.Generic;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Acquisition.Weather.Qc;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The recorded weather sites (task 4.05), loaded once per test run. "Fixes must be general": every acceptance test runs
    /// on all seven (owner, 2026-11-08: more climates, so the fit is tuned to none):
    /// - Jackson Hole (the 2 km fixture): continental Rockies, cold and dry snow;
    /// - Olympic Valley (Palisades Tahoe, 4 km): Sierra atmospheric rivers, rain at the base;
    /// - Stevens Pass: Cascades, maritime, wet and heavy, the snow line on the hill;
    /// - Winter Park: Colorado Front Range, high, cold and windy, POWER's thinnest wet-day match;
    /// - Jackson NH (4.4 km): New England, ice and thaws, no SNOTEL;
    /// - Snowshoe: Appalachian, marginal, rain and refreezes, no SNOTEL;
    /// - Snow Ridge (Tug Hill): lake effect, low and snowy in a cold wind, no SNOTEL.
    /// </summary>
    static class WeatherFixtures
    {
        public const string JacksonHole = "jackson-hole-2km", OlympicValley = "olympic-valley-4km", StevensPass = "stevens-pass-2km",
                            WinterPark = "winter-park-2km", JacksonNh = "jackson-nh-4km", Snowshoe = "snowshoe-2km", SnowRidge = "snow-ridge-2km";
        public static readonly string[] Sites = { JacksonHole, OlympicValley, StevensPass, WinterPark, JacksonNh, Snowshoe, SnowRidge };

        static readonly Dictionary<string, (WeatherSourceRecord Record, WeatherQcLog Log, BlendedRecord Blend)> Cache =
            new Dictionary<string, (WeatherSourceRecord, WeatherQcLog, BlendedRecord)>();

        /// <summary>A fresh, unscreened copy (tests that plant faults change it).</summary>
        public static WeatherSourceRecord Raw(string site) =>
            WeatherFetch.LoadRecordingAsync(TestData.Folder("weather/" + site)).GetAwaiter().GetResult();

        /// <summary>The screened record, its QC log and its blend (shared: never change them).</summary>
        public static (WeatherSourceRecord Record, WeatherQcLog Log, BlendedRecord Blend) Screened(string site)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(site, out var hit)) return hit;
                var record = Raw(site);
                var log = new WeatherQcLog();
                DaymetScreen.Apply(record, log);
                PowerScreen.Apply(record, log);
                SnotelScreen.Apply(record, log);
                var blend = WeatherBlend.Build(record);
                return Cache[site] = (record, log, blend);
            }
        }

        static readonly Dictionary<string, (WeatherRows Rows, CalibrationReport Report)> Rows =
            new Dictionary<string, (WeatherRows, CalibrationReport)>();

        /// <summary>The site's calibrated rows at the default tuning (shared: clone before changing them).</summary>
        public static (WeatherRows Rows, CalibrationReport Report) Calibrated(string site)
        {
            var screened = Screened(site);
            lock (Rows)
            {
                if (Rows.TryGetValue(site, out var hit)) return hit;
                var s = screened.Record.Site;
                var points = new ReferencePoints(s.BaseFeet, s.SummitFeet);
                return Rows[site] = WeatherCalibration.Build(screened.Blend, screened.Record, points, WeatherTuning.Default);
            }
        }
    }
}
