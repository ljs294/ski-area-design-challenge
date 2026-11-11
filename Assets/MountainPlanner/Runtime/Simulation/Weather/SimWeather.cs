using System;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.05 PR B1: the weather the world plays (SI-21: built off the worker, delivered whole, never changed). The rows are
    /// the area's own (<c>weather.rows</c>) or the typical mountain's (<see cref="WeatherGeneric"/>); the reference points
    /// (WX-35's base, mid and summit, ft) are the area's, from its own downloaded heights, never the player's earthwork.
    /// The generator reads only the rows, so the reference points can follow later (an area's heights finish loading after
    /// the clock starts) without touching the weather; they move the point samples and each lift's exposure.
    /// </summary>
    public sealed class SimWeather
    {
        public readonly WeatherRows Rows;
        /// <summary>SI-22: the rows' content hash: a save keeps the rows it ran with until October 1 (WX-32 v2).</summary>
        public readonly ulong RowsHash;
        /// <summary>The typical mountain's rows: this area has no weather history yet.</summary>
        public readonly bool Generic;
        public readonly int BaseFeet, MidFeet, SummitFeet;

        public SimWeather(WeatherRows rows, bool generic, int baseFeet, int summitFeet)
        {
            Rows = (rows ?? throw new ArgumentNullException(nameof(rows))).Clone();
            RowsHash = Rows.ContentHash();
            Generic = generic;
            if (summitFeet < baseFeet) (baseFeet, summitFeet) = (summitFeet, baseFeet);
            BaseFeet = baseFeet;
            SummitFeet = summitFeet;
            MidFeet = (baseFeet + summitFeet) / 2;
        }

        /// <summary>The area's own rows, with the reference points they were built with.</summary>
        public static SimWeather Own(WeatherRows rows) => new SimWeather(rows, false, rows.BaseFeet, rows.SummitFeet);

        /// <summary>The same rows at other reference points.</summary>
        public SimWeather WithPoints(int baseFeet, int summitFeet) => new SimWeather(Rows, Generic, baseFeet, summitFeet);

        /// <summary>WX-21 and the rest of the generator's tuning, from the pacing table.</summary>
        public static WeatherTuning Tuning(Pacing p) =>
            new WeatherTuning(p.SeasonCalendarDays, p.SeasonDays, p.GustFactorStorm, p.GustFactorFair, p.StormBurst[0], p.StormBurst[1],
                              p.PoolBuildPerHour, p.PoolMax, p.WinterLiquid, p.WinterTemp, p.WindScale);

        /// <summary>
        /// WX-39 (B1's form, before the steering grid of B-fields): a point's exposure factor (‰), from its height between the
        /// base and the summit: the sheltered valley class at the base and below, the exposed summit class at the summit and
        /// above, the classes between in steps.
        /// </summary>
        public int ExposureAt(Pacing p, int feet)
        {
            var e = p.WindExposure;
            int span = SummitFeet - BaseFeet;
            if (span <= 0) return e[e.Length / 2];
            long at = (long)(feet - BaseFeet) * (e.Length - 1) * 1000 / span;   // 0 … 7000
            if (at <= 0) return e[0];
            if (at >= (e.Length - 1) * 1000L) return e[e.Length - 1];
            int k = (int)(at / 1000), f = (int)(at % 1000);
            return e[k] + (int)((long)(e[k + 1] - e[k]) * f / 1000);
        }
    }
}
