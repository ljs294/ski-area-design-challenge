using System;
using MountainPlanner.Presentation;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Settings › Data (task P2-05): where the library lives and offline mode. Kept outside the library itself
    /// (it says where the library is), in the same store as the other settings. The app flow applies them.
    /// </summary>
    public static class DataPreferences
    {
        const string FolderKey = "LibraryFolder", OfflineKey = "Offline";

        /// <summary>The chosen library folder, or "" for the default (%LOCALAPPDATA%\SkiAreaDesignChallenge).</summary>
        public static string LibraryFolder => SettingsStore.GetString(FolderKey, "");

        public static bool Offline => SettingsStore.GetInt(OfflineKey, 0) != 0;

        /// <summary>Raised after offline mode changes.</summary>
        public static event Action OfflineChanged;

        public static void SetLibraryFolder(string folder) => SettingsStore.SetString(FolderKey, folder ?? "");

        public static void SetOffline(bool on, bool remember = true)
        {
            if (remember) SettingsStore.SetInt(OfflineKey, on ? 1 : 0);
            _session = on;
            OfflineChanged?.Invoke();
        }

        static bool? _session;

        /// <summary>Offline mode in effect: this run's choice (also when not remembered), else the saved one.</summary>
        public static bool OfflineNow => _session ?? Offline;

        /// <summary>Forgets this run's choice, so the saved one counts again (tests).</summary>
        internal static void Reload() => _session = null;
    }
}
