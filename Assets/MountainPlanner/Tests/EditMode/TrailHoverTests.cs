using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.26 BB3: hovering a trail lit it by draping a new white casing on the ground, sampling the package heights
    /// (12 to 33 MB in one frame, finding A3-13). The casing now copies the points the trail's own line already draped.
    /// </summary>
    public sealed class TrailHoverTests
    {
        sealed class Counting : ITerrainSurface
        {
            public int Samples;
            public Rect Footprint => new Rect(-2000, -2000, 4000, 4000);
            public float LiftAt(float x, float z) => 0;
            public float HeightAt(float x, float z) { Samples++; return 1000 + x * 0.1f; }
        }

        [Test]
        public void BB3_HoveringATrailSamplesNoGroundAndAllocatesNothing()
        {
            var ground = new Counting();
            var root = new GameObject("Trail hover test");
            var layer = new TrailMapView(root.transform, ground, null);
            try
            {
                var a = new[] { new Vector2(0, 0), new Vector2(400, 300), new Vector2(800, 0) };
                var b = new[] { new Vector2(0, 500), new Vector2(900, 700) };
                layer.SetTrailLines(new List<(Vector2[], PreviewInk)> { (a, PreviewInk.Plan), (b, PreviewInk.Plan) }, new HashSet<TrailId>());
                Assert.That(ground.Samples, Is.GreaterThan(0), "the lines themselves are draped once");
                layer.FocusLine(a);
                layer.FocusLine(b);
                layer.Focus(default);
                ground.Samples = 0;
                long before = System.GC.GetAllocatedBytesForCurrentThread();
                for (int k = 0; k < 20; k++) { layer.FocusLine(a); layer.FocusLine(b); layer.Focus(default); }
                long bytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.That(ground.Samples, Is.EqualTo(0), "the casing copies the draped line; it samples no ground");
                Assert.That(bytes, Is.EqualTo(0), "and allocates nothing");
                var unknown = new[] { new Vector2(10, 10), new Vector2(20, 20) };
                layer.FocusLine(unknown);
                Assert.That(ground.Samples, Is.GreaterThan(0), "a line not drawn yet is still draped for its casing");
            }
            finally
            {
                layer.Dispose();
                Object.DestroyImmediate(root);
            }
        }
    }
}
