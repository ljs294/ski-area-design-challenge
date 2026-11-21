using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.25 PR A (GO-01…GO-09, SN-22 v4): grooming as an operation. No groomer, no grooming; a groomer works its
    /// trail in a shift at its model's speed, crewed night first; Never and Skip are kept; the work keeps the trail's water;
    /// the same bytes twice and on one or four threads; a save mid-night plays on the same; the snapshot's view fits its
    /// budget; the hour's grooming allocates nothing once warm.
    /// </summary>
    public sealed class GroomingTests
    {
        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        /// <summary>The one-double resort with its first trail as a groomed run, and (if any) <paramref name="groomers"/>.</summary>
        static SimWorld OneRun(int groomers, GroomerModel model = GroomerModel.S800W, GroomSetting setting = GroomSetting.EveryNight,
                               Pacing pacing = null, bool tiller = false)
        {
            var w = TrailSnowWorldTests.World(600, null, pacing, GuestFixture.OneDouble());
            var layout = TrailSnowWorldTests.GroomedLayout(w);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            if (groomers > 0 && !tiller) TrailSnowWorldTests.Groomers(w, layout, groomers, model, setting);
            if (groomers > 0 && tiller)
            {
                var nums = new int[groomers]; var models = new GroomerModel[groomers]; var drives = new GroomerDrive[groomers]; var tillers = new bool[groomers];
                for (int i = 0; i < groomers; i++) { nums[i] = i + 1; models[i] = model; drives[i] = GroomerDrive.Diesel; tillers[i] = true; }
                var g = new SimGrooming(nums, models, drives, tillers, -1, -1, -1, new[] { layout.TrailId[0] }, new[] { setting }, new[] { GroomPriority.Normal });
                w.ApplyNow(SimCommand.Of(SimCommandKind.SetGrooming, 0, g));
            }
            w.JoinSnow();
            return w;
        }

        static GroomView View(SimWorld w)
        {
            var s = new SimSnapshot(w.Pacing);
            w.Write(s);
            return s.Grooming;
        }

        /// <summary>Steps the world <paramref name="hours"/> hour rolls on; the hours of day (the roll's) each TrailGroomed
        /// event came at.</summary>
        static int[] Hours(SimWorld w, int hours)
        {
            var list = new System.Collections.Generic.List<int>();
            for (int i = 0; i < hours; i++)
            {
                long seq = w.Events.Count, h0 = w.HourIndex;
                while (w.HourIndex == h0) w.Step(1);
                for (long q = seq; q < w.Events.Count; q++)
                    if (w.Events.TryGet(q, out var e) && e.Type == SimEventType.TrailGroomed) list.Add((int)(w.HourIndex % 24));
            }
            w.JoinSnow();
            return list.ToArray();
        }

        /// <summary>Steps on to the next roll into hour of day <paramref name="hour"/>.</summary>
        static void To(SimWorld w, int hour)
        {
            do { long h0 = w.HourIndex; while (w.HourIndex == h0) w.Step(1); } while (w.HourIndex % 24 != hour);
            w.JoinSnow();
        }

        [Test]
        public void GO01_ANewResortHasNoGroomersAndGroomsNothing()
        {
            var w = OneRun(0);
            var hours = Hours(w, 48);
            var v = View(w);
            Say($"[none] hours groomed {hours.Length}; view live {v.Live}, groomers {v.GroomerCount}");
            Assert.That(hours, Is.Empty, "no groomer, no grooming (owner round 1)");
            Assert.That(v.GroomerCount, Is.Zero);
        }

        [Test]
        public void GO03_TheNightShiftGroomsTheRunOnceInItsHours()
        {
            var w = OneRun(2);
            To(w, 12);
            var hours = Hours(w, 21);   // noon to 9 AM: the swing and the night
            var v = View(w);
            int swing = (w.Design.Hours.StopSecond / 60 + Pacing.Default.GroomSweepMinutes) / 60;
            Say($"[night] groomed at {string.Join(",", hours)}; crews swing {v.CrewsSwing} night {v.CrewsNight}; last night {v.LastState[0]}, " +
                $"work {v.LastWorkMinutes} min, cost ${v.LastCostUsd}");
            Assert.That(hours.Length, Is.EqualTo(1), "one groom a night");
            Assert.That(hours[0] >= swing || hours[0] < 8, Is.True, "in the swing or night shift");
            Assert.That(v.LastState[0], Is.EqualTo(GroomRowState.Done));
            Assert.That(v.LastWorkMinutes, Is.GreaterThan(0));
            Assert.That(v.LastCostUsd, Is.GreaterThan(0), "the night's priced estimate (GO-06)");
        }

        [Test]
        public void GO05_AutopilotCrewsJustEnoughNightFirst()
        {
            var w = OneRun(3);
            To(w, 23);
            var v = View(w);
            Say($"[crews] swing {v.CrewsSwing}, night {v.CrewsNight}; want {v.WantMinutes} min, crewed {v.CrewMinutes} min, short {v.ShortMinutes}");
            Assert.That(v.CrewsNight, Is.EqualTo(1), "one trail fits one night crew");
            Assert.That(v.CrewsSwing, Is.Zero, "the swing isn't crewed while the night has room");
            Assert.That(v.ShortMinutes, Is.Zero);
            Assert.That(v.Job[0], Is.EqualTo(GroomJob.Till), "Every night: the till job");
        }

        [Test]
        public void GO04_EachModelWorksAtItsOwnSpeed()
        {
            int Need(GroomerModel m, bool tiller = false)
            {
                var w = OneRun(1, m, tiller: tiller);
                To(w, 23);
                return View(w).NeedMin[0];
            }
            // the small mountain's run is past the winch grade: the W models work it
            int s400 = Need(GroomerModel.S400W), s800 = Need(GroomerModel.S800W), s800t = Need(GroomerModel.S800W, tiller: true);
            Say($"[speed] the run needs 400: {s400} min, 800: {s800} min, 800 with the wider tiller: {s800t} min");
            Assert.That(s400, Is.GreaterThan(s800), "the 800 tills more acres an hour");
            Assert.That(s800t, Is.LessThan(s800), "the wider tiller is faster on a wide trail");
            Assert.That(s800, Is.GreaterThanOrEqualTo(Pacing.Default.GroomTaskMinutes));
        }

        [Test]
        public void GO07_AutopilotGroomsAfterSnowAndNeverPaysAnIdleCrew()
        {
            // the test mountain is all steeper than 35 %: lift the powder rule so the trail is one Autopilot grooms
            var w = OneRun(2, setting: GroomSetting.Autopilot, pacing: Pacing.Parse("GroomWinchMaxGrade = 1000\nGroomPowderGrade = 1000"));
            int groomed = 0, nights = 0, lastNight = int.MinValue;
            for (int h = 0; h < 24 * 30; h++)
            {
                Hours(w, 1);
                var v = View(w);
                if (v.LastNightDay == lastNight) continue;
                lastNight = v.LastNightDay;
                nights++;
                if (v.LastState[0] == GroomRowState.Done || v.LastState[0] == GroomRowState.PitchSkipped) groomed++;
                Say($"[autopilot] morning after day {v.LastNightDay}: {v.LastState[0]}, worked {v.LastWorkMinutes} min, ≈ ${v.LastCostUsd}, not reached {v.LastNotReached}");
                Assert.That(v.LastCostUsd == 0 || v.LastWorkMinutes > 0, Is.True, "a crew is paid only on a night that works");
                Assert.That(v.LastNotReached == 0 || v.LastWorkMinutes > 0 || v.ShortMinutes > 0, Is.True, "work isn't left without a crew");
            }
            Say($"[autopilot] {groomed} of {nights} nights groomed");
            Assert.That(groomed, Is.GreaterThan(0), "Autopilot grooms a trail that has snow and was never groomed (SN-22 v4)");
        }

        [Test]
        public void GO13_TheDotsKnowWhereEachGroomerIs()
        {
            var w = OneRun(2);
            To(w, 12);
            int working = 0, parked = 0, moving = 0;
            long end = w.FaceMs + SimWorld.DayMs;
            while (w.FaceMs < end)
            {
                long next = w.FaceMs + 10 * 60_000;
                while (w.FaceMs < next) w.Step(1);
                w.JoinSnow();
                var v = View(w);
                int now = (int)(w.FaceMs / 60_000);
                for (int g = 0; g < v.GroomerCount; g++)
                {
                    if (v.AtTrailA[g] < 0 && v.AtTrailB[g] < 0 && v.AtMinA[g] == 0 && v.AtMinB[g] == 0) { parked++; continue; }
                    Assert.That(v.AtMinA[g], Is.LessThanOrEqualTo(v.AtMinB[g]), "A comes before B");
                    if (v.AtJob[g] == GroomJob.None) { moving++; continue; }
                    working++;
                    Assert.That(v.AtTrailA[g], Is.EqualTo(v.AtTrailB[g]), "a piece is on one trail");
                    Assert.That(v.AtTrailA[g], Is.EqualTo(0));
                    Assert.That(v.AtMinA[g] <= now && now < v.AtMinB[g], Is.True, "the piece is the one under way now");
                    Assert.That(0 <= v.AtPpmA[g] && v.AtPpmA[g] <= v.AtPpmB[g] && v.AtPpmB[g] <= 1_000_000, Is.True, "its share of the trail");
                }
            }
            Say($"[where] over a day at 10-minute samples: {working} working, {moving} driving, {parked} parked");
            Assert.That(working, Is.GreaterThan(0), "a groomer is seen at work on the run");
            Assert.That(parked, Is.GreaterThan(0), "and parked between shifts");
        }

        [Test]
        public void GO07_NeverIsNeverGroomed()
        {
            var w = OneRun(2, setting: GroomSetting.Never);
            To(w, 12);
            var hours = Hours(w, 21);
            var v = View(w);
            Assert.That(hours, Is.Empty, "a Never trail isn't groomed");
            Assert.That(v.LastState[0], Is.EqualTo(GroomRowState.Never));
        }

        [Test]
        public void GO07_ASkippedTrailWaitsANight()
        {
            var w = OneRun(2);
            To(w, 12);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGrooming, 1, new SimGroomOrder(TrailSnowWorldTests.GroomedLayout(w).TrailId[0], GroomOrder.Skip)));
            var hours = Hours(w, 21);
            Assert.That(hours, Is.Empty, "skipped tonight");
            Assert.That(View(w).LastState[0], Is.EqualTo(GroomRowState.Skipped));
            Hours(w, 24);
            var v = View(w);
            Say($"[skip] the next night: {v.LastState[0]} (October's snow melts to Bare by then); warnings {v.WarningCount}");
            Assert.That(v.LastState[0], Is.Not.EqualTo(GroomRowState.Skipped), "the order is for one night: back on its setting");
            Assert.That(v.LastState[0] == GroomRowState.Done || v.LastState[0] == GroomRowState.TooBare, Is.True);
            if (v.LastState[0] == GroomRowState.TooBare)
                Assert.That(v.WarningKind[0], Is.EqualTo(GroomWarningKind.TooBare), "a scheduled trail too bare to groom warns (GO-10)");
        }

        [Test]
        public void GO08_TheSameBytesTwiceAndOnOneAndFourThreads()
        {
            var a = OneRun(2);
            var b = OneRun(2);
            var one = OneRun(2, pacing: Pacing.Parse("SnowThreads = 1\nGroomWinchMaxGrade = 1000"));
            var four = OneRun(2, pacing: Pacing.Parse("SnowThreads = 4\nGroomWinchMaxGrade = 1000"));
            foreach (var w in new[] { a, b, one, four })
            {
                long end = w.FaceMs + 3 * SimWorld.DayMs;
                while (w.FaceMs < end) w.Step(Pacing.Default.IdleTickFactor);
                w.JoinSnow();
            }
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the same world twice");
            int differ = 0;
            for (int f = 0; f < one.FineSnow.Cells; f++)
                if (one.FineSnow.Swe[f] != four.FineSnow.Swe[f] || one.FineSnow.Flags[f] != four.FineSnow.Flags[f]
                    || one.FineSnow.Type[f] != four.FineSnow.Type[f] || one.FineSnow.Age[f] != four.FineSnow.Age[f]) differ++;
            Assert.That(differ, Is.Zero, "the grooming doesn't depend on the threads");
            Assert.That(View(four).LastWorkMinutes, Is.EqualTo(View(one).LastWorkMinutes));
        }

        [Test]
        public void GO09_ASaveMidNightPlaysOnTheSame()
        {
            var w = TrailSnowWorldTests.GroomSaveWorld();
            byte[] bytes = w.SaveState();
            var back = TrailSnowWorldTests.Reopen(bytes);
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()), "the restored state is the saved one");
            Assert.That(back.SaveState(), Is.EqualTo(bytes), "saving again before the layout arrives writes the same bytes");
            back.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, TrailSnowWorldTests.GroomedLayout(back)));
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()), "the layout arriving changes nothing");
            foreach (var x in new[] { w, back })
            {
                long end = x.FaceMs + 2 * SimWorld.DayMs;
                while (x.FaceMs < end) x.Step(Pacing.Default.IdleTickFactor);
                x.JoinSnow();
            }
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()), "two days on, the same future");
            Assert.That(back.GroomerCount, Is.EqualTo(2));
        }

        [Test]
        public void GO08_TheViewFitsItsBudget()
        {
            var v = new GroomView(Pacing.Default.MaxTrails);
            Say($"[view] the Grooming tab's view at {Pacing.Default.MaxTrails} trails and {GroomFleet.MaxGroomers} groomers: {v.SizeBytes} bytes");
            Assert.That(v.SizeBytes, Is.LessThanOrEqualTo(16 * 1024), "0.0's ~16 KB side ring");
        }

        /// <summary>Past .NET's tiered-JIT threshold (see TrailSnowWorldTests' TR09).</summary>
        const int WarmCalls = 64;

        [Test]
        public void GO08_AnHoursGroomingAllocatesNothingOnceWarm()
        {
            var w = OneRun(2);
            long day = w.FaceMs / SimWorld.DayMs + 1;
            for (int i = 0; i < WarmCalls; i++) w.HourStepTrails(day * 24 + i);   // nearly three days of shifts, warm
            day += 3;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int h = 0; h < 24; h++) w.HourStepTrails(day * 24 + h);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Say($"[alloc] a day of trail hours with two groomers: {bytes} bytes");
            Assert.That(bytes, Is.Zero);
        }
    }
}
