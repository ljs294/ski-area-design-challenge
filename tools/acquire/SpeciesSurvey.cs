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
using MountainPlanner.Domain.Flora;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// The species-model priority survey (task 09, F1). For every operating downhill ski area in the lower 48
/// (OpenSkiMap, from OpenStreetMap), samples BIGMAP species the way the downloader does for a 5 km site
/// (a 20 × 20 grid over its 11 km surround, every species layer in locked batches of 20), then ranks the
/// species without a model by the flora-score points a model would add: 25 × their share of biomass.
///
///   acquire species-survey --areas ski_areas.geojson --out survey.jsonl [--km 5] [--limit N]
///   acquire species-report --survey survey.jsonl [--out report.md]
///
/// Three ski areas at a time (the downloader itself sends four requests at once); every response is
/// cached, so a stopped survey resumes where it left off.
/// </summary>
static class SpeciesSurvey
{
    public sealed class Area
    {
        public string Name = "", State = "";
        public double Lat, Lon;
        public double VerticalMetres;
        public List<Share> Species = new List<Share>();
    }

    public sealed class Share
    {
        public int Spcd;
        public string CommonName = "";
        public double ShareOfBiomass;
    }

    /// <summary>Operating downhill ski areas with runs and lifts, in the contiguous US.</summary>
    public static List<Area> ReadAreas(string geojson)
    {
        var root = JObject.Parse(File.ReadAllText(geojson));
        var areas = new List<Area>();
        foreach (var f in root["features"] as JArray ?? new JArray())
        {
            var p = f["properties"];
            if (p == null) continue;
            var places = p["places"] as JArray ?? new JArray();
            if (!places.Any(x => (string?)x["iso3166_1Alpha2"] == "US")) continue;
            string state = (string?)places.First?["iso3166_2"] ?? "";
            if (state is "US-AK" or "US-HI" or "US-PR" or "US-GU" or "US-VI") continue;
            if ((string?)p["status"] != "operating") continue;
            if (!(p["activities"] as JArray ?? new JArray()).Any(a => (string?)a == "downhill")) continue;
            var runs = p["statistics"]?["runs"]?["byActivity"]?["downhill"]?["byDifficulty"] as JObject;
            var lifts = p["statistics"]?["lifts"]?["byType"] as JObject;
            if (runs == null || !runs.Properties().Any(r => (int?)r.Value["count"] > 0) || lifts == null || !lifts.Properties().Any()) continue;
            var centre = p["viewportHint"]?["center"] as JArray;
            if (centre == null || centre.Count < 2) continue;
            areas.Add(new Area
            {
                Name = (string?)p["name"] ?? "(unnamed)", State = state.Replace("US-", ""),
                Lon = (double)centre[0], Lat = (double)centre[1],
                VerticalMetres = ((double?)p["statistics"]?["maxElevation"] ?? 0) - ((double?)p["statistics"]?["minElevation"] ?? 0),
            });
        }
        return areas.OrderBy(a => a.State, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    public static async Task<int> RunAsync(Dictionary<string, string> opts, string cacheFolder, CancellationToken ct)
    {
        var areas = ReadAreas(opts["areas"]);
        if (opts.TryGetValue("limit", out string? limit)) areas = areas.Take(int.Parse(limit, CultureInfo.InvariantCulture)).ToList();
        double km = double.Parse(opts.GetValueOrDefault("km", "5"), CultureInfo.InvariantCulture);
        string output = opts["out"];
        var done = File.Exists(output)
            ? File.ReadAllLines(output).Where(l => l.Length > 0).Select(l => JsonConvert.DeserializeObject<Area>(l)!).Select(a => a.Name + "|" + a.State).ToHashSet()
            : new HashSet<string>();
        var meter = new TransferMeter();
        var bigmap = new BigmapService(new DiskCache(cacheFolder), meter);
        Console.WriteLine($"{areas.Count} ski areas; {done.Count} already surveyed. Results: {output}");
        var began = DateTime.UtcNow;
        int n = 0;
        var gate = new SemaphoreSlim(3);
        var writeLock = new object();
        await Task.WhenAll(areas.Where(area => !done.Contains(area.Name + "|" + area.State)).Select(async area =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var site = SiteSquare.Create(new GeoPoint(area.Lat, area.Lon), km);
                var box = site.Ring;
                try
                {
                    var layers = await bigmap.LayersAsync(box, ct);
                    const int grid = CoverAssembler.SpeciesSampleGrid;
                    var points = new List<AlbersPoint>();
                    for (int j = 0; j < grid; j++)
                        for (int i = 0; i < grid; i++)
                            points.Add(new AlbersPoint(box.West + (i + 0.5) * box.Width / grid, box.South + (j + 0.5) * box.Height / grid));
                    var totals = new Dictionary<int, double>();
                    foreach (var batch in layers.Select((l, i) => (l, i)).GroupBy(t => t.i / BigmapService.MaxMosaicItems, t => t.l))
                        foreach (var kv in await bigmap.SampleAsync(points, batch.ToList(), ct))
                            totals[kv.Key] = (totals.TryGetValue(kv.Key, out double t) ? t : 0) + kv.Value;
                    double all = totals.Values.Sum();
                    area.Species = layers.Where(l => totals.ContainsKey(l.ObjectId)).OrderByDescending(l => totals[l.ObjectId]).ThenBy(l => l.Spcd)
                        .Select(l => new Share { Spcd = l.Spcd, CommonName = l.CommonName, ShareOfBiomass = Math.Round(totals[l.ObjectId] / Math.Max(all, 1e-9), 4) })
                        .ToList();
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException)
                {
                    Console.WriteLine($"  {area.Name} ({area.State}): {ex.Message} (skipped; run again to retry)");
                    return;
                }
                lock (writeLock)
                {
                    File.AppendAllText(output, JsonConvert.SerializeObject(area) + "\n");
                    n++;
                    string top = string.Join(", ", area.Species.Take(3).Select(s => $"{s.CommonName} {100 * s.ShareOfBiomass:F0}%"));
                    Console.WriteLine($"[{n + done.Count}/{areas.Count}] {area.Name} ({area.State}): {(area.Species.Count == 0 ? "no forest species" : top)} · " +
                                      $"{meter.Requests} requests, {(DateTime.UtcNow - began).TotalMinutes:F0} min");
                }
            }
            finally { gate.Release(); }
        }));
        Console.WriteLine($"Survey complete: {areas.Count} areas in {(DateTime.UtcNow - began).TotalMinutes:F0} min, {meter.Requests} requests");
        return 0;
    }

