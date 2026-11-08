using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests). Each test names its p4-02 rule.
    public sealed class SimWorldTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;

        static SimDesign Resort(int lifts = 1, int trails = 1, long revision = 1)
        {
            var l = new LiftInput[lifts];
            for (int i = 0; i < lifts; i++) l[i] = new LiftInput((ulong)(10 + i), true);
            return new SimDesign(revision, (ulong)revision, 77, DayHours.Default, l, trails);
        }

        static SimWorld World(SimDesign d = null) => new SimWorld(P, d ?? Resort(), SimSite.None, 2026);

        static void RunTo(SimWorld w, long faceMs)
        {
            while (w.FaceMs < faceMs) w.Step(P.IdleTickFactor);
        }

        static void Open(SimWorld w) => w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));

        [Test]
        public void ANewGameStartsOnOctoberFirstAtTheOpening()
        {
            var w = World();
            Assert.That(w.FaceMs, Is.EqualTo(DayHours.Default.OpenSecond * 1000L));
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.PreSeason));
            Assert.That(w.SeasonDay, Is.Zero);
            var s = new SimSnapshot(P);
            w.Write(s);
            Assert.That((s.Month, s.DayOfMonth, s.ViewYear, s.SecondOfDay), Is.EqualTo((10, 1, 2026, 30600)));
        }

        [Test]
        public void TM02_TheDayRateRunsFromOpeningToStopOnSeasonDaysOnly()
        {
            var w = World();
            // Pre-season: the day nobody skis runs whole at the night rate (TM-14).
            long f = w.FaceMs;
            w.Step(1);
            Assert.That(w.FaceMs - f, Is.EqualTo(P.NightFaceMsPerTick));
            Open(w);
            RunTo(w, Day + DayHours.Default.OpenSecond * 1000L);
            w.Step(1);   // the opening: Day 1
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.Season));
            f = w.FaceMs;
            w.Step(1);
            Assert.That(w.FaceMs - f, Is.EqualTo(P.DayFaceMsPerTick), "4 face seconds a tick while the resort is open");
            Assert.That(P.TicksPerFaceHour(true), Is.EqualTo(900));
            Assert.That(P.TicksPerFaceHour(false), Is.EqualTo(150));
            RunTo(w, Day + DayHours.Default.StopSecond * 1000L);
            Assert.That(w.FaceMs, Is.EqualTo(Day + DayHours.Default.StopSecond * 1000L), "a step stops on the stop exactly");
            f = w.FaceMs;
            w.Step(1);
            Assert.That(w.FaceMs - f, Is.EqualTo(P.NightFaceMsPerTick));
        }

        [Test]
        public void TM11_DayOneComesAtAnOpeningWithTheMountainOpenAndALiftServingARun()
        {
            var w = World(Resort(trails: 0));
            Open(w);
            RunTo(w, Day + 10 * Hour);
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.PreSeason), "no built trail: no lift serves a run");
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, Resort(revision: 2)));
            RunTo(w, 2 * Day + 10 * Hour);
            Assert.That((w.Phase, w.SeasonDay), Is.EqualTo((SeasonPhase.Season, 1)));
            RunTo(w, 4 * Day + 10 * Hour);
            Assert.That(w.SeasonDay, Is.EqualTo(3), "each opening counts a day");
        }

        [Test]
        public void TM11_ClosingInTheSeasonEndsItAndSummerRefusesOpen()
        {
            var w = World();
            Open(w);
            w.Step(1);
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.Season));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 0));
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.Summer));
            long before = w.Events.Count;
            Open(w);
            Assert.That(w.Open, Is.False);
            Assert.That(w.Events.TryGet(before, out var e) && e.Type == SimEventType.CommandRejected && e.Detail == (ushort)SimRefusal.NotInSummer);
        }

        [Test]
        public void TM11_ASeasonNotBegunByAprilFirstGoesToSummerAndOctoberFirstRollsTheYear()
        {
            var w = World();
            var cal = new LabelCalendar(P);
            ulong seed1 = w.SeasonSeed;
            RunTo(w, cal.April1FaceDay * Day + Hour);
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.Summer));
            RunTo(w, cal.YearFaceDays * Day + Hour);
            Assert.That((w.Phase, w.SeasonNumber, w.Open), Is.EqualTo((SeasonPhase.PreSeason, 2, false)));
            Assert.That(w.SeasonSeed, Is.Not.EqualTo(seed1), "SI-15: a new season draws a new seed");
            var again = World();
            Assert.That(again.SeasonSeed, Is.EqualTo(seed1), "the seed is a keyed hash of the resort's seed and the season");
        }

        [Test]
        public void SI15_APinnedSeedHoldsUntilUnpinned()
        {
            var w = World();
            w.ApplyNow(SimCommand.Of(SimCommandKind.PinSeasonSeed, 42));
            Assert.That((w.SeasonSeed, w.SeedPinned), Is.EqualTo((42UL, true)));
            w.ApplyNow(SimCommand.Of(SimCommandKind.UnpinSeasonSeed));
            Assert.That(w.SeedPinned, Is.False);
            Assert.That(w.SeasonSeed, Is.EqualTo(World().SeasonSeed));
        }

        [Test]
        public void RP07_ARopeRunsOnlyInTheHoursOfAnOpenSeasonDay()
        {
            var w = World();
            RunTo(w, Day + 10 * Hour);
            Assert.That(w.RunMsOf(10), Is.Zero, "pre-season, closed: no chair moves");
            Open(w);
            RunTo(w, 3 * Day);
            long dayMs = w.RunMsOf(10);
            // Day 1 at the next opening: 8 hours at 900 ticks an hour, 100 ms a tick.
            Assert.That(dayMs, Is.EqualTo(8L * 900 * P.TickMs));
            RunTo(w, 3 * Day + 8 * Hour);
            Assert.That(w.RunMsOf(10), Is.EqualTo(dayMs), "the night: parked");
        }

        [Test]
        public void SI20_ANewDesignKeepsEachLiftsRopeByID()
        {
            var w = World();
            Open(w);
            RunTo(w, Day + 9 * Hour);
            long ms = w.RunMsOf(10);
            Assert.That(ms, Is.GreaterThan(0));
            var d = new SimDesign(2, 2, 77, DayHours.Default, new[] { new LiftInput(5, true), new LiftInput(10, true) }, 1);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, d));
            Assert.That((w.RunMsOf(10), w.RunMsOf(5)), Is.EqualTo((ms, 0L)));
        }

        [Test]
        public void TM15_TheDayReportArrivesAtClosingWithEachLiftsMinutes()
        {
            var w = World(Resort(lifts: 2));
            Open(w);
            RunTo(w, DayHours.Default.StopSecond * 1000L);
            w.Step(1);
            var r = w.LastReport;
            Assert.That((r.FaceDay, r.SeasonDay, r.Phase, r.LiftCount), Is.EqualTo((0, 1, SeasonPhase.Season, 2)));
            Assert.That((r.RunMinutes[0], r.RunMinutes[1]), Is.EqualTo((480, 480)));
            RunTo(w, 2 * Day + Hour);
            Assert.That(w.HistoryCount, Is.EqualTo(2), "a history line for every face day, at midnight");
            Assert.That(w.HistoryRunMinutes(1), Is.EqualTo(960));
        }

        [Test]
        public void TM06_NothingPausesAtClosingAndTheClockRunsOn()
        {
            var w = World();
            Open(w);
            long rejected = 0, pauses = 0;
            RunTo(w, 3 * Day);
            for (long q = 0; q < w.Events.Count; q++)
                if (w.Events.TryGet(q, out var e))
                {
                    if (e.Type == SimEventType.PauseRequested) pauses++;
                    if (e.Type == SimEventType.CommandRejected) rejected++;
                }
            Assert.That((pauses, rejected), Is.EqualTo((0L, 0L)));
        }

        [Test]
        public void TM06_SkipToMorningLandsAnHourBeforeOpeningAndNotWhileTheLiftsRun()
        {
            var w = World();
            Open(w);
            w.Step(1);
            Assert.That(w.SkipTarget(SkipKind.Morning, out _), Is.False, "the lifts are running");
            RunTo(w, 18 * Hour);
            Assert.That(w.SkipTarget(SkipKind.Morning, out long t), Is.True);
            Assert.That(t, Is.EqualTo(Day + (DayHours.Default.OpenSecond - 3600) * 1000L), "7:30 the next day");
            RunTo(w, Day + 7 * Hour + 45 * 60_000L);
            Assert.That(w.SkipTarget(SkipKind.Morning, out _), Is.False, "already morning");
        }

        [Test]
        public void TM14_TheNextThirdIsForPreSeasonAndTheNextMonthForSummer()
        {
            var w = World();
            Assert.That(w.SkipTarget(SkipKind.NextThird, out long t), Is.True);
            var cal = new LabelCalendar(P);
            Assert.That(t, Is.EqualTo(cal.NextThirdStart(0) * Day + (DayHours.Default.OpenSecond - 3600) * 1000L));
            Assert.That(w.SkipTarget(SkipKind.NextMonth, out _), Is.False);
            Open(w);
            w.Step(1);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 0));
            Assert.That(w.SkipTarget(SkipKind.NextThird, out _), Is.False);
            Assert.That(w.SkipTarget(SkipKind.NextMonth, out _), Is.True);
        }

        [Test]
        public void SI03_AStaleCommandIsRefused()
        {
            var w = World();
            for (int i = 0; i < 30; i++) w.Step(1);
            long before = w.Events.Count;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1, issuedAgainstTick: 0));
            Assert.That(w.Open, Is.False);
            Assert.That(w.Events.TryGet(before, out var e) && e.Detail == (ushort)SimRefusal.Stale);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1, issuedAgainstTick: w.Tick - 5));
            Assert.That(w.Open, Is.True);
            Assert.That(w.Log.Count, Is.EqualTo(1), "SI-22: only applied world commands are logged");
            Assert.That(w.Log[0].Tick, Is.EqualTo(w.Tick));
        }

        [Test]
        public void SI11_TM03_TheIdleStepGivesTheSameHashesAsSingleTicks()
        {
            var one = new List<(long, ulong)>();
            var many = new List<(long, ulong)>();
            var a = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            var b = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            var script = SeasonRunner.GoldenScript(P);
            SeasonRunner.Run(a, script, 12 * Day, (i, h) => one.Add((i, h)), maxStep: 1);
            SeasonRunner.Run(b, script, 12 * Day, (i, h) => many.Add((i, h)));
            Assert.That(many, Is.EqualTo(one));
            Assert.That(one.Count, Is.GreaterThan(12 * 23));
        }

        [Test]
        public void SI08_TheGoldenSeasonsHashesArePinned()
        {
            ulong fold = SeasonRunner.GoldenFold(P, 2 * new LabelCalendar(P).YearFaceDays);
            TestContext.Progress.WriteLine($"golden fold: 0x{fold:X16}");
            Assert.That(fold, Is.EqualTo(SeasonRunner.GoldenFoldValue), "a behaviour change to the simulation needs an approved re-pin");
            Assert.That(SeasonRunner.GoldenFold(P, 2 * new LabelCalendar(P).YearFaceDays, maxStep: 1), Is.EqualTo(fold), "TM-03: one tick at a time");
        }

        [Test, Category("Nightly"), Explicit("Ten label years: the nightly job (repo-checks.yml sim-nightly)")]
        public void SI08_TenSeasonsGiveTheSameHashesAtAnyStep()
        {
            int days = 10 * new LabelCalendar(P).YearFaceDays;
            var site = new SimSite(43.59, -110.83);
            Assert.That(SeasonRunner.GoldenFold(P, days, maxStep: 1, site: site), Is.EqualTo(SeasonRunner.GoldenFold(P, days, site: site)));
        }

        [Test]
        public void SI08_ThePacingTableIsInTheHash()
        {
            var other = Pacing.Parse("LapsTarget = 5");
            Assert.That(other.ContentsHash, Is.Not.EqualTo(P.ContentsHash));
            Assert.That(new SimWorld(other, Resort(), SimSite.None, 2026).Hash(), Is.Not.EqualTo(World().Hash()));
        }

        [Test]
        public void LO07_TheHoursAreCutToCivilDaylight()
        {
            // Far north in mid-December the day is short: the resort opens at civil dawn and stops at dusk.
            var w = new SimWorld(P, Resort(), new SimSite(68.4, 18.1), 2026);
            var cal = new LabelCalendar(P);
            RunTo(w, (cal.December1FaceDay + 5) * Day + Hour);
            var s = new SimSnapshot(P);
            w.Write(s);
            Assert.That(s.HoursTrimmed, Is.True);
            Assert.That(s.OpenSecond, Is.GreaterThan(DayHours.Default.OpenSecond));
            Assert.That(s.OpenSecond % 60, Is.Zero);
            // Jackson Hole keeps its hours all season.
            var jh = new SimWorld(P, Resort(), new SimSite(43.59, -110.83), 2026);
            RunTo(jh, (cal.December1FaceDay + 5) * Day + Hour);
            jh.Write(s);
            Assert.That(s.HoursTrimmed, Is.False);
        }

        [Test]
        public void SI05_EveryRowReadsOnlyWhatAnEarlierRowWritesOrAnInput()
        {
            var written = SystemsTable.Inputs;
            foreach (var row in SystemsTable.Rows)
            {
                var missing = row.Reads & ~written;
                Assert.That(missing, Is.EqualTo(SimColumns.None), $"{row.Name} reads {missing} before anything writes it");
                written |= row.Writes;
            }
        }

        [Test]
        public void SI16_ASnapshotFitsIn64KB()
        {
            Assert.That(new SimSnapshot(P).SizeBytes, Is.LessThanOrEqualTo(64 * 1024));
        }

        [Test]
        public void SI23_BG01_ATickAllocatesNothing()
        {
            var w = World(Resort(lifts: 20));
            Open(w);
            var s = new SimSnapshot(P);
            for (int i = 0; i < 2000; i++) w.Step(P.IdleTickFactor, s);   // warm up into the season
            long before;
            // Unity's Mono has no per-thread counter (P2-06); .NET (tools/domain-tests, CI) measures it.
            try { before = GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { Assert.Ignore("no per-thread allocation counter here"); return; }
            for (int i = 0; i < 20_000; i++) w.Step(P.IdleTickFactor, s);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.Zero, "bytes allocated over 20,000 steps");
        }

        [Test]
        public void SI18_TheReaderCountsEventsItMissed()
        {
            var ring = new EventRing(8);
            var snap = new SimSnapshot(P);
            var reader = new EventReader();
            for (int i = 0; i < 3; i++) ring.Add(i, SimEventType.FaceDayBegan, -1, i);
            snap.FirstEventSeq = ring.CopyNewest(snap.Events, out snap.EventCount);
            var seen = new List<long>();
            reader.Read(snap, e => seen.Add(e.Value));
            reader.Read(snap, e => seen.Add(e.Value));
            Assert.That(seen, Is.EqualTo(new long[] { 0, 1, 2 }), "each event once");
            for (int i = 3; i < 20; i++) ring.Add(i, SimEventType.FaceDayBegan, -1, i);
            snap.FirstEventSeq = ring.CopyNewest(snap.Events, out snap.EventCount);
            reader.Read(snap, e => seen.Add(e.Value));
            Assert.That(reader.Missed, Is.EqualTo(9), "the ring holds the newest 8 of 17 new ones");
            Assert.That(seen[seen.Count - 1], Is.EqualTo(19));
        }
    }

    public sealed class SimulationHostTests
    {
        static readonly Pacing P = Pacing.Default;

        sealed class FakeTime : IRealTime
        {
            public long Microseconds { get; set; }
        }

        static (SimulationHost, FakeTime) Host()
        {
            var t = new FakeTime();
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            return (new SimulationHost(w, t, threaded: false), t);
        }

        [Test]
        public void TheHostStartsPausedAndPublishes()
        {
            var (h, t) = Host();
            var s = h.Latest;
            Assert.That(s, Is.Not.Null);
            Assert.That((s.Paused, s.Tick, s.Speed), Is.EqualTo((true, 0L, 1)));
            t.Microseconds += 5_000_000;
            h.Service();
            Assert.That(h.Latest.Tick, Is.Zero, "paused: no time passes");
        }

        [TestCase(1, 10)]
        [TestCase(2, 20)]
        [TestCase(3, 40)]
        [TestCase(4, 80)]
        public void TM02_EachSpeedRunsItsTicksPerRealSecond(int speed, int ticks)
        {
            var (h, t) = Host();
            h.Send(SimCommandKind.SetSpeed, speed);
            h.Service();
            for (int i = 0; i < 100; i++) { t.Microseconds += 10_000; h.Service(); }
            Assert.That(h.Latest.Tick, Is.EqualTo(ticks));
            Assert.That(h.Latest.SustainedPermille, Is.EqualTo(1000));
        }

        [Test]
        public void SI11_AStallRunsSlowerRatherThanCatchingUp()
        {
            var (h, t) = Host();
            h.Send(SimCommandKind.SetSpeed, 1);
            h.Service();
            t.Microseconds += 10_000_000;   // ten seconds frozen
            h.Service();
            Assert.That(h.Latest.Tick, Is.EqualTo(P.MaxBacklogRealTicks), "the backlog is capped");
        }

        [Test]
        public void SI03_ACommandWhilePausedAppliesAtOnce()
        {
            var (h, _) = Host();
            h.Send(SimCommandKind.SetMountainOpen, 1);
            h.Service();
            Assert.That(h.Latest.Open, Is.True);
            Assert.That(h.Latest.Tick, Is.Zero);
        }

        [Test]
        public void TM06_ASkipLandsWhereTheWorldSaysAndKeepsThePause()
        {
            var (h, t) = Host();
            h.Send(SimCommandKind.SkipToMorning);
            for (int i = 0; i < 100 && (h.Latest.Skip != SkipKind.None || h.Latest.Tick == 0); i++) h.Service();
            var s = h.Latest;
            Assert.That(s.Skip, Is.EqualTo(SkipKind.None));
            Assert.That((s.FaceDay, s.SecondOfDay), Is.EqualTo((1, DayHours.Default.OpenSecond - 3600)));
            Assert.That(s.Paused, Is.True, "paused stays paused");
        }

        [Test]
        public void TM03_ASkipGivesTheSameStateAsRunningTheTicks()
        {
            var (h, _) = Host();
            h.Send(SimCommandKind.SkipToNextThird);
            for (int i = 0; i < 1000 && (h.Latest.Skip != SkipKind.None || h.Latest.Tick == 0); i++) h.Service();
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            while (w.FaceMs < h.Latest.FaceMs) w.Step(1);
            var hw = h.WorldUnsafe;
            Assert.That((hw.Tick, hw.FaceMs, hw.Phase, hw.SeasonDay, hw.HistoryCount), Is.EqualTo((w.Tick, w.FaceMs, w.Phase, w.SeasonDay, w.HistoryCount)));
            Assert.That(hw.Hash(), Is.EqualTo(w.Hash()));
        }

        [Test]
        public void ASkipCanBeCancelled()
        {
            var (h, _) = Host();
            h.Send(SimCommandKind.SkipToNextThird);
            h.Send(SimCommandKind.CancelSkip);
            h.Service();
            Assert.That(h.Latest.Skip, Is.EqualTo(SkipKind.None));
            h.Send(SimCommandKind.SkipToNextMonth);
            h.Service();
            Assert.That(h.Latest.Skip, Is.EqualTo(SkipKind.None), "pre-season: no month skip");
        }

        [Test]
        public void TheRopeClockNeverDrawsAheadOfTheNewestSnapshot()
        {
            var clock = new RopeClock(P);
            var a = new SimSnapshot(P) { Seq = 1, Tick = 100, LiftCount = 1, TicksPerRealTick = 1 };
            a.LiftIds[0] = 7; a.RunMs[0] = 10_000;
            clock.Advance(a, 0, P.TickMs);
            var b = new SimSnapshot(P) { Seq = 2, Tick = 101, LiftCount = 1, TicksPerRealTick = 1 };
            b.LiftIds[0] = 7; b.RunMs[0] = 10_100;
            clock.Advance(b, 0.05, P.TickMs);
            Assert.That(clock.Seconds(7), Is.EqualTo(10.05).Within(1e-9));
            clock.Advance(b, 1.0, P.TickMs);
            Assert.That(clock.Seconds(7), Is.EqualTo(10.1).Within(1e-9));
        }

        [Test]
        public void TheGameClockFollowsTheSnapshot()
        {
            var (h, _) = Host();
            var c = new SimGameClock();
            int changed = 0;
            c.Changed += _ => changed++;
            Assert.That(c.Feed(h.Latest), Is.True);
            Assert.That(c.Now, Is.EqualTo(new ViewTime(2026, 274, DayHours.Default.OpenSecond)));
            Assert.That(c.Feed(h.Latest), Is.False);
            Assert.That(changed, Is.EqualTo(1));
        }
    }
}
