using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// One trail as the fine snow field needs it (task 4.07 PR B1, SN-03 v3): its 250 mm mask, its centreline and mean
    /// width in design mm, and where along the centreline each of its network segments starts.
    /// </summary>
    public sealed class TrailSnowInput
    {
        /// <summary>The painted footprint (design frame, 250 mm cells).</summary>
        public TrailMask Mask;
        /// <summary>The centreline, head first, design mm from the site's centre (x east, y north).</summary>
        public int[] LineXMm, LineYMm;
        /// <summary>The trail's mean width, mm (its footprint area over its length, as the guest network has it).</summary>
        public int WidthMm;
        /// <summary>Where each network segment starts, mm along the centreline from the head, ascending.</summary>
        public int[] SegmentStartMm;
        /// <summary>Each segment's edge in the simulation's network (one per entry of <see cref="SegmentStartMm"/>).</summary>
        public int[] SegmentEdge;
    }

    /// <summary>
    /// SN-03 v3's fine field layout (task 4.07 PR B1): every 8 m snow cell a trail's mask touches is split into a block
    /// of fine cells, one size per resort (1, 2 or 4 m, the finest that fits the cap). Each fine cell belongs to one
    /// trail, the nearest centreline where masks overlap (or none: a block keeps its off-trail cells so the block's water
    /// is the 8 m cell's), and carries its distance along that centreline (grooming, SN-23) and its lane, 0–7 across the
    /// width (wear, SN-24), and the network edge of the segment it lies on. Engine-free and integer only: the same
    /// trails give the same bytes. Built off the worker (it allocates); the worker only reads it.
    /// </summary>
    public sealed class TrailSnowLayout
    {
        /// <summary>Lanes across a trail's width (SN-24 v3; the guests' WearBands).</summary>
        public const int Lanes = 8;
        const int CellMm = SurfaceGrid.CellMetres * 1000;
        // Half a block's diagonal, mm, rounded up, with a margin for the floored distances: a segment can only be the
        // nearest for some point in a block if its distance to the block's centre is within twice this of the best.
        const long HalfDiagonalMm = 5658 + 2;

        /// <summary>The fine cell's side, metres: 1, 2 or 4.</summary>
        public int FineMetres { get; private set; }
        /// <summary>Fine cells along a block's side (8 ÷ <see cref="FineMetres"/>).</summary>
        public int PerSide { get; private set; }
        /// <summary>Fine cells in a block.</summary>
        public int BlockCells => PerSide * PerSide;
        /// <summary>The 8 m grid this layout was made for.</summary>
        public int GridWidth { get; private set; }
        public int GridHeight { get; private set; }
        public long OriginXMm { get; private set; }
        public long OriginYMm { get; private set; }
        /// <summary>Even 4 m cells didn't fit the cap (the layout is 4 m anyway).</summary>
        public bool OverCap { get; private set; }
        /// <summary>The network the segments were taken from (the caller's shape hash), so the worker can refuse a stale layout.</summary>
        public ulong NetworkHash { get; private set; }
        /// <summary>The trails' count, in the input's order.</summary>
        public int Trails { get; private set; }

        /// <summary>Each block's 8 m cell index (j × width + i), ascending.</summary>
        public int[] BlockCell { get; private set; }
        /// <summary>Per fine cell (block, then rows south to north, then west to east): the owning trail, or −1.</summary>
        public short[] Owner { get; private set; }
        /// <summary>Per fine cell: mm along the owner's centreline from its head (0 off-trail).</summary>
        public int[] AlongMm { get; private set; }
        /// <summary>Per fine cell: its lane, 0 at the left edge looking downhill from the head, to 7 (0 off-trail).</summary>
        public byte[] Lane { get; private set; }
        /// <summary>Per fine cell: the network edge of the owner's segment there, or −1.</summary>
        public int[] Edge { get; private set; }
        /// <summary>Fine cells each trail owns.</summary>
        public int[] TrailCells { get; private set; }

        public int Blocks => BlockCell.Length;
        public int FineCells => Owner.Length;

        /// <summary>The block on an 8 m cell, or −1.</summary>
        public int BlockOf(int cell)
        {
            int b = Array.BinarySearch(BlockCell, cell);
            return b >= 0 ? b : -1;
        }

        /// <summary>The fine cell's centre, design mm.</summary>
        public void CentreMm(int fine, out long x, out long y)
        {
            int b = fine / BlockCells, k = fine - b * BlockCells;
            int cell = BlockCell[b], ci = cell % GridWidth, cj = cell / GridWidth;
            int fi = k % PerSide, fj = k / PerSide;
            long s = FineMetres * 1000L;
            x = OriginXMm + (long)ci * CellMm + fi * s + s / 2;
            y = OriginYMm + (long)cj * CellMm + fj * s + s / 2;
        }

        /// <summary>A hash of everything the worker reads (the same trails, the same hash).</summary>
        public ulong Hash()
        {
            ulong h = 14695981039346656037UL;
            void Mix(long v) { unchecked { for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; } } }
            Mix(FineMetres); Mix(GridWidth); Mix(GridHeight); Mix(OriginXMm); Mix(OriginYMm); Mix((long)NetworkHash);
            foreach (int c in BlockCell) Mix(c);
            for (int i = 0; i < Owner.Length; i++) { Mix(Owner[i]); Mix(AlongMm[i]); Mix(Lane[i]); Mix(Edge[i]); }
            return h;
        }

        /// <summary>
        /// Lays the fine field over <paramref name="trails"/> on the 8 m grid (<paramref name="gridWidth"/> ×
        /// <paramref name="gridHeight"/> cells from the south-west corner at the origin, design mm). The fine size is
        /// the finest of 1, 2 and 4 m whose cells fit <paramref name="fineCellCap"/>.
        /// </summary>
        public static TrailSnowLayout Build(int gridWidth, int gridHeight, long originXMm, long originYMm,
            IReadOnlyList<TrailSnowInput> trails, int fineCellCap, ulong networkHash = 0)
        {
            if (gridWidth <= 0 || gridHeight <= 0) throw new ArgumentException("An empty grid.");
            int n = trails?.Count ?? 0;
            if (n > short.MaxValue) throw new ArgumentException("Too many trails.");
            var L = new TrailSnowLayout
            {
                GridWidth = gridWidth, GridHeight = gridHeight, OriginXMm = originXMm, OriginYMm = originYMm,
                NetworkHash = networkHash, Trails = n,
            };

            // 1. The 8 m cells each trail's mask touches (by its cells' centres), as (cell, trail) pairs.
            var stamp = new int[gridWidth * gridHeight];
            for (int i = 0; i < stamp.Length; i++) stamp[i] = -1;
            var pairs = new List<long>();
            for (int t = 0; t < n; t++)
            {
                var m = trails[t].Mask;
                if (m == null || m.IsEmpty) continue;
                for (int j = m.Row0; j < m.RowEnd; j++)
                {
                    long cj = FloorDiv((long)j * TrailMask.CellMm - originYMm, CellMm);
                    if (cj < 0 || cj >= gridHeight) continue;
                    var r = m.Row(j);
                    for (int k = 0; k < r.Count; k += 2)
                    {
                        long ci0 = Math.Max(0, FloorDiv((long)r[k] * TrailMask.CellMm - originXMm, CellMm));
                        long ci1 = Math.Min(gridWidth - 1, FloorDiv((long)(r[k + 1] - 1) * TrailMask.CellMm - originXMm, CellMm));
                        for (long ci = ci0; ci <= ci1; ci++)
                        {
                            int cell = (int)(cj * gridWidth + ci);
                            if (stamp[cell] == t) continue;
                            stamp[cell] = t;
                            pairs.Add(((long)cell << 16) | (uint)t);
                        }
                    }
                }
            }
            pairs.Sort();
            var blocks = new List<int>();
            foreach (long p in pairs) { int c = (int)(p >> 16); if (blocks.Count == 0 || blocks[blocks.Count - 1] != c) blocks.Add(c); }
            L.BlockCell = blocks.ToArray();

            // 2. One fine size for the whole resort.
            L.FineMetres = 4; L.OverCap = true;
            foreach (int s in new[] { 1, 2, 4 })
            {
                long cells = (long)blocks.Count * (8 / s) * (8 / s);
                if (cells <= fineCellCap) { L.FineMetres = s; L.OverCap = false; break; }
            }
            L.PerSide = 8 / L.FineMetres;
            int per = L.PerSide, bc = per * per, total = blocks.Count * bc;
            L.Owner = new short[total]; L.AlongMm = new int[total]; L.Lane = new byte[total]; L.Edge = new int[total];
            L.TrailCells = new int[n];
            for (int i = 0; i < total; i++) { L.Owner[i] = -1; L.Edge[i] = -1; }

            // 3. Each trail's segments: lengths and where each starts along the line.
            var lines = new Line[n];
            for (int t = 0; t < n; t++) lines[t] = new Line(trails[t]);

            // 4. Per block: the trails on it and the segments of each that can be nearest; then every fine cell.
            var cand = new List<int>();
            var candStart = new List<int>();
            var onBlock = new List<int>();
            long fs = L.FineMetres * 1000L;
            int pi = 0;
            for (int b = 0; b < blocks.Count; b++)
            {
                int cell = blocks[b], ci = cell % gridWidth, cj = cell / gridWidth;
                long bx = originXMm + (long)ci * CellMm, by = originYMm + (long)cj * CellMm;
                onBlock.Clear(); cand.Clear(); candStart.Clear();
                while (pi < pairs.Count && (int)(pairs[pi] >> 16) == cell)
                {
                    int t = (int)(pairs[pi] & 0xFFFF);
                    onBlock.Add(t);
                    candStart.Add(cand.Count);
                    lines[t].Candidates(bx + CellMm / 2, by + CellMm / 2, cand);
                    pi++;
                }
                candStart.Add(cand.Count);
                for (int k = 0; k < bc; k++)
                {
                    int fi = k % per, fj = k / per;
                    long x = bx + fi * fs + fs / 2, y = by + fj * fs + fs / 2;
                    int best = -1; long bestD = long.MaxValue, bestAlong = 0, bestLat = 0;
                    for (int o = 0; o < onBlock.Count; o++)
                    {
                        int t = onBlock[o];
                        if (!ContainsMm(trails[t].Mask, x, y)) continue;
                        lines[t].Nearest(x, y, cand, candStart[o], candStart[o + 1], out long d, out long along, out long lat);
                        if (d < bestD) { bestD = d; best = t; bestAlong = along; bestLat = lat; }
                    }
                    if (best < 0) continue;
                    int f = b * bc + k;
                    L.Owner[f] = (short)best;
                    L.AlongMm[f] = (int)Math.Min(int.MaxValue, bestAlong);
                    L.Lane[f] = LaneOf(bestLat, trails[best].WidthMm);
                    L.Edge[f] = lines[best].EdgeAt(bestAlong);
                    L.TrailCells[best]++;
                }
            }
            return L;
        }

        /// <summary>The lane of a signed offset from the centreline (mm, positive to the left): 0 at the left edge, 7 at the right.</summary>
        public static byte LaneOf(long lateralMm, int widthMm)
        {
            long half = Math.Max(1, widthMm / 2);
            long lane = FloorDiv((half - lateralMm) * Lanes, 2 * half);
            return (byte)Math.Max(0, Math.Min(Lanes - 1, lane));
        }

        static bool ContainsMm(TrailMask m, long x, long y)
            => m.Contains((int)FloorDiv(x + TrailMask.CellMm / 2, TrailMask.CellMm), (int)FloorDiv(y + TrailMask.CellMm / 2, TrailMask.CellMm));

        static long FloorDiv(long a, long b) { long q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q; }

        /// <summary>The integer square root (floor).</summary>
        public static long ISqrt(long v)
        {
            if (v <= 0) return 0;
            long r = (long)Math.Sqrt(v);
            while (r * r > v) r--;
            while ((r + 1) * (r + 1) <= v) r++;
            return r;
        }

        /// <summary>A trail's centreline as integer segments.</summary>
        sealed class Line
        {
            readonly long[] _x, _y, _len, _len2, _start;
            readonly int[] _segStart, _segEdge;
            readonly int _count;   // segments (at least one; a lone point is a segment of length 0)

            public Line(TrailSnowInput t)
            {
                int pts = Math.Min(t.LineXMm?.Length ?? 0, t.LineYMm?.Length ?? 0);
                if (pts == 0) { _x = new long[] { 0, 0 }; _y = new long[] { 0, 0 }; }
                else if (pts == 1) { _x = new long[] { t.LineXMm[0], t.LineXMm[0] }; _y = new long[] { t.LineYMm[0], t.LineYMm[0] }; }
                else { _x = new long[pts]; _y = new long[pts]; for (int i = 0; i < pts; i++) { _x[i] = t.LineXMm[i]; _y[i] = t.LineYMm[i]; } }
                _count = _x.Length - 1;
                _len = new long[_count]; _len2 = new long[_count]; _start = new long[_count];
                long run = 0;
                for (int k = 0; k < _count; k++)
                {
                    long dx = _x[k + 1] - _x[k], dy = _y[k + 1] - _y[k];
                    _len2[k] = dx * dx + dy * dy; _len[k] = ISqrt(_len2[k]); _start[k] = run; run += _len[k];
                }
                _segStart = t.SegmentStartMm ?? Array.Empty<int>();
                _segEdge = t.SegmentEdge ?? Array.Empty<int>();
            }

            /// <summary>Adds the segments that can be nearest to some point of the block centred here.</summary>
            public void Candidates(long cx, long cy, List<int> into)
            {
                long min = long.MaxValue;
                for (int k = 0; k < _count; k++) { Distance(k, cx, cy, out long d, out _, out _); if (d < min) min = d; }
                long reach = min + 2 * HalfDiagonalMm;
                for (int k = 0; k < _count; k++) { Distance(k, cx, cy, out long d, out _, out _); if (d <= reach) into.Add(k); }
            }

            /// <summary>The nearest point among the candidate segments: its distance, along (from the head) and signed offset (left +).</summary>
            public void Nearest(long px, long py, List<int> cand, int from, int to, out long dist, out long along, out long lateral)
            {
                dist = long.MaxValue; along = 0; lateral = 0;
                for (int c = from; c < to; c++)
                {
                    int k = cand[c];
                    Distance(k, px, py, out long d, out long a, out long l);
                    if (d < dist) { dist = d; along = _start[k] + a; lateral = l; }
                }
            }

            void Distance(int k, long px, long py, out long dist, out long along, out long lateral)
            {
                long ax = _x[k], ay = _y[k], dx = _x[k + 1] - ax, dy = _y[k + 1] - ay;
                long wx = px - ax, wy = py - ay, len2 = _len2[k], len = _len[k];
                if (len2 == 0) { dist = ISqrt(wx * wx + wy * wy); along = 0; lateral = 0; return; }
                long dot = wx * dx + wy * dy, cross = dx * wy - dy * wx;
                lateral = cross / len;
                if (dot <= 0) { dist = ISqrt(wx * wx + wy * wy); along = 0; }
                else if (dot >= len2) { long ex = px - _x[k + 1], ey = py - _y[k + 1]; dist = ISqrt(ex * ex + ey * ey); along = len; }
                else { dist = Math.Abs(cross) / len; along = dot / len; }
            }

            /// <summary>The network edge of the segment at this distance along the line (the first before its start), or −1.</summary>
            public int EdgeAt(long along)
            {
                int n = Math.Min(_segStart.Length, _segEdge.Length);
                if (n == 0) return -1;
                int lo = 0, hi = n - 1;
                while (lo < hi) { int mid = (lo + hi + 1) / 2; if (_segStart[mid] <= along) lo = mid; else hi = mid - 1; }
                return _segEdge[lo];
            }
        }
    }
}
