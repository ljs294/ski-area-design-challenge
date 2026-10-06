using UnityEngine;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Settings › Graphics › Display mode (the accepted mockup's stepper; owner, 2026-10-06): a window, borderless full
    /// screen (the default) or exclusive full screen. Unity remembers the mode between runs, and Alt+Enter still switches
    /// between a window and full screen.
    /// </summary>
    public static class UiDisplay
    {
        /// <summary>The modes in the stepper's order, with the mockup's names.</summary>
        public static readonly FullScreenMode[] Modes = { FullScreenMode.Windowed, FullScreenMode.FullScreenWindow, FullScreenMode.ExclusiveFullScreen };
        public static readonly string[] Names = { "Windowed", "Borderless", "Fullscreen" };

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
    }
}
