using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail's painted footprint on the edit lattice's 1 m cells (TR-02, task 08): each part's strokes stamped in order
    /// (paint sets a cell, erase clears it), clipped by the part's cuts, the parts unioned. A cell is in when its centre
    /// lies within a stroke's half width of the stroke's path. Unpainted holes are islands (TR-02, CL-08).
    ///
    /// Also holds what the rules read from the footprint: how many separate patches it makes (TF-02), each cell's
    /// clearance (the distance from its centre to the nearest cell outside, islands included, less half a cell: about
    /// the distance to the painted edge), and which lattice points are interior (all four cells round them in; only
    /// those may change height, GR-06). Built once per footprint, off the main thread; immutable. Deterministic (SH-04).
    /// </summary>
    public sealed class TrailFootprint
    {
        /// <summary>The first cell column and row of the footprint's box (one empty cell of margin all round).</summary>
        public readonly int Column0, Row0;
        public readonly int Width, Height;
        public readonly EditLattice Lattice;
        /// <summary>Where the design's local frame (TE5: metres from the site's centre) sits in the lattice's frame.</summary>
        public readonly double OriginX, OriginY;
        public readonly int Cells;
        /// <summary>Separate 4-connected patches (TF-02: a second one blocks Next).</summary>
        public readonly int Patches;
        /// <summary>The largest cell clearance, metres: about half the trail's widest painted width.</summary>
        public readonly double MaxClearance;

        readonly bool[] _in;
        readonly float[] _clear;

        TrailFootprint(EditLattice lattice, double ox, double oy, int c0, int r0, int w, int h, bool[] on)
        {
            Lattice = lattice;
            OriginX = ox;
            OriginY = oy;
            Column0 = c0;
            Row0 = r0;
            Width = w;
            Height = h;
            _in = on;
            int cells = 0;
            foreach (bool b in on) if (b) cells++;
            Cells = cells;
            Patches = CountPatches(on, w, h);
            _clear = Clearances(on, w, h, out double max);
            MaxClearance = max;
        }

        public double AreaSquareMetres => Cells;
        public bool IsEmpty => Cells == 0;

        /// <summary>The box's lattice point bounds (inclusive): every point a footprint cell touches.</summary>
        public int LastColumn => Column0 + Width;
        public int LastRow => Row0 + Height;

        /// <summary>Whether cell (column, row) of the lattice is painted.</summary>
        public bool Contains(int column, int row)
        {
            int i = column - Column0, j = row - Row0;
            return i >= 0 && j >= 0 && i < Width && j < Height && _in[j * Width + i];
        }

        /// <summary>Whether the cell under a lattice-frame position is painted.</summary>
        public bool ContainsAt(double x, double y)
        {
            Lattice.CellAt(x, y, out int c, out int r);
            return Contains(c, r);
        }

        /// <summary>Whether a design point (TE5 mm from the site's centre) lies on the footprint.</summary>
        public bool Contains(PointMm p) => ContainsAt(OriginX + p.XMetres, OriginY + p.YMetres);

        /// <summary>The cell's clearance in metres (0 outside).</summary>
        public double Clearance(int column, int row)
        {
            int i = column - Column0, j = row - Row0;
            return i >= 0 && j >= 0 && i < Width && j < Height ? _clear[j * Width + i] : 0;
        }

        /// <summary>GR-06: a lattice point may change height only when all four cells round it are painted.</summary>
        public bool Interior(int column, int row) =>
            Contains(column - 1, row - 1) && Contains(column, row - 1) && Contains(column - 1, row) && Contains(column, row);

        /// <summary>A point's distance to the painted edge, metres: from the nearest of its four cells (0 off the interior).</summary>
        public double PointClearance(int column, int row)
        {
            if (!Interior(column, row)) return 0;
            double a = Clearance(column - 1, row - 1), b = Clearance(column, row - 1), c = Clearance(column - 1, row), d = Clearance(column, row);
            return Math.Min(Math.Min(a, b), Math.Min(c, d)) + 0.5;
        }

        /// <summary>The centre of cell (column, row) in the lattice's frame.</summary>
        public double CellX(int column) => Lattice.West + column + 0.5;
        public double CellY(int row) => Lattice.North - row - 0.5;

        /// <summary>
        /// Rasterises a trail's parts (TR-02). <paramref name="originX"/>, <paramref name="originY"/> place the design's
        /// local frame in the lattice's (the site's centre, TE5).
        /// </summary>
        public static TrailFootprint Build(IReadOnlyList<FootprintPart> parts, EditLattice lattice, double originX, double originY)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p0 in parts)
                foreach (var s in p0.Strokes)
                {
                    if (s.Erase) continue;
                    double r = s.WidthMm / 2000.0;
                    foreach (var p in s.Points)
                    {
                        double x = originX + p.XMetres, y = originY + p.YMetres;
                        minX = Math.Min(minX, x - r); maxX = Math.Max(maxX, x + r);
                        minY = Math.Min(minY, y - r); maxY = Math.Max(maxY, y + r);
                    }
                }
            if (minX > maxX) return new TrailFootprint(lattice, originX, originY, 0, 0, 1, 1, new bool[1]);
            lattice.CellAt(minX, maxY, out int c0, out int r0);
            lattice.CellAt(maxX, minY, out int c1, out int r1);
            c0 -= 1; r0 -= 1; c1 += 1; r1 += 1;   // a cell of margin, so every painted cell has an outside neighbour in the box
            int w = c1 - c0 + 1, h = r1 - r0 + 1;
            if ((long)w * h > 64_000_000) throw new ArgumentException("The footprint is too large.");
            var on = new bool[w * h];
            var part = new bool[w * h];
            foreach (var fp in parts)
            {
                Array.Clear(part, 0, part.Length);
                foreach (var s in fp.Strokes)
                {
                    double r = s.WidthMm / 2000.0;
                    var pts = s.Points;
                    if (pts.Count == 0) continue;
                    for (int k = 0; k < Math.Max(1, pts.Count - 1); k++)
                    {
                        var a = pts[k];
                        var b = pts[Math.Min(k + 1, pts.Count - 1)];
                        Capsule(part, w, h, lattice, c0, r0, originX + a.XMetres, originY + a.YMetres, originX + b.XMetres, originY + b.YMetres, r, !s.Erase);
                    }
                }
                foreach (var cut in fp.Cuts)
                    for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++)
                        {
                            int at = j * w + i;
                            if (!part[at]) continue;
                            double lx = lattice.West + c0 + i + 0.5 - originX, ly = lattice.North - (r0 + j) - 0.5 - originY;
                            if (!cut.Keeps(lx, ly)) part[at] = false;
                        }
                for (int k = 0; k < on.Length; k++) on[k] |= part[k];
            }
            return new TrailFootprint(lattice, originX, originY, c0, r0, w, h, on);
        }

        static void Capsule(bool[] on, int w, int h, EditLattice lattice, int c0, int r0, double ax, double ay, double bx, double by, double r, bool paint)
        {
            int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r - lattice.West) - c0 - 1);
            int i1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r - lattice.West) - c0 + 1);
            int j0 = Math.Max(0, (int)Math.Floor(lattice.North - Math.Max(ay, by) - r) - r0 - 1);
            int j1 = Math.Min(h - 1, (int)Math.Ceiling(lattice.North - Math.Min(ay, by) + r) - r0 + 1);
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy, r2 = r * r;
            for (int j = j0; j <= j1; j++)
            {
                double y = lattice.North - (r0 + j) - 0.5;
                for (int i = i0; i <= i1; i++)
                {
                    double x = lattice.West + c0 + i + 0.5;
                    double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
                    double ex = ax + t * dx - x, ey = ay + t * dy - y;
                    if (ex * ex + ey * ey <= r2) on[j * w + i] = paint;
                }
            }
        }

        static int CountPatches(bool[] on, int w, int h)
        {
            var seen = new bool[on.Length];
            var queue = new int[on.Length];
            int patches = 0;
            for (int start = 0; start < on.Length; start++)
            {
                if (!on[start] || seen[start]) continue;
                patches++;
                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int c = queue[head++], x = c % w, y = c / w;
                    if (x > 0 && on[c - 1] && !seen[c - 1]) { seen[c - 1] = true; queue[tail++] = c - 1; }
                    if (x < w - 1 && on[c + 1] && !seen[c + 1]) { seen[c + 1] = true; queue[tail++] = c + 1; }
                    if (y > 0 && on[c - w] && !seen[c - w]) { seen[c - w] = true; queue[tail++] = c - w; }
                    if (y < h - 1 && on[c + w] && !seen[c + w]) { seen[c + w] = true; queue[tail++] = c + w; }
                }
            }
            return patches;
        }

        /// <summary>
        /// The exact Euclidean distance transform (Felzenszwalb and Huttenlocher) from each painted cell's centre to the
        /// nearest unpainted one, less half a cell.
        /// </summary>
        static float[] Clearances(bool[] on, int w, int h, out double max)
        {
            // Only the painted extent of each column and row is transformed: beyond it every cell is unpainted (distance
            // 0), and the unpainted cell just past either end is always nearer than any farther one, so the result is
            // exactly the whole grid's. A diagonal run's box is mostly empty, so this is most of the speed (task 08).
            const double Inf = 1e20;
            var colLo = new int[w];
            var colHi = new int[w];
            var rowLo = new int[h];
            var rowHi = new int[h];
            for (int i = 0; i < w; i++) { colLo[i] = int.MaxValue; colHi[i] = -1; }
            for (int j = 0; j < h; j++)
            {
                rowLo[j] = int.MaxValue;
                rowHi[j] = -1;
                for (int i = 0; i < w; i++)
                {
                    if (!on[j * w + i]) continue;
                    if (i < rowLo[j]) rowLo[j] = i;
                    rowHi[j] = i;
                    if (j < colLo[i]) colLo[i] = j;
                    colHi[i] = j;
                }
            }
            var d2 = new double[w * h];
            int n = Math.Max(w, h) + 2;
            var f = new double[n];
            var outv = new double[n];
            var v = new int[n];
            var z = new double[n + 1];
            for (int i = 0; i < w; i++)
            {
                if (colHi[i] < 0) continue;
                int a0 = Math.Max(0, colLo[i] - 1), b0 = Math.Min(h - 1, colHi[i] + 1), m = b0 - a0 + 1;
                for (int t = 0; t < m; t++) f[t] = on[(a0 + t) * w + i] ? Inf : 0;
                Transform(f, m, outv, v, z);
                for (int t = 0; t < m; t++) d2[(a0 + t) * w + i] = outv[t];
            }
            var clear = new float[w * h];
            max = 0;
            for (int j = 0; j < h; j++)
            {
                if (rowHi[j] < 0) continue;
                int a0 = Math.Max(0, rowLo[j] - 1), b0 = Math.Min(w - 1, rowHi[j] + 1), m = b0 - a0 + 1;
                for (int t = 0; t < m; t++) f[t] = d2[j * w + a0 + t];
                Transform(f, m, outv, v, z);
                for (int t = 0; t < m; t++)
                {
                    int k = j * w + a0 + t;
                    if (!on[k]) continue;
                    double c = Math.Sqrt(outv[t]) - 0.5;
                    clear[k] = (float)c;
                    if (c > max) max = c;
                }
            }
            return clear;
        }

        // One dimension of the squared distance transform: the lower envelope of parabolas.
        static void Transform(double[] f, int n, double[] d, int[] v, double[] z)
        {
            const double Inf = 1e30;
            int k = 0;
            v[0] = 0;
            z[0] = -Inf;
            z[1] = Inf;
            for (int q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    int p = v[k];
                    s = ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * q - 2.0 * p);
                    if (s <= z[k] && k > 0) { k--; continue; }
                    if (s <= z[k]) { s = z[k]; }
                    break;
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = Inf;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }
    }
}
