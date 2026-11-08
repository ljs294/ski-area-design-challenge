using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A trail's true difficulty (TD-01 v2, TD-02 v2, TD-03, TD-06 v2) and the profile the tools draw (TF-04). Slopes are
    /// grades in percent, the fall-line slope of the 1 m cells, as the Slope angle layer shows them. The true rating is
    /// the game's own; the player sees only the rating they post (TD-07, owner 2026-11-08).
    /// </summary>
    public static class TrailRating
    {
        /// <summary>
        /// TD-02 v2 (owner, 2026-11-07, GU-17): the steepest sustained pitch is six stations (150 ft) averaged. A Domain
        /// constant, not a simulation tuning, so pacing can never change a trail's rating.
        /// </summary>
        public const int SustainedStations = 6;

        /// <summary>TD-06 v2: the estimate's square, 150 ft (46 cells of 1 m) a side.</summary>
        public const int EstimateSquareCells = 46;

        /// <summary>TD-02 v1's window (three stations, 75 ft), kept only for <see cref="Result.LegacyV1Percent"/>.</summary>
        const int V1WindowStations = 3;

        public sealed class Result
        {
            /// <summary>Each station's median cross-section slope, percent (TD-01).</summary>
            public double[] StationSlope { get; internal set; }
            /// <summary>Each station's distance from the head, metres (the grade's).</summary>
            public double[] Along { get; internal set; }
            /// <summary>The graded surface at each station on the centreline (the profile's solid line).</summary>
            public double[] Surface { get; internal set; }
            public double MeanPercent { get; internal set; }
            /// <summary>The steepest 150 ft's mean slope: the rating's value (TD-02 v2).</summary>
            public double MaxPercent { get; internal set; }
            /// <summary>The first station of the steepest 150 ft window.</summary>
            public int SteepestStation { get; internal set; }
            /// <summary>TD-02 v2: the steepest sustained pitch, percent.</summary>
            public double ValuePercent => MaxPercent;
            public SlopeBands.Band Band => SlopeBands.Of(ValuePercent);
            public double DropMetres { get; internal set; }
            /// <summary>
            /// TD-02 v1, (3 × mean + the steepest 75 ft) / 4: what v0.3 rated this run, kept only to post an older save's
            /// trails with the sign they showed (TD-07).
            /// </summary>
            public double LegacyV1Percent { get; internal set; }
        }

        /// <summary>A stretch's steepest sustained pitch (TD-02 v2) and its length-weighted mean. NaN with no stations.</summary>
        public readonly struct Pitch
        {
            public readonly double Percent;
            /// <summary>The window's first station, counted in the stretch.</summary>
            public readonly int First;
            /// <summary>The window's stations: six, or every station of a stretch shorter than that.</summary>
            public readonly int Count;
            public readonly double MeanPercent;

            public Pitch(double percent, int first, int count, double mean)
            {
                Percent = percent;
                First = first;
                Count = count;
                MeanPercent = mean;
            }

            public static Pitch None => new Pitch(double.NaN, 0, 0, double.NaN);
            public bool IsNone => double.IsNaN(Percent);
            public SlopeBands.Band Band => SlopeBands.Of(Percent);
        }

        /// <summary>The harder of two bands: GU-17's gate reads the harder of the true and the posted rating.</summary>
        public static SlopeBands.Band Harder(SlopeBands.Band a, SlopeBands.Band b) => a >= b ? a : b;

        /// <summary>The fall-line slope of cell (column, row), percent, from its four corners on <paramref name="surface"/>.</summary>
        public static double CellSlope(IHeightSource surface, EditLattice lattice, int column, int row)
        {
            double x0 = lattice.X(column), x1 = lattice.X(column + 1), y0 = lattice.Y(row), y1 = lattice.Y(row + 1);
            double a = surface.At(x0, y0), b = surface.At(x1, y0), c = surface.At(x0, y1), d = surface.At(x1, y1);
            double gx = ((b + d) - (a + c)) / 2, gy = ((a + b) - (c + d)) / 2;
            return Math.Sqrt(gx * gx + gy * gy) * 100;
        }

        /// <summary>
        /// TD-02 v2 over stations <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> − 1: the
        /// mean of the steepest <see cref="SustainedStations"/> in a row (all of them when there are fewer), and the
        /// stations' length-weighted mean, each standing for half the way to each neighbour. Ties keep the first window.
        /// </summary>
        public static Pitch Steepest(IReadOnlyList<double> slope, IReadOnlyList<double> along, int first, int count) =>
            Window(slope, along, first, count, SustainedStations);

        static Pitch Window(IReadOnlyList<double> slope, IReadOnlyList<double> along, int first, int count, int stations)
        {
            if (slope == null || along == null) throw new ArgumentNullException(nameof(slope));
            if (count <= 0) return Pitch.None;
            int last = first + count - 1;
            double sum = 0, weight = 0;
            for (int k = first; k <= last; k++)
            {
                double w = (k < last ? along[k + 1] - along[k] : 0) / 2 + (k > first ? along[k] - along[k - 1] : 0) / 2;
                if (count == 1) w = 1;
                sum += slope[k] * w;
                weight += w;
            }
            double mean = weight > 0 ? sum / weight : 0;
            int win = Math.Min(stations, count);
            double best = double.NegativeInfinity;
            int at = first;
            for (int k = first; k + win <= last + 1; k++)
            {
                double s = 0;
                for (int t = 0; t < win; t++) s += slope[k + t];
                s /= win;
                if (s > best) { best = s; at = k; }
            }
            return new Pitch(best, at - first, win, mean);
        }

        /// <summary>
        /// TD-01, TD-02 v2: rates a graded (or natural) trail on <paramref name="surface"/>, the ground with its grade.
        /// </summary>
        public static Result Rate(IHeightSource surface, TrailFootprint fp, TrailGrader.Graded grade)
        {
            var st = grade.Stations;
            int n = st.Length / 2;
            var slope = new double[n];
            var surf = new double[n];
            var cells = new List<double>();
            var seen = new HashSet<long>();
            for (int k = 0; k < n; k++)
            {
                double cx = st[2 * k], cy = st[2 * k + 1];
                surf[k] = surface.At(cx, cy);
                int a = Math.Max(0, k - 1), b = Math.Min(n - 1, k + 1);
                double ex = st[2 * b] - st[2 * a], ey = st[2 * b + 1] - st[2 * a + 1], len = Math.Sqrt(ex * ex + ey * ey);
                if (len <= 0) { slope[k] = k > 0 ? slope[k - 1] : 0; continue; }
                double nx = -ey / len, ny = ex / len;
                cells.Clear();
                seen.Clear();
                // TD-01 v2 (owner, TL15): a benched station reads its bench, the running surface; the rest read the whole
                // section inside the footprint.
                bool bench = grade.IsGraded && grade.Benched[k];
                foreach (int side in new[] { 1, -1 })
                    for (double d = side > 0 ? 0 : 1; ; d += 1)
                    {
                        if (bench && d > (side > 0 ? grade.BenchLeft[k] : grade.BenchRight[k])) break;
                        double x = cx + nx * d * side, y = cy + ny * d * side;
                        fp.Lattice.CellAt(x, y, out int c, out int r);
                        if (!fp.Contains(c, r)) break;
                        if (seen.Add(((long)c << 32) ^ (uint)r)) cells.Add(CellSlope(surface, fp.Lattice, c, r));
                    }
                slope[k] = cells.Count > 0 ? Median(cells) : k > 0 ? slope[k - 1] : 0;
            }

            var along = grade.Along;
            var pitch = Steepest(slope, along, 0, n);
            var v1 = Window(slope, along, 0, n, V1WindowStations);
            return new Result
            {
                StationSlope = slope,
                Along = along,
                Surface = surf,
                MeanPercent = n > 0 ? pitch.MeanPercent : 0,
                MaxPercent = n > 0 ? pitch.Percent : 0,
                SteepestStation = pitch.First,
                DropMetres = n > 0 ? surf[0] - surf[n - 1] : 0,
                LegacyV1Percent = n > 0 ? (3 * v1.MeanPercent + Math.Max(0, v1.Percent)) / 4 : 0,
            };
        }

        /// <summary>
        /// TD-02 v2 for a trail as the player sees it (owner R4, 2026-11-08): only the stations of the segments it holds,
        /// top first, joined where one segment's end meets the next one's start, so a trail cut from a run is rated on its
        /// own stretch and never on the rest of the run. <paramref name="ratingOf"/> gives each run's rating (null for one
        /// not rated yet, which is skipped). <see cref="Pitch.None"/> when no station falls in any segment.
        /// </summary>
        public static Pitch OfTrail(IReadOnlyList<Segment> segments, Func<TrailId, Result> ratingOf)
        {
            if (segments == null) throw new ArgumentNullException(nameof(segments));
            if (ratingOf == null) throw new ArgumentNullException(nameof(ratingOf));
            var slope = new List<double>();
            var along = new List<double>();
            TrailId lastPiece = default;
            int lastIndex = -1;
            double run = 0;
            foreach (var s in segments)
            {
                var r = ratingOf(s.Piece);
                if (r?.StationSlope == null || r.Along == null) continue;
                double from = s.StartMm / 1000.0, to = s.EndMm / 1000.0;
                int n = Math.Min(r.StationSlope.Length, r.Along.Length);
                for (int k = 0; k < n; k++)
                {
                    if (r.Along[k] < from - 1e-6 || r.Along[k] > to + 1e-6) continue;
                    bool samePiece = slope.Count > 0 && s.Piece == lastPiece;
                    if (samePiece && k <= lastIndex) continue;   // a station on the boundary between two segments of one run
                    if (slope.Count > 0)
                        run += samePiece && lastIndex >= 0 ? r.Along[k] - r.Along[lastIndex] : 0;   // a new run starts where the last ended
                    slope.Add(r.StationSlope[k]);
                    along.Add(run);
                    lastPiece = s.Piece;
                    lastIndex = k;
                }
            }
            return Steepest(slope, along, 0, slope.Count);
        }

        [ThreadStatic] static double[] _sums;
        [ThreadStatic] static int[] _counts;
        [ThreadStatic] static float[] _slopes;
        [ThreadStatic] static double[] _colSum;
        [ThreadStatic] static int[] _colCount;

        /// <summary>
        /// TD-06 v2, the estimate before a head and tail exist (owner R5, 2026-11-08): the mean natural slope of the
        /// painted cells in the steepest 150 ft square (<see cref="EstimateSquareCells"/> a side) centred on a painted
        /// cell. A square counts only if it holds at least three quarters as many painted cells as the fullest one, so a
        /// strip of steep edge cells can't decide it; a footprint smaller than the square reads its mean. NaN for an empty
        /// footprint. Allocation-free after the first call on a thread (pooled buffers); the same footprint always gives
        /// the same value.
        /// </summary>
        public static double Estimate(IHeightSource ground, TrailFootprint fp)
        {
            if (fp.IsEmpty) return double.NaN;
            int w = fp.Width, h = fp.Height;
            var slopes = Pool(ref _slopes, w * h);
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    slopes[j * w + i] = fp.Contains(fp.Column0 + i, fp.Row0 + j) ? (float)CellSlope(ground, fp.Lattice, fp.Column0 + i, fp.Row0 + j) : 0f;
            var sums = Pool(ref _sums, fp.Cells);
            var counts = Pool(ref _counts, fp.Cells);
            var colSum = Pool(ref _colSum, w);
            var colCount = Pool(ref _colCount, w);
            Array.Clear(colSum, 0, w);
            Array.Clear(colCount, 0, w);
            const int below = EstimateSquareCells / 2, above = EstimateSquareCells - below - 1;   // rows j − 23 … j + 22
            void Row(int j, int sign)
            {
                if (j < 0 || j >= h) return;
                for (int i = 0; i < w; i++)
                {
                    if (!fp.Contains(fp.Column0 + i, fp.Row0 + j)) continue;
                    colSum[i] += sign * (double)slopes[j * w + i];
                    colCount[i] += sign;
                }
            }
            for (int j = 0; j <= above; j++) Row(j, 1);
            int m = 0, fullest = 0;
            for (int j = 0; j < h; j++)
            {
                if (j > 0) { Row(j + above, 1); Row(j - below - 1, -1); }
                double runSum = 0;
                int runCount = 0;
                for (int i = 0; i <= above && i < w; i++) { runSum += colSum[i]; runCount += colCount[i]; }
                for (int i = 0; i < w; i++)
                {
                    if (i > 0)
                    {
                        int add = i + above, drop = i - below - 1;
                        if (add < w) { runSum += colSum[add]; runCount += colCount[add]; }
                        if (drop >= 0) { runSum -= colSum[drop]; runCount -= colCount[drop]; }
                    }
                    if (!fp.Contains(fp.Column0 + i, fp.Row0 + j)) continue;
                    sums[m] = runSum;
                    counts[m] = runCount;
                    if (runCount > fullest) fullest = runCount;
                    m++;
                }
            }
            double best = double.NaN;
            for (int k = 0; k < m; k++)
            {
                if (counts[k] * 4 < fullest * 3 || counts[k] <= 0) continue;
                double v = sums[k] / counts[k];
                if (double.IsNaN(best) || v > best) best = v;
            }
            return best;
        }

        static T[] Pool<T>(ref T[] buffer, int length)
        {
            if (buffer == null || buffer.Length < length) buffer = new T[Math.Max(length, 64)];
            return buffer;
        }

        static double Median(List<double> v)
        {
            v.Sort();
            int m = v.Count / 2;
            return v.Count % 2 == 1 ? v[m] : (v[m - 1] + v[m]) / 2;
        }
    }
}
