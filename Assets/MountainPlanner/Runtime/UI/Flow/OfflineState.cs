using System;

namespace MountainPlanner.UI.Flow
{
    /// <summary>Why the game can't reach the network: offline mode (the setting or -offline), or the connection itself.</summary>
    public enum OfflineMode { None, Setting, NoConnection }

    /// <summary>
    /// The game's offline state, for every flow that needs the network (task P2-06, S11): the picker, downloads and the
    /// credits (P2-07). The app flow sets it from offline mode; the network's own results (a download or the picker
    /// failing to connect, then succeeding) set or clear <see cref="OfflineMode.NoConnection"/>. Nothing polls.
    /// Main thread only.
    /// </summary>
    public static class OfflineState
    {
        static bool _setting, _noConnection;

        /// <summary>Offline mode wins: with it on, whether the cable is in doesn't matter.</summary>
        public static OfflineMode Mode => _setting ? OfflineMode.Setting : _noConnection ? OfflineMode.NoConnection : OfflineMode.None;

        public static bool IsOffline => Mode != OfflineMode.None;

        /// <summary>Raised on the main thread when <see cref="Mode"/> changes.</summary>
        public static event Action Changed;

        /// <summary>Offline mode on or off (Settings › Data, or -offline for the whole run).</summary>
        public static void SetSetting(bool on)
        {
            var was = Mode;
            _setting = on;
            if (Mode != was) Changed?.Invoke();
        }

        /// <summary>A network call failed to connect (true) or got through (false).</summary>
        public static void ReportConnection(bool connected)
        {
            var was = Mode;
            _noConnection = !connected;
            if (Mode != was) Changed?.Invoke();
        }

        /// <summary>One line for a screen that works offline but would show more online (the credits, S9).</summary>
        public static string Describe() => Mode switch
        {
            OfflineMode.Setting => "Offline mode is on: what's shown comes from the areas on this computer.",
            OfflineMode.NoConnection => "No connection: what's shown comes from the areas on this computer.",
            _ => "",
        };

        /// <summary>Back to online (tests).</summary>
        public static void Reset()
        {
            _setting = false;
            _noConnection = false;
        }
    }
}
