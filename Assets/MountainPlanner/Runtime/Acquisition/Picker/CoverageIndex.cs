#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Acquisition.Picker
{
    /// <summary>
    /// What terrain data exists where, for the picker's data-quality overlay and estimate (0.3 §4.2,
    /// §6): published S1M tiles from the USGS bucket's folder listings (one request per 100 km folder,
    /// only for folders the map shows), and the USGS 3DEP Elevation Index map service for where 1 m and
    /// 1/9 arc-second (about 3 m) DEMs exist. Everywhere else 3DEP has 1/3 arc-second (about 10 m).
    /// </summary>
    public sealed class CoverageIndex
    {
        public const string IndexService = "https://index.nationalmap.gov/arcgis/rest/services/3DEPElevationIndex/MapServer";
        /// <summary>Index layers: 1 m DEM and 1/9 arc-second footprints.</summary>
        public const int OneMetreLayer = 1, ThreeMetreLayer = 2;
        public const string Attribution = "Coverage: USGS 3DEP";

        readonly NominatimClient.Transport _send;
        readonly DiskCache? _cache;
        readonly string _day;
        readonly Dictionary<string, Task<HashSet<string>>> _folders = new Dictionary<string, Task<HashSet<string>>>();
        readonly object _lock = new object();

        /// <param name="cache">Optional disk cache for folder listings.</param>
        /// <param name="day">Part of the disk cache key (for example 2026-10-02), so listings refresh daily.</param>
        public CoverageIndex(NominatimClient.Transport? send = null, DiskCache? cache = null, string day = "")
        {
            _send = send ?? MapTileSource.GetAsync;
            _cache = cache;
            _day = day;
        }

        /// <summary>The 100 km listing folder holding the S1M tile at an Albers point, for example n24w12.</summary>
        public static string FolderAt(double x, double y)
        {
            S1mTiles.TileName(x, y, out string folder, out _, out _);
            return folder;
        }

        /// <summary>Every listing folder whose tiles touch a box, in a stable order.</summary>
        public static IReadOnlyList<string> FoldersFor(AlbersBox box) =>
            S1mTiles.TilesFor(box).Select(t => t.Folder).Distinct().ToList();

        /// <summary>A published tile's box from its name: n2400w1200 has its north-west corner at (−1,200 km, 2,400 km).</summary>
        public static AlbersBox TileBox(string name)
        {
            var m = Regex.Match(name, @"^n(\d{4})([ew])(\d{4})$");
            if (!m.Success) throw new FormatException($"Not an S1M tile name: {name}");
            double north = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 1000.0;
            double west = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * 1000.0 * (m.Groups[2].Value == "w" ? -1 : 1);
            return new AlbersBox(west, north - S1mTiles.TileMetres, west + S1mTiles.TileMetres, north);
        }

        /// <summary>Tile names in one S3 folder listing (its common prefixes).</summary>
        public static IEnumerable<string> ParseListing(string xml)
        {
            foreach (Match m in Regex.Matches(xml, @"<Prefix>[^<]*/(n\d{4}[ew]\d{4})/</Prefix>"))
                yield return m.Groups[1].Value;
        }

        /// <summary>Published S1M tile names in one folder; each folder is listed at most once.</summary>
        public Task<HashSet<string>> TilesInFolderAsync(string folder, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!_folders.TryGetValue(folder, out var task) || task.IsFaulted || task.IsCanceled)
                    _folders[folder] = task = ListAsync(folder, ct);
                return task;
            }
        }

        async Task<HashSet<string>> ListAsync(string folder, CancellationToken ct)
        {
            string url = $"{S1mTiles.Bucket}/?list-type=2&delimiter=/&prefix={S1mTiles.Prefix}{folder}/&max-keys=1000";
            byte[] bytes = _cache != null
                ? await _cache.GetOrAddAsync(url + "#" + _day, () => _send(url, ct), null).ConfigureAwait(false)
                : await _send(url, ct).ConfigureAwait(false);
            return new HashSet<string>(ParseListing(Encoding.UTF8.GetString(bytes)));
        }

        /// <summary>Published S1M tiles touching a box (listing only the folders it needs).</summary>
        public async Task<IReadOnlyList<AlbersBox>> S1mTilesAsync(AlbersBox box, CancellationToken ct)
        {
            var result = new List<AlbersBox>();
            foreach (var (name, folder, _, _) in S1mTiles.TilesFor(box))
                if ((await TilesInFolderAsync(folder, ct).ConfigureAwait(false)).Contains(name))
                    result.Add(TileBox(name));
            return result;
        }

        /// <summary>The share of a box (0–1) inside published S1M tiles.</summary>
        public async Task<double> S1mShareAsync(AlbersBox box, CancellationToken ct)
        {
            double covered = 0;
            foreach (var tile in await S1mTilesAsync(box, ct).ConfigureAwait(false))
            {
                double w = Math.Min(box.East, tile.East) - Math.Max(box.West, tile.West);
                double h = Math.Min(box.North, tile.North) - Math.Max(box.South, tile.South);
                if (w > 0 && h > 0) covered += w * h;
            }
            return Math.Min(1, covered / (box.Width * box.Height));
        }

        /// <summary>One layer's footprints over a Web Mercator box as a transparent PNG (see <see cref="ExportUrl"/>), or null.</summary>
        public async Task<byte[]?> ImageAsync(int layer, double west, double south, double east, double north, int width, int height, CancellationToken ct)
        {
            byte[] png = await _send(ExportUrl(layer, west, south, east, north, width, height), ct).ConfigureAwait(false);
            return png.Length == 0 ? null : png;
        }

        /// <summary>
        /// A transparent PNG of one index layer's footprints over a Web Mercator box: filled where that
        /// DEM exists. The picker recolours it into the overlay and samples it for the estimate.
        /// </summary>
        public static string ExportUrl(int layer, double west, double south, double east, double north, int width, int height) =>
            string.Format(CultureInfo.InvariantCulture,
                "{0}/export?bbox={1:F1},{2:F1},{3:F1},{4:F1}&bboxSR=3857&imageSR=3857&size={5},{6}&format=png32&transparent=true&layers=show:{7}&f=image",
                IndexService, west, south, east, north, width, height, layer);
    }
}
