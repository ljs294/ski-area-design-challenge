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

        [Test]
        public void TheWorkersTilesReachTheGpuAndAWholeMapUploadKeepsThem()
        {
            // Task 4.07 PR A2: tiles go up by rectangle (a staging strip and CopyTexture), so the map's own CPU copy falls
            // behind; a later whole-map upload (a seed, an old save's snow) must not bring the old depths back.
            var states = new SurfaceStates(new Rect(0, 0, 800, 400));
            try
            {
                const int x = 64, y = 0, w = 36, h = 50;
                var mm = new ushort[w * h];
                for (int k = 0; k < mm.Length; k++) mm[k] = 250;
                var surface = new byte[w * h * 4];
                for (int k = 0; k < surface.Length; k += 4) surface[k] = 3;
                states.TakeTile(x, y, w, h, mm, 0, surface, 0);
                for (int f = 0; f < 4 && states.TilesWaiting > 0; f++) states.Sync();
                Assert.That(states.TilesWaiting, Is.Zero);
                float[] gpu = ReadBack((Texture2D)Shader.GetGlobalTexture(DepthMap));
                Assert.That(gpu[10 * 100 + 70], Is.EqualTo(0.25f).Within(1e-6f), "inside the tile");
                Assert.That(gpu[10 * 100 + 10], Is.EqualTo(SnowDepthField.IterationOneMetres).Within(1e-6f), "outside it");
                Assert.That(states.SurfaceAt(70, 10).Type, Is.EqualTo(3), "the surface bytes too");

                states.Snow.SetRect(2, 2, 2, 2, 0.05f);
                states.Sync();
                gpu = ReadBack((Texture2D)Shader.GetGlobalTexture(DepthMap));
                Assert.That(gpu[3 * 100 + 3], Is.EqualTo(0.05f).Within(1e-6f), "the whole-map write");
                Assert.That(gpu[10 * 100 + 70], Is.EqualTo(0.25f).Within(1e-6f), "the tile is still there after it");
            }
            finally { states.Dispose(); }
        }

        /// <summary>What the GPU holds (not the texture's CPU copy, which tile uploads skip).</summary>
        static float[] ReadBack(Texture2D map)
        {
            var rt = RenderTexture.GetTemporary(map.width, map.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            var read = new Texture2D(map.width, map.height, TextureFormat.RFloat, false, true);
            var was = RenderTexture.active;
            try
            {
                Graphics.Blit(map, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, map.width, map.height), 0, 0);
                read.Apply(false);
                return read.GetPixelData<float>(0).ToArray();
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(read);
            }
        }
    }
}
