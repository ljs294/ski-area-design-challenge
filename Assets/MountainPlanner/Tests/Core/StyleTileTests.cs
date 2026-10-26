using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Persistence.Saves;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // The style tile's grader, clearing and rope (task P3-07; rules GR-01 to GR-08, GR-12, CL-01 to CL-03, RP-03, RP-05,
    // TW-01, TW-04, TW-14, LC-02, LC-03).
    public sealed class StyleTileTests
    {
        sealed class Plane : IHeightSource
        {
            readonly double _east, _north;
            public Plane(double east, double north) { _east = east; _north = north; }
            public double At(double x, double y) => 1800 + _east * (x - West) + _north * (y - North);
        }

        /// <summary>A valley floor that rises into a hill: flat, then 40% up, with a gentle wave.</summary>
        sealed class Rolling : IHeightSource
        {
            public double At(double x, double y)
            {
                double s = North - y;
                return 2000 - 0.35 * s + 0.25 * (x - West) + 3 * Math.Sin(s * 0.02) + 0.6 * Math.Sin(x * 0.09) * Math.Cos(y * 0.05);
            }
        }

        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly TrailId Id = TrailId.Make(77, 1);

        static EditedHeights Natural(IHeightSource g) => new EditedHeights(g, Lattice, TerrainEdits.Empty);

        /// <summary>A curving centreline down the fall line (north to south), head first.</summary>
        static double[] Curve() => new[]
        {
            West + 300, North - 100,
            West + 320, North - 250,
            West + 360, North - 380,
            West + 350, North - 520,
        };

        static TrailGrader.Result Grade(EditedHeights h, double round = 0, bool graded = true) =>
            TrailGrader.Grade(h, Curve(), 30.48, graded, 77, Id, round);

        [Test]
        public void GR01_TheBenchIsLevelAcrossAtTheCentrelineGround()
        {
            var natural = Natural(new Plane(0.3, 0.2));
            var result = Grade(natural);
            var h = new EditedHeights(natural.Source, Lattice, result.Edits);
            Assert.That(result.UngradedStations, Is.EqualTo(0), "a 30% sidehill holds a bench");
            // The first segment runs due south at x = 300 to 320; the bench at the head station is level at the ground.
            Assert.That(h.AtPoint(300, 100), Is.EqualTo(natural.AtPoint(300, 100)).Within(0.001), "head station");
            // Across a station square to the centreline the bench is level, to 1 mm (GR-10).
            var stations = TrailGrader.Stations(Curve(), out _);
            int k = 20;
            double cx = stations[2 * k], cy = stations[2 * k + 1];
            double tx = stations[2 * k + 2] - stations[2 * k - 2], ty = stations[2 * k + 3] - stations[2 * k - 1];
            double len = Math.Sqrt(tx * tx + ty * ty);
            double nx = -ty / len, ny = tx / len;
            var across = Enumerable.Range(-3, 7).Select(d => h.At(cx + nx * d, cy + ny * d)).ToArray();
            Assert.That(across.Max() - across.Min(), Is.LessThan(0.08), "level across (bilinear between lattice points on a curve)");
        }

        [Test]
        public void GR02_FacesAreNoSteeperThanOneToOneAcrossTheSection()
        {
            var natural = Natural(new Rolling());
            var h = new EditedHeights(natural.Source, Lattice, Grade(natural).Edits);
            var stations = TrailGrader.Stations(Curve(), out _);
            int count = stations.Count / 2, checkedFaces = 0;
            for (int k = 2; k <= 17; k += 3)   // the straight first stretch, where the station's normal is the section's
            {
                double cx = stations[2 * k], cy = stations[2 * k + 1];
                double tx = stations[2 * k + 2] - stations[2 * k - 2], ty = stations[2 * k + 3] - stations[2 * k - 1];
                double len = Math.Sqrt(tx * tx + ty * ty);
                double nx = -ty / len, ny = tx / len;
                foreach (int side in new[] { 1, -1 })
                    for (double d = 0; d < 15; d += 0.5)
                    {
                        double a = h.At(cx + side * nx * d, cy + side * ny * d), b = h.At(cx + side * nx * (d + 0.5), cy + side * ny * (d + 0.5));
                        double na = natural.At(cx + side * nx * d, cy + side * ny * d), nb = natural.At(cx + side * nx * (d + 0.5), cy + side * ny * (d + 0.5));
                        if (Math.Abs(a - na) < 0.01 && Math.Abs(b - nb) < 0.01) continue;   // natural ground
                        checkedFaces++;
                        Assert.That(Math.Abs(b - a) / 0.5, Is.LessThanOrEqualTo(1.05), $"station {k}, side {side}, {d} m out (bilinear between lattice points)");
                    }
            }
            Assert.That(checkedFaces, Is.GreaterThan(40));
        }

        [Test]
        public void GR06_NothingChangesOutsideTheFootprint()
        {
            var natural = Natural(new Rolling());
            var result = Grade(natural);
            var h = new EditedHeights(natural.Source, Lattice, result.Edits);
            var stations = TrailGrader.Stations(Curve(), out _);
            var along = new double[stations.Count / 2];
            for (int k = 1; k < along.Length; k++)
                along[k] = along[k - 1] + Math.Sqrt(Math.Pow(stations[2 * k] - stations[2 * k - 2], 2) + Math.Pow(stations[2 * k + 1] - stations[2 * k - 1], 2));
            var located = TrailGrader.Locate(stations, along, Lattice, 200, 50, 260, 520, 0, 0, 20);
            int changed = 0, outsideChanged = 0;
            for (int j = 0; j < 520; j++)
                for (int i = 0; i < 260; i++)
                {
                    int c = 200 + i, r = 50 + j;
                    if (h.AtPoint(c, r) == natural.AtPoint(c, r)) continue;
                    changed++;
                    if (located.Dist[j * 260 + i] > 15.24) outsideChanged++;
                }
            Assert.That(changed, Is.GreaterThan(1000));
            Assert.That(outsideChanged, Is.EqualTo(0), "byte-identical heights outside the footprint");
            // And no cliff at its edge: within the last metre the change is at most a metre of 1:1 face.
            Assert.That(result.EdgeStepMetres, Is.LessThanOrEqualTo(1.05));
        }

        [Test]
        public void GR07_TooSteepSidehillsStayNaturalAndAreReported()
        {
            var natural = Natural(new Plane(1.2, 0.1));
            var result = Grade(natural);
            Assert.That(result.UngradedStations, Is.EqualTo(result.Stations));
            Assert.That(result.UngradedMetres, Is.GreaterThan(0));
            Assert.That(result.CutCubicMetres + result.FillCubicMetres, Is.EqualTo(0));
        }

        [Test]
        public void GR08_CutAndFillAreSummedOverTheRaster()
        {
            var natural = Natural(new Plane(0.3, 0.2));
            var result = Grade(natural);
            var h = new EditedHeights(natural.Source, Lattice, result.Edits);
            double cut = 0, fill = 0;
            for (int r = 50; r < 580; r++)
                for (int c = 240; c < 420; c++)
                {
                    double d = h.AtPoint(c, r) - natural.AtPoint(c, r);
                    if (d > 0) fill += d; else cut -= d;
                }
            Assert.That(result.CutCubicMetres, Is.EqualTo(cut).Within(cut * 0.001));
            Assert.That(result.FillCubicMetres, Is.EqualTo(fill).Within(fill * 0.001));
            Assert.That(cut, Is.GreaterThan(1000));
            Assert.That(fill, Is.GreaterThan(1000), "a sidehill bench cuts on the uphill side and fills on the downhill side");
        }

        [Test]
        public void GR12_RoundedTieInsSoftenTheEdgeWithoutLeavingTheFootprint()
        {
            var natural = Natural(new Rolling());
            var sharp = Grade(natural);
            var round = Grade(natural, 1.524);
            Assert.That(round.EdgeStepMetres, Is.LessThanOrEqualTo(sharp.EdgeStepMetres + 0.01));
            Assert.That(round.Tiles, Is.EquivalentTo(sharp.Tiles));
            // The bench itself doesn't move: on the centreline, rounded and sharp agree to 1 mm (GR-10), and the volumes
            // stay close.
            var hs = new EditedHeights(natural.Source, Lattice, sharp.Edits);
            var hr = new EditedHeights(natural.Source, Lattice, round.Edits);
            var stations = TrailGrader.Stations(Curve(), out _);
            for (int k = 1; k < stations.Count / 2 - 1; k += 5)
                Assert.That(hr.At(stations[2 * k], stations[2 * k + 1]), Is.EqualTo(hs.At(stations[2 * k], stations[2 * k + 1])).Within(0.002), $"station {k}");
            // Rounding inside the footprint narrows the bench where a face reaches the edge, so the volumes drop a little.
            Assert.That(round.CutCubicMetres, Is.LessThanOrEqualTo(sharp.CutCubicMetres + 1).And.GreaterThan(sharp.CutCubicMetres * 0.6));
            Assert.That(round.FillCubicMetres, Is.LessThanOrEqualTo(sharp.FillCubicMetres + 1).And.GreaterThan(sharp.FillCubicMetres * 0.6));
        }

        [Test]
        public void SH04_GradingIsDeterministic()
        {
            var natural = Natural(new Rolling());
            var a = TerrainEditBytes.Encode(Grade(natural).Edits);
            var b = TerrainEditBytes.Encode(Grade(natural).Edits);
            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void CL01_CL02_TheFootprintIsClearedWithARaggedEdge()
        {
            var natural = Natural(new Plane(0.1, 0.2));
            var result = Grade(natural);
            var edits = result.Edits;
            // Down the straight first stretch (x ≈ 300 at row 150), cells well inside are cleared, cells outside aren't,
            // and the 10 ft band at each edge is mixed.
            int row = 150;
            Assert.That(edits.IsClearedAt(300, row), Is.True, "the middle");
            Assert.That(edits.IsClearedAt(330, row), Is.False, "outside (the centreline is at x ≈ 307 here)");
            int band = 0, bandCleared = 0;
            for (int r = 120; r < 220; r++)
                for (int c = 280; c < 330; c++)
                {
                    // Locate roughly: the first segment runs from (300, 100) to (320, 250).
                    double x = c + 0.5, y = r + 0.5;
                    double t = Math.Max(0, Math.Min(1, ((x - 300) * 20 + (y - 100) * 150) / (20.0 * 20 + 150.0 * 150)));
                    double dx = x - (300 + 20 * t), dy = y - (100 + 150 * t);
                    double inside = 15.24 - Math.Sqrt(dx * dx + dy * dy);
                    if (inside <= 0.3 || inside >= TrailGrader.EdgeBandMetres - 0.3) continue;
                    band++;
                    if (edits.IsClearedAt(c, r)) bandCleared++;
                }
            Assert.That(band, Is.GreaterThan(200));
            Assert.That((double)bandCleared / band, Is.InRange(0.3, 0.7), "about half the band is cleared");
            Assert.That(result.ClearedCells, Is.GreaterThan(10_000));
        }

        [Test]
        public void TF06_ANaturalTrailOnlyClears()
        {
            var natural = Natural(new Rolling());
            var result = Grade(natural, 0, graded: false);
            var h = new EditedHeights(natural.Source, Lattice, result.Edits);
            Assert.That(h.AtPoint(300, 150), Is.EqualTo(natural.AtPoint(300, 150)));
            Assert.That(result.ClearedCells, Is.GreaterThan(10_000));
        }

        [Test]
        public void CL03_ALiftClearsAStraightCorridor()
        {
            var edits = LiftClearing.Corridor(TerrainEdits.Empty, Lattice, West + 100, North - 500, West + 100, North - 100, out int cleared);
            Assert.That(edits.IsClearedAt(100, 300), Is.True);
            Assert.That(edits.IsClearedAt(107, 300), Is.True, "8 m off the line, inside the 8.38 m half-width");
            Assert.That(edits.IsClearedAt(109, 300), Is.False);
            Assert.That(cleared, Is.EqualTo(17 * 400).Within(400), "16.8 m × 400 m");
        }

        [Test]
        public void CL09_ATerminalClearsItsFootprintAMarginAndItsStationZone()
        {
            // A terminal at (100, 100) facing north (+y), 10 m behind its bullwheel, 6 m in front, 4 m either side.
            var e = LiftClearing.Terminal(TerrainEdits.Empty, Lattice, West + 100, North - 100, 0, 1, 10, 6, 4, out int cleared);
            Assert.That(e.IsClearedAt(100, 115), Is.True, "15 m behind: inside 10 m + the 20 ft margin");
            Assert.That(e.IsClearedAt(100, 118), Is.False, "18 m behind: outside");
            Assert.That(e.IsClearedAt(100, 100 - 26), Is.True, "26 m in front: inside 6 m + 20 ft + the 50 ft station zone");
            Assert.That(e.IsClearedAt(100, 100 - 29), Is.False);
            Assert.That(e.IsClearedAt(109, 100), Is.True, "9 m to the side: inside 4 m + 20 ft");
            Assert.That(e.IsClearedAt(111, 100), Is.False);
            Assert.That(cleared, Is.GreaterThan(800));
        }

        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

        static LiftRope Line(IHeightSource ground, TowerStance stance, params (double along, double height)[] towers)
        {
            var list = towers.Select((t, i) => new Tower(TowerId.Make(5, i), (int)(t.along * 1000), (int)(t.height * 1000))).ToList();
            double bx = West + 100, by = North - 900, tx = West + 100, ty = North - 100;
            double b0 = ground.At(bx, by), t0 = ground.At(tx, ty);
            return LiftRope.Solve(Quad, ground, bx, by, tx, ty, list, stance, 6, b0 + 3.04 + 1.5, 4, t0 + 3.04);
        }

        [Test]
        public void LC02_LC03_TheQuadCarries2400PeoplePerHour()
        {
            Assert.That(Quad.Capacity, Is.EqualTo(2400).Within(1));
            // RP-05's allowed rise with LC-02 v2's 1¾ in rope: (rated − counterweight) ÷ loaded load, about 1,130 ft.
            double rise = (Quad.RatedTensionNewtons - Quad.CounterweightNewtons) / Quad.LoadedNewtonsPerMetre;
            Assert.That(rise / 0.3048, Is.InRange(1100, 1160));
            Assert.That(Quad.RopeDiameterMetres, Is.EqualTo(0.04445).Within(1e-5), "SK17: 1¾ in");
        }

        [Test]
        public void RP03_SpansSagUnderTheirLoadAndMeetTheirEnds()
        {
            var ground = new Plane(0, 0.3);
            var rope = Line(ground, TowerStance.Plumb, (200, 10.67), (400, 10.67), (600, 10.67));
            foreach (var spans in new[] { rope.Loaded, rope.Empty })
                for (int k = 0; k < spans.Count; k++)
                {
                    var s = spans[k];
                    double mid = (s.From + s.To) / 2;
                    double chord = (s.Y(s.From) + s.Y(s.To)) / 2;
                    Assert.That(chord - s.Y(mid), Is.GreaterThan(0.3), $"span {k} sags");
                    if (k > 0) Assert.That(s.Y(s.From), Is.EqualTo(spans[k - 1].Y(s.From)).Within(1e-6), "continuous over each tower");
                }
            // The loaded side sags more than the empty one.
            var l = rope.Loaded[1];
            var e = rope.Empty[1];
            double m = (l.From + l.To) / 2;
            Assert.That(l.Y(m), Is.LessThan(e.Y(m)));
            Assert.That(rope.TopTensionShare, Is.InRange(0.5, 1.0), "about 240 m of rise is within the rating");
        }

        [Test]
        public void TW04_TheRopePressesDownOnACrestAndPullsUpInAHollow()
        {
            // A hollow: steep, then flat, then steep again; the tower in the dip has the rope turning up over it.
            // Spans of about 330 ft (100 m), as real fixed-grip lines run.
            // A loaded 330 ft span sags about 20 ft at 18,000 lbf, so the rope pulls up only where the line turns up sharply.
            var hollow = new Profile(s => s < 300 ? 0.25 * s : s < 500 ? 75 : 75 + 0.7 * (s - 500));
            var rope = Line(hollow, TowerStance.Plumb, (100, 10.67), (200, 10.67), (300, 12.19), (400, 10.67), (500, 7.62), (600, 10.67), (700, 10.67));
            Assert.That(rope.Towers[4].BendLoaded, Is.GreaterThan(0), "the rope turns up over the tower at the foot of the rise");
            Assert.That(rope.Towers[4].Head, Is.EqualTo(LiftRope.Head.D8).Or.EqualTo(LiftRope.Head.C8), "the rope pulls up before the rise");
            // A crest: steeper, then flattening.
            // The loaded rope bends about span / a at every tower (a = H / w, about 650 ft at LC-02's tension): 230 ft spans
            // and a gentle crest stay within a b8.
            var crest = new Profile(s => s < 420 ? 0.3 * s : 126 + 0.22 * (s - 420));
            var rope2 = Line(crest, TowerStance.Plumb, Enumerable.Range(1, 10).Select(i => (i * 70.0, 10.67)).ToArray());
            Assert.That(rope2.Towers[5].BendLoaded, Is.LessThan(0));
            Assert.That(rope2.Towers[5].Head, Is.EqualTo(LiftRope.Head.S4).Or.EqualTo(LiftRope.Head.S6).Or.EqualTo(LiftRope.Head.B8), "the rope presses down on the crest");
        }

        [Test]
        public void TW14_LeanedMastsStandSquareToTheRopeUpToFifteenDegrees()
        {
            var steep = new Plane(0, 0.6);
            var plumb = Line(steep, TowerStance.Plumb, (300, 12), (500, 12));
            var leaned = Line(steep, TowerStance.Leaned, (300, 12), (500, 12));
            Assert.That(plumb.Towers.All(t => t.Lean == 0));
            foreach (var t in leaned.Towers)
            {
                Assert.That(Math.Abs(t.Lean), Is.LessThanOrEqualTo(LiftRope.MaxLean + 1e-12));
                Assert.That(t.RopeS, Is.LessThan(t.BaseS), "the head leans down the line");
            }
            Assert.That(leaned.Towers[0].Lean, Is.EqualTo(LiftRope.MaxLean).Within(1e-9), "a 60% line (31°) caps at 15°");
        }

        [Test]
        public void TW14v2_OnALeanedLiftOnlyTheTowersThatNeedItLean()
        {
            // A gentle 10% line (about 6°): support heads, the rope well under 10° at every head, so all stay plumb.
            var gentle = Line(new Plane(0, 0.1), TowerStance.Leaned, Enumerable.Range(1, 9).Select(i => (i * 80.0, 10.67)).ToArray());
            Assert.That(gentle.Towers.Where(t => t.Head == LiftRope.Head.S4 || t.Head == LiftRope.Head.S6).All(t => t.Lean == 0), Is.True);
            // A 40% line (22°): every tower leans, in 2.5° steps.
            var steep = Line(new Plane(0, 0.4), TowerStance.Leaned, Enumerable.Range(1, 9).Select(i => (i * 80.0, 10.67)).ToArray());
            foreach (var t in steep.Towers)
            {
                Assert.That(t.Lean, Is.GreaterThan(0));
                Assert.That(t.Lean / LiftRope.LeanStep, Is.EqualTo(Math.Round(t.Lean / LiftRope.LeanStep)).Within(1e-9));
            }
        }

        [Test]
        public void TW01_ClearanceIsMeasuredOutsideTheLoadingZones()
        {
            var ground = new Plane(0, 0.3);
            var low = Line(ground, TowerStance.Plumb, (150, 7.62), (650, 7.62));
            var high = Line(ground, TowerStance.Plumb, Enumerable.Range(1, 9).Select(i => (i * 80.0, 12.19)).ToArray());
            Assert.That(low.LowestClearance, Is.LessThan(0), "a 500 m span between two short towers sags into the ground, and it isn't exempt");
            Assert.That(low.ClearanceOk, Is.False);
            // 40 ft towers every 260 ft keep 15 ft between them, but the first span (from the station to 262 ft) is no longer
            // exempt (TW-01 v2, finding r3 #4): its chairs must be back at 15 ft by 100 ft.
            Assert.That(high.ShortfallBetween(90, 700, out _), Is.LessThanOrEqualTo(LiftRope.ClearanceTolerance), "15 ft between the towers");
            Assert.That(high.ShortfallBetween(0, 90, out _), Is.GreaterThan(LiftRope.ClearanceTolerance), "the first span falls short");
        }

        [Test]
        public void SaveV1_AStyleTileDesignSavesAndLoadsUnchanged()
        {
            // The style tile's shape (task 07): a built graded trail with its edits, and a built Leaned lift with towers
            // and its corridor, through the frozen save v1 and back, byte for byte.
            var natural = Natural(new Rolling());
            var history = new DesignHistory(DesignDocument.Empty(77));
            var graded = Grade(natural);
            var points = Enumerable.Range(0, Curve().Length / 2)
                .Select(i => PointMm.FromMetres(Curve()[2 * i] - West - 2500, Curve()[2 * i + 1] - North + 2500)).ToArray();
            var parts = new[] { new FootprintPart(new[] { new Stroke(DesignUnits.FeetToMm(100), false, points) }, Array.Empty<Cut>()) };
            Assert.That(history.Submit(new AddTrail(history.Revision, "Style Tile Run", TrailSettings.GradedRun, parts, points[0], points[points.Length - 1], WorkStage.Built),
                                       graded.Edits).Accepted, Is.True);
            var corridor = LiftClearing.Corridor(history.Terrain, Lattice, West + 100, North - 500, West + 100, North - 100, out _);
            var towers = new[] { new TowerSpec(DesignUnits.FeetToMm(400), DesignUnits.FeetToMm(35)), new TowerSpec(DesignUnits.FeetToMm(800), DesignUnits.FeetToMm(45)) };
            Assert.That(history.Submit(new AddLift(history.Revision, "Style Tile Quad", LiftCatalogIds.SesselliftFgq4, PointMm.FromMetres(-2400, -2000),
                                                   PointMm.FromMetres(-2400, -2400 + 1900), TowerMode.ByHand, TowerStance.Leaned, towers, WorkStage.Built), corridor).Accepted, Is.True);

            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mp-styletile-" + Guid.NewGuid().ToString("N"));
            try
            {
                var save = new SaveFile { Name = "Style tile", SavedUtc = "2026-10-17T12:00:00Z", GameVersion = "0.2.0", PackageId = "jackson-hole-test",
                                          HeightsSha256 = new string('0', 64), Design = history.Document, Edits = history.Terrain };
                string folder = SaveStore.NewSaveFolder(root, save.PackageId);
                Assert.That(SaveStore.Write(folder, save), Is.True);
                var back = SaveStore.Read(folder);
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(save.Design)), Is.True);
                Assert.That(TerrainEditBytes.Encode(back.Edits), Is.EqualTo(TerrainEditBytes.Encode(save.Edits)));
                Assert.That(back.Design.Lifts.Single().Stance, Is.EqualTo(TowerStance.Leaned));
            }
            finally
            {
                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            }
        }

        [Test]
        public void LF12_APadIsLevelWithOneToOneFacesAndNothingChangesPastItsEnvelope()
        {
            var natural = Natural(new Plane(0.15, 0.3));
            double x = West + 300, y = North - 300;
            double level = natural.At(x, y);
            var pad = TerminalPad.Grade(natural, x, y, 0, 1, 2.3, 8.1, 3.0, level);
            var h = new EditedHeights(natural.Source, Lattice, pad.Edits);
            // Inside the footprint plus 5 ft (from 2.3 + 1.5 m behind to 8.1 + 1.5 m in front, 4.5 m either side) the ground is
            // the terminal's level: higher ground cut to it, lower ground filled to it (LF-12 v4, LT35).
            for (int dy = -3; dy <= 9; dy += 2)
                for (int dx = -4; dx <= 4; dx += 2)
                    Assert.That(h.At(x + dx, y + dy), Is.EqualTo(level).Within(0.002), $"({dx}, {dy}) on the pad");
            Assert.That(pad.FillCubicMetres, Is.GreaterThan(0), "the low side filled");
            // Faces at most 1:1 across the pad's side.
            for (int d = 5; d < 20; d++)
                Assert.That(Math.Abs(h.At(x + d + 1, y) - h.At(x + d, y)), Is.LessThanOrEqualTo(1.0 + 0.02), $"{d} m out");
            // Past the 50 ft envelope nothing changes (GR-06's exception stops there).
            for (int d = 0; d < 360; d += 20)
            {
                double a = d * Math.PI / 180, px = x + Math.Cos(a) * 30, py = y + 3 + Math.Sin(a) * 30;
                Assert.That(h.At(px, py), Is.EqualTo(natural.At(px, py)).Within(1e-9), $"{d}°, 30 m out");
            }
            Assert.That(pad.ClearedCells, Is.GreaterThan(100), "CL-09 v2 clears the pad and its faces");
        }

        [Test]
        public void LF12v2_ATopPadEndsJustBehindItsTerminal()
        {
            // Finding r3 #2: the pad hugs the terminal, ending just behind its hood, with no unload extension.
            var natural = Natural(new Plane(0, 0.25));   // the hill keeps rising behind the top terminal (north)
            double x = West + 300, y = North - 300;
            double level = natural.At(x, y);
            // The other terminal is downhill (south), so +u points south and "behind" is north, up the hill.
            var pad = TerminalPad.Grade(natural, x, y, 0, -1, 2.5, 7.2, 3.3, level);
            var h = new EditedHeights(natural.Source, Lattice, pad.Edits);
            Assert.That(h.At(x, y + 3.9), Is.EqualTo(level).Within(0.002), "level 2.5 m + 5 ft behind the bullwheel");
            // Behind the pad's edge (4.0 m) the 1:1 face climbs to meet the hill at about 5.4 m; beyond, the hill is untouched.
            Assert.That(h.At(x, y + 4.8), Is.GreaterThan(level + 0.5), "the face rises behind the pad");
            Assert.That(h.At(x, y + 8.0), Is.EqualTo(natural.At(x, y + 8.0)).Within(1e-9), "untouched 26 ft behind");
            Assert.That(h.At(x, y + 18.0), Is.EqualTo(natural.At(x, y + 18.0)).Within(1e-9), "no unload extension");
        }

        /// <summary>A profile along the test line, which runs due north from (100, 900) to (100, 100).</summary>
        sealed class Profile : IHeightSource
        {
            readonly Func<double, double> _f;
            public Profile(Func<double, double> f) => _f = f;
            public double At(double x, double y) => 1500 + _f(y - (North - 900));
        }
    }
}
