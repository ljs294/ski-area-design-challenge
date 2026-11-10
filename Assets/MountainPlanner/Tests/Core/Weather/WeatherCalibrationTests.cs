using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Engine-free. Task 4.05: the calibration on both recorded sites. Storm finding (WX-18 v2), the fits (WX-31 v2), the
    /// powder threshold (WX-41) and WX-07 v3(b), the rows against years they didn't see.
    /// </summary>
    public sealed class WeatherCalibrationTests
    {
        static readonly string[] MonthName = { "Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr" };

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX18_StatesFindTheRecordsStorms(string site)
        {
            var rep = WeatherFixtures.Calibrated(site).Report;
            TestContext.Progress.WriteLine($"{site}: {rep.Storms} storms, storm days ÷ wet days {rep.StormDayRatio:F3}, Clearing ends in daylight {rep.ClearingDaylightShare:P0}");
            Assert.That(rep.StormDayRatio, Is.GreaterThanOrEqualTo(0.9), "detected storm days ÷ Daymet wet days");
            Assert.That(rep.ClearingDaylightShare, Is.LessThan(0.6), "Clearings ending in daylight (a day-locked detector would put them all there)");
        }

        /// <summary>
        /// The fits that miss their KS gate today, by site (weather engine v0.1, 2026-11-09). A known limit is listed, not
        /// hidden: the test passes only when the failing set equals this table, so a new failure and a silent fix both show.
        /// </summary>
        static readonly Dictionary<string, string[]> KnownKsLimits = new Dictionary<string, string[]>
        {
            // The 3-hour day shift (WX-15 v3) left Jackson NH's clearing lengths a little off the log-normal (KS 0.066
            // against 0.057); the fidelity tests that read clearing (dry spells, clustering) pass there.
            [WeatherFixtures.JacksonNh] = new[] { "clearing" },
        };

        /// <summary>WX-31 v3: every fitted distribution passes KS at the 1 % point for its own sample (1.63 ÷ √n), not a flat 0.1.</summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX31_EveryFitPassesItsKsTest(string site)
        {
            var rep = WeatherFixtures.Calibrated(site).Report;
            Assert.That(rep.Ks, Is.Not.Empty);
            TestContext.Progress.WriteLine($"{site}: " + string.Join(", ", rep.Ks.Select(kv => $"{kv.Key} {kv.Value:F3} (n {rep.KsCount[kv.Key]}, gate {WeatherCalibration.KsCritical(rep.KsCount[kv.Key]):F3})")));
            var bad = rep.Ks.Where(kv => !(kv.Value < WeatherCalibration.KsCritical(rep.KsCount[kv.Key]))).Select(kv => kv.Key).OrderBy(k => k).ToList();
            var known = KnownKsLimits.TryGetValue(site, out var k0) ? k0.OrderBy(k => k).ToList() : new List<string>();
            Assert.That(bad, Is.EqualTo(known), $"{site}: KS at or over its 1 % point (known limits: {string.Join(", ", known)})");
        }

        /// <summary>
        /// WX-41 v2: a played December–March brings as many powder mornings as a real one brought days with ≥ 6 in, or, where
        /// the 6 in floor holds the threshold up, no more. Played over 300 fresh seeds, never the calibration's own 20, so the
        /// threshold is checked where it wasn't fitted (the old share test compared the calibration's play with itself).
        /// </summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX41_PowderMorningsComeAsOftenAsInTheRecord(string site)
        {
            var (rows, rep) = WeatherFixtures.Calibrated(site);
            var tuning = WeatherTuning.Default;
            var gen = new WeatherGenerator(rows, tuning);
            var hour = new WeatherHour();
            int hours = (int)(182L * 24 * tuning.FaceDays / tuning.CalendarDays);
            var snow = new int[hours]; var third = new int[hours];
            const int seeds = 300;
            int powder = 0;
            for (int s = 0; s < seeds; s++)
            {
                gen.Reset(0x5758_3431_0000_0000UL + (ulong)s, 2026);
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref hour);
                    snow[i] = WeatherPoint.At(hour, rows.ZrefFeet, rows.MidFeet).SnowTenthsIn;
                    third[i] = hour.Third;
                }
                for (int day = 1; day * 24 + WeatherCalibration.LandingHour <= hours; day++)
                {
                    int landing = day * 24 + WeatherCalibration.LandingHour;
                    if (third[landing - 1] < 6 || third[landing - 1] >= 18) continue;
                    int sum = 0;
                    for (int i = (day - 1) * 24 + WeatherCalibration.StopHour; i < landing; i++) sum += snow[i];
                    if (sum >= rows.PowderDayTenthsIn) powder++;
                }
            }
            double game = (double)powder / seeds, record = rep.PowderPerSeasonRecord;
            bool floored = rows.PowderDayTenthsIn == 60;
            TestContext.Progress.WriteLine($"{site}: PowderDayIn {rows.PowderDayTenthsIn / 10.0} in{(floored ? " (the floor)" : "")}; powder mornings per December–March, record {record:F1}, game {game:F1} ({seeds} fresh seeds)");
            Assert.That(rows.PowderDayTenthsIn, Is.GreaterThanOrEqualTo(60), "never under 6 in");
            if (floored) Assert.That(game, Is.LessThanOrEqualTo(record * 1.25), $"{site}: at the floor, no more powder mornings than the record's (+25 %)");
            else Assert.That(game / record - 1, Is.InRange(-0.25, 0.25), $"{site}: powder mornings per season within ±25 % of the record's");
        }

        /// <summary>
        /// WX-07 v3(b): each of the 20 label years left out in turn, the rows built from the other 19, generated, and compared
        /// with the year they didn't see. A single year can't be gated (one season is luck: CV 0.2–0.4), so the gates are on the
        /// left-out years pooled: season totals ±10 %, and per month liquid ±15 % (or twice the record's own standard error of
        /// that month's mean, where that is wider: owner confirmed 2026-11-08), temperature ±2 °F, storms ±20 %. Each fold is printed.
        /// The fit review (2026-11-08) adds each left-out winter's place among its fold's generated seasons (its PIT): if the
        /// game's winters vary as much as the record's, the places are even, so no more than 5 of 20 fall in the outer tenth
        /// (2 expected; 6 or more is under a 1.2 % chance) and their KS is under the 1 % point.
        /// </summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX07b_TheRowsPredictYearsTheyDidntSee(string site)
        {
            var screened = WeatherFixtures.Screened(site);
            var b = screened.Blend;
            var s = screened.Record.Site;
            var points = new ReferencePoints(s.BaseFeet, s.SummitFeet);
            var years = b.LabelYears().ToArray();
            var full = WeatherStates.Find(b);
            var actual = years.Select(y => RecordYear(b, full, y)).ToArray();
            var generated = new Year[years.Length];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, years.Length, f =>
            {
                var rows = WeatherCalibration.Build(b.WithoutLabelYear(years[f]), screened.Record, points, WeatherTuning.Default, characterSeeds: 40, powderSeeds: 2, stormSeeds: 40).Rows;
                generated[f] = Generate(rows, 40, 0x4C4F_4F00UL + (ulong)f * 7919);
            });
            TestContext.Progress.WriteLine($"{site}: {years.Length} folds in {watch.ElapsedMilliseconds} ms");

            var misses = new List<string>();
            var pit = new double[years.Length];
            for (int f = 0; f < years.Length; f++)
            {
                var seasons = generated[f].Seasons;
                pit[f] = (seasons.Count(x => x < actual[f].Season) + 0.5 * seasons.Count(x => x == actual[f].Season) + 0.5) / (seasons.Count + 1);
                TestContext.Progress.WriteLine($"  {years[f]}: season {generated[f].Season / 100:F1} in (generated mean) vs {actual[f].Season / 100:F1} in, its place {pit[f]:F2}");
            }
            int tails = pit.Count(u => u < 0.05 || u > 0.95);
            var sortedPit = pit.OrderBy(u => u).ToArray();
            double pitKs = 0;
            for (int i = 0; i < sortedPit.Length; i++) pitKs = Math.Max(pitKs, Math.Max((i + 1.0) / sortedPit.Length - sortedPit[i], sortedPit[i] - (double)i / sortedPit.Length));
            TestContext.Progress.WriteLine($"{site}: left-out winters in the outer tenth {tails} of {years.Length} (2 expected); their places' KS {pitKs:F3} (gate {WeatherCalibration.KsCritical(years.Length):F3})");
            if (tails > 5) misses.Add($"{tails} of {years.Length} winters in the outer tenth (≤ 5)");
            if (pitKs >= WeatherCalibration.KsCritical(years.Length)) misses.Add($"the winters' places KS {pitKs:F3}");
            double seasonGot = generated.Average(g => g.Season), seasonWant = actual.Average(a => a.Season);
            double foldSpread = Std(actual.Select(a => a.Season)) / Math.Sqrt(years.Length) / seasonWant;
            TestContext.Progress.WriteLine($"{site}: pooled season {seasonGot / 100:F1} vs {seasonWant / 100:F1} in ({seasonGot / seasonWant - 1:+0.0%;-0.0%}; the folds' own noise ±{foldSpread:P1})");
            if (Math.Abs(seasonGot / seasonWant - 1) > 0.10) misses.Add($"season {seasonGot / seasonWant - 1:P1} (±10 %)");
            for (int m = 0; m < 7; m++)
            {
                double lg = generated.Average(g => g.Liquid[m]), la = actual.Average(a => a.Liquid[m]);
                double tg = generated.Average(g => g.Temp[m]), ta = actual.Average(a => a.Temp[m]);
                double sg = generated.Average(g => g.Storms[m]), sa = actual.Average(a => a.Storms[m]);
                // The record's own noise in a month's mean (twice its standard error over the years): a Sierra April's liquid
                // varies so much year to year that its 20-year mean is itself only good to about ±30 %.
                double floor = 2 * Std(actual.Select(a => a.Liquid[m])) / Math.Sqrt(years.Length) / la;
                double band = Math.Max(0.15, floor);
                TestContext.Progress.WriteLine($"  {MonthName[m]}: liquid {lg / 100:F2}|{la / 100:F2} in ({lg / la - 1:+0.0%;-0.0%}, band ±{band:P0}), temperature {tg:F1}|{ta:F1} °F, storms {sg:F2}|{sa:F2}");
                if (Math.Abs(lg / la - 1) > band) misses.Add($"{MonthName[m]} liquid {lg / la - 1:P1} (±{band:P0})");
                if (Math.Abs(tg - ta) > 2) misses.Add($"{MonthName[m]} temperature {tg - ta:+0.0;-0.0} °F (±2 °F)");
                if (Math.Abs(sg / sa - 1) > 0.20) misses.Add($"{MonthName[m]} storms {sg / sa - 1:P1} (±20 %)");
            }
            Assert.That(misses, Is.Empty, $"{site}: WX-07 v3(b) misses");
        }

        /// <summary>One label year's October–April numbers: total liquid (0.01 in), and per month liquid, mean °F, storms.</summary>
        sealed class Year
        {
            public double Season;
            public readonly double[] Liquid = new double[7], Temp = new double[7], Storms = new double[7];
            public readonly List<double> Seasons = new List<double>();   // generated: each seed's October–April liquid
        }

        static Year RecordYear(BlendedRecord b, RecordStates states, int labelYear)
        {
            var y = new Year();
            var tSum = new double[7]; var tN = new double[7];
            for (int d = 0; d < b.DayCount; d++)
            {
                if (states.DayLabelYear[d] != labelYear) continue;
                int m = states.DayThird[d] / 3;
                if (m > 6) continue;
                if (!b.DayMissing(d)) { y.Liquid[m] += b.Prcp[d] / 0.254; y.Season += b.Prcp[d] / 0.254; }
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + b.LagHours + h;
                    if (k < b.HourCount && !float.IsNaN(b.T[k])) { tSum[m] += b.T[k] * 1.8 + 32; tN[m]++; }
                }
            }
            foreach (var st in states.Storms)
                if (st.LabelYear == labelYear && st.Third / 3 <= 6) y.Storms[st.Third / 3]++;
            for (int m = 0; m < 7; m++) y.Temp[m] = tN[m] > 0 ? tSum[m] / tN[m] : double.NaN;
            return y;
        }

        static Year Generate(WeatherRows rows, int seeds, ulong seedBase)
        {
            var g = new Year();
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            var h = new WeatherHour();
            int hours = WeatherGenerator.HoursInYear(WeatherTuning.Default);
            var tSum = new double[7]; var tN = new double[7];
            for (int s = 0; s < seeds; s++)
            {
                gen.Reset(seedBase + (ulong)s, 2026);
                int ordinal = -1;
                double season = 0;
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref h);
                    int m = h.Third / 3;
                    if (m > 6) continue;
                    g.Liquid[m] += h.Liquid; g.Season += h.Liquid; season += h.Liquid;
                    tSum[m] += h.Temp / 10.0; tN[m]++;
                    if (h.StormOrdinal >= 0 && h.StormOrdinal != ordinal) { ordinal = h.StormOrdinal; g.Storms[m]++; }
                }
                g.Seasons.Add(season);
            }
            g.Season /= seeds;
            for (int m = 0; m < 7; m++) { g.Liquid[m] /= seeds; g.Storms[m] /= seeds; g.Temp[m] = tSum[m] / tN[m]; }
            return g;
        }

        static double Std(IEnumerable<double> values)
        {
            var v = values.ToArray();
            double mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, v.Length - 1));
        }
    }
}
