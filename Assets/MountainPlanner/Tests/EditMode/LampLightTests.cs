using MountainPlanner.Presentation.Buildings;
using MountainPlanner.World.Buildings;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.50 slice 3 part 2: a lit lamp lights the ground in front of it, not behind its wall and not past its reach,
    /// and a hut's downlights only while its lift runs. Round 2 (owner 2026-11-26): a lamp is shielded (nothing above its
    /// own height), and the wall it is on takes a dimmer wash than the ground under it. The mini hut (glass +x under its canopy downlight, door and sconce
    /// −z) stands at the origin over a 40 m plate drawn with nothing but the lamps' light (LampLightProbe), seen from above.
    /// </summary>
    public sealed class LampLightTests
    {
        const int Side = 256;
        const float Metres = 32f;   // the view across
        const int PlateLayer = 31;

        [Test]
        public void ALampLightsItsSideOfTheWallWithinItsReach()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device (-nographics).");
            var probe = Shader.Find("Hidden/MountainPlanner/Tests/LampLightProbe");
            Assert.That(probe, Is.Not.Null);
            var set = Resources.Load<LiftModelSet>(DevBuildingPlacement.SetName);
            var rt = new RenderTexture(Side, Side, 24, RenderTextureFormat.ARGBHalf);
            var hut = Object.Instantiate(set.Find("bldg_hut_mini"));
            var plate = GameObject.CreatePrimitive(PrimitiveType.Plane);
            var camGo = new GameObject("Lamp light test camera");
            var material = new Material(probe);
            var map = new LampLightMap();
            try
            {
                hut.transform.position = Vector3.zero;
                var state = hut.AddComponent<BuildingGlowState>();
                state.Init(hut.GetComponent<BuildingLights>(), false);
                var buildings = new[] { state };

                plate.layer = PlateLayer;
                plate.transform.localScale = new Vector3(4, 1, 4);
                plate.GetComponent<Renderer>().sharedMaterial = material;

                var cam = camGo.AddComponent<Camera>();
                cam.targetTexture = rt;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.orthographic = true;
                cam.orthographicSize = Metres / 2;
                cam.cullingMask = 1 << PlateLayer;
                cam.transform.SetPositionAndRotation(new Vector3(0, 50, 0), Quaternion.Euler(90, 0, 0));   // x right, z up the picture

                Texture2D Render()
                {
                    cam.Render();
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var tex = new Texture2D(Side, Side, TextureFormat.RGBAHalf, false);
                    tex.ReadPixels(new Rect(0, 0, Side, Side), 0, 0);
                    RenderTexture.active = prev;
                    return tex;
                }
                float At(Texture2D t, float x, float z) =>
                    t.GetPixel((int)((x + Metres / 2) / Metres * Side), (int)((z + Metres / 2) / Metres * Side)).r;

                Assert.That(map.Tick(1, 1, buildings), Is.True, "built");
                Assert.That(map.LampCount, Is.EqualTo(1), "a closed lift's hut: only its door sconce");
                var closed = Render();
                float front = At(closed, -0.73f, -3f), behind = At(closed, -0.73f, 0.5f), far = At(closed, -0.73f, -12f);
                Assert.That(front, Is.GreaterThan(0.01f), "the snow before the door is lit");
                Assert.That(behind, Is.LessThan(front * 0.01f), "nothing behind the door wall");
                Assert.That(far, Is.EqualTo(0).Within(1e-4), "nothing past its reach");
                Assert.That(At(closed, 2.6f, 0), Is.LessThan(front * 0.01f), "the downlight is off");
                Object.DestroyImmediate(closed);

                state.InUse = true;
                Assert.That(map.Tick(1, 1, buildings), Is.True, "rebuilt when the lift opens");
                Assert.That(map.LampCount, Is.EqualTo(2));
                var open = Render();
                Assert.That(At(open, 2.6f, 0), Is.GreaterThan(0.01f), "the canopy downlight lights the ground before the glass");
                Assert.That(At(open, -0.73f, -3f), Is.EqualTo(front).Within(front * 0.05f + 1e-4f), "the sconce as before");
                Object.DestroyImmediate(open);

                // Shielded: a plate above every lamp (the downlight's lens is 2.5 m up) takes nothing.
                plate.transform.position = new Vector3(0, 3, 0);
                var above = Render();
                Assert.That(At(above, 2.6f, 0) + At(above, -0.73f, -3f), Is.EqualTo(0).Within(1e-4), "nothing above the lamps");
                Object.DestroyImmediate(above);

                // The door wall itself (z = -1.21, facing -z), seen square on: lit under the sconce, but dimmer than the snow before it.
                plate.transform.SetPositionAndRotation(new Vector3(0, 0, -1.214f), Quaternion.Euler(-90, 0, 0));
                cam.transform.SetPositionAndRotation(new Vector3(0, 0, -50), Quaternion.identity);   // x right, y up the picture
                var wall = Render();
                float onWall = At(wall, -0.73f, 1.15f);
                Assert.That(onWall, Is.GreaterThan(0.0005f), "the wall under the sconce takes a wash");
                Assert.That(onWall, Is.LessThan(front), "dimmer than the snow before the door");
                Assert.That(At(wall, -0.73f, 2.6f), Is.EqualTo(0).Within(1e-4), "and nothing up the wall above the sconce");
                Object.DestroyImmediate(wall);
                plate.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                cam.transform.SetPositionAndRotation(new Vector3(0, 50, 0), Quaternion.Euler(90, 0, 0));

                Assert.That(map.Tick(0, 1, buildings), Is.False, "no change, no rebuild");
                var day = Render();
                Assert.That(At(day, 2.6f, 0) + At(day, -0.73f, -3f), Is.EqualTo(0).Within(1e-4), "by day: nothing");
                Object.DestroyImmediate(day);
            }
            finally
            {
                map.Dispose();
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(plate);
                Object.DestroyImmediate(hut);
                Object.DestroyImmediate(rt);
            }
        }
    }
}
