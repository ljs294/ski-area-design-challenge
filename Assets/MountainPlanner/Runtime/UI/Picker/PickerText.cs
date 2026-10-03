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
    /// Data resolutions follow the game's units like every other figure (owner, 2026-10-03): 1 m reads
    /// 3 ft, about 3 m reads about 10 ft, about 10 m reads about 33 ft.
    /// </summary>
    public static class PickerText
    {
        /// <summary>The quality word for a score, on the HUD badge's bands (90 / 75 / 50).</summary>
        public static string QualityWord(int score) =>
            score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 50 ? "Fair" : "Limited";

        /// <summary>A source's name in the game's units: "1 m S1M" or "3 ft S1M", "~3 m" or "~10 ft".</summary>
        public static string SourceLabel(TerrainSource source) => SourceLabel(source, PickerUnits.Imperial);

        public static string SourceLabel(TerrainSource source, bool imperial)
        {
            switch (source)
            {
                case TerrainSource.S1m: return imperial ? "3 ft S1M" : "1 m S1M";
                case TerrainSource.Lidar1m: return imperial ? "3 ft lidar" : "1 m lidar";
                case TerrainSource.ThreeMetre: return imperial ? "~10 ft" : "~3 m";
                default: return imperial ? "~33 ft" : "~10 m";
            }
        }

        /// <summary>For example "9% 1 m lidar · 90% ~3 m · 1% ~10 m": shares of 1% or more, finest first.</summary>
        public static string Sources(SiteEstimate e) => Sources(e, PickerUnits.Imperial);

        public static string Sources(SiteEstimate e, bool imperial)
        {
            var parts = new List<string>();
            foreach (var s in new[] { TerrainSource.S1m, TerrainSource.Lidar1m, TerrainSource.ThreeMetre, TerrainSource.TenMetre })
            {
                double pct = e.Share(s) * 100;
                if (pct >= 1) parts.Add(((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture) + "% " + SourceLabel(s, imperial));
            }
            return parts.Count == 0 ? SourceLabel(TerrainSource.TenMetre, imperial) : string.Join(" · ", parts);
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
        public static string Warning(SiteEstimate e) => Warning(e, PickerUnits.Imperial);

        public static string Warning(SiteEstimate e, bool imperial)
        {
            string ten = SourceLabel(TerrainSource.TenMetre, imperial), three = SourceLabel(TerrainSource.ThreeMetre, imperial);
            if (e.IsRough) return $"The terrain data here couldn't be checked, so this assumes the coarsest ({ten}) terrain.";
            double coarse = e.Share(TerrainSource.ThreeMetre) + e.Share(TerrainSource.TenMetre);
            if (coarse < 0.01) return null;
            int pct = Math.Max(1, (int)Math.Round(coarse * 100));
            string fine = imperial ? "3 ft" : "1 m";
            return $"Not all {fine}: {pct.ToString(CultureInfo.InvariantCulture)}% of this site is {three} or {ten} terrain, which shows less detail.";
        }
    }
}
