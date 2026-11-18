using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests). Task 4.24 PR B, RD-1…RD-5 and
    // LO-10 v2, on SeasonRunner.RideDownDesign: chairs 101 and 202 (indices 0, 1) and lift 303 (index 2) from the base (node
    // 0) to the peak (node 3), whose only way home is a black.
    public sealed class RideDownTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;
        const int Gondola = 2;
        static long Stop => Day + DayHours.Default.StopSecond * 1000L;

        static SimWorld Opened(SimDesign d)
        {
            var w = new SimWorld(P, d, SimSite.None, 2026, 4242UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            return w;
        }

        /// <summary>Per state, per band, how many skier-ticks were seen on lift 303 over day 1 (to one tick past the stop).</summary>
        static int[,] PlayDay(SimWorld w)
        {
            var seen = new int[10, 4];
            while (w.FaceMs < Stop)
            {
                w.Step(1);
                w.ForEachSkier((serial, band, edge, lift, state) => { if (lift == Gondola) seen[(int)state, band]++; });
            }
            w.Step(1);
            return seen;
        }

        static void Balances(SimWorld w)
        {
            var g = w.GuestLedger();
            Assert.That(g.arrived, Is.GreaterThan(0), "guests came");
            Assert.That(g.left, Is.EqualTo(g.arrived), "SI-10: everyone who came left");
            Assert.That(g.stranded, Is.Zero, "RD-1: nobody is stranded at a top");
            Assert.That(w.SkierCount, Is.Zero, "the hill is clear for the night");
        }

        [Test]
        public void RD2_AChairsOnlyDayCarriesNobodyDown()
        {
            var w = Opened(SeasonRunner.RideDownDesign(gondola: false));
            var seen = PlayDay(w);
            for (int b = 0; b < 4; b++)
            {
                Assert.That(seen[(int)GuestState.QueuedDown, b] + seen[(int)GuestState.RidingDown, b], Is.Zero, "a chair carries nobody down");
                Assert.That(seen[(int)GuestState.Riding, 0], Is.Zero, "GU-07: no beginner rides a chair to a black-only top");
            }
            foreach (ulong id in new ulong[] { 101, 202, 303 }) Assert.That(w.DownLedgerOf(id).ridesDown, Is.Zero);
            Balances(w);
        }

        [Test]
        public void RD3_RD4_BeginnersRideTheGondolaUpToLookAndRideBackDown()
        {
            var w = Opened(SeasonRunner.RideDownDesign(gondola: true));
            var seen = PlayDay(w);
            Assert.That(seen[(int)GuestState.Riding, 0], Is.GreaterThan(0), "RD-4: some beginners ride up to the hard top");
            Assert.That(seen[(int)GuestState.RidingDown, 0], Is.GreaterThan(0), "RD-3: and ride back down");
            Assert.That(seen[(int)GuestState.QueuedDown, 0], Is.GreaterThan(0), "through the top line (RD-2)");
            Balances(w);
        }

        [Test]
        public void RD3_AnExpertSkisTheBlackHomeUnlessTheDayIsEnding()
        {
            var w = Opened(SeasonRunner.RideDownDesign(gondola: true));
            int expertsUp = 0, expertsDownMidday = 0;
            while (w.FaceMs < Stop)
            {
                w.Step(1);
                bool midday = w.FaceMs < Day + 13 * Hour;
                w.ForEachSkier((serial, band, edge, lift, state) =>
                {
                    if (lift != Gondola || band != 3) return;
                    if (state == GuestState.Riding) expertsUp++;
                    if (midday && state == GuestState.QueuedDown) expertsDownMidday++;
                });
            }
            Assert.That(expertsUp, Is.GreaterThan(0), "experts ride the gondola");
            // An expert's laps can end at the top (homeward), so a few may ride down; most ski the black.
            Assert.That(expertsDownMidday, Is.LessThan(expertsUp / 4), "RD-3: a band that can ski home is offered the ride down only to go home");
        }

        [Test]
        public void RD5_TheStopEmptiesTheTopLineAndTheCabins()
        {
            var w = Opened(SeasonRunner.RideDownDesign(gondola: true));
            PlayDay(w);
            var d = w.DownLedgerOf(303);
            Assert.That((d.topLine, d.ridingDown), Is.EqualTo((0, 0)));
            Balances(w);
        }

        [Test]
        public void RD1_ADownloadingChairCarriesItsRidersDownAndStrandsNobody()
        {
            var w = Opened(SeasonRunner.RideDownDesign(gondola: false));
            while (w.FaceMs < Day + 11 * Hour) w.Step(1);
            Assert.That(w.StatusOf(303), Is.EqualTo(LiftOpStatus.Open));
            // The black goes mid-day: the chair downloads (LO-10), and everyone it brings up rides it back down.
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, SeasonRunner.RideDownDesign(false, 2, peakTrail: false)));
            Assert.That(w.StatusOf(303), Is.EqualTo(LiftOpStatus.Downloading));
            int down = 0;
            while (w.FaceMs < Day + 13 * Hour)
            {
                w.Step(1);
                w.ForEachSkier((serial, band, edge, lift, state) =>
                {
                    if (lift == Gondola && (state == GuestState.QueuedDown || state == GuestState.RidingDown)) down++;
                });
            }
            Assert.That(down, Is.GreaterThan(0), "the riders who reached the top ride down");
            var d = w.DownLedgerOf(303);
            Assert.That((d.topLine, d.ridingDown), Is.EqualTo((0, 0)), "and the top has drained within two hours (a compressed ride down is about as long as the ride up)");
            while (w.FaceMs < Stop) w.Step(1);
            w.Step(1);
            Balances(w);
        }

        [Test]
        public void LO10v2_AGondolaIsItsOwnWayDown()
        {
            var w = Opened(SeasonRunner.RideDownDesign(true, 1, peakTrail: false));
            while (w.FaceMs < Day + 10 * Hour) w.Step(4);
            Assert.That(w.StatusOf(303), Is.EqualTo(LiftOpStatus.Open), "a gondola opens with no trail from its top");
            var c = Opened(SeasonRunner.RideDownDesign(false, 1, peakTrail: false));
            while (c.FaceMs < Day + 10 * Hour) c.Step(4);
            Assert.That(c.StatusOf(303), Is.EqualTo(LiftOpStatus.Closed), "a chair does not");
        }

        [Test]
        public void RD2_ASaveMidRideHasTheSameFuture()
        {
            var w = Opened(SeasonRunner.RideDownDesign(true));
            while (w.FaceMs < Day + 11 * Hour) w.Step(1);
            var d = w.DownLedgerOf(303);
            Assert.That(d.topLine + d.ridingDown, Is.GreaterThan(0), "saved with someone at the top or on the way down");
            byte[] bytes = w.SaveState();
            var back = SimSidecar.Read(bytes, P, SeasonRunner.RideDownDesign(true), SimSite.None, out var header);
            Assert.That(header.SidecarVersion, Is.EqualTo(SimSidecar.Version));
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()));
            var a = new List<ulong>(); var b = new List<ulong>();
            for (int k = 0; k < 600; k++) { w.Step(1); back.Step(1); a.Add(w.Hash()); b.Add(back.Hash()); }
            Assert.That(b, Is.EqualTo(a), "SI-12: the restored world plays on exactly as the one that saved");
        }

        [Test]
        public void RD_RemovingALowerLiftKeepsEverySkierOnItsOwnLift()
        {
            var w = Opened(SeasonRunner.RideDownDesign(true));
            while (w.FaceMs < Day + 11 * Hour) w.Step(1);
            int before = w.DownLedgerOf(303).topLine + w.DownLedgerOf(303).ridingDown;
            int lineBefore = w.LineOf(303);
            // Lift 101 (index 0) goes: 303 becomes index 1, and everyone on it must follow it there.
            var d = SeasonRunner.RideDownDesign(true, 2);
            var lifts = new[] { d.Lifts[1], d.Lifts[2] };
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetDesign, 0, new SimDesign(2, d.RevisionHash ^ 7, d.ResortSeed, d.Hours, lifts, 5, d.Network)));
            Assert.That(w.DownLedgerOf(303).topLine + w.DownLedgerOf(303).ridingDown, Is.EqualTo(before), "the top line and the cabins kept");
            Assert.That(w.LineOf(303), Is.EqualTo(lineBefore), "and the bottom line");
            int wrong = 0;
            w.ForEachSkier((serial, band, edge, lift, state) =>
            {
                if ((state == GuestState.QueuedDown || state == GuestState.RidingDown) && lift != 1) wrong++;
            });
            Assert.That(wrong, Is.Zero, "every top-line skier and down rider is on 303's new index");
            while (w.FaceMs < Stop) w.Step(4);
            w.Step(1);
            Balances(w);
        }

        /// <summary>
        /// The -drawngondola layout in miniature (SeasonRunner.UpperMountainDesign): the upper mountain's skiers leave it only
        /// by riding gondola 303 down (its top is no base, though chair 404 leaves it), so riders go down during the day (not only at the stop), and nobody is stranded.
        /// </summary>
        [Test]
        public void RD1_TheUpperMountainLeavesByTheGondola()
        {
            var w = Opened(SeasonRunner.UpperMountainDesign());
            int before16 = 0;
            for (int hour = 9; hour <= 16; hour++)
            {
                while (w.FaceMs < Day + hour * Hour) w.Step(1);
                var d = w.DownLedgerOf(303);
                if (hour < 16) before16 = d.ridesDown;
                TestContext.WriteLine($"{hour}:00 {w.SkierCount} on the hill, {d.topLine} at the top, {d.ridingDown} riding down, {d.ridesDown} down");
            }
            Assert.That(before16, Is.GreaterThan(100), "riders go down all day, not only at the stop (GU-06: a gondola top is no base)");
            while (w.FaceMs < Stop) w.Step(1);
            w.Step(1);
            Balances(w);
        }
    }
}
