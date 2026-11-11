using System;
using System.IO;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -simgolden &lt;report.txt&gt; (task 4.03, SI-08): plays the golden season script in the player, as the Core tests and the
    /// nightly job do under .NET, and checks the same pinned hash: Mono or IL2CPP in a build gives the same simulation as
    /// .NET. Writes PASS or FAIL with the fold and the time it took, then quits (exit code 0 or 1).
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>Before any scene, in batch mode too (the flow itself stays out of batch runs): -simgolden runs and quits.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void SimGoldenAtBoot() => StartSimGoldenIfAsked(Environment.GetCommandLineArgs());

        static void StartSimGoldenIfAsked(string[] args)
        {
            int i = Array.IndexOf(args, "-simgolden");
            if (i < 0) return;
            string report = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? Path.GetFullPath(args[i + 1]) : null;
            var p = Pacing.Default;
            int days = 2 * new LabelCalendar(p).YearFaceDays;
            float start = Time.realtimeSinceStartup;
            ulong fold = SeasonRunner.GoldenFold(p, days, weather: TypicalWeather.Golden());
            float seconds = Time.realtimeSinceStartup - start;
            bool ok = fold == SeasonRunner.GoldenFoldValue;
            string line = $"{(ok ? "PASS" : "FAIL")} golden season fold 0x{fold:X16} (pinned 0x{SeasonRunner.GoldenFoldValue:X16}), {days} face days in {seconds:F1} s";
            Debug.Log("[SimGolden] " + line);
            if (report != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, line + Environment.NewLine);
            }
            Application.Quit(ok ? 0 : 1);
        }
    }
}
