using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10: the trail geometry behind the network (NW-07, PA-05 to PA-07, JN-04, TR-05), on painted footprints and
    // real centrelines.
    public sealed class NetworkGeometryTests
    {
        static readonly EarthworkSite Site = new EarthworkSite(new EditLattice(-5000, 5000), 0, 0);

        /// <summary>z = −slope × x: the ground falls to the east.</summary>
        sealed class Tilt : IHeightSource
        {
            readonly double _slope;
            public Tilt(double slope) { _slope = slope; }
            public double At(double x, double y) => 1000 - _slope * x;
        }

        sealed class Box : IPathBlockers
        {
            readonly double _w, _s, _e, _n;
            public Box(double w, double s, double e, double n) { _w = w; _s = s; _e = e; _n = n; }
            public bool Blocks(double x, double y) => x >= _w && x <= _e && y >= _s && y <= _n;
        }

        /// <summary>A built, graded trail painted as one stroke through <paramref name="through"/> (head to tail).</summary>
        static TrailId Painted(DesignHistory h, string name, int widthFt, params PointMm[] through)
        {
            var parts = new[] { new FootprintPart(new[] { new Stroke(Ft(widthFt), false, through) }) };
            var r = h.Submit(new AddTrail(h.Revision, name, TrailSettings.GradedRun, parts, through[0], through[through.Length - 1], WorkStage.Built));
            Assert.That(r.Accepted, Is.True, r.ToString());
            return new TrailId(r.Created[0]);
        }

        static List<TrailJunction> Junctions(DesignDocument doc, TrailNetworkGeometry g = null)
        {
            var list = new List<TrailJunction>();
            (g ?? new TrailNetworkGeometry(Site)).FindJunctions(doc, list);
            return list;
        }

        static List<TrailPath> Paths(DesignDocument doc, TrailNetworkGeometry g)
        {
            var list = new List<TrailPath>();
            g.FindPaths(doc, list);
            return list;
        }

        static bool Pair(TrailJunction j, TrailId a, TrailId b) => (j.A == a && j.B == b) || (j.A == b && j.B == a);

        [Test]
        public void TR05_NW07_AnEndWithin30FtOfAnotherTrailJoinsItAtTheNearestPoint()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var main = Painted(h, "Main", 100, P(0, 3000), P(0, 0));
            var spur = Painted(h, "Spur", 60, P(25, 1500), P(1500, 0));   // its head lies on Main, 25 ft off its line
            var js = Junctions(h.Document);
            Assert.That(js.Count, Is.EqualTo(1), "one junction; the overlap round the head adds nothing");
            var j = js[0];
            Assert.That(Pair(j, main, spur), Is.True);
            int spurAlong = j.A == spur ? j.AlongAMm : j.AlongBMm, mainAlong = j.A == main ? j.AlongAMm : j.AlongBMm;
            Assert.That(spurAlong, Is.EqualTo(0), "at the spur's head");
            Assert.That(mainAlong, Is.EqualTo(Ft(1500)).Within(1000), "halfway down Main");
            Assert.That(j.At.X, Is.EqualTo(0).Within(1000), "on Main's centreline");

            var graph = DesignNetwork.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(graph.Edges.Count(e => e.Owner == main.Value), Is.EqualTo(2), "NW-08: Main is cut there");
            Assert.That(graph.Nodes.Count(n => n.Kind == NodeKind.TrailHead), Is.EqualTo(1), "the spur has no head of its own");
        }

        [Test]
        public void TR05_AnEnd31FtAwayStaysApart()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            Painted(h, "Main", 30, P(0, 3000), P(0, 0));
            Painted(h, "Spur", 30, P(31, 1500), P(1500, 0));
            Assert.That(Junctions(h.Document).Count, Is.EqualTo(0));
        }

        [Test]
        public void NW07_EachOverlapPatchIsOneJunction()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var straight = Painted(h, "Straight", 80, P(0, 3000), P(0, 0));
            var vee = Painted(h, "Vee", 80, P(-600, 3000), P(600, 1500), P(-600, 0));   // crosses Straight twice
            var js = Junctions(h.Document);
            Assert.That(js.Count, Is.EqualTo(2));
            Assert.That(js.All(j => Pair(j, straight, vee)), Is.True);
            var ys = js.Select(j => j.At.Y).OrderBy(y => y).ToArray();
            Assert.That(ys[0], Is.EqualTo(Ft(750)).Within(Ft(30)), "the lower crossing");
            Assert.That(ys[1], Is.EqualTo(Ft(2250)).Within(Ft(30)), "the upper crossing");
            foreach (var j in js) Assert.That(j.At.X, Is.EqualTo(0).Within(Ft(30)), "nearest the overlap's middle");
        }

        [Test]
        public void PA05_TrailsWithin30FtGetOnePathAtTheirClosestPoint()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var a = Painted(h, "A", 100, P(0, 3000), P(0, 0));          // edges at ±50 ft
            var b = Painted(h, "B", 100, P(110, 3000), P(135, 0));      // 10 ft apart at the top, 35 ft at the bottom
            var paths = Paths(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(paths.Count, Is.EqualTo(1), "one close stretch, one path");
            var p = paths[0];
            Assert.That((p.A == a && p.B == b) || (p.A == b && p.B == a), Is.True);
            Assert.That(p.AtA.Y, Is.GreaterThan(Ft(2900)), "at the closest point, near the top");
            Assert.That(System.Math.Sqrt(p.AtA.DistanceSquared(p.AtB)), Is.LessThan(Ft(30) + 1500));
            Assert.That(p.Direction, Is.EqualTo(PathDirection.Both), "PA-06: no ground given, both ways");
        }

        [Test]
        public void PA05_TrailsFartherThan30FtOrTouchingGetNoPath()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            Painted(h, "A", 100, P(0, 3000), P(0, 0));
            Painted(h, "Far", 100, P(140, 3000), P(140, 0));     // 40 ft apart
            Painted(h, "Touching", 100, P(-100, 3000), P(-100, 0)); // edge to edge
            var paths = Paths(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(paths.Count, Is.EqualTo(0));
        }

        [Test]
        public void PA06_APathRunsDownhillOrBothWaysWithin3Ft()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var a = Painted(h, "A", 100, P(0, 3000), P(0, 0));
            var b = Painted(h, "B", 100, P(120, 3000), P(120, 0));  // a 20 ft (6.1 m) gap
            var steep = Paths(h.Document, new TrailNetworkGeometry(Site, new Tilt(0.25))).Single();   // ~1.5 m drop
            var gentle = Paths(h.Document, new TrailNetworkGeometry(Site, new Tilt(0.1))).Single();   // ~0.6 m
            Assert.That(gentle.Direction, Is.EqualTo(PathDirection.Both));
            // The ground falls east, so the path runs from whichever end is west.
            bool aWest = steep.AtA.X < steep.AtB.X;
            Assert.That(steep.Direction, Is.EqualTo(aWest ? PathDirection.AToB : PathDirection.BToA));

            var graph = DesignNetwork.Build(h.Document, new TrailNetworkGeometry(Site, new Tilt(0.25)));
            var edge = graph.Edges.Single(e => e.Kind == EdgeKind.Path);
            Assert.That(graph.TryGetNode(edge.From, out var from), Is.True);
            Assert.That(graph.TryGetNode(edge.To, out var to), Is.True);
            Assert.That(from.At.X, Is.LessThan(to.At.X), "downhill: west to east");
        }

        [Test]
        public void PA07_APathNeverCrossesABlockerOrALiftTerminal()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            Painted(h, "A", 100, P(0, 3000), P(0, 0));
            Painted(h, "B", 100, P(110, 3000), P(135, 0));
            // Block the gap's top 1,000 ft: the path moves to the closest point below it.
            double w = Ft(50) / 1000.0, e = Ft(70) / 1000.0, s = Ft(2000) / 1000.0, n = Ft(3100) / 1000.0;
            var moved = Paths(h.Document, new TrailNetworkGeometry(Site, blockers: new Box(w, s, e, n))).Single();
            Assert.That(moved.AtA.Y, Is.LessThan(Ft(2000)).And.GreaterThan(Ft(1800)));

            // A lift's bottom terminal sitting in the gap where it's narrowest does the same: the path goes round it.
            var free = Paths(h.Document, new TrailNetworkGeometry(Site)).Single();
            AddLift(h, "Chair", new PointMm(Ft(60), free.AtA.Y), P(60, 7000));
            var round = Paths(h.Document, new TrailNetworkGeometry(Site)).Single();
            Assert.That(round.AtA, Is.Not.EqualTo(free.AtA));
            // The line runs north, so the terminal covers Behind south to Ahead north of the bullwheel, HalfWidth each side.
            var end = LiftType.Of(LiftCatalogIds.SesselliftFgq4).Bottom;
            double bx = Ft(60) / 1000.0, by = free.AtA.Y / 1000.0;
            for (int q = 0; q <= 100; q++)
            {
                double x = (round.AtA.X + (round.AtB.X - round.AtA.X) * q / 100.0) / 1000.0;
                double y = (round.AtA.Y + (round.AtB.Y - round.AtA.Y) * q / 100.0) / 1000.0;
                bool inside = y - by >= -end.Behind && y - by <= end.Ahead && System.Math.Abs(x - bx) <= end.HalfWidth;
                Assert.That(inside, Is.False, "the path crosses the terminal");
            }
        }

        [Test]
        public void NW07_DeletingATrailPrunesOnlyItsOwnJunctions()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var a = Painted(h, "A", 80, P(0, 3000), P(0, 0));
            var b = Painted(h, "B", 80, P(-800, 2600), P(800, 1800));
            var c = Painted(h, "C", 80, P(-800, 1200), P(800, 400));
            var before = DesignNetwork.Build(h.Document, new TrailNetworkGeometry(Site));
            var ab = Junctions(h.Document).Single(j => Pair(j, a, b));
            ulong abId = DesignNetwork.JunctionNode(h.Document.Seed, ab.A, ab.B, ab.At);
            Assert.That(before.Nodes.Count(x => x.Kind == NodeKind.Junction), Is.EqualTo(2));

            h.Submit(new DeleteTrail(h.Revision, c));
            var after = DesignNetwork.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(after.Nodes.Count(x => x.Kind == NodeKind.Junction), Is.EqualTo(1));
            Assert.That(after.TryGetNode(abId, out _), Is.True, "A–B's junction stays, with the same ID (JN-04)");
            Assert.That(after.Edges.Any(x => x.Owner == c.Value), Is.False);
        }

        [Test]
        public void NW10_ASplitTrailIsJoinedEndToEnd()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            var lift = AddLift(h, "Chair", P(0, 0), P(0, 3000));
            var run = Painted(h, "Run", 100, P(20, 2990), P(20, 10));
            var split = h.Submit(new SplitTrail(h.Revision, run, P(20, 1500), 0, -1000));
            Assert.That(split.Accepted, Is.True, split.ToString());
            var lower = new TrailId(split.Created[0]);
            var js = Junctions(h.Document);
            Assert.That(js.Count, Is.EqualTo(1), "one end-to-end junction at the cut");
            var j = js[0];
            Assert.That(Pair(j, run, lower), Is.True);
            Assert.That(j.A == run ? j.AlongBMm : j.AlongAMm, Is.EqualTo(0), "the lower part's head");

            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            Assert.That(report.Of(lift).Connected, Is.True, "NW-03: still a way down through both parts");
            Assert.That(report.Of(lift).Reaches, Is.EquivalentTo(new[] { run, lower }));
        }

        [Test]
        public void NW01_JN04_TheNetworkIsTheSameFromAnyLoadAndAnyOrder()
        {
            var doc = TestResort(out _, out _, out _);
            var one = DesignNetwork.Build(doc, new TrailNetworkGeometry(Site, new Tilt(0.2))).Encode();
            var reloaded = DesignBytes.Decode(DesignBytes.Encode(doc));
            var two = DesignNetwork.Build(reloaded, new TrailNetworkGeometry(Site, new Tilt(0.2))).Encode();
            Assert.That(two, Is.EqualTo(one), "from a reload");
            var reversed = DesignDocument.From(doc.Seed, doc.NextOrdinal, doc.Trails.Reverse().ToArray(), doc.Lifts.Reverse().ToArray());
            Assert.That(DesignNetwork.Build(reversed, new TrailNetworkGeometry(Site, new Tilt(0.2))).Encode(), Is.EqualTo(one), "in any order");
            // The same geometry asked twice (its shapes cached) gives the same bytes.
            var g = new TrailNetworkGeometry(Site, new Tilt(0.2));
            DesignNetwork.Build(doc, g);
            Assert.That(DesignNetwork.Build(doc, g).Encode(), Is.EqualTo(one), "cached shapes");
            Assert.That(Sha(one), Is.EqualTo(GoldenNetworkSha), "the network golden");
        }

        /// <summary>The test resort's graph bytes (NW-01). Pinned; re-pin only with the owner's approval.</summary>
        const string GoldenNetworkSha = "bd9b3855842650b9b78a241761970c74ef46fa8e59706b05cc7c053acf7bdcc5";

        /// <summary>
        /// The test resort: Chair 1 with a run down it, a spur off the run and a parallel run a path away; Chair 2 whose
        /// only trail leads nowhere; and a trail no lift serves.
        /// </summary>
        internal static DesignDocument TestResort(out LiftId chair1, out LiftId chair2, out TrailId orphan)
        {
            var h = new DesignHistory(DesignDocument.Empty(0x5EED_0010));
            chair1 = AddLift(h, "Chair 1", P(0, 0), P(0, 3000));
            chair2 = AddLift(h, "Chair 2", P(3000, 0), P(3000, 3000));
            Painted(h, "Home Run", 100, P(20, 2990), P(400, 1500), P(20, 10));
            Painted(h, "Spur", 60, P(395, 1550), P(900, 800), P(15, 15));
            Painted(h, "Parallel", 100, P(-120, 2990), P(-480, 1500), P(-100, 20));
            Painted(h, "Dead End", 80, P(3010, 2990), P(2600, 500));
            orphan = Painted(h, "Orphan", 80, P(5000, 3000), P(5000, 0));
            return h.Document;
        }
    }
}
