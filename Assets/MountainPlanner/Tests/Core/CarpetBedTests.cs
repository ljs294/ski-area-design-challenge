using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // The Glidepath magic carpets in the game (surface lifts PR B): their ids and numbers, and the bed (riding the snow room
    // over the ground, owner 2026-11-06; C12's turns, C13's approaches, fill only, the limits), laid on made-up ground.
    public sealed class CarpetBedTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);

        /// <summary>A profile along a line due north from (100, 900).</summary>
        sealed class Profile : IHeightSource
        {
            readonly Func<double, double> _f;
            public Profile(Func<double, double> f) { _f = f; }
            public double At(double x, double y) => 1500 + _f(y - (North - 900));
        }

        static EditedHeights Ground(Func<double, double> f) => new EditedHeights(new Profile(f), Lattice, TerrainEdits.Empty);

        static CarpetBed.Result Lay(string id, Func<double, double> f, double length) =>
            CarpetBed.Build(LiftType.Of(id), Ground(f), West + 100, North - 900, West + 100, North - 900 + length);

        /// <summary>Each module's angle, from one joint to the next.</summary>
        static double[] Angles(CarpetBed.Result b) =>
            Enumerable.Range(0, b.Joints.Count - 1).Select(k => Math.Atan2(b.Joints[k + 1].y - b.Joints[k].y, b.Joints[k + 1].s - b.Joints[k].s)).ToArray();

        /// <summary>C12: every turn within 1°, the stations' included; each station pitched within the end limit.</summary>
        static void AssertC12(CarpetBed.Result b)
        {
            Assert.That(Math.Abs(b.BottomPitch), Is.LessThanOrEqualTo(CarpetBed.EndGradeLimit + 1e-9), "the bottom's pitch");
            Assert.That(Math.Abs(b.TopPitch), Is.LessThanOrEqualTo(CarpetBed.EndGradeLimit + 1e-9), "the top's pitch");
            var a = Angles(b);
            double prev = Math.Atan(b.BottomPitch);
            foreach (double x in a.Append(Math.Atan(b.TopPitch)))
            {
                Assert.That(Math.Abs(x - prev), Is.LessThanOrEqualTo(CarpetBed.BendPerJoint + 1e-4), "a turn over 1°");
                prev = x;
            }
        }

        /// <summary>Owner 2026-11-06: the carpet's foot at least the snow room over the ground everywhere under it (the deck
        /// across its width, the stations across theirs).</summary>
        static void AssertAboveGround(CarpetBed.Result b, IHeightSource ground, LiftType t, double slack = 0.003, double inset = 0.1)
        {
            // Inside the plates' outer edges: the approaches are filled to the plates' tops less the room, so skiers step on.
            for (double s = -t.Bottom.Behind + inset; s <= b.TopOriginS + t.Top.Behind - inset; s += 0.2)
            {
                bool station = s < t.Bottom.Ahead || s > b.TopOriginS - t.Top.Ahead;
                double w = station ? t.Bottom.HalfWidth : t.DeckHalfWidthMetres;
                for (double d = -w; d <= w + 1e-9; d += w / 2)
                    Assert.That(b.BaseAt(s) - ground.At(West + 100 + d, North - 900 + s), Is.GreaterThanOrEqualTo(CarpetBed.SnowRoomMetres - slack),
                                $"the snow room at s {s:F1}, d {d:F2}");
            }
        }

        /// <summary>Owner 2026-11-06: the ground is never cut, at every post the earthwork reaches.</summary>
        static void AssertNoCut(CarpetBed.Result b, Func<double, double> f)
        {
            var before = new Profile(f);
            var after = new EditedHeights(before, Lattice, b.Edits);
            for (int row = 840 - (int)b.TopOriginS; row <= 960; row++)
                for (int col = 60; col <= 140; col++)
                {
                    double x = Lattice.X(col), y = Lattice.Y(row);
                    Assert.That(after.At(x, y), Is.GreaterThanOrEqualTo(before.At(x, y) - 1e-9), $"cut at column {col}, row {row}");
                }
            Assert.That(b.CutCubicMetres, Is.EqualTo(0));
        }

        [Test]
        public void TheFourCarpetsAreBuildableBeltLifts()
        {
            var ids = new[] { LiftCatalogIds.GlidepathCarpet, LiftCatalogIds.GlidepathCarpetCovered, LiftCatalogIds.GlidepathCarpet2, LiftCatalogIds.GlidepathCarpet2Covered };
            foreach (string id in ids)
            {
                Assert.That(LiftCatalogIds.Known(id) && LiftCatalogIds.Buildable(id) && LiftCatalogIds.IsBelt(id), Is.True, id);
                Assert.That((LiftCatalogIds.Maker(id), LiftCatalogIds.Carrier(id)), Is.EqualTo(("glidepath", "carpet")), id);
                var t = LiftType.Of(id);
                Assert.That(t.Belt && !t.Tow, Is.True, id);
                Assert.That(t.Covered, Is.EqualTo(LiftCatalogIds.IsCovered(id)), id);
                Assert.That(t.SpeedMetresPerSecond, Is.EqualTo(160 * 0.3048 / 60).Within(0.001), "C11: 160 fpm");
                Assert.That(t.Capacity, Is.EqualTo(LiftCatalogIds.IsDoubleBelt(id) ? 3000 : 1500), id);
            }
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Belt, Is.False);
        }

        [TestCase(0.10, 60)]
        [TestCase(0.15, 120)]
        [TestCase(0.05, 250)]
        [TestCase(0.16, 60)]
        public void OnAnEvenPitchTheBedRidesTheSnowRoomOverIt(double grade, double length)
        {
            var t = LiftType.Of(LiftCatalogIds.GlidepathCarpet);
            Func<double, double> f = s => grade * s;
            var b = Lay(LiftCatalogIds.GlidepathCarpet, f, length);
            Assert.That(b.Ok, Is.True, string.Join("; ", b.Problems.Select(p => p.Message)));
            AssertC12(b);
            AssertAboveGround(b, new Profile(f), t);
            AssertNoCut(b, f);
            Assert.That(b.TopOriginS, Is.LessThanOrEqualTo(length + 1e-9).And.GreaterThan(length - 1.2), "the top within a metre of where it was placed");
            Assert.That(b.Modules.Count(m => m == CarpetBed.FillerMetres), Is.LessThanOrEqualTo(1));
            Assert.That(b.Grade, Is.EqualTo(grade).Within(0.01), "the modules on the hill's grade");
            // Little fill on an even hill; on one steeper than the stations may pitch, the ends ride up to ease into it.
            Assert.That(b.MostFillMetres, Is.LessThan(grade <= CarpetBed.EndGradeLimit + 0.02 ? 0.25 : 0.8), "the fill on an even hill");
        }

        [Test]
        public void OverAHumpTheBedRisesAndIntoAHollowItBendsAndIsFilled()
        {
            var t = LiftType.Of(LiftCatalogIds.GlidepathCarpet);
            Func<double, double> hump = s => 0.10 * s + 1.2 * Math.Exp(-Math.Pow((s - 40) / 6, 2));
            Func<double, double> hollow = s => 0.10 * s - 1.2 * Math.Exp(-Math.Pow((s - 40) / 6, 2));
            var over = Lay(LiftCatalogIds.GlidepathCarpet, hump, 80);
            var under = Lay(LiftCatalogIds.GlidepathCarpet, hollow, 80);
            AssertC12(over);
            AssertC12(under);
            AssertAboveGround(over, new Profile(hump), t);
            AssertAboveGround(under, new Profile(hollow), t);
            AssertNoCut(over, hump);
            AssertNoCut(under, hollow);
            var plain = Lay(LiftCatalogIds.GlidepathCarpet, s => 0.10 * s, 80);
            Assert.That(under.FillCubicMetres, Is.GreaterThan(plain.FillCubicMetres + 5), "the hollow filled up to the room under it");
            Assert.That(over.BaseAt(40), Is.GreaterThanOrEqualTo(1500 + hump(40) + CarpetBed.SnowRoomMetres - 0.003), "over the hump's top");
            // On the graded ground the room is still there under the deck, and no more than the room where it was filled.
            var after = new EditedHeights(new Profile(hollow), Lattice, under.Edits);
            AssertAboveGround(under, after, t, slack: 0.01, inset: 1.1);   // a lattice post in from the plates: the 1 m grid blends into the approach
            Assert.That(under.BaseAt(40) - after.At(West + 100, North - 900 + 40), Is.EqualTo(CarpetBed.SnowRoomMetres).Within(0.01), "filled up to the room");
            // And laid again over its own fill (what the view does), the same bed.
            var again = CarpetBed.Lay(t, after, West + 100, North - 900, West + 100, North - 900 + 80, under.BottomLevel, under.TopLevel);
            Assert.That(again.BaseAt(40), Is.EqualTo(under.BaseAt(40)).Within(0.01), "the view's bed on the fill");
        }

        [Test]
        public void TheBottomApproachIsFilledToTheRoomUnderItsPlate()
        {
            var t = LiftType.Of(LiftCatalogIds.GlidepathCarpet);
            Func<double, double> f = s => 0.08 * s;
            var b = Lay(LiftCatalogIds.GlidepathCarpet, f, 60);
            AssertNoCut(b, f);
            var after = new EditedHeights(new Profile(f), Lattice, b.Edits);
            double At(double s) => after.At(West + 100, North - 900 + s);
            // C13 at the bottom: the room under the plate's top for 16 ft before its edge (downhill, so filled).
            double edge = -t.Bottom.Behind;
            foreach (double s in new[] { edge - 1, edge - 3, edge - 4.5 })
                Assert.That(At(s), Is.EqualTo(b.BaseAt(edge) + t.PlateTopMetres - CarpetBed.SnowRoomMetres).Within(0.06), $"bottom approach at {s}");
        }

        [TestCase(0.30, 80, "CB-04")]
        [TestCase(0.05, 5, "CB-01")]
        [TestCase(0.20, 300, "CB-02")]
        public void PastTheLimitsTheCarpetIsRed(double grade, double length, string rule)
        {
            var b = Lay(LiftCatalogIds.GlidepathCarpet2, s => grade * s, length);
            Assert.That(b.Ok, Is.False, string.Join("; ", b.Problems.Select(p => p.Rule + " " + p.Message)) + $" grade {b.Grade:F3}");
            Assert.That(b.Problems.Any(p => p.Rule == rule && p.Blocks), Is.True, string.Join("; ", b.Problems.Select(p => p.Rule + " " + p.Message)));
        }

        [Test]
        public void SteepButWithinTheLimitIsAmber()
        {
            var b = Lay(LiftCatalogIds.GlidepathCarpet, s => 0.20 * s, 160);
            Assert.That(b.Ok, Is.True, string.Join("; ", b.Problems.Select(p => p.Message)));
            Assert.That(b.Problems.Any(p => p.Rule == "CB-04" && !p.Blocks), Is.True, $"grade {b.Grade:F3} avg {b.AverageGrade:F3}");
        }

        [Test]
        public void ADeepNarrowDipIsFilledAndAmber()
        {
            // A ditch 3 m deep and a few metres wide: the bed can't bend down into it at 1° a joint, so it rides over on fill.
            Func<double, double> ditch = s => 0.05 * s - 3 * Math.Exp(-Math.Pow((s - 40) / 3, 2));
            var b = Lay(LiftCatalogIds.GlidepathCarpet, ditch, 80);
            Assert.That(b.Ok, Is.True, string.Join("; ", b.Problems.Select(p => p.Message)));
            Assert.That(b.MostFillMetres, Is.GreaterThan(CarpetBed.DeepFillMetres));
            Assert.That(b.Problems.Any(p => p.Rule == "CB-05" && !p.Blocks), Is.True, string.Join("; ", b.Problems.Select(p => p.Message)));
            AssertNoCut(b, ditch);
        }

        [Test]
        public void AStationLevelSetByHandRaisesTheBedButNeverBuriesIt()
        {
            var t = LiftType.Of(LiftCatalogIds.GlidepathCarpet);
            Func<double, double> f = s => 0.10 * s;
            var low = Lay(LiftCatalogIds.GlidepathCarpet, f, 80);
            var high = CarpetBed.Build(t, Ground(f), West + 100, North - 900, West + 100, North - 900 + 80, low.BottomLevel + 0.5, low.TopLevel);
            Assert.That(high.BottomLevel, Is.EqualTo(low.BottomLevel + 0.5).Within(0.003));
            AssertC12(high);
            var under = CarpetBed.Build(t, Ground(f), West + 100, North - 900, West + 100, North - 900 + 80, low.BottomLevel - 0.5, low.TopLevel);
            Assert.That(under.BottomLevel, Is.EqualTo(low.BottomLevel).Within(0.003), "never lower than the snow room allows");
        }

        [Test]
        public void TheSameLineLaysTheSameBed()
        {
            Func<double, double> f = s => 0.12 * s + 0.6 * Math.Sin(s / 9);
            var a = Lay(LiftCatalogIds.GlidepathCarpet2Covered, f, 140);
            var b = Lay(LiftCatalogIds.GlidepathCarpet2Covered, f, 140);
            Assert.That(TerrainEditBytes.Encode(a.Edits), Is.EqualTo(TerrainEditBytes.Encode(b.Edits)));
            Assert.That(a.Joints, Is.EqualTo(b.Joints));
            AssertC12(a);
            AssertAboveGround(a, new Profile(f), LiftType.Of(LiftCatalogIds.GlidepathCarpet2Covered));
            AssertNoCut(a, f);
        }

        [Test]
        public void ACarpetBuildsWithNoTowersSavesAndReplaysToTheSameBytes()
        {
            // The game's one build path (LiftBuild): a belt lays its bed, stores its levels and no towers, and the replay of the
            // saved design (LiftReplay, Remove and Edit lift's ground) gives the build's bytes.
            Func<double, double> f = s => 0.09 * s + 0.4 * Math.Sin(s / 7);
            var type = LiftType.Of(LiftCatalogIds.GlidepathCarpetCovered);
            var b = LiftBuild.Build(type, Ground(f), West + 100, North - 900, West + 100, North - 900 + 90, TowerStance.Plumb);
            Assert.That(b.Ok, Is.True, string.Join("; ", b.Problems.Select(p => p.Message)));
            Assert.That((b.Rope, b.Towers.Count, b.Bed != null), Is.EqualTo(((LiftRope)null, 0, true)));
            var h = new DesignHistory(DesignDocument.Empty(3));
            var r = h.Submit(new AddLift(h.Revision, "Carpet 1", type.CatalogId, new PointMm(0, 0), new PointMm(0, 90_000), TowerMode.Automatic,
                                         TowerStance.Plumb, b.Towers, WorkStage.Built, b.Levels), b.Edits);
            Assert.That(r.Accepted, Is.True, r.Reason);
            var replayed = LiftReplay.Rebuild(new Profile(f), Lattice, h.Document, p => (West + 100 + p.XMetres, North - 900 + p.YMetres));
            Assert.That(TerrainEditBytes.Encode(replayed), Is.EqualTo(TerrainEditBytes.Encode(b.Edits)));
            var edit = LiftBuild.BuildWith(type, Ground(f), West + 100, North - 900, West + 100, North - 900 + 90, Array.Empty<TowerSpec>(),
                                           TowerStance.Plumb, b.Levels);
            Assert.That(TerrainEditBytes.Encode(edit.Edits), Is.EqualTo(TerrainEditBytes.Encode(b.Edits)), "Edit lift's build on the stored levels");
        }
    }
}
