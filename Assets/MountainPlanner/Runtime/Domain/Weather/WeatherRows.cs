using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// WX-17 v2's calibration rows: the site's climate boiled down to integers (WX-31), built at package build from the
    /// record (Acquisition) and read by the generator (<see cref="WeatherGenerator"/>). Two levels: 36 thirds of a month
    /// (October early first, means, medians and scales, each shrunk toward its parent) and site-wide pooled fields (shapes,
    /// tails, regressions). Durations are in tenths of a <b>real</b> hour, never face hours: the compression (WX-21) is the
    /// generator's, from <c>Pacing</c>, so a tuning of the season's length never stales the rows.
    ///
    /// Units: temperatures in 0.1 °F, liquid in 0.01 in, wind in 0.1 mph, shares in ‰, lapse in 0.01 °F per 100 ft, rates
    /// in 0.001 in per real hour, Q16 = 1/65,536, Q32 = 1/2³². The bytes (<c>Persistence/WeatherRowsFile</c>, every field in
    /// <see cref="Visit"/>'s order) are the rows' truth: a new field is a format change (0.3 §5).
    /// </summary>
    public sealed class WeatherRows
    {
        public const int Thirds = 36;
        /// <summary>The four hidden states (WX-01 v2, WX-23), in cycle order.</summary>
        public const int States = 4;
        public const int Fair = 0, Building = 1, Storm = 2, Clearing = 3;
        /// <summary>The quantile points of every fitted table (WX-31 v2), in 1/10,000; the 0 point is implicit (0).</summary>
        public static readonly int[] QuantilePoints = { 50, 100, 250, 500, 1000, 2500, 5000, 7500, 9000, 9500, 9750, 9900, 9950, 9990 };
        public static int QuantileCount => QuantilePoints.Length;
        /// <summary>Distributions with a quantile table: Fair gap, Building, Storm duration, Clearing (real 0.1 h), storm total (0.01 in).</summary>
        public const int Distributions = 5, StormTotal = 4;
        /// <summary>The three patterns (WX-57).</summary>
        public const int Patterns = 3, Ridge = 0, Zonal = 1, Active = 2;
        /// <summary>WX-11 v1: winters in the season character table (WX-19 v2).</summary>
        public const int Winters = 20;

        // ---- Site constants ----
        /// <summary>The reference pixel's elevation, Daymet's (ft): zref.</summary>
        public int ZrefFeet;
        /// <summary>WX-35's reference points from the package heights: base (10th percentile of the box), mid, summit (95th), ft.</summary>
        public int BaseFeet, MidFeet, SummitFeet;
        /// <summary>WX-41: the overnight snow at mid-mountain that makes a powder morning (0.1 in), never below 6 in.</summary>
        public int PowderDayTenthsIn;
        /// <summary>WX-15 v2's observation lag (h), for the record.</summary>
        public int ObservationLagHours;
        /// <summary>1 when the site is limited (WX-13, WX-17 v2).</summary>
        public int Limited;
        /// <summary>WX-11 v1: the record window's first label year.</summary>
        public int FirstLabelYear;
        /// <summary>WX-29: sunrise and sunset per label month (October first), face-clock minutes after midnight.</summary>
        public readonly int[] SunriseMinute = new int[12], SunsetMinute = new int[12];

        // ---- Per third (index 0 = October early) ----
        /// <summary>Climate at zref: mean daily max and min, the 10 % quantile of daily max (Cold snap, WX-41).</summary>
        public readonly int[] MaxMean = new int[Thirds], MinMean = new int[Thirds], MaxQ10 = new int[Thirds];
        /// <summary>
        /// The 24-hour diurnal shape above the day's min, fair days (‰ of the day's own range) and storm days (‰ of the
        /// third's mean range, so a storm day's smaller swing is kept, WX-25 v3): [third × 24 + hour].
        /// </summary>
        public readonly int[] DiurnalFair = new int[Thirds * 24], DiurnalStorm = new int[Thirds * 24];
        /// <summary>
        /// The temperature anomaly (WX-25 v2): its persistence per real hour (Q32; the generator raises it to the face hour in
        /// integers) and its stationary spread (0.1 °F).
        /// </summary>
        public readonly long[] AnomalyPhiHourQ32 = new long[Thirds];
        public readonly int[] AnomalySpread = new int[Thirds];
        /// <summary>
        /// WX-25 v3: the anomaly's shape. The anomaly runs as a normal (its persistence), and its value is read through this
        /// table: the record's November–April daily departures, standardised, at normal scores −3, −2.75 … 3 (‰ of the
        /// spread), linear between. Centred and scaled, so the mean and spread stay and only the shape is the record's:
        /// left-skewed in the West (cold outbreaks run further than warm spells; Stevens Pass −0.63). The default is a normal.
        /// </summary>
        public readonly int[] AnomalyShape = NormalShape();
        public const int AnomalyShapePoints = 25, AnomalyShapeStep = 250;
        /// <summary>
        /// WX-25 v4 (the fit review, 2026-11-08): how deep a night runs, its spread (tenths °F). Each night draws a depth
        /// when the day peaks, and the hour takes it in full at the bottom of the fair-day shape and none at its top: clear or
        /// cloudy, calm or windy, the record's nights vary far more than its afternoons. Set by playing the rows until the
        /// game's night spread over its afternoon spread is the record's; the anomaly gives up what the nights add to a day.
        /// </summary>
        public int NightDepthSpread;
        /// <summary>
        /// WX-25 v4: the hour's own wobble (tenths °F) and its memory from one face hour to the next (‰): a front's edge, a
        /// cloud deck coming and going, the wind turning. The record's hours vary more than its days explain; set by playing
        /// the rows until the game's hourly spread is the record's, its memory the record's within-day correlation at 3 h.
        /// </summary>
        public int FastSpread, FastMemory = 600;

        static int[] NormalShape()
        {
            var a = new int[AnomalyShapePoints];
            for (int i = 0; i < a.Length; i++) a[i] = (i - AnomalyShapePoints / 2) * AnomalyShapeStep;
            return a;
        }
        /// <summary>Quantile tables (WX-31 v2), [(third × Distributions + distribution) × QuantileCount + point].</summary>
        public readonly int[] Quantiles = new int[Thirds * Distributions * 14];
        /// <summary>The share of Buildings that are zero hours long (a storm out of a fair sky, WX-01 v2), ‰.</summary>
        public readonly int[] BuildingZeroShare = new int[Thirds];
        /// <summary>The storm total's log-normal median (0.01 in), kept beside the table for the post-storm cold (WX-25 v2).</summary>
        public readonly int[] StormMedian = new int[Thirds];
        /// <summary>WX-24 v2: the arrival octant histogram (‰, N first, clockwise), [third × 8 + octant].</summary>
        public readonly int[] ArrivalOctants = new int[Thirds * 8];
        /// <summary>
        /// The storm-hour temperature offset against the hour-of-day climate: mean and spread (0.1 °F). The spread is the
        /// storm's own, net of what the anomaly, the season and the size term already vary (calibration plays it, WX-25 v3).
        /// </summary>
        public readonly int[] StormOffsetMean = new int[Thirds], StormOffsetSpread = new int[Thirds];
        /// <summary>
        /// WX-25 v3: how much of the day-to-day anomaly a storm keeps (‰; 1,000 = all). Below 1,000 only where the record's
        /// storms vary less than the anomaly alone would make them (a maritime storm brings its own air).
        /// </summary>
        public readonly int[] StormAnomalyWeight = new int[Thirds];
        /// <summary>WX-25 v2: Building's and Clearing's temperature offsets (0.1 °F).</summary>
        public readonly int[] BuildingOffset = new int[Thirds], ClearingOffset = new int[Thirds];
        /// <summary>WX-57: the pattern mix (‰ × 3) and its derived 3 × 3 matrix (‰ × 9, row = from).</summary>
        public readonly int[] PatternMix = new int[Thirds * Patterns], PatternMatrix = new int[Thirds * Patterns * Patterns];
        /// <summary>WX-18 v2: the share of storms whose Clearing goes straight into Building (‰).</summary>
        public readonly int[] BackToBack = new int[Thirds];
        /// <summary>WX-24 v2: the share of the third's liquid that fell in Clearing hours (‰): the flurries.</summary>
        public readonly int[] ClearingLiquidShare = new int[Thirds];
        /// <summary>WX-26: mean dew-point depression by state (0.1 °F), [third × 4 + state].</summary>
        public readonly int[] DewDepression = new int[Thirds * States];
        /// <summary>WX-28 v2: free wind mean and spread by state (0.1 mph), [third × 4 + state].</summary>
        public readonly int[] WindMean = new int[Thirds * States], WindSpread = new int[Thirds * States];
        /// <summary>WX-28 v2: wind direction histograms for Fair and for Clearing (‰), [third × 8 + octant].</summary>
        public readonly int[] FairOctants = new int[Thirds * 8], ClearingOctants = new int[Thirds * 8];
        /// <summary>WX-27 v2: cloud amount by state by day and by night (‰), daytime clearness by state (‰), [third × 4 + state].</summary>
        public readonly int[] CloudDay = new int[Thirds * States], CloudNight = new int[Thirds * States], Clearness = new int[Thirds * States];
        /// <summary>WX-36 v2, WX-37, WX-38 v2: lapse by day and night and the dew-point lapse (0.01 °F per 100 ft), precipitation gradient (‰ per 100 ft).</summary>
        public readonly int[] LapseDay = new int[Thirds], LapseNight = new int[Thirds], DewLapse = new int[Thirds], PrecipGradient = new int[Thirds];
        /// <summary>WX-07 v3's targets at zref, per label year (checks, never inputs): mean temperature (0.1 °F), liquid and snow-phase liquid (0.01 in), storms, thaw days and rain days (× 100).</summary>
        public readonly int[] TargetMeanTemp = new int[Thirds], TargetLiquid = new int[Thirds], TargetSnowLiquid = new int[Thirds],
                              TargetStorms = new int[Thirds], TargetThawDays = new int[Thirds], TargetRainDays = new int[Thirds];

        // ---- Pooled over the site's winter ----
        /// <summary>Weibull shapes by state (× 1,000), the storm total's log-normal spread (× 1,000).</summary>
        public readonly int[] WeibullShape = new int[States];
        public int StormTotalSigma;
        /// <summary>The Gaussian-copula correlation between storm duration and total (‰) and its complement √(1 − ρ²) (‰).</summary>
        public int DurationTotalRho, DurationTotalRhoComplement;
        /// <summary>WX-24 v2, compass quarters N, E, S, W: size factor (‰), warm-sector and post-frontal offsets (0.1 °F).</summary>
        public readonly int[] QuarterSize = new int[4], QuarterWarm = new int[4], QuarterPost = new int[4];
        /// <summary>WX-57: persistence per season half (‰; November–January, February–April; the half also governs October and summer by nearness).</summary>
        public readonly int[] PatternPersistence = new int[2];
        /// <summary>Per pattern: gap factor and size factor (‰), temperature lean (0.1 °F), and a direction histogram (‰ × 8, all zero = use the third's).</summary>
        public readonly int[] PatternGap = new int[Patterns], PatternSize = new int[Patterns], PatternLean = new int[Patterns];
        public readonly int[] PatternOctants = new int[Patterns * 8];
        /// <summary>The dwell cap (cycles): a pattern held this long must leave.</summary>
        public int DwellCap;
        /// <summary>WX-25 v2: extra Clearing cold per storm size above the median (°F per inch of liquid above the median × 100; the cold is K × excess(0.01 in) ÷ 1,000 in 0.1 °F), and its cap (0.1 °F).</summary>
        public int PostStormColdK, PostStormColdCap;
        /// <summary>WX-24 v2: the fitted 99.5 % real hourly liquid rate (0.001 in per real hour); the generator caps at K × it.</summary>
        public int RateCapRealMilliIn;
        /// <summary>
        /// WX-25 v3 (owner 2026-11-08): bigger storms run warmer, by this much per standard deviation of the storm total's
        /// normal score (0.1 °F); and within a storm, heavier hours run warmer, by this much per unit of the hour's liquid
        /// over the storm's mean hour, less one (0.1 °F). Both are centred, so the storm's hour-weighted mean stays.
        /// </summary>
        public int StormSizeWarm, StormIntensityWarm;
        /// <summary>
        /// WX-25 v3: bigger storms vary less from storm to storm (their own air), smaller ones more: the storm's departure (its
        /// own draw and the anomaly it keeps) is scaled by 1 + this × the total's normal score, clamped to 0.25–2 (‰ per
        /// standard deviation). Weighted by liquid, the storms that bring the snow then vary as the record's do (Stevens Pass
        /// 3.7 °F, not 4.7).
        /// </summary>
        public int StormSizeSpread;
        /// <summary>
        /// WX-28 v3: the free wind's memory from one face hour to the next (‰), the record's correlation of its departures
        /// across a face hour's real length (about 2.9 h) outside storms; and in a storm, how its wind follows the hour's rate
        /// (‰ of the wind per unit of the hour's liquid over the storm's mean hour, less one). Both were fixed at 500.
        /// </summary>
        public int WindMemory = 500, StormWindPerRate = 500;
        /// <summary>WX-19 v2: the season character table, shrunk: per winter the liquid share (‰) and the temperature anomaly (0.1 °F).</summary>
        public readonly int[] CharacterShare = new int[Winters], CharacterAnomaly = new int[Winters];
        /// <summary>
        /// WX-25 v5 (owner 2026-11-09: Tahoe, Winter Park and Snowshoe melted out early): a wet spell's temperature. The running
        /// liquid keeps <see cref="WetMemory"/> ‰ of itself each face hour (an e-folding of a week); about its month and
        /// state's mean (<see cref="WetMean"/>, in its spreads <see cref="WetSpread"/>, per third × state: 0.00001 in a face
        /// hour), it leans every hour by <see cref="WetLean"/> tenths °F per spread, colder when wetter (negative: warmer).
        /// Centred on each state, so the states keep their own temperatures (the record's): a Fair day in a wet fortnight runs
        /// colder than one in a dry month. Set by playing the rows until the game's days run as much colder in a wet week as
        /// the record's did.
        /// </summary>
        public int WetMemory, WetLean;
        public readonly int[] WetMean = new int[Thirds * States], WetSpread = new int[Thirds * States];

        /// <summary>Walks every field in the file's order: one place for the writer, the reader and the hash.</summary>
        public void Visit(IWeatherRowsVisitor v)
        {
            v.Int(ref ZrefFeet); v.Int(ref BaseFeet); v.Int(ref MidFeet); v.Int(ref SummitFeet);
            v.Int(ref PowderDayTenthsIn); v.Int(ref ObservationLagHours); v.Int(ref Limited); v.Int(ref FirstLabelYear);
            v.Ints(SunriseMinute); v.Ints(SunsetMinute);
            v.Ints(MaxMean); v.Ints(MinMean); v.Ints(MaxQ10); v.Ints(DiurnalFair); v.Ints(DiurnalStorm);
            v.Longs(AnomalyPhiHourQ32); v.Ints(AnomalySpread); v.Ints(AnomalyShape); v.Int(ref NightDepthSpread); v.Int(ref FastSpread); v.Int(ref FastMemory);
            v.Ints(Quantiles); v.Ints(BuildingZeroShare); v.Ints(StormMedian);
            v.Ints(ArrivalOctants); v.Ints(StormOffsetMean); v.Ints(StormOffsetSpread); v.Ints(StormAnomalyWeight); v.Ints(BuildingOffset); v.Ints(ClearingOffset);
            v.Ints(PatternMix); v.Ints(PatternMatrix); v.Ints(BackToBack); v.Ints(ClearingLiquidShare);
            v.Ints(DewDepression); v.Ints(WindMean); v.Ints(WindSpread); v.Ints(FairOctants); v.Ints(ClearingOctants);
            v.Ints(CloudDay); v.Ints(CloudNight); v.Ints(Clearness);
            v.Ints(LapseDay); v.Ints(LapseNight); v.Ints(DewLapse); v.Ints(PrecipGradient);
            v.Ints(TargetMeanTemp); v.Ints(TargetLiquid); v.Ints(TargetSnowLiquid); v.Ints(TargetStorms); v.Ints(TargetThawDays); v.Ints(TargetRainDays);
            v.Ints(WeibullShape); v.Int(ref StormTotalSigma); v.Int(ref DurationTotalRho); v.Int(ref DurationTotalRhoComplement);
            v.Ints(QuarterSize); v.Ints(QuarterWarm); v.Ints(QuarterPost);
            v.Ints(PatternPersistence); v.Ints(PatternGap); v.Ints(PatternSize); v.Ints(PatternLean); v.Ints(PatternOctants); v.Int(ref DwellCap);
            v.Int(ref PostStormColdK); v.Int(ref PostStormColdCap); v.Int(ref RateCapRealMilliIn);
            v.Int(ref StormSizeWarm); v.Int(ref StormIntensityWarm); v.Int(ref StormSizeSpread); v.Int(ref WindMemory); v.Int(ref StormWindPerRate);
            v.Ints(CharacterShare); v.Ints(CharacterAnomaly);
            v.Int(ref WetMemory); v.Int(ref WetLean); v.Ints(WetMean); v.Ints(WetSpread);
        }

        public int Quantile(int third, int distribution, int point) => Quantiles[(third * Distributions + distribution) * QuantileCount + point];
        public void SetQuantile(int third, int distribution, int point, int value) => Quantiles[(third * Distributions + distribution) * QuantileCount + point] = value;

        /// <summary>A content hash over every field (FNV-1a over 64-bit words), for the replay header (SI-22).</summary>
        public ulong ContentHash()
        {
            var h = new HashVisitor();
            Visit(h);
            return h.Value;
        }

        public WeatherRows Clone()
        {
            var copy = new WeatherRows();
            var values = new CollectVisitor();
            Visit(values);
            var fill = new FillVisitor(values.Values.ToArray());
            copy.Visit(fill);
            return copy;
        }

        sealed class HashVisitor : IWeatherRowsVisitor
        {
            ulong _h = 14695981039346656037UL;
            public ulong Value => _h;
            void Add(long v) { for (int i = 0; i < 8; i++) { _h ^= (byte)(v >> (8 * i)); _h *= 1099511628211UL; } }
            public void Int(ref int v) => Add(v);
            public void Ints(int[] a) { Add(a.Length); foreach (int v in a) Add(v); }
            public void Longs(long[] a) { Add(a.Length); foreach (long v in a) Add(v); }
        }

        sealed class CollectVisitor : IWeatherRowsVisitor
        {
            public readonly System.Collections.Generic.List<long> Values = new System.Collections.Generic.List<long>();
            public void Int(ref int v) => Values.Add(v);
            public void Ints(int[] a) { foreach (int v in a) Values.Add(v); }
            public void Longs(long[] a) { foreach (long v in a) Values.Add(v); }
        }

        sealed class FillVisitor : IWeatherRowsVisitor
        {
            readonly long[] _v;
            int _i;
            public FillVisitor(long[] v) => _v = v;
            public void Int(ref int v) => v = (int)_v[_i++];
            public void Ints(int[] a) { for (int k = 0; k < a.Length; k++) a[k] = (int)_v[_i++]; }
            public void Longs(long[] a) { for (int k = 0; k < a.Length; k++) a[k] = _v[_i++]; }
        }
    }

    /// <summary>A walk over <see cref="WeatherRows"/>' fields in their fixed order.</summary>
    public interface IWeatherRowsVisitor
    {
        void Int(ref int value);
        void Ints(int[] values);
        void Longs(long[] values);
    }
}
