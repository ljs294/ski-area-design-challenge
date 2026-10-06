using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Roads;
using MountainPlanner.Persistence;

// The downloader and library tool (Phase 1 tasks 04 and 05; 0.3 §5–6).
//   acquire --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2 [--out <folder> | --library <root>] [--cache <folder>]
//   acquire library [--library <root>]            list downloaded mountains
//   acquire prepare --package <folder>            (re)build a package's terrain cache
//   acquire validate --package <folder>           check a package's files
//   acquire cover-map --package <folder> --out <file.ppm> [--core] [--snow]
//                                                 draw the prepared ground cover (whole ring at 4 m, or the core tiles at 1 m)
//   acquire forest-info [--package <folder>]      grow a package's forest (or every downloaded one's) and report trees, species, treeline
//   acquire refresh-osm [--package <folder>] [--cache <folder>] [--no-prepare]
//                                                 adds the roads (task 12d) to a package made before them (or to every such
//                                                 mountain in the library), then re-prepares it
//   acquire forest-dump --package <folder> --out <dir>
//                                                 grow a package's forest and write its trees and 10 m cells (forest-structure report, NE8)
//   acquire species-survey --areas <ski_areas.geojson> --out <survey.jsonl> [--km 5] [--limit N]
//                                                 BIGMAP species at every US ski area (task 09 priority report)
//   acquire species-report --survey <survey.jsonl> [--out <report.md>] [--top 30]
//                                                 ranks species without a model by the flora points they'd add
// Interrupt a download at any time (Ctrl+C) and run it again: it resumes from the cache.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "download";
var opts = new Dictionary<string, string>();
for (int i = 0; i < args.Length - 1; i++) if (args[i].StartsWith("--")) opts[args[i][2..]] = args[++i];
string dataRoot = opts.GetValueOrDefault("library",
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge"));

switch (command)
{
    case "library":
        var entries = ResortLibrary.Scan(dataRoot, measure: true);
        Console.WriteLine($"{entries.Count} mountain(s) in {ResortLibrary.ResortsFolder(dataRoot)}");
        foreach (var e in entries)
            Console.WriteLine($"  {e.Name,-24} {e.SizeKm:0.#} km  terrain {e.TerrainScore,3}/100  flora {e.FloraScore,3}/100  {e.BytesOnDisk / 1e6,7:F1} MB  " +
                              $"{(e.CacheReady ? "ready" : "needs preparing")}  {e.PackageId}");
        return 0;

    case "validate":
    {
        var problems = PackageValidator.Validate(opts["package"]);
        Console.WriteLine(problems.Count == 0 ? "The package is sound." : string.Join(Environment.NewLine, problems));
        return problems.Count == 0 ? 0 : 1;
    }

    case "refresh-osm":
    {
        // Packages made before task 12d have no roads: fetch the site's OpenStreetMap features (the same Overpass query
        // a download makes now) and add the roads layer. The package id changes, so its terrain cache is prepared again.
        // --package <folder> for one; otherwise every mountain in the library that has no roads yet.
        var folders = opts.TryGetValue("package", out string? one) ? new List<string> { one }
            : ResortLibrary.Scan(dataRoot).Select(e => e.Folder).Where(f => !RoadsFile.Has(ResortPackage.ReadManifest(f))).ToList();
        if (folders.Count == 0) Console.WriteLine("Every mountain already has its roads.");
        string cache = opts.GetValueOrDefault("cache", Path.Combine(dataRoot, "download-cache"));
        foreach (string folder in folders)
        {
            var manifest = ResortPackage.ReadManifest(folder);
            var site = SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0);
            var response = await new OsmFeatures(new DiskCache(cache), new TransferMeter()).DownloadAsync(site.Ring, CancellationToken.None);
            var roads = OsmFeatures.ParseRoads(response);
            RoadsFile.Add(folder, manifest, roads);
            if (!manifest.Attribution.Any(a => a.Contains("OpenStreetMap"))) manifest.Attribution.Add("Water and roads: © OpenStreetMap contributors (ODbL).");
            ResortPackage.WriteManifest(folder, manifest);
            Console.WriteLine($"{manifest.Site.Name}: {roads.Count:N0} roads ({roads.Count(r => r.Surface == RoadSurface.Paved):N0} paved, " +
                              $"{roads.Count(r => r.Surface == RoadSurface.Unpaved):N0} unpaved)");
            if (!args.Contains("--no-prepare"))   // --no-prepare: test packages, which keep no cache
            {
                Console.WriteLine("  preparing its terrain...");
                TerrainCache.Build(folder, manifest, null, CancellationToken.None);
            }
        }
        Console.WriteLine("Done.");
        return 0;
    }

    case "cover-map":
    {
        string folder = opts["package"];
        var cache = TerrainCache.ReadManifest(folder);
        bool coreOnly = args.Contains("--core"), snow = args.Contains("--snow");
        var tiles = cache.Tiles.Where(t => !coreOnly || t.Core).ToList();
        int texel = coreOnly ? TerrainCache.CoreCoverResolution - 1 : 256;   // pixels per 1,024 m tile
        int minC = tiles.Min(t => t.Column), minR = tiles.Min(t => t.Row);
        int w = (tiles.Max(t => t.Column) - minC + 1) * texel, h = (tiles.Max(t => t.Row) - minR + 1) * texel;
        var rgb = new byte[w * h * 3];
        // Forest floor, grass, rock, developed, water, paved road, unpaved road (flat class colours, like the in-game overlay).
        var colours = new (double R, double G, double B)[] { (40, 74, 52), (178, 170, 98), (132, 134, 140), (196, 88, 64), (48, 110, 196), (40, 40, 44), (214, 160, 70) };
        foreach (var t in tiles)
        {
            byte[] cover = TerrainCache.ReadCover(folder, t);
            int n = t.CoverResolution, bands = TerrainCache.CoverBands;
            for (int y = 0; y < texel; y++)
                for (int x = 0; x < texel; x++)
                {
                    int i = Math.Min(n - 1, x * (n - 1) / texel), j = Math.Min(n - 1, y * (n - 1) / texel);
                    int o = (j * n + i) * bands;
                    double r = 0, g = 0, b = 0;
                    for (int k = 0; k < colours.Length; k++) { double v = cover[o + k] / 255.0; r += colours[k].R * v; g += colours[k].G * v; b += colours[k].B * v; }
                    if (snow) { double s2 = cover[o + MountainPlanner.Domain.Cover.GroundCover.Layers] / 255.0 * 0.85; r += (245 - r) * s2; g += (248 - g) * s2; b += (252 - b) * s2; }
                    int px = ((t.Row - minR) * texel + y) * w + (t.Column - minC) * texel + x;
                    rgb[px * 3] = (byte)r; rgb[px * 3 + 1] = (byte)g; rgb[px * 3 + 2] = (byte)b;
                }
        }
        using (var fs = File.Create(opts["out"]))
        {
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
            fs.Write(header, 0, header.Length);
            fs.Write(rgb, 0, rgb.Length);
        }
        Console.WriteLine($"Wrote {w}×{h} cover map to {opts["out"]}");
        return 0;
    }

    case "forest-info":
    {
        var folders = opts.TryGetValue("package", out string? one) ? new List<string> { one } : ResortLibrary.Scan(dataRoot).Select(e => e.Folder).ToList();
        foreach (string folder in folders) ForestInfo(folder);
        return 0;
    }

    case "forest-dump":
        ForestDump(opts["package"], opts["out"], opts);
        return 0;

    case "species-survey":
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        return await SpeciesSurvey.RunAsync(opts, opts.GetValueOrDefault("cache", Path.Combine(dataRoot, "download-cache")), stop.Token);
    }

    case "species-report":
        return SpeciesSurvey.Report(opts);

    case "prepare":
    {
        string folder = opts["package"];
        var manifest = ResortPackage.ReadManifest(folder);
        var started = DateTime.UtcNow;
        var cache = TerrainCache.Build(folder, manifest, new Progress<CacheProgress>(p =>
            Console.Write($"\rPreparing terrain tile {p.Tile} of {p.Tiles} · {100 * p.Tile / p.Tiles}%   ")));
        Console.WriteLine($"\n{cache.Tiles.Count} tiles in {(DateTime.UtcNow - started).TotalSeconds:F1} s; heights {cache.HeightMin:F1}–{cache.HeightMin + cache.HeightRange:F1} m, " +
                          $"step {cache.HeightRange / TerrainCache.MaxValue * 100:F2} cm");
        return 0;
    }
}

