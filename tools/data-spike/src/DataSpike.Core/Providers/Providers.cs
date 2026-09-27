using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.DataSpike.IO;

namespace MountainPlanner.DataSpike.Providers
{
    /// <summary>A box on the EPSG:6350 grid (metres), west/south/east/north.</summary>
    public readonly struct AlbersBox
    {
        public readonly double West, South, East, North;
        public AlbersBox(double west, double south, double east, double north) { West = west; South = south; East = east; North = north; }
        public static AlbersBox Around(double x, double y, double sizeMetres)
        {
            // Snap the centre to whole metres so the box sits exactly on the S1M 1 m grid.
            double cx = Math.Round(x), cy = Math.Round(y), h = sizeMetres / 2;
            return new AlbersBox(cx - h, cy - h, cx + h, cy + h);
        }
        public AlbersBox Expand(double metres) => new AlbersBox(West - metres, South - metres, East + metres, North + metres);
        public double Width => East - West;
        public double Height => North - South;
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", West, South, East, North);
    }

    /// <summary>USGS 3DEP Seamless 1-meter DEM (S1M) tiles on the public S3 bucket.</summary>
    public static class S1m
    {
        public const string Bucket = "https://prd-tnm.s3.amazonaws.com";
        public const string Prefix = "StagedProducts/Elevation/S1M/";

        /// <summary>Tile name for an Albers point: north edge and west edge in km, e.g. n0470e1490.</summary>
        public static string TileName(double x, double y, out string folder, out double tileWest, out double tileNorth)
        {
            tileWest = Math.Floor(x / 10000.0) * 10000.0;
            tileNorth = Math.Ceiling(y / 10000.0) * 10000.0;
            if (tileNorth == y) tileNorth += 10000; // a point exactly on an edge belongs to the tile below it
            string ew = tileWest >= 0 ? "e" : "w";
            long westKm = (long)Math.Abs(tileWest) / 1000, northKm = (long)tileNorth / 1000;
            folder = string.Format(CultureInfo.InvariantCulture, "n{0:00}{1}{2:00}", northKm / 100, ew, westKm / 100);
            return string.Format(CultureInfo.InvariantCulture, "n{0:0000}{1}{2:0000}", northKm, ew, westKm);
        }

        /// <summary>URL of the published GeoTIFF for a tile, or null if S1M has no tile there yet.</summary>
        public static async Task<string?> FindTileUrlAsync(string tileName, string folder, TransferStats? stats, CancellationToken ct)
        {
            string url = $"{Bucket}/?list-type=2&prefix={Prefix}{folder}/{tileName}/&max-keys=20";
            byte[] body = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct).ConfigureAwait(false);
            var match = Regex.Match(System.Text.Encoding.UTF8.GetString(body), "<Key>([^<]+\\.tif)</Key>");
            return match.Success ? $"{Bucket}/{match.Groups[1].Value}" : null;
        }

