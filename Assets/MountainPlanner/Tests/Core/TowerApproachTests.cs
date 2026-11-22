using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.26 BB16 (owner, 2026-11-21): the last towers before a terminal stood far taller than the line, so the chairs
    /// went high, then low, then up to the terminal. Measured over the Jackson Hole fan of real lines: a tower's "spike" is
    /// how much taller it stands than the straight line between its neighbours' heights (at its place along the line).
    /// </summary>
    public class TowerApproachTests
    {
        const double Ft = 0.3048;

        /// <summary>A tower's height over the line between its neighbours' heights, metres.</summary>
        static double Spike(IReadOnlyList<LiftRope.TowerSolution> t, int i)
        {
            double s0 = t[i - 1].BaseS, s1 = t[i + 1].BaseS, f = (t[i].BaseS - s0) / Math.Max(1e-6, s1 - s0);
            double h0 = t[i - 1].RopeY - t[i - 1].BaseY, h1 = t[i + 1].RopeY - t[i + 1].BaseY;
            return t[i].RopeY - t[i].BaseY - (h0 + (h1 - h0) * f);
        }

        internal static IEnumerable<(string What, LiftBuild.Result Build)> Fan(string catalogId)
        {
            var type = LiftType.Of(catalogId);
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string file in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(file));
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            using var heights = new PackageHeights(dir, m);
            var natural = new EditedHeights(heights, new EditLattice(tiles.West, tiles.North), TerrainEdits.Empty);
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
            var results = new List<(string, LiftBuild.Result)>();
            foreach (var cell in sites.GroupBy(p => (Math.Floor((p.X - cx) / 250), Math.Floor((p.Y - cy) / 250))).OrderBy(g => g.Key))
            {
                var bottom = cell.First();
                var top = sites.Where(t =>
                {
                    double d = Math.Sqrt((t.X - bottom.X) * (t.X - bottom.X) + (t.Y - bottom.Y) * (t.Y - bottom.Y));
                    return d >= 300 && d <= 900 && t.H - bottom.H >= 30 && (t.H - bottom.H) / Ft <= 1100;
                }).OrderBy(t => Math.Abs(t.H - bottom.H - 183)).ThenBy(t => t.X).ThenBy(t => t.Y).FirstOrDefault();
                if (top == default) continue;
                var b = DesignTestKit.AsPlayer((hb, ht) => LiftBuild.Build(type, natural, bottom.X, bottom.Y, top.X, top.Y, TowerStance.Leaned, null, hb, ht));
                results.Add(($"({bottom.X - cx:F0}, {bottom.Y - cy:F0}) to ({top.X - cx:F0}, {top.Y - cy:F0})", b));
            }
            return results;
        }

        // Before the fix (2026-11-21): the worst tower next to a station tower stood 60 ft (FGQ-4) and 55 ft (D6) above
        // the line between its neighbours, and 50 of the two lifts' 651 towers were 80 ft or taller (a 97 ft tower beside a
        // 15 ft station tower). After: 34 and 33 ft, and 11 of 682 (1 of 335 FGQ-4, 10 of 347 D6).
        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.SesselliftD6)]
        public void BB16_TheTowersApproachingATerminalStandInLineWithTheRest(string catalogId)
        {
            int towers = 0, tall = 0, red = 0, lines = 0;
            var ends = new List<double>();
            var middles = new List<double>();
            foreach (var (what, b) in Fan(catalogId))
            {
                var t = b.Rope.Towers;
                lines++;
                if (!b.Ok) red++;
                towers += t.Count;
                tall += t.Count(x => x.RopeY - x.BaseY >= 80 * Ft);
                if (t.Count < 4) continue;
                double endB = Spike(t, 1), endT = Spike(t, t.Count - 2);
                double mid = 0;
                for (int i = 2; i < t.Count - 2; i++) mid = Math.Max(mid, Spike(t, i));
                ends.Add(Math.Max(endB, endT));
                middles.Add(mid);
                TestContext.Progress.WriteLine($"{what}: {(b.Ok ? "ok" : "RED")} {t.Count} towers; spike at the bottom {endB / Ft:F0} ft, " +
                    $"top {endT / Ft:F0} ft, middle max {mid / Ft:F0} ft; heights {string.Join(" ", t.Select(x => ((x.RopeY - x.BaseY) / Ft).ToString("F0")))}; at {string.Join(" ", t.Select(x => (x.BaseS / Ft).ToString("F0")))} of {b.Rope.LengthMetres / Ft:F0} ft; ground {string.Join(" ", t.Select(x => (x.BaseY / Ft).ToString("F0")))}");
            }
            ends.Sort();
            middles.Sort();
            TestContext.Progress.WriteLine($"{ends.Count} lines: end spike median {ends[ends.Count / 2] / Ft:F0} ft, 90th {ends[ends.Count * 9 / 10] / Ft:F0} ft, " +
                $"worst {ends[ends.Count - 1] / Ft:F0} ft; middle median {middles[middles.Count / 2] / Ft:F0} ft, worst {middles[middles.Count - 1] / Ft:F0} ft; " +
                $"{tall} of {towers} towers 80 ft or taller, {red} red");
            Assert.That(ends[ends.Count - 1], Is.LessThanOrEqualTo(40 * Ft), "no tower next to a station tower stands far above its neighbours");
            Assert.That(tall, Is.LessThanOrEqualTo(towers * 3 / 100), "80 ft towers only where the ground needs them");
            Assert.That(red, Is.LessThanOrEqualTo(lines * 3 / 10), "and the lines still build (TW16v3_AFanOfRealLinesBuildsWithinTheRules)");
        }
    }
}
