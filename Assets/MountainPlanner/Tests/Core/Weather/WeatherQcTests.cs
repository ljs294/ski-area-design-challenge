using System;
using System.Linq;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Acquisition.Weather.Qc;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Engine-free. Task 4.05's data QC ("SNOTEL is notoriously janky"): faults planted in the real recordings are each caught,
    /// the rows barely move when they are, and the screens reject almost nothing on the two clean sites (the false-positive
    /// budget: no Daymet day, under 0.5 % of POWER's values).
    /// </summary>
    public sealed class WeatherQcTests
    {
        static WeatherQcLog Screen(WeatherSourceRecord record)
        {
            var log = new WeatherQcLog();
            DaymetScreen.Apply(record, log);
            PowerScreen.Apply(record, log);
            SnotelScreen.Apply(record, log);
            return log;
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void FalsePositiveBudget(string site)
        {
            var record = WeatherFixtures.Raw(site);
            long powerValues = record.Power.Values.Values.Sum(v => (long)v.Count);
            var log = Screen(record);
            TestContext.Progress.WriteLine($"{site}: verdict {WeatherQcLog.Name(log.Verdict)}; " + string.Join("; ", log.Entries.Where(e => e.Rejected > 0).Select(e => $"{e.Source} {e.Test}: {e.Rejected}/{e.Checked}")));
            Assert.That(log.Rejected(DaymetScreen.Source, "ranges"), Is.Zero, "Daymet days rejected");
            long power = log.Rejected(PowerScreen.Source, "ranges") + log.Rejected(PowerScreen.Source, "flatlines");
            Assert.That(power, Is.LessThan(powerValues * 0.005), "POWER values rejected (budget 0.5 %)");
            Assert.That(log.Entries.Where(e => e.Outcome == "flag" && e.Source != SnotelScreen.Source).Select(e => e.Note), Is.Empty,
                        "no Daymet or POWER anchor flag on a clean site (SNOTEL's ratio is the owner's call, reported)");
        }

        [Test]
        public void PlantedDaymetFaultsAreDroppedWhole()
        {
            var record = WeatherFixtures.Raw(WeatherFixtures.JacksonHole);
            var px = record.Reference!;
            int i = DayIndex(px.Days, CivilDay.Of(record.FirstLabelYear + 3, 1, 15));
            px.Series("tmax")[i] = px.Series("tmin")[i] - 5;           // max below min
            px.Series("prcp")[i + 1] = 900;                             // a 900 mm day
            px.Series("tmin")[i + 2] = -75;                             // below any recorded air
            var log = Screen(record);
            Assert.That(log.Rejected(DaymetScreen.Source, "ranges"), Is.EqualTo(3));
            for (int k = 0; k < 3; k++) Assert.That(px.RejectedDays, Does.Contain(px.Days[i + k]));
        }

        [Test]
        public void APlantedWrongPixelChecksTheAnchor()
        {
            var record = WeatherFixtures.Raw(WeatherFixtures.JacksonHole);
            record.Reference!.ElevationMetres += 300;
            var log = Screen(record);
            Assert.That(log.Verdict, Is.EqualTo(WeatherVerdict.CheckAnchor));
            Assert.That(log.Entries.Any(e => e.Source == DaymetScreen.Source && e.Test == "georeference" && e.Outcome == "flag"), Is.True, string.Join("; ", log.AnchorReasons));
        }

        [Test]
        public void PlantedPowerFaultsBecomeMissing()
        {
            var clean = Screen(WeatherFixtures.Raw(WeatherFixtures.JacksonHole));
            var record = WeatherFixtures.Raw(WeatherFixtures.JacksonHole);
            var t = record.Power.Series("T2M");
            int start = t.Count / 2;
            for (int k = 0; k < 48; k++) t[start + k] = 1.234;        // a stuck sensor for two days
            t[start + 100] = 95;                                        // 95 °C
            for (int k = 0; k < 10; k++) record.Power.Series("WS50M")[start + 200 + k] = double.NaN;   // −999 fills, as parsed
            var log = Screen(record);
            Assert.That(log.Rejected(PowerScreen.Source, "flatlines") - clean.Rejected(PowerScreen.Source, "flatlines"), Is.EqualTo(48), "the flatline");
            Assert.That(log.Rejected(PowerScreen.Source, "ranges") - clean.Rejected(PowerScreen.Source, "ranges"), Is.EqualTo(1), "the 95 °C hour");
            Assert.That(log.Rejected(PowerScreen.Source, "fill values") - clean.Rejected(PowerScreen.Source, "fill values"), Is.EqualTo(10), "the fills");
            Assert.That(double.IsNaN(t[start]) && double.IsNaN(t[start + 100]), Is.True, "removed, never repaired");
        }

        [Test]
        public void PlantedSnotelFaultsAreCaught()
        {
            var record = WeatherFixtures.Raw(WeatherFixtures.JacksonHole);
            Assume.That(record.Snotel.Count, Is.GreaterThan(0), "the fixture has a SNOTEL station");
            var clean = Screen(WeatherFixtures.Raw(WeatherFixtures.JacksonHole));
            var s = record.Snotel[0];
            string tag = s.Station.Triplet;
            var days = s.Days.Keys.ToList();
            int year = record.FirstLabelYear + 5;

            // Suspect flags on five January days: dropped.
            int jan = days.IndexOf(CivilDay.Of(year + 1, 1, 10));
            Assume.That(jan, Is.GreaterThan(0));
            for (int k = 0; k < 5; k++) { var d = s.Days[days[jan + k]]; d.PrecQc = 'S'; s.Days[days[jan + k]] = d; }

            // An 8 in PREC spike that falls back the next day: dropped, not counted as rain.
            int feb = days.IndexOf(CivilDay.Of(year + 1, 2, 10));
            { var d = s.Days[days[feb]]; d.Prec += 8; s.Days[days[feb]] = d; }

            // A 6 in SWE jump on a dry day: dropped.
            int dry = -1;
            for (int k = days.IndexOf(CivilDay.Of(year + 1, 3, 1)); k < days.Count - 1 && dry < 0; k++)
                if (days[k + 1] == days[k] + 1 && Math.Abs(s.Days[days[k + 1]].Prec - s.Days[days[k]].Prec) < 0.01) dry = k + 1;
            Assume.That(dry, Is.GreaterThan(0));
            { var d = s.Days[days[dry]]; d.Wteq += 6; s.Days[days[dry]] = d; }

            var log = Screen(record);
            Assert.That(log.Rejected(SnotelScreen.Source, "flags " + tag) - clean.Rejected(SnotelScreen.Source, "flags " + tag), Is.EqualTo(5), "suspect flags");
            Assert.That(log.Rejected(SnotelScreen.Source, "PREC accumulator " + tag) - clean.Rejected(SnotelScreen.Source, "PREC accumulator " + tag), Is.EqualTo(1), "the spike");
            Assert.That(log.Rejected(SnotelScreen.Source, "SWE pillow " + tag) - clean.Rejected(SnotelScreen.Source, "SWE pillow " + tag), Is.GreaterThanOrEqualTo(1), "the jump");
        }

        /// <summary>
        /// Planted faults are screened out and the rows come out as if those days had simply been missing: the faulty record is
        /// set against the clean record with the same days and hours blanked (the adversarial review, 2026-11-09). Against the
        /// untouched clean record, losing five real January days moved a sparse third's storm median by more than the gate,
        /// which measured the lost data, not the screen.
        /// </summary>
        [Test]
        public void TheRowsBarelyMoveWhenFaultsAreRemoved()
        {
            // A bad day in each of five winters, and a stuck POWER week; the reference has the same days and hours missing.
            WeatherRows Build(double prcp, double t2m)
            {
                var record = WeatherFixtures.Raw(WeatherFixtures.JacksonHole);
                var px = record.Reference!;
                for (int y = 0; y < 5; y++)
                {
                    int i = DayIndex(px.Days, CivilDay.Of(record.FirstLabelYear + 2 * y + 1, 1, 20));
                    px.Series("prcp")[i] = prcp;
                }
                var t = record.Power.Series("T2M");
                for (int k = 0; k < 24 * 7; k++) t[t.Count / 3 + k] = t2m;
                Screen(record);
                var s = record.Site;
                return WeatherCalibration.Build(WeatherBlend.Build(record), record, new ReferencePoints(s.BaseFeet, s.SummitFeet), WeatherTuning.Default).Rows;
            }
            var clean = Build(double.NaN, double.NaN);
            var rows = Build(900, -3.21);
            double Season(WeatherRows r) => Enumerable.Range(0, 21).Sum(k => r.TargetLiquid[k]);
            double Winter(WeatherRows r) => Enumerable.Range(6, 9).Average(k => r.TargetMeanTemp[k]) / 10.0;
            Assert.That(Season(rows) / Season(clean) - 1, Is.InRange(-0.02, 0.02), "October–April liquid");
            Assert.That(Winter(rows) - Winter(clean), Is.InRange(-0.2, 0.2), "December–February mean temperature (°F)");
            // Within 5 %, past the rows' own rounding (one 0.01 in: a third's 0.20 in median moves 5 % by one unit).
            Assert.That(Enumerable.Range(0, 36).Max(k => (Math.Abs(rows.StormMedian[k] - clean.StormMedian[k]) - 1) / (double)Math.Max(1, clean.StormMedian[k])), Is.LessThan(0.05), "storm medians");
        }

        static int DayIndex(System.Collections.Generic.List<int> days, int day)
        {
            int i = days.IndexOf(day);
            Assume.That(i, Is.GreaterThanOrEqualTo(0), $"day {day} in the recording");
            return i;
        }
    }
}
