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

        /// <summary>Chair with Main down it, and Spur from the woods joining Main halfway down.</summary>
        static DesignDocument SpurOntoMain(out LiftId chair, out TrailId main, out TrailId spur)
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            chair = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            main = AddTrail(h, "Main", P(20, 2990), P(20, 10));
            var r = h.Submit(new AddTrail(h.Revision, "Spur", TrailSettings.GradedRun, Straight(P(1500, 3000), P(25, 1500), 60),
                                          P(1500, 3000), P(25, 1500), WorkStage.Built));
            Assert.That(r.Accepted, Is.True, r.ToString());
            spur = new TrailId(r.Created[0]);
            return h.Document;
        }

        [Test]
        public void NW13_Finding4_ATrailMetPartwayIsReachedOnlyBelowTheMeeting()
        {
            var doc = SpurOntoMain(out _, out var main, out var spur);
            var report = NetworkReport.Build(doc, new TrailNetworkGeometry(Site));
            var reach = report.ReachFrom(spur);
            var edges = report.Graph.Edges;
            var mainSegments = Enumerable.Range(0, edges.Count).Where(i => edges[i].Kind == EdgeKind.TrailSegment && edges[i].Owner == main.Value).ToArray();
            Assert.That(mainSegments.Length, Is.EqualTo(2), "Main is cut where Spur joins");
            int upper = mainSegments.OrderBy(i => edges[i].StartMm).First(), lower = mainSegments.OrderBy(i => edges[i].StartMm).Last();
            Assert.That(reach.Segments, Has.Member(lower), "below the junction");
            Assert.That(reach.Segments, Has.No.Member(upper), "no skiing up Main from the junction");
            Assert.That(reach.Trails, Is.EquivalentTo(new[] { main, spur }));
        }

        [Test]
        public void NW13_Finding2_ATrailsReachGoesDownItNotDownItsNeighbours()
        {
            // Two runs leave Chair's top; picking one mustn't light the other.
            var h = new DesignHistory(DesignDocument.Empty(3));
            var chair = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            // 25 ft wide, their heads 56 ft apart and running apart: no overlap and no path between them
            TrailId Narrow(string name, PointMm head, PointMm tail)
            {
                var r = h.Submit(new AddTrail(h.Revision, name, TrailSettings.GradedRun, Straight(head, tail, 25), head, tail, WorkStage.Built));
                Assert.That(r.Accepted, Is.True, r.ToString());
                return new TrailId(r.Created[0]);
            }
            var east = Narrow("East", P(28, 2995), P(1500, 2000));
            var west = Narrow("West", P(-28, 2995), P(-1500, 2000));
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(report.ReachFrom(east).Trails, Is.EqualTo(new[] { east }));
            Assert.That(report.ReachFrom(west).Trails, Is.EqualTo(new[] { west }));
            Assert.That(report.ReachFrom(chair).Trails, Is.EquivalentTo(new[] { east, west }), "the lift's top reaches both");
        }

        [Test]
        public void NW13_R2_2_APickedSegmentReachesNothingAboveIt()
        {
            // Main from Chair's top to its bottom; Branch leaves Main halfway down and ends in the woods.
            var h = new DesignHistory(DesignDocument.Empty(3));
            var chair = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            var main = AddTrail(h, "Main", P(20, 2990), P(20, 10));
            var r = h.Submit(new AddTrail(h.Revision, "Branch", TrailSettings.GradedRun, Straight(P(25, 1500), P(1500, 0), 60),
                                          P(25, 1500), P(1500, 0), WorkStage.Built));
            Assert.That(r.Accepted, Is.True, r.ToString());
            var branch = new TrailId(r.Created[0]);
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));

            int upper = report.SegmentAt(main, Ft(500)), lower = report.SegmentAt(main, Ft(2500));
            Assert.That(upper, Is.Not.EqualTo(lower), "Main is cut where Branch leaves");
            var fromUpper = report.ReachFromSegment(upper);
            Assert.That(fromUpper.Trails, Is.EquivalentTo(new[] { main, branch }), "the upper stretch passes the branch");

            var fromLower = report.ReachFromSegment(lower);
            Assert.That(fromLower.Segments, Is.EqualTo(new[] { lower }), "below the branch: only itself");
            Assert.That(fromLower.Trails, Is.EqualTo(new[] { main }), "R2-2: nothing above its top node");
            Assert.That(fromLower.Lifts, Is.EqualTo(new[] { chair }));
            Assert.That(report.ReachFromSegment(-1).Nodes, Is.Empty);
        }

        [Test]
        public void NW13_Finding5_ATrailReachesTheLiftAtItsBottomButDoesntRideIt()
        {
            var doc = SpurOntoMain(out var chair, out var main, out var spur);
            var report = NetworkReport.Build(doc, new TrailNetworkGeometry(Site));
            var reach = report.ReachFrom(spur);
            Assert.That(reach.Lifts, Is.EqualTo(new[] { chair }), "Main ends at Chair's bottom");
            var upper = report.Graph.Edges.Select((e, i) => (e, i)).Where(x => x.e.Kind == EdgeKind.TrailSegment && x.e.Owner == main.Value)
                .OrderBy(x => x.e.StartMm).First().i;
            Assert.That(reach.Segments, Has.No.Member(upper), "NT10: Chair's top isn't reached by riding it");
            Assert.That(report.ReachFrom(chair).Lifts, Is.EqualTo(new[] { chair }), "a lift's own bottom counts (NW-03)");
        }

        [Test]
        public void NW13_Finding7_AnyNodeCanBeTheStart()
        {
            var doc = SpurOntoMain(out var chair, out var main, out _);
            var report = NetworkReport.Build(doc, new TrailNetworkGeometry(Site));
            var junction = report.Graph.Nodes.Single(n => n.Kind == NodeKind.Junction).Id;
            var fromJunction = report.ReachFrom(junction);
            Assert.That(fromJunction.Trails, Is.EqualTo(new[] { main }));
            Assert.That(fromJunction.Lifts, Is.EqualTo(new[] { chair }));
            Assert.That(fromJunction.Nodes, Has.Member(junction), "the start is reached");

            var fromBottom = report.ReachFrom(DesignNetwork.LiftBottomNode(doc.Seed, chair));
            Assert.That(fromBottom.Segments, Is.Empty, "nothing runs downhill from a lift's bottom");
            Assert.That(fromBottom.Lifts, Is.Empty);
            Assert.That(report.ReachFrom(12345UL).Nodes, Is.Empty, "a node not in the graph");
        }

        [Test]
        public void NW13_Finding7_APathEndReachesAcrossThePath()
        {
            var h = new DesignHistory(DesignDocument.Empty(3));
            var a = AddTrail(h, "A", P(0, 3000), P(0, 0));
            var b = AddTrail(h, "B", P(120, 3000), P(120, 0));   // a 20 ft gap
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            var ends = report.Graph.Nodes.Where(n => n.Kind == NodeKind.PathEnd).ToArray();
            Assert.That(ends.Length, Is.EqualTo(2));
            var reach = report.ReachFrom(ends[0].Id);
            Assert.That(reach.Nodes, Has.Member(ends[1].Id), "PA-06: level, so both ways");
            Assert.That(reach.Trails, Is.EquivalentTo(new[] { a, b }));
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
