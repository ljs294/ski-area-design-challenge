using System;
using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // TW-04 and TW-05 (task 09): the rope's load on each head in both load cases (RP-04), LC-02's per-sheave limits and the
    // 750 lbf group minimum, worked by hand.
    public sealed class TowerForcesTests
    {
        const double Lbf = 4.448222;
        static double Deg(double d) => d * Math.PI / 180;

        [Test]
        public void TW05_OneSheaveTakesTheSmallerOfFourAndAHalfDegreesAndFifteenHundredPounds()
        {
            // At the counterweight's 18,000 lbf, 4.5° on one sheave loads it 2 × 18,000 × sin 2.25° = 1,413 lbf: the bend governs.
            Assert.That(TowerForces.GroupLoad(18_000 * Lbf, Deg(4.5)) / Lbf, Is.EqualTo(1413).Within(1));
            Assert.That(TowerForces.SheavesFor(18_000 * Lbf, Deg(4.5)), Is.EqualTo(1));
            Assert.That(TowerForces.SheavesFor(18_000 * Lbf, Deg(4.6)), Is.EqualTo(2));
            // Near the top of a 1,000 ft rise (about 46,000 lbf) the load governs: 4.5° is 3,612 lbf, three sheaves' worth, and
            // one sheave takes only 2·asin(1,500 ÷ 92,000) = 1.87°.
            Assert.That(TowerForces.GroupLoad(46_000 * Lbf, Deg(4.5)) / Lbf, Is.EqualTo(3612).Within(1));
            Assert.That(TowerForces.SheavesFor(46_000 * Lbf, Deg(4.5)), Is.EqualTo(3));
            Assert.That(TowerForces.SheavesFor(46_000 * Lbf, Deg(1.86)), Is.EqualTo(1));
            Assert.That(TowerForces.SheavesFor(46_000 * Lbf, Deg(1.88)), Is.EqualTo(2));
            // So a b8 turns the rope 8 × 4.5° = 36° at the bottom but only 2·asin(12,000 ÷ 92,000) = 14.99° there.
            Assert.That(TowerForces.SheavesFor(46_000 * Lbf, Deg(14.9)), Is.LessThanOrEqualTo(8));
            Assert.That(TowerForces.SheavesFor(46_000 * Lbf, Deg(15.1)), Is.GreaterThan(8));
        }

        [Test]
        public void TW04_TheSmallestSupportHeadThatCarriesTheRope()
        {
            double t = 25_000 * Lbf;
            Assert.That(TowerForces.Choose(t, t, Deg(-6), Deg(-5)), Is.EqualTo(LiftRope.Head.S4));
            Assert.That(TowerForces.Choose(t, t, Deg(-20), Deg(-18)), Is.EqualTo(LiftRope.Head.S6));
            Assert.That(TowerForces.Choose(t, t, Deg(-26), Deg(-24)), Is.EqualTo(LiftRope.Head.B8));
            Assert.That(TowerForces.Choose(t, t, Deg(-40), Deg(-35)), Is.EqualTo(LiftRope.Head.TooSharp), "TW-06: more than eight sheaves");
        }

        [Test]
        public void TW04_PullingUpInBothCasesIsAHoldDown()
        {
            double t = 25_000 * Lbf;
            Assert.That(TowerForces.Choose(t, t, Deg(5), Deg(6)), Is.EqualTo(LiftRope.Head.D8));
            Assert.That(TowerForces.Choose(t, t, Deg(40), Deg(41)), Is.EqualTo(LiftRope.Head.TooSharp));
        }

        [Test]
        public void TW04_FlippingBetweenTheCasesIsACombination()
        {
            double t = 25_000 * Lbf;
            Assert.That(TowerForces.Choose(t, t, Deg(-3), Deg(2)), Is.EqualTo(LiftRope.Head.C8));
        }

        [Test]
        public void LC02_AGroupWithinSevenHundredFiftyPoundsOfZeroIsACombination()
        {
            // 750 lbf at 25,000 lbf is a bend of 2·asin(750 ÷ 50,000) = 1.72°: just under it the rope isn't held seated.
            double t = 25_000 * Lbf;
            Assert.That(TowerForces.Choose(t, t, Deg(-1.8), Deg(-1.8)), Is.EqualTo(LiftRope.Head.S4));
            Assert.That(TowerForces.Choose(t, t, Deg(-1.8), Deg(-1.6)), Is.EqualTo(LiftRope.Head.C8), "the empty side presses too lightly");
            Assert.That(TowerForces.Choose(t, t, Deg(1.6), Deg(1.8)), Is.EqualTo(LiftRope.Head.C8), "a hold-down too light");
        }

        [Test]
        public void TW04_AShortStationSpanOnTheEmptySideStillSeatsTheRope()
        {
            // A station tower 45 ft past the bottom bullwheel at 18,000 lbf, the empty rope turned 3° over it: a 942 lbf group,
            // over the minimum, so it holds down without a combination.
            double t = 18_000 * Lbf;
            Assert.That(TowerForces.GroupLoad(t, Deg(3)) / Lbf, Is.EqualTo(942).Within(1));
            Assert.That(TowerForces.Choose(t, t, Deg(3.5), Deg(3)), Is.EqualTo(LiftRope.Head.D8));
        }

        [Test]
        public void TW11_TheGaugeReadsOneSheavesLoadAgainstItsLimit()
        {
            double t = 25_000 * Lbf;
            var loads = new TowerForces.Loads(t, t, Deg(-6), Deg(-5));
            Assert.That(loads.Head, Is.EqualTo(LiftRope.Head.S4));
            Assert.That(loads.Gauge(true), Is.EqualTo(TowerForces.GroupLoad(t, Deg(-6)) / 4 / TowerForces.SheaveLimitNewtons).Within(1e-12));
            Assert.That(loads.Gauge(true), Is.LessThan(0).And.GreaterThan(-1), "presses down, within the limit");
        }

        [Test]
        public void TW05_EachTerminalRowCarriesItsRope()
        {
            var sl = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var monta = LiftType.Of(LiftCatalogIds.MontaFg4);
            // The Sessellift return's eight sheaves at the counterweight: 14.3° is fine.
            Assert.That(TowerForces.RowCarries(sl.Bottom, sl.CounterweightNewtons, Deg(14.3)), Is.True);
            // The Monta return's single deflection sheave (LT6) at the top of a long lift: 1.5° fits, 3° doesn't.
            Assert.That(TowerForces.RowCarries(monta.Top, 40_000 * Lbf, Deg(1.5)), Is.True);
            Assert.That(TowerForces.RowCarries(monta.Top, 40_000 * Lbf, Deg(3)), Is.False);
        }

        [Test]
        public void TW16v3_TheMontaDrivesFromTheBottom()
        {
            // The owner's ruling (2026-10-18): terminals face where their bullwheels are.
            var monta = LiftType.Of(LiftCatalogIds.MontaFg4);
            Assert.That(monta.Bottom.Model, Is.EqualTo("monta_fg4_drive"));
            Assert.That(monta.Bottom.Row, Is.EqualTo(TerminalRow.HoldDown));
            Assert.That(monta.Bottom.Sheaves, Is.EqualTo(12));
            Assert.That(monta.Top.Model, Is.EqualTo("monta_fg4_return"));
            Assert.That(monta.Top.Row, Is.EqualTo(TerminalRow.Support));
            var sl = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            Assert.That(sl.Bottom.Model, Is.EqualTo("sessellift_fgq4_return"));
            Assert.That(sl.Top.Model, Is.EqualTo("sessellift_fgq4_drive"));
        }
        [Test]
        public void TW04v2_EachTypeChoosesFromItsOwnHeads()
        {
            // G6, LT41: a bend that needs 9-10 sheaves is carried by the D6's wide kit and too sharp for the quad's.
            double t = 50_000, deg = Math.PI / 180;   // light enough that the bend, not the load, sets the sheaves
            double bend = -9 * 4.5 * deg + 0.1 * deg;   // presses down: 9 sheaves by bend
            Assert.That(TowerForces.Choose(t, t, bend, bend), Is.EqualTo(LiftRope.Head.TooSharp), "the quad stops at 8");
            Assert.That(TowerForces.Choose(t, t, bend, bend, TowerForces.HeadKit.Wide), Is.EqualTo(LiftRope.Head.B10), "a sharp bend: the curved b10");
            Assert.That(TowerForces.Choose(t, t, -bend, -bend, TowerForces.HeadKit.Wide), Is.EqualTo(LiftRope.Head.D10), "pulling up: the d10");
            // A gentle bend under a heavy rope (the load needs the sheaves): the flat s10.
            double heavy = 150_000, gentle = -5 * 4.5 * deg;   // 22.5°: 5 sheaves by bend, 8 by load
            int need = TowerForces.SheavesFor(heavy, gentle);
            Assert.That(need, Is.InRange(7, 10));
            Assert.That(TowerForces.Choose(heavy, heavy, gentle, gentle, TowerForces.HeadKit.Wide), Is.EqualTo(LiftRope.Head.S10));
            // Small bends pick the same heads in both kits.
            Assert.That(TowerForces.Choose(t, t, -2 * deg, -2 * deg, TowerForces.HeadKit.Wide), Is.EqualTo(TowerForces.Choose(t, t, -2 * deg, -2 * deg)));
            Assert.That(TowerForces.SupportSheavesOf(LiftRope.Head.B10), Is.EqualTo(10));
            Assert.That(TowerForces.HoldDownSheavesOf(LiftRope.Head.D10), Is.EqualTo(10));
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftD6).Heads, Is.SameAs(TowerForces.HeadKit.Wide));
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Heads, Is.SameAs(TowerForces.HeadKit.Quad));
        }
    }
}
