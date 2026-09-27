using System;
using MountainPlanner.Domain.Sky;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 11 acceptance (docs/plans/phase0-0.7-phase1-plan.md): sun position within 0.1° of
    // published values. Engine-free.
    public sealed class SolarPositionTests
    {
        // Reference: NREL Solar Position Algorithm (Reda & Andreas 2004, ±0.0003°), computed with
        // pvlib 0.13 spa_python at sea level; times are UTC. Columns: name, lat, lon, year, day of
        // year, second of day, apparent elevation, geometric elevation, azimuth.
        public static readonly object[] NrelSpa =
        {
            new object[] { "Jackson Hole winter noon", 43.593, -110.848, 2026, 15, 68400, 24.9883, 24.9509, 171.5332 },
            new object[] { "Jackson Hole winter morning", 43.593, -110.848, 2026, 15, 55800, 4.9405, 4.7668, 125.2322 },
            new object[] { "Jackson Hole summer evening", 43.593, -110.848, 2026, 172, 9000, 5.2496, 5.0840, 297.8150 },
            new object[] { "Crystal Mountain Feb afternoon", 46.93, -121.49, 2026, 41, 82800, 19.2897, 19.2402, 221.2874 },
            new object[] { "Crystal Mountain Dec solstice noon", 46.93, -121.49, 2026, 355, 72600, 19.6665, 19.6180, 181.4141 },
            new object[] { "Killington March morning", 43.6045, -72.8201, 2027, 79, 43200, 11.0007, 10.9142, 100.7834 },
            new object[] { "Taos leap day", 36.5961, -105.4545, 2028, 60, 67500, 45.3426, 45.3252, 169.6924 },
            new object[] { "Big Sky night", 45.284, -111.401, 2026, 15, 21600, -58.9671, -58.9671, 313.2736 },
        };

        [TestCaseSource(nameof(NrelSpa))]
        public void MatchesTheNrelAlgorithmWithinATenthOfADegree(string name, double lat, double lon, int year, int day, int second,
                                                                 double apparent, double geometric, double azimuth)
        {
            var sun = SolarPosition.At(lat, lon, year, day, second);
            Assert.That(sun.GeometricElevation, Is.EqualTo(geometric).Within(0.1), name + " elevation");
            Assert.That(AngleBetween(sun.Azimuth, azimuth), Is.LessThan(0.1), name + " azimuth");
            if (geometric > 2)  // refraction models differ most at the horizon
                Assert.That(sun.Elevation, Is.EqualTo(apparent).Within(0.1), name + " apparent elevation");
        }

        [Test]
        public void NightIsBelowTheHorizon()
        {
            Assert.That(SolarPosition.At(45.284, -111.401, 2026, 15, 21600).IsAboveHorizon, Is.False);
            Assert.That(SolarPosition.At(43.593, -110.848, 2026, 15, 68400).IsAboveHorizon, Is.True);
        }

        [Test]
        public void TheSameInputsGiveTheSameAnswer()
        {
            var a = SolarPosition.At(43.593, -110.848, 2026, 15, 68400.5);
            var b = SolarPosition.At(43.593, -110.848, 2026, 15, 68400.5);
            Assert.That(a.Elevation, Is.EqualTo(b.Elevation));
            Assert.That(a.Azimuth, Is.EqualTo(b.Azimuth));
        }

        [Test]
        public void GridConvergenceRotatesTheSunOnTheResortGrid()
        {
            var sun = SolarPosition.At(43.593, -110.848, 2026, 15, 68400);
            // At Jackson Hole true north lies 8.95° clockwise of grid north (PROJ, EPSG:6350), so every
            // true bearing is 8.95° larger on the grid.
            Assert.That(sun.GridAzimuth(8.9519), Is.EqualTo(sun.Azimuth + 8.9519).Within(1e-9));
            Assert.That(sun.GridAzimuth(-10), Is.EqualTo(sun.Azimuth - 10).Within(1e-9));
            var (x, y, z) = sun.DirectionInFrame(0);
            Assert.That(x * x + y * y + z * z, Is.EqualTo(1).Within(1e-12));
            Assert.That(z, Is.LessThan(0), "a winter noon sun is in the south");
            Assert.That(y, Is.GreaterThan(0));
        }

        [TestCase(2026, 0, 0.0)]
        [TestCase(2026, 366, 0.0)]
        [TestCase(2026, 1, 86400.0)]
        public void InvalidTimesAreRejected(int year, int day, double second)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SolarPosition.At(43.6, -110.8, year, day, second));
        }

        static double AngleBetween(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }
    }
}
