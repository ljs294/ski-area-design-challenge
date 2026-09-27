#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Acquisition.Tiff;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Terrain;

namespace MountainPlanner.Acquisition
{
    /// <summary>What <see cref="HeightAssembler"/> will fetch for one grid: sectors, S1M windows and bytes.</summary>
    public sealed class HeightPlan
    {
        public GridSpec Grid;
        public int Level;
        public readonly List<Sector> Sectors = new List<Sector>();
        public readonly List<string> Tiles = new List<string>();
        public readonly List<string> MissingTiles = new List<string>();
        public long S1mBytes;
        /// <summary>Estimated fallback bytes for cells no S1M tile covers (uncompressed float32).</summary>
        public long FallbackBytesEstimate;

        public long ExpectedBytes => S1mBytes + FallbackBytesEstimate;

        public HeightPlan(GridSpec grid, int level)
        {
            Grid = grid;
            Level = level;
        }

        public sealed class Sector
        {
            public int Column, Row, Width, Height;
            public readonly List<(TiffImage Tile, int X0, int Y0, int Width, int Height, int DestColumn, int DestRow)> Windows =
                new List<(TiffImage, int, int, int, int, int, int)>();
            public long Bytes;
        }
    }

    /// <summary>The assembled heights and where they came from.</summary>
    public sealed class HeightResult
    {
        public float[] Heights = Array.Empty<float>();
        public readonly Dictionary<TerrainSource, long> SourceCells = new Dictionary<TerrainSource, long>();
        public readonly SortedSet<string> FallbackSources = new SortedSet<string>(StringComparer.Ordinal);
        public int FallbackSectors;
        public long FilledHoles;
    }

    /// <summary>
    /// Builds one height grid (core at 1 m or ring at 2 m) on the S1M grid (0.3 §4.2–4.3):
    /// S1M blocks first, then 3DEP for any cell S1M lacks, blended over 50 m so seams show no step.
    /// Works sector by sector (1,000 × 1,000 cells) and reports each sector's exact progress (U6).
    /// </summary>
    public sealed class HeightAssembler
    {
        public const int SectorCells = 1000;
        public const double BlendMetres = 50;

        readonly S1mTiles _s1m;
        readonly Dep3Service _dep3;
        readonly TransferMeter _meter;

        public HeightAssembler(S1mTiles s1m, Dep3Service dep3, TransferMeter meter)
        {
            _s1m = s1m;
            _dep3 = dep3;
            _meter = meter;
        }

