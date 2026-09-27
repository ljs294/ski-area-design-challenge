using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MountainPlanner.Domain.Terrain
{
    /// <summary>Where a terrain cell's height came from, finest first (0.3 §4.2).</summary>
    public enum TerrainSource
    {
        /// <summary>USGS 3DEP Standard 1 m DEM (S1M).</summary>
        S1m = 0,
        /// <summary>1 m project lidar from the 3DEP dynamic service, where S1M isn't published yet.</summary>
        Lidar1m = 1,
        /// <summary>3DEP 1/9 arc-second, about 3 m.</summary>
        ThreeMetre = 2,
        /// <summary>3DEP 1/3 arc-second, about 10 m (covers the whole contiguous US).</summary>
        TenMetre = 3,
    }

    /// <summary>
    /// The terrain quality score and one-line summary shown after a download (T18): the core site's
    /// area-weighted source quality, 0–100. The ring is scenery and isn't scored.
    /// </summary>
    public static class TerrainQuality
    {
        public static int Weight(TerrainSource source)
        {
            switch (source)
            {
                case TerrainSource.S1m: return 100;
                case TerrainSource.Lidar1m: return 95;
                case TerrainSource.ThreeMetre: return 60;
                case TerrainSource.TenMetre: return 30;
                default: throw new ArgumentOutOfRangeException(nameof(source));
            }
        }

        public static string Label(TerrainSource source)
        {
            switch (source)
            {
                case TerrainSource.S1m: return "USGS S1M 1 m lidar";
                case TerrainSource.Lidar1m: return "3DEP 1 m lidar";
                case TerrainSource.ThreeMetre: return "3DEP 3 m";
                case TerrainSource.TenMetre: return "3DEP 10 m";
                default: throw new ArgumentOutOfRangeException(nameof(source));
            }
        }

        /// <summary>Classifies a fallback source by its cell size in metres.</summary>
        public static TerrainSource FromCellSize(double metres) =>
            metres <= 1.5 ? TerrainSource.Lidar1m : metres <= 5 ? TerrainSource.ThreeMetre : TerrainSource.TenMetre;

        /// <summary>The score: cell counts per source, weighted, rounded to the nearest whole number.</summary>
        public static int Score(IReadOnlyDictionary<TerrainSource, long> cells)
        {
            long total = cells.Values.Sum();
            if (total <= 0) throw new ArgumentException("No cells to score.", nameof(cells));
            double weighted = cells.Sum(kv => (double)kv.Value * Weight(kv.Key));
            return (int)Math.Round(weighted / total, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// For example: "Terrain quality 97/100: 96% USGS S1M 1 m lidar, 4% 3DEP 10 m". Shares are
        /// whole percentages, largest first; a source under 1% shows as "&lt;1%".
        /// </summary>
        public static string OneLiner(IReadOnlyDictionary<TerrainSource, long> cells)
        {
            long total = cells.Values.Sum();
            var parts = cells.Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                .Select(kv =>
                {
                    double pct = 100.0 * kv.Value / total;
                    string share = pct < 1 ? "<1%" : ((int)Math.Round(pct, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";
                    return share + " " + Label(kv.Key);
                });
            return $"Terrain quality {Score(cells)}/100: " + string.Join(", ", parts);
        }
    }
}
