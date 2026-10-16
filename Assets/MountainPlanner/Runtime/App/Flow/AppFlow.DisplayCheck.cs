using System;
using System.Collections;
using System.IO;
using System.Text;
using MountainPlanner.UI;
using MountainPlanner.UI.Flow;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -displaycheck &lt;folder&gt; (task P3-01; P2-05 left it untested): the live window switch and the 15-second keep or
    /// revert, in a real player, by the Settings › Display buttons a player presses. Windowed then Keep; a resolution
    /// left to go back by itself; Borderless then Go back; Fullscreen left to go back by itself. Each step's window is
    /// logged and captured; report.txt says PASS or FAIL per check. At the end the window goes back to how it started,
    /// so the owner's saved display mode is unchanged. Run it on the desktop, not over remote desktop (640×480).
    /// </summary>
    public sealed partial class AppFlow
    {
        void StartDisplayCheckIfAsked(string[] args)
        {
            int i = Array.IndexOf(args, "-displaycheck");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(DisplayCheck(Path.GetFullPath(args[i + 1])));
        }

        IEnumerator DisplayCheck(string folder)
        {
            Directory.CreateDirectory(folder);
            var report = new StringBuilder();
            int failures = 0;
            void Note(string line) { report.AppendLine(line); Debug.Log("[DisplayCheck] " + line); }
            void Check(bool ok, string what) { if (!ok) failures++; Note((ok ? "PASS " : "FAIL ") + what); }
            string Window() => $"{UiDisplay.Names[Mathf.Max(0, UiDisplay.Current)]} {Screen.width}×{Screen.height}";

            yield return WaitForMountain(300);
            yield return Wait(2);
            var startMode = Screen.fullScreenMode;
            var startSize = UiDisplay.Size;
            Note($"desktop {Screen.currentResolution.width}×{Screen.currentResolution.height}; the game starts {Window()}");
            if (Screen.currentResolution.width < 1280) Note("WARNING: a small desktop (remote desktop?): sizes below may not mean much");

            Screens.ShowSettings();
            Screens.Settings.ShowPage(3);   // Display
            yield return Wait(0.5f);
            var root = Screens.Document.rootVisualElement;
            var keepBar = root.Q("display-keep");
            bool KeepShown() => keepBar != null && !keepBar.ClassListContains("hidden");
            IEnumerator Press(string name)
            {
                var button = root.Q<Button>(name);
                if (button == null) { Check(false, $"{name} is on the page"); yield break; }
                using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
                yield return Wait(1.5f);   // Unity changes the window over a frame or two; the page redraws after 300 ms
            }
            IEnumerator ModeTo(int target)
            {
                for (int n = 0; n < UiDisplay.Modes.Length && UiDisplay.Current != target; n++)
                    yield return Press(UiDisplay.Current < target ? "display-next" : "display-prev");
            }
            IEnumerator WaitOutTheCountdown()
            {
                float until = Time.realtimeSinceStartup + SettingsWindow.KeepSeconds + 5;
                while (KeepShown() && Time.realtimeSinceStartup < until) yield return null;
                yield return Wait(1.5f);
            }

            // 1. Windowed, then Keep.
            yield return ModeTo(0);
            Note("Windowed: " + Window());
            yield return Shot(folder, "1-windowed-asking");
            Check(Screen.fullScreenMode == FullScreenMode.Windowed, "Windowed switches the window live");
            Check(KeepShown(), "it asks to keep the change");
            yield return Press("display-keep-ok");
            Check(!KeepShown() && Screen.fullScreenMode == FullScreenMode.Windowed, "Keep keeps it and closes the question");
            var windowed = UiDisplay.Size;

            // 2. Another resolution, left alone: it goes back by itself after 15 s.
            yield return Press(UiDisplay.Resolutions().IndexOf(windowed) > 0 ? "resolution-prev" : "resolution-next");
            var changed = UiDisplay.Size;
            Note("resolution: " + Window());
            yield return Shot(folder, "2-resolution-asking");
            Check(changed != windowed, $"a resolution switches live ({windowed.x}×{windowed.y} → {changed.x}×{changed.y})");
            yield return WaitOutTheCountdown();
            Note("after 15 s: " + Window());
            Check(!KeepShown() && UiDisplay.Size == windowed, "left alone, it goes back after 15 s");
            yield return Shot(folder, "2-resolution-went-back");

            // 3. Borderless, then Go back.
            yield return ModeTo(1);
            Note("Borderless: " + Window());
            yield return Shot(folder, "3-borderless-asking");
            Check(Screen.fullScreenMode == FullScreenMode.FullScreenWindow, "Borderless switches live");
            Check(Screen.width == Screen.currentResolution.width && Screen.height == Screen.currentResolution.height, "and fills the screen");
            yield return Press("display-revert");
            Check(!KeepShown() && Screen.fullScreenMode == FullScreenMode.Windowed && UiDisplay.Size == windowed, "Go back puts the window back");

            // 4. Fullscreen (the screen to itself), left alone.
            yield return ModeTo(2);
            yield return Wait(1.5f);   // a mode change of the monitor itself takes longer
            Note("Fullscreen: " + Window());
            yield return Shot(folder, "4-fullscreen-asking");
            Check(Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen, "Fullscreen switches live");
            yield return WaitOutTheCountdown();
            Note("after 15 s: " + Window());
            Check(!KeepShown() && Screen.fullScreenMode == FullScreenMode.Windowed && UiDisplay.Size == windowed, "left alone, it goes back after 15 s");

            // As it started: the owner's saved display mode stays theirs.
            Screens.CloseOverlay();
            Screen.SetResolution(startSize.x, startSize.y, startMode);
            yield return Wait(2);
            Note("restored: " + Window());
            Check(Screen.fullScreenMode == startMode && UiDisplay.Size == startSize, "the window is back as it started");

            Note(failures == 0 ? "display check PASSED" : $"display check FAILED ({failures})");
            File.WriteAllText(Path.Combine(folder, "report.txt"), report.ToString());
            Quit();
        }
    }
}
