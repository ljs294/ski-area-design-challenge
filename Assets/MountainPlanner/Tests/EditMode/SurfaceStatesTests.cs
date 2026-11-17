using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Water;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Task 10 acceptance: the lake state switches rendering through the API, and the snow-depth field
    // reaches the terrain shader. Task 4.09 PR B: each body through the body map. (The visual check: -lake, -lakemix.)
    public sealed class SurfaceStatesTests
    {
        static readonly int BodyParams = Shader.PropertyToID("_WaterBodyParams"), BodyMap = Shader.PropertyToID("_WaterBodyMap"),
                            BodyStates = Shader.PropertyToID("_WaterBodyStates"), DepthParams = Shader.PropertyToID("_SnowDepthParams"),
                            DepthMap = Shader.PropertyToID("_SnowDepthMap");

        static Color StateTexel(int body) => ((Texture2D)Shader.GetGlobalTexture(BodyStates)).GetPixel(body, 0);

        [Test]
        public void TheLakeStateSwitchesWhatTheShaderDraws()
        {
            var states = new SurfaceStates(new Rect(-500, -400, 1000, 800));
            try
            {
                Assert.That(Shader.GetGlobalVector(BodyParams).x, Is.Zero, "no body map yet: every water pixel reads body 0");
                Assert.That(StateTexel(0).r, Is.EqualTo(3), "iteration 1: snow-covered ice (state 2, sent as 3)");
                int uploads = states.StateUploads;
                states.Water.Set(WaterBodies.AllWater, new WaterSurface(WaterSurfaceState.OpenWater, 0, 0));
                Assert.That(StateTexel(0).r, Is.EqualTo(3), "nothing reaches the GPU until Sync");
                states.Sync();
                states.Sync();
                Assert.That(StateTexel(0).r, Is.EqualTo(1), "open water");
                Assert.That(states.StateUploads, Is.EqualTo(uploads + 1), "one upload per change, none when nothing moved");
                states.Water.Set(WaterBodies.AllWater, new WaterSurface(WaterSurfaceState.Ice, 0.3f, 0));
                states.Sync();
                Assert.That(StateTexel(0).r, Is.EqualTo(2), "bare ice");
                Assert.That(StateTexel(0).g, Is.EqualTo(0.3f).Within(1e-3f), "with its thickness");
            }
            finally { states.Dispose(); }
            Assert.That(Shader.GetGlobalVector(BodyParams), Is.EqualTo(Vector4.zero), "closing the mountain clears the state");
        }

        /// <summary>A 10 × 5 table at the ring's corner: the streams in cell 0, a lake (index 1) in cells 12–13.</summary>
        static WaterBodyTable Table(Rect ring, int width = 10, int depth = 5)
        {
            var mask = new byte[width * depth];
            mask[0] = 1;
            mask[12] = mask[13] = 2;
            var bodies = new[]
            {
                new WaterBody(WaterBodyKind.Streams, 0, 1, -1, 0, 0, 1000, 0, 10, false),
                new WaterBody(WaterBodyKind.Lake, 0xA, 2, -1, 0, 0, 1000, 6000, 50, false),
            };
            return WaterBodyTable.Of(width, depth, (int)System.Math.Round(ring.xMin * 1000), (int)System.Math.Round(ring.yMin * 1000), mask, bodies, 4);
        }

        [Test]
        public void EachBodyGetsItsOwnStateThroughTheMap()
        {
            var ring = new Rect(-40.0004f, -20, 80, 40);   // 10 × 5 cells; the corner a fraction of a millimetre off whole mm
            var states = new SurfaceStates(ring);
            try
            {
                var t = Table(ring);
                Assert.That(states.SetBodies(t, LakeSurfaces.Dilated(t)), Is.True);
                var map = (Texture2D)Shader.GetGlobalTexture(BodyMap);
                Assert.That(map.format, Is.EqualTo(TextureFormat.R8));
                Assert.That(map.filterMode, Is.EqualTo(FilterMode.Point));
                Assert.That(map.isDataSRGB, Is.False, "linear: the shader reads the id back exactly");
                Assert.That(Mathf.RoundToInt(map.GetPixelData<byte>(0)[12]), Is.EqualTo(2), "the lake's cell: index 1 + 1");
                Assert.That(map.GetPixelData<byte>(0)[11], Is.EqualTo(2), "dilated: the shore cell takes the lake");
                Assert.That(Shader.GetGlobalVector(BodyParams), Is.EqualTo(new Vector4(1, 2, 10, 5)));
                Assert.That(states.Water.Count, Is.EqualTo(2));
                Assert.That(states.BoundTable, Is.EqualTo(t.Hash));

                states.Water.Set(1, new WaterSurface(WaterSurfaceState.OpenWater, 0, 0));
                states.Sync();
                Assert.That(StateTexel(0).r, Is.EqualTo(3), "the streams keep their snow");
                Assert.That(StateTexel(1).r, Is.EqualTo(1), "the lake is open");

                // 4.14: a pond added later rebinds with one more body.
                var withPond = t.WithPond(0xB, new[] { 40 }, 40, 1000);
                Assert.That(states.SetBodies(withPond, LakeSurfaces.Dilated(withPond)), Is.True);
                Assert.That(states.Water.Count, Is.EqualTo(3));
                Assert.That(Shader.GetGlobalVector(BodyParams).y, Is.EqualTo(3));
                Assert.That(StateTexel(1).r, Is.EqualTo(1), "the lake keeps its state over the rebind");
            }
            finally { states.Dispose(); }
            Assert.That(Shader.GetGlobalTexture(BodyMap), Is.Null);
        }

        [Test]
        public void ATableOnAnotherGridIsRefusedOutLoud()
        {
            var ring = new Rect(0, 0, 80, 40);
            var states = new SurfaceStates(ring);
            try
            {
                var t = Table(new Rect(8, 0, 80, 40));   // shifted one cell
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("isn't the snow map's"));
                Assert.That(states.SetBodies(t, LakeSurfaces.Dilated(t)), Is.False);
                Assert.That(Shader.GetGlobalVector(BodyParams).x, Is.Zero, "the lakes keep one state");
            }
            finally { states.Dispose(); }
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
