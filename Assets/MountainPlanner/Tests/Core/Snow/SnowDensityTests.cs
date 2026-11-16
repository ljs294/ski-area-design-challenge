using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2: SN-08 v2's density. New snow is light when it's cold and heavy when it's wet (the wet bulb's snow
    /// ratio); settling moves the pack toward 300, 380 or 450 kg/m³ by wetness, faster when wet, without changing its water;
    /// the depth is always the water ÷ the density.
    /// </summary>
    public sealed class SnowDensityTests
    {
        static readonly SnowTuning K = SnowTuning.Default;

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        /// <summary>A night hour with snow of the hour's wet bulb's density.</summary>
        static SnowInput Snowing(double airF, double dewF, int snowUm)
        {
            var x = SnowEnergyTests.Hour(airF, dewF, windMph: 0, cloud: 1000);
            x.Snow = snowUm;
            x.SnowDensity = 10_000 / WeatherAir.SnowRatioTenths(x.WetBulb);
            return x;
        }

        [Test]
        public void SN08_NewSnowIsLightWhenColdAndHeavyWhenWet()
        {
            var cold = new SnowPack();
            var wet = new SnowPack();
            SnowEnergy.Step(ref cold, Snowing(12, 8, 5080), K);
            SnowEnergy.Step(ref wet, Snowing(33, 32.5, 5080), K);
            Say($"[density] a fifth of an inch of water: {cold.DensityKg} kg/m³ at 12 °F ({cold.DepthMm / 25.4:F1} in), " +
                $"{wet.DensityKg} kg/m³ at 33 °F ({wet.DepthMm / 25.4:F1} in)");
            Assert.That(cold.DensityKg, Is.LessThan(90), "cold snow is light");
            Assert.That(wet.DensityKg, Is.GreaterThan(100), "wet snow is heavy");
            Assert.That(cold.DepthMm, Is.GreaterThan(wet.DepthMm), "the same water stands deeper when it's cold");
        }

        /// <summary>Hours of the same weather on a pack; checks each hour's water and depth; the density at the end.</summary>
        static int Settle(ref SnowPack p, SnowInput x, int hours)
        {
            for (int h = 0; h < hours; h++)
            {
                int swe = p.Swe;
                var o = SnowEnergy.Step(ref p, x, K);
                Assert.That(p.Swe, Is.EqualTo(swe + x.Snow + x.Rain - o.Runoff - o.Sublimated), $"hour {h}: settling keeps the water");
                Assert.That(p.DepthMm, Is.EqualTo((int)((long)p.Swe * 1000 / p.Density)), "depth is water ÷ density");
            }
            return p.DensityKg;
        }

        [Test]
        public void SN08_SettlingMovesTowardItsTargetByWetnessAndKeepsTheWater()
        {
            var cold = SnowEnergyTests.Pack(10, -8, 120);
            var wet = SnowEnergyTests.Pack(10, 0, 120, liquidShare: 0.05);
            int c1 = Settle(ref cold, SnowEnergyTests.Hour(10, 9, windMph: 0, cloud: 1000), 240);
            int w1 = Settle(ref wet, SnowEnergyTests.Hour(34, 33.5, windMph: 0, cloud: 1000), 240);
            Say($"[density] 10 face days from 120 kg/m³: cold {c1} kg/m³, wet {w1} kg/m³");
            Assert.That(c1, Is.GreaterThan(120).And.LessThanOrEqualTo(300), "cold snow settles toward 300");
            Assert.That(w1, Is.GreaterThan(c1), "wet snow settles faster and denser");
            Assert.That(w1, Is.LessThanOrEqualTo(450), "toward 450, never past it");
            var dense = SnowEnergyTests.Pack(10, -8, 420);
            int d1 = Settle(ref dense, SnowEnergyTests.Hour(10, 9, windMph: 0, cloud: 1000), 48);
            Assert.That(d1, Is.EqualTo(420), "settling never thins a pack");
        }
    }
}
