using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// TR-03's automatic centreline (owner, TL1): the path from the head to the tail through the footprint's cells that
    /// stays as far from the edges and islands as it can, then smoothed. Each step between neighbouring cells costs its
    /// length times 1 + (W / clearance)², where W is the footprint's widest clearance, so the path keeps to the middle
    /// at any brush width and goes round islands by the wider side. Ties go to the lower cell index (SH-04).
    /// Positions are metres in the edit lattice's frame, head first.
    /// </summary>
    public static class TrailCentreline
    {
        public sealed class Result
        {
            /// <summary>The smoothed centreline, x, y pairs, head first. Empty when no path joins the ends.</summary>
            public double[] Points { get; internal set; }
            public double LengthMetres { get; internal set; }
            /// <summary>Whether the head or tail lay off the footprint and was moved to its nearest painted cell.</summary>
            public bool HeadMoved { get; internal set; }
            public bool TailMoved { get; internal set; }
            public bool Found => Points != null && Points.Length >= 4;
        }

        public static Result Find(TrailFootprint fp, double headX, double headY, double tailX, double tailY)
        {
            if (fp == null) throw new ArgumentNullException(nameof(fp));
            var result = new Result { Points = Array.Empty<double>() };
            if (fp.IsEmpty) return result;
            int w = fp.Width, h = fp.Height;
            int start = NearestCell(fp, headX, headY, out bool headMoved);
            int goal = NearestCell(fp, tailX, tailY, out bool tailMoved);
            result.HeadMoved = headMoved;
            result.TailMoved = tailMoved;
            if (start < 0 || goal < 0 || start == goal) return result;

            double W = Math.Max(1, fp.MaxClearance);
            // The painted cells only, compactly (a diagonal run's box is mostly empty): idx maps a box cell to its slot.
            var idx = new int[w * h];
            var box = new int[fp.Cells];
            var weight = new double[fp.Cells];
            int m = 0;
            var mask = fp.Mask;
            var clear = fp.ClearanceGrid;
            for (int at = 0; at < w * h; at++)
            {
                if (!mask[at]) { idx[at] = -1; continue; }
                double q = W / Math.Max(0.5, clear[at]);
                idx[at] = m;
                box[m] = at;
                weight[m] = 1 + q * q;
                m++;
            }

            var cost = new double[m];
            var from = new int[m];
            for (int k = 0; k < m; k++) { cost[k] = double.PositiveInfinity; from[k] = -1; }
            var heap = new MinHeap(1024);
            int s0 = idx[start], g0 = idx[goal];
            cost[s0] = 0;
            heap.Push(0, s0);
            const double Diag = 1.4142135623730951;
            while (heap.Count > 0)
            {
                heap.Pop(out double c0, out int slot);
                if (c0 > cost[slot]) continue;
                if (slot == g0) break;
                int at = box[slot], x = at % w, y = at / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ns = idx[ny * w + nx];
                        if (ns < 0) continue;
                        // A diagonal step needs both its side cells painted, so the path never cuts a corner off the footprint.
                        if (dx != 0 && dy != 0 && (idx[y * w + nx] < 0 || idx[ny * w + x] < 0)) continue;
                        double step = (dx != 0 && dy != 0 ? Diag : 1) * 0.5 * (weight[slot] + weight[ns]);
                        double next = c0 + step;
                        if (next < cost[ns] || (next == cost[ns] && box[slot] < (from[ns] < 0 ? int.MaxValue : box[from[ns]])))
                        {
                            cost[ns] = next;
                            from[ns] = slot;
                            heap.Push(next, ns);
                        }
                    }
            }
            if (double.IsInfinity(cost[g0])) return result;

            var cells = new List<int>();
            for (int k = g0; k >= 0; k = from[k])
            {
                cells.Add(box[k]);
                if (k == s0) break;
            }
            cells.Reverse();
            int count = cells.Count;
            var raw = new double[count * 2];
            for (int k = 0; k < count; k++)
            {
                raw[2 * k] = fp.CellX(fp.Column0 + cells[k] % w);
                raw[2 * k + 1] = fp.CellY(fp.Row0 + cells[k] / w);
            }
            // The ends sit where the player put them when that's on the footprint.
            if (!headMoved) { raw[0] = headX; raw[1] = headY; }
            if (!tailMoved) { raw[2 * count - 2] = tailX; raw[2 * count - 1] = tailY; }
            result.Points = Simplify(fp, Smooth(fp, raw, (int)Math.Round(Math.Max(2, Math.Min(30, W)))), SimplifyMetres);
            double len = 0;
            for (int k = 1; k < result.Points.Length / 2; k++)
            {
                double ex = result.Points[2 * k] - result.Points[2 * k - 2], ey = result.Points[2 * k + 1] - result.Points[2 * k - 1];
                len += Math.Sqrt(ex * ex + ey * ey);
            }
            result.LengthMetres = len;
            return result;
        }

        /// <summary>
        /// A moving average over about the footprint's widest clearance, the window shrinking at the ends so they stay put.
        /// A point that smoothing would move off the footprint, or much nearer its edge, keeps its place.
        /// </summary>
        static double[] Smooth(TrailFootprint fp, double[] raw, int k)
        {
            int n = raw.Length / 2;
            var outp = new double[raw.Length];
            for (int i = 0; i < n; i++)
            {
                int win = Math.Min(k, Math.Min(i, n - 1 - i));
                double sx = 0, sy = 0;
                for (int t = i - win; t <= i + win; t++) { sx += raw[2 * t]; sy += raw[2 * t + 1]; }
                double x = sx / (2 * win + 1), y = sy / (2 * win + 1);
                fp.Lattice.CellAt(raw[2 * i], raw[2 * i + 1], out int c0, out int r0);
                fp.Lattice.CellAt(x, y, out int c1, out int r1);
                bool keep = fp.Contains(c1, r1) && fp.Clearance(c1, r1) >= 0.5 * fp.Clearance(c0, r0);
                outp[2 * i] = keep ? x : raw[2 * i];
                outp[2 * i + 1] = keep ? y : raw[2 * i + 1];
            }
            return outp;
        }

        /// <summary>How far simplifying may move the smoothed line: a straight trail comes out straight, not stepped by cells.</summary>
        public const double SimplifyMetres = 0.75;

        /// <summary>
        /// Douglas–Peucker: drops points within <paramref name="tolerance"/> of the line through their neighbours, keeping
        /// any whose removal would take a segment off the footprint. The ends always stay.
        /// </summary>
        static double[] Simplify(TrailFootprint fp, double[] pts, double tolerance)
        {
            int n = pts.Length / 2;
            if (n <= 2) return pts;
            var keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            var stack = new Stack<(int a, int b)>();
            stack.Push((0, n - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (b - a < 2) continue;
                double ax = pts[2 * a], ay = pts[2 * a + 1], bx = pts[2 * b], by = pts[2 * b + 1];
                double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
                int worst = -1;
                double far = -1;
                for (int i = a + 1; i < b; i++)
                {
                    double px = pts[2 * i] - ax, py = pts[2 * i + 1] - ay;
                    double d = len > 1e-9 ? Math.Abs(px * dy - py * dx) / len : Math.Sqrt(px * px + py * py);
                    if (d > far) { far = d; worst = i; }
                }
                if (far > tolerance || !OnFootprint(fp, ax, ay, bx, by))
                {
                    keep[worst] = true;
                    stack.Push((a, worst));
                    stack.Push((worst, b));
                }
            }
            var outp = new List<double>();
            for (int i = 0; i < n; i++) if (keep[i]) { outp.Add(pts[2 * i]); outp.Add(pts[2 * i + 1]); }
            return outp.ToArray();
        }

        static bool OnFootprint(TrailFootprint fp, double ax, double ay, double bx, double by)
        {
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int steps = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                if (!fp.ContainsAt(ax + (bx - ax) * t, ay + (by - ay) * t)) return false;
            }
            return true;
        }

        /// <summary>The painted cell under a point, or the nearest one (lowest index on ties); -1 on an empty footprint.</summary>
        static int NearestCell(TrailFootprint fp, double x, double y, out bool moved)
        {
            fp.Lattice.CellAt(x, y, out int c, out int r);
            moved = false;
            if (fp.Contains(c, r)) return (r - fp.Row0) * fp.Width + (c - fp.Column0);
            moved = true;
            int best = -1;
            double bestD = double.MaxValue;
            for (int j = 0; j < fp.Height; j++)
                for (int i = 0; i < fp.Width; i++)
                {
                    if (!fp.Contains(fp.Column0 + i, fp.Row0 + j)) continue;
                    double dx = fp.CellX(fp.Column0 + i) - x, dy = fp.CellY(fp.Row0 + j) - y, d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; best = j * fp.Width + i; }
                }
            return best;
        }

        /// <summary>A binary min-heap of (cost, cell), ties by the lower cell.</summary>
        sealed class MinHeap
        {
            double[] _k;
            int[] _v;
            public int Count { get; private set; }
            public MinHeap(int capacity) { _k = new double[capacity]; _v = new int[capacity]; }

            static bool Less(double ka, int va, double kb, int vb) => ka < kb || (ka == kb && va < vb);

            public void Push(double key, int value)
            {
                if (Count == _k.Length) { Array.Resize(ref _k, Count * 2); Array.Resize(ref _v, Count * 2); }
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (!Less(key, value, _k[p], _v[p])) break;
                    _k[i] = _k[p]; _v[i] = _v[p];
                    i = p;
                }
                _k[i] = key; _v[i] = value;
            }

            public void Pop(out double key, out int value)
            {
                key = _k[0]; value = _v[0];
                double lk = _k[--Count];
                int lv = _v[Count];
                int i = 0;
                while (true)
                {
                    int a = 2 * i + 1;
                    if (a >= Count) break;
                    int b = a + 1;
                    int m = b < Count && Less(_k[b], _v[b], _k[a], _v[a]) ? b : a;
                    if (!Less(_k[m], _v[m], lk, lv)) break;
                    _k[i] = _k[m]; _v[i] = _v[m];
                    i = m;
                }
                _k[i] = lk; _v[i] = lv;
            }
        }
    }
}
