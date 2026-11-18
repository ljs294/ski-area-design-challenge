using System;
using System.IO;
using System.Text;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -simridedown &lt;report.txt&gt; (task 4.24 PR B): plays one face day of guests on <see cref="SeasonRunner.RideDownDesign"/>
    /// twice, lift 303 as a gondola and then as a detachable chair on the same line, and writes what the gondola changed:
    /// each lift's rides up, its rides down (RD-2), the top line's peak, who was stranded, and whether the ledger balances.
    /// It is the headless half of demo 129, the build saying what the Core tests say under .NET.
    /// </summary>
    public sealed partial class AppFlow
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void SimRideDownAtBoot() => StartSimRideDownIfAsked(Environment.GetCommandLineArgs());

        static readonly ulong[] RideDownLifts = { 101, 202, 303 };

        static void StartSimRideDownIfAsked(string[] args)
        {
            int i = Array.IndexOf(args, "-simridedown");
            if (i < 0) return;
            string report = i + 1 < args.Length && !args[i + 1].StartsWith("-") ? Path.GetFullPath(args[i + 1]) : null;
            var text = new StringBuilder();
            text.Append("Riding down, one face day each way (task 4.24 PR B)").Append(Environment.NewLine);
            text.Append($"Pacing v{Pacing.Version} 0x{Pacing.Default.ContentsHash:X16}, RideDownScore {Pacing.Default.RideDownScore}, " +
                        $"sidecar v{SimSidecar.Version}").Append(Environment.NewLine);
            bool ok = true;
            foreach (bool gondola in new[] { true, false })
                ok &= Day(gondola, text);
            ok &= UpperMountainDay(text);
            text.Append(ok ? "PASS" : "FAIL").Append(" both days balance, nobody is stranded, and only the gondola carries anyone down")
                .Append(Environment.NewLine);
            Debug.Log("[SimRideDown] " + text);
            if (report != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(report));
                File.WriteAllText(report, text.ToString());
            }
            Application.Quit(ok ? 0 : 1);
        }

        /// <summary>
        /// The <c>-drawngondola</c> layout in miniature (<see cref="SeasonRunner.UpperMountainDesign"/>): the gondola's top
        /// line and its riders going down, hour by hour, so the review capture's hour is chosen from numbers, not hope.
        /// </summary>
        static bool UpperMountainDay(StringBuilder text)
        {
            var w = new SimWorld(Pacing.Default, SeasonRunner.UpperMountainDesign(), SimSite.None, 2026, 4242UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            long stop = SimWorld.DayMs + Domain.Design.DayHours.Default.StopSecond * 1000L;
            text.Append(Environment.NewLine).Append("Upper mountain served only by gondola 303 (the -drawngondola layout)").Append(Environment.NewLine);
            int peakTop = 0, peakDown = 0, downs = 0;
            for (int hour = 9; hour <= 16; hour++)
            {
                while (w.FaceMs < SimWorld.DayMs + hour * SimWorld.HourMs) w.Step(1);
                var d = w.DownLedgerOf(303);
                downs = Math.Max(downs, d.ridesDown);
                text.Append($"  {hour,2}:00  {w.SkierCount,4} on the hill, {d.topLine,3} at the gondola's top, {d.ridingDown,3} riding down, " +
                            $"{d.ridesDown,4} down so far").Append(Environment.NewLine);
            }
            while (w.FaceMs < stop)
            {
                w.Step(1);
                var d = w.DownLedgerOf(303);
                peakTop = Math.Max(peakTop, d.topLine);
                peakDown = Math.Max(peakDown, d.ridingDown);
                downs = Math.Max(downs, d.ridesDown);
            }
            w.Step(1);
            var g = w.GuestLedger();
            text.Append($"  day: arrived {g.arrived}, left {g.left}, stranded {g.stranded}, {downs} rode down; after 16:00 the top line peaked at " +
                        $"{peakTop} and {peakDown} rode at once").Append(Environment.NewLine);
            return g.arrived == g.left && g.stranded == 0 && downs > 0;
        }

        static bool Day(bool gondola, StringBuilder text)
        {
            var w = new SimWorld(Pacing.Default, SeasonRunner.RideDownDesign(gondola), SimSite.None, 2026, 4242UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            long stop = SimWorld.DayMs + Domain.Design.DayHours.Default.StopSecond * 1000L;
            var peakTop = new int[RideDownLifts.Length];
            var downs = new int[RideDownLifts.Length];
            float start = Time.realtimeSinceStartup;
            int ticks = 0;
            while (w.FaceMs < stop)
            {
                ticks += w.Step(1);
                for (int k = 0; k < RideDownLifts.Length; k++)
                {
                    var d = w.DownLedgerOf(RideDownLifts[k]);
                    peakTop[k] = Math.Max(peakTop[k], d.topLine);
                    downs[k] = Math.Max(downs[k], d.ridesDown);   // the ledger clears at the stop: keep the day's last count
                }
            }
            ticks += w.Step(1);
            float seconds = Time.realtimeSinceStartup - start;
            var g = w.GuestLedger();
            text.Append(Environment.NewLine).Append(gondola ? "Lift 303 a gondola" : "Lift 303 a detachable chair").Append(Environment.NewLine);
            text.Append($"  arrived {g.arrived}, left {g.left}, went home from a line {g.wentHome}, stranded {g.stranded}").Append(Environment.NewLine);
            bool ok = g.arrived == g.left && w.SkierCount == 0 && g.arrived > 0 && g.stranded == 0;
            for (int k = 0; k < RideDownLifts.Length; k++)
            {
                var l = w.LedgerOf(RideDownLifts[k]);
                text.Append($"  lift {RideDownLifts[k]}: {l.rides,6} up, {downs[k],5} down, peak line {l.peakLine,4}, " +
                            $"peak top line {peakTop[k],4}, left a line {l.leftInLine}").Append(Environment.NewLine);
                if (downs[k] > 0 && !(gondola && RideDownLifts[k] == 303)) ok = false;
            }
            var sat = new StringBuilder();
            for (int a = 0; a < Pacing.Archetypes; a++) sat.Append(a == 0 ? "" : " / ").Append(w.VerdictOf(a).meanSat / 10).Append('%');
            text.Append($"  mean satisfaction by band {sat}; {ticks} ticks in {seconds:F1} s").Append(Environment.NewLine);
            return ok;
        }
    }
}
