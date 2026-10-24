using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // The owner's rulings after the lift mockup (task 09, LT13–LT15): lift types with placeholder numbers (LC-08), a rise
    // limit per type instead of RP-05's tension limit (LC-07), and lenient placement where only clearance, spacing and the
    // rise limit block (LC-09).
    public sealed class LiftCatalogTests
    {
        const double Ft = 0.3048;
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);

        sealed class Line : IHeightSource
        {
            readonly Func<double, double> _f;
            public Line(Func<double, double> f) { _f = f; }
            public double At(double x, double y) => 1500 + _f(y - (North - 1500));
        }

        [Test]
        public void LC08_EveryCarrierAndMakerIsAKnownType()
        {
            Assert.That(LiftCatalogIds.Known(LiftCatalogIds.SesselliftFgq4), Is.True);
            Assert.That(LiftCatalogIds.Known(LiftCatalogIds.MontaFg4), Is.True);
            foreach (string carrier in LiftCatalogIds.Carriers)
                foreach (string maker in LiftCatalogIds.Makers)
                {
                    string id = LiftCatalogIds.Of(carrier, maker);
                    Assert.That(LiftCatalogIds.Known(id), Is.True, id);
                    if (id == LiftCatalogIds.SesselliftD6) continue;   // its own models (LT41, below)
                    var type = LiftType.Of(id);
                    Assert.That(type.Bottom.Model, Does.StartWith(maker == "monta" ? "monta_fg4_" : "sessellift_fgq4_"), "drawn with the maker's quad");
                    Assert.That(type.GaugeMetres, Is.EqualTo(4.12), "the models' gauge (LT12)");
                }
            Assert.That(LiftCatalogIds.Known("detach-quad:acme"), Is.False);
            Assert.That(LiftCatalogIds.Known("chair-lift"), Is.False);
        }

        [Test]
        public void LC08_ThePlaceholdersGiveTypicalCapacities()
        {
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Capacity, Is.EqualTo(2400).Within(1));
            Assert.That(LiftType.Of("fixed-double:sessellift").Capacity, Is.EqualTo(1200).Within(1));
            Assert.That(LiftType.Of("detach-six:monta").Capacity, Is.EqualTo(3000).Within(1), "LT41: 7.2 s, 120 ft");
            Assert.That(LiftType.Of("detach-quad:monta").Capacity, Is.EqualTo(2400).Within(1), "LT41: 6 s, 100 ft");
            Assert.That(LiftType.Of("detach-eight:monta").Capacity, Is.EqualTo(3600).Within(1), "LT41: 8 s");
            Assert.That(LiftType.Of("detach-six:monta").SpeedMetresPerSecond, Is.EqualTo(1000 * Ft / 60).Within(1e-9));
            Assert.That(LiftType.Of("gondola-8:sessellift").Capacity, Is.EqualTo(3000).Within(1));
            Assert.That(LiftType.Of("tram-100:sessellift").Capacity, Is.EqualTo(750), "a tram's own (one cabin each way)");
        }

        [Test]
        public void TW02_TramsStandOnTowersUpTo250Feet()
        {
            Assert.That(LiftType.Of("tram-60:monta").TowerMaxMetres, Is.EqualTo(250 * Ft).Within(1e-9));
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).TowerMaxMetres, Is.EqualTo(100 * Ft).Within(1e-9));
            Assert.That(DesignRules.TowerHeightProblem(Ft(200), false, LiftCatalogIds.TowerMaxMm("tram-60:monta")), Is.Null);
            Assert.That(DesignRules.TowerHeightProblem(Ft(200)), Is.Not.Null, "100 ft for everything else");
        }

        [Test]
        public void LT41_OnlyTypesWithTheirOwnModelsCanBeAdded()
        {
            var h = new DesignHistory(DesignDocument.Empty(4));
            var tram = h.Submit(new AddLift(h.Revision, "Tram", "tram-100:sessellift", P(0, 0), P(0, 6000), TowerMode.ByHand, TowerStance.Plumb,
                                            new[] { Tower(3000, 200) }, WorkStage.Built));
            Assert.That(tram.Accepted, Is.False, "a menu placeholder (owner, LT41)");
            Assert.That(tram.Reason, Does.Contain("can't be built yet"));
            foreach (string id in new[] { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.MontaFg4, LiftCatalogIds.SesselliftD6 })
            {
                var r = h.Submit(new AddLift(h.Revision, id, id, P(0, 0), P(0, 3000), TowerMode.Automatic, TowerStance.Plumb, null, WorkStage.Plan));
                Assert.That(r.Accepted, Is.True, id + ": " + r);
            }
        }

        [Test]
        public void LT41_TheD6IsADetachableSixPack()
        {
            var d6 = LiftType.Of(LiftCatalogIds.SesselliftD6);
            Assert.That(d6.Detachable && d6.Buildable, Is.True);
            Assert.That(d6.Seats, Is.EqualTo(6));
            Assert.That(d6.SpeedMetresPerSecond, Is.EqualTo(1000 * Ft / 60).Within(1e-9));
            Assert.That(d6.IntervalSeconds, Is.EqualTo(7.2).Within(1e-9));
            Assert.That(d6.ChairSpacingMetres / Ft, Is.EqualTo(120).Within(1e-6), "G4: 120 ft");
            Assert.That(d6.Capacity, Is.EqualTo(3000).Within(1e-6));
            Assert.That(d6.GaugeMetres, Is.EqualTo(5.6));
            Assert.That(d6.ChairDropMetres, Is.EqualTo(3.27), "G3");
            Assert.That(d6.RopeDiameterMetres, Is.EqualTo(0.0508));
            Assert.That(d6.Bottom.Model, Is.EqualTo("sessellift_d6_return"));
            Assert.That(d6.Top.Model, Is.EqualTo("sessellift_d6_drive"));
            // Roughly twice a fixed grip's spacing (owner): 120 ft against the quad's 45.
            Assert.That(d6.ChairSpacingMetres / LiftType.Of(LiftCatalogIds.SesselliftFgq4).ChairSpacingMetres, Is.InRange(2.0, 3.0));
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Detachable, Is.False);
        }

        [Test]
        public void LT41_StationSpeedsAndSpacingFollowTheLineSpeed()
        {
            var d6 = LiftType.Of(LiftCatalogIds.SesselliftD6);
            double v = d6.SpeedMetresPerSecond;
            Assert.That(d6.BoardSpeed(v), Is.EqualTo(1.0).Within(0.01), "boarding about 1 m/s at 1,000 fpm");
            Assert.That(d6.AlightSpeed(v), Is.EqualTo(1.4).Within(0.01), "deboarding about 1.4 m/s");
            // A faster line (a future slider): loading speeds up with it, within its band; the spacing grows; capacity holds.
            Assert.That(d6.BoardSpeed(1.2 * v), Is.EqualTo(1.2).Within(0.01));
            Assert.That(d6.BoardSpeed(2 * v), Is.EqualTo(LiftType.BoardMax), "never past the band");
            Assert.That(d6.BoardSpeed(0.5 * v), Is.EqualTo(LiftType.BoardMin));
            Assert.That(d6.LineSpacing(2 * v), Is.EqualTo(2 * d6.LineSpacing(v)).Within(1e-9));
            Assert.That(d6.Seats * 3600 / d6.IntervalSeconds, Is.EqualTo(d6.Capacity).Within(1e-6));
            // The ramps (finding E7): about 8 m from line speed down to boarding at 1.5 m/s².
            Assert.That(LiftType.RampLength(v, d6.BoardSpeed(v)), Is.EqualTo(8.3).Within(0.2));
            // Round the bullwheel slower still (E7): 0.55 m/s, scaling with the line within its band.
            Assert.That(d6.TurnSpeed(v), Is.EqualTo(0.55).Within(0.01));
            Assert.That(d6.TurnSpeed(0.5 * v), Is.EqualTo(LiftType.TurnMin));
            Assert.That(d6.TurnSpeed(2 * v), Is.EqualTo(LiftType.TurnMax));
            Assert.That(LiftType.TurnMin * d6.IntervalSeconds, Is.GreaterThanOrEqualTo(3.5), "never closer than G4's 3.5 m");
            // The ride: the line at its speed, plus a second or so for each ramp.
            double ride = d6.RideSeconds(600);
            Assert.That(ride - 600 / v, Is.InRange(1.5, 4.0));
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).RideSeconds(600), Is.EqualTo(600 / LiftType.Of(LiftCatalogIds.SesselliftFgq4).SpeedMetresPerSecond));
        }

        [TestCase(LiftCatalogIds.SesselliftFgq4, 2200)]
        [TestCase("fixed-double:monta", 2800)]
        [TestCase("detach-quad:sessellift", 3600)]
        [TestCase("gondola-10:monta", 4500)]
        public void LC07_EachTypeHasItsRiseLimit(string id, double feet)
        {
            Assert.That(LiftType.Of(id).RiseLimitMetres, Is.EqualTo(feet * Ft).Within(1e-9));
        }

        [Test]
        public void LC07_TooMuchRiseBlocksAndTensionNoLongerDoes()
        {
            // A straight 50% line: 1,900 ft of rise passes the quad's 2,200 ft (its top tension well past the rope's rating,
            // which is shown, not blocking); 2,400 ft doesn't; a tram has no limit.
            double bx = West + 100, by = North - 1500;
            Func<double, double> f = s => 0.5 * s;
            LiftBuild.Result Build(string id, double lengthFt) =>
                LiftBuild.Build(LiftType.Of(id), new EditedHeights(new Line(f), Lattice, TerrainEdits.Empty), bx, by, bx, by + lengthFt * Ft, TowerStance.Leaned);
            var ok = Build(LiftCatalogIds.SesselliftFgq4, 3800);
            Assert.That(ok.Rope.TensionOk, Is.False, "past the old RP-05 rating");
            Assert.That(ok.Problems.Where(p => p.Rule == "LC-07"), Is.Empty, string.Join("; ", ok.Problems));
            var over = Build(LiftCatalogIds.SesselliftFgq4, 4800);
            var rise = over.Problems.Single(p => p.Rule == "LC-07");
            Assert.That(rise.Blocks, Is.True);
            Assert.That(rise.Message, Does.StartWith("Too much rise for a fixed-grip quad"));
            Assert.That(over.Ok, Is.False);
            Assert.That(Build("tram-100:monta", 4800).Problems.Where(p => p.Rule == "LC-07"), Is.Empty, "a tram has no limit");
        }

        [Test]
        public void LC09_HeadLoadsAndStationAnglesAreAmberNotRed()
        {
            // A rope with a too-sharp head and a station angle out of range: amber, so the lift can still be built.
            var rope = LiftRope.Solve(LiftType.Of(LiftCatalogIds.SesselliftFgq4),
                new EditedHeights(new Line(s => s < 300 ? 0 : (s - 300) * 0.9), Lattice, TerrainEdits.Empty),
                West + 100, North - 1500, West + 100, North - 1000, new[] { new Tower(TowerId.Make(1, 0), 300_000, Ft(100)) }, TowerStance.Plumb,
                7.88, 1503.5, 7.0, 1500 + 180 + 3.0);
            var problems = LiftProblems.For(rope);
            foreach (var p in problems)
                if (p.Rule == "TW-05" || p.Rule == "TW-06" || p.Rule == "TW-16" || p.Rule == "LF-08" || p.Rule == "LF-12")
                    Assert.That(p.Blocks, Is.False, p.ToString());
                else
                    Assert.That(p.Rule, Is.EqualTo("TW-01").Or.EqualTo("LC-07"), p.ToString());
        }
    }
}
