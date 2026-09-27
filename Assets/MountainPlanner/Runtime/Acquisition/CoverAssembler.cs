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
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition
{
    /// <summary>A source raster window planned for a band of an output grid.</summary>
    public sealed class SourceWindow
    {
        public TiffImage Image = null!;
        public int X0, Y0, Width, Height;
        public double OriginX, OriginY, PixelX, PixelY;
        public long Bytes;
    }

    /// <summary>What <see cref="CoverAssembler"/> will read for one layer: row bands and exact bytes.</summary>
    public sealed class CoverPlan
    {
        public GridSpec Grid;
        public readonly List<(int Row0, int Rows, List<SourceWindow> Windows)> Bands = new List<(int, int, List<SourceWindow>)>();
        public readonly List<string> Sources = new List<string>();
        public long Bytes;

        public CoverPlan(GridSpec grid) => Grid = grid;
    }

    /// <summary>
    /// Forest canopy, land cover and tree species on the package grids (0.3 §4.4; task 04b).
    /// Canopy covers the 1 m core only: its files have no overviews, so the 11 km ring would cost
    /// 500–700 MB more (P7); the ring's forest comes from WorldCover and BIGMAP.
    /// </summary>
    public sealed class CoverAssembler
    {
        public const int BandRows = 1000;
        public const int SpeciesSampleGrid = 20;
        public const int MaxSpecies = 24;

        readonly CanopyTiles _canopy;
        readonly WorldCoverTiles _cover;
        readonly BigmapService _bigmap;
        readonly TransferMeter _meter;

        public CoverAssembler(CanopyTiles canopy, WorldCoverTiles cover, BigmapService bigmap, TransferMeter meter)
        {
            _canopy = canopy;
            _cover = cover;
            _bigmap = bigmap;
            _meter = meter;
        }

        static (double X, double Y) ToMercator(AlbersPoint p) => WebMercator.Forward(Albers6350.Inverse(p));

        static (double X, double Y) ToLonLat(AlbersPoint p)
        {
            var g = Albers6350.Inverse(p);
            return (g.Longitude, g.Latitude);
        }

        // ---- canopy -----------------------------------------------------------------------------

        public async Task<CoverPlan> PlanCanopyAsync(GridSpec core, CancellationToken ct)
        {
            var box = core.Bounds;
            var corners = new[] { new AlbersPoint(box.West, box.South), new AlbersPoint(box.West, box.North), new AlbersPoint(box.East, box.South), new AlbersPoint(box.East, box.North) }
                .Select(Albers6350.Inverse);
            var tiles = new List<TiffImage>();
            foreach (string key in CanopyTiles.QuadKeysFor(corners))
            {
                tiles.Add(await _canopy.OpenAsync(key, ct).ConfigureAwait(false));
            }
            var plan = new CoverPlan(core);
            plan.Sources.AddRange(CanopyTiles.QuadKeysFor(corners));
            PlanBands(plan, tiles, ToMercator);
            return plan;
        }

        /// <summary>
        /// Canopy height per 1 m core cell, in 0.25 m steps (0 = open ground, 255 = 63.75 m or taller),
        /// and how many cells the source had no data for (the flora score's coverage).
        /// </summary>
        public async Task<(byte[] Heights, long Missing)> CanopyAsync(CoverPlan plan, ProgressTracker progress, CancellationToken ct)
        {
            var result = new byte[plan.Grid.CellCount];
            long missing = 0;
            await ResampleAsync(plan, progress, "downloading canopy, band", ToMercator, (i, v) =>
            {
                if (float.IsNaN(v)) missing++;
                else result[i] = EncodeCanopy(v);
            }, ct).ConfigureAwait(false);
            return (result, missing);
        }

        /// <summary>A 10 m cell counts as forest when any of its 1 m canopy cells reaches 3 m (D4).</summary>
        public const byte ForestCanopy = 12;
        public const byte WorldCoverTrees = 10;

        /// <summary>
        /// The flora score's measurements (F1): on the core's 10 m cells, how often canopy and WorldCover
        /// agree on forest; over the ring, the share of WorldCover forest that has species data.
        /// </summary>
        public static (double Agreement, double SpeciesCoverage) MeasureFlora(GridSpec core, byte[] canopy, GridSpec coverGrid, byte[] cover,
                                                                             GridSpec speciesGrid, byte[] speciesIds)
        {
            int block = (int)Math.Round(coverGrid.CellSize / core.CellSize);
            int offC = (int)Math.Round((core.West - coverGrid.West) / coverGrid.CellSize);
            int offR = (int)Math.Round((coverGrid.North - core.North) / coverGrid.CellSize);
            long agree = 0, compared = 0;
            for (int br = 0; br < core.Rows / block; br++)
                for (int bc = 0; bc < core.Columns / block; bc++)
                {
                    byte cls = cover[(long)(br + offR) * coverGrid.Columns + bc + offC];
                    if (cls == 0) continue; // no WorldCover data
                    bool canopyForest = false;
                    for (int r = 0; r < block && !canopyForest; r++)
                        for (int c = 0; c < block && !canopyForest; c++)
                            canopyForest = canopy[(long)(br * block + r) * core.Columns + bc * block + c] >= ForestCanopy;
                    compared++;
                    if (canopyForest == (cls == WorldCoverTrees)) agree++;
                }

            long forest = 0, withSpecies = 0;
            for (int r = 0; r < coverGrid.Rows; r++)
                for (int c = 0; c < coverGrid.Columns; c++)
                {
                    if (cover[(long)r * coverGrid.Columns + c] != WorldCoverTrees) continue;
                    forest++;
                    var p = coverGrid.CellCentre(c, r);
                    if (speciesGrid.TryCellAt(p, out int sc, out int sr) && speciesIds[((long)sr * speciesGrid.Columns + sc) * 4] != 0) withSpecies++;
                }
            return (compared > 0 ? (double)agree / compared : 0, forest > 0 ? (double)withSpecies / forest : 1);
        }

        public static byte EncodeCanopy(float metres)
        {
            if (!(metres > 0)) return 0;
            double q = Math.Round(metres * 4, MidpointRounding.AwayFromZero);
            return (byte)Math.Min(255, q);
        }

        // ---- land cover -------------------------------------------------------------------------

        public async Task<CoverPlan> PlanCoverAsync(GridSpec grid, CancellationToken ct)
        {
            var box = grid.Bounds;
            var corners = new[] { new AlbersPoint(box.West, box.South), new AlbersPoint(box.West, box.North), new AlbersPoint(box.East, box.South), new AlbersPoint(box.East, box.North) }
                .Select(Albers6350.Inverse).ToList();
            var names = corners.Select(WorldCoverTiles.TileName).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            var tiles = new List<TiffImage>();
            foreach (string name in names) tiles.Add(await _cover.OpenAsync(name, ct).ConfigureAwait(false));
            var plan = new CoverPlan(grid);
            plan.Sources.AddRange(names);
            PlanBands(plan, tiles, ToLonLat);
            return plan;
        }

        /// <summary>ESA WorldCover class per cell (10 tree cover, 20 shrubland, 30 grassland, 60 bare, 70 snow and ice, 80 water, …).</summary>
        public async Task<byte[]> CoverAsync(CoverPlan plan, ProgressTracker progress, CancellationToken ct)
        {
            var result = new byte[plan.Grid.CellCount];
            await ResampleAsync(plan, progress, "downloading land cover, band", ToLonLat, (i, v) =>
            {
                if (!float.IsNaN(v)) result[i] = (byte)Math.Max(0, Math.Min(255, v));
            }, ct).ConfigureAwait(false);
            return result;
        }

        // ---- shared: plan windows per band, then nearest-neighbour resample ---------------------

        static void PlanBands(CoverPlan plan, List<TiffImage> tiles, Func<AlbersPoint, (double X, double Y)> transform)
        {
            var grid = plan.Grid;
            var counted = new HashSet<(TiffImage, int)>();
            for (int r0 = 0; r0 < grid.Rows; r0 += BandRows)
            {
                int rows = Math.Min(BandRows, grid.Rows - r0);
                // The band's footprint in the source CRS, from its edge cells, padded by two source pixels.
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (int c = 0; c <= grid.Columns; c += Math.Max(1, grid.Columns / 64))
                    foreach (int r in new[] { r0, r0 + rows })
                    {
                        var (x, y) = transform(new AlbersPoint(grid.West + Math.Min(c, grid.Columns) * grid.CellSize, grid.North - r * grid.CellSize));
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                for (int r = r0; r <= r0 + rows; r += Math.Max(1, rows / 16))
                    foreach (int c in new[] { 0, grid.Columns })
                    {
                        var (x, y) = transform(new AlbersPoint(grid.West + c * grid.CellSize, grid.North - r * grid.CellSize));
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }

                var windows = new List<SourceWindow>();
                foreach (var image in tiles)
                {
                    var dir = image.Directories[0];
                    double sx = dir.PixelScale![0], sy = dir.PixelScale[1];
                    double x0 = dir.TiePoint![3] - dir.TiePoint[0] * sx, y0 = dir.TiePoint[4] + dir.TiePoint[1] * sy;
                    int c0 = Math.Max(0, (int)Math.Floor((minX - x0) / sx) - 2), c1 = Math.Min(dir.Width, (int)Math.Ceiling((maxX - x0) / sx) + 2);
                    int q0 = Math.Max(0, (int)Math.Floor((y0 - maxY) / sy) - 2), q1 = Math.Min(dir.Height, (int)Math.Ceiling((y0 - minY) / sy) + 2);
                    if (c1 <= c0 || q1 <= q0) continue;
                    var w = new SourceWindow
                    {
                        Image = image, X0 = c0, Y0 = q0, Width = c1 - c0, Height = q1 - q0,
                        OriginX = x0 + c0 * sx, OriginY = y0 - q0 * sy, PixelX = sx, PixelY = sy,
                        Bytes = image.WindowBytes(0, c0, q0, c1 - c0, q1 - q0),
                    };
                    windows.Add(w);
                    for (int by = q0 / dir.BlockHeight; by <= (q1 - 1) / dir.BlockHeight; by++)
                        for (int bx = c0 / dir.BlockWidth; bx <= (c1 - 1) / dir.BlockWidth; bx++)
                            if (counted.Add((image, by * dir.BlocksAcross + bx))) plan.Bytes += dir.BlockByteCounts[by * dir.BlocksAcross + bx];
                }
                plan.Bands.Add((r0, rows, windows));
            }
        }

        async Task ResampleAsync(CoverPlan plan, ProgressTracker progress, string verb, Func<AlbersPoint, (double X, double Y)> transform,
                                 Action<long, float> store, CancellationToken ct)
        {
            var grid = plan.Grid;
            for (int b = 0; b < plan.Bands.Count; b++)
            {
                var (r0, rows, windows) = plan.Bands[b];
                long start = _meter.Bytes, expected = Math.Max(1, windows.Sum(w => w.Bytes));
                progress.BeginStep(verb, b + 1, plan.Bands.Count, () => (_meter.Bytes - start) / (double)expected, "AWS");
                var data = new List<float[]>();
                foreach (var w in windows) data.Add(await w.Image.ReadWindowAsync(0, w.X0, w.Y0, w.Width, w.Height, ct).ConfigureAwait(false));
                var projector = new GridReprojector(grid, r0, rows, transform);
                for (int r = r0; r < r0 + rows; r++)
                    for (int c = 0; c < grid.Columns; c++)
                    {
                        var (x, y) = projector.At(c, r);
                        float v = float.NaN;
                        for (int k = 0; k < windows.Count && float.IsNaN(v); k++)
                        {
                            var w = windows[k];
                            int col = (int)Math.Floor((x - w.OriginX) / w.PixelX), row = (int)Math.Floor((w.OriginY - y) / w.PixelY);
                            if (col >= 0 && row >= 0 && col < w.Width && row < w.Height) v = data[k][row * w.Width + col];
                        }
                        store((long)r * grid.Columns + c, v);
                    }
            }
        }

        // ---- species ----------------------------------------------------------------------------

        public sealed class SpeciesResult
        {
            public byte[] Ids = Array.Empty<byte>();
            public byte[] Weights = Array.Empty<byte>();
            public readonly List<SpeciesInfo> Species = new List<SpeciesInfo>();
            public int LayersCovering;
        }

        /// <summary>
        /// The top four species per 30 m cell over the ring, with weights (0–255, summing to about 255).
        /// Species present are found by sampling a 20 × 20 grid across every covering layer.
        /// </summary>
        public Task<List<BigmapService.Layer>> PlanSpeciesAsync(GridSpec grid, CancellationToken ct) => _bigmap.LayersAsync(grid.Bounds, ct);

        /// <summary>
        /// The species stage's weight for overall progress: its time is mostly the service thinking, so
        /// estimate seconds (about 10 s per round of four sampling batches, 4 s per round of four maps)
        /// and convert at a typical 4 MB/s, the unit the other stages use.
        /// </summary>
        public static double SpeciesWeight(int layers)
        {
            int batches = (layers + BigmapService.MaxMosaicItems - 1) / BigmapService.MaxMosaicItems;
            int maps = Math.Min(MaxSpecies, Math.Max(4, layers / 3));
            double seconds = Math.Ceiling(batches / 4.0) * 10 + Math.Ceiling(maps / 4.0) * 4;
            return seconds * 4e6;
        }

        public async Task<SpeciesResult> SpeciesAsync(GridSpec grid, List<BigmapService.Layer> layers, ProgressTracker progress, CancellationToken ct)
        {
            var box = grid.Bounds;
            const string Server = "the USDA Forest Service";

            var points = new List<AlbersPoint>();
            for (int j = 0; j < SpeciesSampleGrid; j++)
                for (int i = 0; i < SpeciesSampleGrid; i++)
                    points.Add(new AlbersPoint(box.West + (i + 0.5) * box.Width / SpeciesSampleGrid, box.South + (j + 0.5) * box.Height / SpeciesSampleGrid));
            var batches = layers.Select((l, i) => (l, i)).GroupBy(t => t.i / BigmapService.MaxMosaicItems, t => t.l).Select(g => g.ToList()).ToList();
            var totals = new Dictionary<int, double>();
            int sampled = 0;
            progress.BeginStep("sampling species, batch", 1, Math.Max(1, batches.Count), () => 0, Server);
            using (var gate = new SemaphoreSlim(4))
            {
                await Task.WhenAll(batches.Select(async batch =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var part = await _bigmap.SampleAsync(points, batch, ct).ConfigureAwait(false);
                        lock (totals) foreach (var kv in part) totals[kv.Key] = (totals.TryGetValue(kv.Key, out double t) ? t : 0) + kv.Value;
                        int d = Interlocked.Increment(ref sampled);
                        if (d < batches.Count) progress.BeginStep("sampling species, batch", d + 1, batches.Count, () => 0, Server);
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }

            var result = new SpeciesResult { LayersCovering = layers.Count };
            double all = totals.Values.Sum();
            var present = layers.Where(l => totals.ContainsKey(l.ObjectId))
                                .OrderByDescending(l => totals[l.ObjectId]).ThenBy(l => l.Spcd).Take(MaxSpecies).ToList();
            for (int i = 0; i < present.Count; i++)
                result.Species.Add(new SpeciesInfo
                {
                    Index = i + 1, Spcd = present[i].Spcd, CommonName = present[i].CommonName,
                    ShareOfBiomass = Math.Round(totals[present[i].ObjectId] / Math.Max(all, 1e-9), 4),
                });

            // One map per species present (30 m, small), four at a time.
            var maps = new float[present.Count][];
            int exported = 0;
            long perMap = grid.CellCount * 4;
            long batchStart = _meter.Bytes;
            progress.BeginStep("downloading species map", 1, Math.Max(1, present.Count), () => (_meter.Bytes - batchStart) / (double)(perMap * Math.Min(4, Math.Max(1, present.Count))), Server);
            using (var gate = new SemaphoreSlim(4))
            {
                await Task.WhenAll(present.Select(async (layer, i) =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        maps[i] = await _bigmap.ExportAsync(layer, box, grid.Columns, grid.Rows, ct).ConfigureAwait(false);
                        int d = Interlocked.Increment(ref exported);
                        long doneBytes = _meter.Bytes;
                        if (d < present.Count)
                            progress.BeginStep("downloading species map", d + 1, present.Count,
                                () => (_meter.Bytes - doneBytes) / (double)(perMap * Math.Min(4, present.Count - d)), Server);
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
            }

            (result.Ids, result.Weights) = TopFour(maps, (int)grid.CellCount);
            return result;
        }

        /// <summary>
        /// Per cell, the four species with the most biomass (1-based indices into the species table,
        /// 0 = none) and their shares of those four, scaled to 0–255. Ties break toward the lower index.
        /// </summary>
        public static (byte[] Ids, byte[] Weights) TopFour(float[][] biomass, int cells)
        {
            var ids = new byte[cells * 4];
            var weights = new byte[cells * 4];
            var best = new (int Index, float Value)[4];
            for (int c = 0; c < cells; c++)
            {
                int n = 0;
                for (int s = 0; s < biomass.Length; s++)
                {
                    float v = biomass[s][c];
                    if (!(v > 0)) continue;
                    int pos = n < 4 ? n++ : 4;
                    while (pos > 0 && best[pos - 1].Value < v)
                    {
                        if (pos < 4) best[pos] = best[pos - 1];
                        pos--;
                    }
                    if (pos < 4) best[pos] = (s + 1, v);
                }
                double sum = 0;
                for (int k = 0; k < n; k++) sum += best[k].Value;
                for (int k = 0; k < n; k++)
                {
                    ids[c * 4 + k] = (byte)best[k].Index;
                    weights[c * 4 + k] = (byte)Math.Round(255 * best[k].Value / sum, MidpointRounding.AwayFromZero);
                }
            }
            return (ids, weights);
        }
    }
}
