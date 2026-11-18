using System;

namespace MountainPlanner.Domain.Water
{
    /// <summary>
    /// WS-04 v2 (task 4.14 PR B): where each 8 m cell's runoff goes. A priority flood over the downloaded mean heights (the
    /// static inputs, SI-21) climbs from every outlet at once, the field's edge and every water cell, so each land cell
    /// drains (over its eight neighbours, pits filled) to the outlet that reaches it first: the land around a pond in its own
    /// bowl drains into the pond, not over its rim. Run off the main thread whenever the water table changes. Integer only,
    /// ties broken by the order cells were reached, so the same heights and water always drain the same way.
    /// </summary>
    public sealed class Catchments
    {
        public readonly int Width, Depth;
        readonly int[] _heights;

        Catchments(int width, int depth, int[] heights) { Width = width; Depth = depth; _heights = heights; }

        /// <summary>The area's heights (row-major, <paramref name="width"/> × <paramref name="depth"/>, mm), kept for every table.</summary>
        public static Catchments Of(int width, int depth, int[] heightsMm)
        {
            int n = width * depth;
            if (width <= 0 || depth <= 0 || heightsMm == null || heightsMm.Length != n) throw new ArgumentException("Heights for every cell.", nameof(heightsMm));
            return new Catchments(width, depth, (int[])heightsMm.Clone());
        }

        // Neighbours, the four sides first: a tie goes to a side before a corner.
        static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 };
        static readonly int[] Dy = { 0, 1, 0, -1, 1, 1, -1, -1 };

        /// <summary>
        /// For each cell, 1 + the source its water reaches first (<paramref name="sourceOfBody"/>[body] is that body's
        /// source + 1, or 0 when the body isn't one): land draining into a pond counts for the pond; land draining into a
        /// stream, a lake that isn't a source or off the field counts for nothing. A water cell reads as its own body's.
        /// </summary>
        public byte[] DrainsTo(byte[] mask, byte[] sourceOfBody)
        {
            int width = Width, depth = Depth, n = width * depth;
            if (mask == null || mask.Length != n) throw new ArgumentException("A mask over the same cells.", nameof(mask));
            var body = new byte[n];   // 1 + the first body reached, 0 for the edge
            var seen = new bool[n];
            var heap = new Heap(Math.Max(16, 2 * (width + depth)));
            int seq = 0;
            for (int c = 0; c < n; c++) if (mask[c] != 0) Seed(c);
            for (int x = 0; x < width; x++) { Seed(x); Seed((depth - 1) * width + x); }
            for (int y = 1; y < depth - 1; y++) { Seed(y * width); Seed(y * width + width - 1); }
            while (heap.Count > 0)
            {
                heap.Pop(out long level, out int c);
                int cx = c % width, cy = c / width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + Dx[k], ny = cy + Dy[k];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= depth) continue;
                    int m = ny * width + nx;
                    if (seen[m]) continue;
                    seen[m] = true;
                    body[m] = body[c];
                    heap.Push(Math.Max(level, _heights[m]), seq++, m);
                }
            }
            var drains = new byte[n];
            for (int c = 0; c < n; c++)
            {
                int b = body[c];
                drains[c] = b > 0 && b - 1 < sourceOfBody.Length ? sourceOfBody[b - 1] : (byte)0;
            }
            return drains;

            void Seed(int c)
            {
                if (seen[c]) return;
                seen[c] = true;
                body[c] = mask[c];
                heap.Push(_heights[c], seq++, c);
            }
        }

        /// <summary>A binary min-heap on (level, then the order pushed): no ties left to chance.</summary>
        sealed class Heap
        {
            long[] _level; int[] _seq; int[] _cell;
            public int Count;

            public Heap(int capacity) { _level = new long[capacity]; _seq = new int[capacity]; _cell = new int[capacity]; }

            bool Less(int a, int b) => _level[a] < _level[b] || (_level[a] == _level[b] && _seq[a] < _seq[b]);

            void Swap(int a, int b)
            {
                (_level[a], _level[b]) = (_level[b], _level[a]);
                (_seq[a], _seq[b]) = (_seq[b], _seq[a]);
                (_cell[a], _cell[b]) = (_cell[b], _cell[a]);
            }

            public void Push(long level, int seq, int cell)
            {
                if (Count == _level.Length)
                {
                    int n = _level.Length * 2;
                    Array.Resize(ref _level, n); Array.Resize(ref _seq, n); Array.Resize(ref _cell, n);
                }
                int i = Count++;
                _level[i] = level; _seq[i] = seq; _cell[i] = cell;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (!Less(i, p)) break;
                    Swap(i, p);
                    i = p;
                }
            }

            public void Pop(out long level, out int cell)
            {
                level = _level[0]; cell = _cell[0];
                Count--;
                if (Count == 0) return;
                _level[0] = _level[Count]; _seq[0] = _seq[Count]; _cell[0] = _cell[Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < Count && Less(l, m)) m = l;
                    if (r < Count && Less(r, m)) m = r;
                    if (m == i) break;
                    Swap(i, m);
                    i = m;
                }
            }
        }
    }
}
