using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>PA-07's other blockers, which the app supplies (lakes and roads), in the lattice's frame. Lift terminals are
    /// read from the document.</summary>
    public interface IPathBlockers
    {
        bool Blocks(double x, double y);
    }

    /// <summary>
    /// Task 10's trail geometry for the network (NW-07, PA-05 to PA-07). Every built trail's footprint and centreline
    /// come from the trail alone, with the same code the earthwork runs (TR-02, TR-03), so a load and an edit give the
    /// same answers; each is kept while the trail is the same object.
    /// <list type="bullet">
    /// <item>A head or tail within 30 ft of another trail's centreline joins it at the nearest point (TR-05), unless it
    /// attaches to a built lift's end. Two ends that meet join end to end.</item>
    /// <item>Each separate patch where two footprints overlap gives one junction, on each centreline nearest the patch's
    /// middle.</item>
    /// <item>Where two footprints come within 30 ft without touching, one path joins them at the closest point of each
    /// close stretch, never across a lift terminal's footprint or a blocker (PA-07), running downhill on the natural
    /// ground, or both ways within 3 ft (PA-06).</item>
    /// </list>
    /// Cells are visited in row order and ties go to the lower trail ID, then the lower row and column (SH-04).
    /// Thread-safe: calls take a lock.
    /// </summary>
    public sealed class TrailNetworkGeometry : ITrailGeometry
    {
        /// <summary>TR-05, PA-05: 30 ft.</summary>
        public const double SnapMetres = DesignUnits.SnapMm / 1000.0;
        /// <summary>PA-06: a path runs both ways when its ends are within 3 ft of each other in elevation.</summary>
        public const double BothWaysMetres = 0.9144;
        const int BucketCells = 16;
        /// <summary>Two cells' centres this close or closer (in cells²) are next to each other: the footprints touch.</summary>
        const int TouchSquared = 2;

        readonly EarthworkSite _site;
        readonly IHeightSource _ground;
        readonly IPathBlockers _blockers;
        readonly Func<Trail, TrailBuild> _known;
        readonly Dictionary<Trail, Shape> _shapes = new Dictionary<Trail, Shape>();
        readonly object _gate = new object();

        /// <param name="site">Where the design sits on the edit lattice.</param>
        /// <param name="naturalGround">PA-06's heights: the package's ground before any edit (so a path's direction never
        /// depends on when the grading finished). Null makes every path two-way.</param>
        /// <param name="blockers">Lakes and roads (PA-07), or null.</param>
        /// <param name="known">The earthwork's last build of a trail, to reuse its footprint and centreline; or null.</param>
        public TrailNetworkGeometry(EarthworkSite site, IHeightSource naturalGround = null, IPathBlockers blockers = null, Func<Trail, TrailBuild> known = null)
        {
            _site = site;
            _ground = naturalGround;
            _blockers = blockers;
            _known = known;
        }

        public int CentrelineLengthMm(Trail trail)
        {
            lock (_gate) return ShapeOf(trail).LengthMm;
        }

        /// <summary>The trail's centreline as the network measures it (x, y pairs in the lattice's frame, head first).</summary>
        public IReadOnlyList<double> CentrelineOf(Trail trail)
        {
            lock (_gate) return ShapeOf(trail).Line;
        }

        /// <summary>The point <paramref name="alongMm"/> from the head along the trail's centreline (clamped to its ends).</summary>
        public PointMm PointAlong(Trail trail, int alongMm)
        {
            lock (_gate)
            {
                var s = ShapeOf(trail);
                s.PointAt(alongMm / 1000.0, out double x, out double y);
                return ToPoint(x, y);
            }
        }

        public void FindJunctions(DesignDocument doc, List<TrailJunction> into)
        {
            lock (_gate)
            {
                var shapes = Prepare(doc);
                var liftEnds = LiftEnds(doc);
                var ends = EndJunctions(shapes, liftEnds);
                into.AddRange(ends);
                for (int i = 0; i < shapes.Count; i++)
                    for (int k = i + 1; k < shapes.Count; k++)
                        Overlaps(shapes[i], shapes[k], ends, into);
            }
        }

        public void FindPaths(DesignDocument doc, List<TrailPath> into)
        {
            lock (_gate)
            {
                var shapes = Prepare(doc);
                var liftEnds = LiftEnds(doc);
                var ends = EndJunctions(shapes, liftEnds);
                var joins = new List<TrailJunction>(ends);
                for (int i = 0; i < shapes.Count; i++)
                    for (int k = i + 1; k < shapes.Count; k++)
                        Overlaps(shapes[i], shapes[k], ends, joins);
                var terminals = Terminals(doc);
                for (int i = 0; i < shapes.Count; i++)
                    for (int k = i + 1; k < shapes.Count; k++)
                        Paths(shapes[i], shapes[k], joins, terminals, into);
            }
        }

        // ---- shapes ----

        /// <summary>The trails the network asks about (built, with both ends), by ID; drops shapes of trails gone.</summary>
        List<Shape> Prepare(DesignDocument doc)
        {
            var list = new List<Shape>();
            var keep = new HashSet<Trail>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
                keep.Add(t);
                list.Add(ShapeOf(t));
            }
            if (_shapes.Count > keep.Count)
            {
                var gone = new List<Trail>();
                foreach (var t in _shapes.Keys) if (!keep.Contains(t)) gone.Add(t);
                foreach (var t in gone) _shapes.Remove(t);
            }
            list.Sort((a, b) => a.Trail.Id.CompareTo(b.Trail.Id));
            return list;
        }

        Shape ShapeOf(Trail t)
        {
            if (_shapes.TryGetValue(t, out var s)) return s;
            s = new Shape(t, _site, _known?.Invoke(t));
            _shapes[t] = s;
            return s;
        }

        PointMm ToPoint(double x, double y) => PointMm.FromMetres(x - _site.OriginX, y - _site.OriginY);

        sealed class Shape
        {
            public readonly Trail Trail;
            public readonly TrailFootprint Footprint;
            public readonly double[] Line;
            readonly double[] _along;
            public readonly int LengthMm;
            public readonly double LengthMetres;
            /// <summary>The boundary cells (painted, with a 4-neighbour unpainted), in row order.</summary>
            public readonly int[] EdgeColumn, EdgeRow;
            readonly Dictionary<long, List<int>> _buckets = new Dictionary<long, List<int>>();
            /// <summary>The painted cells' bounds, inclusive.</summary>
            public readonly int MinC, MinR, MaxC, MaxR;
            public readonly double HeadX, HeadY, TailX, TailY;

            public Shape(Trail t, EarthworkSite site, TrailBuild known)
            {
                Trail = t;
                bool reuse = known != null && ReferenceEquals(known.Trail, t);
                Footprint = reuse && known.Footprint != null ? known.Footprint : TrailFootprint.Build(t.Parts, site.Lattice, site.OriginX, site.OriginY);
                site.PointOf(t.Head.Value, out HeadX, out HeadY);
                site.PointOf(t.Tail.Value, out TailX, out TailY);
                TrailCentreline.Result c = reuse && known.CentrelineIsOwn ? known.Centreline : null;
                if (c == null && !Footprint.IsEmpty) c = TrailCentreline.Find(Footprint, HeadX, HeadY, TailX, TailY);
                // No path through the footprint: the network still knows the trail, as a straight line from head to tail.
                Line = c != null && c.Found ? c.Points : new[] { HeadX, HeadY, TailX, TailY };
                _along = new double[Line.Length / 2];
                for (int i = 1; i < _along.Length; i++)
                {
                    double dx = Line[2 * i] - Line[2 * i - 2], dy = Line[2 * i + 1] - Line[2 * i - 1];
                    _along[i] = _along[i - 1] + Math.Sqrt(dx * dx + dy * dy);
                }
                LengthMetres = _along[_along.Length - 1];
                LengthMm = (int)Math.Min(int.MaxValue, Math.Round(LengthMetres * 1000));

                var ec = new List<int>();
                var er = new List<int>();
                var fp = Footprint;
                int minC = int.MaxValue, minR = int.MaxValue, maxC = int.MinValue, maxR = int.MinValue;
                for (int r = fp.Row0; r < fp.Row0 + fp.Height; r++)
                    for (int col = fp.Column0; col < fp.Column0 + fp.Width; col++)
                    {
                        if (!fp.Contains(col, r)) continue;
                        if (col < minC) minC = col;
                        if (col > maxC) maxC = col;
                        if (r < minR) minR = r;
                        if (r > maxR) maxR = r;
                        if (fp.Contains(col - 1, r) && fp.Contains(col + 1, r) && fp.Contains(col, r - 1) && fp.Contains(col, r + 1)) continue;
                        int index = ec.Count;
                        ec.Add(col);
                        er.Add(r);
                        long key = BucketKey(FloorDiv(col, BucketCells), FloorDiv(r, BucketCells));
                        if (!_buckets.TryGetValue(key, out var list)) _buckets.Add(key, list = new List<int>());
                        list.Add(index);
                    }
                EdgeColumn = ec.ToArray();
                EdgeRow = er.ToArray();
                if (ec.Count == 0) { minC = maxC = 0; minR = maxR = 0; }
                MinC = minC; MinR = minR; MaxC = maxC; MaxR = maxR;
            }

            public bool Empty => EdgeColumn.Length == 0;

            /// <summary>Whether the painted boxes come within <paramref name="cells"/> of each other.</summary>
            public bool Near(Shape o, int cells) =>
                !Empty && !o.Empty && MinC - cells <= o.MaxC && o.MinC - cells <= MaxC && MinR - cells <= o.MaxR && o.MinR - cells <= MaxR;

            /// <summary>The nearest boundary cell to (column, row) within the 3 × 3 buckets round it: its index and squared
            /// distance in cells, ties to the lower index; -1 when none.</summary>
            public int NearestEdge(int column, int row, out int distanceSquared)
            {
                int bc = FloorDiv(column, BucketCells), br = FloorDiv(row, BucketCells);
                int best = -1;
                distanceSquared = int.MaxValue;
                for (int j = br - 1; j <= br + 1; j++)
                    for (int i = bc - 1; i <= bc + 1; i++)
                    {
                        if (!_buckets.TryGetValue(BucketKey(i, j), out var list)) continue;
                        foreach (int k in list)
                        {
                            int dc = EdgeColumn[k] - column, dr = EdgeRow[k] - row;
                            int d = dc * dc + dr * dr;
                            if (d < distanceSquared || (d == distanceSquared && k < best))
                            {
                                distanceSquared = d;
                                best = k;
                            }
                        }
                    }
                return best;
            }

            /// <summary>The nearest point on the centreline to (px, py), its distance along from the head and the squared
            /// distance to it; the first segment wins a tie.</summary>
            public void Nearest(double px, double py, out double x, out double y, out double along, out double distanceSquared)
            {
                x = Line[0];
                y = Line[1];
                along = 0;
                distanceSquared = (x - px) * (x - px) + (y - py) * (y - py);
                for (int i = 0; i + 1 < _along.Length; i++)
                {
                    double ax = Line[2 * i], ay = Line[2 * i + 1], bx = Line[2 * i + 2], by = Line[2 * i + 3];
                    double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                    double f = len2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0;
                    f = f < 0 ? 0 : f > 1 ? 1 : f;
                    double qx = ax + f * dx, qy = ay + f * dy;
                    double d = (qx - px) * (qx - px) + (qy - py) * (qy - py);
                    if (d < distanceSquared)
                    {
                        distanceSquared = d;
                        x = qx;
                        y = qy;
                        along = _along[i] + f * (_along[i + 1] - _along[i]);
                    }
                }
            }

            public void PointAt(double along, out double x, out double y)
            {
                if (along <= 0) { x = Line[0]; y = Line[1]; return; }
                for (int i = 0; i + 1 < _along.Length; i++)
                {
                    if (along > _along[i + 1]) continue;
                    double span = _along[i + 1] - _along[i];
                    double f = span > 0 ? (along - _along[i]) / span : 0;
                    x = Line[2 * i] + f * (Line[2 * i + 2] - Line[2 * i]);
                    y = Line[2 * i + 1] + f * (Line[2 * i + 3] - Line[2 * i + 1]);
                    return;
                }
                x = Line[Line.Length - 2];
                y = Line[Line.Length - 1];
            }

            public int AlongMm(double along) => (int)Math.Max(0, Math.Min(LengthMm, Math.Round(along * 1000)));
        }

        static long BucketKey(int i, int j) => ((long)i << 32) ^ (uint)j;

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        // ---- junctions ----

        List<PointMm> LiftEnds(DesignDocument doc)
        {
            var ends = new List<PointMm>();
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Built) continue;
                ends.Add(l.Bottom);
                ends.Add(l.Top);
            }
            return ends;
        }

        static bool OnLiftEnd(List<PointMm> liftEnds, PointMm p)
        {
            foreach (var e in liftEnds) if (e.Within(p, DesignUnits.SnapMm)) return true;
            return false;
        }

        /// <summary>
        /// TR-05: each head and tail (in ID order, head first) that isn't on a lift's end joins the nearest other trail
        /// within 30 ft: end to end when it's that close to one of the other's ends, otherwise at the nearest point on
        /// its centreline. A meeting of two ends is reported once.
        /// </summary>
        List<TrailJunction> EndJunctions(List<Shape> shapes, List<PointMm> liftEnds)
        {
            var found = new List<TrailJunction>();
            double reach2 = SnapMetres * SnapMetres;
            int reachCells = (int)Math.Ceiling(SnapMetres) + 1;
            foreach (var s in shapes)
            {
                for (int e = 0; e < 2; e++)
                {
                    var end = e == 0 ? s.Trail.Head.Value : s.Trail.Tail.Value;
                    if (OnLiftEnd(liftEnds, end)) continue;
                    double px = e == 0 ? s.HeadX : s.TailX, py = e == 0 ? s.HeadY : s.TailY;
                    int along = e == 0 ? 0 : s.LengthMm;
                    Shape best = null;
                    double bestD = double.MaxValue, bestAlong = 0, bestX = 0, bestY = 0;
                    _site.Lattice.CellAt(px, py, out int pc, out int pr);
                    foreach (var o in shapes)
                    {
                        if (o == s) continue;
                        if (!o.Empty && (pc < o.MinC - reachCells || pc > o.MaxC + reachCells || pr < o.MinR - reachCells || pr > o.MaxR + reachCells)
                            && !NearEnd(o, px, py, reach2)) continue;
                        double x, y, a, d;
                        // End to end first: the other's own end, when it's within reach.
                        double dh = (o.HeadX - px) * (o.HeadX - px) + (o.HeadY - py) * (o.HeadY - py);
                        double dt = (o.TailX - px) * (o.TailX - px) + (o.TailY - py) * (o.TailY - py);
                        if (dh <= reach2 || dt <= reach2)
                        {
                            bool head = dh <= dt;
                            x = head ? o.HeadX : o.TailX;
                            y = head ? o.HeadY : o.TailY;
                            a = head ? 0 : o.LengthMetres;
                            d = head ? dh : dt;
                        }
                        else o.Nearest(px, py, out x, out y, out a, out d);
                        if (d > reach2) continue;
                        if (d < bestD) // shapes are in ID order, so a tie keeps the lower ID
                        {
                            best = o;
                            bestD = d;
                            bestAlong = a;
                            bestX = x;
                            bestY = y;
                        }
                    }
                    if (best == null) continue;
                    int otherAlong = best.AlongMm(bestAlong);
                    bool endToEnd = otherAlong == 0 || otherAlong == best.LengthMm;
                    bool seen = false;
                    foreach (var j in found)
                        if (j.A == best.Trail.Id && j.B == s.Trail.Id && j.AlongAMm == otherAlong && j.AlongBMm == along) seen = true;
                    if (seen) continue;
                    // Two ends meet where this one is; an end on a line sits on that line.
                    var at = endToEnd ? end : ToPoint(bestX, bestY);
                    found.Add(new TrailJunction(s.Trail.Id, along, best.Trail.Id, otherAlong, at));
                }
            }
            return found;
        }

        static bool NearEnd(Shape o, double px, double py, double reach2) =>
            (o.HeadX - px) * (o.HeadX - px) + (o.HeadY - py) * (o.HeadY - py) <= reach2
            || (o.TailX - px) * (o.TailX - px) + (o.TailY - py) * (o.TailY - py) <= reach2;

        /// <summary>
        /// NW-07: each separate (4-connected) patch of cells both footprints paint is one junction, on each centreline
        /// nearest the patch's middle; a patch within 30 ft of where the pair already joins end to end or end to line adds
        /// nothing.
        /// </summary>
        void Overlaps(Shape a, Shape b, List<TrailJunction> joins, List<TrailJunction> into)
        {
            if (!a.Near(b, 0)) return;
            var seeds = new List<(int c, int r)>();
            for (int i = 0; i < a.EdgeColumn.Length; i++)
                if (b.Footprint.Contains(a.EdgeColumn[i], a.EdgeRow[i])) seeds.Add((a.EdgeColumn[i], a.EdgeRow[i]));
            for (int i = 0; i < b.EdgeColumn.Length; i++)
                if (a.Footprint.Contains(b.EdgeColumn[i], b.EdgeRow[i])) seeds.Add((b.EdgeColumn[i], b.EdgeRow[i]));
            if (seeds.Count == 0) return;
            seeds.Sort((x, y) => x.r != y.r ? x.r.CompareTo(y.r) : x.c.CompareTo(y.c));
            var seen = new HashSet<long>();
            var queue = new Queue<(int c, int r)>();
            var lat = _site.Lattice;
            foreach (var seed in seeds)
            {
                if (!seen.Add(BucketKey(seed.c, seed.r))) continue;
                queue.Enqueue(seed);
                long cells = 0, sumC = 0, sumR = 0;
                int minC = seed.c, maxC = seed.c, minR = seed.r, maxR = seed.r;
                while (queue.Count > 0)
                {
                    var (c, r) = queue.Dequeue();
                    cells++;
                    sumC += c;
                    sumR += r;
                    if (c < minC) minC = c;
                    if (c > maxC) maxC = c;
                    if (r < minR) minR = r;
                    if (r > maxR) maxR = r;
                    Visit(c + 1, r);
                    Visit(c - 1, r);
                    Visit(c, r + 1);
                    Visit(c, r - 1);
                }
                double mx = lat.West + (double)sumC / cells + 0.5, my = lat.North - (double)sumR / cells - 0.5;
                if (JoinedNear(a, b, joins, lat.West + minC - SnapMetres, lat.North - maxR - 1 - SnapMetres,
                               lat.West + maxC + 1 + SnapMetres, lat.North - minR + SnapMetres)) continue;
                a.Nearest(mx, my, out double ax, out double ay, out double alongA, out _);
                b.Nearest(mx, my, out double bx, out double by, out double alongB, out _);
                into.Add(new TrailJunction(a.Trail.Id, a.AlongMm(alongA), b.Trail.Id, b.AlongMm(alongB), ToPoint((ax + bx) / 2, (ay + by) / 2)));
            }

            void Visit(int c, int r)
            {
                if (!a.Footprint.Contains(c, r) || !b.Footprint.Contains(c, r)) return;
                if (seen.Add(BucketKey(c, r))) queue.Enqueue((c, r));
            }
        }

        /// <summary>Whether one of <paramref name="joins"/> joins the pair inside the box (lattice frame).</summary>
        bool JoinedNear(Shape a, Shape b, List<TrailJunction> joins, double west, double south, double east, double north)
        {
            foreach (var j in joins)
            {
                if (!(j.A == a.Trail.Id && j.B == b.Trail.Id) && !(j.A == b.Trail.Id && j.B == a.Trail.Id)) continue;
                _site.PointOf(j.At, out double x, out double y);
                if (x >= west && x <= east && y >= south && y <= north) return true;
            }
            return false;
        }

        // ---- paths ----

        /// <summary>A built lift terminal's measured footprint (PA-07), from its bullwheel along the line.</summary>
        readonly struct Terminal
        {
            public readonly double X, Y, Ux, Uy, Behind, Ahead, HalfWidth;
            public Terminal(double x, double y, double ux, double uy, double behind, double ahead, double halfWidth)
            {
                X = x; Y = y; Ux = ux; Uy = uy; Behind = behind; Ahead = ahead; HalfWidth = halfWidth;
            }

            public bool Covers(double x, double y)
            {
                double dx = x - X, dy = y - Y;
                double s = dx * Ux + dy * Uy, t = Math.Abs(-dx * Uy + dy * Ux);
                return s >= -Behind && s <= Ahead && t <= HalfWidth;
            }
        }

        List<Terminal> Terminals(DesignDocument doc)
        {
            var list = new List<Terminal>();
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Built) continue;
                var type = LiftType.Of(l.CatalogId);
                _site.PointOf(l.Bottom, out double bx, out double by);
                _site.PointOf(l.Top, out double tx, out double ty);
                double len = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
                if (len <= 0 || type == null) continue;
                double ux = (tx - bx) / len, uy = (ty - by) / len;
                if (type.Bottom != null) list.Add(new Terminal(bx, by, ux, uy, type.Bottom.Behind, type.Bottom.Ahead, type.Bottom.HalfWidth));
                if (type.Top != null) list.Add(new Terminal(tx, ty, -ux, -uy, type.Top.Behind, type.Top.Ahead, type.Top.HalfWidth));
            }
            return list;
        }

        bool Blocked(double ax, double ay, double bx, double by, List<Terminal> terminals)
        {
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int q = 0; q <= steps; q++)
            {
                double x = ax + (bx - ax) * q / steps, y = ay + (by - ay) * q / steps;
                foreach (var t in terminals) if (t.Covers(x, y)) return true;
                if (_blockers != null && _blockers.Blocks(x, y)) return true;
            }
            return false;
        }

        /// <summary>
        /// PA-05: a's boundary cells within 30 ft of b's footprint (and not on it) form close stretches (8-connected);
        /// a stretch where the two touch, or near where they already join, adds nothing; each other stretch gets one
        /// path at its closest cell pair that PA-07 allows (nearest first), or none when PA-07 rules them all out.
        /// </summary>
        void Paths(Shape a, Shape b, List<TrailJunction> joins, List<Terminal> terminals, List<TrailPath> into)
        {
            int reachCells = (int)Math.Ceiling(SnapMetres) + 1;
            if (!a.Near(b, reachCells)) return;
            double limit = SnapMetres + 1;   // centre to centre: the gap between the cells' edges is a cell less
            int limit2 = (int)Math.Floor(limit * limit);
            var close = new Dictionary<long, int>();   // a's boundary cell → its index in the lists below
            var cells = new List<(int c, int r, int d2, int other)>();
            for (int i = 0; i < a.EdgeColumn.Length; i++)
            {
                int c = a.EdgeColumn[i], r = a.EdgeRow[i];
                if (c < b.MinC - reachCells || c > b.MaxC + reachCells || r < b.MinR - reachCells || r > b.MaxR + reachCells) continue;
                int k = -1, d2 = 0;
                if (!b.Footprint.Contains(c, r))
                {
                    k = b.NearestEdge(c, r, out d2);
                    if (k < 0 || d2 > limit2) continue;
                }
                close[BucketKey(c, r)] = cells.Count;
                cells.Add((c, r, d2, k));
            }
            if (cells.Count == 0) return;

            var lat = _site.Lattice;
            double maxClear = Math.Max(a.Footprint.MaxClearance, b.Footprint.MaxClearance);
            var done = new bool[cells.Count];
            var stack = new Stack<int>();
            for (int s0 = 0; s0 < cells.Count; s0++)
            {
                if (done[s0]) continue;
                var stretch = new List<int>();
                done[s0] = true;
                stack.Push(s0);
                bool touches = false;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    stretch.Add(n);
                    var (c, r, d2, _) = cells[n];
                    if (d2 <= TouchSquared) touches = true;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                            if ((dc != 0 || dr != 0) && close.TryGetValue(BucketKey(c + dc, r + dr), out int m) && !done[m])
                            {
                                done[m] = true;
                                stack.Push(m);
                            }
                }
                if (touches) continue;
                stretch.Sort((x, y) =>
                {
                    var p = cells[x];
                    var q = cells[y];
                    return p.d2 != q.d2 ? p.d2.CompareTo(q.d2) : p.r != q.r ? p.r.CompareTo(q.r) : p.c.CompareTo(q.c);
                });
                // Where the pair already joins, a path beside the junction adds nothing.
                var first = cells[stretch[0]];
                double fx = lat.West + first.c + 0.5, fy = lat.North - first.r - 0.5, near = 2 * SnapMetres + maxClear;
                if (JoinedNear(a, b, joins, fx - near, fy - near, fx + near, fy + near)) continue;
                for (int t = 0; t < stretch.Count; t++)
                {
                    var (c, r, _, other) = cells[stretch[t]];
                    double ax = lat.West + c + 0.5, ay = lat.North - r - 0.5;
                    double bx = lat.West + b.EdgeColumn[other] + 0.5, by = lat.North - b.EdgeRow[other] - 0.5;
                    if (Blocked(ax, ay, bx, by, terminals)) continue;
                    a.Nearest(ax, ay, out _, out _, out double alongA, out _);
                    b.Nearest(bx, by, out _, out _, out double alongB, out _);
                    into.Add(new TrailPath(a.Trail.Id, a.AlongMm(alongA), ToPoint(ax, ay), b.Trail.Id, b.AlongMm(alongB), ToPoint(bx, by), Direction(ax, ay, bx, by)));
                    break;
                }
            }
        }

        PathDirection Direction(double ax, double ay, double bx, double by)
        {
            if (_ground == null) return PathDirection.Both;
            double za = _ground.At(ax, ay), zb = _ground.At(bx, by);
            if (double.IsNaN(za) || double.IsNaN(zb) || Math.Abs(za - zb) <= BothWaysMetres) return PathDirection.Both;
            return za > zb ? PathDirection.AToB : PathDirection.BToA;
        }
    }
}
