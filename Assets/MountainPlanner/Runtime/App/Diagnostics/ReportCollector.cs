using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Reports;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.Presentation;
using UnityEngine;

namespace MountainPlanner.App.Diagnostics
{
    /// <summary>
    /// Fills a problem report from the running game (task P3-14): the version and commit, the machine and display, every
    /// setting the player can change, the area and save that are open, and where the logs, crash folders and save live.
    /// The engine-free <see cref="ReportBundle"/> then writes it. Main thread only (SystemInfo, Screen, PlayerPrefs).
    /// </summary>
    public static class ReportCollector
    {
        /// <summary>The settings a report carries: the game's own, the HUD's and Unity's window and quality choices. Not Unity's session ids.</summary>
        static readonly Regex SettingNames = new Regex(@"^(MountainPlanner\.|hud\.|Screenmanager |UnityGraphicsQuality)", RegexOptions.CultureInvariant);
        /// <summary>Unity appends _h&lt;hash&gt; to each value name in the registry; PlayerPrefs uses the name without it.</summary>
        static readonly Regex HashSuffix = new Regex(@"_h\d+$", RegexOptions.CultureInvariant);

        public static ProblemReport Report(string kind, string packageFolder, bool afterCrash)
        {
            var build = BuildInfo.Current;
            var report = new ProblemReport
            {
                Kind = kind,
                CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                GameVersion = build.version ?? "",
                DisplayVersion = build.DisplayVersion,
                Commit = build.commit ?? "",
                AfterCrash = afterCrash,
                Area = Area(packageFolder),
            };
            FillSystem(report);
            FillSettings(report);
            return report;
        }

        public static ReportSources Sources(string dataRoot, byte[] screenshot)
        {
            return new ReportSources
            {
                ErrorLogs = ErrorReporter.Folder ?? ErrorLog.FolderIn(dataRoot),
                GameLogs = Path.GetDirectoryName(Application.consoleLogPath) ?? Application.persistentDataPath,
                Crashes = CrashFolder,
                SaveFolder = OpenDesign.Current?.SaveFolder,
                Screenshot = screenshot,
            };
        }

        /// <summary>Where Unity's own crash handler writes after a hard crash.</summary>
        public static string CrashFolder => Path.Combine(Path.GetTempPath(), Application.companyName, Application.productName, "Crashes");

        public static ReportPrivacy Privacy => new ReportPrivacy(Environment.UserName, Environment.MachineName);

        static ReportArea Area(string packageFolder)
        {
            if (string.IsNullOrEmpty(packageFolder)) return null;
            var area = new ReportArea { PackageId = OpenDesign.Current?.PackageId ?? "", SaveName = OpenDesign.Current?.SaveName ?? "" };
            try
            {
                var m = ResortPackage.ReadManifest(packageFolder);
                area.PackageId = m.PackageId;
                area.Name = m.Site.Name;
                area.Latitude = m.Site.Latitude;
                area.Longitude = m.Site.Longitude;
                area.SizeMetres = m.Site.SizeMetres;
                area.Attribution.AddRange(m.Attribution);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Debug.LogWarning($"[Report] The area's manifest couldn't be read: {e.Message}");
            }
            return area;
        }

        static void FillSystem(ProblemReport report)
        {
            var s = report.System;
            s[SystemKeys.Gpu] = SystemInfo.graphicsDeviceName;
            s[SystemKeys.Driver] = SystemInfo.graphicsDeviceVersion;
            s[SystemKeys.Vram] = $"{SystemInfo.graphicsMemorySize / 1024.0:0.#} GB";
            s[SystemKeys.Cpu] = $"{SystemInfo.processorType}, {SystemInfo.processorCount} threads";
            s[SystemKeys.Memory] = $"{SystemInfo.systemMemorySize / 1024.0:0.#} GB";
            s[SystemKeys.Windows] = SystemInfo.operatingSystem;
            var r = Screen.currentResolution;
            s[SystemKeys.Display] = $"{Screen.width}x{Screen.height} {Screen.fullScreenMode}, desktop {r.width}x{r.height} at {r.refreshRateRatio.value:0.#} Hz, {Screen.dpi:0} dpi";
            s[SystemKeys.Scale] = $"{UI.UiPreferences.ScalePercent}%";
            s[SystemKeys.Theme] = $"{UI.UiPreferences.Choice} ({UI.UiPreferences.Theme})";
            s[SystemKeys.Quality] = QualitySettings.names.Length > 0 ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "";
            s["Command line"] = string.Join(" ", Environment.GetCommandLineArgs(), 1, Math.Max(0, Environment.GetCommandLineArgs().Length - 1));
        }

