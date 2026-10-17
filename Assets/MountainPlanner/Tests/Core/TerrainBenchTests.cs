using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // The edited heights, the straight test bench and terrain undo (task P3-05; rules SH-02, GR-01 to GR-07, GR-09,
    // GR-10, CF-03, CF-05, CL-07).
    public sealed class TerrainBenchTests
    {
        /// <summary>A sidehill: rising 30% to the east, falling 10% to the north, with a gentle wave.</summary>
        sealed class Sidehill : IHeightSource
        {
            public double At(double x, double y) => 2000 + 0.3 * (x - West) - 0.1 * (North - y) * -1 + 0.5 * Math.Sin(x * 0.05) * Math.Cos(y * 0.07);
        }

        sealed class Plane : IHeightSource
        {
            readonly double _east;
            public Plane(double east) => _east = east;
            public double At(double x, double y) => 1500 + _east * (x - West);
        }

        const double West = 500_000.25, North = 4_200_000.75;   // fractional, as package corners are
        static readonly EditLattice Lattice = new EditLattice(West, North);

        static EditedHeights Natural(IHeightSource g) => new EditedHeights(g, Lattice, TerrainEdits.Empty);

        /// <summary>A 300 m bench running north (down the 10% fall line) across the sidehill, 100 ft wide.</summary>
        static TestBench.Result Bench(EditedHeights h) =>
            TestBench.Build(h, West + 400, North - 200, West + 400, North - 500, 30.48);

        [Test]
        public void SH02_EditedHeightsAreTheSourcePlusTheChangeAndTheSourceWhereNothingChanged()
        {
            var g = new Sidehill();
            var tile = new TerrainEditTile.Builder(new EditTileKey(1, 1)).SetDeltaMm(10, 20, 1500).Build();
            var h = new EditedHeights(g, Lattice, TerrainEdits.From(new[] { tile }));
            int c = 64 + 10, r = 64 + 20;
            Assert.That(h.AtPoint(c, r), Is.EqualTo(g.At(Lattice.X(c), Lattice.Y(r)) + 1.5));
            Assert.That(h.At(Lattice.X(c), Lattice.Y(r)), Is.EqualTo(g.At(Lattice.X(c), Lattice.Y(r)) + 1.5).Within(1e-9));
            Assert.That(h.At(Lattice.X(c) + 0.5, Lattice.Y(r)), Is.EqualTo(g.At(Lattice.X(c) + 0.5, Lattice.Y(r)) + 0.75).Within(1e-9), "bilinear between points");
            double far = h.At(Lattice.X(c) + 3.3, Lattice.Y(r) - 2.1);
            Assert.That(far, Is.EqualTo(g.At(Lattice.X(c) + 3.3, Lattice.Y(r) - 2.1)), "exactly the source where nothing changed");
        }

        [Test]
        public void GR01_GR10_TheBenchIsLevelAcrossEachStationToWithinAMillimetre()
        {
            var natural = Natural(new Sidehill());
            var bench = Bench(natural);
            var h = new EditedHeights(natural.Source, Lattice, bench.Edits);
            Assert.That(bench.UngradedStations, Is.EqualTo(0), "a 30% sidehill holds a bench");
            // At the head and tail stations the bench stands at the centreline's natural ground (GR-01), to 1 mm (GR-10).
            Assert.That(h.AtPoint(400, 200), Is.EqualTo(natural.AtPoint(400, 200)).Within(0.001), "head station");
            Assert.That(h.AtPoint(400, 500), Is.EqualTo(natural.AtPoint(400, 500)).Within(0.001), "tail station");
            // Exactly level across: every graded point on one row within the bench has one height (GR-01, GR-10).
            int benchRow = 200 + 150;
            var heights = Enumerable.Range(395, 10).Select(c => h.AtPoint(c, benchRow)).ToArray();
            Assert.That(heights.Max() - heights.Min(), Is.LessThanOrEqualTo(0.001), "level across the section");
        }

        [Test]
        public void GR02_FacesAreNoSteeperThanOneToOne()
        {
            var natural = Natural(new Plane(0.4));
            var bench = TestBench.Build(natural, West + 300, North - 100, West + 300, North - 400, 30.48);
            var h = new EditedHeights(natural.Source, Lattice, bench.Edits);
            int row = 250;
            for (int c = 284; c < 316; c++)
            {
                double step = Math.Abs(h.AtPoint(c + 1, row) - h.AtPoint(c, row));
                Assert.That(step, Is.LessThanOrEqualTo(1.0 + 0.002), $"column {c}");
            }
            // A 40% sidehill gives a bench of about budget × (1 − m/s) per side (GR-03).
            var level = Enumerable.Range(284, 32).Count(c => Math.Abs(h.AtPoint(c, row) - h.AtPoint(300, row)) < 0.001);
            Assert.That(level, Is.InRange(16, 20), "the level part: 30.5 m × 0.6 ≈ 18 m");
        }

        [Test]
        public void GR07_AStretchTooSteepForABenchStaysNatural()
        {
            var natural = Natural(new Plane(1.2));   // a 120% sidehill: no bench fits (GR-03 at m > s)
            var bench = TestBench.Build(natural, West + 300, North - 100, West + 300, North - 400, 30.48);
            Assert.That(bench.UngradedStations, Is.EqualTo(bench.Stations));
            Assert.That(bench.Edits.Tiles.All(t => !t.HasHeights), Is.True, "no height changes");
            Assert.That(bench.Edits.Tiles.Any(t => t.HasClearing), Is.True, "still cleared (grading is a tool, never a gate)");
        }

        [Test]
        public void GR06_NothingChangesOutsideTheFootprint()
        {
            var natural = Natural(new Sidehill());
            var bench = Bench(natural);
            var h = new EditedHeights(natural.Source, Lattice, bench.Edits);
            for (int r = 150; r < 560; r++)
                for (int c = 340; c < 460; c++)
                {
                    double x = Lattice.X(c), y = Lattice.Y(r);
                    bool inside = Math.Abs(x - (West + 400)) <= 15.24 && y <= North - 200 && y >= North - 500;
                    if (!inside)
                    {
                        Assert.That(bench.Edits.DeltaMmAt(c, r), Is.EqualTo(0), $"point ({c}, {r})");
                        Assert.That(h.AtPoint(c, r), Is.EqualTo(natural.AtPoint(c, r)));
                    }
                    bool cellInside = Math.Abs(x + 0.5 - (West + 400)) <= 15.24 && y - 0.5 <= North - 200 && y - 0.5 >= North - 500;
                    Assert.That(bench.Edits.IsClearedAt(c, r), Is.EqualTo(cellInside), $"cell ({c}, {r})");
                }
        }

        [Test]
        public void SH04_TheSameBenchGivesTheSameBytes()
        {
            var a = TerrainEditBytes.Encode(Bench(Natural(new Sidehill())).Edits);
            var b = TerrainEditBytes.Encode(Bench(Natural(new Sidehill())).Edits);
            Assert.That(DesignBytes.Same(a, b), Is.True);
        }

        [Test]
        public void CF05_CL07_UndoAndRedoRestoreTheEditsExactlyAndMoveTheTerrainRevision()
        {
            var natural = Natural(new Sidehill());
            var h = new DesignHistory(DesignDocument.Empty(7));
            long rev0 = h.TerrainRevision;
            var before = TerrainEditBytes.Encode(h.Terrain);

            var bench = Bench(new EditedHeights(natural.Source, Lattice, h.Terrain));
            var add = new AddTrail(h.Revision, "Bench", TrailSettings.GradedRun, Straight(P(0, 0), P(0, -1000)), P(0, 0), P(0, -1000), WorkStage.Built);
            Assert.That(h.Submit(add, bench.Edits).Accepted, Is.True);
            long rev1 = h.TerrainRevision;
            Assert.That(rev1, Is.GreaterThan(rev0));
            Assert.That(h.Current.Terrain, Is.SameAs(bench.Edits));
            var after = TerrainEditBytes.Encode(h.Terrain);

            // A command that changes no ground keeps the edits and the terrain revision.
            h.Submit(new RenameTrail(h.Revision, h.Document.Trails[0].Id, "Bench 2"));
            Assert.That(h.TerrainRevision, Is.EqualTo(rev1));

            h.Undo();
            Assert.That(h.TerrainRevision, Is.EqualTo(rev1), "undoing a rename keeps the ground");
            h.Undo();
            Assert.That(DesignBytes.Same(TerrainEditBytes.Encode(h.Terrain), before), Is.True, "undo restores the edits exactly");
            Assert.That(h.TerrainRevision, Is.GreaterThan(rev1), "an undo gets a new terrain revision, never an old one");
            h.Redo();
            Assert.That(DesignBytes.Same(TerrainEditBytes.Encode(h.Terrain), after), Is.True);
            Assert.That(h.Stamp(3).TerrainRevision, Is.EqualTo(h.TerrainRevision));

            h.Load(DesignDocument.Empty(7), bench.Edits);
            Assert.That(h.Terrain, Is.SameAs(bench.Edits));
            Assert.That(h.CanUndo, Is.False);
        }
    }
}
