using MountainPlanner.Domain.Water;

namespace MountainPlanner.Simulation
{
    /// <summary>Task 4.09: the body map's tuning from the Pacing table (SI-01), so Domain reads no Simulation type.</summary>
    public static class SimWater
    {
        /// <summary>LK-01 v2's numbers, with the simulation's id tag.</summary>
        public static WaterBodyTuning Tuning(Pacing p) => new WaterBodyTuning(
            (p.LakeWaterShare * 255 + 999) / 1000, p.LakeMinArea, p.RiverElongation, p.LakeGrowCells, p.MaxLakes, p.MaxPonds,
            p.LakeDepthMin, p.LakeDepthMax, p.StreamsDepth, SimKey.WaterBodyTag);
    }
}
