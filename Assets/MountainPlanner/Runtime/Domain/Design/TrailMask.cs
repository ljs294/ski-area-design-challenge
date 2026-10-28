using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail's clearing as cells (task P3-10, sections finding 7; save v8): the 250 mm cells of the design's frame (TE5:
    /// millimetres from the site's centre) that are in, held as each row's runs. Cell (i, j) is centred on (250·i, 250·j)
    /// mm and reaches 125 mm either way, so every test against a centre is exact in whole millimetres, and every whole and
    /// half metre of the frame is a centre: the 1 m lattice's cell centres fall on them, so its footprint reads the same
    /// test the strokes always had. Painting stamps a brush capsule (a cell is in when its centre lies within
    /// half the width of the stroke's path, as the 1 m footprint always did); erasing clears; a cut keeps one side.
    /// <see cref="TrailShape"/> writes it down as rings. Immutable; deterministic (SH-04).
    /// </summary>
    public sealed class TrailMask : IEquatable<TrailMask>
    {
        public const int CellMm = 250;
        const int HalfCellMm = CellMm / 2;

        /// <summary>The first row (j) held; rows are the half-open run pairs [from, to) of i, sorted and apart.</summary>
        public int Row0 { get; }
        readonly int[][] _rows;

        public static readonly TrailMask Empty = new TrailMask(0, Array.Empty<int[]>());

        TrailMask(int row0, int[][] rows)
        {
            Row0 = row0;
            _rows = rows;
            long cells = 0;
            int minI = int.MaxValue, maxI = int.MinValue;
            foreach (var r in rows)
            {
                for (int k = 0; k < r.Length; k += 2) cells += r[k + 1] - r[k];
                if (r.Length == 0) continue;
                minI = Math.Min(minI, r[0]);
                maxI = Math.Max(maxI, r[r.Length - 1]);
            }
            Cells = cells;
            Column0 = cells == 0 ? 0 : minI;
            ColumnEnd = cells == 0 ? 0 : maxI;
        }

        public int RowCount => _rows.Length;
        /// <summary>One past the last row.</summary>
        public int RowEnd => Row0 + _rows.Length;
        /// <summary>The first column any row holds, and one past the last.</summary>
        public int Column0 { get; }
        public int ColumnEnd { get; }
        public long Cells { get; }
        public bool IsEmpty => Cells == 0;
        public double AreaSquareMetres => Cells * (CellMm / 1000.0) * (CellMm / 1000.0);

        /// <summary>Row <paramref name="j"/>'s runs (pairs [from, to) of i); empty outside the mask.</summary>
        public IReadOnlyList<int> Row(int j)
        {
            int k = j - Row0;
            return k >= 0 && k < _rows.Length ? _rows[k] : Array.Empty<int>();
        }

        public bool Contains(int i, int j)
        {
            int k = j - Row0;
            if (k < 0 || k >= _rows.Length) return false;
            var r = _rows[k];
            for (int s = 0; s < r.Length; s += 2)
            {
                if (i < r[s]) return false;
                if (i < r[s + 1]) return true;
            }
            return false;
        }

        /// <summary>Whether the cell under a design-frame point (metres from the site's centre) is in.</summary>
        public bool ContainsAt(double xMetres, double yMetres) =>
            Contains((int)Math.Floor(xMetres * (1000.0 / CellMm) + 0.5), (int)Math.Floor(yMetres * (1000.0 / CellMm) + 0.5));

        public static long CentreMm(int index) => (long)index * CellMm;
        /// <summary>The west (or south) edge of cell <paramref name="index"/>: corner index k of the cells' outline.</summary>
        public static long EdgeMm(int index) => (long)index * CellMm - HalfCellMm;

        public bool Equals(TrailMask o)
        {
            if (o is null || o.Cells != Cells) return false;
            if (Cells == 0) return true;
            if (o.Row0 != Row0 || o._rows.Length != _rows.Length) return false;
            for (int k = 0; k < _rows.Length; k++)
            {
                var a = _rows[k];
                var b = o._rows[k];
                if (a.Length != b.Length) return false;
                for (int s = 0; s < a.Length; s++) if (a[s] != b[s]) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is TrailMask o && Equals(o);
        public override int GetHashCode() => (int)Cells ^ (Row0 * 397) ^ (_rows.Length << 16);

        /// <summary>The cells of both.</summary>
        public TrailMask Union(TrailMask other)
        {
            if (other == null || other.IsEmpty) return this;
            if (IsEmpty) return other;
            var b = new Builder(Math.Min(Row0, other.Row0), Math.Max(RowEnd, other.RowEnd));
            b.Or(this);
            b.Or(other);
            return b.ToMask();
        }

        /// <summary>The cells a cut keeps (NW-10's legacy split).</summary>
        public TrailMask Clip(Cut cut)
        {
            if (IsEmpty) return this;
            var b = new Builder(Row0, RowEnd);
            b.Or(this);
            b.Clip(cut);
            return b.ToMask();
        }

        /// <summary>Whether a cell of each shares a side with a cell of the other (4-adjacent) or a cell.</summary>
        public bool Touches(TrailMask other)
        {
            if (other == null || IsEmpty || other.IsEmpty) return false;
            if (other.Row0 > RowEnd || Row0 > other.RowEnd) return false;
            for (int j = Math.Max(Row0, other.Row0 - 1); j < Math.Min(RowEnd, other.RowEnd + 1); j++)
            {
                var a = Row(j);
                if (a.Count == 0) continue;
                if (Overlap(a, other.Row(j), 1) || Overlap(a, other.Row(j - 1), 0) || Overlap(a, other.Row(j + 1), 0)) return true;
            }
            return false;
        }

        // Whether runs of a and b overlap once each of b's is widened by <paramref name="grow"/> cells either side.
        static bool Overlap(IReadOnlyList<int> a, IReadOnlyList<int> b, int grow)
        {
            int x = 0, y = 0;
            while (x < a.Count && y < b.Count)
            {
                int a0 = a[x], a1 = a[x + 1], b0 = b[y] - grow, b1 = b[y + 1] + grow;
                if (a0 < b1 && b0 < a1) return true;
                if (a1 <= b1) x += 2; else y += 2;
            }
            return false;
        }

        /// <summary>
        /// Builds a mask row by row: paint and erase strokes in order, clip by cuts, take unions. Rows outside the range it
        /// was made for are refused, so callers size it from the strokes' reach first.
        /// </summary>
        public sealed class Builder
        {
            readonly int _row0;
            readonly List<int>[] _rows;
            List<int> _scratch = new List<int>();

            public Builder(int row0, int rowEnd)
            {
                _row0 = row0;
                int n = Math.Max(0, rowEnd - row0);
                if (n > 4_000_000) throw new ArgumentException("The clearing is too large.");
                _rows = new List<int>[n];
            }

            /// <summary>The rows a stroke's capsules reach: from the lowest centre row within reach to one past the highest.</summary>
            public static void RowsOf(Stroke s, ref int row0, ref int rowEnd)
            {
                long r = (s.WidthMm + 1) / 2 + CellMm;
                foreach (var p in s.Points)
                {
                    row0 = Math.Min(row0, (int)FloorDiv(p.Y - r, CellMm));
                    rowEnd = Math.Max(rowEnd, (int)FloorDiv(p.Y + r, CellMm) + 1);
                }
            }

            public void Or(TrailMask m)
            {
                for (int j = m.Row0; j < m.RowEnd; j++)
                {
                    var src = m.Row(j);
                    for (int s = 0; s < src.Count; s += 2) Set(j, src[s], src[s + 1], true);
                }
            }

            /// <summary>A stroke stamped: each segment's capsule painted (or erased) in order.</summary>
            public void Stamp(Stroke s)
            {
                var pts = s.Points;
                if (pts.Count == 0) return;
                for (int k = 0; k < Math.Max(1, pts.Count - 1); k++)
                    Capsule(pts[k], pts[Math.Min(k + 1, pts.Count - 1)], s.WidthMm, !s.Erase);
            }

            void Capsule(PointMm a, PointMm b, long w, bool paint)
            {
                long r = (w + 1) / 2;
                long minX = Math.Min(a.X, b.X) - r, maxX = Math.Max(a.X, b.X) + r;
                long minY = Math.Min(a.Y, b.Y) - r, maxY = Math.Max(a.Y, b.Y) + r;
                int i0 = (int)FloorDiv(minX, CellMm) - 1, i1 = (int)FloorDiv(maxX, CellMm) + 1;
                int j0 = Math.Max(_row0, (int)FloorDiv(minY, CellMm) - 1), j1 = Math.Min(_row0 + _rows.Length - 1, (int)FloorDiv(maxY, CellMm) + 1);
                for (int j = j0; j <= j1; j++)
                {
                    long cy = CentreMm(j);
                    // The capsule is convex, so its cells in a row are one run. Its ends are worked out from the shape, then
                    // settled cell by cell with the exact test (a cell or two either way), so the run is the test's own.
                    if (!RowSpan(cy, a, b, w / 2.0, out double x0, out double x1)) continue;
                    int lo = Math.Max(i0, (int)Math.Floor(x0 / CellMm) - 1), hi = Math.Min(i1, (int)Math.Ceiling(x1 / CellMm) + 1);
                    while (lo <= hi && !InCapsule(CentreMm(lo), cy, a, b, w)) lo++;
                    if (lo > hi) continue;
                    while (hi > lo && !InCapsule(CentreMm(hi), cy, a, b, w)) hi--;
                    while (lo > i0 && InCapsule(CentreMm(lo - 1), cy, a, b, w)) lo--;
                    while (hi < i1 && InCapsule(CentreMm(hi + 1), cy, a, b, w)) hi++;
                    Set(j, lo, hi + 1, paint);
                }
            }

            // Where the line y = cy crosses the capsule of radius r round ab: the union of the two end discs and the band
            // between them (one interval, the capsule being convex). False when it misses.
            static bool RowSpan(long cy, PointMm a, PointMm b, double r, out double x0, out double x1)
            {
                x0 = double.MaxValue; x1 = double.MinValue;
                foreach (var c in new[] { a, b })
                {
                    double dy = cy - c.Y;
                    if (Math.Abs(dy) > r) continue;
                    double h = Math.Sqrt(r * r - dy * dy);
                    x0 = Math.Min(x0, c.X - h);
                    x1 = Math.Max(x1, c.X + h);
                }
                double dx = (double)b.X - a.X, dyy = (double)b.Y - a.Y, l2 = dx * dx + dyy * dyy, py = cy - a.Y;
                if (l2 > 0)
                {
                    double lo = double.MinValue, hi = double.MaxValue, rl = r * Math.Sqrt(l2);
                    // Within r of the line: |(x − ax)·dy − py·dx| ≤ r·L.
                    if (dyy != 0)
                    {
                        double u = a.X + (py * dx - rl) / dyy, v = a.X + (py * dx + rl) / dyy;
                        lo = Math.Max(lo, Math.Min(u, v)); hi = Math.Min(hi, Math.Max(u, v));
                    }
                    else if (Math.Abs(py * dx) > rl) lo = hi + 1;
                    // Between the ends: 0 ≤ (x − ax)·dx + py·dy ≤ L².
                    if (dx != 0)
                    {
                        double u = a.X + (0 - py * dyy) / dx, v = a.X + (l2 - py * dyy) / dx;
                        lo = Math.Max(lo, Math.Min(u, v)); hi = Math.Min(hi, Math.Max(u, v));
                    }
                    else if (py * dyy < 0 || py * dyy > l2) lo = hi + 1;
                    if (lo <= hi) { x0 = Math.Min(x0, lo); x1 = Math.Max(x1, hi); }
                }
                return x0 <= x1;
            }

            /// <summary>
            /// The stroke's test (TR-02): a centre is within half the width of the segment ab. Whole millimetres throughout,
            /// with the one product too large for a long taken in doubles.
            /// </summary>
            public static bool InCapsule(long cx, long cy, PointMm a, PointMm b, long w)
            {
                long dx = (long)b.X - a.X, dy = (long)b.Y - a.Y, px = cx - a.X, py = cy - a.Y;
                long len2 = dx * dx + dy * dy, t = px * dx + py * dy;
                if (len2 == 0 || t <= 0) return 4 * (px * px + py * py) <= w * w;
                if (t >= len2)
                {
                    long qx = cx - b.X, qy = cy - b.Y;
                    return 4 * (qx * qx + qy * qy) <= w * w;
                }
                double cross = (double)(px * dy - py * dx);
                return 4.0 * cross * cross <= (double)(w * w) * len2;
            }

            /// <summary>Keeps the cells whose centres the cut keeps (exact: whole millimetres in longs).</summary>
            public void Clip(Cut cut)
            {
                for (int k = 0; k < _rows.Length; k++)
                {
                    var row = _rows[k];
                    if (row == null || row.Count == 0) continue;
                    int j = _row0 + k;
                    long cy = CentreMm(j);
                    // Side(i) = (centre(i) − At.X)·DirX + (cy − At.Y)·DirY; ahead when ≥ 0.
                    // So the cells ahead are those with (centre − At.X)·DirX ≥ kc: one side of a column, or all or none.
                    long kc = -(cy - cut.At.Y) * cut.DirY;
                    int from = int.MinValue, to = int.MaxValue;   // the cells ahead: [from, to)
                    if (cut.DirX == 0)
                    {
                        if (kc > 0) to = from;                   // none ahead
                    }
                    else if (cut.DirX > 0)
                    {
                        long x = CeilDiv(kc, cut.DirX);          // centre − At.X ≥ x
                        from = (int)Clamp(CeilDiv(x + cut.At.X, CellMm));
                    }
                    else
                    {
                        long x = FloorDiv(kc, cut.DirX);         // centre − At.X ≤ x
                        to = (int)Clamp(FloorDiv(x + cut.At.X, CellMm) + 1);
                    }
                    if (!cut.KeepAhead) Set(j, from, to, false);
                    else if (from >= to) row.Clear();
                    else
                    {
                        Set(j, int.MinValue, from, false);
                        Set(j, to, int.MaxValue, false);
                    }
                }
            }

            static long Clamp(long v) => Math.Max(int.MinValue + 1L, Math.Min(int.MaxValue - 1L, v));

            /// <summary>Sets cells [from, to) of row j in or out.</summary>
            public void Set(int j, int from, int to, bool on)
            {
                int k = j - _row0;
                if (k < 0 || k >= _rows.Length) throw new ArgumentOutOfRangeException(nameof(j), "The row is outside the builder.");
                if (from >= to) return;
                var row = _rows[k] ?? (_rows[k] = new List<int>());
                var o = _scratch;
                o.Clear();
                if (on)
                {
                    int s = 0;
                    while (s < row.Count && row[s + 1] < from) { o.Add(row[s]); o.Add(row[s + 1]); s += 2; }
                    int a = from, b = to;
                    while (s < row.Count && row[s] <= to) { a = Math.Min(a, row[s]); b = Math.Max(b, row[s + 1]); s += 2; }
                    o.Add(a); o.Add(b);
                    while (s < row.Count) { o.Add(row[s]); o.Add(row[s + 1]); s += 2; }
                }
                else
                {
                    for (int s = 0; s < row.Count; s += 2)
                    {
                        int a = row[s], b = row[s + 1];
                        if (b <= from || a >= to) { o.Add(a); o.Add(b); continue; }
                        if (a < from) { o.Add(a); o.Add(from); }
                        if (b > to) { o.Add(to); o.Add(b); }
                    }
                }
                _scratch = row;
                _rows[k] = o;
            }

            public TrailMask ToMask()
            {
                int first = 0, last = _rows.Length - 1;
                while (first <= last && (_rows[first] == null || _rows[first].Count == 0)) first++;
                while (last >= first && (_rows[last] == null || _rows[last].Count == 0)) last--;
                if (first > last) return Empty;
                var rows = new int[last - first + 1][];
                for (int k = first; k <= last; k++) rows[k - first] = _rows[k] == null ? Array.Empty<int>() : _rows[k].ToArray();
                return new TrailMask(_row0 + first, rows);
            }
        }

        public static long FloorDiv(long a, long b)
        {
            long q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        public static long CeilDiv(long a, long b) => -FloorDiv(-a, b);
    }
}
