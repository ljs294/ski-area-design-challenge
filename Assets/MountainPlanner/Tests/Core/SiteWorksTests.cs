using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Costs;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.01 (owner, 2026-11-06): site works as cost lines. Grading is the cut and fill; Clearing is the forest a work
    // newly clears, each cell once, weighted by its natural tree share. Public DOT bid rates ×0.8 (a private party pays
    // less). Lifts take both lines on their price; trails pay Clearing, and Grading when graded.
    public sealed class SiteWorksTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

        sealed class Slope : IHeightSource
        {
            readonly double _grade;
            public Slope(double grade) { _grade = grade; }
            public double At(double x, double y) => 1500 + _grade * (y - (North - 900));
        }

        sealed class Cover : ITreeCover
        {
            readonly Func<double, double, double> _f;
            public Cover(Func<double, double, double> f) { _f = f; }
            public double TreeShare(double x, double y) => _f(x, y);
        }

        static readonly ITreeCover Forest = new Cover((x, y) => 1), Meadow = new Cover((x, y) => 0);

        static EditedHeights Natural(double grade) => new EditedHeights(new Slope(grade), Lattice, TerrainEdits.Empty);

        static LiftBuild.Result Quadlift(EditedHeights ground) =>
            LiftBuild.Build(Quad, ground, West + 100, North - 900, West + 100, North - 100, TowerStance.Leaned);

        // Cells cleared in after and not in before, counted once each.
        static int NewCells(TerrainEdits before, TerrainEdits after)
        {
            int n = 0;
            foreach (var key in TerrainEdits.Changed(before, after))
            {
                var was = before.Get(key);
                var now = after.Get(key);
                for (int j = 0; j < EditTileKey.Side; j++)
                    for (int i = 0; i < EditTileKey.Side; i++)
                        if (now.IsCleared(i, j) && !was.IsCleared(i, j)) n++;
            }
            return n;
        }

        [Test]
        public void EachLineIsItsVolumeOrAreaTimesItsRate()
        {
            // 1,000 yd³ of cut, 500 of fill and one acre of forest.
            var w = new SiteWorks(1000 * SoilPrices.CubicMetresPerCubicYard, 500 * SoilPrices.CubicMetresPerCubicYard, SoilPrices.SquareMetresPerAcre);
            Assert.That(SiteCost.GradingUsd(w), Is.EqualTo((long)Math.Round(1000 * SoilPrices.CutUsdPerCubicYard + 500 * SoilPrices.FillUsdPerCubicYard)));
            Assert.That(SiteCost.ClearingUsd(w), Is.EqualTo((long)Math.Round(SoilPrices.ClearingUsdPerAcre)));
            Assert.That(SiteCost.GradingUsd(default), Is.Zero);
            Assert.That(SiteCost.ClearingUsd(default), Is.Zero);
        }

        [Test]
        public void ThePrivateShareIsAppliedOnce()
        {
            Assert.That(SoilPrices.PrivateShare, Is.EqualTo(0.8));
            Assert.That(SoilPrices.CutUsdPerCubicYard, Is.EqualTo(SoilPrices.CutPublicUsdPerCubicYard * 0.8).Within(1e-9));
            Assert.That(SoilPrices.FillUsdPerCubicYard, Is.EqualTo(SoilPrices.FillPublicUsdPerCubicYard * 0.8).Within(1e-9));
            Assert.That(SoilPrices.ClearingUsdPerAcre, Is.EqualTo(SoilPrices.ClearingPublicUsdPerAcre * 0.8).Within(1e-9));
            // The lines read the private rates, never the public ones.
            var acre = new SiteWorks(0, 0, SoilPrices.SquareMetresPerAcre);
            Assert.That(SiteCost.ClearingUsd(acre), Is.EqualTo((long)Math.Round(SoilPrices.ClearingPublicUsdPerAcre * 0.8)));
        }

        [Test]
        public void ALiftPaysItsPriceGradingAndClearing()
        {
            var ground = Natural(0.24);
            var b = Quadlift(ground);
            var w = SiteCost.OfLift(b, ground, Forest);
            Assert.That(w.CutCubicMetres, Is.EqualTo(b.BottomPad.CutCubicMetres + b.TopPad.CutCubicMetres + b.Envelope.CutCubicMetres).Within(1e-9));
            Assert.That(w.FillCubicMetres, Is.EqualTo(b.BottomPad.FillCubicMetres + b.TopPad.FillCubicMetres).Within(1e-9));
            Assert.That(w.CutCubicMetres, Is.GreaterThan(0), "the pads and the trench cut");
            var lift = LiftCost.OfLine(Quad, 800, 800 * 0.24);
            var cost = SiteCost.With(lift, w);
            Assert.That(cost.Lines.Count, Is.EqualTo(5));
            Assert.That(cost.Line(SiteCost.Grading), Is.EqualTo(SiteCost.GradingUsd(w)));
            Assert.That(cost.Line(SiteCost.Clearing), Is.EqualTo(SiteCost.ClearingUsd(w)));
            Assert.That(cost.TotalUsd, Is.EqualTo(lift.TotalUsd + SiteCost.GradingUsd(w) + SiteCost.ClearingUsd(w)));
            Assert.That(cost.Price, Is.SameAs(lift.Price));
        }

        [Test]
        public void ClearingCountsEachNewCellOnceAndBareGroundIsFree()
        {
            var ground = Natural(0.24);
            var b = Quadlift(ground);
            int cells = NewCells(ground.Edits, b.Edits);
            Assert.That(cells, Is.GreaterThan(0));
            Assert.That(b.ClearedCells, Is.GreaterThan(cells), "the build's own count counts the corridor and terminal clearings twice where they meet");
            Assert.That(SiteCost.OfLift(b, ground, Forest).ForestSquareMetres, Is.EqualTo(cells).Within(1e-9), "each newly cleared cell once, 1 m² of forest");
            Assert.That(SiteCost.OfLift(b, ground, Meadow).ForestSquareMetres, Is.Zero, "bare ground costs nothing to clear");
            // Half forest (east of the line): about half the clearing.
            var half = new Cover((x, y) => x > West + 100 ? 1 : 0);
            double east = SiteCost.OfLift(b, ground, half).ForestSquareMetres;
            Assert.That(east, Is.InRange(cells * 0.35, cells * 0.65));
        }

        [Test]
        public void GroundAlreadyClearedCostsNothing()
        {
            var natural = Natural(0.24);
            // A trail (or another lift) cleared the corridor first: the lift pays only for what it clears beyond.
            var cleared = LiftClearing.Corridor(TerrainEdits.Empty, Lattice, West + 100, North - 900, West + 100, North - 100, out int corridor);
            var ground = new EditedHeights(natural.Source, Lattice, cleared);
            var onNatural = SiteCost.OfLift(Quadlift(natural), natural, Forest).ForestSquareMetres;
            var onCleared = SiteCost.OfLift(Quadlift(ground), ground, Forest).ForestSquareMetres;
            Assert.That(corridor, Is.GreaterThan(0));
            Assert.That(onCleared, Is.LessThan(onNatural - corridor * 0.9), "the corridor already cleared is not paid for again");
            // The same lift built again over its own clearing clears nothing new.
            var b = Quadlift(natural);
            Assert.That(SiteWorks.NewForest(b.Edits, b.Edits, Lattice, Forest), Is.Zero);
        }

        [Test]
        public void SurfaceLiftsArePricedWithTheirSiteWorks()
        {
            var ground = Natural(0.10);
            foreach (string id in new[] { LiftCatalogIds.MontaTbar, LiftCatalogIds.MontaJbar, LiftCatalogIds.GlidepathCarpet, LiftCatalogIds.GlidepathCarpet2Covered })
            {
                var type = LiftType.Of(id);
                double len = type.Belt ? 60 : 300;
                var b = LiftBuild.Build(type, ground, West + 100, North - 900, West + 100, North - 900 + len, TowerStance.Leaned);
                var w = SiteCost.OfLift(b, ground, Forest);
                if (type.Belt)
                {
                    Assert.That(w.FillCubicMetres, Is.EqualTo(b.Bed.FillCubicMetres).Within(1e-9), id + ": a carpet's bed fill");
                    Assert.That(w.CutCubicMetres, Is.EqualTo(b.Bed.CutCubicMetres).Within(1e-9), id);
                }
                var cost = SiteCost.With(LiftCost.OfLine(type, len, len * 0.10), w);
                Assert.That(cost, Is.Not.Null, id + " has a price (task 4.01)");
                Assert.That(cost.Line(SiteCost.Clearing), Is.GreaterThan(0), id + " clears its line through forest");
                Assert.That(cost.TotalUsd, Is.GreaterThan(cost.Line(LiftCost.Terminals)), id);
            }
        }

        [Test]
        public void ATrailPaysClearingAndGradingWhenGraded()
        {
            var writes = new EarthworkWrites();
            for (int r = 100; r < 120; r++)
                for (int c = 50; c < 60; c++) writes.Clear(c, r);   // 200 cells
            writes.Clear(55, 110);                                   // twice: still once
            int seen = 0;
            writes.ForEachCleared((c, r) => seen++);
            Assert.That(seen, Is.EqualTo(200), "each cleared cell once");
            Assert.That(SiteWorks.NewForest(writes, null, Lattice, Forest), Is.EqualTo(200));
            // Ten rows already cleared by earlier work: half the cells are new.
            Assert.That(SiteWorks.NewForest(writes, (c, r) => r < 110, Lattice, Forest), Is.EqualTo(100));
            Assert.That(SiteWorks.NewForest(writes, null, Lattice, Meadow), Is.Zero);

            var w = new SiteWorks(300, 200, SiteWorks.NewForest(writes, null, Lattice, Forest));
            var natural = SiteCost.Trail(w, graded: false);
            Assert.That(natural.Lines.Count, Is.EqualTo(1), "a natural trail pays Clearing only");
            Assert.That(natural.TotalUsd, Is.EqualTo(SiteCost.ClearingUsd(w)));
            var graded = SiteCost.Trail(w, graded: true);
            Assert.That(graded.Line(SiteCost.Grading), Is.EqualTo(SiteCost.GradingUsd(w)));
            Assert.That(graded.TotalUsd, Is.EqualTo(SiteCost.ClearingUsd(w) + SiteCost.GradingUsd(w)));
        }

        [Test]
        public void TheSameWorkGivesTheSameDollars()
        {
            var ground = Natural(0.24);
            var a = SiteCost.With(LiftCost.OfLine(Quad, 800, 192), SiteCost.OfLift(Quadlift(ground), ground, Forest));
            var b = SiteCost.With(LiftCost.OfLine(Quad, 800, 192), SiteCost.OfLift(Quadlift(ground), ground, Forest));
            Assert.That(b.Lines.Count, Is.EqualTo(a.Lines.Count));
            for (int i = 0; i < a.Lines.Count; i++) Assert.That(b.Lines[i].Usd, Is.EqualTo(a.Lines[i].Usd));
        }
    }
}
