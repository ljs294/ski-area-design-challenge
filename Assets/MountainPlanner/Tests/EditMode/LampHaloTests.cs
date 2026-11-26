using MountainPlanner.Presentation.Buildings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.50 slice 3: a lamp's halo draws at its size on screen. Rendering into a texture flips the projection's y, which
    /// once pinned every halo to its minimum size (then 3 px: 4 pixels lit here instead of ~270), invisible in the game.
    /// </summary>
    public sealed class LampHaloTests
    {
        [Test]
        public void AHaloDrawsAtItsSizeOnScreen()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var shader = Resources.Load<Shader>(LampHaloLayer.ShaderResource);
            Assert.That(shader, Is.Not.Null, LampHaloLayer.ShaderResource);
            var rt = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGBHalf);
            var go = new GameObject("Halo test camera");
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.fieldOfView = 60;
                var layer = new LampHaloLayer(shader);
                // 10 m away, 1.2 m across: 27 px at this height, clamped to 26 px (~270 pixels over 0.05)
                int Lit(float level)
                {
                    layer.Begin();
                    layer.Add(new Vector3(0, 0, 10), level);
                    layer.Draw(cam);
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(256, 256, TextureFormat.RGBAHalf, false);
                    tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                    RenderTexture.active = prev;
                    int n = 0;
                    foreach (var c in tex.GetPixels()) if (c.r > 0.05f) n++;
                    Object.DestroyImmediate(tex);
                    return n;
                }
                Assert.That(Lit(1), Is.InRange(150, 500), "a disc about 26 px across (its core and glow over 0.05)");
                Assert.That(Lit(0), Is.EqualTo(0), "an unlit lamp: no halo");
                layer.Dispose();
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
            }
        }
    }
}
