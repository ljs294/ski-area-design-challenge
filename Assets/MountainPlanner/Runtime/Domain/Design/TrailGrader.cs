using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The trail grader (task 07, decision SK1): a level bench with 1:1 cut and fill faces along a centreline, inside a
    /// footprint of one brush width swept along it, cleared with a ragged edge. Task 08 extends it to painted footprints
    /// and the automatic centreline (TR-02, TR-03); the rules it follows are the same:
    /// stations every 25 ft along the centreline (TR-03), the bench level across at the centreline's natural ground
    /// (GR-01), 1:1 faces (GR-02), GR-03's widest bench per side, stations under 25 ft of bench left natural and
    /// reported (GR-04, GR-07), sections blended linearly between stations (GR-05), nothing changed outside the footprint
    /// (GR-06), the volumes from the design-minus-ground raster (GR-08), and the trees cleared over the footprint with
    /// CL-02's ragged 10 ft band at its edge (CL-01, CL-02).
    ///
    /// Where a section's face is still above or below the ground at the footprint's edge, the face is the min/max of
    /// the ground and the bench's 1:1 envelope, so the section always meets the ground continuously wherever the
    /// envelope reaches it; the edge step that remains is reported (<see cref="Result.EdgeStepMetres"/>).
    /// Positions are metres in the edit lattice's frame (x east, y north).
    /// </summary>
    public static class TrailGrader
    {
        public const double StationMetres = 7.62;
        public const double MinBenchMetres = 7.62;
        public const double FaceSlope = 1;
        /// <summary>CL-02: the ragged band along the footprint's edge, 10 ft.</summary>
        public const double EdgeBandMetres = 3.048;
        public const int MaxStations = 800;

        public sealed class Result
        {
            public TerrainEdits Edits { get; internal set; }
            public int Stations { get; internal set; }
            public int UngradedStations { get; internal set; }
            /// <summary>GR-07's ungraded length: the ungraded stations' share of the centreline.</summary>
            public double UngradedMetres { get; internal set; }
            public double LengthMetres { get; internal set; }
            /// <summary>GR-08, in cubic metres (1 m² per lattice point).</summary>
            public double CutCubicMetres { get; internal set; }
            public double FillCubicMetres { get; internal set; }
            public int ClearedCells { get; internal set; }
            /// <summary>The largest height difference between the design and the ground within 1 m of the footprint's edge.</summary>
            public double EdgeStepMetres { get; internal set; }
            public IReadOnlyList<EditTileKey> Tiles { get; internal set; }
        }

        /// <summary>
        /// Grades (or, for a natural trail, only clears) a trail along <paramref name="centreline"/> (head first),
        /// <paramref name="widthMetres"/> wide, over the current edited heights; returns the edits with it on top.
        /// <paramref name="roundMetres"/> rounds the crest and toe of each face over that distance (GR-12, 0 = sharp).
        /// </summary>
        public static Result Grade(EditedHeights current, IReadOnlyList<double> centreline, double widthMetres, bool graded,
                                   ulong seed, TrailId id, double roundMetres = 0)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (centreline == null || centreline.Count < 4 || centreline.Count % 2 != 0)
                throw new ArgumentException("The centreline needs at least two points, as x, y pairs.");
            if (widthMetres < 1) throw new ArgumentException("The trail needs at least 1 m of width.");
            double half = widthMetres / 2;

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
                double nx = -ey, ny = ex;   // across, to the left of the way down
                z[k] = current.At(cx, cy);
                left[k] = Side(current, cx, cy, nx, ny, half, z[k]);
                right[k] = Side(current, cx, cy, -nx, -ny, half, z[k]);
                ok[k] = graded && left[k] + right[k] >= MinBenchMetres;
                if (graded && !ok[k]) ungraded++;
            }

            // Every lattice point and cell centre near the footprint, located on the centreline: the distance to it,
            // the signed offset (left positive) and how far along. Point (c, r) is lattice.X(c), lattice.Y(r); the cell
            // (c, r) has its centre half a metre east and south of it.
            var lattice = current.Lattice;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int k = 0; k < count; k++)
            {
                minX = Math.Min(minX, line[2 * k]); maxX = Math.Max(maxX, line[2 * k]);
                minY = Math.Min(minY, line[2 * k + 1]); maxY = Math.Max(maxY, line[2 * k + 1]);
            }
            int c0 = (int)Math.Floor(minX - half - 2 - lattice.West), c1 = (int)Math.Ceiling(maxX + half + 2 - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - (maxY + half + 2)), r1 = (int)Math.Ceiling(lattice.North - (minY - half - 2));
            int w = c1 - c0 + 1, h = r1 - r0 + 1;
            var point = Locate(line, along, lattice, c0, r0, w, h, 0, 0, half);
            var cell = Locate(line, along, lattice, c0, r0, w, h, 0.5, -0.5, half);

            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            TerrainEditTile.Builder For(int c, int r)
            {
                var key = EditTileKey.Of(c, r);
                if (!builders.TryGetValue(key, out var b)) builders[key] = b = current.Edits.Get(key).Edit();
                return b;
            }

            var result = new Result { Stations = count, UngradedStations = ungraded, LengthMetres = length };
            result.UngradedMetres = count > 1 ? length * ungraded / count : 0;
            double cut = 0, fill = 0, edgeStep = 0;
            int cleared = 0;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int c = c0 + i, r = r0 + j, at = j * w + i;
                    if (graded && point.Dist[at] <= half)
                    {
                        double ground = current.AtPoint(c, r);
                        double reach = half - point.Dist[at];
                        double edge = current.At(lattice.X(c) + point.OutX[at] * reach, lattice.Y(r) + point.OutY[at] * reach);
                        double design = Design(point.Along[at], point.Offset[at], ground, edge, half, along, z, left, right, ok, roundMetres);
                        double source = current.Source.At(lattice.X(c), lattice.Y(r));
                        long mm = (long)Math.Round((design - source) * 1000, MidpointRounding.AwayFromZero);
                        mm = Math.Max(-TerrainEditTile.DeltaLimitMm, Math.Min(TerrainEditTile.DeltaLimitMm, mm));
                        double change = source + mm / 1000.0 - ground;
                        if (change > 0) fill += change; else cut -= change;
                        if (point.Dist[at] > half - 1) edgeStep = Math.Max(edgeStep, Math.Abs(change));
                        var key = EditTileKey.Of(c, r);
                        For(c, r).SetDeltaMm(c - key.FirstColumn, r - key.FirstRow, (int)mm);
                    }
                    double d = cell.Dist[at];
                    if (d <= half && Clears(seed, id, c, r, half - d))
                    {
                        var key = EditTileKey.Of(c, r);
                        For(c, r).SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
                        cleared++;
                    }
                }

            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var b in builders.Values) tiles.Add(b.Build());
            result.Edits = current.Edits.With(tiles);
            result.Tiles = new List<EditTileKey>(builders.Keys);
            result.CutCubicMetres = cut;
            result.FillCubicMetres = fill;
            result.EdgeStepMetres = edgeStep;
            result.ClearedCells = cleared;
            return result;
        }

        /// <summary>
        /// CL-02: inside the 10 ft band along the edge, a tree's cell is cleared by a keyed hash with a chance rising from
        /// 0 at the edge to 1 at the band's inner side; beyond the band, always.
        /// </summary>
        public static bool Clears(ulong seed, TrailId id, int column, int row, double inside)
        {
            if (inside >= EdgeBandMetres) return true;
            if (inside <= 0) return false;
            ulong hash = DesignKey.Hash(seed, DesignKey.ClearingEdgeTag, id.Value, ((ulong)(uint)column << 32) | (uint)row);
            double u = (hash >> 11) * (1.0 / (1UL << 53));
            return u < inside / EdgeBandMetres;
        }

        /// <summary>Points located on the centreline: per lattice slot, the distance to it, the signed offset and the distance along.</summary>
        public sealed class Located
        {
            public readonly double[] Dist, Offset, Along;
            /// <summary>The unit direction from the nearest centreline point out to the slot.</summary>
            public readonly double[] OutX, OutY;
            public Located(int n)
            {
                Dist = new double[n];
                Offset = new double[n];
                Along = new double[n];
                OutX = new double[n];
                OutY = new double[n];
                for (int i = 0; i < n; i++) Dist[i] = double.MaxValue;
            }
        }

        /// <summary>
        /// Locates the w × h lattice slots from (c0, r0), each at (X(c) + dx, Y(r) + dy), on the station polyline: each
        /// segment visits only the slots within <paramref name="reach"/> of it and keeps the nearest.
        /// </summary>
        public static Located Locate(IReadOnlyList<double> line, double[] along, EditLattice lattice, int c0, int r0, int w, int h,
                                     double dx, double dy, double reach)
        {
            var located = new Located(w * h);
            int count = line.Count / 2;
            for (int k = 0; k + 1 < count; k++)
            {
                double ax = line[2 * k], ay = line[2 * k + 1], bx = line[2 * k + 2], by = line[2 * k + 3];
                double sx = bx - ax, sy = by - ay, len2 = sx * sx + sy * sy;
                if (len2 <= 0) continue;
                double len = Math.Sqrt(len2);
                int ci0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - reach - 1 - dx - lattice.West) - c0);
                int ci1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, bx) + reach + 1 - dx - lattice.West) - c0);
                int rj0 = Math.Max(0, (int)Math.Floor(lattice.North - (Math.Max(ay, by) + reach + 1 - dy)) - r0);
                int rj1 = Math.Min(h - 1, (int)Math.Ceiling(lattice.North - (Math.Min(ay, by) - reach - 1 - dy)) - r0);
                for (int j = rj0; j <= rj1; j++)
                    for (int i = ci0; i <= ci1; i++)
                    {
                        double px = lattice.X(c0 + i) + dx, py = lattice.Y(r0 + j) + dy;
                        double rx = px - ax, ry = py - ay;
                        double t = (rx * sx + ry * sy) / len2;
                        // Clamped to the segment, so the ends come out round (a stroke's footprint is a union of discs, TR-02).
                        double tc = t < 0 ? 0 : t > 1 ? 1 : t;
                        double qx = ax + sx * tc - px, qy = ay + sy * tc - py;
                        double dist = Math.Sqrt(qx * qx + qy * qy);
                        int at = j * w + i;
                        if (dist >= located.Dist[at]) continue;
                        double cross = (sx * ry - sy * rx) / len;   // left of the way down is positive
                        located.Dist[at] = dist;
                        located.Offset[at] = cross >= 0 ? dist : -dist;
                        if (dist > 1e-9) { located.OutX[at] = -qx / dist; located.OutY[at] = -qy / dist; }
                        else { located.OutX[at] = -sy / len; located.OutY[at] = sx / len; }
                        located.Along[at] = along[k] + tc * len;
                    }
            }
            return located;
        }

        /// <summary>The stations: the centreline resampled every 25 ft of its length (TR-03), head first, as x, y pairs.</summary>
        public static List<double> Stations(IReadOnlyList<double> centreline, out double length)
        {
            int n = centreline.Count / 2;
            var cumulative = new double[n];
            for (int i = 1; i < n; i++)
                cumulative[i] = cumulative[i - 1] + Dist(centreline[2 * i - 2], centreline[2 * i - 1], centreline[2 * i], centreline[2 * i + 1]);
            length = cumulative[n - 1];
            if (length < 1) throw new ArgumentException("The centreline needs at least 1 m of length.");
            int count = (int)Math.Ceiling(length / StationMetres) + 1;
            if (count > MaxStations) throw new ArgumentException($"A trail has at most {MaxStations} stations (TR-03).");
            var line = new List<double>(count * 2);
            int seg = 0;
            for (int k = 0; k < count; k++)
            {
                double t = Math.Min(k * StationMetres, length);
                while (seg < n - 2 && cumulative[seg + 1] < t) seg++;
                double span = cumulative[seg + 1] - cumulative[seg];
                double f = span > 0 ? (t - cumulative[seg]) / span : 0;
                line.Add(centreline[2 * seg] + (centreline[2 * seg + 2] - centreline[2 * seg]) * f);
                line.Add(centreline[2 * seg + 1] + (centreline[2 * seg + 3] - centreline[2 * seg + 1]) * f);
            }
            return line;
        }

        static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));

        static void Tangent(IReadOnlyList<double> line, int k, out double ex, out double ey)
        {
            int count = line.Count / 2;
            int a = Math.Max(0, k - 1), b = Math.Min(count - 1, k + 1);
            ex = line[2 * b] - line[2 * a];
            ey = line[2 * b + 1] - line[2 * a + 1];
            double len = Math.Sqrt(ex * ex + ey * ey);
            ex /= len;
            ey /= len;
        }

        /// <summary>GR-03: the widest b with b ≤ d − |g(d) − z| / s for some offset d inside the footprint, one outward scan in 0.5 m steps.</summary>
        static double Side(IHeightSource ground, double cx, double cy, double nx, double ny, double half, double z)
        {
            double best = 0;
            for (double d = 0.5; ; d += 0.5)
            {
                double dd = Math.Min(d, half);
                double b = dd - Math.Abs(ground.At(cx + nx * dd, cy + ny * dd) - z) / FaceSlope;
                if (b > best) best = b;
                if (dd >= half) break;
            }
            return Math.Min(best, half);
        }

        /// <summary>
        /// GR-05: between two stations the bench's elevation and width blend linearly. A station too steep for a bench
        /// (GR-07) makes its half of the stretch natural.
        /// </summary>
        static double Design(double t, double u, double ground, double edge, double half, double[] along, double[] z, double[] left,
                             double[] right, bool[] ok, double round)
        {
            int count = along.Length;
            int k = 0;
            int lo = 0, hi = count - 2;
            while (lo <= hi)   // the last station at or before t
            {
                int mid = (lo + hi) >> 1;
                if (along[mid] <= t) { k = mid; lo = mid + 1; } else hi = mid - 1;
            }
            k = Math.Min(k, count - 2);
            double span = along[k + 1] - along[k];
            double f = span > 0 ? Math.Max(0, Math.Min(1, (t - along[k]) / span)) : 0;
            double zz, bench;
            if (!ok[k] || !ok[k + 1])
            {
                int s = f < 0.5 ? k : k + 1;
                if (!ok[s]) return ground;
                zz = z[s];
                bench = u >= 0 ? left[s] : right[s];
            }
            else
            {
                zz = z[k] + (z[k + 1] - z[k]) * f;
                bench = u >= 0 ? left[k] + (left[k + 1] - left[k]) * f : right[k] + (right[k + 1] - right[k]) * f;
            }
            // GR-06: the face along this point's own line out from the centreline must meet the ground by the footprint's
            // edge. Between stations, and on curves, the blended bench can overreach; narrow it here (to below zero if it
            // must, which only narrows the 1:1 envelope) so the section reaches the edge on natural ground.
            // With GR-12's rounding the face meets the ground that much sooner, so its toe has room to round inside the
            // footprint (finding #12: an unrounded toe at the edge stair-stepped on the 1 m grid).
            bench = Math.Min(bench, half - round - Math.Abs(edge - zz) / FaceSlope);
            // GR-12 v2: the rounding fades out at the footprint's edge, and never takes more than half the bench's width.
            double r = Math.Min(round, Math.Max(0, half - Math.Abs(u)));
            r = Math.Min(r, Math.Max(0, bench));
            return Section(u, ground, zz, bench, r);
        }

        /// <summary>
        /// GR-01, GR-02: level at z out to the bench edge, then the 1:1 envelope: the ground wherever it lies within the
        /// envelope, else the face. With <paramref name="round"/> &gt; 0 the face's crest and toe are rounded (GR-12).
        /// </summary>
        static double Section(double u, double ground, double z, double bench, double round)
        {
            double off = Math.Abs(u) - bench;
            if (off <= 0 && round <= 0) return z;
            double rise = Math.Max(0, off) * FaceSlope;
            if (round <= 0) return Math.Max(z - rise, Math.Min(z + rise, ground));
            // Rounded: the crest (the envelope's corner at the bench edge) becomes a parabola over `round` metres, and the
            // toe (where the face meets the ground) a smooth min (cut) or max (fill) on that side only, tapering to sharp at
            // the bench so the bench itself never moves.
            double soft = SoftRise(off, round) * FaceSlope;
            double k = round * Math.Max(0, Math.Min(1, off / round));
            if (k <= 1e-9) return Math.Max(z - soft, Math.Min(z + soft, ground));
            return ground >= z ? SoftMin(z + soft, ground, k) : SoftMax(z - soft, ground, k);
        }

        /// <summary>max(0, x) with its corner replaced by a parabola over [−r/2, r/2].</summary>
        static double SoftRise(double x, double r)
        {
            double a = r / 2;
            if (x <= -a) return 0;
            if (x >= a) return x;
            return (x + a) * (x + a) / (2 * r);
        }

        static double SoftMin(double a, double b, double k)
        {
            double hh = Math.Max(k - Math.Abs(a - b), 0) / k;
            return Math.Min(a, b) - hh * hh * k * 0.25;
        }

        static double SoftMax(double a, double b, double k) => -SoftMin(-a, -b, k);
    }
}
