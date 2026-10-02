using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Water;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Task 10 acceptance: the lake state switches rendering through the API, and the snow-depth field
    // reaches the terrain shader. (The visual check is in the PR: -lake open|ice|snow.)
    public sealed class SurfaceStatesTests
    {
        static readonly int Lake = Shader.PropertyToID("_LakeState"), DepthParams = Shader.PropertyToID("_SnowDepthParams"),
                            DepthMap = Shader.PropertyToID("_SnowDepthMap");

        [Test]
        public void TheLakeStateSwitchesWhatTheShaderDraws()
        {
            var states = new SurfaceStates(new Rect(-500, -400, 1000, 800));
            try
            {
                Assert.That(Shader.GetGlobalVector(Lake).x, Is.EqualTo(3), "iteration 1: snow-covered ice (state 2, sent as 3)");
                states.Water.Set(WaterBodies.AllWater, new WaterSurface(WaterSurfaceState.OpenWater, 0, 0));
                Assert.That(Shader.GetGlobalVector(Lake).x, Is.EqualTo(3), "nothing reaches the GPU until Sync");
                states.Sync();
                Assert.That(Shader.GetGlobalVector(Lake).x, Is.EqualTo(1), "open water");
                states.Water.Set(WaterBodies.AllWater, new WaterSurface(WaterSurfaceState.Ice, 0.3f, 0));
                states.Sync();
                var v = Shader.GetGlobalVector(Lake);
                Assert.That(v.x, Is.EqualTo(2), "bare ice");
                Assert.That(v.y, Is.EqualTo(0.3f).Within(1e-6f), "with its thickness");
            }
            finally { states.Dispose(); }
            Assert.That(Shader.GetGlobalVector(Lake).x, Is.EqualTo(0), "closing the mountain clears the state");
        }

        [Test]
        public void TheSnowDepthReachesTheTexture()
        {
            var states = new SurfaceStates(new Rect(0, 0, 800, 400));
            try
            {
                var map = (Texture2D)Shader.GetGlobalTexture(DepthMap);
                Assert.That(map.width, Is.EqualTo(100));
                Assert.That(map.height, Is.EqualTo(50));
                Assert.That(map.GetPixel(10, 10).r, Is.EqualTo(SnowDepthField.IterationOneMetres).Within(1e-6f));
                Assert.That(Shader.GetGlobalVector(DepthParams).w, Is.EqualTo(1));
                Assert.That(SnowDepthField.IterationOneMetres / SurfaceStates.FullCoverMetres, Is.GreaterThanOrEqualTo(1),
                            "12 in covers the ground completely, so the picture is unchanged");

                states.Snow.SetRect(20, 5, 4, 3, 0.05f);
                states.Sync();
                Assert.That(map.GetPixel(21, 6).r, Is.EqualTo(0.05f).Within(1e-6f), "inside the write");
                Assert.That(map.GetPixel(30, 6).r, Is.EqualTo(SnowDepthField.IterationOneMetres).Within(1e-6f), "outside it");
            }
            finally { states.Dispose(); }
        }
    }
}
