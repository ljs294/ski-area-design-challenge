using System;
using System.Diagnostics;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    [Explicit, Category("SnowReport")]
    public sealed class SnowProbeTests
    {
        static void Say(string s) { TestContext.Progress.WriteLine(s); }

        [Test]
        public void ProbeTheFieldByElevation()
        {
            var sw = Stopwatch.StartNew();
            var w = SnowFieldTests.World(Pacing.Default);
            var m = SnowFieldTests.Shared;
            int lastDay = -1;
            long fell = 0, canopy = 0, runoff = 0, sub = 0, melt = 0;
            while (w.FaceMs < 33 * SimWorld.DayMs)
            {
                w.Step(Pacing.Default.IdleTickFactor);
                if (w.SnowLastDay.Day != lastDay)
                {
                    lastDay = w.SnowLastDay.Day;
                    var l = w.SnowLastDay;
                    fell += l.Fell; canopy += l.Canopy; runoff += l.Runoff; sub += l.Sublimated; melt += l.Melt;
                    if (lastDay % 3 == 0) Report(w, m, $"day {lastDay}");
                }
            }
            double n = 256.0 * 256 * 1000;
            Say($"[probe] {sw.Elapsed.TotalSeconds:F1} s; per cell over 33 days: fell {fell / n:F0} mm water, canopy {canopy / n:F0}, runoff {runoff / n:F0}, sublimated {sub / n:F0}, melt {melt / n:F0}");
        }

        static void Report(SimWorld w, SnowFieldTests.Mountain m, string label)
        {
            const int Bands = 6;
            var swe = new double[Bands]; var depth = new double[Bands]; var count = new int[Bands];
            var types = new int[11];
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (int f in m.Feet) { lo = Math.Min(lo, f); hi = Math.Max(hi, f); }
            for (int j = 0; j < 256; j++)
                for (int i = 0; i < 256; i++)
                {
                    int c = j * 256 + i, b = Math.Min(Bands - 1, (m.Feet[c] - lo) * Bands / (hi - lo + 1));
                    var p = w.Snow.PackAt(i, j);
                    swe[b] += p.Swe / 1000.0; depth[b] += w.Snow[i, j]; count[b]++;
                    types[(int)w.Snow.TypeAt(i, j)]++;
                }
            var line = $"[probe] {label}:";
            for (int b = 0; b < Bands; b++)
            {
                double s = swe[b] / Math.Max(1, count[b]), d = depth[b] / Math.Max(1, count[b]);
                line += $" | {lo + (hi - lo) * (b + 0.5) / Bands:F0} ft: {s / 25.4:F1} in water, {d / 25.4:F1} in deep, {(d > 0 ? s / d * 1000 : 0):F0} kg/m3";
            }
            Say(line);
            var t = "[probe]   types:";
            for (int k = 0; k < 11; k++) if (types[k] > 0) t += $" {(SnowType)k} {types[k] * 100.0 / 65536:F0}%";
            Say(t + $"; wx {w.WeatherNow.Temp / 10.0:F0} F at zref");
        }
    }
}
