using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 B1 part 2a: the trails' fine snow in a whole world (SN-03 v3, SN-14 v3, SN-17, SN-23 v3, SN-24 v3,
    /// SN-30 v2). A trail no one grooms or skis leaves the 8 m snow byte for byte; every block's water stays its 8 m cell's;
    /// a groomed trail keeps more water through a thaw, and the ledger with the trails' term balances; grooming keeps a
    /// trail's water exactly, packs it, runs once a night at the trail's hour and skips snow too thin to work; wear lands
    /// on its lane, packs and hardens the snow and takes its water.
    /// </summary>
    public sealed class TrailSnowWorldTests
    {
        const int W = 128, D = 128;
        const long Origin = -W / 2 * 8000L;

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        static SnowFieldTests.Mountain _small;
        static SnowFieldTests.Mountain Small => _small ??= SnowFieldTests.Build(Pacing.Default, W, D);

        /// <summary>A straight run down the mountain's east flank, 30 m wide, one segment on edge 0.</summary>
        static TrailSnowInput Run(ulong id, int x, bool neverGroom, int width = 30_000, int dx = 0)
        {
            var stroke = new Stroke(width, false, new[] { new PointMm(x - dx, 380_000), new PointMm(x + dx, -380_000) });
            int row0 = int.MaxValue, rowEnd = int.MinValue;
            TrailMask.Builder.RowsOf(stroke, ref row0, ref rowEnd);
            var b = new TrailMask.Builder(row0, rowEnd);
            b.Stamp(stroke);
            return new TrailSnowInput
            {
                Mask = b.ToMask(), LineXMm = new[] { x - dx, x + dx }, LineYMm = new[] { 380_000, -380_000 }, WidthMm = width,
                SegmentStartMm = new[] { 0 }, SegmentEdge = new[] { 0 }, NeverGroom = neverGroom, Id = id,
            };
        }

        static TrailSnowLayout Layout(params TrailSnowInput[] trails) =>
            TrailSnowLayout.Build(W, D, Origin, Origin, trails, Pacing.Default.FineCellCap);

        /// <summary>A world on the small mountain with an even seed of settled snow (mm) and, if given, the trails.</summary>
        static SimWorld World(int seedMm, TrailSnowLayout layout, Pacing pacing = null, SimDesign design = null)
        {
            var w = new SimWorld(pacing ?? Pacing.Default, design ?? SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
            SnowFieldTests.SendInputs(w, Small);
            var mm = new ushort[W * D];
            for (int c = 0; c < mm.Length; c++) mm[c] = (ushort)seedMm;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, 0, 0, W, D, mm)));
            if (layout != null) w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, layout));
            return w;
        }

        static void RunDays(SimWorld w, int days)
        {
            long end = w.FaceMs + days * SimWorld.DayMs;
            while (w.FaceMs < end) w.Step(Pacing.Default.IdleTickFactor);
            w.JoinSnow();
        }

        /// <summary>Every block's water is its 8 m cell's: <c>sum = bc × Swe8 + r</c>, <c>0 ≤ r &lt; bc</c>.</summary>
        static int BlocksOff(SimWorld w)
        {
            var fine = w.FineSnow;
            int off = 0, bc = fine.BlockCells;
            for (int b = 0; b < fine.Layout.Blocks; b++)
            {
                long sum = fine.BlockSum(b), swe = w.Snow.Field.Pack[fine.Layout.BlockCell[b]].Swe;
                if (sum < bc * swe || sum >= bc * (swe + 1)) off++;
            }
            return off;
        }

        static long TrailWater(SimWorld w, int trail)
        {
            var fine = w.FineSnow;
            long s = 0;
            for (int f = 0; f < fine.Cells; f++) if (fine.Layout.Owner[f] == trail) s += fine.Swe[f];
            return s;
        }

        [Test]
        public void TS01_ATrailNoOneGroomsOrSkisLeavesTheSnowAsItWas()
        {
            var layout = Layout(Run(11, 150_000, neverGroom: true));
            var with = World(600, layout);
            var without = World(600, null);
            Assert.That(with.FineSnow, Is.Not.Null, "the layout makes the fine field");
            Assert.That(with.FineSnow.Layout.Blocks, Is.GreaterThan(100));
            RunDays(with, 3);
            RunDays(without, 3);
            int differ = 0;
            for (int c = 0; c < W * D; c++)
            {
                var a = with.Snow.Field.Pack[c]; var b = without.Snow.Field.Pack[c];
                if (a.Swe != b.Swe || a.Density != b.Density || a.Liquid != b.Liquid || with.Snow.Field.Type[c] != without.Snow.Field.Type[c]) differ++;
            }
            Say($"[untouched] {layout.Blocks} blocks at {layout.FineMetres} m; {differ} of {W * D} cells differ; blocks off {BlocksOff(with)}");
            Assert.That(differ, Is.Zero, "loose fine cells follow the 8 m snow exactly");
            Assert.That(BlocksOff(with), Is.Zero, "every block's water is its 8 m cell's");
            // 2b hashes the trails' board (their labels and open state), so the world hashes differ; the snow doesn't.
        }

        [Test]
        public void TS02_AGroomedTrailKeepsMoreWaterThroughAThawAndTheLedgerBalances()
        {
            var groomed = World(1500, Layout(Run(11, 150_000, neverGroom: false)));
            var loose = World(1500, Layout(Run(11, 150_000, neverGroom: true)));
            long balance = 0;
            int lastDay = groomed.SnowToday.Day;
            long end = groomed.FaceMs + 4 * SimWorld.DayMs;
            while (groomed.FaceMs < end)
            {
                groomed.Step(Pacing.Default.IdleTickFactor);
                if (groomed.SnowToday.Day != lastDay) { lastDay = groomed.SnowToday.Day; balance += groomed.SnowLastDay.Balance; }
            }
            groomed.JoinSnow();
            balance += groomed.SnowToday.Balance;   // the day in progress too: the trails' term lands with the next hour
            RunDays(loose, 4);
            long g = TrailWater(groomed, 0), l = TrailWater(loose, 0);
            var fine = groomed.FineSnow;
            int packed = 0, mg = 0, onTrail = 0;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (fine.Layout.Owner[f] != 0) continue;
                onTrail++;
                if ((fine.Flags[f] & FineSnow.Packed) != 0) packed++;
                if (fine.Type[f] == (byte)SnowType.MG) mg++;
            }
            Say($"[thaw] after 4 days the groomed run holds {g / 1e3 / onTrail:F1} mm of water a fine cell, the loose one {l / 1e3 / onTrail:F1}; " +
                $"{packed} of {onTrail} packed, {mg} groomed; ledger {balance} µm, trail term {groomed.TrailRemovedTotal} µm");
            Assert.That(packed, Is.GreaterThan(onTrail / 2), "the groomer worked the run");
            Assert.That(g, Is.GreaterThan(l), "a packed base melts slower (SN-30 v2)");
            Assert.That(balance, Is.EqualTo(groomed.TrailRemovedTotal), "over the run, the ledger misses only the trails' term");
            Assert.That(BlocksOff(groomed), Is.Zero);
        }

        /// <summary>The hour roll's trail work at each hour of the next face day; returns the hours it groomed.</summary>
        static int[] GroomDay(SimWorld w, out long waterBefore, out long waterAfter)
        {
            waterBefore = TrailWater(w, 0);
            var fine = w.FineSnow;
            var hours = new System.Collections.Generic.List<int>();
            long day = w.FaceMs / SimWorld.DayMs + 1;
            for (int h = 0; h < 24; h++)
            {
                var before = (byte[])fine.Flags.Clone();
                w.HourStepTrails(day * 24 + h);
                for (int f = 0; f < fine.Cells; f++) if (fine.Flags[f] != before[f]) { hours.Add(h); break; }
            }
            waterAfter = TrailWater(w, 0);
            return hours.ToArray();
        }

        [Test]
        public void TS03_GroomingKeepsTheWaterPacksAndRunsOnceANight()
        {
            var w = World(600, Layout(Run(11, 150_000, neverGroom: false)));
            w.JoinSnow();
            var fine = w.FineSnow;
            long densityBefore = 0, densityAfter = 0;
            for (int f = 0; f < fine.Cells; f++) if (fine.Layout.Owner[f] == 0) densityBefore += fine.Density[f];
            var hours = GroomDay(w, out long before, out long after);
            int groomed = 0, onTrail = 0;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (fine.Layout.Owner[f] != 0) continue;
                onTrail++;
                densityAfter += fine.Density[f];
                if (fine.Type[f] == (byte)SnowType.MG && (fine.Flags[f] & FineSnow.Packed) != 0 && fine.Age[f] == 0) groomed++;
            }
            Say($"[groom] hours {string.Join(",", hours)}; water {before} -> {after}; {groomed} of {onTrail} cells groomed; " +
                $"density {densityBefore / onTrail / FineSnow.DensityOne} -> {densityAfter / onTrail / FineSnow.DensityOne} kg/m³");
            Assert.That(hours.Length, Is.EqualTo(1), "once a night");
            Assert.That(hours[0], Is.EqualTo(Pacing.Default.GroomStartHour), "at the nightly hour (4.25 brings the shifts)");
            Assert.That(after, Is.EqualTo(before), "grooming moves water, never makes or loses it");
            Assert.That(groomed, Is.EqualTo(onTrail), "600 mm of snow is all workable");
            Assert.That(densityAfter, Is.GreaterThan(densityBefore), "packed toward 400 kg/m³");
            Assert.That(BlocksOff(w), Is.Zero, "the roll-up wrote every touched block back");
        }

        [Test]
        public void TS10_GroomingStartsTheNewSnowCountAgain()
        {
            // SN-18 v2: 0.1 in of water since the last surface event makes powder. A groom is one, so the day's earlier
            // flakes must not count against tonight's corduroy (the game showed none left by the morning, 2026-11-19).
            var w = World(600, Layout(Run(11, 150_000, neverGroom: true)));
            w.JoinSnow();
            var field = w.Snow.Field; var L = w.FineSnow.Layout;
            for (int b = 0; b < L.Blocks; b++) field.NewSince[L.BlockCell[b]] = 900;   // 0.09 in of water so far
            Assert.That(w.GroomTrail(0, GroomStrength.Heavy(Pacing.Default)), Is.True);
            var fine = w.FineSnow;
            int bc = fine.Cells / L.Blocks, counting = 0, groomedBlocks = 0;
            for (int b = 0; b < L.Blocks; b++)
            {
                bool worked = false;
                for (int f = b * bc; f < (b + 1) * bc && !worked; f++) worked = (fine.Flags[f] & FineSnow.GroomMask) != 0;
                if (!worked) continue;   // a strip too narrow to work: the groomer never touched it
                groomedBlocks++;
                if (field.NewSince[L.BlockCell[b]] != 0) counting++;
            }
            Assert.That(groomedBlocks, Is.GreaterThan(L.Blocks / 2), "most of the run was workable");
            Assert.That(counting, Is.Zero, "every groomed square's new-snow count starts again");
        }

        // ================= 4.07 B1 part 2b: readiness (SN-20 v3), the rating (SN-16 v3), the walk (GU-07 v2) =================

        /// <summary>A run on the golden design's first network trail (its id and an edge of it), so the board can map it.</summary>
        static TrailSnowLayout NetLayout(SimWorld w, bool graded = false, int dx = 0, int width = 30_000)
        {
            var net = w.Design.Network;
            int edge = -1;
            for (int e = 0; e < net.EdgeTrail.Length && edge < 0; e++) if (net.EdgeTrail[e] == 0) edge = e;
            var run = Run(net.TrailIds[0], 150_000, neverGroom: true, width: width, dx: dx);
            run.SegmentEdge = new[] { Math.Max(0, edge) };
            run.Graded = graded;
            return TrailSnowLayout.Build(W, D, Origin, Origin, new[] { run }, Pacing.Default.FineCellCap, net.ShapeHash);
        }

        static SimWorld NetWorld(int seedMm, bool graded = false, int dx = 0, int width = 30_000)
        {
            var w = World(seedMm, null, design: GuestFixture.OneDouble());   // one green on a network
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, NetLayout(w, graded, dx, width)));
            w.JoinSnow();
            return w;
        }

        /// <summary>Clears the trail's fine cells where <paramref name="bare"/> says (no water, no depth).</summary>
        static int Strip(SimWorld w, Func<int, int, bool> bare)
        {
            var fine = w.FineSnow; var L = fine.Layout;
            int n = 0;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (L.Owner[f] != 0 || !bare(L.AlongMm[f], L.LateralMm[f])) continue;
                fine.Swe[f] = 0; fine.Density[f] = 0; fine.Type[f] = 0; n++;
            }
            return n;
        }

        [Test]
        public void TR01_AFullyCoveredTrailIsCovered()
        {
            var w = NetWorld(600);
            w.RateTrailsNow();
            Say($"[ready] full: {w.TrailLabelOf(0)}, walk {w.TrailWalkMmOf(0)} mm, cover {w.TrailCoverPermilleOf(0)} ‰");
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Covered));
            Assert.That(w.TrailWalkMmOf(0), Is.Zero);
            Assert.That(w.TrailCoverPermilleOf(0), Is.EqualTo(1000));
        }

        [Test]
        public void TR02_ABareRowAcrossIsBareThenAWalkInTheLateSeason()
        {
            var w = NetWorld(600);
            int cleared = Strip(w, (along, lat) => along >= 370_000 && along < 373_000);   // 3 m across the whole width
            w.RateTrailsNow();
            var before = w.TrailLabelOf(0);
            int walk = w.TrailWalkMmOf(0);
            w.SetLateSeason(true, 120);
            w.RateTrailsNow();
            Say($"[ready] bare row ({cleared} cells): {before} before the late season, {w.TrailLabelOf(0)} in it; walk {walk} mm");
            Assert.That(before, Is.EqualTo(TrailSnowLabel.Bare), "a walk closes a trail before the late season");
            Assert.That(walk, Is.InRange(3000, 4000), "the walk is the gap down the trail");
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Marginal), "in the late season it's Marginal, with a walk");
        }

        [Test]
        public void TR03_ABareStripAtTheSideLeavesALine()
        {
            var w = NetWorld(600);
            Strip(w, (along, lat) => lat > 11_000);   // the left 4 m bare all the way down: 26 m of the 30 still covered
            w.RateTrailsNow();
            Say($"[ready] side strip: {w.TrailLabelOf(0)}, walk {w.TrailWalkMmOf(0)} mm, cover {w.TrailCoverPermilleOf(0)} ‰");
            Assert.That(w.TrailWalkMmOf(0), Is.Zero, "a line remains");
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Marginal), "under 90 % covered: Marginal, no walk");
        }

        [Test]
        public void TR04_TwoStripsThatNeverTouchAreNotALine()
        {
            var w = NetWorld(600);
            // The middle 4 m bare all the way; the left side bare below halfway, the right side bare above it.
            Strip(w, (along, lat) => Math.Abs(lat) < 2000 || (lat > 0 && along > 380_000) || (lat < 0 && along <= 380_000));
            w.RateTrailsNow();
            Say($"[ready] two strips: {w.TrailLabelOf(0)}, walk {w.TrailWalkMmOf(0)} mm, cover {w.TrailCoverPermilleOf(0)} ‰");
            Assert.That(w.TrailWalkMmOf(0), Is.GreaterThan(0), "crossing from one strip to the other is a walk");
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Bare));
        }

        [Test]
        public void TR05_GradedCoversAtSixInchesNaturalAtTwelve()
        {
            var graded = NetWorld(230, graded: true);
            var natural = NetWorld(230, graded: false);
            graded.RateTrailsNow();
            natural.RateTrailsNow();
            Say($"[ready] 9 in: graded {graded.TrailLabelOf(0)}, natural {natural.TrailLabelOf(0)}");
            Assert.That(graded.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Covered));
            Assert.That(natural.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Bare));
        }

        [Test]
        public void TR06_BareClosesTheTrailAtTheHourRoll()
        {
            var w = NetWorld(600);
            Assert.That(w.TrailReady(0), Is.True, "Covered snow: open");
            Strip(w, (along, lat) => true);
            w.RateTrailsNow();
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Bare), "rated Bare at once");
            Assert.That(w.TrailReady(0), Is.True, "but the board only flips at the hour roll");
            long seq = w.Events.Count;
            long next = (w.FaceMs / SimWorld.HourMs + 1) * SimWorld.HourMs;
            while (w.FaceMs <= next) w.Step(1);
            w.JoinSnow();
            bool readiness = false;
            for (long q = seq; q < w.Events.Count; q++)
                if (w.Events.TryGet(q, out var ev) && ev.Type == SimEventType.TrailReadinessChanged && ev.Entity == 0) readiness = true;
            Say($"[ready] stripped: label {w.TrailLabelOf(0)}, ready {w.TrailReady(0)}, late season {w.LateSeason}");
            Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Bare));
            Assert.That(w.TrailReady(0), Is.False, "Bare closes the trail");
            Assert.That(readiness, Is.True, "the change is an event");
            Assert.That(w.LateSeason, Is.False, "October is never the late season");
        }

        [Test]
        public void TR07_TheWalkPullsLessTheLongerItIs()
        {
            int hops = SimWorld.WalkPullOf(20, 60), far = SimWorld.WalkPullOf(300, 60), may = SimWorld.WalkPullOf(300, 250);
            Say($"[walk] 20 ft at L 60: {hops} ‰; 300 ft at L 60: {far} ‰; 300 ft at L 250: {may} ‰");
            Assert.That(SimWorld.WalkPullOf(0, 60), Is.EqualTo(1000));
            Assert.That(hops, Is.InRange(880, 900), "two 10 ft hops in December ≈ 0.9");
            Assert.That(far, Is.Zero, "300 ft in December ≈ 0");
            Assert.That(may, Is.InRange(230, 245), "300 ft in May ≈ 0.24");
        }

        [Test]
        public void TR08_TheRatingReadsTheSnowByBand()
        {
            var w = NetWorld(600);
            w.RateTrailsNow();
            var net = w.Design.Network;
            int edge = -1;
            for (int e = 0; e < net.EdgeTrail.Length && edge < 0; e++) if (net.EdgeTrail[e] == 0) edge = e;
            int beginner = w.QualityOf(edge, 0), expert = w.QualityOf(edge, Pacing.Bands - 1);
            Say($"[rating] fresh snow on edge {edge}: beginner {beginner} ‰, expert {expert} ‰ (the stand-in was 900)");
            Assert.That(beginner, Is.GreaterThan(0));
            Assert.That(expert, Is.GreaterThanOrEqualTo(beginner), "experts like powder");
        }

        /// <summary>A run along a polyline (mm), its mask stamped from the same line, one segment on the network trail's edge.</summary>
        static TrailSnowInput Line(SimWorld w, int width, params PointMm[] pts)
        {
            var net = w.Design.Network;
            int edge = -1;
            for (int e = 0; e < net.EdgeTrail.Length && edge < 0; e++) if (net.EdgeTrail[e] == 0) edge = e;
            var stroke = new Stroke(width, false, pts);
            int row0 = int.MaxValue, rowEnd = int.MinValue;
            TrailMask.Builder.RowsOf(stroke, ref row0, ref rowEnd);
            var b = new TrailMask.Builder(row0, rowEnd);
            b.Stamp(stroke);
            var xs = new int[pts.Length]; var ys = new int[pts.Length];
            for (int i = 0; i < pts.Length; i++) { xs[i] = (int)pts[i].X; ys[i] = (int)pts[i].Y; }
            return new TrailSnowInput
            {
                Mask = b.ToMask(), LineXMm = xs, LineYMm = ys, WidthMm = width,
                SegmentStartMm = new[] { 0 }, SegmentEdge = new[] { Math.Max(0, edge) }, NeverGroom = true, Id = net.TrailIds[0],
            };
        }

        static SimWorld LineWorld(int seedMm, params TrailSnowInput[] runs)
        {
            var w = World(seedMm, null, design: GuestFixture.OneDouble());
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0,
                TrailSnowLayout.Build(W, D, Origin, Origin, runs, Pacing.Default.FineCellCap, w.Design.Network.ShapeHash)));
            w.JoinSnow();
            return w;
        }

        [Test]
        public void TR11_BendsAndCrossingsKeepAFullyCoveredRunCovered()
        {
            var probe = World(600, null, design: GuestFixture.OneDouble());
            var cases = new (string name, Func<SimWorld, TrailSnowInput[]> runs)[]
            {
                ("90° bend", w => new[] { Line(w, 30_000, new PointMm(150_000, 380_000), new PointMm(150_000, 0), new PointMm(-300_000, 0)) }),
                ("zig-zag", w => new[] { Line(w, 20_000, new PointMm(0, 380_000), new PointMm(200_000, 200_000), new PointMm(0, 0), new PointMm(200_000, -200_000), new PointMm(0, -380_000)) }),
                ("hairpin", w => new[] { Line(w, 12_000, new PointMm(0, 380_000), new PointMm(0, 0), new PointMm(60_000, 0), new PointMm(60_000, 380_000)) }),
                ("crossed", w =>
                {
                    var a = Line(w, 30_000, new PointMm(150_000, 380_000), new PointMm(150_000, -380_000));
                    var o = Line(w, 30_000, new PointMm(-300_000, 0), new PointMm(400_000, 10_000)); o.Id = 999;
                    return new[] { a, o };
                }),
                ("joined at the side", w =>
                {
                    var a = Line(w, 30_000, new PointMm(150_000, 380_000), new PointMm(150_000, -380_000));
                    var o = Line(w, 30_000, new PointMm(-300_000, 100_000), new PointMm(150_000, 0)); o.Id = 999;
                    return new[] { a, o };
                }),
            };
            int bad = 0;
            foreach (var (name, runs) in cases)
            {
                var w = LineWorld(600, runs(probe));
                w.RateTrailsNow();
                Say($"[ready] {name}: {w.TrailLabelOf(0)}, walk {w.TrailWalkMmOf(0)} mm, cover {w.TrailCoverPermilleOf(0)} ‰");
                if (w.TrailLabelOf(0) != TrailSnowLabel.Covered) bad++;
            }
            Assert.That(bad, Is.Zero);
        }

        [Test]
        public void TR10_AFullyCoveredRunAtAnyAngleIsCovered()
        {
            // The owner's drawn Jackson Hole read Bare at 100 % covered (2026-11-19): runs at an angle to the grid.
            foreach (int width in new[] { 30_000, 6_000, 2_000 })
                foreach (int dx in new[] { 0, 50_000, 100_000, 190_000, 250_000, 380_000 })
                {
                    var w = NetWorld(600, dx: dx, width: width);
                    w.RateTrailsNow();
                    Say($"[ready] {width / 1000} m wide, {Math.Atan2(2.0 * dx, 760_000) * 180 / Math.PI:F0}°: {w.TrailLabelOf(0)}, walk {w.TrailWalkMmOf(0)} mm, cover {w.TrailCoverPermilleOf(0)} ‰");
                    Assert.That(w.TrailWalkMmOf(0), Is.Zero, $"{width} mm wide, dx {dx}");
                    Assert.That(w.TrailLabelOf(0), Is.EqualTo(TrailSnowLabel.Covered), $"{width} mm wide, dx {dx}");
                }
        }

        [Test]
        public void TR09_RatingTheTrailsAllocatesNothingOnceWarm()
        {
            var w = NetWorld(600);
            w.RateTrailsNow();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++) w.RateTrailsNow();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Say($"[ready] ten ratings: {bytes} bytes");
            Assert.That(bytes, Is.Zero);
        }

        [Test]
        public void TS09_ALightPassEvensLessThanAHeavyOne()
        {
            var light = World(1500, Layout(Run(11, 150_000, neverGroom: true)));
            var heavy = World(1500, Layout(Run(11, 150_000, neverGroom: true)));
            RunDays(light, 2);
            RunDays(heavy, 2);
            long l0 = TrailWater(light, 0);
            Assert.That(light.GroomTrail(0, GroomStrength.Light(Pacing.Default)), Is.True);
            Assert.That(heavy.GroomTrail(0, GroomStrength.Heavy(Pacing.Default)), Is.True);
            long Spread(SimWorld w)
            {
                var fine = w.FineSnow; var L = fine.Layout;
                var lo = new System.Collections.Generic.Dictionary<int, int>(); var hi = new System.Collections.Generic.Dictionary<int, int>();
                for (int f = 0; f < fine.Cells; f++)
                {
                    if (L.Owner[f] != 0) continue;
                    int a = L.AlongMm[f];
                    lo[a] = lo.TryGetValue(a, out int x) ? Math.Min(x, fine.Swe[f]) : fine.Swe[f];
                    hi[a] = hi.TryGetValue(a, out int y) ? Math.Max(y, fine.Swe[f]) : fine.Swe[f];
                }
                long sum = 0; foreach (var kv in lo) sum += hi[kv.Key] - kv.Value;
                return sum / Math.Max(1, lo.Count);
            }
            long sl = Spread(light), sh = Spread(heavy);
            Say($"[strength] spread across the run after one pass: light {sl} µm, heavy {sh} µm");
            Assert.That(TrailWater(light, 0), Is.EqualTo(l0), "a light pass keeps the water too");
            Assert.That(sh, Is.LessThan(sl), "a heavy pass evens more");
        }

        [Test]
        public void TS04_TheGroomerSkipsSnowTooThinToWork()
        {
            var w = World(200, Layout(Run(11, 150_000, neverGroom: false)));   // 8 in: under the 12 in floor
            w.JoinSnow();
            var hours = GroomDay(w, out long before, out long after);
            Assert.That(hours, Is.Empty, "a groomer would tear 8 in of snow to mud");
            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void TS05_IceGoesToLooseGranularThenGroomedAndFiveNightsHarden()
        {
            var w = World(600, Layout(Run(11, 150_000, neverGroom: false)));
            w.JoinSnow();
            var fine = w.FineSnow;
            int probe = -1;
            for (int f = 0; f < fine.Cells && probe < 0; f++) if (fine.Layout.Owner[f] == 0) probe = f;
            for (int f = 0; f < fine.Cells; f++) if (fine.Layout.Owner[f] == 0) fine.Type[f] = (byte)SnowType.IS;
            var seen = new SnowType[6];
            for (int night = 0; night < 6; night++)
            {
                for (int h = 0; h < 24; h++) w.HourStepTrails((night + 1) * 24 + h);
                seen[night] = (SnowType)fine.Type[probe];
            }
            Say($"[types] nights: {string.Join(", ", seen)}");
            Assert.That(seen[0], Is.EqualTo(SnowType.LG), "ice breaks up first");
            Assert.That(seen[1], Is.EqualTo(SnowType.MG), "then it's machine groomed");
            Assert.That(seen[4], Is.EqualTo(SnowType.HP), "five groomings in a row harden it");
        }

        [Test]
        public void TS07_TheSameBytesOnOneAndFourThreads()
        {
            var layout = Layout(Run(11, 150_000, neverGroom: false), Run(12, -150_000, neverGroom: false, width: 20_000));
            var one = World(1500, layout, Pacing.Parse("SnowThreads = 1"));
            var four = World(1500, layout, Pacing.Parse("SnowThreads = 4"));
            RunDays(one, 2);
            RunDays(four, 2);
            int differ = 0;
            for (int f = 0; f < one.FineSnow.Cells; f++)
                if (one.FineSnow.Swe[f] != four.FineSnow.Swe[f] || one.FineSnow.Density[f] != four.FineSnow.Density[f]
                    || one.FineSnow.Type[f] != four.FineSnow.Type[f] || one.FineSnow.Flags[f] != four.FineSnow.Flags[f]) differ++;
            Assert.That(differ, Is.Zero, "the fine field doesn't depend on the threads");
            Assert.That(four.Snow.TileHashes, Is.EqualTo(one.Snow.TileHashes), "the 8 m snow too (the world hash has the thread count in its table)");
            Assert.That(four.SnowToday, Is.EqualTo(one.SnowToday));
            Assert.That(one.TrailRemovedTotal, Is.EqualTo(four.TrailRemovedTotal));
        }

        /// <summary>Two weeks of December on two groomed runs (one 20 m wide) from 1.5 m of snow: each day's world hash and
        /// the fine field, folded. A behaviour change re-pins it, with the owner's OK.</summary>
        [Test]
        public void TS08_TheTrailWeeksArePinned()
        {
            var w = new SimWorld(Pacing.Default, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
            SnowFieldTests.SendInputs(w, Small);
            SnowFieldTests.RunTo(w, 12, 1, Pacing.Default.IdleTickFactor);
            w.JoinSnow();
            var mm = new ushort[W * D];
            for (int c = 0; c < mm.Length; c++) mm[c] = 1500;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, 0, 0, W, D, mm)));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, Layout(Run(11, 150_000, neverGroom: false), Run(12, -150_000, neverGroom: false, width: 20_000))));
            ulong fold = 14695981039346656037UL;
            void Mix(ulong v) { for (int k = 0; k < 8; k++) { fold ^= (v >> (8 * k)) & 0xFF; fold *= 1099511628211UL; } }
            for (int day = 0; day < 14; day++)
            {
                RunDays(w, 1);
                Mix(w.Hash());
                var fine = w.FineSnow;
                for (int f = 0; f < fine.Cells; f++)
                    Mix((ulong)(uint)fine.Swe[f] | (ulong)fine.Density[f] << 32 | (ulong)fine.Type[f] << 48 | (ulong)fine.Flags[f] << 56);
                Mix((ulong)w.TrailRemovedTotal);
            }
            int cells = 0;
            for (int f = 0; f < w.FineSnow.Cells; f++) if (w.FineSnow.Layout.Owner[f] == 0) cells++;
            Say($"[weeks] trail weeks fold 0x{fold:X16}; the main run holds {TrailWater(w, 0) / 1e3 / Math.Max(1, cells):F0} mm of water a fine cell after two weeks");
            Assert.That(fold, Is.EqualTo(TrailWeeksFold), "a behaviour change to the trail snow needs an approved re-pin");
        }

        public const ulong TrailWeeksFold = 0x58F8E603EB371FE1UL;   // re-pinned at 4.07 B1 part 2b (the trails open and close by their snow, SN-20 v3; the Pacing table grew; owner OK 2026-11-19), over 0x2108110A752AB77B; 4.07 B1 part 2a: re-pinned for the groom resetting the new-snow count (owner OK 2026-11-19), over the first pin at Pacing v13 0xCE2ED3C83F51E459 (owner OK 2026-11-19)

        /// <summary>Demo 113's chart data (not a check): a groomed run and its loose twin through an October thaw, hour by
        /// hour, and the run's depth across its lanes along its length before and after a night's grooming.</summary>
        [Test, Explicit, Category("SnowReport")]
        public void TS90_WriteTheReportData()
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(TestData.Folder("formats"), "..", "..", "test-results", "snow-b2a"));
            System.IO.Directory.CreateDirectory(root);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var groomed = World(1500, Layout(Run(11, 150_000, neverGroom: false)));
            var loose = World(1500, Layout(Run(11, 150_000, neverGroom: true)));
            int cells = 0;
            for (int f = 0; f < groomed.FineSnow.Cells; f++) if (groomed.FineSnow.Layout.Owner[f] == 0) cells++;
            var sb = new System.Text.StringBuilder("hour,groomed_mm,loose_mm,groomed_depth_mm,loose_depth_mm\n");
            double Depth(SimWorld w) { long d = 0; var fn = w.FineSnow; for (int f = 0; f < fn.Cells; f++) if (fn.Layout.Owner[f] == 0) d += fn.DepthUm(f); return d / 1e3 / cells; }
            for (int h = 0; h <= 6 * 24; h++)
            {
                if (h > 0) { long end = groomed.FaceMs + SimWorld.HourMs; while (groomed.FaceMs < end) groomed.Step(Pacing.Default.IdleTickFactor); end = loose.FaceMs + SimWorld.HourMs; while (loose.FaceMs < end) loose.Step(Pacing.Default.IdleTickFactor); }
                groomed.JoinSnow(); loose.JoinSnow();
                sb.Append(h).Append(',').Append((TrailWater(groomed, 0) / 1e3 / cells).ToString("F2", inv)).Append(',')
                  .Append((TrailWater(loose, 0) / 1e3 / cells).ToString("F2", inv)).Append(',')
                  .Append(Depth(groomed).ToString("F1", inv)).Append(',').Append(Depth(loose).ToString("F1", inv)).Append('\n');
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "thaw.csv"), sb.ToString());
            // Across the lanes: an ungroomed run after two days of melt, then the night's grooming.
            var w = World(1500, Layout(Run(11, 150_000, neverGroom: true)));
            RunDays(w, 2);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetTrailSnow, 0, Layout(Run(11, 150_000, neverGroom: false))));   // keeps the cells
            var fine = w.FineSnow; var L = fine.Layout;
            var before = new long[fine.Cells];
            var sweBefore = (int[])fine.Swe.Clone();
            for (int f = 0; f < fine.Cells; f++) before[f] = fine.DepthUm(f);
            long day = w.FaceMs / SimWorld.DayMs + 1;
            for (int h = 0; h < 24; h++) w.HourStepTrails(day * 24 + h);
            var lanes = new System.Text.StringBuilder("x_mm,y_mm,lane,along_mm,before_mm,after_mm,swe_before_um,swe_after_um\n");
            for (int f = 0; f < fine.Cells; f++)
            {
                if (L.Owner[f] != 0) continue;
                L.CentreMm(f, out long x, out long y);
                lanes.Append(x).Append(',').Append(y).Append(',').Append(L.Lane[f]).Append(',').Append(L.AlongMm[f]).Append(',')
                     .Append((before[f] / 1e3).ToString("F0", inv)).Append(',').Append((fine.DepthUm(f) / 1e3).ToString("F0", inv)).Append(',')
                     .Append(sweBefore[f]).Append(',').Append(fine.Swe[f]).Append('\n');
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "groom.csv"), lanes.ToString());
            Say($"[report] wrote {root}");
        }

        [Test]
        public void TS06_WearLandsOnItsLanePacksHardensAndTakesWater()
        {
            var w = World(600, Layout(Run(11, 150_000, neverGroom: true)));
            w.JoinSnow();
            var fine = w.FineSnow;
            var L = fine.Layout;
            int lanes = Pacing.Default.WearBands;
            var passes = new int[lanes];
            passes[3] = Pacing.Default.PackedPasses;
            var swe0 = (int[])fine.Swe.Clone();
            w.ApplyWear(passes, 1);
            int onLane = 0, off = 0, pp = 0;
            long worn = 0;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (L.Owner[f] != 0) continue;
                if (L.Lane[f] == 3)
                {
                    onLane++;
                    worn += swe0[f] - fine.Swe[f];
                    if (fine.Passes[f] == passes[3] && (fine.Flags[f] & FineSnow.Packed) != 0) pp += fine.Type[f] == (byte)SnowType.PP ? 1 : 0;
                }
                else if (fine.Swe[f] != swe0[f] || fine.Passes[f] != 0) off++;
            }
            Say($"[wear] {onLane} lane-3 cells, {pp} packed powder, {worn / Math.Max(1, onLane)} µm worn a cell; {off} other cells touched");
            Assert.That(onLane, Is.GreaterThan(0));
            Assert.That(off, Is.Zero, "lane 3's passes land on lane 3");
            Assert.That(worn, Is.GreaterThan((long)onLane * passes[3] * Pacing.Default.WearPerPassUm / 2), "each pass wears its water");
            Assert.That(pp, Is.EqualTo(onLane), $"{Pacing.Default.PackedPasses} passes make packed powder");
            passes[3] = Pacing.Default.HardPasses;
            w.ApplyWear(passes, 1);
            int hp = 0;
            for (int f = 0; f < fine.Cells; f++) if (L.Owner[f] == 0 && L.Lane[f] == 3 && fine.Type[f] == (byte)SnowType.HP) hp++;
            Assert.That(hp, Is.EqualTo(onLane), "heavy traffic hardens it");
        }
    }
}
