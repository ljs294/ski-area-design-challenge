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

        [Test]
        public void TB07_SlopeRampsAtTheGradeLevelAcrossThenHoldsAtTheTarget()
        {
            // Start 1,500 m, target 1,490 m, 10%: 100 m of ramp then level at the target.
            var run = Run(BrushSettings.Slope(9144, 1000, 1_500_000, 1_490_000, 100), new Plane());
            for (double x = 0; x <= 160; x += 2.25) run.Dab(At(x, 0));
            var g = new EditedHeights(new Plane(), Lattice, run.Preview(TerrainEdits.Empty));
            foreach (double x in new[] { 10.0, 40, 70, 95 })
            {
                double h = g.AtPoint(Column(x), Row(0));
                Assert.That(h, Is.EqualTo(1500 - 0.1 * x).Within(0.03), $"on the ramp at {x} m");
                for (int d = -3; d <= 3; d++)
                    Assert.That(g.AtPoint(Column(x), Row(d)), Is.EqualTo(h).Within(0.0015), "level across (TB-07, GR-01)");
            }
            Assert.That(g.AtPoint(Column(130), Row(0)), Is.EqualTo(1490).Within(0.001), "holds at the target past the ramp");
            // Smooth along: second differences stay at the millimetre.
            for (int c = Column(15); c < Column(90); c++)
                Assert.That(Math.Abs(g.AtPoint(c - 1, Row(0)) - 2 * g.AtPoint(c, Row(0)) + g.AtPoint(c + 1, Row(0))), Is.LessThanOrEqualTo(0.0021));
        }

        [Test]
        public void TB07_ACurvedSlopeStaysLevelAcrossItsPath()
        {
            var run = Run(BrushSettings.Slope(7620, 1000, 1_500_000, 1_480_000, 150), new Plane());
            var path = new List<(double x, double y)>();
            for (int i = 0; i <= 60; i++)
            {
                double a = i * Math.PI / 120;   // a quarter circle of 60 m radius
                path.Add((60 * Math.Sin(a), 60 - 60 * Math.Cos(a)));
            }
            foreach (var p in path) run.Dab(At(p.x, p.y));
            var g = new EditedHeights(new Plane(), Lattice, run.Preview(TerrainEdits.Empty));
            for (int i = 10; i <= 50; i += 10)
            {
                var (x, y) = path[i];
                double nx = Math.Sin(i * Math.PI / 120), ny = -Math.Cos(i * Math.PI / 120);   // the path's normal, toward the centre
                double h0 = g.At(Site.OriginX + x - ny * 0, Site.OriginY + y);
                double hIn = g.At(Site.OriginX + x + -nx * 2, Site.OriginY + y + -ny * 2), hOut = g.At(Site.OriginX + x + nx * 2, Site.OriginY + y + ny * 2);
                Assert.That(hIn, Is.EqualTo(h0).Within(0.03), "level across inside the bend");
                Assert.That(hOut, Is.EqualTo(h0).Within(0.03), "level across outside the bend");
            }
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
                new StrokePainting(BrushSettings.Slope(7620, 350, 1_503_000, 1_496_000, 120), line),
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

        const string BrushGolden = "1cc1a2930bfa378b71e1b67c1ea148acc77e61c3294c4307b3c161bb8bcca108";

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
