using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // The Monta T-bar and J-bar in the game (surface lifts, v0.3.2): their ids, numbers and the tow's line rules (the rope's
    // band over the track, low towers on the tow kit's heads), built the way every lift is, on made-up ground.
    public sealed class SurfaceTowTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        const double Ft = 0.3048;
        static readonly EditLattice Lattice = new EditLattice(West, North);

        /// <summary>A profile along a line due north from (100, 900).</summary>
        sealed class Profile : IHeightSource
        {
            readonly Func<double, double> _f;
            public Profile(Func<double, double> f) { _f = f; }
            public double At(double x, double y) => 1500 + _f(y - (North - 900));
        }

        static LiftBuild.Result Build(string id, Func<double, double> f, double length) =>
            DesignTestKit.AsPlayer((hb, ht) => LiftBuild.Build(LiftType.Of(id), new EditedHeights(new Profile(f), Lattice, TerrainEdits.Empty),
                                                               West + 100, North - 900, West + 100, North - 900 + length, TowerStance.Plumb, null, hb, ht));

        [Test]
        public void TheTbarAndJbarAreBuildableTowsDrivenAtTheBottom()
        {
            foreach (string id in new[] { LiftCatalogIds.MontaTbar, LiftCatalogIds.MontaJbar })
            {
                Assert.That(LiftCatalogIds.Known(id) && LiftCatalogIds.Buildable(id) && LiftCatalogIds.IsTow(id), Is.True, id);
                Assert.That(LiftCatalogIds.Maker(id), Is.EqualTo("monta"), id);
                Assert.That(LiftCatalogIds.DriveChoice(id), Is.False, id);
                var lift = new Lift(new LiftId(1), "T-bar 1", WorkStage.Built, id, new PointMm(0, 0), new PointMm(0, 300_000), TowerMode.Automatic,
                                    TowerStance.Plumb, null, null, LiftDrive.Top);
                Assert.That(lift.Drive, Is.EqualTo(LiftDrive.Bottom), id + ": D1, the drive at the bottom");
                var t = LiftType.Of(id);
                Assert.That((t.Tow, t.Bottom.Model, t.Top.Model, t.TowerKit), Is.EqualTo((true, "monta_tbar_drive", "monta_tbar_return", "monta_tbar_")), id);
                Assert.That(t.ClearanceMetres, Is.LessThan(t.RopeMaxMetres), id);
            }
            Assert.That(LiftType.Of(LiftCatalogIds.MontaTbar).Capacity, Is.EqualTo(1200).Within(1e-6), "Q2: two riders a bar, every 15 m at 2.5 m/s");
            Assert.That(LiftType.Of(LiftCatalogIds.MontaJbar).Capacity, Is.EqualTo(600).Within(1e-6), "one rider a J");
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Tow, Is.False);
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).ClearanceMetres, Is.EqualTo(LiftRope.Clearance), "the chairs keep TW-01's 15 ft");
        }

        [TestCase(0.20, 400)]
        [TestCase(0.30, 700)]
        [TestCase(0.12, 250)]
        public void ATbarBuildsOnLowTowersWithItsRopeInTheBand(double grade, double length)
        {
            // An even pitch with gentle rolls: the automatic line stands 15–30 ft towers on the tow kit's heads, the rope never
            // under the bars' 17 ft or over their cords' 33 ft, and it builds.
            var b = Build(LiftCatalogIds.MontaTbar, s => grade * s + 1.5 * Math.Sin(s / 35), length);
            TestContext.Progress.WriteLine($"T-bar {grade:P0} {length} m: {b.Towers.Count} towers " +
                                           $"({string.Join(", ", b.Towers.Select(t => Math.Round(t.HeightMm / 1000.0 / Ft) + " ft"))}), " +
                                           $"band {b.Rope.LowestClearance:F1}–{b.Rope.HighestOverTrack:F1} m; " + string.Join("; ", b.Problems.Select(p => p.Message)));
            Assert.That(b.Ok, Is.True, string.Join("; ", b.Problems.Select(p => p.Message)));
            Assert.That(b.Towers.Count, Is.GreaterThan(0));
            Assert.That(b.Towers.Select(t => t.HeightMm), Is.All.InRange(DesignUnits.TowStationTowerMinMm, DesignUnits.TowTowerMaxMm));
            var kit = new[] { LiftRope.Head.S4, LiftRope.Head.S6, LiftRope.Head.D4 };
            Assert.That(b.Rope.Towers.All(t => kit.Contains(t.Head)), Is.True, string.Join(", ", b.Rope.Towers.Select(t => t.Head)));
            Assert.That(b.Rope.ClearanceOk && b.Rope.BandOk, Is.True);
        }

        [Test]
        public void ADipUnderTheLineGetsATowerRatherThanARopeTooHighForTheBars()
        {
            // A 12 m deep hollow halfway up: spanning it would leave the rope far over the cords' reach, so the line stands a
            // tower in it.
            Func<double, double> f = s => 0.2 * s - 12 * Math.Exp(-Math.Pow((s - 200) / 25, 2));
            var b = Build(LiftCatalogIds.MontaTbar, f, 400);
            Assert.That(b.Rope.BandOk, Is.True, $"highest {b.Rope.HighestOverTrack:F1} m at {b.Rope.HighestOverTrackS:F0} m");
            Assert.That(b.Towers.Any(t => Math.Abs(t.AlongMm / 1000.0 - 200) < 40), Is.True, "a tower in the hollow");
        }

        [Test]
        public void TheTowKitHasNoCombinationHead()
        {
            var kit = TowerForces.HeadKit.Tow;
            Assert.That((kit.MaxSheaves, kit.MaxHoldDown, kit.Combination), Is.EqualTo((6, 4, 0)));
            double t = 20_000;
            Assert.That(TowerForces.Choose(t, t, -0.05, -0.05, kit), Is.EqualTo(LiftRope.Head.S4));
            Assert.That(TowerForces.Choose(t, t, -0.35, -0.35, kit), Is.EqualTo(LiftRope.Head.S6));
            Assert.That(TowerForces.Choose(t, t, 0.1, 0.1, kit), Is.EqualTo(LiftRope.Head.D4));
            Assert.That(TowerForces.Choose(t, t, 0.4, 0.4, kit), Is.EqualTo(LiftRope.Head.TooSharp));
            Assert.That(TowerForces.HoldDownSheavesOf(LiftRope.Head.D4), Is.EqualTo(4));
        }
    }
}
