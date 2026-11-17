using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// SN-06 v3 (task 4.07 PR A2, owner 2026-11-14): the shed's run-out fan builds a cone at a cliff's foot, but a fan cell
    /// takes shed snow only while its water stands under <see cref="Pacing.ShedPileRatio"/> (3×) the mean water round it; the
    /// rest runs on down the fall line. Without it the 11 km Jackson Hole lapse grew one cell under a cliff band to 736 in by
    /// February (19 × the mean).
    /// </summary>
    public sealed class SnowShedTests
    {
        const int W = 128, D = 128;

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        /// <summary>A V-shaped bowl draining over a 320 m cliff (200 % grade, longer than the shed's 100 m slide) onto a gentle
        /// apron: every steep cell's shed converges on the couloir at the bowl's middle, as above Corbet's.</summary>
        static SnowFieldTests.Mountain CliffBand(Pacing p)
        {
            var m = new SnowFieldTests.Mountain { Mm = new int[W * D], Feet = new int[W * D], Canopy = new byte[W * D] };
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++)
                {
                    // Rows count north; the ground falls to the south. Apron (rows < 40) at 10 %, the face (40–80) at 200 %,
                    // a bowl above at 10 %; from the face up, the sides rise toward the middle column at up to 100 % across.
                    double y = j, apron = Math.Min(y, 40) * 0.8, face = Math.Max(0, Math.Min(y, 80) - 40) * 16, top = Math.Max(0, y - 80) * 0.8;
                    double v = Math.Max(0, Math.Min(1, (y - 40) / 40)) * 8 * Math.Abs(i - W / 2);
                    double metres = 2400 + apron + face + top + v;
                    int c = j * W + i;
                    m.Mm[c] = (int)Math.Round(metres * 1000);
                    m.Feet[c] = (int)Math.Round(metres / 0.3048);
                }
            m.Weather = WeatherTerrain.Build(W, D, m.Feet, -W / 2 * 8000, -D / 2 * 8000, (W / 5, D / 5, W * 4 / 5, D * 4 / 5), D / 2 * W + W / 2,
                                             SimWeather.TerrainTuning(p));
            m.Snow = SnowTerrain.Build(W, D, m.Mm, m.Canopy, m.Weather, (int[])p.WindExposure.Clone());
            return m;
        }

        /// <summary>The deepest cell's water against the mean water of the 16 × 16 cells round it, by November 30.</summary>
        static (double Ratio, int Swe, int Bad) Cone(string pacing)
        {
            var p = Pacing.Parse(pacing);
            var m = CliffBand(p);
            var w = new SimWorld(p, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
            SnowFieldTests.SendInputs(w, m);
            int bad = 0;
            SnowFieldTests.RunTo(w, 11, 30, p.IdleTickFactor, l => { if (l.Balance != 0) bad++; });
            int best = 0, bx = 0, by = 0;
            for (int j = 8; j < D - 8; j++)
                for (int i = 8; i < W - 8; i++)
                {
                    int s = w.Snow.PackAt(i, j).Swe;
                    if (s > best) { best = s; bx = i; by = j; }
                }
            long sum = 0;
            for (int j = by - 8; j < by + 8; j++)
                for (int i = bx - 8; i < bx + 8; i++) sum += w.Snow.PackAt(i, j).Swe;
            double mean = sum / 256.0;
            Say($"[cone] {(pacing.Length == 0 ? "SN-06 v3" : pacing)}: deepest {best / 25_400.0:F1} in of water at {bx},{by}, " +
                $"{best / Math.Max(1, mean):F1} × the 128 m round it; ledger misses {bad}");
            return (best / Math.Max(1, mean), best, bad);
        }

        [Test]
        public void SN06_ACliffFootsConeSpreadsOnceItStandsThreeTimesTheSnowRoundIt()
        {
            var on = Cone("");
            var off = Cone("ShedPileRatio = 100000");
            Assert.That(on.Bad, Is.Zero, "every day's ledger balances");
            Assert.That(off.Ratio, Is.GreaterThan(on.Ratio), "the cone cap is what holds the pile down");
            Assert.That(on.Ratio, Is.LessThan(3.5), "the cone stands about three times the snow round it, no more");
        }
    }
}
