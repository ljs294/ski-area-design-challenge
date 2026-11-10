using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// WX-21–WX-34 and WX-56–WX-58: the site-level weather machine, integers only (WX-31 v2). It reads a site's
    /// <see cref="WeatherRows"/>, a season seed and the face hour, and nothing else (WX-33): never the design, the snow, the
    /// skiers, the speed or a skip. One <see cref="Step"/> per face hour fills a <see cref="WeatherHour"/>; everything over
    /// the map is computed from that (WX-02 v2, Simulation's field step).
    ///
    /// <para>The synoptic clock (WX-21): a face hour is 10 × C units and a tenth of a real hour is S units (C calendar days
    /// over S face days of the season), so every real duration from the rows converts exactly and states change inside a
    /// face hour with their remainders carried (WX-23 v2). The diurnal cycle alone runs on the face clock.</para>
    ///
    /// <para>All buffers are allocated here, in the constructor: stepping allocates nothing (WX-51).</para>
    /// </summary>
    public sealed class WeatherGenerator
    {
        public const int MaxSlots = 256;
        /// <summary>WX-24 v2: the rate cap's floor, 0.40 in of liquid per face hour.</summary>
        public const int RateCapFloor = 40;
        /// <summary>WX-27 v2: cloud base, 228 ft per °F of dew-point depression (the depression is in 0.1 °F).</summary>
        public const int CloudBaseFeetPerTenthF = 228;
        /// <summary>WX-29's thresholds: clear under 30 % cloud, calm under 8 mph, scoured above 15 mph at the floor (× 0.4 of the free wind).</summary>
        public const int PoolClearCloud = 300, PoolCalmWind = 80, PoolScourWind = 150, ShelteredFactor = 400;
        /// <summary>WX-29: fog when the pool is at least 3 °F and the depression under 2 °F.</summary>
        public const int FogPool = 30, FogDepression = 20;

        static readonly int[] MonthStartDay = { 0, 31, 61, 92, 123, 151, 182, 212, 243, 273, 304, 335, 365 };

        readonly WeatherRows _rows;
        readonly WeatherTuning _t;
        readonly long _unitsPerFaceHour, _unitsPerRealTenth;
        readonly long[] _phiFace = new long[WeatherRows.Thirds];
        readonly int[] _innovationQ16 = new int[WeatherRows.Thirds];
        readonly int[] _fairTop = new int[WeatherRows.Thirds], _fairTopHour = new int[WeatherRows.Thirds];   // the fair shape's peak (‰) and its hour
        readonly int _windMemory, _windFresh, _fastMemory, _fastFresh;   // ‰: the wind's memory per face hour and √(1 − memory²)
        /// <summary>The middle of each third in real tenths of an hour after October 1.</summary>
        readonly int[] _thirdMiddle = new int[WeatherRows.Thirds];
        readonly int _rateCap, _minShare, _maxShare;
        readonly int[] _slotLiquid = new int[MaxSlots], _slotWeight = new int[MaxSlots];

        // ---- The state (WX-32): every field below is in VisitState, so a save and reload gives the same future ----
        ulong _seed;
        int _labelYear, _hour, _state, _changes, _cycle, _storms, _pattern, _dwell;
        long _unitsLeft, _stateUnits, _elapsed;
        int _anomaly;                 // 0.01 °F
        int _pool, _poolAtSunrise;
        int _stormWeight;             // ‰, the diurnal shape's blend toward the storm-day shape
        int _eased, _easeRate, _lastTarget;
        int _share, _sqrtShare, _seasonTemp;
        int _windDev;                 // ‰ of a standard deviation, persistent between hours
        int _storm, _stormOctant, _stormDir, _clearDir, _stormTotal, _stormSlots, _front, _stormWind, _stormOffset, _stormWarm, _stormPost, _nextSlot;
        int _stormScale = 1000;       // ‰, the storm's departure scale by its size (WX-25 v3)
        long _stormUnits;
        int _lastStormTotal, _flurryTotal;
        long _flurryAcc;
        int _weightSum;
        int _wet;                     // the running liquid (WX-25 v5), 0.00001 in a face hour

        // ---- Per-hour scratch (not state) ----
        int _tA, _tB, _wB;

        public WeatherGenerator(WeatherRows rows, WeatherTuning tuning)
        {
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _t = tuning;
            if (tuning.CalendarDays <= 0 || tuning.FaceDays <= 0) throw new ArgumentException("The season needs calendar and face days.");
            _unitsPerFaceHour = 10L * tuning.CalendarDays;
            _unitsPerRealTenth = tuning.FaceDays;
            for (int i = 0; i < WeatherRows.Thirds; i++)
            {
                long phi = WeatherMath.Clamp(rows.AnomalyPhiHourQ32[i], 0, WeatherMath.Q32One - 1);
                _phiFace[i] = WeatherMath.PowRatioQ32(phi, tuning.CalendarDays, tuning.FaceDays);
                long rest = WeatherMath.Q32One - WeatherMath.MulQ32Exact(_phiFace[i], _phiFace[i]);
                _innovationQ16[i] = (int)WeatherMath.ISqrt(Math.Max(0, rest));   // √ of a Q32 is a Q16
                int month = i / 3, part = i % 3;
                int start = MonthStartDay[month] + part * 10;
                int length = part < 2 ? 10 : MonthStartDay[month + 1] - MonthStartDay[month] - 20;
                _thirdMiddle[i] = (start * 24 + length * 12) * 10;
                for (int h = 0; h < 24; h++)
                    if (h == 0 || rows.DiurnalFair[i * 24 + h] > _fairTop[i]) { _fairTop[i] = rows.DiurnalFair[i * 24 + h]; _fairTopHour[i] = h; }
            }
            // WX-17 v2: K × the fitted 99.5 % real hourly rate, in 0.01 in per face hour, never below the floor.
            _rateCap = Math.Max(RateCapFloor, (int)WeatherMath.DivRound((long)rows.RateCapRealMilliIn * tuning.CalendarDays, 10L * tuning.FaceDays));
            _windMemory = WeatherMath.Clamp(rows.WindMemory, 0, 990);
            _windFresh = (int)WeatherMath.ISqrt(1_000_000L - (long)_windMemory * _windMemory);
            _fastMemory = WeatherMath.Clamp(rows.FastMemory, 0, 950);
            _fastFresh = (int)WeatherMath.ISqrt(1_000_000L - (long)_fastMemory * _fastMemory);
            _minShare = int.MaxValue; _maxShare = int.MinValue;
            foreach (int s in rows.CharacterShare) { _minShare = Math.Min(_minShare, s); _maxShare = Math.Max(_maxShare, s); }
            if (_minShare > _maxShare) { _minShare = _maxShare = 1000; }
            _storm = -1;
        }

        /// <summary>WX-21: face hours in a label year (365 calendar days from October 1, compressed).</summary>
        public static int HoursInYear(WeatherTuning tuning) => (int)(365L * 24 * tuning.FaceDays / tuning.CalendarDays);

        public WeatherRows Rows => _rows;
        public int LabelYear => _labelYear;
        /// <summary>The face hour the next <see cref="Step"/> generates (face hours since 00:00 on October 1).</summary>
        public int Hour => _hour;
        public int State => _state;
        public int Pattern => _pattern;
        /// <summary>WX-57: cycles begun (one per Fair spell) and how many in a row kept the pattern (the dwell cap's count).</summary>
        public int Cycle => _cycle;
        public int Dwell => _dwell;
        public int RateCap => _rateCap;
        /// <summary>WX-34: this season's liquid share (‰) and temperature offset (0.1 °F).</summary>
        public int SeasonShare => _share;
        public int SeasonTemperature => _seasonTemp;
        /// <summary>WX-25 v2: the day-to-day temperature anomaly (0.01 °F), before a storm's weight (calibration's plays read it).</summary>
        /// <summary>The last hour's anomaly as it entered the temperature, through the shape (0.01 °F; not state).</summary>
        public int Anomaly => _shaped;
        int _shaped;
        int _nightDepth;              // WX-25 v4: tonight's depth (tenths °F), drawn as the day peaks
        int _fast;                    // WX-25 v4: the hour's own wobble (‰ of its spread)

        /// <summary>
        /// WX-56: 00:00 on October 1. Re-initialised from the season's seed alone: Fair with a keyed duration, the anomaly drawn
        /// from its stationary spread, pool 0, the season character drawn (WX-34), the first pattern from October-early's mix.
        /// </summary>
        public void Reset(ulong seasonSeed, int labelYear)
        {
            _seed = seasonSeed;
            _labelYear = labelYear;
            _hour = 0; _changes = 0; _cycle = 0; _storms = 0; _dwell = 0;
            _pool = 0; _poolAtSunrise = 0; _stormWeight = 0; _eased = 0; _easeRate = 0; _lastTarget = 0; _windDev = 0; _nightDepth = 0; _fast = 0;
            _storm = -1; _stormOctant = 0; _stormDir = 0; _clearDir = 0; _stormTotal = 0; _stormSlots = 0; _front = 0; _stormWind = 0;
            _stormOffset = 0; _stormScale = 1000; _stormWarm = 0; _stormPost = 0; _nextSlot = 0; _stormUnits = 0; _lastStormTotal = 0; _flurryTotal = 0; _flurryAcc = 0; _weightSum = 0;

            // WX-34: one of the record's winters, jittered, its share clamped to the record's own range.
            int w = WeatherMath.Between(WeatherMath.Key(_seed, WeatherKey.SeasonCharacter, 0), 0, WeatherRows.Winters - 1);
            int jitter = WeatherMath.Between(WeatherMath.Key(_seed, WeatherKey.SeasonCharacter, 1), -100, 100);
            _share = WeatherMath.Clamp((int)WeatherMath.DivRound((long)_rows.CharacterShare[w] * (1000 + jitter), 1000), _minShare, _maxShare);
            _share = Math.Max(1, _share);
            _sqrtShare = (int)WeatherMath.ISqrt((long)_share * 1000);
            _seasonTemp = _rows.CharacterAnomaly[w] + WeatherMath.Between(WeatherMath.Key(_seed, WeatherKey.SeasonCharacter, 2), -10, 10);

            Calendar(0);
            int spread = Blend(_rows.AnomalySpread, 1, 0);
            _anomaly = (int)WeatherMath.Clamp(WeatherMath.DivRound((long)WeatherMath.Normal1000(WeatherMath.Key(_seed, WeatherKey.Anomaly, -1)) * spread, 100), -30L * spread, 30L * spread);

            int third = Nearer();
            _wet = _rows.WetMean[third * WeatherRows.States + WeatherRows.Fair];
            _pattern = WeatherMath.FromHistogram(_rows.PatternMix, third * WeatherRows.Patterns, WeatherRows.Patterns, WeatherMath.Key(_seed, WeatherKey.Pattern, -1));
            _state = WeatherRows.Fair;
            _stateUnits = _unitsLeft = FairUnits();
            _elapsed = 0;
        }

        /// <summary>WX-56: re-run from October 1 to the given face hour (a seed changed mid-year, or a load).</summary>
        public void RunTo(ulong seasonSeed, int labelYear, int hour)
        {
            Reset(seasonSeed, labelYear);
            var scratch = new WeatherHour();
            while (_hour < hour) Step(ref scratch);
        }

        /// <summary>Generates face hour <see cref="Hour"/> and advances to the next.</summary>
        public void Step(ref WeatherHour h)
        {
            Calendar(_hour);
            int liquid = 0;
            bool stormInHour = false, changed = false;
            long remaining = _unitsPerFaceHour;
            int guard = 0;
            while (remaining > 0)
            {
                if (_unitsLeft <= 0)
                {
                    Transition(remaining);
                    changed = true;
                    if (++guard > 64) break;   // a pathological row of zero-length states: let the hour end
                    continue;
                }
                long take = Math.Min(remaining, _unitsLeft);
                if (_state == WeatherRows.Storm)
                {
                    stormInHour = true;
                    long end = _elapsed + take;
                    while (_nextSlot < _stormSlots && SlotRelease(_nextSlot) < end) liquid += _slotLiquid[_nextSlot++];
                }
                else if (_state == WeatherRows.Clearing && _flurryTotal > 0 && _stateUnits > 0)
                {
                    _flurryAcc += (long)_flurryTotal * take;
                    // The cap is per face hour: what a storm's last slot already dropped here defers the flurries to the next hour.
                    long fall = Math.Min(_flurryAcc / _stateUnits, Math.Max(0, _rateCap - liquid));
                    _flurryAcc -= fall * _stateUnits;
                    liquid += (int)fall;
                }
                _elapsed += take;
                _unitsLeft -= take;
                remaining -= take;
            }
            Fill(ref h, liquid, stormInHour, changed);
            _hour++;
        }

        // ---- The calendar (WX-20) ----

        /// <summary>The thirds either side of the face hour's middle on the synoptic clock, and the weight of the later one.</summary>
        void Calendar(int hour)
        {
            long units = hour * _unitsPerFaceHour + _unitsPerFaceHour / 2;
            int tenths = (int)Math.Min(units / _unitsPerRealTenth, 365L * 240 - 1);
            int a = WeatherRows.Thirds - 1;
            for (int i = 0; i < WeatherRows.Thirds; i++)
            {
                if (_thirdMiddle[i] > tenths) break;
                a = i;
            }
            if (tenths < _thirdMiddle[0])
            {
                // Before October early's middle: between the previous September late (wrapped) and October early.
                int prev = _thirdMiddle[WeatherRows.Thirds - 1] - 365 * 240;
                _tA = WeatherRows.Thirds - 1; _tB = 0;
                _wB = (int)((long)(tenths - prev) * 1000 / (_thirdMiddle[0] - prev));
                return;
            }
            if (a == WeatherRows.Thirds - 1)
            {
                int next = _thirdMiddle[0] + 365 * 240;
                _tA = a; _tB = 0;
                _wB = (int)((long)(tenths - _thirdMiddle[a]) * 1000 / (next - _thirdMiddle[a]));
                return;
            }
            _tA = a; _tB = a + 1;
            _wB = (int)((long)(tenths - _thirdMiddle[a]) * 1000 / (_thirdMiddle[a + 1] - _thirdMiddle[a]));
        }

        int Nearer() => _wB < 500 ? _tA : _tB;

        int Blend(int[] a, int stride, int index)
        {
            int x = a[_tA * stride + index], y = a[_tB * stride + index];
            return x + (int)((long)(y - x) * _wB / 1000);
        }

        int LabelMonth(int hour)
        {
            long units = hour * _unitsPerFaceHour + _unitsPerFaceHour / 2;
            int day = (int)Math.Min(units / _unitsPerRealTenth / 240, 364);
            int m = 0;
            while (m < 11 && MonthStartDay[m + 1] <= day) m++;
            return m;
        }

        /// <summary>
        /// A draw from the two thirds' quantile tables (real tenths, or 0.01 in for storm totals), blended linearly by the
        /// date (WX-20). The Fair gap blends harmonically, x·y ÷ (w·x + (1 − w)·y): what blends linearly between thirds is the
        /// storm rate, 1 ÷ gap, so a dry third next to a stormy one doesn't stretch the stormy one's gaps.
        /// </summary>
        int Draw(int distribution, int u10k)
        {
            int off = WeatherRows.QuantileCount;
            int x = WeatherMath.FromQuantiles(_rows.Quantiles, (_tA * WeatherRows.Distributions + distribution) * off, u10k);
            int y = WeatherMath.FromQuantiles(_rows.Quantiles, (_tB * WeatherRows.Distributions + distribution) * off, u10k);
            if (distribution == 0 && x > 0 && y > 0)
                return (int)((long)x * y * 1000 / ((long)_wB * x + (long)(1000 - _wB) * y));
            return x + (int)((long)(y - x) * _wB / 1000);
        }

        // ---- States (WX-23 v2) ----

        /// <param name="hourLeft">The units left in the face hour being generated.</param>
        void Transition(long hourLeft)
        {
            // States change inside the face hour at their exact unit (Step's loop), so the remainder WX-23 v2 carries is
            // already exact: nothing is rounded to a face hour.
            _changes++;
            int from = _state;
            switch (from)
            {
                case WeatherRows.Fair:
                    _state = WeatherRows.Building;
                    _stateUnits = BuildingUnits();
                    break;
                case WeatherRows.Building:
                    _state = WeatherRows.Storm;
                    StartStorm();
                    _stateUnits = _stormUnits;
                    break;
                case WeatherRows.Storm:
                    _lastStormTotal = _stormTotal;
                    _state = WeatherRows.Clearing;
                    // At least to the end of this face hour, so the next storm can't start in it: a face hour belongs to one
                    // storm at most (its liquid, its ordinal and its rate cap; found at Stevens Pass, back to back and short).
                    _stateUnits = Math.Max(ClearingUnits(), hourLeft + 1);
                    int share = WeatherMath.Clamp(Blend(_rows.ClearingLiquidShare, 1, 0), 0, 900);
                    _flurryTotal = (int)((long)_stormTotal * share / (1000 - share));
                    _flurryAcc = 0;
                    break;
                default:
                    if (WeatherMath.Permille(WeatherMath.Key(_seed, WeatherKey.State, _changes, 9)) < Blend(_rows.BackToBack, 1, 0))
                    {
                        _state = WeatherRows.Building;
                        _stateUnits = BuildingUnits();
                    }
                    else
                    {
                        _state = WeatherRows.Fair;
                        StepPattern();
                        _stateUnits = FairUnits();
                    }
                    _flurryTotal = 0;
                    break;
            }
            _unitsLeft = _stateUnits;
            _elapsed = 0;
        }

        long FairUnits()
        {
            int u = WeatherMath.Per10k(WeatherMath.Key(_seed, WeatherKey.State, _changes, WeatherRows.Fair));
            long tenths = Draw(0, u);
            tenths = tenths * _rows.PatternGap[_pattern] / 1000;
            tenths = tenths * 1000 / Math.Max(1, _sqrtShare);   // WX-34: Fair spells × 1/√share
            return Math.Max(1, tenths) * _unitsPerRealTenth;
        }

        long BuildingUnits()
        {
            if (WeatherMath.Permille(WeatherMath.Key(_seed, WeatherKey.State, _changes, 8)) < Blend(_rows.BuildingZeroShare, 1, 0)) return 0;
            int u = WeatherMath.Per10k(WeatherMath.Key(_seed, WeatherKey.State, _changes, WeatherRows.Building));
            return Math.Max(1, Draw(1, u)) * _unitsPerRealTenth;
        }

        long ClearingUnits()
        {
            int u = WeatherMath.Per10k(WeatherMath.Key(_seed, WeatherKey.State, _changes, WeatherRows.Clearing));
            return Math.Max(1, Draw(3, u)) * _unitsPerRealTenth;
        }

        /// <summary>WX-57: once per cycle, when a Fair spell begins, a keyed draw against the third's matrix; the dwell cap forces a change.</summary>
        void StepPattern()
        {
            _cycle++;
            int third = Nearer();
            int row = (third * WeatherRows.Patterns + _pattern) * WeatherRows.Patterns;
            ulong key = WeatherMath.Key(_seed, WeatherKey.Pattern, _cycle);
            int next;
            bool mustLeave = _rows.DwellCap > 0 && _dwell + 1 >= _rows.DwellCap;
            if (!mustLeave) next = WeatherMath.FromHistogram(_rows.PatternMatrix, row, WeatherRows.Patterns, key);
            else
            {
                // Over the other two, renormalised (an even split if both are zero).
                int a = (_pattern + 1) % 3, b = (_pattern + 2) % 3;
                int wa = Math.Max(0, _rows.PatternMatrix[row + a]), wb = Math.Max(0, _rows.PatternMatrix[row + b]);
                if (wa + wb <= 0) { wa = 1; wb = 1; }
                int pick = (int)((key >> 32) * (ulong)(wa + wb) >> 32);
                next = pick < wa ? a : b;
            }
            if (next == _pattern) _dwell++;
            else { _pattern = next; _dwell = 0; }
        }

        // ---- A storm (WX-24 v2) ----

        void StartStorm()
        {
            int ordinal = _storms++;
            _storm = ordinal;
            int third = Nearer();
            ulong K(int field) => WeatherMath.Key(_seed, WeatherKey.Storm, ordinal, field);

            // Arrival octant from the pattern's histogram where it has one, else the third's; a direction within it.
            int[] hist = _rows.ArrivalOctants;
            int offset = third * 8;
            int patternTotal = 0;
            for (int i = 0; i < 8; i++) patternTotal += _rows.PatternOctants[_pattern * 8 + i];
            if (patternTotal > 0) { hist = _rows.PatternOctants; offset = _pattern * 8; }
            _stormOctant = WeatherMath.FromHistogram(hist, offset, 8, K(0));
            _stormDir = Wrap(_stormOctant * 45 + WeatherMath.Between(K(1), -22, 22));
            int quarter = Wrap(_stormDir + 45) / 90;
            _clearDir = Wrap(WeatherMath.FromHistogram(_rows.ClearingOctants, third * 8, 8, K(2)) * 45 + WeatherMath.Between(K(3), -22, 22));

            // Duration and total through the Gaussian copula (the site's rank correlation).
            int z1 = WeatherMath.Normal1000(K(4)), z0 = WeatherMath.Normal1000(K(5));
            int z2 = (int)(((long)_rows.DurationTotalRho * z1 + (long)_rows.DurationTotalRhoComplement * z0) / 1000);
            long tenths = Math.Max(1, Draw(2, WeatherMath.NormalCdf10k(z1)));
            long total = Math.Max(5, Draw(WeatherRows.StormTotal, WeatherMath.NormalCdf10k(z2)));
            long factor = 1000L * _rows.QuarterSize[quarter] / 1000 * _rows.PatternSize[_pattern] / 1000 * _sqrtShare / 1000 * _t.WinterLiquid / 1000;
            factor = WeatherMath.Clamp(factor, 400, 2000);   // WX-58: stacked factors never make a storm the record couldn't
            _stormTotal = (int)Math.Max(5, WeatherMath.DivRound(total * factor, 1000));

            _stormUnits = Math.Max(_unitsPerRealTenth, tenths * _unitsPerRealTenth);
            // Whole face hours, rounded down, so slots are at least a face hour apart and no face hour holds two (the cap is per hour).
            _stormSlots = (int)WeatherMath.Clamp(_stormUnits / _unitsPerFaceHour, 1, MaxSlots);
            int built = BuildSlots(_stormSlots);
            if (built != _stormSlots)
            {
                // Every hour capped: lengthened by whole face hours, and to at least a face hour a slot (a storm shorter than
                // a face hour has one slot; split in two, it must still never put both in one face hour).
                _stormUnits = Math.Max(_stormUnits + (built - _stormSlots) * _unitsPerFaceHour, built * _unitsPerFaceHour);
                _stormSlots = built;
            }
            _front = _stormSlots / 4 + (_stormSlots >= 2 ? WeatherMath.Between(K(6), 0, Math.Max(0, _stormSlots / 2 - 1)) : 0);
            _front = Math.Min(_front, _stormSlots - 1);

            // Temperatures: the quarter's warm-sector and post-frontal values around the third's mean, one spread draw for the
            // storm, and the size term on the total's own normal score (WX-25 v3: bigger storms run warmer).
            int mean = Blend(_rows.StormOffsetMean, 1, 0), spread = Blend(_rows.StormOffsetSpread, 1, 0);
            // Big storms vary less, small ones more: one scale on the storm's draw and on the anomaly it keeps.
            _stormScale = (int)WeatherMath.Clamp(1000 + WeatherMath.DivRound((long)_rows.StormSizeSpread * z2, 1000), 250, 2000);
            _stormOffset = mean + (int)WeatherMath.DivRound((long)_rows.StormSizeWarm * z2, 1000)
                         + (int)WeatherMath.DivRound((long)WeatherMath.Normal1000(K(7)) * spread * _stormScale, 1_000_000);
            _stormWarm = _rows.QuarterWarm[quarter];
            _stormPost = _rows.QuarterPost[quarter];

            int windMean = Blend(_rows.WindMean, WeatherRows.States, WeatherRows.Storm);
            int windSpread = Blend(_rows.WindSpread, WeatherRows.States, WeatherRows.Storm);
            _stormWind = Math.Max(10, windMean + (int)WeatherMath.DivRound((long)WeatherMath.Normal1000(K(8)) * windSpread, 1000));
            _nextSlot = 0;
        }

        /// <summary>
        /// WX-24 v2's shape: ramp, peak and tail 1 : 2 : 1 times a keyed burst, renormalised to the drawn total by largest
        /// remainder, then the rate cap (excess to the lightest hour still under it). Returns the slot count that fits: more
        /// than asked when every hour is capped. A pure function of (seed, storm, slots, total), so a load rebuilds it.
        /// </summary>
        int BuildSlots(int n)
        {
            while (true)
            {
                int sum = 0;
                for (int k = 0; k < n; k++)
                {
                    int quarter = n / 4;
                    int shape = n <= 3 ? 2 : k < quarter || k >= n - quarter ? 1 : 2;
                    int burst = WeatherMath.Between(WeatherMath.Key(_seed, WeatherKey.Burst, _storm, k), _t.BurstMin, _t.BurstMax);
                    _slotWeight[k] = shape * burst;
                    sum += _slotWeight[k];
                }
                _weightSum = Math.Max(1, sum);
                int given = 0;
                for (int k = 0; k < n; k++)
                {
                    _slotLiquid[k] = (int)((long)_stormTotal * _slotWeight[k] / _weightSum);
                    given += _slotLiquid[k];
                }
                // Largest remainder, ties to the earlier hour.
                for (int left = _stormTotal - given; left > 0; left--)
                {
                    int best = -1;
                    long bestRem = -1;
                    for (int k = 0; k < n; k++)
                    {
                        long rem = (long)_stormTotal * _slotWeight[k] - (long)_slotLiquid[k] * _weightSum;
                        if (rem > bestRem) { bestRem = rem; best = k; }
                    }
                    _slotLiquid[best]++;
                }
                if (Cap(n) || n >= MaxSlots) return n;
                n++;
            }
        }

        bool Cap(int n)
        {
            for (int k = 0; k < n; k++)
            {
                int excess = _slotLiquid[k] - _rateCap;
                while (excess > 0)
                {
                    int light = -1;
                    for (int j = 0; j < n; j++)
                        if (_slotLiquid[j] < _rateCap && (light < 0 || _slotLiquid[j] < _slotLiquid[light])) light = j;
                    if (light < 0) return false;
                    int move = Math.Min(excess, _rateCap - _slotLiquid[light]);
                    _slotLiquid[light] += move;
                    _slotLiquid[k] -= move;
                    excess -= move;
                }
            }
            return true;
        }

        /// <summary>Slot k's liquid falls at the middle of its share of the storm's units.</summary>
        long SlotRelease(int k) => (2L * k + 1) * _stormUnits / (2L * _stormSlots);

        int CurrentSlot() => _stormSlots <= 0 ? 0 : (int)WeatherMath.Clamp(_elapsed * _stormSlots / Math.Max(1, _stormUnits), 0, _stormSlots - 1);

        // ---- The hour (WX-25 v2 – WX-30) ----

        void Fill(ref WeatherHour h, int liquid, bool stormInHour, bool changed)
        {
            var r = _rows;
            int hourOfDay = _hour % 24;
            int month = LabelMonth(_hour);
            int minute = hourOfDay * 60 + 30;
            bool night = minute < r.SunriseMinute[month] || minute >= r.SunsetMinute[month];
            int third = Nearer();
            ulong hk(ulong tag, int sub) => WeatherMath.Key(_seed, tag, _hour, sub);

            // The storm weight moves 1,000 over 2 face hours after a storm starts, and back after it ends.
            _stormWeight = WeatherMath.Clamp(_stormWeight + (_state == WeatherRows.Storm || stormInHour ? 500 : -500), 0, 1000);

            // Climate on the diurnal shape.
            int max = Blend(r.MaxMean, 1, 0), min = Blend(r.MinMean, 1, 0);
            int fair = Blend(r.DiurnalFair, 24, hourOfDay), stormShape = Blend(r.DiurnalStorm, 24, hourOfDay);
            int shape = fair + (int)((long)(stormShape - fair) * _stormWeight / 1000);
            int climate = min + (int)WeatherMath.DivRound((long)(max - min) * shape, 1000);

            // The anomaly: φ × anomaly + innovation × a keyed normal, clamped at ±3 × its stationary spread (WX-58).
            int spread = Blend(r.AnomalySpread, 1, 0);
            long innovation = (long)spread * 10 * _innovationQ16[third] >> 16;
            long next = ((long)_anomaly * _phiFace[third] + (1L << 31) >> 32) + WeatherMath.DivRound(innovation * WeatherMath.Normal1000(hk(WeatherKey.Anomaly, 0)), 1000);
            _anomaly = (int)WeatherMath.Clamp(next, -30L * spread, 30L * spread);
            _shaped = Shape(_anomaly, spread);

            // The state's offset, eased (2 face hours for a change of state, 1 for the front).
            int slot = CurrentSlot();
            int target;
            switch (_state)
            {
                case WeatherRows.Building: target = Blend(r.BuildingOffset, 1, 0); break;
                case WeatherRows.Storm:
                    target = _stormOffset + (slot < _front ? _stormWarm : slot == _front ? (_stormWarm + _stormPost) / 2 : _stormPost);
                    // WX-25 v3: a heavier hour runs warmer, on its liquid over the storm's mean hour (zero on average over the storm).
                    if (liquid > 0 && _stormTotal > 0)
                    {
                        long above = WeatherMath.Clamp((long)liquid * _stormSlots * 1000 / _stormTotal - 1000, -1000, 1500);
                        target += (int)WeatherMath.DivRound(r.StormIntensityWarm * above, 1000);
                    }
                    break;
                case WeatherRows.Clearing:
                    int extra = (int)Math.Min(r.PostStormColdCap, Math.Max(0L, (long)r.PostStormColdK * (_lastStormTotal - Blend(r.StormMedian, 1, 0)) / 1000));
                    target = Blend(r.ClearingOffset, 1, 0) - extra;
                    break;
                default: target = 0; break;
            }
            if (changed) _easeRate = Math.Max(5, Math.Abs(target - _eased) / 2);
            else if (_state == WeatherRows.Storm && target != _lastTarget) _easeRate = Math.Max(_easeRate, Math.Abs(target - _eased));
            _lastTarget = target;
            int step = WeatherMath.Clamp(target - _eased, -Math.Max(1, _easeRate), Math.Max(1, _easeRate));
            _eased += step;

            int season = _seasonTemp + _t.WinterTemp;
            int lean = r.PatternLean[_pattern];
            // WX-25 v5: a wet spell runs as the record's did: the running liquid about its month and state's mean, in its
            // spreads (±3), so each state keeps its own temperature.
            _wet = (int)(((long)_wet * r.WetMemory + (long)liquid * 1000 * (1000 - r.WetMemory) + 500) / 1000);
            int wetSpread = Blend(r.WetSpread, WeatherRows.States, _state), wetLean = 0;
            if (r.WetLean != 0 && wetSpread > 0)
            {
                long z = WeatherMath.Clamp(((long)_wet - Blend(r.WetMean, WeatherRows.States, _state)) * 1000 / wetSpread, -3000, 3000);
                wetLean = -(int)WeatherMath.DivRound(z * r.WetLean, 1000);
            }
            // WX-25 v3: a storm keeps its third's share of the anomaly, scaled by its size, eased in and out with its weight.
            long kept = 1000 - (long)_stormWeight * (1000 - (long)Blend(r.StormAnomalyWeight, 1, 0) * _stormScale / 1000) / 1000;
            // WX-25 v4: the night's depth, drawn as the day peaks, taken in full at the fair shape's bottom and none at its top,
            // and the hour's wobble; both fade with the storm weight (under a storm's cloud the night doesn't radiate).
            if (hourOfDay == _fairTopHour[third]) _nightDepth = (int)WeatherMath.DivRound((long)WeatherMath.Normal1000(hk(WeatherKey.Anomaly, 1)) * r.NightDepthSpread, 1000);
            // WX-25 v4: the hour's own wobble, with the site's memory per face hour.
            _fast = (int)(((long)_fast * _fastMemory + (long)WeatherMath.Normal1000(hk(WeatherKey.Anomaly, 2)) * _fastFresh) / 1000);
            int top = Blend(_fairTop, 1, 0);
            long below = top <= 0 ? 0 : WeatherMath.Clamp(1000L * (top - fair) / top, 0, 1000);
            long calm = 1000 - _stormWeight;
            int temp = climate + (int)WeatherMath.DivRound(_shaped * kept, 10_000) + _eased + lean + season + wetLean
                     + (int)WeatherMath.DivRound(((long)_fast * r.FastSpread - _nightDepth * below) * calm, 1_000_000);

            int depression = Math.Max(0, Blend(r.DewDepression, WeatherRows.States, _state));
            int dew = temp - depression;

            // The sky: cloud by state, day or night, with a keyed wobble; clearness by day with the same wobble.
            int wobble = WeatherMath.Between(hk(WeatherKey.Sky, 0), -100, 100);
            int cloud = WeatherMath.Clamp((night ? Blend(r.CloudNight, WeatherRows.States, _state) : Blend(r.CloudDay, WeatherRows.States, _state)) + wobble, 0, 1000);
            int clearness = night ? 0 : WeatherMath.Clamp(Blend(r.Clearness, WeatherRows.States, _state) - wobble, 0, 1000);

            // The free wind (WX-28 v3): its memory per face hour is the site's.
            _windDev = (int)(((long)_windDev * _windMemory + (long)WeatherMath.Normal1000(hk(WeatherKey.Wind, 0)) * _windFresh) / 1000);
            int wind, direction, gust;
            int jitter = WeatherMath.Between(hk(WeatherKey.Wind, 1), 900, 1100);
            if (_state == WeatherRows.Storm)
            {
                int mean = _weightSum / Math.Max(1, _stormSlots);
                int rate = (int)WeatherMath.Clamp(1000 + (long)r.StormWindPerRate * ((long)_slotWeight[slot] * 1000 / Math.Max(1, mean) - 1000) / 1000, 600, 1600);
                wind = (int)((long)_stormWind * rate / 1000 * WeatherMath.Between(hk(WeatherKey.Wind, 2), 950, 1050) / 1000);
                gust = _t.GustFactorStorm + Math.Max(0, rate - 1000) / 2;
                if (slot <= _front || _stormSlots - 1 <= _front) direction = _stormDir;
                else direction = Turn(_stormDir, _clearDir, slot - _front, _stormSlots - 1 - _front);
            }
            else
            {
                int mean = Blend(r.WindMean, WeatherRows.States, _state), sd = Blend(r.WindSpread, WeatherRows.States, _state);
                wind = Math.Max(0, mean + (int)((long)sd * _windDev / 1000));
                gust = _state == WeatherRows.Fair ? _t.GustFactorFair : (_t.GustFactorFair + _t.GustFactorStorm) / 2;
                if (_state == WeatherRows.Clearing) direction = Wrap(_clearDir + WeatherMath.Between(hk(WeatherKey.Wind, 3), -20, 20));
                else direction = Wrap(WeatherMath.FromHistogram(r.FairOctants, third * 8, 8, hk(WeatherKey.Wind, 4)) * 45 + WeatherMath.Between(hk(WeatherKey.Wind, 5), -22, 22));
            }
            wind = (int)((long)wind * _t.WindScale / 1000);
            gust = (int)((long)gust * jitter / 1000);

            // The cold pool (WX-29).
            if ((long)wind * ShelteredFactor / 1000 > PoolScourWind) _pool = 0;
            else if (night)
            {
                if (cloud < PoolClearCloud && wind < PoolCalmWind) _pool = Math.Min(_t.PoolMax, _pool + _t.PoolBuildPerHour);
                else if (cloud >= PoolClearCloud) _pool = _pool * 891 / 1000;
                _poolAtSunrise = _pool;
            }
            else if (cloud < PoolClearCloud) _pool = Math.Max(0, _pool - Math.Max(1, (_poolAtSunrise + 1) / 2));
            else _pool = _pool * 891 / 1000;   // half each 6 face hours under cloud

            h.LabelYear = _labelYear;
            h.HourOfYear = _hour;
            h.HourOfDay = hourOfDay;
            h.Third = third;
            h.NeighbourThird = third == _tA ? _tB : _tA;
            h.NeighbourWeight = Math.Min(_wB, 1000 - _wB);
            h.State = _state;
            h.Pattern = _pattern;
            h.Night = night;
            h.Temp = temp;
            h.Dew = dew;
            h.Lapse = night && _state != WeatherRows.Storm ? Blend(r.LapseNight, 1, 0) : Blend(r.LapseDay, 1, 0);
            h.DewLapse = Blend(r.DewLapse, 1, 0);
            h.PrecipGradient = Blend(r.PrecipGradient, 1, 0);
            h.Pool = _pool;
            h.Fog = _pool >= FogPool && depression < FogDepression;
            h.Liquid = liquid;
            bool inStorm = stormInHour;   // the storm fell in this hour, even if it ended inside it
            h.StormOrdinal = inStorm ? _storm : -1;
            h.StormOctant = inStorm ? _stormOctant : -1;
            h.StormDirection = inStorm ? _stormDir : -1;
            h.StormTotal = inStorm ? _stormTotal : 0;
            h.FrontPassed = inStorm && slot > _front;
            h.Wind = wind;
            h.Direction = direction;
            h.GustFactor = gust;
            h.Cloud = cloud;
            h.Clearness = clearness;
            h.CloudBaseFeet = depression * CloudBaseFeetPerTenthF / 10;
            h.SeasonOffset = season;
            h.WetLean = wetLean;
            h.SeasonShare = _share;
        }

        static int Wrap(int degrees) => ((degrees % 360) + 360) % 360;

        /// <summary>From a toward b the short way round, by step of steps.</summary>
        static int Turn(int a, int b, int step, int steps)
        {
            int d = Wrap(b - a);
            if (d > 180) d -= 360;
            return Wrap(a + (int)((long)d * step / Math.Max(1, steps)));
        }

        /// <summary>
        /// WX-25 v3: the anomaly (0.01 °F) read through the rows' shape. Its normal score (‰ of the spread, clamped at ±3) indexes
        /// the table, linear between points; the result is in the same units. The table's identity is a normal.
        /// </summary>
        int Shape(int anomaly, int spread)
        {
            if (spread <= 0) return 0;
            const int half = WeatherRows.AnomalyShapePoints / 2 * WeatherRows.AnomalyShapeStep;
            long z = WeatherMath.Clamp(WeatherMath.DivRound((long)anomaly * 100, spread), -half, half) + half;
            int i = (int)Math.Min(z / WeatherRows.AnomalyShapeStep, WeatherRows.AnomalyShapePoints - 2);
            long f = z - (long)i * WeatherRows.AnomalyShapeStep;
            var q = _rows.AnomalyShape;
            long s = q[i] + WeatherMath.DivRound((long)(q[i + 1] - q[i]) * f, WeatherRows.AnomalyShapeStep);
            return (int)WeatherMath.DivRound(s * spread, 100);
        }

        // ---- The save (WX-32) ----

        /// <summary>Walks the generator's state in a fixed order. After a read, call <see cref="Restore"/>.</summary>
        public void VisitState(IWeatherStateVisitor v)
        {
            long seed = unchecked((long)_seed);
            v.Long(ref seed); _seed = unchecked((ulong)seed);
            v.Int(ref _labelYear); v.Int(ref _hour); v.Int(ref _state); v.Int(ref _changes); v.Int(ref _cycle); v.Int(ref _storms);
            v.Int(ref _pattern); v.Int(ref _dwell);
            v.Long(ref _unitsLeft); v.Long(ref _stateUnits); v.Long(ref _elapsed);
            v.Int(ref _anomaly); v.Int(ref _pool); v.Int(ref _poolAtSunrise); v.Int(ref _stormWeight);
            v.Int(ref _eased); v.Int(ref _easeRate); v.Int(ref _lastTarget);
            v.Int(ref _share); v.Int(ref _sqrtShare); v.Int(ref _seasonTemp); v.Int(ref _windDev); v.Int(ref _nightDepth); v.Int(ref _fast);
            v.Int(ref _storm); v.Int(ref _stormOctant); v.Int(ref _stormDir); v.Int(ref _clearDir); v.Int(ref _stormTotal);
            v.Int(ref _stormSlots); v.Int(ref _front); v.Int(ref _stormWind); v.Int(ref _stormOffset); v.Int(ref _stormScale); v.Int(ref _stormWarm); v.Int(ref _stormPost);
            v.Int(ref _nextSlot); v.Long(ref _stormUnits);
            v.Int(ref _lastStormTotal); v.Int(ref _flurryTotal); v.Long(ref _flurryAcc); v.Int(ref _wet);
        }

        /// <summary>Rebuilds what the state implies but doesn't hold: the current storm's hours, from its keys.</summary>
        public void Restore()
        {
            if (_storm >= 0 && _stormSlots > 0)
            {
                int n = _stormSlots;
                _stormSlots = BuildSlots(n);
            }
        }

        /// <summary>Copies another generator's state (the forecast's run-ahead, WX-05 v3), allocating nothing.</summary>
        public void CopyStateFrom(WeatherGenerator o)
        {
            if (!ReferenceEquals(o._rows, _rows)) throw new ArgumentException("The run-ahead must share the rows.");
            _seed = o._seed; _labelYear = o._labelYear; _hour = o._hour; _state = o._state; _changes = o._changes; _cycle = o._cycle; _storms = o._storms;
            _pattern = o._pattern; _dwell = o._dwell; _unitsLeft = o._unitsLeft; _stateUnits = o._stateUnits; _elapsed = o._elapsed;
            _anomaly = o._anomaly; _pool = o._pool; _poolAtSunrise = o._poolAtSunrise; _stormWeight = o._stormWeight;
            _eased = o._eased; _easeRate = o._easeRate; _lastTarget = o._lastTarget;
            _share = o._share; _sqrtShare = o._sqrtShare; _seasonTemp = o._seasonTemp; _windDev = o._windDev; _nightDepth = o._nightDepth; _fast = o._fast;
            _storm = o._storm; _stormOctant = o._stormOctant; _stormDir = o._stormDir; _clearDir = o._clearDir; _stormTotal = o._stormTotal;
            _stormSlots = o._stormSlots; _front = o._front; _stormWind = o._stormWind; _stormOffset = o._stormOffset; _stormScale = o._stormScale; _stormWarm = o._stormWarm; _stormPost = o._stormPost;
            _nextSlot = o._nextSlot; _stormUnits = o._stormUnits; _lastStormTotal = o._lastStormTotal; _flurryTotal = o._flurryTotal; _flurryAcc = o._flurryAcc;
            _weightSum = o._weightSum; _wet = o._wet;
            Array.Copy(o._slotLiquid, _slotLiquid, MaxSlots);
            Array.Copy(o._slotWeight, _slotWeight, MaxSlots);
        }
    }
}
