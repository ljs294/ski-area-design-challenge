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
    /// per view (each as <c>-view</c>: an orbit round a ground point) and quits.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartCrowdShots(string[] args)
        {
            int i = Array.IndexOf(args, "-crowdshot"), v = Array.IndexOf(args, "-crowdviews");
            if (i < 0 || i + 1 >= args.Length || v < 0 || v + 1 >= args.Length) return;
            StartCoroutine(CrowdShots(Path.GetFullPath(args[i + 1]), args[v + 1], GuestsArgument(args, 1500)));
        }

        IEnumerator CrowdShots(string dir, string views, int crowd)
        {
            BeginScriptedRun();
            Directory.CreateDirectory(dir);
            yield return BenchCrowd(crowd);
            yield return WaitUntilFaceSecondOfDay(11 * 3600);
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
                ScreenCapture.CaptureScreenshot(path);
                yield return null;
                yield return null;
                Debug.Log($"[CrowdShots] {path} ({_simNow?.Guests?.SkierCount ?? 0} on the hill)");
            }
            Application.Quit(0);
        }
    }
}