if (!opts.ContainsKey("lat") || !opts.ContainsKey("lon"))
{
    Console.WriteLine("usage: acquire --name N --lat L --lon L [--km 2] [--out <folder> | --library <root>] [--cache <folder>]");
    Console.WriteLine("       acquire library | prepare --package <folder> | validate --package <folder>");
    return 2;
}

var request = new SiteRequest
{
    Name = opts.GetValueOrDefault("name", "Site"),
    Centre = new GeoPoint(double.Parse(opts["lat"], CultureInfo.InvariantCulture), double.Parse(opts["lon"], CultureInfo.InvariantCulture)),
    SizeKm = double.Parse(opts.GetValueOrDefault("km", "2"), CultureInfo.InvariantCulture),
};
string cacheFolder = opts.GetValueOrDefault("cache", Path.Combine(dataRoot, "download-cache"));
// Without --out, the package goes into the library as Resorts/<packageId> once it's complete.
bool toLibrary = !opts.ContainsKey("out");
string outFolder = opts.GetValueOrDefault("out", Path.Combine(ResortLibrary.ResortsFolder(dataRoot), "_incoming-" + Slug(request.Name)));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"{request.Name}: {request.SizeKm} km at ({request.Centre.Latitude}, {request.Centre.Longitude})");
Console.WriteLine($"Resume cache: {cacheFolder}");
var display = new ProgressLine();
var began = DateTime.UtcNow;
try
{
    var manifest = await new AcquisitionPipeline(cacheFolder).RunAsync(request, outFolder, display, cts.Token);
    display.Done();
    string final = toLibrary ? ResortLibrary.Add(dataRoot, outFolder) : outFolder;
    Console.WriteLine($"Package {manifest.PackageId} ready in {Path.GetFullPath(final)} after {(DateTime.UtcNow - began).TotalSeconds:F0} s");
    Console.WriteLine(manifest.Quality.OneLiner);
    Console.WriteLine(manifest.Flora.OneLiner);
    long size = 0;
    foreach (var f in Directory.GetFiles(final, "*", SearchOption.AllDirectories)) size += new FileInfo(f).Length;
    Console.WriteLine($"On disk {size / 1e6:F1} MB, including the prepared terrain");
    return 0;
}
catch (OperationCanceledException)
{
    display.Done();
    Console.WriteLine("Stopped. Run the same command again to resume where it left off.");
    return 1;
}

