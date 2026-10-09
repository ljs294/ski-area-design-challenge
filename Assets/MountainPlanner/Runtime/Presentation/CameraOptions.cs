namespace MountainPlanner.Presentation
{
    public enum ZoomSpeed { Slow, Normal, Fast }

    /// <summary>Settings › Controls › Camera (the mockup's rows; task P2-05): scroll direction and zoom speed, live and remembered.</summary>
    public static class CameraOptions
    {
        const string InvertKey = "InvertZoom", SpeedKey = "ZoomSpeed";
        static bool _loaded, _invert;
        static ZoomSpeed _speed = ZoomSpeed.Normal;

        /// <summary>Scroll down zooms in.</summary>
        public static bool InvertZoom { get { Load(); return _invert; } }
        public static ZoomSpeed Speed { get { Load(); return _speed; } }

        /// <summary>The zoom rate against Normal's, for the wheel and the keys.</summary>
        public static float SpeedFactor => Speed == ZoomSpeed.Slow ? 0.5f : Speed == ZoomSpeed.Fast ? 2f : 1f;

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _invert = SettingsStore.GetInt(InvertKey, 0) != 0;
            int s = SettingsStore.GetInt(SpeedKey, (int)ZoomSpeed.Normal);
            _speed = s >= 0 && s <= (int)ZoomSpeed.Fast ? (ZoomSpeed)s : ZoomSpeed.Normal;
        }

        /// <summary>Forgets what was read, so the next use reads the store again (tests).</summary>
        internal static void Reload() => _loaded = false;

        public static void SetInvertZoom(bool on, bool remember = true)
        {
            Load();
            _invert = on;
            if (remember) SettingsStore.SetInt(InvertKey, on ? 1 : 0);
        }

        public static void SetSpeed(ZoomSpeed speed, bool remember = true)
        {
            Load();
            _speed = speed;
            if (remember) SettingsStore.SetInt(SpeedKey, (int)speed);
        }
    }
}
