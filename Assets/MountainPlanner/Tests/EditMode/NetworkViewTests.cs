using MountainPlanner.Presentation.Design;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-10: the Network view's diagram builds, ends (a long dashed stretch once looped for ever on rounding), and
    /// picks a node before a line and a line by its segment.
    /// </summary>
    public sealed class NetworkViewTests
    {
        GameObject _camera, _parent;
        Camera _cam;
        NetworkView _view;

        [SetUp]
        public void SetUp()
        {
            _camera = new GameObject("Network test camera");
            _cam = _camera.AddComponent<Camera>();
            _cam.fieldOfView = 60;
            _cam.transform.position = new Vector3(0, 3000, -3000);
            _cam.transform.LookAt(Vector3.zero);
            _parent = new GameObject("Network test parent");
            _view = new NetworkView(_parent.transform);
        }

        [TearDown]
        public void TearDown()
        {
            _view.Dispose();
            Object.DestroyImmediate(_parent);
            Object.DestroyImmediate(_camera);
        }

        static Vector3[] Line(float x0, float z0, float x1, float z1, int n)
        {
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++) pts[i] = Vector3.Lerp(new Vector3(x0, 0, z0), new Vector3(x1, 0, z1), i / (n - 1f));
            return pts;
        }

        [Test]
        public void PA08_NW12_ALongUnservedStretchDrawsDashedAndEnds()
        {
            // A stretch no lift serves is dashed: thousands of metres in short pieces, far from the camera.
            var segments = new[]
            {
                new NetworkView.Segment { Edge = 0, Trail = 1, Points = Line(-2000, 0, 2000, 400, 400), Own = Color.blue, Served = false },
                new NetworkView.Segment { Edge = 1, Trail = 2, Points = Line(-500, -800, 500, -800, 20), Own = Color.green, Served = true },
            };
            var lifts = new[] { new NetworkView.LiftLine { Id = 9, Bottom = new Vector3(0, 0, -900), Top = new Vector3(0, 0, 900), Connected = false } };
            var nodes = new[] { new NetworkView.Node { Id = 5, Kind = NetworkView.NodeKind.Junction, At = new Vector3(-500, 0, -800) } };
            var links = new[] { new NetworkView.Link { A = new Vector3(100, 0, -790), B = new Vector3(100, 0, -760) } };
            _view.Set(segments, lifts, nodes, links, new[] { new NetworkView.DeadEnd { At = new Vector3(500, 0, -800), Dir = Vector3.right } });
            _view.Visible = true;
            _view.Follow(_cam);
            Assert.That(_view.VertexCount, Is.GreaterThan(1000), "lines, chevrons, circles and dashes");

            // A pick lights; the same build ends again with it.
            _view.SetPick(1, 0, 0, new[] { 1 }, new ulong[0]);
            _view.SetHover(0, 0, 0, 0);
            _view.Follow(_cam);
            Assert.That(_view.VertexCount, Is.GreaterThan(1000));
        }

        static Vector3[] Rim(Vector3 c, float r)
        {
            var rim = new Vector3[49];
            for (int k = 0; k <= 48; k++) rim[k] = c + new Vector3(Mathf.Cos(k * Mathf.PI / 24), 0, Mathf.Sin(k * Mathf.PI / 24)) * r;
            return rim;
        }

        [Test]
        public void NW14_NT15_NT16_StationAreasAndTheCatchmentDraw()
        {
            // NT15: a shared base, two terminals' areas under one station.
            var areas = new[]
            {
                new NetworkView.StationArea { Station = 11, Lift = 1, Centre = Vector3.zero, Rim = Rim(Vector3.zero, 30.48f) },
                new NetworkView.StationArea { Station = 11, Lift = 2, Centre = new Vector3(40, 0, 0), Rim = Rim(new Vector3(40, 0, 0), 30.48f) },
            };
            _view.Set(null, null, null, null, null, areas);
            _view.Visible = true;
            _view.Follow(_cam);
            int plain = _view.VertexCount;
            Assert.That(plain, Is.GreaterThan(2 * 49), "two rims and two fills");

            // NT16: the lift tool's catchment, a terminal in hand and the trail it gathers lit.
            var candidate = new NetworkView.StationArea { Candidate = true, Centre = new Vector3(0, 0, 200), Rim = Rim(new Vector3(0, 0, 200), 30.48f) };
            var gathered = new[] { new NetworkView.Segment { Edge = 0, Trail = 7, Points = Line(-100, 210, 100, 210, 8), Own = Color.blue, Served = true } };
            _view.SetCatchment(gathered, new[] { areas[0], areas[1], candidate });
            _view.Follow(_cam);
            Assert.That(_view.VertexCount, Is.GreaterThan(plain + 49), "the candidate's area and the lit trail");
        }

        [Test]
        public void NW13_ANodeWinsTheClickOverTheLineThroughIt()
        {
            var segments = new[] { new NetworkView.Segment { Edge = 3, Trail = 7, Points = Line(-500, 0, 500, 0, 10), Own = Color.blue, Served = true } };
            var nodes = new[] { new NetworkView.Node { Id = 42, Kind = NetworkView.NodeKind.Junction, At = Vector3.zero } };
            _view.Set(segments, null, nodes, null, null);
            var atNode = (Vector2)_cam.WorldToScreenPoint(Vector3.zero);
            var hit = _view.Pick(_cam, atNode);
            Assert.That(hit.Node, Is.EqualTo(42UL));
            var onLine = (Vector2)_cam.WorldToScreenPoint(new Vector3(300, 0, 0));
            hit = _view.Pick(_cam, onLine);
            Assert.That(hit.Node, Is.EqualTo(0UL));
            Assert.That(hit.Edge, Is.EqualTo(3), "R2-2: the segment under the pointer");
            Assert.That(hit.Trail, Is.EqualTo(7UL));
            Assert.That(_view.Pick(_cam, onLine + new Vector2(0, 200)).Any, Is.False, "empty ground picks nothing (NT11 clears)");
        }
    }
}
