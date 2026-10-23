using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// Built lifts on the map (task 09, LT10, LT23): each a straight red trail-map line from bullwheel to bullwheel (owner
    /// 2026-10-20: a lift on a ski map is one straight stroke), drawn over the ground and never cut by it, with a dot at each end and a short tick at each tower (the mockup's drawLift). Seen from afar they read like
    /// a trail map; closer in they fade away and the 3D lift takes over. A hovered lift's line gets a wider white casing
    /// (UI-15) while lines show; close in, its 3D glow shows the hover. Lines are rebuilt only when a lift is (re)built; each frame only sets the fade and the widths, with no
    /// allocation.
    /// </summary>
    public sealed class LiftMapLines : IDisposable
    {
        public const string ShaderResource = "MountainPlannerLifts/LiftMapLine";
        /// <summary>Fully shown from this far out (camera distance, metres: 3,000 ft) and gone by this close (2,000 ft).</summary>
        public const float ShownFrom = 914.4f, GoneBy = 609.6f;
        /// <summary>Widths in screen pixels: the line, the lit casing; dots and ticks scale with the line.</summary>
        const float LinePixels = 3.2f, CasingPixels = 7.5f;
        static readonly Color Red = new Color(0.78f, 0.13f, 0.1f, 1f);

        sealed class Group
        {
            public GameObject Root;
            public LineRenderer Line, Casing;
            public readonly List<LineRenderer> Marks = new List<LineRenderer>();   // end dots and tower ticks
            public Vector3 Middle;
            public bool Lit;
        }

        readonly Transform _parent;
        readonly Material _red, _redLit, _white;
        readonly Dictionary<ulong, Group> _groups = new Dictionary<ulong, Group>();
        ulong _lit;
        bool _visible = true;

        /// <summary>The fade applied last frame (tests and captures): 1 shown, 0 hidden.</summary>
        public float Alpha { get; private set; } = 1;
        public int Count => _groups.Count;

        public LiftMapLines(Transform parent)
        {
            _parent = parent;
            var shader = Resources.Load<Shader>(ShaderResource);
            Material Make(Color c, int queueOffset)
            {
                var m = shader != null ? new Material(shader) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                m.color = c;
                m.renderQueue = 3000 - 10 + queueOffset;
                return m;
            }
            _white = Make(new Color(1, 1, 1, 0.95f), 0);   // the casing under the lit line
            _red = Make(Red, 1);
            _redLit = Make(Red, 1);
        }

        /// <summary>Shows or hides every line (the Lifts map layer).</summary>
        public void SetVisible(bool on)
        {
            _visible = on;
            foreach (var g in _groups.Values) g.Root.SetActive(on);
        }

        /// <summary>(Re)draws lift <paramref name="id"/>: a straight line from <paramref name="bottom"/> to <paramref name="top"/>
        /// (local metres, y ignored), each end <paramref name="lift"/> metres over <paramref name="height"/>, with a tick at each of
        /// <paramref name="towers"/> on the line.</summary>
        public void Set(ulong id, Vector3 bottom, Vector3 top, IReadOnlyList<Vector3> towers, Func<float, float, float> height, float lift = 2f)
        {
            Remove(id);
            var g = new Group { Root = new GameObject("Lift map line " + id.ToString("x16")) };
            g.Root.transform.SetParent(_parent, false);
            var flat = new Vector3(top.x - bottom.x, 0, top.z - bottom.z);
            float length = flat.magnitude;
            var dir = length > 1e-3f ? flat / length : Vector3.forward;
            var side = new Vector3(dir.z, 0, -dir.x);
            var from = new Vector3(bottom.x, height(bottom.x, bottom.z) + lift, bottom.z);
            var to = new Vector3(top.x, height(top.x, top.z) + lift, top.z);
            var points = new[] { from, to };
            int n = 2;
            g.Middle = (from + to) / 2;
            g.Casing = MakeLine(g.Root, "Casing", points, _white);
            g.Casing.enabled = false;
            g.Casing.sortingOrder = -1;   // under the red line, whatever their distances
            g.Line = MakeLine(g.Root, "Line", points, _red);
            g.Line.sortingOrder = 1;
            g.Marks.Add(Dot(g.Root, points[0], _red));
            g.Marks.Add(Dot(g.Root, points[n - 1], _red));
            foreach (var t in towers)
            {
                float along = length > 1e-3f ? Mathf.Clamp01(Vector3.Dot(new Vector3(t.x - bottom.x, 0, t.z - bottom.z), dir) / length) : 0;
                var c = Vector3.Lerp(from, to, along);
                g.Marks.Add(MakeLine(g.Root, "Tick", new[] { c - side * 6, c + side * 6 }, _red));
            }
            g.Root.SetActive(_visible);
            _groups[id] = g;
        }

        public void Remove(ulong id)
        {
            if (!_groups.TryGetValue(id, out var g)) return;
            Object.Destroy(g.Root);
            _groups.Remove(id);
        }

        /// <summary>Removes every lift not in <paramref name="keep"/>.</summary>
        public void KeepOnly(HashSet<ulong> keep)
        {
            var gone = new List<ulong>();
            foreach (var id in _groups.Keys) if (!keep.Contains(id)) gone.Add(id);
            foreach (var id in gone) Remove(id);
        }

        /// <summary>
        /// Each frame: the fade from the camera's distance to what it looks at (<paramref name="viewDistance"/>), the lines'
        /// widths in pixels, and the lit lift (<paramref name="lit"/>, 0 for none), which stays shown with its white casing.
        /// </summary>
        public void Follow(Camera camera, float viewDistance, ulong lit)
        {
            if (camera == null) return;
            float alpha = Mathf.Clamp01((viewDistance - GoneBy) / (ShownFrom - GoneBy));
            if (!Mathf.Approximately(alpha, Alpha))
            {
                Alpha = alpha;
                var c = Red;
                c.a = alpha;
                _red.color = c;
            }
            if (lit != _lit)
            {
                if (_groups.TryGetValue(_lit, out var was)) Paint(was, false);
                _lit = lit;
                if (_groups.TryGetValue(_lit, out var now)) Paint(now, true);
            }
            float perPixel = 2 * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight);
            var eye = camera.transform.position;
            foreach (var g in _groups.Values)
            {
                // Close in the 3D lift takes over, lit or not (its own glow shows the hover there).
                bool shown = alpha > 0.001f;
                if (g.Line.enabled != shown)
                {
                    g.Line.enabled = shown;
                    foreach (var m in g.Marks) m.enabled = shown;
                }
                bool casing = shown && g.Lit;
                if (g.Casing.enabled != casing) g.Casing.enabled = casing;
                if (!shown) continue;
                float px = perPixel * Vector3.Distance(eye, g.Middle);
                g.Line.widthMultiplier = LinePixels * px;
                g.Casing.widthMultiplier = CasingPixels * px;
                foreach (var m in g.Marks) m.widthMultiplier = LinePixels * px;
            }
        }

        void Paint(Group g, bool lit)
        {
            g.Lit = lit;
            var m = lit ? _redLit : _red;
            g.Line.sharedMaterial = m;
            foreach (var r in g.Marks) r.sharedMaterial = m;
        }

        static LineRenderer MakeLine(GameObject root, string name, Vector3[] points, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = points.Length;
            lr.SetPositions(points);
            lr.sharedMaterial = material;
            lr.widthMultiplier = 1;
            lr.numCapVertices = 4;
            lr.numCornerVertices = 2;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sortingOrder = 1;
            return lr;
        }

        // A dot: a very short, round-capped line, so it keeps its pixel size like the line.
        static LineRenderer Dot(GameObject root, Vector3 at, Material material)
        {
            var lr = MakeLine(root, "Dot", new[] { at, at + new Vector3(0.01f, 0, 0) }, material);
            lr.numCapVertices = 8;
            lr.widthCurve = AnimationCurve.Constant(0, 1, 2.6f);
            return lr;
        }

        public void Dispose()
        {
            foreach (var g in _groups.Values) Object.Destroy(g.Root);
            _groups.Clear();
            Object.Destroy(_red);
            Object.Destroy(_redLit);
            Object.Destroy(_white);
        }
    }
}
