using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// The Network view's diagram (task P3-10; the approved mockup, owner findings 1–7 and R2-1–R2-3), drawn over a dark
    /// relief while Analysis › Network is open: runs pale with chevrons pointing downhill, lifts red with chevrons
    /// pointing up, a stretch no lift serves amber and dashed, a run that leads nowhere ending in an amber bar, paths
    /// dashed white, and every node a circle (junctions large and white, lift ends red, path ends small, a run's own
    /// start or end open). A pick (a trail segment, a lift or a node) lights what a skier can reach from it in each
    /// thing's own colour, the picked one wider with a white glow, the rest grey; hover widens only the segment or node
    /// under the pointer.
    /// <para>
    /// Everything is one mesh in draw order, coloured per vertex, its widths in screen pixels: rebuilt only when the
    /// camera moves or the state changes, into pooled lists (nothing allocated per frame).
    /// </para>
    /// </summary>
    public sealed class NetworkView : IDisposable
    {
        public const string ShaderResource = "MountainPlannerDesign/NetworkLine";

        public enum NodeKind : byte { LiftEnd = 1, Junction = 2, PathEnd = 3, RunEnd = 4 }

        /// <summary>A trail segment (NW-08): its edge in the graph, its trail, its points (local metres, draped) and its
        /// trail's own colour (its rating's).</summary>
        public struct Segment
        {
            public int Edge;
            public ulong Trail;
            public Vector3[] Points;
            public Color Own;
            public bool Served;
        }

        public struct LiftLine
        {
            public ulong Id;
            public Vector3 Bottom, Top;
            public bool Connected;
        }

        public struct Node
        {
            public ulong Id;
            public NodeKind Kind;
            /// <summary>For a lift end: the lift, and whether it's the bottom.</summary>
            public ulong Lift;
            public bool Bottom;
            /// <summary>A run's own end that leads nowhere: ringed amber.</summary>
            public bool DeadEnd;
            public Vector3 At;
        }

        public struct Link { public Vector3 A, B; }

        /// <summary>One terminal's part of a station's area (NW-14, NT15): its station, its lift, the terminal and the
        /// area's rim draped over the ground (closed: the last point repeats the first).</summary>
        public struct StationArea
        {
            public ulong Station, Lift;
            /// <summary>A terminal being placed (NT16): a blue area in a dashed white ring, lit whatever the pick.</summary>
            public bool Candidate;
            public Vector3 Centre;
            public Vector3[] Rim;
        }

        /// <summary>A dead end's bar: where the run ends, and its direction there.</summary>
        public struct DeadEnd { public Vector3 At, Dir; }

        /// <summary>What's under a point on screen: a node first, else a lift, else a trail segment.</summary>
        public struct Hit
        {
            public ulong Node, Lift, Trail;
            public int Edge;
            public bool Any => Node != 0 || Lift != 0 || Edge >= 0;
        }

        // The mockup's colours (sRGB; turned linear for the vertices).
        static readonly Color Lift = Hex(0xe0483a), Run = Hex(0xdfe8ee), Warn = Hex(0xf0b43c), Off = Hex(0x6d7378);
        static readonly Color Halo = new Color(12 / 255f, 14 / 255f, 16 / 255f, 0.85f), White = Color.white;
        // A station's area (NT15): lit while it's picked or reached, grey under someone else's pick.
        static readonly Color AreaRing = WithAlpha(Hex(0xff8f80), 0.8f), AreaFill = WithAlpha(Hex(0x4a2622), 0.8f);
        static readonly Color AreaRingOff = WithAlpha(Hex(0x77736d), 0.5f), AreaFillOff = WithAlpha(Hex(0x2a2a2a), 0.5f);
        static readonly Color CandidateFill = WithAlpha(Hex(0x3aa0ff), 0.28f), CandidateEdge = Hex(0x123a66);
        const float NodePickPixels = 8, LinePickPixels = 8;

        readonly GameObject _root;
        readonly Mesh _mesh;
        readonly Material _material;
        readonly List<Vector3> _vertices = new List<Vector3>(16384);
        readonly List<Color32> _colours = new List<Color32>(16384);
        readonly List<int> _triangles = new List<int>(32768);

        Segment[] _segments = Array.Empty<Segment>();
        LiftLine[] _lifts = Array.Empty<LiftLine>();
        Node[] _nodes = Array.Empty<Node>();
        Link[] _links = Array.Empty<Link>();
        DeadEnd[] _deadEnds = Array.Empty<DeadEnd>();
        StationArea[] _areas = Array.Empty<StationArea>();

        int _pickEdge = -1, _hoverEdge = -1;
        ulong _pickLift, _pickNode, _hoverLift, _hoverNode, _rowTrail;
        readonly HashSet<int> _reachSegments = new HashSet<int>();
        readonly HashSet<ulong> _reachLifts = new HashSet<ulong>();
        readonly HashSet<ulong> _reachNodes = new HashSet<ulong>(), _litAreas = new HashSet<ulong>();
        bool _picked, _dirty = true, _visible;
        Vector3 _lastEye;
        Quaternion _lastTurn;
        float _lastFov;
        int _lastHeight;

        public NetworkView(Transform parent, string name = "Network view")
        {
            _root = new GameObject(name);
            _root.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "Network diagram", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            var shader = Resources.Load<Shader>(ShaderResource);
            _material = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Unlit")) { name = "Network diagram" };
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

        /// <summary>The vertex count of the last build (tests).</summary>
        public int VertexCount => _vertices.Count;

        /// <summary>The network to draw (rebuilt with the design, never per frame).</summary>
        public void Set(Segment[] segments, LiftLine[] lifts, Node[] nodes, Link[] links, DeadEnd[] deadEnds, StationArea[] areas = null)
        {
            _areas = areas ?? Array.Empty<StationArea>();
            _segments = segments ?? Array.Empty<Segment>();
            _lifts = lifts ?? Array.Empty<LiftLine>();
            _nodes = nodes ?? Array.Empty<Node>();
            _links = links ?? Array.Empty<Link>();
            _deadEnds = deadEnds ?? Array.Empty<DeadEnd>();
            _dirty = true;
        }

        /// <summary>
        /// The lift tool's catchment (NT16), drawn on the trail map: the built terminals' areas faint, the terminals in
        /// hand as <see cref="StationArea.Candidate"/> areas, and the trails they'd gather lit in their own colours.
        /// </summary>
        public void SetCatchment(Segment[] gathered, StationArea[] areas)
        {
            Set(gathered, null, null, null, null, areas);
            var edges = new List<int>();
            foreach (var s in _segments) edges.Add(s.Edge);
            SetPick(-1, 0, 0, edges, null);
            _picked = true;   // the built areas faint, the gathered trails lit
        }

        /// <summary>The pick and what it reaches; nothing picked when every pick is empty.</summary>
        public void SetPick(int edge, ulong lift, ulong node, IEnumerable<int> segments, IEnumerable<ulong> lifts, IEnumerable<ulong> nodes = null)
        {
            _reachNodes.Clear();
            if (nodes != null) foreach (ulong n in nodes) _reachNodes.Add(n);
            _pickEdge = edge;
            _pickLift = lift;
            _pickNode = node;
            _picked = edge >= 0 || lift != 0 || node != 0;
            _reachSegments.Clear();
            _reachLifts.Clear();
            if (segments != null) foreach (int s in segments) _reachSegments.Add(s);
            if (lifts != null) foreach (ulong l in lifts) _reachLifts.Add(l);
            _dirty = true;
        }

        /// <summary>The hovered segment, lift or node (R2-3), or a whole trail from its list row.</summary>
        public void SetHover(int edge, ulong lift, ulong node, ulong rowTrail)
        {
            if (edge == _hoverEdge && lift == _hoverLift && node == _hoverNode && rowTrail == _rowTrail) return;
            _hoverEdge = edge;
            _hoverLift = lift;
            _hoverNode = node;
            _rowTrail = rowTrail;
            _dirty = true;
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

            // Station areas (NT15), under everything: every terminal's rim first, then the fills over them, so a shared
            // base reads as one area with one outline.
            _litAreas.Clear();
            foreach (var a in _areas)
                if (!_picked || a.Station == _pickNode || _reachNodes.Contains(a.Station) || (a.Lift != 0 && a.Lift == _pickLift)) _litAreas.Add(a.Station);
            foreach (var a in _areas)
                if (!a.Candidate && a.Rim != null && a.Rim.Length > 2) Ribbon(a.Rim, 3.2f, _litAreas.Contains(a.Station) ? AreaRing : AreaRingOff, 0, 0);
            foreach (var a in _areas)
                if (!a.Candidate && a.Rim != null && a.Rim.Length > 2) Fan(a.Centre, a.Rim, _litAreas.Contains(a.Station) ? AreaFill : AreaFillOff);
            foreach (var a in _areas)
            {
                if (!a.Candidate || a.Rim == null || a.Rim.Length <= 2) continue;
                Fan(a.Centre, a.Rim, CandidateFill);
                Ribbon(a.Rim, 3.8f, CandidateEdge, 0, 0);
                Ribbon(a.Rim, 1.8f, White, 5, 3);
            }

            // Runs: the base line (grey under a pick; pale, or amber dashed where no lift serves it).
            foreach (var s in _segments)
            {
                if (s.Points == null || s.Points.Length < 2) continue;
                float w = _rowTrail == s.Trail ? 4.4f : 2.6f;
                Color c = _picked ? Off : s.Served ? Run : Warn;
                Ribbon(s.Points, w + 3, Halo, 0, 0);
                if (!_picked && !s.Served) Ribbon(s.Points, w, c, 7, 4);
                else Ribbon(s.Points, w, c, 0, 0);
                Chevrons(s.Points, c);
            }
            // What the pick reaches, in each trail's own colour; the picked segment wider with the glow.
            if (_picked)
                foreach (var s in _segments)
                    if (s.Points != null && s.Points.Length >= 2 && (_reachSegments.Contains(s.Edge) || s.Edge == _pickEdge))
                        Lit(s.Points, s.Own, s.Edge == _pickEdge);
            // The hovered segment alone, wider (R2-3).
            foreach (var s in _segments)
            {
                if (s.Edge != _hoverEdge || s.Points == null || s.Points.Length < 2) continue;
                bool lit = _picked && (_reachSegments.Contains(s.Edge) || s.Edge == _pickEdge);
                Color c = lit ? s.Own : _picked ? Off : s.Served ? Run : Warn;
                Ribbon(s.Points, lit ? 11 : 8, new Color(1, 1, 1, 0.85f), 0, 0);
                Ribbon(s.Points, lit ? 5 : 4.4f, c, 0, 0);
                Chevrons(s.Points, lit ? White : c);
            }
            // Paths (PA-08): short dashed white links.
            foreach (var l in _links)
            {
                var pts = Two(l.A, l.B);
                Ribbon(pts, 5, Halo, 0, 0);
                Ribbon(pts, 1.8f, White, 2.4f, 2.2f);
            }
            // Lifts: red with chevrons up; reached or picked, lit; under someone else's pick, grey.
            foreach (var l in _lifts)
            {
                var pts = Two(l.Bottom, l.Top);
                bool picked = _pickLift == l.Id, reached = _picked && !picked && _reachLifts.Contains(l.Id);
                if (picked || reached) Lit(pts, Lift, picked, 40);
                else
                {
                    float w = _hoverLift == l.Id ? 5 : 3.4f;
                    Color c = _picked ? Off : Lift;
                    Ribbon(pts, w + 3, Halo, 0, 0);
                    Ribbon(pts, w, c, 0, 0);
                    Chevrons(pts, White, 40);
                }
                if (!l.Connected) Ring(l.Top, 11, 2.4f, Warn);   // NW-12: no way down
            }
            // Nodes: every one a circle (finding 1).
            foreach (var n in _nodes)
            {
                float r;
                Color fill, ring;
                switch (n.Kind)
                {
                    case NodeKind.LiftEnd:
                        r = 6.5f;
                        fill = !_picked || _pickLift == n.Lift || (n.Bottom && _reachLifts.Contains(n.Lift)) ? Lift : Off;
                        ring = White;
                        break;
                    case NodeKind.Junction: r = 6.5f; fill = White; ring = Halo; break;
                    case NodeKind.PathEnd: r = 4; fill = White; ring = Halo; break;
                    default: r = 4.5f; fill = Halo; ring = n.DeadEnd ? Warn : Run; break;
                }
                if (_pickNode == n.Id) Disc(n.At, r + 8, new Color(1, 1, 1, 0.32f));
                Disc(n.At, r + 1.6f, Halo);
                Disc(n.At, r, ring);
                Disc(n.At, r - 2, fill);
                if (_hoverNode == n.Id && _pickNode != n.Id) Ring(n.At, r + 4.5f, 2.2f, new Color(1, 1, 1, 0.9f));
            }
            // Dead ends: an amber bar across the tail.
            foreach (var d in _deadEnds)
            {
                var side = Vector3.Cross(Vector3.up, d.Dir).normalized * (7 * Px(d.At));
                Ribbon(Two(d.At - side, d.At + side), 3.4f, Warn, 0, 0);
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colours);
            _mesh.SetTriangles(_triangles, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        readonly Vector3[] _two = new Vector3[2];
        Vector3[] Two(Vector3 a, Vector3 b) { _two[0] = a; _two[1] = b; return _two; }

        /// <summary>A lit line in its own colour (R2-1): a dark edge, a white casing, the colour, white chevrons; the picked
        /// one wider, with a soft white glow.</summary>
        void Lit(Vector3[] pts, Color own, bool picked, float chevronStep = 34)
        {
            float w = picked ? 5 : 3.4f;
            if (picked) Ribbon(pts, 17, new Color(1, 1, 1, 0.32f), 0, 0);
            Ribbon(pts, w + 6.5f, Halo, 0, 0);
            Ribbon(pts, w + 3.4f, White, 0, 0);
            Ribbon(pts, w, own, 0, 0);
            Chevrons(pts, White, chevronStep);
        }

        /// <summary>A screen-width ribbon along the points, round at its joints; dashed when <paramref name="dash"/> &gt; 0
        /// (pixels on and off).</summary>
        void Ribbon(Vector3[] pts, float widthPx, Color colour, float dash, float gap)
        {
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1];
                float len = Vector3.Distance(a, b);
                if (len < 1e-4f) continue;
                if (dash <= 0)
                {
                    Quad(a, b, widthPx, c);
                    continue;
                }
                // Dashes in pixels at this stretch's distance, counted from its start (a whole number of steps, so the
                // loop always ends, whatever the rounding).
                float px = Px((a + b) * 0.5f), on = Mathf.Max(dash * px, 1e-3f), period = on + Mathf.Max(gap * px, 1e-3f);
                int count = Mathf.Min(4096, Mathf.CeilToInt(len / period));
                for (int k = 0; k < count; k++)
                {
                    float s0 = k * period, s1 = Mathf.Min(len, s0 + on);
                    if (s1 > s0) Quad(Vector3.Lerp(a, b, s0 / len), Vector3.Lerp(a, b, s1 / len), widthPx, c);
                }
            }
            // Round joints and ends.
            if (dash <= 0) foreach (var p in pts) DiscRaw(p, widthPx * 0.5f, c, 8);
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

        /// <summary>Small V marks every <paramref name="stepPx"/> pixels along the line, pointing along it.</summary>
        void Chevrons(Vector3[] pts, Color colour, float stepPx = 34)
        {
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
            float due = 0.5f;   // the part of a step still to go before the next mark: the first is half a step in
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Vector3 a = pts[i], b = pts[i + 1];
                float len = Vector3.Distance(a, b);
                if (len < 1e-4f) continue;
                var mid = (a + b) * 0.5f;
                float px = Px(mid), step = Mathf.Max(stepPx * px, 1e-3f);
                var u = (b - a) / len;
                var side = Vector3.Cross(u, _eye - mid).normalized;
                // A whole number of marks, so the loop always ends.
                float first = due * step;
                int count = first < len ? Mathf.Min(4096, Mathf.FloorToInt((len - first) / step) + 1) : 0;
                for (int k = 0; k < count; k++)
                {
                    var tip = a + u * (first + k * step);
                    var back = tip - u * (4.2f * px);
                    Quad(back - side * (3.6f * px), tip, 1.8f, c);
                    Quad(back + side * (3.6f * px), tip, 1.8f, c);
                }
                due = Mathf.Repeat((first + count * step - len) / step, 1f);
            }
        }

        /// <summary>A filled area in world units: a fan from <paramref name="centre"/> to each rim point.</summary>
        void Fan(Vector3 centre, Vector3[] rim, Color colour)
        {
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
            int v0 = _vertices.Count;
            _vertices.Add(centre);
            _colours.Add(c);
            foreach (var p in rim) { _vertices.Add(p); _colours.Add(c); }
            for (int k = 0; k + 1 < rim.Length; k++)
            {
                _triangles.Add(v0);
                _triangles.Add(v0 + 1 + k);
                _triangles.Add(v0 + 2 + k);
            }
        }

        void Disc(Vector3 at, float radiusPx, Color colour)
        {
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
            DiscRaw(at, radiusPx, c, 20);
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
            Color32 c = colour.linear;
            c.a = (byte)Mathf.RoundToInt(colour.a * 255);
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

        // ---------- picking (findings 2, 7; R2-2, R2-3) ----------

        /// <summary>What's under <paramref name="screen"/> (pixels): a node within 8 px first, else a lift, else the nearest
        /// trail segment within 8 px. Nothing allocated.</summary>
        public Hit Pick(Camera camera, Vector2 screen)
        {
            var hit = new Hit { Edge = -1 };
            if (camera == null) return hit;
            float best = NodePickPixels * NodePickPixels;
            foreach (var n in _nodes)
            {
                var p = camera.WorldToScreenPoint(n.At);
                if (p.z <= 0) continue;
                float d = (new Vector2(p.x, p.y) - screen).sqrMagnitude;
                if (d <= best) { best = d; hit.Node = n.Id; }
            }
            if (hit.Node != 0) return hit;
            best = LinePickPixels * LinePickPixels;
            foreach (var l in _lifts)
            {
                float d = ScreenDistance(camera, l.Bottom, l.Top, screen);
                if (d <= best) { best = d; hit.Lift = l.Id; }
            }
            if (hit.Lift != 0) return hit;
            best = LinePickPixels * LinePickPixels;
            foreach (var s in _segments)
            {
                if (s.Points == null) continue;
                for (int i = 0; i + 1 < s.Points.Length; i++)
                {
                    float d = ScreenDistance(camera, s.Points[i], s.Points[i + 1], screen);
                    if (d <= best) { best = d; hit.Edge = s.Edge; hit.Trail = s.Trail; }
                }
            }
            return hit;
        }

        static float ScreenDistance(Camera camera, Vector3 a, Vector3 b, Vector2 screen)
        {
            var pa = camera.WorldToScreenPoint(a);
            var pb = camera.WorldToScreenPoint(b);
            if (pa.z <= 0 || pb.z <= 0) return float.MaxValue;
            var A = new Vector2(pa.x, pa.y);
            var B = new Vector2(pb.x, pb.y);
            var ab = B - A;
            float t = ab.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(screen - A, ab) / ab.sqrMagnitude) : 0;
            return (A + ab * t - screen).sqrMagnitude;
        }

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
        static Color WithAlpha(Color c, float a) { c.a = a; return c; }

        /// <summary>A trail's own colour from its rating (TD-03 bands), as the trail map draws it.</summary>
        public static Color RatingColour(double percent) =>
            double.IsNaN(percent) || percent < 25 ? Hex(0x1f8a4c) : percent < 40 ? Hex(0x2f6bb0) : Hex(0x1f1c19);

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
