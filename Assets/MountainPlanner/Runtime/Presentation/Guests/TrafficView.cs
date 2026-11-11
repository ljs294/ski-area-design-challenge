using System;
using System.Collections.Generic;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// SN-31, the Traffic info layer (task 4.13 PR C): over the Network view's dark relief, every run piece in its crowding
    /// step (<see cref="InfoLegend.RunBreaks"/>), walking paths thin, every lift's line in its load step
    /// (<see cref="InfoLegend.LoadBreaksPercent"/>), and a disc at each lift's bottom that grows with its line.
    /// <para>
    /// Display only. The lines come from the dots' own paths (<see cref="SkierPaths"/>, the simulation's edge and lift order),
    /// so a step is always the counter of the piece it is drawn on. One vertex-coloured mesh with widths in screen pixels, as
    /// <see cref="NetworkView"/> draws: rebuilt when the camera moves or the steps change (at most once a second), into
    /// pooled lists, so nothing is allocated per frame.
    /// </para>
    /// </summary>
    public sealed class TrafficView : IDisposable
    {
        /// <summary>A run's line is drawn through every this many of its 2 m samples (8 m: smooth at any zoom, a quarter the vertices).</summary>
        const int SampleStride = 4;
        /// <summary>Lines float this far over the ground, as the Network view's do.</summary>
        const float DrapeMetres = 2f;
        const float RunPixels = 4.2f, PathPixels = 1.8f, LiftPixels = 3.6f;
        /// <summary>A lift's disc: this radius with nobody in line, growing with the square root of the line to the most.</summary>
        public const float DiscMinPixels = 5f, DiscMaxPixels = 34f, DiscPixelsPerRootSkier = 2.2f;

        static readonly Color Halo = new Color(12 / 255f, 14 / 255f, 16 / 255f, 0.85f);
        static readonly Color PathColour = new Color(0.62f, 0.66f, 0.72f, 0.75f);
        static readonly Color DiscRim = new Color(0.96f, 0.97f, 0.98f, 0.95f);

        readonly GameObject _root;
        readonly Mesh _mesh;
        readonly Material _material;
        readonly List<Vector3> _vertices = new List<Vector3>(32768);
        readonly List<Color32> _colours = new List<Color32>(32768);
        readonly List<int> _triangles = new List<int>(65536);

        Vector3[][] _runs = Array.Empty<Vector3[]>();
        bool[] _isPath = Array.Empty<bool>();
        float[] _width = Array.Empty<float>();
        Vector3[][] _lifts = Array.Empty<Vector3[]>();
        byte[] _runStep = Array.Empty<byte>(), _liftStep = Array.Empty<byte>();
        float[] _discPx = Array.Empty<float>();
        long _revision = -1;
        bool _dirty = true, _visible;
        Vector3 _lastEye;
        Quaternion _lastTurn;
        float _lastFov;
        int _lastHeight;

        public TrafficView(Transform parent)
        {
            _root = new GameObject("Traffic layer");
            _root.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "Traffic", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            var shader = Resources.Load<Shader>(NetworkView.ShaderResource);
            _material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { name = "Traffic" };
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = _root.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _root.SetActive(false);
        }

        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                _root.SetActive(value);
                _dirty = true;
            }
        }

        /// <summary>False once the scene it hangs in is gone (a mountain closed).</summary>
        public bool Alive => _root != null;

        /// <summary>The design revision of the lines held (-1: none yet).</summary>
        public long Revision => _revision;
        public int RunCount => _runs.Length;
        public int LiftCount => _lifts.Length;
        /// <summary>The vertex count of the last build (tests).</summary>
        public int VertexCount => _vertices.Count;

        /// <summary>
        /// The lines to draw, once per design: each edge's centreline and each lift's line from the dots' paths.
        /// <paramref name="isPath"/> marks the edges that are walking paths (drawn thin, never coloured).
        /// </summary>
        public void SetPaths(SkierPaths paths, bool[] isPath)
        {
            if (paths == null) return;
            int edges = paths.EdgeCount;
            _runs = new Vector3[edges][];
            _isPath = new bool[edges];
            _width = new float[edges];
            for (int e = 0; e < edges; e++)
            {
                var info = paths.Edges[e];
                _runs[e] = Decimated(paths, info.x, info.y);
                _isPath[e] = isPath != null && e < isPath.Length && isPath[e];
                _width[e] = paths.EdgeSize[e].y * 2;
            }
            int lifts = paths.LiftCount;
            _lifts = new Vector3[lifts][];
            for (int i = 0; i < lifts; i++)
            {
                var info = paths.Lifts[i];
                _lifts[i] = Decimated(paths, info.x, info.y);
            }
            if (_runStep.Length != edges) _runStep = new byte[edges];
            if (_liftStep.Length != lifts) { _liftStep = new byte[lifts]; _discPx = new float[lifts]; }
            _revision = paths.Revision;
            _dirty = true;
        }

        static Vector3[] Decimated(SkierPaths paths, int first, int count)
        {
            if (count <= 0) return Array.Empty<Vector3>();
            int n = (count - 1) / SampleStride + 1;
            bool tail = (count - 1) % SampleStride != 0;
            var pts = new Vector3[n + (tail ? 1 : 0)];
            for (int k = 0; k < n; k++) pts[k] = At(paths, first + k * SampleStride);
            if (tail) pts[n] = At(paths, first + count - 1);
            return pts;
        }

        static Vector3 At(SkierPaths paths, int sample)
        {
            var s = paths.Samples[sample];
            return new Vector3(s.x, s.y + DrapeMetres, s.z);
        }

        /// <summary>
        /// A run piece's step from its passes (over the hour, or today's average hour) and its width: skiers an hour per
        /// 100 ft of width against <see cref="InfoLegend.RunBreaks"/>.
        /// </summary>
        public static int RunStep(float passesPerHour, float widthMetres) =>
            InfoLegend.Step(passesPerHour * (30.48f / Mathf.Max(3f, widthMetres)), InfoLegend.RunBreaks);

        /// <summary>A lift's step from its boardings over the hour against what it can carry in one.</summary>
        public static int LoadStep(float boardedPerHour, float capacityPerHour) =>
            InfoLegend.Step(capacityPerHour <= 0 ? 0 : boardedPerHour * 100f / capacityPerHour, InfoLegend.LoadBreaksPercent);

        /// <summary>A lift's disc radius in pixels for a line of <paramref name="inLine"/>.</summary>
        public static float DiscPixels(int inLine) =>
            Mathf.Min(DiscMaxPixels, DiscMinPixels + DiscPixelsPerRootSkier * Mathf.Sqrt(Mathf.Max(0, inLine)));

        /// <summary>Sets a run piece's step; the mesh rebuilds on the next <see cref="Follow"/> only if a step changed.</summary>
        public void SetRunStep(int edge, int step)
        {
            if ((uint)edge >= (uint)_runStep.Length) return;
            byte b = (byte)Mathf.Clamp(step, 0, InfoLegend.TrafficColours.Length - 1);
            if (_runStep[edge] == b) return;
            _runStep[edge] = b;
            _dirty = true;
        }

        public void SetLift(int lift, int loadStep, int inLine)
        {
            if ((uint)lift >= (uint)_liftStep.Length) return;
            byte b = (byte)Mathf.Clamp(loadStep, 0, InfoLegend.TrafficColours.Length - 1);
            float px = DiscPixels(inLine);
            if (_liftStep[lift] == b && Mathf.Abs(_discPx[lift] - px) < 0.25f) return;
            _liftStep[lift] = b;
            _discPx[lift] = px;
            _dirty = true;
        }

        /// <summary>A run piece's width in metres (from its paths), for its crowding.</summary>
        public float WidthOf(int edge) => (uint)edge < (uint)_width.Length ? _width[edge] : 0;

        /// <summary>Where a lift's line starts, at its bottom (its circle's centre).</summary>
        public bool LiftBottom(int lift, out Vector3 at)
        {
            at = default;
            if ((uint)lift >= (uint)_lifts.Length || _lifts[lift].Length == 0) return false;
            at = _lifts[lift][0];
            return true;
        }

        /// <summary>The middle of a run piece's line (for a capture's pointer).</summary>
        public bool RunMiddle(int edge, out Vector3 at)
        {
            at = default;
            if ((uint)edge >= (uint)_runs.Length || _runs[edge].Length == 0) return false;
            at = _runs[edge][_runs[edge].Length / 2];
            return true;
        }

        public int RunStepOf(int edge) => (uint)edge < (uint)_runStep.Length ? _runStep[edge] : 0;

        /// <summary>The edge whose line passes nearest <paramref name="screen"/> within <paramref name="radiusPx"/> (paths too), or -1.</summary>
        public int RunAt(Camera camera, Vector2 screen, float radiusPx) => Nearest(camera, _runs, screen, radiusPx);

        /// <summary>The lift whose line or bottom disc is nearest <paramref name="screen"/>, or -1.</summary>
        public int LiftAt(Camera camera, Vector2 screen, float radiusPx)
        {
            if (camera == null) return -1;
            for (int i = 0; i < _lifts.Length; i++)
            {
                if (_lifts[i].Length == 0) continue;
                var p = camera.WorldToScreenPoint(_lifts[i][0]);
                if (p.z > 0 && ((Vector2)p - screen).sqrMagnitude <= Mathf.Max(radiusPx, _discPx[i]) * Mathf.Max(radiusPx, _discPx[i])) return i;
            }
            return Nearest(camera, _lifts, screen, radiusPx);
        }

        static int Nearest(Camera camera, Vector3[][] lines, Vector2 screen, float radiusPx)
        {
            if (camera == null) return -1;
            int best = -1;
            float bestD = radiusPx * radiusPx;
            for (int e = 0; e < lines.Length; e++)
            {
                var pts = lines[e];
                for (int k = 0; k + 1 < pts.Length; k++)
                {
                    var a = camera.WorldToScreenPoint(pts[k]);
                    var b = camera.WorldToScreenPoint(pts[k + 1]);
                    if (a.z <= 0 || b.z <= 0) continue;
                    float d = SegmentDistanceSq(screen, a, b);
                    if (d < bestD) { bestD = d; best = e; }
                }
            }
            return best;
        }

        static float SegmentDistanceSq(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0 : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (a + ab * t - p).sqrMagnitude;
        }

        /// <summary>Each frame: rebuilds the mesh when the camera moved or a step changed.</summary>
        public void Follow(Camera camera)
        {
            if (!_visible || camera == null) return;
            var t = camera.transform;
            if (!_dirty && t.position == _lastEye && t.rotation == _lastTurn && camera.fieldOfView == _lastFov && camera.pixelHeight == _lastHeight) return;
            _dirty = false;
            _lastEye = t.position;
            _lastTurn = t.rotation;
            _lastFov = camera.fieldOfView;
            _lastHeight = camera.pixelHeight;
            Build(camera);
        }

        // ---------- building (the Network view's primitives, at Traffic's widths) ----------

        float _perPixel;
        Vector3 _eye, _right, _up;

        float Px(Vector3 at) => _perPixel * Vector3.Distance(_eye, at);

        void Build(Camera camera)
        {
            _vertices.Clear();
            _colours.Clear();
            _triangles.Clear();
            _perPixel = 2 * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight);
            _eye = camera.transform.position;
            _right = camera.transform.right;
            _up = camera.transform.up;

            // Paths under everything, then every run's halo, then the runs quiet first, so a packed run is on top where they meet.
            for (int e = 0; e < _runs.Length; e++) if (_isPath[e]) Ribbon(_runs[e], PathPixels, PathColour);
            for (int e = 0; e < _runs.Length; e++) if (!_isPath[e]) Ribbon(_runs[e], RunPixels + 3, Halo);
            for (int step = 0; step < InfoLegend.TrafficColours.Length; step++)
            {
                var c = InfoLegend.TrafficColours[step];
                for (int e = 0; e < _runs.Length; e++) if (!_isPath[e] && _runStep[e] == step) Ribbon(_runs[e], RunPixels, c);
            }
            // Lifts over the runs: the halo, the load colour, then the line's disc at the bottom.
            for (int i = 0; i < _lifts.Length; i++)
            {
                var pts = _lifts[i];
                if (pts.Length < 2) continue;
                Ribbon(pts, LiftPixels + 3, Halo);
                Ribbon(pts, LiftPixels, InfoLegend.TrafficColours[_liftStep[i]]);
            }
            for (int i = 0; i < _lifts.Length; i++)
            {
                var pts = _lifts[i];
                if (pts.Length == 0) continue;
                Disc(pts[0], _discPx[i] + 2.5f, Halo);
                Disc(pts[0], _discPx[i] + 1.2f, DiscRim);
                Disc(pts[0], _discPx[i], InfoLegend.TrafficColours[_liftStep[i]]);
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colours);
            _mesh.SetTriangles(_triangles, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        static Color32 Linear(Color colour)
        {
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
            return c;
        }

        void Ribbon(Vector3[] pts, float widthPx, Color colour)
        {
            if (pts == null || pts.Length < 2) return;
            var c = Linear(colour);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1];
                if ((b - a).sqrMagnitude < 1e-8f) continue;
                Quad(a, b, widthPx, c);
            }
            foreach (var p in pts) DiscRaw(p, widthPx * 0.5f, c, 6);   // round joints and ends
        }

        void Quad(Vector3 a, Vector3 b, float widthPx, Color32 c)
        {
            var mid = (a + b) * 0.5f;
            var side = Vector3.Cross(b - a, _eye - mid);
            float m = side.magnitude;
            if (m < 1e-6f) return;
            side *= widthPx * 0.5f * Px(mid) / m;
            int v = _vertices.Count;
            _vertices.Add(a - side);
            _vertices.Add(a + side);
            _vertices.Add(b + side);
            _vertices.Add(b - side);
            for (int k = 0; k < 4; k++) _colours.Add(c);
            _triangles.Add(v); _triangles.Add(v + 1); _triangles.Add(v + 2);
            _triangles.Add(v); _triangles.Add(v + 2); _triangles.Add(v + 3);
        }

        void Disc(Vector3 at, float radiusPx, Color colour) => DiscRaw(at, radiusPx, Linear(colour), 24);

        void DiscRaw(Vector3 at, float radiusPx, Color32 c, int sides)
        {
            float r = radiusPx * Px(at);
            int centre = _vertices.Count;
            _vertices.Add(at);
            _colours.Add(c);
            for (int k = 0; k < sides; k++)
            {
                float a = k * Mathf.PI * 2 / sides;
                _vertices.Add(at + (_right * Mathf.Cos(a) + _up * Mathf.Sin(a)) * r);
                _colours.Add(c);
            }
            for (int k = 0; k < sides; k++)
            {
                _triangles.Add(centre);
                _triangles.Add(centre + 1 + k);
                _triangles.Add(centre + 1 + (k + 1) % sides);
            }
        }

        public void Dispose()
        {
            if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }
    }
}
