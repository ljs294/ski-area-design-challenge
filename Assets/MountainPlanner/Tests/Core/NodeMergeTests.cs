using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10, finding S1 and owner decision NT42 (2026-10-27): nodes within 100 ft of each other are one, under the
    // hood: section ends for a trail's sections, and every node for the network. Never saved, drawn or named.
    public sealed class NodeMergeTests
    {
        sealed class StubGeometry : ITrailGeometry
        {
            public readonly List<TrailJunction> Junctions = new List<TrailJunction>();
            public int CentrelineLengthMm(Trail t) => (int)System.Math.Round(System.Math.Sqrt(t.Head.Value.DistanceSquared(t.Tail.Value)));
            public void FindJunctions(DesignDocument doc, List<TrailJunction> into) => into.AddRange(Junctions);
            public void FindPaths(DesignDocument doc, List<TrailPath> into) { }
        }

        static DesignHistory New() => new DesignHistory(DesignDocument.Empty(0x5EED_2026));

        [Test]
        public void NT42_EndsWithin100FtAreOneNodeAndChain()
        {
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "B", P(0, 1910), P(0, 1000));   // 90 ft below A's tail
            var nodes = TrailNodes.Of(h.Document);
            Assert.That(nodes.TailNode(a), Is.EqualTo(nodes.HeadNode(b)));
            Assert.That(nodes.CountOf(nodes.TailNode(a)), Is.EqualTo(2));
            Assert.That(nodes.PointOf(nodes.TailNode(a)), Is.EqualTo(P(0, 1955)), "drawn at the mean of its ends");
            var ts = Sections(h.Document);
            var route = TrailSectionsEdit.Route(ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]);
            Assert.That(route, Is.Not.Null, "B carries on from A at the shared node");
            var r = h.Submit(TrailSectionsEdit.Add(h.Document, h.Revision, ts, ts.TrailOf(a), route));
            Assert.That(r.Accepted, Is.True, r.ToString());
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(b), Is.SameAs(ts.TrailOf(a)));
        }

        [Test]
        public void NT42_EndsMoreThan100FtApartAreNot()
        {
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "B", P(0, 1890), P(0, 1000));   // 110 ft
            var nodes = TrailNodes.Of(h.Document);
            Assert.That(nodes.TailNode(a), Is.Not.EqualTo(nodes.HeadNode(b)));
            var ts = Sections(h.Document);
            Assert.That(TrailSectionsEdit.Route(ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]), Is.Null);
        }

        [Test]
        public void NT42_CloseEndsMergeInAChain()
        {
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "B", P(80, 2000), P(80, 1000));
            var c = AddTrail(h, "C", P(160, 2000), P(160, 1000));   // 160 ft from A's tail, 80 ft from B's head
            var nodes = TrailNodes.Of(h.Document);
            Assert.That(nodes.HeadNode(b), Is.EqualTo(nodes.TailNode(a)));
            Assert.That(nodes.HeadNode(c), Is.EqualTo(nodes.TailNode(a)));
        }

        [Test]
        public void NT42_AShortSectionKeepsBothItsEnds()
        {
            var h = New();
            var s = AddTrail(h, "Stub", P(0, 3000), P(0, 2950));   // 50 ft long
            var nodes = TrailNodes.Of(h.Document);
            Assert.That(nodes.HeadNode(s), Is.Not.EqualTo(nodes.TailNode(s)));
        }

        [Test]
        public void NT42_PlanAndBuiltEndsNeverMerge()
        {
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "B", P(0, 1950), P(0, 1000), WorkStage.Plan);
            var nodes = TrailNodes.Of(h.Document);
            Assert.That(nodes.TailNode(a), Is.Not.EqualTo(nodes.HeadNode(b)));
        }

        [Test]
        public void NT42_ALoneEndKeepsTheNetworksOwnKey()
        {
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var nodes = TrailNodes.Of(h.Document);
            Assert.That(nodes.HeadNode(a), Is.EqualTo(DesignKey.Hash(h.Document.Seed, DesignKey.TrailHeadTag, a.Value)));
            Assert.That(nodes.TailNode(a), Is.EqualTo(DesignKey.Hash(h.Document.Seed, DesignKey.TrailTailTag, a.Value)));
        }

        [Test]
        public void NT42_ATrailWhoseSectionsAre80FtApartLoadsWhole()
        {
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "B", P(0, 1920), P(0, 1000));
            var ts = Sections(h.Document);
            Assert.That(h.Submit(TrailSectionsEdit.Add(h.Document, h.Revision, ts, ts.TrailOf(a), TrailSectionsEdit.Route(ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]))).Accepted, Is.True);
            ts = Sections(h.Document);
            Assert.That(h.Submit(TrailSectionsEdit.Rename(h.Document, h.Revision, ts, ts.TrailOf(a), "Ridge")).Accepted, Is.True);
            var back = DesignBytes.Decode(DesignBytes.Encode(h.Document));
            var bt = Sections(back).TrailOf(a);
            Assert.That(bt.Segments.Select(s => s.Piece), Is.EqualTo(new[] { a, b }));
            Assert.That(bt.Name, Is.EqualTo("Ridge"), "one trail, not Upper and Lower");
        }

        [Test]
        public void NT42_TheNetworkJoinsACloseSeamSoNothingWarnsThere()
        {
            var h = New();
            var lift = AddLift(h, "Chair 1", P(0, 0), P(0, 3000));
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "B", P(0, 1920), P(0, 0));   // 80 ft below A's tail, down to the lift's bottom
            var g = DesignNetwork.Build(h.Document);
            var ea = g.Edges.Single(e => e.Owner == a.Value);
            var eb = g.Edges.Single(e => e.Owner == b.Value);
            Assert.That(ea.To, Is.EqualTo(eb.From), "one node at the seam");
            Assert.That(g.TryGetNode(ea.To, out var n) && n.Kind == NodeKind.Junction, Is.True);
            var report = NetworkReport.Of(h.Document, g);
            Assert.That(report.Trails.Single(t => t.Trail == a).Warning, Is.EqualTo(NetworkWarning.None));
            Assert.That(report.Trails.Single(t => t.Trail == b).Warning, Is.EqualTo(NetworkWarning.None));
            Assert.That(ea.From, Is.EqualTo(g.StationOf(lift, top: true)));
        }

        [Test]
        public void NT42_TwoJunctionsCloseTogetherAreOneNodeWithNoSliver()
        {
            var h = New();
            var run = AddTrail(h, "Run", P(0, 3000), P(0, 0));
            var b = AddTrail(h, "B", P(-500, 2100), P(500, 1900));
            var c = AddTrail(h, "C", P(-500, 2060), P(500, 1860));
            var geo = new StubGeometry();
            geo.Junctions.Add(new TrailJunction(run, Ft(1000), b, Ft(500), P(0, 2000)));
            geo.Junctions.Add(new TrailJunction(run, Ft(1040), c, Ft(500), P(0, 1960)));   // 40 ft further down the run
            var g = DesignNetwork.Build(h.Document, geo);
            var segments = g.Edges.Where(e => e.Owner == run.Value).OrderBy(e => e.StartMm).ToList();
            Assert.That(segments.Count, Is.EqualTo(2), "head to the merged junction, then on to the tail");
            Assert.That(g.Edges.Where(e => e.Owner == b.Value).Select(e => e.To).Concat(g.Edges.Where(e => e.Owner == b.Value).Select(e => e.From)),
                        Does.Contain(segments[0].To), "B crosses at the one node");
            Assert.That(g.Edges.Where(e => e.Owner == c.Value).Select(e => e.To).Concat(g.Edges.Where(e => e.Owner == c.Value).Select(e => e.From)),
                        Does.Contain(segments[0].To), "and so does C");
        }

        [Test]
        public void NT42_AJunctionNeverJoinsTwoEndNodes()
        {
            // A's tail and C's head are 150 ft apart: two end nodes. A junction halfway between them joins the nearer one
            // (or the first, on a tie), never both, so the sections and the network agree.
            var h = New();
            var a = AddTrail(h, "A", P(0, 3000), P(0, 2000));
            var c = AddTrail(h, "C", P(150, 2000), P(150, 1000));
            var d = AddTrail(h, "D", P(75, 2500), P(75, 1500));
            var geo = new StubGeometry();
            var e = AddTrail(h, "E", P(-200, 1990), P(300, 1990));
            geo.Junctions.Add(new TrailJunction(d, Ft(500), e, Ft(275), P(75, 2000)));
            var g = DesignNetwork.Build(h.Document, geo);
            ulong aTail = g.Edges.Single(x => x.Owner == a.Value).To, cHead = g.Edges.Single(x => x.Owner == c.Value).From;
            Assert.That(aTail, Is.Not.EqualTo(cHead));
        }

        [Test]
        public void NT42_StationsStillWin()
        {
            // B's head is 150 ft from the top terminal, but 60 ft from A's head, which is in the station: B's head is the
            // station too.
            var h = New();
            var lift = AddLift(h, "Chair 1", P(0, 0), P(0, 3000));
            var a = AddTrail(h, "A", P(90, 3000), P(90, 0));
            var b = AddTrail(h, "B", P(150, 3000), P(400, 0));
            var g = DesignNetwork.Build(h.Document);
            Assert.That(g.Edges.Single(x => x.Owner == a.Value).From, Is.EqualTo(g.StationOf(lift, top: true)));
            Assert.That(g.Edges.Single(x => x.Owner == b.Value).From, Is.EqualTo(g.StationOf(lift, top: true)));
        }
    }
}
