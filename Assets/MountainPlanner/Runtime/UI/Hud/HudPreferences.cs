using System;
using UnityEngine;

namespace MountainPlanner.UI.Hud
{
    /// <summary>
    /// The HUD's own setting (task P2-02): the status bar docked to the bottom edge (the default) or floating above it,
    /// switched in the menu and remembered. -hudfloat starts it floating without remembering (captures).
    /// </summary>
    public static class HudPreferences
    {
        const string DockedKey = "hud.docked";
        static bool? _docked;

        public static event Action Changed;

        public static bool Docked
        {
            get
            {
                if (_docked == null)
                    _docked = Array.IndexOf(Environment.GetCommandLineArgs(), "-hudfloat") < 0 && PlayerPrefs.GetInt(DockedKey, 1) != 0;
                return _docked.Value;
            }
        }

        const string OpacityKey = "hud.opacity";
        static int? _opacity;

        /// <summary>
        /// How solid the panels are, 50–100%, 95% unless chosen (owner, 2026-10-06): their colour plates draw at this opacity.
        /// -hudopacity N sets it without remembering (captures).
        /// </summary>
        /// <summary>The owner's pick (2026-10-06): the mountain just shows through.</summary>
        public const int DefaultPanelOpacity = 95;

        public static int PanelOpacity
        {
            get
            {
                if (_opacity == null)
                {
                    var args = Environment.GetCommandLineArgs();
                    int i = Array.IndexOf(args, "-hudopacity");
                    _opacity = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int flag) ? flag : PlayerPrefs.GetInt(OpacityKey, DefaultPanelOpacity);
                    _opacity = Mathf.Clamp(_opacity.Value, 50, 100);
                }
                return _opacity.Value;
            }
        }

        public static void SetPanelOpacity(int percent, bool remember = true)
        {
            percent = Mathf.Clamp(percent, 50, 100);
            if (remember) PlayerPrefs.SetInt(OpacityKey, percent);
            if (percent == PanelOpacity) return;
            _opacity = percent;
            Changed?.Invoke();
        }

        public static void SetDocked(bool docked, bool remember = true)
        {
            if (remember) PlayerPrefs.SetInt(DockedKey, docked ? 1 : 0);
            if (docked == Docked) return;
            _docked = docked;
            Changed?.Invoke();
        }
    }
}
