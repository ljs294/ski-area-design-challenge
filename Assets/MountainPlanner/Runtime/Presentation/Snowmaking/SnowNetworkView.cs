using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Snowmaking
{
    /// <summary>
    /// Task 4.15's snowmaking network on the map (the approved mockup's Snowmaking tab and its Pipe pressure info layer): pipes
    /// as lines in their own blue over a dark edge (wider for a bigger pipe), hydrants as small white dots, junctions as
    /// white rings, pumps as an arrow in a disc (the way they push), pump houses as a dark square with a white edge, intakes
    /// and outlets as rings on the water, guns as dots on a dashed hose to their hydrant (red ringed when none is in reach).
    /// <para>
    /// The Pipe pressure layer colours every pipe by the pressure along it on one fixed scale for every network (plan: an
    /// absolute ramp anchored at 200 psi, so colours compare between edits): under 200 warm (red to orange), 200–800 cool
    /// (pale to deep blue), 800–1,000 amber, over the 1,000 psi class red. A result older than the last edit shows greyed.
    /// </para>
    /// <para>
    /// One mesh, coloured per vertex, its widths in screen pixels (the Network view's shader, drawn over the ground): rebuilt
    /// only when the camera moves or the network changes, into pooled lists (nothing allocated per frame).
    /// </para>
    /// </summary>
    public sealed class SnowNetworkView : IDisposable
    {
        public const string ShaderResource = "MountainPlannerDesign/NetworkLine";

        public enum MarkKind : byte { Hydrant, Junction, Pump, House, Intake, Outlet, Gun }

        public enum GunLook : byte
        {
            /// <summary>Running (or would, in the check).</summary>
            Running,
            /// <summary>Off, or Auto waiting for 4.16's schedules.</summary>
            Idle,
            /// <summary>On but short of pressure, air or water: red.</summary>
            Short,
        }

        /// <summary>A pipe: its route draped (local metres) and the pressure at each point, psi (NaN: not solved).</summary>
        public struct Pipe
        {
            public ulong Id;
            public Vector3[] Points;
            public float[] Psi;
            public int DiameterIn;
        }

        public struct Mark
        {
            public ulong Id;
            public MarkKind Kind;
            public Vector3 At;
            /// <summary>A pump's push and a house's long axis, on the ground (x, z).</summary>
            public Vector2 Dir;
            /// <summary>A gun's hydrant (its hose ends there); no hydrant: a red ring.</summary>
            public bool Hooked;
            public Vector3 Hydrant;
            public GunLook Look;
        }

        // The mockup's colours (sRGB; turned linear for the vertices).
        static readonly Color Water = Hex(0x4f9be0), Edge = new Color(10 / 255f, 22 / 255f, 34 / 255f, 0.9f), White = Color.white;
        static readonly Color Red = Hex(0xe0483a), Amber = Hex(0xf0b43c), Idle = Hex(0x8b9298), Ink = Hex(0x1b2127);
        static readonly Color Lit = new Color(1, 1, 1, 0.38f);

        readonly GameObject _root;
        readonly Mesh _mesh;
        readonly Material _material;
        readonly List<Vector3> _vertices = new List<Vector3>(16384);
        readonly List<Color32> _colours = new List<Color32>(16384);
        readonly List<int> _triangles = new List<int>(32768);
        readonly HashSet<ulong> _lit = new HashSet<ulong>();

        Pipe[] _pipes = Array.Empty<Pipe>();
        Mark[] _marks = Array.Empty<Mark>();
        bool _pressure, _stale, _dirty = true, _visible;
        Vector3 _lastEye;
        Quaternion _lastTurn;
        float _lastFov;
        int _lastHeight;

        public SnowNetworkView(Transform parent, string name = "Snowmaking network")
        {
            _root = new GameObject(name);
            _root.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "Snowmaking network", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            var shader = Resources.Load<Shader>(ShaderResource);
            _material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { name = "Snowmaking network" };
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

        /// <summary>The Pipe pressure info layer: pipes by pressure on the fixed ramp instead of their own blue.</summary>
        public bool Pressure
        {
            get => _pressure;
            set { if (_pressure != value) { _pressure = value; _dirty = true; } }
        }

        /// <summary>The result is older than the last edit (HY-08's latency): drawn greyed until the new one lands.</summary>
        public bool Stale
        {
            get => _stale;
            set { if (_stale != value) { _stale = value; _dirty = true; } }
        }

        public int VertexCount => _vertices.Count;
        public int PipeCount => _pipes.Length;
        public int MarkCount => _marks.Length;

        /// <summary>The network to draw (rebuilt with the design or a new solve, never per frame).</summary>
        public void Set(Pipe[] pipes, Mark[] marks)
        {
            _pipes = pipes ?? Array.Empty<Pipe>();
            _marks = marks ?? Array.Empty<Mark>();
            _dirty = true;
        }

        /// <summary>Lights pieces by id (a card's subject, a problem's "Show me"); empty for none.</summary>
        public void Light(IReadOnlyList<Domain.Design.SnowId> ids)
        {
            _lit.Clear();
            if (ids != null) foreach (var id in ids) _lit.Add(id.Value);
            _dirty = true;
        }

        /// <summary>The fixed pressure ramp (plan, the mockup's legend): 0 / 200 / 500 / 800 / 1,000 psi.</summary>
        public static Color PressureColour(float psi)
        {
            if (float.IsNaN(psi)) return Water;
            if (psi < 200) return Color.Lerp(Hex(0xb8321f), Hex(0xf08a3c), Mathf.Clamp01(psi / 200f));
            if (psi < 800) return Color.Lerp(Hex(0x8cc8ee), Hex(0x17408f), Mathf.Clamp01((psi - 200) / 600f));   // one hue, light to dark; the light end still reads on snow
            if (psi <= 1000) return Amber;
            return Red;
        }

        /// <summary>Each frame: rebuilds the mesh when the camera moved or anything changed.</summary>
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

        // ---------- building ----------

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

            // Hoses under everything (dashed, thin), then the pipes: a glow when lit, a dark edge, then the water.
            foreach (var m in _marks)
                if (m.Kind == MarkKind.Gun && m.Hooked) Dashed(m.Hydrant, m.At, 1.6f, Grey(White, 0.85f), 4, 3);
            foreach (var p in _pipes)
                if (p.Points != null && p.Points.Length >= 2 && _lit.Contains(p.Id)) Line(p, Width(p) + 12, Lit, false);
            foreach (var p in _pipes)
                if (p.Points != null && p.Points.Length >= 2) Line(p, Width(p) + 3, Edge, false);
            foreach (var p in _pipes)
                if (p.Points != null && p.Points.Length >= 2) Line(p, Width(p), Water, _pressure);

            foreach (var m in _marks)
            {
                if (_lit.Contains(m.Id)) DiscRaw(m.At, 13, C(Lit), 20);
                switch (m.Kind)
                {
                    case MarkKind.Hydrant:
                        DiscRaw(m.At, 3.4f, C(Edge), 10);
                        DiscRaw(m.At, 2.4f, C(Grey(White, 1)), 10);
                        break;
                    case MarkKind.Junction:
                        Ring(m.At, 4.2f, 2.2f, Edge);
                        Ring(m.At, 4.2f, 1.3f, Grey(White, 1));
                        break;
                    case MarkKind.Intake:
                    case MarkKind.Outlet:
                        DiscRaw(m.At, 6.5f, C(Edge), 16);
                        Ring(m.At, 4.6f, 1.8f, Grey(Water, 1));
                        if (m.Kind == MarkKind.Outlet) DiscRaw(m.At, 2.2f, C(Grey(Water, 1)), 10);
                        break;
                    case MarkKind.Pump:
                        DiscRaw(m.At, 8, C(Edge), 18);
                        DiscRaw(m.At, 6.6f, C(Grey(White, 1)), 18);
                        Arrow(m.At, m.Dir, 5.2f, Ink);
                        break;
                    case MarkKind.House:
                        Square(m.At, m.Dir, 9.5f, Edge);
                        Square(m.At, m.Dir, 8, Grey(White, 1));
                        Square(m.At, m.Dir, 5.6f, Ink);
                        break;
                    case MarkKind.Gun:
                    {
                        var look = m.Look == GunLook.Running ? Water : m.Look == GunLook.Short ? Red : Idle;
                        DiscRaw(m.At, 5.2f, C(Edge), 14);
                        DiscRaw(m.At, 4, C(Grey(look, 1)), 14);
                        if (!m.Hooked) Ring(m.At, 9, 2.2f, Red);
                        break;
                    }
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colours);
            _mesh.SetTriangles(_triangles, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        // Wider while the Pipe pressure layer is on: the colour is the reading, so it needs the pixels.
        float Width(in Pipe p) => (2.2f + Mathf.Clamp(p.DiameterIn, 4, 24) * 0.17f) * (_pressure ? 1.8f : 1f);

        // A stale result keeps its shape, greyed: the mockup's "Updating the result".
        Color Grey(Color c, float a)
        {
            if (_stale) { float l = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f; c = new Color(l, l, l, c.a); }
            c.a *= a;
            return c;
        }

        Color32 C(Color colour)
        {
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
            return c;
        }

        /// <summary>A pipe as a screen-width line, its own colour or (<paramref name="byPressure"/>) each point's pressure.</summary>
        void Line(in Pipe p, float widthPx, Color colour, bool byPressure)
        {
            var pts = p.Points;
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Color32 a = C(Grey(byPressure && p.Psi != null ? PressureColour(p.Psi[i]) : colour, 1));
                Color32 b = C(Grey(byPressure && p.Psi != null ? PressureColour(p.Psi[i + 1]) : colour, 1));
                if (!byPressure) { a.a = b.a = C(colour).a; }
                Quad(pts[i], pts[i + 1], widthPx, a, b);
            }
            Color32 j = C(Grey(colour, 1));
            j.a = C(colour).a;
            for (int i = 0; i < pts.Length; i++)
            {
                var jc = byPressure && p.Psi != null ? C(Grey(PressureColour(p.Psi[i]), 1)) : j;
                DiscRaw(pts[i], widthPx * 0.5f, jc, 8);
            }
        }

        void Dashed(Vector3 a, Vector3 b, float widthPx, Color colour, float dash, float gap)
        {
            var c = C(colour);
            float len = Vector3.Distance(a, b);
            if (len < 1e-4f) return;
            float px = Px((a + b) * 0.5f), on = Mathf.Max(dash * px, 1e-3f), period = on + Mathf.Max(gap * px, 1e-3f);
            int count = Mathf.Min(256, Mathf.CeilToInt(len / period));
            for (int k = 0; k < count; k++)
            {
                float s0 = k * period, s1 = Mathf.Min(len, s0 + on);
                if (s1 > s0) Quad(Vector3.Lerp(a, b, s0 / len), Vector3.Lerp(a, b, s1 / len), widthPx, c, c);
            }
        }

        void Quad(Vector3 a, Vector3 b, float widthPx, Color32 ca, Color32 cb)
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
            _colours.Add(ca); _colours.Add(ca); _colours.Add(cb); _colours.Add(cb);
            _triangles.Add(v); _triangles.Add(v + 1); _triangles.Add(v + 2);
            _triangles.Add(v); _triangles.Add(v + 2); _triangles.Add(v + 3);
        }

        /// <summary>An arrow on the ground through <paramref name="at"/> pointing along <paramref name="dir"/>, its size in pixels.</summary>
        void Arrow(Vector3 at, Vector2 dir, float sizePx, Color colour)
        {
            var c = C(Grey(colour, 1));
            float px = Px(at);
            var u = new Vector3(dir.x, 0, dir.y);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.forward;
            u.Normalize();
            var s = Vector3.Cross(Vector3.up, u);
            var tip = at + u * (sizePx * px);
            var back = at - u * (sizePx * 0.8f * px);
            Quad(back, at + u * (sizePx * 0.2f * px), 1.8f, c, c);
            int v = _vertices.Count;
            _vertices.Add(tip);
            _vertices.Add(at - u * (sizePx * 0.15f * px) + s * (sizePx * 0.75f * px));
            _vertices.Add(at - u * (sizePx * 0.15f * px) - s * (sizePx * 0.75f * px));
            _colours.Add(c); _colours.Add(c); _colours.Add(c);
            _triangles.Add(v); _triangles.Add(v + 1); _triangles.Add(v + 2);
            _triangles.Add(v); _triangles.Add(v + 2); _triangles.Add(v + 1);
        }

        /// <summary>A square on the ground turned along <paramref name="dir"/>, its half-side in pixels.</summary>
        void Square(Vector3 at, Vector2 dir, float halfPx, Color colour)
        {
            var c = C(Grey(colour, 1));
            float h = halfPx * Px(at);
            var u = new Vector3(dir.x, 0, dir.y);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.forward;
            u.Normalize();
            var s = Vector3.Cross(Vector3.up, u);
            int v = _vertices.Count;
            _vertices.Add(at + (u + s) * h);
            _vertices.Add(at + (u - s) * h);
            _vertices.Add(at + (-u - s) * h);
            _vertices.Add(at + (-u + s) * h);
            for (int k = 0; k < 4; k++) _colours.Add(c);
            // Both windings: seen from above or below.
            _triangles.Add(v); _triangles.Add(v + 1); _triangles.Add(v + 2);
            _triangles.Add(v); _triangles.Add(v + 2); _triangles.Add(v + 3);
            _triangles.Add(v); _triangles.Add(v + 2); _triangles.Add(v + 1);
            _triangles.Add(v); _triangles.Add(v + 3); _triangles.Add(v + 2);
        }

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

        void Ring(Vector3 at, float radiusPx, float widthPx, Color colour)
        {
            var c = C(colour);
            const int sides = 24;
            float px = Px(at), outer = (radiusPx + widthPx * 0.5f) * px, inner = (radiusPx - widthPx * 0.5f) * px;
            int v0 = _vertices.Count;
            for (int k = 0; k < sides; k++)
            {
                float a = k * Mathf.PI * 2 / sides;
                var d = _right * Mathf.Cos(a) + _up * Mathf.Sin(a);
                _vertices.Add(at + d * outer);
                _vertices.Add(at + d * inner);
                _colours.Add(c);
                _colours.Add(c);
            }
            for (int k = 0; k < sides; k++)
            {
                int a0 = v0 + 2 * k, b0 = v0 + 2 * ((k + 1) % sides);
                _triangles.Add(a0); _triangles.Add(a0 + 1); _triangles.Add(b0 + 1);
                _triangles.Add(a0); _triangles.Add(b0 + 1); _triangles.Add(b0);
            }
        }

        // ---------- picking ----------

        /// <summary>The mark (node, house or gun) nearest a screen point within <paramref name="radiusPx"/>, else 0.</summary>
        public ulong MarkAt(Camera camera, Vector2 screen, float radiusPx, out MarkKind kind)
        {
            kind = default;
            ulong best = 0;
            float bestD = radiusPx * radiusPx;
            foreach (var m in _marks)
            {
                var s = camera.WorldToScreenPoint(m.At);
                if (s.z <= 0) continue;
                float d = (new Vector2(s.x, s.y) - screen).sqrMagnitude;
                // Guns sit beside their hydrants: on a tie the gun wins (it's what a player clicks for).
                if (d < bestD || d == bestD && m.Kind == MarkKind.Gun) { bestD = d; best = m.Id; kind = m.Kind; }
            }
            return best;
        }

        /// <summary>The pipe nearest a screen point within <paramref name="radiusPx"/>, else 0, and the point index nearest it.</summary>
        public ulong PipeAt(Camera camera, Vector2 screen, float radiusPx, out int point)
        {
            point = -1;
            ulong best = 0;
            float bestD = radiusPx * radiusPx;
            foreach (var p in _pipes)
            {
                if (p.Points == null) continue;
                for (int i = 0; i + 1 < p.Points.Length; i++)
                {
                    float d = ScreenDistance(camera, p.Points[i], p.Points[i + 1], screen, out float t);
                    if (d < bestD) { bestD = d; best = p.Id; point = t < 0.5f ? i : i + 1; }
                }
            }
            return best;
        }

        static float ScreenDistance(Camera camera, Vector3 a, Vector3 b, Vector2 screen, out float t)
        {
            t = 0;
            var pa = camera.WorldToScreenPoint(a);
            var pb = camera.WorldToScreenPoint(b);
            if (pa.z <= 0 || pb.z <= 0) return float.MaxValue;
            var A = new Vector2(pa.x, pa.y);
            var B = new Vector2(pb.x, pb.y);
            var ab = B - A;
            t = ab.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(screen - A, ab) / ab.sqrMagnitude) : 0;
            return (A + ab * t - screen).sqrMagnitude;
        }

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

        public void Dispose()
        {
            Gone(_root);
            Gone(_mesh);
            Gone(_material);
        }

        static void Gone(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);   // EditMode tests
        }
    }
}
