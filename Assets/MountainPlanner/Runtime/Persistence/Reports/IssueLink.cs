#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace MountainPlanner.Persistence.Reports
{
    /// <summary>
    /// The pre-filled GitHub issue a report opens (decision PR2), and the plain-text summary Copy summary puts on the
    /// clipboard. The link carries only what the player typed, the kind, the version and the graphics card: never a
    /// path, a log line or the save. Its field ids match .github/ISSUE_TEMPLATE/problem.yml and suggestion.yml.
    /// </summary>
    public static class IssueLink
    {
        public const string Repository = "https://github.com/ljs294/ski-area-design-challenge";
        /// <summary>The note is cut here, so the link stays well inside what browsers accept.</summary>
        public const int NoteLimit = 1500;

        public static string For(ProblemReport report, string zipName)
        {
            bool problem = report.Kind != ReportKind.Suggestion;
            var query = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("template", problem ? "problem.yml" : "suggestion.yml"),
                new KeyValuePair<string, string>("title", report.Title.Trim()),
                new KeyValuePair<string, string>("version", Version(report)),
            };
            if (problem) query.Add(new KeyValuePair<string, string>("system", Gpu(report)));
            string note = report.Note.Trim();
            if (note.Length > NoteLimit) note = note.Substring(0, NoteLimit) + " …";
            if (note.Length > 0) query.Add(new KeyValuePair<string, string>("details", note));
            if (zipName.Length > 0) query.Add(new KeyValuePair<string, string>("report", zipName));

            var sb = new StringBuilder(Repository).Append("/issues/new");
            for (int i = 0; i < query.Count; i++)
                sb.Append(i == 0 ? '?' : '&').Append(query[i].Key).Append('=').Append(Uri.EscapeDataString(query[i].Value));
            return sb.ToString();
        }

        /// <summary>"v0.3.0 (a1b2c3d)".</summary>
        public static string Version(ProblemReport report)
        {
            string v = report.DisplayVersion.Length > 0 ? report.DisplayVersion : report.GameVersion;
            string sha = report.Commit.Length > 7 ? report.Commit.Substring(0, 7) : report.Commit;
            return sha.Length > 0 ? $"{v} ({sha})" : v;
        }

        static string Gpu(ProblemReport report) =>
            report.System.TryGetValue(SystemKeys.Gpu, out string? gpu) ? gpu : "";

        /// <summary>The summary a player pastes anywhere: kind, title, version, machine, area and the zip's name.</summary>
        public static string Summary(ProblemReport report, string zipName)
        {
            var sb = new StringBuilder();
            sb.Append(report.Kind == ReportKind.Suggestion ? "Suggestion: " : "Problem: ").Append(report.Title.Trim()).Append("\r\n");
            sb.Append("Ski Area Design Challenge ").Append(Version(report)).Append("\r\n");
            foreach (string key in new[] { SystemKeys.Gpu, SystemKeys.Windows, SystemKeys.Display })
                if (report.System.TryGetValue(key, out string? value) && value.Length > 0) sb.Append(key).Append(": ").Append(value).Append("\r\n");
            if (report.Area != null && report.Area.Name.Length > 0) sb.Append("Area: ").Append(report.Area.Name).Append("\r\n");
            if (report.Note.Trim().Length > 0) sb.Append("\r\n").Append(report.Note.Trim()).Append("\r\n");
            if (zipName.Length > 0) sb.Append("\r\nReport file: ").Append(zipName).Append("\r\n");
            return sb.ToString();
        }
    }

    /// <summary>The names <see cref="ProblemReport.System"/> uses, so the summary and the link find them.</summary>
    public static class SystemKeys
    {
        public const string Gpu = "GPU";
        public const string Driver = "Graphics API";
        public const string Vram = "VRAM";
        public const string Cpu = "CPU";
        public const string Memory = "Memory";
        public const string Windows = "Windows";
        public const string Display = "Display";
        public const string Scale = "Interface scale";
        public const string Theme = "Theme";
        public const string Quality = "Quality";
    }
}
