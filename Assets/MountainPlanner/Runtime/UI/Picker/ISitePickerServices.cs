using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.UI.Picker
{
    /// <summary>A search result for the picker's list.</summary>
    public sealed class PlaceResult
    {
        public string Name;
        public string DisplayName;
        public GeoPoint Location;
        public double South, North, West, East;
    }

    /// <summary>Which DEM footprints a coverage image shows.</summary>
    public enum CoverageLayer { OneMetre, ThreeMetre }

    /// <summary>
    /// The picker's network side. UI may not reference Acquisition (0.3 §2), so the app passes an
    /// adapter over Acquisition's picker clients. Every call that fails for lack of a network throws
    /// <see cref="System.IO.IOException"/>; the picker then shows its offline panel.
    /// </summary>
    public interface ISitePickerServices
    {
        /// <summary>Attribution for every source the map shows, one line.</summary>
        string Attribution { get; }

        /// <summary>Up to five places. Called only when the player presses Enter; at most one request a second.</summary>
        Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct);

        /// <summary>A name for a site centred here, or null. Superseded calls return null without a request.</summary>
        Task<string> SuggestNameAsync(GeoPoint centre, CancellationToken ct);

        /// <summary>A basemap tile's JPEG or PNG bytes, or null where there's no tile.</summary>
        Task<byte[]> TileAsync(bool imagery, int zoom, int x, int y, CancellationToken ct);

        /// <summary>Published S1M tiles touching a box.</summary>
        Task<IReadOnlyList<AlbersBox>> S1mTilesAsync(AlbersBox box, CancellationToken ct);

        /// <summary>A transparent PNG, filled where a DEM exists, over a Web Mercator box (metres).</summary>
        Task<byte[]> CoverageImageAsync(CoverageLayer layer, double west, double south, double east, double north, int width, int height, CancellationToken ct);

        /// <summary>
        /// The estimate for a site, found the way the downloader will score it (one request to the 3DEP
        /// service, plus the cached S1M listing). Never throws for a network problem: it returns a rough
        /// estimate that says so (<see cref="SiteEstimate.IsRough"/>).
        /// </summary>
        Task<SiteEstimate> EstimateAsync(SiteSquare site, CancellationToken ct);
    }
}
