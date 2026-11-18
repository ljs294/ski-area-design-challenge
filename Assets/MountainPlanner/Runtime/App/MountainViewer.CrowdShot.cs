using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.24's review capture of loaded carriers (a gondola's riders on their benches):
    /// <c>-crowdshot &lt;folder&gt; -crowdviews "x,z,dist,yaw,pitch;..." [-guests N]</c> opens the mountain, lands on the
    /// morning with the crowd topped up to N (1,500 by default, as <c>-skiershot</c>), waits for 11:00, then takes one still
    /// per view (each as <c>-view</c>: an orbit round a ground point) and quits. Task 4.24 PR B: <c>-crowdtime H</c> waits
    /// for H:00 instead, and under <c>-nographics</c> it takes no picture but still logs who sits where (the scene is proved
    /// on the CPU before a GPU run is spent on it).
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartCrowdShots(string[] args)
        {
            int i = Array.IndexOf(args, "-crowdshot"), v = Array.IndexOf(args, "-crowdviews");
            if (i < 0 || i + 1 >= args.Length || v < 0 || v + 1 >= args.Length) return;
            int t = Array.IndexOf(args, "-crowdtime");
            int hour = t >= 0 && t + 1 < args.Length && int.TryParse(args[t + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ? h : 11;
            StartCoroutine(CrowdShots(Path.GetFullPath(args[i + 1]), args[v + 1], GuestsArgument(args, 1500), hour));
        }

        IEnumerator CrowdShots(string dir, string views, int crowd, int hour = 11)
        {
            BeginScriptedRun();
            Directory.CreateDirectory(dir);
            yield return BenchCrowd(crowd);
            bool drawn = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            if (!drawn)
            {
                // Task 4.24 PR B: the proof off the GPU, on the very design the stills would show: who waits at each top
                // and who rides down, every face hour to the stop, so the shot's hour is chosen from the game's numbers.
                for (int h = 10; h <= 17; h++)
                {
                    yield return WaitUntilFaceSecondOfDay(h * 3600);
                    var lifts = new System.Text.StringBuilder();
                    for (int k = 0; _simNow != null && k < _simNow.LiftCount; k++)
                        lifts.Append($" {_simNow.LiftIds[k] % 10000}: {_simNow.RidesToday[k]} up, {_simNow.LineCount[k]} in line, {_simNow.Aboard[k]} aboard, {_simNow.LiftStatus[k]};");
                    Debug.Log($"[CrowdShots] {h,2}:00 {_simNow?.Guests?.SkierCount ?? 0} on the hill |{RideDownCounts()} | lifts:{lifts}");
                }
                Application.Quit(0);
                yield break;
            }
            yield return WaitUntilFaceSecondOfDay(Mathf.Clamp(hour, 0, 23) * 3600);
            SendSim(SimCommandKind.SetSpeed, 1);
            int n = 0;
            foreach (string view in views.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = view.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                if (p.Length < 5 || Camera == null) continue;
                Camera.Frame(new Vector3(p[0], _resort.Surface.HeightAt(p[0], p[1]), p[1]), p[2]);
                Camera.SetAngles(p[3], p[4]);
                for (int k = 0; k < 90; k++) yield return null;   // LOD, shadows and the figures settle
                string path = Path.Combine(dir, $"crowd-{++n}.png");
                if (drawn) ScreenCapture.CaptureScreenshot(path);
                yield return null;
                yield return null;
                Debug.Log($"[CrowdShots] {path} ({_simNow?.Guests?.SkierCount ?? 0} on the hill)");
                // Task 4.24 PR B: the proof for a still's caption: who sits on the carriers (up and down) and who waits at
                // each top, so a picture is described only by what the game says was there.
                Debug.Log($"[CrowdShots] riders: {_riders?.Describe()} |{RideDownCounts()}");
            }
            Application.Quit(0);
        }

        /// <summary>Each lift that carries riders down or has a top line: how many wait at its top and rode down today; then the states.</summary>
        string RideDownCounts()
        {
            var s = _simNow;
            var tops = new System.Text.StringBuilder();
            for (int k = 0; s != null && k < s.LiftCount; k++)
                if (s.CarriesDown[k] || s.TopLineCount[k] > 0)
                    tops.Append($" lift {s.LiftIds[k]}: {s.TopLineCount[k]} at the top, {s.RidesDownToday[k]} down today;");
            var g = s?.Guests;
            return $" tops:{tops} | states: " +
                   (g == null ? "-" : $"{g.ByState[(int)GuestState.QueuedDown]} at a top, {g.ByState[(int)GuestState.RidingDown]} riding down");
        }
    }
}
