using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Engine-free. Task 4.05: the integer generator over <b>frozen</b> rows (TestData/weather/&lt;site&gt;/weather.rows), never
    /// rows recomputed with floats here, so the golden is the same on every runtime (WX-31, WX-33). The rows are re-recorded
    /// only with an approved calibration change (<see cref="RecordFrozenRows"/>), and the goldens re-pin with them.
    /// </summary>
    public sealed class WeatherGeneratorTests
    {
        const ulong Seed = 0x4D50_5758_0000_0001UL;
        const int LabelYear = 2026;

        /// <summary>WX-33: the fold of every field of every face hour of label year 2026 under <see cref="Seed"/>, per site.</summary>
        static readonly Dictionary<string, ulong> Golden = new Dictionary<string, ulong>
        {
            [WeatherFixtures.JacksonHole] = 0x80A9552A8A216584UL,
            [WeatherFixtures.OlympicValley] = 0x8220C8F5E28C4476UL,
            [WeatherFixtures.StevensPass] = 0xBC3BD9A3B36E922EUL,
            [WeatherFixtures.WinterPark] = 0x88B8FBCF657B7A91UL,
            [WeatherFixtures.JacksonNh] = 0x8A9EC67AAB75F0D1UL,
            [WeatherFixtures.Snowshoe] = 0xFC8DC1FDEABA62CEUL,
            [WeatherFixtures.SnowRidge] = 0xFAA4A529F8FFE6FDUL,
        };

        static WeatherRows Frozen(string site) =>
            WeatherRowsFile.FromBytes(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));

        static int Year => WeatherGenerator.HoursInYear(WeatherTuning.Default);

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX33_SameSeedSameYear(string site)
        {
            var rows = Frozen(site);
            ulong a = PlayYear(rows, Seed), b = PlayYear(rows, Seed);
            Assert.That(b, Is.EqualTo(a), "two plays of one seed");
            Assert.That(PlayYear(rows, Seed + 1), Is.Not.EqualTo(a), "another seed is another year");
            TestContext.Progress.WriteLine($"{site}: WX-33 fold 0x{a:X16}UL");
            Assert.That(a, Is.EqualTo(Golden[site]), $"the {site} golden (an approved behaviour change re-pins it)");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX32_SaveAndReloadGivesTheSameFuture(string site)
        {
            var rows = Frozen(site);
            var tuning = WeatherTuning.Default;
            var h = new WeatherHour();
            // Stop inside a storm and at a quiet hour: the storm's slots are what Restore rebuilds.
            foreach (bool inStorm in new[] { true, false })
            {
                var live = new WeatherGenerator(rows, tuning);
                live.Reset(Seed, LabelYear);
                int start = Year / 3;
                while (live.Hour < start || (live.State == WeatherRows.Storm) != inStorm) live.Step(ref h);
                var saved = new Collect();
                live.VisitState(saved);

                var loaded = new WeatherGenerator(rows, tuning);
                loaded.Reset(Seed ^ 0xFFFF, LabelYear - 3);   // any state: the load replaces it all
                var fill = new Fill(saved.Values);
                loaded.VisitState(fill);
                loaded.Restore();
                Assert.That(fill.Read, Is.EqualTo(saved.Values.Count), "the load reads what the save wrote");

                var ha = new WeatherHour(); var hb = new WeatherHour();
                for (int i = 0; i < 400; i++)
                {
                    live.Step(ref ha); loaded.Step(ref hb);
                    Assert.That(Fold(Fnv, hb), Is.EqualTo(Fold(Fnv, ha)), $"{site}: hour {ha.HourOfYear} after a {(inStorm ? "storm" : "quiet")} save");
                }
            }
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX56_RunToEqualsStepping(string site)
        {
            var rows = Frozen(site);
            var a = new WeatherGenerator(rows, WeatherTuning.Default);
            var b = new WeatherGenerator(rows, WeatherTuning.Default);
            a.Reset(Seed, LabelYear);
            var h = new WeatherHour();
            while (a.Hour < 1234) a.Step(ref h);
            b.RunTo(Seed, LabelYear, 1234);
            var ha = new WeatherHour(); var hb = new WeatherHour();
            for (int i = 0; i < 200; i++) { a.Step(ref ha); b.Step(ref hb); Assert.That(Fold(Fnv, hb), Is.EqualTo(Fold(Fnv, ha))); }
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX05_TheRunAheadCopyFollowsTheTruth(string site)
        {
            var rows = Frozen(site);
            var truth = new WeatherGenerator(rows, WeatherTuning.Default);
            var ahead = new WeatherGenerator(rows, WeatherTuning.Default);
            truth.RunTo(Seed, LabelYear, 900);
            ahead.CopyStateFrom(truth);
            var ha = new WeatherHour(); var hb = new WeatherHour();
            for (int i = 0; i < 400; i++) { truth.Step(ref ha); ahead.Step(ref hb); Assert.That(Fold(Fnv, hb), Is.EqualTo(Fold(Fnv, ha))); }
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX51_AStepAllocatesNothingAndFitsItsBudget(string site)
        {
            var rows = Frozen(site);
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            var h = new WeatherHour();
            gen.Reset(Seed, LabelYear);
            for (int i = 0; i < 500; i++) gen.Step(ref h);   // warm up
            var watch = new Stopwatch();
            long before;
            // Unity's Mono has no per-thread counter (P2-06); .NET (tools/domain-tests, CI) measures it.
            try { before = GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { Assert.Ignore("no per-thread allocation counter here"); return; }
            watch.Start();
            int steps = 0;
            for (int y = 0; y < 3; y++)
            {
                gen.Reset(Seed + (ulong)y, LabelYear + y);
                for (int i = 0; i < Year; i++, steps++) gen.Step(ref h);
            }
            watch.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.Zero, $"bytes allocated over {steps} face hours");
            Budget.AtMost(watch.Elapsed.TotalMilliseconds / steps, 0.01, "WX-51: generator per face hour");

            watch.Restart();
            gen.RunTo(Seed, LabelYear, Year - 1);
            Budget.AtMost(watch.Elapsed.TotalMilliseconds, 50, "WX-51: re-run from October 1 to the year's last hour");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX24_StormsReleaseTheirTotalUnderTheRateCap(string site)
        {
            var rows = Frozen(site);
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            var h = new WeatherHour();
            int storms = 0, checkedStorms = 0;
            for (int s = 0; s < 5; s++)
            {
                gen.Reset(Seed + (ulong)s * 977, LabelYear);
                int ordinal = -1, fell = 0, total = 0;
                for (int i = 0; i < Year; i++)
                {
                    gen.Step(ref h);
                    Assert.That(h.Liquid, Is.InRange(0, gen.RateCap), $"{site}: the rate cap at hour {h.HourOfYear}");
                    if (h.StormOrdinal != ordinal)
                    {
                        // Its hours hold every slot; the hour it ended in may also hold the start of the Clearing flurries.
                        if (ordinal >= 0 && total > 0) { checkedStorms++; Assert.That(fell, Is.InRange(total, total + gen.RateCap), $"{site}: storm {ordinal} fell its drawn total"); }
                        ordinal = h.StormOrdinal; fell = 0; total = h.StormTotal;
                        if (ordinal >= 0) storms++;
                    }
                    if (h.StormOrdinal >= 0) fell += h.Liquid;
                }
            }
            Assert.That(storms, Is.GreaterThan(50), "storms played");
            Assert.That(checkedStorms, Is.GreaterThan(50), "storms checked");
        }

        [Test]
        public void WX27_CloudBaseIsTheDepressionTimesTheRule()
        {
            // The pin: 2.28 ft... per 0.1 °F of dew-point depression is 228 ft per °F (the meteorologist's 2.5 °C per 1,000 ft).
            Assert.That(WeatherGenerator.CloudBaseFeetPerTenthF, Is.EqualTo(228));
            var rows = Frozen(WeatherFixtures.JacksonHole);
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            var h = new WeatherHour();
            gen.Reset(Seed, LabelYear);
            for (int i = 0; i < 2000; i++)
            {
                gen.Step(ref h);
                Assert.That(h.CloudBaseFeet, Is.EqualTo(Math.Max(0, h.Temp - h.Dew) * WeatherGenerator.CloudBaseFeetPerTenthF / 10));
            }
        }

        [Test]
        public void WX21_TheTuningDefaultsMatchPacing()
        {
            var p = MountainPlanner.Simulation.Pacing.Default;
            var t = WeatherTuning.Default;
            Assert.That(t.CalendarDays, Is.EqualTo(p.SeasonCalendarDays), "calendar days");
            Assert.That(t.FaceDays, Is.EqualTo(p.SeasonDays), "face days (Pacing.SeasonDays)");
        }

        // ---- weather.rows (0.3 §5) ----

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void RowsFile_ReadsAndRewritesByteForByte(string site)
        {
            byte[] bytes = TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName);
            var rows = WeatherRowsFile.FromBytes(bytes);
            Assert.That(WeatherRowsFile.ToBytes(rows), Is.EqualTo(bytes));
            Assert.That(WeatherRowsFile.ToBytes(rows.Clone()), Is.EqualTo(bytes), "a clone writes the same bytes");
            Assert.That(rows.Clone().ContentHash(), Is.EqualTo(rows.ContentHash()));
        }

        /// <summary>
        /// The guard (adversarial review, 2026-11-09): the frozen rows every test plays are what today's calibration makes from
        /// the recorded sources. A calibration change without re-recording (RecordFrozenRows) fails here, not silently later.
        /// </summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void RowsFile_TheFrozenRowsAreTodaysCalibration(string site)
        {
            string frozen = WeatherRowsFile.Sha256(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));
            string today = WeatherRowsFile.Sha256(WeatherRowsFile.ToBytes(WeatherFixtures.Calibrated(site).Rows));
            Assert.That(today, Is.EqualTo(frozen), $"{site}: weather.rows is stale; re-record it (an approved calibration change)");
        }

        [Test]
        public void RowsFile_TheV1FixtureStillReads()
        {
            string folder = TestData.Folder("formats/v1-weather-rows");
            byte[] bytes = TestData.Bytes(folder, WeatherRowsFile.FileName);
            var rows = WeatherRowsFile.FromBytes(bytes);
            Assert.That(WeatherRowsFile.ToBytes(rows), Is.EqualTo(bytes), "today's writer reproduces v1 byte for byte");
            Assert.That(WeatherRowsFile.Sha256(bytes), Is.EqualTo(File.ReadAllText(Path.Combine(folder, "weather.rows.sha256")).Trim()));
            Assert.That(rows.ZrefFeet, Is.GreaterThan(5000), "Jackson Hole's zref");
        }

        [Test]
        public void RowsFile_RejectsDamagedBytes()
        {
            byte[] good = WeatherRowsFile.ToBytes(Frozen(WeatherFixtures.JacksonHole));
            byte[] magic = (byte[])good.Clone(); magic[0] = (byte)'X';
            byte[] version = (byte[])good.Clone(); version[4] = 9;
            byte[] truncated = new byte[good.Length - 3]; Array.Copy(good, truncated, truncated.Length);
            byte[] extra = new byte[good.Length + 4]; Array.Copy(good, extra, good.Length);
            byte[] resized = (byte[])good.Clone();
            // The first array (SunriseMinute) has its length right after the header and eight scalars.
            resized[8 + 8 * 4] ^= 1;
            foreach (var (bad, why) in new[] { (magic, "magic"), (version, "version"), (truncated, "truncated"), (extra, "extra bytes"), (resized, "resized array") })
                Assert.Throws<InvalidDataException>(() => WeatherRowsFile.FromBytes(bad), why);
        }

        [Test]
        public void RowsFile_ChecksTheManifestHash()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mp-weather-rows-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(folder);
            try
            {
                var rows = Frozen(WeatherFixtures.OlympicValley);
                string sha = WeatherRowsFile.Write(folder, rows);
                Assert.That(WeatherRowsFile.Read(folder, sha).ContentHash(), Is.EqualTo(rows.ContentHash()));
                Assert.Throws<InvalidDataException>(() => WeatherRowsFile.Read(folder, new string('0', 64)));
            }
            finally { Directory.Delete(folder, true); }
        }

        // ---- Recording (explicit: an approved calibration change) ----

        [Test, Explicit("Re-records TestData/weather/<site>/weather.rows from today's calibration (an approved change; re-pin the goldens)")]
        public void RecordFrozenRows()
        {
            foreach (string site in WeatherFixtures.Sites)
            {
                string sha = WeatherRowsFile.Write(TestData.Folder("weather/" + site), WeatherFixtures.Calibrated(site).Rows);
                TestContext.Progress.WriteLine($"{site}: weather.rows {sha}");
            }
        }

        [Test, Explicit("Writes the frozen v1 weather.rows fixture (once; never edited after merge)")]
        public void RecordV1Fixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v1-weather-rows");
            Directory.CreateDirectory(folder);
            byte[] bytes = TestData.Bytes(TestData.Folder("weather/" + WeatherFixtures.JacksonHole), WeatherRowsFile.FileName);
            File.WriteAllBytes(Path.Combine(folder, WeatherRowsFile.FileName), bytes);
            File.WriteAllText(Path.Combine(folder, "weather.rows.sha256"), WeatherRowsFile.Sha256(bytes) + "\n");
        }

        // ---- Helpers ----

        static ulong PlayYear(WeatherRows rows, ulong seed)
        {
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            gen.Reset(seed, LabelYear);
            var h = new WeatherHour();
            ulong fold = Fnv;
            for (int i = 0; i < Year; i++)
            {
                gen.Step(ref h);
                fold = Fold(fold, h);
            }
            return fold;
        }

        const ulong Fnv = 14695981039346656037UL;

        static ulong Fold(ulong f, in WeatherHour h)
        {
            f = Add(f, h.LabelYear); f = Add(f, h.HourOfYear); f = Add(f, h.HourOfDay);
            f = Add(f, h.Third); f = Add(f, h.NeighbourThird); f = Add(f, h.NeighbourWeight);
            f = Add(f, h.State); f = Add(f, h.Pattern); f = Add(f, h.Night ? 1 : 0);
            f = Add(f, h.Temp); f = Add(f, h.Dew); f = Add(f, h.Lapse); f = Add(f, h.DewLapse); f = Add(f, h.PrecipGradient);
            f = Add(f, h.Pool); f = Add(f, h.Fog ? 1 : 0); f = Add(f, h.Liquid);
            f = Add(f, h.StormOrdinal); f = Add(f, h.StormOctant); f = Add(f, h.StormDirection); f = Add(f, h.StormTotal); f = Add(f, h.FrontPassed ? 1 : 0);
            f = Add(f, h.Wind); f = Add(f, h.Direction); f = Add(f, h.GustFactor);
            f = Add(f, h.Cloud); f = Add(f, h.Clearness); f = Add(f, h.CloudBaseFeet);
            f = Add(f, h.SeasonOffset); f = Add(f, h.SeasonShare);
            return f;
        }

        static ulong Add(ulong f, int v)
        {
            for (int i = 0; i < 4; i++) { f ^= (byte)(v >> (8 * i)); f *= 1099511628211UL; }
            return f;
        }

        sealed class Collect : IWeatherStateVisitor
        {
            public readonly List<long> Values = new List<long>();
            public void Int(ref int value) => Values.Add(value);
            public void Long(ref long value) => Values.Add(value);
        }

        sealed class Fill : IWeatherStateVisitor
        {
            readonly List<long> _v;
            public int Read;
            public Fill(List<long> v) => _v = v;
            public void Int(ref int value) => value = (int)_v[Read++];
            public void Long(ref long value) => value = _v[Read++];
        }
    }
}
