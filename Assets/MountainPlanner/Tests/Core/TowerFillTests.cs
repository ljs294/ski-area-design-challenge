using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 09: towers by hand (LF-04). Fill the rest finishes a line from the towers placed so far, and BuildWith builds the
    // lift on exactly the towers given.
    public sealed class TowerFillTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
        const double Bx = West + 100, By = North - 900, Tx = West + 100, Ty = North - 100;

        sealed class Slope : IHeightSource
        {
            readonly Func<double, double> _f;
            public Slope(Func<double, double> f) { _f = f; }
            public double At(double x, double y) => 1500 + _f(y - (North - 900));
        }

        static EditedHeights Ground(Func<double, double> f) => new EditedHeights(new Slope(f), Lattice, TerrainEdits.Empty);

        static readonly Func<double, double> Dip = s => 0.2 * s - (s > 380 && s < 520 ? 12 * Math.Sin((s - 380) / 140 * Math.PI) : 0);

        static string Describe(IEnumerable<TowerSpec> t) => string.Join(", ", t.Select(x => $"{x.AlongMm / 304.8:F0}/{x.HeightMm / 304.8:F0}"));

        [Test]
        public void LF04_FillTheRestFinishesTheLineAndBuildsClear()
        {
            foreach (var (name, f) in new[] { ("20% with a dip", Dip), ("15% plane", (Func<double, double>)(s => 0.15 * s)), ("convex", s => 0.0004 * s * s) })
            {
                var g = Ground(f);
                var levels = LiftBuild.StartLevels(Quad, g, Bx, By, Tx, Ty, 0, 0, out _, out _);
                var fill = TowerFill.Fill(Quad, g, Bx, By, Tx, Ty, 0, 0, Array.Empty<Tower>(), TowerStance.Plumb);
                TestContext.Progress.WriteLine($"{name}: {fill.Added} towers [{Describe(fill.Towers)}] {fill.Problem?.ToString() ?? "complete"}");
                Assert.That(fill.Ok, Is.True, $"{name}: {fill.Problem}");
                Assert.That(fill.Towers.Count, Is.GreaterThan(0));
                // As a player builds it: the suggested levels taken (LT34).
                var built = DesignTestKit.AsPlayer((hb, ht) => LiftBuild.BuildWith(Quad, g, Bx, By, Tx, Ty, fill.Towers, TowerStance.Plumb, hb, ht));
                Assert.That(built.Towers.Select(t => t.AlongMm), Is.EqualTo(fill.Towers.Select(t => t.AlongMm)), "built on exactly those towers");
                Assert.That(built.Rope.ClearanceOk, Is.True, $"{name}: shortfall {built.Rope.Shortfall:F2} m at {built.Rope.ShortfallS:F0} m; {string.Join("; ", built.Problems)}");
            }
        }

        [Test]
        public void LF04_FillKeepsThePlacedTowersAndIsDeterministic()
        {
            var g = Ground(Dip);
            var levels = LiftBuild.StartLevels(Quad, g, Bx, By, Tx, Ty, 0, 0, out _, out _);
            var hand = new TowerInHand(Quad, g, Bx, By, Tx, Ty, levels, Array.Empty<Tower>(), TowerStance.Plumb);
            int h = DesignUnits.FeetToMm(35);
            var band = hand.Band(h);
            Assume.That(band, Is.Not.Empty);
            var mine = new Tower(TowerId.Make(9, 0), (int)Math.Round(band[0].From * 1000 / DesignUnits.FiveFeetMm) * DesignUnits.FiveFeetMm, h);
            var a = TowerFill.Fill(Quad, g, Bx, By, Tx, Ty, 0, 0, new[] { mine }, TowerStance.Plumb);
            var b = TowerFill.Fill(Quad, g, Bx, By, Tx, Ty, 0, 0, new[] { mine }, TowerStance.Plumb);
            Assert.That(a.Towers[0].AlongMm, Is.EqualTo(mine.AlongMm));
            Assert.That(a.Towers[0].HeightMm, Is.EqualTo(mine.HeightMm), "the tower placed by hand stays as placed");
            Assert.That(a.Added, Is.EqualTo(a.Towers.Count - 1));
            Assert.That(Describe(a.Towers), Is.EqualTo(Describe(b.Towers)), "the same towers in, the same towers out");
            Assert.That(a.Towers.Zip(a.Towers.Skip(1), (x, y) => y.AlongMm - x.AlongMm).All(d => d >= DesignUnits.TowerGapMm), Is.True, "TW-03: 50 ft apart");
        }

        [Test]
        public void BuildWithStandsOnTheStartLevelsAndKeepsAShortLineRed()
        {
            // One tower on a long line can't carry it: red with TW-01's reason, not quietly fixed.
            var g = Ground(Dip);
            var one = new[] { new TowerSpec(DesignUnits.FeetToMm(60), DesignUnits.FeetToMm(25)) };
            var built = LiftBuild.BuildWith(Quad, g, Bx, By, Tx, Ty, one, TowerStance.Plumb);
            var start = LiftBuild.StartLevels(Quad, g, Bx, By, Tx, Ty, 0, 0, out _, out _);
            Assert.That(built.Levels, Is.EqualTo(start), "no search: the levels are where it starts");
            Assert.That(built.Ok, Is.False);
            Assert.That(built.Problems.Any(p => p.Rule == "TW-01" && p.Blocks), Is.True, string.Join("; ", built.Problems));
        }
    }
}
