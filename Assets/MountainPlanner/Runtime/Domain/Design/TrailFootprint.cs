using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail's painted footprint on the edit lattice's 1 m cells (TR-02, task 08): its clearing's shape (Shape 8,
    /// <see cref="TrailShape"/>) read at each cell's centre. Unpainted holes are islands (TR-02, CL-08).
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

        /// <summary>The box's cells in row order (painted or not) and their clearances, for the centreline's search.</summary>
        internal bool[] Mask => _in;
        internal float[] ClearanceGrid => _clear;
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
        /// A trail's clearing on the lattice (sections finding 7, Shape 8): a 1 m cell is in when the clearing's 250 mm cell
        /// under its centre is. <paramref name="originX"/>, <paramref name="originY"/> place the design's local frame in the
        /// lattice's (the site's centre, TE5).
        /// </summary>
        public static TrailFootprint Build(TrailShape shape, EditLattice lattice, double originX, double originY)
        {
            if (shape == null) throw new ArgumentNullException(nameof(shape));
            if (shape.IsEmpty) return new TrailFootprint(lattice, originX, originY, 0, 0, 1, 1, new bool[1]);
            shape.Bounds(out int x0, out int y0, out int x1, out int y1);
            lattice.CellAt(originX + x0 / 1000.0, originY + y1 / 1000.0, out int c0, out int r0);
            lattice.CellAt(originX + x1 / 1000.0, originY + y0 / 1000.0, out int c1, out int r1);
            c0 -= 1; r0 -= 1; c1 += 1; r1 += 1;   // a cell of margin, so every painted cell has an outside neighbour in the box
            int w = c1 - c0 + 1, h = r1 - r0 + 1;
            if ((long)w * h > 64_000_000) throw new ArgumentException("The footprint is too large.");
            var on = new bool[w * h];
            var mask = shape.Mask;
            const double PerMetre = 1000.0 / TrailMask.CellMm;
            for (int j = 0; j < h; j++)
            {
                double ly = lattice.North - (r0 + j) - 0.5 - originY;
                var runs = mask.Row((int)Math.Floor(ly * PerMetre + 0.5));
                if (runs.Count == 0) continue;
                int s = 0;
                for (int i = 0; i < w; i++)
                {
                    double lx = lattice.West + c0 + i + 0.5 - originX;
                    int fi = (int)Math.Floor(lx * PerMetre + 0.5);
                    while (s < runs.Count && runs[s + 1] <= fi) s += 2;
                    if (s >= runs.Count) break;
                    if (fi >= runs[s]) on[j * w + i] = true;
                }
            }
            return new TrailFootprint(lattice, originX, originY, c0, r0, w, h, on);
        }

        /// <summary>
        /// Each box cell's patch, in row order: 0 unpainted, else 1, 2, … in the order the patches' first cells come.
        /// <paramref name="largest"/> is the patch with the most cells (the lower label on a tie): the trail; any other is
        /// a patch apart (TF-02, shown red while painting).
        /// </summary>
        public int[] PatchLabels(out int largest)
        {
            var label = new int[_in.Length];
            var queue = new int[_in.Length];
            int w = Width, h = Height, patches = 0, bestCount = 0;
            largest = 0;
            for (int start = 0; start < _in.Length; start++)
            {
                if (!_in[start] || label[start] != 0) continue;
                patches++;
                int head = 0, tail = 0;
                queue[tail++] = start;
                label[start] = patches;
                while (head < tail)
                {
                    int c = queue[head++], x = c % w, y = c / w;
                    if (x > 0 && _in[c - 1] && label[c - 1] == 0) { label[c - 1] = patches; queue[tail++] = c - 1; }
                    if (x < w - 1 && _in[c + 1] && label[c + 1] == 0) { label[c + 1] = patches; queue[tail++] = c + 1; }
                    if (y > 0 && _in[c - w] && label[c - w] == 0) { label[c - w] = patches; queue[tail++] = c - w; }
                    if (y < h - 1 && _in[c + w] && label[c + w] == 0) { label[c + w] = patches; queue[tail++] = c + w; }
                }
                if (tail > bestCount) { bestCount = tail; largest = patches; }
            }
            return label;
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