static void ForestInfo(string folder)
{
    var manifest = ResortPackage.ReadManifest(folder);
    float[] core = ResortPackage.ReadLayer(folder, manifest, "heights-core", out var coreHeader);
    float[] ring = ResortPackage.ReadLayer(folder, manifest, "heights-ring", out var ringHeader);
    var field = new ForestField(manifest, folder, new TerrainCache.HeightField(core, coreHeader, ring, ringHeader));
    var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0));
    var plan = field.Prepare(tiles);
    new ManagedForestPlanter().Plant(plan);
    long trees = plan.TileCount.Sum(c => (long)c);
    var byModel = new long[MountainPlanner.Domain.Flora.SpeciesMap.Models.Length];
    for (int t = 0; t < plan.TileCountTotal; t++)
        for (int k = 0; k < plan.TileCount[t]; k++) byModel[plan.Points[plan.TileOffset[t] + k].Prototype / plan.Variants]++;
    long krummholzCells = plan.Cells.LongCount(c => c.Krummholz > 0), fullCells = plan.Cells.LongCount(c => c.Krummholz == 255);
    // Where the krummholz is: the 250 m square with the most fully krummholz cells, from the site centre (east, north), for -view.
    var best = (Count: 0, X: 0.0, Z: 0.0);
    var squares = new Dictionary<(int, int), int>();
    for (int j = 0; j < plan.CellsY; j++)
        for (int i = 0; i < plan.CellsX; i++)
            if (plan.Cells[j * plan.CellsX + i].Krummholz == 255)
            {
                var key = (i / 25, j / 25);
                squares[key] = squares.TryGetValue(key, out int n) ? n + 1 : 1;
                if (squares[key] > best.Count)
                {
                    double x = tiles.West + plan.CellOriginX / 256.0 + (key.Item1 * 25 + 12.5) * 10 - manifest.Site.CentreX;
                    double z = tiles.North - tiles.Rows * TileGrid.TileMetres + plan.CellOriginY / 256.0 + (key.Item2 * 25 + 12.5) * 10 - manifest.Site.CentreY;
                    best = (squares[key], x, z);
                }
            }
    Console.WriteLine($"{manifest.Site.Name}: {trees:N0} trees in {plan.TileCountTotal:N0} tiles of 64 m (ring share {field.RingTreeShare:F2})");
    Console.WriteLine("  " + string.Join(", ", byModel.Select((n, m) => (n, m)).Where(p => p.n > 0).OrderByDescending(p => p.n)
        .Select(p => $"{MountainPlanner.Domain.Flora.SpeciesMap.Models[p.m]} {100.0 * p.n / trees:F0}%")));
    Console.WriteLine($"  treeline {(double.IsNaN(field.TreelineMetres) ? "none" : field.TreelineMetres.ToString("F0") + " m")}; " +
                      $"{krummholzCells:N0} forest cells in the krummholz band ({fullCells:N0} fully krummholz)" +
                      (best.Count > 0 ? $"; most of it around {best.X:F0} m east, {best.Z:F0} m north of the centre" : ""));
}

