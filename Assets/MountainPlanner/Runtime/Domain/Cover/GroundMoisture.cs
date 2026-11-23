using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>
    /// Task 4.28: how wet the ground is, one byte per 8 m cell of the surface grid (<see cref="SurfaceGrid"/>): 0 the driest
    /// ridge, <see cref="Neutral"/> an ordinary slope, 255 a stream bank. The grass draws from it (lush in the draws, straw
    /// on the ridges and the steep sun-facing slopes); nothing in the simulation reads it. From the cells' mean heights and the
    /// water bodies' mask only, so it works on any mountain and follows earthwork and ponds when rebuilt:
    /// - the water gathering downhill: a priority flood (pits filled) orders the cells, then each passes its area to every
    ///   lower neighbour in proportion to the drop, and the area against the slope gives a wetness index;
    /// - the cell above or below the land about 100 m round it (ridges dry, hollows wet);
    /// - the way a slope faces (the sun's side, south in the northern hemisphere, dries);
    /// - very steep ground sheds its water;
    /// - a green margin beside water.
    /// Integers only, ties broken by the order cells were reached: the same heights give the same bytes on every runtime.
    /// </summary>
    public static class GroundMoisture
    {
        public const byte Neutral = 128;
        /// <summary>The window of the ridge-or-hollow measure, cells each way (96 m).</summary>
        public const int RiseRadius = 12;
        /// <summary>How far the green margin beside water reaches, cells.</summary>
        public const int WaterReach = 6;

        // The four sides first, then the corners.
        static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 };
        static readonly int[] Dy = { 0, 1, 0, -1, 1, 1, -1, -1 };

        /// <summary>
        /// The map over <paramref name="width"/> × <paramref name="depth"/> cells (west to east, then south to north).
        /// <paramref name="mm"/>: mean heights, <see cref="SurfaceSampler.NoData"/> where none. <paramref name="water"/>: non-zero
        /// on water cells (null: none). (<paramref name="sunX1024"/>, <paramref name="sunY1024"/>): the unit vector toward the
        /// sun's side of the sky in grid east and north, × 1024 (see <see cref="SunSide"/>).
        /// </summary>
        public static byte[] Build(int width, int depth, int[] mm, byte[] water, int sunX1024, int sunY1024)
        {
            int n = width * depth;
            if (width <= 0 || depth <= 0 || mm == null || mm.Length != n) throw new ArgumentException("One height per cell.", nameof(mm));
            if (water != null && water.Length != n) throw new ArgumentException("A mask over the same cells.", nameof(water));

            // ---- the flood: every cell's filled level, the order reached and the cell it was reached from ----
            var level = new int[n];
            var parent = new int[n];
            var order = new int[n];       // cells in the order popped: levels never fall along it
            var pushed = new int[n];      // the cell of each push (the heap holds only keys)
            var seen = new bool[n];
            var heap = new KeyHeap(Math.Max(16, 2 * (width + depth)));
            int pushes = 0, pops = 0;
            for (int c = 0; c < n; c++)
                if (mm[c] == SurfaceSampler.NoData || (water != null && water[c] != 0)) Seed(c);
            for (int x = 0; x < width; x++) { Seed(x); Seed((depth - 1) * width + x); }
            for (int y = 1; y < depth - 1; y++) { Seed(y * width); Seed(y * width + width - 1); }
            while (heap.Count > 0)
            {
                int c = pushed[(int)(heap.Pop() & 0xFFFFFFFF)];
                order[pops++] = c;
                int cx = c % width, cy = c / width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + Dx[k], ny = cy + Dy[k];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= depth) continue;
                    int m = ny * width + nx;
                    if (seen[m]) continue;
                    seen[m] = true;
                    parent[m] = c;
                    level[m] = Math.Max(level[c], mm[m]);
                    Push(m);
                }
            }

            // ---- the area each cell gathers (1/256 cells), highest first: shared among the lower neighbours by their drop ----
            var area = new long[n];
            for (int c = 0; c < n; c++) area[c] = 256;
            var weight = new long[8];
            for (int p = n - 1; p >= 0; p--)
            {
                int c = order[p];
                if (parent[c] < 0) continue;   // an outlet: water, the edge, no data
                int cx = c % width, cy = c / width;
                long total = 0;
                for (int k = 0; k < 8; k++)
                {
                    weight[k] = 0;
                    int nx = cx + Dx[k], ny = cy + Dy[k];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= depth) continue;
                    long drop = (long)level[c] - level[ny * width + nx];
                    if (drop <= 0) continue;
                    weight[k] = k < 4 ? drop * 1024 : drop * 724;   // a corner is √2 further
                    total += weight[k];
                }
                if (total == 0) { area[parent[c]] += area[c]; continue; }   // a filled flat: on toward its outlet
                long a = area[c];
                for (int k = 0; k < 8; k++)
                    if (weight[k] > 0) area[(cy + Dy[k]) * width + cx + Dx[k]] += a * weight[k] / total;
            }

            // ---- ridge or hollow: the cell against the mean of the window round it ----
            int sw = width + 1;
            var sum = new long[sw * (depth + 1)];
            var cnt = new int[sw * (depth + 1)];
            for (int y = 0; y < depth; y++)
            {
                long row = 0; int rc = 0;
                for (int x = 0; x < width; x++)
                {
                    int v = mm[y * width + x];
                    if (v != SurfaceSampler.NoData) { row += v; rc++; }
                    sum[(y + 1) * sw + x + 1] = sum[y * sw + x + 1] + row;
                    cnt[(y + 1) * sw + x + 1] = cnt[y * sw + x + 1] + rc;
                }
            }

            // ---- how far each cell is from water (thirds of a cell, a 3-4 chamfer) ----
            int far = WaterReach * 3 + 1;
            var toWater = new int[n];
            for (int c = 0; c < n; c++) toWater[c] = water != null && water[c] != 0 ? 0 : far;
            if (water != null)
            {
                for (int y = 0; y < depth; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int c = y * width + x, d = toWater[c];
                        if (x > 0) d = Math.Min(d, toWater[c - 1] + 3);
                        if (y > 0)
                        {
                            d = Math.Min(d, toWater[c - width] + 3);
                            if (x > 0) d = Math.Min(d, toWater[c - width - 1] + 4);
                            if (x < width - 1) d = Math.Min(d, toWater[c - width + 1] + 4);
                        }
                        toWater[c] = d;
                    }
                for (int y = depth - 1; y >= 0; y--)
                    for (int x = width - 1; x >= 0; x--)
                    {
                        int c = y * width + x, d = toWater[c];
                        if (x < width - 1) d = Math.Min(d, toWater[c + 1] + 3);
                        if (y < depth - 1)
                        {
                            d = Math.Min(d, toWater[c + width] + 3);
                            if (x < width - 1) d = Math.Min(d, toWater[c + width + 1] + 4);
                            if (x > 0) d = Math.Min(d, toWater[c + width - 1] + 4);
                        }
                        toWater[c] = d;
                    }
            }

            // ---- the score ----
            var raw = new byte[n];
            int log100 = Log2Q8(100);
            for (int y = 0; y < depth; y++)
                for (int x = 0; x < width; x++)
                {
                    int c = y * width + x;
                    if (mm[c] == SurfaceSampler.NoData) { raw[c] = Neutral; continue; }
                    if (toWater[c] == 0) { raw[c] = 255; continue; }
                    // The slope over 16 m (one-sided at an edge or beside missing data): mm of rise per 16 m, east and north.
                    long gx = Gradient(mm, width, depth, x, y, 1, 0), gy = Gradient(mm, width, depth, x, y, 0, 1);
                    long g = WeatherMath.ISqrt(gx * gx + gy * gy);
                    int slope = (int)Math.Min(4000, g / 16);   // ‰
                    // Wetness index, log2 (cells gathered ÷ slope against a 10 % slope), in 1/256.
                    int index = Log2Q8(area[c]) - 8 * 256 - (Log2Q8(WeatherMath.Clamp(slope, 30, 1000)) - log100);
                    int points = WeatherMath.Clamp((index - 640) * 14 / 256, -40, 110);   // a flat field with little gathered reads ordinary, not dry
                    // Ridge (+) or hollow (−), metres: 4 points a metre.
                    int x0 = Math.Max(0, x - RiseRadius), x1 = Math.Min(width, x + RiseRadius + 1), y0 = Math.Max(0, y - RiseRadius), y1 = Math.Min(depth, y + RiseRadius + 1);
                    int k = cnt[y1 * sw + x1] - cnt[y0 * sw + x1] - cnt[y1 * sw + x0] + cnt[y0 * sw + x0];
                    long mean = (sum[y1 * sw + x1] - sum[y0 * sw + x1] - sum[y1 * sw + x0] + sum[y0 * sw + x0]) / Math.Max(1, k);
                    points -= (int)WeatherMath.Clamp((mm[c] - mean) / 250, -40L, 40L);
                    // The sun's side dries, the shaded side keeps its water; flat ground faces nowhere.
                    if (g > 0)
                    {
                        long sunward = (-gx * sunX1024 - gy * sunY1024) / g;   // ±1024
                        int facing = Ramp(slope, 80, 350);
                        points -= (int)((sunward > 0 ? 45 : 30) * sunward * facing / (1024 * 256));
                    }
                    points -= Ramp(slope, 500, 1000) * 30 / 256;
                    // The margin beside water.
                    if (toWater[c] < far) { int near = far - toWater[c]; points += 120 * near * near / (far * far); }
                    raw[c] = (byte)WeatherMath.Clamp(Neutral + points, 0, 254);
                }

            // ---- two 3 × 3 means: no 8 m squares, no one-cell lines ----
            var smooth = new byte[n];
            Blur(raw, smooth, mm, width, depth);
            Blur(smooth, raw, mm, width, depth);
            return raw;

            void Seed(int c)
            {
                if (seen[c]) return;
                seen[c] = true;
                parent[c] = -1;
                level[c] = mm[c];   // no data: below everything, so it floods first and holds nothing back
                Push(c);
            }

            void Push(int c)
            {
                pushed[pushes] = c;
                heap.Push(((ulong)((long)level[c] - int.MinValue) << 32) | (uint)pushes);
                pushes++;
            }
        }

        /// <summary>
        /// The unit vector (× 1024, grid east and north) toward the sun's side of the sky: true south in the northern
        /// hemisphere, true north in the southern, turned by the grid's convergence (degrees, as <see cref="Snowpack"/> takes it).
        /// </summary>
        public static (int X, int Y) SunSide(double latitudeDegrees, double gridConvergenceDegrees)
        {
            double gamma = gridConvergenceDegrees * Math.PI / 180;
            int sign = latitudeDegrees >= 0 ? -1 : 1;
            return ((int)Math.Round(sign * Math.Sin(gamma) * 1024), (int)Math.Round(sign * Math.Cos(gamma) * 1024));
        }

        /// <summary>The rise over two cells along (dx, dy), mm per 16 m; one-sided (doubled) where a neighbour is missing.</summary>
        static long Gradient(int[] mm, int width, int depth, int x, int y, int dx, int dy)
        {
            int c = y * width + x;
            int ax = x + dx, ay = y + dy, bx = x - dx, by = y - dy;
            bool a = ax < width && ay < depth && mm[ay * width + ax] != SurfaceSampler.NoData;
            bool b = bx >= 0 && by >= 0 && mm[by * width + bx] != SurfaceSampler.NoData;
            if (a && b) return (long)mm[ay * width + ax] - mm[by * width + bx];
            if (a) return 2L * ((long)mm[ay * width + ax] - mm[c]);
            if (b) return 2L * ((long)mm[c] - mm[by * width + bx]);
            return 0;
        }

        /// <summary>0 at or below <paramref name="from"/>, 256 at or above <paramref name="to"/>, straight between.</summary>
        static int Ramp(int v, int from, int to) => v <= from ? 0 : v >= to ? 256 : (v - from) * 256 / (to - from);

        /// <summary>log2(v) × 256 for v ≥ 1: the top bit's place, then the bits below it straight across the octave.</summary>
        public static int Log2Q8(long v)
        {
            if (v < 1) return 0;
            int top = 0;
            for (long t = v; t > 1; t >>= 1) top++;
            long part = top >= 8 ? (v >> (top - 8)) - 256 : (v << (8 - top)) - 256;
            return top * 256 + (int)part;
        }

        static void Blur(byte[] from, byte[] to, int[] mm, int width, int depth)
        {
            for (int y = 0; y < depth; y++)
                for (int x = 0; x < width; x++)
                {
                    int c = y * width + x;
                    if (mm[c] == SurfaceSampler.NoData) { to[c] = from[c]; continue; }
                    int total = 0, k = 0;
                    for (int j = Math.Max(0, y - 1); j <= Math.Min(depth - 1, y + 1); j++)
                        for (int i = Math.Max(0, x - 1); i <= Math.Min(width - 1, x + 1); i++)
                        {
                            int m = j * width + i;
                            if (mm[m] == SurfaceSampler.NoData) continue;
                            total += from[m]; k++;
                        }
                    to[c] = (byte)((total + k / 2) / k);
                }
        }

        /// <summary>A binary min-heap of keys (the level above, the push's number below): no ties left to chance.</summary>
        sealed class KeyHeap
        {
            ulong[] _keys;
            public int Count;

            public KeyHeap(int capacity) { _keys = new ulong[capacity]; }

            public void Push(ulong key)
            {
                if (Count == _keys.Length) Array.Resize(ref _keys, Count * 2);
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_keys[p] <= key) break;
                    _keys[i] = _keys[p];
                    i = p;
                }
                _keys[i] = key;
            }

            public ulong Pop()
            {
                ulong top = _keys[0], last = _keys[--Count];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1;
                    if (l >= Count) break;
                    if (l + 1 < Count && _keys[l + 1] < _keys[l]) l++;
                    if (_keys[l] >= last) break;
                    _keys[i] = _keys[l];
                    i = l;
                }
                _keys[i] = last;
                return top;
            }
        }
    }
}
