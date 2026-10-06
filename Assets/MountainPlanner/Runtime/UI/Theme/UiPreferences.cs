using System;
using UnityEngine;

namespace MountainPlanner.UI
{
    /// <summary>The game's two UI themes (game-ui-direction.md UI-6): warm graphite or sign white.</summary>
    public enum UiTheme { Dark, Light }

    /// <summary>
    /// The interface settings every screen shares (0.4 §1 and S8 Interface; task P2-01): the theme and the UI scale,
    /// 50–150% in 5% steps. Remembered between sessions; <see cref="UiPanels"/> applies them to every panel.
    /// The command line can set them for one run without remembering them: -theme dark|light and -uiscale 50..150
    /// (captures and tests).
    /// </summary>
    public static class UiPreferences
    {
        public const int MinScalePercent = 50, MaxScalePercent = 150, ScaleStepPercent = 5;
        const string ThemeKey = "MountainPlanner.UiTheme", ScaleKey = "MountainPlanner.UiScale";

        static bool _loaded;
        static UiTheme _theme = UiTheme.Dark;
        static int _scale = 100;

        /// <summary>Raised after the theme or the scale changes.</summary>
        public static event Action Changed;

        public static UiTheme Theme
        {
            get
            {
                if (!_loaded) Load();
                return _theme;
            }
        }

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
            _theme = PlayerPrefs.GetInt(ThemeKey, (int)UiTheme.Dark) == (int)UiTheme.Light ? UiTheme.Light : UiTheme.Dark;
            _scale = SnapScale(PlayerPrefs.GetInt(ScaleKey, 100));
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-theme");
            if (i >= 0 && i + 1 < args.Length) _theme = args[i + 1] == "light" ? UiTheme.Light : UiTheme.Dark;
            i = Array.IndexOf(args, "-uiscale");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int s)) _scale = SnapScale(s);
        }

        /// <summary>Sets the theme; <paramref name="remember"/> false for tests and captures, so the player's choice is untouched.</summary>
        public static void SetTheme(UiTheme theme, bool remember = true)
        {
            if (!_loaded) Load();
            if (theme == _theme) return;
            _theme = theme;
            if (remember) Save(ThemeKey, (int)theme);
            Changed?.Invoke();
        }

        public static void ToggleTheme() => SetTheme(Dark ? UiTheme.Light : UiTheme.Dark);

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
