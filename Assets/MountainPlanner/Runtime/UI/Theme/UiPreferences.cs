using System;
using UnityEngine;

namespace MountainPlanner.UI
{
    /// <summary>The game's two UI themes (game-ui-direction.md UI-6): warm graphite or sign white.</summary>
    public enum UiTheme { Dark, Light }

    /// <summary>The player's theme setting: one of the themes, or Auto, which follows the sun (light by day, dark at night).</summary>
    public enum UiThemeChoice { Dark, Light, Auto }

    /// <summary>
    /// The interface settings every screen shares (0.4 §1 and S8 Interface; task P2-01): the theme (dark, light or
    /// auto by the sun, as the accepted mockup's switch) and the UI scale, 50–150% in 5% steps. Remembered between
    /// sessions; <see cref="UiPanels"/> applies them to every panel. The command line can set them for one run
    /// without remembering them: -theme dark|light|auto and -uiscale 50..150 (captures and tests).
    /// </summary>
    public static class UiPreferences
    {
        public const int MinScalePercent = 50, MaxScalePercent = 150, ScaleStepPercent = 5;
        /// <summary>The interface scale until the player picks one: 85% of the mockup's size (owner, 2026-10-06, task P2-02).</summary>
        public const int DefaultScalePercent = 85;
        const string ThemeKey = "MountainPlanner.UiTheme", ScaleKey = "MountainPlanner.UiScale";

        static bool _loaded, _daylight = true;
        static UiThemeChoice _choice = UiThemeChoice.Dark;
        static int _scale = DefaultScalePercent;

        /// <summary>Raised after the theme or the scale changes.</summary>
        public static event Action Changed;

        /// <summary>The theme in effect: the chosen one, or under Auto light while the sun is up and dark otherwise.</summary>
        public static UiTheme Theme => ThemeFor(Choice, _daylight);

        /// <summary>What the player chose: Dark, Light or Auto.</summary>
        public static UiThemeChoice Choice
        {
            get
            {
                if (!_loaded) Load();
                return _choice;
            }
        }

        /// <summary>True while the in-game sun is up (the viewer reports it; the title and menus count as day).</summary>
        public static bool Daylight => _daylight;

        public static UiTheme ThemeFor(UiThemeChoice choice, bool daylight) =>
            choice == UiThemeChoice.Light || (choice == UiThemeChoice.Auto && daylight) ? UiTheme.Light : UiTheme.Dark;

        public static bool Dark => Theme == UiTheme.Dark;

        /// <summary>The UI scale in percent, 50–150.</summary>
        public static int ScalePercent
        {
            get
            {
                if (!_loaded) Load();
                return _scale;
            }
        }

        public static float Scale => ScalePercent / 100f;

        /// <summary>A scale in percent, snapped to the 5% steps and kept within 50–150%.</summary>
        public static int SnapScale(int percent) =>
            Mathf.Clamp(Mathf.RoundToInt(percent / (float)ScaleStepPercent) * ScaleStepPercent, MinScalePercent, MaxScalePercent);

        static void Load()
        {
            _loaded = true;
            _choice = Parse(PlayerPrefs.GetInt(ThemeKey, (int)UiThemeChoice.Dark));
            _scale = SnapScale(PlayerPrefs.GetInt(ScaleKey, DefaultScalePercent));
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-theme");
            if (i >= 0 && i + 1 < args.Length) _choice = args[i + 1] == "light" ? UiThemeChoice.Light : args[i + 1] == "auto" ? UiThemeChoice.Auto : UiThemeChoice.Dark;
            i = Array.IndexOf(args, "-uiscale");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int s)) _scale = SnapScale(s);
        }

        static UiThemeChoice Parse(int value) => value == (int)UiThemeChoice.Light ? UiThemeChoice.Light : value == (int)UiThemeChoice.Auto ? UiThemeChoice.Auto : UiThemeChoice.Dark;

        /// <summary>Sets the theme choice; <paramref name="remember"/> false for tests and captures, so the player's choice is untouched.</summary>
        public static void SetChoice(UiThemeChoice choice, bool remember = true)
        {
            if (!_loaded) Load();
            if (choice == _choice) return;
            _choice = choice;
            if (remember) Save(ThemeKey, (int)choice);
            Changed?.Invoke();   // also when the theme in effect stays the same: the switch shows the choice
        }

        /// <summary>Chooses one theme outright (not Auto).</summary>
        public static void SetTheme(UiTheme theme, bool remember = true) =>
            SetChoice(theme == UiTheme.Light ? UiThemeChoice.Light : UiThemeChoice.Dark, remember);

        /// <summary>Dark, then Light, then Auto, then Dark again.</summary>
        public static void ToggleTheme() => SetChoice((UiThemeChoice)(((int)Choice + 1) % 3));

        /// <summary>
        /// The viewer reports whether the sun is up; under Auto the theme follows it. Cheap to call every frame: it
        /// does nothing unless the answer changed.
        /// </summary>
        public static void SetDaylight(bool up)
        {
            if (up == _daylight) return;
            var was = Theme;
            _daylight = up;
            if (Theme != was) Changed?.Invoke();
        }

        /// <summary>Sets the UI scale in percent (snapped to 5% steps, 50–150%).</summary>
        public static void SetScale(int percent, bool remember = true)
        {
            if (!_loaded) Load();
            percent = SnapScale(percent);
            if (percent == _scale) return;
            _scale = percent;
            if (remember) Save(ScaleKey, percent);
            Changed?.Invoke();
        }

        /// <summary>One 5% step up (+1) or down (−1).</summary>
        public static void StepScale(int steps) => SetScale(ScalePercent + steps * ScaleStepPercent);

        static void Save(string key, int value)
        {
            PlayerPrefs.SetInt(key, value);
            PlayerPrefs.Save();
        }
    }
}
