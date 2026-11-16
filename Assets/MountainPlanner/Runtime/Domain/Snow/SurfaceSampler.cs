using System;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>One tile's samples as the sampler reads them: <see cref="Resolution"/> × <see cref="Resolution"/> values spanning
    /// the tile edge to edge, row 0 at its south edge. Heights in whole mm (<see cref="SurfaceSampler.NoData"/> for none), or a
    /// cover texel's forest-floor weight (0–255).</summary>
    public interface ITileSamples
    {
        int Resolution { get; }
        int Value(int column, int row);
    }

    /// <summary>
    /// SN-01 v2 (task 4.07 PR A2): the one 8 m sampler, shared by the opener, the edit pipeline and the tests, so all three
    /// build the same bytes. A cell is the mean of every post (and canopy texel) that falls in it, not the one post nearest its
    /// centre: the nearest post let a metre of lidar relief alias into the slopes the snow reads. A sample exactly on a cell
    /// edge counts half to each side (a quarter on a corner), so a cell's mean sits on its centre whatever the post spacing.
    /// Each tile counts its samples up to but not on its own east and north edges (the neighbouring tile has them) unless it
    /// is the last tile that way. Integer sums and one rounding: the same bytes on every runtime.
    /// </summary>
    public sealed class SurfaceSampler
    {
        public const int NoData = int.MinValue;

        readonly int _width, _depth;
        readonly long[] _sum;
        readonly int[] _weight, _canopySum, _canopyWeight;

        /// <summary>The 8 m grid: <paramref name="width"/> × <paramref name="depth"/> cells from the ring's south-west corner.</summary>
        public SurfaceSampler(int width, int depth)
        {
            if (width < 1 || depth < 1 || (long)width * depth > 64_000_000) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width; _depth = depth;
            int n = width * depth;
            _sum = new long[n]; _weight = new int[n]; _canopySum = new int[n]; _canopyWeight = new int[n];
        }

        public int Width => _width;
        public int Depth => _depth;

        /// <summary>Forgets the heights' sums (and the canopy's, when asked) in a rectangle of cells, before its tiles are added
        /// again after an edit.</summary>
        public void Clear(int i0, int j0, int i1, int j1, bool canopy)
        {
            Clip(ref i0, ref j0, ref i1, ref j1);
            for (int j = j0; j < j1; j++)
            {
                int row = j * _width;
                Array.Clear(_sum, row + i0, i1 - i0);
                Array.Clear(_weight, row + i0, i1 - i0);
                if (!canopy) continue;
                Array.Clear(_canopySum, row + i0, i1 - i0);
                Array.Clear(_canopyWeight, row + i0, i1 - i0);
            }
        }

        void Clip(ref int i0, ref int j0, ref int i1, ref int j1)
        {
            i0 = Math.Max(0, i0); j0 = Math.Max(0, j0);
            i1 = Math.Min(_width, i1); j1 = Math.Min(_depth, j1);
            if (i1 < i0) i1 = i0;
            if (j1 < j0) j1 = j0;
        }

        /// <summary>
        /// Adds one tile's height posts to the cells of [i0, i1) × [j0, j1) they fall in. The tile's south-west corner sits
        /// <paramref name="westMm"/>, <paramref name="southMm"/> from the ring's south-west corner (mm, either side), and it is
        /// <paramref name="tileMm"/> on a side. <paramref name="lastEast"/>, <paramref name="lastNorth"/>: no tile lies beyond
        /// that edge, so the tile's own edge samples count.
        /// </summary>
        public void AddPosts<T>(in T posts, long westMm, long southMm, long tileMm, bool lastEast, bool lastNorth,
                                int i0, int j0, int i1, int j1) where T : ITileSamples =>
            Add(posts, westMm, southMm, tileMm, lastEast, lastNorth, i0, j0, i1, j1, _sum, _weight, null, null);

        /// <summary>As <see cref="AddPosts{T}"/>, for a tile's canopy texels.</summary>
        public void AddCanopy<T>(in T cover, long westMm, long southMm, long tileMm, bool lastEast, bool lastNorth,
                                 int i0, int j0, int i1, int j1) where T : ITileSamples =>
            Add(cover, westMm, southMm, tileMm, lastEast, lastNorth, i0, j0, i1, j1, null, null, _canopySum, _canopyWeight);

        const long CellMm = SurfaceGrid.CellMetres * 1000L;

        void Add<T>(in T s, long westMm, long southMm, long tileMm, bool lastEast, bool lastNorth, int i0, int j0, int i1, int j1,
                    long[] sum, int[] weight, int[] isum, int[] iweight) where T : ITileSamples
        {
            int res = s.Resolution;
            if (res < 2 || tileMm <= 0) return;
            Clip(ref i0, ref j0, ref i1, ref j1);
            if (i1 <= i0 || j1 <= j0) return;
            int span = res - 1;
            // The samples that can touch the rectangle (one past each side, for the shared edges).
            int c0 = Math.Max(0, First(westMm, tileMm, span, i0) - 1), c1 = Math.Min(lastEast ? res : span, First(westMm, tileMm, span, i1) + 1);
            int r0 = Math.Max(0, First(southMm, tileMm, span, j0) - 1), r1 = Math.Min(lastNorth ? res : span, First(southMm, tileMm, span, j1) + 1);
            for (int r = r0; r < r1; r++)
            {
                Cells(southMm, tileMm, span, r, out int ja, out int jb);
                for (int c = c0; c < c1; c++)
                {
                    Cells(westMm, tileMm, span, c, out int ia, out int ib);
                    int v = s.Value(c, r);
                    if (v == NoData) continue;
                    // jb, ib: the second cell of a sample on an edge (−1: none). Each half weighs 1, a whole sample 2 a side.
                    AddOne(ia, ja, ib < 0 ? 2 : 1, jb < 0 ? 2 : 1, v, i0, j0, i1, j1, sum, weight, isum, iweight);
                    if (ib >= 0) AddOne(ib, ja, 1, jb < 0 ? 2 : 1, v, i0, j0, i1, j1, sum, weight, isum, iweight);
                    if (jb >= 0) AddOne(ia, jb, ib < 0 ? 2 : 1, 1, v, i0, j0, i1, j1, sum, weight, isum, iweight);
                    if (ib >= 0 && jb >= 0) AddOne(ib, jb, 1, 1, v, i0, j0, i1, j1, sum, weight, isum, iweight);
                }
            }
        }

        void AddOne(int i, int j, int wx, int wy, int v, int i0, int j0, int i1, int j1, long[] sum, int[] weight, int[] isum, int[] iweight)
        {
            if (i < i0 || i >= i1 || j < j0 || j >= j1) return;
            int k = j * _width + i, w = wx * wy;
            if (sum != null) { sum[k] += (long)v * w; weight[k] += w; }
            else { isum[k] += v * w; iweight[k] += w; }
        }

        /// <summary>The cell (along one axis) sample <paramref name="k"/> falls in, at origin + k × tile ÷ span; a sample exactly
        /// on an edge is in both cells (<paramref name="b"/> the second, else −1).</summary>
        static void Cells(long originMm, long tileMm, int span, int k, out int a, out int b)
        {
            long pos = originMm * span + k * tileMm, cell = CellMm * span;   // both × span: exact
            long q = FloorDiv(pos, cell);
            if (pos - q * cell == 0) { a = (int)q - 1; b = (int)q; }
            else { a = (int)q; b = -1; }
        }

        /// <summary>The first sample index at or past cell <paramref name="cell"/>'s near edge.</summary>
        static int First(long originMm, long tileMm, int span, int cell)
        {
            long num = (cell * CellMm - originMm) * span;
            long k = -FloorDiv(-num, tileMm);   // ceiling
            return (int)Math.Max(int.MinValue / 2, Math.Min(int.MaxValue / 2, k));
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>The cells' mean heights (mm; <see cref="NoData"/> where no post fell) over [i0, i1) × [j0, j1) into
        /// <paramref name="mm"/> (the whole grid's array).</summary>
        public void Heights(int[] mm, int i0 = 0, int j0 = 0, int i1 = int.MaxValue, int j1 = int.MaxValue)
        {
            if (mm == null || mm.Length != _width * _depth) throw new ArgumentException("One height per cell.", nameof(mm));
            Clip(ref i0, ref j0, ref i1, ref j1);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    int k = j * _width + i, n = _weight[k];
                    mm[k] = n == 0 ? NoData : (int)DivRound(_sum[k], n);
                }
        }

        /// <summary>The cells' mean canopy (0–255; 0 where no texel fell), as <see cref="Heights"/>.</summary>
        public void Canopy(byte[] canopy, int i0 = 0, int j0 = 0, int i1 = int.MaxValue, int j1 = int.MaxValue)
        {
            if (canopy == null || canopy.Length != _width * _depth) throw new ArgumentException("One share per cell.", nameof(canopy));
            Clip(ref i0, ref j0, ref i1, ref j1);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++)
                {
                    int k = j * _width + i, n = _canopyWeight[k];
                    canopy[k] = n == 0 ? (byte)0 : (byte)((_canopySum[k] + n / 2) / n);
                }
        }

        static long DivRound(long a, long b) => a >= 0 ? (a + b / 2) / b : -((-a + b / 2) / b);
    }
}
