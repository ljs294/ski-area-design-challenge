using System;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2: the golden snow weeks. The real Jackson Hole 2 km package (256 × 256 cells at 8 m, the cells' mean
    /// heights) under its frozen weather rows, from bare October 1 to November 15 (14 face days): storms, thaws and refreezes. Every
    /// day's ledger balances to the µm, the snow has its natural types, no cell piles up (SN-06 v2's run-out fan), and the
    /// days' tile hashes fold to one pinned value. A behaviour change re-pins it, approved (old → new in the comment).
    /// </summary>
    public sealed class SnowGoldenWeekTests
    {
        const int W = 256, D = 256;

        // 4.07 PR A2 round 2 (2026-11-14, for the owner's OK at the review): WX-35 v3 and WX-02 v3 (the precipitation and the
        // snow's read follow the ground), SN-07 v3's drift, SN-09 v3's crumb, SN-06 v3's cone, the crop through SurfaceSampler;
        // over the first pin 0x2FC0AB70D6381D58.
        const ulong GoldenWeeksFold = 0x55DBFD753C91126DUL;

        [Test]
        public void SN_TheGoldenWeeksOnJacksonHole()
        {
            var m = SnowRunoutProbeTests.JacksonHole();
            var p = Pacing.Default;
            var w = SnowFieldTests.World(p, true, m);
            ulong fold = 14_695_981_039_346_656_037UL;
            int bad = 0, days = 0;
            var seen = new bool[11];
            SnowFieldTests.RunTo(w, 11, 15, p.IdleTickFactor, l =>
            {
                days++;
                if (l.Balance != 0) bad++;
                foreach (ulong h in w.Snow.TileHashes) { fold ^= h; fold *= 1_099_511_628_211UL; }
                for (int j = 0; j < D; j += 2)
                    for (int i = 0; i < W; i += 2)
                        seen[(int)w.Snow.TypeAt(i, j)] = true;
            });
            long deepest = 0, sum = 0;
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++) { long d = w.Snow[i, j]; sum += d; deepest = Math.Max(deepest, d); }
            double meanIn = sum / (double)(W * D) / 25.4, deepIn = deepest / 25.4;
            TestContext.Progress.WriteLine($"[golden weeks] {days} days, fold 0x{fold:X16}; Nov 15 mean {meanIn:F1} in, deepest {deepIn:F0} in; types:"
                                           + string.Concat(Array.ConvertAll(Enumerable(seen), k => " " + (SnowType)k)));
            Assert.That(bad, Is.Zero, "every day's ledger balances");
            // A day's end is night: a thaw shows as the crust or the ice it froze into.
            Assert.That(seen[(int)SnowType.P], Is.True, "powder");
            Assert.That(seen[(int)SnowType.FG] || seen[(int)SnowType.IS], Is.True, "a thaw, frozen into a crust or ice");
            Assert.That(deepIn, Is.LessThan(Math.Max(24, 20 * meanIn)), "no cell piles up (SN-06 v2)");
            Assert.That(fold, Is.EqualTo(GoldenWeeksFold), $"the golden weeks moved: 0x{fold:X16}");
        }

        static int[] Enumerable(bool[] seen)
        {
            var list = new System.Collections.Generic.List<int>();
            for (int k = 0; k < seen.Length; k++) if (seen[k]) list.Add(k);
            return list.ToArray();
        }
    }
}
