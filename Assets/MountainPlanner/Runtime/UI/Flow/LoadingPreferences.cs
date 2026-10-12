using System;
using MountainPlanner.Presentation;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Whether the loading box shows its Details list (task P2-03.1): the player's last choice, remembered through
    /// <see cref="SettingsStore"/> (not a frozen format). Off by default. -loadingdetails on|off chooses for one run
    /// (captures), without changing the saved choice.
    /// </summary>
    public static class LoadingPreferences
    {
        const string Key = "LoadingDetails";

        static bool _loaded, _details;

        public static event Action Changed;

        public static bool Details
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    string[] args = Environment.GetCommandLineArgs();
                    int i = Array.IndexOf(args, "-loadingdetails");
                    _details = i >= 0 && i + 1 < args.Length ? args[i + 1] == "on" : SettingsStore.GetInt(Key, 0) != 0;
                }
                return _details;
            }
        }

        public static void SetDetails(bool on, bool remember = true)
        {
            if (on == Details) return;
            _details = on;
            if (remember) SettingsStore.SetInt(Key, on ? 1 : 0);
            Changed?.Invoke();
        }

        /// <summary>Forgets what was read, so the next use reads the store again (tests).</summary>
        internal static void Reload() => _loaded = false;
    }
}
