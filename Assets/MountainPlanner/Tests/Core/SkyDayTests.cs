using System;
using MountainPlanner.Domain.Sky;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 11: the game's clock (local standard time) to the sun, the day's sun events, and the moon (A4).
    // Engine-free.
    public sealed class SkyDayTests
    {
        const double JacksonLat = 43.593, JacksonLon = -110.848;

        [TestCase(-110.848, -7)]
        [TestCase(-121.49, -8)]
        [TestCase(-70.3, -5)]
        [TestCase(-72.82, -5)]
        [TestCase(7.5, 1)]
        [TestCase(0, 0)]
        public void TheTimeZoneIsTheNearestFifteenDegrees(double longitude, int hours)
        {
            Assert.That(SolarDay.StandardOffsetHours(longitude), Is.EqualTo(hours));
        }

        [Test]
        public void LocalTimeConvertsToUtcAcrossDaysAndYears()
        {
            var day = new SolarDay(JacksonLat, JacksonLon, 2026, 15);
            Assert.That(day.ToUtc(12 * 3600), Is.EqualTo((2026, 15, 19.0 * 3600)));
            Assert.That(day.ToUtc(22 * 3600), Is.EqualTo((2026, 16, 5.0 * 3600)), "10 pm MST is the next day in UTC");
            var newYearsEve = new SolarDay(JacksonLat, JacksonLon, 2026, 365);
            Assert.That(newYearsEve.ToUtc(20 * 3600), Is.EqualTo((2027, 1, 3.0 * 3600)));
            var tokyo = new SolarDay(35.7, 139.7, 2027, 1);
            Assert.That(tokyo.ToUtc(3600), Is.EqualTo((2026, 365, 16.0 * 3600)), "east of Greenwich runs back into last year");
        }

        [Test]
        public void TheSunAtALocalTimeIsTheSunAtTheMatchingUtcMoment()
        {
            // 12:00 MST on 15 January 2026 is 19:00 UTC: the NREL reference case in SolarPositionTests.
            var sun = new SolarDay(JacksonLat, JacksonLon, 2026, 15).SunAt(12 * 3600);
            Assert.That(sun.GeometricElevation, Is.EqualTo(24.9509).Within(0.1));
            Assert.That(sun.Azimuth, Is.EqualTo(171.5332).Within(0.1));
        }

        [Test]
        public void SunriseNoonAndSunsetFallWhereTheyShouldInJacksonHole()
        {
            // Mid-January at Jackson Hole (MST): sunrise about 7:53, solar noon about 12:33 (5.8° west of the zone's
            // meridian, and the equation of time runs 9 minutes slow), sunset about 17:13.
            var day = new SolarDay(JacksonLat, JacksonLon, 2026, 15);
            double rise = day.Sunrise(), noon = day.SolarNoon(), set = day.Sunset();
            Assert.That(rise / 3600, Is.EqualTo(7.87).Within(0.15), "sunrise");
            Assert.That(noon / 3600, Is.EqualTo(12.55).Within(0.05), "solar noon");
            Assert.That(set / 3600, Is.EqualTo(17.22).Within(0.15), "sunset");
            Assert.That(day.SunAt(rise).GeometricElevation, Is.EqualTo(SolarDay.HorizonDegrees).Within(0.01));
            Assert.That(day.SunAt(set).GeometricElevation, Is.EqualTo(SolarDay.HorizonDegrees).Within(0.01));
            double top = day.SunAt(noon).GeometricElevation;
            Assert.That(top, Is.GreaterThanOrEqualTo(day.SunAt(noon - 120).GeometricElevation));
            Assert.That(top, Is.GreaterThanOrEqualTo(day.SunAt(noon + 120).GeometricElevation));
        }

        [Test]
        public void PolarNightHasNoSunrise()
        {
            var svalbard = new SolarDay(78.2, 15.6, 2026, 15);
            Assert.That(double.IsNaN(svalbard.Sunrise()), Is.True);
            Assert.That(double.IsNaN(svalbard.Sunset()), Is.True);
        }

        [Test]
        public void TheSunLightsTheDayAndFadesAtTheHorizon()
        {
            var day = new SolarDay(JacksonLat, JacksonLon, 2026, 15);
            var noon = SkyLight.From(day.SunAt(12 * 3600), 8.95);
            Assert.That(noon.IsMoon, Is.False);
            Assert.That(noon.Strength, Is.EqualTo(1));
            var sun = day.SunAt(12 * 3600);
            var (x, y, z) = sun.DirectionInFrame(8.95);
            Assert.That(noon.X, Is.EqualTo(x));
            Assert.That(noon.Y, Is.EqualTo(y));
            Assert.That(noon.Z, Is.EqualTo(z));
            var sunset = SkyLight.From(day.SunAt(day.Sunset()), 8.95);
            Assert.That(sunset.IsMoon, Is.False);
            Assert.That(sunset.Strength, Is.LessThan(0.2), "nearly dark as the sun sets");
        }

        [Test]
        public void EveryNightHasAHighMoonOppositeTheSun()
        {
            var day = new SolarDay(JacksonLat, JacksonLon, 2026, 15);
            for (int hour = 19; hour <= 29; hour++)   // 7 pm to 5 am, across midnight
            {
                var at = hour < 24 ? day : new SolarDay(JacksonLat, JacksonLon, 2026, 16);
                var sun = at.SunAt(hour % 24 * 3600);
                var moon = SkyLight.From(sun, 8.95);
                Assert.That(moon.IsMoon, Is.True, $"{hour % 24}:00");
                Assert.That(moon.Strength, Is.EqualTo(1).Within(1e-9), $"{hour % 24}:00: full moonlight");
                Assert.That(Math.Asin(moon.Y) * 180 / Math.PI, Is.EqualTo(SkyLight.MoonElevation).Within(1e-9));
                double moonAz = Math.Atan2(moon.X, moon.Z) * 180 / Math.PI;
                double diff = ((moonAz - sun.GridAzimuth(8.95)) % 360 + 360) % 360;
                Assert.That(diff, Is.EqualTo(180).Within(1e-6), $"{hour % 24}:00: opposite the sun");
            }
        }

        [Test]
        public void TheLightNeverPopsThroughTwilight()
        {
            // Strength is continuous, and both lights are dark where the switch happens.
            var day = new SolarDay(JacksonLat, JacksonLon, 2026, 15);
            double set = day.Sunset();
            double previous = SkyLight.From(day.SunAt(set - 3600), 0).Strength;
            for (double s = set - 3600; s < set + 7200; s += 30)
            {
                var light = SkyLight.From(day.SunAt(s), 0);
                if (Math.Abs(light.SunElevation - SkyLight.SwitchElevation) < 0.3) Assert.That(light.Strength, Is.LessThan(0.02));
                Assert.That(Math.Abs(light.Strength - previous), Is.LessThan(0.08), $"at {s / 3600:F2} h");
                previous = light.Strength;
            }
        }
    }
}
