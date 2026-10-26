#nullable enable
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence.Reports
{
    /// <summary>What a report is about: a problem (logs and save ticked) or a suggestion (decision PR1).</summary>
    public static class ReportKind
    {
        public const string Problem = "problem";
        public const string Suggestion = "suggestion";

        public static bool IsKnown(string? kind) => kind == Problem || kind == Suggestion;
    }

    /// <summary>
    /// The parts a report can hold, each a row in the Send feedback dialog the player can untick (decision PR5). The
    /// ids are the zip's folders and the values report.json lists against each file.
    /// </summary>
    public static class ReportPart
    {
        public const string Screenshot = "screenshot";
        public const string ErrorLogs = "errorlogs";
        public const string GameLog = "gamelog";
        public const string CrashDump = "crashdump";
        public const string Save = "save";
        public const string System = "system";

        /// <summary>In the dialog's order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Screenshot, ErrorLogs, GameLog, CrashDump, Save, System };

        /// <summary>Ticked when the dialog opens on <paramref name="kind"/>: a suggestion starts with only the picture and the system.</summary>
        public static bool OnByDefault(string part, string kind) =>
            kind == ReportKind.Problem || part == Screenshot || part == System;
    }

    /// <summary>The area the player had open: its package id and attribution, never the package itself.</summary>
    public sealed class ReportArea
    {
        public string PackageId = "";
        public string Name = "";
        public string SaveName = "";
        /// <summary>The area's centre and size, so it can be downloaded again when the owner hasn't got it.</summary>
        public double Latitude, Longitude, SizeMetres;
        public List<string> Attribution = new List<string>();
    }

    /// <summary>One file inside the zip, as report.json lists it.</summary>
    public sealed class ReportFile
    {
        public string Path = "";
        public string Part = "";
        public long Bytes;
        /// <summary>True when only the end of a long log was kept.</summary>
        public bool Cut;
    }

    /// <summary>
    /// A problem report's <c>report.json</c> (task P3-14): who made it (game version and commit), what the player said,
    /// the machine and settings, the area, and the files beside it. Not a frozen format (0.3 §5), but versioned so an
    /// older game's report still opens.
    /// </summary>
    public sealed class ProblemReport
    {
        public const string FileName = "report.json";
        public const int CurrentVersion = 1;
        public static readonly VersionedJson Format = new VersionedJson("problem report", nameof(Version), CurrentVersion);

        public int Version = CurrentVersion;
        public string Kind = ReportKind.Problem;
        public string Title = "";
        public string Note = "";
        /// <summary>When it was made, UTC, ISO 8601 (passed in: Persistence never reads the clock).</summary>
        public string CreatedUtc = "";
        public string GameVersion = "";
        public string DisplayVersion = "";
        public string Commit = "";
        /// <summary>Made from the title's offer after the game closed unexpectedly (decision PR7).</summary>
        public bool AfterCrash;
        public ReportArea? Area;
        /// <summary>GPU, driver, CPU, memory, Windows, display and scale, keyed by name.</summary>
        public SortedDictionary<string, string> System = new SortedDictionary<string, string>(global::System.StringComparer.Ordinal);
        /// <summary>
        /// Every setting the player can change, by its stored key; each value is typed: "i:3", "f:1.5" or "s:text" (as
        /// PlayerPrefs keeps it), so a report's settings can be applied as they were (decision PR9).
        /// </summary>
        public SortedDictionary<string, string> Settings = new SortedDictionary<string, string>(global::System.StringComparer.Ordinal);
        public List<ReportFile> Files = new List<ReportFile>();

        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore };

        public string ToJson() => JsonConvert.SerializeObject(this, JsonSettings) + "\n";

        public static ProblemReport Parse(string json) => Format.Read<ProblemReport>(json, JsonSettings);
    }
}
