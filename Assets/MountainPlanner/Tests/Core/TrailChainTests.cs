using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10, owner decision NT2: a split trail's parts grade as one, so Split never changes the ground (NW-10).
    public sealed class TrailChainTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 1000, North - 1000);

        sealed class Rolling : IHeightSource
        {
            public double At(double x, double y)
            {
                double s = North - y;
                return 2000 - 0.35 * s + 0.25 * (x - West) + 3 * Math.Sin(s * 0.02) + 0.6 * Math.Sin(x * 0.09) * Math.Cos(y * 0.05);
            }
        }

        static PointMm M(double x, double y) => PointMm.FromMetres(x, y);

        static FootprintPart[] Strip(int widthFt, params (double x, double y)[] pts) =>
            new[] { new FootprintPart(new[] { new Stroke(Ft(widthFt), false, pts.Select(p => M(p.x, p.y)).ToArray()) }) };

        static (DesignHistory h, DesignEarthwork work) Session(IHeightSource g)
        {
            var h = new DesignHistory(DesignDocument.Empty(77));
            var work = new DesignEarthwork(g, Site);
            work.Prime(h.Document, h.Terrain);
            return (h, work);
        }

        static CommandResult Commit(DesignHistory h, DesignEarthwork work, DesignCommand cmd)
        {
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.ToString());
            var rebuilt = work.Rebuild(after.Document, h.Terrain);
            var r = h.Submit(cmd, rebuilt.Edits);
            Assert.That(r.Accepted, Is.True);
            work.Accept(rebuilt);
            return r;
        }

        static TrailId Add(DesignHistory h, DesignEarthwork work, FootprintPart[] parts, PointMm head, PointMm tail, bool graded = true) =>
            new TrailId(Commit(h, work, new AddTrail(h.Revision, "Run " + (h.Document.Trails.Count + 1),
                graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun, parts, head, tail, WorkStage.Built)).Created[0]);

        static TerrainEdits Fresh(IHeightSource g, DesignDocument doc)
        {
            var w = new DesignEarthwork(g, Site);
            w.Prime(DesignDocument.Empty(doc.Seed), TerrainEdits.Empty);
            return w.Rebuild(doc, TerrainEdits.Empty).Edits;
        }

        static bool Same(TerrainEdits a, TerrainEdits b) => TerrainEdits.Changed(a, b).Count == 0;

        static TrailId CurvedRun(DesignHistory h, DesignEarthwork work) =>
            Add(h, work, Strip(100, (0, 0), (30, -150), (0, -300), (-20, -420)), M(0, 0), M(-20, -420));

        [Test]
        public void NW10_NT2_SplitChangesNoGround()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var run = CurvedRun(h, work);
            var before = h.Terrain;
            Assert.That(before.IsEmpty, Is.False, "graded");

            var lower = new TrailId(Commit(h, work, new SplitTrail(h.Revision, run, M(0, -300), -20, -120)).Created[0]);
            Assert.That(Same(h.Terrain, before), Is.True, "the ground after the split is the ground before it");
            Assert.That(Same(Fresh(g, h.Document), before), Is.True, "and a load of the split design grades the same");

            // Each part sees its share of the one grade.
            var whole = work.DetailOf(run.Value) as TrailBuild;
            var part = work.DetailOf(lower.Value) as TrailBuild;
            Assert.That(whole, Is.Not.Null);
            Assert.That(part, Is.Not.Null);
            Assert.That(part.Trail.Id, Is.EqualTo(lower));
            Assert.That(part.CentrelineIsOwn, Is.False);
            Assert.That(whole.Grade.LengthMetres + part.Grade.LengthMetres, Is.EqualTo(TrailCentreline.Find(
                TrailFootprint.Build(TrailChains.Whole(new[] { h.Document.FindTrail(run), h.Document.FindTrail(lower) }).Parts, Lattice, Site.OriginX, Site.OriginY),
                Site.OriginX, Site.OriginY, Site.OriginX - 20, Site.OriginY - 420).LengthMetres).Within(0.01));
            Assert.That(part.Grade.Along[0], Is.LessThan(TrailGrader.StationMetres), "the part's stations start at its head");
        }

        [Test]
        public void NW10_NT2_SplitTwiceThenJoinChangesNoGround()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var run = CurvedRun(h, work);
            var before = h.Terrain;
            var lower = new TrailId(Commit(h, work, new SplitTrail(h.Revision, run, M(0, -300), -20, -120)).Created[0]);
            var middle = new TrailId(Commit(h, work, new SplitTrail(h.Revision, run, M(30, -150), 0, -1000)).Created[0]);
            Assert.That(Same(h.Terrain, before), Is.True, "three parts, one grade");
            Commit(h, work, new JoinTrails(h.Revision, run, middle));
            Commit(h, work, new JoinTrails(h.Revision, run, lower));
            Assert.That(h.Document.Trails.Count, Is.EqualTo(1));
            Assert.That(Same(h.Terrain, before), Is.True, "joined back, the same ground");
        }

        [Test]
        public void NT2_ARepaintedPartGradesOnItsOwn()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var run = CurvedRun(h, work);
            var lower = new TrailId(Commit(h, work, new SplitTrail(h.Revision, run, M(0, -300), -20, -120)).Created[0]);
            var t = h.Document.FindTrail(lower);
            Commit(h, work, new RepaintTrail(h.Revision, lower, Strip(120, (0, -300), (-20, -420))));
            Assert.That(TrailChains.Of(h.Document.Trails).Count, Is.EqualTo(2), "no longer the two sides of one cut");
            Assert.That(work.DetailOf(lower.Value) is TrailBuild own && own.CentrelineIsOwn, Is.True);
        }

        [Test]
        public void NT3_JoiningTwoSeparateGradedTrailsRegradesTheSeam()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var upper = Add(h, work, Strip(100, (0, 0), (0, -200)), M(0, 0), M(0, -200));
            var lower = Add(h, work, Strip(100, (0, -200), (0, -400)), M(0, -200), M(0, -400));
            var apart = h.Terrain;
            Commit(h, work, new JoinTrails(h.Revision, upper, lower));
            Assert.That(Same(h.Terrain, apart), Is.False, "one bench through the seam");
            h.Undo();
            Assert.That(Same(h.Terrain, apart), Is.True, "undo restores it");
        }
    }
}
