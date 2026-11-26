using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.50 slice 3 (owner 2026-11-25): when a building's lights glow.
    public sealed class BuildingGlowTests
    {
        [Test]
        public void ADuskLightBurnsAllNightAndTheOthersOnlyInUse()
        {
            Assert.That(BuildingGlow.On(BuildingGlow.Dusk, false), Is.True, "a photocell lamp: on whether or not the building works");
            Assert.That(BuildingGlow.On(BuildingGlow.LiftOpen, true), Is.True);
            Assert.That(BuildingGlow.On(BuildingGlow.LiftOpen, false), Is.False, "a closed lift's hut: its downlights off");
            Assert.That(BuildingGlow.On(BuildingGlow.Running, false), Is.False);
            Assert.That(BuildingGlow.On(BuildingGlow.NightShift, true), Is.True);
            Assert.That(BuildingGlow.On(null, true), Is.False, "no schedule, no light");
        }

        [Test]
        public void ADuskLampWaitsForNightAndAHutInUseShowsAsTheLightFails()
        {
            // Lifts close before dark in winter: a hut in use glows from the sun 10° up, the photocell lamps only at night.
            Assert.That(BuildingGlow.Level(BuildingGlow.Dusk, true, 0, 5), Is.EqualTo(0), "the sun 5° up: no photocell lamp");
            Assert.That(BuildingGlow.Level(BuildingGlow.Dusk, false, 1, -10), Is.EqualTo(1));
            Assert.That(BuildingGlow.Level(BuildingGlow.LiftOpen, true, 0, 5), Is.GreaterThan(0.2).And.LessThan(0.6), "a running hut at closing, the sun 5° up");
            Assert.That(BuildingGlow.Level(BuildingGlow.LiftOpen, true, 0, 30), Is.EqualTo(0), "midday: no glow");
            Assert.That(BuildingGlow.Level(BuildingGlow.LiftOpen, false, 1, -10), Is.EqualTo(0), "closed at night: dark");
            Assert.That(BuildingGlow.Dim(BuildingGlow.DimFromDegrees), Is.EqualTo(0));
            Assert.That(BuildingGlow.Dim(BuildingGlow.DimFullDegrees), Is.EqualTo(1));
            Assert.That(BuildingGlow.Dim(2), Is.GreaterThan(BuildingGlow.Dim(6)), "darker as the sun goes down");
        }
    }
}
