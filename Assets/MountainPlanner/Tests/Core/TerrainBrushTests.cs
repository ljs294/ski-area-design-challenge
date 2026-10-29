using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 15 (TB-01 to TB-14): the terrain brush's maths, its limits round lifts, and its replay through the earthwork fold.
    public sealed class TerrainBrushTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        // Design points are mm from the site centre at (West + 1000, North - 1000).
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 1000, North - 1000);

        sealed class Plane : IHeightSource
        {
            public double At(double x, double y) => 1500;
        }

        sealed class Tilted : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * (y - (North - 1000)) + 0.02 * (x - West);
        }

        // Bumps 0.5 m high every 4 m: something for Smooth to take out.
        sealed class Bumpy : IHeightSource
        {
            public double At(double x, double y) => 1500 + (((int)Math.Floor(x - West) / 2 + (int)Math.Floor(North - y) / 2) % 2 == 0 ? 0.5 : 0);
        }

        static int Column(double xMetres) => (int)Math.Round(Site.OriginX + xMetres - West);
        static int Row(double yMetres) => (int)Math.Round(North - (Site.OriginY + yMetres));

        static BrushRun Run(BrushSettings s, IHeightSource ground, TerrainEdits edits = null, EarthworkSite? site = null, FixedGround fixedGround = null) =>
            new BrushRun(s, new EditedHeights(ground, Lattice, edits ?? TerrainEdits.Empty), site ?? Site, fixedGround);

        static BrushDab At(double x, double y, int weight = 1000) => new BrushDab(PointMm.FromMetres(x, y), weight);

        // The change a run leaves at a point, mm.
        static int Change(BrushRun run, TerrainEdits under, double x, double y) => run.Preview(under ?? TerrainEdits.Empty).DeltaMmAt(Column(x), Row(y));

        [Test]
        public void TB04_RaiseMovesTheCentreByTheStepAndNothingAtTheEdge()
        {
            var run = Run(BrushSettings.RaiseOrLower(false, 9144, 81), new Plane());
            run.Dab(At(0, 0));
            Assert.That(Change(run, null, 0, 0), Is.EqualTo(81), "a full-weight dab moves the hard core by the step");
            Assert.That(Change(run, null, 9.2, 0), Is.EqualTo(0), "nothing past the radius");
            Assert.That(Change(run, null, 3, 0), Is.EqualTo(81), "inside the hard core (the inner half)");
            Assert.That(Change(run, null, 7, 0), Is.InRange(1, 80), "the soft edge falls away");
            Assert.That(Change(run, null, -7, 0), Is.EqualTo(Change(run, null, 7, 0)), "symmetric");
            Assert.That(Change(run, null, 0, 7), Is.EqualTo(Change(run, null, 7, 0)));
            var half = Run(BrushSettings.RaiseOrLower(false, 9144, 81), new Plane());
            half.Dab(At(0, 0, 500));
            Assert.That(Change(half, null, 0, 0), Is.EqualTo(41), "a dab's weight shares the tick's step (TB-03)");
        }

        [Test]
        public void TB04_LowerMirrorsRaise()
        {
            var up = Run(BrushSettings.RaiseOrLower(false, 9144, 60), new Tilted());
            var down = Run(BrushSettings.RaiseOrLower(true, 9144, 60), new Tilted());
            for (int i = 0; i < 5; i++) { up.Dab(At(i, 0)); down.Dab(At(i, 0)); }
            for (double x = -10; x <= 15; x += 1.5)
                Assert.That(Change(down, null, x, 1), Is.EqualTo(-Change(up, null, x, 1)));
        }

        [Test]
        public void TB05_FlattenAtFullStrengthReachesTheTarget()
        {
            var run = Run(BrushSettings.Flatten(9144, 1000, 1_515_000), new Tilted());
            run.Dab(At(0, 0));
            var g = new EditedHeights(new Tilted(), Lattice, run.Preview(TerrainEdits.Empty));
            for (double x = -3; x <= 3; x += 1)
                Assert.That(g.AtPoint(Column(x), Row(1)), Is.EqualTo(1515.0).Within(0.001), "the hard core stands at the target");
            var gentle = Run(BrushSettings.Flatten(9144, TerrainBrush.StrengthFor(50), 1_497_000), new Plane());
            gentle.Dab(At(0, 0));
            Assert.That(Change(gentle, null, 0, 0), Is.EqualTo(-525), "50% moves 17.5% of the 3 m gap a tick");
        }

        [Test]
        public void TB06_SmoothTakesOutBumpsAndLeavesAPlaneAlone()
        {
            double Roughness(TerrainEdits e)
            {
                var g = new EditedHeights(new Bumpy(), Lattice, e);
                double sum = 0;
                for (int c = Column(-4); c <= Column(4); c++)
                    sum += Math.Abs(g.AtPoint(c - 1, Row(0)) - 2 * g.AtPoint(c, Row(0)) + g.AtPoint(c + 1, Row(0)));
                return sum;
            }
            var run = new BrushRun(BrushSettings.Smooth(12192, 1000), new EditedHeights(new Bumpy(), Lattice, TerrainEdits.Empty), Site, null);
            for (int i = 0; i < 6; i++) run.Dab(At(0, 0));
            Assert.That(Roughness(run.Preview(TerrainEdits.Empty)), Is.LessThan(Roughness(TerrainEdits.Empty) * 0.25), "bumps smoothed");
            var flat = Run(BrushSettings.Smooth(12192, 1000), new Plane());
            for (int i = 0; i < 6; i++) flat.Dab(At(0, 0));
            Assert.That(flat.Preview(TerrainEdits.Empty).IsEmpty, Is.True, "a plane's mean is itself: nothing moves");
        }

        static PointMm P(double x, double y) => PointMm.FromMetres(x, y);

        [Test]
        public void TB07_SlopeRampsEvenlyBetweenItsTwoPointsLevelAcrossThenHoldsAtTheTarget()
        {
            // BR16: start (0, 0) at 1,500 m, target (100, 0) at 1,490 m: 10% falls out of the two points.
            var run = Run(BrushSettings.Slope(9144, 1000, P(0, 0), 1_500_000, P(100, 0), 1_490_000), new Plane());
            for (double x = 0; x <= 160; x += 2.25) run.Dab(At(x, 0));
            var g = new EditedHeights(new Plane(), Lattice, run.Preview(TerrainEdits.Empty));
            foreach (double x in new[] { 10.0, 40, 70, 95 })
            {
                double h = g.AtPoint(Column(x), Row(0));
                Assert.That(h, Is.EqualTo(1500 - 0.1 * x).Within(0.03), $"on the ramp at {x} m");
                for (int d = -3; d <= 3; d++)
                    Assert.That(g.AtPoint(Column(x), Row(d)), Is.EqualTo(h).Within(0.0015), "level across (TB-07, GR-01)");
            }
            Assert.That(g.AtPoint(Column(130), Row(0)), Is.EqualTo(1490).Within(0.001), "holds at the target past it");
            // Smooth along: second differences stay at the millimetre.
            for (int c = Column(15); c < Column(90); c++)
                Assert.That(Math.Abs(g.AtPoint(c - 1, Row(0)) - 2 * g.AtPoint(c, Row(0)) + g.AtPoint(c + 1, Row(0))), Is.LessThanOrEqualTo(0.0021));
        }

        [Test]
        public void TB07_PaintingBackAndForthKeepsTheSameRamp()
        {
            // BR16: a point's height comes from where it lies between the two points, never from how far the mouse went,
            // so scrubbing over a ramp to widen it leaves the ramp where it was.
            var settings = BrushSettings.Slope(7620, 1000, P(0, 0), 1_500_000, P(80, 0), 1_488_000);
            var once = Run(settings, new Plane());
            for (double x = 0; x <= 80; x += 1.9) once.Dab(At(x, 0));
            var scrubbed = Run(settings, new Plane());
            for (int pass = 0; pass < 4; pass++)
                for (double x = 0; x <= 80; x += 1.9) scrubbed.Dab(At(pass % 2 == 0 ? x : 80 - x, 0));
            var g = new EditedHeights(new Plane(), Lattice, scrubbed.Preview(TerrainEdits.Empty));
            var g1 = new EditedHeights(new Plane(), Lattice, once.Preview(TerrainEdits.Empty));
            foreach (double x in new[] { 10.0, 30, 50, 70 })
            {
                Assert.That(g.AtPoint(Column(x), Row(0)), Is.EqualTo(1500 - 0.15 * x).Within(0.002), $"on the ramp at {x} m");
                Assert.That(g.AtPoint(Column(x), Row(0)), Is.EqualTo(g1.AtPoint(Column(x), Row(0))).Within(0.002), "the same as one pass");
            }
        }

        [Test]
        public void TB07_ASlopeOnTopOfAnEarlierOneJoinsIt()
        {
            // BR16: a second stroke starting where the first ramp ended carries it on to a new target.
            var first = Run(BrushSettings.Slope(7620, 1000, P(0, 0), 1_500_000, P(60, 0), 1_494_000), new Plane());
            for (double x = 0; x <= 60; x += 1.9) first.Dab(At(x, 0));
            var after = first.Preview(TerrainEdits.Empty);
            var second = Run(BrushSettings.Slope(7620, 1000, P(60, 0), 1_494_000, P(120, 0), 1_482_000), new Plane(), after);
            for (double x = 60; x <= 120; x += 1.9) second.Dab(At(x, 0));
            var g = new EditedHeights(new Plane(), Lattice, second.Preview(after));
            Assert.That(g.AtPoint(Column(30), Row(0)), Is.EqualTo(1497).Within(0.01), "the first ramp at 10%");
            Assert.That(g.AtPoint(Column(90), Row(0)), Is.EqualTo(1488).Within(0.01), "the second at 20%");
        }

        [Test]
        public void TB10_NothingMovesMoreThanFortyFeetFromNaturalGround()
        {
            var run = Run(BrushSettings.RaiseOrLower(false, 9144, DesignUnits.FiveFeetMm), new Plane());
            for (int i = 0; i < 20; i++) run.Dab(At(0, 0));
            Assert.That(Change(run, null, 0, 0), Is.EqualTo(TerrainBrush.LimitMm));
            Assert.That(run.Held().Select(h => h.Kind), Does.Contain(HoldKind.Depth));
            // A deeper cut already there is never pulled back.
            var deep = TerrainEdits.Empty.With(new[] { Tile(Column(0), Row(0), -15_000) });
            var lower = Run(BrushSettings.RaiseOrLower(true, 9144, 500), new Plane(), deep);
            lower.Dab(At(0, 0));
            Assert.That(lower.Preview(deep).DeltaMmAt(Column(0), Row(0)), Is.EqualTo(-15_000), "never worse than before");
        }

        [Test]
        public void TB10_OutsideTheCoreNothingMoves()
        {
            var core = new PointRect(Column(-100), Row(100), Column(0), Row(-100));
            var site = new EarthworkSite(Lattice, Site.OriginX, Site.OriginY, core);
            var run = Run(BrushSettings.RaiseOrLower(false, 9144, 81), new Plane(), site: site);
            run.Dab(At(0, 0));
            var e = run.Preview(TerrainEdits.Empty);
            Assert.That(e.DeltaMmAt(Column(-2), Row(0)), Is.EqualTo(81));
            Assert.That(e.DeltaMmAt(Column(2), Row(0)), Is.EqualTo(0), "past the core's edge");
            Assert.That(run.Held().Select(h => h.Kind), Does.Contain(HoldKind.Area));
        }

        [Test]
        public void TB10_ACellMovedAFootIsClearedOfTrees()
        {
            var foot = Run(BrushSettings.RaiseOrLower(false, 9144, 305), new Plane());
            foot.Dab(At(0, 0));
            var less = Run(BrushSettings.RaiseOrLower(false, 9144, 304), new Plane());
            less.Dab(At(0, 0));
            Assert.That(foot.Preview(TerrainEdits.Empty).IsClearedAt(Column(0), Row(0)), Is.True);
            Assert.That(less.Preview(TerrainEdits.Empty).IsClearedAt(Column(0), Row(0)), Is.False);
            Assert.That(foot.ClearedCount, Is.GreaterThan(0));
            Assert.That(less.ClearedCount, Is.EqualTo(0));
        }

        static TerrainEditTile Tile(int c, int r, int deltaMm)
        {
            var key = EditTileKey.Of(c, r);
            var b = TerrainEditTile.Empty(key).Edit();
            for (int dr = -12; dr <= 12; dr++)
                for (int dc = -12; dc <= 12; dc++)
                {
                    int cc = c + dc, rr = r + dr;
                    if (EditTileKey.Of(cc, rr) == key) b.SetDeltaMm(cc - key.FirstColumn, rr - key.FirstRow, deltaMm);
                }
            return b.Build();
        }

        // ---------- round lifts: TB-11 to TB-13 ----------

        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

        // A built quad up the tilted ground from (0, -300 m) to (0, 500 m), through the history and the fold.
        static (DesignHistory History, DesignEarthwork Fold, Lift Lift) Lifted()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var fold = new DesignEarthwork(new Tilted(), Site);
            fold.Prime(h.Document, TerrainEdits.Empty);
            var bottom = new PointMm(0, -300_000);
            var top = new PointMm(0, 500_000);
            var g = new EditedHeights(new Tilted(), Lattice, TerrainEdits.Empty);
            var b = LiftBuild.Build(Quad, g, Site.OriginX, Site.OriginY - 300, Site.OriginX, Site.OriginY + 500, TowerStance.Plumb);
            var cmd = new AddLift(h.Revision, "Chair 1", Quad.CatalogId, bottom, top, TowerMode.Automatic, TowerStance.Plumb, b.Towers, WorkStage.Built, b.Levels);
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.Reason);
            var r = fold.Rebuild(after.Document, h.Terrain);
            Assert.That(h.Submit(cmd, r.Edits).Accepted, Is.True);
            fold.Accept(r);
            return (h, fold, h.Document.Lifts[0]);
        }

        // Confirms one stroke through the history and the fold, as the tool does (TB-08).
        static DesignEarthwork.Result Confirm(DesignHistory h, DesignEarthwork fold, BrushSettings s, IEnumerable<BrushDab> dabs)
        {
            var cmd = new AddTerrainStrokes(h.Revision, new[] { new StrokePainting(s, dabs.ToList()) });
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.Reason);
            var r = fold.Rebuild(after.Document, h.Terrain);
            Assert.That(h.Submit(cmd, r.Edits).Accepted, Is.True);
            Assert.That(fold.Accept(r), Is.True);
            return r;
        }

        static IEnumerable<BrushDab> Hold(double x, double y, int ticks) => Enumerable.Range(0, ticks).Select(_ => At(x, y));

        [Test]
        public void TB11_ATerminalsPadIsFixedAndTheGroundSlopesIntoItAtMostOneToOne()
        {
            var (h, fold, lift) = Lifted();
            var before = new EditedHeights(new Tilted(), Lattice, h.Terrain);
            // Lower hard right beside the bottom terminal (its bullwheel at (0, -300 m)).
            var r = Confirm(h, fold, BrushSettings.RaiseOrLower(true, 18288, DesignUnits.FiveFeetMm), Hold(0, -290, 12));
            var after = new EditedHeights(new Tilted(), Lattice, h.Terrain);
            double level = lift.Levels.Value.BottomMetres;
            var end = Quad.Bottom;
            int checkedPad = 0, checkedFace = 0;
            for (double dy = -20; dy <= 30; dy += 1)
                for (double dx = -20; dx <= 20; dx += 1)
                {
                    int c = Column(dx), row = Row(-300 + dy);
                    double along = dy, across = Math.Abs(dx);
                    double b = end.Behind + TerminalPad.MarginMetres, a = end.Ahead + TerminalPad.MarginMetres, w = end.HalfWidth + TerminalPad.MarginMetres;
                    double da = along < -b ? -b - along : along > a ? along - a : 0, dc = across > w ? across - w : 0;
                    double d = Math.Sqrt(da * da + dc * dc);
                    double was = before.AtPoint(c, row), now = after.AtPoint(c, row);
                    if (d <= 0) { Assert.That(now, Is.EqualTo(was), "the pad never moves"); checkedPad++; }
                    else if (Math.Abs(now - was) > 0.001)
                    {
                        Assert.That(Math.Abs(now - level), Is.LessThanOrEqualTo(Math.Max(d, Math.Abs(was - level)) + 0.002), "1:1 into the pad, or no worse");
                        checkedFace++;
                    }
                }
            Assert.That(checkedPad, Is.GreaterThan(50));
            Assert.That(checkedFace, Is.GreaterThan(50), "the brush did lower ground round the pad");
            var held = ((StrokeBuild)r.DetailOf(h.Document.Strokes[0].Id.Value)).Held;
            Assert.That(held.Select(x => x.Kind), Does.Contain(HoldKind.Terminal));
        }

        [Test]
        public void TB12_RaisingUnderTheLineNeverPushesTheChairsBelowTheClearance()
        {
            var (h, fold, lift) = Lifted();
            var rope = ((LiftBuild.Works)fold.DetailOf(lift.Id.Value)).Rope;
            Assert.That(LiftClearance.Gaps(rope), Is.Empty, "clear before");
            // Mid-span between two towers, raise as hard as it goes.
            double s = (rope.Towers[1].BaseS + rope.Towers[2].BaseS) / 2;
            var r = Confirm(h, fold, BrushSettings.RaiseOrLower(false, 30480, DesignUnits.FiveFeetMm), Hold(0, -300 + s, 30));
            var after = new EditedHeights(new Tilted(), Lattice, h.Terrain);
            var line = LineGround.Sample(after, Quad, Site.OriginX, Site.OriginY - 300, Site.OriginX, Site.OriginY + 500);
            for (double at = s - 25; at <= s + 25; at += 1)
            {
                Assert.That(rope.ClearanceAt(at, out _, out double need), Is.True);
                double up = Math.Min(LiftRope.RopeAt(rope.Loaded, at), LiftRope.RopeAt(rope.Empty, at)), down = LiftRope.RopeAt(rope.Empty, at);
                int j = (int)Math.Round(at);
                double clear = Math.Min(up - Quad.ChairDropMetres - line.UnderUp[j], down - Quad.ChairDropMetres - line.UnderDown[j]);
                Assert.That(clear, Is.GreaterThanOrEqualTo(need - LiftRope.ClearanceTolerance), $"TW-01 kept at {at:F0} m");
            }
            Assert.That(after.AtPoint(Column(20), Row(-300 + s)), Is.GreaterThan(new EditedHeights(new Tilted(), Lattice, TerrainEdits.Empty).AtPoint(Column(20), Row(-300 + s)) + 5),
                "ground away from the chairs still rises");
            var held = ((StrokeBuild)r.DetailOf(h.Document.Strokes[0].Id.Value)).Held;
            Assert.That(held.Select(x => x.Kind), Does.Contain(HoldKind.LiftLine));
        }

        [Test]
        public void TB13_RaisingAtATowerStopsWhereItCanStillReachItsHead()
        {
            var (h, fold, lift) = Lifted();
            var rope = ((LiftBuild.Works)fold.DetailOf(lift.Id.Value)).Rope;
            var t = rope.Towers[2];
            var r = Confirm(h, fold, BrushSettings.RaiseOrLower(false, 12192, DesignUnits.FiveFeetMm), Hold(0, -300 + t.BaseS, 30));
            var after = new EditedHeights(new Tilted(), Lattice, h.Terrain);
            double ground = after.At(Site.OriginX, Site.OriginY - 300 + t.BaseS);
            double shortest = DesignUnits.TowerMinMm / 1000.0 * Math.Cos(t.Lean);
            Assert.That(t.RopeY - ground, Is.GreaterThanOrEqualTo(shortest - 0.002), "the tower stays at least 25 ft under its head (TB-13)");
            Assert.That(t.RopeY - ground, Is.LessThanOrEqualTo(shortest + 0.05), "and the brush raised the ground up to that");
            var held = ((StrokeBuild)r.DetailOf(h.Document.Strokes[0].Id.Value)).Held;
            Assert.That(held, Does.Contain(new Hold(HoldKind.Tower, lift.Id, 3)));
        }

        [Test]
        public void TW02v4_HeadsStayPutAndTheSteelFollowsTheGround()
        {
            var (h, fold, lift) = Lifted();
            var built = ((LiftBuild.Works)fold.DetailOf(lift.Id.Value)).Rope;
            var t = built.Towers[2];
            Confirm(h, fold, BrushSettings.RaiseOrLower(true, 9144, 300), Hold(0, -300 + t.BaseS, 12));
            var after = new EditedHeights(new Tilted(), Lattice, h.Terrain);
            double bx = Site.OriginX, by = Site.OriginY - 300, tx = Site.OriginX, ty = Site.OriginY + 500;
            var levels = lift.Levels.Value;
            var bases = built.Towers.Select(x => x.BaseY).ToList();
            var fixedRope = LiftRope.SolveWithBases(Quad, after, bx, by, tx, ty, lift.Towers, lift.Stance, Quad.Bottom.ExitAlong,
                levels.BottomMetres + Quad.Bottom.ExitRise, Quad.Top.ExitAlong, levels.TopMetres + Quad.Top.ExitRise, bases);
            for (int i = 0; i < built.Towers.Count; i++)
            {
                Assert.That(fixedRope.Towers[i].RopeY, Is.EqualTo(built.Towers[i].RopeY).Within(1e-9), "the head stays where it was built");
                Assert.That(fixedRope.Towers[i].RopeS, Is.EqualTo(built.Towers[i].RopeS).Within(1e-9));
            }
            double ground = after.At(bx, by + t.BaseS);
            Assert.That(ground, Is.LessThan(t.BaseY - 1), "the brush lowered the ground at the tower");
            LiftRope.AsBuilt(t, ground, out double steel, out double reveal);
            Assert.That(steel * Math.Cos(t.Lean) + reveal + ground, Is.EqualTo(t.RopeY).Within(1e-6), "steel and foundation reach the head");
            Assert.That(steel, Is.GreaterThan(lift.Towers[2].HeightMm / 1000.0), "a taller tower");
            Assert.That(reveal, Is.InRange(0, LiftRope.MaxReveal));
            Assert.That(Math.Round(steel / 1.524, 6) % 1, Is.EqualTo(0), "steel in 5 ft steps");
            LiftRope.AsBuilt(t, t.BaseY, out double same, out _);
            Assert.That(same, Is.EqualTo(lift.Towers[2].HeightMm / 1000.0).Within(1e-9), "unchanged ground: the stored height");
        }

        // ---------- replay: TB-14 ----------

        static IReadOnlyList<StrokePainting> Scripted()
        {
            var line = Enumerable.Range(0, 40).Select(i => At(-50 + i * 2.5, 10)).ToList();
            return new[]
            {
                new StrokePainting(BrushSettings.RaiseOrLower(false, 9144, 81), Enumerable.Range(0, 30).Select(i => At(i * 0.8, -20)).ToList()),
                new StrokePainting(BrushSettings.RaiseOrLower(true, 6096, 40), Enumerable.Range(0, 20).Select(i => At(10, -25 + i)).ToList()),
                new StrokePainting(BrushSettings.Flatten(12192, 350, 1_502_000), Enumerable.Range(0, 25).Select(i => At(-30 + i, 0, 700)).ToList()),
                new StrokePainting(BrushSettings.Smooth(15240, 350), Enumerable.Range(0, 15).Select(i => At(5, -15 + i * 0.5)).ToList()),
                new StrokePainting(BrushSettings.Slope(7620, 350, PointMm.FromMetres(-50, 10), 1_503_000, PointMm.FromMetres(47.5, 10), 1_496_000), line),
            };
        }

        static string Sha(TerrainEdits e)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(TerrainEditBytes.Encode(e))).Replace("-", "").ToLowerInvariant();
        }

        [Test]
        public void TB14_TheSameStrokesGiveTheSameHeightsWhicheverWayTheyArrive()
        {
            // One by one through the history, all at once in one command, and a fresh fold of the final design.
            var h = new DesignHistory(DesignDocument.Empty(11));
            var fold = new DesignEarthwork(new Tilted(), Site);
            fold.Prime(h.Document, TerrainEdits.Empty);
            foreach (var s in Scripted()) Confirm(h, fold, s.Settings, s.Dabs);

            var h2 = new DesignHistory(DesignDocument.Empty(11));
            var fold2 = new DesignEarthwork(new Tilted(), Site);
            fold2.Prime(h2.Document, TerrainEdits.Empty);
            var cmd = new AddTerrainStrokes(h2.Revision, Scripted());
            var r2 = fold2.Rebuild(cmd.Apply(h2.Document).Document, h2.Terrain);
            Assert.That(h2.Submit(cmd, r2.Edits).Accepted, Is.True);

            var fresh = new DesignEarthwork(new Tilted(), Site);
            fresh.Prime(DesignDocument.Empty(11), TerrainEdits.Empty);
            var r3 = fresh.Rebuild(h.Document, TerrainEdits.Empty);

            Assert.That(TerrainEdits.Changed(h.Terrain, h2.Terrain), Is.Empty, "one by one = all at once");
            Assert.That(TerrainEdits.Changed(h.Terrain, r3.Edits), Is.Empty, "a fresh replay of the saved design");
            Assert.That(h.Document.Strokes.Count, Is.EqualTo(5));
            TestContext.WriteLine($"Brush golden: {Sha(h.Terrain)}");
            Assert.That(Sha(h.Terrain), Is.EqualTo(BrushGolden), "the scripted strokes' heights (a behaviour change re-pins with the owner's OK)");
        }

        // Re-pinned for BR16 (owner, 2026-10-26: the slope runs between two points), from 1cc1a293….
        const string BrushGolden = "a60545cac90628b263f8ca515edeb21bb1decda5d305cc47dba50fc718d88fc0";

        [Test]
        public void TB14_TheLivePreviewIsWhatTheReplayConfirms()
        {
            var h = new DesignHistory(DesignDocument.Empty(11));
            var fold = new DesignEarthwork(new Tilted(), Site);
            fold.Prime(h.Document, TerrainEdits.Empty);
            var pending = h.Terrain;
            var paintings = Scripted();
            foreach (var p in paintings)
            {
                var run = new BrushRun(p.Settings, new EditedHeights(new Tilted(), Lattice, pending), Site, FixedGround.None(Site));
                run.Dabs(p.Dabs);
                pending = run.Preview(pending);
            }
            var cmd = new AddTerrainStrokes(h.Revision, paintings);
            var r = fold.Rebuild(cmd.Apply(h.Document).Document, h.Terrain);
            Assert.That(TerrainEdits.Changed(pending, r.Edits), Is.Empty, "the ground the player saw is the ground confirmed");
        }

        [Test]
        public void PR_C_ASeededFoldIsTheFoldFromScratch()
        {
            // Round 4 PR C: a session folded again after each press, seeded with the fold before, gives exactly the fold of the
            // whole session from scratch: the edits byte for byte, each stroke's writes and detail, and what Accept then knows.
            // Beside a lift (strokes read its rope) and over a committed stroke; then after a Ctrl+Z, a changed stroke, and a
            // seed from before an Accept (ignored).
            var (h, fold, _) = Lifted();
            Confirm(h, fold, BrushSettings.RaiseOrLower(false, 9144, 81), Enumerable.Range(0, 10).Select(i => At(i * 2.0, 30)));
            var strokes = Scripted().Concat(new[] { new StrokePainting(BrushSettings.RaiseOrLower(true, 9144, 120).WithClearing(false), Enumerable.Range(0, 12).Select(i => At(-5 + i, -280)).ToList()) }).ToList();

            DesignEarthwork.Result Fold(IList<StrokePainting> session, DesignEarthwork.Result seed) =>
                fold.Rebuild(new AddTerrainStrokes(h.Revision, session.ToList()).Apply(h.Document).Document, h.Terrain, seed);

            void Same(DesignEarthwork.Result seeded, IList<StrokePainting> session, string what)
            {
                var doc = new AddTerrainStrokes(h.Revision, session.ToList()).Apply(h.Document).Document;
                var fresh = fold.Rebuild(doc, h.Terrain);
                Assert.That(TerrainEditBytes.Encode(seeded.Edits), Is.EqualTo(TerrainEditBytes.Encode(fresh.Edits)), what + ": the edits");
                foreach (var k in doc.Strokes)
                {
                    Assert.That(seeded.WritesOf(k.Id.Value)?.CanonicalBytes(), Is.EqualTo(fresh.WritesOf(k.Id.Value)?.CanonicalBytes()), what + ": a stroke's writes");
                    var a = seeded.DetailOf(k.Id.Value) as StrokeBuild;
                    var b = fresh.DetailOf(k.Id.Value) as StrokeBuild;
                    Assert.That((a?.CutCubicMetres, a?.FillCubicMetres, a?.ClearedCells), Is.EqualTo((b?.CutCubicMetres, b?.FillCubicMetres, b?.ClearedCells)), what + ": a stroke's detail");
                }
            }

            DesignEarthwork.Result last = null;
            for (int n = 1; n <= strokes.Count; n++)
            {
                var session = strokes.Take(n).ToList();
                var seeded = Fold(session, last);
                Same(seeded, session, $"after press {n}");
                if (last != null) Assert.That(seeded.Regraded, Is.LessThan(Fold(session, null).Regraded), $"press {n}: only the new stroke graded");
                last = seeded;
            }
            // Ctrl+Z: the session one shorter, seeded with the longer fold.
            var undone = strokes.Take(strokes.Count - 1).ToList();
            Same(Fold(undone, last), undone, "after Ctrl+Z");
            // A different last stroke (Ctrl+Z, then another press).
            var other = undone.Concat(new[] { new StrokePainting(BrushSettings.Smooth(12192, 500), Enumerable.Range(0, 8).Select(i => At(i, 5)).ToList()) }).ToList();
            var otherFold = Fold(other, last);
            Same(otherFold, other, "after a different press");
            // Confirmed: the seeded result is what Accept learns, so the next rebuild of the same design grades nothing.
            var cmd = new AddTerrainStrokes(h.Revision, other);
            Assert.That(h.Submit(cmd, otherFold.Edits).Accepted, Is.True);
            Assert.That(fold.Accept(otherFold), Is.True);
            Assert.That(fold.Rebuild(h.Document, h.Terrain).Regraded, Is.Zero, "everything the seeded fold laid is known after Accept");
            // A seed from before that Accept is ignored: the fold is the one from scratch.
            var next = new[] { new StrokePainting(BrushSettings.RaiseOrLower(false, 6096, 60), Enumerable.Range(0, 6).Select(i => At(20 + i, 20)).ToList()) };
            var stale = fold.Rebuild(new AddTerrainStrokes(h.Revision, next).Apply(h.Document).Document, h.Terrain, last);
            Same(stale, next, "a stale seed");
        }

        [Test]
        public void TB08_UndoAndRedoGiveByteIdenticalHeights()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var fold = new DesignEarthwork(new Tilted(), Site);
            fold.Prime(h.Document, TerrainEdits.Empty);
            var seen = new List<byte[]> { TerrainEditBytes.Encode(h.Terrain) };
            var docs = new List<byte[]> { DesignBytes.Encode(h.Document) };
            foreach (var s in Scripted().Concat(Scripted()))
            {
                Confirm(h, fold, s.Settings, s.Dabs);
                seen.Add(TerrainEditBytes.Encode(h.Terrain));
                docs.Add(DesignBytes.Encode(h.Document));
            }
            for (int i = seen.Count - 2; i >= 0; i--)
            {
                Assert.That(h.Undo(), Is.True);
                Assert.That(TerrainEditBytes.Encode(h.Terrain), Is.EqualTo(seen[i]));
                Assert.That(DesignBytes.Encode(h.Document), Is.EqualTo(docs[i]));
            }
            for (int i = 1; i < seen.Count; i++)
            {
                Assert.That(h.Redo(), Is.True);
                Assert.That(TerrainEditBytes.Encode(h.Terrain), Is.EqualTo(seen[i]));
            }
        }

        [Test]
        public void TB14_StrokesRoundTripThroughTheDesignsBytes()
        {
            var r = new AddTerrainStrokes(1, Scripted()).Apply(DesignDocument.Empty(9));
            Assert.That(r.Accepted, Is.True, r.Reason);
            Assert.That(r.Created.Count, Is.EqualTo(5));
            var bytes = DesignBytes.Encode(r.Document);
            var back = DesignBytes.Decode(bytes);
            Assert.That(DesignBytes.Encode(back), Is.EqualTo(bytes));
            Assert.That(back.Strokes.Count, Is.EqualTo(5));
            for (int i = 0; i < 5; i++) Assert.That(back.Strokes[i].SamePainting(r.Document.Strokes[i]), Is.True);
        }

        [Test, Explicit("Timing: 500 strokes primed, then one more confirmed, then a lift edited under them")]
        public void TB14_FiveHundredStrokesFoldInTime()
        {
            var (h, fold, lift) = Lifted();
            var paintings = new List<StrokePainting>();
            for (int i = 0; i < 500; i++)
            {
                double x = -60 + (i % 25) * 5, y = -250 + (i / 25) * 35;
                var dabs = Enumerable.Range(0, 24).Select(k => At(x + k * 0.6, y)).ToList();
                paintings.Add(new StrokePainting(i % 2 == 0 ? BrushSettings.RaiseOrLower(false, 9144, 40) : BrushSettings.Smooth(9144, 350), dabs));
            }
            var doc = new AddTerrainStrokes(h.Revision, paintings).Apply(h.Document).Document;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var fresh = new DesignEarthwork(new Tilted(), Site);
            fresh.Prime(doc, TerrainEdits.Empty);
            var all = fresh.Rebuild(doc, TerrainEdits.Empty);
            long prime = watch.ElapsedMilliseconds;
            fresh.Accept(all);
            watch.Restart();
            var one = new AddTerrainStrokes(1, new[] { paintings[0] }).Apply(doc).Document;
            var r = fresh.Rebuild(one, all.Edits);
            long add = watch.ElapsedMilliseconds;
            TestContext.WriteLine($"500 strokes: prime + rebuild {prime} ms; one more stroke {add} ms ({r.Regraded} regraded)");
            Assert.That(r.Regraded, Is.LessThan(40), "one stroke regrades only the strokes after it that it meets");
        }

        // Brush speed (BR17, demo 75): the live ground is read point by point from the stroke in hand, and the bill is kept as
        // it goes; both must be exactly what the tiles built at the end say.
        static IEnumerable<(string Name, BrushSettings Settings, IHeightSource Ground)> Strokes()
        {
            const int r = 18288;   // 120 ft across
            yield return ("raise", BrushSettings.RaiseOrLower(false, r, 81), new Tilted());
            yield return ("lower", BrushSettings.RaiseOrLower(true, r, 81), new Tilted());
            yield return ("flatten", BrushSettings.Flatten(r, 350, 1_480_000), new Tilted());
            yield return ("smooth", BrushSettings.Smooth(r, 350), new Bumpy());
            yield return ("slope", BrushSettings.Slope(r, 350, PointMm.FromMetres(10, 0), 1_500_000, PointMm.FromMetres(45, 5), 1_470_000), new Tilted());
        }

        // The ground before the stroke: an earlier one across the same tiles, so some cells are cleared already.
        static TerrainEdits Earlier(IHeightSource ground)
        {
            var first = Run(BrushSettings.RaiseOrLower(false, 18288, 81), ground);
            for (int i = 0; i < 12; i++) first.Dab(At(20 + i, -6));
            return first.Preview(TerrainEdits.Empty);
        }

        // The stroke crosses the 64 m tile edge at x = 24 m (lattice column 1024) and back.
        static IEnumerable<BrushDab> Across()
        {
            for (int i = 0; i <= 30; i++) yield return At(8 + i * 1.2, 2 + (i % 5) * 0.7);
            for (int i = 0; i <= 10; i++) yield return At(44 - i * 2.5, 4, 600);
        }

        [Test]
        public void BR17_TheLiveGroundIsThePreviewPointByPoint()
        {
            foreach (var (name, settings, ground) in Strokes())
            {
                var under = Earlier(ground);
                var run = Run(settings, ground, under);
                int c0 = Column(-20), c1 = Column(80), r0 = Row(40), r1 = Row(-40);
                int dabs = 0;
                foreach (var d in Across())
                {
                    run.Dab(d);
                    if (++dabs % 4 != 1) continue;
                    var preview = run.Preview(under);
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                            if (run.Live.DeltaMmAt(c, r) != preview.DeltaMmAt(c, r))
                                Assert.Fail($"{name}: point ({c}, {r}) after dab {dabs}: live {run.Live.DeltaMmAt(c, r)}, tiles {preview.DeltaMmAt(c, r)}");
                }
            }
        }

        [Test]
        public void BR22_TheLiveClearingIsThePreviewsCellByCell()
        {
            // The trees come out every frame (round 4): the stroke in hand's cleared cells, read cell by cell, are exactly its
            // preview's, over a ground with cells cleared already, with Clear trees on and off.
            foreach (bool clear in new[] { true, false })
                foreach (var (name, settings, ground) in Strokes())
                {
                    var under = Earlier(ground);
                    var run = Run(settings.WithClearing(clear), ground, under);
                    var live = (ICellClearing)run.Live;
                    int c0 = Column(-20), c1 = Column(80), r0 = Row(40), r1 = Row(-40), dabs = 0;
                    foreach (var d in Across())
                    {
                        run.Dab(d);
                        if (++dabs % 4 != 1) continue;
                        var preview = run.Preview(under);
                        for (int r = r0; r <= r1; r++)
                            for (int c = c0; c <= c1; c++)
                                if (live.IsClearedAt(c, r) != preview.IsClearedAt(c, r))
                                    Assert.Fail($"{name}, clear {clear}: cell ({c}, {r}) after dab {dabs}: live {live.IsClearedAt(c, r)}, tiles {preview.IsClearedAt(c, r)}");
                    }
                    if (!clear) Assert.That(run.ClearedCount, Is.Zero, $"{name}: Clear trees off clears nothing (TB-10 v2)");
                }
        }

        [Test]
        public void BR22_ClearTreesOffReplaysWithNoClearing()
        {
            // TB-10 v2: a stroke painted with Clear trees off writes heights and no cleared cells, and replays so.
            int cleared = 0;
            foreach (var (name, settings, ground) in Strokes())
            {
                var off = Run(settings.WithClearing(false), ground);
                var on = Run(settings, ground);
                foreach (var d in Across()) { off.Dab(d); on.Dab(d); }
                var w = off.Writes();
                Assert.That(off.ClearedCount, Is.Zero, name);
                for (int r = Row(40); r <= Row(-40); r++)
                    for (int c = Column(-20); c <= Column(80); c++)
                        if (w.Clears(c, r)) Assert.Fail($"{name}: cell ({c}, {r}) cleared with Clear trees off");
                Assert.That(off.Preview(TerrainEdits.Empty).Tiles.Any(t => t.HasClearing), Is.False, name);
                if (on.ClearedCount > 0) { cleared++; Assert.That(TerrainEdits.Changed(off.Preview(TerrainEdits.Empty), on.Preview(TerrainEdits.Empty)).Count, Is.GreaterThan(0), name + ": on clears"); }
                foreach (var k in on.Preview(TerrainEdits.Empty).Tiles.Select(t => t.Key))
                    for (int j = 0; j < 64; j++)
                        for (int i = 0; i < 64; i++)
                            Assert.That(off.Preview(TerrainEdits.Empty).Get(k).DeltaMm(i, j), Is.EqualTo(on.Preview(TerrainEdits.Empty).Get(k).DeltaMm(i, j)), $"{name}: the same heights either way");
            }
            Assert.That(cleared, Is.GreaterThan(0), "some of the strokes clear with Clear trees on");
        }

        [Test]
        public void R4_DeltaHeightsAreTheEditedHeights()
        {
            // The towers under a stroke in hand read DeltaHeights; at Confirm they read EditedHeights: the same doubles.
            var ground = new Tilted();
            var run = Run(BrushSettings.RaiseOrLower(false, 18288, 200), ground);
            foreach (var d in Across()) run.Dab(d);
            var edited = new EditedHeights(ground, Lattice, run.Preview(TerrainEdits.Empty));
            var live = new DeltaHeights(ground, Lattice, run.Live);
            for (double y = -40.3; y <= 40; y += 3.7)
                for (double x = -20.1; x <= 80; x += 2.9)
                {
                    double ax = West + 1000 + x, ay = North - 1000 + y;
                    Assert.That(live.At(ax, ay), Is.EqualTo(edited.At(ax, ay)), $"({x}, {y})");
                }
        }

        [Test]
        public void BR21_APreviewRebuiltOnlyWhereItChangedIsTheFullPreview()
        {
            // A brush's catch-ups preview a few times a second, rebuilding only the tiles changed since the last one; each must
            // equal a fresh run's preview of the same dabs, tile for tile (heights and cleared cells).
            foreach (var (name, settings, ground) in Strokes())
            {
                var under = Earlier(ground);
                var run = Run(settings, ground, under);
                var laid = new List<BrushDab>();
                foreach (var d in Across())
                {
                    run.Dab(d);
                    laid.Add(d);
                    if (laid.Count % 5 != 2) continue;
                    var fresh = Run(settings, ground, under);
                    foreach (var f in laid) fresh.Dab(f);
                    var kept = run.Preview(under);
                    Assert.That(TerrainEdits.Changed(fresh.Preview(under), kept), Is.Empty, $"{name}: after dab {laid.Count}");
                }
                Assert.That(TerrainEdits.Changed(run.Preview(Earlier(ground)), run.Preview(under)), Is.Empty, $"{name}: over a new set, built whole");
            }
        }

        [Test]
        public void R4_AReusedBuilderGivesTheSameTileAsAFreshOne()
        {
            // Round 4: the preview's builders are reused and the built tile takes their arrays; the tile is the same as
            // Edit().Build() gives, and the builder starts clean afterwards.
            var key = new EditTileKey(3, 5);
            var from = new TerrainEditTile.Builder(key).SetDeltaMm(1, 2, 300).SetCleared(4, 4, true).Build();
            var want = from.Edit().SetDeltaMm(10, 10, -500).SetCleared(20, 20, true).Build();
            var b = new TerrainEditTile.Builder(key);
            var got = b.Reset(key, from).SetDeltaMm(10, 10, -500).SetCleared(20, 20, true).BuildTaking();
            Assert.That(got, Is.EqualTo(want));
            Assert.That(b.Reset(key, null).BuildTaking().IsEmpty, "the next reset starts empty");
            Assert.That(got, Is.EqualTo(want), "the built tile kept its content");
            var heights = b.Reset(key, null).SetDeltaMm(0, 0, 1).BuildTaking();
            Assert.That(heights.HasHeights && !heights.HasClearing, "heights only");
            var cleared = b.Reset(key, null).SetCleared(1, 1, true).BuildTaking();
            Assert.That(!cleared.HasHeights && cleared.HasClearing && !cleared.IsCleared(0, 0) && cleared.IsCleared(1, 1), "clearing only, from a clean word");
            Assert.That(b.Reset(key, from).BuildTaking(), Is.EqualTo(from), "from a tile again, after both arrays were taken");
        }

        [Test]
        public void BR14_TheRunningBillIsTheFullCount()
        {
            foreach (var (name, settings, ground) in Strokes())
            {
                var under = Earlier(ground);
                var run = Run(settings, ground, under);
                int c0 = Column(-20), c1 = Column(80), r0 = Row(40), r1 = Row(-40);
                int dabs = 0;
                foreach (var d in Across())
                {
                    run.Dab(d);
                    if (++dabs % 3 != 0) continue;
                    var preview = run.Preview(under);
                    long cut = 0, fill = 0;
                    int cleared = 0;
                    bool Moved(int c, int r) => Math.Abs((long)preview.DeltaMmAt(c, r) - under.DeltaMmAt(c, r)) >= TerrainBrush.ClearMm;
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                        {
                            long change = (long)preview.DeltaMmAt(c, r) - under.DeltaMmAt(c, r);
                            if (change < 0) cut -= change; else fill += change;
                            // TB-10, from the heights alone: a cell not cleared before with a corner moved a foot or more.
                            bool clears = !under.IsClearedAt(c, r) && (Moved(c, r) || Moved(c + 1, r) || Moved(c, r + 1) || Moved(c + 1, r + 1));
                            if (clears) cleared++;
                            Assert.That(preview.IsClearedAt(c, r), Is.EqualTo(clears || under.IsClearedAt(c, r)), $"{name}: cell ({c}, {r}) cleared after dab {dabs}");
                        }
                    run.Volumes(out double runCut, out double runFill);
                    Assert.That(runCut, Is.EqualTo(cut / 1000.0), $"{name}: cut after dab {dabs}");
                    Assert.That(runFill, Is.EqualTo(fill / 1000.0), $"{name}: fill after dab {dabs}");
                    Assert.That(run.ClearedCount, Is.EqualTo(cleared), $"{name}: cells cleared after dab {dabs}");
                }
                run.Volumes(out double finalCut, out double finalFill);
                Assert.That(finalCut + finalFill, Is.GreaterThan(0), $"{name} moved the ground");
            }
        }

        [Test]
        public void TB02_BadStrokesAreTurnedAway()
        {
            var doc = DesignDocument.Empty(9);
            var dab = new[] { At(0, 0) };
            Assert.That(new AddTerrainStrokes(1, new[] { new StrokePainting(BrushSettings.RaiseOrLower(false, 1000, 81), dab) }).Apply(doc).Accepted, Is.False, "a radius off the 5 ft steps");
            Assert.That(new AddTerrainStrokes(1, new[] { new StrokePainting(BrushSettings.Flatten(9144, 0, 1), dab) }).Apply(doc).Accepted, Is.False, "no strength");
            Assert.That(new AddTerrainStrokes(1, new[] { new StrokePainting(BrushSettings.Smooth(9144, 300), Array.Empty<BrushDab>()) }).Apply(doc).Accepted, Is.False, "no dabs");
            Assert.That(new AddTerrainStrokes(1, Array.Empty<StrokePainting>()).Apply(doc).Accepted, Is.False);
            var many = Enumerable.Range(0, DesignRules.MaxDabsPerStroke + 1).Select(i => At(0, 0)).ToList();
            Assert.That(new AddTerrainStrokes(1, new[] { new StrokePainting(BrushSettings.Smooth(9144, 300), many) }).Apply(doc).Accepted, Is.False);
        }
    }
}
