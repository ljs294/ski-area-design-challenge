using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail's clearing (task P3-10, sections finding 7; save v8, Shape 8): what a built or planned trail keeps instead of
    /// the strokes it was painted with. It is its <see cref="TrailMask"/> (250 mm cells in the design's frame), written
    /// down as closed rings of whole-millimetre points: the cells' outline, traced and then straightened wherever a
    /// straight edge passes the same cells. A cell is in when its centre lies inside the rings by the even-odd rule, an
    /// exact test in whole millimetres, so the rings give back the same cells every time; the rings are a function of the
    /// cells alone (canonical), so equal clearings have equal bytes. Strokes live only in the trail tool while painting.
    /// Immutable; deterministic (SH-04).
    /// </summary>
    public sealed class TrailShape : IEquatable<TrailShape>
    {
        public TrailMask Mask { get; }

        /// <summary>
        /// Outer rings anticlockwise and holes clockwise (the clearing on the left), each from its lowest point. Worked out
        /// the first time they're read (once, whichever thread reads them): painting needs only the cells.
        /// </summary>
        public IReadOnlyList<PointMm[]> Rings
        {
            get
            {
                lock (_gate)
                {
                    if (_rings == null)
                    {
                        _rings = Mask.IsEmpty ? Array.Empty<PointMm[]>() : Outline(Mask);
                        int n = 0;
                        foreach (var r in _rings) n += r.Length;
                        _points = n;
                    }
                    return _rings;
                }
            }
        }

        readonly object _gate = new object();
        PointMm[][] _rings;
        int _points;

        public static readonly TrailShape Empty = new TrailShape(TrailMask.Empty);

        TrailShape(TrailMask mask) { Mask = mask; }

        public bool IsEmpty => Mask.IsEmpty;
        /// <summary>Every ring's points.</summary>
        public int Points { get { _ = Rings; return _points; } }

        public bool Equals(TrailShape o) => o is object && Mask.Equals(o.Mask);
        public override bool Equals(object obj) => obj is TrailShape o && Equals(o);
        public override int GetHashCode() => Mask.GetHashCode();

        public static TrailShape Of(TrailMask mask) => mask.IsEmpty ? Empty : new TrailShape(mask);

        /// <summary>Strokes painted (and erased) in order onto <paramref name="baseShape"/> (null for a new trail).</summary>
        public static TrailShape FromStrokes(TrailShape baseShape, IReadOnlyList<Stroke> strokes)
        {
            var mask = baseShape?.Mask ?? TrailMask.Empty;
            if (strokes == null || strokes.Count == 0) return baseShape ?? Empty;
            int row0 = mask.IsEmpty ? int.MaxValue : mask.Row0, rowEnd = mask.IsEmpty ? int.MinValue : mask.RowEnd;
            foreach (var s in strokes) if (!s.Erase) TrailMask.Builder.RowsOf(s, ref row0, ref rowEnd);
            if (row0 >= rowEnd) return Empty;
            var b = new TrailMask.Builder(row0, rowEnd);
            b.Or(mask);
            foreach (var s in strokes) b.Stamp(s);
            return Of(b.ToMask());
        }

        /// <summary>
        /// A legacy footprint (saves before v8): each part's strokes in order, clipped by its cuts, the parts unioned, as
        /// the 1 m footprint read them (TR-02).
        /// </summary>
        public static TrailShape FromParts(IReadOnlyList<FootprintPart> parts)
        {
            if (parts == null || parts.Count == 0) return Empty;
            int row0 = int.MaxValue, rowEnd = int.MinValue;
            foreach (var p in parts) foreach (var s in p.Strokes) if (!s.Erase) TrailMask.Builder.RowsOf(s, ref row0, ref rowEnd);
            if (row0 >= rowEnd) return Empty;
            var all = TrailMask.Empty;
            foreach (var p in parts)
            {
                var b = new TrailMask.Builder(row0, rowEnd);
                foreach (var s in p.Strokes) b.Stamp(s);
                foreach (var c in p.Cuts) b.Clip(c);
                all = all.Union(b.ToMask());
            }
            return Of(all);
        }

        /// <summary>Rings read back (a save): the cells inside them, written down canonically.</summary>
        public static TrailShape FromRings(IReadOnlyList<PointMm[]> rings) => Of(Fill(rings));

        public TrailShape Clip(Cut cut) => Of(Mask.Clip(cut));
        public TrailShape Union(TrailShape other) => other == null ? this : Of(Mask.Union(other.Mask));

        /// <summary>The design-frame box (mm) the clearing's cells cover.</summary>
        public void Bounds(out int minX, out int minY, out int maxX, out int maxY)
        {
            minX = (int)TrailMask.EdgeMm(Mask.Column0);
            maxX = (int)TrailMask.EdgeMm(Mask.ColumnEnd);
            minY = (int)TrailMask.EdgeMm(Mask.Row0);
            maxY = (int)TrailMask.EdgeMm(Mask.RowEnd);
        }

        // ---------- rings to cells ----------

        /// <summary>
        /// The cells whose centres lie inside <paramref name="rings"/> by the even-odd rule: an edge toggles every centre
        /// strictly right of where it crosses the centre's row. Exact in longs.
        /// </summary>
        public static TrailMask Fill(IReadOnlyList<PointMm[]> rings)
        {
            if (rings == null || rings.Count == 0) return TrailMask.Empty;
            long minY = long.MaxValue, maxY = long.MinValue;
            foreach (var ring in rings) foreach (var p in ring) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            if (minY >= maxY) return TrailMask.Empty;
            int row0 = (int)TrailMask.FloorDiv(minY, TrailMask.CellMm), rowEnd = (int)TrailMask.FloorDiv(maxY, TrailMask.CellMm) + 1;
            var toggles = new List<int>[rowEnd - row0];
            foreach (var ring in rings)
            {
                int n = ring.Length;
                for (int k = 0; k < n; k++)
                {
                    var p = ring[k];
                    var q = ring[(k + 1) % n];
                    if (p.Y == q.Y) continue;
                    long y0 = Math.Min(p.Y, q.Y), y1 = Math.Max(p.Y, q.Y);
                    // Rows whose centre cy has y0 ≤ cy < y1 (half-open, so a vertex is counted once).
                    int j0 = (int)TrailMask.CeilDiv(y0, TrailMask.CellMm);
                    int j1 = (int)TrailMask.CeilDiv(y1, TrailMask.CellMm) - 1;
                    long den = (long)q.Y - p.Y, dxe = (long)q.X - p.X;
                    for (int j = j0; j <= j1; j++)
                    {
                        long cy = TrailMask.CentreMm(j);
                        // The crossing x = p.X + (cy − p.Y)·dxe / den; the first centre strictly right of it.
                        long num = (cy - p.Y) * dxe, d = den;
                        if (d < 0) { num = -num; d = -d; }
                        // centre(i) − p.X > num / d  ⇔  250·i > num/d + p.X  ⇔  i > (num + p.X·d) / (250·d)
                        long first = TrailMask.FloorDiv(num + (long)p.X * d, TrailMask.CellMm * d) + 1;
                        var list = toggles[j - row0] ?? (toggles[j - row0] = new List<int>());
                        list.Add((int)first);
                    }
                }
            }
            var b = new TrailMask.Builder(row0, rowEnd);
            for (int k = 0; k < toggles.Length; k++)
            {
                var list = toggles[k];
                if (list == null) continue;
                list.Sort();
                for (int s = 0; s + 1 < list.Count; s += 2) b.Set(row0 + k, list[s], list[s + 1], true);
            }
            return b.ToMask();
        }

        // ---------- cells to rings ----------

        // Directions of a unit step along cell edges: east, north, west, south (left of each is the next).
        static readonly int[] StepX = { 1, 0, -1, 0 };
        static readonly int[] StepY = { 0, 1, 0, -1 };

        /// <summary>
        /// The mask's outline as rings: its cell edges traced with the clearing on the left (a pinch point, where two cells
        /// meet only at a corner, turns left so they stay apart, as 4-connected patches), then straightened. If a
        /// straightened ring would change a cell, the traced edges are kept (corners only), which are always exact.
        /// </summary>
        static PointMm[][] Outline(TrailMask mask)
        {
            var traced = Trace(mask);
            var straight = new PointMm[traced.Count][];
            var corners = new PointMm[traced.Count][];
            bool ok = true;
            for (int k = 0; k < traced.Count; k++)
            {
                straight[k] = Straighten(traced[k]);
                corners[k] = Corners(traced[k]);
                if (straight[k] == null || straight[k].Length < 3) ok = false;
            }
            if (ok)
            {
                Array.Sort(straight, CompareRings);
                if (Fill(straight).Equals(mask)) return straight;
            }
            Array.Sort(corners, CompareRings);
            return corners;
        }

        static int CompareRings(PointMm[] a, PointMm[] b)
        {
            int c = a[0].Y.CompareTo(b[0].Y);
            if (c != 0) return c;
            c = a[0].X.CompareTo(b[0].X);
            if (c != 0) return c;
            c = a.Length.CompareTo(b.Length);
            if (c != 0) return c;
            for (int i = 1; i < a.Length; i++)
            {
                c = a[i].Y.CompareTo(b[i].Y);
                if (c != 0) return c;
                c = a[i].X.CompareTo(b[i].X);
                if (c != 0) return c;
            }
            return 0;
        }

        /// <summary>A traced ring: its start corner (cell-corner indices) and its unit steps.</summary>
        sealed class Traced
        {
            public int X0, Y0;
            public byte[] Steps;
        }

        static List<Traced> Trace(TrailMask mask)
        {
            // Every boundary unit edge, keyed by its start corner: up to two leave a corner (at a pinch).
            var outs = new Dictionary<long, int>();   // corner → bit per direction leaving it
            var starts = new List<long>();
            void Edge(int x, int y, int dir)
            {
                long key = Key(x, y);
                if (outs.TryGetValue(key, out int bits)) outs[key] = bits | (1 << dir);
                else { outs[key] = 1 << dir; starts.Add(key); }
            }
            for (int j = mask.Row0 - 1; j <= mask.RowEnd; j++)
            {
                var row = mask.Row(j);
                // Vertical edges: a run's west side goes south, its east side north (clearing on the left).
                for (int s = 0; s < row.Count; s += 2)
                {
                    Edge(row[s], j + 1, 3);
                    Edge(row[s + 1], j, 1);
                }
                // Horizontal edges along y = j: east where row j is in and row j − 1 out; west where the reverse.
                var below = mask.Row(j - 1);
                Xor(row, below, (from, to, aboveIn) =>
                {
                    if (aboveIn) for (int i = from; i < to; i++) Edge(i, j, 0);
                    else for (int i = to; i > from; i--) Edge(i, j, 2);
                });
            }
            starts.Sort((a, b) =>
            {
                int ya = (int)(a >> 32), yb = (int)(b >> 32);
                if (ya != yb) return ya.CompareTo(yb);
                return ((int)a).CompareTo((int)b);
            });
            // Each edge's successor is fixed by its end corner's edges alone (left, else ahead, else right), so the rings are
            // the same whichever edge a trace starts from: at a pinch the two ways in take the two ways out.
            var used = new Dictionary<long, int>();
            var rings = new List<Traced>();
            var steps = new List<byte>();
            foreach (long startKey in starts)
            {
                int all = outs[startKey];
                for (int first = 0; first < 4; first++)
                {
                    if ((all & (1 << first)) == 0) continue;
                    used.TryGetValue(startKey, out int done);
                    if ((done & (1 << first)) != 0) continue;
                    int x0 = (int)startKey, y0 = (int)(startKey >> 32), x = x0, y = y0, dir = first;
                    steps.Clear();
                    while (true)
                    {
                        long key = Key(x, y);
                        used.TryGetValue(key, out int u);
                        used[key] = u | (1 << dir);
                        steps.Add((byte)dir);
                        x += StepX[dir];
                        y += StepY[dir];
                        dir = Successor(outs[Key(x, y)], dir);
                        if (x == x0 && y == y0 && dir == first) break;
                    }
                    rings.Add(Canonical(x0, y0, steps));
                }
            }
            return rings;
        }

        static int Successor(int bits, int dir)
        {
            int left = (dir + 1) & 3, right = (dir + 3) & 3;
            return (bits & (1 << left)) != 0 ? left : (bits & (1 << dir)) != 0 ? dir : right;
        }

        static long Key(int x, int y) => ((long)y << 32) | (uint)x;

        // Runs where exactly one of a (row j) and b (row j − 1) is in: aboveIn true where a is.
        static void Xor(IReadOnlyList<int> a, IReadOnlyList<int> b, Action<int, int, bool> run)
        {
            var cuts = new List<int>(a.Count + b.Count);
            for (int k = 0; k < a.Count; k++) cuts.Add(a[k]);
            for (int k = 0; k < b.Count; k++) cuts.Add(b[k]);
            cuts.Sort();
            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                int from = cuts[k], to = cuts[k + 1];
                if (from == to) continue;
                bool inA = InRuns(a, from), inB = InRuns(b, from);
                if (inA != inB) run(from, to, inA);
            }
        }

        static bool InRuns(IReadOnlyList<int> r, int i)
        {
            for (int s = 0; s < r.Count; s += 2)
            {
                if (i < r[s]) return false;
                if (i < r[s + 1]) return true;
            }
            return false;
        }

        // The ring restarted at its lowest corner (lowest y, then x) where a step leaves it, so its bytes are its cells'.
        static Traced Canonical(int x0, int y0, List<byte> steps)
        {
            int n = steps.Count, best = 0, bx = x0, by = y0, x = x0, y = y0;
            for (int k = 0; k < n; k++)
            {
                if (y < by || (y == by && (x < bx || x == bx && steps[k] < steps[best]))) { best = k; bx = x; by = y; }
                x += StepX[steps[k]];
                y += StepY[steps[k]];
            }
            var outSteps = new byte[n];
            for (int k = 0; k < n; k++) outSteps[k] = steps[(best + k) % n];
            return new Traced { X0 = bx, Y0 = by, Steps = outSteps };
        }

        static PointMm Corner(int x, int y) => new PointMm((int)TrailMask.EdgeMm(x), (int)TrailMask.EdgeMm(y));

        /// <summary>The traced ring's corners only (where its direction changes): exact.</summary>
        static PointMm[] Corners(Traced t)
        {
            var pts = new List<PointMm>();
            int x = t.X0, y = t.Y0, n = t.Steps.Length;
            for (int k = 0; k < n; k++)
            {
                if (t.Steps[k] != t.Steps[(k + n - 1) % n]) pts.Add(Corner(x, y));
                x += StepX[t.Steps[k]];
                y += StepY[t.Steps[k]];
            }
            return pts.ToArray();
        }

        /// <summary>
        /// The traced ring straightened. From each kept point, the chord runs on over the steps ahead while some direction
        /// still passes every step's clearing cell strictly on its left and outside cell strictly on its right (no centre
        /// changes side), over steps of at most two neighbouring directions. It ends at the ring's start if that is in
        /// reach, else at a whole-millimetre point between the last step's two cells on the middle of the directions left
        /// (or, failing that, the farthest corner or edge midpoint the chord passes). Null when a ring can't be
        /// straightened this way. Integer cross products only.
        /// </summary>
        static PointMm[] Straighten(Traced t)
        {
            const int C = TrailMask.CellMm, H = C / 2;
            int n = t.Steps.Length;
            var cx = new long[n + 1];
            var cy = new long[n + 1];
            long x = TrailMask.EdgeMm(t.X0), y = TrailMask.EdgeMm(t.Y0);
            for (int k = 0; k <= n; k++)
            {
                cx[k] = x;
                cy[k] = y;
                if (k < n) { x += C * StepX[t.Steps[k]]; y += C * StepY[t.Steps[k]]; }
            }
            var kept = new List<PointMm>();
            var spans = new List<int>();   // per kept point: the first step after it, and whether it is a corner (end = first)
            var corner = new List<bool>();
            bool atCorner = true;
            long sx = cx[0], sy = cy[0];
            // The first step the chord from s must pass. A chord from a midpoint or a free point on a step's line between
            // its cells starts past that step: going forward it keeps that step's cells either side of it.
            int first = 0;
            while (true)
            {
                kept.Add(new PointMm((int)sx, (int)sy));
                spans.Add(first);
                corner.Add(atCorner);
                if (first >= n) return Merge(t, cx, cy, kept, spans, corner);
                if (kept.Count > n + 1) return null;
                long hiX = 0, hiY = 0, loX = 0, loY = 0;
                int dirs = 0, last = first - 1;
                long bestX = 0, bestY = 0;
                int bestStep = -1;
                bool closed = false;
                for (int k = first; k < n; k++)
                {
                    int dir = t.Steps[k];
                    int nd = dirs | 1 << dir;
                    if (Opposed(nd)) break;
                    long mx = cx[k] + H * StepX[dir], my = cy[k] + H * StepY[dir];
                    long lx = mx - H * StepY[dir] - sx, ly = my + H * StepX[dir] - sy, rx = mx + H * StepY[dir] - sx, ry = my - H * StepX[dir] - sy;
                    long nhX = hiX, nhY = hiY, nlX = loX, nlY = loY;
                    if (k == first) { nhX = lx; nhY = ly; nlX = rx; nlY = ry; }
                    else
                    {
                        if (Cross(lx, ly, hiX, hiY) > 0) { nhX = lx; nhY = ly; }
                        if (Cross(loX, loY, rx, ry) > 0) { nlX = rx; nlY = ry; }
                    }
                    if (Cross(nlX, nlY, nhX, nhY) <= 0) break;
                    dirs = nd; hiX = nhX; hiY = nhY; loX = nlX; loY = nlY; last = k;
                    // This step's midpoint and end corner, when the chord to them passes everything so far.
                    for (int q = 0; q < 2; q++)
                    {
                        long ex = q == 0 ? mx : cx[k + 1], ey = q == 0 ? my : cy[k + 1];
                        long dx = ex - sx, dy = ey - sy;
                        if ((dx != 0 || dy != 0) && Cross(dx, dy, hiX, hiY) > 0 && Cross(loX, loY, dx, dy) > 0)
                        {
                            bestX = ex; bestY = ey; bestStep = q == 0 ? k : k + 1;
                            if (q == 1 && k == n - 1) closed = true;
                        }
                    }
                }
                if (closed) return Merge(t, cx, cy, kept, spans, corner);   // the chord reaches the ring's start
                if (last < first) return null;
                int next;
                long vx = 0, vy = 0;
                bool free = false;
                if (last < n - 1 && bestStep <= last)
                {
                    // A point between the last step's cells on the cone's middle direction, rounded and checked exactly.
                    int dir = t.Steps[last];
                    double mx = cx[last] + H * StepX[dir] - sx, my = cy[last] + H * StepY[dir] - sy;
                    double nx = -StepY[dir], ny = StepX[dir];   // towards the clearing cell
                    double lh = Math.Sqrt((double)hiX * hiX + (double)hiY * hiY), ll = Math.Sqrt((double)loX * loX + (double)loY * loY);
                    double bx = hiX / lh + loX / ll, by = hiY / lh + loY / ll;
                    // s + u·b = m + v·n  →  solve for u (2×2).
                    double det = bx * (-ny) - by * (-nx);
                    if (Math.Abs(det) > 1e-12)
                    {
                        double u = (mx * (-ny) - my * (-nx)) / det;
                        long px = (long)Math.Round(u * bx), py = (long)Math.Round(u * by);
                        double side = (px - mx) * nx + (py - my) * ny;
                        if ((px != 0 || py != 0) && Math.Abs(side) < H && Cross(px, py, hiX, hiY) > 0 && Cross(loX, loY, px, py) > 0)
                        {
                            vx = sx + px; vy = sy + py; free = true;
                        }
                    }
                }
                if (free) { sx = vx; sy = vy; next = last + 1; atCorner = false; }
                else if (bestStep > first || (bestStep == first && (bestX != sx || bestY != sy)))
                {
                    // The farthest corner or midpoint: a midpoint of step k starts the next chord on step k, a corner after it.
                    bool mid = (bestX - cx[Math.Min(bestStep, n)]) != 0 || (bestY - cy[Math.Min(bestStep, n)]) != 0;
                    sx = bestX; sy = bestY; next = mid ? bestStep + 1 : bestStep;
                    atCorner = !mid;
                    if (!mid && next >= n) return Merge(t, cx, cy, kept, spans, corner);
                }
                else return null;
                first = next;
            }
        }

        /// <summary>
        /// Drops each point whose neighbours' chord passes every step between them with the steps' cells either side of it
        /// (strictly; at most two neighbouring directions), until none can go. The start stays.
        /// </summary>
        static PointMm[] Merge(Traced t, long[] cx, long[] cy, List<PointMm> pts, List<int> first, List<bool> corner)
        {
            const int H = TrailMask.CellMm / 2;
            int n = t.Steps.Length;
            pts = new List<PointMm>(pts) { new PointMm((int)cx[n], (int)cy[n]) };
            first = new List<int>(first) { n };
            corner = new List<bool>(corner) { true };
            bool Passes(int a, int b)
            {
                long ax = pts[a].X, ay = pts[a].Y, dx = pts[b].X - ax, dy = pts[b].Y - ay;
                int end = corner[b] ? first[b] : first[b] - 1, dirs = 0;
                for (int k = first[a]; k < end; k++)
                {
                    int dir = t.Steps[k];
                    dirs |= 1 << dir;
                    if (Opposed(dirs)) return false;
                    long mx = cx[k] + H * StepX[dir] - ax, my = cy[k] + H * StepY[dir] - ay;
                    if (Cross(dx, dy, mx - H * StepY[dir], my + H * StepX[dir]) <= 0) return false;
                    if (Cross(dx, dy, mx + H * StepY[dir], my - H * StepX[dir]) >= 0) return false;
                }
                return true;
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 1; i + 1 < pts.Count; i++)
                {
                    if (!Passes(i - 1, i + 1)) continue;
                    pts.RemoveAt(i);
                    first.RemoveAt(i);
                    corner.RemoveAt(i);
                    changed = true;
                }
            }
            pts.RemoveAt(pts.Count - 1);
            return pts.ToArray();
        }

        static bool Opposed(int dirs) => (dirs & 5) == 5 || (dirs & 10) == 10;
        static long Cross(long ax, long ay, long bx, long by) => ax * by - ay * bx;
    }
}
