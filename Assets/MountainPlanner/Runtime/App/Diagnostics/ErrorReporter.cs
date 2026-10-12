using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Persistence;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MountainPlanner.App.Diagnostics
{
    /// <summary>
    /// The crash and error log a tester can send (task P2-10). Every exception, error and assert Unity reports, from any
    /// thread, plus unobserved task exceptions, goes to this session's <see cref="ErrorLog"/> under
    /// %LOCALAPPDATA%\SkiAreaDesignChallenge\Logs (the data root, so it doesn't move with the company name), headed by the
    /// version, commit, machine and settings. The session's first error raises <see cref="FirstError"/> on the main
    /// thread, once, for the notice. A hard native crash can't be caught from C#: the header points at Player.log and
    /// Unity's own crash folder for those.
    ///
    /// Players install it before the first scene; the editor doesn't (its errors are the developer's console). Flags:
    /// -errorlogs &lt;folder&gt; writes elsewhere (the release smoke test); -crashtest forces a test exception on the main
    /// thread and one on a worker once the title is in; -smoketest does that, then quits with 0 when both reached the log
    /// and the title came from the built-in demo, else 3.
    /// </summary>
    public sealed class ErrorReporter : MonoBehaviour
    {
        public const string ForcedMessage = "Forced test exception (-crashtest)";
        /// <summary>The smoke test gives up after this long: the title never came in.</summary>
        public const float SmokeTimeoutSeconds = 120;

        static ErrorLog _log;
        static readonly Stopwatch Clock = new Stopwatch();
        static volatile bool _firstPending;
        static volatile string _header = "", _display = "";
        static ErrorReporter _pump;

        /// <summary>The session's first error, on the main thread, with its log file.</summary>
        public static event Action<string> FirstError;

        /// <summary>The folder this session logs to (null before <see cref="Install"/>).</summary>
        public static string Folder { get; private set; }

        /// <summary>This session's log file, or null while there's been no error.</summary>
        public static string LogPath => _log?.Path;

        /// <summary>Errors recorded this session.</summary>
        public static int Count => _log?.Count ?? 0;

        int _width, _height;
        FullScreenMode _mode;
        bool _crashTest, _smokeTest, _forced;
        float _smokeStart;

        // Hooked as early as possible, so even errors in other startup code reach the log; the main-thread pump and
        // the header follow before the first scene.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Boot()
        {
            if (Application.isEditor) return;
            PrefsMigration.Run();   // before anything reads PlayerPrefs (Alpine Labs, task P2-10)
            string[] args = Environment.GetCommandLineArgs();
            Install(Arg(args, "-errorlogs") ?? ErrorLog.FolderIn(MountainViewer.DataRoot), header: false);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void StartPump()
        {
            if (_log == null) return;
            RefreshHeader();
            PrefsMigration.ApplyWindowMode();
            CreatePump();
            string[] args = Environment.GetCommandLineArgs();
            _pump._smokeTest = Array.IndexOf(args, "-smoketest") >= 0;
            _pump._crashTest = _pump._smokeTest || Array.IndexOf(args, "-crashtest") >= 0;
            var build = BuildInfo.Current;
            Debug.Log($"[MountainPlanner] {Application.productName} {build.DisplayVersion} (commit {build.ShortCommit}{(build.dirty ? ", uncommitted changes" : "")}, built {build.builtUtc}); error logs go to {Folder}");
        }

        /// <summary>
        /// Starts logging into <paramref name="folder"/> (players at boot; tests with a scratch folder). At boot it's too
        /// early for the header's Unity calls: <paramref name="header"/> false leaves them to <see cref="RefreshHeader"/>.
        /// </summary>
        public static void Install(string folder, bool header = true)
        {
            Uninstall();
            Folder = folder;
            Clock.Restart();
            _header = Application.productName + " - error log (an error came before the game had started; no details)\r\n";
            _display = "";
            if (header) RefreshHeader();
            _log = new ErrorLog(folder, ErrorLog.Stamp(DateTime.UtcNow), Header);
            Application.logMessageReceivedThreaded += OnLog;
            TaskScheduler.UnobservedTaskException += OnUnobserved;
            Application.quitting += Uninstall;
        }

        /// <summary>The main-thread object that raises <see cref="FirstError"/> and keeps the display line current (tests create it too).</summary>
        public static ErrorReporter CreatePump()
        {
            if (_pump != null) return _pump;
            var go = new GameObject("Error reporter");
            if (Application.isPlaying) DontDestroyOnLoad(go);
            _pump = go.AddComponent<ErrorReporter>();
            return _pump;
        }

        /// <summary>Gathers the header's details (main thread): version, machine, settings.</summary>
        public static void RefreshHeader()
        {
            _header = BaseHeader();
            _display = DisplayLine();
        }

        /// <summary>Stops logging and closes the file (quitting, tests).</summary>
        public static void Uninstall()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            Application.quitting -= Uninstall;
            _log?.Dispose();
            _log = null;
            _firstPending = false;
        }

        static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            Record(type.ToString(), condition, stack);
        }

        static void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e)
        {
            var inner = e.Exception?.InnerException ?? e.Exception;
            Record("Unobserved task exception", inner == null ? "" : $"{inner.GetType().Name}: {inner.Message}", e.Exception?.ToString() ?? "");
        }

        static void Record(string kind, string message, string stack)
        {
            var log = _log;
            if (log == null) return;
            if (log.Write(new ErrorEntry(kind, Private(message), Private(stack), DateTime.UtcNow, Clock.Elapsed.TotalSeconds))) _firstPending = true;
        }

        void Update()
        {
            if (_width != Screen.width || _height != Screen.height || _mode != Screen.fullScreenMode)
            {
                _width = Screen.width;
                _height = Screen.height;
                _mode = Screen.fullScreenMode;
                _display = DisplayLine();
            }
            if (_firstPending && _log?.Path != null)
            {
                _firstPending = false;
                FirstError?.Invoke(_log.Path);
            }
            if (_crashTest) CrashTest();
        }

        /// <summary>-crashtest and -smoketest: once the title is in (or a few seconds after a run without one).</summary>
        void CrashTest()
        {
            if (_smokeStart == 0) _smokeStart = Time.realtimeSinceStartup;
            float waited = Time.realtimeSinceStartup - _smokeStart;
            bool titleIn = Flow.AppFlow.TitleReadySeconds >= 0 || (Flow.AppFlow.Instance == null && waited > 5);
            if (!_forced && titleIn)
            {
                _forced = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { throw new InvalidOperationException(ForcedMessage + " on a worker thread"); }
                    catch (Exception e) { Debug.LogException(e); }
                });
                throw new InvalidOperationException(ForcedMessage);
            }
            if (!_smokeTest) return;
            bool logged = _forced && (_log?.Count ?? 0) >= 2;
            if (logged || waited > SmokeTimeoutSeconds)
            {
                _smokeTest = _crashTest = false;
                bool demo = Flow.AppFlow.TitleFromBundledDemo;
                bool ok = logged && demo;
                Debug.Log($"[Smoke] {(ok ? "ok" : "FAILED")}: version {BuildInfo.Current.DisplayVersion}, title in at {Flow.AppFlow.TitleReadySeconds:F1} s " +
                          $"({(demo ? "built-in demo" : "not the built-in demo")}), {Count} errors logged to {LogPath ?? "(no file)"}");
                _log?.Flush();
                Application.Quit(ok ? 0 : 3);
            }
        }

        /// <summary>The log's header: built once when the first error opens the file, from strings gathered on the main thread.</summary>
        static string Header()
        {
            return _display.Length == 0 ? _header : _header + "Display".PadRight(12) + _display + "\r\n";
        }

        static string BaseHeader()
        {
            var build = BuildInfo.Current;
            var text = new StringBuilder(1024);
            void Line(string name, string value) => text.Append(name.PadRight(12)).Append(value).Append("\r\n");
            text.Append(Application.productName).Append(' ').Append(build.DisplayVersion).Append(" - error log\r\n");
            text.Append("Send this file with your bug report. It holds nothing about you beyond what's listed here; your user folder reads %USERPROFILE%.\r\n\r\n");
            Line("Version", $"{build.DisplayVersion} (commit {build.commit}{(build.dirty ? ", uncommitted changes" : "")}, built {build.builtUtc})");
            Line("Session", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            Line("Windows", SystemInfo.operatingSystem);
            Line("CPU", $"{SystemInfo.processorType}, {SystemInfo.processorCount} threads");
            Line("Memory", $"{SystemInfo.systemMemorySize / 1024.0:0.#} GB");
            Line("GPU", $"{SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsMemorySize / 1024.0:0.#} GB, {SystemInfo.graphicsDeviceVersion}");
            Line("Quality", QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "");
            Line("Command", Environment.CommandLine);
            Line("Player.log", Application.consoleLogPath);
            Line("Crashes", Path.Combine(Path.GetTempPath(), Application.companyName, Application.productName, "Crashes") + " (Unity's own, after a hard crash)");
            return Private(text.ToString());
        }

        static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>
        /// The tester's Windows user name stays out of the file they send: paths under their profile read %USERPROFILE%
        /// (both slash styles; Unity reports some paths with forward slashes).
        /// </summary>
        public static string Private(string text)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(Profile)) return text;
            return text.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                       .Replace(Profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        static string DisplayLine()
        {
            var r = Screen.currentResolution;
            return $"{Screen.width}x{Screen.height} {Screen.fullScreenMode}, desktop {r.width}x{r.height} at {r.refreshRateRatio.value:0.#} Hz";
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
