using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Guests;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// SN-31, the Traffic info layer (task 4.13 PR C): its steps, its legend's figures, and that it takes its turn with the
    /// other info layers over the dark relief.
    /// </summary>
    public sealed class TrafficLayerTests
    {
        [TearDown]
        public void Reset() => new MapLayers().Apply();   // the shader globals back to their defaults

        [Test]
        public void AStepCountsTheBreaksAValueReaches()
        {
            int[] breaks = { 15, 40, 80, 150 };
            Assert.That(InfoLegend.Step(0, breaks), Is.EqualTo(0));
            Assert.That(InfoLegend.Step(14.9f, breaks), Is.EqualTo(0));
            Assert.That(InfoLegend.Step(15, breaks), Is.EqualTo(1), "a break starts its step");
            Assert.That(InfoLegend.Step(79, breaks), Is.EqualTo(2));
            Assert.That(InfoLegend.Step(10_000, breaks), Is.EqualTo(4), "Packed has no top");
        }

        [Test]
        public void ARunsCrowdingIsSkiersAnHourPerHundredFeetOfWidth()
        {
            const float hundredFeet = 30.48f;
            Assert.That(TrafficView.RunStep(14, hundredFeet), Is.EqualTo(0), "Quiet");
            Assert.That(TrafficView.RunStep(100, hundredFeet), Is.EqualTo(3), "Crowded");
            Assert.That(TrafficView.RunStep(50, hundredFeet / 2), Is.EqualTo(3), "the same skiers on half the width feel twice as crowded");
            Assert.That(TrafficView.RunStep(50, hundredFeet * 2), Is.EqualTo(1));
            Assert.That(TrafficView.RunStep(0, 0), Is.EqualTo(0), "a piece with no width is quiet, not a division by zero");
        }

        [Test]
        public void ALiftsLoadIsHowFullItsChairsRan()
        {
            Assert.That(TrafficView.LoadStep(100, 2400), Is.EqualTo(0), "4%");
            Assert.That(TrafficView.LoadStep(1200, 2400), Is.EqualTo(2), "50% starts Busy");
            Assert.That(TrafficView.LoadStep(2300, 2400), Is.EqualTo(4), "96% is Packed");
            Assert.That(TrafficView.LoadStep(50, 0), Is.EqualTo(0), "a lift that can carry no one isn't full");
        }

        [Test]
        public void ALinesCircleGrowsWithItsLineToAMost()
        {
            Assert.That(TrafficView.DiscPixels(0), Is.EqualTo(TrafficView.DiscMinPixels));
            float last = 0;
            foreach (int line in new[] { 0, 1, 10, 50, 100, 300 })
            {
                float px = TrafficView.DiscPixels(line);
                Assert.That(px, Is.GreaterThanOrEqualTo(last), "never shrinks as the line grows");
                last = px;
            }
            Assert.That(TrafficView.DiscPixels(100_000), Is.EqualTo(TrafficView.DiscMaxPixels));
        }

        [Test]
        public void TheLegendGivesEachStepItsRealFigures()
        {
            var runs = InfoLegend.TrafficRuns;
            Assert.That(runs.Length, Is.EqualTo(5));
            Assert.That(runs[0].Figure, Is.EqualTo("under " + InfoLegend.RunBreaks[0]));
            Assert.That(runs[1].Figure, Is.EqualTo(InfoLegend.RunBreaks[0] + "–" + InfoLegend.RunBreaks[1]));
            Assert.That(runs[4].Figure, Is.EqualTo(InfoLegend.RunBreaks[3] + "+"));
            Assert.That(InfoLegend.TrafficLifts[4].Figure, Is.EqualTo(InfoLegend.LoadBreaksPercent[3] + "%+"));
            for (int k = 0; k < 5; k++)
            {
                Assert.That(runs[k].Colour, Is.EqualTo(InfoLegend.TrafficColours[k]), "a run and a lift share each step's colour");
                Assert.That(InfoLegend.TrafficLifts[k].Label, Is.EqualTo(InfoLegend.TrafficWords[k]));
            }
        }

        [Test]
        public void TrafficTakesItsTurnWithTheOtherInfoLayersOverTheRelief()
        {
            var layers = new MapLayers();
            Assert.That(MapLayers.IsSwitchable(MapLayers.Traffic) && MapLayers.IsExclusive(MapLayers.Traffic), Is.True);
            Assert.That(layers.Set(MapLayers.Traffic, true), Is.True);
            Assert.That(layers.InfoLayerId, Is.EqualTo(MapLayers.Traffic));
            Assert.That(layers.ReliefOn, Is.True, "the ground goes to the dark relief");
            Assert.That(Shader.GetGlobalFloat("_MP_InfoView"), Is.GreaterThan(4.5f), "the shaders paint the relief past 4.5");
            layers.Set(MapLayers.SlopeAngle, true);
            Assert.That(layers.IsOn(MapLayers.Traffic), Is.False, "Slope angle took its turn");
            Assert.That(layers.ReliefOn, Is.False);
            layers.Set(MapLayers.Traffic, true);
            layers.Set(MapLayers.Contours, true);
            Assert.That(layers.IsOn(MapLayers.Traffic) && layers.ContoursOn, Is.True, "contours go with it, as with any");
            layers.Set(MapLayers.Traffic, false);
            Assert.That(layers.Info, Is.EqualTo(MapLayers.InfoView.None));
        }

        [Test]
        public void TrafficIsShiftFive()
        {
            var info = KeyBindings.Actions[(int)GameAction.InfoTraffic];
            Assert.That((info.Group, info.Label), Is.EqualTo(("Layers", "Traffic")));
            Assert.That(info.First, Is.EqualTo(new KeyChord(UnityEngine.InputSystem.Key.Digit5, KeyMods.Shift)));
        }
    }
}
