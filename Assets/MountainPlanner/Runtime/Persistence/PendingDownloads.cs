using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// A download that has started but not finished (task 14). It is written when the download starts and
    /// deleted when the package reaches the library, so a quit or crash leaves it behind for S1 and S2 to
    /// offer as "Paused, resume". Resuming reruns the same request, and the shared download cache makes that
    /// continue rather than start over.
    /// </summary>
    public sealed class PendingDownload
    {
        public const int CurrentVersion = 1;
        public static readonly VersionedJson Migrations = new VersionedJson("paused download", nameof(Version), CurrentVersion);

        public int Version { get; set; } = CurrentVersion;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>The snapped site centre, WGS84 (what the pipeline's SiteRequest takes).</summary>
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double SizeKm { get; set; }
        public string StartedUtc { get; set; } = "";
        /// <summary>The last progress seen, 0–1, and its stage: shown on the paused row, never trusted for work.</summary>
        public double LastOverall { get; set; }
        public string LastStage { get; set; } = "";
    }

    /// <summary>The unfinished downloads under &lt;data&gt;/Downloads, one folder each: the record plus its build folder.</summary>
    public static class PendingDownloads
    {
        public const string RecordFile = "download.json";

        public static string Folder(string dataRoot) => Path.Combine(dataRoot, "Downloads");

        /// <summary>Where this download's record and partial package live.</summary>
        public static string FolderOf(string dataRoot, PendingDownload d) => Path.Combine(Folder(dataRoot), d.Id);

        /// <summary>Where the pipeline writes the package before it moves into the library.</summary>
        public static string BuildFolder(string dataRoot, PendingDownload d) => Path.Combine(FolderOf(dataRoot, d), "build");

        /// <summary>
        /// A stable id from the name and the site: the same request always maps to the same folder, so
        /// starting it again resumes rather than duplicating it.
        /// </summary>
        public static string IdFor(string name, double latitude, double longitude, double sizeKm)
        {
            var slug = new StringBuilder();
            foreach (char c in name.Trim().ToLowerInvariant())
                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') slug.Append(c);
                else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
            string s = slug.ToString().Trim('-');
            if (s.Length > 40) s = s.Substring(0, 40).TrimEnd('-');
            if (s.Length == 0) s = "site";
            string where = string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F1}", latitude, longitude, sizeKm);
            return s + "-" + Fnv(where).ToString("x8", CultureInfo.InvariantCulture);
        }

        /// <summary>Writes the record; false (and nothing written) when a newer version of the game left a record there.</summary>
        public static bool Save(string dataRoot, PendingDownload d)
        {
            if (string.IsNullOrEmpty(d.Id)) throw new ArgumentException("The download needs an id.", nameof(d));
            string folder = FolderOf(dataRoot, d);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, RecordFile);
            if (PendingDownload.Migrations.IsNewer(path)) return false;
            AtomicFile.WriteJson(path, d);
            return true;
        }

        /// <summary>
        /// Every readable record, migrated, oldest first; unreadable or newer-version records are skipped, never an error
        /// (a newer game's paused download stays on disk for that game to resume).
        /// </summary>
        public static List<PendingDownload> List(string dataRoot)
        {
            var list = new List<PendingDownload>();
            string root = Folder(dataRoot);
            if (!Directory.Exists(root)) return list;
            foreach (string folder in Directory.GetDirectories(root))
            {
                string path = Path.Combine(folder, RecordFile);
                if (!File.Exists(path)) continue;
                try
                {
                    var d = PendingDownload.Migrations.Read<PendingDownload>(File.ReadAllText(path));
                    if (d.Id == Path.GetFileName(folder)) list.Add(d);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is UnauthorizedAccessException) { }
            }
            return list.OrderBy(d => d.StartedUtc, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>Forgets a finished download: its record and whatever is left of its folder (the package has moved out).</summary>
        public static void Remove(string dataRoot, PendingDownload d)
        {
            string folder = FolderOf(dataRoot, d);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    /// <summary>When each mountain was last opened (&lt;data&gt;/recent.json), for Continue and the library's sort.</summary>
    public sealed class RecentResorts
    {
        public const int CurrentVersion = 1;
        public const string FileName = "recent.json";
        public static readonly VersionedJson Migrations = new VersionedJson("recently opened list", nameof(Version), CurrentVersion);

        public int Version { get; set; } = CurrentVersion;

        /// <summary>Set when recent.json is from a newer version of the game: this list is empty, and Touch leaves that file alone.</summary>
        [JsonIgnore] public string Refusal { get; private set; } = "";
        /// <summary>Package id → when it was last opened, ISO 8601 UTC.</summary>
        public Dictionary<string, string> Opened { get; set; } = new Dictionary<string, string>();

        /// <summary>The most recently opened package id that's still in <paramref name="present"/>, or null.</summary>
        public string Latest(IEnumerable<string> present)
        {
            var have = new HashSet<string>(present, StringComparer.Ordinal);
            return Opened.Where(kv => have.Contains(kv.Key))
                         .OrderByDescending(kv => kv.Value, StringComparer.Ordinal).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                         .Select(kv => kv.Key).FirstOrDefault();
        }

        public static RecentResorts Load(string dataRoot)
        {
            string path = Path.Combine(dataRoot, FileName);
            if (!File.Exists(path)) return new RecentResorts();
            try
            {
                var r = Migrations.Read<RecentResorts>(File.ReadAllText(path));
                return r.Opened == null ? new RecentResorts() : r;
            }
            catch (FormatTooNewException e) { return new RecentResorts { Refusal = e.Message }; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is UnauthorizedAccessException) { return new RecentResorts(); }
        }

        /// <summary>
        /// Records an open; <paramref name="utc"/> comes from the caller, so this stays free of the clock. False (and
        /// nothing written) when recent.json is from a newer version of the game.
        /// </summary>
        public static bool Touch(string dataRoot, string packageId, string utc)
        {
            var r = Load(dataRoot);
            if (r.Refusal.Length > 0) return false;
            r.Opened[packageId] = utc;
            Directory.CreateDirectory(dataRoot);
            AtomicFile.WriteJson(Path.Combine(dataRoot, FileName), r);
            return true;
        }
    }
}