        /// <summary>Opens the S1M tiles over the grid and works out every sector's windows and bytes.</summary>
        public async Task<HeightPlan> PlanAsync(GridSpec grid, int level, CancellationToken ct)
        {
            var plan = new HeightPlan(grid, level);
            var tiles = new List<TiffImage>();
            var box = grid.Bounds;
            long uncovered = grid.CellCount;
            foreach (var (name, folder, west, north) in S1mTiles.TilesFor(box))
            {
                var image = await _s1m.OpenAsync(name, folder, ct).ConfigureAwait(false);
                if (image == null)
                {
                    plan.MissingTiles.Add(name);
                    continue;
                }
                plan.Tiles.Add(name);
                tiles.Add(image);
                double w = Math.Max(0, Math.Min(box.East, west + S1mTiles.TileMetres) - Math.Max(box.West, west));
                double h = Math.Max(0, Math.Min(box.North, north) - Math.Max(box.South, north - S1mTiles.TileMetres));
                uncovered -= (long)Math.Round(w * h / (grid.CellSize * grid.CellSize));
            }
            plan.FallbackBytesEstimate = Math.Max(0, uncovered) * 4;
            var counted = new HashSet<(TiffImage, int)>();

            for (int r0 = 0; r0 < grid.Rows; r0 += SectorCells)
                for (int c0 = 0; c0 < grid.Columns; c0 += SectorCells)
                {
                    var sector = new HeightPlan.Sector
                    {
                        Column = c0, Row = r0,
                        Width = Math.Min(SectorCells, grid.Columns - c0), Height = Math.Min(SectorCells, grid.Rows - r0),
                    };
                    double sw = grid.West + c0 * grid.CellSize, sn = grid.North - r0 * grid.CellSize;
                    foreach (var tile in tiles)
                    {
                        var dir = tile.Directories[level];
                        double px = dir.PixelScale![0], x0 = dir.TiePoint![3], y0 = dir.TiePoint[4];
                        if (Math.Abs(px - grid.CellSize) > 1e-6) throw new InvalidOperationException($"S1M level {level} is {px} m, the grid is {grid.CellSize} m.");
                        int tc = (int)Math.Round((sw - x0) / px), tr = (int)Math.Round((y0 - sn) / px);
                        int a0 = Math.Max(0, tc), b0 = Math.Max(0, tr);
                        int a1 = Math.Min(dir.Width, tc + sector.Width), b1 = Math.Min(dir.Height, tr + sector.Height);
                        if (a1 <= a0 || b1 <= b0) continue;
                        sector.Windows.Add((tile, a0, b0, a1 - a0, b1 - b0, c0 + (a0 - tc), r0 + (b0 - tr)));
                        sector.Bytes += tile.WindowBytes(level, a0, b0, a1 - a0, b1 - b0);
                        // The job's total counts each block once; neighbouring sectors share edge blocks.
                        for (int by = b0 / dir.BlockHeight; by <= (b1 - 1) / dir.BlockHeight; by++)
                            for (int bx = a0 / dir.BlockWidth; bx <= (a1 - 1) / dir.BlockWidth; bx++)
                                if (counted.Add((tile, by * dir.BlocksAcross + bx))) plan.S1mBytes += dir.BlockByteCounts[by * dir.BlocksAcross + bx];
                    }
                    plan.Sectors.Add(sector);
                }
            return plan;
        }

