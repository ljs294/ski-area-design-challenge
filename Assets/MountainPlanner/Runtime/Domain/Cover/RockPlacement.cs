using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>
    /// Where rocks go (style tile, owner request): deterministic rules from the lidar slope and the land
    /// cover, so real cliffs get outcrops and real scree slopes get boulders.
    /// - Outcrops: big stepped slabs on the steepest faces (over about 42°).
    /// - Boulders: talus on steep slopes and on bare ground; many small, few large.
    /// - None on water or built-up land, few inside forest; boulders thin out beyond the core.
    /// </summary>
    public static class RockPlacement
    {
        public const int BoulderVariants = 6;
        public const int OutcropVariants = 4;
        public const int Prototypes = BoulderVariants + OutcropVariants;
        public const int CellMetres = 10;
        public const double OutcropSlopeStart = 48, OutcropSlopeFull = 65;
        /// <summary>
        /// Winter: boulders on skiable slopes are buried, so visible talus starts on steep, wind-scoured ground.
        /// </summary>
        public const double TalusSlopeStart = 36, TalusSlopeFull = 50;
        /// <summary>Boulders per 10 m cell where the ground is fully rocky.</summary>
        public const double MaxBouldersPerCell = 0.9;
        /// <summary>Boulders stop this far beyond the core (too small to see); outcrops go everywhere.</summary>
        public const double BoulderRingMetres = 1500;

        /// <summary>How rocky a cell is (0–1): steep slope, bare land cover, less under forest.</summary>
        public static double Rockiness(double slopeDegrees, double bareShare, double treeShare)
        {
            double steep = GroundCover.SmoothStep(TalusSlopeStart, TalusSlopeFull, slopeDegrees);
            double r = Math.Max(steep, 0.4 * bareShare);
            return r * (1 - 0.7 * Math.Min(1, treeShare * 2));
        }

        /// <summary>Chance of an outcrop in a cell (0–0.3).</summary>
        public static double OutcropChance(double slopeDegrees) => 0.3 * GroundCover.SmoothStep(OutcropSlopeStart, OutcropSlopeFull, slopeDegrees);

        /// <summary>Boulder height in metres from a uniform draw: mostly small, a few large (0.5–4 m).</summary>
        public static double BoulderSize(double u) => 0.5 + 3.5 * u * u * u;

        /// <summary>Outcrop height in metres (5–16 m).</summary>
        public static double OutcropSize(double u) => 5 + 11 * u;
    }
}
