using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Terrain;

namespace MountainPlanner.UI.Picker
{
    /// <summary>
    /// The picker's words and figures for the estimate, in plain language (0.4 §1 "Honest data"): the
    /// quality as a number with its word (as the HUD's quality badge and the S5 card show it), the
    /// sources, the download size and time, and the warning before a site that is not all 1 m (0.3 §4.2).
    /// </summary>
    public static class PickerText
    {
        /// <summary>The quality word for a score, on the HUD badge's bands (90 / 75 / 50).</summary>
        public static string QualityWord(int score) =>
            score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 50 ? "Fair" : "Limited";

        /// <summary>For example "9% 1 m lidar · 90% ~3 m · 1% ~10 m": shares of 1% or more, finest first.</summary>
        public static string Sources(SiteEstimate e)
        {
            var parts = new List<string>();
            void Add(TerrainSource s, string label)
            {
                double pct = e.Share(s) * 100;
                if (pct >= 1) parts.Add(((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture) + "% " + label);
            }
            Add(TerrainSource.S1m, "1 m S1M");
            Add(TerrainSource.Lidar1m, "1 m lidar");
            Add(TerrainSource.ThreeMetre, "~3 m");
            Add(TerrainSource.TenMetre, "~10 m");
            return parts.Count == 0 ? "~10 m" : string.Join(" · ", parts);
        }

        /// <summary>Megabytes, rounded as an estimate deserves: "85 MB", "390 MB".</summary>
        public static string Megabytes(long bytes)
        {
            double mb = bytes / 1_000_000.0;
            double rounded = mb < 100 ? Math.Round(mb / 5) * 5 : Math.Round(mb / 10) * 10;
            return Math.Max(5, rounded).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>"under 1 min" or "2 min".</summary>
        public static string Minutes(double seconds) =>
            seconds < 60 ? "under 1 min" : ((int)Math.Round(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min";

        /// <summary>"about 390 MB, 2 min".</summary>
        public static string Download(SiteEstimate e) => "about " + Megabytes(e.Bytes) + ", " + Minutes(e.Seconds);

        /// <summary>
        /// The warning under the estimate, or null when the whole site is 1 m: what is coarser and what that
        /// means, in words (warnings are amber, never colour alone).
        /// </summary>
        public static string Warning(SiteEstimate e)
        {
            if (e.IsRough) return "The terrain data here couldn't be checked, so this assumes the coarsest (~10 m) terrain.";
            double coarse = e.Share(TerrainSource.ThreeMetre) + e.Share(TerrainSource.TenMetre);
            if (coarse < 0.01) return null;
            int pct = Math.Max(1, (int)Math.Round(coarse * 100));
            return $"Not all 1 m: {pct.ToString(CultureInfo.InvariantCulture)}% of this site is ~3 m or ~10 m terrain, which shows less detail.";
        }
    }
}
