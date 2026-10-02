using System.Collections;
using System.Diagnostics;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace MountainPlanner.Tests
{
    // Task 11 acceptance (docs/plans/phase0-0.7-phase1-plan.md): the scene's sun within 0.1° of the published
    // position; the camera never below the snow or beyond the ring; input response within 100 ms.
    public sealed class LightAndCameraTests
    {
        /// <summary>Rolling hills with a 1,200 m peak, cliff-steep in places, over a 6 km ring.</summary>
        sealed class Hills : ITerrainSurface
        {
            public static readonly Rect RingRect = new Rect(-3000, -3000, 6000, 6000);
            public Rect Footprint => RingRect;
            public float HeightAt(float x, float z) =>
                RingRect.Contains(new Vector2(x, z))
                    ? 2000 + 1200 * Mathf.Exp(-(x * x + z * z) / 2e6f) + 40 * Mathf.Sin(x * 0.02f) * Mathf.Cos(z * 0.017f)
                    : float.NaN;
        }

        readonly System.Collections.Generic.List<Object> _made = new System.Collections.Generic.List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) Object.Destroy(o);
            _made.Clear();
        }

        T Make<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _made.Add(go);
            return go.AddComponent<T>();
        }

        [UnityTest]
        public IEnumerator TheSceneSunIsTheNoaaSunOnTheResortGrid()
        {
            var light = Make<Light>("Sun");
            light.type = LightType.Directional;
            var lighting = light.gameObject.AddComponent<SceneLighting>();
            lighting.Sun = light;
            yield return null;
            // Jackson Hole, 15 January 2026, 12:00 MST (19:00 UTC): the NREL reference case.
            lighting.SetSite(43.593, -110.848, 8.9519);
            lighting.SetTime(new ViewTime(2026, 15, 12 * 3600));
            yield return null;
            var sun = SolarPosition.At(43.593, -110.848, 2026, 15, 68400);
            Assert.That(sun.Elevation, Is.EqualTo(24.9883).Within(0.1), "NREL apparent elevation");
            var (x, y, z) = sun.DirectionInFrame(8.9519);
            var expected = new Vector3((float)x, (float)y, (float)z);
            Assert.That(Vector3.Angle(-light.transform.forward, expected), Is.LessThan(0.1f), "the light shines from the sun");
            float azimuth = Mathf.Atan2(-light.transform.forward.x, -light.transform.forward.z) * Mathf.Rad2Deg;
            Assert.That(Mathf.DeltaAngle(azimuth, (float)(171.5332 + 8.9519)), Is.LessThan(0.1f).And.GreaterThan(-0.1f),
                        "the published azimuth, turned onto the grid");

            // Presets are times: noon is the day's highest sun, night has the moon.
            lighting.Set(LightingPreset.Noon, instant: true);
            yield return null;
            var day = new SolarDay(43.593, -110.848, 2026, 15);
            Assert.That(lighting.Clock.Now.SecondOfDay, Is.EqualTo(day.SolarNoon()).Within(1.0));
            Assert.That(lighting.CurrentName, Is.EqualTo("Noon"));
            lighting.Set(LightingPreset.Night, instant: true);
            yield return null;
            Assert.That(lighting.CurrentLight.IsMoon, Is.True);
            Assert.That(lighting.CurrentName, Is.EqualTo("Night"));
            Assert.That(light.intensity, Is.GreaterThan(0.1f), "moonlit, never black");
        }

        ViewCamera MakeCamera()
        {
            var cam = Make<Camera>("Camera");
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 30000f;
            var view = cam.gameObject.AddComponent<ViewCamera>();
            view.Surface = new Hills();
            view.Ring = Hills.RingRect;
            view.HasRing = true;
            view.PlinthTop = 1800;
            view.InputEnabled = false;
            return view;
        }

        /// <summary>Asserts the camera sits above the snow by its near plane where there is terrain, and within the bounds.</summary>
        static void AssertInBounds(ViewCamera view, string what)
        {
            var p = view.transform.position;
            var surface = view.Surface;
            float ground = surface.HeightAt(p.x, p.z);
            var cam = view.GetComponent<Camera>();
            // The near plane's lowest corner, a conservative bound at this field of view.
            float halfHeight = cam.nearClipPlane * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float reach = Mathf.Sqrt(cam.nearClipPlane * cam.nearClipPlane + halfHeight * halfHeight * (1 + cam.aspect * cam.aspect));
            if (!float.IsNaN(ground))
                Assert.That(p.y - reach, Is.GreaterThanOrEqualTo(ground + view.SnowDepth - 1e-3f), $"{what}: above the snow");
            else
                Assert.That(p.y, Is.GreaterThanOrEqualTo(view.PlinthTop), $"{what}: above the plinth");
            Assert.That(view.Target.x, Is.InRange(view.Ring.xMin, view.Ring.xMax), $"{what}: focus inside the ring");
            Assert.That(view.Target.z, Is.InRange(view.Ring.yMin, view.Ring.yMax), $"{what}: focus inside the ring");
            float margin = view.Current == ViewCamera.Mode.Orbit ? view.OutsideRing + 0.01f : 0.01f;
            Assert.That(p.x, Is.InRange(view.Ring.xMin - margin, view.Ring.xMax + margin), $"{what}: eye within bounds");
            Assert.That(p.z, Is.InRange(view.Ring.yMin - margin, view.Ring.yMax + margin), $"{what}: eye within bounds");
        }

        [UnityTest]
        public IEnumerator TheOrbitCameraNeverGoesUnderTheSnowOrPastTheRing()
        {
            var view = MakeCamera();
            int checks = 0;
            foreach (var target in new[] { new Vector3(0, 0, 0), new Vector3(2990, 0, -2990), new Vector3(-9000, 0, 500), new Vector3(1500, 0, 2900) })
                foreach (float distance in new[] { 2f, 15f, 300f, 4000f, 20000f })
                    foreach (float pitch in new[] { 2f, 10f, 45f, 89f })
                        foreach (float yaw in new[] { 0f, 135f, 270f })
                        {
                            view.Frame(target, distance);
                            view.SetAngles(yaw, pitch);
                            yield return null;
                            AssertInBounds(view, $"orbit {target} d{distance} p{pitch} y{yaw}");
                            checks++;
                        }
            Assert.That(checks, Is.EqualTo(240));
        }

        [UnityTest]
        public IEnumerator FreeFlyStaysOverTheRingAndAboveTheSnow()
        {
            var view = MakeCamera();
            foreach (var eye in new[] { new Vector3(0, 0, 0), new Vector3(0, 5000, 0), new Vector3(2500, 1990, 100), new Vector3(8000, 2100, -8000), new Vector3(-100, -500, 2999) })
                foreach (float pitch in new[] { -80f, 0f, 80f })
                {
                    view.FlyFrom(eye, 30, pitch);
                    yield return null;
                    AssertInBounds(view, $"free-fly {eye} p{pitch}");
                    var p = view.transform.position;
                    Assert.That(p.x, Is.InRange(Hills.RingRect.xMin, Hills.RingRect.xMax), $"free-fly {eye}: over the ring");
                    Assert.That(p.z, Is.InRange(Hills.RingRect.yMin, Hills.RingRect.yMax), $"free-fly {eye}: over the ring");
                }
            // Switching back to orbit keeps the view's ground point in the middle.
            view.FlyFrom(new Vector3(-1000, 3000, -1000), 45, 30);
            yield return null;
            var looking = view.transform.forward;
            view.ToggleMode();
            yield return null;
            Assert.That(view.Current, Is.EqualTo(ViewCamera.Mode.Orbit));
            Assert.That(Vector3.Angle(view.transform.forward, looking), Is.LessThan(2f), "the same view, now orbiting");
            AssertInBounds(view, "back to orbit");
        }

        [UnityTest]
        public IEnumerator AKeyMovesTheCameraOnTheNextFrame()
        {
            // Batch-mode editors have no focused Game view, and by default the editor keeps keys from a play
            // session that isn't focused; send them to the game for this test.
            var routing = InputSystem.settings.editorInputBehaviorInPlayMode;
            var background = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            keyboard.MakeCurrent();
            try
            {
                var view = MakeCamera();
                view.InputEnabled = true;
                view.Frame(new Vector3(0, 0, 0), 1000);
                view.SetAngles(0, 30);
                yield return null;
                yield return null;
                var before = view.transform.position;
                var clock = Stopwatch.StartNew();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                // The input system reads the queued key at the start of the next frame, and the camera moves in that
                // frame's LateUpdate. Coroutines resume before LateUpdate, so the move shows on the second resume.
                int frames = 0;
                do
                {
                    yield return null;
                    frames++;
                } while (Vector3.Distance(view.transform.position, before) < 0.01f && frames < 30);
                clock.Stop();
                Assert.That(frames, Is.LessThanOrEqualTo(2), $"the camera moves in the first frame that sees the key (moved after {frames})");
                Assert.That(clock.Elapsed.TotalMilliseconds, Is.LessThan(100), "within 100 ms");
                Assert.That(view.transform.position.z, Is.GreaterThan(before.z), "W pans north here (yaw 0)");

                // With the Toolbox tray open, letters are tool keys: W no longer moves the camera, the arrows still do.
                view.LettersToTools = true;
                yield return null;
                before = view.transform.position;
                yield return null;
                Assert.That(Vector3.Distance(view.transform.position, before), Is.LessThan(0.01f), "W belongs to the tools");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.UpArrow));
                yield return null;
                yield return null;
                Assert.That(view.transform.position.z, Is.GreaterThan(before.z), "the arrows still pan");
            }
            finally
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                InputSystem.RemoveDevice(keyboard);
                InputSystem.settings.editorInputBehaviorInPlayMode = routing;
                InputSystem.settings.backgroundBehavior = background;
            }
        }
    }
}
