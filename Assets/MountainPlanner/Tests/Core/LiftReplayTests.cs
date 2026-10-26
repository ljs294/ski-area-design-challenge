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
