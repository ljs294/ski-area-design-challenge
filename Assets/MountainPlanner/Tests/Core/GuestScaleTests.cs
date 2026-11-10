using System;
using System.Diagnostics;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// BG-01 and BG-08 for the guests, and the owner's requirement that the crowd scale: Phase 4 caps the hill at 3,000
    /// skiers, but the design is meant to carry tens of thousands without a rewrite. These tests hold the first part —
    /// **nothing is allocated per tick** — and report the second: the tick's cost and the memory at 30,000 skiers on a
    /// fifty-lift mountain, so the numbers are in front of us from the first day rather than discovered at 4.18.
    ///
    /// The per-tick work is meant to follow what is *happening*, not the cap: the timing wheel wakes only the skiers
    /// whose turn it is, a party is scheduled once for all of its members, and the routing search runs at most once a
    /// tick however many lifts changed. A cap ten times larger should therefore cost far less than ten times as much.
    /// </summary>
    public sealed class GuestScaleTests
    {
        const long Day = SimWorld.DayMs;
        static readonly long Stop = DayHours.Default.StopSecond * 1000L;

        static SimWorld Opened(Pacing p, SimDesign d)
        {
            var w = new SimWorld(p, d, SimSite.None, 2026, 4242UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            return w;
        }

        /// <summary>Allocation per tick is only measurable on CoreCLR; Unity's Mono has no per-thread counter.</summary>
        static long AllocatedByThread()
        {
            try { return GC.GetAllocatedBytesForCurrentThread(); }
            catch (PlatformNotSupportedException) { return -1; }
        }

        [Test]
        public void BG01_ABusyDayWithThreeThousandSkiersAllocatesNothing()
        {
            var p = Pacing.Parse("LocationDraw = 4000");
            var w = Opened(p, GuestFixture.Big(12, out _));
            // Into the middle of the morning, so the hill is full, lines are long and every path is in play.
            while (w.FaceMs < Day + (DayHours.Default.OpenSecond + 2 * 3600) * 1000L) w.Step(1);
            Assert.That(w.SkierCount, Is.GreaterThan(500), "there is a real crowd on the hill");
            var snapshot = new SimSnapshot(p);
            for (int i = 0; i < 500; i++) w.Step(1, snapshot);           // warm every path up
            long before = AllocatedByThread();
            if (before < 0) Assert.Ignore("GC.GetAllocatedBytesForCurrentThread needs CoreCLR (the .NET test run has it).");
            for (int i = 0; i < 4000; i++) w.Step(1, snapshot);
            long bytes = AllocatedByThread() - before;
            Assert.That(bytes, Is.Zero, $"BG-01: a tick must allocate nothing, with the snapshot and the render column published ({bytes} B over 4,000 ticks)");
        }

        [Test]
        public void BG08_ThirtyThousandSkiersStayWithinTheirMemory()
        {
            var p = Pacing.Parse("SkierCap = 30000");
            long before = GC.GetTotalMemory(true);
            var w = Opened(p, GuestFixture.Big(50, out _));
            for (int i = 0; i < 100; i++) w.Step(1);
            long after = GC.GetTotalMemory(true);
            double mb = (after - before) / 1e6;
            TestContext.Progress.WriteLine($"30,000 skiers: {mb:F1} MB of simulation state");
            Assert.That(mb, Is.LessThan(40), "BG-08: the skiers and their day records stay in a sane amount of memory");
        }

        /// <summary>
        /// The report the owner asked for: the tick's cost with 30,000 skiers at 8× on a fifty-lift mountain. It asserts
        /// only that the day runs and balances; the timings are printed, because a test machine's numbers are not the
        /// reference PC's (BG-09 measures there). The Stopwatch is the test host's, which p4-02 allows for harness code.
        /// </summary>
        [Test]
        public void BG01_ThirtyThousandSkiersAtEightTimesIsReported()
        {
            var p = Pacing.Parse("SkierCap = 30000\nLocationDraw = 40000");
            var w = Opened(p, GuestFixture.Big(50, out var net));
            var snapshot = new SimSnapshot(p);
            long stop = Day + Stop;
            // Warm up to the opening, then time the day itself at 8 ticks a step (the fastest preset).
            while (w.FaceMs < Day + DayHours.Default.OpenSecond * 1000L) w.Step(8, snapshot);
            int steps = 0, ticks = 0, peak = 0;
            var clock = Stopwatch.StartNew();
            while (w.FaceMs < stop)
            {
                ticks += w.Step(8, snapshot);
                steps++;
                if (w.SkierCount > peak) peak = w.SkierCount;
            }
            clock.Stop();
            w.Step(1, snapshot);
            var g = w.GuestLedger();
            double perTick = clock.Elapsed.TotalMilliseconds / Math.Max(1, ticks);
            TestContext.Progress.WriteLine(
                $"30,000-skier day: peak {peak} skiers, {g.arrived} arrived, {g.left} left, {g.wentHome} went home, " +
                $"{g.capped} found the hill full; {ticks} ticks in {clock.Elapsed.TotalSeconds:F1} s = {perTick:F3} ms a tick " +
                $"({perTick * 10:F1} ms of worker per real second at 1×, {perTick * 80:F1} at 8×)");
            Assert.That(peak, Is.GreaterThan(5000), "the hill really did fill up");
            Assert.That(g.arrived, Is.EqualTo(g.left), "SI-10: every skier who came has left by the stop");
            Assert.That(w.SkierCount, Is.Zero, "GU-12: and the hill is clear for the night");
        }

        /// <summary>
        /// The claim that makes the cap raisable: the tick's cost follows the skiers who are *doing* something, not the
        /// size of the arrays. Ten times the cap, with the same crowd, must not cost ten times as much.
        /// </summary>
        [Test]
        public void BG01_TheTicksCostFollowsTheCrowdNotTheCap()
        {
            double small = TimeADay(Pacing.Parse("SkierCap = 3000\nLocationDraw = 2000"));
            double big = TimeADay(Pacing.Parse("SkierCap = 30000\nLocationDraw = 2000"));
            TestContext.Progress.WriteLine($"same crowd, ten times the cap: {small:F3} ms a tick vs {big:F3} ms");
            Assert.That(big, Is.LessThan(Math.Max(small * 4, 0.05)),
                "a cap ten times larger must not cost ten times as much: the work follows the crowd (the timing wheel)");
        }

        static double TimeADay(Pacing p)
        {
            var w = Opened(p, GuestFixture.Big(20, out _));
            while (w.FaceMs < Day + DayHours.Default.OpenSecond * 1000L) w.Step(8);
            int ticks = 0;
            var clock = Stopwatch.StartNew();
            while (w.FaceMs < Day + Stop) ticks += w.Step(8);
            clock.Stop();
            return clock.Elapsed.TotalMilliseconds / Math.Max(1, ticks);
        }

        [Test]
        public void GU07_TheRoutingSearchIsRebuiltOnceATickHoweverManyLiftsChange()
        {
            // Fifty lifts all pass their last chair within the same minute, and a gust holds the lot: the search must
            // still run once, because the row rebuilds a dirty board rather than reacting to each change.
            var p = Pacing.Parse("SkierCap = 2000");
            var w = Opened(p, GuestFixture.Big(50, out _));
            while (w.FaceMs < Day + (DayHours.Default.OpenSecond + 3600) * 1000L) w.Step(1);
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++)
            {
                w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, i % 2 == 0 ? 90 : 0));
                w.Step(1);
            }
            clock.Stop();
            double perTick = clock.Elapsed.TotalMilliseconds / 200;
            TestContext.Progress.WriteLine($"fifty lifts holding and resuming every tick: {perTick:F3} ms a tick");
            Assert.That(perTick, Is.LessThan(20), "a board change storm costs one rebuild a tick, not one per lift");
        }

        [Test]
        public void SI08_ADayIsTheSameAtEveryPresetWithABigCrowd()
        {
            var p = Pacing.Parse("SkierCap = 5000\nLocationDraw = 3000");
            ulong one = Play(p, 1), two = Play(p, 2), four = Play(p, 4), eight = Play(p, 8);
            Assert.That(two, Is.EqualTo(one), "SI-08: two ticks a step gives the same day");
            Assert.That(four, Is.EqualTo(one), "and four");
            Assert.That(eight, Is.EqualTo(one), "and eight");
        }

        static ulong Play(Pacing p, int step)
        {
            var w = Opened(p, GuestFixture.Big(10, out _));
            while (w.FaceMs < Day + Stop) w.Step(step);
            w.Step(1);
            return w.Hash();
        }

        [Test]
        public void SI08_TheWheelsSizeNeverChangesTheDay()
        {
            // The timing wheel is an index, not a rule: a different number of buckets must give the very same day. The
            // state hash can't say so (the number of buckets is in the Pacing table, which the hash covers), so this
            // compares the day itself: who came, who left, who went home, what each archetype thought, what was skied.
            string wide = PlayForTheDay(Pacing.Parse("SkierCap = 2000\nWheelBuckets = 8192"));
            string narrow = PlayForTheDay(Pacing.Parse("SkierCap = 2000\nWheelBuckets = 256"));
            Assert.That(narrow, Is.EqualTo(wide),
                "SI-08: a skier due further out than the wheel is long comes round again in the same place in the order");
        }

        /// <summary>A day's outcome in words: the ledger, the verdicts and what every trail and lift carried.</summary>
        static string PlayForTheDay(Pacing p)
        {
            var w = Opened(p, GuestFixture.Big(10, out var net));
            while (w.FaceMs < Day + Stop) w.Step(4);
            w.Step(1);
            var sb = new System.Text.StringBuilder();
            sb.Append(w.GuestLedger()).Append('|');
            for (int a = 0; a < Pacing.Archetypes; a++) sb.Append(w.ReputationOf(a)).Append(',').Append(w.VerdictOf(a)).Append(';');
            sb.Append('|');
            for (int t = 0; t < net.TrailIds.Length; t++) sb.Append(w.TrailMetric(t, SimMetric.Passes)).Append(',');
            sb.Append('|');
            for (int i = 0; i < 10; i++) sb.Append(w.LiftMetric(i, SimMetric.Boardings)).Append(',');
            return sb.ToString();
        }
    }
}
