using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // One build path for every lift (task P3-07 round 3; rules TW-01 v2, TW-02 v3, TW-03 v3, TW-16 v3, LF-12 v2, GR-11 v2,
    // RP-08): station towers aimed at what their terminals need, the low zones, the trench under the chairs and the unload
    // ramp, on made-up ground and on a fan of real Jackson Hole lines, every one built the same way with no tuning.
    public sealed class LiftBuildTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
        const double Ft = 0.3048;

        /// <summary>A profile along the test line, which runs due north from (100, 900) to (100, 100); a cross slope rises east.</summary>
        sealed class Profile : IHeightSource
        {
            readonly Func<double, double> _f;
            readonly double _cross;
            public Profile(Func<double, double> f, double cross = 0) { _f = f; _cross = cross; }
            public double At(double x, double y) => 1500 + _f(y - (North - 900)) + _cross * (x - (West + 100));
        }

        static LiftBuild.Result Build(Func<double, double> f, double cross = 0, TowerStance stance = TowerStance.Leaned) =>
            LiftBuild.Build(Quad, new EditedHeights(new Profile(f, cross), Lattice, TerrainEdits.Empty),
                            West + 100, North - 900, West + 100, North - 100, stance);

        static double Deg(double radians) => radians * 180 / Math.PI;

        static string Describe(LiftBuild.Result b) =>
            $"towers [{string.Join(", ", b.Towers.Select(t => $"{t.AlongMm / 304.8:F0}/{t.HeightMm / 304.8:F0}"))}], heads [{string.Join(",", b.Rope.Towers.Select(t => t.Head))}], " +
            $"exit {Deg(b.Rope.BottomExitLoaded):F1}/{Deg(b.Rope.BottomExitEmpty):F1}°, top {Deg(b.Rope.TopEntryLoaded):F2}/{Deg(b.Rope.TopEntryEmpty):F2}°, " +
            $"shortfall {b.Rope.Shortfall / Ft:F2} ft at {b.Rope.ShortfallS / Ft:F0} ft, raise {b.BottomRaise / Ft:F0}/{b.TopRaise / Ft:F0} ft, " +
            $"trench {b.Envelope.DeepestMetres / Ft:F1} ft, rows {(b.Rope.RowsOk ? "ok" : "OVER")}, top tension {b.Rope.TopTensionShare:P0}";

        /// <summary>Every rule the lift's geometry answers to, and the document's own (TW-02, TW-03). Some made-up grounds put a
        /// terminal on a slope LF-08 forbids (reported, tested on its own below); the rest must hold everywhere.</summary>
        static void AssertBuilt(LiftBuild.Result b, string what)
        {
            Assert.That(b.Rope.ClearanceOk && b.Rope.TerminalsOk, Is.True, $"{what}: {Describe(b)}");
            Assert.That(b.Rope.Towers.All(t => t.Head != LiftRope.Head.TooSharp), Is.True, $"{what}: {Describe(b)}");
            Assert.That(b.BottomPad.ClippedStepMetres < 0.05 && b.TopPad.ClippedStepMetres < 0.05, Is.True, $"{what}: {Describe(b)}");
            var towers = b.Towers.Select((t, i) => new Tower(TowerId.Make(9, i), t.AlongMm, t.HeightMm)).ToList();
            Assert.That(DesignRules.TowersProblem(800_000L * 800_000L, towers), Is.Null, $"{what}: {Describe(b)}");
        }

        [Test]
        public void TW16v3_OnFlatGroundTheFirstTowerStandsWhereTheChairsHaveClimbed()
        {
            // Finding r3 #3: steeper out of the station, to a taller first tower farther out. The empty rope leaves the return's
            // hold-down row at its aim (90% of 15.9°), the loaded one within its range, and the first tower stands where the
            // chairs have climbed to 15 ft: farther than the old 45 ft and taller than the old 15 ft.
            var b = Build(s => 0);
            AssertBuilt(b, "flat");
            Assert.That(Deg(b.Rope.BottomExitEmpty), Is.EqualTo(0.9 * 15.9).Within(0.1));
            Assert.That(Deg(b.Rope.BottomExitLoaded), Is.InRange(8.3, 15.9));
            var first = b.Rope.Towers[0];
            Assert.That(first.BaseS / Ft, Is.GreaterThan(45));
            Assert.That((first.RopeY - first.BaseY) / Ft, Is.GreaterThan(20), "rope height at the first tower");
            Assert.That(first.RopeY - Quad.ChairDropMetres - first.BaseY, Is.GreaterThanOrEqualTo(LiftRope.Clearance - LiftRope.ClearanceTolerance));
            Assert.That(b.Envelope.DeepestMetres, Is.LessThan(0.3), "flat ground needs no real trench");
        }

        [Test]
        public void TW16v3_TheLastSpanIsLevelIntoASupportRow()
        {
            // Finding r3 #1: on a top where the hill falls away, the last tower carries the rope at the station's rope height,
            // so it runs level into the drive and arrives from just below in both cases: the drive's row only supports it.
            var b = Build(s => 0.3 * s);
            AssertBuilt(b, "30%");
            Assert.That(Deg(b.Rope.TopEntryEmpty), Is.InRange(0, 0.1), "level, the empty sag's own small rise");
            Assert.That(Deg(b.Rope.TopEntryLoaded), Is.InRange(0, 2), "the loaded rope presses down harder, never pulls up");
            var last = b.Rope.Towers[b.Rope.Towers.Count - 1];
            Assert.That(800 - last.BaseS, Is.LessThanOrEqualTo(DesignUnits.StationZoneMm / 1000.0 + 1e-6), "a station tower");
            Assert.That(b.TopRaise, Is.EqualTo(0), "a falling hill needs no ramp");
        }

        [Test]
        public void TW16v3_AFlatSummitGetsARaisedUnloadRamp()
        {
            // Owner Q6: where the ground doesn't fall away from the top terminal, it stands on a built-up pad, a few feet.
            var b = Build(s => s < 650 ? 0.3 * s : 195);
            AssertBuilt(b, "flat top");
            Assert.That(b.TopRaise / Ft, Is.InRange(1, 10), Describe(b));
            Assert.That(Deg(b.Rope.TopEntryEmpty), Is.InRange(0, 0.1));
        }

        [Test]
        public void TW01v2_FindingFoursFirstSpanIsRed()
        {
            // Finding r3 #4: a 15 ft station tower 45 ft out, then a 250 ft span to a 35 ft tower over gentle ground. The old
            // rule exempted the whole span (the chairs only reached 15 ft near the next tower); the low zone now asks them
            // to climb to 15 ft by 100 ft, under each rope.
            var ground = new EditedHeights(new Profile(s => 0.12 * s), Lattice, TerrainEdits.Empty);
            var towers = new List<Tower> { new Tower(TowerId.Make(4, 0), 13_716, 4_572), new Tower(TowerId.Make(4, 1), 89_916, 10_668) };
            for (int i = 2; i < 10; i++) towers.Add(new Tower(TowerId.Make(4, i), 89_916 + (i - 1) * 76_200, 10_668));
            double bx = West + 100, by = North - 900;
            var rope = LiftRope.Solve(Quad, ground, bx, by, bx, North - 100, towers, TowerStance.Plumb,
                                      Quad.Bottom.ExitAlong, ground.At(bx, by) + Quad.Bottom.ExitRise, Quad.Top.ExitAlong, ground.At(bx, North - 100) + Quad.Top.ExitRise);
            Assert.That(rope.ClearanceOk, Is.False);
            double inFirstSpan = rope.ShortfallBetween(14, 89, out double at);
            Assert.That(inFirstSpan, Is.GreaterThan(1), $"short {inFirstSpan / Ft:F1} ft at {at / Ft:F0} ft");
        }

        [Test]
        public void TW01v2_ClearanceIsMeasuredUnderEachRopeAcrossTheChair()
        {
            // The same line and towers on a 30% sidehill: the centreline is the same height, but the uphill rope's chairs run
            // over higher ground, so the lowest clearance drops by the slope times the rope's and the chair's offset.
            var towers = Enumerable.Range(1, 9).Select(i => new Tower(TowerId.Make(6, i), i * 80_000, 12_192)).ToList();
            double bx = West + 100, by = North - 900;
            LiftRope On(double cross)
            {
                var g = new EditedHeights(new Profile(s => 0.3 * s, cross), Lattice, TerrainEdits.Empty);
                return LiftRope.Solve(Quad, g, bx, by, bx, North - 100, towers, TowerStance.Plumb,
                                      Quad.Bottom.ExitAlong, g.At(bx, by) + Quad.Bottom.ExitRise, Quad.Top.ExitAlong, g.At(bx, North - 100) + Quad.Top.ExitRise);
            }
            var level = On(0);
            var side = On(0.3);
            double expected = 0.3 * (Quad.GaugeMetres / 2 + Quad.ChairHalfWidthMetres);
            Assert.That(level.LowestClearance - side.LowestClearance, Is.EqualTo(expected).Within(0.05));
        }

        [Test]
        public void GR11v2_TheTrenchLetsTheChairsClimbOutOfASteepBottom()
        {
            // 30% from the bottom terminal on: steeper than the return's row can climb, so a narrow cut under the chairs (both
            // ropes' paths plus 2 ft) lets them rise to 15 ft by 100 ft; never deeper than 10 ft, cut only, cleared.
            var natural = new EditedHeights(new Profile(s => 0.3 * s), Lattice, TerrainEdits.Empty);
            var b = Build(s => 0.3 * s);
            AssertBuilt(b, "30%");
            var e = b.Envelope;
            Assert.That(e.CutCubicMetres, Is.GreaterThan(0));
            Assert.That(e.DeepestMetres, Is.LessThanOrEqualTo(LiftLineEnvelope.CapMetres + 0.06));
            Assert.That(e.ClearedCells, Is.GreaterThan(0));
            double half = Quad.GaugeMetres / 2 + Quad.ChairHalfWidthMetres + LiftLineEnvelope.MarginMetres;
            Assert.That(e.WidthMetres, Is.LessThanOrEqualTo(2 * (half + e.DeepestMetres) + 2), "the chair paths and their 1:1 sides");
            Assert.That(e.FromS, Is.GreaterThanOrEqualTo(Quad.Bottom.Ahead + TerminalPad.MarginMetres - 1.5 - 1e-6), "from the pad's edge");
            Assert.That(e.ToS, Is.LessThan(b.Rope.Towers[1].BaseS - LiftLineEnvelope.TowerKeepMetres + 1e-6), "clear of the first line tower");
            var edited = new EditedHeights(natural.Source, Lattice, e.Edits);
            for (double s = e.FromS; s <= e.ToS; s += 2)
                for (double d = -half - 15; d <= half + 15; d += 1.5)
                {
                    double x = West + 100 + d, y = North - 900 + s;
                    Assert.That(edited.At(x, y), Is.LessThanOrEqualTo(natural.At(x, y) + 1e-6), $"cut only at {s:F0} m, {d:F1} m");
                }
        }

        [Test]
        public void TW16v3_EveryGroundBuildsWithinTheRulesOrSaysWhy()
        {
            // Made-up ground covering what a player meets: each builds within every rule, or (a straight 40% line running into
            // a dead-flat summit) is left red for TW-06 with modest towers, never ever taller ones (TW-12).
            var grounds = new (string Name, Func<double, double> F, double Cross)[]
            {
                ("flat", s => 0, 0),
                ("gentle 5%", s => 0.05 * s, 0),
                ("30% easing to a flat top", s => s < 40 ? 0 : s < 670 ? 0.3 * (s - 40) : s < 760 ? 189 + 0.3 * (s - 670) - 0.15 * (s - 670) * (s - 670) / 90 : 202.5, 0),
                ("10% top", s => s < 650 ? 0.3 * s : 195 + 0.1 * (s - 650), 0),
                ("10% bottom", s => s < 150 ? 0.1 * s : 15 + 0.3 * (s - 150), 0),
                ("crest", s => s < 420 ? 0.3 * s : 126 + 0.12 * (s - 420), 0),
                ("hollow", s => s < 300 ? 0.25 * s : s < 500 ? 75 : 75 + 0.5 * (s - 500), 0),
                ("30% sidehill", s => 0.3 * s, 0.3),
            };
            foreach (var (name, f, cross) in grounds)
            {
                var b = Build(f, cross);
                TestContext.Progress.WriteLine($"{name}: {Describe(b)}");
                AssertBuilt(b, name);
                Assert.That(Deg(b.Rope.BottomExitEmpty), Is.EqualTo(0.9 * 15.9).Within(0.1), name);
                Assert.That(Deg(b.Rope.TopEntryEmpty), Is.InRange(0, 0.1), name);
            }
            var steep = Build(s => s < 740 ? 0.4 * s : 296);
            TestContext.Progress.WriteLine($"40% into a flat summit: {Describe(steep)}");
            Assert.That(steep.Rope.ClearanceOk && steep.Rope.TerminalsOk, Is.True, "clearance and terminals still hold");
            Assert.That(steep.Towers.Max(t => t.HeightMm), Is.LessThanOrEqualTo(DesignUnits.FeetToMm(60)), "modest towers, not a runaway");
        }

        [Test]
        public void LF08v2_ATerminalOnASteepSiteStandsHigherAndOnlyCuts()
        {
            // Finding r4 #3 (owner, SK30): steep sites are allowed; the terminal stands higher on its pier until it cuts at
            // most 10 ft into the hill, and its pad only cuts (LF-12 v3): nothing is filled up to it.
            var bench = Build(s => s < 30 ? 0 : s < 770 ? 0.3 * (s - 30) : 222);
            Assert.That(bench.BottomSteepRaise + bench.TopSteepRaise, Is.EqualTo(0), "both terminals on benches");
            Assert.That(bench.Ok, Is.True, Describe(bench));
            var cliff = Build(s => s < 30 ? 0 : s < 700 ? 0.3 * (s - 30) : 201 + 1.5 * (s - 700));   // a 150% face at the top
            Assert.That(cliff.TopSteepRaise, Is.GreaterThan(0), "the top on a 150% face stands higher");
            Assert.That(cliff.BottomPad.FillCubicMetres + cliff.TopPad.FillCubicMetres, Is.EqualTo(0), "cut only");
            Assert.That(cliff.Rope.TerminalsOk, Is.True, Describe(cliff));
        }

        [Test]
        public void SH04_ALiftBuildsTheSameEveryTime()
        {
            var a = Build(s => s < 300 ? 0.25 * s : s < 500 ? 75 : 75 + 0.5 * (s - 500), 0.1);
            var b = Build(s => s < 300 ? 0.25 * s : s < 500 ? 75 : 75 + 0.5 * (s - 500), 0.1);
            Assert.That(b.Towers.Select(t => (t.AlongMm, t.HeightMm)), Is.EqualTo(a.Towers.Select(t => (t.AlongMm, t.HeightMm))));
            Assert.That(TerrainEditBytes.Encode(b.Edits), Is.EqualTo(TerrainEditBytes.Encode(a.Edits)));
        }

        [Test]
        public void TW12_AOneMileLinePlacesWithinFiftyMilliseconds()
        {
            var ground = new EditedHeights(new Profile(s => 0.3 * s + 6 * Math.Sin(s / 60)), Lattice, TerrainEdits.Empty);
            double bx = West + 100, by = North - 1700, ty = by + 1609;
            TowerPlacement.Place(Quad, ground, bx, by, bx, ty, ground.At(bx, by), ground.At(bx, ty), TowerStance.Leaned);
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                TowerPlacement.Place(Quad, ground, bx, by, bx, ty, ground.At(bx, by), ground.At(bx, ty), TowerStance.Leaned);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            TestContext.Progress.WriteLine($"TW-12, a 1-mile line: median {times[2]:F1} ms");
            Assert.That(times[2], Is.LessThanOrEqualTo(50));
        }

        // Real ground: the committed Jackson Hole 2 km package, a fixed fan of lines.

        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.MontaFg4)]
        public void TW16v3_AFanOfRealLinesBuildsWithinTheRules(string catalogId)
        {
            var quad = LiftType.Of(catalogId);
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string file in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(file));
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            using var heights = new PackageHeights(dir, m);
            var natural = new EditedHeights(heights, new EditLattice(tiles.West, tiles.North), TerrainEdits.Empty);
            int built = 0, red = 0, gentle = 0, steep = 0, crested = 0, sidehill = 0;
            var problems = new List<string>();
            var buildMs = new List<double>();
            var heads = new SortedDictionary<LiftRope.Head, int>();
            // Where a terminal could stand (LF-08): natural ground varying by 10 ft or less over a 40 ft square, on a 20 m grid.
            double cx = m.Site.CentreX, cy = m.Site.CentreY, half = m.Site.SizeMetres / 2 - 60;
            var sites = new List<(double X, double Y, double H)>();
            for (double y = cy - half; y <= cy + half; y += 20)
                for (double x = cx - half; x <= cx + half; x += 20)
                {
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (double dy = -6; dy <= 6; dy += 2)
                        for (double dx = -6; dx <= 6; dx += 2)
                        {
                            double h = natural.At(x + dx, y + dy);
                            lo = Math.Min(lo, h);
                            hi = Math.Max(hi, h);
                        }
                    if (hi - lo <= 10 * Ft) sites.Add((x, y, natural.At(x, y)));
                }
            // Lines between them, as a player draws them: from the first site in each 250 m square up to the site whose rise is
            // nearest 600 ft, 1,000 to 3,000 ft away and within RP-05's rise.
            foreach (var cell in sites.GroupBy(p => (Math.Floor((p.X - cx) / 250), Math.Floor((p.Y - cy) / 250))).OrderBy(g => g.Key))
            {
                var bottom = cell.First();
                var tops = sites.Where(t =>
                {
                    double d = Math.Sqrt((t.X - bottom.X) * (t.X - bottom.X) + (t.Y - bottom.Y) * (t.Y - bottom.Y));
                    return d >= 300 && d <= 900 && t.H - bottom.H >= 30 && (t.H - bottom.H) / Ft <= 1100;
                }).OrderBy(t => Math.Abs(t.H - bottom.H - 183)).ThenBy(t => t.X).ThenBy(t => t.Y).ToList();
                if (tops.Count == 0) continue;
                var top = tops[0];
                double ax = bottom.X, ay = bottom.Y, bx = top.X, by = top.Y;
                double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)), rise = top.H - bottom.H;
                // The line's ground, for its class.
                double dx = (bx - ax) / length, dy = (by - ay) / length, bend = 0, side = 0;
                for (double k = 0; k <= length; k += 10)
                {
                    double x = ax + dx * k, y = ay + dy * k;
                    bend = Math.Max(bend, Math.Abs(natural.At(x, y) - (bottom.H + rise * k / length)));
                    side = Math.Max(side, Math.Abs(natural.At(x + dy * 5, y - dx * 5) - natural.At(x - dy * 5, y + dx * 5)) / 10);
                }
                double grade = rise / length;
                if (grade < 0.2) gentle++;
                if (grade > 0.4) steep++;
                if (bend > 10) crested++;
                if (side > 0.25) sidehill++;
                var sw = Stopwatch.StartNew();
                var b = LiftBuild.Build(quad, natural, ax, ay, bx, by, TowerStance.Leaned);
                foreach (var t in b.Rope.Towers) heads[t.Head] = heads.TryGetValue(t.Head, out int c) ? c + 1 : 1;
                buildMs.Add(sw.Elapsed.TotalMilliseconds);
                built++;
                string what = $"({ax - cx:F0}, {ay - cy:F0}) to ({bx - cx:F0}, {by - cy:F0}) m ({length / Ft:F0} ft, {grade:P0}, bend {bend:F0} m, side {side:P0})";
                TestContext.Progress.WriteLine($"{what}: {(b.Ok ? "ok" : "RED")} {Describe(b)}");
                if (!b.Rope.TerminalsOk) problems.Add(what + ": " + Describe(b));
                if (!b.Ok)
                {
                    red++;
                    // Task 09: a line nothing fits is red with a named reason, never a silent failure.
                    Assert.That(b.Problems, Is.Not.Empty, what);
                    TestContext.Progress.WriteLine("    " + string.Join("; ", b.Problems));
                }
                Assert.That(b.Towers.Max(t => t.HeightMm), Is.LessThanOrEqualTo(DesignUnits.TowerMaxMm), what);
            }
            buildMs.Sort();
            TestContext.Progress.WriteLine($"LiftBuild over the fan: median {buildMs[buildMs.Count / 2]:F0} ms, slowest {buildMs[buildMs.Count - 1]:F0} ms");
            TestContext.Progress.WriteLine($"heads: {string.Join(", ", heads.Select(kv => $"{kv.Key} {kv.Value}"))}");
            TestContext.Progress.WriteLine($"{built} lines: {red} red; gentle {gentle}, steep {steep}, crest or hollow {crested}, sidehill {sidehill}");
            // Every terminal takes its rope (TW-16 v3); most lines build within every rule. The rest are red for a rule the
            // tool will show: a crest too sharp for the room on cliffy ground (TW-06), or a small shortfall just past a low
            // zone (task 09's full placement).
            Assert.That(problems, Is.Empty);
            Assert.That(built - red, Is.GreaterThanOrEqualTo(0.7 * built), $"{built - red} of {built} lines within every rule");
            Assert.That(gentle, Is.GreaterThan(0), "the fan covers gentle ground");
            Assert.That(steep, Is.GreaterThan(0), "and steep");
            Assert.That(crested, Is.GreaterThan(0), "and a crest or hollow");
            Assert.That(sidehill, Is.GreaterThan(0), "and a sidehill");
        }
    }
}
