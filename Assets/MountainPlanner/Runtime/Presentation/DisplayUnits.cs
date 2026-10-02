using System;
using MountainPlanner.Domain.Measure;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The player's units (task 12b.2): imperial by default (owner, 2026-10-02), metric on request with U or the
    /// menu, remembered between sessions. Every screen that shows a distance, elevation or depth reads
    /// <see cref="Current"/> and formats with <see cref="UnitFormat"/>; <see cref="Changed"/> tells them to redraw.
    /// The game itself always works in metres.
    /// </summary>
    public static class DisplayUnits
    {
        const string PrefKey = "MountainPlanner.Units";
        static bool _loaded;
        static UnitSystem _current = UnitSystem.Imperial;

        /// <summary>Raised after the units change.</summary>
        public static event Action Changed;

        public static UnitSystem Current
        {
            get
            {
                if (!_loaded) Load();
                return _current;
            }
        }

        public static bool Imperial => Current == UnitSystem.Imperial;

        static void Load()
        {
            _loaded = true;
            _current = PlayerPrefs.GetInt(PrefKey, (int)UnitSystem.Imperial) == (int)UnitSystem.Metric ? UnitSystem.Metric : UnitSystem.Imperial;
        }

        public static void Set(UnitSystem units, bool remember = true)
        {
            if (!_loaded) Load();
            if (units == _current) return;
            _current = units;
            if (remember)
            {
                PlayerPrefs.SetInt(PrefKey, (int)units);
                PlayerPrefs.Save();
            }
            Changed?.Invoke();
        }

        public static void Toggle() => Set(Imperial ? UnitSystem.Metric : UnitSystem.Imperial);
    }
}
