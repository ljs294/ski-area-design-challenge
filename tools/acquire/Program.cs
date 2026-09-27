using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Domain.Geo;

// Downloads a site and writes its resort package (Phase 1 task 04; 0.3 §6).
//   acquire --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2 --out <package folder> [--cache <folder>]
// Interrupt it at any time (Ctrl+C) and run it again: it resumes from the cache.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = new Dictionary<string, string>();
for (int i = 0; i < args.Length - 1; i++) if (args[i].StartsWith("--")) opts[args[i][2..]] = args[++i];
if (!opts.ContainsKey("lat") || !opts.ContainsKey("lon") || !opts.ContainsKey("out"))
{
    Console.WriteLine("usage: acquire --name N --lat L --lon L [--km 2] --out <folder> [--cache <folder>]");
    return 2;
}

var request = new SiteRequest
{
    Name = opts.GetValueOrDefault("name", "Site"),
    Centre = new GeoPoint(double.Parse(opts["lat"], CultureInfo.InvariantCulture), double.Parse(opts["lon"], CultureInfo.InvariantCulture)),
    SizeKm = double.Parse(opts.GetValueOrDefault("km", "2"), CultureInfo.InvariantCulture),
};
string cache = opts.GetValueOrDefault("cache",
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge", "download-cache"));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine($"{request.Name}: {request.SizeKm} km at ({request.Centre.Latitude}, {request.Centre.Longitude})");
Console.WriteLine($"Resume cache: {cache}");
var display = new ProgressLine();
var started = DateTime.UtcNow;
try
{
    var manifest = await new AcquisitionPipeline(cache).RunAsync(request, opts["out"], display, cts.Token);
    display.Done();
    Console.WriteLine($"Package {manifest.PackageId} written to {Path.GetFullPath(opts["out"])} in {(DateTime.UtcNow - started).TotalSeconds:F0} s");
    Console.WriteLine(manifest.Quality.OneLiner);
    Console.WriteLine(manifest.Flora.OneLiner);
    long size = 0;
    foreach (var f in Directory.GetFiles(opts["out"])) size += new FileInfo(f).Length;
    Console.WriteLine($"Package size {size / 1e6:F1} MB");
    return 0;
}
catch (OperationCanceledException)
{
    display.Done();
    Console.WriteLine("Stopped. Run the same command again to resume where it left off.");
    return 1;
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
