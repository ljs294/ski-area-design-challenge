using System;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.05 PR C, WX-05 v3: the forecast. Issued once a face day at the resort's stop (the evening sheet) and refreshed at
    /// the landing; each issue runs a copy of the generator ahead seven face days from where it stands (the truth), then shows
    /// a softened, slightly wobbled version of it: shown = lean × truth + (1 − lean) × the date's climate + a keyed wobble.
    /// Every wobble is keyed by the target face day (or the storm), so a day's forecast tells the same story more sharply each
    /// issue and never a new one. The physics never reads the forecast (WX-06); the crowd, the alerts and the player's
    /// automation do (WX-42, WX-43, WX-44).
    /// - **Not saved** (WX-32): the world keeps the issue's hour; a load (or new static inputs, or a seed changed mid-year)
    ///   marks the sheet stale and the next reader makes it again exactly, by <see cref="WeatherGenerator.RunTo"/> to the
    ///   issue, so a reloaded game's sheet is the one that saved.
    /// - **Integer throughout** (WX-31, WX-37 v2): the run-ahead is the generator's, the samples the reference points' integer
    ///   air, so the sheet is the same on every runtime.
    /// </summary>
    public sealed partial class SimWorld
    {
        /// <summary>The run-ahead's longest reach: the rest of the issue's day and seven face days.</summary>
        const int ForecastTruthHours = 24 + ForecastSheet.Days * 24;

        ForecastSheet _forecast;
        WeatherGenerator _fcGen;
        WeatherHour[] _fcTruth;
        WeatherSample[] _fcBase, _fcMid, _fcSummit;
        int _fcTruthCount, _fcFirstCard;
        long _fcTruthFirst;
        /// <summary>The issue (the world's face hour, −1 none) and whether it was the evening's: state, saved (sidecar v6).</summary>
        long _fcIssueHour = -1;
        bool _fcEvening;
        /// <summary>The sheet needs making again from the issue (a load, new inputs, a new seed): not state.</summary>
        bool _fcStale;
        /// <summary>Bumped each time the sheet is made, so the snapshot copies it only then.</summary>
        int _fcVersion;
        /// <summary>Tests: a generator standing at the issue to copy instead of the world's.</summary>
        WeatherGenerator _fcSource;

        // Per-card truth, worked out at each issue (scratch, not state).
        readonly int[] _fcDayHighB = new int[ForecastSheet.Days], _fcDayLowB = new int[ForecastSheet.Days];
        readonly int[] _fcDayHighS = new int[ForecastSheet.Days], _fcDayLowS = new int[ForecastSheet.Days];
        readonly int[] _fcDayHighZ = new int[ForecastSheet.Days], _fcDayLiquid = new int[ForecastSheet.Days];
        readonly int[] _fcDaySnowB = new int[ForecastSheet.Days], _fcDaySnowS = new int[ForecastSheet.Days];
        readonly int[] _fcDayGustS = new int[ForecastSheet.Days], _fcDayWindS = new int[ForecastSheet.Days];
        readonly int[] _fcDaySnowShare = new int[ForecastSheet.Days], _fcDaySnowLine = new int[ForecastSheet.Days];
        readonly int[] _fcDayStorm = new int[ForecastSheet.Days], _fcDayHours = new int[ForecastSheet.Days];
        readonly int[] _fcDayScale = new int[ForecastSheet.Days], _fcDayShift = new int[ForecastSheet.Days];
        readonly bool[] _fcDayWet = new bool[ForecastSheet.Days], _fcDayFreezing = new bool[ForecastSheet.Days], _fcDayBluebird = new bool[ForecastSheet.Days];
        // The run-ahead's storms: key, main card, drawn total, octant, the octant it ends from, its liquid on the main card.
        const int ForecastStorms = 16;
        readonly int[] _fcStormKey = new int[ForecastStorms], _fcStormMain = new int[ForecastStorms], _fcStormTotal = new int[ForecastStorms];
        readonly int[] _fcStormOctant = new int[ForecastStorms], _fcStormTurn = new int[ForecastStorms], _fcStormShown = new int[ForecastStorms];
        readonly int[] _fcStormDayLiquid = new int[ForecastStorms * ForecastSheet.Days];
        int _fcStormCount;

        /// <summary>The sheet as the world last made it (worked out from the issue; read by tests and the snapshot).</summary>
        public ForecastSheet Forecast { get { ForecastBuffers(); EnsureForecast(); return _forecast; } }
        /// <summary>The issue's face hour (−1 none).</summary>
        public long ForecastIssueHour => _fcIssueHour;

        /// <summary>The sheet and the run-ahead's buffers, made once when the world gets weather (never in a tick: SI-23).</summary>
        void ForecastBuffers()
        {
            if (_forecast != null) return;
            _forecast = new ForecastSheet(Pacing.MaxLifts);
            _fcTruth = new WeatherHour[ForecastTruthHours];
            _fcBase = new WeatherSample[ForecastTruthHours];
            _fcMid = new WeatherSample[ForecastTruthHours];
            _fcSummit = new WeatherSample[ForecastTruthHours];
        }

        /// <summary>The stop's evening sheet, or the landing's refresh (RunHourStep, after the hour's weather).</summary>
        void IssueForecast(bool evening)
        {
            if (_weather == null || !_wxHourSet)
            {
                if (_forecast != null && (_fcIssueHour >= 0 || _forecast.Known)) { _forecast.Clear(); _fcVersion++; }
                _fcIssueHour = -1;
                return;
            }
            ForecastBuffers();
            _fcIssueHour = FaceMs / HourMs;
            _fcEvening = evening;
            BuildForecast(live: true);
            RaiseForecastAlerts();
            Events.Add(Tick, SimEventType.ForecastIssued, -1, _forecast.IssueFaceDay, (ushort)(evening ? 1 : 0));
        }

        /// <summary>Makes the sheet again if it went stale (WX-32): an exact function of the issue, the rows, the seed and the inputs.</summary>
        void EnsureForecast()
        {
            if (!_fcStale) return;
            _fcStale = false;
            if (_fcIssueHour < 0 || _weather == null) { if (_forecast != null && _forecast.Known) { _forecast.Clear(); _fcVersion++; } return; }
            ForecastBuffers();
            BuildForecast(live: false);
        }

        void MarkForecastStale() { if (_fcIssueHour >= 0) _fcStale = true; }

        // ---- tests: an issue from a generator the test steps itself (WX-54's 50 seeds without playing 50 worlds) ----

        /// <summary>Issues the sheet at <paramref name="issueHour"/> from <paramref name="source"/>, standing at the next hour.</summary>
        internal ForecastSheet IssueForTest(WeatherGenerator source, ulong seed, int year, long issueHour, bool evening)
        {
            ForecastBuffers();
            _genSeed = seed; _genYear = year;
            _fcIssueHour = issueHour; _fcEvening = evening; _fcStale = false;
            _fcSource = source;
            try { BuildForecast(live: true); }
            finally { _fcSource = null; }
            return _forecast;
        }

        internal int TruthHighBase(int k) => _fcDayHighB[k];
        internal int TruthLiquid(int k) => _fcDayLiquid[k];
        internal bool TruthWet(int k) => _fcDayWet[k];
        internal int TruthStormCount => _fcStormCount;
        /// <summary>A run-ahead storm's main card (−1 none) and its key.</summary>
        internal int TruthStormMain(int s) => _fcStormMain[s];
        internal int TruthStormKey(int s) => _fcStormKey[s];
        internal int ClimateHighBase(int faceDay)
        {
            var rows = _weather.Rows;
            int t = ThirdOfDay(faceDay);
            return rows.MaxMean[t] - (int)WeatherMath.DivRound((long)rows.LapseDay[t] * (_weather.BaseFeet - rows.ZrefFeet), 1000);
        }

        void BuildForecast(bool live)
        {
            var sheet = _forecast;
            sheet.Clear();
            _fcVersion++;
            long yearHours = (long)Calendar.YearFaceDays * 24;
            long issue = _fcIssueHour;
            int year = StartYear + (int)(issue / yearHours);
            int issueOfYear = (int)(issue % yearHours);
            // The truth: a copy of the generator where it stands, or (a load, a stale sheet) run to the issue from October 1.
            if (year != _genYear) return;   // the issue was last label year: nothing to show (the rows' October 1 reset)
            // The run-ahead plays the generator's own rows (after a load the world's SimWeather may be an equal copy).
            var source = _fcSource ?? _gen;
            var rows = source?.Rows ?? _weather.Rows;
            if (_fcGen == null || !ReferenceEquals(_fcGen.Rows, rows)) _fcGen = new WeatherGenerator(rows, _tuning);
            if (live && source != null && source.Hour == issueOfYear + 1) _fcGen.CopyStateFrom(source);
            else _fcGen.RunTo(_genSeed, year, issueOfYear + 1);

            int issueDay = (int)(issue / 24);
            int firstCard = _fcEvening ? issueDay + 1 : issueDay;
            long end = (long)(firstCard + ForecastSheet.Days) * 24;
            long yearEnd = (long)(year - StartYear + 1) * yearHours;
            if (end > yearEnd) end = yearEnd;
            _fcTruthFirst = issue + 1;
            _fcTruthCount = (int)Math.Max(0, Math.Min(ForecastTruthHours, end - _fcTruthFirst));
            for (int i = 0; i < _fcTruthCount; i++)
            {
                _fcGen.Step(ref _fcTruth[i]);
                _fcBase[i] = SampleAt(_fcTruth[i], _weather.BaseFeet);
                _fcMid[i] = SampleAt(_fcTruth[i], _weather.MidFeet);
                _fcSummit[i] = SampleAt(_fcTruth[i], _weather.SummitFeet);
            }

            sheet.Known = true;
            sheet.IssueHour = issue;
            sheet.IssueFaceDay = issueDay;
            sheet.Evening = _fcEvening;
            int days = (int)Math.Max(0, Math.Min(ForecastSheet.Days, (end + 23) / 24 - firstCard));
            sheet.DayCount = days;
            _fcFirstCard = firstCard;
            TruthByDay(firstCard, days);
            for (int k = 0; k < days; k++) ShowCard(sheet, k, firstCard + k, issue);
            FirmStorms(sheet, days);
            ShowHours(sheet, issue, issueDay);
            ShowOutlook(sheet, firstCard);
            NamedFields(sheet, issueDay);
        }

        // ================= the truth, card by card =================

        void TruthByDay(int firstCard, int days)
        {
            for (int k = 0; k < ForecastSheet.Days; k++)
            {
                _fcDayHighB[k] = _fcDayHighS[k] = _fcDayHighZ[k] = int.MinValue;
                _fcDayLowB[k] = _fcDayLowS[k] = int.MaxValue;
                _fcDayLiquid[k] = _fcDaySnowB[k] = _fcDaySnowS[k] = _fcDayGustS[k] = _fcDayWindS[k] = _fcDayHours[k] = 0;
                _fcDaySnowShare[k] = 0; _fcDaySnowLine[k] = -1; _fcDayStorm[k] = -1;
                _fcDayWet[k] = _fcDayFreezing[k] = _fcDayBluebird[k] = false;
                _fcDayScale[k] = 1000; _fcDayShift[k] = 0;
            }
            _fcStormCount = 0;
            Array.Clear(_fcStormDayLiquid, 0, _fcStormDayLiquid.Length);
            long lineSum = 0, snowSum = 0;
            int lastKey = -1, lastCard = -1;
            int zref = _weather.Rows.ZrefFeet;
            for (int i = 0; i < _fcTruthCount; i++)
            {
                long hour = _fcTruthFirst + i;
                int k = (int)(hour / 24) - firstCard;
                if (k < 0 || k >= days) continue;
                if (k != lastCard)
                {
                    if (lastCard >= 0) CloseTruthDay(lastCard, lineSum, snowSum);
                    lineSum = snowSum = 0;
                    lastCard = k;
                }
                ref var h = ref _fcTruth[i];
                _fcDayHours[k]++;
                _fcDayHighB[k] = Math.Max(_fcDayHighB[k], _fcBase[i].Temp); _fcDayLowB[k] = Math.Min(_fcDayLowB[k], _fcBase[i].Temp);
                _fcDayHighS[k] = Math.Max(_fcDayHighS[k], _fcSummit[i].Temp); _fcDayLowS[k] = Math.Min(_fcDayLowS[k], _fcSummit[i].Temp);
                _fcDayHighZ[k] = Math.Max(_fcDayHighZ[k], h.Temp);
                _fcDayLiquid[k] += h.Liquid;
                _fcDaySnowB[k] += _fcBase[i].SnowTenthsIn;
                _fcDaySnowS[k] += _fcSummit[i].SnowTenthsIn;
                _fcDayGustS[k] = Math.Max(_fcDayGustS[k], _fcSummit[i].Gust);
                _fcDayWindS[k] += _fcSummit[i].Wind;
                if (_fcBase[i].FreezingRain && _fcBase[i].Liquid > 0) _fcDayFreezing[k] = true;
                if (h.Liquid > 0)
                {
                    snowSum += (long)_fcSummit[i].SnowPermille * h.Liquid;
                    // The snow line: where the hour's air reaches the snow-to-rain midpoint, by its lapse (liquid-weighted).
                    int line = h.Lapse > 0 ? zref + (int)((long)(h.Temp - (WeatherAir.SnowWetBulb + WeatherAir.RainWetBulb) / 2) * 1000 / h.Lapse) : zref;
                    lineSum += (long)Math.Max(0, line) * h.Liquid;
                }
                if (h.StormOrdinal >= 0 && h.Liquid > 0)
                {
                    int key = h.LabelYear * 1000 + h.StormOrdinal;
                    int s = StormSlot(key, h);
                    if (s >= 0)
                    {
                        _fcStormDayLiquid[s * ForecastSheet.Days + k] += h.Liquid;
                        _fcStormTurn[s] = (((h.Direction % 360) + 360) % 360) / 45;
                    }
                    lastKey = key;
                }
            }
            if (lastCard >= 0) CloseTruthDay(lastCard, lineSum, snowSum);
            // Each storm's main card is the one it drops the most on (the first, at a tie).
            for (int s = 0; s < _fcStormCount; s++)
            {
                int best = -1, most = 0;
                for (int k = 0; k < days; k++)
                {
                    int l = _fcStormDayLiquid[s * ForecastSheet.Days + k];
                    if (l > most) { most = l; best = k; }
                }
                _fcStormMain[s] = best;
                if (best >= 0 && (_fcDayStorm[best] < 0 || _fcStormDayLiquid[_fcDayStorm[best] * ForecastSheet.Days + best] < most)) _fcDayStorm[best] = s;
            }
            // Bluebird: clear through the resort's hours within a face day of a storm's end (the truth's sky; WX-41).
            for (int k = 0; k < days; k++)
            {
                int day = firstCard + k;
                HoursFor(day, out int open, out _, out int stop, out _);
                long sum = 0; int n = 0; long lastWet = long.MinValue;
                for (int i = 0; i < _fcTruthCount; i++)
                {
                    long hour = _fcTruthFirst + i;
                    if (hour >= (long)day * 24 + stop / 3600) break;
                    if (_fcTruth[i].Liquid > 0) lastWet = hour;
                    if (hour >= (long)day * 24 + open / 3600 && hour / 24 == day) { sum += _fcTruth[i].Clearness; n++; }
                }
                _fcDayBluebird[k] = n > 0 && sum / n > Pacing.BluebirdClearness && lastWet != long.MinValue && (long)day * 24 + open / 3600 - lastWet <= 24
                                    && _fcDayLiquid[k] < Pacing.StormEventLiquid;
            }
        }

        int StormSlot(int key, in WeatherHour h)
        {
            for (int s = 0; s < _fcStormCount; s++) if (_fcStormKey[s] == key) return s;
            if (_fcStormCount == ForecastStorms) return -1;
            int n = _fcStormCount++;
            _fcStormKey[n] = key;
            _fcStormTotal[n] = h.StormTotal;
            _fcStormOctant[n] = h.StormOctant;
            _fcStormTurn[n] = h.StormOctant;
            _fcStormMain[n] = -1;
            _fcStormShown[n] = h.StormTotal;
            return n;
        }

        void CloseTruthDay(int k, long lineSum, long snowSum)
        {
            int liquid = _fcDayLiquid[k];
            _fcDayWet[k] = liquid >= Pacing.StormEventLiquid;
            if (_fcDayHours[k] > 0) _fcDayWindS[k] /= _fcDayHours[k];
            if (liquid > 0)
            {
                _fcDaySnowShare[k] = (int)(snowSum / liquid);
                _fcDaySnowLine[k] = (int)(lineSum / liquid);
            }
        }

        // ================= what the sheet shows =================

        /// <summary>WX-05 v3's lean on the truth (‰) at a lead in face hours: a day out to a week, linear, flat outside; × skill.</summary>
        int LeanAt(int leadHours, out int pull)
        {
            int w = Lerp(leadHours, 24, Pacing.ForecastLeanDay1, 168, Pacing.ForecastLeanDay7);
            pull = (int)Math.Min(1000, (long)(1000 - w) * Pacing.ForecastSkill / 1000);
            return 1000 - pull;
        }

        static int Lerp(int x, int x0, int y0, int x1, int y1)
        {
            if (x <= x0) return y0;
            if (x >= x1) return y1;
            return y0 + (int)((long)(y1 - y0) * (x - x0) / (x1 - x0));
        }

        /// <summary>The evening-before, 3-day and week points of a three-entry Pacing list, linear between.</summary>
        static int ByLead(int leadHours, int[] three)
        {
            if (leadHours <= 72) return Lerp(leadHours, 24, three[0], 72, three[1]);
            return Lerp(leadHours, 72, three[1], 168, three[2]);
        }

        int ThirdOfDay(int faceDay)
        {
            int j = faceDay % Calendar.YearFaceDays;
            return Calendar.MonthIndex(j) * 3 + (int)Calendar.Third(j) - 1;
        }

        int Shown(int truth, int clim, int lean, int pull, int wobble) =>
            (int)WeatherMath.DivRound((long)truth * lean + (long)clim * pull, 1000) + wobble;

        ulong ForecastKey(long a, long b) => WeatherMath.Key(_genSeed, WeatherKey.Forecast, a, b);

        void ShowCard(ForecastSheet sheet, int k, int day, long issue)
        {
            ref var c = ref sheet.Cards[k];
            c.FaceDay = day;
            int lead = (int)Math.Max(1, (long)day * 24 + 12 - issue);
            c.LeadHours = lead;
            int leadDay = day - sheet.IssueFaceDay;
            int lean = LeanAt(lead, out int pull);
            var rows = _weather.Rows;
            int t = ThirdOfDay(day);
            int zref = rows.ZrefFeet;
            if (_fcDayHours[k] == 0) { c.Words = ForecastWords.Unknown; c.SnowLineFeet = -1; c.StormOctant = c.TurnOctant = -1; return; }

            // Temperatures: one wobble a day (σ half the lead's half-range), the same for every height.
            int spread = (int)((long)Lerp(lead, 24, Pacing.ForecastTempTenths[0], 168, Pacing.ForecastTempTenths[1]) * Pacing.ForecastSkill / 1000);
            int wob = (int)WeatherMath.DivRound((long)WeatherMath.Normal1000(ForecastKey(day, 1)) * spread, 2000);
            int climHighZ = rows.MaxMean[t], climLowZ = rows.MinMean[t];
            int climHighB = climHighZ - (int)WeatherMath.DivRound((long)rows.LapseDay[t] * (_weather.BaseFeet - zref), 1000);
            int climHighS = climHighZ - (int)WeatherMath.DivRound((long)rows.LapseDay[t] * (_weather.SummitFeet - zref), 1000);
            int climLowB = climLowZ - (int)WeatherMath.DivRound((long)rows.LapseNight[t] * (_weather.BaseFeet - zref), 1000);
            int climLowS = climLowZ - (int)WeatherMath.DivRound((long)rows.LapseNight[t] * (_weather.SummitFeet - zref), 1000);
            c.HighBase = Shown(_fcDayHighB[k], climHighB, lean, pull, wob);
            c.LowBase = Shown(_fcDayLowB[k], climLowB, lean, pull, wob);
            c.HighSummit = Shown(_fcDayHighS[k], climHighS, lean, pull, wob);
            c.LowSummit = Shown(_fcDayLowS[k], climLowS, lean, pull, wob);
            c.HighBaseLo = c.HighBase - spread; c.HighBaseHi = c.HighBase + spread;
            c.LowBaseLo = c.LowBase - spread; c.LowBaseHi = c.LowBase + spread;
            c.HighSummitLo = c.HighSummit - spread; c.HighSummitHi = c.HighSummit + spread;
            c.LowSummitLo = c.LowSummit - spread; c.LowSummitHi = c.LowSummit + spread;
            int highZ = Shown(_fcDayHighZ[k], climHighZ, lean, pull, wob);
            int shift = c.HighBase - _fcDayHighB[k];   // the shown air's offset from the truth, for the phase reads
            _fcDayShift[k] = shift;

            // Wind: the lean toward the third's fair wind at the summit, no wobble.
            int climWind = (int)((long)rows.WindMean[t * WeatherRows.States + WeatherRows.Fair] * _weather.ExposureAt(Pacing, _weather.SummitFeet) / 1000);
            c.WindSummit = Shown(_fcDayWindS[k], climWind, lean, pull, 0);
            c.GustSummit = Shown(_fcDayGustS[k], (int)((long)climWind * Pacing.GustFactorFair / 1000), lean, pull, 0);

            // Precipitation: the storm's card (and, far out, its two-day window); the size leans to the third's median storm.
            int s = _fcDayStorm[k];
            bool wet = _fcDayWet[k];
            bool windowed = false;
            if (!wet)
            {
                // A far storm's two-day window holds its true day and one neighbour (drawn once per storm).
                for (int o = 0; o < _fcStormCount; o++)
                {
                    int main = _fcStormMain[o];
                    if (main < 0 || main == k || day - sheet.IssueFaceDay - (k - main) < 4) continue;
                    int side = WeatherMath.Permille(ForecastKey(-_fcStormKey[o], 2)) < 500 ? -1 : 1;
                    if (main + side == k) { windowed = true; s = o; break; }
                }
            }
            int p = ByLead(lead, Pacing.ForecastPrecipPermille);
            int liquid = _fcDayLiquid[k];
            if (wet || windowed)
            {
                int climLiquid = rows.StormMedian[t];
                int truthLiquid = wet ? liquid : (s >= 0 && _fcStormMain[s] >= 0 ? _fcDayLiquid[_fcStormMain[s]] : climLiquid);
                int centre = Shown(truthLiquid, climLiquid, lean, pull, 0);
                int storm = s >= 0 ? _fcStormKey[s] : day;
                if (leadDay <= 1 && s >= 0 && WeatherMath.Permille(ForecastKey(-storm, 4)) < Pacing.ForecastBustPermille)
                {
                    // A bust (WX-05 v3): the evening-before range misses the truth, high or low.
                    int miss = p + 150;
                    centre = WeatherMath.Permille(ForecastKey(-storm, 5)) < 500
                        ? (int)WeatherMath.DivRound((long)truthLiquid * 1000, 1000 + miss)
                        : (int)WeatherMath.DivRound((long)truthLiquid * 1000, Math.Max(100, 1000 - miss));
                }
                else
                {
                    int u = WeatherMath.Between(ForecastKey(day, 3), -p / 3, p / 3);
                    centre = (int)WeatherMath.DivRound((long)centre * (1000 + u), 1000);
                    // Only a bust misses (WX-05 v3): the pull toward the median storm never pushes a small day's range past it.
                    if (truthLiquid > 0)
                    {
                        int least = (int)((long)truthLiquid * 1000 / (1000 + p)) + 1, most = (int)((long)truthLiquid * 1000 / Math.Max(1, 1000 - p));
                        centre = WeatherMath.Clamp(centre, least, Math.Max(least, most));
                    }
                }
                centre = Math.Max(1, centre);
                int scale = truthLiquid > 0 ? (int)Math.Min(4000, (long)centre * 1000 / truthLiquid) : 1000;
                _fcDayScale[k] = scale;
                c.LiquidLo = (int)WeatherMath.DivRound((long)centre * (1000 - p), 1000);
                c.LiquidHi = (int)WeatherMath.DivRound((long)centre * (1000 + p), 1000);
                int snowS = wet ? _fcDaySnowS[k] : 0, snowB = wet ? _fcDaySnowB[k] : 0;
                if (!wet && s >= 0 && _fcStormMain[s] >= 0) { snowS = _fcDaySnowS[_fcStormMain[s]]; snowB = _fcDaySnowB[_fcStormMain[s]]; }
                long ss = (long)snowS * scale / 1000, sb = (long)snowB * scale / 1000;
                c.SnowSummitLo = (int)(ss * (1000 - p) / 1000); c.SnowSummitHi = (int)(ss * (1000 + p) / 1000);
                c.SnowBaseLo = (int)(sb * (1000 - p) / 1000); c.SnowBaseHi = (int)(sb * (1000 + p) / 1000);
                if (s >= 0) _fcStormShown[s] = (int)Math.Min(int.MaxValue, (long)_fcStormTotal[s] * scale / 1000);
                int share = wet ? _fcDaySnowShare[k] : (s >= 0 && _fcStormMain[s] >= 0 ? _fcDaySnowShare[_fcStormMain[s]] : 1000);
                c.SnowLineFeet = wet ? _fcDaySnowLine[k] : (s >= 0 && _fcStormMain[s] >= 0 ? _fcDaySnowLine[_fcStormMain[s]] : -1);
                c.Words = Words(share, leadDay);
                int chance = ByLead(lead, Pacing.ForecastChancePermille);
                chance += WeatherMath.Between(ForecastKey(day, 8), -30, 30);
                c.ChancePermille = WeatherMath.Clamp(chance, 0, 1000);
            }
            else
            {
                c.SnowLineFeet = -1;
                c.Words = ForecastWords.Fair;
                c.ChancePermille = (int)((long)pull * Pacing.ForecastDryChancePermille / 1000);
                // Phantom storms (WX-05 v3): rare, only 5–7 days out, never sized.
                if (leadDay >= 5 && WeatherMath.Permille(ForecastKey(day, 9)) < PhantomPermille(t))
                {
                    c.Words = ForecastWords.Unsettled;
                    c.ChancePermille = Math.Max(c.ChancePermille, 300 + WeatherMath.Between(ForecastKey(day, 10), 0, 150));
                }
            }
            c.StormOctant = c.TurnOctant = -1;

            // The named events the card expects, from the shown values (so a busted forecast misleads the crowd).
            int ex = 0;
            bool shownWet = c.Words != ForecastWords.Fair && c.Words != ForecastWords.Unsettled && c.Words != ForecastWords.Unknown;
            if (shownWet && c.SnowSummitHi > 0) ex |= 1 << (int)WeatherEventKind.Storm;
            if (shownWet && s >= 0 && _fcStormShown[s] > rows.Quantile(t, WeatherRows.StormTotal, QuantileIndex(7500))) ex |= 1 << (int)WeatherEventKind.BigStorm;
            c.RainAtBase = shownWet && RainHoursAtBase(k, shift, false) >= Pacing.RainEventHours;
            c.RainWhileOpen = c.RainAtBase && RainHoursAtBase(k, shift, true) >= Pacing.RainEventHours;
            if (c.RainAtBase) ex |= 1 << (int)WeatherEventKind.Rain;
            if (shownWet && _fcDayFreezing[k]) ex |= 1 << (int)WeatherEventKind.FreezingRain;
            int month = Calendar.Month(day % Calendar.YearFaceDays);
            if ((month == 12 || month == 1 || month == 2) && ThawHoursAtMid(k, shift) >= Pacing.ThawHours) ex |= 1 << (int)WeatherEventKind.Thaw;
            if (highZ < rows.MaxQ10[t]) ex |= 1 << (int)WeatherEventKind.ColdSnap;
            if (c.GustSummit >= Pacing.WindEventGust * 10) ex |= 1 << (int)WeatherEventKind.WindEvent;
            if (_fcDayBluebird[k] && c.Words == ForecastWords.Fair) ex |= 1 << (int)WeatherEventKind.Bluebird;
            c.Expected = ex;
        }

        /// <summary>
        /// A storm's day is firm from 3 days out (its arrival octant and turn named on that card); at 3 days a few storms
        /// (ForecastTimingPermille, drawn once per storm) show a day off. Further out the two-day window stands in (ShowCard).
        /// </summary>
        void FirmStorms(ForecastSheet sheet, int days)
        {
            for (int s = 0; s < _fcStormCount; s++)
            {
                int main = _fcStormMain[s];
                if (main < 0) continue;
                int leadDay = sheet.Cards[main].FaceDay - sheet.IssueFaceDay;
                if (leadDay > 3) continue;
                int key = _fcStormKey[s];
                int shown = main;
                if (leadDay == 3 && WeatherMath.Permille(ForecastKey(-key, 6)) < Pacing.ForecastTimingPermille)
                    shown = main + (WeatherMath.Permille(ForecastKey(-key, 7)) < 500 ? -1 : 1);
                if (shown < 0 || shown >= days) shown = main;
                ref var c = ref sheet.Cards[shown];
                if (c.StormFirm) continue;   // two storms on a card: the first named
                c.StormFirm = true;
                c.StormOctant = _fcStormOctant[s];
                c.TurnOctant = _fcStormTurn[s];
            }
        }

        static int QuantileIndex(int point) => Array.IndexOf(WeatherRows.QuantilePoints, point);

        /// <summary>Phantom storms per dry card (‰): ForecastPhantomPermille × the third's storms per face day.</summary>
        int PhantomPermille(int t)
        {
            var rows = _weather.Rows;
            int median = QuantileIndex(5000);
            long cycle = 0;
            for (int d = 0; d < 4; d++) cycle += Math.Max(0, rows.Quantile(t, d, median));   // real tenths of an hour
            if (cycle <= 0) return 0;
            long realTenthsPerFaceDay = 240L * _tuning.CalendarDays / _tuning.FaceDays;
            long stormsPerDay = realTenthsPerFaceDay * 1000 / cycle;   // ‰
            return (int)Math.Min(1000, stormsPerDay * Pacing.ForecastPhantomPermille / 1000);
        }

        static ForecastWords Words(int snowShare, int leadDay)
        {
            bool snow = snowShare >= 700, rain = snowShare <= 300;
            if (leadDay <= 2) return snow ? ForecastWords.Snow : rain ? ForecastWords.Rain : ForecastWords.Mixed;
            if (leadDay <= 5) return snow ? ForecastWords.SnowLikely : rain ? ForecastWords.RainLikely : ForecastWords.SnowLikely;
            return rain ? ForecastWords.RainPossible : ForecastWords.SnowPossible;
        }

        /// <summary>The card's rain or mixed hours at the base, with the shown air's shift on the truth's wet bulb.</summary>
        /// <summary>The longest run of rain hours at the base on card <paramref name="k"/>; <paramref name="open"/>: inside the fixed ski day only.</summary>
        int RainHoursAtBase(int k, int shift, bool open)
        {
            int n = 0, run = 0;
            int first = MountainPlanner.Domain.Design.DayHours.Default.OpenSecond / 3600, last = (MountainPlanner.Domain.Design.DayHours.Default.StopSecond - 1) / 3600;
            for (int i = 0; i < _fcTruthCount; i++)
            {
                if (CardOfTruth(i) != k) continue;
                int hourOfDay = (int)((_fcTruthFirst + i) % 24);
                if (open && (hourOfDay < first || hourOfDay > last)) { run = 0; continue; }
                var b = _fcBase[i];
                if (b.Liquid > 0 && b.WetBulb + shift > WeatherAir.SnowWetBulb) n = Math.Max(n, ++run); else run = 0;
            }
            return n;
        }

        int ThawHoursAtMid(int k, int shift)
        {
            int n = 0, run = 0;
            for (int i = 0; i < _fcTruthCount; i++)
            {
                if (CardOfTruth(i) != k) continue;
                if (_fcMid[i].Temp + shift > Pacing.ThawTemp) n = Math.Max(n, ++run); else run = 0;
            }
            return n;
        }

        int CardOfTruth(int i) => (int)((_fcTruthFirst + i) / 24) - _fcFirstCard;

        void ShowHours(ForecastSheet sheet, long issue, int issueDay)
        {
            int n = Math.Min(ForecastSheet.Hours, _fcTruthCount);
            sheet.HourCount = n;
            for (int i = 0; i < n; i++)
            {
                long hour = _fcTruthFirst + i;
                int k = CardOfTruth(i);
                int lead = (int)(hour - issue);
                int lean = LeanAt(lead, out int pull);
                int day = (int)(hour / 24);
                int spread = (int)((long)Lerp(lead, 24, Pacing.ForecastTempTenths[0], 168, Pacing.ForecastTempTenths[1]) * Pacing.ForecastSkill / 1000);
                int wob = (int)WeatherMath.DivRound((long)WeatherMath.Normal1000(ForecastKey(day, 1)) * spread, 2000);
                int scale = k >= 0 && k < ForecastSheet.Days ? _fcDayScale[k] : 1000;
                ref var line = ref sheet.Hourly[i];
                line.Hour = hour;
                // The hour leans to its own truth (the day's climate is far off hour by hour): only the day's wobble moves it.
                int d = (int)WeatherMath.DivRound((long)wob * lean, 1000);
                line.TempBase = _fcBase[i].Temp + d; line.TempMid = _fcMid[i].Temp + d; line.TempSummit = _fcSummit[i].Temp + d;
                line.WetBulbBase = _fcBase[i].WetBulb + d; line.WetBulbMid = _fcMid[i].WetBulb + d; line.WetBulbSummit = _fcSummit[i].WetBulb + d;
                line.SnowBase = (int)((long)_fcBase[i].SnowTenthsIn * scale / 1000);
                line.SnowMid = (int)((long)_fcMid[i].SnowTenthsIn * scale / 1000);
                line.SnowSummit = (int)((long)_fcSummit[i].SnowTenthsIn * scale / 1000);
                line.RainBase = _fcBase[i].Liquid > 0 && line.WetBulbBase > WeatherAir.SnowWetBulb;
                line.Cloud = _fcTruth[i].Cloud;
                line.WindSummit = _fcSummit[i].Wind;
                line.GustSummit = _fcSummit[i].Gust;
                _ = pull;
            }
        }

        void ShowOutlook(ForecastSheet sheet, int firstCard)
        {
            // WX-57: the run-ahead's patterns (an active one first), firm in words inside 3 days.
            int active = -1, ridge = -1;
            for (int i = 0; i < _fcTruthCount; i++)
            {
                int day = (int)((_fcTruthFirst + i) / 24);
                if (_fcTruth[i].Pattern == WeatherRows.Active && active < 0) active = day;
                if (_fcTruth[i].Pattern == WeatherRows.Ridge && ridge < 0) ridge = day;
            }
            if (active >= 0) { sheet.Outlook = ForecastOutlook.Active; sheet.OutlookDay = active; }
            else if (ridge >= 0) { sheet.Outlook = ForecastOutlook.Ridge; sheet.OutlookDay = ridge; }
            sheet.OutlookFirm = sheet.Outlook != ForecastOutlook.None && sheet.OutlookDay - sheet.IssueFaceDay <= 3;
        }

        /// <summary>WX-43's named fields: tonight's gun window and snow by band, tomorrow's snow and summit gust, the next thaw.</summary>
        void NamedFields(ForecastSheet sheet, int issueDay)
        {
            // "Tonight" is from the stop to the next opening (the evening issue's own night; the morning's is tonight's).
            HoursFor(issueDay, out _, out _, out int stopToday, out _);
            HoursFor(issueDay + 1, out int openNext, out _, out _, out _);
            long from = sheet.Evening ? sheet.IssueHour + 1 : (long)issueDay * 24 + stopToday / 3600 + 1;
            long to = (long)(issueDay + 1) * 24 + openNext / 3600;
            for (int i = 0; i < sheet.HourCount; i++)
            {
                var h = sheet.Hourly[i];
                if (h.Hour < from || h.Hour >= to) continue;
                if (h.WetBulbBase <= Pacing.GunWindowWetBulb) sheet.GunWindowHours[0]++;
                if (h.WetBulbMid <= Pacing.GunWindowWetBulb) sheet.GunWindowHours[1]++;
                if (h.WetBulbSummit <= Pacing.GunWindowWetBulb) sheet.GunWindowHours[2]++;
                sheet.TonightSnow[0] += h.SnowBase; sheet.TonightSnow[1] += h.SnowMid; sheet.TonightSnow[2] += h.SnowSummit;
            }
            int tomorrow = issueDay + 1;
            for (int i = 0; i < sheet.HourCount; i++)
            {
                var h = sheet.Hourly[i];
                if (h.Hour / 24 != tomorrow) continue;
                sheet.TomorrowSnow[0] += h.SnowBase; sheet.TomorrowSnow[1] += h.SnowMid; sheet.TomorrowSnow[2] += h.SnowSummit;
            }
            int card = sheet.CardOf(tomorrow);
            sheet.TomorrowSummitGust = card >= 0 ? sheet.Cards[card].GustSummit : 0;
            sheet.NextThawDay = -1;
            for (int k = 0; k < sheet.DayCount; k++) if (sheet.Cards[k].Expects(WeatherEventKind.Thaw)) { sheet.NextThawDay = sheet.Cards[k].FaceDay; break; }
            // Each lift's top: tomorrow's summit gust by the height's exposure (B1's rule; the lift card's "Holds likely").
            int summitExposure = Math.Max(1, _weather.ExposureAt(Pacing, _weather.SummitFeet));
            for (int i = 0; i < _liftCount && i < sheet.LiftGustTenths.Length; i++)
            {
                int top = Design.Lifts[i].TopFeet;
                int e = _weather.ExposureAt(Pacing, top > 0 ? top : _weather.SummitFeet);
                sheet.LiftGustTenths[i] = (int)((long)sheet.TomorrowSummitGust * e / summitExposure);
            }
        }

        // ================= WX-42: alerts from the forecast =================

        static readonly WeatherEventKind[] AlertKinds =
        {
            WeatherEventKind.BigStorm, WeatherEventKind.Rain, WeatherEventKind.FreezingRain,
            WeatherEventKind.Thaw, WeatherEventKind.ColdSnap, WeatherEventKind.WindEvent,
        };

        /// <summary>
        /// On the evening or morning an event first sits within <c>AlertLeadDays</c>: one alert per (kind, target face day),
        /// with a cooldown of one face day, so a storm the forecast keeps showing alerts once. None pauses yet (4.17 tunes it).
        /// </summary>
        void RaiseForecastAlerts()
        {
            var sheet = _forecast;
            for (int k = 0; k < sheet.DayCount; k++)
            {
                ref var c = ref sheet.Cards[k];
                if (c.FaceDay - sheet.IssueFaceDay > Pacing.AlertLeadDays || c.FaceDay < sheet.IssueFaceDay) continue;
                foreach (var kind in AlertKinds)
                {
                    if (!c.Expects(kind)) continue;
                    if (c.FaceDay <= _alertLastDay[(int)kind] + 1) continue;
                    _alertLastDay[(int)kind] = c.FaceDay;
                    Events.Add(Tick, SimEventType.WeatherAlert, -1, c.FaceDay, (ushort)kind);
                }
            }
        }

        // ================= WX-44: the crowd's morning read =================

        /// <summary>
        /// The two weather lines of the day's demand, set just before <see cref="PlanTheDay"/>: the overnight snow at mid
        /// (× 1 at none, rising to <c>PowderPull</c> at twice the site's powder day) and the forecast the crowd read the evening
        /// before (never the truth, so a bust is drama).
        /// </summary>
        void WeatherPullForDay(int faceDay)
        {
            int powder = 1000, forecast = 1000;
            if (_weather != null)
            {
                int p = _weather.Rows.PowderDayTenthsIn;
                if (p > 0)
                {
                    int s = Math.Min(_evOvernightMid, 2 * p);
                    powder = 1000 + (int)((long)(Pacing.PowderPull - 1000) * s / (2 * p));
                }
                EnsureForecast();
                var sheet = _forecast;
                if (sheet != null && sheet.Known && sheet.Evening && sheet.IssueFaceDay == faceDay - 1)
                {
                    int k = sheet.CardOf(faceDay);
                    if (k >= 0)
                    {
                        var c = sheet.Cards[k];
                        long f = 1000;
                        bool wet = c.Words != ForecastWords.Fair && c.Words != ForecastWords.Unsettled && c.Words != ForecastWords.Unknown;
                        if (c.RainWhileOpen) f = f * Pacing.ForecastPullRain / 1000;
                        if (wet && c.GustSummit > Pacing.ForecastPullStormGust * 10) f = f * Pacing.ForecastPullStorm / 1000;
                        if (c.Expects(WeatherEventKind.Bluebird)) f = f * Pacing.ForecastPullBluebird / 1000;
                        if (c.Expects(WeatherEventKind.ColdSnap)) f = f * Pacing.ForecastPullColdSnap / 1000;
                        forecast = (int)f;
                    }
                }
            }
            SetWeatherPull(powder, forecast);
        }
    }
}
