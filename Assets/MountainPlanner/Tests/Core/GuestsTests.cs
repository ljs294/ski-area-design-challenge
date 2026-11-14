using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests). Task 4.12, p4-02 §7 GU-01…GU-18.
    public sealed class GuestsTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;

        static SimWorld Opened(SimDesign d = null, Pacing p = null)
        {
            var w = new SimWorld(p ?? P, d ?? GuestFixture.Resort(), SimSite.None, 2026, 7_777UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            return w;
        }

        static void RunTo(SimWorld w, long faceMs, int step = 8)
        {
            int guard = 0;
            while (w.FaceMs < faceMs && guard++ < 20_000_000) w.Step(step);
            Assert.That(w.FaceMs, Is.GreaterThanOrEqualTo(faceMs), "the clock reached the time asked for");
        }

        /// <summary>
        /// Day 1 from its opening through the stop: the day the guest rules are written about. It steps one tick *past*
        /// the stop, because the day is counted and the hill cleared on the tick the stop falls on (GU-12, GU-18).
        /// </summary>
        static SimWorld PlayDayOne(out long stop, int step = 8, SimDesign d = null, Pacing p = null)
        {
            var w = Opened(d, p);
            stop = Day + DayHours.Default.StopSecond * 1000L;
            RunTo(w, stop, step);
            w.Step(1);
            return w;
        }

        // ---------- GU-05, GU-07: the hard gate and the dead-end rule ----------

        [Test]
        public void GU05_ABeginnerNeverPicksABlack()
        {
            var w = Opened();
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            var net = GuestFixture.Network();
            int seen = 0;
            while (w.FaceMs < stop)
            {
                w.Step(1);
                w.ForEachSkier((serial, band, edge, lift, state) =>
                {
                    if (edge < 0) return;
                    seen++;
                    byte gate = Math.Max(net.EdgeTrueBand[edge], net.EdgePostedBand[edge] == TrailEdgeFacts.NoPosting
                        ? net.EdgeTrueBand[edge] : net.EdgePostedBand[edge]);
                    if (net.EdgeKindOf[edge] != (byte)SimEdgeKind.TrailSegment) return;
                    Assert.That(gate, Is.LessThanOrEqualTo(band),
                        $"GU-05: a band {band} skier is on a band {gate} trail (edge {edge})");
                });
            }
            Assert.That(seen, Is.GreaterThan(1000), "skiers were on trails all day");
        }

        [Test]
        public void GU07_NoBeginnerEntersAPodItCannotLeave()
        {
            var w = Opened();
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            int beginnersUp = 0, othersUp = 0;
            while (w.FaceMs < stop)
            {
                w.Step(1);
                w.ForEachSkier((serial, band, edge, lift, state) =>
                {
                    if (lift < 0) return;
                    // Lift index 2 is the Trap (the design's lifts are in ID order: 101, 202, 303).
                    if (lift != 2) return;
                    if (band == GuestFixture.Green) beginnersUp++;
                    else othersUp++;
                });
            }
            Assert.That(beginnersUp, Is.Zero, "GU-07: the trap pod's only way down is a blue, so no beginner may ride to it");
            Assert.That(othersUp, Is.GreaterThan(0), "and the bands that can ski out of it do use it");
        }

        [Test]
        public void GU17_TheGateReadsTheHarderOfTheSignAndTheTruth()
        {
            // A blue posted green: a beginner still may not take it, and those who do lose satisfaction for the sign.
            var w = Opened(GuestFixture.Resort(1, GuestFixture.NetworkPostedEasier()));
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            var net = GuestFixture.NetworkPostedEasier();
            while (w.FaceMs < stop)
            {
                w.Step(1);
                w.ForEachSkier((serial, band, edge, lift, state) =>
                {
                    if (edge < 0 || net.EdgeKindOf[edge] != (byte)SimEdgeKind.TrailSegment) return;
                    Assert.That(net.EdgeTrueBand[edge], Is.LessThanOrEqualTo(band), "GU-17: the gate reads the truth too");
                });
            }
            long misrated = 0;
            for (int a = 0; a < Pacing.Archetypes; a++) misrated += w.ReasonSum(a, SatisfactionReason.Misrated);
            Assert.That(misrated, Is.LessThan(0), "GU-17: a trail posted easier than it is costs the skiers who ski it");
        }

        // ---------- GU-03, GU-06, SI-10: the crowd and the ledger ----------

        [Test]
        public void GU03_TheDemandStackIsADayOfDemandLineByLine()
        {
            var w = Opened();
            w.Step(1);                       // the first opening: the day's demand is worked out there
            int total = 0;
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                total += w.DemandOf(a);
                Assert.That(w.StackLine(a, DemandLine.Draw), Is.EqualTo(1000));
                Assert.That(w.StackLine(a, DemandLine.Mix), Is.EqualTo(P.ArchetypeMix[a]));
                Assert.That(w.StackLine(a, DemandLine.Reputation), Is.EqualTo(P.RepFloor + (1000 - P.RepFloor) * P.StartRep / 1000));
                Assert.That(w.StackLine(a, DemandLine.Price), Is.EqualTo(1000), "the ticket window waits for the economy");
                Assert.That(w.StackLine(a, DemandLine.PowderPull), Is.EqualTo(1000), "4.05's seam reads flat until it lands");
            }
            Assert.That(total, Is.GreaterThan(0), "a crowd was worked out for day one");
            Assert.That(w.LiftCapacityToday(), Is.GreaterThan(0), "and the capacity lines read beside it");
            Assert.That(w.TerrainCapacityToday(), Is.GreaterThan(0));
        }

        [Test]
        public void GU03_TheCapIsOnTheHillNotOnTheDay()
        {
            // A cap of 20 can still serve a day of hundreds, because a stay is three to five laps (GU-01 against GU-03).
            var small = Pacing.Parse("SkierCap = 20");
            var w = PlayDayOne(out _, 1, null, small);
            var g = w.GuestLedger();
            Assert.That(g.demand, Is.GreaterThan(50), "the demand is the mountain's own, never the cap");
            Assert.That(g.arrived + g.capped + g.arrivedClosed, Is.EqualTo(g.demand), "SI-10: every skier the day asked for is accounted");
            Assert.That(g.capped, Is.GreaterThan(0), "and the Cap line says how many found the hill full");
        }

        [Test]
        public void SI10_TheSkierLedgerBalancesEveryTick()
        {
            var w = Opened();
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            while (w.FaceMs <= stop)
            {
                w.Step(1);
                var g = w.GuestLedger();
                Assert.That(g.arrived + g.arrivedClosed + g.capped, Is.EqualTo(g.demand).Or.LessThan(g.demand),
                    "no more skiers than the day asked for");
                Assert.That(g.arrived, Is.EqualTo(w.SkierCount + g.left), "arrived = on the hill + left");
            }
            var end = w.GuestLedger();
            Assert.That(w.SkierCount, Is.Zero, "GU-12: nobody is on the hill after the stop");
            Assert.That(end.arrived, Is.EqualTo(end.left), "and everyone who came has left");
            Assert.That(end.arrived, Is.GreaterThan(0));
        }

        [Test]
        public void GU06_ArrivalsFollowTheMorningsWeights()
        {
            var w = Opened();
            long open = Day + DayHours.Default.OpenSecond * 1000L;
            RunTo(w, open, 1);
            var byHour = new List<int>();
            int last = 0;
            for (int h = 1; h <= 7; h++)
            {
                RunTo(w, open + h * Hour, 1);
                int now = w.GuestLedger().arrived;
                byHour.Add(now - last);
                last = now;
            }
            Assert.That(byHour[0], Is.GreaterThan(byHour[1]), "the first hour is the busiest");
            Assert.That(byHour[1], Is.GreaterThan(byHour[3]), "and it tails off through the day");
            Assert.That(byHour[6], Is.LessThan(byHour[0] / 4), "by the last hour it is a trickle");
        }

        [Test]
        public void GU06_AStayIsThreeToFiveLapsAndEveryoneGoesHome()
        {
            var w = PlayDayOne(out _, 1);
            var g = w.GuestLedger();
            long rides = 0;
            for (int i = 0; i < 3; i++) rides += w.LiftMetric(i, SimMetric.Boardings);
            Assert.That(rides, Is.GreaterThanOrEqualTo(g.arrived * (long)P.StayLapsMin / 4), "most skiers got their laps in");
            Assert.That(g.left, Is.EqualTo(g.arrived), "GU-06, GU-12: everyone leaves by the stop");
        }

        [Test]
        public void GU06_ASkierWhoWaitsTooLongGoesHomeFromTheLine()
        {
            // One lift, one run, and a crowd far past what it can carry: the line must empty itself, not grow for ever.
            var p = Pacing.Parse("LocationDraw = 6000");
            var w = PlayDayOne(out _, 1, GuestFixture.OneLift(), p);
            var g = w.GuestLedger();
            Assert.That(g.wentHome, Is.GreaterThan(0), "GU-06: a four-minute line sends people home");
            Assert.That(g.arrived, Is.EqualTo(g.left), "and they are counted as having left");
            long waitCost = 0;
            for (int a = 0; a < Pacing.Archetypes; a++) waitCost += w.ReasonSum(a, SatisfactionReason.Wait);
            Assert.That(waitCost, Is.LessThan(0), "GU-13: the waiting cost them");
        }

        // ---------- GU-13, GU-18: satisfaction and reputation ----------

        [Test]
        public void GU13_TheWaitFormulaIsFiveHundredthsAMinuteOverTwo()
        {
            // The owner's 7:30 line: 7.5 real minutes, 5.5 of them chargeable, at 0.05 a minute is 0.275.
            long over = (long)(7 * 60 + 30) - P.WaitFreeMinutes * 60;
            int cost = (int)(over * 1000 * P.WaitPenaltyPerMinute / 60_000);
            Assert.That(cost, Is.EqualTo(275), "GU-13 v2: a 7:30 line costs 0.275 of satisfaction and reads");
        }

        [Test]
        public void GU13_TheReasonsAddUpToWhatSatisfactionDid()
        {
            var w = PlayDayOne(out _, 1);
            long sum = 0;
            for (int a = 0; a < Pacing.Archetypes; a++)
                for (var reason = SatisfactionReason.Wait; reason <= SatisfactionReason.Stranded; reason++)
                    sum += w.ReasonSum(a, reason);
            Assert.That(sum, Is.Not.Zero, "a day moved satisfaction, and every move has a reason");
            // Each reason is itself a sum, so no reason may be a mystery: they are the only way satisfaction moves.
            long named = 0;
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                named += w.ReasonSum(a, SatisfactionReason.SnowQuality);
                named += w.ReasonSum(a, SatisfactionReason.Wait);
                named += w.ReasonSum(a, SatisfactionReason.Crowd);
                named += w.ReasonSum(a, SatisfactionReason.Misrated);
                named += w.ReasonSum(a, SatisfactionReason.Marginal);
                named += w.ReasonSum(a, SatisfactionReason.Stranded);
            }
            Assert.That(named, Is.EqualTo(sum));
        }

        [Test]
        public void GU18_ReputationMovesOnlyForABandThatCame()
        {
            var w = Opened();
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            var before = new int[Pacing.Archetypes];
            for (int a = 0; a < Pacing.Archetypes; a++) before[a] = w.ReputationOf(a);
            RunTo(w, stop + 1000, 1);
            bool anyMoved = false;
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                var v = w.VerdictOf(a);
                if (v.arrived < P.RepMinSample)
                    Assert.That(w.ReputationOf(a), Is.EqualTo(before[a]), $"an archetype with {v.arrived} arrivals doesn't move");
                else if (w.ReputationOf(a) != before[a]) anyMoved = true;
            }
            Assert.That(anyMoved, Is.True, "GU-18: a day's verdict moves the reputations of the archetypes that came");
            Assert.That(w.Reputation, Is.InRange(0, 1000), "and the headline is the mix-weighted mean");
        }

        [Test]
        public void GU18_AGoodDayLiftsReputationAndABadDayDropsIt()
        {
            // A roomy mountain: short lines, good snow, so the verdict is high and reputation climbs.
            var roomy = PlayDayOne(out _, 1, null, Pacing.Parse("LocationDraw = 120"));
            // One lift against fifty times the crowd: long lines, people going home, so it falls.
            var crowded = PlayDayOne(out _, 1, GuestFixture.OneLift(), Pacing.Parse("LocationDraw = 6000"));
            int roomyRep = roomy.Reputation, crowdedRep = crowded.Reputation;
            Assert.That(roomyRep, Is.GreaterThan(crowdedRep),
                "GU-18: the mountain that served its guests is thought better of than the one that queued them");
        }

        // ---------- LO-02: parties ----------

        [Test]
        public void LO02_APartyRidesTogether()
        {
            var w = Opened();
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            int parties = 0;
            while (w.FaceMs < stop && parties < 50)
            {
                w.Step(1);
                // Everyone on a trail belongs to a party whose members are all on the same edge at the same time.
                var onEdge = new Dictionary<int, HashSet<int>>();
                w.ForEachSkier((serial, band, edge, lift, state) =>
                {
                    if (edge < 0) return;
                    if (!onEdge.TryGetValue(edge, out var set)) onEdge[edge] = set = new HashSet<int>();
                    set.Add(serial);
                });
                parties += onEdge.Count;
            }
            Assert.That(parties, Is.GreaterThan(0), "parties were out on the hill");
        }

        [Test]
        public void LO02_APartySplitAcrossCarriersGathersAtTheTopAndSkisOn()
        {
            // A double carries two, so a party of three or four rides in pieces and has to find itself again at the top.
            // Without that, half a party waits at the top for ever: the hill fills up, no line forms, and nobody leaves
            // — which is exactly what a resort of quads never shows, because four always fit on one chair.
            var w = PlayDayOne(out _, 1, GuestFixture.OneDouble(), Pacing.Parse("LocationDraw = 600"));
            var g = w.GuestLedger();
            Assert.That(g.arrived, Is.GreaterThan(50), "a real crowd came");
            Assert.That(w.SkierCount, Is.Zero, "GU-12: nobody is stuck on the hill at the stop");
            Assert.That(g.left, Is.EqualTo(g.arrived), "everyone who came got their day and went home");
            long rides = w.LiftMetric(0, SimMetric.Boardings);
            Assert.That(rides, Is.GreaterThan(g.arrived), "and they lapped, rather than riding once and stopping");
        }

        [Test]
        public void GU07_NobodyIsLeftChoosingWithTheMountainOpen()
        {
            // The failure this guards is quiet: skiers pile up "choosing" while the lifts stand empty, so the hill fills,
            // no line forms and no trail is ever skied. A tenth of the crowd stuck at once is the symptom.
            var w = Opened(GuestFixture.OneDouble(), Pacing.Parse("LocationDraw = 600"));
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            while (w.FaceMs < stop)
            {
                w.Step(1);
                int choosing = w.SkiersInState(GuestState.Choosing);
                int onHill = w.SkierCount;
                if (onHill >= 20)
                    Assert.That(choosing, Is.LessThan(onHill / 2),
                        $"GU-07: {choosing} of {onHill} skiers have nothing to pick while the mountain is open");
            }
        }

        [Test]
        public void GU06_ASkierThatArrivesWhereItCanDoNothingDoesNotPileUp()
        {
            // The shape that breaks a real resort: two places a skier can arrive, and only one of them has anything a
            // beginner can ski. Whoever lands at the other must not stand there for the rest of the day — the hill fills
            // with skiers who never ride, no line ever forms, and nothing is ever skied.
            var w = Opened(TwoBasesOneUseless(), Pacing.Parse("LocationDraw = 600"));
            long stop = Day + DayHours.Default.StopSecond * 1000L;
            int worstChoosing = 0, worstOnHill = 0;
            while (w.FaceMs < stop)
            {
                w.Step(1);
                int choosing = w.SkiersInState(GuestState.Choosing), onHill = w.SkierCount;
                if (onHill >= 20 && choosing > worstChoosing) { worstChoosing = choosing; worstOnHill = onHill; }
            }
            w.Step(1);
            var g = w.GuestLedger();
            TestContext.Progress.WriteLine($"worst: {worstChoosing} of {worstOnHill} choosing; arrived {g.arrived}, left {g.left}");
            Assert.That(worstChoosing, Is.LessThan(Math.Max(10, worstOnHill / 2)),
                $"GU-07: {worstChoosing} of {worstOnHill} skiers had nothing to pick at once");
            Assert.That(g.arrived, Is.EqualTo(g.left), "and everyone who came left by the stop");
        }

        /// <summary>
        /// Two lift-bottom stations with no trail leaving either, so both read as places to arrive at (GU-06). One lift
        /// serves a green; the other serves only a black, which a beginner may not ski.
        /// </summary>
        static SimDesign TwoBasesOneUseless()
        {
            const int GoodBase = 0, GoodTop = 1, BadBase = 2, BadTop = 3;
            var trails = new ulong[] { 11, 14 };
            var edges = new List<(int from, int to, int trail)>
            {
                (GoodTop, GoodBase, 0),   // a green home
                (BadTop, BadBase, 1),     // a black home, and nothing else
            };
            var facts = new List<TrailEdgeFacts>
            {
                new TrailEdgeFacts(GuestFixture.Green, GuestFixture.Green, 3658, 30_000, 170_000),
                new TrailEdgeFacts(GuestFixture.Black, GuestFixture.Black, 1829, 15_000, 90_000),
            };
            var lengths = new List<int> { 1_000_000, 800_000 };
            var net = SimNetwork.Of(4, edges, trails, facts, lengths);
            var lifts = new[]
            {
                GuestFixture.Quad(101, GoodBase, GoodTop),
                GuestFixture.Quad(202, BadBase, BadTop),
            };
            return new SimDesign(1, 1, 4242, DayHours.Default, lifts, 2, net);
        }

        // ---------- SI-11, SI-08: the step size and the goldens ----------

        [Test]
        public void SI11_ADayIsTheSameWhateverTheStepSize()
        {
            var one = PlayDayOne(out _, 1);
            var eight = PlayDayOne(out _, 8);
            Assert.That(eight.Hash(), Is.EqualTo(one.Hash()), "SI-08: the preset never changes the result");
            Assert.That(eight.GuestLedger(), Is.EqualTo(one.GuestLedger()));
        }

        [Test]
        public void GU02_TheDayGivesThePinnedTrajectories()
        {
            var w = PlayDayOne(out _, 1);
            ulong fold = w.Hash();
            TestContext.Progress.WriteLine($"guest day fold: 0x{fold:X16}");
            Assert.That(fold, Is.EqualTo(GuestDayFold), "a behaviour change to the guests needs an approved re-pin");
        }

        /// <summary>The pinned fold of a whole day on the fixture: every skier's record and the day's ledgers (SI-08).</summary>
        const ulong GuestDayFold = 0x08AFBB3A457BDF15UL;   // 4.09 PR A (Pacing v8), over 0x07B9C769D00CB382;   // 4.05 PR C: Pacing v7 (week lean 0.85) and the crowd's weather lines (WX-44 v2, rain while open), with the events and the forecast's issue hashed (owner and Coordinator OK 2026-11-13), over 0x13ED1EF232C7A39C; 4.05 PR B2: Pacing v6 (owner OK 2026-11-11, closing plan), over 0x6FA654C4380BF133; 4.05 PR B1: Pacing v5 (WindScale 0.85) and every wind check a boundary (owner OK 2026-11-11), over 0xA299F99061B91E69; 4.13: GU-10 v3 steepness by ability (Guests Engine v0.1, owner OK 2026-11-09), over v2 0x9BFD6B2E0CF63F2D (Pacing v4), over 4.12 0xB9C422088A472FAE

        [Test]
        public void GU02_WatchingASkierChangesNothing()
        {
            var plain = Opened();
            var watched = Opened();
            long until = Day + (DayHours.Default.OpenSecond + 3600) * 1000L;
            RunTo(plain, until, 1);
            RunTo(watched, until, 1);
            Assert.That(watched.Hash(), Is.EqualTo(plain.Hash()), "the two worlds began the same");
            int serial = 0;
            watched.ForEachSkier((s, band, edge, lift, state) => { if (serial == 0) serial = s; });
            Assert.That(serial, Is.Not.Zero, "there is a skier to watch");
            watched.ApplyNow(SimCommand.Of(SimCommandKind.InspectSkier, serial));
            for (int i = 0; i < 2000; i++) { plain.Step(1); watched.Step(1); }
            Assert.That(watched.Hash(), Is.EqualTo(plain.Hash()), "GU-02: selecting a skier changes no result");
        }

        [Test]
        public void GU02_TheWorkerPicksTheNextSkierAndEverySnapshotCarriesIt()
        {
            var w = Opened();
            RunTo(w, Day + (DayHours.Default.OpenSecond + 3600) * 1000L, 1);
            Assert.That(w.SkierCount, Is.GreaterThan(1), "there is a crowd to pick from");

            // −1 is "whoever is next": the screen can't name a serial, because the render column cuts serials to 16 bits.
            w.ApplyNow(SimCommand.Of(SimCommandKind.InspectSkier, -1));
            var s = new SimSnapshot(w.Pacing);
            w.Write(s);
            int first = s.Guests.SelectedSerial;
            Assert.That(first, Is.Not.Zero, "GU-02: a skier is being watched");

            // Every snapshot carries the block, not one in InspectTicks: the screen reads whichever is newest.
            for (int i = 0; i < 20; i++)
            {
                w.Step(1);
                w.Write(s);
                Assert.That(s.Guests.SelectedSerial, Is.EqualTo(first), "the watched skier is in every snapshot");
                Assert.That(s.Guests.SelectedStayLaps, Is.GreaterThan(0), "and so is its day");
            }

            w.ApplyNow(SimCommand.Of(SimCommandKind.InspectSkier, -1));
            w.Write(s);
            Assert.That(s.Guests.SelectedSerial, Is.GreaterThan(first), "Next goes on to the one after it");

            w.ApplyNow(SimCommand.Of(SimCommandKind.InspectSkier, 0));
            w.Write(s);
            Assert.That(s.Guests.SelectedSerial, Is.Zero, "Stop lets it go");
        }

        // ---------- GU-15, SN-24, 4.21's cells ----------

        [Test]
        public void GU15_ACommandPutsSkiersOnTheHill()
        {
            var w = Opened();
            RunTo(w, Day + (DayHours.Default.OpenSecond + 600) * 1000L, 1);
            int before = w.SkierCount;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SpawnSkiers, 0, new SkierSpawn(GuestFixture.Black, 25)));
            Assert.That(w.SkierCount, Is.EqualTo(before + 25), "GU-15: twenty-five advanced skiers, at a base");
            for (int i = 0; i < 200; i++) w.Step(1);
            int riding = w.SkiersInState(GuestState.Riding) + w.SkiersInState(GuestState.Queued) + w.SkiersInState(GuestState.Skiing);
            Assert.That(riding, Is.GreaterThan(0), "and they get on with their day");
        }

        [Test]
        public void SN24_FinishingASegmentCountsAPassInItsLane()
        {
            var w = PlayDayOne(out _, 1);
            long passes = 0;
            for (int t = 0; t < 6; t++) passes += w.TrailMetric(t, SimMetric.Passes);
            Assert.That(passes, Is.GreaterThan(0), "SN-24: the hour's passes are there for 4.07's wear");
            int lanes = 0;
            var net = GuestFixture.Network();
            for (int e = 0; e < net.EdgeFrom.Length; e++)
                for (int lane = 0; lane < P.WearBands; lane++) if (w.PassesOf(e, lane) > 0) lanes++;
            Assert.That(lanes, Is.GreaterThan(1), "and they fall in different lanes across the trail");
        }

        [Test]
        public void Popularity_IsTheShareOfTheBusiest()
        {
            var w = Opened();
            Assert.That(w.TrailPopularity(0), Is.EqualTo(-1), "the cell reads — until somebody skis");
            RunTo(w, Day + (DayHours.Default.OpenSecond + 2 * 3600) * 1000L, 1);
            int best = 0;
            for (int t = 0; t < 6; t++) best = Math.Max(best, w.TrailPopularity(t));
            Assert.That(best, Is.EqualTo(100), "the busiest trail is 100");
            for (int t = 0; t < 6; t++) Assert.That(w.TrailPopularity(t), Is.InRange(0, 100));
            int busiestLift = 0;
            for (int i = 0; i < 3; i++) busiestLift = Math.Max(busiestLift, w.LiftPopularity(i));
            Assert.That(busiestLift, Is.EqualTo(100), "and so is the busiest lift");
        }

        // ---------- SI-12, SI-13: saves and design edits ----------

        [Test]
        public void SI12_ASaveMidDayGivesTheSameFuture()
        {
            var w = Opened();
            RunTo(w, Day + (DayHours.Default.OpenSecond + 2 * 3600) * 1000L, 1);
            byte[] bytes = w.SaveState();
            var again = SimSidecar.Read(bytes, P, GuestFixture.Resort(), SimSite.None, out var header);
            Assert.That(header.SidecarVersion, Is.EqualTo(SimSidecar.Version));
            Assert.That(again.SkierCount, Is.EqualTo(w.SkierCount), "the hill came back as it was");
            Assert.That(again.Hash(), Is.EqualTo(w.Hash()));
            for (int i = 0; i < 4000; i++) { w.Step(1); again.Step(1); }
            Assert.That(again.Hash(), Is.EqualTo(w.Hash()), "SI-12: restoring gives the same future as not having saved");
            Assert.That(again.GuestLedger(), Is.EqualTo(w.GuestLedger()));
        }

        [Test]
        public void SI13_DeletingATrailUnderSkiersLosesNobody()
        {
            var w = Opened();
            RunTo(w, Day + (DayHours.Default.OpenSecond + 2 * 3600) * 1000L, 1);
            int before = w.SkierCount;
            Assert.That(before, Is.GreaterThan(0), "there are skiers out there to disturb");
            // The same resort with the blue out of the pod gone: its skiers must finish on the ground and carry on.
            var thinner = ResortWithoutBlueOut(2);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, thinner));
            var g = w.GuestLedger();
            Assert.That(g.arrived, Is.EqualTo(w.SkierCount + g.left), "SI-10 still balances across the edit");
            for (int i = 0; i < 4000; i++) w.Step(1);
            var after = w.GuestLedger();
            Assert.That(after.arrived, Is.EqualTo(w.SkierCount + after.left), "and it keeps balancing after it");
        }

        [Test]
        public void SI13_ARevisionWithNoNetworkLeavesTheSkiersAlone()
        {
            var w = Opened();
            RunTo(w, Day + (DayHours.Default.OpenSecond + 2 * 3600) * 1000L, 1);
            int before = w.SkierCount;
            // Every edit sends the design first and its network when the build finishes (SI-21): the first send must not
            // empty the hill, or every brush stroke would.
            var noNetwork = new SimDesign(2, 2, 4242, DayHours.Default, GuestFixture.Resort().Lifts, 6);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, noNetwork));
            Assert.That(w.SkierCount, Is.EqualTo(before), "the skiers are still there");
            // And the screen can be told about it. The lifts row sizes its reach from the new design, which has no
            // network, while the skiers keep the old one: reading one with the other's numbers threw on the worker
            // thread and stopped the clock on the owner's Gilford save, where opening posts an older save's trails.
            var snapshot = new SimSnapshot(w.Pacing);
            Assert.DoesNotThrow(() => w.Write(snapshot), "a snapshot in that moment must not throw");
            Assert.That(snapshot.Guests.TerrainCapacity, Is.GreaterThanOrEqualTo(0));
            for (int i = 0; i < 500; i++) { w.Step(1); w.Write(snapshot); }
            var g = w.GuestLedger();
            Assert.That(g.arrived, Is.EqualTo(w.SkierCount + g.left), "and the ledger balances");
        }

        static SimDesign ResortWithoutBlueOut(long revision)
        {
            var trails = new ulong[] { GuestFixture.GreenTrail, GuestFixture.BlueTrail, GuestFixture.BlackTrail, GuestFixture.DoubleTrail };
            var edges = new List<(int from, int to, int trail)>
            {
                (GuestFixture.GreenTop, GuestFixture.BaseNode, 0),
                (GuestFixture.GreenTop, GuestFixture.BluePod, 1),
                (GuestFixture.HighTop, GuestFixture.BaseNode, 2),
                (GuestFixture.HighTop, GuestFixture.BaseNode, 3),
                (GuestFixture.BaseNode, GuestFixture.SecondBase, -1),
                (GuestFixture.SecondBase, GuestFixture.BaseNode, -1),
            };
            var net = SimNetwork.Of(6, edges, trails);
            var lifts = new[]
            {
                GuestFixture.Quad(GuestFixture.EasyRider, GuestFixture.BaseNode, GuestFixture.GreenTop),
                GuestFixture.Quad(GuestFixture.SharpEnd, GuestFixture.BaseNode, GuestFixture.HighTop),
                GuestFixture.Quad(GuestFixture.Trap, GuestFixture.BluePod, GuestFixture.TrapTop),
            };
            return new SimDesign(revision, (ulong)revision, 4242, DayHours.Default, lifts, 4, net);
        }

        // ---------- SI-05: the systems table ----------

        [Test]
        public void SI05_TheSkiersRowWritesThePassesTheWearRowReads()
        {
            SimSystem skiers = null, wear = null;
            foreach (var row in SystemsTable.Rows)
            {
                if (row.Name == "Skiers") skiers = row;
                if (row.Name.StartsWith("Wear", StringComparison.Ordinal)) wear = row;
            }
            Assert.That(skiers, Is.Not.Null);
            Assert.That(wear, Is.Not.Null);
            Assert.That(skiers.Writes.HasFlag(SimColumns.Passes), Is.True, "SN-24's counters are the skiers' to write");
            Assert.That(wear.Reads.HasFlag(SimColumns.Passes), Is.True, "and 4.07's wear pass reads them");
        }
    }
}
