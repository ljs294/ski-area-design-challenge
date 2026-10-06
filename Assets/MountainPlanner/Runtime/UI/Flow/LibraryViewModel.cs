using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MountainPlanner.Domain.Terrain;
using MountainPlanner.Persistence;

namespace MountainPlanner.UI.Flow
{
    public enum LibrarySort { LastOpened, Name, Quality }

    /// <summary>One row of S2: a downloaded mountain, or a paused download.</summary>
    public sealed class LibraryRow
    {
        public LibraryEntry Entry;
        public PendingDownload Pending;
        public bool IsPaused => Pending != null;
        public string Name = "";
        public string Place = "";
        public string Size = "";
        public int TerrainScore, FloraScore;
        public string TerrainText = "", FloraText = "";
        public string Disk = "";
        public string Opened = "";
        /// <summary>The paused row's progress line: "Paused at 38% · Forest".</summary>
        public string PausedText = "";
        /// <summary>Set on an area made by a newer version of the game (task 08): it can be deleted, not opened.</summary>
        public string NewerText = "";
        public bool IsNewer => NewerText.Length > 0;
        public bool CanOpen => Entry != null && !IsNewer;
    }

    /// <summary>
    /// S2 Load Area and Manage Areas (0.4 S2) as rows: paused downloads first, then the mountains in the chosen order, then any
    /// areas made by a newer version of the game (greyed: Delete works, Open doesn't). Built
    /// from the library scan, the pending-download records and the recently-opened file; nothing here
    /// touches the network.
    /// </summary>
    public sealed class LibraryViewModel
    {
        public readonly List<LibraryRow> Rows = new List<LibraryRow>();
        public string Summary { get; private set; } = "";
        public LibrarySort Sort { get; private set; }
        public bool IsEmpty => Rows.Count == 0;
        /// <summary>What an empty list says: no areas yet, or why a newer game's library can't be listed.</summary>
        public string EmptyText { get; private set; } = NoAreas;
        public const string NoAreas = "No areas yet. Choose New Area to download one.";

        /// <summary>A data folder laid out by a newer version of the game (task 08): no rows, just the reason.</summary>
        public static LibraryViewModel Refused(string refusal, LibrarySort sort) =>
            new LibraryViewModel { Sort = sort, EmptyText = refusal, Summary = "Needs a newer version of Mountain Planner" };

        public static LibraryViewModel Build(IReadOnlyList<LibraryEntry> entries, IReadOnlyList<PendingDownload> pending,
                                             RecentResorts recent, LibrarySort sort, DateTime nowUtc,
                                             IReadOnlyList<LibraryEntry> newer = null)
        {
            newer = newer ?? Array.Empty<LibraryEntry>();
            var vm = new LibraryViewModel { Sort = sort };
            foreach (var p in pending)
                vm.Rows.Add(new LibraryRow
                {
                    Pending = p, Name = p.Name, Place = Place(p.Latitude, p.Longitude), Size = FlowUnits.SiteSize(p.SizeKm),
                    PausedText = $"Paused at {(int)Math.Floor(Math.Max(0, Math.Min(1, p.LastOverall)) * 100)}%" + (p.LastStage.Length > 0 ? " · " + p.LastStage : ""),
                });
            IEnumerable<LibraryEntry> ordered;
            string Opened(LibraryEntry e) => recent.Opened.TryGetValue(e.PackageId, out var t) ? t : "";
            switch (sort)
            {
                case LibrarySort.Name:
                    ordered = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.PackageId, StringComparer.Ordinal);
                    break;
                case LibrarySort.Quality:
                    ordered = entries.OrderByDescending(e => e.TerrainScore).ThenByDescending(e => e.FloraScore)
                                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.PackageId, StringComparer.Ordinal);
                    break;
                default:
                    // Never-opened mountains go last, newest download first among them.
                    ordered = entries.OrderByDescending(e => Opened(e), StringComparer.Ordinal).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal)
                                     .ThenBy(e => e.PackageId, StringComparer.Ordinal);
                    break;
            }
            foreach (var e in ordered)
                vm.Rows.Add(new LibraryRow
                {
                    Entry = e, Name = e.Name, Place = Place(e.Latitude, e.Longitude), Size = FlowUnits.SiteSize(e.SizeKm),
                    TerrainScore = e.TerrainScore, FloraScore = e.FloraScore,
                    TerrainText = Score(e.TerrainScore), FloraText = Score(e.FloraScore),
                    Disk = Disk(e.BytesOnDisk), Opened = When(Opened(e), nowUtc),
                });
            foreach (var e in newer.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Folder, StringComparer.Ordinal))
                vm.Rows.Add(new LibraryRow { Entry = e, Name = e.Name, Disk = Disk(e.BytesOnDisk), NewerText = NewerVersion });
            long total = entries.Sum(e => e.BytesOnDisk) + newer.Sum(e => e.BytesOnDisk);
            vm.Summary = $"{entries.Count} {(entries.Count == 1 ? "area" : "areas")} · {Disk(total)} on disk"
                         + (pending.Count > 0 ? $" · {pending.Count} paused" : "")
                         + (newer.Count > 0 ? $" · {newer.Count} {(newer.Count == 1 ? "needs" : "need")} a newer version" : "");
            return vm;
        }

        public const string NewerVersion = "Made by a newer version of Mountain Planner. Update the game to open it.";

        public static string Score(int score) => $"{score} {QualityBands.Word(QualityBands.Of(score))}";

        public static string Place(double lat, double lon) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.000} {1} {2:0.000} {3}", Math.Abs(lat), lat >= 0 ? "N" : "S", Math.Abs(lon), lon >= 0 ? "E" : "W");

        public static string Disk(long bytes) =>
            bytes >= 1e9 ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : (bytes / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";

        /// <summary>"today", "yesterday", "Sep 30", "Sep 30 2025", or "not opened yet".</summary>
        public static string When(string utc, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(utc) || !DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return "not opened yet";
            int days = (nowUtc.Date - t.Date).Days;
            if (days <= 0) return "today";
            if (days == 1) return "yesterday";
            return t.Year == nowUtc.Year ? t.ToString("MMM d", CultureInfo.InvariantCulture) : t.ToString("MMM d yyyy", CultureInfo.InvariantCulture);
        }
    }
}
