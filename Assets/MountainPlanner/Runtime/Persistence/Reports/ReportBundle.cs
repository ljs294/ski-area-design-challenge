#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace MountainPlanner.Persistence.Reports
{
    /// <summary>Where a report's files come from. Any folder may be missing; its part then has nothing in it.</summary>
    public sealed class ReportSources
    {
        /// <summary>The game's error logs: &lt;data&gt;/Logs (task P2-10).</summary>
        public string ErrorLogs = "";
        /// <summary>Unity's Player.log and Player-prev.log: LocalLow\Alpine Labs\Ski Area Design Challenge.</summary>
        public string GameLogs = "";
        /// <summary>Unity's crash folders (Crash_2026-10-24_211203 and so on): %TEMP%\Alpine Labs\Ski Area Design Challenge\Crashes.</summary>
        public string Crashes = "";
        /// <summary>The open save's folder (save.json and tiles/), read only; null when nothing is saved.</summary>
        public string? SaveFolder;
        /// <summary>The view just before the dialog opened, encoded (JPG or PNG); null for none.</summary>
        public byte[]? Screenshot;
        public string ScreenshotName = "screenshot.jpg";
    }

    /// <summary>One file a report can hold, sized before anything is written so the dialog can show it.</summary>
    public sealed class ReportItem
    {
        public string Part = "";
        /// <summary>Its path inside the zip, with forward slashes.</summary>
        public string ZipPath = "";
        public string? SourcePath;
        public byte[]? Bytes;
        /// <summary>Text gets the user and computer names taken out; binary files go in as they are.</summary>
        public bool IsText;
        /// <summary>The bytes it adds before compression (a long log counts only the end that is kept).</summary>
        public long Size;
        public bool Cut;
    }

    /// <summary>
    /// The problem report zip (task P3-14, decisions PR5 and PR6): report.json and the files the player left ticked, in
    /// folders by part. Saves, logs and crash files are only ever read (shared, so the running game's logs open too); a
    /// long log keeps only its last <see cref="LogTail"/> bytes; every log and text file has the player's names taken out
    /// (the save goes in byte for byte, so it opens exactly as the player had it). The
    /// zip is written beside its final name and moved into place, so a half-written report never shows. Opening a
    /// report unpacks only paths inside the target folder (reports come from other people's PCs).
    /// </summary>
    public static class ReportBundle
    {
        public const string FolderName = "Reports";
        public const string FilePrefix = "report-";
        /// <summary>GitHub's limit for a file dragged into an issue.</summary>
        public const long GitHubLimit = 25L * 1024 * 1024;
        /// <summary>The end of Player.log kept (it can grow without bound in a long session).</summary>
        public const long LogTail = 4L * 1024 * 1024;
        /// <summary>Unity's crash folders older than this aren't offered.</summary>
        public static readonly TimeSpan CrashAge = TimeSpan.FromDays(14);
        public const int Keep = 10;

        public static string FolderIn(string dataRoot) => Path.Combine(dataRoot, FolderName);

        /// <summary>report-20261025-143210.zip, from the report's UTC time.</summary>
        public static string FileNameFor(DateTime utc) => FilePrefix + utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip";

        static readonly string[] TextExtensions = { ".log", ".txt", ".json" };

        /// <summary>Everything a report could hold, every part included; the dialog's ticks pick from it.</summary>
        public static IReadOnlyList<ReportItem> Gather(ReportSources sources, DateTime nowUtc)
        {
            var items = new List<ReportItem>();
            if (sources.Screenshot != null && sources.Screenshot.Length > 0)
                items.Add(new ReportItem { Part = ReportPart.Screenshot, ZipPath = sources.ScreenshotName, Bytes = sources.Screenshot, Size = sources.Screenshot.Length });

            foreach (string log in ErrorLog.Files(sources.ErrorLogs).Take(ErrorLog.Keep))
                AddFile(items, ReportPart.ErrorLogs, "logs/" + Path.GetFileName(log), log);

            foreach (string name in new[] { "Player.log", "Player-prev.log" })
                AddFile(items, ReportPart.GameLog, "logs/" + name, Path.Combine(sources.GameLogs, name));

            string? crash = NewestCrash(sources.Crashes, nowUtc);
            if (crash != null)
                foreach (string file in Directory.GetFiles(crash).OrderBy(f => f, StringComparer.Ordinal))
                    AddFile(items, ReportPart.CrashDump, "crash/" + Path.GetFileName(file), file);

            if (!string.IsNullOrEmpty(sources.SaveFolder) && Directory.Exists(sources.SaveFolder))
            {
                string root = Path.GetFullPath(sources.SaveFolder);
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    if (rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    AddFile(items, ReportPart.Save, "save/" + rel, file);
                }
            }
            return items;
        }

        /// <summary>The newest of Unity's crash folders, if it is recent enough to belong to this player's problem.</summary>
        public static string? NewestCrash(string crashes, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(crashes) || !Directory.Exists(crashes)) return null;
            string? newest = Directory.GetDirectories(crashes, "Crash_*").OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).FirstOrDefault();
            if (newest == null) return null;
            return nowUtc - Directory.GetLastWriteTimeUtc(newest) <= CrashAge ? newest : null;
        }

        static void AddFile(List<ReportItem> items, string part, string zipPath, string path)
        {
            if (!File.Exists(path)) return;
            long length = new FileInfo(path).Length;
            // The save goes in byte for byte, so the owner opens exactly what the player had (frozen format, read only).
            bool text = part != ReportPart.Save && TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
            bool cut = text && length > LogTail;
            items.Add(new ReportItem { Part = part, ZipPath = zipPath, SourcePath = path, IsText = text, Size = cut ? LogTail : length, Cut = cut });
        }

        /// <summary>The bytes the ticked parts add, report.json's allowance included.</summary>
        public static long SizeOf(IEnumerable<ReportItem> items, ICollection<string> parts) =>
            4096 + items.Where(i => parts.Contains(i.Part)).Sum(i => i.Size);

        /// <summary>
        /// Writes the zip to <paramref name="zipPath"/> with the ticked <paramref name="parts"/>. report.json lists every
        /// file; without the System part its system and settings are left empty. Returns the zip's size.
        /// </summary>
        public static long Write(string zipPath, ProblemReport report, IEnumerable<ReportItem> items, ICollection<string> parts, ReportPrivacy privacy)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? ".";
            Directory.CreateDirectory(folder);
            string temp = zipPath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);

            report.Files.Clear();
            if (!parts.Contains(ReportPart.System)) { report.System.Clear(); report.Settings.Clear(); }
            report.Title = privacy.Scrub(report.Title);
            report.Note = privacy.Scrub(report.Note);

            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var item in items.Where(i => parts.Contains(i.Part)))
                {
                    byte[]? bytes = Contents(item, privacy, out bool cut);
                    if (bytes == null) continue;
                    var entry = zip.CreateEntry(item.ZipPath, item.IsText || item.Part == ReportPart.Save ? CompressionLevel.Optimal : CompressionLevel.Fastest);
                    using (var s = entry.Open()) s.Write(bytes, 0, bytes.Length);
                    report.Files.Add(new ReportFile { Path = item.ZipPath, Part = item.Part, Bytes = bytes.Length, Cut = cut });
                }
                byte[] json = new UTF8Encoding(false).GetBytes(report.ToJson());
                using (var s = zip.CreateEntry(ProblemReport.FileName, CompressionLevel.Optimal).Open()) s.Write(json, 0, json.Length);
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(temp, zipPath);
            return new FileInfo(zipPath).Length;
        }

        /// <summary>A file's bytes for the zip: shared read, the end of a long log, names taken out of text. Null if it vanished.</summary>
        static byte[]? Contents(ReportItem item, ReportPrivacy privacy, out bool cut)
        {
            cut = false;
            if (item.Bytes != null) return item.Bytes;
            if (item.SourcePath == null) return null;
            byte[] raw;
            try { raw = ReadShared(item.SourcePath, item.Cut ? LogTail : long.MaxValue, out cut); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if (!item.IsText) return raw;
            string text = new UTF8Encoding(false).GetString(raw);
            if (cut)
            {
                int newline = text.IndexOf('\n');
                if (newline >= 0) text = text.Substring(newline + 1);
                text = "[Only the end of this log is kept: the last " + (LogTail / (1024 * 1024)) + " MB.]\n" + text;
            }
            return new UTF8Encoding(false).GetBytes(privacy.Scrub(text));
        }

        static byte[] ReadShared(string path, long tail, out bool cut)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            cut = length > tail;
            if (cut) stream.Seek(length - tail, SeekOrigin.Begin);
            var buffer = new MemoryStream((int)Math.Min(length, tail));
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        /// <summary>Reads a report's report.json without unpacking the rest.</summary>
        public static ProblemReport Read(string zipPath)
        {
            using var zip = OpenZip(zipPath);
            var entry = zip.GetEntry(ProblemReport.FileName) ?? throw new InvalidDataException($"{Path.GetFileName(zipPath)} has no {ProblemReport.FileName}: it isn't a problem report.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return ProblemReport.Parse(reader.ReadToEnd());
        }

        /// <summary>
        /// Unpacks a report into <paramref name="folder"/> (created) and returns its report.json. An entry whose path
        /// would land outside the folder is refused, so a crafted zip can't write anywhere else.
        /// </summary>
        public static ProblemReport Unpack(string zipPath, string folder)
        {
            string root = Path.GetFullPath(folder);
            Directory.CreateDirectory(root);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = OpenZip(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // a folder
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"The report holds a file outside its folder ({entry.FullName}); it wasn't opened.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using (var from = entry.Open())
                    using (var to = new FileStream(target, FileMode.Create, FileAccess.Write)) from.CopyTo(to);
                }
            }
            string json = Path.Combine(root, ProblemReport.FileName);
            if (!File.Exists(json)) throw new InvalidDataException($"{Path.GetFileName(zipPath)} has no {ProblemReport.FileName}: it isn't a problem report.");
            return ProblemReport.Parse(File.ReadAllText(json));
        }

        static ZipArchive OpenZip(string path) => new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);

        /// <summary>Deletes all but the newest <paramref name="keep"/> reports in <paramref name="folder"/>.</summary>
        public static void Prune(string folder, int keep = Keep)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string old in Directory.GetFiles(folder, FilePrefix + "*.zip").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Math.Max(0, keep)))
            {
                try { File.Delete(old); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
