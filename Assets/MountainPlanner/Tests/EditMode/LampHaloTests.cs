using MountainPlanner.Presentation.Buildings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.50 slice 3: a lamp's glare draws at its size on screen. Rendering into a texture flips the projection's y, which
    /// once pinned every one to its minimum size. Round 2 (owner 2026-11-26): a glare is about its lens's size and shows
    /// only where the lens does: from in front of a shaded lamp's aim, and all round a cage sconce.
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
                // 3 m away, 0.35 m across: 26 px at this height, clamped to 14 px (~70 pixels over 0.05)
                int Lit(float level, Vector3 aim, bool allRound = false, float z = 3)
                {
                    layer.Begin();
                    layer.Add(new Vector3(0, 0, z), level, aim, allRound, Color.white);
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
                int facing = Lit(1, Vector3.back);
                Assert.That(facing, Is.InRange(30, 160), "a disc 14 px across (its core and glow over 0.05)");
                Assert.That(Lit(0, Vector3.back), Is.EqualTo(0), "an unlit lamp: no glare");
                Assert.That(Lit(1, Vector3.forward), Is.EqualTo(0), "a shaded lamp seen from behind its aim: none");
                Assert.That(Lit(1, Vector3.down), Is.LessThan(facing), "seen side on: fading");
                Assert.That(Lit(1, Vector3.forward, true), Is.EqualTo(facing), "a cage sconce shines all round");
                Assert.That(Lit(1, Vector3.back, false, 200), Is.InRange(2, 16), "from afar: a point 4 px across");
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
