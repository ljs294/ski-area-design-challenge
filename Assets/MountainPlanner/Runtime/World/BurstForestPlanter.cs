using MountainPlanner.Domain.Cover;
using MountainPlanner.Persistence;
using Unity.Burst;

namespace MountainPlanner.World
{
    /// <summary>
    /// Grows a forest plan with Burst (0.3 §3, T2): the same <see cref="PoissonForest.PlaceTile"/> as the plain
    /// C# planter, Burst-compiled and called directly, so it runs on the worker threads that build the cache
    /// (jobs can only be scheduled from the main thread). Integer maths only, so the bytes match the plain C#
    /// planter's (EditMode test).
    /// </summary>
    [BurstCompile]
    public sealed class BurstForestPlanter : IForestPlanter
    {
        public unsafe void Plant(ForestPlan plan) => ForestPasses.Run(plan, PlaceTile);

        [BurstCompile(CompileSynchronously = true)]
        static unsafe int PlaceTile(ForestInputs* inputs, int tile, ForestScratch* scratch) =>
            PoissonForest.PlaceTile(ref *inputs, tile, ref *scratch);

        /// <summary>True when the planter runs Burst-compiled rather than as plain C# (tests, benchmarks).</summary>
        public static bool IsBurstCompiled() => RunsInBurst();

        [BurstCompile(CompileSynchronously = true)]
        static bool RunsInBurst()
        {
            bool managed = false;
            MarkManaged(ref managed);
            return !managed;
        }

        [BurstDiscard]
        static void MarkManaged(ref bool managed) => managed = true;
    }
}
