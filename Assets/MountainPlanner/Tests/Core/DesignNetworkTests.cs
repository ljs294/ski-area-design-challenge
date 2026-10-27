using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // The derived network graph (task P3-03; NW-01, NW-02, NW-06 to NW-08, JN-04, TR-05, PA-06). Real centrelines,
    // overlaps and paths are task 08/10's geometry; a stub stands in for it here.
    public sealed class DesignNetworkTests
    {
        /// <summary>The scripted design's graph bytes with the stub geometry below. Pinned like the document golden.</summary>
        const string GoldenGraphSha = "5afff60b5267c580720fb16bd6ca567c728278c49ed24e45a2b1717df63af273";

        /// <summary>Straight-line centrelines, plus whatever junctions and paths a test lists (in any order).</summary>
        sealed class StubGeometry : ITrailGeometry
        {
            public readonly List<TrailJunction> Junctions = new List<TrailJunction>();
            public readonly List<TrailPath> Paths = new List<TrailPath>();
            public int CentrelineLengthMm(Trail t) => (int)System.Math.Round(System.Math.Sqrt(t.Head.Value.DistanceSquared(t.Tail.Value)));
            public void FindJunctions(DesignDocument doc, List<TrailJunction> into) => into.AddRange(Junctions);
            public void FindPaths(DesignDocument doc, List<TrailPath> into) => into.AddRange(Paths);
        }

        [Test]
        public void NW01_TheGraphIsRebuiltIdenticallyFromAnyLoad()
        {
            var doc = Scripted().Document;
            var a = DesignNetwork.Build(doc).Encode();
            var reloaded = DesignBytes.Decode(DesignBytes.Encode(doc));
            Assert.That(DesignNetwork.Build(reloaded).Encode(), Is.EqualTo(a));
            Assert.That(Sha(DesignNetwork.Build(doc, ScriptedGeometry(doc)).Encode()), Is.EqualTo(GoldenGraphSha), "golden graph bytes");
        }

        static StubGeometry ScriptedGeometry(DesignDocument doc)
        {
            var g = new StubGeometry();
            var byName = doc.Trails.ToDictionary(t => t.Name);
            var run2 = byName["Run 2"];
            var lower = byName["Lower Run 1"];
            g.Junctions.Add(new TrailJunction(run2.Id, Ft(800), lower.Id, Ft(300), P(1900, 1300)));
            g.Paths.Add(new TrailPath(lower.Id, Ft(900), P(800, 900), run2.Id, Ft(1500), P(830, 900), PathDirection.Both));
            return g;
        }

        [Test]
        public void NW02_LiftsGoUpAndPlansAreLeftOut()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var built = AddLift(h, "Chair 1", P(0, 0), P(0, 3000));
            AddLift(h, "Chair 2", P(500, 0), P(500, 3000), WorkStage.Plan);
            AddTrail(h, "Planned", P(0, 3000), P(0, 0), WorkStage.Plan);
            var g = DesignNetwork.Build(h.Document);
            Assert.That(g.Edges.Count, Is.EqualTo(1));
            var e = g.Edges[0];
            Assert.That(e.Kind, Is.EqualTo(EdgeKind.Lift));
            Assert.That(e.From, Is.EqualTo(g.StationOf(built, top: false)), "NW-02 v2: from the bottom's station");
            Assert.That(e.To, Is.EqualTo(g.StationOf(built, top: true)));
            Assert.That(g.Nodes.Count, Is.EqualTo(2));
            Assert.That(g.Nodes.All(n => n.Kind == NodeKind.Station), Is.True);
        }

        [Test]
        public void NW14_ATrailEndWithin100FtAttachesToTheStation()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var lift = AddLift(h, "Chair 1", P(0, 0), P(0, 3000));
            var near = AddTrail(h, "Near", P(100, 3000), P(0, 99));
            var far = AddTrail(h, "Far", P(101, 3000), P(300, 0));
            var g = DesignNetwork.Build(h.Document);
            var nearEdge = g.Edges.Single(e => e.Owner == near.Value);
            Assert.That(nearEdge.From, Is.EqualTo(g.StationOf(lift, top: true)), "NT13: 100 ft from the top: attached");
            Assert.That(nearEdge.To, Is.EqualTo(g.StationOf(lift, top: false)));
            var farEdge = g.Edges.Single(e => e.Owner == far.Value);
            Assert.That(g.TryGetNode(farEdge.From, out var n) && n.Kind == NodeKind.TrailHead, Is.True, "101 ft away: its own head");

            // Moving the lift re-derives the attachment (owner ruling P3-03 Q3): nothing stored needs fixing.
            h.Submit(new DeleteLift(h.Revision, lift));
            var after = DesignNetwork.Build(h.Document);
            Assert.That(after.Nodes.Count(x => x.Kind == NodeKind.TrailHead), Is.EqualTo(2));
        }

        [Test]
        public void NW08_TrailsAreCutIntoSegmentsAtJunctions()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var a = AddTrail(h, "A", P(0, 3000), P(0, 0));
            var b = AddTrail(h, "B", P(500, 2500), P(0, 1000)); // its tail lands on A
            var c = AddTrail(h, "C", P(30, 2000), P(30, 500));
            var g = new StubGeometry();
            int bLength = g.CentrelineLengthMm(h.Document.FindTrail(b));
            g.Junctions.Add(new TrailJunction(b, bLength, a, Ft(2000), P(0, 1000)));
            g.Paths.Add(new TrailPath(a, Ft(1500), P(0, 1500), c, Ft(500), P(30, 1500), PathDirection.Both));
            var graph = DesignNetwork.Build(h.Document, g);

            var aSegments = graph.Edges.Where(e => e.Owner == a.Value).OrderBy(e => e.StartMm).ToArray();
            Assert.That(aSegments.Select(e => (e.StartMm, e.EndMm)), Is.EqualTo(new[] { (0, Ft(2000)), (Ft(2000), Ft(3000)) }), "NT22: no cut at a path's end");
            var junction = DesignNetwork.JunctionNode(1, a, b, P(0, 1000));
            Assert.That(aSegments[0].To, Is.EqualTo(junction));
            var bSegments = graph.Edges.Where(e => e.Owner == b.Value).ToArray();
            Assert.That(bSegments.Length, Is.EqualTo(1));
            Assert.That(bSegments[0].To, Is.EqualTo(junction), "B's tail is the junction, not a node of its own");
            Assert.That(graph.Nodes.Count(n => n.Kind == NodeKind.TrailTail), Is.EqualTo(2), "A and C keep their tails");

            Assert.That(graph.Edges.Any(e => e.Kind == EdgeKind.Path), Is.False, "NT22: automatic paths are off, so the path found adds nothing");
            Assert.That(graph.Nodes.Any(n => n.Kind == NodeKind.PathEnd), Is.False);
        }

        [Test, Ignore("NT22: automatic paths are off for now")]
        public void PA06_APathRunsOneWayWhenItsEndsDiffer()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var a = AddTrail(h, "A", P(0, 3000), P(0, 0));
            var c = AddTrail(h, "C", P(30, 2000), P(30, 500));
            var g = new StubGeometry();
            // Reported from C's side: C to A downhill. The network normalises the order but keeps the direction.
            g.Paths.Add(new TrailPath(c, Ft(500), P(30, 1500), a, Ft(1500), P(0, 1500), PathDirection.AToB));
            var graph = DesignNetwork.Build(h.Document, g);
            var path = graph.Edges.Single(e => e.Kind == EdgeKind.Path);
            Assert.That(graph.TryGetNode(path.From, out var from), Is.True);
            Assert.That(from.At, Is.EqualTo(P(30, 1500)), "from C");
        }

        [Test]
        public void JN04_JunctionIdsAreKeyedByWhatTheyJoin()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var a = AddTrail(h, "A", P(0, 3000), P(0, 0));
            var b = AddTrail(h, "B", P(500, 2500), P(0, 1000));
            var forward = new StubGeometry();
            forward.Junctions.Add(new TrailJunction(a, Ft(2000), b, Ft(1581), P(0, 1000)));
            forward.Junctions.Add(new TrailJunction(a, Ft(2600), b, Ft(300), P(100, 2400)));
            var backward = new StubGeometry();
            backward.Junctions.Add(new TrailJunction(b, Ft(300), a, Ft(2600), P(100, 2400)));
            backward.Junctions.Add(new TrailJunction(b, Ft(1581), a, Ft(2000), P(0, 1000)));
            backward.Junctions.Add(new TrailJunction(b, Ft(1581), a, Ft(2000), P(0, 1000))); // reported twice

            var one = DesignNetwork.Build(h.Document, forward);
            var two = DesignNetwork.Build(h.Document, backward);
            Assert.That(two.Encode(), Is.EqualTo(one.Encode()), "the order geometry finds things in doesn't matter");
            Assert.That(DesignNetwork.JunctionNode(1, a, b, P(0, 1000)), Is.EqualTo(DesignNetwork.JunctionNode(1, b, a, P(0, 1000))));
            Assert.That(one.Nodes.Count(n => n.Kind == NodeKind.Junction), Is.EqualTo(2));

            // An unrelated edit elsewhere leaves the junction's ID as it was.
            AddTrail(h, "Elsewhere", P(3000, 3000), P(3000, 0));
            var three = DesignNetwork.Build(h.Document, forward);
            Assert.That(three.TryGetNode(DesignNetwork.JunctionNode(1, a, b, P(0, 1000)), out _), Is.True);
        }

        [Test]
        public void NW06_NothingDerivedIsStored()
        {
            // The network is a function of the document: deleting a trail removes its junctions with nothing to prune.
            var h = new DesignHistory(DesignDocument.Empty(1));
            var a = AddTrail(h, "A", P(0, 3000), P(0, 0));
            var b = AddTrail(h, "B", P(500, 2500), P(0, 1000));
            var g = new StubGeometry();
            g.Junctions.Add(new TrailJunction(a, Ft(2000), b, Ft(1581), P(0, 1000)));
            var before = DesignBytes.Encode(h.Document);
            DesignNetwork.Build(h.Document, g);
            Assert.That(DesignBytes.Encode(h.Document), Is.EqualTo(before), "building the network doesn't touch the document");
            h.Submit(new DeleteTrail(h.Revision, b));
            var after = DesignNetwork.Build(h.Document, g);
            Assert.That(after.Nodes.Any(n => n.Kind == NodeKind.Junction), Is.False);
            Assert.That(after.Edges.Single().Owner, Is.EqualTo(a.Value));
        }
    }
}
