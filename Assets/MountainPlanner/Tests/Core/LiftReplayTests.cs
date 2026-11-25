using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 09 (LT24): Remove lift's ground. Replaying the built lifts from what they store gives their first builds' bytes,
    // and leaving one out gives the ground as if it was never built.
    public sealed class LiftReplayTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

        sealed class Slope : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * (y - (North - 900)) + 0.02 * (x - West);
        }

        // Design points are mm from the site centre at (West + 100, North - 900).
        static (double X, double Y) ToGround(PointMm p) => (West + 100 + p.XMetres, North - 900 + p.YMetres);

        static LiftId Add(DesignHistory h, string name, PointMm bottom, PointMm top, LiftBuild.Result b, WorkStage stage = WorkStage.Built)
        {
            var r = h.Submit(new AddLift(h.Revision, name, Quad.CatalogId, bottom, top, TowerMode.Automatic, TowerStance.Plumb,
                                         stage == WorkStage.Built ? b.Towers : null, stage, stage == WorkStage.Built ? b.Levels : (LiftLevels?)null),
                             stage == WorkStage.Built ? b.Edits : null);
            Assert.That(r.Accepted, Is.True, r.Reason);
            return new LiftId(r.Created[0]);
        }

        static LiftBuild.Result Build(TerrainEdits on, PointMm bottom, PointMm top)
        {
            var g = new EditedHeights(new Slope(), Lattice, on);
            var b = ToGround(bottom);
            var t = ToGround(top);
            return LiftBuild.Build(Quad, g, b.X, b.Y, t.X, t.Y, TowerStance.Plumb);
        }

        [Test]
        public void LT24_ReplayGivesTheBuildsBytesAndLeavingOneOutRemovesItsGround()
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            var b1 = Build(TerrainEdits.Empty, new PointMm(0, 0), new PointMm(0, 800_000));
            var id1 = Add(h, "Chair 1", new PointMm(0, 0), new PointMm(0, 800_000), b1);
            var b2 = Build(b1.Edits, new PointMm(300_000, 0), new PointMm(300_000, 700_000));
            var id2 = Add(h, "Chair 2", new PointMm(300_000, 0), new PointMm(300_000, 700_000), b2);
            Add(h, "Plan 3", new PointMm(-300_000, 0), new PointMm(-300_000, 600_000), null, WorkStage.Plan);   // a plan has no ground

            var all = LiftReplay.Rebuild(new Slope(), Lattice, h.Document, ToGround);
            Assert.That(TerrainEdits.Changed(all, b2.Edits), Is.Empty, "every lift replayed: the ground as built");
            var without2 = LiftReplay.Rebuild(new Slope(), Lattice, h.Document, ToGround, id2);
            Assert.That(TerrainEdits.Changed(without2, b1.Edits), Is.Empty, "without the last lift: the ground before it");
            var without1 = LiftReplay.Rebuild(new Slope(), Lattice, h.Document, ToGround, id1);
            var only2 = Build(TerrainEdits.Empty, new PointMm(300_000, 0), new PointMm(300_000, 700_000));
            Assert.That(TerrainEdits.Changed(without1, only2.Edits), Is.Empty, "without the first: only the second's own work");
            Assert.That(TerrainEdits.Changed(without1, b2.Edits).Count, Is.GreaterThan(0), "the first lift's pads and clearing are gone");
        }

        [Test]
        public void LF12v6_AnOldSavesRaisedTopKeepsItsPad()
        {
            // Task 4.50 (LF-12 v6, owner 2026-11-25): a fixed grip's top raised 5 ft or more gets no pad now. A save made before
            // holds that pad in its terrain edits; opening it, and building elsewhere, keep the pad (the edits are saved,
            // not replayed); only work that rebuilds that ground lays the top by today's rule.
            var bottom = new PointMm(0, 0);
            var top = new PointMm(0, 800_000);
            var g = new EditedHeights(new Slope(), Lattice, TerrainEdits.Empty);
            var b = ToGround(bottom);
            var t = ToGround(top);
            var built = LiftBuild.Build(Quad, g, b.X, b.Y, t.X, t.Y, TowerStance.Plumb, null, 0, 2.0);
            Assert.That(built.TopPad.CutCubicMetres + built.TopPad.FillCubicMetres, Is.EqualTo(0), "today: no top pad");
            // What v5 saved: the same lift with its top's pad graded at its level.
            var end = Quad.TopFootprint;
            var v5 = TerminalPad.Grade(new EditedHeights(new Slope(), Lattice, built.Edits), t.X, t.Y, 0, -1, end.Behind, end.Ahead,
                                       end.HalfWidth, built.Levels.TopMetres, end.PadAhead).Edits;
            Assert.That(TerrainEdits.Changed(v5, built.Edits).Count, Is.GreaterThan(0), "the v5 save holds a top pad");

            var h = new DesignHistory(DesignDocument.Empty(3));
            Add(h, "Chair 1", bottom, top, built);
            var site = new EarthworkSite(Lattice, West + 100, North - 900);
            var fold = new DesignEarthwork(new Slope(), site);
            fold.Prime(h.Document, v5);   // the load: the saved edits laid as they are, the design learnt
            var same = fold.Rebuild(h.Document, v5);
            Assert.That(TerrainEdits.Changed(same.Edits, v5), Is.Empty, "opened and rebuilt with nothing changed: the pad kept");

            // A second lift 300 m east: the first lift's ground is not rebuilt, its pad kept.
            var far = Build(v5, new PointMm(300_000, 0), new PointMm(300_000, 700_000));
            Add(h, "Chair 2", new PointMm(300_000, 0), new PointMm(300_000, 700_000), far);
            var withFar = fold.Rebuild(h.Document, v5);
            var heights = new EditedHeights(new Slope(), Lattice, withFar.Edits);
            var saved = new EditedHeights(new Slope(), Lattice, v5);
            for (double along = -end.Behind; along <= end.Ahead; along += 1)
                for (double d = -end.HalfWidth; d <= end.HalfWidth; d += 1)
                    Assert.That(heights.At(t.X + d, t.Y - along), Is.EqualTo(saved.At(t.X + d, t.Y - along)).Within(1e-3), "a lift elsewhere: the pad kept");
            TestContext.WriteLine($"the far lift rebuilt {withFar.Changed.Count} tiles; the first top's pad kept");
            Assert.That(fold.Accept(withFar), Is.True);

            // A third lift starting 40 m east of the first's top: its work reaches the first top's tiles, so they fold again.
            var nearBottom = new PointMm(40_000, 800_000);
            var nearTop = new PointMm(40_000, 1_200_000);
            var near = Build(withFar.Edits, nearBottom, nearTop);
            Add(h, "Chair 3", nearBottom, nearTop, near);
            var withNear = fold.Rebuild(h.Document, withFar.Edits);
            var nearHeights = new EditedHeights(new Slope(), Lattice, withNear.Edits);
            int kept = 0, lost = 0;
            for (double along = -end.Behind; along <= end.Ahead; along += 1)
                for (double d = -end.HalfWidth; d <= end.HalfWidth; d += 1)
                {
                    double was = saved.At(t.X + d, t.Y - along), natural = new Slope().At(t.X + d, t.Y - along);
                    if (Math.Abs(was - natural) < 0.05) continue;   // the pad's own cells only
                    if (Math.Abs(nearHeights.At(t.X + d, t.Y - along) - was) < 1e-3) kept++; else lost++;
                }
            TestContext.WriteLine($"a lift beside the old top: {kept} pad cells kept, {lost} changed");
            Assert.That(kept, Is.GreaterThan(0));
            Assert.That(lost, Is.EqualTo(0), "work beside it folds those tiles again: the saved pad kept (laid as work from before)");
            Assert.That(fold.Accept(withNear), Is.True);

            // Removing the first lift: its own work comes off; the old pad, which no piece explains, stays as saved work.
            var id1 = h.Document.Lifts.First(l => l.Name == "Chair 1").Id;
            var removed = new DeleteLift(h.Revision, id1).Apply(h.Document);
            Assert.That(removed.Accepted, Is.True);
            var without = fold.Rebuild(removed.Document, withNear.Edits);
            var gone = new EditedHeights(new Slope(), Lattice, without.Edits);
            int stays = 0, back = 0;
            for (double along = -end.Behind; along <= end.Ahead; along += 1)
                for (double d = -end.HalfWidth; d <= end.HalfWidth; d += 1)
                {
                    double was = saved.At(t.X + d, t.Y - along), natural = new Slope().At(t.X + d, t.Y - along);
                    if (Math.Abs(was - natural) < 0.05) continue;
                    if (Math.Abs(gone.At(t.X + d, t.Y - along) - was) < 1e-3) stays++; else back++;
                }
            TestContext.WriteLine($"the first lift removed: {stays} pad cells stay, {back} changed");
        }

        [Test]
        public void TL24_TheEarthworkFoldLaysLiftsAsTheirBuildsDid()
        {
            // The shared fold (P3-08's DesignEarthwork, TL3) with task 09's lift contributor: the same bytes as the builds,
            // and a removed lift's ground gone with the other's kept.
            var h = new DesignHistory(DesignDocument.Empty(3));
            var b1 = Build(TerrainEdits.Empty, new PointMm(0, 0), new PointMm(0, 800_000));
            Add(h, "Chair 1", new PointMm(0, 0), new PointMm(0, 800_000), b1);
            var b2 = Build(b1.Edits, new PointMm(300_000, 0), new PointMm(300_000, 700_000));
            var id2 = Add(h, "Chair 2", new PointMm(300_000, 0), new PointMm(300_000, 700_000), b2);
            var site = new EarthworkSite(Lattice, West + 100, North - 900);
            var fold = new DesignEarthwork(new Slope(), site);
            fold.Prime(DesignDocument.Empty(3), TerrainEdits.Empty);
            var all = fold.Rebuild(h.Document, TerrainEdits.Empty);
            Assert.That(TerrainEdits.Changed(all.Edits, b2.Edits), Is.Empty, "the fold lays both lifts as built");
            Assert.That(fold.Accept(all), Is.True);
            var removed = new DeleteLift(h.Revision, id2).Apply(h.Document);
            Assert.That(removed.Accepted, Is.True);
            var without = fold.Rebuild(removed.Document, all.Edits);
            Assert.That(TerrainEdits.Changed(without.Edits, b1.Edits), Is.Empty, "removing the second lift leaves the first's ground exactly");
        }
    }
}
