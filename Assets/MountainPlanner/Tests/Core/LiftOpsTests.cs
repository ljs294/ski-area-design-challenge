using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests). Task 4.11, p4-02 §6 LO-01…LO-13.
    public sealed class LiftOpsTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;
        static readonly long OpenMs = DayHours.Default.OpenSecond * 1000L;

        static LiftInput Quad(ulong id, LiftSchedule schedule = LiftSchedule.Daily, DayHours? hours = null, int top = -1, int bottom = -1) =>
            new LiftInput(id, true, schedule, hours, LiftKind.FixedGripChair, 4, 6000, 360_000, top, bottom);

        static LiftInput DetachableQuad(ulong id) => new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.DetachableChair, 4, 5000, 300_000);

        static LiftInput Six(ulong id) => new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.DetachableChair, 6, 7200, 300_000);

        static SimDesign Resort(long revision, SimNetwork net, params LiftInput[] lifts) =>
            new SimDesign(revision, (ulong)revision, 77, DayHours.Default, lifts, 1, net);

        static SimDesign Resort(params LiftInput[] lifts) => Resort(1, null, lifts);

        /// <summary>A world in pre-season, opened: Day 1 is the next morning (face day 1).</summary>
        static SimWorld Opened(SimDesign d)
        {
            var w = new SimWorld(P, d, SimSite.None, 2026);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            return w;
        }

        static void RunTo(SimWorld w, long faceMs, int step = 10)
        {
            while (w.FaceMs < faceMs) w.Step(step);
        }

        static void Feed(SimWorld w, ulong lift, int add, int perMinute = 0) =>
            w.ApplyNow(SimCommand.Of(SimCommandKind.FeedLine, 0, new LiftFeed(lift, add, perMinute)));

        [Test]
        public void LO03_AQuadAtSixSecondsCarries2400AnHour()
        {
            Assert.That(Quad(1).RatedPerHour, Is.EqualTo(2400));
            Assert.That(new LiftInput(2, true, kind: LiftKind.Tow, seats: 2, intervalMs: 6000).RatedPerHour, Is.EqualTo(1200), "a T-bar");
            Assert.That(new LiftInput(3, true, kind: LiftKind.Carpet, seats: 1, intervalMs: 2400).RatedPerHour, Is.EqualTo(1500), "a single carpet");
            // From the catalog: the Sessellift fixed-grip quad.
            var lift = new Lift(new LiftId(9), "Q", WorkStage.Built, LiftCatalogIds.SesselliftFgq4, new PointMm(0, 0), new PointMm(1_000_000, 0),
                                TowerMode.Automatic, TowerStance.Plumb, null);
            var input = SimDesign.Input(lift);
            Assert.That((input.Kind, input.Seats, input.IntervalMs, input.RatedPerHour), Is.EqualTo((LiftKind.FixedGripChair, 4, 6000, 2400)));
            Assert.That(input.RideMs, Is.InRange(400_000, 500_000), "1 km at 450 fpm");
        }

        [Test]
        public void LO04_ThreeHundredInLineAtAQuadIsASevenThirtyWait()
        {
            var w = Opened(Resort(Quad(10)));
            RunTo(w, Day + 7 * Hour);           // Day 1, before the opening: the lift is Scheduled and the line may form
            Assert.That(w.StatusOf(10), Is.EqualTo(LiftOpStatus.Scheduled));
            Feed(w, 10, 300);
            Assert.That(w.LineOf(10), Is.EqualTo(300));
            Assert.That(w.WaitMs(10), Is.EqualTo(450_000), "(300 ÷ 4) × 6 s = 7:30 at 1×");
            var s = new SimSnapshot(P);
            w.Write(s);
            Assert.That((s.LineCount[0], s.WaitMs[0], s.LiftStatus[0]), Is.EqualTo((300, 450_000, LiftOpStatus.Scheduled)));
            // At the opening the line moves: four a carrier, one carrier every 6 s.
            RunTo(w, Day + OpenMs);
            for (int i = 0; i < 600; i++) w.Step(1);   // 60 s of motion: 10 carriers
            Assert.That(w.LineOf(10), Is.EqualTo(260));
            Assert.That(w.StatusOf(10), Is.EqualTo(LiftOpStatus.Open));
        }

        /// <summary>LO-13 as built (see the plan's finding): the rated rate for a whole day, and 300 at once.</summary>
        [Test]
        public void LO13_AFeederAtTheRatedRateNeverBuildsALine()
        {
            var w = Opened(Resort(Quad(10)));
            RunTo(w, Day + OpenMs);
            Feed(w, 10, 0, perMinute: 40);   // 2,400 an hour: the quad's rated rate
            var (rides, peak, _, _, _, _) = RunDay(w, 10);
            // A day is 12 real minutes (TM-02 v2); boarding stops at last chair (4:15): 7¾ face hours = 697.5 s, 116 carriers.
            Assert.That(rides, Is.InRange(456, 468));
            Assert.That(peak, Is.LessThanOrEqualTo(8), "never more than two carriers' worth waiting");
        }

        static (int rides, int peak, int riding, int aboard, int released, int left) RunDay(SimWorld w, ulong lift)
        {
            (int, int, int, int, int, int) last = default;
            long stop = (w.FaceMs / Day) * Day + DayHours.Default.StopSecond * 1000L;
            while (w.FaceMs < stop)
            {
                w.Step(1);
                last = w.LedgerOf(lift);
            }
            return last;
        }

        [Test]
        public void LO13_TheSameDayGivesTheSameThroughputAtAnySpeed()
        {
            ulong Run(int step, out int rides)
            {
                var w = Opened(Resort(Quad(10), Six(20)));
                RunTo(w, Day + 6 * Hour, step);
                Feed(w, 10, 300, perMinute: 30);
                Feed(w, 20, 120, perMinute: 55);
                w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 41));
                RunTo(w, 2 * Day, step);
                rides = w.LastReport.Rides[0] + w.LastReport.Rides[1];
                return w.Hash();
            }
            ulong a = Run(1, out int ra), b = Run(10, out int rb), c = Run(3, out int rc);
            Assert.That(b, Is.EqualTo(a), "TM-03: the step size never changes a result");
            Assert.That(c, Is.EqualTo(a));
            Assert.That(rb, Is.EqualTo(ra));
            Assert.That(ra, Is.GreaterThan(400), "the quad's day; the six stands held at 41 mph");
        }

        [Test]
        public void LO06_RidersStepOffAtTheTopOnACarrierArrivalAfterTheCompressedRide()
        {
            var w = Opened(Resort(Quad(10)));
            RunTo(w, Day + OpenMs - 60_000);
            Feed(w, 10, 4);
            RunTo(w, Day + OpenMs);
            long boarded = -1;
            for (int i = 0; i < 2000; i++)
            {
                w.Step(1);
                var l = w.LedgerOf(10);
                if (boarded < 0 && l.riding == 4) boarded = w.RunMsOf(10);
                if (l.released == 4)
                {
                    long at = w.RunMsOf(10) - boarded;
                    // 360 s ÷ 8 = 45 s, then the next arrival at the top (one interval at most).
                    Assert.That(at, Is.InRange(45_000 - 100, 45_000 + 6000));
                    Assert.That(l.aboard, Is.EqualTo(4), "LO-05: the chair still carries them up at real speed");
                    return;
                }
            }
            Assert.Fail("the riders never stepped off");
        }

        [Test]
        public void LO06_TowAndCarpetRidersAreNotCompressed()
        {
            var tow = new LiftInput(30, true, kind: LiftKind.Tow, seats: 2, intervalMs: 6000, rideMs: 120_000);
            var w = Opened(Resort(tow));
            RunTo(w, Day + OpenMs - 60_000);
            Feed(w, 30, 2);
            RunTo(w, Day + OpenMs);
            for (int i = 0; i < 600 + 60; i++) w.Step(1);   // 66 s: boarded at 6 s, not off before 126 s
            Assert.That(w.LedgerOf(30).released, Is.Zero);
            for (int i = 0; i < 700; i++) w.Step(1);
            Assert.That(w.LedgerOf(30).released, Is.EqualTo(2));
        }

        [Test]
        public void LO07_LastChairClosesTheLineAndTheStopSendsItHome()
        {
            var w = Opened(Resort(Quad(10)));
            RunTo(w, Day + DayHours.Default.LastChairSecond * 1000L - 4000);
            Feed(w, 10, 200);
            RunTo(w, Day + DayHours.Default.LastChairSecond * 1000L);
            w.Step(1);
            Assert.That(w.StatusOf(10), Is.EqualTo(LiftOpStatus.Closed), "past last chair");
            int before = w.LineOf(10);
            Feed(w, 10, 50);
            Assert.That(w.LineOf(10), Is.EqualTo(before), "no one joins after last chair");
            for (int i = 0; i < 120; i++) w.Step(1);
            int after = w.LineOf(10);
            Assert.That(after, Is.LessThan(before), "the line still boards until the stop");
            RunTo(w, Day + DayHours.Default.StopSecond * 1000L + 60_000);
            var l = w.LedgerOf(10);
            Assert.That((w.LineOf(10), l.riding, l.aboard), Is.EqualTo((0, 0, 0)));
            // The 15 face minutes from last chair to the stop are 22.5 real seconds: three or four chairs, the rest go home.
            Assert.That(l.leftInLine, Is.InRange(before - 20, before - 8));
        }

        [Test]
        public void LO07_ALiftsOwnHoursSitInsideTheResorts()
        {
            var late = new DayHours(10 * 3600, 14 * 3600, 15 * 3600);
            var w = Opened(Resort(Quad(10), Quad(11, hours: late)));
            RunTo(w, Day + 9 * Hour);
            Assert.That((w.StatusOf(10), w.StatusOf(11)), Is.EqualTo((LiftOpStatus.Open, LiftOpStatus.Scheduled)));
            long ran = w.RunMsOf(11);
            RunTo(w, Day + 9 * Hour + 30 * 60_000);
            Assert.That(w.RunMsOf(11), Is.EqualTo(ran), "its rope waits for its own opening");
            RunTo(w, Day + 11 * Hour);
            Assert.That(w.StatusOf(11), Is.EqualTo(LiftOpStatus.Open));
            RunTo(w, Day + 14 * Hour + 60_000);
            Assert.That(w.StatusOf(11), Is.EqualTo(LiftOpStatus.Closed));
            ran = w.RunMsOf(11);
            RunTo(w, Day + 15 * Hour + 60_000);
            long stopped = w.RunMsOf(11);
            RunTo(w, Day + 16 * Hour);
            Assert.That(w.RunMsOf(11), Is.EqualTo(stopped), "stopped at its own stop");
        }

        [Test]
        public void LO09_APeakDaysLiftStandsIdleOnWeekdays()
        {
            var w = Opened(Resort(Quad(10), Quad(11, LiftSchedule.PeakDays)));
            var cal = new LabelCalendar(P);
            int weekday = -1, weekend = -1;
            for (int d = 1; d < 30 && (weekday < 0 || weekend < 0); d++)
            {
                var t = cal.TypeOf(d);
                if (t == DayType.Weekday && weekday < 0) weekday = d;
                if (t != DayType.Weekday && weekend < 0) weekend = d;
            }
            RunTo(w, weekday * Day + 10 * Hour);
            Assert.That((w.StatusOf(10), w.StatusOf(11)), Is.EqualTo((LiftOpStatus.Open, LiftOpStatus.Closed)));
            long parked = w.RunMsOf(11);
            RunTo(w, weekday * Day + 11 * Hour);
            Assert.That(w.RunMsOf(11), Is.EqualTo(parked), "its chairs stay parked");
            RunTo(w, weekend * Day + 10 * Hour);
            Assert.That(w.StatusOf(11), Is.EqualTo(LiftOpStatus.Open));
        }

        [Test]
        public void LO11_WindHoldsADetachableBeforeAFixedGripAndTheRopeStops()
        {
            var w = Opened(Resort(Quad(10), DetachableQuad(20)));
            RunTo(w, Day + 9 * Hour);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 40));   // × 0.9 over the detachable quad's 35, × 1.1 under the fixed quad's 46
            RunTo(w, Day + 10 * Hour + 60_000);
            Assert.That((w.HeldNow(10), w.HeldNow(20)), Is.EqualTo((false, true)));
            Assert.That(w.StatusOf(20), Is.EqualTo(LiftOpStatus.OnHold));
            long rope = w.RunMsOf(20);
            RunTo(w, Day + 10 * Hour + 20 * 60_000);
            Assert.That(w.RunMsOf(20), Is.EqualTo(rope), "RP-07 v2: the chairs stop where they are");
            // Calm: a held lift reads the gust every 15 face minutes and runs again after half an hour 10 mph below.
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 0));
            RunTo(w, Day + 10 * Hour + 35 * 60_000);
            Assert.That(w.HeldNow(20), Is.True, "one calm reading isn't half an hour");
            RunTo(w, Day + 11 * Hour + 6 * 60_000);
            Assert.That(w.HeldNow(20), Is.False);
            Assert.That(w.StatusOf(20), Is.EqualTo(LiftOpStatus.Open));
        }

        [Test]
        public void LO11_TheLimitFollowsTheTypeAndTheCarrierSize()
        {
            int Limit(LiftKind kind, int seats) => SimWorld.HoldLimitMph(P, new LiftInput(1, true, kind: kind, seats: seats));
            // Owner 2026-11-08: surface lifts never; fixed grips above detachables; bigger, heavier chairs above smaller.
            Assert.That((Limit(LiftKind.FixedGripChair, 2), Limit(LiftKind.FixedGripChair, 3), Limit(LiftKind.FixedGripChair, 4)), Is.EqualTo((40, 43, 46)));
            Assert.That((Limit(LiftKind.DetachableChair, 4), Limit(LiftKind.DetachableChair, 6), Limit(LiftKind.DetachableChair, 8)), Is.EqualTo((35, 38, 41)));
            Assert.That((Limit(LiftKind.Gondola, 8), Limit(LiftKind.Tram, 100)), Is.EqualTo((45, 55)));
            Assert.That((Limit(LiftKind.Tow, 2), Limit(LiftKind.Carpet, 1)), Is.EqualTo((-1, -1)));
            Assert.That(Limit(LiftKind.FixedGripChair, 4), Is.GreaterThan(Limit(LiftKind.DetachableChair, 4)), "a fixed grip beats a detachable of its size");
        }

        [Test]
        public void LO11_TowsAndCarpetsNeverHold()
        {
            var tow = new LiftInput(30, true, kind: LiftKind.Tow, seats: 2);
            var carpet = new LiftInput(31, true, kind: LiftKind.Carpet, seats: 1, intervalMs: 2400, rideMs: 60_000);
            var w = Opened(Resort(tow, carpet));
            RunTo(w, Day + 9 * Hour);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 200));
            RunTo(w, Day + 12 * Hour);
            Assert.That((w.HeldNow(30), w.HeldNow(31)), Is.EqualTo((false, false)));
        }

        [Test]
        public void LO11_EachLiftChecksAtItsOwnKeyedMinute()
        {
            var lifts = new LiftInput[6];
            for (int i = 0; i < lifts.Length; i++) lifts[i] = Six((ulong)(100 + i));
            var w = Opened(Resort(lifts));
            RunTo(w, Day + 9 * Hour);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 60));
            var seen = new HashSet<long>();
            var s = new SimSnapshot(P);
            while (w.FaceMs < Day + 10 * Hour + 1)
            {
                w.Step(1, s);
                for (int e = 0; e < s.EventCount; e++)
                    if (s.Events[e].Type == SimEventType.LiftHeld) seen.Add(s.Events[e].Tick);
            }
            Assert.That(seen.Count, Is.GreaterThan(1), "not every lift stops in the same tick");
        }

        /// <summary>A network: lift 10's top (node 0) has a trail (index 0) to its bottom (node 1); lift 11's top (node 2) has none.</summary>
        static SimNetwork Net(bool trail) => SimNetwork.Of(3, trail ? new List<(int, int, int)> { (0, 1, 0) } : new List<(int, int, int)>(), new ulong[] { 500 });

        [Test]
        public void LO10_ALiftWithNoWayDownStaysClosedAndOneThatLosesItDownloads()
        {
            var d = Resort(1, Net(true), Quad(10, top: 0, bottom: 1), Quad(11, top: 2, bottom: 1));
            var w = Opened(d);
            RunTo(w, Day + 10 * Hour);
            Assert.That((w.StatusOf(10), w.StatusOf(11)), Is.EqualTo((LiftOpStatus.Open, LiftOpStatus.Closed)));
            var s = new SimSnapshot(P);
            w.Write(s);
            Assert.That((s.TrailsKnown, s.TrailCount, s.TrailStatus[0]), Is.EqualTo((true, 1, LiftOpStatus.Open)));
            Feed(w, 10, 40);
            // Mid-day the trail goes (a new design without it): the lift downloads, closed to boarding, and the line leaves.
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, Resort(2, Net(false), Quad(10, top: 0, bottom: 1), Quad(11, top: 2, bottom: 1))));
            Assert.That(w.StatusOf(10), Is.EqualTo(LiftOpStatus.Downloading));
            Assert.That(w.LineOf(10), Is.Zero);
            long rope = w.RunMsOf(10);
            RunTo(w, Day + 10 * Hour + 60_000);
            Assert.That(w.RunMsOf(10), Is.GreaterThan(rope), "the chairs still run to bring riders down");
            // The next morning it doesn't open.
            RunTo(w, 2 * Day + 10 * Hour);
            Assert.That(w.StatusOf(10), Is.EqualTo(LiftOpStatus.Closed));
        }

        [Test]
        public void LO12_TheDayReportCountsRidesWaitsAndUtilisation()
        {
            var w = Opened(Resort(Quad(10)));
            RunTo(w, Day + 7 * Hour);
            Feed(w, 10, 300, perMinute: 40);
            RunTo(w, Day + DayHours.Default.StopSecond * 1000L + 60_000, 1);
            var r = w.LastReport;
            Assert.That(r.FaceDay, Is.EqualTo(1));
            // Full all day: a carrier every 6 s from 8:30 to the stop at 4:30 (the line boards past last chair), 120 carriers.
            Assert.That(r.Rides[0], Is.EqualTo(480));
            Assert.That(r.PeakLine[0], Is.GreaterThanOrEqualTo(300));
            Assert.That(r.MeanWaitMs[0], Is.GreaterThan(60_000));
            Assert.That(r.UtilisationPermille[0], Is.InRange(950, 1000), "full all day");
            Assert.That(r.LeftInLine[0], Is.GreaterThan(0), "the morning's 300 never cleared");
        }

        [Test]
        public void BG01_ATickWithFullLinesAllocatesNothing()
        {
            var lifts = new LiftInput[20];
            for (int i = 0; i < lifts.Length; i++) lifts[i] = i % 2 == 0 ? Quad((ulong)(10 + i)) : Six((ulong)(10 + i));
            var w = Opened(Resort(lifts));
            RunTo(w, Day + OpenMs - 60_000);
            for (int i = 0; i < lifts.Length; i++) Feed(w, lifts[i].Id, 500, perMinute: 60);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 38));
            var s = new SimSnapshot(P);
            for (int i = 0; i < 2000; i++) w.Step(1, s);
            long before;
            try { before = GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { Assert.Ignore("no per-thread allocation counter here"); return; }
            for (int i = 0; i < 4000; i++) w.Step(1, s);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.Zero, "bytes allocated over 4,000 ticks with 20 full lines");
            Assert.That(w.LedgerOf(10).rides, Is.GreaterThan(0));
        }

        [Test]
        public void SI12_ASaveKeepsTheLinesRidersAndHolds()
        {
            var w = Opened(Resort(Quad(10), Six(20)));
            RunTo(w, Day + 7 * Hour);
            Feed(w, 10, 300, perMinute: 20);
            Feed(w, 20, 77);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 40));
            RunTo(w, Day + 11 * Hour + 123_000, 1);
            byte[] bytes = w.SaveState();
            var back = SimSidecar.Read(bytes, P, w.Design, SimSite.None, out var header);
            Assert.That(header.SidecarVersion, Is.EqualTo(SimSidecar.Version));
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()));
            Assert.That((back.LineOf(10), back.LedgerOf(10), back.HeldNow(20)), Is.EqualTo((w.LineOf(10), w.LedgerOf(10), w.HeldNow(20))));
            for (int i = 0; i < 3000; i++) { w.Step(1); back.Step(1); }
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()), "and it plays on the same");
        }
    }
}
