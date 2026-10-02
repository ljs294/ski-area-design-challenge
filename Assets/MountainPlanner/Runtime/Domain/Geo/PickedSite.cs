using System;
using MountainPlanner.Domain.Terrain;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// What the site picker (S3, task 13) hands to the download flow (task 14): a named, exact
    /// EPSG:6350 square. The app turns it into an acquisition request; the square it downloads is
    /// <see cref="Square"/>, so what the player saw is exactly what downloads (0.3 §6.1).
    /// </summary>
    public sealed class PickedSite
    {
        public readonly string Name;
        /// <summary>The exact core square in CONUS Albers metres; its ring is <c>Square.Ring</c>.</summary>
        public readonly SiteSquare Square;
        /// <summary>Latitude and longitude of the square's centre. <c>SiteSquare.Create(Centre, SizeKm)</c> gives <see cref="Square"/> again.</summary>
        public readonly GeoPoint Centre;
        /// <summary>The optional satellite imagery layer (G7). Ignored until imagery downloads exist.</summary>
        public readonly bool IncludeImagery;
        /// <summary>The picker's estimate when the player chose the site; for display only.</summary>
        public readonly SiteEstimate Estimate;

        PickedSite(string name, SiteSquare square, bool includeImagery, SiteEstimate estimate)
        {
            Name = name;
            Square = square;
            Centre = Albers6350.Inverse(square.Centre);
            IncludeImagery = includeImagery;
            Estimate = estimate;
        }

        public double SizeKm => Square.SizeKm;

        /// <summary>
        /// A site around <paramref name="clicked"/>. Throws if the name is blank or the size is not
        /// 2–5 km in 0.1 km steps (see <see cref="SiteSquare.Create(AlbersPoint, double)"/>).
        /// </summary>
        public static PickedSite Create(string name, AlbersPoint clicked, double sizeKm, bool includeImagery, SiteEstimate estimate)
        {
            string trimmed = name?.Trim() ?? "";
            if (trimmed.Length == 0) throw new ArgumentException("A site needs a name.", nameof(name));
            return new PickedSite(trimmed, SiteSquare.Create(clicked, sizeKm), includeImagery, estimate);
        }
    }

    /// <summary>
    /// The picker's estimate line for a site: the expected terrain score, the share of each terrain
    /// source, and the download size and time. Computed outside Domain; plain data here.
    /// </summary>
    public readonly struct SiteEstimate
    {
        /// <summary>Expected terrain quality score (T18), 0–100.</summary>
        public readonly int TerrainScore;
        /// <summary>Expected share of the core per source, indexed by <see cref="TerrainSource"/>; sums to 1.</summary>
        readonly double[] _shares;
        public readonly long Bytes;
        public readonly double Seconds;
        /// <summary>True when the coverage behind the estimate could not be checked (offline, or still loading).</summary>
        public readonly bool IsRough;

        public SiteEstimate(int terrainScore, double[] shares, long bytes, double seconds, bool isRough)
        {
            if (shares == null || shares.Length != 4) throw new ArgumentException("One share per terrain source.", nameof(shares));
            TerrainScore = terrainScore;
            _shares = (double[])shares.Clone();
            Bytes = bytes;
            Seconds = seconds;
            IsRough = isRough;
        }

        public double Share(TerrainSource source) => _shares == null ? 0 : _shares[(int)source];
    }
}
