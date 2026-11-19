using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MountainPlanner.Domain.Snowmaking;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -solvebench &lt;report.txt&gt; (task 4.15, BG-03): solves <see cref="SolveBench"/>'s 500-gun network in the player (Mono
    /// or IL2CPP, as shipped), cold and warm, and checks the 20 ms budget. No GPU, no scene: writes PASS or FAIL with the
    /// medians of eleven solves each, then quits (exit code 0 or 1).
    /// </summary>
    public sealed partial class AppFlow
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void SolveBenchAtBoot() => StartSolveBenchIfAsked(Environment.GetCommandLineArgs());

        static void StartSolveBenchIfAsked(string[] args)
        {
            int i = Array.IndexOf(args, "-solvebench");
            if (i < 0) return;
            string report = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? Path.GetFullPath(args[i + 1]) : null;
            string line;
            bool ok;
            try
            {
                SolveBench.Build();
                var watch = Stopwatch.StartNew();
                var b = SolveBench.Build();
                double buildMs = watch.Elapsed.TotalMilliseconds;
                var asks = b.Asks();
                new SnowSolve(b.Graph).Run(asks, b.Level);
                var cold = new List<double>();
                SnowSolve s = null;
                for (int run = 0; run < 11; run++)
                {
                    watch.Restart();
                    s = new SnowSolve(b.Graph);
                    s.Run(asks, b.Level);
                    cold.Add(watch.Elapsed.TotalMilliseconds);
                }
                int resolves = s.Resolves;
                var warm = new List<double>();
                for (int run = 0; run < 11; run++)
                {
                    asks[run].Wanted = false;
                    watch.Restart();
                    s.Run(asks, b.Level);
                    warm.Add(watch.Elapsed.TotalMilliseconds);
                }
                cold.Sort();
                warm.Sort();
                double coldMs = cold[cold.Count / 2], warmMs = warm[warm.Count / 2];
                ok = s.Solved && coldMs <= SolveBench.BudgetMs;
                line = $"{(ok ? "PASS" : "FAIL")} snow solve {b.Graph.Guns.Length} guns, {b.Network.Pipes.Count} pipes, {resolves} re-solves: " +
                       $"cold {coldMs:F2} ms (budget {SolveBench.BudgetMs:F0}), warm {warmMs:F2} ms, build {buildMs:F1} ms, {SystemInfo.processorType}";
            }
            catch (Exception e)
            {
                ok = false;
                line = "FAIL snow solve: " + e.Message;
            }
            Debug.Log("[SolveBench] " + line);
            if (report != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, line + Environment.NewLine);
            }
            Application.Quit(ok ? 0 : 1);
        }
    }
}
