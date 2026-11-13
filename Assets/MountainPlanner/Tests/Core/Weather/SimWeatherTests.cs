using System;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 PR B1: the weather in the world. The world's hours are the generator's (WX-33), whatever the step; a save
    /// reloads to the same future (WX-32 v2); new rows wait for October 1; the typical mountain's rows move to an area by
    /// their sun only; a lift at the summit holds in wind a lift at the base rides through (LO-11, WX-39).
    /// </summary>
    public sealed class SimWeatherTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;

        internal static WeatherRows Frozen(string site) =>
            WeatherRowsFile.FromBytes(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));

        /// <summary>The golden's weather: the western typical mountain's rows as they ship (the frozen Jackson Hole rows).</summary>
        internal static SimWeather Golden() => SimWeather.Own(Frozen(WeatherFixtures.JacksonHole));

        static SimDesign Resort(params LiftInput[] lifts) => new SimDesign(1, 1, 77, DayHours.Default, lifts, 1);

        static LiftInput Quad(ulong id, int topFeet) =>
            new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topFeet: topFeet);

        static void RunTo(SimWorld w, long faceMs, Action<SimWorld> eachHour = null)
        {
            long last = w.HourIndex;
            while (w.FaceMs < faceMs)
            {
                w.Step(P.IdleTickFactor);
                if (w.HourIndex != last) { last = w.HourIndex; eachHour?.Invoke(w); }
            }
        }

        [Test]
        public void TheWorldsHoursAreTheGenerators()
        {
            var wx = Golden();
            var w = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: wx);
            var seen = new List<WeatherHour>();
            var hours = new List<long>();
            RunTo(w, 4 * Day, x => { seen.Add(x.WeatherNow); hours.Add(x.HourIndex); });
            Assert.That(seen.Count, Is.GreaterThan(80));

            var gen = new WeatherGenerator(wx.Rows, SimWeather.Tuning(P));
            gen.RunTo(w.SeasonSeed, 2026, (int)hours[0]);
            for (int i = 0; i < seen.Count; i++)
            {
                var h = new WeatherHour();
                gen.Step(ref h);
                Assert.That(seen[i], Is.EqualTo(h), $"face hour {hours[i]}");
            }
        }

        [Test]
        public void ASkippedWeekIsAPlayedOne()
        {
            ulong Fold(int maxStep)
            {
                var w = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: Golden());
                w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
                var h = new StateHasher();
                while (w.FaceMs < 8 * Day)
                {
                    long last = w.HourIndex;
                    w.Step(maxStep);
                    if (w.HourIndex != last) { h.Add(w.HourIndex); h.Add(w.HourHash); h.Add(w.WeatherNow.Temp); }
                }
                return h.Value;
            }
            Assert.That(Fold(1), Is.EqualTo(Fold(int.MaxValue)));
        }

        [Test]
        public void ASaveReloadsToTheSameFuture()
        {
            var wx = Golden();
            var a = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: wx);
            a.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            RunTo(a, 3 * Day + 7 * Hour + 1234);
            byte[] bytes = a.SaveState();
            Assert.That(SimSidecar.VersionOf(bytes), Is.EqualTo(SimSidecar.Version));
            var b = SimSidecar.Read(bytes, P, a.Design, SimSite.None, out _, wx);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()));
            Assert.That(b.WeatherNow, Is.EqualTo(a.WeatherNow), "the hour comes back from October 1");
            var ha = new List<ulong>();
            var hb = new List<ulong>();
            RunTo(a, 6 * Day, x => ha.Add(x.HourHash));
            RunTo(b, 6 * Day, x => hb.Add(x.HourHash));
            Assert.That(hb, Is.EqualTo(ha));
        }

        [Test]
        public void NewRowsWaitForOctoberFirst()
        {
            var west = Golden();
            var east = SimWeather.Own(Frozen(WeatherFixtures.JacksonNh));
            var w = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: west);
            RunTo(w, 20 * Day);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeather, 0, east));
            Assert.That(w.Weather.RowsHash, Is.EqualTo(west.RowsHash), "this winter keeps its weather");
            Assert.That(w.WeatherNext.RowsHash, Is.EqualTo(east.RowsHash));

            // A save keeps both, and the area's rows given at the load don't jump the queue.
            var b = SimSidecar.Read(w.SaveState(), P, w.Design, SimSite.None, out _, east);
            Assert.That((b.Weather.RowsHash, b.WeatherNext.RowsHash), Is.EqualTo((west.RowsHash, east.RowsHash)));

            long nextOctober = new LabelCalendar(P).YearFaceDays * Day;
            RunTo(b, nextOctober + 2 * Hour);
            Assert.That(b.Weather.RowsHash, Is.EqualTo(east.RowsHash), "October 1 brings the new rows");
            Assert.That(b.WeatherNext, Is.Null);
            Assert.That(b.WeatherNow.LabelYear, Is.EqualTo(2027));
        }

        [Test]
        public void TheSameRowsAtNewPointsApplyAtOnce()
        {
            var wx = Golden();
            var w = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: wx);
            RunTo(w, 2 * Day);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeather, 0, wx.WithPoints(2000, 3000)));
            Assert.That(w.WeatherNext, Is.Null);
            Assert.That((w.Weather.BaseFeet, w.Weather.SummitFeet), Is.EqualTo((2000, 3000)));
        }

        [Test]
        public void AVersionFourSaveStartsTheWeatherAndKeepsItsHolds()
        {
            byte[] fixture = File.ReadAllBytes(Path.Combine(TestData.Folder(Path.Combine("formats", "v4-sim")), "golden.sim"));
            var w = SimSidecar.Read(fixture, P, SeasonRunner.GoldenDesign(1, false), SimSite.None, out var header, Golden());
            Assert.That(header.SidecarVersion, Is.EqualTo(4));
            Assert.That(w.Weather, Is.Not.Null);
            Assert.That(w.HeldNow(202), Is.True, "the saved override (60 mph) still holds it");
            for (int i = 0; i < 2000; i++) w.Step(1);
            Assert.That(w.WeatherKnown, "the weather runs from October 1 to the hour");
        }

        [Test]
        public void TheTypicalMountainMovesByItsSunAlone()
        {
            var template = Frozen(WeatherFixtures.JacksonHole);
            var vermont = WeatherGeneric.MoveTo(template, 43.1, -72.9);
            Assert.That(vermont.ZrefFeet, Is.EqualTo(template.ZrefFeet), "zref stays: the lapse carries the climate to the area");
            Assert.That(vermont.Limited, Is.EqualTo(1));
            Assert.That(vermont.SunriseMinute, Is.Not.EqualTo(template.SunriseMinute), "the sun is the area's");
            Assert.That(vermont.ContentHash(), Is.Not.EqualTo(template.ContentHash()), "the hash covers the move");
            Assert.That(WeatherGeneric.MoveTo(template, 43.1, -72.9).ContentHash(), Is.EqualTo(vermont.ContentHash()), "and is the same every time");
            Assert.That(WeatherGeneric.FileFor(-110.8), Is.EqualTo(WeatherGeneric.WestFile));
            Assert.That(WeatherGeneric.FileFor(-72.9), Is.EqualTo(WeatherGeneric.EastFile));
        }

        [Test]
        public void TheSummitRunsColderAndWindierThanTheBase()
        {
            var wx = Golden();
            var w = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: wx);
            var s = new SimSnapshot(P);
            long colder = 0, windier = 0, hours = 0;
            RunTo(w, 10 * Day, x =>
            {
                x.Write(s);
                hours++;
                if (s.WeatherSummit.Temp < s.WeatherBase.Temp) colder++;
                if (s.WeatherSummit.Wind > s.WeatherBase.Wind) windier++;
            });
            Assert.That(s.WeatherKnown);
            Assert.That(s.WeatherBase.Feet, Is.LessThan(s.WeatherSummit.Feet));
            Assert.That(colder, Is.GreaterThan(hours * 9 / 10), "the lapse");
            Assert.That(windier, Is.GreaterThan(hours * 9 / 10), "the exposure");
        }

        [Test]
        public void ASummitLiftHoldsInWindABaseLiftRidesThrough()
        {
            var wx = Golden();
            // The same detachable quad (the lift that holds first, LO-11) at the base and at the summit, for the whole sim year
            // at the shipped WindScale (the owner's 0.85, demo 94): the summit's holds come in the winter's storms.
            LiftInput Detachable(ulong id, int topFeet) =>
                new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.DetachableChair, 4, 6000, 360_000, topFeet: topFeet);
            var w = new SimWorld(P, Resort(Detachable(1, wx.BaseFeet), Detachable(2, wx.SummitFeet)), SimSite.None, 2026, weather: wx);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            int baseHeld = 0, summitHeld = 0;
            RunTo(w, new LabelCalendar(P).YearFaceDays * Day - Day, x =>
            {
                if (x.HeldNow(1)) baseHeld++;
                if (x.HeldNow(2)) summitHeld++;
            });
            TestContext.Progress.WriteLine($"held face hours over a sim year: base {baseHeld}, summit {summitHeld}");
            Assert.That(summitHeld, Is.GreaterThan(baseHeld));
            Assert.That(summitHeld, Is.GreaterThanOrEqualTo(5), "the summit holds in a winter's storms");
        }

        /// <summary>
        /// WX-51: the weather adds no allocation to the hour step. The world without weather allocates a little at its day
        /// boundaries (the crowd's day, not the weather's), so the weather is measured against it over the same face days.
        /// </summary>
        [Test]
        public void TheWeatherAllocatesNothing()
        {
            long Bytes(bool weather)
            {
                var w = new SimWorld(P, Resort(Quad(1, 0), Quad(2, 9000)), SimSite.None, 2026, weather: weather ? Golden() : null);
                var s = new SimSnapshot(P);
                RunTo(w, 2 * Day);
                long before = GC.GetAllocatedBytesForCurrentThread();
                while (w.FaceMs < 6 * Day) w.Step(P.IdleTickFactor, s);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            try { GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { Assert.Ignore("no per-thread allocation counter here"); return; }
            long without = Bytes(false), with = Bytes(true);
            TestContext.Progress.WriteLine($"four face days: {without} B without weather, {with} B with");
            Assert.That(with, Is.LessThanOrEqualTo(without));
        }

        [Test]
        public void AMonthsSkipFitsItsBudget()
        {
            var w = new SimWorld(P, Resort(Quad(1, 0)), SimSite.None, 2026, weather: Golden());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            RunTo(w, 31 * Day);
            watch.Stop();
            TestContext.Progress.WriteLine($"31 face days with weather: {watch.Elapsed.TotalMilliseconds:F0} ms");
            Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(5));
        }
    }
}
