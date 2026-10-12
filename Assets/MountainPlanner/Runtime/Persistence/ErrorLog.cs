#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MountainPlanner.Persistence
{
    /// <summary>One error the game hit: what kind, the message, its stack trace, and when.</summary>
    public readonly struct ErrorEntry
    {
        public readonly string Kind, Message, Stack;
        public readonly DateTime Utc;
        /// <summary>Seconds since the session started.</summary>
        public readonly double Uptime;

        public ErrorEntry(string kind, string message, string stack, DateTime utc, double uptime)
        {
            Kind = kind ?? "";
            Message = message ?? "";
            Stack = stack ?? "";
            Utc = utc;
            Uptime = uptime;
        }

        public bool SameAs(in ErrorEntry other) => Kind == other.Kind && Message == other.Message && Stack == other.Stack;
    }

    /// <summary>
    /// The crash and error log a tester can send (task P2-10): one file per session, errors-&lt;stamp&gt;.log under the
    /// data root's Logs folder, created at the first error so clean sessions leave nothing behind. It starts with a
    /// header (version, commit, machine, settings) and then records every exception, error and assert with its stack
    /// trace. A run of identical entries is written once and counted; the file stops growing at <see cref="MaxBytes"/>;
    /// only the newest <see cref="Keep"/> logs are kept. Every write is flushed, so the file survives a crash right
    /// after it. Thread-safe: Unity reports errors from worker threads too. Not a frozen format (0.3 §5).
    /// </summary>
    public sealed class ErrorLog : IDisposable
    {
        public const string FolderName = "Logs";
        public const string FilePrefix = "errors-";
        public const string FileExtension = ".log";
        public const int Keep = 10;
        public const long MaxBytes = 5L * 1024 * 1024;

        public static string FolderIn(string dataRoot) => System.IO.Path.Combine(dataRoot, FolderName);

        /// <summary>A session's file name stamp, from its start time: 20261012-180311.</summary>
        public static string Stamp(DateTime utc) => utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        readonly object _gate = new object();
        readonly string _folder, _stamp;
        readonly Func<string> _header;
        readonly long _maxBytes;
        readonly int _keep;
        StreamWriter? _writer;
        ErrorEntry _last;
        bool _hasLast, _capped, _disposed;
        int _repeats;
        double _lastRepeatUptime;

        /// <param name="header">Called once, when the first error creates the file.</param>
        public ErrorLog(string folder, string sessionStamp, Func<string> header, long maxBytes = MaxBytes, int keep = Keep)
        {
            _folder = folder;
            _stamp = sessionStamp;
            _header = header;
            _maxBytes = maxBytes;
            _keep = Math.Max(1, keep);
        }

        /// <summary>The session's file, or null until the first error.</summary>
        public string? Path { get; private set; }

        /// <summary>Entries recorded, repeats included.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// Records <paramref name="entry"/>. True only for the session's first entry (the file was just created), so the
        /// caller can tell the player once. Never throws: a log that can't be written is no reason to stop the game.
        /// </summary>
        public bool Write(in ErrorEntry entry)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                Count++;
                try
                {
                    bool first = false;
                    if (_writer == null)
                    {
                        Open();
                        first = true;
                    }
                    if (_capped) return first;
                    if (_hasLast && _last.SameAs(entry))
                    {
                        _repeats++;
                        _lastRepeatUptime = entry.Uptime;
                        return first;
                    }
                    WriteRepeats();
                    Append(Format(entry));
                    _last = entry;
                    _hasLast = true;
                    return first;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Writes out a pending "the same entry N more times" line.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                try { WriteRepeats(); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { WriteRepeats(); }
                catch (Exception) { }
                _writer?.Dispose();
                _writer = null;
                _disposed = true;
            }
        }

        void Open()
        {
            Directory.CreateDirectory(_folder);
            Prune(_folder, _keep - 1);
            string path = System.IO.Path.Combine(_folder, FilePrefix + _stamp + FileExtension);
            for (int n = 2; File.Exists(path); n++)   // two sessions in the same second
                path = System.IO.Path.Combine(_folder, $"{FilePrefix}{_stamp}-{n}{FileExtension}");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            Path = path;
            string header;
            try { header = _header(); }
            catch (Exception e) { header = $"(the header failed: {e.Message})"; }
            Append(header.TrimEnd() + "\r\n" + new string('-', 72) + "\r\n");
        }

        void WriteRepeats()
        {
            if (_repeats == 0 || _writer == null || _capped) return;
            Append($"    (the same entry {_repeats} more time{(_repeats == 1 ? "" : "s")}, the last at +{_lastRepeatUptime.ToString("0.0", CultureInfo.InvariantCulture)} s)\r\n\r\n");
            _repeats = 0;
        }

        void Append(string text)
        {
            var writer = _writer!;
            long length = writer.BaseStream.Length;
            int bytes = writer.Encoding.GetByteCount(text);
            if (length + bytes > _maxBytes)
            {
                writer.Write($"(The log reached {_maxBytes / (1024 * 1024)} MB; later errors in this session aren't recorded.)\r\n");
                _capped = true;
                return;
            }
            writer.Write(text);
        }

        public static string Format(in ErrorEntry entry)
        {
            var text = new StringBuilder(256 + entry.Message.Length + entry.Stack.Length);
            text.Append('[').Append(entry.Utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                .Append(" +").Append(entry.Uptime.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s] ")
                .Append(entry.Kind).Append(": ").Append(entry.Message.TrimEnd()).Append("\r\n");
            foreach (string line in entry.Stack.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) text.Append("    ").Append(line.TrimEnd()).Append("\r\n");
            text.Append("\r\n");
            return text.ToString();
        }

        /// <summary>A log's text, read even while the game still has it open (Copy report).</summary>
        public static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>The logs in <paramref name="folder"/>, newest first (stamps sort by time).</summary>
        public static IReadOnlyList<string> Files(string folder)
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Directory.GetFiles(folder, FilePrefix + "*" + FileExtension)
                            .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
                            .ToArray();
        }

        /// <summary>Deletes all but the newest <paramref name="keep"/> logs; ones another program holds open stay.</summary>
        public static void Prune(string folder, int keep)
        {
            foreach (string old in Files(folder).Skip(Math.Max(0, keep)))
            {
                try { File.Delete(old); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
