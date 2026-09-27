using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;

// The downloader and library tool (Phase 1 tasks 04 and 05; 0.3 §5–6).
//   acquire --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2 [--out <folder> | --library <root>] [--cache <folder>]
//   acquire library [--library <root>]            list downloaded mountains
//   acquire prepare --package <folder>            (re)build a package's terrain cache
//   acquire validate --package <folder>           check a package's files
//   acquire cover-map --package <folder> --out <file.ppm> [--core] [--snow]
//                                                 draw the prepared ground cover (whole ring at 4 m, or the core tiles at 1 m)
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
        var entries = ResortLibrary.Scan(dataRoot);
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
        // Forest floor, grass, rock, developed, water (flat class colours, like the in-game overlay).
        var colours = new (double R, double G, double B)[] { (40, 74, 52), (178, 170, 98), (132, 134, 140), (196, 88, 64), (48, 110, 196) };
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
                    for (int k = 0; k < 5; k++) { double v = cover[o + k] / 255.0; r += colours[k].R * v; g += colours[k].G * v; b += colours[k].B * v; }
                    if (snow) { double s2 = cover[o + 5] / 255.0 * 0.85; r += (245 - r) * s2; g += (248 - g) * s2; b += (252 - b) * s2; }
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
