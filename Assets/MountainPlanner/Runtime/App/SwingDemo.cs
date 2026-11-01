using System;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// TEMPORARY (task P3-16, BB3's owner demo gate): with -swingdemo, F7 cycles the chair swing presets and F8 the side
    /// sway's driver, shown in a corner. The owner picks; the picked values become <see cref="ChairSwing.Current"/>'s
    /// default and this file is deleted before PR B.
    /// </summary>
    public sealed class SwingDemo : MonoBehaviour
    {
        int _preset = 1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-swingdemo") < 0) return;
            var go = new GameObject("Swing demo");
            DontDestroyOnLoad(go);
            go.AddComponent<SwingDemo>();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f7Key.wasPressedThisFrame)
            {
                var sway = ChairSwing.Current.Sway;
                _preset = (_preset + 1) % ChairSwing.Presets.Length;
                ChairSwing.Current = ChairSwing.Presets[_preset];
                if (_preset > 0) ChairSwing.Current.Sway = sway;
            }
            if (kb.f8Key.wasPressedThisFrame && _preset > 0)
                ChairSwing.Current.Sway = (ChairSwing.SwayDriver)(((int)ChairSwing.Current.Sway + 1) % 3);
        }

        void OnGUI()
        {
            var look = ChairSwing.Current;
            string text = $"Chair swing: {look.Name} (F7) · side sway: {look.Sway} (F8) · wind: developer panel (F1) · Space runs the chairs";
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleLeft };
            style.normal.textColor = Color.white;
            GUI.Box(new Rect(20, Screen.height - 60, 900, 36), text, style);
        }
    }
}
