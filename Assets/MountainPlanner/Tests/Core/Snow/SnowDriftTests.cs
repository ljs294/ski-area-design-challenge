using System;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// SN-07 v3 (task 4.07 PR A2): the wind's drift as a gather. Each drifting cell weighs one footprint downwind, the same for
    /// the whole map in an hour; each cell then gathers its share from upwind, with the same integers, so the drift conserves
    /// to the µm, runs on any number of threads, and lands spread, not in lines.
    /// </summary>
    public sealed class SnowDriftTests
    {
        const int W = 256, D = 256;

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        [Test]
        public void SN07_TheFootprintSumsExactlyAndLiesDownwind()
        {
            int steps = Pacing.Default.DriftReach / SimSnow.CellMetres;
            var kx = new int[1024]; var ky = new int[1024]; var kw = new int[1024];
            double worstAcross = 0, worstAlong = 0;
            int fewestAcross = int.MaxValue, biggest = 0;
            for (int to = 0; to < 360; to++)
            {
                int n = SnowHour.DriftKernel(to, steps, kx, ky, kw);
                long sum = 0;
                double along = 0, across = 0;
                double ux = Math.Sin(to * Math.PI / 180), uy = Math.Cos(to * Math.PI / 180);   // grid degrees: 0 = +y
                var lanes = new System.Collections.Generic.HashSet<int>();
                for (int k = 0; k < n; k++)
                {
                    Assert.That(kw[k], Is.GreaterThan(0));
                    sum += kw[k];
                    along += kw[k] * (kx[k] * ux + ky[k] * uy);
                    double a = kx[k] * -uy + ky[k] * ux;
                    across += kw[k] * a;
                    lanes.Add((int)Math.Round(a));
                    biggest = Math.Max(biggest, kw[k]);
                }
                Assert.That(sum, Is.EqualTo(65_536), $"{to}°: the footprint's weights sum to one exactly");
                worstAlong = Math.Max(worstAlong, Math.Abs(along / sum - (steps + 1) / 2.0));
                worstAcross = Math.Max(worstAcross, Math.Abs(across / sum));
                fewestAcross = Math.Min(fewestAcross, lanes.Count);
            }
            Say($"[drift] footprint over 360°: centre along the wind off by {worstAlong:F2} cells, across by {worstAcross:F2}; " +
                $"at least {fewestAcross} lanes across; the largest cell takes {biggest * 100.0 / 65_536:F1} %");
            Assert.That(worstAlong, Is.LessThan(0.6), "the footprint's centre sits half the reach downwind");
            Assert.That(worstAcross, Is.LessThan(0.15), "and on the wind's line");
            Assert.That(fewestAcross, Is.GreaterThanOrEqualTo(3), "spread across the wind, not one line of cells");
            Assert.That(biggest, Is.LessThan(65_536 / steps), "no cell takes more than one step's share");
        }

        /// <summary>A windy world: every wet hour drifts (no wind threshold, the whole share).</summary>
        static Pacing Windy => Pacing.Parse("DriftWind = 0\nDriftShareMax = 1000");
        static Pacing Calm => Pacing.Parse("DriftShareMax = 0");

        static SimWorld RunToNov15(Pacing p, out int days, out int bad, out long worst, out long offMap)
        {
            var w = new SimWorld(p, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
            SnowFieldTests.SendInputs(w, SnowFieldTests.Shared);
            int b = 0; long wo = 0, off = 0;
            days = SnowFieldTests.RunTo(w, 11, 15, p.IdleTickFactor, l =>
            {
                if (l.Balance != 0) { b++; wo = Math.Max(wo, Math.Abs(l.Balance)); }
                off += l.OffMap;
            });
            bad = b; worst = wo; offMap = off;
            return w;
        }

        [Test]
        public void SN07_TheDriftConservesToTheMicron()
        {
            var windy = RunToNov15(Windy, out int days, out int bad, out long worst, out long off);
            var calm = RunToNov15(Calm, out _, out _, out _, out _);
            long moved = 0;
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++) moved += Math.Abs(windy.Snow.PackAt(i, j).Swe - calm.Snow.PackAt(i, j).Swe);
            Say($"[drift] {days} windy days: ledger misses {bad} (worst {worst} µm); {moved / 1e6:F1} m³·µm/m² differs from calm; {off / 1e6:F1} blown off the map");
            Assert.That(bad, Is.Zero, "every day's ledger balances, the drift included");
            Assert.That(moved, Is.GreaterThan(0), "the wind moved snow");
        }

        /// <summary>A one-cell line: a cell standing out from both neighbours along a row (or a column) the same way.</summary>
        static int Lines(SimWorld w, out int cells)
        {
            int lines = 0; cells = 0;
            for (int j = 2; j < D - 2; j++)
                for (int i = 2; i < W - 2; i++)
                {
                    float d = w.Snow[i, j];
                    if (d < 5) continue;
                    cells++;
                    float l = w.Snow[i - 1, j], r = w.Snow[i + 1, j], u = w.Snow[i, j - 1], b = w.Snow[i, j + 1];
                    float tol = Math.Max(1f, d * 0.08f);
                    bool row = (d - l > tol && d - r > tol) || (l - d > tol && r - d > tol);
                    bool col = (d - u > tol && d - b > tol) || (u - d > tol && b - d > tol);
                    if (row || col) lines++;
                }
            return lines;
        }

        [Test]
        public void SN07_TheDriftLeavesNoStripes()
        {
            var windy = RunToNov15(Windy, out _, out _, out _, out _);
            var calm = RunToNov15(Calm, out _, out _, out _, out _);
            int wl = Lines(windy, out int wc), cl = Lines(calm, out int cc);
            Say($"[drift] one-cell lines on Nov 15: windy {wl} of {wc} snowy cells, calm {cl} of {cc}");
            Assert.That(wc, Is.GreaterThan(1000), "there is snow to look at");
            Assert.That(wl, Is.LessThanOrEqualTo(cl + wc / 500), "the drift adds no lines of cells to the ground's own");
        }
    }
}
