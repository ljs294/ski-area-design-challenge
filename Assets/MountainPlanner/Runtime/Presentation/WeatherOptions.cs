using System;

namespace MountainPlanner.Presentation
{
    /// <summary>How the clouds are drawn (Settings › Weather › Clouds, task 4.06).</summary>
    public enum CloudLook { Auto, ThreeD, Flat, Off }

    /// <summary>The weather's parts a player can turn off one by one (Settings › Weather, task 4.06).</summary>
    public enum WeatherPart { ValleyFog, Falling, Spindrift, CloudShadows, StormLight, TreeSway, Sparkle, Rays }

    /// <summary>
    /// Settings › Weather (task 4.06). <see cref="OnScreen"/> off shows a clear day always: no clouds, fog, rain or snow in the
    /// air, cloud shadows or storm light. The rest choose its parts: how the clouds are drawn, a ceiling that keeps them over
    /// the mountain, how thick the fog and haze are, and each part on or off. Only the screen changes: the weather itself
    /// still runs (snow, wind holds, the HUD's forecast). Keys of their own, not <see cref="GraphicsOptions"/> fields, so the
    /// presets never read as Custom. The defaults are the game's own look.
    /// </summary>
    public static class WeatherOptions
    {
        public const int MaxFog = 100, FogStep = 5;
        /// <summary>The ceiling: clouds never lower than the area's highest ground plus 1,000 ft.</summary>
        public const float CeilingClearance = 304.8f;

        const string OnScreenKey = "WeatherOnScreen", CloudsKey = "WeatherClouds", CeilingKey = "WeatherCeiling", FogKey = "WeatherFog";
        /// <summary>One key per <see cref="WeatherPart"/>, in its order.</summary>
        internal static readonly string[] PartKeys =
            { "WeatherValleyFog", "WeatherFalling", "WeatherSpindrift", "WeatherCloudShadows", "WeatherStormLight", "WeatherTreeSway", "WeatherSparkle", "WeatherRays" };
        /// <summary>Every key, for the tests that clear the store.</summary>
        internal static readonly string[] AllKeys = { OnScreenKey, CloudsKey, CeilingKey, FogKey,
            "WeatherValleyFog", "WeatherFalling", "WeatherSpindrift", "WeatherCloudShadows", "WeatherStormLight", "WeatherTreeSway", "WeatherSparkle", "WeatherRays" };

        static bool _loaded, _onScreen = true, _ceiling;
        static CloudLook _clouds = CloudLook.Auto;
        static int _fog = MaxFog;
        static readonly bool[] _parts = new bool[PartKeys.Length];

        public static bool OnScreen { get { Load(); return _onScreen; } }
        public static CloudLook Clouds { get { Load(); return _clouds; } }
        /// <summary>Clouds kept 1,000 ft over the area's highest ground.</summary>
        public static bool Ceiling { get { Load(); return _ceiling; } }
        /// <summary>How thick the fog and haze are, 0–100 % of the weather's own.</summary>
        public static int FogPercent { get { Load(); return _fog; } }
        public static bool Shows(WeatherPart part) { Load(); return _parts[(int)part]; }

        /// <summary>Raised after a setting changes.</summary>
        public static event Action Changed;

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _onScreen = SettingsStore.GetInt(OnScreenKey, 1) != 0;
            int c = SettingsStore.GetInt(CloudsKey, (int)CloudLook.Auto);
            _clouds = c >= 0 && c <= (int)CloudLook.Off ? (CloudLook)c : CloudLook.Auto;
            _ceiling = SettingsStore.GetInt(CeilingKey, 0) == 1;
            int f = SettingsStore.GetInt(FogKey, MaxFog);
            _fog = f >= 0 && f <= MaxFog ? f / FogStep * FogStep : MaxFog;
            for (int i = 0; i < _parts.Length; i++) _parts[i] = SettingsStore.GetInt(PartKeys[i], 1) != 0;
        }

        /// <summary>Forgets what was read, so the next use reads the store again (tests).</summary>
        internal static void Reload() => _loaded = false;

        public static void SetOnScreen(bool on, bool remember = true)
        {
            Load();
            bool changed = on != _onScreen;
            _onScreen = on;
            if (remember) SettingsStore.SetInt(OnScreenKey, on ? 1 : 0);
            if (changed) Changed?.Invoke();
        }

        public static void SetClouds(CloudLook look, bool remember = true)
        {
            Load();
            bool changed = look != _clouds;
            _clouds = look;
            if (remember) SettingsStore.SetInt(CloudsKey, (int)look);
            if (changed) Changed?.Invoke();
        }

        public static void SetCeiling(bool on, bool remember = true)
        {
            Load();
            bool changed = on != _ceiling;
            _ceiling = on;
            if (remember) SettingsStore.SetInt(CeilingKey, on ? 1 : 0);
            if (changed) Changed?.Invoke();
        }

        public static void SetFogPercent(int percent, bool remember = true)
        {
            Load();
            percent = Math.Max(0, Math.Min(MaxFog, percent / FogStep * FogStep));
            bool changed = percent != _fog;
            _fog = percent;
            if (remember) SettingsStore.SetInt(FogKey, percent);
            if (changed) Changed?.Invoke();
        }

        public static void SetShows(WeatherPart part, bool on, bool remember = true)
        {
            Load();
            bool changed = on != _parts[(int)part];
            _parts[(int)part] = on;
            if (remember) SettingsStore.SetInt(PartKeys[(int)part], on ? 1 : 0);
            if (changed) Changed?.Invoke();
        }

        /// <summary>Every row back to the game's own look (Restore defaults).</summary>
        public static void SetDefaults(bool remember = true)
        {
            SetOnScreen(true, remember);
            SetClouds(CloudLook.Auto, remember);
            SetCeiling(false, remember);
            SetFogPercent(MaxFog, remember);
            for (int i = 0; i < PartKeys.Length; i++) SetShows((WeatherPart)i, true, remember);
        }
    }
}
