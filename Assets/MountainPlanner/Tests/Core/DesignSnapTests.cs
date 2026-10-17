using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    /// <summary>Snapping for the construction tools (task P3-06; TR-05, NW-07, SH-04).</summary>
    public class DesignSnapTests
    {
        static DesignHistory Resort(out LiftId chair, out TrailId run)
        {
            var h = new DesignHistory(DesignDocument.Empty(0x5EED_0006));
            chair = AddLift(h, "Chair 1", P(0, 0), P(0, 2000));
            run = AddTrail(h, "Run 1", P(40, 2000), P(500, 0));
            return h;
        }

        [Test]
        public void TR05_EndsWithin30FtSnapToTheNearestTarget()
        {
            var h = Resort(out var chair, out var run);
            var hit = DesignSnap.Nearest(h.Document, P(10, 2010));
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.LiftTop), "the lift top is nearer than the trail head");
            Assert.That(hit.At, Is.EqualTo(P(0, 2000)));
            Assert.That(hit.Owner, Is.EqualTo(chair.Value));

            hit = DesignSnap.Nearest(h.Document, P(495, 20));
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.TrailTail));
            Assert.That(hit.Owner, Is.EqualTo(run.Value));
        }

        [Test]
        public void TR05_NothingBeyond30Ft()
        {
            var h = Resort(out _, out _);
            Assert.That(DesignSnap.Nearest(h.Document, P(0, 31)).Snapped, Is.False, "31 ft from the bottom terminal");
            var edge = DesignSnap.Nearest(h.Document, P(0, 30));
            Assert.That(edge.Kind, Is.EqualTo(SnapKind.LiftBottom), "exactly 30 ft still snaps");
            var miss = DesignSnap.Nearest(h.Document, P(1000, 1000));
            Assert.That(miss.Snapped, Is.False);
            Assert.That(miss.At, Is.EqualTo(P(1000, 1000)), "an unsnapped hit keeps the pointer's point");
        }

        [Test]
        public void TargetsAndSkipFilterTheCandidates()
        {
            var h = Resort(out var chair, out var run);
            Assert.That(DesignSnap.Nearest(h.Document, P(25, 2005), SnapTargets.TrailEnds).Kind, Is.EqualTo(SnapKind.TrailHead));
            Assert.That(DesignSnap.Nearest(h.Document, P(25, 2005), skip: chair.Value).Kind, Is.EqualTo(SnapKind.TrailHead),
                        "a trail being edited doesn't snap to itself; nor does a lift");
            Assert.That(DesignSnap.Nearest(h.Document, P(40, 2000), SnapTargets.LiftEnds, skip: chair.Value).Snapped, Is.False);
        }

        sealed class StraightLines : ICentrelineSnap
        {
            // Each trail's centreline is the straight head–tail line (a stand-in for task 08's geometry).
            public bool Nearest(Trail trail, PointMm from, out PointMm on)
            {
                on = default;
                if (!trail.HasEnds) return false;
                var a = trail.Head.Value; var b = trail.Tail.Value;
                double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
                double t = len == 0 ? 0 : ((from.X - a.X) * dx + (from.Y - a.Y) * dy) / len;
                t = System.Math.Max(0, System.Math.Min(1, t));
                on = new PointMm((int)System.Math.Round(a.X + t * dx), (int)System.Math.Round(a.Y + t * dy));
                return true;
            }
        }

        [Test]
        public void NW07_PointsOnAnotherTrailSnapThroughTheCentrelineHook()
        {
            var h = Resort(out _, out var run);
            var mid = P(270, 1000);
            Assert.That(DesignSnap.Nearest(h.Document, P(290, 1000)).Snapped, Is.False, "no hook, no line snap");
            var hit = DesignSnap.Nearest(h.Document, P(290, 1000), lines: new StraightLines());
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.TrailLine));
            Assert.That(hit.Owner, Is.EqualTo(run.Value));
            Assert.That(hit.At.Within(mid, Ft(20)), Is.True);
        }

        [Test]
        public void SH04_TiesBreakTheSameWayEveryTime()
        {
            var h = new DesignHistory(DesignDocument.Empty(0x5EED_0007));
            var a = AddLift(h, "A", P(0, 0), P(0, 2000));
            var b = AddLift(h, "B", P(40, 0), P(40, 2000));
            // Halfway between A's and B's bottoms: the lower ID wins, whichever was added first.
            var hit = DesignSnap.Nearest(h.Document, P(20, 0));
            Assert.That(hit.Owner, Is.EqualTo(System.Math.Min(a.Value, b.Value)));
            // An end beats a line at the same distance.
            var t = AddTrail(h, "Run", P(20, 2000), P(20, 30));
            hit = DesignSnap.Nearest(h.Document, P(20, 30), lines: new StraightLines());
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.TrailTail));
            Assert.That(hit.Owner, Is.EqualTo(t.Value));
        }
    }
}
