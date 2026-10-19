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
            result.Stations = line.ToArray();
            result.Along = along;
            result.Ground = (double[])z.Clone();
            result.Benched = ok;
            result.LengthMetres = length;
            result.UngradedStations = ungraded;
            result.UngradedMetres = count > 1 ? length * ungraded / count : 0;

            // The footprint's lattice points: located on the centreline, and their design heights.
            int pc0 = fp.Column0, pr0 = fp.Row0, pw = fp.Width + 1, ph = fp.Height + 1;
            bool any = false;
            var design = graded ? new double[pw * ph] : null;
            var ground = graded ? new double[pw * ph] : null;
            var free = graded ? new bool[pw * ph] : null;   // a point GR-13 may clamp
            Located point = null;
            if (graded)
            {
                for (int k = 0; k < count && !any; k++) any = ok[k];
                double reach = 1.5 * fp.MaxClearance + 3;
                point = any ? Locate(line, along, lattice, pc0, pr0, pw, ph, 0, 0, reach) : null;
            }
            for (int j = 0; j < ph; j++)
                for (int i = 0; i < pw; i++)
                {
                    int c = pc0 + i, r = pr0 + j, at = j * pw + i;
                    bool interior = fp.Interior(c, r);
                    bool mine = interior && (isFixed == null || !isFixed(c, r));
                    if (mine) writes.Claim(c, r);
                    if (!graded) continue;
                    double g = current.AtPoint(c, r);
                    ground[at] = g;
                    design[at] = g;
                    if (!mine || point == null || point.Dist[at] == double.MaxValue) continue;
                    double pcl = fp.PointClearance(c, r);
                    double u = point.Offset[at];
                    double edge = current.At(lattice.X(c) + point.OutX[at] * pcl, lattice.Y(r) + point.OutY[at] * pcl);
                    double d = Design(point.Along[at], u, g, edge, Math.Abs(u) + pcl, along, z, left, right, ok, roundMetres);
                    design[at] = d;
                    free[at] = d != g;
                }

            if (graded && any)
            {
                // GR-13: the 1:1 slopes up and down from every fixed point bound every changed one. Two chamfer passes on the
                // 1 m lattice (steps 1 and √2), so neighbouring points never differ by more than the face slope allows.
                var upper = new double[pw * ph];
                var lower = new double[pw * ph];
                for (int k = 0; k < upper.Length; k++)
                {
                    upper[k] = free[k] ? double.PositiveInfinity : ground[k];
                    lower[k] = free[k] ? double.NegativeInfinity : ground[k];
                }
                Chamfer(upper, lower, pw, ph, FaceSlope);
                double cut = 0, fill = 0;
                for (int j = 0; j < ph; j++)
                    for (int i = 0; i < pw; i++)
                    {
                        int at = j * pw + i, c = pc0 + i, r = pr0 + j;
                        if (!writes.Claims(c, r)) continue;
                        double h = design[at];
                        if (free[at])
                        {
                            if (lower[at] > upper[at]) h = ground[at];
                            else h = Math.Max(lower[at], Math.Min(upper[at], h));
                        }
                        double source = current.Source.At(lattice.X(c), lattice.Y(r));
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

        /// <summary>
        /// GR-13's bounds: <paramref name="upper"/> becomes min over fixed q of g(q) + s·dist(p, q) and
        /// <paramref name="lower"/> max of g(q) − s·dist, distances along 8-neighbour steps (1 and √2).
        /// </summary>
        static void Chamfer(double[] upper, double[] lower, int w, int h, double s)
        {
            double a = s, b = s * 1.4142135623730951;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int at = j * w + i;
                    double u = upper[at], l = lower[at];
                    if (i > 0) { u = Math.Min(u, upper[at - 1] + a); l = Math.Max(l, lower[at - 1] - a); }
                    if (j > 0)
                    {
                        u = Math.Min(u, upper[at - w] + a); l = Math.Max(l, lower[at - w] - a);
                        if (i > 0) { u = Math.Min(u, upper[at - w - 1] + b); l = Math.Max(l, lower[at - w - 1] - b); }
                        if (i < w - 1) { u = Math.Min(u, upper[at - w + 1] + b); l = Math.Max(l, lower[at - w + 1] - b); }
                    }
                    upper[at] = u; lower[at] = l;
                }
            for (int j = h - 1; j >= 0; j--)
                for (int i = w - 1; i >= 0; i--)
                {
                    int at = j * w + i;
                    double u = upper[at], l = lower[at];
                    if (i < w - 1) { u = Math.Min(u, upper[at + 1] + a); l = Math.Max(l, lower[at + 1] - a); }
                    if (j < h - 1)
                    {
                        u = Math.Min(u, upper[at + w] + a); l = Math.Max(l, lower[at + w] - a);
                        if (i < w - 1) { u = Math.Min(u, upper[at + w + 1] + b); l = Math.Max(l, lower[at + w + 1] - b); }
                        if (i > 0) { u = Math.Min(u, upper[at + w - 1] + b); l = Math.Max(l, lower[at + w - 1] - b); }
                    }
                    upper[at] = u; lower[at] = l;
                }
        }
    }
}
