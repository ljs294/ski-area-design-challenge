#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MountainPlanner.Persistence.Reports
{
    /// <summary>
    /// Tells the next launch that the game closed unexpectedly (decision PR7). A session writes
    /// &lt;data&gt;/Reports/running-&lt;process id&gt;.txt when it starts and deletes it when it quits cleanly. If a later
    /// launch finds a marker whose process is gone, that session crashed or was killed: it becomes last-crash.txt, which
    /// the title's offer reads and clears once the player answers, so each crash is offered once. Tool runs (benchmarks,
    /// captures, smoke tests) never call <see cref="Begin"/>.
    ///
    /// Task 4.26 BB20: each session has a marker of its own, named by its process and holding the process's start time,
    /// so a second game running at the same time (another copy, a test run) no longer reads the first one's marker as a
    /// crash, and a process id Windows reuses later isn't mistaken for the session that wrote it. The old single
    /// running.txt (v0.3.4 and before) still counts as a crash once.
    /// </summary>
    public sealed class CrashMarker
    {
        /// <summary>The marker before task 4.26 (one for every session).</summary>
        public const string LegacyRunningName = "running.txt";
        public const string RunningPrefix = "running-";
        public const string LastCrashName = "last-crash.txt";

        readonly string _folder;
        readonly Func<int, long, bool> _isAlive;
        string? _own;

        /// <param name="isAlive">
        /// Whether the process with this id is running and started at this time (UTC ticks): that session is still going.
        /// The game asks Windows; tests answer for themselves.
        /// </param>
        public CrashMarker(string dataRoot, Func<int, long, bool> isAlive)
        {
            _folder = ReportBundle.FolderIn(dataRoot);
            _isAlive = isAlive;
        }

        string LastCrash => Path.Combine(_folder, LastCrashName);

        /// <summary>A session's marker file.</summary>
        public static string RunningName(int processId) => RunningPrefix + processId.ToString(CultureInfo.InvariantCulture) + ".txt";

        /// <summary>
        /// Starts a session: a marker that a session left behind (its process gone) becomes the pending crash, then this
        /// session writes its own. <paramref name="session"/> names the session (version and start time) for the offer and
        /// the report; <paramref name="processId"/> and <paramref name="startedTicks"/> (UTC) identify its process.
        /// </summary>
        public void Begin(string session, int processId, long startedTicks)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                string? newest = null;
                DateTime newestAt = DateTime.MinValue;
                var stale = new List<string>();
                foreach (string file in Directory.GetFiles(_folder, "running*.txt"))
                {
                    string name = Path.GetFileName(file);
                    bool legacy = string.Equals(name, LegacyRunningName, StringComparison.OrdinalIgnoreCase);
                    if (!legacy)
                    {
                        if (!TryParse(file, out int pid, out long ticks)) { stale.Add(file); continue; }
                        if (pid == processId || _isAlive(pid, ticks)) continue;   // a session that is still running
                    }
                    stale.Add(file);
                    var at = File.GetLastWriteTimeUtc(file);
                    if (newest == null || at > newestAt) { newest = file; newestAt = at; }
                }
                if (newest != null)
                {
                    string line = SessionLine(newest);
                    File.WriteAllText(LastCrash, line + "\n", new UTF8Encoding(false));
                }
                foreach (string file in stale) File.Delete(file);
                _own = Path.Combine(_folder, RunningName(processId));
                File.WriteAllText(_own, startedTicks.ToString(CultureInfo.InvariantCulture) + "\n" + session + "\n", new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The clean quit: this session didn't crash.</summary>
        public void End()
        {
            try { if (_own != null && File.Exists(_own)) File.Delete(_own); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The crashed session's line, or null when the last session closed cleanly (or it was already answered).</summary>
        public string? PendingCrash
        {
            get
            {
                try { return File.Exists(LastCrash) ? File.ReadAllText(LastCrash).Trim() : null; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        /// <summary>The player answered the offer (sent, or Not now): it isn't asked again for this crash.</summary>
        public void Answered()
        {
            try { if (File.Exists(LastCrash)) File.Delete(LastCrash); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>A per-session marker's process id (from its name) and start time (its first line).</summary>
        static bool TryParse(string file, out int processId, out long startedTicks)
        {
            startedTicks = 0;
            string name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(RunningPrefix, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(name.Substring(RunningPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out processId))
            {
                processId = 0;
                return false;
            }
            try
            {
                using var reader = new StreamReader(file);
                return long.TryParse(reader.ReadLine(), NumberStyles.None, CultureInfo.InvariantCulture, out startedTicks);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>The session line a marker holds: the legacy file's only line, a per-session file's second.</summary>
        static string SessionLine(string file)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                bool legacy = string.Equals(Path.GetFileName(file), LegacyRunningName, StringComparison.OrdinalIgnoreCase);
                int at = legacy ? 0 : 1;
                return lines.Length > at ? lines[at].Trim() : "";
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }
    }
}
