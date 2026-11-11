using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests). Task 4.13 PR A: the boundary the
    // screen draws the skiers from (GU-16 v2, LO-02's ranks, SN-31's counters), GU-10's table and GU-10 v2's confidence.
    public sealed class GuestsOnScreenTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs;

        static SimWorld Busy(long secondsAfterOpening = 3600)
        {
            var w = new SimWorld(P, GuestFixture.Resort(), SimSite.None, 2026, 7_777UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            long until = Day + (DayHours.Default.OpenSecond + secondsAfterOpening) * 1000L;
            int guard = 0;
            while (w.FaceMs < until && guard++ < 20_000_000) w.Step(1);
            return w;
        }

        static SkierRenderBuffers.Frame Published(SimWorld w)
        {
            w.PublishSkierRender();
            return w.SkierRender.Latest;
        }

        // ---------- GU-16 v2: the column ----------

        [Test]
        public void GU16_TheHostPublishesTheColumnWithEverySnapshot()
        {
            var w = Busy();
            var host = new SimulationHost(w, new FakeTime(), threaded: false);
            var f = host.SkierRender.Latest;
            Assert.That(f, Is.Not.Null, "the host published a column with its first snapshot");
            Assert.That(f.Count, Is.EqualTo(w.SkierCount), "every skier on the hill is in it (4.12 left it to the tests)");
            Assert.That(f.Tick, Is.EqualTo(host.Latest.Tick), "at the snapshot's own tick");
            Assert.That(f.DayStartTick, Is.GreaterThan(0).And.LessThanOrEqualTo(f.Tick), "with the tick the day counts from");
        }

        [Test]
        public void GU16_EachRecordCarriesTheWholeSerialAndAStableSlot()
        {
            var w = Busy();
            var serials = new HashSet<uint>();
            w.ForEachSkier((serial, band, edge, lift, state) => serials.Add((uint)serial));
            var f = Published(w);
            var slots = new HashSet<ushort>();
            for (int i = 0; i < f.Count; i++)
            {
                var r = f.Skiers[i];
                Assert.That(serials.Contains(r.Serial), Is.True, "the serial InspectSkier takes, not 16 bits of it");
                Assert.That(slots.Add(r.Slot), Is.True, "one record per slot");
            }
            // A slot keeps its skier from one frame to the next, whatever leaves in between.
            var bySlot = new Dictionary<ushort, uint>();
            for (int i = 0; i < f.Count; i++) bySlot[f.Skiers[i].Slot] = f.Skiers[i].Serial;
            for (int k = 0; k < 50; k++) w.Step(1);
            f = Published(w);
            int kept = 0;
            for (int i = 0; i < f.Count; i++)
                if (bySlot.TryGetValue(f.Skiers[i].Slot, out uint was) && was == f.Skiers[i].Serial) kept++;
            Assert.That(kept, Is.GreaterThan(f.Count / 2), "most skiers are where they were, by slot");
        }

        [Test]
        public void LO02_ALinesRanksAreItsOrderOfJoining()
        {
            var w = Busy();
            w.ApplyNow(SimCommand.Of(SimCommandKind.FeedLine, 0, new LiftFeed(101, 120, 20)));
            for (int k = 0; k < 30; k++) w.Step(1);
            var f = Published(w);
            var byLift = new Dictionary<int, List<SkierRender>>();
            for (int i = 0; i < f.Count; i++)
            {
                var r = f.Skiers[i];
                if (r.State != (byte)GuestState.Queued) continue;
                Assert.That(r.OnALift, Is.True, "a queued skier's segment is its lift");
                if (!byLift.TryGetValue(r.Index, out var list)) byLift[r.Index] = list = new List<SkierRender>();
                list.Add(r);
            }
            Assert.That(byLift.Count, Is.GreaterThan(0), "there is a line to look at");
            foreach (var kv in byLift)
            {
                var list = kv.Value;
                list.Sort((a, b) => a.Rank.CompareTo(b.Rank));
                Assert.That(list.Count, Is.EqualTo(w.LineOf(w.Design.Lifts[kv.Key].Id)), "everyone in the line is drawn");
                for (int k = 0; k < list.Count; k++)
                {
                    Assert.That((int)list[k].Rank, Is.EqualTo(k), "ranks run 0, 1, 2… from the front");
                    Assert.That(list[k].ExitTick, Is.Zero);
                    if (k > 0) Assert.That(list[k].EntryTick, Is.GreaterThanOrEqualTo(list[k - 1].EntryTick), "FIFO by joining");
                }
            }
        }

        [Test]
        public void LO06_ARidersRecordIsItsRopeTravelFromBoardingToRelease()
        {
            var w = Busy();
            var f = Published(w);
            int riders = 0;
            for (int i = 0; i < f.Count; i++)
            {
                var r = f.Skiers[i];
                if (r.State != (byte)GuestState.Riding) continue;
                riders++;
                var lift = w.Design.Lifts[r.Index];
                uint now = unchecked((uint)w.RunMsOf(lift.Id));
                long span = LiftKinds.Compressed(lift.Kind) ? lift.RideMs / P.RideCompression : lift.RideMs;
                Assert.That(unchecked((int)(r.ExitTick - r.EntryTick)), Is.EqualTo((int)span), "release − boarding is the ride");
                Assert.That(unchecked((int)(r.ExitTick - now)), Is.GreaterThanOrEqualTo(0), "the release is still ahead on the rope");
                Assert.That(unchecked((int)(now - r.EntryTick)), Is.GreaterThanOrEqualTo(-lift.IntervalMs), "and boarding behind it");
            }
            Assert.That(riders, Is.GreaterThan(0), "someone was riding");
        }

        [Test]
        public void GU16_PublishingAllocatesNothingAndChangesNoResult()
        {
            var a = Busy();
            var b = Busy();
            Published(a); Published(a); Published(a);   // warm: the frames' arrays are grown on the first fills
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 10; k++) a.PublishSkierRender();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero, "BG-01: no garbage per publish");
            for (int k = 0; k < 500; k++) { a.Step(1); a.PublishSkierRender(); b.Step(1); }
            Assert.That(a.Hash(), Is.EqualTo(b.Hash()), "the column is read from the state, never written into it");
        }

        [Test]
        public void BG01_TheHostRunningABusyDayAllocatesNothing()
        {
            // The whole worker loop as the game runs it (ticks, snapshots, the column, commands), at 1x, a tenth of a second
            // a service; the reads the screen makes in between too. The Development player saw ~128 B a snapshot.
            // As the game has it: a real site (sun, daylight) and a snowpack, its tiles drained as the screen drains them.
            var w = new SimWorld(P, GuestFixture.Resort(), new SimSite(43.59, -110.83), 2026, 7_777UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            const int fw = 160, fh = 120;
            var metres = new float[fw * fh];
            for (int i = 0; i < metres.Length; i++) metres[i] = 1.2f + (i % 7) * 0.05f;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, SimSnowpack.FromMetres(metres, fw, fh, 0, 0, fw, fh)));
            long until = Day + (DayHours.Default.OpenSecond + 3600) * 1000L;
            for (int guard = 0; w.FaceMs < until && guard < 20_000_000; guard++) w.Step(1);
            var time = new FakeTime();
            var host = new SimulationHost(w, time, threaded: false);
            host.Send(SimCommandKind.Resume, 0);
            host.Send(SimCommandKind.SetSpeed, 1);
            long read = 0;
            void Run(int services)
            {
                for (int k = 0; k < services; k++)
                {
                    time.Microseconds += 100_000;
                    host.Service();
                    var s = host.Latest;
                    var f = host.SkierRender.Latest;
                    read += s.Tick + f.Count + s.Guests.SkierCount;
                    var ring = host.Staging;
                    for (long n = ring.Released; n < s.StagedTiles; n++) ring.Read(n, out _, out _, out _, out _, out _, out _);
                    ring.Release(s.StagedTiles);
                }
            }
            Run(600);   // warm every path up
            long tick = host.Latest.Tick;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Run(3000);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(host.Latest.Tick, Is.GreaterThan(tick + 1000), "the day ran");
            Assert.That(read, Is.Not.Zero);
            Assert.That(bytes, Is.Zero, $"BG-01: the host's loop allocates nothing ({bytes} B over 3,000 services)");
        }

        [Test]
        public void BG01_TheTickP99IsTheSortedValue()
        {
            // The host selects the p99 tick time in place (Unity's Mono allocates in Array.Sort); it must be what sorting gives.
            for (int n = 1; n <= 256; n += 17)
                for (int seed = 0; seed < 5; seed++)
                {
                    var a = new int[n];
                    for (int i = 0; i < n; i++) a[i] = (int)((uint)(i * 2654435761u + seed * 40503u) % 97u);
                    var sorted = (int[])a.Clone();
                    Array.Sort(sorted);
                    int k = Math.Min(n - 1, n * 99 / 100);
                    Assert.That(SimulationHost.Select(a, n, k), Is.EqualTo(sorted[k]), $"n {n}, seed {seed}");
                }
        }

        // ---------- GU-02: selection inside the 100 ms bar ----------

        [Test]
        public void GU02_APickWhilePausedIsAnsweredWithoutATick()
        {
            var w = Busy();
            var time = new FakeTime();
            var host = new SimulationHost(w, time, threaded: false);
            long tick = host.Latest.Tick;
            var f = host.SkierRender.Latest;
            uint serial = f.Skiers[f.Count / 2].Serial;
            host.Send(SimCommandKind.InspectSkier, serial);
            host.Service();
            Assert.That(host.Latest.Tick, Is.EqualTo(tick), "paused: no tick ran");
            Assert.That((uint)host.Latest.Guests.SelectedSerial, Is.EqualTo(serial), "the card's block is in the very next snapshot");
            Assert.That(host.Latest.Guests.SelectedStayLaps, Is.GreaterThan(0));
        }

        [Test]
        public void GU02_APickWhileRunningIsAnsweredOnTheTickItLands()
        {
            var w = Busy();
            uint serial = 0;
            var f = Published(w);
            serial = f.Skiers[0].Serial;
            w.Step(3);   // put the refresh clock mid-interval
            w.ApplyNow(SimCommand.Of(SimCommandKind.InspectSkier, serial));
            var s = new SimSnapshot(w.Pacing);
            w.Write(s);
            Assert.That((uint)s.Guests.SelectedSerial, Is.EqualTo(serial), "not up to InspectTicks later");
        }

        // ---------- SN-31: the Traffic counters ----------

        [Test]
        public void SN31_TheTrafficCountersAreTheDaysPassesAndTheRollingHour()
        {
            var w = Busy(2 * 3600);
            var f = Published(w);
            Assert.That(f.EdgeCount, Is.EqualTo(GuestFixture.Network().EdgeFrom.Length), "one counter per network edge");
            long today = 0, hour = 0;
            var onEdge = new int[f.EdgeCount];
            for (int i = 0; i < f.Count; i++)
            {
                var r = f.Skiers[i];
                if (!r.OnALift && (r.State == (byte)GuestState.Skiing || r.State == (byte)GuestState.Walking)) onEdge[r.Index]++;
            }
            for (int e = 0; e < f.EdgeCount; e++)
            {
                today += f.EdgeToday[e]; hour += f.EdgeLastHour[e];
                Assert.That(f.EdgeLastHour[e], Is.LessThanOrEqualTo(f.EdgeToday[e]), "the hour is part of the day");
                Assert.That(f.EdgeOn[e], Is.EqualTo(onEdge[e]), "skiers on it now");
            }
            Assert.That(hour, Is.GreaterThan(0).And.LessThan(today), "two hours in, the last hour is some of the day");
            // A whole quiet hour empties the window, bucket by bucket, without a flash at the hour.
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 0));
            long until = w.FaceMs + 2 * SimWorld.HourMs;
            while (w.FaceMs < until) w.Step(8);
            f = Published(w);
            long later = 0;
            for (int e = 0; e < f.EdgeCount; e++) later += f.EdgeLastHour[e];
            Assert.That(later, Is.LessThan(hour), "the window rolled on");
        }

        [Test]
        public void SN31_ALiftsLoadIsItsBoardingsOverTheRollingHourAgainstWhatItCanCarry()
        {
            var w = Busy(2 * 3600);
            var s = new SimSnapshot(P);
            w.Step(1, s);
            var f = Published(w);
            Assert.That(f.LiftCount, Is.EqualTo(s.LiftCount), "one load per lift");
            long hour = 0, today = 0;
            for (int i = 0; i < f.LiftCount; i++)
            {
                Assert.That(f.LiftHourCapacity[i], Is.GreaterThan(0), "every lift can carry someone");
                Assert.That(f.LiftLastHour[i], Is.LessThanOrEqualTo(s.RidesToday[i]), "the hour is part of the day");
                Assert.That(f.LiftLastHour[i], Is.LessThanOrEqualTo(f.LiftHourCapacity[i] + s.Seats[i]),
                            "no lift boards more in an hour than its carriers hold");
                hour += f.LiftLastHour[i]; today += s.RidesToday[i];
            }
            Assert.That(hour, Is.GreaterThan(0).And.LessThan(today), "two hours in, the last hour is some of the day's rides");
            // Closed, the lifts stop boarding and the window empties.
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 0));
            long until = w.FaceMs + 2 * SimWorld.HourMs;
            while (w.FaceMs < until) w.Step(8);
            f = Published(w);
            long later = 0;
            for (int i = 0; i < f.LiftCount; i++) later += f.LiftLastHour[i];
            Assert.That(later, Is.LessThan(hour), "the window rolled on");
        }

        // ---------- GU-10: the table the sim and the screen share ----------

        static TrailRating.Result Profile()
        {
            // A 200 m trail of eight 25 m stations: steep at the top, a flat runout at the bottom.
            var along = new double[9];
            var surface = new double[9];
            double z = 100;
            for (int k = 0; k < 9; k++)
            {
                along[k] = k * 25;
                surface[k] = z;
                z -= k < 5 ? 12 : 1;
            }
            return new TrailRating.Result { Along = along, Surface = surface };
        }

        [Test]
        public void GU10_TheTableEndsOnTheTotalTheSimulationUses()
        {
            var r = Profile();
            var along = new float[64];
            var ms = new float[64];
            foreach (var (start, end) in new[] { (0, 200_000), (30_000, 170_000), (0, 12_000), (190_000, 200_000) })
            {
                int len = end - start;
                int total = SkiTimeTable.TotalMs(r, 1, start, end, len);
                int n = SkiTimeTable.Fill(r, 1, start, end, len, along, ms);
                Assert.That(n, Is.GreaterThanOrEqualTo(2));
                Assert.That((along[0], ms[0]), Is.EqualTo((0f, 0f)));
                Assert.That(ms[n - 1], Is.EqualTo((float)total).Within(0.5f), "the dot reaches the bottom on the worker's tick");
                Assert.That(along[n - 1], Is.EqualTo(len / 1000f).Within(0.01f), "at the piece's length");
                for (int k = 1; k < n; k++)
                {
                    Assert.That(along[k], Is.GreaterThan(along[k - 1]));
                    Assert.That(ms[k], Is.GreaterThan(ms[k - 1]));
                }
            }
            // The runout is slower per metre than the headwall: that is the point of the table.
            int whole = SkiTimeTable.Fill(r, 1, 0, 200_000, 200_000, along, ms);
            float headwall = (ms[1] - ms[0]) / (along[1] - along[0]);
            float runout = (ms[whole - 1] - ms[whole - 2]) / (along[whole - 1] - along[whole - 2]);
            Assert.That(runout, Is.GreaterThan(headwall));
        }

        [Test]
        public void GU10_WithNoProfileThePieceGoesAtTheBandsOwnSpeed()
        {
            var along = new float[4];
            var ms = new float[4];
            int total = SkiTimeTable.TotalMs(null, 0, 0, 100_000, 100_000);
            int n = SkiTimeTable.Fill(null, 0, 0, 100_000, 100_000, along, ms);
            Assert.That(n, Is.EqualTo(2));
            Assert.That(ms[1], Is.EqualTo((float)total));
            Assert.That(total, Is.EqualTo((int)(100_000 * 10_000L / (130 * 4_470L))), "13 mph");
        }

        // ---------- GU-10 v2: confidence ----------

        [Test]
        public void GU10v2_ConfidenceIsAStepPerBandClamped()
        {
            var w = new SimWorld(P, GuestFixture.Resort(), SimSite.None, 2026, 7_777UL);
            Assert.That(w.ConfidencePermille(3, 0), Is.EqualTo(1400), "an expert on a green zooms (capped at 1.4)");
            Assert.That(w.ConfidencePermille(2, 0), Is.EqualTo(1400));
            Assert.That(w.ConfidencePermille(1, 0), Is.EqualTo(1200));
            Assert.That(w.ConfidencePermille(1, 1), Is.EqualTo(1000), "on its own band, its own speed");
            Assert.That(w.ConfidencePermille(0, 1), Is.EqualTo(800), "a beginner on a blue is a bit slower");
            Assert.That(w.ConfidencePermille(0, 3), Is.EqualTo(700), "and never slower than the floor");
        }

        [Test]
        public void GU10v2_AnExpertTakesAGreenFasterThanItsOwnBlack()
        {
            // The fixture's black and the green are the same length; an expert's time on each, per metre, from the column.
            var w = Busy(3 * 3600);
            var net = GuestFixture.Network();
            double greenRate = 0, blackRate = 0;
            int greens = 0, blacks = 0;
            int guard = 0;
            while ((greens < 3 || blacks < 3) && guard++ < 20_000)
            {
                w.Step(1);
                var f = Published(w);
                for (int i = 0; i < f.Count; i++)
                {
                    var r = f.Skiers[i];
                    if (r.OnALift || r.State != (byte)GuestState.Skiing || r.Band != 3) continue;
                    double perMetre = (r.ExitTick - (double)r.EntryTick) / Math.Max(1, net.EdgeBaseMs[r.Index]);
                    if (net.EdgeTrueBand[r.Index] == 0 && greens < 3) { greenRate += perMetre; greens++; }
                    if (net.EdgeTrueBand[r.Index] == 2 && blacks < 3) { blackRate += perMetre; blacks++; }
                }
            }
            if (greens == 0 || blacks == 0) Assert.Inconclusive("the fixture's experts didn't ski both");
            Assert.That(greenRate / greens, Is.LessThan(blackRate / blacks),
                "against the trail's own reference time, an expert is quicker on a green than on a black");
        }

        // ---------- GU-10 v3 (Guests Engine v0.1): steepness by ability ----------

        static float PerMetre(float[] along, float[] ms, int k) => (ms[k] - ms[k - 1]) / (along[k] - along[k - 1]);

        [Test]
        public void GU10v3_ABeginnerHoldsBackOnThePitchAndCoastsLikeAnExpertOnTheFlat()
        {
            // The profile's top is a 48 % headwall on a blue, its bottom a 4 % runout.
            var r = Profile();
            var a0 = new float[64]; var m0 = new float[64];
            var a3 = new float[64]; var m3 = new float[64];
            int n0 = SkiTimeTable.Fill(r, 1, 0, 0, 200_000, 200_000, a0, m0, null);
            int n3 = SkiTimeTable.Fill(r, 1, 3, 0, 200_000, 200_000, a3, m3, null);
            Assert.That(n0, Is.EqualTo(n3), "every band walks the same stations");
            Assert.That(PerMetre(a0, m0, 1), Is.GreaterThan(2.5f * PerMetre(a3, m3, 1)),
                "on the headwall a beginner goes at well under half an expert's speed (1.0 x 0.5 against 1.3)");
            Assert.That(PerMetre(a0, m0, n0 - 1), Is.EqualTo(PerMetre(a3, m3, n3 - 1)).Within(0.5f),
                "on the runout everybody coasts alike");
            Assert.That(m0[n0 - 1], Is.GreaterThan(m3[n3 - 1]));
        }

        [Test]
        public void GU10v3_AHigherBandIsNeverSlowerAnywhere()
        {
            for (byte trail = 0; trail < 4; trail++)
                for (int grade = 0; grade <= 100; grade++)
                    for (byte skier = 0; skier < 3; skier++)
                        Assert.That(SkiTimeTable.StationFactor(trail, (byte)(skier + 1), grade),
                            Is.GreaterThanOrEqualTo(SkiTimeTable.StationFactor(trail, skier, grade)), $"trail {trail}, {grade} %, skier {skier}");
        }

        [Test]
        public void GU10v3_TheExpertsTableIsTheReferenceSoTheReferenceNeverMoved()
        {
            var r = Profile();
            foreach (byte trail in new byte[] { 0, 1, 2, 3 })
            {
                Assert.That(SkiTimeTable.TotalMs(r, trail, SkiTimeTable.Reference, 0, 200_000, 200_000),
                    Is.EqualTo(SkiTimeTable.TotalMs(r, trail, 0, 200_000, 200_000)));
                // The old cap and no fear: the v1 factor, clamp(grade / design, 0.4, 1.3).
                for (int grade = 0; grade <= 100; grade += 5)
                    Assert.That(SkiTimeTable.StationFactor(trail, 3, grade),
                        Is.EqualTo(Math.Max(0.4, Math.Min(1.3, grade / (double)SkiTimeTable.BandDesignGrade(trail)))).Within(1e-12));
            }
        }

        [Test]
        public void GU10v3_TheTableMarksWhereASkierHoldsBack()
        {
            var r = Profile();
            var along = new float[64]; var ms = new float[64]; var caution = new float[64];
            int n = SkiTimeTable.Fill(r, 1, 0, 0, 200_000, 200_000, along, ms, caution);
            Assert.That(caution[0], Is.EqualTo(1f));
            Assert.That(caution[1], Is.EqualTo((float)SkiTimeTable.CautionFloor), "a beginner on a 48 % pitch is at the floor");
            Assert.That(caution[n - 1], Is.EqualTo(1f), "and at ease on the runout");
            int e = SkiTimeTable.Fill(r, 1, 3, 0, 200_000, 200_000, along, ms, caution);
            for (int k = 0; k < e; k++) Assert.That(caution[k], Is.EqualTo(1f), "an expert never holds back");
            Assert.That(SkiTimeTable.Caution(25, 18), Is.EqualTo(2 - 25 / 18.0).Within(1e-12));
            Assert.That(SkiTimeTable.Caution(40, 18), Is.EqualTo(SkiTimeTable.CautionFloor), "never under the floor");
            Assert.That(SkiTimeTable.Caution(10, 18), Is.EqualTo(1));
            Assert.That(SkiTimeTable.Caution(90, 0), Is.EqualTo(1), "no comfort grade: no fear");
        }

        [Test]
        public void GU10v3_TheNetworkCarriesEveryBandsTimeAndOldFactsKeepOne()
        {
            var net = GuestFixture.Network();
            for (int e = 0; e < net.EdgeBaseMs.Length; e++)
            {
                Assert.That(net.EdgeBandMs[e * Pacing.Bands + 3], Is.EqualTo(net.EdgeBaseMs[e]), "the expert's is the reference");
                for (int b = 0; b < 3; b++)
                    Assert.That(net.EdgeBandMs[e * Pacing.Bands + b], Is.GreaterThanOrEqualTo(net.EdgeBandMs[e * Pacing.Bands + b + 1]));
                if (net.EdgeKindOf[e] == (byte)SimEdgeKind.Path)
                    for (int b = 0; b < Pacing.Bands; b++) Assert.That(net.EdgeBandMs[e * Pacing.Bands + b], Is.EqualTo(net.EdgeBaseMs[e]));
            }
            var old = new TrailEdgeFacts(1, 1, 3048, 24_000, 90_000);
            for (int b = 0; b < Pacing.Bands; b++) Assert.That(old.MsFor(b), Is.EqualTo(90_000), "facts with one time give it to every band");
            var oneTime = SimNetwork.Of(2, new List<(int, int, int)> { (1, 0, 0) }, new ulong[] { 7 }, new List<TrailEdgeFacts> { old }, new List<int> { 800_000 });
            for (int b = 0; b < Pacing.Bands; b++) Assert.That(oneTime.EdgeBandMs[b], Is.EqualTo(90_000));
            // A steady blue pitch: a beginner past its comfort, an intermediate a touch held back, an advanced skier at ease.
            var blue = TrailEdgeFacts.SteadyPitch(1, 1, 3048, 24_000, 100_000);
            Assert.That(blue.MsFor(0), Is.GreaterThan(blue.MsFor(1)));
            Assert.That(blue.MsFor(1), Is.GreaterThan(blue.MsFor(2)));
            Assert.That(blue.MsFor(2), Is.EqualTo(100_000));
        }

        [Test]
        public void GU10v3_APartyIsScheduledFromItsOwnBandsTime()
        {
            var w = Busy(600);
            var net = GuestFixture.Network();
            for (int e = 0; e < net.EdgeBaseMs.Length; e++)
            {
                bool path = net.EdgeKindOf[e] == (byte)SimEdgeKind.Path;
                for (int band = 0; band < Pacing.Bands; band++)
                {
                    int want = path ? net.EdgeBaseMs[e] : net.EdgeBandMs[e * Pacing.Bands + band];
                    long real = (long)want * 1000 / 1000 / P.SkierSpeedFactor;
                    Assert.That(w.EdgeTicks(e, 1000, path, band), Is.EqualTo((int)Math.Max(1, (real + P.TickMs - 1) / P.TickMs)),
                        $"edge {e}, band {band}");
                }
                if (!path) Assert.That(w.EdgeTicks(e, 1000, false, 0), Is.GreaterThan(w.EdgeTicks(e, 1000, false, 3)), "a beginner takes longer");
            }
        }

        sealed class FakeTime : IRealTime
        {
            public long Microseconds { get; set; }
        }
    }
}
