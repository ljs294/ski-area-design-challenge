using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task 09: editing a built lift (LT2, LT9: towers along the line and in height, either terminal anywhere, the type and the
    // terminal levels, applied as one step), towers by hand (LF-04, TW-09, TW-10, TW-13) and the clearance check P3-08 calls.
    public sealed class LiftEditTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

        sealed class Slope : IHeightSource
        {
            readonly Func<double, double> _f;
            public Slope(Func<double, double> f) { _f = f; }
            public double At(double x, double y) => 1500 + _f(y - (North - 900));
        }

        static DesignHistory BuiltLift(out LiftId id)
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            id = AddLift(h, "Chair 1", P(0, 0), P(0, 2600), WorkStage.Built, Tower(300, 35), Tower(1000, 35), Tower(1800, 40));
            var l = h.Document.FindLift(id);
            Assert.That(h.Submit(EditLiftWithLevels(h, l, new LiftLevels(2_000_000, 2_200_000))).Accepted, Is.True);
            return h;
        }

        static EditLift EditLiftWithLevels(DesignHistory h, Lift l, LiftLevels levels) =>
            new EditLift(h.Revision, l.Id, l.CatalogId, l.Bottom, l.Top, l.TowerMode, l.Stance, l.Towers, null, levels, LiftEdit.TerminalLevel);

        [Test]
        public void LT2_AWholeEditIsOneStepAndUndoesExactly()
        {
            var h = BuiltLift(out var id);
            var before = Bytes(h);
            int undo = h.UndoCount;
            var l = h.Document.FindLift(id);
            // Move the top terminal (anywhere: off the line), change the type, move a tower and its height, drop one, add one.
            var kept = new List<Tower> { new Tower(l.Towers[0].Id, Ft(320), Ft(40)), l.Towers[1] };
            var edit = new EditLift(h.Revision, id, LiftCatalogIds.MontaFg4, l.Bottom, P(200, 2700), TowerMode.ByHand, TowerStance.Leaned,
                                    kept, new[] { Tower(2100, 45) }, new LiftLevels(2_000_000, 2_210_000));
            var r = h.Submit(edit);
            Assert.That(r.Accepted, Is.True, r.ToString());
            Assert.That(h.UndoCount, Is.EqualTo(undo + 1), "one step");
            Assert.That(h.UndoLabel, Is.EqualTo("Edit lift"));
            var after = h.Document.FindLift(id);
            Assert.That(after.CatalogId, Is.EqualTo(LiftCatalogIds.MontaFg4));
            Assert.That(after.Top, Is.EqualTo(P(200, 2700)));
            Assert.That(after.Towers.Select(t => t.AlongMm), Is.EqualTo(new[] { Ft(320), Ft(1000), Ft(2100) }));
            Assert.That(after.Towers[0].Id, Is.EqualTo(l.Towers[0].Id), "a moved tower keeps its ID");
            Assert.That(after.Levels, Is.EqualTo(new LiftLevels(2_000_000, 2_210_000)));
            Assert.That(h.Undo(), Is.True);
            Assert.That(DesignBytes.Same(Bytes(h), before), Is.True, "undo restores the lift exactly");
            Assert.That(h.Redo(), Is.True);
            Assert.That(h.Document.FindLift(id).Top, Is.EqualTo(P(200, 2700)));
        }

        [Test]
        public void LF05v2_MovingTheBottomTerminalKeepsTowersMeasuredFromIt()
        {
            // LT9, By hand: towers keep their distance from the bottom terminal; those past the new top are the tool's to drop.
            var h = BuiltLift(out var id);
            var l = h.Document.FindLift(id);
            var r = h.Submit(new EditLift(h.Revision, id, l.CatalogId, P(100, 100), l.Top, TowerMode.ByHand, l.Stance,
                                          l.Towers.Take(2).ToList(), null, l.Levels, LiftEdit.MoveTerminal));
            Assert.That(r.Accepted, Is.True, r.ToString());
            Assert.That(h.UndoLabel, Is.EqualTo("Move terminal"));
            Assert.That(h.Document.FindLift(id).Towers.Select(t => t.AlongMm), Is.EqualTo(l.Towers.Take(2).Select(t => t.AlongMm)));
        }

        [Test]
        public void CF05_AStaleOrEmptyEditIsRejectedWhole()
        {
            var h = BuiltLift(out var id);
            var l = h.Document.FindLift(id);
            long old = h.Revision;
            AddTrail(h, "Run 1", P(500, 1000), P(500, 0));
            var before = Bytes(h);
            var r = h.Submit(new EditLift(old, id, LiftCatalogIds.MontaFg4, l.Bottom, l.Top, l.TowerMode, l.Stance, l.Towers, null, l.Levels, LiftEdit.ChangeType));
            Assert.That(r.Code, Is.EqualTo(RejectionCode.Stale));
            Assert.That(h.Submit(EditLift.From(h.Revision, h.Document.FindLift(id))).Accepted, Is.False, "nothing changes");
            var t = l.Towers[0];
            Assert.That(h.Submit(new EditLift(h.Revision, id, l.CatalogId, l.Bottom, P(0, 200), l.TowerMode, l.Stance, l.Towers, null, l.Levels)).Accepted,
                Is.False, "towers past a moved top must be dropped first");
            Assert.That(DesignBytes.Same(Bytes(h), before), Is.True);
        }

        // Towers by hand on a line climbing 20% (steeper bottoms need the pit and the raise that only the automatic build digs), with a dip in the middle: the tower in hand's status and green band.
        static (TowerInHand Hand, EditedHeights Ground) Hand(IReadOnlyList<Tower> placed)
        {
            var ground = new EditedHeights(new Slope(s => 0.2 * s - (s > 380 && s < 520 ? 12 * Math.Sin((s - 380) / 140 * Math.PI) : 0)), Lattice, TerrainEdits.Empty);
            double bx = West + 100, by = North - 900, tx = West + 100, ty = North - 100;
            var levels = LiftBuild.GroundLevels(Quad, ground, bx, by, tx, ty);
            return (new TowerInHand(Quad, ground, bx, by, tx, ty, levels, placed, TowerStance.Plumb), ground);
        }

        [Test]
        public void TW13_TheTowerInHandSaysWhyItsRedAndWhereTheNextCanReach()
        {
            var (hand, _) = Hand(Array.Empty<Tower>());
            // Too close to the bottom terminal for a tower.
            var near = hand.Evaluate(3, DesignUnits.FeetToMm(25));
            Assert.That(near.Ok, Is.False);
            Assert.That(near.Problem.Value.Rule, Is.EqualTo("TW-03"));
            // A short station tower far out: the chairs sag below 15 ft in the span behind it.
            var low = hand.Evaluate(150, DesignUnits.FeetToMm(15));
            Assert.That(low.Ok, Is.False);
            Assert.That(low.Problem.Value.Rule, Is.EqualTo("TW-01").Or.EqualTo("TW-02").Or.EqualTo("TW-16"));
            Assert.That(low.Problem.Value.Message, Is.Not.Empty);
            // Somewhere in the green band it's fine, and it says how far the next one can reach.
            var band = hand.Band(DesignUnits.FeetToMm(15));
            Assert.That(band, Is.Not.Empty, "a 15 ft station tower can stand somewhere");
            double at = band[0].From;
            var ok = hand.Evaluate(at, DesignUnits.FeetToMm(15));
            TestContext.Progress.WriteLine($"band {string.Join(", ", band.Select(b => $"{b.From:F0}-{b.To:F0} m"))}; at {at:F0} m: {ok.Problem?.ToString() ?? "ok"}, " +
                                           $"head {ok.Head}, next within {ok.NextReach / 0.3048:F0} ft or {ok.NextReachTallest / 0.3048:F0} ft at 100 ft");
            Assert.That(ok.NextReach, Is.GreaterThan(0));
            Assert.That(ok.NextReachTallest, Is.GreaterThanOrEqualTo(ok.NextReach), "a 100 ft tower reaches at least as far");
        }

        [Test]
        public void TW09_TheGreenBandAgreesWithTheFullCheck()
        {
            // TW-09's quick test and TW-13's full one agree on where the first tower can stand, but for the full check's
            // stricter low-zone line within a slot of the band's edge.
            var (hand, _) = Hand(Array.Empty<Tower>());
            int h = DesignUnits.FeetToMm(35);
            var band = hand.Band(h);
            int agree = 0, total = 0;
            for (double s = hand.FirstAlong; s < 250; s += 1.524)
            {
                bool green = band.Any(b => s >= b.From - 1e-6 && s <= b.To + 1e-6);
                bool full = hand.Evaluate(s, h).Ok;
                total++;
                if (green == full) agree++;
            }
            TestContext.Progress.WriteLine($"TW-09 band vs the full check: {agree} of {total} slots agree");
            Assert.That(agree, Is.GreaterThanOrEqualTo(0.9 * total));
        }

        [Test]
        public void TW10_ATowerMoveAndTheBandAreFast()
        {
            var placed = new List<Tower> { new Tower(TowerId.Make(1, 0), DesignUnits.FeetToMm(100), DesignUnits.FeetToMm(25)) };
            var (hand, _) = Hand(placed);
            hand.Evaluate(60, DesignUnits.FeetToMm(35));
            hand.Band(DesignUnits.FeetToMm(35));
            var moves = new List<double>();
            var bands = new List<double>();
            for (int i = 0; i < 9; i++)
            {
                var sw = Stopwatch.StartNew();
                hand.Evaluate(60 + i * 1.524, DesignUnits.FeetToMm(35));
                moves.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                hand.Band(DesignUnits.FeetToMm(35 + 5 * (i % 3)));
                bands.Add(sw.Elapsed.TotalMilliseconds);
            }
            moves.Sort();
            bands.Sort();
            Budget.AtMost(moves[4], 2.0, "TW-10 on an 800 m line: a tower move (median; 0.5 ms on the reference PC)");
            Budget.AtMost(bands[4], 8.0, "TW-10 on an 800 m line: the band (median; 2 ms on the reference PC)");
        }

        [Test]
        public void TF11_TheClearanceCheckFindsAFillUnderTheChairs()
        {
            // P3-08's TF-11 asks whether its grading would push a lift's chairs below 15 ft.
            var natural = new EditedHeights(new Slope(s => 0.25 * s), Lattice, TerrainEdits.Empty);
            double bx = West + 100, by = North - 900, tx = West + 100, ty = North - 100;
            var b = LiftBuild.Build(Quad, natural, bx, by, tx, ty, TowerStance.Plumb);
            var withLift = new EditedHeights(natural.Source, Lattice, b.Edits);
            double siteX = West + 100, siteY = North - 900;   // the bottom bullwheel at the site's centre
            var lift = new Lift(LiftId.Make(1, 1), "Chair", WorkStage.Built, Quad.CatalogId, new PointMm(0, 0), new PointMm(0, 800_000),
                                TowerMode.Automatic, TowerStance.Plumb, b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(),
                                b.Levels);
            Assert.That(LiftClearance.Failing(withLift, lift, Quad, siteX, siteY), Is.Empty, b.Ok ? "" : string.Join("; ", b.Problems));
            // A 12 ft fill across the line midway between two towers past the station zone.
            int k = b.Towers.Count / 2;
            double mid = (b.Towers[k].AlongMm + b.Towers[k + 1].AlongMm) / 2000.0;
            var fill = new Dictionary<EditTileKey, TerrainEditTile.Builder>();
            int r0 = (int)Math.Round(North - (by + mid + 5)), r1 = (int)Math.Round(North - (by + mid - 5));
            for (int r = r0; r <= r1; r++)
                for (int c = (int)Math.Round(bx - 12 - West); c <= (int)Math.Round(bx + 12 - West); c++)
                {
                    var key = EditTileKey.Of(c, r);
                    if (!fill.TryGetValue(key, out var tb)) fill[key] = tb = new TerrainEditTile.Builder(key);
                    tb.SetDeltaMm(c - key.FirstColumn, r - key.FirstRow, withLift.Edits.DeltaMmAt(c, r) + 3658);
                }
            var edits = withLift.Edits.With(fill.Values.Select(v => v.Build()));
            var gaps = LiftClearance.Failing(new EditedHeights(natural.Source, Lattice, edits), lift, Quad, siteX, siteY);
            Assert.That(gaps, Is.Not.Empty);
            Assert.That(gaps.Any(g => g.FromMetres <= mid + 5 && g.ToMetres >= mid - 5), Is.True, string.Join("; ", gaps));
        }
    }
}
