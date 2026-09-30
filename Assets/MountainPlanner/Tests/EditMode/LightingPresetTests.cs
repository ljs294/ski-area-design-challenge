using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // The lighting presets (0.5 §4, style tile): four time-of-day looks in cycle order, a winter sun from
    // the south, and blends that start and end exactly on a preset without the sun passing underground.
    public sealed class LightingPresetTests
    {
        [Test]
        public void FourPresetsInCycleOrder()
        {
            Assert.That(LightingPreset.All.Length, Is.EqualTo(4));
            Assert.That(LightingPreset.IndexOf("dawn"), Is.EqualTo(0));
            Assert.That(LightingPreset.IndexOf("noon"), Is.EqualTo(1));
            Assert.That(LightingPreset.IndexOf("golden"), Is.EqualTo(2));
            Assert.That(LightingPreset.IndexOf("Golden hour"), Is.EqualTo(2));
            Assert.That(LightingPreset.IndexOf("night"), Is.EqualTo(3));
            Assert.That(LightingPreset.IndexOf("tea time"), Is.EqualTo(-1));
            Assert.That(LightingPreset.All[2].Bloom, Is.GreaterThan(0), "bloom at golden hour");
            for (int i = 0; i < 4; i++)
                if (i != 2) Assert.That(LightingPreset.All[i].Bloom, Is.EqualTo(0), "and only then");
        }

        [Test]
        public void TheNoonSunIsInTheSouth()
        {
            var toSun = LightingPreset.All[LightingPreset.IndexOf("noon")].ToSun;
            Assert.That(toSun.magnitude, Is.EqualTo(1).Within(1e-5f));
            Assert.That(toSun.z, Is.LessThan(-0.8f), "grid north is +z; a mid-January sun at 43.6° N stands in the south");
            Assert.That(toSun.y, Is.GreaterThan(0.3f).And.LessThan(0.5f), "about 25 degrees up");
            foreach (var p in LightingPreset.All) Assert.That(p.ToSun.y, Is.GreaterThan(0), $"{p.Name}: the light comes from above");
        }

        [Test]
        public void BlendsStartAndEndOnThePresetsAndKeepTheSunUp()
        {
            var all = LightingPreset.All;
            for (int i = 0; i < all.Length; i++)
            {
                var a = all[i];
                var b = all[(i + 1) % all.Length];
                var start = LightingPreset.Lerp(a, b, 0);
                var end = LightingPreset.Lerp(a, b, 1);
                Assert.That(Vector3.Distance(start.ToSun, a.ToSun), Is.LessThan(1e-4f));
                Assert.That(Vector3.Distance(end.ToSun, b.ToSun), Is.LessThan(1e-4f));
                Assert.That(end.Exposure, Is.EqualTo(b.Exposure).Within(1e-5f));
                Assert.That(end.Zenith, Is.EqualTo(b.Zenith));
                for (float t = 0; t <= 1; t += 0.05f)
                    Assert.That(LightingPreset.Lerp(a, b, t).ToSun.y, Is.GreaterThan(0), $"{a.Name} to {b.Name} at {t:F2}");
            }
        }
    }
}
