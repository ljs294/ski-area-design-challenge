#nullable enable
using System;
using System.IO;
using System.Text;

namespace MountainPlanner.Persistence.Reports
{
    /// <summary>
    /// Tells the next launch that the game closed unexpectedly (decision PR7). A session writes
    /// &lt;data&gt;/Reports/running.txt when it starts and deletes it when it quits cleanly. If the next launch finds the
    /// file, the last session crashed or was killed: the file becomes last-crash.txt, which the title's offer reads and
    /// clears once the player answers, so each crash is offered once. Tool runs (benchmarks, captures, smoke tests)
    /// never call <see cref="Begin"/>.
    /// </summary>
    public sealed class CrashMarker
    {
        public const string RunningName = "running.txt";
        public const string LastCrashName = "last-crash.txt";

        readonly string _folder;

        public CrashMarker(string dataRoot) { _folder = ReportBundle.FolderIn(dataRoot); }

        string Running => Path.Combine(_folder, RunningName);
        string LastCrash => Path.Combine(_folder, LastCrashName);

        /// <summary>
        /// Starts a session: keeps a marker the previous session left as the pending crash, then writes this one's.
        /// <paramref name="session"/> is a line naming the session (version and start time) for the offer and the report.
        /// </summary>
        public void Begin(string session)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                if (File.Exists(Running))
                {
                    if (File.Exists(LastCrash)) File.Delete(LastCrash);
                    File.Move(Running, LastCrash);
                }
                File.WriteAllText(Running, session + "\n", new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The clean quit: this session didn't crash.</summary>
        public void End()
        {
            try { if (File.Exists(Running)) File.Delete(Running); }
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
    }
}
