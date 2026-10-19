using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    public static partial class TrailGrader
    {
        /// <summary>A footprint grade (task 08): the writes, and what review, the profile and the map read.</summary>
        public sealed class Graded
        {
            public EarthworkWrites Writes { get; internal set; }
            /// <summary>The stations, head first, as x, y pairs in the lattice's frame (TR-03).</summary>
            public double[] Stations { get; internal set; }
            /// <summary>Each station's distance along the centreline, metres.</summary>
            public double[] Along { get; internal set; }
            /// <summary>The natural ground at each station (the base heights, before this trail).</summary>
            public double[] Ground { get; internal set; }
            /// <summary>Whether each station holds a bench (GR-04); false is drawn red (GR-07). All false on a Natural trail.</summary>
            public bool[] Benched { get; internal set; }
            /// <summary>Each benched station's bench width left and right of the centreline, metres (0 where unbenched).</summary>
            public double[] BenchLeft { get; internal set; }
            public double[] BenchRight { get; internal set; }
            public int UngradedStations { get; internal set; }
            public double UngradedMetres { get; internal set; }
            public double LengthMetres { get; internal set; }
            public double CutCubicMetres { get; internal set; }
            public double FillCubicMetres { get; internal set; }
            public int ClearedCells { get; internal set; }
            public bool IsGraded { get; internal set; }
        }

        /// <summary>
        /// Grades a painted trail (GR-01 to GR-13) over <paramref name="current"/>, the ground as it is before this trail:
        /// stations every 25 ft along <paramref name="centreline"/>; at each, a level bench at the centreline's ground
        /// (GR-01) as wide as GR-03 v2 allows on each side, the side's budget running along the section to the painted
        /// edge or an island; 1:1 faces (GR-02), rounded (GR-12 v2); then every changed point clamped between the 1:1 slopes
        /// rising from the fixed ground round it (GR-13: the footprint's boundary, islands, points
        /// <paramref name="isFixed"/> keeps, ungraded stretches), so the faces meet the ground at every edge. Only the
        /// footprint's interior points are written (GR-06). A Natural trail (TF-06) writes no heights. Clearing follows
        /// CL-01, CL-02 (the ragged band, measured from the edge, islands included) and CL-08 (islands keep their trees).
        /// </summary>
        public static Graded GradeFootprint(EditedHeights current, TrailFootprint fp, IReadOnlyList<double> centreline, bool graded,
                                            ulong seed, TrailId id, Func<int, int, bool> isFixed = null, double roundMetres = RoundingMetres)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (fp == null) throw new ArgumentNullException(nameof(fp));
            var lattice = current.Lattice;
            var writes = new EarthworkWrites();
            var result = new Graded { Writes = writes, IsGraded = graded };

            // Stations (TR-03) and their sections.
            var line = Stations(centreline, out double length);
            int count = line.Count / 2;
            var along = new double[count];
            var z = new double[count];
            var left = new double[count];
            var right = new double[count];
            var ok = new bool[count];
            int ungraded = 0;
            double walked = 0;
            for (int k = 0; k < count; k++)
            {
                if (k > 0) walked += Dist(line[2 * k - 2], line[2 * k - 1], line[2 * k], line[2 * k + 1]);
                along[k] = walked;
                double cx = line[2 * k], cy = line[2 * k + 1];
                Tangent(line, k, out double ex, out double ey);
                double nx = -ey, ny = ex;
                z[k] = current.At(cx, cy);
                if (graded && fp.ContainsAt(cx, cy))
                {
                    left[k] = Side(current, cx, cy, nx, ny, Budget(fp, cx, cy, nx, ny), z[k]);
                    right[k] = Side(current, cx, cy, -nx, -ny, Budget(fp, cx, cy, -nx, -ny), z[k]);
                    ok[k] = left[k] + right[k] >= MinBenchMetres;
                }
                if (graded && !ok[k]) ungraded++;
            }
            // Proposed GR-05 v2: the bench's width follows its neighbours. Each side takes the narrowest GR-03 width within
            // two stations, then the average of those over two stations, so it can never be wider than GR-03 allows at the
            // station, and lidar roughness doesn't step the faces in and out every 25 ft.
            if (graded) { Smooth(left, ok); Smooth(right, ok); }
            result.Stations = line.ToArray();
            result.Along = along;
            result.Ground = (double[])z.Clone();
            result.Benched = ok;
            result.BenchLeft = new double[count];
            result.BenchRight = new double[count];
            for (int k = 0; k < count; k++)
                if (ok[k]) { result.BenchLeft[k] = left[k]; result.BenchRight[k] = right[k]; }
            result.LengthMetres = length;
            result.UngradedStations = ungraded;
            result.UngradedMetres = count > 1 ? length * ungraded / count : 0;
            // The footprint's lattice points, kept compact: per point row, only the span round its paint (a diagonal run's
            // box is mostly empty), within three cells. Located on the centreline, then their design heights.
            int pc0 = fp.Column0, pr0 = fp.Row0, pw = fp.Width + 1, ph = fp.Height + 1;
            var grid = new SpanGrid(fp, pw, ph, 3);
            int n = grid.Count;
            bool any = false;
            var design = graded ? new double[n] : null;
            var ground = graded ? new double[n] : null;
            var free = graded ? new bool[n] : null;   // a point GR-13 may clamp
            var near = graded ? new bool[n] : null;   // within two cells of the paint: GR-13's fixed ground
            var claim = new bool[n];
            Located point = null;
            if (graded)
            {
                for (int k = 0; k < count && !any; k++) any = ok[k];
                double reach = 1.5 * fp.MaxClearance + 3;
                point = any ? LocateSpans(line, along, lattice, pc0, pr0, grid, reach) : null;
                MarkNear(fp, grid, near, 2);
            }
            for (int j = 0; j < ph; j++)
            {
                if (grid.Lo[j] > grid.Hi[j]) continue;
                // A span's ground in one read (exactly AtPoint each, SH-02), then only the points near the paint keep it.
                if (graded) current.AtPointsRow(pc0 + grid.Lo[j], pr0 + j, grid.Hi[j] - grid.Lo[j] + 1, ground, grid.Offset[j]);
                for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                {
                    int c = pc0 + i, r = pr0 + j, at = grid.Index(i, j);
                    bool mine = fp.Interior(c, r) && (isFixed == null || !isFixed(c, r));
                    if (mine) { writes.Claim(c, r); claim[at] = true; }
                    if (graded && !near[at]) ground[at] = double.NaN;
                }
            }
            for (int j = 0; j < ph && graded; j++)
                for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                {
                    int c = pc0 + i, r = pr0 + j, at = grid.Index(i, j);
                    double g = ground[at];
                    design[at] = g;
                    if (!claim[at] || point == null || point.Dist[at] == double.MaxValue) continue;
                    double pcl = fp.PointClearance(c, r);
                    double u = point.Offset[at];
                    // The ground where this point's line out meets the edge: bilinear on the lattice (SH-02), from the
                    // points already read where it can.
                    double fi = i + point.OutX[at] * pcl, fj = j - point.OutY[at] * pcl;
                    double edge = grid.Bilinear(ground, fi, fj, out bool okEdge);
                    if (!okEdge) edge = current.At(lattice.X(pc0) + fi, lattice.Y(pr0) - fj);
                    double d = Design(point.Along[at], u, g, edge, Math.Abs(u) + pcl, along, z, left, right, ok, roundMetres);
                    design[at] = d;
                    free[at] = d != g;
                }
            if (graded && any)
            {
                // GR-13: the 1:1 slopes up and down from every fixed point bound every changed one. Two chamfer passes on the
                // 1 m lattice (steps 1 and sqrt 2), so neighbouring points never differ by more than the face slope allows.
                var upper = new double[n];
                var lower = new double[n];
                for (int k = 0; k < n; k++)
                {
                    bool source = !free[k] && near[k];
                    upper[k] = source ? ground[k] : double.PositiveInfinity;
                    lower[k] = source ? ground[k] : double.NegativeInfinity;
                }
                ChamferSpans(upper, lower, grid, ph, FaceSlope);
                double cut = 0, fill = 0;
                for (int j = 0; j < ph; j++)
                    for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                    {
                        int at = grid.Index(i, j), c = pc0 + i, r = pr0 + j;
                        if (!claim[at]) continue;
                        double h = design[at];
                        if (free[at])
                        {
                            if (lower[at] > upper[at]) h = ground[at];
                            else h = Math.Max(lower[at], Math.Min(upper[at], h));
                        }
                        double source = ground[at] - current.Edits.DeltaMmAt(c, r) / 1000.0;
                        long mm = (long)Math.Round((h - source) * 1000, MidpointRounding.AwayFromZero);
                        mm = Math.Max(-TerrainEditTile.DeltaLimitMm, Math.Min(TerrainEditTile.DeltaLimitMm, mm));
                        double change = source + mm / 1000.0 - ground[at];
                        if (change > 0) fill += change; else cut -= change;
                        writes.Write(c, r, (int)mm);
                    }
                result.CutCubicMetres = cut;
                result.FillCubicMetres = fill;
            }

            // CL-01, CL-02, CL-08: every painted cell, the ragged band measured from the edge (islands included).
            int cleared = 0;
            for (int j = 0; j < fp.Height; j++)
                for (int i = 0; i < fp.Width; i++)
                {
                    int c = fp.Column0 + i, r = fp.Row0 + j;
                    if (!fp.Contains(c, r)) continue;
                    if (!Clears(seed, id, c, r, fp.Clearance(c, r))) continue;
                    writes.Clear(c, r);
                    cleared++;
                }
            result.ClearedCells = cleared;
            return result;
        }


        /// <summary>GR-05 v2 (proposed): how far the bench width's smoothing reaches, in stations (50 ft each way).</summary>
        public const int WidthSmoothStations = 2;

        static void Smooth(double[] width, bool[] ok)
        {
            int n = width.Length, k = WidthSmoothStations;
            var min = new double[n];
            for (int i = 0; i < n; i++)
            {
                double m = width[i];
                for (int t = Math.Max(0, i - k); t <= Math.Min(n - 1, i + k); t++) if (ok[t]) m = Math.Min(m, width[t]);
                min[i] = m;
            }
            for (int i = 0; i < n; i++)
            {
                if (!ok[i]) continue;
                double sum = 0;
                int c = 0;
                for (int t = Math.Max(0, i - k); t <= Math.Min(n - 1, i + k); t++) if (ok[t]) { sum += min[t]; c++; }
                width[i] = Math.Min(width[i], sum / c);
            }
        }



        /// <summary>
        /// The grader's compact lattice: per point row j, columns Lo[j]..Hi[j] (empty when Lo &gt; Hi), within a reach of
        /// the row's paint (rows next to painted ones count, so the chamfer's passes see every point they need).
        /// </summary>
        sealed class SpanGrid
        {
            public readonly int[] Lo, Hi, Offset;
            public readonly int Count, Height;

            public SpanGrid(TrailFootprint fp, int pw, int ph, int reach)
            {
                Height = ph;
                Lo = new int[ph];
                Hi = new int[ph];
                Offset = new int[ph];
                var rowLo = new int[fp.Height];
                var rowHi = new int[fp.Height];
                for (int j = 0; j < fp.Height; j++)
                {
                    rowLo[j] = int.MaxValue;
                    rowHi[j] = int.MinValue;
                    for (int i = 0; i < fp.Width; i++)
                        if (fp.Contains(fp.Column0 + i, fp.Row0 + j)) { if (i < rowLo[j]) rowLo[j] = i; rowHi[j] = i; }
                }
                int total = 0;
                for (int j = 0; j < ph; j++)
                {
                    int l = int.MaxValue, h = int.MinValue;
                    for (int cj = Math.Max(0, j - 1 - reach); cj <= Math.Min(fp.Height - 1, j + reach); cj++)
                    {
                        if (rowLo[cj] > rowHi[cj]) continue;
                        l = Math.Min(l, rowLo[cj]);
                        h = Math.Max(h, rowHi[cj] + 1);
                    }
                    if (l > h) { Lo[j] = 1; Hi[j] = 0; }
                    else { Lo[j] = Math.Max(0, l - reach); Hi[j] = Math.Min(pw - 1, h + reach); }
                    Offset[j] = total;
                    total += Math.Max(0, Hi[j] - Lo[j] + 1);
                }
                Count = total;
            }

            /// <summary>The compact index of point (i, j), or -1 outside the spans.</summary>
            public int Index(int i, int j) => j < 0 || j >= Height || i < Lo[j] || i > Hi[j] ? -1 : Offset[j] + i - Lo[j];

            /// <summary>Bilinear between the four points round (fi, fj); false when one is outside the spans or unread.</summary>
            public double Bilinear(double[] g, double fi, double fj, out bool found)
            {
                int i = (int)Math.Floor(fi), j = (int)Math.Floor(fj);
                double tx = fi - i, ty = fj - j;
                int a = Index(i, j), b = Index(i + 1, j), c = Index(i, j + 1), d = Index(i + 1, j + 1);
                found = a >= 0 && b >= 0 && c >= 0 && d >= 0 && !double.IsNaN(g[a]) && !double.IsNaN(g[b]) && !double.IsNaN(g[c]) && !double.IsNaN(g[d]);
                if (!found) return double.NaN;
                double top = g[a] + (g[b] - g[a]) * tx, bottom = g[c] + (g[d] - g[c]) * tx;
                return top + (bottom - top) * ty;
            }
        }

        /// <summary>Marks the grid's points within <paramref name="reach"/> cells of a painted edge, and every point a painted cell touches.</summary>
        static void MarkNear(TrailFootprint fp, SpanGrid grid, bool[] near, int reach)
        {
            for (int j = 0; j < fp.Height; j++)
                for (int i = 0; i < fp.Width; i++)
                {
                    if (!fp.Contains(fp.Column0 + i, fp.Row0 + j)) continue;
                    bool edge = !fp.Contains(fp.Column0 + i - 1, fp.Row0 + j) || !fp.Contains(fp.Column0 + i + 1, fp.Row0 + j)
                             || !fp.Contains(fp.Column0 + i, fp.Row0 + j - 1) || !fp.Contains(fp.Column0 + i, fp.Row0 + j + 1);
                    int k = edge ? reach : 0;
                    for (int y = j - k; y <= j + 1 + k; y++)
                        for (int x = i - k; x <= i + 1 + k; x++)
                        {
                            int at = grid.Index(x, y);
                            if (at >= 0) near[at] = true;
                        }
                }
        }

        /// <summary><see cref="Locate"/> on the compact grid: each segment visits only the span points within reach.</summary>
        static Located LocateSpans(IReadOnlyList<double> line, double[] along, EditLattice lattice, int c0, int r0, SpanGrid grid, double reach)
        {
            var located = new Located(grid.Count);
            int count = line.Count / 2;
            for (int k = 0; k + 1 < count; k++)
            {
                double ax = line[2 * k], ay = line[2 * k + 1], bx = line[2 * k + 2], by = line[2 * k + 3];
                double sx = bx - ax, sy = by - ay, len2 = sx * sx + sy * sy;
                if (len2 <= 0) continue;
                double len = Math.Sqrt(len2);
                int ci0 = (int)Math.Floor(Math.Min(ax, bx) - reach - 1 - lattice.West) - c0;
                int ci1 = (int)Math.Ceiling(Math.Max(ax, bx) + reach + 1 - lattice.West) - c0;
                int rj0 = Math.Max(0, (int)Math.Floor(lattice.North - (Math.Max(ay, by) + reach + 1)) - r0);
                int rj1 = Math.Min(grid.Height - 1, (int)Math.Ceiling(lattice.North - (Math.Min(ay, by) - reach - 1)) - r0);
                for (int j = rj0; j <= rj1; j++)
                    for (int i = Math.Max(ci0, grid.Lo[j]); i <= Math.Min(ci1, grid.Hi[j]); i++)
                    {
                        double px = lattice.X(c0 + i), py = lattice.Y(r0 + j);
                        double rx = px - ax, ry = py - ay;
                        double t = (rx * sx + ry * sy) / len2;
                        double tc = t < 0 ? 0 : t > 1 ? 1 : t;
                        double qx = ax + sx * tc - px, qy = ay + sy * tc - py;
                        double dist = Math.Sqrt(qx * qx + qy * qy);
                        int at = grid.Index(i, j);
                        if (dist >= located.Dist[at]) continue;
                        double cross = (sx * ry - sy * rx) / len;
                        located.Dist[at] = dist;
                        located.Offset[at] = cross >= 0 ? dist : -dist;
                        if (dist > 1e-9) { located.OutX[at] = -qx / dist; located.OutY[at] = -qy / dist; }
                        else { located.OutX[at] = -sy / len; located.OutY[at] = sx / len; }
                        located.Along[at] = along[k] + tc * len;
                    }
            }
            return located;
        }

        /// <summary>
        /// GR-13's bounds on the compact grid: <paramref name="upper"/> becomes min over fixed q of g(q) + s·dist(p, q) and
        /// <paramref name="lower"/> max of g(q) - s·dist, distances along 8-neighbour steps (1 and sqrt 2).
        /// </summary>
        static void ChamferSpans(double[] upper, double[] lower, SpanGrid grid, int h, double s)
        {
            double a = s, b = s * 1.4142135623730951;
            for (int j = 0; j < h; j++)
                for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                {
                    int at = grid.Index(i, j);
                    double u = upper[at], l = lower[at];
                    Take(upper, lower, grid.Index(i - 1, j), a, ref u, ref l);
                    Take(upper, lower, grid.Index(i, j - 1), a, ref u, ref l);
                    Take(upper, lower, grid.Index(i - 1, j - 1), b, ref u, ref l);
                    Take(upper, lower, grid.Index(i + 1, j - 1), b, ref u, ref l);
                    upper[at] = u; lower[at] = l;
                }
            for (int j = h - 1; j >= 0; j--)
                for (int i = grid.Hi[j]; i >= grid.Lo[j]; i--)
                {
                    int at = grid.Index(i, j);
                    double u = upper[at], l = lower[at];
                    Take(upper, lower, grid.Index(i + 1, j), a, ref u, ref l);
                    Take(upper, lower, grid.Index(i, j + 1), a, ref u, ref l);
                    Take(upper, lower, grid.Index(i + 1, j + 1), b, ref u, ref l);
                    Take(upper, lower, grid.Index(i - 1, j + 1), b, ref u, ref l);
                    upper[at] = u; lower[at] = l;
                }
        }

        static void Take(double[] upper, double[] lower, int from, double step, ref double u, ref double l)
        {
            if (from < 0) return;
            u = Math.Min(u, upper[from] + step);
            l = Math.Max(l, lower[from] - step);
        }

        /// <summary>A footprint's claims and clearing only, no heights (a trail without a centreline yet).</summary>
        public static EarthworkWrites ClearOnly(TrailFootprint fp, ulong seed, TrailId id, Func<int, int, bool> isFixed)
        {
            var writes = new EarthworkWrites();
            for (int r = fp.Row0; r <= fp.LastRow; r++)
                for (int c = fp.Column0; c <= fp.LastColumn; c++)
                {
                    if (fp.Interior(c, r) && (isFixed == null || !isFixed(c, r))) writes.Claim(c, r);
                    if (fp.Contains(c, r) && Clears(seed, id, c, r, fp.Clearance(c, r))) writes.Clear(c, r);
                }
            return writes;
        }

        /// <summary>GR-03 v2: how far the section runs from the centreline along (nx, ny) before it leaves the footprint.</summary>
        static double Budget(TrailFootprint fp, double cx, double cy, double nx, double ny)
        {
            double limit = 2 * fp.MaxClearance + 4;
            double d = 0;
            while (d < limit && fp.ContainsAt(cx + nx * (d + 0.5), cy + ny * (d + 0.5))) d += 0.5;
            return d;
        }

    }
}