        /// <summary>Downloads, fills and blends the grid. <paramref name="classify"/> records sources for the score.</summary>
        public async Task<HeightResult> ExecuteAsync(HeightPlan plan, ProgressTracker progress, bool classify, CancellationToken ct)
        {
            var grid = plan.Grid;
            var heights = new float[grid.CellCount];
            for (long i = 0; i < heights.LongLength; i++) heights[i] = float.NaN;

            // 1. S1M, sector by sector, with exact byte progress. Phases share the stage by expected bytes.
            double s1mShare = plan.ExpectedBytes > 0 ? (double)plan.S1mBytes / plan.ExpectedBytes : 1;
            progress.SetPhase(0, s1mShare);
            for (int s = 0; s < plan.Sectors.Count; s++)
            {
                var sector = plan.Sectors[s];
                long start = _meter.Bytes;
                long expected = Math.Max(1, sector.Bytes);
                progress.BeginStep("downloading sector", s + 1, plan.Sectors.Count, () => (_meter.Bytes - start) / (double)expected, "USGS");
                foreach (var w in sector.Windows)
                {
                    float[] block = await w.Tile.ReadWindowAsync(plan.Level, w.X0, w.Y0, w.Width, w.Height, ct).ConfigureAwait(false);
                    for (int r = 0; r < w.Height; r++)
                        Array.Copy(block, r * w.Width, heights, (long)(w.DestRow + r) * grid.Columns + w.DestColumn, w.Width);
                }
            }

            var result = new HeightResult();
            bool[] fromS1m = new bool[heights.Length];
            long s1mCells = 0;
            for (long i = 0; i < heights.LongLength; i++)
                if (!float.IsNaN(heights[i])) { fromS1m[i] = true; s1mCells++; }
            if (classify && s1mCells > 0) result.SourceCells[TerrainSource.S1m] = s1mCells;
            if (s1mCells == heights.LongLength)
            {
                result.Heights = heights;
                return result;
            }

            // 2. Fallback: every sector with a gap, or within the blend band of one, from 3DEP.
            progress.SetPhase(s1mShare, 0.98);
            float[] gapDistance = DistanceToGaps(fromS1m, grid.Columns, grid.Rows, grid.CellSize, BlendMetres);
            var fallbackSectors = plan.Sectors.Where(sec => AnyWithin(gapDistance, grid.Columns, sec, BlendMetres)).ToList();
            result.FallbackSectors = fallbackSectors.Count;
            var fallback = new float[heights.Length];
            for (long i = 0; i < fallback.LongLength; i++) fallback[i] = float.NaN;
            // Four requests at a time. The step shown is the next sector to finish; its fraction is the
            // bytes received across the requests in flight.
            int done = 0;
            long perSector = (long)SectorCells * SectorCells * 4;
            long batchStart = _meter.Bytes;
            progress.BeginStep("filling from 3DEP, sector", 1, fallbackSectors.Count,
                () => (_meter.Bytes - batchStart) / (double)(perSector * Math.Min(4, fallbackSectors.Count)), "USGS");
            var fetched = new float[fallbackSectors.Count][];
            var identified = new (string Source, double CellSize)?[fallbackSectors.Count];
            var gaps = fallbackSectors.Select(sec =>
            {
                long g = 0;
                for (int r = 0; r < sec.Height; r++)
                    for (int c = 0; c < sec.Width; c++)
                        if (!fromS1m[(long)(sec.Row + r) * grid.Columns + sec.Column + c]) g++;
                return g;
            }).ToArray();
            using (var gate = new SemaphoreSlim(4))
            {
                await Task.WhenAll(fallbackSectors.Select(async (sec, f) =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var box = SectorBox(grid, sec);
                        fetched[f] = await _dep3.ExportAsync(box, sec.Width, sec.Height, ct).ConfigureAwait(false);
                        // Which 3DEP product filled this sector, for the quality score (T18).
                        if (classify && gaps[f] > 0) identified[f] = await _dep3.IdentifySourceAsync(box.Centre, ct).ConfigureAwait(false);
                        int d = Interlocked.Increment(ref done);
                        long doneBytes = _meter.Bytes;
                        if (d < fallbackSectors.Count)
                            progress.BeginStep("filling from 3DEP, sector", d + 1, fallbackSectors.Count,
                                () => (_meter.Bytes - doneBytes) / (double)(perSector * Math.Min(4, fallbackSectors.Count - d)), "USGS");
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }
            for (int f = 0; f < fallbackSectors.Count; f++)
            {
                var sec = fallbackSectors[f];
                float[] values = fetched[f];
                for (int r = 0; r < sec.Height; r++)
                    Array.Copy(values, r * sec.Width, fallback, (long)(sec.Row + r) * grid.Columns + sec.Column, sec.Width);
                if (identified[f] is (string source, double cellSize))
                {
                    var cls = TerrainQuality.FromCellSize(cellSize);
                    result.SourceCells[cls] = (result.SourceCells.TryGetValue(cls, out long n) ? n : 0) + gaps[f];
                    result.FallbackSources.Add(source);
                }
            }

            // 3. Blend: fallback in the gaps; within 50 m of a gap, ease from fallback to S1M.
            progress.SetPhase(0.98, 1);
            progress.BeginStep("blending seams", 1, 1, () => 0.5);
            Blend(heights, fromS1m, fallback, gapDistance);
            result.FilledHoles = FillRemaining(heights, grid.Columns, grid.Rows);
            result.Heights = heights;
            return result;
        }

        /// <summary>
        /// Fills gaps from the fallback and, within <see cref="BlendMetres"/> of a gap, eases from the
        /// fallback to S1M (smoothstep over distance), so the seam has no step (0.3 §4.2).
        /// </summary>
        public static void Blend(float[] heights, bool[] fromS1m, float[] fallback, float[] gapDistance)
        {
            for (long i = 0; i < heights.LongLength; i++)
            {
                float fb = fallback[i];
                if (!fromS1m[i]) heights[i] = fb;
                else if (gapDistance[i] < BlendMetres && !float.IsNaN(fb))
                {
                    double t = Smoothstep(gapDistance[i] / BlendMetres);
                    heights[i] = (float)(fb + (heights[i] - fb) * t);
                }
            }
        }

        static AlbersBox SectorBox(GridSpec grid, HeightPlan.Sector sec) =>
            new AlbersBox(grid.West + sec.Column * grid.CellSize, grid.North - (sec.Row + sec.Height) * grid.CellSize,
                          grid.West + (sec.Column + sec.Width) * grid.CellSize, grid.North - sec.Row * grid.CellSize);

        static double Smoothstep(double t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        static bool AnyWithin(float[] distance, int columns, HeightPlan.Sector sec, double metres)
        {
            for (int r = 0; r < sec.Height; r++)
                for (int c = 0; c < sec.Width; c++)
                    if (distance[(long)(sec.Row + r) * columns + sec.Column + c] <= metres) return true;
            return false;
        }

        /// <summary>
        /// Distance in metres from every cell to the nearest gap cell (a two-pass 3-4 chamfer transform),
        /// capped just above <paramref name="cap"/>. Gap cells are 0.
        /// </summary>
        public static float[] DistanceToGaps(bool[] filled, int columns, int rows, double cell, double cap)
        {
            const float Straight = 3, Diagonal = 4, Scale = 1 / 3f;
            float capUnits = (float)(cap / cell / Scale) + Diagonal;
            var d = new float[filled.Length];
            for (long i = 0; i < d.LongLength; i++) d[i] = filled[i] ? capUnits : 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                {
                    long i = (long)r * columns + c;
                    float v = d[i];
                    if (v == 0) continue;
                    if (c > 0) v = Math.Min(v, d[i - 1] + Straight);
                    if (r > 0)
                    {
                        v = Math.Min(v, d[i - columns] + Straight);
                        if (c > 0) v = Math.Min(v, d[i - columns - 1] + Diagonal);
                        if (c < columns - 1) v = Math.Min(v, d[i - columns + 1] + Diagonal);
                    }
                    d[i] = v;
                }
            for (int r = rows - 1; r >= 0; r--)
                for (int c = columns - 1; c >= 0; c--)
                {
                    long i = (long)r * columns + c;
                    float v = d[i];
                    if (v == 0) continue;
                    if (c < columns - 1) v = Math.Min(v, d[i + 1] + Straight);
                    if (r < rows - 1)
                    {
                        v = Math.Min(v, d[i + columns] + Straight);
                        if (c < columns - 1) v = Math.Min(v, d[i + columns + 1] + Diagonal);
                        if (c > 0) v = Math.Min(v, d[i + columns - 1] + Diagonal);
                    }
                    d[i] = v;
                }
            float toMetres = (float)(cell * Scale);
            for (long i = 0; i < d.LongLength; i++) d[i] *= toMetres;
            return d;
        }

        /// <summary>Fills any cell no source covers (open water at a coast, say) from its neighbours.</summary>
        public static long FillRemaining(float[] heights, int columns, int rows)
        {
            long holes = heights.LongCount(float.IsNaN);
            if (holes == 0 || holes == heights.LongLength) return 0;
            long remaining = holes;
            while (remaining > 0)
            {
                long before = remaining;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < columns; c++)
                    {
                        long i = (long)r * columns + c;
                        if (!float.IsNaN(heights[i])) continue;
                        double sum = 0;
                        int n = 0;
                        if (c > 0 && !float.IsNaN(heights[i - 1])) { sum += heights[i - 1]; n++; }
                        if (c < columns - 1 && !float.IsNaN(heights[i + 1])) { sum += heights[i + 1]; n++; }
                        if (r > 0 && !float.IsNaN(heights[i - columns])) { sum += heights[i - columns]; n++; }
                        if (r < rows - 1 && !float.IsNaN(heights[i + columns])) { sum += heights[i + columns]; n++; }
                        if (n > 0)
                        {
                            heights[i] = (float)(sum / n);
                            remaining--;
                        }
                    }
                if (remaining == before) break;
            }
            return holes;
        }
    }
}
