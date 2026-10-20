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
            /// <summary>GR-01 v2: each station's bench side slope, rise per metre toward the left of the centreline.</summary>
            public double[] CrossSlope { get; internal set; }
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
                                            ulong seed, TrailId id, Func<int, int, bool> isFixed = null, double roundMetres = RoundingMetres,
                                            Options options = null)
        {
            options ??= Options.Rules;
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
            var cross = new double[count];   // GR-01 v2: each station's side slope, rise per metre toward the left (+n)
            var budgetL = new double[count];
            var budgetR = new double[count];
            var inside = new bool[count];
            int ungraded = 0;
            double walked = 0;
            for (int k = 0; k < count; k++)
            {
                if (k > 0) walked += Dist(line[2 * k - 2], line[2 * k - 1], line[2 * k], line[2 * k + 1]);
                along[k] = walked;
                double cx = line[2 * k], cy = line[2 * k + 1];
                SweepTangent(line, k, out double ex, out double ey);
                double nx = -ey, ny = ex;
                z[k] = current.At(cx, cy);
                if (graded && fp.ContainsAt(cx, cy))
                {
                    budgetL[k] = Budget(fp, cx, cy, nx, ny);
                    budgetR[k] = Budget(fp, cx, cy, -nx, -ny);
                    cross[k] = CrossSlope(current, cx, cy, nx, ny, budgetL[k], budgetR[k], z[k], options.CrossSlopeCap);
                    inside[k] = true;
                }
            }
            // The side slope follows its neighbours (two stations each way), so lidar roughness doesn't twist the bench.
            if (graded) cross = Average(cross, inside, WidthSmoothStations);
            var natural = (double[])z.Clone();
            // GR-01 v3 (owner, TL23, after measuring the rolls on their own trails): the bench's long profile is the ground
            // under the centreline averaged over 75 ft either way, so lidar rolls don't carry into the bench (the rolls drop
            // three to four times for a few percent more earth); the window shrinks at the ends, so the head and tail keep
            // their ground and still join what they join.
            if (graded && options.ProfileSmoothStations > 0) z = Profile(z, along, options.ProfileSmoothStations * StationMetres);
            // The grade the bench carries on past each end: over the last 25 ft at least, never a short last stretch's.
            double headGrade = EndGrade(z, along, true), tailGrade = EndGrade(z, along, false);
            for (int k = 0; k < count && graded; k++)
            {
                if (inside[k])
                {
                    double cx = line[2 * k], cy = line[2 * k + 1];
                    SweepTangent(line, k, out double ex, out double ey);
                    double nx = -ey, ny = ex;
                    left[k] = Side(current, cx, cy, nx, ny, budgetL[k], z[k], cross[k]);
                    right[k] = Side(current, cx, cy, -nx, -ny, budgetR[k], z[k], -cross[k]);
                    ok[k] = left[k] + right[k] >= MinBenchMetres;
                }
            }
            // GR-07 over a stretch (the review: on a marginal sidehill, stations that alternate between a bench and none
            // made a washboard): a benched run shorter than three stations (75 ft) stays natural with its neighbours.
            if (graded)
                for (int k = 0; k < count;)
                {
                    if (!ok[k]) { k++; continue; }
                    int e = k;
                    while (e < count && ok[e]) e++;
                    if (e - k < ShortestBenchStations && !(k == 0 && e == count)) for (int q = k; q < e; q++) ok[q] = false;
                    k = e;
                }
            for (int k = 0; k < count && graded; k++) if (!ok[k]) ungraded++;
            // Proposed GR-05 v2: the bench's width follows its neighbours. Each side takes the narrowest GR-03 width within
            // two stations, then the average of those over two stations, so it can never be wider than GR-03 allows at the
            // station, and lidar roughness doesn't step the faces in and out every 25 ft.
            if (graded) { Smooth(left, ok); Smooth(right, ok); }
            result.CrossSlope = cross;
            result.Stations = line.ToArray();
            result.Along = along;
            result.Ground = natural;
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
            Swept point = null;
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
                    double u = point.Offset[at];
                    // The ground where this point's line out meets the edge: bilinear on the lattice (SH-02), from the
                    // points already read where it can.
                    // GR-06 per point (GR-05 v3, the owner's corner report): the section's own painted extent on this side,
                    // blended between its stations like the bench, not the nearest unpainted ground in any direction (in a
                    // corner that's often across the other leg, and it cut the bench back for nothing). Beyond the extent,
                    // the point is at the edge.
                    double EdgeAlong(double ox, double oy, double dist)
                    {
                        double fi = i + ox * dist, fj = j - oy * dist;
                        double e = grid.Bilinear(ground, fi, fj, out bool okEdge);
                        return okEdge ? e : current.At(lattice.X(pc0) + fi, lattice.Y(pr0) - fj);
                    }
                    double half1 = Math.Max(Math.Abs(u), SectionReach(point.Along[at], u, along, budgetL, budgetR, point.Seg[at]));
                    double d = Design(point.Along[at], u, g, EdgeAlong(point.OutX[at], point.OutY[at], half1 - Math.Abs(u)), half1, along, z, left, right,
                                      ok, roundMetres, ramp: true, cross: cross, segment: point.Seg[at], headGrade: headGrade, tailGrade: tailGrade);
                    // Inside a tight bend, the next section too (GR-05 v3), blended in by how nearly as close it is.
                    double gap = point.Dist2[at] - point.Dist[at];
                    if (point.Dist2[at] != double.MaxValue && gap < SectionBlendMetres)
                    {
                        double u2 = point.Offset2[at];
                        double half2 = Math.Max(Math.Abs(u2), SectionReach(point.Along2[at], u2, along, budgetL, budgetR, point.Seg2[at]));
                        double d2 = Design(point.Along2[at], u2, g, EdgeAlong(point.OutX2[at], point.OutY2[at], half2 - Math.Abs(u2)), half2, along, z,
                                           left, right, ok, roundMetres, ramp: true, cross: cross, segment: point.Seg2[at], headGrade: headGrade, tailGrade: tailGrade);
                        double w2 = 0.5 * (1 - gap / SectionBlendMetres);
                        d += (d2 - d) * w2;
                    }
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
                // The clamped design at every point the trail may change; every other point keeps its ground.
                var height0 = new double[n];
                for (int k = 0; k < n; k++) height0[k] = ground[k];
                for (int j = 0; j < ph; j++)
                    for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                    {
                        int at = grid.Index(i, j);
                        if (!claim[at] || !free[at]) continue;   // (non-free points keep their ground)
                        // Where the 1:1 cones from fixed ground on either side can't both hold (steep ground: they cross),
                        // the point settles between them rather than dropping to natural ground beside designed points (the
                        // smoothness audit, 2026-10-20: small cliffs across the bench on the steep face). Both cases are the
                        // same clamp of the design between the two bounds, so there's no step where they meet.
                        double lo = Math.Min(lower[at], upper[at]), hi = Math.Max(lower[at], upper[at]);
                        lower[at] = lo; upper[at] = hi;
                        height0[at] = Math.Max(lo, Math.Min(hi, design[at]));
                    }
                // GR-12 v3, the finish (the smoothness audit, 2026-10-20): the surface is smoothed over about 2 m (two
                // passes of a 1-4-6-4-1 kernel each way) at the points the trail changes, which rounds what the clamp leaves
                // sharp (faces meeting fixed ground, the toes round islands, the lattice's stair-steps along diagonal creases)
                // and leaves a straight bench or face exactly as it was (the heights, not the change, are smoothed, so the
                // ground's own roughness never comes back into the bench). Then the cones clamp again, so GR-13 still holds.
                var smooth = roundMetres > 0 ? Finish(height0, grid, ph, 2) : height0;   // part of GR-12: off with the rounding
                double cut = 0, fill = 0;
                for (int j = 0; j < ph; j++)
                    for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                    {
                        int at = grid.Index(i, j), c = pc0 + i, r = pr0 + j;
                        if (!claim[at]) continue;
                        double h = design[at];
                        if (free[at]) h = Math.Max(lower[at], Math.Min(upper[at], smooth[at]));
                        else h = ground[at];
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

        /// <summary>
        /// Each point's place on the centreline (GR-05 v3, owner's report 2026-10-19: terraces fanning out of bends): the
        /// sections sweep smoothly between stations, each one square to a tangent that turns linearly from one station's
        /// to the next, so a point's along-distance changes evenly all the way round a bend. (Square to the nearest straight
        /// piece instead, every point in the wedge outside a station's corner took that station's height: flat wedges with
        /// steep bands between them.) A point lies on segment k's sweep where (p − C(t)) · T(t) = 0, found by bisection;
        /// inside a tight bend, where sweeps cross, the nearest section wins. Before the head and past the tail, the end
        /// stations' sections run on.
        /// </summary>
        static Swept LocateSpans(IReadOnlyList<double> line, double[] along, EditLattice lattice, int c0, int r0, SpanGrid grid, double reach)
        {
            var located = new Swept(grid.Count);
            int count = line.Count / 2;
            var tx = new double[count];
            var ty = new double[count];
            for (int k = 0; k < count; k++) { SweepTangent(line, k, out double ex, out double ey); tx[k] = ex; ty[k] = ey; }
            for (int k = 0; k + 1 < count; k++)
            {
                double ax = line[2 * k], ay = line[2 * k + 1], bx = line[2 * k + 2], by = line[2 * k + 3];
                double sx = bx - ax, sy = by - ay, len = Math.Sqrt(sx * sx + sy * sy);
                if (len <= 0) continue;
                bool first = k == 0, last = k + 2 == count;
                int ci0 = (int)Math.Floor(Math.Min(ax, bx) - reach - 1 - lattice.West) - c0;
                int ci1 = (int)Math.Ceiling(Math.Max(ax, bx) + reach + 1 - lattice.West) - c0;
                int rj0 = Math.Max(0, (int)Math.Floor(lattice.North - (Math.Max(ay, by) + reach + 1)) - r0);
                int rj1 = Math.Min(grid.Height - 1, (int)Math.Ceiling(lattice.North - (Math.Min(ay, by) - reach - 1)) - r0);
                for (int j = rj0; j <= rj1; j++)
                    for (int i = Math.Max(ci0, grid.Lo[j]); i <= Math.Min(ci1, grid.Hi[j]); i++)
                    {
                        double px = lattice.X(c0 + i), py = lattice.Y(r0 + j);
                        // Which side of each station's section line the point lies: ahead of the first, behind the second.
                        double g0 = (px - ax) * tx[k] + (py - ay) * ty[k];
                        double g1 = (px - bx) * tx[k + 1] + (py - by) * ty[k + 1];
                        double t;
                        // A crossing either way: past a bend's centre of curvature the order of the section lines reverses.
                        if ((g0 >= 0 && g1 <= 0) || (g0 <= 0 && g1 >= 0))
                        {
                            // Regula falsi (the Illinois variant) on the bracket: a few steps to well under a millimetre.
                            double lo = 0, hi = 1, flo = g0, fhi = g1;
                            t = flo == 0 ? 0 : fhi == 0 ? 1 : 0.5;
                            int side = 0;
                            for (int it = 0; it < 24 && flo != 0 && fhi != 0; it++)
                            {
                                t = (lo * fhi - hi * flo) / (fhi - flo);
                                double f = SweepSide(px, py, ax, ay, sx, sy, tx[k], ty[k], tx[k + 1], ty[k + 1], t);
                                if (Math.Abs(f) < 1e-5 || hi - lo < 1e-7) break;
                                if ((f >= 0) == (fhi >= 0)) { hi = t; fhi = f; if (side == -1) flo *= 0.5; side = -1; }
                                else { lo = t; flo = f; if (side == 1) fhi *= 0.5; side = 1; }
                            }
                        }
                        else if (first && g0 < 0) t = 0;
                        else if (last && g1 > 0) t = 1;
                        else continue;
                        double cx = ax + sx * t, cy = ay + sy * t;
                        double ux = tx[k] + (tx[k + 1] - tx[k]) * t, uy = ty[k] + (ty[k + 1] - ty[k]) * t, ul = Math.Sqrt(ux * ux + uy * uy);
                        if (ul < 1e-9) { ux = sx / len; uy = sy / len; } else { ux /= ul; uy /= ul; }
                        double qx = px - cx, qy = py - cy;
                        int at = grid.Index(i, j);
                        double cross = ux * qy - uy * qx;
                        double ahead = qx * ux + qy * uy;   // past the end station along its tangent (0 on a section)
                        bool cap = (first && t == 0 && ahead < 0) || (last && t == 1 && ahead > 0);
                        // Past the head or the tail (the smoothness audit, 2026-10-20: a seam ran straight out of each end):
                        // the end station's section runs on, the point's offset measured square to it, not as the distance
                        // to the end point (whose side flipped across the centreline's extension).
                        double dist = cap ? Math.Abs(cross) : Math.Sqrt(qx * qx + qy * qy);
                        double ox, oy;
                        if (cap || dist <= 1e-9) { double sgn = cross >= 0 ? 1 : -1; ox = -uy * sgn; oy = ux * sgn; }
                        else { ox = qx / dist; oy = qy / dist; }
                        located.Offer(at, dist, cross >= 0 ? dist : -dist, along[k] + t * len + (cap ? ahead : 0), ox, oy, k);
                    }
            }
            return located;
        }

        /// <summary>
        /// A station's tangent for the sweep (GR-05 v3): the chord over two stations each way, so the sections turn smoothly
        /// where the simplified centreline has a corner and neighbouring sections don't cross within the trail's width.
        /// </summary>
        static void SweepTangent(IReadOnlyList<double> line, int k, out double ex, out double ey)
        {
            int count = line.Count / 2;
            int a = Math.Max(0, k - 2), b = Math.Min(count - 1, k + 2);
            ex = line[2 * b] - line[2 * a];
            ey = line[2 * b + 1] - line[2 * a + 1];
            double len = Math.Sqrt(ex * ex + ey * ey);
            ex /= len;
            ey /= len;
        }

        /// <summary>
        /// The swept location's two nearest sections per point (GR-05 v3): inside a bend tighter than the trail is wide,
        /// several stations' sections reach the same ground, and the design blends the nearest two by distance.
        /// </summary>
        sealed class Swept
        {
            public readonly double[] Dist, Offset, Along, OutX, OutY;
            public readonly double[] Dist2, Offset2, Along2, OutX2, OutY2;
            /// <summary>The stretch between stations each section lies on (its first station).</summary>
            public readonly int[] Seg, Seg2;
            public Swept(int n)
            {
                Seg = new int[n]; Seg2 = new int[n];
                Dist = new double[n]; Offset = new double[n]; Along = new double[n]; OutX = new double[n]; OutY = new double[n];
                Dist2 = new double[n]; Offset2 = new double[n]; Along2 = new double[n]; OutX2 = new double[n]; OutY2 = new double[n];
                for (int i = 0; i < n; i++) { Dist[i] = double.MaxValue; Dist2[i] = double.MaxValue; }
            }

            /// <summary>A section through slot <paramref name="at"/>; the same place on the line again (a station both its segments reach) counts once.</summary>
            public void Offer(int at, double dist, double offset, double along, double ox, double oy, int seg)
            {
                if (Dist[at] != double.MaxValue && Math.Abs(along - Along[at]) < SameSectionMetres)
                {
                    if (dist < Dist[at]) Set(at, dist, offset, along, ox, oy, seg, false);
                    return;
                }
                if (Dist2[at] != double.MaxValue && Math.Abs(along - Along2[at]) < SameSectionMetres)
                {
                    if (dist < Dist2[at]) Set(at, dist, offset, along, ox, oy, seg, true);
                    if (Dist2[at] < Dist[at]) Swap(at);
                    return;
                }
                if (dist < Dist[at])
                {
                    Dist2[at] = Dist[at]; Offset2[at] = Offset[at]; Along2[at] = Along[at]; OutX2[at] = OutX[at]; OutY2[at] = OutY[at]; Seg2[at] = Seg[at];
                    Set(at, dist, offset, along, ox, oy, seg, false);
                }
                else if (dist < Dist2[at]) Set(at, dist, offset, along, ox, oy, seg, true);
            }

            void Set(int at, double dist, double offset, double along, double ox, double oy, int seg, bool second)
            {
                if (second) { Dist2[at] = dist; Offset2[at] = offset; Along2[at] = along; OutX2[at] = ox; OutY2[at] = oy; Seg2[at] = seg; }
                else { Dist[at] = dist; Offset[at] = offset; Along[at] = along; OutX[at] = ox; OutY[at] = oy; Seg[at] = seg; }
            }

            void Swap(int at)
            {
                (Dist[at], Dist2[at]) = (Dist2[at], Dist[at]);
                (Offset[at], Offset2[at]) = (Offset2[at], Offset[at]);
                (Along[at], Along2[at]) = (Along2[at], Along[at]);
                (OutX[at], OutX2[at]) = (OutX2[at], OutX[at]);
                (OutY[at], OutY2[at]) = (OutY2[at], OutY[at]);
                (Seg[at], Seg2[at]) = (Seg2[at], Seg[at]);
            }
        }

        /// <summary>
        /// GR-12 v3's finish: <paramref name="passes"/> passes of a 1-4-6-4-1 kernel along the rows, then the columns, over
        /// the compact grid's heights; a neighbour off the grid, or unread, counts as the point itself. A plane (a straight
        /// bench, a face) comes through exactly where the kernel stays on it.
        /// </summary>
        static double[] Finish(double[] change, SpanGrid grid, int h, int passes)
        {
            var a = (double[])change.Clone();
            var b = new double[a.Length];
            int self = 0;
            double V(double[] v, int idx) => idx < 0 || double.IsNaN(v[idx]) ? v[self] : v[idx];
            for (int pass = 0; pass < passes; pass++)
            {
                for (int j = 0; j < h; j++)
                    for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                    {
                        int at = grid.Index(i, j);
                        self = at;
                        if (double.IsNaN(a[at])) { b[at] = a[at]; continue; }
                        b[at] = (V(a, grid.Index(i - 2, j)) + 4 * V(a, grid.Index(i - 1, j)) + 6 * a[at] + 4 * V(a, grid.Index(i + 1, j)) + V(a, grid.Index(i + 2, j))) / 16;
                    }
                for (int j = 0; j < h; j++)
                    for (int i = grid.Lo[j]; i <= grid.Hi[j]; i++)
                    {
                        int at = grid.Index(i, j);
                        self = at;
                        if (double.IsNaN(b[at])) { a[at] = b[at]; continue; }
                        a[at] = (V(b, grid.Index(i, j - 2)) + 4 * V(b, grid.Index(i, j - 1)) + 6 * b[at] + 4 * V(b, grid.Index(i, j + 1)) + V(b, grid.Index(i, j + 2))) / 16;
                    }
            }
            return a;
        }

        /// <summary>A section's painted extent on the point's side, blended between the stations either side of it.</summary>
        static double SectionReach(double t, double u, double[] along, double[] reachL, double[] reachR, int segment)
        {
            int count = along.Length;
            if (count < 2) return u >= 0 ? reachL[0] : reachR[0];
            int k = Math.Min(segment, count - 2);
            double span = along[k + 1] - along[k];
            double f = span > 0 ? Math.Max(0, Math.Min(1, (t - along[k]) / span)) : 0;
            var r = u >= 0 ? reachL : reachR;
            return r[k] + (r[k + 1] - r[k]) * f;
        }

        /// <summary>Two sections closer along the line than this are the same one.</summary>
        const double SameSectionMetres = 1.0;

        /// <summary>
        /// GR-05 v3: inside a bend, how much nearer one section must be than the next to take a point alone. Between, the two
        /// blend linearly (half each where they are equally near), so the bench changes smoothly through the bend.
        /// </summary>
        public const double SectionBlendMetres = 8.0;

        /// <summary>(p − C(t)) · T(t) on a segment's sweep (T unnormalised: only its sign matters).</summary>
        static double SweepSide(double px, double py, double ax, double ay, double sx, double sy, double t0x, double t0y, double t1x, double t1y, double t)
        {
            double cx = ax + sx * t, cy = ay + sy * t;
            double ux = t0x + (t1x - t0x) * t, uy = t0y + (t1y - t0y) * t;
            return (px - cx) * ux + (py - cy) * uy;
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
        /// <summary>
        /// GR-01 v2 (owner, 2026-10-19: "side slope should be permitted to an extent on a graded trail"): the bench keeps
        /// the ground's side slope across the section, up to <see cref="MaxCrossSlope"/> either way. The ground's slope is the
        /// least-squares fit of the section's ground across its painted width (to 15 m a side), from the centreline's height.
        /// </summary>
        static double CrossSlope(IHeightSource ground, double cx, double cy, double nx, double ny, double left, double right, double z,
                                 double cap = MaxCrossSlope)
        {
            double sum = 0, sq = 0;
            for (double d = 0.5; d <= Math.Min(left, 15); d += 0.5) { sum += d * (ground.At(cx + nx * d, cy + ny * d) - z); sq += d * d; }
            for (double d = 0.5; d <= Math.Min(right, 15); d += 0.5) { sum += -d * (ground.At(cx - nx * d, cy - ny * d) - z); sq += d * d; }
            if (sq <= 0) return 0;
            return Math.Max(-cap, Math.Min(cap, sum / sq));
        }

        /// <summary>The average over <paramref name="reach"/> stations each way, of the stations that count.</summary>
        static double[] Average(double[] v, bool[] counts, int reach)
        {
            var o = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                if (!counts[i]) continue;
                double sum = 0;
                int c = 0;
                for (int t = Math.Max(0, i - reach); t <= Math.Min(v.Length - 1, i + reach); t++) if (counts[t]) { sum += v[t]; c++; }
                o[i] = sum / c;
            }
            return o;
        }

        /// <summary>
        /// The grader's tunable rules, for experiments and review pictures (the game always grades with <see cref="Rules"/>).
        /// </summary>
        public sealed class Options
        {
            /// <summary>GR-01 v2's side slope cap (rise over run). The ground's fit is capped at <see cref="MaxCrossSlope"/>
            /// first; a larger cap here lets the bench keep up to this much instead.</summary>
            public double CrossSlopeCap = MaxCrossSlope;
            /// <summary>The bench's long profile as a moving average over this many stations each way (0: the ground).</summary>
            public int ProfileSmoothStations = ProfileStations;
            public static readonly Options Rules = new Options();
        }

        /// <summary>
        /// GR-01 v3's profile: at each station, a straight line fitted to the ground under the centreline within
        /// <paramref name="reach"/> metres either way (75 ft, the owner's window, every station weighted alike), evaluated
        /// there. The window shrinks toward the ends so the head and tail keep their ground; the fit is by distance, so a
        /// uniform grade comes through exactly however the stations are spaced (the last stretch is usually short: the
        /// review, 2026-10-20, found a plain average bent the profile there).
        /// </summary>
        static double[] Profile(double[] z, double[] along, double reach)
        {
            int n = z.Length;
            var o = new double[n];
            for (int i = 0; i < n; i++)
            {
                double r = Math.Min(reach, Math.Min(along[i] - along[0], along[n - 1] - along[i]));
                if (r <= 1e-6) { o[i] = z[i]; continue; }
                double sw = 0, sd = 0, sdd = 0, sz = 0, sdz = 0;
                for (int t = i; t >= 0 && along[i] - along[t] <= r; t--) Acc(t);
                for (int t = i + 1; t < n && along[t] - along[i] <= r; t++) Acc(t);
                double den = sw * sdd - sd * sd;
                o[i] = Math.Abs(den) > 1e-12 ? (sz * sdd - sdz * sd) / den : sz / sw;

                void Acc(int t)
                {
                    double d = along[t] - along[i], w = 1;
                    sw += w; sd += w * d; sdd += w * d * d; sz += w * z[t]; sdz += w * d * z[t];
                }
            }
            return o;
        }

        /// <summary>The profile's grade at the head (or the tail), over at least one full station's length.</summary>
        static double EndGrade(double[] z, double[] along, bool head)
        {
            int n = z.Length;
            if (n < 2) return 0;
            if (head)
            {
                int j = 1;
                while (j < n - 1 && along[j] - along[0] < StationMetres) j++;
                return along[j] > along[0] ? (z[j] - z[0]) / (along[j] - along[0]) : 0;
            }
            int m = n - 2;
            while (m > 0 && along[n - 1] - along[m] < StationMetres) m--;
            return along[n - 1] > along[m] ? (z[n - 1] - z[m]) / (along[n - 1] - along[m]) : 0;
        }

        /// <summary>GR-07 over a stretch: the shortest benched run graded, in stations (75 ft).</summary>
        public const int ShortestBenchStations = 3;

        /// <summary>GR-01 v2: the steepest side slope a graded bench keeps, rise over run (owner, TL23: 20%).</summary>
        public const double MaxCrossSlope = 0.20;

        /// <summary>GR-01 v3 (owner, TL23): the bench's long profile is the average of the ground under the centreline over
        /// this many stations each way (75 ft), so lidar rolls don't carry into the bench.</summary>
        public const int ProfileStations = 3;

        static double Budget(TrailFootprint fp, double cx, double cy, double nx, double ny)
        {
            double limit = 2 * fp.MaxClearance + 4;
            double d = 0;
            while (d < limit && fp.ContainsAt(cx + nx * (d + 0.5), cy + ny * (d + 0.5))) d += 0.5;
            return d;
        }

    }
}
