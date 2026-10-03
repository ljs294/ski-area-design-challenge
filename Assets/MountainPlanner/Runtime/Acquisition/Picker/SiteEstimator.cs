using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// The estimate for a site, the way the downloader will score it (HeightAssembler): the core in 1 km
        /// sectors (1,000 × 1,000 cells from the north-west corner, the last ones narrower); S1M wherever a
        /// published tile covers a sector; the rest from the 3DEP service's own choice at each sector's
        /// centre. Two lookups at most (the S1M listing is cached for a day) and one getSamples request.
        /// If either fails, the estimate is rough and says so.
        /// </summary>
        public static async Task<SiteEstimate> EstimateAsync(SiteSquare site, CoverageIndex index, CancellationToken ct)
        {
            var sectors = CoreSectors(site);
            IReadOnlyList<AlbersBox> tiles = Array.Empty<AlbersBox>();
            TerrainSource[] sources = null;
            bool known = true;
            try
            {
                tiles = await index.S1mTilesAsync(site.Ring, ct).ConfigureAwait(false);
                var centres = new List<AlbersPoint>();
                foreach (var s in sectors) centres.Add(s.Centre);
                sources = await index.FallbackSourcesAsync(centres, ct).ConfigureAwait(false);
            }
            catch (IOException) { known = false; }
            catch (Newtonsoft.Json.JsonException) { known = false; }
            return Estimate(site, Shares(site, sectors, tiles, sources, known));
        }

        /// <summary>The core's 1 km sectors, as the downloader cuts them.</summary>
        public static List<AlbersBox> CoreSectors(SiteSquare site)
        {
            var grid = site.CoreGrid;
            const int cells = HeightAssembler.SectorCells;
            var sectors = new List<AlbersBox>();
            for (int r0 = 0; r0 < grid.Rows; r0 += cells)
                for (int c0 = 0; c0 < grid.Columns; c0 += cells)
                {
                    int w = Math.Min(cells, grid.Columns - c0), h = Math.Min(cells, grid.Rows - r0);
                    sectors.Add(new AlbersBox(grid.West + c0 * grid.CellSize, grid.North - (r0 + h) * grid.CellSize,
                                              grid.West + (c0 + w) * grid.CellSize, grid.North - r0 * grid.CellSize));
                }
            return sectors;
        }

        /// <summary>
        /// The shares over the core: each sector's S1M part from the tiles, its remainder from the source at
        /// its centre (about 10 m when unknown), weighted by area. The ring's S1M share sizes the download.
        /// </summary>
        public static CoverageShares Shares(SiteSquare site, IReadOnlyList<AlbersBox> sectors, IReadOnlyList<AlbersBox> s1mTiles,
                                            IReadOnlyList<TerrainSource> sources, bool known)
        {
            double total = 0, s1m = 0, lidar = 0, three = 0;
            for (int i = 0; i < sectors.Count; i++)
            {
                var sector = sectors[i];
                double area = sector.Width * sector.Height;
                double fromS1m = Overlap(sector, s1mTiles);
                double rest = area - fromS1m;
                total += area;
                s1m += fromS1m;
                var source = sources != null && i < sources.Count ? sources[i] : TerrainSource.TenMetre;
                if (source == TerrainSource.S1m || source == TerrainSource.Lidar1m) lidar += rest;
                else if (source == TerrainSource.ThreeMetre) three += rest;
            }
            var ring = site.Ring;
            return new CoverageShares
            {
                S1m = s1m / total,
                OneMetre = (s1m + lidar) / total,
                ThreeMetre = (s1m + lidar + three) / total,
                RingS1m = Overlap(ring, s1mTiles) / (ring.Width * ring.Height),
                Known = known,
            };
        }

        static double Overlap(AlbersBox box, IReadOnlyList<AlbersBox> tiles)
        {
            double covered = 0;
            foreach (var t in tiles)
            {
                double w = Math.Min(box.East, t.East) - Math.Max(box.West, t.West);
                double h = Math.Min(box.North, t.North) - Math.Max(box.South, t.South);
                if (w > 0 && h > 0) covered += w * h;
            }
            return Math.Min(covered, box.Width * box.Height);
        }

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
