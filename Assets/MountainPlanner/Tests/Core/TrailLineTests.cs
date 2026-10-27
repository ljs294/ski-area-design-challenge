using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10, owner decisions NT24–NT46 (2026-10-27): a trail is made of segments, the stretches of runs between
    // nodes (their ends, every junction, the lift stations they pass, the points the player cut).
    public sealed class TrailLineTests
    {
        /// <summary>Straight runs and the junctions a test lists, by stage (NT44), and where a point falls along a run.</summary>
        sealed class StubGeometry : ITrailGeometry, IStagedTrailGeometry
        {
            public readonly List<TrailJunction> Junctions = new List<TrailJunction>();
            public int CentrelineLengthMm(Trail t) => (int)System.Math.Round(System.Math.Sqrt(t.Head.Value.DistanceSquared(t.Tail.Value)));
            public void FindJunctions(DesignDocument doc, List<TrailJunction> into) => FindJunctions(doc, WorkStage.Built, into);
            public void FindJunctions(DesignDocument doc, WorkStage stage, List<TrailJunction> into)
            {
                foreach (var j in Junctions)
                {
                    var a = doc.FindTrail(j.A);
                    var b = doc.FindTrail(j.B);
                    if (a != null && b != null && a.Stage == stage && b.Stage == stage) into.Add(j);
                }
            }
            public void FindPaths(DesignDocument doc, List<TrailPath> into) { }
            public int AlongOf(Trail t, PointMm p)
            {
                double hx = t.Head.Value.X, hy = t.Head.Value.Y, dx = t.Tail.Value.X - hx, dy = t.Tail.Value.Y - hy, l2 = dx * dx + dy * dy;
                double f = System.Math.Max(0, System.Math.Min(1, ((p.X - hx) * dx + (p.Y - hy) * dy) / l2));
                return (int)System.Math.Round(f * System.Math.Sqrt(l2));
            }
        }

        static TrailSections Sections(DesignDocument doc, StubGeometry g) =>
            TrailSections.Of(doc, DesignNetwork.Build(doc, g, WorkStage.Built), DesignNetwork.Build(doc, g, WorkStage.Plan));

        static CommandResult Submit(DesignHistory h, DesignCommand cmd)
        {
            Assert.That(cmd, Is.Not.Null, "a click that does something");
            var r = h.Submit(cmd);
            Assert.That(r.Accepted, Is.True, r.ToString());
            return r;
        }

        // Cornice runs straight down 3,000 ft; Chute's tail lands on it 1,000 ft down; Run 2 crosses it 2,000 ft down.
        static (DesignHistory h, StubGeometry g, TrailId cornice, TrailId chute, TrailId run2) Cornice(WorkStage stage = WorkStage.Built)
        {
            var h = new DesignHistory(DesignDocument.Empty(0x5EED_2026));
            var g = new StubGeometry();
            var cornice = AddTrail(h, "Cornice", P(0, 3000), P(0, 0), stage);
            var chute = AddTrail(h, "Chute", P(600, 2600), P(0, 2000), stage);
            var run2 = AddTrail(h, "Run 2", P(-500, 1200), P(500, 800), stage);
            int chuteLength = g.CentrelineLengthMm(h.Document.FindTrail(chute)), run2Length = g.CentrelineLengthMm(h.Document.FindTrail(run2));
            g.Junctions.Add(new TrailJunction(cornice, Ft(1000), chute, chuteLength, P(0, 2000)));
            g.Junctions.Add(new TrailJunction(cornice, Ft(2000), run2, run2Length / 2, P(0, 1000)));
            return (h, g, cornice, chute, run2);
        }

        [Test]
        public void NT44_EveryJunctionSplitsARunAndItStaysOneTrail()
        {
            var (h, g, cornice, chute, run2) = Cornice();
            var ts = Sections(h.Document, g);
            Assert.That(ts.SegmentsOf(cornice).Count, Is.EqualTo(3), "where Chute lands, and where Run 2 crosses");
            Assert.That(ts.SegmentsOf(run2).Count, Is.EqualTo(2), "Run 2 is split where it crosses Cornice too");
            var trail = ts.TrailOf(cornice);
            Assert.That(trail.Name, Is.EqualTo("Cornice"));
            Assert.That(trail.Segments.Count, Is.EqualTo(3), "S2: still one trail, in three sections");
            Assert.That(trail.Line, Is.Null, "nothing saved for it");
            Assert.That(h.Document.Lines, Is.Empty);
        }

        [Test]
        public void NT44_PlansSplitAmongPlansNeverAcrossStages()
        {
            var (h, g, cornice, _, _) = Cornice();
            var plan = AddTrail(h, "Planned", P(-300, 2600), P(300, 2400), WorkStage.Plan);
            g.Junctions.Add(new TrailJunction(cornice, Ft(500), plan, Ft(300), P(0, 2500)));
            var ts = Sections(h.Document, g);
            Assert.That(ts.SegmentsOf(cornice).Count, Is.EqualTo(3), "a plan crossing a built run doesn't split it");
            Assert.That(ts.SegmentsOf(plan).Count, Is.EqualTo(1));
            Assert.That(ts.TrailOf(plan).Built, Is.False);
        }

        [Test]
        public void NT35_AClickReroutesAtAJunctionAndTheRestStaysTheTrailAbove()
        {
            var (h, g, cornice, chute, _) = Cornice();
            var ts = Sections(h.Document, g);
            var chuteTrail = ts.TrailOf(chute);
            var bottom = ts.SegmentsOf(cornice)[2];
            var route = TrailSectionsEdit.Route(ts, chuteTrail, bottom);
            Assert.That(route, Is.EqualTo(new[] { ts.SegmentsOf(cornice)[1], bottom }), "down Cornice from the junction, nearest first");
            Submit(h, TrailSectionsEdit.Add(h.Document, h.Revision, ts, chuteTrail, route));
            ts = Sections(h.Document, g);
            Assert.That(ts.TrailOf(chute).Segments.Count, Is.EqualTo(3));
            Assert.That(ts.TrailOf(chute).Name, Is.EqualTo("Chute"));
            Assert.That(ts.TrailOf(chute).Id, Is.EqualTo(chuteTrail.Id), "the trail keeps its ID when it's first shaped");
            var top = ts.TrailOf(cornice);
            Assert.That(top.Segments, Is.EqualTo(new[] { ts.SegmentsOf(cornice)[0] }));
            Assert.That(top.Name, Is.EqualTo("Cornice"), "what's left of Cornice is still Cornice: it runs end to end");
            Assert.That(ts.TrailOf(ts.SegmentsOf(cornice)[2]), Is.SameAs(ts.TrailOf(chute)));
        }

        [Test]
        public void NT35_NoRouteForkingOffTheMiddle()
        {
            var (h, g, cornice, chute, run2) = Cornice();
            var ts = Sections(h.Document, g);
            var corniceTrail = ts.TrailOf(cornice);
            Assert.That(TrailSectionsEdit.Route(ts, corniceTrail, ts.SegmentsOf(chute)[0]), Is.Null, "Chute meets Cornice partway down: a fork");
        }

        [Test]
        public void NT27_ATakenOutSegmentIsATrailOfItsOwnNamedRunN()
        {
            var (h, g, cornice, _, _) = Cornice();
            var ts = Sections(h.Document, g);
            var trail = ts.TrailOf(cornice);
            Submit(h, TrailSectionsEdit.TakeOut(h.Document, h.Revision, ts, trail, ts.SegmentsOf(cornice)[2]));
            ts = Sections(h.Document, g);
            Assert.That(ts.TrailOf(cornice).Segments.Count, Is.EqualTo(2));
            Assert.That(ts.TrailOf(ts.SegmentsOf(cornice)[2]).Name, Does.StartWith("Run "));
        }

        [Test]
        public void NT28_TakingOutAMiddleSegmentShowsUpperAndLower()
        {
            var (h, g, cornice, _, _) = Cornice();
            var ts = Sections(h.Document, g);
            Submit(h, TrailSectionsEdit.Rename(h.Document, h.Revision, ts, ts.TrailOf(cornice), "Ridge"));
            ts = Sections(h.Document, g);
            Submit(h, TrailSectionsEdit.TakeOut(h.Document, h.Revision, ts, ts.TrailOf(cornice), ts.SegmentsOf(cornice)[1]));
            ts = Sections(h.Document, g);
            var segs = ts.SegmentsOf(cornice);
            Assert.That(ts.TrailOf(segs[0]).Name, Is.EqualTo("Upper Ridge"));
            Assert.That(ts.TrailOf(segs[2]).Name, Is.EqualTo("Lower Ridge"));
            Assert.That(ts.TrailOf(segs[1]).Name, Does.StartWith("Run "));
            Assert.That(ts.TrailOf(segs[0]).Id, Is.Not.EqualTo(ts.TrailOf(segs[2]).Id));
        }

        [Test]
        public void NT45_ACutPointAddsANodeAndNeverTouchesTheRun()
        {
            var (h, g, cornice, _, _) = Cornice();
            var paint = DesignBytes.Encode(h.Document.FindTrail(cornice));
            Submit(h, TrailSectionsEdit.Cut(h.Revision, cornice, P(0, 2500)));
            var ts = Sections(h.Document, g);
            Assert.That(ts.SegmentsOf(cornice).Count, Is.EqualTo(4));
            Assert.That(ts.TrailOf(cornice).Segments.Count, Is.EqualTo(4), "both pieces stay in the trail");
            Assert.That(DesignBytes.Same(DesignBytes.Encode(h.Document.FindTrail(cornice)), paint), Is.True, "the paint is untouched");
            Submit(h, TrailSectionsEdit.Rejoin(h.Revision, h.Document.Cuts[0]));
            Assert.That(Sections(h.Document, g).SegmentsOf(cornice).Count, Is.EqualTo(3));
        }

        [Test]
        public void NT46_ANodeThatGoesLeavesItsStretchWithTheTrailAbove()
        {
            // Chute takes Cornice below the junction; then Chute is removed, so the junction goes and Cornice's top and
            // middle are one segment: it stays with Cornice (the trail above), and the shaped trail keeps the bottom.
            var (h, g, cornice, chute, _) = Cornice();
            var ts = Sections(h.Document, g);
            var chuteTrail = ts.TrailOf(chute);
            Submit(h, TrailSectionsEdit.Add(h.Document, h.Revision, ts, chuteTrail, TrailSectionsEdit.Route(ts, chuteTrail, ts.SegmentsOf(cornice)[2])));
            Submit(h, new DeleteTrail(h.Revision, chute));
            g.Junctions.RemoveAt(0);
            ts = Sections(h.Document, g);
            var segs = ts.SegmentsOf(cornice);
            Assert.That(segs.Count, Is.EqualTo(2));
            Assert.That(ts.TrailOf(segs[0]).Name, Is.EqualTo("Cornice"));
            Assert.That(ts.TrailOf(segs[1]).Name, Is.EqualTo("Chute"), "the bottom stays in the shaped trail, named as it was");
        }

        [Test]
        public void NT43_ACrossingAnchorFollowsItsRunOrFallsBackToWhereItWas()
        {
            // Master Planner's note: a span ending at "the k-th crossing with Run 2" survives Run 2 being repainted.
            var (h, g, cornice, chute, run2) = Cornice();
            var ts = Sections(h.Document, g);
            var chuteTrail = ts.TrailOf(chute);
            Submit(h, TrailSectionsEdit.Add(h.Document, h.Revision, ts, chuteTrail, TrailSectionsEdit.Route(ts, chuteTrail, ts.SegmentsOf(cornice)[1])));
            var before = DesignBytes.Encode(h.Document);
            Assert.That(h.Document.Lines.Single().Spans.Last().To.Kind, Is.EqualTo(AnchorKind.Crosses));
            // Run 2 moves 50 ft down: the crossing moves with it, and Chute still ends there.
            g.Junctions[1] = new TrailJunction(cornice, Ft(2050), run2, g.Junctions[1].AlongBMm, P(0, 950));
            ts = Sections(h.Document, g);
            Assert.That(ts.TrailOf(chute).Segments.Last().EndMm, Is.EqualTo(Ft(2050)));
            // Run 2 moves 1,000 ft away (over 100 m): the span ends where it was saved instead.
            g.Junctions[1] = new TrailJunction(cornice, Ft(2800), run2, g.Junctions[1].AlongBMm, P(0, 200));
            ts = Sections(h.Document, g);
            Assert.That(ts.TrailOf(chute).Segments.Last().EndMm, Is.EqualTo(Ft(2800)), "the segment holding its last top still goes to Chute");
            // An edit and its undo give the same bytes.
            Submit(h, new RenameTrail(h.Revision, run2, "Run 2b"));
            h.Undo();
            Assert.That(DesignBytes.Same(DesignBytes.Encode(h.Document), before), Is.True);
        }

        [Test]
        public void NT43_ShapedTrailsAndCutsRoundTripThroughTheBytes()
        {
            var (h, g, cornice, chute, _) = Cornice();
            var ts = Sections(h.Document, g);
            var chuteTrail = ts.TrailOf(chute);
            Submit(h, TrailSectionsEdit.Add(h.Document, h.Revision, ts, chuteTrail, TrailSectionsEdit.Route(ts, chuteTrail, ts.SegmentsOf(cornice)[2])));
            Submit(h, TrailSectionsEdit.Cut(h.Revision, cornice, P(0, 2500)));
            var bytes = DesignBytes.Encode(h.Document);
            var back = DesignBytes.Decode(bytes);
            Assert.That(back.Cuts.Count, Is.EqualTo(1));
            Assert.That(back.Lines.Single().Spans.Count, Is.EqualTo(2));
            Assert.That(DesignBytes.Same(DesignBytes.Encode(back), bytes), Is.True);
        }

        [Test]
        public void NT33_RemovingARunDropsItsSpansAndCuts()
        {
            var (h, g, cornice, chute, _) = Cornice();
            var ts = Sections(h.Document, g);
            var chuteTrail = ts.TrailOf(chute);
            Submit(h, TrailSectionsEdit.Add(h.Document, h.Revision, ts, chuteTrail, TrailSectionsEdit.Route(ts, chuteTrail, ts.SegmentsOf(cornice)[2])));
            Submit(h, TrailSectionsEdit.Cut(h.Revision, cornice, P(0, 2500)));
            Submit(h, new DeleteTrail(h.Revision, cornice));
            Assert.That(h.Document.Cuts, Is.Empty);
            Assert.That(h.Document.Lines.Single().Spans.Select(s => s.Piece), Is.EqualTo(new[] { chute }));
        }

        [Test]
        public void NT32_BreakGroundBuildsEveryRunOfTheShapedTrail()
        {
            var (h, g, cornice, chute, _) = Cornice(WorkStage.Plan);
            var ts = Sections(h.Document, g);
            var chuteTrail = ts.TrailOf(chute);
            Submit(h, TrailSectionsEdit.Add(h.Document, h.Revision, ts, chuteTrail, TrailSectionsEdit.Route(ts, chuteTrail, ts.SegmentsOf(cornice)[2])));
            Submit(h, new BreakGround(h.Revision, new[] { chute }, null));
            Assert.That(h.Document.FindTrail(cornice).Stage, Is.EqualTo(WorkStage.Built));
        }

        [Test]
        public void NT40_PositionsAreReplacedNeverStackedAndNamesFit()
        {
            Assert.That(TrailLines.BreakNames("Upper Run 4", 2), Is.EqualTo(new[] { "Upper Run 4", "Lower Run 4" }));
            Assert.That(TrailLines.BreakNames("Middle 2 Run 4", 3), Is.EqualTo(new[] { "Upper Run 4", "Middle Run 4", "Lower Run 4" }));
            Assert.That(TrailLines.BreakNames("Run 4", 4), Is.EqualTo(new[] { "Upper Run 4", "Middle 1 Run 4", "Middle 2 Run 4", "Lower Run 4" }));
            string longName = new string('x', DesignRules.NameMaxLength);
            foreach (string n in TrailLines.BreakNames(longName, 2)) Assert.That(DesignRules.NameProblem(n), Is.Null, n);
            Assert.That(TrailLines.NextName(2, new[] { "Run 3" }), Is.EqualTo("Run 4"));
        }
    }
}