    /// <summary>
    /// Ranks species without a real model by the flora points a model would add across the surveyed areas
    /// (F1 species fidelity: 25 points × share of biomass), each area counted once.
    /// </summary>
    public static int Report(Dictionary<string, string> opts)
    {
        var areas = File.ReadAllLines(opts["survey"]).Where(l => l.Length > 0).Select(l => JsonConvert.DeserializeObject<Area>(l)!).ToList();
        var forested = areas.Where(a => a.Species.Count > 0).ToList();
        var bySpecies = new Dictionary<int, Tally>();
        double modelledNow = 0;
        foreach (var a in forested)
        {
            double total = a.Species.Sum(s => s.ShareOfBiomass);
            modelledNow += a.Species.Where(s => SpeciesMap.IsModelled(s.Spcd)).Sum(s => s.ShareOfBiomass) / Math.Max(total, 1e-9);
            foreach (var s in a.Species.Where(s => !SpeciesMap.IsModelled(s.Spcd)))
            {
                double share = s.ShareOfBiomass / Math.Max(total, 1e-9);
                if (!bySpecies.TryGetValue(s.Spcd, out var e)) bySpecies[s.Spcd] = e = new Tally { Name = s.CommonName };
                e.Sum += share;
                if (share >= 0.03) e.Over3++;
                e.Present++;
                e.Top.Add((share, a));
            }
        }
        var rows = bySpecies.OrderByDescending(kv => kv.Value.Sum).ToList();
        var md = new System.Text.StringBuilder();
        md.AppendLine($"Surveyed {areas.Count} ski areas; {forested.Count} have BIGMAP forest species. " +
                      $"Species fidelity today averages {100 * modelledNow / Math.Max(1, forested.Count):F0}% ({SpeciesMap.ModelledCodes.Count} modelled species).");
        md.AppendLine();
        md.AppendLine("| Rank | Species (FIA code) | Drawn today as | Mean flora points per area | Areas where ≥3% | Areas present | Biggest shares |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        int rank = 0;
        foreach (var (spcd, v) in rows.Take(int.Parse(opts.GetValueOrDefault("top", "30"), CultureInfo.InvariantCulture)))
        {
            rank++;
            string examples = string.Join("; ", v.Top.OrderByDescending(t => t.Share).Take(3).Select(t => $"{t.Area.Name} ({t.Area.State}) {100 * t.Share:F0}%"));
            md.AppendLine($"| {rank} | {Capital(v.Name)} ({spcd}) | {SpeciesMap.Models[SpeciesMap.ModelFor(spcd)].Replace('_', ' ')} | " +
                          $"{25 * v.Sum / Math.Max(1, forested.Count):F2} | {v.Over3} | {v.Present} | {examples} |");
        }
        string text = md.ToString();
        if (opts.TryGetValue("out", out string? path)) File.WriteAllText(path, text);
        Console.Write(text);
        return 0;
    }

    sealed class Tally
    {
        public string Name = "";
        public double Sum;
        public int Over3, Present;
        public readonly List<(double Share, Area Area)> Top = new List<(double, Area)>();
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}
