using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Updates;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Task 4.23 R1: a start of the game exe itself goes through the launcher's quick check (see
    /// <see cref="HandOffToLauncher"/>). The launcher starts the game again with -updatechecked.
    ///
    /// Task 4.23 (UP7): the in-game update check, now the fallback. Once the title is in, once per run, it asks GitHub's
    /// /releases/latest (Acquisition.Updates, shared with tools/launcher) off the main thread, unless:
    /// - the launcher already asked (it starts the game with -updatechecked);
    /// - Settings › Data › Check for updates is off, or offline mode or -offline is on;
    /// - this is a capture, test or benchmark run, or a preview build (previews are never offered releases).
    /// A newer release shows the notice; Update starts the launcher with --update and quits (after "Save before
    /// leaving?" in an area). A game unzipped by hand has no launcher, so its notice offers the installer's page.
    /// No answer, a slow one or a bad one changes nothing: the game plays on.
    /// </summary>
    public sealed partial class AppFlow
    {
        bool _updateDecided, _updateChecking, _updateAuto;
        UpdateResult _updateResult;

        static string[] Args => Environment.GetCommandLineArgs();

        static string ArgValue(string name)
        {
            int i = Array.IndexOf(Args, name);
            return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
        }

        /// <summary>
        /// The version the check compares: the build's own, or -asversion's for a preview build (demo.bat 115 makes a
        /// preview pretend to be an older release; a release build ignores it).
        /// </summary>
        static string InstalledVersion
        {
            get
            {
                string own = BuildInfo.Current.version;
                string pretend = ArgValue("-asversion");
                return pretend != null && !ReleaseVersion.IsRelease(own) ? pretend : own;
            }
        }

        /// <summary>The game's own folder (the exe's), where an update lands next to.</summary>
        static string GameFolder => Path.GetDirectoryName(Application.dataPath) ?? ".";

        /// <summary>
        /// The launcher: -launcher's, else the install's (&lt;root&gt;\Launcher.exe above versions\&lt;v&gt;\), else the
        /// Launcher.exe every zip ships beside the game (a copy unzipped by hand). Null for a game without one.
        /// </summary>
        static string LauncherPath
        {
            get
            {
                string given = ArgValue("-launcher");
                if (given != null && File.Exists(given)) return given;
                string root = VersionStore.RootOfGame(GameFolder);
                if (root != null) return Path.Combine(root, VersionStore.LauncherExe);
                string beside = Path.Combine(GameFolder, VersionStore.LauncherExe);
                return File.Exists(beside) ? beside : null;
            }
        }

        /// <summary>Where Unity's Player-prev.log is kept while a hand-off run is in the way (see <see cref="KeepPreviousLog"/>).</summary>
        const string KeptLog = "Player-prev-kept.log";

        /// <summary>
        /// Task 4.23 R1. Hands this start to the launcher's quick check and quits, when all of these hold: the launcher
        /// didn't start it (no -updatechecked); offline mode, -offline and the Check for updates switch don't rule it out;
        /// it's a player session with a connection; it's a release build (or -asversion); and a launcher is found
        /// (<see cref="LauncherPath"/>). The launcher gets --game &lt;this exe&gt; --gameversion &lt;v&gt; and this run's
        /// arguments, and starts the game again with -updatechecked (no update, or GitHub unreachable), or asks about the
        /// update. If it can't be started, this run just plays. Returns true when this run is quitting.
        /// </summary>
        static bool HandOffToLauncher(string[] args)
        {
            if (Application.isEditor) return false;
            string version = InstalledVersion;
            string launcher = null;
            string why = Array.IndexOf(args, "-updatechecked") >= 0 ? "the launcher checked"
                : Array.IndexOf(args, "-offline") >= 0 || DataPreferences.Offline ? "offline mode"
                : !DataPreferences.UpdateCheck ? "Settings › Data › Check for updates is off"
                : NotAPlayerSession.Where(a => a != "-data").Any(a => Array.IndexOf(args, a) >= 0) ? "not a player session"
                : !MountainPlanner.Acquisition.Updates.GameVersion.TryParse(version, out var v) || v.IsPreview ? "a preview build"
                : Application.internetReachability == NetworkReachability.NotReachable ? "no connection"
                : (launcher = LauncherPath) == null ? "no launcher here"
                : null;
            if (why != null)
            {
                Debug.Log($"[Updates] Started directly: {why}.");
                RestorePreviousLog();
                return false;
            }
            string exe = ExePath();
            var line = new System.Text.StringBuilder();
            line.Append("--game ").Append(QuoteArg(exe)).Append(" --gameversion ").Append(QuoteArg(version)).Append(" --");
            for (int i = 1; i < args.Length; i++) line.Append(' ').Append(QuoteArg(args[i]));
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher, line.ToString())
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(launcher) ?? ".",
                });
            }
            catch (Exception e)
            {
                Debug.Log($"[Updates] Couldn't start {launcher} ({e.Message}): starting directly.");
                RestorePreviousLog();
                return false;
            }
            KeepPreviousLog();
            Debug.Log($"[Updates] Handed over to {launcher} {line}; quitting.");
            Application.Quit();
            return true;
        }

        /// <summary>This game's exe: the process's own file, else the folder's SkiAreaDesignChallenge.exe.</summary>
        static string ExePath()
        {
            try
            {
                string own = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(own) && File.Exists(own)) return own;
            }
            catch (Exception) { }
            return Path.Combine(GameFolder, VersionStore.GameExe);
        }

        /// <summary>
        /// Unity moves Player.log to Player-prev.log at every start, so a hand-off run would push the last real session
        /// out of Player-prev.log, which problem reports send (task P3-14). The hand-off run keeps a copy (only if none
        /// is kept yet: a second hand-off before a real run is no session either); the next run that plays puts it back.
        /// </summary>
        static void KeepPreviousLog()
        {
            try
            {
                if (!UnityLogs(out string folder)) return;
                string prev = Path.Combine(folder, "Player-prev.log"), kept = Path.Combine(folder, KeptLog);
                if (File.Exists(prev) && !File.Exists(kept)) File.Copy(prev, kept);
            }
            catch (Exception e) { Debug.Log($"[Updates] Couldn't keep Player-prev.log: {e.Message}"); }
        }

        static void RestorePreviousLog()
        {
            try
            {
                if (!UnityLogs(out string folder)) return;
                string prev = Path.Combine(folder, "Player-prev.log"), kept = Path.Combine(folder, KeptLog);
                if (!File.Exists(kept)) return;
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(kept, prev);
                Debug.Log("[Updates] Player-prev.log is the last session's again (a launcher hand-off ran in between).");
            }
            catch (Exception e) { Debug.Log($"[Updates] Couldn't restore Player-prev.log: {e.Message}"); }
        }

        /// <summary>Unity's log folder, when this run logs to the usual Player.log (not a -logFile of its own).</summary>
        static bool UnityLogs(out string folder)
        {
            string log = Application.consoleLogPath;
            folder = string.IsNullOrEmpty(log) ? null : Path.GetDirectoryName(log);
            return folder != null && string.Equals(Path.GetFileName(log), "Player.log", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One argument quoted the way Windows reads a command line back (CommandLineToArgvW).</summary>
        static string QuoteArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                sb.Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        /// <summary>True when <paramref name="folder"/> is inside the game's install (the launcher's root, or the game folder).</summary>
        static bool InsideInstall(string folder)
        {
            if (Application.isEditor) return false;
            string install = VersionStore.RootOfGame(GameFolder) ?? GameFolder;
            return VersionStore.IsInside(install, folder);
        }

        void WireUpdates()
        {
            Screens.UpdateChosen += StartUpdate;
            Screens.UpdateNewsChosen += OpenReleasePage;
            Screens.UpdateDismissed += () => Debug.Log("[Updates] Not now");
            Screens.SettingsUpdateCheckNowChosen += () => CheckForUpdate(manual: true);
            Screens.SettingsDataShown += ShowUpdateStatus;
        }

        /// <summary>Once the title is in: the automatic check, if nothing rules it out. The notice belongs to the title.</summary>
        void UpdateUpdateCheck()
        {
            if (_updateAuto && Screens.UpdateNoticeShown && !Screens.TitleShown) Screens.ShowUpdateNotice(null, null);
            if (_updateDecided || TitleReadySeconds < 0 || Screens.CoverUp) return;
            _updateDecided = true;
            string why = Array.IndexOf(Args, "-updatechecked") >= 0 ? "the launcher checked"
                : !DataPreferences.UpdateCheck ? "Settings › Data › Check for updates is off"
                : NotAPlayerSession.Where(a => a != "-data").Any(a => Array.IndexOf(Args, a) >= 0) ? "not a player session"
                : null;
            if (why != null) { Debug.Log($"[Updates] No check at start: {why}."); return; }
            _updateAuto = true;
            CheckForUpdate(manual: false);
        }

        async void CheckForUpdate(bool manual)
        {
            if (_updateChecking) return;
            if (Http.NetworkDisabled || OfflineState.Mode == OfflineMode.Setting)
            {
                Debug.Log("[Updates] No check: offline mode.");
                if (manual) Screens.Toast("Offline mode is on, so the game can't check for updates.", 5);
                return;
            }
            _updateChecking = true;
            ShowUpdateStatus();
            string installed = InstalledVersion;
            try
            {
                _updateResult = await Task.Run(() => UpdateCheck.CheckAsync(new GitHubFeed(), installed, CancellationToken.None));
            }
            catch (Exception e)
            {
                _updateResult = new UpdateResult { State = UpdateState.Unreachable, Message = $"Update check failed: {e.Message}" };
            }
            finally { _updateChecking = false; }
            if (this == null) return;
            Debug.Log("[Updates] " + _updateResult.Message);
            ShowUpdateStatus();
            if (_updateResult.State == UpdateState.Available && _updateResult.Latest != null)
            {
                _updateAuto = !manual;
                if (manual || Screens.TitleShown)
                    Screens.ShowUpdateNotice($"{_updateResult.Latest.Version} is out (you have {installed}).",
                                             LauncherPath != null ? "Update" : "Get the installer");
                return;
            }
            if (!manual) return;
            Screens.Toast(_updateResult.State == UpdateState.Unreachable ? "Couldn't reach GitHub to check for updates."
                          : _updateResult.State == UpdateState.Preview ? "This is a preview build: it isn't offered releases."
                          : "You have the latest version.", 5);
        }

        /// <summary>Update: hand over to the launcher (it downloads, checks and installs, then offers to launch the new version).</summary>
        void StartUpdate()
        {
            string launcher = LauncherPath;
            if (launcher == null)
            {
                Debug.Log("[Updates] No launcher here (a hand-unzipped game): opening the release page for the installer.");
                OpenReleasePage();
                return;
            }
            AskBeforeLeaving(() =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launcher, "--update")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(launcher) ?? ".",
                    });
                }
                catch (Exception e)
                {
                    Screens.Toast($"Couldn't start the launcher: {e.Message}", 6);
                    return;
                }
                Debug.Log($"[Updates] Started {launcher} --update; quitting.");
                _quitConfirmed = true;
                Application.Quit();
            });
        }

        void OpenReleasePage()
        {
            string page = _updateResult?.Latest?.PageUrl ?? "";
            if (!page.StartsWith("https://github.com/" + GitHubFeed.Repository + "/", StringComparison.Ordinal))
                page = "https://github.com/" + GitHubFeed.Repository + "/releases/latest";
            Application.OpenURL(page);
        }

        /// <summary>Settings › Data › Version: this version and what the last check found.</summary>
        void ShowUpdateStatus()
        {
            string have = BuildInfo.Current.DisplayVersion;
            string found = _updateChecking ? "Checking GitHub…"
                : _updateResult == null ? ""
                : _updateResult.State == UpdateState.Available ? $"{_updateResult.Latest.Version} is out."
                : _updateResult.State == UpdateState.UpToDate ? "That's the latest."
                : _updateResult.State == UpdateState.Preview ? "A preview build: it isn't offered releases."
                : "GitHub couldn't be reached.";
            Screens.Settings.ShowUpdateStatus(found.Length > 0 ? $"This is {have}. {found}" : $"This is {have}.", !_updateChecking);
        }
    }
}
