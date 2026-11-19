using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>BG-03: <see cref="SolveBench"/>'s 500 guns on about 200 pipes solve in 20 ms or less, the SG-07 re-solves included.</summary>
    public sealed class SolveBenchTests
    {
        [Test]
        public void FiveHundredGunsSolveWithinBudget()
        {
            SolveBench.Build();   // JIT
            var compileWatch = Stopwatch.StartNew();
            var b = SolveBench.Build();
            compileWatch.Stop();
            var g = b.Graph;
            Assert.That(g.Guns.Length, Is.EqualTo(SolveBench.Guns));
            Assert.That(b.Network.Pipes.Count, Is.GreaterThanOrEqualTo(200));
            var asks = b.Asks();

            // Warm-up, then the median of seven cold solves (a fresh solver each: ordering, factor, stages).
            new SnowSolve(g).Run(asks, b.Level);
            var cold = new List<double>();
            SnowSolve s = null;
            for (int run = 0; run < 7; run++)
            {
                var w = Stopwatch.StartNew();
                s = new SnowSolve(g);
                bool ok = s.Run(asks, b.Level);
                w.Stop();
                Assert.That(ok, Is.True);
                cold.Add(w.Elapsed.TotalMilliseconds);
            }
            int coldResolves = s.Resolves, coldSteps = s.TotalIterations, coldRunning = s.GunStates.Count(x => x == GunState.Running);

            // Warm: a gun switched off, solved again from the last answer (the tools' live preview).
            var warm = new List<double>();
            for (int run = 0; run < 7; run++)
            {
                asks[run].Wanted = false;
                var w = Stopwatch.StartNew();
                bool ok = s.Run(asks, b.Level);
                w.Stop();
                Assert.That(ok, Is.True);
                warm.Add(w.Elapsed.TotalMilliseconds);
            }
            double coldMs = Median(cold), warmMs = Median(warm);
            TestContext.Progress.WriteLine(
                $"BENCH snow solve: {g.NodeCount} nodes, {g.LinkCount} links, {b.Network.Pipes.Count} pipes, {SolveBench.Guns} guns; cold {coldRunning} running, " +
                $"{coldResolves} re-solves, {coldSteps} Newton steps; warm {s.Resolves} re-solves, {s.TotalIterations} steps: build {compileWatch.Elapsed.TotalMilliseconds:F1} ms, " +
                $"cold {coldMs:F2} ms, warm {warmMs:F2} ms");
            Assert.That(coldResolves, Is.GreaterThan(0), "the bench should exercise SG-07's re-solves");
            Assert.That(coldMs, Is.LessThanOrEqualTo(SolveBench.BudgetMs), "BG-03: 500 guns cold within 20 ms");
        }

        static double Median(List<double> xs)
        {
            var a = xs.OrderBy(x => x).ToArray();
            return a[a.Length / 2];
        }
    }
}
