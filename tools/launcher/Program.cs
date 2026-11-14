using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using MountainPlanner.Acquisition.Updates;

namespace MountainPlanner.Launcher
{
    /// <summary>
    /// The command line (task 4.23). Friends never type any of it: the Start-menu shortcut runs Launcher.exe bare, and
    /// the game exe adds --game itself (R1).
    ///   --game EXE         the game exe that handed this start over: check, then start it again (or offer the update)
    ///   --gameversion V    that exe's version (it says, since a Unity exe's file version isn't the game's)
    ///   --installroot DIR  install somewhere else (demos and tests: a scratch folder, never the real install)
    ///   --version X.Y.Z    install and play that release (demos: start from an older one)
    ///   --feed DIR         read releases from a local folder instead of GitHub (tests)
    ///   --update           the game's Update button: check even if the Settings switch is off, and show the result
    ///   --choose           show the window even with nothing to update (also: hold Shift while it starts)
    ///   --uninstall        remove the install (never the saves)
    ///   --no-shortcuts     no Start-menu or desktop shortcuts (demos)
    ///   --no-start         install but don't start the game (tests)
    ///   --yes              answer the install or update question with yes (demos and tests)
    ///   -- ARGS            passed to the game
    /// </summary>
    sealed class Options
    {
        public string Root = "";
        public bool RootGiven;
        public string? Version, Feed, Game, GameVersionText;
        public bool Update, Choose, Uninstall, NoShortcuts, NoStart, Yes;
        public readonly List<string> GameArgs = new List<string>();
        public string[] Raw = Array.Empty<string>();

        public static Options Parse(string[] args)
        {
            var o = new Options { Raw = args };
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
                switch (a.ToLowerInvariant())
                {
                    case "--installroot": o.Root = Path.GetFullPath(Next()); o.RootGiven = true; break;
                    case "--version": o.Version = Next(); if (!GameVersion.TryParse(o.Version, out _)) throw new ArgumentException($"{o.Version} isn't a version such as 0.3.2."); break;
                    case "--feed": o.Feed = Path.GetFullPath(Next()); break;
                    case "--game": o.Game = Path.GetFullPath(Next()); break;
                    case "--gameversion": o.GameVersionText = Next(); break;
                    case "--update": o.Update = true; break;
                    case "--choose": o.Choose = true; break;
                    case "--uninstall": o.Uninstall = true; break;
                    case "--no-shortcuts": o.NoShortcuts = true; break;
                    case "--no-start": o.NoStart = true; break;
                    case "--yes": o.Yes = true; break;
                    case "--":
                        for (i++; i < args.Length; i++) o.GameArgs.Add(args[i]);
                        break;
                    default: throw new ArgumentException($"Unknown option {a}.");
                }
            }
            if (!o.RootGiven) o.Root = DefaultRoot();
            if ((Control.ModifierKeys & Keys.Shift) != 0) o.Choose = true;
            return o;
        }

