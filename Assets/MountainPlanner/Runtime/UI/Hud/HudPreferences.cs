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

        public static void SetDocked(bool docked, bool remember = true)
        {
            if (remember) PlayerPrefs.SetInt(DockedKey, docked ? 1 : 0);
            if (docked == Docked) return;
            _docked = docked;
            Changed?.Invoke();
        }
    }
}
