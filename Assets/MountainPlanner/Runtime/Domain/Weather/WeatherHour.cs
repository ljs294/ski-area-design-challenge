namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// WX-30: one face hour's parameter set at zref, site-wide (integers, WX-31). Everything over the map is a pure function of
    /// this and a cell's static inputs (WX-02 v2). Temperatures in 0.1 °F, liquid in 0.01 in, wind in 0.1 mph, shares in ‰.
    /// </summary>
    public struct WeatherHour
    {
        public int LabelYear;
        /// <summary>Face hours since 00:00 on October 1 of the label year, and the face clock's hour of the day (the diurnal cycle, WX-21).</summary>
        public int HourOfYear, HourOfDay;
        /// <summary>WX-20: the label date's third, its neighbour, and the neighbour's weight (‰).</summary>
        public int Third, NeighbourThird, NeighbourWeight;
        /// <summary>The hidden state the hour ends in (WX-23) and the pattern (WX-57). Never shown by name (owner Q4).</summary>
        public int State, Pattern;
        public bool Night;
        public int Temp, Dew;
        /// <summary>WX-36 v2: the hour's lapse (day or night; a storm's is the daytime one), the dew-point lapse (0.01 °F per 100 ft), the precipitation gradient (‰ per 100 ft).</summary>
        public int Lapse, DewLapse, PrecipGradient;
        /// <summary>WX-29: the cold pool's strength at the valley floor (0.1 °F) and the fog flag.</summary>
        public int Pool;
        public bool Fog;
        /// <summary>Liquid that fell at zref in this face hour (0.01 in).</summary>
        public int Liquid;
        /// <summary>The storm under way, if any: ordinal (−1 none), arrival octant and direction, total, whether its front has passed.</summary>
        public int StormOrdinal, StormOctant, StormDirection, StormTotal;
        public bool FrontPassed;
        /// <summary>WX-28 v2: the free wind's speed (0.1 mph), direction (degrees from), gust factor (‰).</summary>
        public int Wind, Direction, GustFactor;
        /// <summary>WX-27 v2: cloud cover (‰), daytime clearness (‰, 0 at night), cloud base above zref (ft).</summary>
        public int Cloud, Clearness, CloudBaseFeet;
        /// <summary>WX-34: the season's temperature offset (0.1 °F), and its liquid share (‰).</summary>
        public int SeasonOffset, SeasonShare;
        /// <summary>WX-25 v5: the wet spell's lean on the hour's temperature (0.1 °F; negative in a wet week).</summary>
        public int WetLean;
    }

    /// <summary>The <c>Pacing</c> numbers the generator reads (SI-01), so Domain needs no reference to Simulation.</summary>
    public readonly struct WeatherTuning
    {
        /// <summary>WX-21: calendar days and face days of the season: a face hour is C ÷ S real hours.</summary>
        public readonly int CalendarDays, FaceDays;
        /// <summary>WX-17 v2: gust factors by state (‰) and the burst spread (‰). The wind-vs-rate slope is the site's (WX-28 v3).</summary>
        public readonly int GustFactorStorm, GustFactorFair, BurstMin, BurstMax;
        /// <summary>WX-29: the pool's build per clear calm night hour and its cap (0.1 °F).</summary>
        public readonly int PoolBuildPerHour, PoolMax;
        /// <summary>WX-34: <c>WinterScale</c> as a liquid factor (‰) and a temperature offset (0.1 °F); WX-39's <c>WindScale</c> (‰).</summary>
        public readonly int WinterLiquid, WinterTemp, WindScale;

        public WeatherTuning(int calendarDays, int faceDays, int gustFactorStorm, int gustFactorFair, int burstMin, int burstMax,
                             int poolBuildPerHour, int poolMax, int winterLiquid, int winterTemp, int windScale)
        {
            CalendarDays = calendarDays; FaceDays = faceDays; GustFactorStorm = gustFactorStorm; GustFactorFair = gustFactorFair;
            BurstMin = burstMin; BurstMax = burstMax; PoolBuildPerHour = poolBuildPerHour; PoolMax = poolMax;
            WinterLiquid = winterLiquid; WinterTemp = winterTemp; WindScale = windScale;
        }

        /// <summary>The defaults, equal to <c>Pacing.Default</c>'s (a test checks they agree): what calibration plays with.</summary>
        public static WeatherTuning Default => new WeatherTuning(121, 42, 1600, 1300, 500, 1600, 10, 80, 1000, 0, 1000);

        public WeatherTuning WithWinter(int liquid, int temp) =>
            new WeatherTuning(CalendarDays, FaceDays, GustFactorStorm, GustFactorFair, BurstMin, BurstMax, PoolBuildPerHour, PoolMax,
                              liquid, temp, WindScale);
    }

    /// <summary>A walk over the generator's state, for the sim sidecar (WX-32).</summary>
    public interface IWeatherStateVisitor
    {
        void Int(ref int value);
        void Long(ref long value);
    }
}