        /// <summary>The folder this exe runs from when it is an install root (it holds versions\), else the default.</summary>
        static string DefaultRoot()
        {
            string here = Path.GetDirectoryName(Program.ExePath) ?? "";
            return Directory.Exists(Path.Combine(here, "versions")) ? here : VersionStore.DefaultRoot;
        }
    }

    static class Program
    {
        public static string ExePath => Assembly.GetExecutingAssembly().Location;

        /// <summary>This launcher's version (0.0.0 for a build outside release.ps1).</summary>
        public static GameVersion OwnVersion => VersionOf(ExePath);

        public static GameVersion VersionOf(string exe)
        {
            try
            {
                string? text = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
                return GameVersion.TryParse(text, out var v) ? v : new GameVersion(0, 0, 0);
            }
            catch (Exception) { return new GameVersion(0, 0, 0); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Options o;
            try { o = Options.Parse(args); }
            catch (ArgumentException e)
            {
                MessageBox.Show(e.Message, "Ski Area Design Challenge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
            // One launcher at a time. The game's Update button starts a new one while the old one waits for the game to
            // close, so wait a little for it. A game exe handing over (--game) doesn't wait long: if another launcher is
            // busy, the game just starts.
            using (var mutex = new Mutex(false, @"Local\AlpineLabs.SkiAreaDesignChallenge.Launcher"))
            {
                bool mine;
                try { mine = mutex.WaitOne(TimeSpan.FromSeconds(o.Game != null ? 3 : 30)); }
                catch (AbandonedMutexException) { mine = true; }
                if (!mine) return o.Game != null && StartGame(o) ? 0 : 1;
                try
                {
                    Log.Open(o.Root);
                    Log.Write($"Launcher {OwnVersion} at {ExePath}; install root {o.Root}; args: {string.Join(" ", args)}");
                    // The window is made now but shown only when a screen needs it: with no update, nothing appears (R2).
                    var form = new LauncherForm(o);
                    var context = new ApplicationContext();
                    form.FormClosed += (s, e) => context.ExitThread();
                    _ = form.Handle;
                    EventHandler? once = null;
                    once = (s, e) => { Application.Idle -= once; form.Begin(); };
                    Application.Idle += once;
                    Application.Run(context);
                    Log.Write($"Exit {form.ExitCode}");
                    return form.ExitCode;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        /// <summary>
        /// --game: starts the exe that handed over, with -updatechecked (so it doesn't hand over again) and -launcher
        /// (its Update button), plus the arguments it was started with. False when Windows couldn't start it.
        /// </summary>
        public static bool StartGame(Options o)
        {
            if (o.Game == null) return false;
            var args = new[] { "-updatechecked", "-launcher", ExePath }.Concat(o.GameArgs).ToArray();
            try
            {
                Log.Write($"Starting {o.Game} {Quote(args)}");
                Process.Start(new ProcessStartInfo(o.Game, Quote(args)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(o.Game) ?? "." });
                return true;
            }
            catch (Exception e)
            {
                Log.Write($"Start failed: {e.Message}");
                return false;
            }
        }

        /// <summary>A Windows command line from arguments, quoted the way CommandLineToArgvW reads them back.</summary>
        public static string Quote(string[] args) => string.Join(" ", args.Select(QuoteOne));

        static string QuoteOne(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
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
    }

    /// <summary>
    /// What the launcher did, in &lt;install root&gt;\launcher.log, rewritten each run (a log, not a state file: nothing
    /// reads it back). Before the install root exists it goes to %TEMP%.
    /// </summary>
    static class Log
    {
        static string? _path;

        public static string? Path => _path;

        public static void Open(string root)
        {
            string folder = Directory.Exists(root) ? root : System.IO.Path.GetTempPath();
            _path = System.IO.Path.Combine(folder, Directory.Exists(root) ? "launcher.log" : "SkiAreaDesignChallenge-launcher.log");
            try { File.WriteAllText(_path, "", new UTF8Encoding(false)); } catch (Exception) { _path = null; }
        }

        /// <summary>Moves the log into the install root once it exists.</summary>
        public static void MoveTo(string root)
        {
            if (_path == null || !Directory.Exists(root)) return;
            string target = System.IO.Path.Combine(root, "launcher.log");
            if (string.Equals(_path, target, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                File.Copy(_path, target, true);
                File.Delete(_path);
                _path = target;
            }
            catch (Exception) { }
        }

        public static void Write(string line)
        {
            if (_path == null) return;
            try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\r\n", new UTF8Encoding(false)); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Two of the game's Settings › Data switches, read from where Unity keeps PlayerPrefs (HKCU\Software\Alpine
    /// Labs\Ski Area Design Challenge, values MountainPlanner.&lt;name&gt;_h&lt;hash&gt;), so the game and the launcher
    /// agree: Check for updates (task 4.23, UP7; on unless turned off) and Offline mode (off unless turned on).
    /// </summary>
    static class UpdatePrefs
    {
        const string Key = @"Software\Alpine Labs\Ski Area Design Challenge";

        public static bool CheckEnabled() => Read("UpdateCheck") != 0;

        public static bool Offline() => Read("Offline") == 1;

        /// <summary>The value, or -1 when the player never set it.</summary>
        static int Read(string name)
        {
            string prefix = "MountainPlanner." + name + "_h";
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (key == null) return -1;
                    foreach (string value in key.GetValueNames())
                        if (value.StartsWith(prefix, StringComparison.Ordinal) && key.GetValue(value) is int v) return v;
                }
            }
            catch (Exception e) { Log.Write($"Couldn't read the game's {name} setting: {e.Message}"); }
            return -1;
        }
    }

    /// <summary>Start-menu and desktop shortcuts to the installed launcher (Windows Script Host, part of Windows).</summary>
    static class Shortcuts
    {
        public const string Name = "Ski Area Design Challenge";

        static string StartFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name);
        static string Desktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

        public static void Create(string launcher, bool desktop)
        {
            Directory.CreateDirectory(StartFolder);
            Make(Path.Combine(StartFolder, Name + ".lnk"), launcher, "", "Play Ski Area Design Challenge");
            Make(Path.Combine(StartFolder, "Uninstall " + Name + ".lnk"), launcher, "--uninstall", "Remove Ski Area Design Challenge (your saves stay)");
            if (desktop) Make(Desktop, launcher, "", "Play Ski Area Design Challenge");
        }

        public static void Remove()
        {
            try { if (Directory.Exists(StartFolder)) Directory.Delete(StartFolder, true); } catch (Exception e) { Log.Write($"Start menu: {e.Message}"); }
            try { if (File.Exists(Desktop)) File.Delete(Desktop); } catch (Exception e) { Log.Write($"Desktop: {e.Message}"); }
        }

        static void Make(string path, string target, string arguments, string description)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = target;
                link.Arguments = arguments;
                link.WorkingDirectory = Path.GetDirectoryName(target);
                link.Description = description;
                link.IconLocation = target + ",0";
                link.Save();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
    }
}
