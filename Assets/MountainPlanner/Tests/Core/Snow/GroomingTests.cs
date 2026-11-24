using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;
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
            Assert.That(hours[0] >= swing || hours[0] < 9, Is.True, "in the swing or night shift (GO-03 v2: to the 8:30 opening)");
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

        /// <summary>A fleet of these models on the one-double resort's groomed trails (Every night), crews by Auto.</summary>
        static SimWorld Fleet(Pacing pacing, params GroomerModel[] models) => Fleet(600, pacing, models);

        static SimWorld Fleet(int seedMm, Pacing pacing, params GroomerModel[] models)
        {
            var w = TrailSnowWorldTests.World(seedMm, null, pacing, GuestFixture.OneDouble());
            var layout = TrailSnowWorldTests.GroomedLayout(w);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            int n = models.Length;
            var nums = new int[n]; var drives = new GroomerDrive[n]; var tillers = new bool[n];
            for (int i = 0; i < n; i++) { nums[i] = i + 1; drives[i] = GroomerDrive.Diesel; }
            var ids = new System.Collections.Generic.List<ulong>();
            for (int t = 0; t < layout.Trails; t++) if (layout.TrailGroomed[t]) ids.Add(layout.TrailId[t]);
            ids.Sort();
            var set = new GroomSetting[ids.Count]; var pri = new GroomPriority[ids.Count];
            for (int i = 0; i < set.Length; i++) set[i] = GroomSetting.EveryNight;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGrooming, 0, new SimGrooming(nums, models, drives, tillers, -1, -1, -1, ids.ToArray(), set, pri)));
            w.JoinSnow();
            return w;
        }

        [Test]
        public void GO05_AutoCrewsTheWinchGroomerForASteepPitchBeforeTheFleetsFirst()
        {
            // owner 2026-11-21: the 400 W sat idle while the 800 skipped a pitch only a W climbs
            var w = Fleet(TrailSnowWorldTests.Steep, GroomerModel.S800, GroomerModel.S400W);
            To(w, 23);
            var v = View(w);
            Say($"[crew] swing {v.CrewsSwing} night {v.CrewsNight}; shifts 800 {v.Shifts[0]}, 400 W {v.Shifts[1]}; state {v.State[0]}");
            Assert.That(v.CrewsNight + v.CrewsSwing, Is.GreaterThan(0));
            int crewsOn = v.CrewsNight > 0 ? 2 : 1;   // the night's bit, else the swing's
            Assert.That(v.Shifts[1] & crewsOn, Is.Not.Zero, "the W has the crew");
            Assert.That(v.State[0], Is.Not.EqualTo(GroomRowState.PitchSkipped));
        }

        [Test]
        public void GO15_AGroomerDrivesOutOverTheTrailsAndBackBeforeItsShiftEnds()
        {
            var w = OneRun(1);
            To(w, 23);
            var v = View(w);
            int drive = 0;
            for (int l = 0; l < 3; l++) drive += v.TransitMin[l];
            Say($"[transit] {v.TransitCount} drives, {drive} min; the run from {v.StartMin[0]} to {v.EndMin[0]} (night ends at {v.NightEndAbsMin - v.SwingStartAbsMin})");
            Assert.That(v.TransitCount, Is.GreaterThanOrEqualTo(1), "back to the depot (the drive out is none when the depot is at the run's foot)");
            Assert.That(drive, Is.GreaterThan(0));
            int last = int.MinValue;
            for (int i = 0; i < v.TransitCount; i++)
            {
                if (v.TransitTrail[i] >= 0) continue;   // a work piece
                Assert.That(v.TransitTo[i], Is.GreaterThan(v.TransitFrom[i]));
                last = Math.Max(last, v.TransitTo[i]);
            }
            Assert.That(last, Is.LessThanOrEqualTo(v.NightEndAbsMin - v.SwingStartAbsMin), "home before the shift ends");
            Assert.That(last, Is.GreaterThanOrEqualTo(v.EndMin[0]), "the drive home comes after the work");
        }

        [Test]
        public void GO16_ARunCutShortIsGroomedInFullLengthPassesAndFinishedLater()
        {
            // GO-16 v2: a groomer far too slow for the run: the night ends with full-length passes side by side across part
            // of the width (from the left edge looking downhill), and the next night goes on from there
            var w = Fleet(1500, Pacing.Parse("GroomWinchMaxGrade = 1000\nGroomTillCentiAcres = 20,20,20,20"), GroomerModel.S800W);
            To(w, 23);
            var plan = View(w);
            Say($"[partial] tonight: job {plan.Job[0]}, state {plan.State[0]}, needs {plan.NeedMin[0]} min, {plan.StartMin[0]}..{plan.EndMin[0]}; crews {plan.CrewsSwing}/{plan.CrewsNight}");
            Hours(w, 12);
            var fine = w.FineSnow; var L = fine.Layout;
            int groomed = 0, cells = 0, minAlong = int.MaxValue, maxAlong = int.MinValue, minInAlong = int.MaxValue, maxInAlong = int.MinValue;
            long latIn = 0, latOut = 0;
            int minLatIn = int.MaxValue;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (L.Owner[f] != 0) continue;
                cells++;
                int a = L.AlongMm[f];
                minAlong = Math.Min(minAlong, a); maxAlong = Math.Max(maxAlong, a);
                if ((fine.Flags[f] & FineSnow.GroomMask) != 0)
                {
                    groomed++; latIn += L.LateralMm[f]; minLatIn = Math.Min(minLatIn, L.LateralMm[f]);
                    minInAlong = Math.Min(minInAlong, a); maxInAlong = Math.Max(maxInAlong, a);
                }
                else latOut += L.LateralMm[f];
            }
            int inside = 0;   // ungroomed cells within the groomed band (left for thin snow or a narrow station, not for time)
            for (int f = 0; f < fine.Cells; f++)
                if (L.Owner[f] == 0 && (fine.Flags[f] & FineSnow.GroomMask) == 0 && L.LateralMm[f] > minLatIn) inside++;
            var v = View(w);
            Say($"[partial] {groomed} of {cells} cells groomed, along {minInAlong}..{maxInAlong} of {minAlong}..{maxAlong} mm, " +
                $"mean lateral in {(groomed > 0 ? latIn / groomed : 0)} out {(cells > groomed ? latOut / (cells - groomed) : 0)} mm, {inside} inside; last night {v.LastState[0]}");
            Assert.That(groomed, Is.GreaterThan(0), "some of it");
            Assert.That(groomed, Is.LessThan(cells), "not all of it");
            Assert.That(maxInAlong - minInAlong, Is.GreaterThan((maxAlong - minAlong) * 8 / 10), "full-length passes");
            Assert.That(latIn / groomed, Is.GreaterThan(latOut / (cells - groomed)), "from the left edge (positive lateral)");
            Assert.That(inside, Is.LessThan(groomed / 5), "the band is worked through");
            Assert.That(v.LastState[0], Is.EqualTo(GroomRowState.Partial));
            // the next night goes on across the width
            Hours(w, 24);
            int groomed2 = 0;
            for (int f = 0; f < fine.Cells; f++) if (L.Owner[f] == 0 && (fine.Flags[f] & FineSnow.GroomMask) != 0) groomed2++;
            Say($"[partial] after the next night {groomed2} of {cells}");
            Assert.That(groomed2, Is.GreaterThan(groomed), "a later shift finishes the job");
        }

        // Round 3: the base (0), a middle station (1) and the top (2). Run A (11) from the middle to the base, run B (12) from
        // the top to the middle, each 760 m; a chair from the base to the top. The depot is the base, so the way to B's foot
        // is up A.
        internal static SimDesign TwoRunDesign(bool islandLift = false)
        {
            var trails = new ulong[] { 11, 12 };
            var edges = new System.Collections.Generic.List<(int from, int to, int trail)> { (1, 0, 0), (2, 1, 1) };
            var net = SimNetwork.Of(islandLift ? 4 : 3, edges, trails, null, new System.Collections.Generic.List<int> { 760_000, 760_000 });
            var chair = new LiftInput(101, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topNode: 2, bottomNode: 0, bottomLevelCm: 10_000);
            var lifts = islandLift
                // a lift lower than the base whose foot no trail touches (the drawn resort's lowest lift, 2026-11-21)
                ? new[] { new LiftInput(99, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topNode: 1, bottomNode: 3, bottomLevelCm: 0), chair }
                : new[] { chair };
            return new SimDesign(1, 1, 4242, DayHours.Default, lifts, 2, net);
        }

        /// <summary>The two runs, one 800 W crewed on the night only, B (High) due tonight unless <paramref name="bDue"/> is off.</summary>
        static SimWorld TwoRuns(bool bDue, bool islandLift = false)
        {
            var w = TrailSnowWorldTests.World(1500, null, TrailSnowWorldTests.Steep, TwoRunDesign(islandLift));
            var layout = TrailSnowWorldTests.TwoRunLayout(w);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            var g = new SimGrooming(new[] { 1 }, new[] { GroomerModel.S800W }, new[] { GroomerDrive.Diesel }, new[] { false }, 0, 1, -1,
                                    new ulong[] { 11, 12 }, new[] { GroomSetting.EveryNight, bDue ? GroomSetting.EveryNight : GroomSetting.Never },
                                    new[] { GroomPriority.Normal, GroomPriority.High });
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGrooming, 0, g));
            w.JoinSnow();
            return w;
        }

        static int WorkMinutes(GroomView v, int trail)
        {
            int m = 0;
            for (int i = 0; i < v.TransitCount; i++) if (v.TransitTrail[i] == trail) m += v.TransitTo[i] - v.TransitFrom[i];
            return m;
        }

        static int Groomed(SimWorld w, int owner)
        {
            var fine = w.FineSnow; var L = fine.Layout; int n = 0;
            for (int f = 0; f < fine.Cells; f++) if (L.Owner[f] == owner && (fine.Flags[f] & FineSnow.GroomMask) != 0) n++;
            return n;
        }

        [Test]
        public void GO16_ADriveAlongARunDueTonightGroomsAPassOfItOnTheWay()
        {
            // GO-16 v2 (owner 2026-11-21): B goes first (High); the drive to B's foot runs up A, due tonight, and grooms a pass
            var w = TwoRuns(bDue: true);
            var alone = TwoRuns(bDue: false);
            To(w, 23); To(alone, 23);
            var v = View(w); var va = View(alone);
            int drive = 0;
            for (int i = 0; i < v.TransitCount; i++) if (v.TransitTrail[i] < 0 && v.TransitFrom[i] < WorkStart(v, 1)) drive = Math.Max(drive, v.TransitTo[i] - v.TransitFrom[i]);
            int plain = (int)Math.Ceiling(760.0 / (Pacing.Default.GroomTransitKmh * 1000 / 60.0));
            Say($"[way] drive to B {drive} min (plain {plain}); A's work {WorkMinutes(v, 0)} min, alone {WorkMinutes(va, 0)} min; B {WorkMinutes(v, 1)} min");
            Assert.That(WorkMinutes(v, 1), Is.GreaterThan(0), "B is planned");
            Assert.That(drive, Is.GreaterThan(plain), "the drive up A is at the tilling speed");
            Assert.That(WorkMinutes(v, 0), Is.LessThan(WorkMinutes(va, 0)), "A needs the pass's minutes fewer");
            Assert.That(WorkMinutes(v, 0), Is.GreaterThan(0), "A is still groomed whole");
            // once the drive is done (the night starts at 1 AM), a stretch of A is groomed before A's own turn
            To(w, 3);
            var after = View(w);
            Say($"[way] at 3 AM: {after.WayPasses} stretches on the way, {Groomed(w, 0)} of A's cells groomed, {Groomed(w, 1)} of B's");
            Assert.That(after.WayPasses, Is.GreaterThanOrEqualTo(1));
            Assert.That(Groomed(w, 0), Is.GreaterThan(0), "the pass on the way");
            // the same bytes twice
            var twin = TwoRuns(bDue: true);
            To(twin, 23); To(twin, 3);
            Assert.That(twin.SaveState(), Is.EqualTo(w.SaveState()));
        }

        static int WorkStart(GroomView v, int trail)
        {
            int first = int.MaxValue;
            for (int i = 0; i < v.TransitCount; i++) if (v.TransitTrail[i] == trail) first = Math.Min(first, v.TransitFrom[i]);
            return first;
        }

        [Test]
        public void GO15_TheDepotIsTheLowestLiftFootATrailReaches()
        {
            // the drawn resort, 2026-11-21: its lowest lift starts where no trail does, so every run read "No way there"
            var w = TwoRuns(bDue: true, islandLift: true);
            To(w, 23);
            var v = View(w);
            Say($"[depot] lift {v.DepotLift}; A {v.State[0]}, B {v.State[1]}");
            Assert.That(v.DepotLift, Is.EqualTo(101UL), "the base chair, not the lower island lift");
            Assert.That(v.State[0], Is.Not.EqualTo(GroomRowState.NoRoute));
            Assert.That(v.State[1], Is.Not.EqualTo(GroomRowState.NoRoute));
        }

        [Test]
        public void GO14_TheDayShowsTonightsPlanAndAnOrderShowsAtOnceChangingNothing()
        {
            // owner 2026-11-21: "when trails are selected to be groomed, the game should automatically update the analysis"
            var w = OneRun(1, setting: GroomSetting.WhenOrdered);
            var off = OneRun(1, setting: GroomSetting.WhenOrdered);
            off.GroomPreviewOff = true;
            To(w, 12); To(off, 12);
            var before = View(w);
            Say($"[preview] noon: preview {before.Preview}, night {before.NightDay}, job {before.Job[0]}, state {before.State[0]}");
            Assert.That(before.Preview, Is.True, "the day shows tonight's plan");
            Assert.That(before.Job[0], Is.EqualTo(GroomJob.None), "nothing ordered yet");
            ulong id = TrailSnowWorldTests.GroomedLayout(w).TrailId[0];
            foreach (var x in new[] { w, off }) x.ApplyNow(SimCommand.Of(SimCommandKind.SetGrooming, 1, new SimGroomOrder(id, GroomOrder.Till)));
            var after = View(w);
            int planned = 0;
            for (int i = 0; i < after.TransitCount; i++) if (after.TransitTrail[i] == 0) planned += after.TransitTo[i] - after.TransitFrom[i];
            Say($"[preview] ordered: job {after.Job[0]}, G{after.Groomer[0]} {after.StartMin[0]}..{after.EndMin[0]}, {planned} min, want {after.WantMinutes} of {after.CrewMinutes}");
            Assert.That(after.Job[0], Is.EqualTo(GroomJob.Till), "the order is in tonight's plan at once");
            Assert.That(planned, Is.GreaterThan(0));
            Assert.That(after.CrewMinutes, Is.GreaterThan(0), "the budget: the crews' hours");
            Assert.That(View(off).Preview, Is.False);
            // through the night: the preview moved nothing (the same bytes as a world without it), and the real plan has it
            for (int h = 0; h < 24; h++) { Hours(w, 1); Hours(off, 1); View(w); View(off); }
            Assert.That(w.SaveState(), Is.EqualTo(off.SaveState()), "the preview changes nothing");
            Assert.That(View(w).LastState[0], Is.EqualTo(GroomRowState.Done), "the ordered run was groomed");
        }

        [Test]
        public void BB24_TonightsPreviewWaitsForTheHoursSnowAndAnOrderFinishesItFirst()
        {
            // Task 4.26 BB24 (4.27's capture at Gilford, 2026-11-23): the hour's snow writes the fine cells on the snow crew's
            // threads until it is joined, and a snapshot's preview read them meanwhile (DepthUm divided by a density a crew
            // thread had just zeroed). The preview now waits for the hour, showing the last one; an order joins it first.
            var w = OneRun(1, setting: GroomSetting.WhenOrdered);
            To(w, 12);
            var noon = View(w);
            Assert.That(noon.Preview, Is.True, "the day shows tonight's plan");
            Assert.That(w.GroomPreviewDue, Is.False);
            long h0 = w.HourIndex;
            while (w.HourIndex == h0) w.Step(1);   // the 13:00 roll: the hour's grooming marks the preview due, its snow starts
            Assert.That(w.SnowInFlight, Is.True, "the hour's snow is on the crew");
            var waiting = View(w);
            Say($"[BB24] in flight: due {w.GroomPreviewDue}, preview {waiting.Preview}, night {waiting.NightDay} (noon {noon.NightDay})");
            Assert.That(w.GroomPreviewDue, Is.True, "no preview reads the trail snow while the crew writes it");
            Assert.That(waiting.Preview, Is.True, "the last preview stays on show");
            Assert.That(waiting.NightDay, Is.EqualTo(noon.NightDay));
            ulong id = TrailSnowWorldTests.GroomedLayout(w).TrailId[0];
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGrooming, 1, new SimGroomOrder(id, GroomOrder.Till)));
            var ordered = View(w);
            Assert.That(w.GroomPreviewDue, Is.False, "the order finished the hour, so its preview is made at once");
            Assert.That(ordered.Job[0], Is.EqualTo(GroomJob.Till), "the order shows at once");
            Assert.That(w.SnowInFlight, Is.True, "joining kept the hour's landing tick");
        }

        [Test]
        public void GO14_TheTabKnowsLastNightAndTheHoursSinceTheGroom()
        {
            var w = OneRun(2);
            To(w, 12);
            var before = View(w);
            Assert.That(before.Drive[0], Is.EqualTo(GroomerDrive.Diesel), "each groomer's drive reaches the screen");
            Assert.That(before.LastGroomerAt(0), Is.EqualTo(-1), "no night yet: no lane");
            To(w, 12);   // a night and its morning
            var v = View(w);
            Say($"[last night] trail 0: groomer {v.LastGroomerAt(0)}, {v.LastStartAt(0)}-{v.LastEndAt(0)} min from {v.LastSwingStartAbsMin}, " +
                $"groomed {v.SinceHoursAt(0)} h ago");
            Assert.That(v.LastGroomerAt(0), Is.InRange(0, v.GroomerCount - 1), "the morning keeps the night's lane");
            Assert.That(v.LastStartAt(0), Is.GreaterThanOrEqualTo(0));
            Assert.That(v.LastEndAt(0), Is.GreaterThan(v.LastStartAt(0)));
            Assert.That(v.LastNightEndAbsMin, Is.GreaterThan(v.LastSwingStartAbsMin));
            Assert.That(v.SinceHoursAt(0), Is.InRange(0, 23), "groomed last night");
            // Two worlds stepped alike write the same view (the new fields are plain copies, nothing hashed).
            var w2 = OneRun(2);
            To(w2, 12); To(w2, 12);
            var v2 = View(w2);
            Assert.That(v2.LastGroomerAt(0), Is.EqualTo(v.LastGroomerAt(0)));
            Assert.That(v2.LastEndAt(0), Is.EqualTo(v.LastEndAt(0)));
            Assert.That(v2.SinceHoursAt(0), Is.EqualTo(v.SinceHoursAt(0)));
            Assert.That(w2.Hash(), Is.EqualTo(w.Hash()));
        }

        [Test]
        public void GO14_ALanePacksItsEndsAndSaturates()
        {
            var v = new GroomView(4);
            v.Lane[0] = GroomView.PackLane(31, 0, 870, 3);
            v.Lane[1] = GroomView.PackLane(2, 100, 2000, 400);   // past the bits: clamped
            v.Lane[2] = GroomView.PackLane(-1, -1, -1, -1);
            Assert.That((v.LastGroomerAt(0), v.LastStartAt(0), v.LastEndAt(0), v.SinceHoursAt(0)), Is.EqualTo((31, 0, 870, 3)));
            Assert.That((v.LastGroomerAt(1), v.LastEndAt(1), v.SinceHoursAt(1)), Is.EqualTo((2, 1023, 126)), "126 reads as that long or more");
            Assert.That((v.LastGroomerAt(2), v.LastStartAt(2), v.SinceHoursAt(2)), Is.EqualTo((-1, -1, -1)), "no lane, never groomed");
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
