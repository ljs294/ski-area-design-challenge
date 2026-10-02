using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // The lighting looks (0.5 §4, style tile; task 11): four presets, blended by the sun's elevation so the
    // look runs night, dawn (or golden hour in the evening), noon without a jump.
    public sealed class LightingPresetTests
    {
        [Test]
        public void FourPresetsInOrder()
        {
            Assert.That(LightingPreset.All.Length, Is.EqualTo(4));
            Assert.That(LightingPreset.IndexOf("dawn"), Is.EqualTo(LightingPreset.Dawn));
            Assert.That(LightingPreset.IndexOf("noon"), Is.EqualTo(LightingPreset.Noon));
            Assert.That(LightingPreset.IndexOf("golden"), Is.EqualTo(LightingPreset.GoldenHour));
            Assert.That(LightingPreset.IndexOf("Golden hour"), Is.EqualTo(LightingPreset.GoldenHour));
            Assert.That(LightingPreset.IndexOf("night"), Is.EqualTo(LightingPreset.Night));
            Assert.That(LightingPreset.IndexOf("tea time"), Is.EqualTo(-1));
            Assert.That(LightingPreset.All[2].Bloom, Is.GreaterThan(0), "bloom at golden hour");
            for (int i = 0; i < 4; i++)
                if (i != 2) Assert.That(LightingPreset.All[i].Bloom, Is.EqualTo(0), "and only then");
        }

        [TestCase(40f, true, LightingPreset.Noon)]
        [TestCase(22f, false, LightingPreset.Noon)]
        [TestCase(3f, true, LightingPreset.Dawn)]
        [TestCase(6f, false, LightingPreset.GoldenHour)]
        [TestCase(-8f, true, LightingPreset.Night)]
        [TestCase(-30f, false, LightingPreset.Night)]
        public void EachLookIsExactAtItsElevation(float elevation, bool morning, int preset)
        {
            var look = LightingPreset.Look(elevation, morning, out int nearest);
            var p = LightingPreset.All[preset];
            Assert.That(nearest, Is.EqualTo(preset));
            Assert.That(look.Zenith, Is.EqualTo(p.Zenith));
            Assert.That(look.SunColor, Is.EqualTo(p.SunColor));
            Assert.That(look.Exposure, Is.EqualTo(p.Exposure).Within(1e-5f));
            Assert.That(look.Bloom, Is.EqualTo(p.Bloom).Within(1e-5f));
        }

        [Test]
        public void TheLookChangesSmoothlyWithTheSun()
        {
            foreach (bool morning in new[] { true, false })
            {
                var previous = LightingPreset.Look(-20, morning, out _);
                for (float e = -20; e <= 60; e += 0.25f)
                {
                    var look = LightingPreset.Look(e, morning, out _);
                    Assert.That(Mathf.Abs(look.Exposure - previous.Exposure), Is.LessThan(0.05f), $"exposure at {e}°");
                    Assert.That(Mathf.Abs(look.SunIntensity - previous.SunIntensity), Is.LessThan(0.05f), $"intensity at {e}°");
                    Assert.That(Vector4.Distance(look.Zenith, previous.Zenith), Is.LessThan(0.03f), $"zenith at {e}°");
                    previous = look;
                }
            }
        }

        [Test]
        public void MorningsAreDawnAndEveningsAreGoldenHour()
        {
            LightingPreset.Look(5, true, out int morning);
            LightingPreset.Look(5, false, out int evening);
            Assert.That(morning, Is.EqualTo(LightingPreset.Dawn));
            Assert.That(evening, Is.EqualTo(LightingPreset.GoldenHour));
        }
    }
}