        /// <summary>The player's settings as PlayerPrefs keeps them, read from the registry key Unity uses on Windows.</summary>
        static void FillSettings(ProblemReport report)
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return;
            const int Binary = 3;
            foreach (var (raw, type) in PrefsMigration.ListValues(PrefsMigration.KeyPath(Application.companyName, Application.productName)))
            {
                string name = HashSuffix.Replace(raw, "");
                if (name == raw || !SettingNames.IsMatch(name) || name.StartsWith(ReportPrefix, StringComparison.Ordinal)) continue;   // no _h: not a PlayerPrefs value
                if (type == Binary) { report.Settings[name] = "s:" + PlayerPrefs.GetString(name, ""); continue; }
                const int Missing = int.MinValue + 7;
                int i = PlayerPrefs.GetInt(name, Missing);
                if (i != Missing) report.Settings[name] = "i:" + i.ToString(CultureInfo.InvariantCulture);
                else report.Settings[name] = "f:" + PlayerPrefs.GetFloat(name, 0).ToString("R", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>The prefix the reported settings live under while a report is open (decision PR9): the owner's own stay untouched.</summary>
        public const string ReportPrefix = "MountainPlanner.Report.";

        /// <summary>
        /// -reportsettings &lt;report.json&gt;: the session reads its settings from the report. They're written under
        /// <see cref="ReportPrefix"/>, never over the owner's, and only the game's own (not Unity's window keys).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ApplyReportedSettings()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-reportsettings");
            if (at < 0 || at + 1 >= args.Length) return;
            try
            {
                var report = ProblemReport.Parse(File.ReadAllText(args[at + 1]));
                SettingsStore.Prefix = ReportPrefix;
                foreach (var (raw, _) in PrefsMigration.ListValues(PrefsMigration.KeyPath(Application.companyName, Application.productName)))
                {
                    string old = HashSuffix.Replace(raw, "");
                    if (old != raw && old.StartsWith(ReportPrefix, StringComparison.Ordinal)) PlayerPrefs.DeleteKey(old);   // the last report's
                }
                int applied = 0;
                foreach (var pair in report.Settings)
                {
                    if (!pair.Key.StartsWith(SettingsStore.DefaultPrefix, StringComparison.Ordinal) || pair.Value.Length < 2 || pair.Value[1] != ':') continue;
                    string key = ReportPrefix + pair.Key.Substring(SettingsStore.DefaultPrefix.Length);
                    string value = pair.Value.Substring(2);
                    switch (pair.Value[0])
                    {
                        case 'i' when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i): PlayerPrefs.SetInt(key, i); break;
                        case 'f' when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f): PlayerPrefs.SetFloat(key, f); break;
                        case 's': PlayerPrefs.SetString(key, value); break;
                        default: continue;
                    }
                    applied++;
                }
                // The library folder is the scratch -data folder, never the reporter's.
                PlayerPrefs.DeleteKey(ReportPrefix + "LibraryFolder");
                PlayerPrefs.Save();
                Debug.Log($"[Report] {applied} settings from {args[at + 1]} apply to this session only (under {ReportPrefix}).");
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is Newtonsoft.Json.JsonException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[Report] The reported settings couldn't be read: {e.Message}");
            }
        }
    }
}
