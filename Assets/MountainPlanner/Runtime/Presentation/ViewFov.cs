using System;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The field of view (Settings › Display; E6; task P2-05). The rule for every game camera (the view, the title,
    /// the benchmark and, later, photo mode): the <b>vertical</b> angle is the setting, and the horizontal one follows
    /// the screen's shape, so a 21:9 or 32:9 screen shows more to the sides and the same above and below (Hor+).
    /// Unity's <c>Camera.fieldOfView</c> is the vertical angle, so a camera only needs <see cref="Vertical"/>; anything
    /// that frames by width asks <see cref="Horizontal"/>.
    /// </summary>
    public static class ViewFov
    {
        public const int Min = 45, Max = 75, Default = 60;
        const string Key = "FieldOfView";

        static bool _loaded;
        static int _vertical = Default;

        /// <summary>Raised after the angle changes.</summary>
        public static event Action Changed;

        /// <summary>The vertical field of view in degrees, 45–75.</summary>
        public static int Vertical
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _vertical = Mathf.Clamp(SettingsStore.GetInt(Key, Default), Min, Max);
                }
                return _vertical;
            }
        }

        /// <summary>Forgets what was read, so the next use reads the store again (tests).</summary>
        internal static void Reload() => _loaded = false;

        public static void Set(int degrees, bool remember = true)
        {
            degrees = Mathf.Clamp(degrees, Min, Max);
            if (degrees == Vertical) return;
            _vertical = degrees;
            if (remember) SettingsStore.SetInt(Key, degrees);
            Changed?.Invoke();
        }

        /// <summary>The horizontal angle, degrees, for a vertical angle on a screen of this width over height.</summary>
        public static float Horizontal(float verticalDegrees, float aspect) =>
            2f * Mathf.Atan(Mathf.Tan(verticalDegrees * 0.5f * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg;

        public static float Horizontal(float aspect) => Horizontal(Vertical, aspect);
    }
}
