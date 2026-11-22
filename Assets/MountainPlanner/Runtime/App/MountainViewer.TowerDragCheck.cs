using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.26 (BB18): <c>-towerdragcheck &lt;out.txt&gt;</c> with <c>-drawnresort</c> times what one step of a tower drag
    /// costs in Edit lift on each built lift: the rope solved again (<see cref="LiftRope.Solve"/>), the profile strip
    /// (<see cref="LiftProfile.From"/>) and the 3D draft redrawn (<see cref="ShowDraftLift"/>), with the middle tower moved
    /// 5 ft at a time, 40 steps. One line per lift with each part's median and slowest, then it quits.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartTowerDragCheck(string[] args)
        {
            int i = Array.IndexOf(args, "-towerdragcheck");
            if (i < 0 || i + 1 >= args.Length) return;
            StartCoroutine(TowerDragCheck(Path.GetFullPath(args[i + 1])));
        }

        IEnumerator TowerDragCheck(string outPath)
        {
            for (int k = 0; k < 120_000; k++)
            {
                if (_resort != null && _resort.CoverReady.IsCompleted) EnsureTerrainEdits();
                if (_resort != null && _resort.CoverReady.IsCompleted && _edits != null && !WorkBusy && Design != null) break;
                yield return null;
            }
            for (int k = 0; k < 120; k++) yield return null;
            var lines = new List<string>();
            var ground = _edits.Pipeline.Heights;
            foreach (var lift in Design.Document.Lifts.Where(l => l.Stage == WorkStage.Built && l.Towers.Count > 2))
            {
                var type = LiftType.Of(lift.CatalogId, lift.Drive);
                var b = _resort.Frame.ToAlbers((float)lift.Bottom.XMetres, (float)lift.Bottom.YMetres);
                var t = _resort.Frame.ToAlbers((float)lift.Top.XMetres, (float)lift.Top.YMetres);
                var levels = lift.Levels ?? LiftBuild.GroundLevels(type, ground, b.X, b.Y, t.X, t.Y);
                var solve = new List<double>();
                var profile = new List<double>();
                var draw = new List<double>();
                var frames = new List<double>();
                var parts = new double[12];
                int mid = lift.Towers.Count / 2;
                for (int step = 0; step < 40; step++)
                {
                    var towers = lift.Towers.Select((w, i) => i == mid ? w.WithAlong(w.AlongMm + (step % 20 - 10) * DesignUnits.FiveFeetMm) : w).ToList();
                    var sw = Stopwatch.StartNew();
                    var rope = LiftRope.Solve(type, ground, b.X, b.Y, t.X, t.Y, towers, lift.Stance, type.Bottom.ExitAlong, levels.BottomMetres + type.Bottom.ExitRise,
                                              type.Top.ExitAlong, levels.TopMetres + type.Top.ExitRise);
                    solve.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    LiftProfile.From(rope, ground, b.X, b.Y, t.X, t.Y, levels);
                    profile.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    ShowDraftLift(new Lift(lift.Id, lift.Name, WorkStage.Built, lift.CatalogId, lift.Bottom, lift.Top, TowerMode.ByHand, lift.Stance, towers, levels, lift.Drive));
                    draw.Add(sw.Elapsed.TotalMilliseconds);
                    if (step >= 5 && _draftView != null) for (int q = 0; q < parts.Length; q++) parts[q] += _draftView.BuildParts[q] / 35.0;
                    yield return null;
                    frames.Add(Time.unscaledDeltaTime * 1000.0);
                }
                ShowDraftLift(null);
                string line = $"{lift.Name} ({lift.CatalogId}, {lift.Towers.Count} towers): solve {P(solve, 50):F1}/{P(solve, 100):F1} ms, " +
                              $"profile {P(profile, 50):F1}/{P(profile, 100):F1} ms, draft redraw {P(draw, 50):F1}/{P(draw, 100):F1} ms, " +
                              $"frame {P(frames, 50):F1}/{P(frames, 100):F1} ms (median/slowest); redraw by part (mean): clear {parts[0]:F1}, terminals {parts[1]:F1}, " +
                              $"solve {parts[2]:F1}, towers {parts[3]:F1}, rope and carriers {parts[4]:F1} ms (read {parts[8]:F1}, prep {parts[9]:F1}, posing {parts[5]:F1}, apply {parts[10]:F1}, spin {parts[11]:F1}, cable {parts[6]:F1}, carriers {parts[7]:F1})";
                lines.Add(line);
                Debug.Log("[TowerDragCheck] " + line);
                yield return null;
            }
            File.WriteAllLines(outPath, lines);
            Application.Quit(0);
        }

        static double P(List<double> v, int pct)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, s.Count * pct / 100)];
        }
    }
}
