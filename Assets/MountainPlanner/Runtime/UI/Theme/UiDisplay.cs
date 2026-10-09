using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Settings › Display (the accepted mockup's stepper; owner, 2026-10-06; task P2-05): a window, borderless full
    /// screen (the default) or exclusive full screen, and the resolution. Unity itself remembers both between runs
    /// (in the registry), and Alt+Enter still switches between a window and full screen.
    /// </summary>
    public static class UiDisplay
    {
        /// <summary>The modes in the stepper's order, with the mockup's names.</summary>
        public static readonly FullScreenMode[] Modes = { FullScreenMode.Windowed, FullScreenMode.FullScreenWindow, FullScreenMode.ExclusiveFullScreen };
        public static readonly string[] Names = { "Windowed", "Borderless", "Fullscreen" };

        /// <summary>Window sizes offered in every shape the game supports (E6): 16:9, 21:9 and 32:9, where they fit the display.</summary>
        static readonly Vector2Int[] WindowSizes =
        {
            new Vector2Int(1280, 720), new Vector2Int(1600, 900), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440),
            new Vector2Int(2560, 1080), new Vector2Int(3440, 1440), new Vector2Int(3840, 1080), new Vector2Int(5120, 1440),
        };

        /// <summary>Where a mode sits in <see cref="Modes"/> (macOS's maximised window counts as a window).</summary>
        public static int IndexOf(FullScreenMode mode) =>
            mode == FullScreenMode.FullScreenWindow ? 1 : mode == FullScreenMode.ExclusiveFullScreen ? 2 : 0;

        public static int Current => IndexOf(Screen.fullScreenMode);

        /// <summary>A window at three quarters of the display, a little smaller in each direction.</summary>
        public static Vector2Int WindowSize(int displayWidth, int displayHeight) =>
            new Vector2Int(Mathf.RoundToInt(displayWidth * 0.75f), Mathf.RoundToInt(displayHeight * 0.75f));

        /// <summary>Switches to the mode at <paramref name="index"/>: full screen at the display's own resolution, or a window.</summary>
        public static void Set(int index)
        {
            index = Mathf.Clamp(index, 0, Modes.Length - 1);
            var display = Screen.currentResolution;
            var mode = Modes[index];
            if (mode == FullScreenMode.Windowed)
            {
                var size = WindowSize(display.width, display.height);
                Screen.SetResolution(size.x, size.y, mode);
            }
            else Screen.SetResolution(display.width, display.height, mode);
        }

        /// <summary>A resolution in the current mode.</summary>
        public static void SetResolution(Vector2Int size) => Screen.SetResolution(size.x, size.y, Screen.fullScreenMode);

        /// <summary>The game's size now: the window, or what full screen renders at.</summary>
        public static Vector2Int Size => new Vector2Int(Screen.width, Screen.height);

        /// <summary>
        /// The resolutions to offer, smallest first. Full screen: the display's own modes (21:9 and 32:9 when the
        /// monitor has them). A window: the 16:9, 21:9 and 32:9 sizes that fit the display, plus the display's own.
        /// The current size is always in the list.
        /// </summary>
        public static List<Vector2Int> Resolutions(bool windowed, Resolution[] displayModes, Vector2Int display, Vector2Int current)
        {
            var list = new List<Vector2Int>();
            foreach (var r in displayModes)
            {
                var s = new Vector2Int(r.width, r.height);
                if (s.x >= 1024 && s.y >= 576 && s.x <= display.x && s.y <= display.y && !list.Contains(s)) list.Add(s);
            }
            if (windowed)
                foreach (var s in WindowSizes)
                    if (s.x <= display.x && s.y <= display.y && !list.Contains(s)) list.Add(s);
            if (!list.Contains(display)) list.Add(display);
            if (current.x > 0 && current.y > 0 && !list.Contains(current)) list.Add(current);
            list.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return list;
        }

        public static List<Vector2Int> Resolutions() =>
            Resolutions(Screen.fullScreenMode == FullScreenMode.Windowed, Screen.resolutions,
                        new Vector2Int(Screen.currentResolution.width, Screen.currentResolution.height), Size);

        /// <summary>"2560 × 1080 · 21:9".</summary>
        public static string Describe(Vector2Int s) => $"{s.x} × {s.y} · {Shape(s.x, s.y)}";

        /// <summary>The screen shape's usual name: 16:9, 16:10, 21:9, 32:9, 4:3, or the plain ratio.</summary>
        public static string Shape(int width, int height)
        {
            float r = width / (float)Mathf.Max(1, height);
            if (Mathf.Abs(r - 16f / 9f) < 0.03f) return "16:9";
            if (Mathf.Abs(r - 1.6f) < 0.03f) return "16:10";
            if (r > 2.3f && r < 2.45f) return "21:9";
            if (r > 3.5f && r < 3.6f) return "32:9";
            if (Mathf.Abs(r - 4f / 3f) < 0.03f) return "4:3";
            return r.ToString("0.00") + ":1";
        }
    }
}
