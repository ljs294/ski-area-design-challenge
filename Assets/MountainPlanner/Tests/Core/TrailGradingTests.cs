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
    // Task P3-08: painted footprints, the automatic centreline, grading on the footprint, clearing, the rating and the
    // earthwork fold (rules TR-02, TR-03, GR-01 to GR-08, GR-13, TF-06, TF-10, TF-11, CL-01, CL-02, CL-08, TD-01 to TD-06;
    // owner decisions TL1 to TL4).
    public sealed class TrailGradingTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        // The site's centre: design point (0, 0) sits 1 km into the lattice.
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 1000, North - 1000);
        static readonly TrailId Id = TrailId.Make(77, 1);

        sealed class Plane : IHeightSource
        {
            readonly double _east, _north;
            public Plane(double east, double north) { _east = east; _north = north; }
            public double At(double x, double y) => 1800 + _east * (x - West) + _north * (y - North);
        }

        /// <summary>A slope falling north to south at 10% whose side slope steepens from 30% to 120% between y −120 and −180 (local).</summary>
        sealed class Steepening : IHeightSource
        {
            public double At(double x, double y)
            {
                double ly = y - Site.OriginY, lx = x - Site.OriginX;
                double f = Math.Max(0, Math.Min(1, (-120 - ly) / 60));
                double side = 0.3 + 0.9 * f * f * (3 - 2 * f);
                return 1800 + 0.1 * ly + side * lx;
            }
        }

        sealed class Rolling : IHeightSource
        {
            public double At(double x, double y)
            {
                double s = North - y;
                return 2000 - 0.35 * s + 0.25 * (x - West) + 3 * Math.Sin(s * 0.02) + 0.6 * Math.Sin(x * 0.09) * Math.Cos(y * 0.05);
            }
        }

        static EditedHeights Natural(IHeightSource g) => new EditedHeights(g, Lattice, TerrainEdits.Empty);

        static PointMm M(double x, double y) => PointMm.FromMetres(x, y);

        static TrailShape Strip(double widthFt, params (double x, double y)[] pts) =>
            TrailShape.FromStrokes(null, new[] { new Stroke(Ft((int)widthFt), false, pts.Select(p => M(p.x, p.y)).ToArray()) });

        static TrailFootprint Footprint(TrailShape parts) => TrailFootprint.Build(parts, Lattice, Site.OriginX, Site.OriginY);

        /// <summary>A straight trail down the y axis, head at y = 0, tail at y = −length.</summary>
        static (TrailFootprint fp, TrailBuild build) Straight(EditedHeights ground, double length = 300, int widthFt = 100, bool graded = true,
                                                             TrailShape parts = null)
        {
            parts = parts ?? Strip(widthFt, (0, 0), (0, -length));
            var trail = new Trail(Id, "Run 1", WorkStage.Built, graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun, parts, M(0, 0), M(0, -length));
            var build = TrailEarthwork.Grade(trail, ground, Site, 77, null, null);
            return (build.Footprint, build);
        }

        static EditedHeights Apply(EditedHeights ground, EarthworkWrites writes)
        {
            var tiles = new List<TerrainEditTile>();
            foreach (var key in writes.Tiles)
            {
                var b = ground.Edits.Get(key).Edit();
                writes.ApplyTo(b);
                tiles.Add(b.Build());
            }
            return new EditedHeights(ground.Source, Lattice, ground.Edits.With(tiles));
        }

        static void LocalToLattice(double x, double y, out int c, out int r)
        {
            c = (int)Math.Round(Site.OriginX + x - West);
            r = (int)Math.Round(North - (Site.OriginY + y));
        }

        [Test]
        public void TR02_TheFootprintIsTheStrokesWithIslandsAndPatches()
        {
            var paint = new Stroke(Ft(100), false, new[] { M(0, 0), M(0, -200) });
            var hole = new Stroke(Ft(30), true, new[] { M(0, -100) });
            var fp = Footprint(TrailShape.FromStrokes(null, new[] { paint, hole }));
            Assert.That(fp.Patches, Is.EqualTo(1), "an island leaves one piece");
            Assert.That(fp.Contains(M(0, -100)), Is.False, "the island is unpainted");
            Assert.That(fp.Contains(M(10, -100)), Is.True);
            Assert.That(fp.Cells, Is.EqualTo(30.48 * 200 + Math.PI * 15.24 * 15.24 - Math.PI * 4.572 * 4.572).Within(150), "area to within a cell row");
            var two = Footprint(TrailShape.FromStrokes(null, new[] { paint, new Stroke(Ft(50), false, new[] { M(200, 0), M(200, -50) }) }));
            Assert.That(two.Patches, Is.EqualTo(2), "TF-02: a second patch");
        }

        [Test]
        public void TR03_TheCentrelineKeepsToTheMiddle()
        {
            var fp = Footprint(Strip(100, (0, 0), (0, -300)));
            Site.PointOf(M(0, 0), out double hx, out double hy);
            Site.PointOf(M(0, -300), out double tx, out double ty);
            var line = TrailCentreline.Find(fp, hx, hy, tx, ty);
            Assert.That(line.Found, Is.True);
            for (int k = 0; k < line.Points.Length / 2; k++)
            {
                double x = line.Points[2 * k] - Site.OriginX;
                Assert.That(Math.Abs(x), Is.LessThan(1.0), $"point {k} within a cell of the axis");
            }
            Assert.That(line.LengthMetres, Is.EqualTo(300).Within(3));
        }

        [Test]
        public void TR03_TheCentrelineGoesRoundIslandsByTheWiderSide()
        {
            // A 200 ft trail with a 60 ft island set 20 ft east of the axis: the west channel is wider.
            var paint = new Stroke(Ft(200), false, new[] { M(0, 0), M(0, -300) });
            var hole = new Stroke(Ft(60), true, new[] { M(6.1, -150) });
            var fp = Footprint(TrailShape.FromStrokes(null, new[] { paint, hole }));
            Site.PointOf(M(0, 0), out double hx, out double hy);
            Site.PointOf(M(0, -300), out double tx, out double ty);
            var line = TrailCentreline.Find(fp, hx, hy, tx, ty);
            Assert.That(line.Found, Is.True);
            double atIsland = double.NaN;
            for (int k = 0; k < line.Points.Length / 2; k++)
            {
                Assert.That(fp.ContainsAt(line.Points[2 * k], line.Points[2 * k + 1]), Is.True, $"point {k} on the footprint");
                if (Math.Abs(line.Points[2 * k + 1] - Site.OriginY + 150) < 1) atIsland = line.Points[2 * k] - Site.OriginX;
            }
            // The island spans x 6.1 ± 9.1 m: its west edge is at −3.0 m, the west channel −30.5 to −3.0 m.
            Assert.That(atIsland, Is.LessThan(-3.0 - 6), "west of the island, well clear of it");
        }

        [Test]
        public void GR01_TheBenchIsStraightAcrossTheSectionAtTheSideSlopeCap()
        {
            // GR-01 v2 (owner, TL21, TL23): the bench keeps the ground's side slope up to 20%: on a 30% side slope it runs
            // straight across at 20%, and on a 5% one it keeps the ground's 5%.
            foreach (var (side, want) in new[] { (0.3, 0.20), (0.05, 0.05) })
            {
                var ground = Natural(new Plane(side, 0.1));
                var (fp, build) = Straight(ground);
                Assert.That(build.Grade.UngradedStations, Is.EqualTo(0));
                var h = Apply(ground, build.Writes);
                for (double y = -50; y >= -250; y -= 50)
                {
                    LocalToLattice(0, y, out int c, out int r);
                    double mid = h.AtPoint(c, r), slope = (h.AtPoint(c + 6, r) - h.AtPoint(c - 6, r)) / 12;
                    Assert.That(Math.Abs(slope), Is.EqualTo(want).Within(0.002), $"the bench's side slope at y {y}, on a {side:P0} side slope");
                    for (int d = -6; d <= 6; d++)
                        Assert.That(h.AtPoint(c + d, r), Is.EqualTo(mid + slope * d).Within(0.0021), $"straight across at y {y}, offset {d} (GR-10, whole-mm storage)");
                }
            }
        }

        /// <summary>A 30% fall line with lidar-like rolls along it: ±0.6 m over 20 m.</summary>
        sealed class Rolls : IHeightSource
        {
            public double At(double x, double y)
            {
                double ly = y - Site.OriginY, lx = x - Site.OriginX;
                return 1800 + 0.3 * ly + 0.1 * lx + 0.6 * Math.Sin(ly * 2 * Math.PI / 20);
            }
        }

        [Test]
        public void GR01v3_TheBenchsLongProfileSmoothsTheRollsUnderTheCentreline()
        {
            // GR-01 v3 (owner, TL23): the bench's height along the trail is the ground under the centreline averaged over
            // 75 ft either way, so a skier doesn't feel every lidar roll; the head and tail keep their ground.
            var ground = Natural(new Rolls());
            var (fp, build) = Straight(ground, 300);
            var h = Apply(ground, build.Writes);
            double RmsGradeChange(EditedHeights e)
            {
                double sum = 0;
                int n = 0;
                for (double y = -30; y >= -270; y -= 5)
                {
                    LocalToLattice(0, y + 5, out int c0, out int r0);
                    LocalToLattice(0, y, out _, out int r1);
                    LocalToLattice(0, y - 5, out _, out int r2);
                    double g0 = (e.AtPoint(c0, r1) - e.AtPoint(c0, r0)) / 5, g1 = (e.AtPoint(c0, r2) - e.AtPoint(c0, r1)) / 5;
                    sum += (g1 - g0) * (g1 - g0); n++;
                }
                return Math.Sqrt(sum / n);
            }
            double natural = RmsGradeChange(ground), graded = RmsGradeChange(h);
            Assert.That(graded, Is.LessThan(0.3 * natural), $"rolls {graded * 100:F1}% RMS graded vs {natural * 100:F1}% natural");
            LocalToLattice(0, 0, out int hc, out int hr);
            Assert.That(h.AtPoint(hc, hr), Is.EqualTo(ground.AtPoint(hc, hr)).Within(0.3), "the head keeps its ground");
        }

        [Test]
        public void GR03_TheBenchOnAUniformSidehillIsTheBudgetTimesOneLessTheGradeLeftToCut()
        {
            // GR-03 with GR-01 v2: the faces take up only the side slope the bench doesn't keep, 30% − 20% here.
            var ground = Natural(new Plane(0.3, 0));
            var fp = Footprint(Strip(100, (0, 0), (0, -300)));
            Site.PointOf(M(0, 0), out double hx, out double hy);
            var line = new[] { hx, hy, hx, hy - 300 };
            var g = TrailGrader.GradeFootprint(ground, fp, line, true, 77, Id, null, 0);
            var h = Apply(ground, g.Writes);
            LocalToLattice(0, -150, out int c, out int r);
            double z = h.AtPoint(c, r), slope = (h.AtPoint(c + 3, r) - h.AtPoint(c - 3, r)) / 6;
            Assert.That(Math.Abs(slope), Is.EqualTo(TrailGrader.MaxCrossSlope).Within(0.002));
            int bench = 0;
            for (int d = -20; d <= 20; d++) if (Math.Abs(h.AtPoint(c + d, r) - (z + slope * d)) < 0.002) bench++;
            // 15.24 m either side × (1 − 0.1) = 13.7 m each way: about 28 points on the bench across.
            Assert.That(bench, Is.InRange(26, 30));
        }

        [Test]
        public void GR05_AGradedStretchRampsIntoAnUngradedOne()
        {
            // The owner's report (2026-10-19): graded trails came out stepped where stations too steep for a bench (GR-07)
            // met benched ones: the benched station's level held flat for half the stretch, then dropped to natural. Now the
            // ground ramps across the stretch: walking down the trail at any offset, no row-to-row change is a riser.
            var ground = Natural(new Steepening());
            var (fp, build) = Straight(ground, 300);
            var g = build.Grade;
            Assert.That(g.UngradedStations, Is.InRange(1, g.Benched.Length - 1), "some stations benched, some too steep");
            var h = Apply(ground, build.Writes);
            for (int off = -8; off <= 8; off += 2)
            {
                LocalToLattice(off, -20, out int c, out int r0);
                LocalToLattice(off, -280, out _, out int r1);
                for (int r = r0; r < r1; r++)
                {
                    double step = Math.Abs(h.AtPoint(c, r + 1) - h.AtPoint(c, r));
                    double natural = Math.Abs(ground.AtPoint(c, r + 1) - ground.AtPoint(c, r));
                    Assert.That(step, Is.LessThanOrEqualTo(Math.Max(natural, 0.1) + 0.35), $"no riser at offset {off} m, row {r} (natural {natural:F2} m)");
                }
            }
        }

        [Test]
        public void GR07_TooSteepStaysNaturalAndIsReported()
        {
            var ground = Natural(new Plane(1.2, 0));
            var (fp, build) = Straight(ground);
            Assert.That(build.Grade.UngradedStations, Is.EqualTo(build.Grade.Benched.Length), "a 120% sidehill holds no bench");
            Assert.That(build.Grade.UngradedMetres, Is.EqualTo(build.Grade.LengthMetres).Within(1e-9));
            var h = Apply(ground, build.Writes);
            for (int r = fp.Row0; r <= fp.LastRow; r++)
                for (int c = fp.Column0; c <= fp.LastColumn; c++)
                    Assert.That(h.AtPoint(c, r), Is.EqualTo(ground.AtPoint(c, r)).Within(0.0005));
        }

        [Test]
        public void GR06_NothingOutsideTheFootprintChanges()
        {
            var ground = Natural(new Rolling());
            var paint = new Stroke(Ft(120), false, new[] { M(0, 0), M(20, -150), M(-10, -300) });
            var wide = new Stroke(Ft(250), false, new[] { M(15, -120), M(10, -180) });
            var hole = new Stroke(Ft(40), true, new[] { M(-5, -220) });
            var parts = TrailShape.FromStrokes(null, new[] { paint, wide, hole });
            var (fp, build) = Straight(ground, 300, 100, true, parts);
            var h = Apply(ground, build.Writes);
            int changed = 0;
            for (int r = fp.Row0 - 3; r <= fp.LastRow + 3; r++)
                for (int c = fp.Column0 - 3; c <= fp.LastColumn + 3; c++)
                {
                    bool moved = h.AtPoint(c, r) != ground.AtPoint(c, r);
                    if (moved) changed++;
                    if (!fp.Interior(c, r)) Assert.That(moved, Is.False, $"point {c},{r} lies off the footprint's interior");
                    if (!fp.Contains(c, r)) Assert.That(h.Edits.IsClearedAt(c, r), Is.False, $"cell {c},{r} is off the footprint");
                }
            Assert.That(changed, Is.GreaterThan(1000), "it did grade");
        }

        [Test]
        public void GR13_FacesMeetTheGroundAtEveryEdgeNoSteeperThanOneToOne()
        {
            var ground = Natural(new Rolling());
            var paint = new Stroke(Ft(100), false, new[] { M(0, 0), M(25, -150), M(-15, -300) });
            var wide = new Stroke(Ft(220), false, new[] { M(20, -110), M(15, -170) });
            var hole = new Stroke(Ft(35), true, new[] { M(18, -140) });
            var (fp, build) = Straight(ground, 300, 100, true, TrailShape.FromStrokes(null, new[] { paint, wide, hole }));
            var h = Apply(ground, build.Writes);
            int edges = 0;
            for (int r = fp.Row0; r <= fp.LastRow; r++)
                for (int c = fp.Column0; c <= fp.LastColumn; c++)
                {
                    if (!fp.Interior(c, r)) continue;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            if (dr == 0 && dc == 0 || fp.Interior(c + dc, r + dr)) continue;
                            double dist = dr != 0 && dc != 0 ? Math.Sqrt(2) : 1;
                            Assert.That(Math.Abs(h.AtPoint(c, r) - h.AtPoint(c + dc, r + dr)), Is.LessThanOrEqualTo(dist + 0.002),
                                        $"point {c},{r} beside the edge");
                            edges++;
                        }
                }
            Assert.That(edges, Is.GreaterThan(10), "some changed points touch the edge or the island");
        }

        [Test]
        public void GR08_VolumesAreTheDesignMinusGroundRaster()
        {
            var ground = Natural(new Plane(0.3, 0.15));
            var (fp, build) = Straight(ground);
            var h = Apply(ground, build.Writes);
            double cut = 0, fill = 0;
            for (int r = fp.Row0; r <= fp.LastRow; r++)
                for (int c = fp.Column0; c <= fp.LastColumn; c++)
                {
                    double d = h.AtPoint(c, r) - ground.AtPoint(c, r);
                    if (d > 0) fill += d; else cut -= d;
                }
            Assert.That(build.Grade.CutCubicMetres, Is.EqualTo(cut).Within(cut * 0.001));
            Assert.That(build.Grade.FillCubicMetres, Is.EqualTo(fill).Within(fill * 0.001));
            Assert.That(cut, Is.GreaterThan(100));
            Assert.That(fill, Is.GreaterThan(100));
        }

        [Test]
        public void TF06_ANaturalTrailClearsButChangesNoGround()
        {
            var ground = Natural(new Plane(0.3, 0.1));
            var (fp, build) = Straight(ground, graded: false);
            var h = Apply(ground, build.Writes);
            for (int r = fp.Row0; r <= fp.LastRow; r++)
                for (int c = fp.Column0; c <= fp.LastColumn; c++)
                    Assert.That(h.AtPoint(c, r), Is.EqualTo(ground.AtPoint(c, r)));
            Assert.That(build.Grade.ClearedCells, Is.GreaterThan(fp.Cells * 0.8));
        }

        [Test]
        public void CL01_CL02_CL08_ClearingCoversTheFootprintWithARaggedEdgeAndSparesIslands()
        {
            var ground = Natural(new Plane(0.2, 0));
            var paint = new Stroke(Ft(150), false, new[] { M(0, 0), M(0, -300) });
            var hole = new Stroke(Ft(40), true, new[] { M(0, -150) });
            var (fp, build) = Straight(ground, 300, 100, true, TrailShape.FromStrokes(null, new[] { paint, hole }));
            int band = 0, bandCleared = 0;
            for (int r = fp.Row0; r < fp.Row0 + fp.Height; r++)
                for (int c = fp.Column0; c < fp.Column0 + fp.Width; c++)
                {
                    bool cleared = build.Writes.Clears(c, r);
                    if (!fp.Contains(c, r)) { Assert.That(cleared, Is.False, "CL-08: islands and outside keep their trees"); continue; }
                    double inside = fp.Clearance(c, r);
                    if (inside >= TrailGrader.EdgeBandMetres) Assert.That(cleared, Is.True, "CL-01");
                    else { band++; if (cleared) bandCleared++; }
                }
            Assert.That(bandCleared, Is.GreaterThan(band / 5).And.LessThan(band * 4 / 5), "CL-02: a ragged band, round the island too");
        }

        [Test]
        public void TD01_TD02_ARunStraightDownAPlaneRatesItsGrade()
        {
            var ground = Natural(new Plane(0, 0.3));   // rises 30% to the north: straight down the fall line
            var (fp, build) = Straight(ground);
            var h = Apply(ground, build.Writes);
            var rating = TrailRating.Rate(h, fp, build.Grade);
            Assert.That(rating.MeanPercent, Is.EqualTo(30).Within(0.5));
            Assert.That(rating.ValuePercent, Is.EqualTo(30).Within(1));
            Assert.That(rating.Band, Is.EqualTo(SlopeBands.Band.Blue), "TD-03: 25–40% is More difficult");
            Assert.That(rating.DropMetres, Is.EqualTo(0.3 * 300).Within(3));
        }

        [Test]
        public void TD06_TheEstimateReadsThePaintedCells()
        {
            var ground = Natural(new Plane(0.45, 0));
            var fp = Footprint(Strip(100, (0, 0), (0, -300)));
            Assert.That(TrailRating.Estimate(ground, fp), Is.EqualTo(45).Within(0.01));
        }

        // ---- the earthwork fold (TL3, TF-10, TF-11) ----

        static (DesignHistory h, DesignEarthwork work) Session(IHeightSource g)
        {
            var h = new DesignHistory(DesignDocument.Empty(77));
            var work = new DesignEarthwork(g, Site);
            work.Prime(h.Document, h.Terrain);
            return (h, work);
        }

        static TrailId Add(DesignHistory h, DesignEarthwork work, TrailShape parts, PointMm head, PointMm tail, bool graded = true)
        {
            var cmd = new AddTrail(h.Revision, "Run " + (h.Document.Trails.Count + 1), graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                   parts, head, tail, WorkStage.Built);
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.ToString());
            var rebuilt = work.Rebuild(after.Document, h.Terrain);
            var r = h.Submit(cmd, rebuilt.Edits);
            Assert.That(r.Accepted, Is.True);
            work.Accept(rebuilt);
            return new TrailId(r.Created[0]);
        }

        static void Commit(DesignHistory h, DesignEarthwork work, DesignCommand cmd)
        {
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.ToString());
            var rebuilt = work.Rebuild(after.Document, h.Terrain);
            Assert.That(h.Submit(cmd, rebuilt.Edits).Accepted, Is.True);
            work.Accept(rebuilt);
        }

        static TerrainEdits Fresh(IHeightSource g, DesignDocument doc)
        {
            var w = new DesignEarthwork(g, Site);
            w.Prime(DesignDocument.Empty(doc.Seed), TerrainEdits.Empty);
            return w.Rebuild(doc, TerrainEdits.Empty).Edits;
        }

        static bool Same(TerrainEdits a, TerrainEdits b) => TerrainEdits.Changed(a, b).Count == 0;

        [Test]
        public void TF02_PatchLabelsNameTheTrailAndAnyPatchApart()
        {
            // Painting shows the trail orange and a patch apart red (owner, 2026-10-19): the larger patch is the trail.
            var parts = TrailShape.FromStrokes(null, new[]
            {
                new Stroke(Ft(100), false, new[] { M(0, 0), M(0, -300) }),
                new Stroke(Ft(50), false, new[] { M(200, 0), M(200, -40) }),
            });
            var fp = Footprint(parts);
            Assert.That(fp.Patches, Is.EqualTo(2));
            var labels = fp.PatchLabels(out int main);
            int mainCells = 0, apart = 0;
            foreach (int l in labels) { if (l == main) mainCells++; else if (l != 0) apart++; }
            Assert.That(mainCells + apart, Is.EqualTo(fp.Cells), "every painted cell has a patch");
            Assert.That(mainCells, Is.GreaterThan(apart), "the long run is the trail, the short dab the patch apart");
            Assert.That(fp.ContainsAt(fp.CellX(fp.Column0 + 1), fp.CellY(fp.Row0 + 1)), Is.False, "the margin is unpainted");
        }

        [Test]
        public void TR05_AnEndSnapsToAnyPointOnAnotherTrail()
        {
            // The owner's report (2026-10-19): a head or tail couldn't join midway down a run. TR-05 snaps an end within
            // 30 ft of any point on another trail's centreline, not only its head and tail.
            var g = new Rolling();
            var (h, work) = Session(g);
            var run = Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300), graded: false);
            var lines = new TrailLineSnap(() => work);
            var hit = DesignSnap.Nearest(h.Document, M(5, -150), SnapTargets.All, lines, 0, DesignUnits.SnapMm);
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.TrailLine), "midway down the run, far from its ends");
            Assert.That(hit.Owner, Is.EqualTo(run.Value));
            Assert.That(hit.At.XMetres, Is.EqualTo(0).Within(1.0), "on the centreline, in the middle of the run");
            Assert.That(hit.At.YMetres, Is.EqualTo(-150).Within(1.0));
            Assert.That(DesignSnap.Nearest(h.Document, M(40, -150), SnapTargets.All, lines, 0, DesignUnits.SnapMm).Snapped, Is.False,
                        "40 m off is beyond 30 ft");
            Assert.That(DesignSnap.Nearest(h.Document, M(5, -150), SnapTargets.All, lines, run.Value, DesignUnits.SnapMm).Snapped, Is.False,
                        "a trail never snaps to itself (Edit skips it)");
        }

        [Test]
        public void TL3_DeleteRestoresTheGroundAndTreesExactly()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var id = Add(h, work, Strip(100, (0, 0), (10, -300)), M(0, 0), M(10, -300));
            Assert.That(h.Terrain.IsEmpty, Is.False);
            Commit(h, work, new DeleteTrail(h.Revision, id));
            Assert.That(h.Terrain.IsEmpty, Is.True, "no edits left");
        }

        [Test]
        public void TF10_AnOverlappingTrailKeepsOffTheEarlierOnesGround()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var first = Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            var before = new EditedHeights(g, Lattice, h.Terrain);
            var claimed = work.WritesOf(first.Value);
            Add(h, work, Strip(100, (-150, -100), (150, -200)), M(-150, -100), M(150, -200));
            var after = new EditedHeights(g, Lattice, h.Terrain);
            int kept = 0;
            for (int r = claimed.MinRow; r <= claimed.MaxRow; r++)
                for (int c = claimed.MinColumn; c <= claimed.MaxColumn; c++)
                    if (claimed.Claims(c, r)) { Assert.That(after.AtPoint(c, r), Is.EqualTo(before.AtPoint(c, r))); kept++; }
            Assert.That(kept, Is.GreaterThan(5000));
        }

        [Test]
        public void TL3_EditingOneTrailEqualsBuildingTheDesignFresh()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var a = Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            var b = Add(h, work, Strip(80, (-120, -80), (140, -220)), M(-120, -80), M(140, -220), graded: false);
            Add(h, work, Strip(90, (100, 50), (120, -260)), M(100, 50), M(120, -260));
            Assert.That(Same(h.Terrain, Fresh(g, h.Document)), Is.True, "incremental adds");
            // Repaint the first: the second (which crosses it) must follow.
            Commit(h, work, new RepaintTrail(h.Revision, a, Strip(140, (0, 0), (15, -300))));
            Assert.That(Same(h.Terrain, Fresh(g, h.Document)), Is.True, "after a repaint");
            Commit(h, work, new DeleteTrail(h.Revision, a));
            Assert.That(Same(h.Terrain, Fresh(g, h.Document)), Is.True, "after a delete");
            Commit(h, work, new SetTrailSettings(h.Revision, b, TrailSettings.GradedRun));   // NT47: grading only goes one way once built
            Assert.That(Same(h.Terrain, Fresh(g, h.Document)), Is.True, "after Graded");
            // Undo restores exactly; the next edit after an undo still matches a fresh build.
            h.Undo();
            h.Undo();
            Commit(h, work, new SetTrailSettings(h.Revision, b, TrailSettings.GradedRun));
            Assert.That(Same(h.Terrain, Fresh(g, h.Document)), Is.True, "an edit after undoing two");
        }

        [Test]
        public void TF11_GradingKeepsFifteenFeetRoundALiftsTowersAndTerminals()
        {
            var g = new Plane(0.3, 0.3);
            var (h, work) = Session(g);
            var lift = h.Submit(new AddLift(h.Revision, "Chair 1", LiftCatalogIds.SesselliftFgq4, M(-200, -150), M(200, -150),
                                            TowerMode.Automatic, TowerStance.Plumb, new[] { new TowerSpec(200_000, Ft(35)) }, WorkStage.Built));
            Assert.That(lift.Accepted, Is.True, lift.ToString());
            Add(h, work, Strip(150, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            var e = new EditedHeights(g, Lattice, h.Terrain);
            LocalToLattice(0, -150, out int c, out int r);   // the tower stands on the trail's axis
            for (int dr = -4; dr <= 4; dr++)
                for (int dc = -4; dc <= 4; dc++)
                    if (dr * dr + dc * dc <= 16) Assert.That(e.AtPoint(c + dc, r + dr), Is.EqualTo(g.At(Lattice.X(c + dc), Lattice.Y(r + dr))));
        }

        [Test]
        public void TL3_ACancelledPreviewChangesNothing()
        {
            var g = new Rolling();
            var (h, work) = Session(g);
            var a = Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            // Preview a repaint, and drop it.
            var preview = new RepaintTrail(h.Revision, a, Strip(200, (0, 0), (30, -300))).Apply(h.Document);
            work.Rebuild(preview.Document, h.Terrain);
            Commit(h, work, new RenameTrail(h.Revision, a, "Renamed"));   // a change that moves no ground
            Assert.That(Same(h.Terrain, Fresh(g, h.Document)), Is.True);
        }

        [Test]
        public void TL3_EditsNoPieceExplainsSurviveARebuildAfterPriming()
        {
            var g = new Plane(0.2, 0.2);
            var (h, work) = Session(g);
            Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            // Work from outside the earthwork (a lift before its contributor exists) lands on the trail's tiles.
            LocalToLattice(80, -150, out int c, out int r);
            var key = EditTileKey.Of(c, r);
            var lifted = h.Terrain.With(new[] { h.Terrain.Get(key).Edit().SetDeltaMm(c - key.FirstColumn, r - key.FirstRow, 700).Build() });
            h.Load(h.Document, lifted);
            Add(h, work, Strip(100, (60, 0), (70, -300)), M(60, 0), M(70, -300));
            Assert.That(h.Terrain.DeltaMmAt(c, r), Is.EqualTo(700));
        }

        [Test]
        public void TF07_EditTrailChangesFootprintEndsAndSettingsInOneCommand()
        {
            var h = new DesignHistory(DesignDocument.Empty(77));
            var r = h.Submit(new AddTrail(h.Revision, "Run 1", TrailSettings.NaturalRun, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300), WorkStage.Built));
            var id = new TrailId(r.Created[0]);
            var before = DesignTestKit.Bytes(h);
            var edit = h.Submit(new EditTrail(h.Revision, id, "Upper Run", Strip(150, (0, 0), (10, -300)), M(0, -5), M(10, -295), TrailSettings.GradedRun, WorkStage.Built));
            Assert.That(edit.Accepted, Is.True, edit.ToString());
            var t = h.Document.FindTrail(id);
            Assert.That(t.Name, Is.EqualTo("Upper Run"));
            Assert.That(t.Settings.Grading, Is.EqualTo(TrailGrading.Graded));
            Assert.That(t.Head, Is.EqualTo(M(0, -5)));
            h.Undo();
            Assert.That(DesignTestKit.Bytes(h), Is.EqualTo(before), "one undo restores the document exactly");
            var same = h.Submit(new EditTrail(h.Revision, id, "Run 1", Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300), TrailSettings.NaturalRun, WorkStage.Built));
            Assert.That(same.Accepted, Is.False, "nothing changes");
            var plan = h.Submit(new EditTrail(h.Revision, id, "Run 1", Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300), TrailSettings.GradedRun, WorkStage.Plan));
            Assert.That(plan.Accepted, Is.False, "a built trail stays built");
        }

        [Test]
        public void TL3_ForeignEditsAreKept()
        {
            var g = new Plane(0.2, 0.2);
            var h = new DesignHistory(DesignDocument.Empty(77));
            var foreign = TerrainEdits.Empty.With(new[] { TerrainEditTile.Empty(new EditTileKey(40, 40)).Edit().SetDeltaMm(3, 3, 500).Build() });
            h.Load(h.Document, foreign);
            var work = new DesignEarthwork(g, Site);
            work.Prime(h.Document, h.Terrain);
            Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            Assert.That(h.Terrain.DeltaMmAt(40 * 64 + 3, 40 * 64 + 3), Is.EqualTo(500));
        }
    }
}
