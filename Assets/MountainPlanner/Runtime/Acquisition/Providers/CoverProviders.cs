#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Tiff;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Acquisition.Providers
{
    /// <summary>
    /// Meta/WRI High Resolution Canopy Height (CC BY 4.0; 0.3 §4.4): level-9 quadkey GeoTIFFs in
    /// EPSG:3857 at about 1.2 m, one row per strip and no overviews, so windows download whole rows.
    /// </summary>
    public sealed class CanopyTiles
    {
        public const string Bucket = "https://dataforgood-fb-data.s3.amazonaws.com/forests/v1/alsgedi_global_v6_float/chm/";
        public const int Zoom = 9;

        readonly DiskCache _cache;
        readonly TransferMeter _meter;
        readonly Dictionary<string, TiffImage> _open = new Dictionary<string, TiffImage>();

        public CanopyTiles(DiskCache cache, TransferMeter meter)
        {
            _cache = cache;
            _meter = meter;
        }

        /// <summary>The quadkeys of the tiles a set of points falls in, in a stable order.</summary>
        public static List<string> QuadKeysFor(IEnumerable<GeoPoint> points) =>
            points.Select(p => WebMercator.Tile(p, Zoom)).Distinct().Select(t => WebMercator.QuadKey(t.X, t.Y, Zoom))
                  .OrderBy(k => k, StringComparer.Ordinal).ToList();

        public async Task<TiffImage> OpenAsync(string quadKey, CancellationToken ct)
        {
            if (_open.TryGetValue(quadKey, out var hit)) return hit;
            var source = _cache.Wrap(new HttpRangeSource(Bucket + quadKey + ".tif", _meter), _meter);
            var image = await TiffImage.OpenAsync(source, ct).ConfigureAwait(false);
            image.Meter = _meter;
            _open[quadKey] = image;
            return image;
        }
    }

    /// <summary>ESA WorldCover 2021 v200 (CC BY 4.0): 3° × 3° GeoTIFFs in EPSG:4326 at 10 m.</summary>
    public sealed class WorldCoverTiles
    {
        public const string Bucket = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/";

        readonly DiskCache _cache;
        readonly TransferMeter _meter;
        readonly Dictionary<string, TiffImage> _open = new Dictionary<string, TiffImage>();

        public WorldCoverTiles(DiskCache cache, TransferMeter meter)
        {
            _cache = cache;
            _meter = meter;
        }

        public static string TileName(GeoPoint p)
        {
            int lat = (int)Math.Floor(p.Latitude / 3.0) * 3, lon = (int)Math.Floor(p.Longitude / 3.0) * 3;
            return string.Format(CultureInfo.InvariantCulture, "ESA_WorldCover_10m_2021_v200_{0}{1:00}{2}{3:000}_Map",
                lat >= 0 ? "N" : "S", Math.Abs(lat), lon >= 0 ? "E" : "W", Math.Abs(lon));
        }

        public async Task<TiffImage> OpenAsync(string tileName, CancellationToken ct)
        {
            if (_open.TryGetValue(tileName, out var hit)) return hit;
            var source = _cache.Wrap(new HttpRangeSource(Bucket + tileName + ".tif", _meter), _meter);
            var image = await TiffImage.OpenAsync(source, ct).ConfigureAwait(false);
            image.Meter = _meter;
            _open[tileName] = image;
            return image;
        }
    }

    /// <summary>
    /// USFS FIA BIGMAP 2018 tree-species aboveground biomass (30 m, 327 species; TR3). The service
    /// mosaics at most 20 of its layers per request, so layers are always addressed in locked batches
    /// (data-spike report §8).
    /// </summary>
    public sealed class BigmapService
    {
        public const string Service = "https://imagery.geoplatform.gov/iipp/rest/services/Vegetation/USFS_FIA_BIGMAP_AboveGroundBiomass/ImageServer";
        public const int MaxMosaicItems = 20;

        readonly DiskCache _cache;
        readonly TransferMeter _meter;

        public BigmapService(DiskCache cache, TransferMeter meter)
        {
            _cache = cache;
            _meter = meter;
        }

        public sealed class Layer
        {
            public int ObjectId;
            public int Spcd;
            public string CommonName = "";
        }

        /// <summary>The species layers whose footprints cover the box (not the "Total" layer).</summary>
        public async Task<List<Layer>> LayersAsync(AlbersBox box, CancellationToken ct)
        {
            string env = Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture,
                "{{\"xmin\":{0},\"ymin\":{1},\"xmax\":{2},\"ymax\":{3},\"spatialReference\":{{\"wkid\":102039}}}}", box.West, box.South, box.East, box.North));
            string url = $"{Service}/query?geometry={env}&geometryType=esriGeometryEnvelope&inSR=102039&spatialRel=esriSpatialRelIntersects" +
                         "&where=spcd%3E0&outFields=objectid,spcd,common_name&returnGeometry=false&f=json";
            var root = JObject.Parse(Encoding.UTF8.GetString(await Get(url, ct).ConfigureAwait(false)));
            var layers = new List<Layer>();
            foreach (var f in root["features"] as JArray ?? new JArray())
            {
                var a = f["attributes"];
                layers.Add(new Layer { ObjectId = (int?)a?["objectid"] ?? 0, Spcd = (int?)a?["spcd"] ?? 0, CommonName = (string?)a?["common_name"] ?? "" });
            }
            return layers.OrderBy(l => l.ObjectId).ToList();
        }

        /// <summary>
        /// Total biomass per layer (object id) over a grid of points, for up to 20 locked layers. A batch
        /// the service rejects is split until the bad layer is isolated and skipped.
        /// </summary>
        public async Task<Dictionary<int, double>> SampleAsync(IReadOnlyList<AlbersPoint> points, IReadOnlyList<Layer> batch, CancellationToken ct)
        {
            var totals = new Dictionary<int, double>();
            var sb = new StringBuilder("{\"points\":[");
            for (int i = 0; i < points.Count; i++)
                sb.Append(i > 0 ? "," : "").AppendFormat(CultureInfo.InvariantCulture, "[{0},{1}]", Math.Round(points[i].X), Math.Round(points[i].Y));
            sb.Append("],\"spatialReference\":{\"wkid\":102039}}");
            await SampleBatch(Uri.EscapeDataString(sb.ToString()), batch.ToList(), totals, ct).ConfigureAwait(false);
            return totals;
        }

        async Task SampleBatch(string geometry, List<Layer> batch, Dictionary<int, double> totals, CancellationToken ct)
        {
            if (batch.Count == 0) return;
            string rule = Uri.EscapeDataString("{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[" +
                string.Join(",", batch.Select(l => l.ObjectId.ToString(CultureInfo.InvariantCulture))) + "]}");
            string body = "geometry=" + geometry + "&geometryType=esriGeometryMultipoint&returnFirstValueOnly=false" +
                          "&outFields=objectid&mosaicRule=" + rule + "&f=json";
            byte[] response = await _cache.GetOrAddAsync(Service + "/getSamples?" + body, () =>
                Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Post, Service + "/getSamples")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
                }, _meter, ct), _meter).ConfigureAwait(false);
            var root = JObject.Parse(Encoding.UTF8.GetString(response));
            if (!(root["samples"] is JArray samples))
            {
                if (batch.Count == 1) return; // this layer refuses sampling; skip it
                int half = batch.Count / 2;
                await SampleBatch(geometry, batch.Take(half).ToList(), totals, ct).ConfigureAwait(false);
                await SampleBatch(geometry, batch.Skip(half).ToList(), totals, ct).ConfigureAwait(false);
                return;
            }
            foreach (var smp in samples)
            {
                if (!double.TryParse((string?)smp["value"], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0) continue;
                int id = (int?)smp["attributes"]?["objectid"] ?? (int?)smp["rasterId"] ?? 0;
                if (id == 0) continue;
                totals[id] = (totals.TryGetValue(id, out double t) ? t : 0) + v;
            }
        }

        /// <summary>One species layer over a grid (north-up, 30 m) in ESRI:102039, as biomass per cell.</summary>
        public async Task<float[]> ExportAsync(Layer layer, AlbersBox box, int width, int height, CancellationToken ct)
        {
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", box.West, box.South, box.East, box.North);
            string rule = Uri.EscapeDataString("{\"mosaicMethod\":\"esriMosaicLockRaster\",\"lockRasterIds\":[" + layer.ObjectId.ToString(CultureInfo.InvariantCulture) + "]}");
            string url = $"{Service}/exportImage?bbox={bbox}&bboxSR=102039&imageSR=102039&size={width},{height}&format=tiff&pixelType=F32" +
                         $"&noData=-1&interpolation=RSP_NearestNeighbor&mosaicRule={rule}&f=image";
            byte[] tif = await Get(url, ct).ConfigureAwait(false);
            var image = await TiffImage.OpenAsync(new MemoryByteSource(tif, "bigmap-export"), ct).ConfigureAwait(false);
            var dir = image.Directories[0];
            if (dir.Width != width || dir.Height != height)
                throw new System.IO.InvalidDataException($"BIGMAP returned {dir.Width}×{dir.Height}, expected {width}×{height}.");
            return await image.ReadWindowAsync(0, 0, 0, width, height, ct).ConfigureAwait(false);
        }

        Task<byte[]> Get(string url, CancellationToken ct) =>
            _cache.GetOrAddAsync(url, () => Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), _meter, ct), _meter);
    }
}
