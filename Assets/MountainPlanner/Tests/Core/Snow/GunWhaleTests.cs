using System;
using System.Linq;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A3 (owner, 2026-11-15: "Build it now, soft"): the gun whale built by SG-03, SG-05 and SN-27 keeps every cm³
    /// of the gun's water, stands no higher than the cap, sums into the snow field's squares exactly, and plays in the live
    /// snow like any pile: every day's water balances and it tapers from its middle with no ring.
    /// </summary>
    public sealed class GunWhaleTests
    {
        static readonly GunWhale.Settings Stick = GunWhale.Settings.Stick20;
        const int Density = 300;

        [Test]
        public void TheSnowMadeIsTheGunsWaterLessTheLoss()
        {
            // SG-05: 48 gpm × 60 × 0.85 = 2,448 gallons of water an hour.
            Assert.That(GunWhale.HourCm3(Stick), Is.EqualTo((long)Math.Round(2448 * GunWhale.GallonCm3)));
            foreach (int nights in new[] { 0, 1, 3 })
            {
                var w = GunWhale.Build(10, -20, 0, 1, nights, Density, Stick);
                long sum = 0;
                foreach (long v in w.WaterCm3) sum += v;
                Assert.That(w.MadeCm3, Is.EqualTo(GunWhale.HourCm3(Stick) * 16 * nights), $"{nights} nights made");
                Assert.That(sum, Is.EqualTo(w.MadeCm3), $"{nights} nights: every cm³ is on the ground");
            }
        }

        [Test]
        public void NoSquareStandsOverTheCapAndTheSpillLosesNothing()
        {
            var one = GunWhale.Build(0, 0, 1, 1, 1, Density, Stick);
            var three = GunWhale.Build(0, 0, 1, 1, 3, Density, Stick);
            double TopOf(GunWhale w)
            {
                double top = 0;
                for (int j = 0; j < w.Size; j++)
                    for (int i = 0; i < w.Size; i++) top = Math.Max(top, w.DepthMetres(i, j));
                return top;
            }
            foreach (long v in three.WaterCm3) Assert.That(v, Is.LessThanOrEqualTo(three.CapCm3));
            double top1 = TopOf(one), top3 = TopOf(three);
            TestContext.Progress.WriteLine($"[gun whale] 1 night: {top1 / 0.3048:F1} ft tall; 3 nights: {top3 / 0.3048:F1} ft (cap 26 ft)");
            Assert.That(top1, Is.GreaterThan(3).And.LessThan(Stick.CapMetres), "one night's whale stands under the cap");
            Assert.That(top3, Is.EqualTo(Stick.CapMetres).Within(0.01), "three nights' reaches the cap");
        }

        [Test]
        public void TheFieldsSquaresHoldTheSameWater()
        {
            var w = GunWhale.Build(101.3, 77.9, -0.3, -1, 3, Density, Stick);
            var cells = w.Coarse(0, 0, 8, 32, 32, out long outside);
            long sum = 0;
            foreach (long v in cells) sum += v;
            Assert.That(outside, Is.Zero);
            Assert.That(sum, Is.EqualTo(w.MadeCm3), "8 m squares hold every cm³ the 3 ft grid does");
            // Off the field's edge, the rest is counted, not lost.
            var part = w.Coarse(100, 0, 8, 32, 32, out long off);
            long partSum = 0;
            foreach (long v in part) partSum += v;
            Assert.That(partSum + off, Is.EqualTo(w.MadeCm3));
            Assert.That(off, Is.GreaterThan(0));
        }

        [Test]
        public void TheWhaleAsAddedDepthIsItsWaterOverTheSquares()
        {
            // AddedMetres is Coarse's water as snow at the whale's density over each square; the model's own peak is the cap.
            var whale = GunWhale.Build(256, 260, 1, 0, 3, 300, Stick);
            var water = whale.Coarse(0, 0, 8, 64, 64, out _);
            var added = whale.AddedMetres(0, 0, 8, 64, 64, out long outside);
            Assert.That(outside, Is.Zero);
            double snowM3 = 0;
            for (int c = 0; c < added.Length; c++)
            {
                Assert.That(added[c], Is.EqualTo(water[c] / 1e6 * 1000.0 / 300 / 64.0).Within(1e-4), "square " + c);
                snowM3 += added[c] * 64.0;
            }
            Assert.That(snowM3, Is.EqualTo(whale.MadeCm3 / 1e6 * 1000.0 / 300).Within(0.5), "all of the gun's snow");
            Assert.That(whale.PeakMetres, Is.EqualTo(Stick.CapMetres).Within(0.02), "three nights fill the cap: the modelled peak is 26 ft");
            Assert.That(added.Max(), Is.LessThan(whale.PeakMetres), "a square's mean stands below the model's peak");
        }

        [Test]
        public void AGunWhalePlaysInTheLiveSnowAndTapers()
        {
            var p = Pacing.Default;
            var m = SnowWhaleTests.Valley(p);
            var world = SnowValleyProbeTests.World(p, m);
            SnowFieldTests.RunTo(world, 11, 19, p.IdleTickFactor);
            world.JoinSnow();
            const int W = 64, D = 64, Cx = 32, Cy = 32;
            // The gun at the middle square's west edge, aimed east; its snow as packed powder at the seed's density.
            var whale = GunWhale.Build(Cx * 8, Cy * 8 + 4, 1, 0, 3, p.SeedDensity, Stick);
            // Laid as the game's developer seed lays it (finding W6): added to the snow already there, by the same method.
            var added = whale.AddedMetres(0, 0, 8, W, D, out long outside);
            Assert.That(outside, Is.Zero);
            var mm = new ushort[W * D];
            int peak = 0, peakAt = 0;
            for (int c = 0; c < W * D; c++)
            {
                int snowMm = (int)Math.Round(added[c] * 1000.0);
                int depth = world.Snow[c % W, c / W] + snowMm;
                mm[c] = (ushort)Math.Min(ushort.MaxValue, depth);
                if (snowMm > peak) { peak = snowMm; peakAt = c; }
            }
            world.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, 0, 0, W, D, mm)));
            TestContext.Progress.WriteLine($"[gun whale] 3 nights on 8 m squares: {peak / 304.8:F1} ft at its tallest square");
            Assert.That(peak, Is.GreaterThan(2000), "drawn on 8 m squares, three nights' whale still stands metres tall");

            int px = peakAt % W, py = peakAt / W, last = world.SnowLastDay.Day, days = 0;
            while (!SnowValleyProbeTests.AtOrPast(world, 12, 15))
            {
                world.Step(p.IdleTickFactor);
                if (world.SnowLastDay.Day == last) continue;
                last = world.SnowLastDay.Day;
                world.JoinSnow();
                days++;
                Assert.That(world.SnowLastDay.Balance, Is.Zero, $"day {days}: the water balances to the µm");
                // From the tallest square outward across the aim the water only falls (no ring round the pile).
                int prev = int.MaxValue;
                for (int y = py; y < D; y++)
                {
                    int swe = world.Snow.PackAt(px, y).Swe;
                    Assert.That(swe, Is.LessThanOrEqualTo(prev), $"day {days}: water across the whale at {(y - py) * 8} m");
                    prev = swe;
                }
            }
            Assert.That(days, Is.GreaterThanOrEqualTo(8));
        }
    }
}
