using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Terrain;

namespace MountainPlanner.Acquisition.Picker
{
    /// <summary>Where each kind of terrain data exists over a site's core, as shares from 0 to 1.</summary>
    public struct CoverageShares
    {
        /// <summary>Inside published S1M tiles.</summary>
        public double S1m;
        /// <summary>Inside any 1 m DEM footprint (S1M areas usually are too).</summary>
        public double OneMetre;
        /// <summary>Inside any 1/9 arc-second (about 3 m) or 1 m footprint.</summary>
        public double ThreeMetre;
        /// <summary>The share of the ring inside published S1M tiles.</summary>
        public double RingS1m;
        /// <summary>False when the coverage couldn't be checked; the estimate then assumes 10 m.</summary>
        public bool Known;
    }

    /// <summary>
    /// The picker's estimate line (0.4 S3): the expected terrain score (T18), the source mix, and the
    /// download size and time. Size and time are fitted to the downloads measured on the reference PC
    /// (tools/acquire/README.md, 2026-09-27): Jackson Hole 2 km (S1M) about 85 MB in 40 s, Jackson Hole
    /// 5 km 252 MB in 69 s, Crystal Mountain 5 km (84% fallback) 484 MB in 101 s. Fallback terrain
    /// costs about twice as much as S1M (3DEP exports are uncompressed).
    /// </summary>
    public static class SiteEstimator
    {
        public const double CoreMegabytesPerKm2 = 5.25;
        public const double RingMegabytesPerKm2 = 1.0;
        public const double FallbackFactor = 2.1;
        public const double FixedSeconds = 25;
        public const double MegabytesPerSecond = 5.8;

        public static SiteEstimate Estimate(SiteSquare site, CoverageShares coverage)
        {
            double s1m = Clamp(coverage.S1m);
            double one = Math.Max(s1m, Clamp(coverage.OneMetre));
            double three = Math.Max(one, Clamp(coverage.ThreeMetre));
            if (!coverage.Known) s1m = one = three = 0;
            var shares = new double[4];
            shares[(int)TerrainSource.S1m] = s1m;
            shares[(int)TerrainSource.Lidar1m] = one - s1m;
            shares[(int)TerrainSource.ThreeMetre] = three - one;
            shares[(int)TerrainSource.TenMetre] = 1 - three;

            var cells = new Dictionary<TerrainSource, long>();
            for (int i = 0; i < 4; i++) cells[(TerrainSource)i] = (long)Math.Round(shares[i] * 1_000_000);
            int score = TerrainQuality.Score(cells);

            double km = site.SizeKm;
            double ringKm = km + 2 * SiteSquare.RingMetres / 1000.0;
            double ringS1m = coverage.Known ? Clamp(coverage.RingS1m) : 0;
            double megabytes = CoreMegabytesPerKm2 * km * km * Mix(s1m) + RingMegabytesPerKm2 * ringKm * ringKm * Mix(ringS1m);
            double seconds = FixedSeconds + megabytes / MegabytesPerSecond;
            return new SiteEstimate(score, shares, (long)Math.Round(megabytes * 1_000_000), seconds, !coverage.Known);
        }

        /// <summary>
        /// The line under the name field, for example "Terrain about 48 · 16% S1M · 24% 3 m · 60% 10 m ·
        /// about 480 MB · about 2 min". Shares under 1% are left out.
        /// </summary>
        public static string Line(SiteEstimate e)
        {
            var parts = new List<string> { (e.IsRough ? "Terrain at least " : "Terrain about ") + e.TerrainScore.ToString(CultureInfo.InvariantCulture) };
            void Add(TerrainSource s, string label)
            {
                double pct = e.Share(s) * 100;
                if (pct >= 1) parts.Add(((int)Math.Round(pct)).ToString(CultureInfo.InvariantCulture) + "% " + label);
            }
            Add(TerrainSource.S1m, "S1M 1 m");
            Add(TerrainSource.Lidar1m, "1 m lidar");
            Add(TerrainSource.ThreeMetre, "3 m");
            Add(TerrainSource.TenMetre, "10 m");
            parts.Add("about " + Megabytes(e.Bytes));
            parts.Add(Duration(e.Seconds));
            return string.Join(" · ", parts);
        }

        static string Megabytes(long bytes)
        {
            double mb = bytes / 1_000_000.0;
            double rounded = mb < 100 ? Math.Round(mb / 5) * 5 : Math.Round(mb / 10) * 10;
            return Math.Max(5, rounded).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        static string Duration(double seconds) =>
            seconds < 60 ? "under 1 min" : "about " + ((int)Math.Round(seconds / 60)).ToString(CultureInfo.InvariantCulture) + " min";

        static double Mix(double s1mShare) => s1mShare + FallbackFactor * (1 - s1mShare);
        static double Clamp(double v) => double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, v));
    }
}
