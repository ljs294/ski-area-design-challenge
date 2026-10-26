using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10: connectivity and the warnings (NW-03, NW-12, NT6) and segment ratings (NW-08, NT9).
    public sealed class NetworkReportTests
    {
        static readonly EarthworkSite Site = new EarthworkSite(new EditLattice(-5000, 5000), 0, 0);

        [Test]
        public void NW03_NW12_TheTestResortFlagsTheDisconnectedLift()
        {
            var doc = NetworkGeometryTests.TestResort(out var chair1, out var chair2, out var orphan);
            var report = NetworkReport.Build(doc, new TrailNetworkGeometry(Site));
            Assert.That(report.Of(chair1).Connected, Is.True, "Home Run reaches Chair 1's bottom");
            Assert.That(report.Of(chair2).Connected, Is.False);
            Assert.That(report.Of(chair2).Warning, Is.EqualTo(NetworkWarning.NoWayDown));
            Assert.That(NetworkReport.Text(report.Of(chair2).Warning), Is.EqualTo("No way down"));
            Assert.That(report.LiftWarnings, Is.EqualTo(1));

            TrailStatusOf(doc, report, "Dead End", out var deadEnd);
            Assert.That(deadEnd.Served, Is.True, "Chair 2's top serves it");
            Assert.That(deadEnd.Warning, Is.EqualTo(NetworkWarning.LeadsNowhere), "NT6: its tail joins nothing");
            Assert.That(report.Of(orphan).Warning, Is.EqualTo(NetworkWarning.NoLiftServes));
            TrailStatusOf(doc, report, "Spur", out var spur);
            Assert.That(spur.Warning, Is.EqualTo(NetworkWarning.None), "off Home Run, down to Chair 1's bottom");
            Assert.That(report.Of(chair1).Reaches.Select(t => doc.FindTrail(t).Name), Is.EquivalentTo(new[] { "Home Run", "Spur" }));
        }

        static void TrailStatusOf(DesignDocument doc, NetworkReport report, string name, out NetworkReport.TrailStatus status) =>
            status = report.Of(doc.Trails.Single(t => t.Name == name).Id);

        [Test]
        public void NW03_ALiftIsConnectedThroughAPath()
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            var chair = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            var other = AddLift(h, "Other", P(1000, 0), P(1000, 3000));
            // Down from Other's top, then across a 20 ft gap onto a run that ends at Chair's bottom.
            AddTrail(h, "From Other", P(1010, 2990), P(130, 1500));
            AddTrail(h, "To Chair", P(20, 2000), P(20, 10));
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(report.Of(other).Connected, Is.True, "PA-05: the path carries skiers across");
            Assert.That(report.Of(chair).Connected, Is.False, "nothing leaves Chair's top");
        }

        [Test]
        public void NW03_ALiftsOwnBottomCounts()
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            var chair = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            var run = AddTrail(h, "Run", P(20, 2990), P(20, 10));
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(report.Of(chair).Connected, Is.True);
            Assert.That(report.Of(run).Warning, Is.EqualTo(NetworkWarning.None));

            h.Submit(new DeleteTrail(h.Revision, run));
            Assert.That(NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site)).Of(chair).Connected, Is.False, "a first lift alone has no way down");
        }

        [Test]
        public void NW12_PlansAreLeftOut()
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            var chair = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            AddTrail(h, "Planned", P(20, 2990), P(20, 10), WorkStage.Plan);
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(report.Of(chair).Connected, Is.False);
            Assert.That(report.Trails.Count, Is.EqualTo(0));
        }

        [Test]
        public void NW08_NT9_ASegmentIsRatedOnItsOwnStations()
        {
            // Ten stations 25 ft apart: gentle (10%) for the first half, steep (50%) after.
            var along = Enumerable.Range(0, 10).Select(k => k * 7.62).ToArray();
            var slope = Enumerable.Range(0, 10).Select(k => k < 5 ? 10.0 : 50.0).ToArray();
            double upper = NetworkReport.SegmentRating(slope, along, 0, (int)(4 * 7620));
            double lower = NetworkReport.SegmentRating(slope, along, (int)(5 * 7620), (int)(9 * 7620));
            Assert.That(upper, Is.EqualTo(10).Within(1e-9));
            Assert.That(lower, Is.EqualTo(50).Within(1e-9));
            double whole = NetworkReport.SegmentRating(slope, along, 0, (int)(9 * 7620));
            Assert.That(whole, Is.GreaterThan(upper).And.LessThan(lower));
            Assert.That(double.IsNaN(NetworkReport.SegmentRating(slope, along, 100_000, 200_000)), Is.True, "no station there");
        }
    }
}
