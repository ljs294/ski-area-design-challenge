using System;
using System.Linq;
using System.Security.Cryptography;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.14's ponds and dams on the ground (PD-01…09): the envelopes, the refusals, the flood, the spillway, the volume
    /// on the graded ground and undo, on a synthetic valley: a stream along x = 0 running south (downhill), the ground
    /// rising 8 % to the north and 25 % to either side.
    /// </summary>
    public sealed class PondDamTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 1500, North - 1500);

        /// <summary>Local (TE5) metres: 2,000 m at the stream on y = 0, +8 % north, +25 % away from the stream.</summary>
        sealed class Valley : IHeightSource
        {
            public double At(double x, double y) => 2000 + 0.08 * (y - Site.OriginY) + 0.25 * Math.Abs(x - Site.OriginX);
        }

        static readonly IHeightSource Ground = new Valley();
        static PointMm M(double x, double y) => PointMm.FromMetres(x, y);
        static EditedHeights Bare => new EditedHeights(Ground, Lattice, TerrainEdits.Empty);

        // A 40 m square pond on the valley's east side, 200 m from the stream.
        static readonly PointMm[] Square = { M(180, 280), M(220, 280), M(220, 320), M(180, 320) };
        static Pond SquarePond(int poolMm, int digMm = 3048) => new Pond(new PondId(1), "Test Pond", Square, poolMm, digMm);
        static Dam ValleyDam(double ax = -40, double bx = 40, SpillBank spill = SpillBank.First) =>
            new Dam(new DamId(2), "Test Dam", M(ax, 0), M(bx, 0), 2_010_000, true, spill, 0);

        [Test]
        public void PD03_TheSuggestedPoolIsTheMeanGroundRoundedUpToHalfAMetrePlusOne()
        {
            // The square's mean ground: 2000 + 0.08 × 300 + 0.25 × 200 = 2074 m → 2074.0 + 1 = 2075 m.
            Assert.That(PondEarthwork.SuggestPoolMm(Square, Ground, Site), Is.EqualTo(2_075_000));
            Assert.That(PondRules.SuggestedPoolMm(2074.2), Is.EqualTo(2_075_500));
        }

        [Test]
        public void PD01_TheRingIsTheWaterlineAndTheBermSitsOutboard()
        {
            var b = PondEarthwork.Analyse(SquarePond(2_075_000), Bare, Site);
            Assert.That(b.Problem, Is.Null);
            var g = Graded(Doc(SquarePond(2_075_000), null));
            // Inside: under full pool everywhere; outside the downhill (south) shore: the crest, 2 ft over full pool.
            Assert.That(Height(g, 200, 300), Is.LessThan(2075.0));
            Assert.That(Height(g, 200, 278), Is.EqualTo(2075.6).Within(0.002), "the crest stands 2 ft over full pool just outboard");
            Assert.That(b.BermMaxMetres, Is.GreaterThan(0.5), "the downhill shore needs a berm");
            Assert.That(b.CrestMetres, Is.EqualTo(2075.6).Within(1e-9));
            Assert.That(b.RingSquareMetres, Is.EqualTo(1600).Within(1e-9));
            Assert.That(b.VolumeCubicMetres, Is.GreaterThan(1000));
            Assert.That(b.CutCubicMetres, Is.GreaterThan(0));
            Assert.That(b.FillCubicMetres, Is.GreaterThan(0));
        }

        [Test]
        public void PD02_ACrossedRingATinyRingOrADeepDigIsRefused()
        {
            Assert.That(PondRules.RingProblem(new[] { M(0, 0), M(40, 40), M(40, 0), M(0, 40) }), Does.Contain("crosses itself"));
            Assert.That(PondRules.RingProblem(new[] { M(0, 0), M(5, 0), M(5, 5), M(0, 5) }), Does.Contain("1,100 ft²"));
            Assert.That(PondRules.RingProblem(new[] { M(0, 0), M(40, 0) }), Does.Contain("three points"));
            Assert.That(PondRules.PondProblem(SquarePond(2_075_000, 16_000)), Does.Contain("50 ft"));
            Assert.That(PondRules.PondProblem(SquarePond(2_075_000)), Is.Null);
        }

        [Test]
        public void PD02_ABermOver65FtIsRefused()
        {
            var b = PondEarthwork.Analyse(SquarePond(2_105_000), Bare, Site);
            Assert.That(b.Problem, Does.Contain("berm").And.Contain("65 ft"));
            Assert.That(b.BermMaxMetres, Is.GreaterThan(20));
        }

        [Test]
        public void PD02_APoolUnderTheGroundIsRefused()
        {
            var b = PondEarthwork.Analyse(SquarePond(2_060_000, 0), Bare, Site);
            Assert.That(b.Problem, Does.Contain("Raise the pool"));
        }

        [Test]
        public void PD04_PD09_TheVolumeIsMeasuredOnTheGradedGround()
        {
            var pond = SquarePond(2_075_000);
            var b = PondEarthwork.Analyse(pond, Bare, Site);
            var g = Graded(Doc(pond, null));
            double vol = 0;
            foreach (var (c, r) in b.Wet.Points()) vol += 2075.0 - g.AtPoint(c, r);
            Assert.That(b.VolumeCubicMetres, Is.EqualTo(vol).Within(1e-6), "Σ (full pool − graded height) × 1 m² over the wet points");
            Assert.That(b.AreaSquareMetres, Is.EqualTo(b.Wet.Count));
            Assert.That(b.MaxDepthMetres, Is.GreaterThanOrEqualTo(3.048 - 0.001), "the dig reaches its floor (ground already lower is kept, PD-01)");
        }

        [Test]
        public void PD05_PD06_ADamHoldsItsReservoirUpstream()
        {
            var b = DamEarthwork.Analyse(ValleyDam(), Bare, Site);
            Assert.That(b.Problem, Is.Null);
            Assert.That(b.HeightMaxMetres, Is.EqualTo(10.6).Within(0.05), "crest 2 ft over full pool, over the stream at 2,000 m");
            Assert.That(b.LengthMetres, Is.EqualTo(80).Within(1e-9));
            // Water stands north (upstream) only: the valley below full pool reaches y = 125 m on the stream.
            var wet = b.Wet.Points().Select(p => (x: Lattice.X(p.Column) - Site.OriginX, y: Lattice.Y(p.Row) - Site.OriginY)).ToList();
            Assert.That(wet.All(p => p.y > 0), Is.True, "nothing floods downstream");
            Assert.That(wet.Max(p => p.y), Is.EqualTo(125).Within(1.5));
            Assert.That(b.VolumeCubicMetres, Is.GreaterThan(5000));
            Assert.That(b.FillCubicMetres, Is.GreaterThan(0));
            Assert.That(b.CutCubicMetres, Is.EqualTo(0), "an embankment is fill only");
        }

        [Test]
        public void PD06_WaterThatRunsRoundTheEndOfADamIsRefused()
        {
            // Bank A sits 5 m under full pool (20 m from the stream): the water goes round it.
            var b = DamEarthwork.Analyse(ValleyDam(-20, 40), Bare, Site);
            Assert.That(b.Problem, Does.Contain("round the end"));
        }

        [Test]
        public void PD06_TheWaterSideDecidesTheFlood()
        {
            Assert.That(DamEarthwork.UpstreamLeftOf(M(-40, 0), M(40, 0), M(0, 50)), Is.True);
            Assert.That(DamEarthwork.UpstreamLeftOf(M(40, 0), M(-40, 0), M(0, 50)), Is.False);
            // The same dam with the water side wrong floods downstream: there the valley runs on below full pool to the edge.
            var wrong = new Dam(new DamId(2), "Test Dam", M(-40, 0), M(40, 0), 2_010_000, false, SpillBank.First, 0);
            Assert.That(DamEarthwork.Analyse(wrong, Bare, Site).Problem, Does.Contain("1,640 ft"));
        }

        /// <summary>The valley again, but the ground sits a fraction of a millimetre off whole millimetres.</summary>
        sealed class OffsetValley : IHeightSource
        {
            readonly double _off;
            public OffsetValley(double off) { _off = off; }
            public double At(double x, double y) => Ground.At(x, y) + _off;
        }

        [Test]
        public void PD07_TheSpillwayNeverLetsWaterOverAtFullPool()
        {
            // Lidar heights fall between whole millimetres: the notch's deck must still hold the water at full pool, for
            // every fraction (rounded to the nearest millimetre, about half its points stood a hair under and leaked).
            foreach (double off in new[] { 0.0001, 0.0003, 0.00049, 0.0007, 0.0009 })
            foreach (var spill in new[] { SpillBank.First, SpillBank.Far })
            {
                var ground = new EditedHeights(new OffsetValley(off), Lattice, TerrainEdits.Empty);
                var b = DamEarthwork.Analyse(ValleyDam(spill: spill), ground, Site);
                Assert.That(b.Problem, Is.Null, $"offset {off} m, {spill}");
                Assert.That(b.Wet.Points().All(p => Lattice.Y(p.Row) - Site.OriginY > 0), Is.True, $"offset {off} m, {spill}: nothing floods downstream");
            }
        }

        /// <summary>A stream that comes into the dam on a slant: south of y = 0 it runs down x = 0, north of it along x = y.</summary>
        sealed class BentValley : IHeightSource
        {
            public double At(double x, double y)
            {
                double lx = x - Site.OriginX, ly = y - Site.OriginY;
                return 2000 + 0.3 * ly + 0.25 * Math.Abs(lx - Math.Max(0, ly));
            }
        }

        [Test]
        public void PD06_TheReservoirIsFoundWhereverTheStreamComesIn()
        {
            // Straight out from the dam's lowest point (x = 0) the ground climbs over full pool, but the stream, and the
            // water, come in from the north-east: the flood starts from all along the water face, not from one point.
            var ground = new EditedHeights(new BentValley(), Lattice, TerrainEdits.Empty);
            var b = DamEarthwork.Analyse(ValleyDam(), ground, Site);
            Assert.That(b.Problem, Is.Null);
            Assert.That(b.AreaSquareMetres, Is.GreaterThan(PondRules.MinAreaSquareMetres));
            var wet = b.Wet.Points().Select(p => (x: Lattice.X(p.Column) - Site.OriginX, y: Lattice.Y(p.Row) - Site.OriginY)).ToList();
            Assert.That(wet.All(p => p.y > 0), Is.True, "nothing floods downstream");
            Assert.That(wet.Average(p => p.x), Is.GreaterThan(5), "the water lies up the slanted stream, to the east");
        }

        /// <summary>The valley with a cliff below the dam: from 5 m south of y = 0 the ground drops 1:1 more.</summary>
        sealed class CliffValley : IHeightSource
        {
            public double At(double x, double y)
            {
                double ly = y - Site.OriginY;
                return Ground.At(x, y) + (ly < -5 ? ly + 5 : 0);
            }
        }

        [Test]
        public void PD06_ADamOnACliffEdgeIsRefused()
        {
            // The 2:1 downstream face falls slower than the ground: it never meets it, and the fill would run off the cliff.
            var ground = new EditedHeights(new CliffValley(), Lattice, TerrainEdits.Empty);
            var b = DamEarthwork.Analyse(ValleyDam(), ground, Site);
            Assert.That(b.Problem, Does.Contain("cliff edge"));
        }

        [Test]
        public void PD06_ADamOver130FtIsRefused()
        {
            // Banks 180 m out on each side: full pool 2,045 m, so the crest stands 45.6 m over the stream.
            var tall = new Dam(new DamId(2), "Tall", M(-180, 0), M(180, 0), 2_045_000, true, SpillBank.First, 0);
            Assert.That(DamEarthwork.Analyse(tall, Bare, Site).Problem, Does.Contain("130 ft"));
        }

        [Test]
        public void PD07_TheSpillwayNotchSitsAtFullPoolAtItsBank()
        {
            foreach (var spill in new[] { SpillBank.First, SpillBank.Far })
            {
                var g = Graded(Doc(null, ValleyDam(spill: spill)));
                double inNotch = spill == SpillBank.First ? -37 : 37, onCrest = spill == SpillBank.First ? 37 : -37;
                Assert.That(Height(g, inNotch, 0), Is.EqualTo(2010.0).Within(0.002), $"{spill}: the notch is at full pool");
                Assert.That(Height(g, onCrest, 0), Is.EqualTo(2010.6).Within(0.002), $"{spill}: the other bank's crest is 2 ft higher");
                Assert.That(Height(g, 0, 0), Is.EqualTo(2010.6).Within(0.002), $"{spill}: the crest over the stream");
            }
        }

        [Test]
        public void PD08_RemoveAndUndoGiveByteIdenticalHeights()
        {
            var h = new DesignHistory(DesignDocument.Empty(77));
            var work = new DesignEarthwork(Ground, Site);
            work.Prime(h.Document, h.Terrain);
            h.Changed += s => work.Follow(s.Document, s.Terrain);
            var empty = TerrainEditBytes.Encode(h.Terrain);

            Commit(h, work, new AddPond(h.Revision, "Base Pond", Square, 2_075_000, 3_048));
            var withPond = TerrainEditBytes.Encode(h.Terrain);
            Commit(h, work, new AddDam(h.Revision, "Twin Dam", M(-40, 0), M(40, 0), 2_010_000, true, SpillBank.First, 0));
            var withBoth = TerrainEditBytes.Encode(h.Terrain);
            Assert.That(withPond, Is.Not.EqualTo(empty));
            Assert.That(withBoth, Is.Not.EqualTo(withPond));
            Assert.That(withBoth, Is.EqualTo(TerrainEditBytes.Encode(DesignEarthwork.Regrade(Ground, Site, h.Document))), "the fold from nothing gives the same ground");

            h.Undo();
            Assert.That(TerrainEditBytes.Encode(h.Terrain), Is.EqualTo(withPond));
            h.Redo();
            Assert.That(TerrainEditBytes.Encode(h.Terrain), Is.EqualTo(withBoth));

            // Remove (PD-08, MapLibre left the berm): the ground comes back exactly.
            ulong pond = h.Document.Ponds[0].Id.Value, dam = h.Document.Dams[0].Id.Value;
            Commit(h, work, new RemoveWater(h.Revision, dam));
            Assert.That(TerrainEditBytes.Encode(h.Terrain), Is.EqualTo(withPond));
            Commit(h, work, new RemoveWater(h.Revision, pond));
            Assert.That(TerrainEditBytes.Encode(h.Terrain), Is.EqualTo(empty));
        }

        [Test]
        public void PD08_PondsAndDamsMayNotOverlap()
        {
            var r = new AddPond(0, "A", Square, 2_075_000, 3_048).Apply(DesignDocument.Empty(1));
            Assert.That(r.Accepted, Is.True, r.Reason);
            var shifted = Square.Select(p => new PointMm(p.X + 20_000, p.Y)).ToArray();
            var again = new AddPond(0, "B", shifted, 2_075_000, 3_048).Apply(r.Document);
            Assert.That(again.Reason, Does.Contain("overlaps A"));
            var across = new AddDam(0, "D", M(170, 300), M(230, 300), 2_075_000, true, SpillBank.First, 0).Apply(r.Document);
            Assert.That(across.Reason, Does.Contain("runs into A"));
        }

        // Golden ground (SH-04): the pond and the dam folded on the valley give the same tiles every time.
        const string GoldenEditsSha = "4681b3242368e36d1be165bf34f08ae82eb40d85844886444b5ff0b7874446b6";

        [Test]
        public void PD08_TheFoldedGroundIsGolden()
        {
            var doc = new AddPond(0, "Base Pond", Square, 2_075_000, 3_048).Apply(DesignDocument.Empty(77)).Document;
            doc = new AddDam(0, "Twin Dam", M(-40, 0), M(40, 0), 2_010_000, true, SpillBank.First, 0).Apply(doc).Document;
            var a = TerrainEditBytes.Encode(DesignEarthwork.Regrade(Ground, Site, doc));
            var b = TerrainEditBytes.Encode(DesignEarthwork.Regrade(Ground, Site, doc));
            Assert.That(a, Is.EqualTo(b));
            using var sha = SHA256.Create();
            Assert.That(BitConverter.ToString(sha.ComputeHash(a)).Replace("-", "").ToLowerInvariant(), Is.EqualTo(GoldenEditsSha));
        }

        [Test]
        public void PondsAndReservoirsBecomeWaterBodies()
        {
            var doc = Doc(SquarePond(2_075_000), ValleyDam());
            var work = new DesignEarthwork(Ground, Site);
            work.Prime(DesignDocument.Empty(doc.Seed), TerrainEdits.Empty);
            var result = work.Rebuild(doc, TerrainEdits.Empty);
            // 8 m cells over ±400 m of the site's centre.
            var lakes = Domain.Water.WaterBodyTable.Empty(100, 100, -400_000, -400_000, 30, PondRules.MaxWater);
            var t = DesignWater.With(lakes, doc, result.DetailOf, Site);
            Assert.That(t.Count, Is.EqualTo(lakes.Count + 2));
            var pond = t[t.IndexOf(doc.Ponds[0].Id.Value)];
            var reservoir = t[t.IndexOf(doc.Dams[0].Id.Value)];
            Assert.That(pond.Kind, Is.EqualTo(Domain.Water.WaterBodyKind.Pond));
            Assert.That(reservoir.Kind, Is.EqualTo(Domain.Water.WaterBodyKind.Reservoir));
            Assert.That(pond.Cells * 64, Is.EqualTo(1600).Within(400), "about the ring's 1,600 m²");
            Assert.That(reservoir.SurfaceFeet, Is.EqualTo((int)Math.Round(2010 / 0.3048)));
            Assert.That(DesignWater.With(lakes, doc, result.DetailOf, Site).Hash, Is.EqualTo(t.Hash), "the same design, the same table");
        }

        static void Commit(DesignHistory h, DesignEarthwork work, DesignCommand cmd)
        {
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.ToString());
            var rebuilt = work.Rebuild(after.Document, h.Terrain);
            var r = h.Submit(cmd, rebuilt.Edits);
            Assert.That(r.Accepted, Is.True, r.ToString());
            Assert.That(work.Accept(rebuilt), Is.True);
        }

        // Local metres sit on lattice points here (the origin is 1,500 m from the lattice's corner).
        static double Height(EditedHeights g, double x, double y) => g.At(Site.OriginX + x, Site.OriginY + y);

        static DesignDocument Doc(Pond pond, Dam dam)
        {
            var doc = DesignDocument.Empty(77);
            if (pond != null) doc = new AddPond(0, pond.Name, pond.Ring, pond.FullPoolMm, pond.ExcavationMm).Apply(doc).Document;
            if (dam != null) doc = new AddDam(0, dam.Name, dam.BankA, dam.BankB, dam.FullPoolMm, dam.UpstreamLeft, dam.Spill, dam.StreamWay, dam.StartsEmpty).Apply(doc).Document;
            return doc;
        }

        static EditedHeights Graded(DesignDocument doc) => new EditedHeights(Ground, Lattice, DesignEarthwork.Regrade(Ground, Site, doc));

        sealed class Cone : IHeightSource
        {
            public double At(double x, double y) => Math.Sqrt(x * x + y * y);
        }

        // F3: the Dam tool's yellow line at full pool round the first bank, where the far bank can land.
        [Test]
        public void F3_ThePoolContourRingsTheFirstBankAtFullPool()
        {
            var segments = new System.Collections.Generic.List<double>();
            PendingContours.AtLevel(new Cone(), 0, 0, 100, 2, 50, segments);
            Assert.That(segments.Count % 5, Is.EqualTo(0));
            Assert.That(segments.Count / 5, Is.GreaterThan(100), "a ring of segments");
            for (int k = 0; k < segments.Count; k += 5)
            {
                Assert.That(Math.Sqrt(segments[k] * segments[k] + segments[k + 1] * segments[k + 1]), Is.EqualTo(50).Within(0.2));
                Assert.That(segments[k + 4], Is.EqualTo(50));
            }
            PendingContours.AtLevel(new Cone(), 0, 0, 30, 2, 50, segments);
            Assert.That(segments, Is.Empty, "the level lies past the radius");
        }
    }
}
