#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Acquisition.Weather.Qc;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>WX-35's reference points (ft): base = 10th percentile of the box's heights, summit = 95th, mid halfway.</summary>
    public readonly struct ReferencePoints
    {
        public readonly int BaseFeet, MidFeet, SummitFeet;
        public ReferencePoints(int baseFeet, int summitFeet) { BaseFeet = baseFeet; SummitFeet = summitFeet; MidFeet = (baseFeet + summitFeet) / 2; }
    }

    /// <summary>What calibration measured, for the manifest's Weather block and the weather report (demo 93).</summary>
    public sealed class CalibrationReport
    {
        public double StormDayRatio, ClearingDaylightShare;
        public int Storms, Cycles, Winters;
        /// <summary>Kolmogorov–Smirnov distance of each fit at the site level (WX-31 v2: under 0.1).</summary>
        public readonly Dictionary<string, double> Ks = new Dictionary<string, double>();
        /// <summary>Each KS statistic's sample size: its gate is <see cref="WeatherCalibration.KsCritical"/> of it.</summary>
        public readonly Dictionary<string, int> KsCount = new Dictionary<string, int>();
        /// <summary>Fit parameters: Weibull shapes, the log-normal spread, the copula's correlations, persistence, shrink factors.</summary>
        public readonly Dictionary<string, double> Fits = new Dictionary<string, double>();
        /// <summary>WX-17 v2's ladder: fields whose third pooled up, and the level each reached (third, month, half, site).</summary>
        public readonly SortedDictionary<string, string> Levels = new SortedDictionary<string, string>();
        public readonly List<string> LimitedReasons = new List<string>();
        public double NightLapseFloorShare;
        public int LapseDays, GradientDays;
        /// <summary>WX-41 v2: powder mornings per December–March season, the record's days with ≥ 6 in and the calibration play's.</summary>
        public double PowderPerSeasonRecord, PowderPerSeasonGame;
    }

    /// <summary>
    /// WX-17 v2–WX-19 v2, WX-31 v2, WX-36 v2, WX-38 v2, WX-41 and WX-57: history in, tycoon tuning out. Reads the blended
    /// record (and the Daymet lattice for the lapse and the gradient) and writes the rows: two levels, the shapes and tails
    /// pooled over the winter, the thirds' means, medians and scales shrunk toward the level above. Transcendental maths
    /// happens only here; the rows are integers. Runs at package build, off the game's threads.
    /// </summary>
    public static class WeatherCalibration
    {
        /// <summary>WX-17 v2's shrinkage weights and minimum samples.</summary>
        public const double MeanN0 = 60, ScaleN0 = 50, HistogramN0 = 30;
        public const int ScaleMin = 20, HistogramMin = 30, EffectMin = 30;
        /// <summary>WX-41: the record's powder day (in of new snow at mid-mountain); PowderDayIn is never below it.</summary>
        public const double PowderInches = 6.0;
        /// <summary>The resort's stop and the morning landing (face clock), for the overnight window of WX-41.</summary>
        public const int StopHour = 16, LandingHour = 8;

        static readonly int[] MonthStart = { 0, 31, 61, 92, 123, 151, 182, 212, 243, 273, 304, 335, 365 };
        const double MmToHundredthsIn = 1 / 0.254, MsToTenthsMph = 22.369362920544;

        static int HalfOf(int month) => month <= 3 || month >= 10 ? 0 : 1;   // Oct–Jan and Aug–Sep; Feb–Jul (WX-57's nearness)
        static bool Winter(int third) => third >= 3 && third < 21;            // November to April
        static bool DecToMar(int third) => third >= 6 && third < 18;

        static readonly int[][] MonthThirds = Enumerable.Range(0, 12).Select(m => new[] { 3 * m, 3 * m + 1, 3 * m + 2 }).ToArray();
        /// <summary>
        /// The ladder's season level: the three months centred on each month (for December and March exactly WX-17 v2's
        /// November–January and February–April halves), so a thin summer third borrows from summer, never from March.
        /// </summary>
        static readonly int[][] SeasonThirds = Enumerable.Range(0, 12).Select(m =>
            new[] { (m + 11) % 12, m, (m + 1) % 12 }.SelectMany(x => new[] { 3 * x, 3 * x + 1, 3 * x + 2 }).ToArray()).ToArray();
        static readonly int[] AllThirds = Enumerable.Range(0, 36).ToArray();
        static readonly int[] WinterThirds = Enumerable.Range(0, 36).Where(Winter).ToArray();

        public static (WeatherRows Rows, CalibrationReport Report) Build(BlendedRecord b, WeatherSourceRecord src, ReferencePoints points, WeatherTuning tuning,
                                                                          int characterSeeds = 200, int powderSeeds = 20, int rateSeeds = 400, int stormSeeds = 120)
        {
            var rows = new WeatherRows();
            var rep = new CalibrationReport();
            var states = WeatherStates.Find(b);
            int nh = b.HourCount, nd = b.DayCount;
            rep.StormDayRatio = states.StormDayRatio;
            rep.ClearingDaylightShare = states.ClearingDaylightShare;
            rep.Storms = states.Storms.Count;
            rep.Cycles = states.Cycles.Count;

            rows.ZrefFeet = (int)Math.Round((src.Reference?.ElevationMetres ?? 0) / 0.3048);
            rows.BaseFeet = points.BaseFeet; rows.MidFeet = points.MidFeet; rows.SummitFeet = points.SummitFeet;
            rows.ObservationLagHours = b.LagHours;
            rep.Fits["blend.lagHours"] = b.LagHours;
            rep.Fits["blend.humidShareDays"] = b.HumidShareDays;
            rep.Fits["blend.evenShareDays"] = b.EvenShareDays;
            rows.FirstLabelYear = b.FirstLabelYear;
            int years = Math.Max(1, b.YearCount);
            rep.Winters = years;
            SunTable(rows, src.Site.Latitude, src.Site.Longitude);

            // Hourly temperatures in °F, night flags, smoothed day sets.
            var tf = new double[nh];
            var night = new bool[nh];
            for (int k = 0; k < nh; k++)
            {
                tf[k] = float.IsNaN(b.T[k]) ? double.NaN : Psychrometrics.CToF(b.T[k]);
                int month = CivilDay.LabelMonth(b.Days[k / 24]);
                int minute = (k % 24) * 60 + 30;
                night[k] = minute < rows.SunriseMinute[month] || minute >= rows.SunsetMinute[month];
            }
            var smoothed = SmoothedDays(states, nd);

            // ---- Climate (daily, smoothed ±5 days) ----
            var maxRaw = new double[36];
            var minRaw = new double[36];
            for (int t = 0; t < 36; t++)
            {
                var days = smoothed[t].Where(d => !b.DayMissing(d)).ToList();
                maxRaw[t] = days.Count == 0 ? 32 : days.Average(d => Psychrometrics.CToF(b.Tmax[d]));
                minRaw[t] = days.Count == 0 ? 20 : days.Average(d => Psychrometrics.CToF(b.Tmin[d]));
                rows.MaxQ10[t] = Tenths(days.Count == 0 ? 32 : Quantile(days.Select(d => Psychrometrics.CToF(b.Tmax[d])).ToList(), 0.10));
            }

            // Diurnal shapes: fair days (no storm hour) and storm days (12 storm hours or more).
            var stormHoursOfDay = new int[nd];
            for (int k = 0; k < nh; k++) if (states.State[k] == WeatherRows.Storm) stormHoursOfDay[k / 24]++;
            for (int pass = 0; pass < 2; pass++)
            {
                var shape = pass == 0 ? rows.DiurnalFair : rows.DiurnalStorm;
                var byThird = new List<double>[24][];
                for (int h = 0; h < 24; h++) byThird[h] = Enumerable.Range(0, 36).Select(_ => new List<double>()).ToArray();
                for (int d = 0; d < nd; d++)
                {
                    bool use = pass == 0 ? stormHoursOfDay[d] == 0 : stormHoursOfDay[d] >= 12;
                    if (!use || b.DayMissing(d)) continue;
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int h = 0; h < 24; h++) { double v = tf[d * 24 + h]; if (double.IsNaN(v)) continue; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
                    if (!(hi - lo > 0.5)) continue;
                    for (int h = 0; h < 24; h++)
                    {
                        double v = tf[d * 24 + h];
                        if (double.IsNaN(v)) continue;
                        // A day belongs to every third whose smoothed window holds it. A fair day's shape is its own range; a
                        // storm day's is in the third's mean range (WX-25 v3): a storm day swings about half as much as the
                        // average day, and on its own range the generator gave storms a clear day's swing.
                        foreach (int t in ThirdsHolding(states.DayOfLabelYear[d]))
                            byThird[h][t].Add((v - lo) / Math.Max(1, maxRaw[t] - minRaw[t]));
                    }
                }
                for (int h = 0; h < 24; h++)
                {
                    var values = Ladder(rep, $"diurnal.{(pass == 0 ? "fair" : "storm")}", byThird[h], l => l.Count == 0 ? double.NaN : l.Average(), MeanN0, ScaleMin, 0.5, quiet: h != 12);
                    for (int t = 0; t < 36; t++) shape[t * 24 + h] = (int)Math.Round(values[t] * 1000);
                }
            }

            // ---- Offsets against the hour-of-day climate (WX-17 v2), then shifted so Fair's is zero ----
            double Climate(int k, bool storm)
            {
                int t = states.HourThird(k), h = k % 24;
                int[] shape = storm ? rows.DiurnalStorm : rows.DiurnalFair;
                return minRaw[t] + (maxRaw[t] - minRaw[t]) * shape[t * 24 + h] / 1000.0;
            }
            var offset = new double[nh];
            for (int k = 0; k < nh; k++) offset[k] = double.IsNaN(tf[k]) ? double.NaN : tf[k] - Climate(k, states.State[k] == WeatherRows.Storm);
            var stateOffset = new double[4][];
            for (int s = 0; s < 4; s++)
            {
                var byThird = Groups();
                for (int k = 0; k < nh; k++) if (states.State[k] == s && !double.IsNaN(offset[k])) byThird[states.HourThird(k)].Add(offset[k]);
                stateOffset[s] = Ladder(rep, "offset." + StateName(s), byThird, Mean, MeanN0, ScaleMin, 0);
            }
            var fairShift = stateOffset[WeatherRows.Fair];
            for (int t = 0; t < 36; t++)
            {
                rows.MaxMean[t] = Tenths(maxRaw[t] + fairShift[t]);
                rows.MinMean[t] = Tenths(minRaw[t] + fairShift[t]);
                rows.BuildingOffset[t] = Tenths(stateOffset[WeatherRows.Building][t] - fairShift[t]);
                rows.ClearingOffset[t] = Tenths(stateOffset[WeatherRows.Clearing][t] - fairShift[t]);
            }

            // Per storm: its offset (whole, first half, second half) net of the Fair shift.
            var stormOffset = new double[states.Storms.Count];
            var firstHalf = new double[states.Storms.Count];
            var secondHalf = new double[states.Storms.Count];
            var clearingOffset = new double[states.Storms.Count];
            for (int i = 0; i < states.Storms.Count; i++)
            {
                var st = states.Storms[i];
                double shift = fairShift[st.Third];
                int mid = st.Start + Math.Max(1, (st.End - st.Start) / 2);
                stormOffset[i] = MeanOver(offset, st.Start, st.End) - shift;
                firstHalf[i] = MeanOver(offset, st.Start, mid) - shift;
                secondHalf[i] = st.End > mid ? MeanOver(offset, mid, st.End) - shift : firstHalf[i];
                clearingOffset[i] = st.ClearingEnd > st.End ? MeanOver(offset, st.End, st.ClearingEnd) - shift : double.NaN;
            }
            {
                var byThird = Groups();
                for (int i = 0; i < stormOffset.Length; i++) if (!double.IsNaN(stormOffset[i])) byThird[states.Storms[i].Third].Add(stormOffset[i]);
                // The mean is over storm hours (long storms weigh by their length, as the game lives them); the spread is storm to storm.
                var mean = stateOffset[WeatherRows.Storm].Select((v, t) => v - fairShift[t]).ToArray();
                var spread = Ladder(rep, "storm.offsetSpread", byThird, Std, ScaleN0, ScaleMin, 4);
                for (int t = 0; t < 36; t++) { rows.StormOffsetMean[t] = Tenths(mean[t]); rows.StormOffsetSpread[t] = Tenths(spread[t]); rows.StormAnomalyWeight[t] = 1000; }
            }

            // Directions.
            HistogramInto(rows.ArrivalOctants, Ladder8(rep, "storm.octants", GroupBy(states.Storms, st => st.Third, st => st.Octant)));
            HistogramInto(rows.ClearingOctants, Ladder8(rep, "clearing.octants", GroupBy(states.Storms.Where(st => st.ClearingEnd > st.End), st => st.Third, st => st.ClearingOctant)));
            {
                var byThird = IntGroups();
                for (int k = 0; k < nh; k++)
                    if (states.State[k] == WeatherRows.Fair && !float.IsNaN(b.Dir[k]) && !float.IsNaN(b.Wind[k]) && b.Wind[k] > 0.5f)
                        byThird[states.HourThird(k)].Add(WeatherStates.Octant(b.Dir[k]));
                HistogramInto(rows.FairOctants, Ladder8(rep, "fair.octants", byThird));
            }

            // ---- Patterns (WX-57): labels, mixes, persistence, matrices ----
            Patterns(rows, rep, states);

            // ---- Storm totals and their factors (WX-24 v2, WX-57) ----
            var totals = states.Storms.Select(st => st.TotalMm).ToArray();
            var rawMedian = Ladder(rep, "storm.rawMedian", GroupBy(states.Storms, st => st.Third, st => Math.Log(Math.Max(0.1, st.TotalMm))), Mean, MeanN0, ScaleMin, Math.Log(10)).Select(Math.Exp).ToArray();
            var mixWinter = new double[3];
            foreach (var cy in states.Cycles.Where(c => !c.Truncated && Winter(c.Third))) mixWinter[cy.Pattern]++;
            double mixTotal = Math.Max(1, mixWinter.Sum());
            for (int p = 0; p < 3; p++) mixWinter[p] /= mixTotal;

            var patternSize = GeometricFactor(rep, "pattern.size", states.Storms.Where(st => Winter(st.Third)).Select(st => (st.Pattern, st.TotalMm / rawMedian[st.Third])), 3, mixWinter);
            var quarterCount = new double[4];
            foreach (var st in states.Storms.Where(st => Winter(st.Third))) quarterCount[st.Quarter]++;
            var quarterShare = quarterCount.Select(c => c / Math.Max(1, quarterCount.Sum())).ToArray();
            var quarterSize = GeometricFactor(rep, "quarter.size", states.Storms.Where(st => Winter(st.Third)).Select(st => (st.Quarter, st.TotalMm / rawMedian[st.Third] / patternSize[st.Pattern])), 4, quarterShare);
            for (int p = 0; p < 3; p++) rows.PatternSize[p] = (int)Math.Round(patternSize[p] * 1000);
            for (int q = 0; q < 4; q++) rows.QuarterSize[q] = (int)Math.Round(quarterSize[q] * 1000);

            var adjusted = new double[states.Storms.Count];
            for (int i = 0; i < adjusted.Length; i++)
            {
                var st = states.Storms[i];
                adjusted[i] = Math.Max(0.1, st.TotalMm) / (rows.PatternSize[st.Pattern] / 1000.0) / (rows.QuarterSize[st.Quarter] / 1000.0);
            }
            // WX-31 v3 (owner 2026-11-08): the shape is the record's own, not a log-normal (which ran 11–17 % light at the 90th
            // percentile): the pooled winter's totals over their third's geometric mean, read at the table's points. Each
            // third's table is that shape scaled so its mean is the third's mean storm: the shape is pooled, the totals kept.
            var logMean = Ladder(rep, "storm.logMean", GroupBy(Enumerable.Range(0, adjusted.Length), i => states.Storms[i].Third, i => Math.Log(adjusted[i])), Mean, ScaleN0, ScaleMin, Math.Log(10));
            var winterStorms = Enumerable.Range(0, adjusted.Length).Where(i => Winter(states.Storms[i].Third)).ToList();
            var residualLogs = winterStorms.Select(i => Math.Log(adjusted[i]) - logMean[states.Storms[i].Third]).ToList();
            double sigma = residualLogs.Count < 2 ? 0.8 : Std(residualLogs);
            var sizeShape = new double[WeatherRows.QuantileCount];
            var ratios = residualLogs.Select(Math.Exp).OrderBy(x => x).ToList();
            for (int i = 0; i < sizeShape.Length; i++)
            {
                double p = WeatherRows.QuantilePoints[i] / 10_000.0;
                sizeShape[i] = ratios.Count >= EffectMin ? EmpiricalQuantile(ratios, p) : Math.Exp(sigma * NormalInv(p));
            }
            double shapeMean = ShapeMean(sizeShape), shapeMedian = sizeShape[Array.IndexOf(WeatherRows.QuantilePoints, 5000)];
            // The storm's normal score as the generator reads it (its rank in the shape), for the size term (WX-25 v3).
            var sizeScore = new double[adjusted.Length];
            for (int i = 0; i < adjusted.Length; i++)
                sizeScore[i] = NormalInv(Math.Max(0.0005, Math.Min(0.9995, ShapeCdf(sizeShape, adjusted[i] / Math.Exp(logMean[states.Storms[i].Third])))));
            // "Never add or remove it on average" (WX-57): the generator draws the pattern and the quarter independently of the
            // storm, so each third's mean is the record's mean storm ÷ the factor product it should expect under its own mix.
            // The quarter follows the generator's own draw: the pattern's arrival histogram where it has one, else the third's.
            var expectedSize = new double[36];
            for (int t = 0; t < 36; t++)
            {
                double e = 0;
                for (int p = 0; p < 3; p++)
                {
                    bool own = Enumerable.Range(0, 8).Sum(o => rows.PatternOctants[p * 8 + o]) > 0;
                    var quarters = own ? QuarterMix(rows.PatternOctants, p) : QuarterMix(rows.ArrivalOctants, t);
                    double q = 0;
                    for (int i = 0; i < 4; i++) q += quarters[i] * quarterSize[i];
                    e += rows.PatternMix[t * 3 + p] / 1000.0 * patternSize[p] * q;
                }
                expectedSize[t] = Math.Max(0.1, e);
            }
            var meanTotal = Ladder(rep, "storm.mean", GroupBy(states.Storms, st => st.Third, st => Math.Max(0.1, st.TotalMm)), Mean, ScaleN0, ScaleMin, 10)
                .Select((m, t) => m / expectedSize[t]).ToArray();
            var logMedian = new double[36];
            rows.StormTotalSigma = (int)Math.Round(sigma * 1000);
            rep.Fits["storm.total.sigma"] = sigma;
            rep.Fits["storm.total.lognormalKs"] = KsNormal(residualLogs.Select(r => r / Math.Max(1e-6, sigma)).ToList());
            rep.Ks["storm.total"] = KsUniform(ratios.Select(x => ShapeCdf(sizeShape, x)).ToList());
            rep.KsCount["storm.total"] = ratios.Count;
            for (int t = 0; t < 36; t++)
            {
                double scale = meanTotal[t] / Math.Max(1e-9, shapeMean);
                logMedian[t] = Math.Log(Math.Max(1e-6, scale * shapeMedian));
                rows.StormMedian[t] = (int)Math.Round(scale * shapeMedian * MmToHundredthsIn);
                for (int i = 0; i < WeatherRows.QuantileCount; i++)
                    rows.SetQuantile(t, WeatherRows.StormTotal, i, Math.Max(1, (int)Math.Round(scale * sizeShape[i] * MmToHundredthsIn)));
            }

            // ---- WX-25 v3: bigger storms and heavier hours run warmer ----
            StormWarmth(rows, rep, b, states, offset, stormOffset, sizeScore, tuning);

            // ---- Durations: Weibull, shape per state over the winter, scale per third (WX-31 v2) ----
            var fairGaps = new List<(int Third, double Hours, int Pattern)>();
            foreach (var cy in states.Cycles)
                if (!cy.Truncated && cy.FairEnd > cy.FairStart) fairGaps.Add((cy.Third, cy.FairEnd - cy.FairStart, cy.Pattern));
            var gapMedian = Ladder(rep, "fair.rawMedian", GroupBy(fairGaps, g => g.Third, g => Math.Log(g.Hours)), Mean, MeanN0, ScaleMin, Math.Log(48)).Select(Math.Exp).ToArray();
            var gapFactor = GeometricFactor(rep, "pattern.gap", fairGaps.Where(g => Winter(g.Third)).Select(g => (g.Pattern, g.Hours / gapMedian[g.Third])), 3, mixWinter);
            for (int p = 0; p < 3; p++) rows.PatternGap[p] = (int)Math.Round(gapFactor[p] * 1000);

            var samples = new List<(int Third, double Hours)>[4];
            samples[WeatherRows.Fair] = fairGaps.Select(g => (g.Third, g.Hours / (rows.PatternGap[g.Pattern] / 1000.0))).ToList();
            // The Fair scale keeps the record's mean gap under the third's own pattern mix (the shape is fitted on the divided gaps).
            var stormRate = new double[36];
            var fairMean = FairGapFromRate(rep, states, nd, years, stormRate)
                .Select((m, t) => m / Math.Max(0.1, Enumerable.Range(0, 3).Sum(i => rows.PatternMix[t * 3 + i] / 1000.0 * gapFactor[i]))).ToArray();
            samples[WeatherRows.Building] = states.Storms.Where(st => st.Start > st.BuildingStart).Select(st => (st.Third, (double)(st.Start - st.BuildingStart))).ToList();
            samples[WeatherRows.Storm] = states.Storms.Select(st => (st.Third, st.Hours)).ToList();
            samples[WeatherRows.Clearing] = states.Storms.Where(st => st.ClearingEnd > st.End).Select(st => (st.Third, (double)(st.ClearingEnd - st.End))).ToList();
            int[] distribution = { 0, 1, 2, 3 };
            for (int s = 0; s < 4; s++) FitWeibull(rows, rep, StateName(s), s, distribution[s], samples[s], s == WeatherRows.Fair ? fairMean : null);

            {
                var byThird = GroupBy(states.Storms, st => st.Third, st => st.Start == st.BuildingStart ? 1.0 : 0.0);
                var zero = Ladder(rep, "building.zeroShare", byThird, Mean, MeanN0, ScaleMin, 0.2);
                for (int t = 0; t < 36; t++) rows.BuildingZeroShare[t] = Permille(zero[t]);
                var next = new List<double>[36];
                for (int t = 0; t < 36; t++) next[t] = new List<double>();
                for (int i = 0; i + 1 < states.Storms.Count; i++) next[states.Storms[i].Third].Add(states.Storms[i + 1].BackToBack ? 1 : 0);
                var b2b = Ladder(rep, "backToBack", next, Mean, MeanN0, ScaleMin, 0.1);
                for (int t = 0; t < 36; t++) rows.BackToBack[t] = Permille(b2b[t]);
            }

            // The copula: Spearman's rank correlation between duration and total over the winter, as a Gaussian ρ.
            {
                var winter = Enumerable.Range(0, states.Storms.Count).Where(i => Winter(states.Storms[i].Third)).ToList();
                double rs = winter.Count < 3 ? 0.5 : Spearman(winter.Select(i => states.Storms[i].Hours).ToList(), winter.Select(i => adjusted[i]).ToList());
                double rho = Math.Max(0, Math.Min(0.95, 2 * Math.Sin(Math.PI * rs / 6)));
                rows.DurationTotalRho = (int)Math.Round(rho * 1000);
                rows.DurationTotalRhoComplement = (int)Math.Round(Math.Sqrt(1 - rho * rho) * 1000);
                rep.Fits["storm.spearman"] = rs;
                rep.Fits["storm.copulaRho"] = rho;
            }

            // Flurries: the share of liquid outside storms, credited to the Clearings (the only wet non-storm state).
            {
                var stormLiquid = new double[36];
                var otherLiquid = new double[36];
                var count = new int[36];
                // A storm's total already holds its run's drizzle either side of Start–End: only liquid outside every storm's
                // span is the Clearings' (counted twice, it made showery months like Olympic Valley's April 13 % too wet).
                var inStorm = new bool[nh];
                foreach (var st in states.Storms)
                {
                    stormLiquid[st.Third] += st.TotalMm; count[st.Third]++;
                    for (int k = Math.Max(0, st.LiquidFrom); k < Math.Min(nh, st.LiquidTo); k++) inStorm[k] = true;
                }
                for (int k = 0; k < nh; k++)
                    if (!inStorm[k] && !float.IsNaN(b.P[k])) otherLiquid[states.HourThird(k)] += b.P[k];
                var share = LadderStat(rep, "clearing.liquidShare", set =>
                {
                    double s = set.Sum(t => stormLiquid[t]), o = set.Sum(t => otherLiquid[t]);
                    return (s + o <= 0 ? double.NaN : o / (s + o), set.Sum(t => count[t]));
                }, MeanN0, ScaleMin, 0.05);
                for (int t = 0; t < 36; t++) rows.ClearingLiquidShare[t] = Math.Min(900, Permille(share[t]));
            }

            // Compass quarters: warm-sector and post-frontal offsets around the third's storm mean.
            {
                var warm = new double[4];
                var post = new double[4];
                var all = Enumerable.Range(0, states.Storms.Count).Where(i => Winter(states.Storms[i].Third) && !double.IsNaN(firstHalf[i])).ToList();
                double W(int i) => firstHalf[i] - rows.StormOffsetMean[states.Storms[i].Third] / 10.0;
                double P(int i) => secondHalf[i] - rows.StormOffsetMean[states.Storms[i].Third] / 10.0;
                double Hw(int i) => states.Storms[i].Hours;
                double WeightedAverage(List<int> set, Func<int, double> f) => set.Sum(i => Hw(i) * f(i)) / Math.Max(1e-9, set.Sum(Hw));
                double pooledW = all.Count == 0 ? 0 : WeightedAverage(all, W), pooledP = all.Count == 0 ? 0 : WeightedAverage(all, P);
                for (int q = 0; q < 4; q++)
                {
                    var inQ = all.Where(i => states.Storms[i].Quarter == q).ToList();
                    bool own = inQ.Count >= EffectMin;
                    warm[q] = own ? WeightedAverage(inQ, W) : pooledW;
                    post[q] = own ? WeightedAverage(inQ, P) : pooledP;
                    rep.Levels[$"quarter.{q}"] = own ? "winter" : "site";
                }
                // Neutral on average: the hour-weighted mean of the storm's two halves is zero.
                var hourShare = new double[4];
                foreach (int i in all) hourShare[states.Storms[i].Quarter] += Hw(i);
                double hoursTotal = Math.Max(1e-9, hourShare.Sum());
                double centre = 0;
                for (int q = 0; q < 4; q++) centre += hourShare[q] / hoursTotal * (warm[q] + post[q]) / 2;
                for (int q = 0; q < 4; q++) { rows.QuarterWarm[q] = Tenths(warm[q] - centre); rows.QuarterPost[q] = Tenths(post[q] - centre); }
            }

            // Post-storm cold: clearing offset on storm size above the third's median (WX-25 v2).
            {
                var xs = new List<double>();
                var ys = new List<double>();
                for (int i = 0; i < states.Storms.Count; i++)
                {
                    var st = states.Storms[i];
                    if (!Winter(st.Third) || double.IsNaN(clearingOffset[i])) continue;
                    xs.Add((st.TotalMm - Math.Exp(logMedian[st.Third])) * MmToHundredthsIn);
                    ys.Add(clearingOffset[i] - rows.ClearingOffset[st.Third] / 10.0);
                }
                double slope = Slope(xs, ys);   // °F per 0.01 in
                // Stored as °F per inch × 100 (never a warming; at most 20 °F per inch), capped at 8 °F by PostStormColdCap.
                rows.PostStormColdK = Math.Min(2000, (int)Math.Round(Math.Max(0, -slope) * 10_000));
                rows.PostStormColdCap = 80;
                rep.Fits["postStorm.slopeFPerIn"] = slope * 100;
                // The record's Clearing mean already holds the big storms' cold: take out what the generator will add back.
                var extra = new double[36]; var extraN = new int[36];
                for (int i = 0; i < states.Storms.Count; i++)
                {
                    var st = states.Storms[i];
                    if (st.ClearingEnd <= st.End) continue;
                    double x = (st.TotalMm - Math.Exp(logMedian[st.Third])) * MmToHundredthsIn;
                    double e = Math.Min(rows.PostStormColdCap, Math.Max(0, rows.PostStormColdK * x / 1000)) / 10.0;
                    extra[st.Third] += e * (st.ClearingEnd - st.End);
                    extraN[st.Third] += st.ClearingEnd - st.End;
                }
                for (int t = 0; t < 36; t++) if (extraN[t] > 0) rows.ClearingOffset[t] += Tenths(extra[t] / extraN[t]);
            }

            // ---- Humidity, wind, sky by state (POWER) ----
            // A storm's dew-point depression is its wet hours', weighted by their liquid (WX-25 v3): the generator's storm hours
            // are nearly all wet (a face hour holds 2.9 real hours), and over all storm hours the drier lulls made its snow
            // fall at a wet bulb 0.3–0.4 °F too cold.
            var wetDep = new double[36]; var wetLiq = new double[36]; var wetN = new int[36];
            for (int k = 0; k < nh; k++)
            {
                if (states.State[k] != WeatherRows.Storm || float.IsNaN(b.T[k]) || float.IsNaN(b.Td[k]) || float.IsNaN(b.P[k]) || b.P[k] <= 0) continue;
                int t = states.HourThird(k);
                wetDep[t] += b.P[k] * (b.T[k] - b.Td[k]) * 1.8; wetLiq[t] += b.P[k]; wetN[t]++;
            }
            var stormDepression = LadderStat(rep, "dew.storm", set =>
            {
                double d = 0, l = 0; int n = 0;
                foreach (int t in set) { d += wetDep[t]; l += wetLiq[t]; n += wetN[t]; }
                return (l <= 0 ? double.NaN : d / l, n);
            }, MeanN0, ScaleMin, 3);
            for (int s = 0; s < 4; s++)
            {
                var dep = Groups(); var cloudDay = Groups(); var cloudNight = Groups(); var clear = Groups(); var wind = Groups();
                for (int k = 0; k < nh; k++)
                {
                    if (states.State[k] != s) continue;
                    int t = states.HourThird(k);
                    if (!float.IsNaN(b.T[k]) && !float.IsNaN(b.Td[k]) && s != WeatherRows.Storm) dep[t].Add((b.T[k] - b.Td[k]) * 1.8);
                    if (!float.IsNaN(b.Cloud[k])) (night[k] ? cloudNight : cloudDay)[t].Add(b.Cloud[k] / 100.0);
                    if (!night[k] && !float.IsNaN(b.Clearness[k])) clear[t].Add(b.Clearness[k]);
                    if (s != WeatherRows.Storm && !float.IsNaN(b.Wind[k])) wind[t].Add(b.Wind[k] * MsToTenthsMph);
                }
                if (s == WeatherRows.Storm)
                    foreach (var st in states.Storms) wind[st.Third].Add(MeanOverF(b.Wind, st.Start, st.End) * MsToTenthsMph);
                string n = StateName(s);
                var depression = s == WeatherRows.Storm ? stormDepression : Ladder(rep, $"dew.{n}", dep, Mean, MeanN0, ScaleMin, 5);
                var cd = Ladder(rep, $"cloud.day.{n}", cloudDay, Mean, MeanN0, ScaleMin, 0.5);
                var cn = Ladder(rep, $"cloud.night.{n}", cloudNight, Mean, MeanN0, ScaleMin, 0.5);
                var cl = Ladder(rep, $"clearness.{n}", clear, Mean, MeanN0, ScaleMin, 0.6);
                var wm = Ladder(rep, $"wind.{n}", wind, Mean, MeanN0, ScaleMin, 100);
                var ws = Ladder(rep, $"wind.spread.{n}", wind, Std, ScaleN0, ScaleMin, 40);
                for (int t = 0; t < 36; t++)
                {
                    int i = t * 4 + s;
                    rows.DewDepression[i] = Tenths(Math.Max(0, depression[t]));
                    rows.CloudDay[i] = Permille(cd[t]); rows.CloudNight[i] = Permille(cn[t]); rows.Clearness[i] = Permille(cl[t]);
                    rows.WindMean[i] = (int)Math.Round(wm[t]); rows.WindSpread[i] = (int)Math.Round(ws[t]);
                }
            }

            Wind(rows, rep, b, states, tuning);

            // Rate cap: K × the fitted 99.5 % real hourly rate of storm hours (log-normal), floor applied by the generator.
            {
                var logs = new List<double>();
                for (int k = 0; k < nh; k++)
                    if (states.State[k] == WeatherRows.Storm && !float.IsNaN(b.P[k]) && b.P[k] >= WeatherStates.ShapingMmPerHour && Winter(states.HourThird(k))) logs.Add(Math.Log(b.P[k]));
                double q995 = logs.Count < 10 ? 5 : Math.Exp(logs.Average() + 2.5758 * Std(logs));
                rows.RateCapRealMilliIn = (int)Math.Round(q995 / 0.0254);
                rep.Fits["rate.q995MmPerHour"] = q995;
            }

            // ---- The anomaly, the pattern lean and the season character (WX-25 v2, WX-19 v2) ----
            Anomaly(rows, rep, b, states, tf, offset, fairShift, stormOffset, firstHalf, secondHalf, smoothed, years);

            // ---- Lapse and gradient from the lattice (WX-36 v2, WX-38 v2) ----
            Lapse(rows, rep, src, states, b);

            // ---- Targets (WX-07 v3: checks, never inputs) ----
            Targets(rows, b, states, tf, years);

            // ---- The season character's shrink and PowderDayIn: calibration plays (WX-19 v2, WX-41) ----
            ShrinkCharacter(rows, rep, b, states, tuning, characterSeeds, years);
            RateCorrection(rows, rep, tuning, stormRate, rateSeeds);
            LiquidCorrection(rows, rep, tuning, stormSeeds);
            NightDepth(rows, rep, states, tf, tuning, stormSeeds);
            HourWobble(rows, rep, states, tf, tuning, stormSeeds);
            StormTemperature(rows, rep, b, states, tf, tuning, stormSeeds);
            // The wet lean is fitted on the finished hours and moves the storms' air, so the storms are fitted again after it.
            WetLean(rows, rep, b, states, tf, tuning, stormSeeds * 5 / 2);   // 300: the link is a small correlation
            ColdSide(rows, rep, states, tf, tuning, stormSeeds);
            StormTemperature(rows, rep, b, states, tf, tuning, stormSeeds);
            PowderDay(rows, rep, b, states, tf, tuning, powderSeeds);

            if (rep.LimitedReasons.Count > 0) rows.Limited = 1;
            return (rows, rep);
        }

        // ---- Pieces ----

        static void SunTable(WeatherRows rows, double lat, double lon)
        {
            for (int m = 0; m < 12; m++)
            {
                int month = (m + 9) % 12 + 1;
                int year = m <= 2 ? 2022 : 2023;
                int doy = (new DateTime(year, month, 15) - new DateTime(year, 1, 1)).Days + 1;
                var day = new SolarDay(lat, lon, year, doy);
                rows.SunriseMinute[m] = (int)Math.Round(day.Sunrise() / 60);
                rows.SunsetMinute[m] = (int)Math.Round(day.Sunset() / 60);
            }
        }

        /// <summary>
        /// The Fair gap's mean per third, from the third's storm rate rather than its gaps. Gaps are assigned to the third
        /// they start in and measured whole (WX-17 v2), so a drought winter's 40-day gap belongs to January and February's
        /// sample comes mostly from stormy years: the mean of the gaps that start in a third undercounts long gaps (length
        /// bias). The rate (storms starting in the third ÷ its hours) has no such bias; the gap is what's left of the time per
        /// storm after its Building, Storm and Clearing: (time per storm − B − S − C) ÷ (1 − back-to-back share). Rates are
        /// shrunk toward the mean of their month's third rates (the season's, then the site's, under 20 storms), since storm
        /// counts go as the rate. The Weibull shape is still fitted on the gaps themselves.
        /// </summary>
        static double[] FairGapFromRate(CalibrationReport rep, RecordStates states, int nd, int years, double[] rateOut)
        {
            var n = new int[36]; var hours = new double[36];
            var b = new double[36]; var st = new double[36]; var c = new double[36]; var b2b = new double[36]; var pairs = new int[36];
            for (int d = 0; d < nd; d++) hours[states.DayThird[d]] += 24;
            for (int i = 0; i < states.Storms.Count; i++)
            {
                var s = states.Storms[i];
                n[s.Third]++;
                b[s.Third] += s.Start - s.BuildingStart;
                st[s.Third] += s.Hours;
                c[s.Third] += s.ClearingEnd - s.End;
                if (i + 1 < states.Storms.Count) { pairs[s.Third]++; if (states.Storms[i + 1].BackToBack) b2b[s.Third]++; }
            }
            double Rate(IEnumerable<int> set, out int count)
            {
                double r = 0; int k = 0; count = 0;
                foreach (int t in set) { count += n[t]; if (hours[t] > 0) { r += n[t] / hours[t]; k++; } }
                return k == 0 ? double.NaN : r / k;
            }
            double Pooled(double[] x, int[] set) { double sx = 0; int sn = 0; foreach (int t in set) { sx += x[t]; sn += n[t]; } return sn == 0 ? 0 : sx / sn; }
            double site = Rate(AllThirds, out int siteN);
            if (double.IsNaN(site) || siteN < ScaleMin) site = 1 / 120.0;
            var result = new double[36];
            for (int t = 0; t < 36; t++)
            {
                double month = Rate(MonthThirds[t / 3], out int monthN);
                double season = Rate(SeasonThirds[t / 3], out int seasonN);
                double parent = monthN >= ScaleMin ? month : seasonN >= ScaleMin ? season : site;
                double own = hours[t] > 0 ? n[t] / hours[t] : parent;
                double rate = n[t] >= ScaleMin ? (n[t] * own + ScaleN0 * parent) / (n[t] + ScaleN0) : parent;
                // The other states' means and the back-to-back share at the same level as the rate.
                int[] set = n[t] >= ScaleMin ? new[] { t } : monthN >= ScaleMin ? MonthThirds[t / 3] : SeasonThirds[t / 3];
                double others = Pooled(b, set) + Pooled(st, set) + Pooled(c, set);
                double backToBack = 0; int pn = 0;
                foreach (int x in set) { backToBack += b2b[x]; pn += pairs[x]; }
                backToBack = pn == 0 ? 0 : backToBack / pn;
                rateOut[t] = rate;
                double perStorm = 1 / Math.Max(1e-6, rate);
                result[t] = Math.Max(6, (perStorm - others) / Math.Max(0.05, 1 - backToBack));
                rep.Fits[$"fair.rateGap.{t}"] = result[t];
            }
            return result;
        }

        /// <summary>A third's quarter shares from its arrival octants: an odd octant straddles two quarters and splits evenly.</summary>
        static double[] QuarterMix(int[] octants, int third)
        {
            var q = new double[4];
            for (int o = 0; o < 8; o++)
            {
                double w = octants[third * 8 + o] / 1000.0;
                if (o % 2 == 0) q[o / 2 % 4] += w;
                else { q[(o / 2) % 4] += w / 2; q[(o / 2 + 1) % 4] += w / 2; }
            }
            return q;
        }

        static void FitWeibull(WeatherRows rows, CalibrationReport rep, string name, int state, int dist, List<(int Third, double Hours)> samples, double[]? meanByThird = null)
        {
            var positive = samples.Where(s => s.Hours > 0).ToList();
            // Shape over the winter, on durations divided by their third's median (the scale differs by third; the shape doesn't).
            var med = Ladder(rep, $"{name}.median", GroupBy(positive, s => s.Third, s => Math.Log(s.Hours)), Mean, MeanN0, ScaleMin, Math.Log(12)).Select(Math.Exp).ToArray();
            var winter = positive.Where(s => Winter(s.Third)).Select(s => s.Hours / med[s.Third]).ToList();
            double k = winter.Count < 10 ? 1.0 : WeibullShape(winter);
            rows.WeibullShape[state] = (int)Math.Round(k * 1000);
            rep.Fits[$"{name}.weibullShape"] = k;
            // The scale keeps each third's mean duration under the pooled shape: λ = mean ÷ Γ(1 + 1/k).
            double gamma = Gamma(1 + 1 / k);
            var scale = (meanByThird ?? Ladder(rep, $"{name}.scale", GroupBy(positive, s => s.Third, s => s.Hours), Mean, ScaleN0, ScaleMin, 12)).Select(m => m / gamma).ToArray();
            for (int t = 0; t < 36; t++)
                for (int i = 0; i < WeatherRows.QuantileCount; i++)
                {
                    double p = WeatherRows.QuantilePoints[i] / 10_000.0;
                    double hours = scale[t] * Math.Pow(-Math.Log(1 - p), 1 / k);
                    rows.SetQuantile(t, dist, i, Math.Max(1, (int)Math.Round(hours * 10)));
                }
            // KS on the winter, each duration spread across its whole hour (the record is hourly).
            double Ks(Func<int, double, double> cdf)
            {
                var u = new List<double>();
                foreach (var group in positive.Where(s => Winter(s.Third)).GroupBy(s => (s.Third, s.Hours)))
                {
                    int m = group.Count(), j = 0;
                    foreach (var s in group) u.Add(cdf(s.Third, Math.Max(0.01, s.Hours - 0.5 + (j++ + 0.5) / m)));
                }
                return KsUniform(u);
            }
            double ks = Ks((t, x) => 1 - Math.Exp(-Math.Pow(x / scale[t], k)));
            rep.Ks[name] = ks;
            rep.KsCount[name] = winter.Count;
            if (ks < KsCritical(winter.Count) || winter.Count < EffectMin) return;

            // WX-31 v3 (owner 2026-11-08): a Weibull that fails its KS test (at the 1 % point for its own sample, so a fit the
            // data reject is never kept however many it has) gives way to the record's own shape (durations over their third's
            // median, pooled over the winter, read at the table's points), each third's table scaled to the same mean as before.
            // Found at Jackson NH, whose Clearings WX-18 v2 cuts at 36 h: a Weibull can't hold that pile.
            var sorted = winter.OrderBy(x => x).ToList();
            var shape = WeatherRows.QuantilePoints.Select(p => EmpiricalQuantile(sorted, p / 10_000.0)).ToArray();
            double shapeMean = ShapeMean(shape);
            var unit = new double[36];
            for (int t = 0; t < 36; t++)
            {
                unit[t] = scale[t] * gamma / Math.Max(1e-9, shapeMean);   // hours per unit of the shape
                for (int i = 0; i < WeatherRows.QuantileCount; i++) rows.SetQuantile(t, dist, i, Math.Max(1, (int)Math.Round(unit[t] * shape[i] * 10)));
            }
            rep.Ks[name] = Ks((t, x) => ShapeCdf(shape, x / unit[t]));
            rep.Fits[$"{name}.weibullKs"] = ks;
            rep.Levels[$"{name}.shape"] = "record";
        }

        static void Patterns(WeatherRows rows, CalibrationReport rep, RecordStates states)
        {
            var cycles = states.Cycles.Where(c => !c.Truncated).ToList();
            var mix = new double[3][];
            for (int p = 0; p < 3; p++)
                mix[p] = Ladder(rep, $"pattern.mix.{p}", GroupBy(cycles, c => c.Third, c => c.Pattern == p ? 1.0 : 0.0), Mean, MeanN0, ScaleMin, 1 / 3.0, quiet: p > 0);
            for (int t = 0; t < 36; t++)
            {
                var w = new double[3];
                for (int p = 0; p < 3; p++) w[p] = Math.Max(0, mix[p][t]);
                var share = ToPermille(w);
                for (int p = 0; p < 3; p++) rows.PatternMix[t * 3 + p] = share[p];
            }

            // Persistence per season half: ρ = (same − Σπ²) ÷ (1 − Σπ²), clamped 0–0.7.
            for (int half = 0; half < 2; half++)
            {
                int same = 0, pairs = 0;
                var counts = new double[3];
                var lagX = new List<double>(); var lagY = new List<double>();
                for (int c = 0; c + 1 < cycles.Count; c++)
                {
                    int month = cycles[c].Third / 3;
                    bool inHalf = half == 0 ? month >= 1 && month <= 3 : month >= 4 && month <= 6;
                    if (!inHalf || cycles[c + 1].LabelYear != cycles[c].LabelYear) continue;
                    pairs++;
                    if (cycles[c].Pattern == cycles[c + 1].Pattern) same++;
                    counts[cycles[c].Pattern]++;
                    lagX.Add(cycles[c].Storminess); lagY.Add(cycles[c + 1].Storminess);
                }
                double sum2 = 0, total = Math.Max(1, counts.Sum());
                foreach (double c in counts) sum2 += (c / total) * (c / total);
                double rho = pairs < 20 || sum2 >= 1 ? 0.2 : ((double)same / pairs - sum2) / (1 - sum2);
                rho = Math.Max(0, Math.Min(0.7, rho));
                rows.PatternPersistence[half] = (int)Math.Round(rho * 1000);
                rep.Fits[$"pattern.persistence.{half}"] = rho;
                rep.Fits[$"pattern.lag1.{half}"] = lagX.Count < 3 ? 0 : Correlation(lagX, lagY);
                if (pairs < 20) rep.Levels[$"pattern.persistence.{half}"] = "default";
            }

            // Each third's matrix from ρ and its mix, every entry ≥ 50 ‰.
            int dwell = 3;
            for (int t = 0; t < 36; t++)
            {
                double rho = rows.PatternPersistence[HalfOf(t / 3)] / 1000.0;
                for (int from = 0; from < 3; from++)
                {
                    var w = new double[3];
                    for (int to = 0; to < 3; to++)
                    {
                        double pi = rows.PatternMix[t * 3 + to] / 1000.0;
                        w[to] = (1 - rho) * pi + (to == from ? rho : 0);
                    }
                    var row = FloorPermille(w, 50);
                    for (int to = 0; to < 3; to++) rows.PatternMatrix[(t * 3 + from) * 3 + to] = row[to];
                    if (Winter(t))
                    {
                        double stay = row[from] / 1000.0;
                        int run = stay <= 0 ? 1 : (int)Math.Ceiling(Math.Log(0.01) / Math.Log(stay));
                        dwell = Math.Max(dwell, run);
                    }
                }
            }
            rows.DwellCap = Math.Min(30, dwell);

            // Per-pattern arrival histograms where the winter has 30 storms or more under the pattern.
            for (int p = 0; p < 3; p++)
            {
                var under = states.Storms.Where(st => Winter(st.Third) && st.Pattern == p).ToList();
                if (under.Count < EffectMin) { rep.Levels[$"pattern.octants.{p}"] = "third"; continue; }
                var w = new double[8];
                foreach (var st in under) w[st.Octant]++;
                var share = ToPermille(w);
                for (int o = 0; o < 8; o++) rows.PatternOctants[p * 8 + o] = share[o];
            }
        }

        static void Anomaly(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, double[] tf, double[] offset, double[] fairShift,
                            double[] stormOffset, double[] firstHalf, double[] secondHalf, List<int>[] smoothed, int years)
        {
            int nd = b.DayCount;
            // Hourly residual: offset net of the state's (the Fair shift, the third's Building/Clearing, the storm's own half).
            var e = new double[nd];
            for (int d = 0; d < nd; d++)
            {
                double sum = 0; int n = 0;
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + h;
                    if (double.IsNaN(offset[k])) continue;
                    int t = states.HourThird(k);
                    double o = offset[k] - fairShift[t];
                    switch (states.State[k])
                    {
                        case WeatherRows.Building: o -= rows.BuildingOffset[t] / 10.0; break;
                        case WeatherRows.Clearing: o -= rows.ClearingOffset[t] / 10.0; break;
                        case WeatherRows.Storm:
                            int i = states.StormOf[k];
                            var st = states.Storms[i];
                            o -= k < st.Start + Math.Max(1, (st.End - st.Start) / 2) ? firstHalf[i] : secondHalf[i];
                            break;
                    }
                    sum += o; n++;
                }
                e[d] = n == 0 ? double.NaN : sum / n;
            }

            // The season character's raw anomaly: each winter's (October–April) mean residual.
            var winterMean = new Dictionary<int, double>();
            foreach (int y in b.LabelYears())
            {
                var days = Enumerable.Range(0, nd).Where(d => states.DayLabelYear[d] == y && states.DayOfLabelYear[d] < 212 && !double.IsNaN(e[d])).ToList();
                winterMean[y] = days.Count == 0 ? 0 : days.Average(d => e[d]);
            }
            double Char(int d) => states.DayOfLabelYear[d] < 212 && winterMean.TryGetValue(states.DayLabelYear[d], out var v) ? v : 0;

            // The pattern lean: the mean residual under each pattern over the winter, neutral when mix-weighted.
            var pattern = new int[nd];
            for (int d = 0; d < nd; d++)
            {
                int k = d * 24 + 12;
                int c = CycleAt(states, k);
                pattern[d] = c < 0 ? WeatherRows.Zonal : states.Cycles[c].Pattern;
            }
            var leanSum = new double[3]; var leanN = new int[3];
            for (int d = 0; d < nd; d++)
            {
                if (double.IsNaN(e[d]) || !Winter(states.DayThird[d])) continue;
                leanSum[pattern[d]] += e[d] - Char(d);
                leanN[pattern[d]]++;
            }
            var lean = new double[3];
            int leanTotal = Math.Max(1, leanN.Sum());
            for (int p = 0; p < 3; p++) lean[p] = leanN[p] == 0 ? 0 : leanSum[p] / leanN[p];
            double leanCentre = 0;
            for (int p = 0; p < 3; p++) leanCentre += lean[p] * leanN[p] / leanTotal;
            for (int p = 0; p < 3; p++) { lean[p] -= leanCentre; rows.PatternLean[p] = Tenths(lean[p]); }

            // Residuals, then per third (smoothed): lag-1 persistence and spread, on days with no storm hour. A storm's own
            // offset already took most of its days' departure out, so pooling them shrank the free anomaly (owner's fit review,
            // 2026-11-08: Fair weather swung too little and cold snaps fell 3–8 °F short at every site); storms keep their
            // share of the anomaly through the played correction (StormTemperature).
            var r = new double[nd];
            for (int d = 0; d < nd; d++) r[d] = double.IsNaN(e[d]) ? double.NaN : e[d] - Char(d) - (Winter(states.DayThird[d]) ? lean[pattern[d]] : 0);
            var free = new bool[nd];
            for (int d = 0; d < nd; d++) free[d] = true;
            for (int k = 0; k < Math.Min(states.State.Length, nd * 24); k++) if (states.State[k] == WeatherRows.Storm) free[k / 24] = false;
            var pairs = new List<(double, double)>[36];
            var values = Groups();
            for (int t = 0; t < 36; t++) pairs[t] = new List<(double, double)>();
            var inThird = new bool[36, nd];
            for (int t = 0; t < 36; t++) foreach (int d in smoothed[t]) inThird[t, d] = true;
            for (int t = 0; t < 36; t++)
                foreach (int d in smoothed[t])
                {
                    if (double.IsNaN(r[d]) || !free[d]) continue;
                    values[t].Add(r[d]);
                    if (d + 1 < nd && inThird[t, d + 1] && free[d + 1] && !double.IsNaN(r[d + 1]) && b.Days[d + 1] - b.Days[d] <= 2) pairs[t].Add((r[d], r[d + 1]));
                }
            var phi = LadderStat(rep, "anomaly.lag1", set =>
            {
                var all = set.SelectMany(t => pairs[t]).ToList();
                return (all.Count < 3 ? double.NaN : Correlation(all.Select(p => p.Item1).ToList(), all.Select(p => p.Item2).ToList()), all.Count);
            }, MeanN0, ScaleMin, 0.6);
            var sd = Ladder(rep, "anomaly.spread", values, Std, ScaleN0, ScaleMin, 5);
            for (int t = 0; t < 36; t++)
            {
                double daily = Math.Max(0.05, Math.Min(0.98, phi[t]));
                double hourly = Math.Pow(daily, 1 / 24.0);
                rows.AnomalyPhiHourQ32[t] = (long)Math.Round(hourly * WeatherMath.Q32One);
                // The daily mean of an hourly AR(1) varies less than its hours: σ_hour = σ_day ÷ √f.
                double f = 0;
                for (int i = 0; i < 24; i++) for (int j = 0; j < 24; j++) f += Math.Pow(hourly, Math.Abs(i - j));
                f /= 576;
                rows.AnomalySpread[t] = Tenths(sd[t] / Math.Sqrt(Math.Max(0.05, f)));
            }
            var standard = new List<double>();
            for (int d = 0; d < nd; d++)
            {
                int t = states.DayThird[d];
                if (!double.IsNaN(r[d]) && free[d] && Winter(t) && sd[t] > 0.05) standard.Add(r[d] / sd[t]);
            }
            AnomalyShape(rows, rep, standard);

            // The raw season character (shrunk later by ShrinkCharacter).
            var liquid = new Dictionary<int, double>();
            foreach (int y in b.LabelYears()) liquid[y] = 0;
            for (int d = 0; d < nd; d++)
                if (states.DayOfLabelYear[d] < 212 && !double.IsNaN(b.Prcp[d]) && liquid.ContainsKey(states.DayLabelYear[d])) liquid[states.DayLabelYear[d]] += b.Prcp[d];
            double meanLiquid = Math.Max(1e-6, liquid.Values.Average());
            var ys = liquid.Keys.OrderBy(y => y).ToList();
            for (int w = 0; w < WeatherRows.Winters; w++)
            {
                int y = ys[w % ys.Count];
                rows.CharacterShare[w] = (int)Math.Round(liquid[y] / meanLiquid * 1000);
                rows.CharacterAnomaly[w] = Tenths(winterMean[y]);
            }
            rep.Fits["character.recordCv"] = Std(liquid.Values.ToList()) / meanLiquid;
            rep.Fits["character.recordTempSd"] = Std(winterMean.Values.ToList());
        }

        static int CycleAt(RecordStates s, int k)
        {
            // The cycle whose span [FairStart, next FairStart) holds hour k (binary search).
            int lo = 0, hi = s.Cycles.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (s.Cycles[mid].FairStart <= k) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found;
        }

        static void Lapse(WeatherRows rows, CalibrationReport rep, WeatherSourceRecord src, RecordStates states, BlendedRecord b)
        {
            var pixels = src.Lattice.Where(p => p.Has("tmax") && p.Has("tmin")).ToList();
            var index = pixels.Select(p => { var map = new Dictionary<int, int>(); for (int i = 0; i < p.Days.Count; i++) map[p.Days[i]] = i; return map; }).ToList();
            var centres = new List<(double X, double Y)>();
            for (int i = 0; i < src.Lattice.Count; i++) if (src.Lattice[i].Has("tmax") && src.Lattice[i].Has("tmin")) centres.Add(src.LatticeCentres[i]);
            double elevRange = pixels.Count == 0 ? 0 : pixels.Max(p => p.ElevationMetres) - pixels.Min(p => p.ElevationMetres);
            bool usable = pixels.Count >= 6 && elevRange >= 100;
            var day = Groups(); var nightL = Groups(); var dew = Groups(); var grad = Groups();
            int floorNights = 0, nights = 0;
            if (usable)
            {
                double x0 = centres.Average(c => c.X), y0 = centres.Average(c => c.Y);
                for (int d = 0; d < b.DayCount; d++)
                {
                    int civil = b.Days[d];
                    var xs = new List<double[]>(); var tmax = new List<double>(); var tmin = new List<double>(); var dp = new List<double>(); var pr = new List<double>();
                    for (int i = 0; i < pixels.Count; i++)
                    {
                        if (!index[i].TryGetValue(civil, out int j) || pixels[i].RejectedDays.Contains(civil)) continue;
                        double a = pixels[i].Series("tmax")[j], c = pixels[i].Series("tmin")[j];
                        if (double.IsNaN(a) || double.IsNaN(c)) continue;
                        xs.Add(new[] { 1, pixels[i].ElevationMetres, (centres[i].X - x0) / 1000, (centres[i].Y - y0) / 1000 });
                        tmax.Add(a); tmin.Add(c);
                        dp.Add(pixels[i].Has("vp") ? Psychrometrics.DewPointC(pixels[i].Series("vp")[j]) : double.NaN);
                        pr.Add(pixels[i].Has("prcp") ? pixels[i].Series("prcp")[j] : double.NaN);
                    }
                    if (xs.Count < 6) continue;
                    const double perHundredFt = 1.8 * 30.48 * 100;   // °C per m → 0.01 °F per 100 ft
                    var targets = new List<int>();
                    foreach (int t in ThirdsHolding(states.DayOfLabelYear[d])) targets.Add(t);
                    double lapseDay = -LeastSquares.Solve(xs, tmax)[1] * perHundredFt;
                    double lapseNight = -LeastSquares.Solve(xs, tmin)[1] * perHundredFt;
                    nights++;
                    if (lapseNight < 10) floorNights++;
                    foreach (int t in targets) { day[t].Add(lapseDay); nightL[t].Add(lapseNight); }
                    if (dp.All(v => !double.IsNaN(v)))
                    {
                        double lapseDew = -LeastSquares.Solve(xs, dp)[1] * perHundredFt;
                        foreach (int t in targets) dew[t].Add(lapseDew);
                    }
                    double meanP = pr.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average();
                    if (meanP >= WeatherStates.WetDayMm && pr.All(v => !double.IsNaN(v)))
                    {
                        double g = LeastSquares.Solve(xs, pr.Select(v => v / meanP).ToList())[1] * 30.48 * 1000;   // ‰ per 100 ft
                        foreach (int t in targets) grad[t].Add(g);
                        rep.GradientDays++;
                    }
                }
                rep.LapseDays = nights;
            }
            else rep.LimitedReasons.Add("lattice: too few pixels or too little relief for a lapse (defaults used)");
            rep.NightLapseFloorShare = nights == 0 ? 0 : (double)floorNights / nights;
            var ld = Ladder(rep, "lapse.day", day, Mean, MeanN0, ScaleMin, 36);
            var ln = Ladder(rep, "lapse.night", nightL, Mean, MeanN0, ScaleMin, 20);
            var lw = Ladder(rep, "lapse.dew", dew, Mean, MeanN0, ScaleMin, 20);
            var lg = Ladder(rep, "precip.gradient", grad, Mean, MeanN0, ScaleMin, 8);
            for (int t = 0; t < 36; t++)
            {
                rows.LapseDay[t] = (int)Math.Round(Math.Max(10, Math.Min(55, ld[t])));
                rows.LapseNight[t] = (int)Math.Round(Math.Max(10, Math.Min(55, ln[t])));
                rows.DewLapse[t] = (int)Math.Round(Math.Max(0, Math.Min(55, lw[t])));
                rows.PrecipGradient[t] = (int)Math.Round(Math.Max(0, Math.Min(20, lg[t])));
            }
        }

        static void Targets(WeatherRows rows, BlendedRecord b, RecordStates states, double[] tf, int years)
        {
            var tSum = new double[36]; var tN = new int[36];
            var liquid = new double[36]; var snow = new double[36];
            var storms = new int[36]; var thaw = new int[36]; var rain = new int[36];
            for (int k = 0; k < b.HourCount; k++)
            {
                int t = states.HourThird(k);
                if (!double.IsNaN(tf[k])) { tSum[t] += tf[k]; tN[t]++; }
            }
            for (int d = 0; d < b.DayCount; d++)
            {
                int t = states.DayThird[d];
                if (b.DayMissing(d)) continue;
                liquid[t] += b.Prcp[d];
                if (Psychrometrics.CToF(b.Tmax[d]) > 40) thaw[t]++;
                double rainMm = 0;
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + b.LagHours + h;
                    if (k >= b.HourCount || float.IsNaN(b.P[k]) || b.P[k] <= 0 || double.IsNaN(tf[k])) continue;
                    double share = Psychrometrics.SnowShare(Psychrometrics.WetBulbF(tf[k], float.IsNaN(b.Rh[k]) ? 90 : b.Rh[k]));
                    snow[t] += b.P[k] * share;
                    rainMm += b.P[k] * (1 - share);
                }
                if (rainMm >= WeatherStates.WetDayMm) rain[t]++;
            }
            foreach (var st in states.Storms) storms[st.Third]++;
            for (int t = 0; t < 36; t++)
            {
                rows.TargetMeanTemp[t] = tN[t] == 0 ? 0 : Tenths(tSum[t] / tN[t]);
                rows.TargetLiquid[t] = (int)Math.Round(liquid[t] / years * MmToHundredthsIn);
                rows.TargetSnowLiquid[t] = (int)Math.Round(snow[t] / years * MmToHundredthsIn);
                rows.TargetStorms[t] = (int)Math.Round(storms[t] * 100.0 / years);
                rows.TargetThawDays[t] = (int)Math.Round(thaw[t] * 100.0 / years);
                rows.TargetRainDays[t] = (int)Math.Round(rain[t] * 100.0 / years);
            }
        }

        /// <summary>Face hours from October 1 to the given number of real days after it, at the tuning's compression (WX-21).</summary>
        public static int FaceHoursFor(WeatherTuning tuning, int realDays) => (int)((long)realDays * 24 * tuning.FaceDays / tuning.CalendarDays);

        /// <summary>
        /// WX-19 v2: play the rows with the character switched off over the seeds; pull each winter's share toward 1.0 by
        /// √(1 − σ²sampling ÷ σ²record), and each temperature anomaly likewise.
        /// </summary>
        static void ShrinkCharacter(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, WeatherTuning tuning, int seeds, int years)
        {
            var off = rows.Clone();
            for (int w = 0; w < WeatherRows.Winters; w++) { off.CharacterShare[w] = 1000; off.CharacterAnomaly[w] = 0; }
            var gen = new WeatherGenerator(off, tuning);
            int hours = FaceHoursFor(tuning, 212);
            var totals = new List<double>(); var temps = new List<double>();
            var h = new WeatherHour();
            for (int s = 0; s < seeds; s++)
            {
                gen.Reset(SeedFor(s), 2000);
                long liquid = 0; double tsum = 0;
                for (int i = 0; i < hours; i++) { gen.Step(ref h); liquid += h.Liquid; tsum += h.Temp; }
                totals.Add(liquid);
                temps.Add(tsum / hours / 10.0);
            }
            double mean = Math.Max(1e-6, totals.Average());
            double cvSampling = Std(totals) / mean;
            double cvRecord = rep.Fits["character.recordCv"];
            double fShare = cvRecord <= 0 ? 0 : Math.Sqrt(Math.Max(0, 1 - cvSampling * cvSampling / (cvRecord * cvRecord)));
            double sdSampling = Std(temps), sdRecord = rep.Fits["character.recordTempSd"];
            double fTemp = sdRecord <= 0 ? 0 : Math.Sqrt(Math.Max(0, 1 - sdSampling * sdSampling / (sdRecord * sdRecord)));
            for (int w = 0; w < WeatherRows.Winters; w++)
            {
                rows.CharacterShare[w] = (int)Math.Round(1000 + (rows.CharacterShare[w] - 1000) * fShare);
                rows.CharacterAnomaly[w] = (int)Math.Round(rows.CharacterAnomaly[w] * fTemp);
            }
            // WX-34 multiplies storm totals by √share and Fair spells by 1/√share; over the table (and its ±10 % jitter) neither
            // averages exactly 1, so divide each expectation out of its tables: the character spreads seasons, never shifts them.
            int lo = rows.CharacterShare.Min(), hi = rows.CharacterShare.Max();
            double e1 = 0, e2 = 0; int en = 0;
            foreach (int share in rows.CharacterShare)
                for (int j = -100; j <= 100; j += 10)
                {
                    double v = Math.Max(lo, Math.Min(hi, share * (1000 + j) / 1000.0)) / 1000.0;
                    e1 += Math.Sqrt(v); e2 += 1 / Math.Sqrt(Math.Max(1e-3, v)); en++;
                }
            e1 /= en; e2 /= en;
            for (int t = 0; t < 36; t++)
            {
                rows.StormMedian[t] = (int)Math.Round(rows.StormMedian[t] / e1);
                for (int i = 0; i < WeatherRows.QuantileCount; i++)
                {
                    rows.SetQuantile(t, WeatherRows.StormTotal, i, Math.Max(1, (int)Math.Round(rows.Quantile(t, WeatherRows.StormTotal, i) / e1)));
                    rows.SetQuantile(t, 0, i, Math.Max(1, (int)Math.Round(rows.Quantile(t, 0, i) / e2)));
                }
            }
            rep.Fits["character.meanSqrtShare"] = e1;
            rep.Fits["character.meanInverseSqrtShare"] = e2;
            rep.Fits["character.samplingCv"] = cvSampling;
            rep.Fits["character.shareShrink"] = fShare;
            rep.Fits["character.samplingTempSd"] = sdSampling;
            rep.Fits["character.tempShrink"] = fTemp;
            // A shrink of 0 is the floor: the game's own sampling already spreads the seasons as widely as the record did.
            rep.Fits["clamp.characterShareFloor"] = fShare <= 0 ? 1 : 0;
            rep.Fits["clamp.characterTempFloor"] = fTemp <= 0 ? 1 : 0;
        }

        /// <summary>
        /// WX-25 v3 (owner 2026-11-08): the record's storms are warmest where the liquid is, two ways the generator didn't
        /// have, which made its snow 13–21 % too heavy. Between storms, a storm's offset rises with its size: the slope on the
        /// normal score of its total, net of its pattern and quarter as the generator draws it, with each third's means taken
        /// out; each third's mean moves down by the slope times the hour-weighted mean score, so the storm hours' mean stays.
        /// Within a storm, heavier hours run warmer: the record's liquid-weighted warmth over each storm's own mean, divided by
        /// how much the generator's hours vary about their storm's mean (its 1 : 2 : 1 shape times the uniform burst), so the
        /// generator makes the same warmth from its own, smoother hours.
        /// </summary>
        static void StormWarmth(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, double[] offset, double[] stormOffset,
                                double[] z, WeatherTuning tuning)
        {
            var winter = Enumerable.Range(0, states.Storms.Count).Where(i => Winter(states.Storms[i].Third) && !double.IsNaN(stormOffset[i])).ToList();
            double sxy = 0, sxx = 0, zh = 0, hours = 0;
            foreach (var g in winter.GroupBy(i => states.Storms[i].Third))
            {
                double zm = g.Average(i => z[i]), om = g.Average(i => stormOffset[i]);
                foreach (int i in g) { sxy += (z[i] - zm) * (stormOffset[i] - om); sxx += (z[i] - zm) * (z[i] - zm); }
            }
            foreach (int i in winter) { zh += states.Storms[i].Hours * z[i]; hours += states.Storms[i].Hours; }
            double beta = winter.Count < EffectMin || sxx <= 0 ? 0 : Math.Max(-3, Math.Min(3, sxy / sxx));
            double zBar = hours <= 0 ? 0 : zh / hours;
            rows.StormSizeWarm = Tenths(beta);
            for (int t = 0; t < 36; t++) rows.StormOffsetMean[t] -= Tenths(rows.StormSizeWarm / 10.0 * zBar);
            rep.Fits["storm.sizeWarmFPerSd"] = beta;
            rep.Fits["storm.sizeHourMeanScore"] = zBar;

            // And bigger storms vary less: weighted by liquid, the record's storms varied 3.7 °F at Stevens Pass where storm by
            // storm they varied 4.9 (the big ones bring their own air). The size of a storm's departure from the line, |r|, is
            // fitted against the score: E|r| = c (1 + δ z), so δ is the slope over the intercept.
            double n = 0, sz = 0, sr = 0, szz = 0, szr = 0;
            foreach (var g in winter.GroupBy(i => states.Storms[i].Third))
            {
                double zm = g.Average(i => z[i]), om = g.Average(i => stormOffset[i]);
                foreach (int i in g)
                {
                    double r = Math.Abs(stormOffset[i] - om - beta * (z[i] - zm));
                    n++; sz += z[i]; sr += r; szz += z[i] * z[i]; szr += z[i] * r;
                }
            }
            double slope = n * szz - sz * sz <= 0 ? 0 : (n * szr - sz * sr) / (n * szz - sz * sz), intercept = (sr - slope * sz) / Math.Max(1, n);
            double delta = winter.Count < EffectMin || intercept <= 0 ? 0 : Math.Max(-0.6, Math.Min(0.6, slope / intercept));
            rows.StormSizeSpread = (int)Math.Round(delta * 1000);
            rep.Fits["storm.sizeSpreadPerSd"] = delta;

            double warm = 0, liquid = 0;
            foreach (int i in winter)
            {
                var st = states.Storms[i];
                double hs = 0, hn = 0, ls = 0, ln = 0;
                for (int k = st.Start; k < st.End && k < b.HourCount; k++)
                {
                    if (double.IsNaN(offset[k])) continue;
                    hs += offset[k]; hn++;
                    if (!float.IsNaN(b.P[k]) && b.P[k] > 0) { ls += b.P[k] * offset[k]; ln += b.P[k]; }
                }
                if (hn == 0 || ln <= 0) continue;
                warm += ln * (ls / ln - hs / hn); liquid += ln;
            }
            double within = liquid <= 0 ? 0 : warm / liquid;
            // A generator hour over its storm's mean hour is S × B ÷ E[S × B]: shape S is 1 for half the hours and 2 for the
            // other half, burst B uniform; its variance is E[S²] E[B²] ÷ (E[S] E[B])² − 1.
            double bMean = (tuning.BurstMin + tuning.BurstMax) / 2.0, bVar = Math.Pow(tuning.BurstMax - tuning.BurstMin, 2) / 12;
            double variance = 2.5 * (bVar + bMean * bMean) / (2.25 * bMean * bMean) - 1;
            double gamma = Math.Max(-8, Math.Min(8, within / Math.Max(0.02, variance)));
            rows.StormIntensityWarm = Tenths(gamma);
            rep.Fits["storm.withinWarmF"] = within;
            rep.Fits["storm.intensityWarmF"] = gamma;
        }

        /// <summary>
        /// WX-25 v3: the storm temperatures, played. Two rounds over fixed seeds, month by month. The spread is set so played
        /// storms differ from each other as much as the record's do (their mean temperatures about their third's mean): what
        /// the anomaly, the season, the pattern and the size term already vary comes off it. It was the record's storm-to-
        /// storm spread, which already holds the anomaly, so the generator counted the anomaly twice (storms 36–58 % too
        /// varied). Where even a spread of zero leaves the storms too varied (Olympic Valley: the anomaly alone varied them
        /// more than the record's storms vary), the storm keeps less of the anomaly. Then the storm mean moves by what the
        /// played wet hours' liquid-weighted wet bulb misses the record's by, or until the played liquid's rain share is the
        /// record's, whichever leaves the storms colder (WX-25 v5, owner 2026-11-09: "err on the side of more snow, for tycoon
        /// fun"). The record's storms fall mild with a long cold tail and the game's are symmetric, so the two targets part:
        /// matching the rain share everywhere warmed the storms enough to thin the pack at four sites, and matching the wet
        /// bulb alone rained 5–9 points too little at Stevens Pass. A month whose record rains under 5 % or over 95 % of its
        /// liquid tells too little about the rain share, so there the wet bulb decides.
        /// </summary>
        static void StormTemperature(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, double[] tf, WeatherTuning tuning, int seeds)
        {
            if (seeds <= 0) return;
            // The wet bulb, not the air: it decides snow or rain and how deep (WX-03 v2, SN-05). At the same air temperature
            // Stevens Pass's played wet bulb ran 0.25 °F cold and its snow 7 % deep.
            static double WetBulb(double t, double d) => Psychrometrics.WetBulbF(t, Psychrometrics.RelativeHumidity(t, Math.Min(t, d)));
            static double Rain(double t, double d) => 1 - Psychrometrics.SnowShare(WetBulb(t, d));
            var recWet = new double[12]; var recLiquid = new double[12]; var recRain = new double[12];
            for (int k = 0; k < b.HourCount; k++)
            {
                if (double.IsNaN(tf[k]) || float.IsNaN(b.P[k]) || b.P[k] <= 0) continue;
                int m = states.HourThird(k) / 3;
                double dew = float.IsNaN(b.Td[k]) ? tf[k] - 2 : Psychrometrics.CToF(b.Td[k]);
                recWet[m] += b.P[k] * WetBulb(tf[k], dew); recLiquid[m] += b.P[k]; recRain[m] += b.P[k] * Rain(tf[k], dew);
            }
            var recStorms = new List<(int Third, double Temp)>();
            foreach (var st in states.Storms)
            {
                double sum = 0; int n = 0;
                for (int k = st.Start; k < st.End && k < b.HourCount; k++) if (!double.IsNaN(tf[k])) { sum += tf[k]; n++; }
                if (n > 0) recStorms.Add((st.Third, sum / n));
            }
            var recVar = StormVariance(recStorms, out var recCount);

            // The size scale's mean square over the normal scores: the storm's own draw and kept anomaly vary by it.
            double scale2 = 0, mass = 0;
            for (int i = -600; i <= 600; i++)
            {
                double zz = i / 100.0, p = Math.Exp(-zz * zz / 2);
                double m = Math.Max(0.25, Math.Min(2, 1 + rows.StormSizeSpread / 1000.0 * zz));
                scale2 += p * m * m; mass += p;
            }
            scale2 /= mass;
            int hours = WeatherGenerator.HoursInYear(tuning);
            var h = new WeatherHour();
            for (int round = 0; round < 3; round++)
            {
                var gen = new WeatherGenerator(rows, tuning);
                var wet = new double[12]; var wetLiquid = new double[12];
                var wetHours = Enumerable.Range(0, 12).Select(_ => new List<(double T, double D, double L, bool Storm)>()).ToArray();
                var storms = new List<(int Third, double Temp)>();
                var anomalies = new List<(int Third, double Temp)>();
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(3000 + s), 2000);
                    int third = -1, n = 0;
                    double sum = 0, anomaly = 0;
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref h);
                        if (h.Liquid > 0)
                        {
                            wet[h.Third / 3] += h.Liquid * WetBulb(h.Temp / 10.0, h.Dew / 10.0); wetLiquid[h.Third / 3] += h.Liquid;
                            wetHours[h.Third / 3].Add((h.Temp / 10.0, h.Dew / 10.0, h.Liquid, h.State == WeatherRows.Storm));
                        }
                        if (h.State == WeatherRows.Storm) { if (n == 0) third = h.Third; sum += h.Temp / 10.0; anomaly += gen.Anomaly / 100.0; n++; }
                        else if (n > 0) { storms.Add((third, sum / n)); anomalies.Add((third, anomaly / n)); sum = 0; anomaly = 0; n = 0; }
                    }
                }
                var gameVar = StormVariance(storms, out _);
                var anomalyVar = StormVariance(anomalies, out _);
                for (int m = 0; m < 12; m++)
                {
                    if (recCount[m] < ScaleMin || recLiquid[m] <= 0 || wetLiquid[m] <= 0) continue;
                    double wetMiss = wet[m] / wetLiquid[m] - recWet[m] / recLiquid[m], recShare = recRain[m] / recLiquid[m];
                    double shift = Bound(rep, "clamp.stormTempShift", -5, 5, -wetMiss);
                    if (recShare >= 0.05 && recShare <= 0.95)
                    {
                        // The played wet hours' rain share were their storm hours shifted by d (the air and dew point move
                        // together, so the depression and the flurries between storms stay): rising in d, so halve to it.
                        var v = wetHours[m];
                        double Share(double d)
                        {
                            double r = 0, l = 0;
                            foreach (var x in v) { double e = x.Storm ? d : 0; r += x.L * Rain(x.T + e, x.D + e); l += x.L; }
                            return l <= 0 ? 0 : r / l;
                        }
                        double lo = -5, hi = 5, rainShift;
                        if (Share(lo) >= recShare) rainShift = lo;
                        else if (Share(hi) <= recShare) rainShift = hi;
                        else
                        {
                            for (int it = 0; it < 30; it++) { double mid = (lo + hi) / 2; if (Share(mid) < recShare) lo = mid; else hi = mid; }
                            rainShift = (lo + hi) / 2;
                        }
                        shift = Math.Min(shift, rainShift);   // the colder of the two: err toward snow
                        rep.Fits[$"storm.rainShareMiss.{m}"] = Share(0) - recShare;
                    }
                    rep.Fits[$"storm.wetBulbMiss.{m}"] = wetMiss;
                    // What the storms' own draw and the anomaly may share: the record's variance less the rest the game varies
                    // (the climate under the storm, its front, the size term, the season, the pattern).
                    double own = 0, kept = 0;
                    for (int t = 3 * m; t < 3 * m + 3; t++) { own += Math.Pow(rows.StormOffsetSpread[t] / 10.0, 2) / 3; kept += rows.StormAnomalyWeight[t] / 1000.0 / 3; }
                    double a = anomalyVar[m], rest = gameVar[m] - scale2 * (own + kept * kept * a);
                    double share = (recVar[m] - rest) / scale2;
                    // The anomaly keeps its full weight while the spread can take the difference; only past that is it weakened.
                    double weight = share >= a ? 1 : Math.Sqrt(Math.Max(0, share) / Math.Max(1e-6, a));
                    double target = Math.Max(0, share - weight * weight * a);
                    for (int t = 3 * m; t < 3 * m + 3; t++)
                    {
                        rows.StormOffsetMean[t] += Tenths(shift);
                        double spread2 = Math.Pow(rows.StormOffsetSpread[t] / 10.0, 2);
                        // Scaled so the month's mean is the target (the thirds keep their order), or set flat from nothing.
                        double next = own > 1e-6 ? spread2 * target / own : target;
                        rows.StormOffsetSpread[t] = Tenths(Math.Sqrt(next));
                        rows.StormAnomalyWeight[t] = (int)Math.Round(weight * 1000);
                    }
                    rep.Fits[$"storm.warmShift.{m}"] = (rep.Fits.TryGetValue($"storm.warmShift.{m}", out var prior) ? prior : 0) + shift;
                    rep.Fits[$"storm.varianceMiss.{m}"] = gameVar[m] - recVar[m];
                    rep.Fits[$"storm.anomalyWeight.{m}"] = weight;
                }
            }
        }

        /// <summary>The running liquid's e-folding (calendar days) for WX-25 v5's wet lean, and the lean's bound (°F per spread).</summary>
        const double WetDays = 7, WetLeanMax = 5;

        /// <summary>
        /// WX-25 v5 (owner 2026-11-09: Tahoe, Winter Park and Snowshoe melted out early): a wet spell's temperature. In the record
        /// a wet week ran colder than the game's at five of seven sites (Tahoe's snowy winters are its cold ones: its days
        /// against the week's running liquid, r −0.25 where the game's was −0.09), so the game's warm spells fell on its deep
        /// packs. The running liquid (e-folding <see cref="WetDays"/> calendar days), about its month and state's mean in its
        /// spreads (so each state keeps the record's own temperature), leans every hour by <see cref="WeatherRows.WetLean"/>
        /// per spread. The lean is played over fixed seeds until
        /// that correlation, December–March, is the record's (a secant: three plays); the anomaly gives up the lean's variance,
        /// so the days keep the record's spread. Either sign: a site whose wet weeks run mild keeps them mild.
        /// </summary>
        static void WetLean(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, double[] tf, WeatherTuning tuning, int seeds)
        {
            if (seeds <= 0) return;
            double fh = (double)tuning.CalendarDays / tuning.FaceDays;
            rows.WetMemory = (int)Math.Round(1000 * Math.Exp(-fh / (WetDays * 24)));
            rows.WetLean = 0;

            // The record: each day's mean temperature and liquid.
            int nd = Math.Min(Math.Min(tf.Length, states.State.Length), b.HourCount) / 24;
            var rec = new List<(int Third, double T, double L)>();
            for (int d = 0; d < nd; d++)
            {
                double s = 0, l = 0; int n = 0;
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + h;
                    if (!double.IsNaN(tf[k])) { s += tf[k]; n++; }
                    if (!float.IsNaN(b.P[k])) l += b.P[k];
                }
                rec.Add((states.HourThird(d * 24 + 12), n < 20 ? double.NaN : s / n, l));
            }
            double recLink = DayLink(new[] { rec }, Math.Exp(-1 / WetDays), out _);

            var spread = rows.AnomalySpread.ToArray();
            int hours = WeatherGenerator.HoursInYear(tuning);
            var hour = new WeatherHour();
            (double Link, double DaySd) Play(bool stats)
            {
                var gen = new WeatherGenerator(rows, tuning);
                var runs = new List<(int Third, double T, double L)>[seeds];
                var wSum = new double[12 * WeatherRows.States]; var wSq = new double[12 * WeatherRows.States]; var wN = new long[12 * WeatherRows.States];
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(7000 + s), 2000);
                    var days = new List<(int Third, double T, double L)>();
                    long w = 0; double ts = 0, ls = 0; int n = 0;
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref hour);
                        w = (w * rows.WetMemory + (long)hour.Liquid * 1000 * (1000 - rows.WetMemory) + 500) / 1000;
                        int m = hour.Third / 3 * WeatherRows.States + hour.State;
                        wSum[m] += w; wSq[m] += (double)w * w; wN[m]++;
                        ts += hour.Temp / 10.0; ls += hour.Liquid; n++;
                        if (hour.HourOfDay == 23) { days.Add((hour.Third, ts / n, ls)); ts = 0; ls = 0; n = 0; }
                    }
                    runs[s] = days;
                }
                if (stats)
                    for (int t = 0; t < 36; t++)
                        for (int st = 0; st < WeatherRows.States; st++)
                        {
                            int m = t / 3 * WeatherRows.States + st, i = t * WeatherRows.States + st;
                            double mean = wSum[m] / Math.Max(1, wN[m]);
                            rows.WetMean[i] = (int)Math.Round(mean);
                            rows.WetSpread[i] = wN[m] < 2 ? 0 : (int)Math.Round(Math.Sqrt(Math.Max(0, wSq[m] / wN[m] - mean * mean)));
                        }
                return (DayLink(runs, Math.Exp(-fh / WetDays), out double daySd), daySd);
            }
            void Apply(double k)
            {
                rows.WetLean = Tenths(k);
                for (int t = 0; t < 36; t++)
                {
                    // The daily mean of an hourly AR(1) varies less than its hours (the anomaly's own factor, as fitted).
                    double phi = rows.AnomalyPhiHourQ32[t] / (double)WeatherMath.Q32One, f = 0;
                    for (int i = 0; i < 24; i++) for (int j = 0; j < 24; j++) f += Math.Pow(phi, Math.Abs(i - j));
                    f /= 576;
                    double kept = spread[t] / 10.0 * spread[t] / 10.0 - k * k / Math.Max(0.05, f);
                    rows.AnomalySpread[t] = Tenths(Math.Sqrt(Math.Max(0.25 * spread[t] / 10.0 * spread[t] / 10.0, kept)));
                }
            }

            var (r0, sd0) = Play(true);
            rep.Fits["wet.recordLink"] = recLink;
            rep.Fits["wet.gameLink"] = r0;
            double kPrev = 0, rPrev = r0, kNow = Math.Max(-WetLeanMax, Math.Min(WetLeanMax, (r0 - recLink) * sd0));
            for (int round = 0; round < 2 && kNow != kPrev; round++)
            {
                Apply(kNow);
                double r = Play(false).Link;
                rep.Fits[$"wet.linkMiss.{round}"] = r - recLink;
                if (Math.Abs(r - rPrev) < 1e-4) break;
                double next = Math.Max(-WetLeanMax, Math.Min(WetLeanMax, kNow + (recLink - r) * (kNow - kPrev) / (r - rPrev)));
                kPrev = kNow; rPrev = r; kNow = next;
            }
            Apply(kNow);
            rep.Fits["wet.lean"] = kNow;
            rep.Fits["clamp.wetLeanBound"] = Math.Abs(kNow) >= WetLeanMax ? 1 : 0;
            rep.Fits["clamp.wetSpreadFloorThirds"] = Enumerable.Range(0, 36).Count(t => rows.AnomalySpread[t] <= Tenths(0.5 * spread[t] / 10.0));
        }

        /// <summary>
        /// WX-25 v5: the correlation, December–March, of each day's temperature (about its third's mean) with the running
        /// liquid (each day keeps <paramref name="keep"/> of it), and the days' spread.
        /// </summary>
        static double DayLink(IEnumerable<List<(int Third, double T, double L)>> runs, double keep, out double daySd)
        {
            var mean = new double[36]; var count = new int[36];
            foreach (var run in runs) foreach (var x in run) if (!double.IsNaN(x.T) && x.Third >= 0 && x.Third < 36) { mean[x.Third] += x.T; count[x.Third]++; }
            for (int t = 0; t < 36; t++) mean[t] /= Math.Max(1, count[t]);
            var xs = new List<double>(); var ys = new List<double>();
            foreach (var run in runs)
            {
                double w = 0;
                foreach (var x in run)
                {
                    w = w * keep + x.L * (1 - keep);
                    if (double.IsNaN(x.T) || x.Third / 3 < 2 || x.Third / 3 > 5) continue;
                    xs.Add(w); ys.Add(x.T - mean[x.Third]);
                }
            }
            daySd = Std(ys);
            return Correlation(xs, ys);
        }

        /// <summary>
        /// WX-25 v6 (the weather review, 2026-11-09): the winter's cold side. Every correction after the anomaly's shape adds
        /// symmetric spread or a lean, and a game day spans three real ones, so the finished hours lost the record's cold
        /// days: December–February, the game's 1 % and 10 % hours ran 1.8 and 1.2 °F mild at all seven sites, its mean
        /// 0.2–0.7 °F warm. So, on the finished hours, three plays: each third's mean moves to the record's (its max and min
        /// together), and the shape is fitted again on December–February, each hour about its third's record mean in its
        /// third's spread, the record's quantile less the game's at each shape point's normal probability moving the point
        /// (kept non-decreasing), then centred: the mean belongs to the climate.
        /// </summary>
        static void ColdSide(WeatherRows rows, CalibrationReport rep, RecordStates states, double[] tf, WeatherTuning tuning, int seeds)
        {
            if (seeds <= 0) return;
            int n = WeatherRows.AnomalyShapePoints, mid = n / 2;
            double step = WeatherRows.AnomalyShapeStep / 1000.0;
            var sum = new double[36]; var count = new int[36];
            int nh = Math.Min(tf.Length, states.State.Length);
            for (int k = 0; k < nh; k++) if (!double.IsNaN(tf[k])) { int t = states.HourThird(k); sum[t] += tf[k]; count[t]++; }
            var mean = new double[36];
            for (int t = 0; t < 36; t++) mean[t] = sum[t] / Math.Max(1, count[t]);
            double Z(int third, double temp) => (temp - mean[third]) / Math.Max(0.1, rows.AnomalySpread[third] / 10.0);
            static bool Midwinter(int third) => third >= 6 && third < 15;   // December to February
            var rec = new List<double>();
            for (int k = 0; k < nh; k++) if (!double.IsNaN(tf[k]) && Midwinter(states.HourThird(k))) rec.Add(Z(states.HourThird(k), tf[k]));
            rec.Sort();
            int hours = WeatherGenerator.HoursInYear(tuning);
            var hour = new WeatherHour();
            for (int round = 0; round < 3; round++)
            {
                var game = new List<double>();
                var gameSum = new double[36]; var gameCount = new int[36];
                var gen = new WeatherGenerator(rows, tuning);
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(9000 + s), 2000);
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref hour);
                        gameSum[hour.Third] += hour.Temp / 10.0; gameCount[hour.Third]++;
                        if (Midwinter(hour.Third)) game.Add(Z(hour.Third, hour.Temp / 10.0));
                    }
                }
                game.Sort();
                double worst = 0;
                for (int t = 0; t < 36; t++)
                {
                    if (count[t] == 0 || gameCount[t] == 0) continue;
                    int shift = Tenths(mean[t] - gameSum[t] / gameCount[t]);
                    rows.MinMean[t] += shift; rows.MaxMean[t] += shift;
                    worst = Math.Max(worst, Math.Abs(shift / 10.0));
                }
                rep.Fits[$"cold.thirdMeanShift.{round}"] = worst;
                rep.Fits[$"cold.miss01.{round}"] = EmpiricalQuantile(game, 0.01) - EmpiricalQuantile(rec, 0.01);
                rep.Fits[$"cold.miss10.{round}"] = EmpiricalQuantile(game, 0.10) - EmpiricalQuantile(rec, 0.10);
                rep.Fits[$"cold.meanMiss.{round}"] = game.Average() - rec.Average();
                for (int i = 0; i < n; i++)
                {
                    double p = NormalCdf((i - mid) * step);
                    rows.AnomalyShape[i] += (int)Math.Round((EmpiricalQuantile(rec, p) - EmpiricalQuantile(game, p)) * 1000);
                }
                for (int i = 1; i < n; i++) rows.AnomalyShape[i] = Math.Max(rows.AnomalyShape[i], rows.AnomalyShape[i - 1]);
                // The shape stays a shape: centred under the clamped normal the generator reads it through, so the mean
                // stays with each third's climate (moved above) and a settled Fair spell still sits on its climate.
                int centre = (int)Math.Round(ShapeMean(rows.AnomalyShape, step));
                for (int i = 0; i < n; i++) rows.AnomalyShape[i] -= centre;
                rep.Fits[$"cold.shapeCentre.{round}"] = centre;
            }
            rep.Levels["anomaly.shape"] = "record, refitted on the finished hours";
        }

        /// <summary>A shape table's mean (‰ of the spread) read off a normal clamped at its ends, as the generator reads it.</summary>
        static double ShapeMean(int[] shape, double step)
        {
            int n = shape.Length, mid = n / 2;
            double edge = mid * step, tail = 1 - NormalCdf(edge), sum = tail * (shape[0] + shape[n - 1]);
            const int steps = 1200;
            double dz = 2 * edge / steps;
            for (int k = 0; k < steps; k++)
            {
                double z = -edge + (k + 0.5) * dz, x = (z + edge) / step;
                int i = Math.Min(n - 2, (int)Math.Floor(x));
                sum += Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI) * dz * (shape[i] + (shape[i + 1] - shape[i]) * (x - i));
            }
            return sum;
        }

        /// <summary>
        /// WX-25 v4 (the fit review, 2026-11-08): the night's depth. Over the winter outside storms (it fades under a storm's
        /// cloud), each hour's temperature about its third and hour's mean; the spread near the fair shape's bottom less the
        /// spread near its top is the nights' own. The game
        /// is played over fixed seeds and the depth's variance moves by the miss, twice; then the anomaly gives up what the
        /// nights add to a day's mean (the fair shape's mean depth, squared), so the days keep the record's spread. The game's
        /// cold snaps fell 3–8 °F short of the record's at every site without it.
        /// </summary>
        static void NightDepth(WeatherRows rows, CalibrationReport rep, RecordStates states, double[] tf, WeatherTuning tuning, int seeds)
        {
            if (seeds <= 0) return;
            var bottom = new int[36]; var top = new int[36]; var depth = new double[36];
            for (int t = 0; t < 36; t++)
            {
                int lo = 0, hi = 0;
                for (int h = 1; h < 24; h++)
                {
                    if (rows.DiurnalFair[t * 24 + h] < rows.DiurnalFair[t * 24 + lo]) lo = h;
                    if (rows.DiurnalFair[t * 24 + h] > rows.DiurnalFair[t * 24 + hi]) hi = h;
                }
                bottom[t] = lo; top[t] = hi;
                double peak = Math.Max(1, rows.DiurnalFair[t * 24 + hi]);
                for (int h = 0; h < 24; h++) depth[t] += Math.Max(0, Math.Min(1, (peak - rows.DiurnalFair[t * 24 + h]) / peak)) / 24;
            }
            static bool Near(int h, int at) { int d = ((h - at) % 24 + 24) % 24; return d <= 1 || d >= 23; }   // within an hour of it
            double Excess(List<(int Third, int Hour, double T)> hours)
            {
                double Spread(Func<int, int, bool> pick)
                {
                    var groups = hours.Where(x => pick(x.Third, x.Hour)).GroupBy(x => (x.Third, x.Hour)).Where(g => g.Count() > 1).ToList();
                    double sum = 0; int dof = 0;
                    foreach (var g in groups) { double m = g.Average(x => x.T); sum += g.Sum(x => (x.T - m) * (x.T - m)); dof += g.Count() - 1; }
                    return dof <= 0 ? 0 : sum / dof;
                }
                return Spread((t, h) => Near(h, bottom[t])) - Spread((t, h) => Near(h, top[t]));
            }
            var rec = new List<(int, int, double)>();
            for (int k = 0; k < Math.Min(tf.Length, states.State.Length); k++)
                if (!double.IsNaN(tf[k]) && Winter(states.HourThird(k)) && states.State[k] != WeatherRows.Storm) rec.Add((states.HourThird(k), k % 24, tf[k]));
            double recExcess = Excess(rec);

            var spread = rows.AnomalySpread.ToArray();
            double variance = 0, f = 0;
            int hours = WeatherGenerator.HoursInYear(tuning);
            var hour = new WeatherHour();
            for (int round = 0; round < 2; round++)
            {
                var game = new List<(int, int, double)>();
                var gen = new WeatherGenerator(rows, tuning);
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(4000 + s), 2000);
                    for (int i = 0; i < hours; i++) { gen.Step(ref hour); if (Winter(hour.Third) && hour.State != WeatherRows.Storm) game.Add((hour.Third, hour.HourOfDay, hour.Temp / 10.0)); }
                }
                double miss = recExcess - Excess(game);
                variance = Math.Max(0, variance + miss);
                rows.NightDepthSpread = Tenths(Math.Sqrt(variance));
                for (int t = 0; t < 36; t++)
                {
                    // The daily mean of an hourly AR(1) varies less than its hours (the anomaly's own factor, as fitted).
                    double phi = rows.AnomalyPhiHourQ32[t] / (double)WeatherMath.Q32One;
                    f = 0;
                    for (int i = 0; i < 24; i++) for (int j = 0; j < 24; j++) f += Math.Pow(phi, Math.Abs(i - j));
                    f /= 576;
                    double kept = spread[t] / 10.0 * spread[t] / 10.0 - depth[t] * depth[t] * variance / Math.Max(0.05, f);
                    rows.AnomalySpread[t] = Tenths(Math.Sqrt(Math.Max(0.25 * spread[t] / 10.0 * spread[t] / 10.0, kept)));
                }
                rep.Fits[$"night.excessMiss.{round}"] = miss;
            }
            rep.Fits["night.depthSpread"] = Math.Sqrt(variance);
            rep.Fits["clamp.nightSpreadFloorThirds"] = Enumerable.Range(0, 36).Count(t => rows.AnomalySpread[t] <= Tenths(0.5 * spread[t] / 10.0));
            rep.Fits["night.recordExcess"] = recExcess;
        }

        /// <summary>
        /// WX-25 v4 (the fit review, 2026-11-08): the hour's own wobble. Its memory is the record's within-day correlation at
        /// 3 h (each winter day's hours less the day's mean and the fair shape), about a face hour. Its spread: the game is
        /// played over fixed seeds and the wobble's variance moves by the miss in the winter's afternoon spread outside storms
        /// (the hours within one of the fair shape's top, each about its third and hour's mean), twice. The nights' extra is
        /// NightDepth's, which runs first and adds nothing to the afternoon; storms keep theirs through StormTemperature,
        /// which runs after. The record's hours varied 12–27 % more than the game's, its 3-day means nearly alike: the miss was
        /// within the day. Fitted on every hour, the wobble warmed midwinter afternoons that already matched (January's 90 %
        /// point +2–4 °F) and melted the reference snowpack early.
        /// </summary>
        static void HourWobble(WeatherRows rows, CalibrationReport rep, RecordStates states, double[] tf, WeatherTuning tuning, int seeds)
        {
            if (seeds <= 0) return;
            int nd = Math.Min(tf.Length, states.State.Length) / 24;
            double sxy = 0, sxx = 0, syy = 0; int pairs = 0;
            for (int d = 0; d < nd; d++)
            {
                int t = states.DayThird[d];
                if (!Winter(t)) continue;
                var dev = new double[24];
                bool whole = true;
                for (int h = 0; h < 24; h++)
                {
                    double v = tf[d * 24 + h];
                    if (double.IsNaN(v)) { whole = false; break; }
                    dev[h] = v - (rows.MinMean[t] + (rows.MaxMean[t] - rows.MinMean[t]) * rows.DiurnalFair[t * 24 + h] / 1000.0) / 10.0;
                }
                if (!whole) continue;
                double mean = dev.Average();
                for (int h = 0; h + 3 < 24; h++) { double x = dev[h] - mean, y = dev[h + 3] - mean; sxy += x * y; sxx += x * x; syy += y * y; pairs++; }
            }
            if (pairs >= EffectMin && sxx > 0 && syy > 0)
            {
                double memory = Math.Max(0.3, Math.Min(0.8, sxy / Math.Sqrt(sxx * syy)));
                rows.FastMemory = (int)Math.Round(memory * 1000);
                rep.Fits["wobble.memory"] = memory;
            }
            double Spread(List<(int Third, int Hour, double T)> hours)
            {
                double sum = 0; int dof = 0;
                foreach (var g in hours.GroupBy(x => (x.Third, x.Hour)))
                {
                    if (g.Count() < 2) continue;
                    double m = g.Average(x => x.T);
                    sum += g.Sum(x => (x.T - m) * (x.T - m)); dof += g.Count() - 1;
                }
                return dof <= 0 ? 0 : sum / dof;
            }
            var top = new int[36];
            for (int t = 0; t < 36; t++)
                for (int h = 1; h < 24; h++) if (rows.DiurnalFair[t * 24 + h] > rows.DiurnalFair[t * 24 + top[t]]) top[t] = h;
            bool Afternoon(int third, int h) { int d = ((h - top[third]) % 24 + 24) % 24; return d <= 1 || d >= 23; }
            var rec = new List<(int, int, double)>();
            for (int k = 0; k < Math.Min(tf.Length, states.State.Length); k++)
            {
                int t = states.HourThird(k);
                if (!double.IsNaN(tf[k]) && Winter(t) && states.State[k] != WeatherRows.Storm && Afternoon(t, k % 24)) rec.Add((t, k % 24, tf[k]));
            }
            double recSpread = Spread(rec), variance = 0;
            int hours = WeatherGenerator.HoursInYear(tuning);
            var hour = new WeatherHour();
            for (int round = 0; round < 2; round++)
            {
                var game = new List<(int, int, double)>();
                var gen = new WeatherGenerator(rows, tuning);
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(5000 + s), 2000);
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref hour);
                        if (Winter(hour.Third) && hour.State != WeatherRows.Storm && Afternoon(hour.Third, hour.HourOfDay)) game.Add((hour.Third, hour.HourOfDay, hour.Temp / 10.0));
                    }
                }
                double miss = recSpread - Spread(game);
                variance = Math.Max(0, variance + miss);
                rows.FastSpread = Tenths(Math.Sqrt(variance));
                rep.Fits[$"wobble.spreadMiss.{round}"] = miss;
            }
            rep.Fits["wobble.spread"] = Math.Sqrt(variance);
        }

        /// <summary>Per label month: the variance of storms' mean temperatures about their own third's mean (°F²), and the count.</summary>
        static double[] StormVariance(List<(int Third, double Temp)> storms, out int[] count)
        {
            var sum = new double[12]; var dof = new int[12];
            count = new int[12];
            foreach (var g in storms.GroupBy(s => s.Third))
            {
                int n = g.Count();
                if (n < 2) continue;
                double mean = g.Average(s => s.Temp);
                int m = g.Key / 3;
                foreach (var s in g) sum[m] += (s.Temp - mean) * (s.Temp - mean);
                count[m] += n; dof[m] += n - 1;
            }
            var v = new double[12];
            for (int m = 0; m < 12; m++) v[m] = dof[m] <= 0 ? 0 : sum[m] / dof[m];
            return v;
        }

        /// <summary>
        /// The Fair scale per month, corrected by playing the rows (WX-07 v3(b), found at Olympic Valley: April +15 %, December
        /// −10 %). A storm rate per third is what the rows mean, but a Fair spell is drawn whole when it starts, so a long dry
        /// spell drawn in a dry month runs on into a wet one (and a wet month's storms run on into the next): the months
        /// either side of a change in the rate are biased, most where cycles are long. Two rounds over 400 fixed seeds: each
        /// month's played storm count is set against its intended one (the calibrated rates over its thirds' hours) and the
        /// month's Fair tables are rescaled so the cycle stretches or shrinks by the miss, damped and clamped. Month-wide, not
        /// per third, so the seeds' own noise (about ±2 % a month) stays out of the rows.
        /// </summary>
        static void RateCorrection(WeatherRows rows, CalibrationReport rep, WeatherTuning tuning, double[] intendedRate, int seeds)
        {
            if (seeds <= 0) return;
            int hours = WeatherGenerator.HoursInYear(tuning);
            var thirdHours = new double[36];
            for (int t = 0; t < 36; t++)
            {
                int month = t / 3, part = t % 3;
                int length = part < 2 ? 10 : MonthDays[month] - 20;
                thirdHours[t] = length * 24.0;
            }
            var h = new WeatherHour();
            for (int round = 0; round < 2; round++)
            {
                var gen = new WeatherGenerator(rows, tuning);
                var played = new double[12];
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(1000 + s), 2000);
                    int ordinal = -1;
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref h);
                        if (h.StormOrdinal >= 0 && h.StormOrdinal != ordinal) { ordinal = h.StormOrdinal; played[h.Third / 3]++; }
                    }
                }
                for (int m = 0; m < 12; m++)
                {
                    double intended = 0, cycle = 0, fair = 0, weight = 0;
                    for (int t = 3 * m; t < 3 * m + 3; t++)
                    {
                        intended += intendedRate[t] * thirdHours[t];
                        double perStorm = 10 / Math.Max(1e-6, intendedRate[t]);   // real tenths per storm
                        double gap = TableMean(rows, t, 0);
                        double mix = Enumerable.Range(0, 3).Sum(p => rows.PatternMix[t * 3 + p] / 1000.0 * rows.PatternGap[p] / 1000.0);
                        double fairPart = gap * mix * rep.Fits["character.meanInverseSqrtShare"] * (1 - rows.BackToBack[t] / 1000.0);
                        cycle += perStorm * thirdHours[t]; fair += fairPart * thirdHours[t]; weight += thirdHours[t];
                    }
                    cycle /= weight; fair /= weight;
                    double got = played[m] / seeds;
                    if (intended <= 0 || got <= 0 || fair <= 0) continue;
                    // Too many storms played: the cycle must lengthen by the ratio; only its Fair part moves.
                    double ratio = got / intended;
                    double scale = 1 + 0.8 * (ratio - 1) * cycle / fair;
                    scale = Bound(rep, "clamp.fairScale", 0.8, 1.25, scale);
                    for (int t = 3 * m; t < 3 * m + 3; t++)
                        for (int i = 0; i < WeatherRows.QuantileCount; i++)
                            rows.SetQuantile(t, 0, i, Math.Max(1, (int)Math.Round(rows.Quantile(t, 0, i) * scale)));
                    rep.Fits[$"fair.rateCorrection.{m}"] = (rep.Fits.TryGetValue($"fair.rateCorrection.{m}", out var prior) ? prior : 1) * scale;
                }
            }
        }

        static readonly int[] MonthDays = { 31, 30, 31, 31, 28, 31, 30, 31, 30, 31, 31, 30 };

        /// <summary>
        /// WX-31 v3 (owner 2026-11-08): each month's liquid, played. The storm counts are right (RateCorrection), but the
        /// tables, their blending between thirds and the flurries on top left October–April 3–4 % wetter than the record at
        /// every site, and snowfall with it. Two rounds over fixed seeds: each month's played liquid is set against the
        /// record's (its thirds' mean liquid per year) and the month's storm-total tables are rescaled by the miss, clamped
        /// to ±20 %. The flurries are a share of the storm's total, so they follow.
        /// </summary>
        static void LiquidCorrection(WeatherRows rows, CalibrationReport rep, WeatherTuning tuning, int seeds)
        {
            if (seeds <= 0) return;
            int hours = WeatherGenerator.HoursInYear(tuning);
            var h = new WeatherHour();
            for (int round = 0; round < 2; round++)
            {
                var gen = new WeatherGenerator(rows, tuning);
                var played = new double[12];
                for (int s = 0; s < seeds; s++)
                {
                    gen.Reset(SeedFor(2000 + s), 2000);
                    for (int i = 0; i < hours; i++) { gen.Step(ref h); played[h.Third / 3] += h.Liquid; }
                }
                for (int m = 0; m < 12; m++)
                {
                    double target = 0;
                    for (int t = 3 * m; t < 3 * m + 3; t++) target += rows.TargetLiquid[t];
                    double got = played[m] / seeds;
                    if (target < 10 || got <= 0) continue;
                    double scale = Bound(rep, "clamp.stormScale", 0.8, 1.25, target / got);
                    for (int t = 3 * m; t < 3 * m + 3; t++)
                    {
                        rows.StormMedian[t] = Math.Max(1, (int)Math.Round(rows.StormMedian[t] * scale));
                        for (int i = 0; i < WeatherRows.QuantileCount; i++)
                            rows.SetQuantile(t, WeatherRows.StormTotal, i, Math.Max(1, (int)Math.Round(rows.Quantile(t, WeatherRows.StormTotal, i) * scale)));
                    }
                    rep.Fits[$"storm.liquidCorrection.{m}"] = (rep.Fits.TryGetValue($"storm.liquidCorrection.{m}", out var prior) ? prior : 1) * scale;
                }
            }
        }

        /// <summary>
        /// WX-25 v3: the anomaly's shape from the record's November–April daily departures, each over its third's spread. The
        /// empirical quantile at each table point's normal probability, then centred and scaled so that, read off a normal
        /// clamped at ±3 as the generator reads it, it keeps the normal's mean and spread: only the shape changes. Western
        /// winters are left-skewed (Stevens Pass −0.63, Winter Park −0.42): their storms run near freezing and rarely warmer,
        /// and a symmetric anomaly put 6 % more of Stevens Pass's liquid below 30 °F than fell there.
        /// </summary>
        static void AnomalyShape(WeatherRows rows, CalibrationReport rep, List<double> standard)
        {
            int n = WeatherRows.AnomalyShapePoints, mid = n / 2;
            double step = WeatherRows.AnomalyShapeStep / 1000.0, edge = mid * step;
            if (standard.Count < EffectMin) { rep.Levels["anomaly.shape"] = "normal"; return; }
            standard.Sort();
            double m = standard.Average();
            double m2 = standard.Average(x => (x - m) * (x - m)), m3 = standard.Average(x => Math.Pow(x - m, 3));
            rep.Fits["anomaly.skew"] = m3 / Math.Pow(Math.Max(1e-9, m2), 1.5);
            var q = new double[n];
            for (int i = 0; i < n; i++) q[i] = EmpiricalQuantile(standard, NormalCdf((i - mid) * step));
            // The moments of a table read off the clamped normal: the inside by quadrature, each tail's mass at its end point.
            (double Mean, double Square) Moments(Func<double, double> g)
            {
                double tail = 1 - NormalCdf(edge), s1 = tail * (g(-edge) + g(edge)), s2 = tail * (g(-edge) * g(-edge) + g(edge) * g(edge));
                const int steps = 1200;
                double dz = 2 * edge / steps;
                for (int k = 0; k < steps; k++)
                {
                    double z = -edge + (k + 0.5) * dz, w = Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI) * dz, v = g(z);
                    s1 += w * v; s2 += w * v * v;
                }
                return (s1, s2);
            }
            double Read(double z)
            {
                double x = (Math.Max(-edge, Math.Min(edge, z)) + edge) / step;
                int i = Math.Min(n - 2, (int)Math.Floor(x));
                return q[i] + (q[i + 1] - q[i]) * (x - i);
            }
            var identity = Moments(z => Math.Max(-edge, Math.Min(edge, z)));
            var shaped = Moments(Read);
            double scale = Math.Sqrt(identity.Square / Math.Max(1e-9, shaped.Square - shaped.Mean * shaped.Mean));
            for (int i = 0; i < n; i++) rows.AnomalyShape[i] = (int)Math.Round((q[i] - shaped.Mean) * scale * 1000);
            rep.Levels["anomaly.shape"] = "record";
        }

        /// <summary>
        /// WX-28 v3: the wind's two site numbers. Memory: outside storms, each hour's wind as a departure from its state and
        /// third (in their spreads), correlated with the departure 2 and 3 hours on, read at a face hour's real length. Storm
        /// wind: within November–April storms, the slope of an hour's wind over its storm's mean wind on its liquid over the
        /// storm's mean hour (both less one): the hours that pour blow harder.
        /// </summary>
        static void Wind(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, WeatherTuning tuning)
        {
            int nh = Math.Min(b.HourCount, states.State.Length);
            var z = new double[nh];
            for (int k = 0; k < nh; k++)
            {
                z[k] = double.NaN;
                int s = states.State[k];
                if (s == WeatherRows.Storm || float.IsNaN(b.Wind[k])) continue;
                int i = states.HourThird(k) * WeatherRows.States + s;
                if (rows.WindSpread[i] <= 0) continue;
                z[k] = (b.Wind[k] * MsToTenthsMph - rows.WindMean[i]) / rows.WindSpread[i];
            }
            double Lag(int lag)
            {
                double sxy = 0, sxx = 0, syy = 0, sx = 0, sy = 0; int n = 0;
                for (int k = 0; k + lag < nh; k++)
                {
                    if (double.IsNaN(z[k]) || double.IsNaN(z[k + lag]) || !Winter(states.HourThird(k))) continue;
                    sx += z[k]; sy += z[k + lag]; sxy += z[k] * z[k + lag]; sxx += z[k] * z[k]; syy += z[k + lag] * z[k + lag]; n++;
                }
                if (n < EffectMin) return double.NaN;
                double cov = sxy / n - sx / n * sy / n, vx = sxx / n - sx / n * sx / n, vy = syy / n - sy / n * sy / n;
                return cov / Math.Sqrt(Math.Max(1e-12, vx * vy));
            }
            double faceHour = (double)tuning.CalendarDays / tuning.FaceDays;
            double r2 = Lag(2), r3 = Lag(3);
            if (!double.IsNaN(r2) && !double.IsNaN(r3))
            {
                double memory = Math.Max(0, Math.Min(0.95, r2 + (faceHour - 2) * (r3 - r2)));
                rows.WindMemory = (int)Math.Round(memory * 1000);
                rep.Fits["wind.memoryPerFaceHour"] = memory;
            }
            else rep.Levels["wind.memory"] = "default";

            double xy = 0, xx = 0; int pairs = 0;
            foreach (var st in states.Storms)
            {
                if (!Winter(st.Third)) continue;
                double pSum = 0, wSum = 0; int n = 0;
                for (int k = st.Start; k < st.End && k < b.HourCount; k++)
                    if (!float.IsNaN(b.P[k]) && !float.IsNaN(b.Wind[k])) { pSum += b.P[k]; wSum += b.Wind[k]; n++; }
                if (n < 2 || pSum <= 0 || wSum <= 0) continue;
                double pMean = pSum / n, wMean = wSum / n;
                for (int k = st.Start; k < st.End && k < b.HourCount; k++)
                {
                    if (float.IsNaN(b.P[k]) || float.IsNaN(b.Wind[k])) continue;
                    double x = b.P[k] / pMean - 1, y = b.Wind[k] / wMean - 1;
                    xy += x * y; xx += x * x; pairs++;
                }
            }
            if (pairs >= EffectMin && xx > 0)
            {
                double slope = Math.Max(0, Math.Min(1.5, xy / xx));
                rows.StormWindPerRate = (int)Math.Round(slope * 1000);
                rep.Fits["wind.stormPerRate"] = slope;
            }
            else rep.Levels["wind.stormPerRate"] = "default";
        }

        /// <summary>
        /// WX-31 v3: the KS statistic's 1 % critical value for n samples (1.63 ÷ √n), so the gate tightens as the record grows
        /// (0.058 at 800 durations, where it was a flat 0.1). The 1 % point, not the 5 %: five fits at seven sites are 35 tests,
        /// and at 5 % each about two would fail by chance alone; the hourly record and WX-18 v2's 36 h Clearing cut leave
        /// misfits of that size (Jackson NH's Clearings 0.051 on the record's own shape).
        /// </summary>
        public static double KsCritical(int n) => 1.63 / Math.Sqrt(Math.Max(1, n));

        /// <summary>The empirical quantile of sorted values at p (plotting positions (i + ½) ÷ n, linear between).</summary>
        static double EmpiricalQuantile(List<double> sorted, double p)
        {
            double h = Math.Max(0, Math.Min(sorted.Count - 1, p * sorted.Count - 0.5));
            int lo = (int)Math.Floor(h), hi = Math.Min(sorted.Count - 1, lo + 1);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (h - lo);
        }

        /// <summary>The mean of a table read the generator's way: linear from 0 at p = 0 through the points, flat above the last.</summary>
        static double ShapeMean(double[] q)
        {
            var p = WeatherRows.QuantilePoints;
            double sum = 0, prevP = 0, prevV = 0;
            for (int i = 0; i < p.Length; i++) { sum += (p[i] - prevP) * (prevV + q[i]) / 2; prevP = p[i]; prevV = q[i]; }
            return (sum + (10_000 - prevP) * prevV) / 10_000;
        }

        /// <summary>The table's cumulative share at x (its inverse, read the generator's way).</summary>
        static double ShapeCdf(double[] q, double x)
        {
            var p = WeatherRows.QuantilePoints;
            double prevP = 0, prevV = 0;
            for (int i = 0; i < p.Length; i++)
            {
                if (x <= q[i]) return (prevP + (p[i] - prevP) * (q[i] > prevV ? (x - prevV) / (q[i] - prevV) : 1)) / 10_000;
                prevP = p[i]; prevV = q[i];
            }
            return 1;
        }

        /// <summary>A third's quantile table's mean (the piecewise-linear table, 0 below its first point).</summary>
        static double TableMean(WeatherRows rows, int third, int distribution)
        {
            var p = WeatherRows.QuantilePoints;
            double sum = 0, prevP = 0, prevV = 0;
            for (int i = 0; i < p.Length; i++)
            {
                double v = rows.Quantile(third, distribution, i);
                sum += (p[i] - prevP) * (prevV + v) / 2;
                prevP = p[i]; prevV = v;
            }
            sum += (10_000 - prevP) * prevV;
            return sum / 10_000;
        }

        /// <summary>
        /// WX-41 v2 (owner 2026-11-08): PowderDayIn is the overnight snow at mid-mountain (stop to landing) at which a played
        /// December–March brings as many powder mornings as a real one brought days with ≥ 6 in of new snow; never below 6 in.
        /// By count, not share: a played December–March has 42 mornings where the real one has 121 days, so matching the share
        /// gave a season about a third of its powder days. A face night holds about two real days' snow, so most sites sit at
        /// the 6 in floor and get somewhat fewer than the record.
        /// </summary>
        static void PowderDay(WeatherRows rows, CalibrationReport rep, BlendedRecord b, RecordStates states, double[] tf, WeatherTuning tuning, int seeds)
        {
            int dz = rows.MidFeet - rows.ZrefFeet;
            int days = 0, powder = 0;
            for (int d = 0; d < b.DayCount; d++)
            {
                if (!DecToMar(states.DayThird[d]) || b.DayMissing(d)) continue;
                days++;
                double snowIn = 0;
                int t = states.DayThird[d];
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + b.LagHours + h;
                    if (k >= b.HourCount || float.IsNaN(b.P[k]) || b.P[k] <= 0 || double.IsNaN(tf[k])) continue;
                    bool isNight = states.State[k] != WeatherRows.Storm && (k % 24 < 7 || k % 24 >= 17);
                    int lapse = isNight ? rows.LapseNight[t] : rows.LapseDay[t];
                    int temp = Tenths(tf[k]) - (int)Math.Round((double)lapse * dz / 1000);
                    int dew = Tenths(Psychrometrics.CToF(float.IsNaN(b.Td[k]) ? b.T[k] : b.Td[k])) - (int)Math.Round((double)rows.DewLapse[t] * dz / 1000);
                    int liquid = (int)Math.Round(b.P[k] * MmToHundredthsIn * Math.Max(0, 1 + rows.PrecipGradient[t] * dz / 100_000.0));
                    snowIn += WeatherPoint.From(temp, Math.Min(temp, dew), liquid).SnowTenthsIn / 10.0;
                }
                if (snowIn >= PowderInches) powder++;
            }
            // Per season: the record's December–March has 121 days (its missing days don't count against it).
            double perSeasonRecord = days == 0 ? 0 : powder * 121.0 / days;

            var gen = new WeatherGenerator(rows, tuning);
            int hours = FaceHoursFor(tuning, 182);
            var mornings = new List<int>();
            var hour = new WeatherHour();
            var perHour = new int[hours];
            var third = new int[hours];
            for (int s = 0; s < seeds; s++)
            {
                gen.Reset(SeedFor(1000 + s), 2000);
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref hour);
                    perHour[i] = WeatherPoint.At(hour, rows.ZrefFeet, rows.MidFeet).SnowTenthsIn;
                    third[i] = hour.Third;
                }
                for (int day = 1; day * 24 + LandingHour <= hours; day++)
                {
                    int landing = day * 24 + LandingHour;
                    if (!DecToMar(third[landing - 1])) continue;
                    int sum = 0;
                    for (int i = (day - 1) * 24 + StopHour; i < landing; i++) sum += perHour[i];
                    mornings.Add(sum);
                }
            }
            mornings.Sort();
            int threshold = 60;
            if (mornings.Count > 0 && perSeasonRecord > 0)
            {
                // The threshold the record's count per season of mornings reaches: the (count × seeds)-th largest morning.
                int index = mornings.Count - (int)Math.Round(perSeasonRecord * seeds);
                threshold = Math.Max(60, mornings[Math.Max(0, Math.Min(mornings.Count - 1, index))]);
            }
            rows.PowderDayTenthsIn = threshold;
            rep.PowderPerSeasonRecord = perSeasonRecord;
            rep.PowderPerSeasonGame = seeds == 0 ? 0 : mornings.Count(m => m >= threshold) / (double)seeds;
        }

        /// <summary>The calibration's own plays: fixed seeds, never the player's.</summary>
        public static ulong SeedFor(int i) => 0x5745_4154_4845_5200UL + (ulong)i * 0x9E37_79B9_7F4A_7C15UL;

        // ---- The ladder (WX-17 v2) ----

        static List<double>[] Groups() => Enumerable.Range(0, 36).Select(_ => new List<double>()).ToArray();
        static List<int>[] IntGroups() => Enumerable.Range(0, 36).Select(_ => new List<int>()).ToArray();

        static List<double>[] GroupBy<T>(IEnumerable<T> items, Func<T, int> third, Func<T, double> value)
        {
            var g = Groups();
            foreach (var item in items) g[third(item)].Add(value(item));
            return g;
        }

        static List<int>[] GroupBy<T>(IEnumerable<T> items, Func<T, int> third, Func<T, int> value)
        {
            var g = IntGroups();
            foreach (var item in items) g[third(item)].Add(value(item));
            return g;
        }

        static double[] Ladder(CalibrationReport rep, string field, List<double>[] byThird, Func<List<double>, double> stat, double n0, int min, double fallback, bool quiet = false) =>
            LadderStat(rep, field, set =>
            {
                var all = new List<double>();
                foreach (int t in set) all.AddRange(byThird[t]);
                return (all.Count == 0 ? double.NaN : stat(all), all.Count);
            }, n0, min, fallback, quiet);

        /// <summary>
        /// WX-17 v2: each third is shrunk toward its month by its n, stored = (n × third + n₀ × month) ÷ (n + n₀). The ladder
        /// (month → season of three months → site → default) only supplies a parent with enough samples: a level under its
        /// minimum takes the level above whole, unshrunk, so shrinkage never compounds across levels. A December–March field
        /// whose month must pool up to the season flags the site limited.
        /// </summary>
        static double[] LadderStat(CalibrationReport rep, string field, Func<int[], (double V, int N)> stat, double n0, int min, double fallback, bool quiet = false)
        {
            var site = stat(AllThirds);
            double siteValue = site.N >= min && !double.IsNaN(site.V) ? site.V : fallback;
            var months = new double[12];
            var monthN = new int[12];
            for (int m = 0; m < 12; m++)
            {
                var season = stat(SeasonThirds[m]);
                var s = stat(MonthThirds[m]);
                monthN[m] = s.N;
                months[m] = Usable(s, min) ? s.V : Usable(season, min) ? season.V : siteValue;
            }
            var result = new double[36];
            string worst = "third";
            for (int t = 0; t < 36; t++)
            {
                var s = stat(new[] { t });
                result[t] = Shrink(s, months[t / 3], n0, min);
                if (!DecToMar(t) || s.N >= min) continue;
                if (monthN[t / 3] < min)
                {
                    worst = "season";
                    if (!quiet && !rep.LimitedReasons.Contains(field)) rep.LimitedReasons.Add(field);
                }
                else if (worst == "third") worst = "month";
            }
            if (!quiet) rep.Levels[field] = site.N < min ? "default" : worst;
            return result;
        }

        static bool Usable((double V, int N) s, int min) => s.N >= min && !double.IsNaN(s.V);

        static double Shrink((double V, int N) s, double parent, double n0, int min) =>
            s.N < min || double.IsNaN(s.V) ? parent : (s.N * s.V + n0 * parent) / (s.N + n0);

        /// <summary>Histograms (‰ × 8) per third, each shrunk toward its month's, and on up.</summary>
        static double[][] Ladder8(CalibrationReport rep, string field, List<int>[] byThird)
        {
            var bins = new double[8][];
            for (int o = 0; o < 8; o++)
            {
                int bin = o;
                bins[o] = LadderStat(rep, field, set =>
                {
                    int n = 0, c = 0;
                    foreach (int t in set) { n += byThird[t].Count; foreach (int v in byThird[t]) if (v == bin) c++; }
                    return (n == 0 ? double.NaN : (double)c / n, n);
                }, HistogramN0, HistogramMin, 1 / 8.0, quiet: o > 0);
            }
            return bins;
        }

        static void HistogramInto(int[] target, double[][] bins)
        {
            for (int t = 0; t < 36; t++)
            {
                var w = new double[8];
                for (int o = 0; o < 8; o++) w[o] = Math.Max(0, bins[o][t]);
                var share = ToPermille(w);
                for (int o = 0; o < 8; o++) target[t * 8 + o] = share[o];
            }
        }

        /// <summary>
        /// A factor per group (pattern or quarter): the geometric mean of the ratios in it, normalised so the share-weighted
        /// mean is 1 and clamped 0.5–1.6 (WX-58); a group with under 30 samples gets 1.
        /// </summary>
        static double[] GeometricFactor(CalibrationReport rep, string field, IEnumerable<(int Group, double Ratio)> samples, int groups, double[] share)
        {
            var sum = new double[groups]; var n = new int[groups];
            foreach (var (g, r) in samples) { if (r <= 0) continue; sum[g] += Math.Log(r); n[g]++; }
            var f = new double[groups];
            for (int g = 0; g < groups; g++) f[g] = n[g] < EffectMin ? 1 : Math.Exp(sum[g] / n[g]);
            // Clamp, then renormalise, until both hold: a clamped factor must not tilt the share-weighted mean off 1.
            for (int pass = 0; pass < 20; pass++)
            {
                double centre = 0;
                for (int g = 0; g < groups; g++) centre += share[g] * f[g];
                if (centre <= 0) break;
                for (int g = 0; g < groups; g++) f[g] = Math.Max(0.5, Math.Min(1.6, f[g] / centre));
            }
            for (int g = 0; g < groups; g++) rep.Fits[$"{field}.{g}"] = f[g];
            return f;
        }

        // ---- Days ----

        static readonly List<int>[] ThirdsOfDay = BuildThirdsOfDay();

        static List<int>[] BuildThirdsOfDay()
        {
            var map = new List<int>[365];
            for (int i = 0; i < 365; i++) map[i] = new List<int>();
            for (int t = 0; t < 36; t++)
            {
                int month = t / 3, part = t % 3;
                int start = MonthStart[month] + part * 10;
                int end = part < 2 ? start + 10 : MonthStart[month + 1];
                for (int d = start - 5; d < end + 5; d++) map[((d % 365) + 365) % 365].Add(t);
            }
            return map;
        }

        /// <summary>The thirds whose ±5-day smoothed window holds this day of the label year.</summary>
        static List<int> ThirdsHolding(int dayOfLabelYear) => ThirdsOfDay[Math.Max(0, Math.Min(364, dayOfLabelYear))];

        static List<int>[] SmoothedDays(RecordStates s, int nd)
        {
            var sets = IntGroups();
            for (int d = 0; d < nd; d++) foreach (int t in ThirdsHolding(s.DayOfLabelYear[d])) sets[t].Add(d);
            return sets;
        }

        // ---- Statistics ----

        static double Mean(List<double> l) => l.Count == 0 ? double.NaN : l.Average();

        static double Std(List<double> l)
        {
            if (l.Count < 2) return double.NaN;
            double m = l.Average(), s = 0;
            foreach (double v in l) s += (v - m) * (v - m);
            return Math.Sqrt(s / (l.Count - 1));
        }

        static double MeanOver(double[] a, int from, int to)
        {
            double s = 0; int n = 0;
            for (int k = from; k < to && k < a.Length; k++) if (!double.IsNaN(a[k])) { s += a[k]; n++; }
            return n == 0 ? double.NaN : s / n;
        }

        static double MeanOverF(float[] a, int from, int to)
        {
            double s = 0; int n = 0;
            for (int k = from; k < to && k < a.Length; k++) if (!float.IsNaN(a[k])) { s += a[k]; n++; }
            return n == 0 ? 0 : s / n;
        }

        static double Quantile(List<double> l, double p)
        {
            var s = l.OrderBy(v => v).ToList();
            double x = p * (s.Count - 1);
            int i = (int)Math.Floor(x);
            return i + 1 < s.Count ? s[i] + (s[i + 1] - s[i]) * (x - i) : s[i];
        }

        public static double Correlation(List<double> x, List<double> y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < x.Count; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
            return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
        }

        static double Slope(List<double> x, List<double> y)
        {
            if (x.Count < 3) return 0;
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0;
            for (int i = 0; i < x.Count; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
            return sxx <= 0 ? 0 : sxy / sxx;
        }

        static double Spearman(List<double> x, List<double> y) => Correlation(Ranks(x), Ranks(y));

        static List<double> Ranks(List<double> v)
        {
            var order = Enumerable.Range(0, v.Count).OrderBy(i => v[i]).ToArray();
            var r = new double[v.Count];
            for (int i = 0; i < order.Length;)
            {
                int j = i;
                while (j + 1 < order.Length && v[order[j + 1]] == v[order[i]]) j++;
                for (int k = i; k <= j; k++) r[order[k]] = (i + j) / 2.0;
                i = j + 1;
            }
            return r.ToList();
        }

        /// <summary>Γ(x) for x &gt; 0 (Lanczos, g = 7).</summary>
        public static double Gamma(double x)
        {
            double[] c = { 0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
                           12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7 };
            if (x < 0.5) return Math.PI / (Math.Sin(Math.PI * x) * Gamma(1 - x));
            x -= 1;
            double a = c[0], t = x + 7.5;
            for (int i = 1; i < 9; i++) a += c[i] / (x + i);
            return Math.Sqrt(2 * Math.PI) * Math.Pow(t, x + 0.5) * Math.Exp(-t) * a;
        }

        /// <summary>The Weibull shape by maximum likelihood (bisection on the profile equation).</summary>
        static double WeibullShape(List<double> x)
        {
            var lx = x.Select(v => Math.Log(Math.Max(1e-9, v))).ToArray();
            double meanLog = lx.Average();
            double G(double k)
            {
                double a = 0, s = 0;
                for (int i = 0; i < lx.Length; i++) { double p = Math.Exp(k * lx[i]); a += p * lx[i]; s += p; }
                return a / s - 1 / k - meanLog;
            }
            double lo = 0.2, hi = 10;
            for (int i = 0; i < 80; i++) { double mid = (lo + hi) / 2; if (G(mid) > 0) hi = mid; else lo = mid; }
            return (lo + hi) / 2;
        }

        static double KsUniform(List<double> u)
        {
            if (u.Count == 0) return 0;
            u.Sort();
            double d = 0;
            for (int i = 0; i < u.Count; i++) d = Math.Max(d, Math.Max(Math.Abs((i + 1.0) / u.Count - u[i]), Math.Abs(u[i] - (double)i / u.Count)));
            return d;
        }

        static double KsNormal(List<double> z) => KsUniform(z.Select(NormalCdf).ToList());

        public static double NormalCdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

        static double Erfc(double x)
        {
            // Numerical Recipes' erfc (Chebyshev), relative error under 1.2e-7.
            double z = Math.Abs(x), t = 1 / (1 + 0.5 * z);
            double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 +
                       t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
            return x >= 0 ? r : 2 - r;
        }

        /// <summary>The standard normal's inverse (Acklam's rational approximation, relative error under 1.2e-9 after one Newton step).</summary>
        public static double NormalInv(double p)
        {
            double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
            double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
            double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
            double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
            double x;
            if (p < 0.02425)
            {
                double q = Math.Sqrt(-2 * Math.Log(p));
                x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            else if (p > 1 - 0.02425)
            {
                double q = Math.Sqrt(-2 * Math.Log(1 - p));
                x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
            }
            else
            {
                double q = p - 0.5, r = q * q;
                x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
            }
            double e = NormalCdf(x) - p;
            double u = e * Math.Sqrt(2 * Math.PI) * Math.Exp(x * x / 2);
            return x - u / (1 + x * u / 2);
        }

        // ---- Units ----

        /// <summary>A correction's bound, counted in the report each time it binds (the review: no clamp binds silently).</summary>
        static double Bound(CalibrationReport rep, string key, double lo, double hi, double x)
        {
            if (x >= lo && x <= hi) return x;
            rep.Fits[key] = (rep.Fits.TryGetValue(key, out var n) ? n : 0) + 1;
            return Math.Max(lo, Math.Min(hi, x));
        }

        static int Tenths(double f) => double.IsNaN(f) ? 0 : (int)Math.Round(f * 10, MidpointRounding.AwayFromZero);
        static int Permille(double share) => double.IsNaN(share) ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, share)) * 1000);
        static string StateName(int s) => s switch { 0 => "fair", 1 => "building", 2 => "storm", _ => "clearing" };

        /// <summary>Weights to ‰ summing to exactly 1,000 (largest remainder, ties to the lower index).</summary>
        static int[] ToPermille(double[] w)
        {
            double total = w.Sum();
            var r = new int[w.Length];
            if (total <= 0) { for (int i = 0; i < w.Length; i++) r[i] = 1000 / w.Length; r[0] += 1000 - r.Sum(); return r; }
            var rem = new double[w.Length];
            for (int i = 0; i < w.Length; i++) { double x = w[i] / total * 1000; r[i] = (int)Math.Floor(x); rem[i] = x - r[i]; }
            for (int left = 1000 - r.Sum(); left > 0; left--)
            {
                int best = 0;
                for (int i = 1; i < w.Length; i++) if (rem[i] > rem[best]) best = i;
                r[best]++; rem[best] = -1;
            }
            return r;
        }

        /// <summary>‰ with every entry at least <paramref name="floor"/> (WX-57), summing to 1,000.</summary>
        static int[] FloorPermille(double[] w, int floor)
        {
            var r = ToPermille(w);
            for (int pass = 0; pass < 4; pass++)
            {
                var low = r.Select(v => v < floor).ToArray();
                if (!low.Any(x => x)) break;
                int fixedTotal = floor * low.Count(x => x);
                var rest = new double[r.Length];
                for (int i = 0; i < r.Length; i++) rest[i] = low[i] ? 0 : r[i];
                var scaled = ToPermille(rest).Select(v => v * (1000 - fixedTotal) / 1000.0).ToArray();
                var share = new int[r.Length];
                for (int i = 0; i < r.Length; i++) share[i] = low[i] ? floor : (int)Math.Floor(scaled[i]);
                share[Array.IndexOf(low, false)] += 1000 - share.Sum();
                r = share;
            }
            return r;
        }
    }
}