// trees.f32: per tree x, y (Albers metres from the site centre), height (m), crown width scale, model, variant.
// cells.u8: per 10 m cell (rows from the south) kind, conifer, canopy, stand, height code, shade-tolerant conifer. forest.json describes them.
static void ForestDump(string folder, string outDir, Dictionary<string, string> opts)
{
    var manifest = ResortPackage.ReadManifest(folder);
    float[] core = ResortPackage.ReadLayer(folder, manifest, "heights-core", out var coreHeader);
    float[] ring = ResortPackage.ReadLayer(folder, manifest, "heights-ring", out var ringHeader);
    var field = new ForestField(manifest, folder, new TerrainCache.HeightField(core, coreHeader, ring, ringHeader));
    var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0));
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var plan = field.Prepare(tiles);
    double prepare = clock.Elapsed.TotalSeconds;
    // Calibration overrides (forest-structure report): the stand table, the clump field and the stand weight ramps.
    double D(string key, double fallback) => opts.TryGetValue(key, out string? v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
    if (opts.ContainsKey("shortest") || opts.ContainsKey("skew"))
    {
        double shortest = D("shortest", MountainPlanner.Domain.Cover.ForestPlacement.StandShortest), skew = D("skew", MountainPlanner.Domain.Cover.ForestPlacement.StandSkew);
        plan.StandHeights = Enumerable.Range(0, plan.StandHeights.Length)
            .Select(i => (int)Math.Round(256 * (1 - (1 - shortest) * Math.Pow(1 - (double)i / (plan.StandHeights.Length - 1), skew)))).ToArray();
    }
    plan.ClumpLattice = (int)Math.Round(D("lattice", plan.ClumpLattice / 256.0) * 256);
    plan.ClumpRadius = (int)Math.Round(D("radius", plan.ClumpRadius / 256.0) * 256);
    plan.ClumpFloor = (int)D("floor", plan.ClumpFloor);
    if (opts.ContainsKey("ramps"))
    {
        var r = opts["ramps"].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();   // tolerant conifer from,to, canopy from,to
        for (int i = 0; i < plan.Cells.Length; i++)
        {
            ref var c = ref plan.Cells[i];
            if (c.Kind != MountainPlanner.Domain.Cover.ForestCell.Core) continue;
            double w = MountainPlanner.Domain.Cover.GroundCover.SmoothStep(r[0], r[1], c.Tolerant / 255.0) * MountainPlanner.Domain.Cover.GroundCover.SmoothStep(r[2], r[3], c.Canopy / 255.0);
            c.Stand = (byte)Math.Round(w * 255);
        }
    }
    if (opts.ContainsKey("nostand")) for (int i = 0; i < plan.Cells.Length; i++) plan.Cells[i].Stand = 0;
    clock.Restart();
    new ManagedForestPlanter().Plant(plan);
    double plant = clock.Elapsed.TotalSeconds;
    double ox = tiles.West, oy = tiles.North - tiles.Rows * TileGrid.TileMetres;
    Directory.CreateDirectory(outDir);
    long trees = plan.TileCount.Sum(c => (long)c), quota = plan.Points.LongLength;
    using (var w = new BinaryWriter(File.Create(Path.Combine(outDir, "trees.f32"))))
        for (int t = 0; t < plan.TileCountTotal; t++)
            for (int k = 0; k < plan.TileCount[t]; k++)
            {
                var p = plan.Points[plan.TileOffset[t] + k];
                w.Write((float)(ox + p.X / 256.0 - manifest.Site.CentreX));
                w.Write((float)(oy + p.Y / 256.0 - manifest.Site.CentreY));
                w.Write((float)(p.HeightCode * ForestField.HeightStep));
                w.Write((float)(p.Width32 * ForestField.WidthStep));
                w.Write((float)(p.Prototype / plan.Variants));
                w.Write((float)(p.Prototype % plan.Variants));
            }
    var cells = new byte[plan.Cells.Length * 6];
    for (int i = 0; i < plan.Cells.Length; i++)
    {
        var c = plan.Cells[i];
        cells[i * 6] = c.Kind; cells[i * 6 + 1] = c.Conifer; cells[i * 6 + 2] = c.Canopy; cells[i * 6 + 3] = c.Stand; cells[i * 6 + 4] = c.HeightCode; cells[i * 6 + 5] = c.Tolerant;
    }
    File.WriteAllBytes(Path.Combine(outDir, "cells.u8"), cells);
    // canopy.u8: the package's 1 m canopy map (0.25 m steps, rows from the north) for the calibration study.
    string canopyInfo = "";
    if (manifest.Layers.Any(l => l.Id == "canopy-core"))
    {
        byte[] canopy = ResortPackage.ReadByteLayer(folder, manifest, "canopy-core", out var ch);
        File.WriteAllBytes(Path.Combine(outDir, "canopy.u8"), canopy);
        canopyInfo = $"\"canopyWest\": {ch.West}, \"canopyNorth\": {ch.North}, \"canopyWidth\": {ch.Width}, \"canopyHeight\": {ch.Height}, \"canopyCell\": {ch.CellSize}, ";
    }
    string models = string.Join(", ", MountainPlanner.Domain.Flora.SpeciesMap.Models.Select(m => $"\"{m}\""));
    File.WriteAllText(Path.Combine(outDir, "forest.json"),
        $"{{\"name\": \"{manifest.Site.Name}\", \"centreX\": {manifest.Site.CentreX}, \"centreY\": {manifest.Site.CentreY}, \"trees\": {trees}, \"quota\": {quota}, " +
        $"\"cellsX\": {plan.CellsX}, \"cellsY\": {plan.CellsY}, \"cellWest\": {ox + plan.CellOriginX / 256.0}, \"cellSouth\": {oy + plan.CellOriginY / 256.0}, " +
        canopyInfo + $"\"prepareSeconds\": {prepare:F2}, \"plantSeconds\": {plant:F2}, \"models\": [{models}]}}");
    Console.WriteLine($"{manifest.Site.Name}: {trees:N0} of {quota:N0} quota trees, prepare {prepare:F1} s, plant {plant:F1} s → {outDir}");
}

