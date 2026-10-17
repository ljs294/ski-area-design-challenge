using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>
    /// A straight graded test edit (owner ruling P3-05 TE4): a level bench with 1:1 faces down a straight centreline,
    /// cleared over its footprint. It drives task 05's timing (CF-09) and demo; it is a stand-in, not the grading
    /// engine, which task 08 writes to the same edit tiles. It follows the rules' shape for a straight trail:
    /// stations every 25 ft (TR-03), the bench level across at the centreline's natural ground (GR-01), 1:1 faces
    /// (GR-02), GR-03's widest bench per side, stations narrower than 25 ft left natural (GR-04, GR-07), sections
    /// blended linearly between stations (GR-05), and nothing changed outside the footprint (GR-06).
    /// </summary>
    public static class TestBench
    {
        public const double StationMetres = 7.62;
        public const double MinBenchMetres = 7.62;
        public const double FaceSlope = 1;

        public readonly struct Result
        {
            public readonly TerrainEdits Edits;
            public readonly int Stations;
            public readonly int UngradedStations;
            /// <summary>The edit tiles this edit wrote, in key order.</summary>
            public readonly IReadOnlyList<EditTileKey> Tiles;

            public Result(TerrainEdits edits, int stations, int ungraded, IReadOnlyList<EditTileKey> tiles)
            {
                Edits = edits;
                Stations = stations;
                UngradedStations = ungraded;
                Tiles = tiles;
            }
        }

        /// <summary>
        /// Grades a bench from <paramref name="headX"/>, <paramref name="headY"/> to the tail, <paramref name="widthMetres"/>
        /// wide, over the current edited heights; returns the edits with it committed on top.
        /// </summary>
        public static Result Build(EditedHeights current, double headX, double headY, double tailX, double tailY, double widthMetres)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double dx = tailX - headX, dy = tailY - headY, length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1 || widthMetres < 1) throw new ArgumentException("The bench needs at least 1 m of length and width.");
            double ex = dx / length, ey = dy / length, nx = -ey, ny = ex;   // along, and across to the left
            double half = widthMetres / 2;

            // Stations: the bench elevation and its width on each side (GR-01, GR-03, GR-04).
            int count = (int)Math.Ceiling(length / StationMetres) + 1;
            var along = new double[count];
            var z = new double[count];
            var left = new double[count];
            var right = new double[count];
            var leftMeet = new double[count];
            var rightMeet = new double[count];
            var graded = new bool[count];
            int ungraded = 0;
            for (int k = 0; k < count; k++)
            {
                double t = Math.Min(k * StationMetres, length);
                along[k] = t;
                double cx = headX + ex * t, cy = headY + ey * t;
                z[k] = current.At(cx, cy);
                left[k] = Side(current, cx, cy, nx, ny, half, z[k]);
                right[k] = Side(current, cx, cy, -nx, -ny, half, z[k]);
                leftMeet[k] = Meet(current, cx, cy, nx, ny, half, z[k], left[k]);
                rightMeet[k] = Meet(current, cx, cy, -nx, -ny, half, z[k], right[k]);
                graded[k] = left[k] + right[k] >= MinBenchMetres;
                if (!graded[k]) ungraded++;
            }

            // The footprint's lattice points and cells, tile by tile.
            var lattice = current.Lattice;
            double minX = Math.Min(headX, tailX) - half - 1, maxX = Math.Max(headX, tailX) + half + 1;
            double minY = Math.Min(headY, tailY) - half - 1, maxY = Math.Max(headY, tailY) + half + 1;
            int c0 = (int)Math.Floor(minX - lattice.West), c1 = (int)Math.Ceiling(maxX - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - maxY), r1 = (int)Math.Ceiling(lattice.North - minY);
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            TerrainEditTile.Builder For(int c, int r)
            {
                var key = EditTileKey.Of(c, r);
                if (!builders.TryGetValue(key, out var b)) builders[key] = b = current.Edits.Get(key).Edit();
                return b;
            }
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    // The point: grade it if it's inside the footprint.
                    double px = lattice.X(c), py = lattice.Y(r);
                    if (Inside(px - headX, py - headY, ex, ey, nx, ny, length, half, out double t, out double u))
                    {
                        double ground = current.AtPoint(c, r);
                        double design = Design(t, u, ground, along, z, left, right, leftMeet, rightMeet, graded);
                        double source = current.Source.At(px, py);
                        long mm = (long)Math.Round((design - source) * 1000, MidpointRounding.AwayFromZero);
                        mm = Math.Max(-TerrainEditTile.DeltaLimitMm, Math.Min(TerrainEditTile.DeltaLimitMm, mm));
                        var key = EditTileKey.Of(c, r);
                        For(c, r).SetDeltaMm(c - key.FirstColumn, r - key.FirstRow, (int)mm);
                    }
                    // The cell: cleared if its centre is inside (CL-01).
                    if (Inside(px + 0.5 - headX, py - 0.5 - headY, ex, ey, nx, ny, length, half, out _, out _))
                    {
                        var key = EditTileKey.Of(c, r);
                        For(c, r).SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
                    }
                }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var b in builders.Values) tiles.Add(b.Build());
            return new Result(current.Edits.With(tiles), count, ungraded, new List<EditTileKey>(builders.Keys));
        }

        /// <summary>GR-03: the widest b with b ≤ d − |g(d) − z| / s for some offset d inside the footprint, one outward scan.</summary>
        static double Side(IHeightSource ground, double cx, double cy, double nx, double ny, double half, double z)
        {
            double best = 0;
            for (double d = 1; ; d += 1)
            {
                double dd = Math.Min(d, half);
                double b = dd - Math.Abs(ground.At(cx + nx * dd, cy + ny * dd) - z) / FaceSlope;
                if (b > best) best = b;
                if (dd >= half) break;
            }
            return Math.Min(best, half);
        }

        /// <summary>Where a face from bench edge <paramref name="bench"/> first meets the ground (GR-02), in 0.5 m steps; the footprint's edge if it doesn't.</summary>
        static double Meet(IHeightSource ground, double cx, double cy, double nx, double ny, double half, double z, double bench)
        {
            for (double d = bench + 0.5; d < half; d += 0.5)
                if ((d - bench) * FaceSlope >= Math.Abs(ground.At(cx + nx * d, cy + ny * d) - z)) return d;
            return half;
        }

        static bool Inside(double rx, double ry, double ex, double ey, double nx, double ny, double length, double half, out double t, out double u)
        {
            t = rx * ex + ry * ey;
            u = rx * nx + ry * ny;
            return t >= 0 && t <= length && Math.Abs(u) <= half;
        }

        /// <summary>
        /// GR-05: between two stations the section's parameters, its bench elevation, width and where its faces meet the
        /// ground, blend linearly, so the bench edge runs straight from station to station. A station too steep for a
        /// bench (GR-07) makes its half of the stretch natural.
        /// </summary>
        static double Design(double t, double u, double ground, double[] along, double[] z, double[] left, double[] right,
                             double[] leftMeet, double[] rightMeet, bool[] graded)
        {
            int k = Math.Min(along.Length - 2, (int)(t / StationMetres));
            double span = along[k + 1] - along[k];
            double w = span > 0 ? Math.Max(0, Math.Min(1, (t - along[k]) / span)) : 0;
            if (!graded[k] || !graded[k + 1])
            {
                int s = w < 0.5 ? k : k + 1;
                return Section(u, ground, z[s], u >= 0 ? left[s] : right[s], u >= 0 ? leftMeet[s] : rightMeet[s], graded[s]);
            }
            double Lerp(double[] a) => a[k] + (a[k + 1] - a[k]) * w;
            return Section(u, ground, Lerp(z), u >= 0 ? Lerp(left) : Lerp(right), u >= 0 ? Lerp(leftMeet) : Lerp(rightMeet), true);
        }

        /// <summary>
        /// GR-01, GR-02: level at z out to the bench edge, then a 1:1 face until it meets the ground; natural ground from
        /// where the face first meets it, so the section never steps at the footprint's edge.
        /// </summary>
        static double Section(double u, double ground, double z, double bench, double meet, bool graded)
        {
            if (!graded) return ground;
            double a = Math.Abs(u);
            double off = a - bench;
            if (off <= 0) return z;
            if (a >= meet) return ground;
            double rise = off * FaceSlope;
            return Math.Abs(ground - z) <= rise ? ground : z + Math.Sign(ground - z) * rise;
        }
    }
}
