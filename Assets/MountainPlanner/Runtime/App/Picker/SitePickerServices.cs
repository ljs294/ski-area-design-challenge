using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Picker;
using MountainPlanner.Domain.Geo;
using MountainPlanner.UI.Picker;

namespace MountainPlanner.App.Picker
{
    /// <summary>
    /// The site picker's network side (task 13): Acquisition's picker clients behind the UI's interface,
    /// since UI may not reference Acquisition (0.3 §2). One instance per picker: it holds the Nominatim
    /// rate gate and the in-memory tile cache. S1M folder listings are cached on disk for a day.
    /// </summary>
    public sealed class SitePickerServices : ISitePickerServices
    {
        readonly NominatimClient _nominatim = new NominatimClient();
        readonly MapTileSource _tiles = new MapTileSource();
        readonly CoverageIndex _coverage;

        /// <param name="cacheFolder">Where S1M folder listings are kept, or null for memory only.</param>
        public SitePickerServices(string cacheFolder = null)
        {
            // The day only names the cache entry, so listings refresh daily; it never reaches Domain.
            string day = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _coverage = new CoverageIndex(null, cacheFolder != null ? new DiskCache(cacheFolder) : null, day);
        }

        public string Attribution => string.Join(" · ", MapTileSource.Attribution, CoverageIndex.Attribution, NominatimClient.Attribution);

        public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct)
        {
            var places = await _nominatim.SearchAsync(query, ct);
            return places.Select(p => new PlaceResult
            {
                Name = p.Name, DisplayName = p.DisplayName, Location = p.Location,
                South = p.South, North = p.North, West = p.West, East = p.East,
            }).ToList();
        }

        public Task<string> SuggestNameAsync(GeoPoint centre, CancellationToken ct) => _nominatim.SuggestNameAsync(centre, ct);

        public Task<byte[]> TileAsync(bool imagery, int zoom, int x, int y, CancellationToken ct) =>
            _tiles.FetchAsync(imagery ? BaseMap.Imagery : BaseMap.Topo, zoom, x, y, ct);

        public Task<IReadOnlyList<AlbersBox>> S1mTilesAsync(AlbersBox box, CancellationToken ct) => _coverage.S1mTilesAsync(box, ct);

        public Task<byte[]> CoverageImageAsync(CoverageLayer layer, double west, double south, double east, double north, int width, int height, CancellationToken ct) =>
            _coverage.ImageAsync(layer == CoverageLayer.OneMetre ? CoverageIndex.OneMetreLayer : CoverageIndex.ThreeMetreLayer,
                west, south, east, north, width, height, ct);

        public Task<SiteEstimate> EstimateAsync(SiteSquare site, CancellationToken ct) => SiteEstimator.EstimateAsync(site, _coverage, ct);
    }
}
