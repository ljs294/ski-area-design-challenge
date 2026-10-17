using System;
using MountainPlanner.Presentation;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Settings › Gameplay (task P3-04; decision SV2): how often the game autosaves. A setting in the same store as the
    /// others, not a frozen format. The app flow reads it each time it checks for an autosave.
    /// </summary>
    public static class GameplayPreferences
    {
        const string AutosaveKey = "AutosaveMinutes";
        /// <summary>The mockup's steps: Off, then every 5, 10, 15 or 30 minutes.</summary>
        public static readonly int[] AutosaveChoices = { 0, 5, 10, 15, 30 };
        public const int AutosaveDefault = 10;

        static int? _session;

        /// <summary>Minutes between autosaves, 0 for off: this run's choice (also when not remembered), else the saved one.</summary>
        public static int AutosaveMinutes
        {
            get
            {
                int m = _session ?? SettingsStore.GetInt(AutosaveKey, AutosaveDefault);
                return Array.IndexOf(AutosaveChoices, m) >= 0 ? m : AutosaveDefault;
            }
        }

        public static void SetAutosave(int minutes, bool remember = true)
        {
            if (Array.IndexOf(AutosaveChoices, minutes) < 0) minutes = AutosaveDefault;
            _session = minutes;
            if (remember) SettingsStore.SetInt(AutosaveKey, minutes);
        }
    }
}
