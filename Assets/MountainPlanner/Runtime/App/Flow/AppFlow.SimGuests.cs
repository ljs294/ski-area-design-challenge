using System;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -simguests &lt;report.txt&gt; [-skiercap N] [-draw N] (task 4.12): plays one face day of guests in the player and
    /// writes the day: the demand stack line by line (GU-03), who came and who left (SI-10), what satisfaction went on
    /// and why (GU-13), the reputations (GU-18), and what the tick cost. It is the headless half of demo 96, and it says
    /// in a build what the Core tests say under .NET: the same crowd, the same day.
    /// </summary>
    public sealed partial class AppFlow
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void SimGuestsAtBoot() => StartSimGuestsIfAsked(Environment.GetCommandLineArgs());

        static void StartSimGuestsIfAsked(string[] args)
        {
            int i = Array.IndexOf(args, "-simguests");
            if (i < 0) return;
            string report = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? Path.GetFullPath(args[i + 1]) : null;
            int cap = ArgInt(args, "-skiercap", 0), draw = ArgInt(args, "-draw", 0);
            var overrides = new StringBuilder();
            if (cap > 0) overrides.Append("SkierCap = ").Append(cap.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (draw > 0) overrides.Append("LocationDraw = ").Append(draw.ToString(CultureInfo.InvariantCulture)).Append('\n');
            var p = overrides.Length > 0 ? Pacing.Parse(overrides.ToString()) : Pacing.Default;

            var design = SeasonRunner.GuestsDesign();
            var w = new SimWorld(p, design, SimSite.None, 2026, 4242UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            long stop = SimWorld.DayMs + Domain.Design.DayHours.Default.StopSecond * 1000L;
            float start = Time.realtimeSinceStartup;
            int ticks = 0, peak = 0;
            while (w.FaceMs < stop)
            {
                ticks += w.Step(1);
                if (w.SkierCount > peak) peak = w.SkierCount;
            }
            ticks += w.Step(1);                 // the stop's own tick: the day is counted and the hill cleared
            float seconds = Time.realtimeSinceStartup - start;

            var g = w.GuestLedger();
            var text = new StringBuilder();
            string[] bands = { "beginner", "intermediate", "advanced", "expert" };
            text.Append("Guests, one face day (task 4.12)").Append(Environment.NewLine);
            text.Append($"Pacing v{Pacing.Version} 0x{p.ContentsHash:X16}, cap {p.SkierCap}, draw {p.LocationDraw}").Append(Environment.NewLine);
            text.Append($"Demand {g.demand}: arrived {g.arrived}, came to a closed mountain {g.arrivedClosed}, found it full {g.capped}")
                .Append(Environment.NewLine);
            text.Append($"Left {g.left}, went home from a line {g.wentHome}, stranded {g.stranded}, peak on the hill {peak}")
                .Append(Environment.NewLine);
            for (int a = 0; a < Pacing.Archetypes; a++)
            {
                var v = w.VerdictOf(a);
                text.Append($"  {bands[a],-13} wanted {w.DemandOf(a),5}  came {v.arrived,5}  mean satisfaction {v.meanSat / 10,3}%" +
                            $"  reputation {w.ReputationOf(a) / 10,3}%").Append(Environment.NewLine);
            }
            text.Append("Satisfaction by reason (thousandths):").Append(Environment.NewLine);
            for (var r = SatisfactionReason.Wait; r <= SatisfactionReason.Stranded; r++)
            {
                long sum = 0;
                for (int a = 0; a < Pacing.Archetypes; a++) sum += w.ReasonSum(a, r);
                if (sum != 0) text.Append($"  {r,-12} {sum,8}").Append(Environment.NewLine);
            }
            text.Append($"Lift capacity {w.LiftCapacityToday()}, terrain capacity {w.TerrainCapacityToday()}").Append(Environment.NewLine);
            text.Append(w.GuestNetworkReport()).Append(Environment.NewLine);
            text.Append($"{ticks} ticks in {seconds:F1} s = {seconds * 1000 / Math.Max(1, ticks):F3} ms a tick")
                .Append(Environment.NewLine);
            bool ok = g.arrived == g.left && w.SkierCount == 0 && g.arrived > 0;
            text.Append(ok ? "PASS" : "FAIL").Append(" the skier ledger balances and the hill is clear for the night")
                .Append(Environment.NewLine);

            Debug.Log("[SimGuests] " + text);
            if (report != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, text.ToString());
            }
            Application.Quit(ok ? 0 : 1);
        }

        static int ArgInt(string[] args, string name, int fallback)
        {
            int i = Array.IndexOf(args, name);
            if (i < 0 || i + 1 >= args.Length) return fallback;
            return int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }
    }
}