static string Slug(string name)
{
    var chars = name.ToLowerInvariant().ToCharArray();
    for (int i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i])) chars[i] = '-';
    return new string(chars);
}

/// <summary>Renders progress snapshots as a live line (or a line per second when output is redirected).</summary>
sealed class ProgressLine : IProgress<AcquisitionProgress>
{
    readonly object _gate = new object();
    readonly bool _live = !Console.IsOutputRedirected;
    string _stage = "";
    DateTime _lastLine = DateTime.MinValue;
    int _width;

    public void Report(AcquisitionProgress p)
    {
        lock (_gate)
        {
            string bar = new string('#', (int)(p.Overall * 20)).PadRight(20, '.');
            string eta = p.SecondsRemaining is double s ? $" · ~{Format(s)} left" : "";
            string line = $"[{bar}] {p.Overall * 100,3:F0}% | {p.Detail} | {p.DownloadedBytes / 1e6:F1} MB downloaded · {p.BytesPerSecond / 1e6:F1} MB/s{eta}";
            if (p.Stage != _stage && _stage.Length > 0 && _live) Console.WriteLine();
            if (p.Stage != _stage) Console.WriteLine($"Stage {p.StageIndex} of {p.StageCount}: {p.Stage}");
            _stage = p.Stage;
            if (_live)
            {
                Console.Write("\r" + line.PadRight(_width));
                _width = line.Length;
            }
            else if ((DateTime.UtcNow - _lastLine).TotalSeconds >= 1 || p.Finished)
            {
                Console.WriteLine(line);
                _lastLine = DateTime.UtcNow;
            }
        }
    }

    public void Done()
    {
        lock (_gate) if (_live) Console.WriteLine();
    }

    static string Format(double seconds) => seconds >= 90 ? $"{seconds / 60:F0} min" : $"{seconds:F0} s";
}
