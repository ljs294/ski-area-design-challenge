#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// How many cores the parallel loops of the cache build (terrain tiles, forest) may take. Unlimited by default, as
    /// when a mountain opens and the player waits for it; a background download limits its own work to half the cores
    /// (task P2-06), so the game keeps its frame rate while it prepares terrain. The limit follows the async flow it was
    /// set in, so a foreground build running at the same time keeps every core.
    /// </summary>
    public static class WorkerLimits
    {
        static readonly AsyncLocal<int> Limit = new AsyncLocal<int>();

        /// <summary>The cores a background download's work may use: half of them, at least one.</summary>
        public static int Background => Math.Max(1, Environment.ProcessorCount / 2);

        /// <summary>Limits the parallel loops run from this async flow (and the tasks it starts) to <paramref name="cores"/>.</summary>
        public static void LimitThisFlow(int cores) => Limit.Value = Math.Max(1, cores);

        /// <summary>The current flow's limit, or -1 for none.</summary>
        public static int MaxDegreeOfParallelism => Limit.Value > 0 ? Limit.Value : -1;

        public static ParallelOptions Options(CancellationToken ct = default) =>
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = MaxDegreeOfParallelism };
    }
}
