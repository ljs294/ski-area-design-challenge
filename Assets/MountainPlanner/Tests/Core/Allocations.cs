using System;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.26 BB8: the per-thread allocation counter can run a few KB high when a collection lands inside the measured
    /// window (seen on CI's parallel run: 8,200 and 14,288 B against a steady 6,152 B). A real allocation in a step happens on
    /// every run, so the least of a few runs still shows it while a one-off counter jump drops out.
    /// </summary>
    static class Allocations
    {
        public const int Runs = 3;

        /// <summary>The least of <paramref name="runs"/> measurements, each a fresh call of <paramref name="bytes"/>.</summary>
        public static long Least(Func<long> bytes, int runs = Runs)
        {
            long least = long.MaxValue;
            for (int k = 0; k < runs; k++) least = Math.Min(least, bytes());
            return least;
        }
    }
}
