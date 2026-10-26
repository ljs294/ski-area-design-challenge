using MountainPlanner.UI;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Names on the map hide behind the ground (owner, 2026-10-20: "labels are visible through terrain").
    public sealed class LabelSightTests
    {
        /// <summary>Flat ground at 1,000 m with a 300 m ridge running north-south at x = 0.</summary>
        sealed class Ridge : ITerrainSurface
        {
            public Rect Footprint => new Rect(-2000, -2000, 4000, 4000);
            public float HeightAt(float x, float z) => 1000 + Mathf.Max(0, 300 - Mathf.Abs(x) * 3);
        }

        [Test]
        public void ALabelBehindARidgeIsHiddenAndOneInSightIsNot()
        {
            var ground = new Ridge();
            var eye = new Vector3(-800, 1100, 0);
            Assert.IsTrue(LabelSight.Hidden(eye, new Vector3(800, 1002, 0), ground), "the far side of the ridge");
            Assert.IsFalse(LabelSight.Hidden(eye, new Vector3(-400, 1002, 300), ground), "this side, in plain sight");
            Assert.IsFalse(LabelSight.Hidden(new Vector3(-800, 2500, 0), new Vector3(800, 1002, 0), ground), "seen from high above the ridge");
            Assert.IsFalse(LabelSight.Hidden(eye, new Vector3(800, 1002, 0), null), "no ground: never hidden");
        }
        /// <summary>The ridge, counting every height it's asked for.</summary>
        sealed class Counting : ITerrainSurface
        {
            readonly Ridge _ridge = new Ridge();
            public int Samples;
            public Rect Footprint => _ridge.Footprint;
            public float HeightAt(float x, float z) { Samples++; return _ridge.HeightAt(x, z); }
        }

        [Test]
        public void D1_AStillCameraChecksNoLabelsSight()
        {
            // Finding D1 (owner, 2026-10-23): the names' sight checks every frame made the game lag. Each name is checked once
            // on screen, again only once the eye moves; a still camera samples the ground no more.
            var root = new UnityEngine.UIElements.VisualElement();
            var layer = new UnityEngine.UIElements.VisualElement();
            root.Add(layer);
            var overlay = new LandmarkLabelOverlay(layer);
            overlay.SetLandmarks(new[] { ("Near", new Vector3(-400, 1002, 100)), ("Far side", new Vector3(800, 1002, 0)), ("Behind", new Vector3(-1500, 1002, 0)) });
            var rt = new RenderTexture(640, 480, 16);
            var go = new GameObject("Label test camera");
            try
            {
                var camera = go.AddComponent<Camera>();
                camera.targetTexture = rt;
                go.transform.SetPositionAndRotation(new Vector3(-800, 1100, 0), Quaternion.LookRotation(Vector3.right));
                var ground = new Counting();
                for (int f = 0; f < 8; f++) overlay.Update(camera, ground, true);
                int checks = overlay.SightChecks, samples = ground.Samples;
                Assert.That(checks, Is.EqualTo(2), "the two names in front of the camera, once each; the one behind it never");
                for (int f = 0; f < 60; f++) overlay.Update(camera, ground, true);
                Assert.That(overlay.SightChecks, Is.EqualTo(checks), "a still camera checks nothing more");
                Assert.That(ground.Samples, Is.EqualTo(samples), "and samples no ground");
                go.transform.position += new Vector3(0, 0, 20);
                for (int f = 0; f < 8; f++) overlay.Update(camera, ground, true);
                Assert.That(overlay.SightChecks, Is.GreaterThan(checks), "a moved eye checks again");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(rt);
            }
        }
    }
}
