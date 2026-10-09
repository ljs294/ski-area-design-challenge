using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Where the Settings window's choices are remembered (task P2-05): Unity's PlayerPrefs, under
    /// <c>MountainPlanner.*</c> keys, beside the theme, scale and units that were already there. Settings are not a
    /// frozen format (0.3 §5): a value that doesn't parse falls back to its default. Tests point
    /// <see cref="Prefix"/> elsewhere so the player's own settings stay untouched.
    /// </summary>
    public static class SettingsStore
    {
        public const string DefaultPrefix = "MountainPlanner.";
        public static string Prefix = DefaultPrefix;

        public static int GetInt(string key, int fallback) => PlayerPrefs.GetInt(Prefix + key, fallback);
        public static float GetFloat(string key, float fallback) => PlayerPrefs.GetFloat(Prefix + key, fallback);
        public static string GetString(string key, string fallback) => PlayerPrefs.GetString(Prefix + key, fallback);
        public static bool Has(string key) => PlayerPrefs.HasKey(Prefix + key);

        public static void SetInt(string key, int value) { PlayerPrefs.SetInt(Prefix + key, value); PlayerPrefs.Save(); }
        public static void SetFloat(string key, float value) { PlayerPrefs.SetFloat(Prefix + key, value); PlayerPrefs.Save(); }
        public static void SetString(string key, string value) { PlayerPrefs.SetString(Prefix + key, value); PlayerPrefs.Save(); }
        public static void Delete(string key) { PlayerPrefs.DeleteKey(Prefix + key); PlayerPrefs.Save(); }

        /// <summary>True when the command line chose the quality outright (-quality, -benchmark): saved graphics, display and FOV choices are left alone for that run.</summary>
        public static bool CommandLineQuality(string[] args) =>
            System.Array.IndexOf(args, "-quality") >= 0 || System.Array.IndexOf(args, "-benchmark") >= 0;
    }
}