        /// <summary>
        /// List published S1M tile names using only folder listings (S3 CommonPrefixes): one request
        /// for the 100 km folders, then one per folder. Far smaller than listing every file.
        /// </summary>
        public static async Task<List<string>> ListTileFoldersAsync(TransferStats? stats, CancellationToken ct)
        {
            async Task<List<string>> Prefixes(string prefix)
            {
                var found = new List<string>();
                string? token = null;
                do
                {
                    string url = $"{Bucket}/?list-type=2&delimiter=/&prefix={prefix}&max-keys=1000" +
                                 (token != null ? "&continuation-token=" + Uri.EscapeDataString(token) : "");
                    string xml = System.Text.Encoding.UTF8.GetString(await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct).ConfigureAwait(false));
                    foreach (Match m in Regex.Matches(xml, "<Prefix>([^<]+)</Prefix>"))
                        if (m.Groups[1].Value != prefix) found.Add(m.Groups[1].Value);
                    var next = Regex.Match(xml, "<NextContinuationToken>([^<]+)</NextContinuationToken>");
                    token = next.Success ? next.Groups[1].Value : null;
                } while (token != null);
                return found;
            }
            var folders = (await Prefixes(Prefix).ConfigureAwait(false)).Where(f => Regex.IsMatch(f, @"/n\d{2}[ew]\d{2}/$")).ToList();
            var tiles = new List<string>();
            using (var gate = new SemaphoreSlim(8))
            {
                var tasks = folders.Select(async f =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { return await Prefixes(f).ConfigureAwait(false); } finally { gate.Release(); }
                }).ToList();
                foreach (var list in await Task.WhenAll(tasks).ConfigureAwait(false))
                    tiles.AddRange(list.Select(s => s.TrimEnd('/').Substring(s.TrimEnd('/').LastIndexOf('/') + 1)));
            }
            return tiles;
        }

        /// <summary>
        /// List every published S1M tile by walking the bucket with S3 ListObjectsV2 (1,000 keys per
        /// page). Measures whether a live coverage lookup is practical without the GeoPackage index.
        /// </summary>
        public static async Task<List<string>> ListAllTilesAsync(TransferStats? stats, CancellationToken ct)
        {
            var tiles = new List<string>();
            string? token = null;
            do
            {
                string url = $"{Bucket}/?list-type=2&prefix={Prefix}&max-keys=1000" +
                             (token != null ? "&continuation-token=" + Uri.EscapeDataString(token) : "");
                string xml = System.Text.Encoding.UTF8.GetString(await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct).ConfigureAwait(false));
                foreach (Match m in Regex.Matches(xml, "<Key>[^<]*/(S1M_[nsew0-9]+_\\d+)\\.tif</Key>")) tiles.Add(m.Groups[1].Value);
                var next = Regex.Match(xml, "<NextContinuationToken>([^<]+)</NextContinuationToken>");
                token = next.Success ? next.Groups[1].Value : null;
            } while (token != null);
            return tiles;
        }
    }

    /// <summary>The USGS 3DEP dynamic elevation service: best available 3DEP DEM for any area.</summary>
    public static class Dep3
    {
        public const string Service = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer";

        /// <summary>Export a float32 GeoTIFF on the EPSG:6350 grid.</summary>
        public static Task<byte[]> ExportAsync(AlbersBox box, int width, int height, TransferStats? stats, CancellationToken ct)
        {
            string url = $"{Service}/exportImage?bbox={box}&bboxSR=6350&imageSR=6350&size={width},{height}" +
                         "&format=tiff&pixelType=F32&noData=-999999&interpolation=RSP_BilinearInterpolation&compression=LZ77&f=image";
            return Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct);
        }

        /// <summary>Finest 3DEP source with data at a point: (source name, nominal cell size in metres).</summary>
        public static async Task<(string Source, double CellSize)> IdentifySourceAsync(double x, double y, TransferStats? stats, CancellationToken ct)
        {
            string geometry = Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture,
                "{{\"x\":{0},\"y\":{1},\"spatialReference\":{{\"wkid\":6350}}}}", x, y));
            string url = $"{Service}/identify?geometry={geometry}&geometryType=esriGeometryPoint&returnCatalogItems=true&returnGeometry=false&maxItemCount=50&f=json";
            byte[] body = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var values = root.GetProperty("properties").GetProperty("Values").EnumerateArray().Select(v => v.GetString()).ToList();
            var items = root.GetProperty("catalogItems").GetProperty("features").EnumerateArray().ToList();
            string best = "none"; double bestSize = double.MaxValue;
            for (int i = 0; i < Math.Min(values.Count, items.Count); i++)
            {
                var a = items[i].GetProperty("attributes");
                string name = a.GetProperty("Name").GetString() ?? "";
                if (values[i] == "NoData" || name.StartsWith("Ov_") || name.StartsWith("metadata")) continue;
                double lowPs = a.GetProperty("LowPS").GetDouble();
                if (lowPs < bestSize) { bestSize = lowPs; best = name; }
            }
            return (best, bestSize);
        }
    }

    /// <summary>Meta/WRI High Resolution Canopy Height: level-9 quadkey GeoTIFFs in EPSG:3857.</summary>
    public static class Canopy
    {
        public const string Bucket = "https://dataforgood-fb-data.s3.amazonaws.com/forests/v1/alsgedi_global_v6_float/chm/";
        public static string TileUrl(string quadKey) => Bucket + quadKey + ".tif";
    }

    /// <summary>ESA WorldCover 2021 v200: 3°×3° GeoTIFFs in EPSG:4326.</summary>
    public static class WorldCover
    {
        public const string Bucket = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/";
        public static string TileUrl(double latDeg, double lonDeg)
        {
            int lat = (int)Math.Floor(latDeg / 3.0) * 3, lon = (int)Math.Floor(lonDeg / 3.0) * 3;
            string ns = lat >= 0 ? "N" : "S", ew = lon >= 0 ? "E" : "W";
            return string.Format(CultureInfo.InvariantCulture, "{0}ESA_WorldCover_10m_2021_v200_{1}{2:00}{3}{4:000}_Map.tif",
                Bucket, ns, Math.Abs(lat), ew, Math.Abs(lon));
        }
    }

    /// <summary>USFS FIA BIGMAP 2018 tree-species biomass (30 m, 327 species) image service, ESRI:102039.</summary>
    public static class Bigmap
    {
        public const string Service = "https://imagery.geoplatform.gov/iipp/rest/services/Vegetation/USFS_FIA_BIGMAP_AboveGroundBiomass/ImageServer";

        public sealed class SpeciesValue { public int Spcd; public string CommonName = ""; public double TonsPerAcre; }

        /// <summary>All species with biomass at one point, largest first.</summary>
        public static async Task<List<SpeciesValue>> SpeciesAtAsync(double x, double y, TransferStats? stats, CancellationToken ct)
        {
            string geometry = Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture,
                "{{\"x\":{0},\"y\":{1},\"spatialReference\":{{\"wkid\":102039}}}}", x, y));
            string url = $"{Service}/identify?geometry={geometry}&geometryType=esriGeometryPoint&returnCatalogItems=true&returnGeometry=false&maxItemCount=400&f=json";
            byte[] body = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var values = root.GetProperty("properties").GetProperty("Values").EnumerateArray().Select(v => v.GetString()).ToList();
            var items = root.GetProperty("catalogItems").GetProperty("features").EnumerateArray().ToList();
            var result = new List<SpeciesValue>();
            for (int i = 0; i < Math.Min(values.Count, items.Count); i++)
            {
                if (!double.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0) continue;
                var a = items[i].GetProperty("attributes");
                int spcd = a.GetProperty("spcd").GetInt32();
                if (spcd == 0) continue; // the "Total" layer
                result.Add(new SpeciesValue { Spcd = spcd, CommonName = a.GetProperty("common_name").GetString() ?? "", TonsPerAcre = v });
            }
            return result.OrderByDescending(s => s.TonsPerAcre).ToList();
        }

        /// <summary>The service mosaics at most this many catalog items per request (maxMosaicImageCount).</summary>
        public const int MaxMosaicItems = 20;

        /// <summary>
        /// Species mix over a box: total biomass per species over an n×n grid of points, largest first.
        /// A single point is fragile (it may fall on a ski run); a grid describes the site.
        ///
        /// The service only mosaics 20 of its 328 species layers per request, and 100+ can cover a
        /// site, so a plain getSamples silently drops most species. Instead: list the layers covering
        /// the box, then sample them 20 at a time, each batch locked by object id.
        /// </summary>
        public static async Task<List<SpeciesValue>> SpeciesMixAsync(AlbersBox box, int n, TransferStats? stats, CancellationToken ct)
        {
            var layers = await CatalogItemsAsync(box, stats, ct).ConfigureAwait(false);
            var pts = new StringBuilder("{\"points\":[");
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    if (i + j > 0) pts.Append(',');
                    pts.AppendFormat(CultureInfo.InvariantCulture, "[{0},{1}]",
                        Math.Round(box.West + (i + 0.5) * box.Width / n), Math.Round(box.South + (j + 0.5) * box.Height / n));
                }
            pts.Append("],\"spatialReference\":{\"wkid\":102039}}");
            string geometry = Uri.EscapeDataString(pts.ToString());

            var totals = new Dictionary<int, SpeciesValue>();
            using var gate = new SemaphoreSlim(4);
            var batches = layers.Select((l, i) => (l, i)).GroupBy(t => t.i / MaxMosaicItems, t => t.l.ObjectId).Select(g => g.ToList()).ToList();
            await Task.WhenAll(batches.Select(async batch =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try { await SampleLockedAsync(batch, geometry, totals, stats, ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
            return totals.Values.OrderByDescending(sv => sv.TonsPerAcre).ThenBy(sv => sv.Spcd).ToList();
        }

        /// <summary>Layers the service refused to sample (a few items reject getSamples); reported, not fatal.</summary>
        public static readonly List<int> SkippedLayers = new List<int>();

        // Samples one batch of locked layers. The service rejects a whole batch if one layer is bad,
        // so a failing batch is split in half until the bad layer is isolated and skipped.
        static async Task SampleLockedAsync(List<int> ids, string geometry, Dictionary<int, SpeciesValue> totals, TransferStats? stats, CancellationToken ct)
        {
            string rule = Uri.EscapeDataString("{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[" +
                string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]}");
            string body = "geometry=" + geometry + "&geometryType=esriGeometryMultipoint&returnFirstValueOnly=false" +
                          "&outFields=spcd,common_name&mosaicRule=" + rule + "&f=json";
            byte[] response = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Post, Service + "/getSamples")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
            }, stats, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty("samples", out var samples))
            {
                if (ids.Count == 1) { lock (SkippedLayers) SkippedLayers.Add(ids[0]); return; }
                int half = ids.Count / 2;
                await SampleLockedAsync(ids.Take(half).ToList(), geometry, totals, stats, ct).ConfigureAwait(false);
                await SampleLockedAsync(ids.Skip(half).ToList(), geometry, totals, stats, ct).ConfigureAwait(false);
                return;
            }
            lock (totals)
                foreach (var smp in samples.EnumerateArray())
                {
                    if (!double.TryParse(smp.GetProperty("value").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0) continue;
                    var a = smp.GetProperty("attributes");
                    int spcd = a.GetProperty("spcd").GetInt32();
                    if (spcd == 0) continue; // the "Total" layer
                    if (!totals.TryGetValue(spcd, out var sv))
                        totals[spcd] = sv = new SpeciesValue { Spcd = spcd, CommonName = a.GetProperty("common_name").GetString() ?? "" };
                    sv.TonsPerAcre += v;
                }
        }

        public sealed class CatalogItem { public int ObjectId; public int Spcd; public string CommonName = ""; }

        /// <summary>The species layers whose footprints cover the box (excluding the "Total" layer).</summary>
        public static async Task<List<CatalogItem>> CatalogItemsAsync(AlbersBox box, TransferStats? stats, CancellationToken ct)
        {
            string env = Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture,
                "{{\"xmin\":{0},\"ymin\":{1},\"xmax\":{2},\"ymax\":{3},\"spatialReference\":{{\"wkid\":102039}}}}", box.West, box.South, box.East, box.North));
            string url = $"{Service}/query?geometry={env}&geometryType=esriGeometryEnvelope&inSR=102039&spatialRel=esriSpatialRelIntersects" +
                         "&where=spcd%3E0&outFields=objectid,spcd,common_name&returnGeometry=false&f=json";
            byte[] body = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("attributes")).Select(a => new CatalogItem
            {
                ObjectId = a.GetProperty("objectid").GetInt32(),
                Spcd = a.GetProperty("spcd").GetInt32(),
                CommonName = a.GetProperty("common_name").GetString() ?? "",
            }).OrderBy(c => c.ObjectId).ToList();
        }

        /// <summary>Export one species' biomass over a box at 30 m as a float32 GeoTIFF.</summary>
        public static Task<byte[]> ExportSpeciesAsync(int spcd, AlbersBox box, TransferStats? stats, CancellationToken ct)
        {
            int w = (int)Math.Ceiling(box.Width / 30), h = (int)Math.Ceiling(box.Height / 30);
            string rule = Uri.EscapeDataString("{\"mosaicMethod\":\"esriMosaicAttribute\",\"where\":\"spcd=" + spcd.ToString(CultureInfo.InvariantCulture) + "\"}");
            string url = $"{Service}/exportImage?bbox={box}&bboxSR=102039&imageSR=102039&size={w},{h}&format=tiff&pixelType=F32&mosaicRule={rule}&f=image";
            return Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), stats, ct);
        }
    }

    /// <summary>LANDFIRE services (fallback for tree species): checks that the image services respond.</summary>
    public static class Landfire
    {
        public const string Folder = "https://lfps.usgs.gov/arcgis/rest/services/Landfire_LF2024";

        public static async Task<List<string>> ListServicesAsync(TransferStats? stats, CancellationToken ct)
        {
            byte[] body = await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, Folder + "?f=json"), stats, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("services").EnumerateArray()
                .Select(s => s.GetProperty("name").GetString() + " (" + s.GetProperty("type").GetString() + ")").ToList();
        }
    }
}
