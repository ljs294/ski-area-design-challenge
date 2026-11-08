using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task 4.21 (GU-17, owner R1–R6, 2026-11-08): TD-02 v2 rates a trail by its steepest sustained 150 ft (six 25 ft
    // stations) over the segments that belong to it; TD-06 v2 estimates a painting by its steepest 150 ft square; TD-07
    // is the sign the player posts, which a trail must have before it is built.
    public sealed class TrailRatingTests
    {
        const double Station = TrailGrader.StationMetres;

        static double[] Slopes(params (int count, double percent)[] runs) =>
            runs.SelectMany(r => Enumerable.Repeat(r.percent, r.count)).ToArray();

        static double[] Along(int n) => Enumerable.Range(0, n).Select(k => k * Station).ToArray();

        static TrailRating.Pitch Steepest(double[] slope) => TrailRating.Steepest(slope, Along(slope.Length), 0, slope.Length);

        // ---- TD-02 v2: the steepest sustained pitch ----

        [Test]
        public void TD02_AGreenTrailWithA150FtCliffBandRatesByTheCliff()
        {
            var slope = Slopes((30, 12), (6, 70), (30, 12));
            var p = Steepest(slope);
            Assert.That(p.Percent, Is.EqualTo(70).Within(1e-9));
            Assert.That(p.Band, Is.EqualTo(SlopeBands.Band.DoubleBlack), "R4: a cliff band in a green trail makes a hard trail");
            Assert.That(p.First, Is.EqualTo(30), "the window starts at the band");
            Assert.That(p.MeanPercent, Is.LessThan(20), "the mean stays gentle; it no longer decides the rating");
        }

        [Test]
        public void TD02_A75FtSpikeIsDilutedByThe150FtWindow()
        {
            var p = Steepest(Slopes((30, 10), (3, 70), (30, 10)));
            Assert.That(p.Percent, Is.EqualTo(40).Within(1e-9), "three steep stations and three gentle ones");
            Assert.That(p.Count, Is.EqualTo(TrailRating.SustainedStations));
            Assert.That(p.Band, Is.EqualTo(SlopeBands.Band.Black));
        }

        [Test]
        public void TD02_ATrailShorterThan150FtIsRatedOnAllItsStations()
        {
            var p = Steepest(new double[] { 10, 20, 30, 40 });
            Assert.That(p.Percent, Is.EqualTo(25).Within(1e-9));
            Assert.That(p.Count, Is.EqualTo(4));
            Assert.That(p.First, Is.EqualTo(0));
        }

        [Test]
        public void TD02_TiesKeepTheFirstWindowAndNoStationsIsNone()
        {
            var p = Steepest(Slopes((6, 50), (4, 10), (6, 50)));
            Assert.That(p.First, Is.EqualTo(0));
            Assert.That(TrailRating.Steepest(new double[0], new double[0], 0, 0).IsNone, Is.True);
        }

        [Test]
        public void TD02_TheGateTakesTheHarderOfTwoRatings()
        {
            Assert.That(TrailRating.Harder(SlopeBands.Band.Green, SlopeBands.Band.Black), Is.EqualTo(SlopeBands.Band.Black));
            Assert.That(TrailRating.Harder(SlopeBands.Band.DoubleBlack, SlopeBands.Band.Blue), Is.EqualTo(SlopeBands.Band.DoubleBlack));
            Assert.That(TrailRating.Harder(SlopeBands.Band.Blue, SlopeBands.Band.Blue), Is.EqualTo(SlopeBands.Band.Blue));
        }

        [Test]
        public void BR3_TheSlopeToolsBandsAreUnchanged()
        {
            // TD-03's bands still read the Slope tool (BR3) and every rating: 25, 40 and 60%.
            Assert.That(SlopeBands.Of(24.9), Is.EqualTo(SlopeBands.Band.Green));
            Assert.That(SlopeBands.Of(25), Is.EqualTo(SlopeBands.Band.Blue));
            Assert.That(SlopeBands.Of(39.9), Is.EqualTo(SlopeBands.Band.Blue));
            Assert.That(SlopeBands.Of(40), Is.EqualTo(SlopeBands.Band.Black));
            Assert.That(SlopeBands.Of(60), Is.EqualTo(SlopeBands.Band.DoubleBlack));
        }

        // ---- R4: a trail is rated on its own segments ----

        static TrailRating.Result Run(double[] slope) => new TrailRating.Result { StationSlope = slope, Along = Along(slope.Length) };

        static int Mm(double metres) => (int)Math.Round(metres * 1000);

        [Test]
        public void R4_UpperAndLowerAreEachRatedOnTheirOwnSegments()
        {
            var id = TrailId.Make(1, 1);
            var run = Run(Slopes((20, 10), (20, 50)));
            double split = 19.5 * Station;
            var upper = new Segment(id, 0, Mm(split), 1, 2, true);
            var lower = new Segment(id, Mm(split), Mm(39 * Station), 2, 3, true);
            Func<TrailId, TrailRating.Result> of = t => t == id ? run : null;
            Assert.That(TrailRating.OfTrail(new[] { upper }, of).Percent, Is.EqualTo(10).Within(1e-9), "Upper never reads Lower's pitch");
            Assert.That(TrailRating.OfTrail(new[] { lower }, of).Percent, Is.EqualTo(50).Within(1e-9));
            Assert.That(TrailRating.OfTrail(new[] { upper, lower }, of).Percent, Is.EqualTo(50).Within(1e-9), "the whole run, as one trail");
        }

        [Test]
        public void R4_ATrailAcrossTwoRunsJoinsTheirStationsTopFirst()
        {
            var a = TrailId.Make(1, 1);
            var b = TrailId.Make(1, 2);
            // Each run's last three stations are steep; joined, the six make one sustained pitch.
            var ra = Run(Slopes((10, 10), (3, 60)));
            var rb = Run(Slopes((3, 60), (10, 10)));
            Func<TrailId, TrailRating.Result> of = t => t == a ? ra : t == b ? rb : null;
            var sa = new Segment(a, 0, Mm(12 * Station), 1, 2, true);
            var sb = new Segment(b, 0, Mm(12 * Station), 2, 3, true);
            Assert.That(TrailRating.OfTrail(new[] { sa }, of).Percent, Is.EqualTo(35).Within(1e-9));
            Assert.That(TrailRating.OfTrail(new[] { sa, sb }, of).Percent, Is.EqualTo(60).Within(1e-9));
            Assert.That(TrailRating.OfTrail(new[] { sa, sb }, t => null).IsNone, Is.True, "no run rated yet");
        }

        // ---- TD-06 v2: the painting's estimate ----

        const double West = 500_000, North = 4_800_000;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 1000, North - 1000);

        /// <summary>A slope falling to the south at 10% with a band of <paramref name="steep"/>% between local y −150 and −150 − depth.</summary>
        sealed class Headwall : IHeightSource
        {
            readonly double _depth, _steep;
            public Headwall(double depth, double steep) { _depth = depth; _steep = steep; }
            public double At(double x, double y)
            {
                double ly = y - Site.OriginY;
                double band = Math.Max(-_depth, Math.Min(0, ly + 150));   // 0 above the band, −depth below it
                return 1800 + 0.1 * ly + (_steep - 10) / 100 * band;
            }
        }

        static TrailFootprint Strip(double length) =>
            TrailFootprint.Build(TrailShape.FromStrokes(null, new[] { new Stroke(Ft(100), false, new[] { PointMm.FromMetres(0, 0), PointMm.FromMetres(0, -length) }) }),
                                 Lattice, Site.OriginX, Site.OriginY);

        static EditedHeights Ground(IHeightSource g) => new EditedHeights(g, Lattice, TerrainEdits.Empty);

        [Test]
        public void TD06_TheEstimateFindsAShortHeadwallInALongGentleTrail()
        {
            var fp = Strip(400);
            double estimate = TrailRating.Estimate(Ground(new Headwall(55, 60)), fp);
            Assert.That(estimate, Is.GreaterThan(55).And.LessThan(61), "a 150 ft square fits inside the 55 m headwall");
            Assert.That(SlopeBands.Of(estimate), Is.EqualTo(SlopeBands.Band.DoubleBlack));
        }

        [Test]
        public void TD06_ANarrowSteepSliverIsDilutedByTheSquare()
        {
            double estimate = TrailRating.Estimate(Ground(new Headwall(8, 80)), Strip(400));
            Assert.That(estimate, Is.GreaterThan(15).And.LessThan(30), "8 m of 80% in a 46 m square of 10%");
        }

        // ---- TD-07: the posted rating ----

        static DesignHistory New() => new DesignHistory(DesignDocument.Empty(0x4_21));

        static CommandResult Add(DesignHistory h, string name, WorkStage stage, SlopeBands.Band? posted, int x = 0) =>
            h.Submit(new AddTrail(h.Revision, name, TrailSettings.GradedRun, Straight(P(x, 3000), P(x, 1000)), P(x, 3000), P(x, 1000), stage, posted));

        [Test]
        public void TD07_ATrailMustBePostedBeforeItIsBuilt()
        {
            var h = New();
            var r = Add(h, "Run 1", WorkStage.Built, null);
            Assert.That(r.Accepted, Is.False);
            Assert.That(r.ToString(), Does.Contain("Post a rating for Run 1 before building it"));
            Assert.That(Add(h, "Run 1", WorkStage.Plan, null).Accepted, Is.True, "a plan needs no sign");
            Assert.That(Add(h, "Run 2", WorkStage.Built, SlopeBands.Band.Black, 900).Accepted, Is.True);
            Assert.That(h.Document.Trails.Single(t => t.Name == "Run 2").Posted, Is.EqualTo(SlopeBands.Band.Black));
        }

        [Test]
        public void TD07_BreakGroundRefusesAnUnpostedPlanAndBuildsAPostedOne()
        {
            var h = New();
            var plan = new TrailId(Add(h, "Run 1", WorkStage.Plan, null).Created[0]);
            var refused = h.Submit(new BreakGround(h.Revision, new[] { plan }, null));
            Assert.That(refused.Accepted, Is.False);
            Assert.That(refused.ToString(), Does.Contain("Post a rating"));
            var t = h.Document.FindTrail(plan);
            var built = h.Submit(new EditTrail(h.Revision, plan, t.Name, t.Shape, t.Head.Value, t.Tail.Value, t.Settings, WorkStage.Built, SlopeBands.Band.Green));
            Assert.That(built.Accepted, Is.True, built.ToString());
            Assert.That(h.Document.FindTrail(plan).Stage, Is.EqualTo(WorkStage.Built));
            Assert.That(h.Document.FindTrail(plan).Posted, Is.EqualTo(SlopeBands.Band.Green));
        }

        [Test]
        public void TD07_ARegradeKeepsTheSignAndASignChangeIsOneUndoStep()
        {
            var h = New();
            var add = h.Submit(new AddTrail(h.Revision, "Run 1", TrailSettings.NaturalRun, Straight(P(0, 3000), P(0, 1000)), P(0, 3000), P(0, 1000),
                                            WorkStage.Built, SlopeBands.Band.Green));
            var id = new TrailId(add.Created[0]);
            var t = h.Document.FindTrail(id);
            var regrade = h.Submit(new EditTrail(h.Revision, id, t.Name, t.Shape, t.Head.Value, t.Tail.Value, TrailSettings.GradedRun, WorkStage.Built));
            Assert.That(regrade.Accepted, Is.True, regrade.ToString());
            Assert.That(h.Document.FindTrail(id).Posted, Is.EqualTo(SlopeBands.Band.Green), "a regrade never changes the sign");
            t = h.Document.FindTrail(id);
            var sign = h.Submit(new EditTrail(h.Revision, id, t.Name, t.Shape, t.Head.Value, t.Tail.Value, t.Settings, WorkStage.Built, SlopeBands.Band.Black));
            Assert.That(sign.Accepted, Is.True, "a sign change alone is a change");
            Assert.That(h.Document.FindTrail(id).Posted, Is.EqualTo(SlopeBands.Band.Black));
            Assert.That(h.Undo(), Is.True);
            Assert.That(h.Document.FindTrail(id).Posted, Is.EqualTo(SlopeBands.Band.Green));
        }

        [Test]
        public void TD07_EditSectionsCarriesAndPostsTheSign()
        {
            var h = New();
            Assert.That(Add(h, "A", WorkStage.Built, SlopeBands.Band.Blue).Accepted, Is.True);
            var a = h.Document.Trails.Single(t => t.Name == "A").Id;
            var rb = h.Submit(new AddTrail(h.Revision, "B", TrailSettings.GradedRun, Straight(P(0, 1000), P(0, 0)), P(0, 1000), P(0, 0), WorkStage.Built,
                                           SlopeBands.Band.DoubleBlack));
            Assert.That(rb.Accepted, Is.True, rb.ToString());
            var b = new TrailId(rb.Created[0]);
            var ts = Sections(h.Document);
            var route = TrailSectionsEdit.Route(ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]);
            Assert.That(route, Is.Not.Null);
            Assert.That(h.Submit(TrailSectionsEdit.Add(h.Document, h.Revision, ts, ts.TrailOf(a), route)).Accepted, Is.True);
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(b), Is.SameAs(ts.TrailOf(a)));
            Assert.That(ts.TrailOf(a).Posted, Is.EqualTo(SlopeBands.Band.Blue), "the trail that grows keeps its sign");

            var post = h.Submit(TrailSectionsEdit.Post(h.Document, h.Revision, ts, ts.TrailOf(a), SlopeBands.Band.Black));
            Assert.That(post.Accepted, Is.True, post.ToString());
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(a).Posted, Is.EqualTo(SlopeBands.Band.Black));
            Assert.That(h.Undo(), Is.True);
            Assert.That(Sections(h.Document).TrailOf(a).Posted, Is.EqualTo(SlopeBands.Band.Blue));
        }

        [Test]
        public void TD07_AnOlderSavesBuiltTrailsArePostedAsV03ShowedThem()
        {
            var h = New();
            Assert.That(Add(h, "A", WorkStage.Built, SlopeBands.Band.Blue).Accepted, Is.True);
            var a = h.Document.Trails.Single(t => t.Name == "A").Id;
            var b = new TrailId(h.Submit(new AddTrail(h.Revision, "B", TrailSettings.GradedRun, Straight(P(0, 1000), P(0, 0)), P(0, 1000), P(0, 0),
                                                      WorkStage.Built, SlopeBands.Band.Blue)).Created[0]);
            var plan = new TrailId(Add(h, "Glade", WorkStage.Plan, null, 2000).Created[0]);
            var ts = Sections(h.Document);
            Assert.That(h.Submit(TrailSectionsEdit.Add(h.Document, h.Revision, ts, ts.TrailOf(a), TrailSectionsEdit.Route(ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]))).Accepted, Is.True);
            var older = Unposted(h.Document);   // as a v10 save opens
            Assert.That(older.Lines.Count, Is.EqualTo(1));

            var legacy = new Dictionary<TrailId, double> { [a] = 20, [b] = 45, [plan] = 70 };
            var posted = DesignRules.PostOlderSave(older, id => legacy.TryGetValue(id, out var p) ? p : double.NaN);
            Assert.That(posted.FindTrail(a).Posted, Is.EqualTo(SlopeBands.Band.Green));
            Assert.That(posted.FindTrail(b).Posted, Is.EqualTo(SlopeBands.Band.Black));
            Assert.That(posted.FindTrail(plan).Posted, Is.Null, "a plan stays unposted");
            Assert.That(posted.Lines[0].Posted, Is.EqualTo(SlopeBands.Band.Black), "a shaped trail takes its hardest run's, as v0.3 drew it");

            var unrated = DesignRules.PostOlderSave(older, _ => double.NaN);
            Assert.That(unrated, Is.SameAs(older), "nothing rated, nothing posted");
            Assert.That(DesignRules.PostOlderSave(posted, id => 90).FindTrail(a).Posted, Is.EqualTo(SlopeBands.Band.Green), "a sign already posted stays");
        }

        [Test]
        public void TD07_ThePostedBytesRoundTripAndAreInTheDocumentsHash()
        {
            var h = New();
            Add(h, "Run 1", WorkStage.Built, SlopeBands.Band.DoubleBlack);
            var doc = h.Document;
            var back = DesignBytes.Decode(DesignBytes.Encode(doc));
            Assert.That(back.Trails[0].Posted, Is.EqualTo(SlopeBands.Band.DoubleBlack));
            Assert.That(DesignBytes.Same(DesignBytes.Encode(doc), DesignBytes.Encode(Unposted(doc))), Is.False, "the sign is part of the design");
        }
    }
}
