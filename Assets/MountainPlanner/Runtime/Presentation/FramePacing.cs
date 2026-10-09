using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Settings › Display: V-Sync and the frame-rate cap (0.4 S8; task P2-05), live and remembered. V-Sync wins
    /// when it's on (Unity ignores the cap then). Benchmarks and reviews turn both off for their run without
    /// changing what's saved.
    /// </summary>
    public static class FramePacing
    {
        /// <summary>The cap's steps; 0 is Unlimited.</summary>
        public static readonly int[] Caps = { 30, 60, 120, 144, 0 };
        const string VSyncKey = "VSync", CapKey = "FrameCap";

        static bool _loaded, _vsync;
        static int _holds;
        static int _cap;

        public static bool VSync { get { Load(); return _vsync; } }
        /// <summary>Frames a second, or 0 for Unlimited.</summary>
        public static int Cap { get { Load(); return _cap; } }

        public static string CapName(int cap) => cap <= 0 ? "Unlimited" : cap + " fps";

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _vsync = SettingsStore.GetInt(VSyncKey, 0) != 0;
            _cap = SettingsStore.GetInt(CapKey, 0);
            if (System.Array.IndexOf(Caps, _cap) < 0) _cap = 0;
        }

        /// <summary>Forgets what was read, so the next use reads the store again (tests).</summary>
        internal static void Reload() => _loaded = false;

        public static void SetVSync(bool on, bool remember = true)
        {
            Load();
            _vsync = on;
            if (remember) SettingsStore.SetInt(VSyncKey, on ? 1 : 0);
            Reapply();
        }

        public static void SetCap(int cap, bool remember = true)
        {
            Load();
            _cap = System.Array.IndexOf(Caps, cap) >= 0 ? cap : 0;
            if (remember) SettingsStore.SetInt(CapKey, _cap);
            Reapply();
        }

        /// <summary>
        /// Uncapped with V-Sync off for this run (benchmarks, reviews, the Auto tree-detail timing) until
        /// <see cref="Release"/> (holds count, so a timing inside a benchmark releases only its own); the saved choices are untouched.
        /// </summary>
        public static void Hold()
        {
            _holds++;
            Reapply();
        }

        public static void Release()
        {
            if (_holds > 0) _holds--;
            Reapply();
        }

        /// <summary>Puts the choices into Unity (also after a quality-level switch, which resets V-Sync).</summary>
        public static void Reapply()
        {
            Load();
            bool held = _holds > 0;
            QualitySettings.vSyncCount = !held && _vsync ? 1 : 0;
            Application.targetFrameRate = held || _cap <= 0 ? -1 : _cap;
        }
    }
}
