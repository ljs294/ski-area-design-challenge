using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Water;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 10: the snow-depth seam and the lake surface-state API (0.3 §4.6, T8). Engine-free.
    public sealed class SnowAndWaterTests
    {
        [Test]
        public void IterationOneIsTwelveInchesEverywhere()
        {
            var field = new SnowDepthField(40, 30, 8);
            Assert.That(SnowDepthField.IterationOneMetres, Is.EqualTo(12 * 0.0254f).Within(1e-6));
            foreach (float d in field.Cells) Assert.That(d, Is.EqualTo(SnowDepthField.IterationOneMetres));
            Assert.That(field.TakeDirty(), Is.EqualTo((0, 0, 40, 30)), "the first upload is the whole field");
            Assert.That(field.TakeDirty().Width, Is.EqualTo(0), "then nothing until a writer changes it");
        }

        [Test]
        public void WritesMarkOnlyTheirRectangleDirty()
        {
            var field = new SnowDepthField(100, 100, 8);
            field.TakeDirty();
            field.SetRect(10, 20, 5, 5, 0.5f);
            field.SetRect(30, 22, 2, 10, 0.1f);
            Assert.That(field[12, 22], Is.EqualTo(0.5f));
            Assert.That(field[31, 31], Is.EqualTo(0.1f));
            Assert.That(field[50, 50], Is.EqualTo(SnowDepthField.IterationOneMetres));
            Assert.That(field.TakeDirty(), Is.EqualTo((10, 20, 22, 12)), "the union of both writes");
            field.SetRect(95, -5, 20, 10, 0);   // clipped to the field
            Assert.That(field.TakeDirty(), Is.EqualTo((95, 0, 5, 5)));
            Assert.Throws<ArgumentOutOfRangeException>(() => field.SetRect(0, 0, 1, 1, -0.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => field.SetRect(0, 0, 1, 1, float.NaN));
        }

        [Test]
        public void IterationOneLakesAreFrozenUnderTheSnow()
        {
            var water = new WaterBodies();
            Assert.That(water.Count, Is.EqualTo(1));
            var all = water[WaterBodies.AllWater];
            Assert.That(all.State, Is.EqualTo(WaterSurfaceState.SnowCoveredIce));
            Assert.That(all.SnowMetres, Is.EqualTo(SnowDepthField.IterationOneMetres));
            Assert.That(all.IceMetres, Is.GreaterThan(0));
        }

        [Test]
        public void ChangingAStateBumpsTheVersionOnce()
        {
            var water = new WaterBodies(3);
            int v = water.Version;
            Assert.That(water.Set(1, new WaterSurface(WaterSurfaceState.OpenWater, 0, 0)), Is.True);
            Assert.That(water.Version, Is.EqualTo(v + 1));
            Assert.That(water.Set(1, new WaterSurface(WaterSurfaceState.OpenWater, 0, 0)), Is.False, "no change, no new version");
            Assert.That(water.Version, Is.EqualTo(v + 1));
            Assert.That(water[0].State, Is.EqualTo(WaterSurfaceState.SnowCoveredIce), "other bodies keep theirs");
            Assert.Throws<ArgumentOutOfRangeException>(() => water.Set(3, WaterSurface.IterationOne));
        }

        [Test]
        public void ASurfaceCarriesOnlyWhatItsStateAllows()
        {
            var open = new WaterSurface(WaterSurfaceState.OpenWater, 0.5f, 0.3f);
            Assert.That(open.IceMetres, Is.Zero);
            Assert.That(open.SnowMetres, Is.Zero);
            var ice = new WaterSurface(WaterSurfaceState.Ice, 0.5f, 0.3f);
            Assert.That(ice.IceMetres, Is.EqualTo(0.5f));
            Assert.That(ice.SnowMetres, Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaterSurface((WaterSurfaceState)7, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaterSurface(WaterSurfaceState.Ice, -1, 0));
        }
    }
}
