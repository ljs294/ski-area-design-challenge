using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // CL-10 (owner, 2026-11-02): a tree whose crown reaches a lift's chair lane is cut, the lane per ANSI B77.1 (the chairs
    // swung 15° plus 5 ft); CL-03 v2: the ground corridor is 70 ft.
    public sealed class LiftLanesTests
    {
        const double Ft = 0.3048;

        [Test]
        public void TheQuadsLaneIsAboutSeventeenAndAHalfFeetEitherSide()
        {
            double half = LiftLanes.HalfWidth(LiftType.Of(LiftCatalogIds.SesselliftFgq4)) / Ft;
            Assert.That(half, Is.InRange(16.5, 18.5));   // 6.75 + 3.4 + 2.3 + 5 ft
            Assert.That(LiftClearing.CorridorMetres / Ft, Is.EqualTo(70).Within(0.01));
            // The lane sits inside the ground corridor: only crowns reaching in from beyond it are cut by it.
            Assert.That(half * Ft, Is.LessThan(LiftClearing.CorridorMetres / 2));
        }

        [Test]
        public void ACrownReachingTheLaneIsCutAndOneShortOfItStays()
        {
            var site = new EarthworkSite(new Domain.Edits.EditLattice(0, 1000), 500, 500);
            var empty = DesignDocument.Empty(1);
            var added = new AddLift(new DesignHistory(empty).Revision, "Test", LiftCatalogIds.SesselliftFgq4, PointMm.FromMetres(0, 0), PointMm.FromMetres(0, 400),
                                    TowerMode.Automatic, TowerStance.Plumb, null, WorkStage.Built).Apply(empty);
            Assert.That(added.Accepted, Is.True, added.Reason);
            var doc = added.Document;
            var lanes = LiftLanes.Of(doc, site);
            double half = LiftLanes.HalfWidth(LiftType.Of(LiftCatalogIds.SesselliftFgq4));
            // A trunk 12 m east of the line, half-way up.
            Assert.That(lanes.Reaches(500 + 12, 700, 12 - half + 0.2), Is.True);
            Assert.That(lanes.Reaches(500 + 12, 700, 12 - half - 0.2), Is.False);
            // Past the bullwheels by more than the crown: no lane there.
            Assert.That(lanes.Reaches(500, 500 - 8, 5), Is.False);
            Assert.That(LiftLanes.Same(lanes, LiftLanes.Of(doc, site)), Is.True);
            Assert.That(LiftLanes.Same(lanes, LiftLanes.Of(DesignDocument.Empty(1), site)), Is.False);
        }
    }
}
