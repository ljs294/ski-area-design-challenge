using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MountainPlanner.DataSpike.Geo;
using MountainPlanner.DataSpike.IO;
using MountainPlanner.DataSpike.Providers;
using MountainPlanner.DataSpike.Raster;
using MountainPlanner.DataSpike.Tiff;

// Phase 1 task 01 data spike (docs/plans/phase0-0.7-phase1-plan.md).
//   site      --name N --lat L --lon L [--km 5] [--out file.json] [--record dir] [--grids dir]
//   coverage  [--out file.json]
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = ParseArgs(args);
var report = new Dictionary<string, object?>();
try
{
    switch (args.FirstOrDefault())
    {
        case "site": await Site(opts, report); break;
        case "coverage": await Coverage(report); break;
        case "tiffinfo":
            var info = await TiffImage.OpenAsync(new HttpRangeSource(args[1]));
            foreach (var d in info.Directories)
                Console.WriteLine($"IFD {d.Index}: {d.Width}x{d.Height} {(d.Tiled ? "tiles" : "strips")} {d.BlockWidth}x{d.BlockHeight} bits {d.BitsPerSample} fmt {d.SampleFormat} comp {d.Compression} pred {d.Predictor} " +
                                  $"epsg {d.Epsg} scale [{string.Join(",", d.PixelScale ?? Array.Empty<double>())}] tie [{string.Join(",", d.TiePoint ?? Array.Empty<double>())}] nodata {d.NoData}");
            break;
        default:
            Console.WriteLine("usage: site --name N --lat L --lon L [--km 5] [--out f.json] [--record dir] [--grids dir] | coverage [--out f.json]");
            return 2;
    }
}
finally
{
    if (opts.TryGetValue("out", out var outPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report → {outPath}");
    }
}
return 0;

// ---------------------------------------------------------------------------------------------

static async Task Coverage(Dictionary<string, object?> report)
{
    var stats = new TransferStats();
    var sw = Stopwatch.StartNew();
    var tiles = await S1m.ListAllTilesAsync(stats, default);
    report["s1mTiles"] = tiles.Count;
    report["listSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
    report["requests"] = stats.Requests;
    report["bytes"] = stats.Bytes;
    Console.WriteLine($"S1M coverage: {tiles.Count} tiles listed in {sw.Elapsed.TotalSeconds:F1}s, {stats.Requests} requests, {stats.Bytes / 1e6:F1} MB");
    stats.Reset(); sw.Restart();
    var folders = await S1m.ListTileFoldersAsync(stats, default);
    report["folderListing"] = new { tiles = folders.Count, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), requests = stats.Requests, megabytes = Math.Round(stats.Bytes / 1e6, 2) };
    Console.WriteLine($"S1M coverage (folders only): {folders.Count} tiles in {sw.Elapsed.TotalSeconds:F1}s, {stats.Requests} requests, {stats.Bytes / 1e6:F2} MB");
}

static async Task Site(Dictionary<string, string> o, Dictionary<string, object?> report)
{
    string name = o.GetValueOrDefault("name", "site");
    double lat = double.Parse(o["lat"]), lon = double.Parse(o["lon"]);
    double km = double.Parse(o.GetValueOrDefault("km", "5"));
    Albers6350.Forward(lat, lon, out double cx, out double cy);
    var core = AlbersBox.Around(cx, cy, km * 1000);
    var ring = core.Expand(3000);
    report["site"] = new { name, lat, lon, km, albersX = Math.Round(cx, 1), albersY = Math.Round(cy, 1), parallelScale = Math.Round(Albers6350.ParallelScale(lat), 6) };
    Console.WriteLine($"== {name}: {km} km at ({lat}, {lon}) → Albers ({cx:F0}, {cy:F0})");

    // ---- elevation, core at 1 m ----
    var elevStats = new TransferStats();
    var sw = Stopwatch.StartNew();
    int size = (int)(km * 1000);
    var coreGrid = new GeoRaster(Filled(size * size, float.NaN), size, size, core.West, core.North, 1, 1);
    var tiles = new List<(string Name, string? Url)>();
    // The overview check reads the block at the site centre, so it needs the tile holding the centre.
    TiffImage? centreS1m = null;
    string centreTile = S1m.TileName(cx, cy, out _, out _, out _);
    foreach (var (tx, ty) in TilePoints(core))
    {
        string tile = S1m.TileName(tx, ty, out string folder, out _, out _);
        string? url = await S1m.FindTileUrlAsync(tile, folder, elevStats, default);
        tiles.Add((tile, url));
        if (url == null) continue;
        var tiff = await TiffImage.OpenAsync(new HttpRangeSource(url, elevStats));
        if (tile == centreTile) centreS1m = tiff;
        var part = await GeoRaster.ReadBoxAsync(tiff, 0, core.West, core.South, core.East, core.North, default);
        if (part != null) Paste(part, coreGrid);
    }
    int s1mMissing = coreGrid.CountNaN();
    double s1mSeconds = sw.Elapsed.TotalSeconds;
    long s1mBytes = elevStats.Bytes, s1mRequests = elevStats.Requests;
    Console.WriteLine($"  S1M core: tiles {string.Join(", ", tiles.Select(t => t.Name + (t.Url == null ? " (none)" : "")))}; " +
                      $"{s1mBytes / 1e6:F1} MB in {s1mRequests} requests, {s1mSeconds:F1}s; missing {100.0 * s1mMissing / coreGrid.Data.Length:F2}%");

    // Fallback for cells S1M does not cover: the 3DEP dynamic service (0.3 §4.2).
    double fallbackSeconds = 0; long fallbackBytes = 0;
    if (s1mMissing > 0)
    {
        var fb = new TransferStats();
        var sw2 = Stopwatch.StartNew();
        await FallbackFill(core, 1, coreGrid, fb);
        fallbackSeconds = sw2.Elapsed.TotalSeconds; fallbackBytes = fb.Bytes;
        Console.WriteLine($"  3DEP fallback export: {fallbackBytes / 1e6:F1} MB, {fallbackSeconds:F1}s; still missing {coreGrid.CountNaN()} cells");
    }

    // Source mix and quality score (T18) from a 5×5 sample of the finest source per point.
    var mix = new Dictionary<string, int>();
    double scoreSum = 0; int samples = 0;
    for (int j = 0; j < 5; j++)
        for (int i = 0; i < 5; i++)
        {
            double px = core.West + (i + 0.5) * core.Width / 5, py = core.South + (j + 0.5) * core.Height / 5;
            string label; double score;
            if (!float.IsNaN(coreGrid.SampleNearest(px, py)) && tiles.Any(t => t.Url != null && S1m.TileName(px, py, out _, out _, out _) == t.Name))
            { label = "S1M 1 m"; score = 100; }
            else
            {
                var (source, cell) = await Dep3.IdentifySourceAsync(px, py, null, default);
                label = cell <= 1.5 ? $"1 m lidar ({source})" : cell <= 5 ? $"~3 m ({source})" : $"~10 m ({source})";
                score = cell <= 1.5 ? 95 : cell <= 5 ? 60 : 30;
            }
            mix[label] = mix.GetValueOrDefault(label) + 1;
            scoreSum += score; samples++;
        }
    int quality = (int)Math.Round(scoreSum / samples);
    string oneLiner = $"Terrain quality {quality}/100: " + string.Join(", ", mix.OrderByDescending(kv => kv.Value).Select(kv => $"{100 * kv.Value / samples}% {kv.Key}"));
    Console.WriteLine($"  {oneLiner}");
    report["elevationCore"] = new
    {
        tiles = tiles.Select(t => new { t.Name, available = t.Url != null }).ToArray(),
        s1mMegabytes = Math.Round(s1mBytes / 1e6, 1), s1mRequests, s1mSeconds = Math.Round(s1mSeconds, 1),
        s1mMissingPercent = Math.Round(100.0 * s1mMissing / coreGrid.Data.Length, 2),
        fallbackMegabytes = Math.Round(fallbackBytes / 1e6, 1), fallbackSeconds = Math.Round(fallbackSeconds, 1),
        remainingNaN = coreGrid.CountNaN(),
        min = Math.Round(coreGrid.Data.Where(v => !float.IsNaN(v)).DefaultIfEmpty().Min(), 1),
        max = Math.Round(coreGrid.Data.Where(v => !float.IsNaN(v)).DefaultIfEmpty().Max(), 1),
        qualityScore = quality, oneLiner, sourceMix = mix,
    };

    // ---- acceptance: 1 m blocks average to the file's own 2 m overview ----
    if (centreS1m != null)
        report["overviewCheck"] = await OverviewCheck(centreS1m, core, o.GetValueOrDefault("record"));

    // ---- elevation, surround ring at 2 m ----
    var ringStats = new TransferStats();
    sw.Restart();
    int ringSize = (int)(ring.Width / 2);
    var ringGrid = new GeoRaster(Filled(ringSize * ringSize, float.NaN), ringSize, ringSize, ring.West, ring.North, 2, 2);
    foreach (var (tx, ty) in TilePoints(ring))
    {
        string tile = S1m.TileName(tx, ty, out string folder, out _, out _);
        string? url = await S1m.FindTileUrlAsync(tile, folder, ringStats, default);
        if (url == null) continue;
        var tiff = await TiffImage.OpenAsync(new HttpRangeSource(url, ringStats));
        var part = await GeoRaster.ReadBoxAsync(tiff, 1, ring.West, ring.South, ring.East, ring.North, default);
        if (part != null) Paste(part, ringGrid);
    }
    int ringMissing = ringGrid.CountNaN();
    long ringS1mBytes = ringStats.Bytes;
    if (ringMissing > 0)
    {
        await FallbackFill(ring, 2, ringGrid, ringStats);
    }
    report["elevationRing"] = new
    {
        sizeKm = ring.Width / 1000, cellMetres = 2,
        s1mMegabytes = Math.Round(ringS1mBytes / 1e6, 1), totalMegabytes = Math.Round(ringStats.Bytes / 1e6, 1),
        requests = ringStats.Requests, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        s1mMissingPercent = Math.Round(100.0 * ringMissing / ringGrid.Data.Length, 2), remainingNaN = ringGrid.CountNaN(),
    };
    Console.WriteLine($"  Ring 2 m ({ring.Width / 1000} km): {ringStats.Bytes / 1e6:F1} MB, {sw.Elapsed.TotalSeconds:F1}s; S1M missing {100.0 * ringMissing / ringGrid.Data.Length:F1}%");

    // ---- alignment: S1M vs the 3DEP service over 1 km at the centre ----
    if (centreS1m != null && s1mMissing < coreGrid.Data.Length)
        report["elevationAlignment"] = await ElevationAlignment(coreGrid, AlbersBox.Around(cx, cy, 1000));

    // ---- ground cover and species on a common 10 m Albers grid ----
    int cells = size / 10;
    var canopy10 = await CanopyOn10m(core, cells, report);
    var cover10 = await WorldCoverOn10m(core, cells, report);
    report["coverAlignment"] = CoverAlignment(canopy10, cover10, cells);
    if (o.TryGetValue("grids", out string? gridDir))
    {
        // Row-major float32 grids (north row first) for the forest-truth comparison in research/.
        Directory.CreateDirectory(gridDir);
        File.WriteAllBytes(Path.Combine(gridDir, "canopy10.f32"), ToBytes(canopy10));
        File.WriteAllBytes(Path.Combine(gridDir, "worldcover10.f32"), ToBytes(cover10));
        File.WriteAllText(Path.Combine(gridDir, "grid.json"), JsonSerializer.Serialize(new { epsg = 6350, west = core.West, south = core.South, east = core.East, north = core.North, cell = 10, cells }));
        Console.WriteLine($"  10 m grids → {gridDir}");
    }
    await Species(core, cx, cy, report);
    await LandfireCheck(core, report);
}

// ---- checks ----------------------------------------------------------------------------------

static async Task<object> OverviewCheck(TiffImage tiff, AlbersBox core, string? recordDir)
{
    // One full-resolution block inside the site and the matching part of the 2 m overview.
    var d0 = tiff.Directories[0];
    double x0 = d0.TiePoint![3], y0 = d0.TiePoint[4];
    int col = (int)((core.West + core.Width / 2 - x0)), row = (int)((y0 - (core.South + core.Height / 2)));
    int bx = col / d0.BlockWidth, by = row / d0.BlockHeight;
    int c0 = bx * d0.BlockWidth, r0 = by * d0.BlockHeight;
    int w = Math.Min(d0.BlockWidth, d0.Width - c0), h = Math.Min(d0.BlockHeight, d0.Height - r0);
    float[] full = await tiff.ReadWindowAsync(0, c0, r0, w, h);
    float[] half = await tiff.ReadWindowAsync(1, c0 / 2, r0 / 2, w / 2, h / 2);
    double sum = 0, max = 0; int n = 0, nan = 0;
    foreach (float v in full) if (float.IsNaN(v)) nan++;
    for (int r = 0; r < h / 2; r++)
        for (int c = 0; c < w / 2; c++)
        {
            float a = full[(2 * r) * w + 2 * c], b = full[(2 * r) * w + 2 * c + 1], cc = full[(2 * r + 1) * w + 2 * c], d = full[(2 * r + 1) * w + 2 * c + 1];
            float ov = half[r * (w / 2) + c];
            if (float.IsNaN(a + b + cc + d + ov)) continue;
            double diff = Math.Abs((a + b + cc + d) / 4 - ov);
            sum += diff; max = Math.Max(max, diff); n++;
        }
    double mean = n > 0 ? sum / n : double.NaN;
    Console.WriteLine($"  Overview check (block {bx},{by}): NaN {nan}; mean |avg(1 m) − 2 m| = {mean:F4} m, max {max:F3} m → {(mean <= 0.05 ? "PASS" : "FAIL")}");

    if (recordDir != null)
    {
        Directory.CreateDirectory(recordDir);
        var d1 = tiff.Directories[1];
        int b0 = by * d0.BlocksAcross + bx;
        int b1 = (r0 / 2 / d1.BlockHeight) * d1.BlocksAcross + (c0 / 2 / d1.BlockWidth);
        var src = new HttpRangeSource(tiff.Name);
        File.WriteAllBytes(Path.Combine(recordDir, "s1m-header.bin"), await src.ReadAsync(0, 65536, default));
        File.WriteAllBytes(Path.Combine(recordDir, "s1m-l0-block.bin"), await src.ReadAsync(d0.BlockOffsets[b0], (int)d0.BlockByteCounts[b0], default));
        File.WriteAllBytes(Path.Combine(recordDir, "s1m-l1-block.bin"), await src.ReadAsync(d1.BlockOffsets[b1], (int)d1.BlockByteCounts[b1], default));
        File.WriteAllText(Path.Combine(recordDir, "s1m-fixture.json"), JsonSerializer.Serialize(new
        {
            source = tiff.Name, l0Offset = d0.BlockOffsets[b0], l1Offset = d1.BlockOffsets[b1],
            l0Col = c0, l0Row = r0, width = w, height = h, meanAbsDiff = Math.Round(mean, 6),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  recorded fixture → {recordDir}");
    }
    return new { block = new[] { bx, by }, nanCells = nan, meanAbsDiffMetres = Math.Round(mean, 4), maxAbsDiffMetres = Math.Round(max, 3), pass = mean <= 0.05 };
}

static async Task<object> ElevationAlignment(GeoRaster s1m, AlbersBox box)
{
    int size = (int)box.Width;
    byte[] tif = await Dep3.ExportAsync(box, size, size, null, default);
    var svc = await GeoRaster.ReadBoxAsync(await TiffImage.OpenAsync(new MemoryByteSource(tif)), 0, box.West, box.South, box.East, box.North, default);
    // Mean |S1M − service| for shifts of ±3 m; the best shift should be (0, 0).
    int best = int.MaxValue; (int, int) bestShift = (0, 0); double bestMad = double.MaxValue, zeroMad = double.NaN;
    for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            double sum = 0; int n = 0;
            for (int r = 5; r < size - 5; r += 2)
                for (int c = 5; c < size - 5; c += 2)
                {
                    double x = box.West + c + 0.5, y = box.North - r - 0.5;
                    float a = s1m.SampleNearest(x, y), b = svc!.SampleNearest(x + dx, y + dy);
                    if (float.IsNaN(a) || float.IsNaN(b)) continue;
                    sum += Math.Abs(a - b); n++;
                }
            double mad = sum / Math.Max(1, n);
            if (dx == 0 && dy == 0) zeroMad = mad;
            if (mad < bestMad) { bestMad = mad; bestShift = (dx, dy); best = n; }
        }
    Console.WriteLine($"  Elevation alignment S1M vs 3DEP service: best shift {bestShift}, MAD {bestMad:F3} m (at 0,0: {zeroMad:F3} m)");
    return new { bestShiftMetres = new[] { bestShift.Item1, bestShift.Item2 }, bestMadMetres = Math.Round(bestMad, 3), zeroShiftMadMetres = Math.Round(zeroMad, 3), samples = best };
}

static async Task<float[]> CanopyOn10m(AlbersBox core, int cells, Dictionary<string, object?> report)
{
    var stats = new TransferStats(); var sw = Stopwatch.StartNew();
    LatLonBounds(core, out double s, out double w, out double n, out double e);
    var rasters = new List<GeoRaster>();
    var keys = new HashSet<string>();
    foreach (var (la, lo) in new[] { (s, w), (s, e), (n, w), (n, e) })
    {
        WebMercator.Tile(la, lo, 9, out int tx, out int ty);
        keys.Add(WebMercator.QuadKey(tx, ty, 9));
    }
    WebMercator.Forward(s, w, out double mw, out double ms);
    WebMercator.Forward(n, e, out double me, out double mn);
    foreach (string key in keys)
    {
        var tiff = await TiffImage.OpenAsync(new HttpRangeSource(Canopy.TileUrl(key), stats));
        var part = await GeoRaster.ReadBoxAsync(tiff, 0, mw, ms, me, mn, default);
        if (part != null) rasters.Add(part);
    }
    // Per 10 m cell: fraction of 5×5 sub-samples (2 m apart) under canopy ≥ 3 m, and the tallest sample.
    var heights = new List<float>();
    var grid = new float[cells * cells];
    for (int r = 0; r < cells; r++)
        for (int c = 0; c < cells; c++)
        {
            int under = 0, seen = 0;
            for (int j = 0; j < 5; j++)
                for (int i = 0; i < 5; i++)
                {
                    Albers6350.Inverse(core.West + c * 10 + 1 + 2 * i, core.North - r * 10 - 1 - 2 * j, out double la, out double lo);
                    WebMercator.Forward(la, lo, out double x, out double y);
                    float v = float.NaN;
                    foreach (var ras in rasters) { v = ras.SampleNearest(x, y); if (!float.IsNaN(v)) break; }
                    if (float.IsNaN(v)) continue;
                    seen++; if (v >= 3) { under++; if ((i + j) % 6 == 0) heights.Add(v); }
                }
            grid[r * cells + c] = seen > 0 ? under / (float)seen : float.NaN;
        }
    double cover = grid.Where(v => !float.IsNaN(v)).Average();
    double treeCells = grid.Count(v => v >= 0.1) / (double)grid.Length;
    heights.Sort();
    object Pct(double p) => heights.Count > 0 ? heights[(int)(p * (heights.Count - 1))] : 0f;
    report["canopy"] = new
    {
        tiles = keys.ToArray(), megabytes = Math.Round(stats.Bytes / 1e6, 1), requests = stats.Requests, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
        canopyCoverFraction = Math.Round(cover, 3), cellsWithTreeCover10pct = Math.Round(treeCells, 3),
        treeHeightPercentiles = new { p10 = Pct(0.1), p50 = Pct(0.5), p90 = Pct(0.9), max = Pct(1.0) },
    };
    Console.WriteLine($"  Canopy: {keys.Count} tile(s), {stats.Bytes / 1e6:F1} MB in {stats.Requests} requests, {sw.Elapsed.TotalSeconds:F1}s; canopy cover {cover:P1}, cells ≥10% cover {treeCells:P1}; tree height p50 {Pct(0.5)} m, p90 {Pct(0.9)} m");
    return grid;
}

static async Task<float[]> WorldCoverOn10m(AlbersBox core, int cells, Dictionary<string, object?> report)
{
    var stats = new TransferStats(); var sw = Stopwatch.StartNew();
    LatLonBounds(core, out double s, out double w, out double n, out double e);
    var urls = new HashSet<string> { WorldCover.TileUrl(s, w), WorldCover.TileUrl(s, e), WorldCover.TileUrl(n, w), WorldCover.TileUrl(n, e) };
    var rasters = new List<GeoRaster>();
    foreach (string url in urls)
    {
        var part = await GeoRaster.ReadBoxAsync(await TiffImage.OpenAsync(new HttpRangeSource(url, stats)), 0, w, s, e, n, default);
        if (part != null) rasters.Add(part);
    }
    var grid = OnAlbers10m(core, cells, (la, lo) =>
    {
        foreach (var r in rasters) { float v = r.SampleNearest(lo, la); if (!float.IsNaN(v)) return v; }
        return float.NaN;
    });
    var classes = grid.Where(v => !float.IsNaN(v)).GroupBy(v => (int)v).OrderBy(g => g.Key)
        .ToDictionary(g => g.Key.ToString(), g => Math.Round(g.Count() / (double)grid.Length, 3));
    report["worldCover"] = new { tiles = urls.Select(Path.GetFileName).ToArray(), megabytes = Math.Round(stats.Bytes / 1e6, 1), requests = stats.Requests, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), classFractions = classes };
    Console.WriteLine($"  WorldCover: {stats.Bytes / 1e6:F1} MB in {stats.Requests} requests, {sw.Elapsed.TotalSeconds:F1}s; tree cover (10) {classes.GetValueOrDefault("10"):P1}");
    return grid;
}

static object CoverAlignment(float[] canopy, float[] cover, int cells)
{
    // Agreement between "≥10% canopy cover" and WorldCover "tree cover" for shifts of ±3 cells (10 m).
    double best = -1, zero = double.NaN; (int, int) bestShift = (0, 0);
    for (int dy = -3; dy <= 3; dy++)
        for (int dx = -3; dx <= 3; dx++)
        {
            int agree = 0, n = 0;
            for (int r = 3; r < cells - 3; r++)
                for (int c = 3; c < cells - 3; c++)
                {
                    float a = canopy[r * cells + c], b = cover[(r + dy) * cells + c + dx];
                    if (float.IsNaN(a) || float.IsNaN(b)) continue;
                    if ((a >= 0.1f) == (b == 10)) agree++;
                    n++;
                }
            double f = agree / (double)Math.Max(1, n);
            if (dx == 0 && dy == 0) zero = f;
            if (f > best) { best = f; bestShift = (dx, dy); }
        }
    Console.WriteLine($"  Canopy vs WorldCover forest agreement: {zero:P1} at (0,0); best {best:P1} at shift {bestShift} (10 m cells)");
    return new { agreementAtZero = Math.Round(zero, 3), bestAgreement = Math.Round(best, 3), bestShiftCells = new[] { bestShift.Item1, bestShift.Item2 } };
}

static async Task Species(AlbersBox core, double cx, double cy, Dictionary<string, object?> report)
{
    var stats = new TransferStats(); var sw = Stopwatch.StartNew();
    var top = await Bigmap.SpeciesMixAsync(core, 10, stats, default);
    double identifySeconds = sw.Elapsed.TotalSeconds;
    var exports = new List<object>();
    foreach (var sp in top.Take(3))
    {
        var sw2 = Stopwatch.StartNew();
        try
        {
            byte[] tif = await Bigmap.ExportSpeciesAsync(sp.Spcd, core, stats, default);
            var r = await GeoRaster.ReadBoxAsync(await TiffImage.OpenAsync(new MemoryByteSource(tif)), 0, core.West, core.South, core.East, core.North, default);
            int present = r?.Data.Count(v => v > 0 && !float.IsNaN(v)) ?? 0;
            exports.Add(new { sp.Spcd, sp.CommonName, seconds = Math.Round(sw2.Elapsed.TotalSeconds, 1), kilobytes = tif.Length / 1024, cells = r?.Data.Length ?? 0, presentFraction = Math.Round(present / (double)Math.Max(1, r?.Data.Length ?? 1), 3) });
        }
        catch (Exception ex) { exports.Add(new { sp.Spcd, sp.CommonName, error = ex.Message }); }
    }
    report["bigmap"] = new
    {
        sampleGrid = "10x10 getSamples", sampleSeconds = Math.Round(identifySeconds, 1),
        topSpecies = top.Take(10).Select(s => new { s.Spcd, s.CommonName, shareOfBiomass = Math.Round(s.TonsPerAcre / Math.Max(1e-9, top.Sum(t => t.TonsPerAcre)), 3) }).ToArray(),
        exports, megabytes = Math.Round(stats.Bytes / 1e6, 2),
    };
    Console.WriteLine($"  BIGMAP: 10×10 samples {identifySeconds:F1}s → {string.Join(", ", top.Take(6).Select(s => s.CommonName))}; {exports.Count} species exports, total {sw.Elapsed.TotalSeconds:F1}s");
}

static async Task LandfireCheck(AlbersBox core, Dictionary<string, object?> report)
{
    var stats = new TransferStats(); var sw = Stopwatch.StartNew();
    try
    {
        string url = $"{Landfire.Folder}/LF2024_EVT_CONUS/ImageServer/exportImage?bbox={core}&bboxSR=6350&imageSR=6350&size={(int)(core.Width / 30)},{(int)(core.Height / 30)}&format=tiff&f=image";
        byte[] tif = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, default);
        var tiff = await TiffImage.OpenAsync(new MemoryByteSource(tif));
        var d = tiff.Directories[0];
        report["landfireEvt"] = new { seconds = Math.Round(sw.Elapsed.TotalSeconds, 1), kilobytes = tif.Length / 1024, width = d.Width, height = d.Height, bits = d.BitsPerSample, compression = d.Compression };
        Console.WriteLine($"  LANDFIRE EVT export: {tif.Length / 1024} KB, {sw.Elapsed.TotalSeconds:F1}s ({d.Width}×{d.Height}, {d.BitsPerSample}-bit)");
    }
    catch (Exception ex)
    {
        report["landfireEvt"] = new { error = ex.Message };
        Console.WriteLine($"  LANDFIRE EVT export failed: {ex.Message}");
    }
}

// ---- helpers ---------------------------------------------------------------------------------

/// <summary>
/// Fill NaN cells from the 3DEP service in 1,000×1,000-pixel pieces (1 km at 1 m, 2 km at 2 m),
/// four at a time. A whole 5 km site in one request times out (HTTP 504).
/// </summary>
static async Task FallbackFill(AlbersBox box, double cell, GeoRaster grid, TransferStats stats)
{
    double step = 1000 * cell;
    var pieces = new List<AlbersBox>();
    for (double x = box.West; x < box.East; x += step)
        for (double y = box.South; y < box.North; y += step)
        {
            var piece = new AlbersBox(x, y, Math.Min(x + step, box.East), Math.Min(y + step, box.North));
            // Only request pieces that still have holes.
            bool needed = false;
            for (double py = piece.South + cell / 2; py < piece.North && !needed; py += cell * 25)
                for (double px = piece.West + cell / 2; px < piece.East && !needed; px += cell * 25)
                    needed = float.IsNaN(grid.SampleNearest(px, py));
            if (needed) pieces.Add(piece);
        }
    using var gate = new SemaphoreSlim(4);
    await Task.WhenAll(pieces.Select(async piece =>
    {
        await gate.WaitAsync();
        try
        {
            int w = (int)Math.Round(piece.Width / cell), h = (int)Math.Round(piece.Height / cell);
            byte[] tif = await Dep3.ExportAsync(piece, w, h, stats, default);
            var part = await GeoRaster.ReadBoxAsync(await TiffImage.OpenAsync(new MemoryByteSource(tif, "3dep")), 0, piece.West, piece.South, piece.East, piece.North, default);
            if (part != null) lock (grid) FillNaNIn(part, grid, piece);
        }
        finally { gate.Release(); }
    }));
}

static void FillNaNIn(GeoRaster src, GeoRaster dst, AlbersBox piece)
{
    int c0 = (int)Math.Round((piece.West - dst.OriginX) / dst.PixelX), c1 = (int)Math.Round((piece.East - dst.OriginX) / dst.PixelX);
    int r0 = (int)Math.Round((dst.OriginY - piece.North) / dst.PixelY), r1 = (int)Math.Round((dst.OriginY - piece.South) / dst.PixelY);
    for (int r = Math.Max(0, r0); r < Math.Min(dst.Height, r1); r++)
        for (int c = Math.Max(0, c0); c < Math.Min(dst.Width, c1); c++)
        {
            int i = r * dst.Width + c;
            if (float.IsNaN(dst.Data[i])) dst.Data[i] = src.SampleNearest(dst.OriginX + (c + 0.5) * dst.PixelX, dst.OriginY - (r + 0.5) * dst.PixelY);
        }
}

static float[] OnAlbers10m(AlbersBox core, int cells, Func<double, double, float> sampleLatLon)
{
    var grid = new float[cells * cells];
    for (int r = 0; r < cells; r++)
        for (int c = 0; c < cells; c++)
        {
            Albers6350.Inverse(core.West + (c + 0.5) * 10, core.North - (r + 0.5) * 10, out double la, out double lo);
            grid[r * cells + c] = sampleLatLon(la, lo);
        }
    return grid;
}

static void LatLonBounds(AlbersBox box, out double s, out double w, out double n, out double e)
{
    s = w = double.MaxValue; n = e = double.MinValue;
    foreach (var (x, y) in new[] { (box.West, box.South), (box.West, box.North), (box.East, box.South), (box.East, box.North),
                                   ((box.West + box.East) / 2, box.South), ((box.West + box.East) / 2, box.North) })
    {
        Albers6350.Inverse(x, y, out double la, out double lo);
        s = Math.Min(s, la); n = Math.Max(n, la); w = Math.Min(w, lo); e = Math.Max(e, lo);
    }
    double pad = 0.002; s -= pad; w -= pad; n += pad; e += pad;
}

static IEnumerable<(double, double)> TilePoints(AlbersBox box)
{
    for (double x = Math.Floor(box.West / 10000) * 10000; x < box.East; x += 10000)
        for (double y = Math.Floor(box.South / 10000) * 10000; y < box.North; y += 10000)
            yield return (x + 5000, y + 5000);
}

static void Paste(GeoRaster src, GeoRaster dst)
{
    for (int r = 0; r < src.Height; r++)
        for (int c = 0; c < src.Width; c++)
        {
            int dc = (int)Math.Round((src.OriginX + c * src.PixelX - dst.OriginX) / dst.PixelX);
            int dr = (int)Math.Round((dst.OriginY - (src.OriginY - r * src.PixelY)) / dst.PixelY);
            if (dc < 0 || dr < 0 || dc >= dst.Width || dr >= dst.Height) continue;
            float v = src.Data[r * src.Width + c];
            if (!float.IsNaN(v)) dst.Data[dr * dst.Width + dc] = v;
        }
}

static byte[] ToBytes(float[] a) { var b = new byte[a.Length * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); return b; }

static float[] Filled(int n, float v) { var a = new float[n]; Array.Fill(a, v); return a; }

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 1; i < a.Length - 1; i++) if (a[i].StartsWith("--")) d[a[i][2..]] = a[++i];
    return d;
}
