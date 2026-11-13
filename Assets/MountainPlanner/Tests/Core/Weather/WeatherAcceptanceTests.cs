using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Engine-free. Task 4.05, WX-07 v3(a) and WX-58: the generator over 200 seeds against what its frozen rows imply. The
    /// implied values are computed here from the rows' tables alone (their fitted distributions integrated, their factor
    /// tables enumerated, the calendar's blend re-done in doubles), never by playing the generator, so a code bug in the
    /// generator shows as a miss. Winter months (December–March) are gated; the shoulders are printed.
    /// <para>These are consistency checks (the adversarial review, 2026-11-09): they show the generator reproduces its own
    /// fitted rows, not that the rows are right. The game against the record lives in <see cref="WeatherFidelityTests"/>,
    /// with gates from the record's own noise, and so do the checks that used to sit here against the record (WX-07 v3(c)'s
    /// snowfall and wet-hour temperature, WX-58's clustering, WX-19's season spread).</para>
    /// </summary>
    [Category("Consistency")]
    public sealed class WeatherAcceptanceTests
    {
        /// <summary>WX-07 v3 asks for at least 200; 1,000 keeps a month's sampling noise near 1.5 %, so a miss is a bug.</summary>
        const int Seeds = 1000;
        const ulong SeedBase = 0x5758_3037_0000_0000UL;
        static readonly int[] MonthStartDay = { 0, 31, 61, 92, 123, 151, 182, 212, 243, 273, 304, 335, 365 };
        static readonly string[] MonthName = { "Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep" };
        static readonly int[] Winter = { 2, 3, 4, 5 };

        static WeatherRows Frozen(string site) =>
            WeatherRowsFile.FromBytes(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));

        /// <summary>What the seeds generated, per label month (and per third where WX-58 asks).</summary>
        sealed class Played
        {
            public readonly double[] Storms = new double[12], StormTotal = new double[12], StormHours = new double[12], Liquid = new double[12];
            public readonly double[] FairResidual = new double[12], FairResidualN = new double[12];
            public readonly double[] SeasonTotals = new double[Seeds], SeasonSnow = new double[Seeds];
            /// <summary>Cycles (Fair spells) by the third they start in and their pattern.</summary>
            public readonly double[,] PatternCycles = new double[36, 3];
            /// <summary>Fair spells (face hours touched) by the month they start in.</summary>
            public readonly List<int>[] FairSpells = Enumerable.Range(0, 12).Select(_ => new List<int>()).ToArray();
            /// <summary>Storms per 10-face-day window, October–December and January–April.</summary>
            public readonly List<int>[] Windows = { new List<int>(), new List<int>() };
            public int LongestRun;
            /// <summary>October–April: liquid-weighted temperature of wet hours (0.01 in × °F), snowfall at the reference (in).</summary>
            public double WetTempSum, WetLiquid, Snowfall;
            /// <summary>Each storm's third (at its start) and its mean temperature over its storm hours (°F).</summary>
            public readonly List<(int Third, double Temp)> StormTemps = new List<(int, double)>();
        }

        const int WindowFaceHours = 10 * 24;
        static readonly Dictionary<string, Played> PlayedCache = new Dictionary<string, Played>();

        static Played Play(string site, WeatherRows rows)
        {
            lock (PlayedCache)
            {
                if (PlayedCache.TryGetValue(site, out var hit)) return hit;
                var p = new Played();
                var tuning = WeatherTuning.Default;
                var gen = new WeatherGenerator(rows, tuning);
                int year = WeatherGenerator.HoursInYear(tuning);
                var h = new WeatherHour();
                for (int s = 0; s < Seeds; s++)
                {
                    gen.Reset(SeedBase + (ulong)s, 2026);
                    int ordinal = -1, stormMonth = 0, prevState = -1, fairAge = 0, fairStart = 0, fairMonth = 0, cycle = 0;
                    double season = 0, seasonSnow = 0;
                    int windowStorms = 0, stormThird = -1;
                    double stormTemp = 0, stormN = 0;
                    for (int i = 0; i < year; i++)
                    {
                        gen.Step(ref h);
                        int m = h.Third / 3;
                        p.Liquid[m] += h.Liquid;
                        if (m <= 6 && h.Liquid > 0)
                        {
                            p.WetTempSum += (double)h.Liquid * h.Temp / 10; p.WetLiquid += h.Liquid;
                            double snow = WeatherPoint.From(h.Temp, h.Dew, h.Liquid, rows.ZrefFeet).SnowTenthsIn / 10.0;
                            p.Snowfall += snow; seasonSnow += snow;
                        }
                        if (h.State == WeatherRows.Storm)
                        {
                            if (stormN == 0) stormThird = h.Third;
                            stormTemp += h.Temp / 10.0; stormN++;
                        }
                        else if (stormN > 0)
                        {
                            if (stormThird < 21) p.StormTemps.Add((stormThird, stormTemp / stormN));
                            stormTemp = 0; stormN = 0;
                        }
                        if (m <= 6) season += h.Liquid;   // October–April
                        if (h.StormOrdinal >= 0 && h.StormOrdinal != ordinal)
                        {
                            ordinal = h.StormOrdinal; stormMonth = m;
                            p.Storms[m]++; p.StormTotal[m] += h.StormTotal;
                            windowStorms++;
                        }
                        if (h.StormOrdinal >= 0) p.StormHours[stormMonth]++;
                        bool fair = h.State == WeatherRows.Fair;
                        if (fair && prevState != WeatherRows.Fair) { fairStart = i; fairMonth = m; }
                        // A cycle began (WX-57: the pattern steps at each Fair start, even one that ends inside the hour).
                        if (gen.Cycle != cycle)
                        {
                            cycle = gen.Cycle;
                            p.PatternCycles[h.Third, gen.Pattern]++;
                            p.LongestRun = Math.Max(p.LongestRun, gen.Dwell + 1);
                        }
                        if (!fair && prevState == WeatherRows.Fair && i > 0) p.FairSpells[fairMonth].Add(i - fairStart);
                        // A settled Fair hour: its offset is eased to 0, so it is climate + anomaly + lean + the season + the wet
                        // spell's lean (WX-25 v5: a long dry spell runs warm, as the record's did).
                        fairAge = fair ? (prevState == WeatherRows.Fair ? fairAge + 1 : 0) : 0;
                        prevState = h.State;
                        if (fair && fairAge >= 8)
                        {
                            p.FairResidual[m] += h.Temp - ImpliedFairTemp(rows, h);
                            p.FairResidualN[m]++;
                        }
                        if ((i + 1) % WindowFaceHours == 0)
                        {
                            int half = HalfOf((i + 1 - WindowFaceHours) * (double)tuning.CalendarDays / tuning.FaceDays / 24);
                            if (half >= 0) p.Windows[half].Add(windowStorms);
                            windowStorms = 0;
                        }
                    }
                    p.SeasonTotals[s] = season;
                    p.SeasonSnow[s] = seasonSnow;
                }
                return PlayedCache[site] = p;
            }
        }

        /// <summary>WX-58's season halves by a window's first day after October 1: October–December 0, January–April 1.</summary>
        static int HalfOf(double day) => day < MonthStartDay[3] ? 0 : day + 10.0 * WeatherTuning.Default.CalendarDays / WeatherTuning.Default.FaceDays <= MonthStartDay[7] ? 1 : -1;

        static double VarianceOverMean(List<int> counts)
        {
            double mean = counts.Average();
            double variance = counts.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, counts.Count - 1);
            return mean > 0 ? variance / mean : 0;
        }

        static double Quantile(List<int> values, double q)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(q * sorted.Length))];
        }

        // ---- What the rows imply ----

        /// <summary>The calendar's blend at a real time (days after October 1): the two thirds and the later one's weight.</summary>
        static (int A, int B, double W) BlendAt(double day)
        {
            double tenths = day * 240;
            var middle = new double[36];
            for (int i = 0; i < 36; i++)
            {
                int month = i / 3, part = i % 3;
                int start = MonthStartDay[month] + part * 10;
                int length = part < 2 ? 10 : MonthStartDay[month + 1] - MonthStartDay[month] - 20;
                middle[i] = (start * 24 + length * 12) * 10;
            }
            if (tenths < middle[0])
            {
                double prev = middle[35] - 365 * 240;
                return (35, 0, (tenths - prev) / (middle[0] - prev));
            }
            for (int i = 35; i >= 0; i--)
                if (middle[i] <= tenths)
                {
                    if (i == 35) { double next = middle[0] + 365 * 240; return (35, 0, (tenths - middle[35]) / (next - middle[35])); }
                    return (i, i + 1, (tenths - middle[i]) / (middle[i + 1] - middle[i]));
                }
            return (0, 1, 0);
        }

        static double Lerp(int[] a, int stride, int index, (int A, int B, double W) b) =>
            a[b.A * stride + index] * (1 - b.W) + a[b.B * stride + index] * b.W;

        /// <summary>The mean of a quantile table's draw, blended between two thirds as the generator blends a draw (Fair harmonically).</summary>
        static double TableMean(WeatherRows rows, int distribution, (int A, int B, double W) b)
        {
            double sum = 0;
            int off = WeatherRows.QuantileCount;
            for (int u = 0; u < 10_000; u++)
            {
                double x = WeatherMath.FromQuantiles(rows.Quantiles, (b.A * WeatherRows.Distributions + distribution) * off, u);
                double y = WeatherMath.FromQuantiles(rows.Quantiles, (b.B * WeatherRows.Distributions + distribution) * off, u);
                sum += distribution == 0 && x > 0 && y > 0 ? x * y / (b.W * x + (1 - b.W) * y) : x + (y - x) * b.W;
            }
            return sum / 10_000;
        }

        /// <summary>The season character's √share and 1/√share (‰), over its 20 winters and the ±10 % jitter.</summary>
        static (double Sqrt, double InvSqrt) Character(WeatherRows rows)
        {
            int min = rows.CharacterShare.Min(), max = rows.CharacterShare.Max();
            double sq = 0, inv = 0;
            int n = 0;
            foreach (int c in rows.CharacterShare)
                for (int j = -100; j <= 100; j++, n++)
                {
                    double share = Math.Max(1, Math.Min(max, Math.Max(min, c * (1000 + j) / 1000.0)));
                    double r = Math.Sqrt(share * 1000);
                    sq += r; inv += 1000 / r;
                }
            return (sq / n, inv / n);
        }

        /// <summary>A storm's size factor (‰) in a pattern: quarter × pattern × √share × WinterScale, clamped 400–2,000, over its draws.</summary>
        static double StormFactor(WeatherRows rows, int third, int p)
        {
            double sum = 0, weight = 0;
            int min = rows.CharacterShare.Min(), max = rows.CharacterShare.Max();
            int[] hist = rows.ArrivalOctants; int offset = third * 8;
            if (Enumerable.Range(0, 8).Sum(i => rows.PatternOctants[p * 8 + i]) > 0) { hist = rows.PatternOctants; offset = p * 8; }
            double octTotal = Enumerable.Range(0, 8).Sum(i => Math.Max(0, hist[offset + i]));
            var quarter = new double[4];
            for (int o = 0; o < 8; o++)
                for (int d = -22; d <= 22; d++)
                {
                    int dir = ((o * 45 + d) % 360 + 360) % 360;
                    quarter[((dir + 45) % 360) / 90] += (octTotal > 0 ? Math.Max(0, hist[offset + o]) / octTotal : 1 / 8.0) / 45;
                }
            foreach (int c in rows.CharacterShare)
                for (int j = -100; j <= 100; j++)
                {
                    double share = Math.Max(1, Math.Min(max, Math.Max(min, c * (1000 + j) / 1000.0)));
                    double sqrt = Math.Sqrt(share * 1000);
                    for (int q = 0; q < 4; q++)
                    {
                        double f = 1000.0 * rows.QuarterSize[q] / 1000 * rows.PatternSize[p] / 1000 * sqrt / 1000;
                        f = Math.Max(400, Math.Min(2000, f));
                        sum += quarter[q] * f; weight += quarter[q];
                    }
                }
            return sum / weight;
        }

        /// <summary>A Fair hour with its offset settled: the diurnal climate, the pattern's lean and the season's offset.</summary>
        static double ImpliedFairTemp(WeatherRows rows, in WeatherHour h)
        {
            var b = (h.Third == h.NeighbourThird ? (h.Third, h.Third, 0.0) : OrderedBlend(h));
            double max = Lerp(rows.MaxMean, 1, 0, b), min = Lerp(rows.MinMean, 1, 0, b);
            double shape = Lerp(rows.DiurnalFair, 24, h.HourOfDay, b);
            return min + (max - min) * shape / 1000 + rows.PatternLean[h.Pattern] + h.SeasonOffset + h.WetLean;
        }

        static (int, int, double) OrderedBlend(in WeatherHour h)
        {
            // WeatherHour reports its third (the nearer) and the neighbour with the neighbour's weight.
            bool neighbourLater = h.NeighbourThird == (h.Third + 1) % 36;
            double w = h.NeighbourWeight / 1000.0;
            return neighbourLater ? (h.Third, h.NeighbourThird, w) : (h.NeighbourThird, h.Third, 1 - w);
        }

        sealed class Implied
        {
            public readonly double[] Storms = new double[12], StormTotal = new double[12], StormHours = new double[12], Liquid = new double[12];
        }

        /// <summary>
        /// The rows' expectation through the year, every 6 real hours: storms at the renewal rate (one per mean cycle), the
        /// pattern's distribution carried by its matrix at each Fair start (the chain lags its mix; that lag is the rows'),
        /// sizes from the table times the factor in each pattern.
        /// </summary>
        static Implied Imply(WeatherRows rows)
        {
            var im = new Implied();
            var (_, invSqrt) = Character(rows);
            var factor = new double[36, 3];
            for (int t = 0; t < 36; t++) for (int p = 0; p < 3; p++) factor[t, p] = StormFactor(rows, t, p);
            const double step = 0.25;
            var pi = new double[3];
            double mix0 = Enumerable.Range(0, 3).Sum(p => Math.Max(0, rows.PatternMix[p]));
            for (int p = 0; p < 3; p++) pi[p] = Math.Max(0, rows.PatternMix[p]) / mix0;   // October 1: October early's mix
            var next = new double[3];
            for (double day = step / 2; day < 365; day += step)
            {
                var b = BlendAt(day);
                int nearer = b.W < 0.5 ? b.A : b.B;
                int m = 0;
                while (m < 11 && MonthStartDay[m + 1] <= day) m++;
                double fairTable = TableMean(rows, 0, b) * invSqrt;
                double building = TableMean(rows, 1, b) * (1 - Lerp(rows.BuildingZeroShare, 1, 0, b) / 1000);
                double storm = TableMean(rows, 2, b), clearing = TableMean(rows, 3, b);
                double backToBack = Lerp(rows.BackToBack, 1, 0, b) / 1000;
                double cycle = 0, total = 0, table = TableMean(rows, WeatherRows.StormTotal, b);
                for (int p = 0; p < 3; p++)
                {
                    cycle += pi[p] * (building + storm + clearing + (1 - backToBack) * fairTable * rows.PatternGap[p] / 1000);
                    total += pi[p] * table * (factor[b.A, p] * (1 - b.W) + factor[b.B, p] * b.W) / 1000;
                }
                double rate = step * 240 / cycle;                      // storms in this step
                double flurry = Math.Min(900, Lerp(rows.ClearingLiquidShare, 1, 0, b)) / 1000;
                im.Storms[m] += rate;
                im.StormTotal[m] += rate * total;
                im.StormHours[m] += rate * storm / 10;
                im.Liquid[m] += rate * total / (1 - flurry);
                // The chain steps at each Fair start: rate × (1 − back-to-back) of them in this step.
                double moves = Math.Min(1, rate * (1 - backToBack));
                for (int to = 0; to < 3; to++)
                {
                    next[to] = 0;
                    for (int from = 0; from < 3; from++)
                    {
                        int row = (nearer * 3 + from) * 3;
                        double rowTotal = Enumerable.Range(0, 3).Sum(k => Math.Max(0, rows.PatternMatrix[row + k]));
                        next[to] += pi[from] * Math.Max(0, rows.PatternMatrix[row + to]) / Math.Max(1, rowTotal);
                    }
                }
                for (int k = 0; k < 3; k++) pi[k] += moves * (next[k] - pi[k]);
            }
            for (int m = 0; m < 12; m++)
            {
                im.StormTotal[m] /= Math.Max(1e-9, im.Storms[m]);
                im.StormHours[m] /= Math.Max(1e-9, im.Storms[m]);
            }
            return im;
        }

        static double Lerp1(double[] a, (int A, int B, double W) b) => a[b.A] * (1 - b.W) + a[b.B] * b.W;

        // ---- The tests ----

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX07a_TheGeneratorMatchesItsRows(string site)
        {
            var rows = Frozen(site);
            var p = Play(site, rows);
            var im = Imply(rows);
            double hoursPerReal = WeatherTuning.Default.FaceDays / (double)WeatherTuning.Default.CalendarDays;
            var misses = new List<string>();
            for (int m = 0; m < 12; m++)
            {
                double storms = p.Storms[m] / Seeds, size = p.StormTotal[m] / Math.Max(1, p.Storms[m]);
                // A storm of L face hours at a random phase touches L + 1 face hours on average.
                double hours = (p.StormHours[m] / Math.Max(1, p.Storms[m]) - 1) / hoursPerReal, liquid = p.Liquid[m] / Seeds;
                double fair = p.FairResidual[m] / Math.Max(1, p.FairResidualN[m]) / 10;
                string line = $"{MonthName[m]}: storms {storms:F2}|{im.Storms[m]:F2} size {size / 100:F2}|{im.StormTotal[m] / 100:F2} in, " +
                              $"storm {hours:F1}|{im.StormHours[m]:F1} h, liquid {liquid / 100:F2}|{im.Liquid[m] / 100:F2} in, settled Fair {fair:+0.00;-0.00} °F";
                TestContext.Progress.WriteLine($"{site} {line}");
                if (!Winter.Contains(m)) continue;
                // Counts and liquid per month compare with a renewal rate (one storm per mean cycle at that date), which can't
                // see a long Fair spell drawn in a dry third running into the next: ±10 % a month, ±5 % over the winter.
                Check(misses, $"{MonthName[m]} storms", storms, im.Storms[m], 0.10);
                Check(misses, $"{MonthName[m]} liquid", liquid, im.Liquid[m], 0.10);
                Check(misses, $"{MonthName[m]} storm size", size, im.StormTotal[m], 0.05);
                Check(misses, $"{MonthName[m]} storm length", hours, im.StormHours[m], 0.05);
                if (Math.Abs(fair) > 0.5) misses.Add($"{MonthName[m]} settled Fair temperature {fair:+0.00;-0.00} °F off its climate (band ±0.5 °F)");
            }
            Check(misses, "Dec–Mar storms", Winter.Sum(m => p.Storms[m]) / Seeds, Winter.Sum(m => im.Storms[m]), 0.05);
            Check(misses, "Dec–Mar liquid", Winter.Sum(m => p.Liquid[m]) / Seeds, Winter.Sum(m => im.Liquid[m]), 0.05);
            Assert.That(misses, Is.Empty, $"{site}: WX-07 v3(a) misses");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX58_TheChainCantStick(string site)
        {
            var rows = Frozen(site);
            for (int t = 0; t < 36; t++)
                for (int from = 0; from < 3; from++)
                {
                    int row = (t * 3 + from) * 3;
                    double total = Enumerable.Range(0, 3).Sum(k => Math.Max(0, rows.PatternMatrix[row + k]));
                    for (int to = 0; to < 3; to++)
                        Assert.That(rows.PatternMatrix[row + to] * 1000 / total, Is.GreaterThanOrEqualTo(50 - 0.5), $"{site}: third {t}, {from} → {to}");
                }
            Assert.That(rows.DwellCap, Is.GreaterThan(0), "a dwell cap");
            var p = Play(site, rows);
            Assert.That(p.LongestRun, Is.LessThanOrEqualTo(rows.DwellCap), "no generated run exceeds the cap");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX58_ThePatternMixFollowsEachThird(string site)
        {
            var rows = Frozen(site);
            var p = Play(site, rows);
            var misses = new List<string>();
            for (int t = 0; t < 21; t++)   // October–April
            {
                double n = Enumerable.Range(0, 3).Sum(k => p.PatternCycles[t, k]);
                double rowTotal = Enumerable.Range(0, 3).Sum(k => Math.Max(0, rows.PatternMix[t * 3 + k]));
                for (int k = 0; k < 3; k++)
                {
                    double got = p.PatternCycles[t, k] * 100 / n, want = rows.PatternMix[t * 3 + k] * 100 / rowTotal;
                    if (Math.Abs(got - want) > 5) misses.Add($"third {t} pattern {k}: {got:F1} % vs {want:F1} %");
                }
            }
            Assert.That(misses, Is.Empty, $"{site}: WX-58 pattern mix (±5 points)");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX58_DrySpellsKeepTheirTail(string site)
        {
            var rows = Frozen(site);
            var p = Play(site, rows);
            var misses = new List<string>();
            double facePerReal = WeatherTuning.Default.FaceDays / (double)WeatherTuning.Default.CalendarDays;
            foreach (int m in Winter)
            {
                double got = Quantile(p.FairSpells[m], 0.99) - 0.5;   // face hours touched, less the average phase
                double want = ImpliedFairQuantile(rows, m, 0.99) / 10 * facePerReal;
                TestContext.Progress.WriteLine($"{site} {MonthName[m]}: Fair spell 99 % {got / facePerReal:F0} h vs fitted {want / facePerReal:F0} h ({p.FairSpells[m].Count} spells)");
                if (Math.Abs(got / want - 1) > 0.20) misses.Add($"{MonthName[m]}: {got / facePerReal:F0} h vs {want / facePerReal:F0} h");
            }
            Assert.That(misses, Is.Empty, $"{site}: WX-58 dry-spell 99 % point (±20 %)");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX58_NoSeasonBreaksTheGuard(string site)
        {
            // Owner, 2026-11-08: very extreme seasons are welcome (1,000 seasons against 20 winters will go past the record at
            // both ends), and how widely seasons vary is WX-19's test. This is only a guard against a broken calibration.
            var rows = Frozen(site);
            var p = Play(site, rows);
            double recordMean = Enumerable.Range(0, 21).Sum(t => rows.TargetLiquid[t]);
            double lo = p.SeasonTotals.Min() / recordMean, hi = p.SeasonTotals.Max() / recordMean;
            TestContext.Progress.WriteLine($"{site}: October–April totals {lo:F2}–{hi:F2} × the record's mean ({recordMean / 100:F1} in)");
            Assert.That(lo, Is.GreaterThanOrEqualTo(0.15), "the driest season");
            Assert.That(hi, Is.LessThanOrEqualTo(2.5), "the wettest season");
        }

        /// <summary>The fitted Fair spell's quantile (real tenths) in a month: its thirds' tables × the pattern gap × 1/√share.</summary>
        static double ImpliedFairQuantile(WeatherRows rows, int month, double q)
        {
            int min = rows.CharacterShare.Min(), max = rows.CharacterShare.Max();
            var values = new List<(double V, double W)>();
            for (int t = month * 3; t < month * 3 + 3; t++)
            {
                double mixTotal = Enumerable.Range(0, 3).Sum(k => Math.Max(0, rows.PatternMix[t * 3 + k]));
                for (int u = 0; u < 10_000; u += 5)
                {
                    double x = WeatherMath.FromQuantiles(rows.Quantiles, t * WeatherRows.Distributions * WeatherRows.QuantileCount, u);
                    for (int k = 0; k < 3; k++)
                    {
                        double pw = Math.Max(0, rows.PatternMix[t * 3 + k]) / mixTotal;
                        if (pw <= 0) continue;
                        foreach (int c in rows.CharacterShare)
                            for (int j = -100; j <= 100; j += 20)
                            {
                                double share = Math.Max(1, Math.Min(max, Math.Max(min, c * (1000 + j) / 1000.0)));
                                values.Add((x * rows.PatternGap[k] / 1000 * 1000 / Math.Sqrt(share * 1000), pw));
                            }
                    }
                }
            }
            values.Sort((a, b) => a.V.CompareTo(b.V));
            double total = values.Sum(v => v.W), acc = 0;
            foreach (var v in values) { acc += v.W; if (acc >= q * total) return v.V; }
            return values[values.Count - 1].V;
        }

        static void Check(List<string> misses, string what, double got, double want, double band)
        {
            if (want <= 0 || Math.Abs(got / want - 1) > band) misses.Add($"{what}: {got:F2} vs implied {want:F2} ({(want > 0 ? got / want - 1 : 0):P1}, band ±{band:P0})");
        }
    }
}
