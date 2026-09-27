#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Tiff;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Acquisition.Providers
{
    /// <summary>
    /// USGS 3DEP Standard 1 m DEM (S1M) tiles on the public S3 bucket (0.3 §6): 10 km Cloud Optimized
    /// GeoTIFFs in EPSG:6350, with a 2 m overview used for the ring.
    /// </summary>
    public sealed class S1mTiles
    {
        public const string Bucket = "https://prd-tnm.s3.amazonaws.com";
        public const string Prefix = "StagedProducts/Elevation/S1M/";
        public const double TileMetres = 10000;

        readonly DiskCache _cache;
        readonly TransferMeter _meter;
        readonly Dictionary<string, TiffImage?> _open = new Dictionary<string, TiffImage?>();

        public S1mTiles(DiskCache cache, TransferMeter meter)
        {
            _cache = cache;
            _meter = meter;
        }

        /// <summary>A tile's name and folder for an Albers point, e.g. n0470e1490 in folder n04e14.</summary>
        public static string TileName(double x, double y, out string folder, out double tileWest, out double tileNorth)
        {
            tileWest = Math.Floor(x / TileMetres) * TileMetres;
            tileNorth = Math.Floor(y / TileMetres) * TileMetres + TileMetres;
            string ew = tileWest >= 0 ? "e" : "w";
            long westKm = (long)Math.Abs(tileWest) / 1000, northKm = (long)tileNorth / 1000;
            folder = string.Format(CultureInfo.InvariantCulture, "n{0:00}{1}{2:00}", northKm / 100, ew, westKm / 100);
            return string.Format(CultureInfo.InvariantCulture, "n{0:0000}{1}{2:0000}", northKm, ew, westKm);
        }

        /// <summary>Every tile touching a box, west to east then north to south (a stable order).</summary>
        public static List<(string Name, string Folder, double West, double North)> TilesFor(AlbersBox box)
        {
            var list = new List<(string, string, double, double)>();
            double north0 = Math.Floor((box.North - 1e-6) / TileMetres) * TileMetres + TileMetres;
            for (double tn = north0; tn > box.South; tn -= TileMetres)
                for (double tw = Math.Floor(box.West / TileMetres) * TileMetres; tw < box.East; tw += TileMetres)
                {
                    string name = TileName(tw + 1, tn - 1, out string folder, out double w, out double n);
                    list.Add((name, folder, w, n));
                }
            return list;
        }

        /// <summary>The tile's image, or null when S1M hasn't published it yet. Directories are cached.</summary>
        public async Task<TiffImage?> OpenAsync(string name, string folder, CancellationToken ct)
        {
            if (_open.TryGetValue(name, out var hit)) return hit;
            string listUrl = $"{Bucket}/?list-type=2&prefix={Prefix}{folder}/{name}/&max-keys=20";
            byte[] listing = await _cache.GetOrAddAsync(listUrl, () =>
                Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, listUrl), _meter, ct), _meter).ConfigureAwait(false);
            var match = Regex.Match(Encoding.UTF8.GetString(listing), "<Key>([^<]+\\.tif)</Key>");
            TiffImage? image = null;
            if (match.Success)
            {
                var source = _cache.Wrap(new HttpRangeSource($"{Bucket}/{match.Groups[1].Value}", _meter), _meter);
                image = await TiffImage.OpenAsync(source, ct).ConfigureAwait(false);
                image.Meter = _meter;
            }
            _open[name] = image;
            return image;
        }
    }

    /// <summary>
    /// The USGS 3DEP dynamic elevation service (0.3 §4.2): the best published 3DEP DEM anywhere in the
    /// contiguous US, exported on the EPSG:6350 grid. Requests are kept to 1,000 × 1,000 pixels; a
    /// whole 5 km site in one request times out (data-spike report §3).
    /// </summary>
    public sealed class Dep3Service
    {
        public const string Service = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer";
        public const int MaxPixels = 1000;

        readonly DiskCache _cache;
        readonly TransferMeter _meter;

        public Dep3Service(DiskCache cache, TransferMeter meter)
        {
            _cache = cache;
            _meter = meter;
        }

        /// <summary>Heights for a box as a north-up float grid (NaN where the service has no data).</summary>
        public async Task<float[]> ExportAsync(AlbersBox box, int width, int height, CancellationToken ct)
        {
            if (width > MaxPixels || height > MaxPixels) throw new ArgumentException("Keep 3DEP requests to 1,000 × 1,000 pixels.");
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", box.West, box.South, box.East, box.North);
            string url = $"{Service}/exportImage?bbox={bbox}&bboxSR=6350&imageSR=6350&size={width},{height}" +
                         "&format=tiff&pixelType=F32&noData=-999999&interpolation=RSP_BilinearInterpolation&f=image";
            byte[] tif = await _cache.GetOrAddAsync(url, () =>
                Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), _meter, ct), _meter).ConfigureAwait(false);
            var image = await TiffImage.OpenAsync(new MemoryByteSource(tif, "3dep-export"), ct).ConfigureAwait(false);
            var dir = image.Directories[0];
            if (dir.Width != width || dir.Height != height)
                throw new System.IO.InvalidDataException($"3DEP returned {dir.Width}×{dir.Height}, expected {width}×{height}.");
            return await image.ReadWindowAsync(0, 0, 0, width, height, ct).ConfigureAwait(false);
        }

        /// <summary>The finest 3DEP source with data at a point: its name and nominal cell size in metres.</summary>
        public async Task<(string Source, double CellSize)> IdentifySourceAsync(AlbersPoint p, CancellationToken ct)
        {
            string geometry = Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture,
                "{{\"x\":{0},\"y\":{1},\"spatialReference\":{{\"wkid\":6350}}}}", p.X, p.Y));
            string url = $"{Service}/identify?geometry={geometry}&geometryType=esriGeometryPoint&returnCatalogItems=true&returnGeometry=false&maxItemCount=50&f=json";
            byte[] body = await _cache.GetOrAddAsync(url, () =>
                Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), _meter, ct), _meter).ConfigureAwait(false);
            var root = JObject.Parse(Encoding.UTF8.GetString(body));
            var values = root["properties"]?["Values"] as JArray ?? new JArray();
            var items = root["catalogItems"]?["features"] as JArray ?? new JArray();
            string best = "none";
            double bestSize = double.MaxValue;
            for (int i = 0; i < Math.Min(values.Count, items.Count); i++)
            {
                var a = items[i]["attributes"];
                string name = (string?)a?["Name"] ?? "";
                if ((string?)values[i] == "NoData" || name.StartsWith("Ov_", StringComparison.Ordinal) || name.StartsWith("metadata", StringComparison.Ordinal)) continue;
                double lowPs = (double?)a?["LowPS"] ?? double.MaxValue;
                if (lowPs < bestSize)
                {
                    bestSize = lowPs;
                    best = name;
                }
            }
            return (best, bestSize);
        }
    }
}
