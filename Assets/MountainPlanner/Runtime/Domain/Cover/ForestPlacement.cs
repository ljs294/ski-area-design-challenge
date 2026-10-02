using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>One placed tree, in a tile's local frame (metres east and north of its south-west corner).</summary>
    public struct PlacedTree
    {
        public float X, Z;
        public float Height;
        /// <summary>Model × 3 + variant (see <see cref="Flora.SpeciesMap"/>).</summary>
        public byte Prototype;
        /// <summary>Rotation in 1/256 turns.</summary>
        public byte Rotation;
        /// <summary>Crown width relative to the model's own proportions.</summary>
        public float WidthScale;
    }

    /// <summary>
    /// The forest rules (0.3 §4.5, T7), all deterministic from keyed hashes:
    /// - Trees stand only where the 1 m canopy map has trees (so real ski runs, glades and tree islands
    ///   stay open), as many per 10 m cell as the calibrated cover and crown size allow.
    /// - Dominant height comes from the cell's tallest canopy value, calibrated (D4); each tree varies below it.
    /// - Outside the canopy map (the ring), WorldCover forest cells get trees at a density that thins with
    ///   distance from the core, with wider crowns so distant forest still reads as forest.
    /// </summary>
    public static class ForestPlacement
    {
        public const int CellMetres = 10;
        public const int MaxTreesPerCell = 14;
        public const double MinHeight = 3, MaxHeight = 50;
        /// <summary>Ring tree share per WorldCover forest cell when the core can't calibrate it (no canopy map).</summary>
        public const double WorldCoverTreeShare = 0.5;
        /// <summary>Ring density falls to this share of the core's over this distance beyond the core.</summary>
        public const double RingMinDensity = 0.1, RingFadeMetres = 2000;
        /// <summary>A typical dominant height where only WorldCover is known.</summary>
        public const double RingDominantHeight = 18;

        /// <summary>Crown radius for a tree of this height (metres): narrow subalpine crowns.</summary>
        public static double CrownRadius(double height) => Math.Max(1.2, Math.Min(4.5, 0.1 * height + 0.6));

        /// <summary>Calibrated trees in one 10 m cell with this tree share (0–1) and dominant height.</summary>
        public static double TreesPerCell(double treeShare, double dominantHeight, ForestCalibration calibration)
        {
            double r = CrownRadius(dominantHeight);
            double crowns = CellMetres * CellMetres / (Math.PI * r * r);
            return Math.Min(MaxTreesPerCell, treeShare * calibration.DensityFactor * crowns);
        }

        /// <summary>Ring thinning: 1 at the core's edge, easing to <see cref="RingMinDensity"/>.</summary>
        public static double RingDensity(double metresOutsideCore)
        {
            double t = GroundCover.SmoothStep(0, RingFadeMetres, metresOutsideCore);
            return 1 + (RingMinDensity - 1) * t;
        }

        /// <summary>Shade-tolerant conifer share and canopy share over which a cell turns into a dense conifer stand (NE8).</summary>
        public const double StandTolerantFrom = 0.3, StandTolerantTo = 0.7, StandCanopyFrom = 0.4, StandCanopyTo = 0.7;

        /// <summary>
        /// How fully (0–1) a cell grows as a dense conifer stand: shade-tolerant conifers (spruce, fir, hemlock) under a
        /// closed canopy. They grow up under their own shade, so their stands are multi-storied and clumped; pine and
        /// Douglas-fir stands are more even (lidar: Sugarloaf against Jackson Hole, forest-structure report).
        /// </summary>
        public static double StandWeight(double tolerantShare, double canopyShare) =>
            GroundCover.SmoothStep(StandTolerantFrom, StandTolerantTo, tolerantShare) * GroundCover.SmoothStep(StandCanopyFrom, StandCanopyTo, canopyShare);

        /// <summary>
        /// Tree height in a full dense conifer stand as a share of the dominant (1/256), at 17 evenly spaced
        /// quantiles, shortest first: share = 1 − (1 − <see cref="StandShortest"/>) (1 − u)^<see cref="StandSkew"/>,
        /// written out so that no build depends on its floating-point maths. Most trees stay near the canopy over a tail
        /// of intermediate and suppressed trees (calibrated against lidar at Sugarloaf and Jackson Hole, NE8).
        /// </summary>
        public static int[] StandHeightTable() => new[] { 90, 110, 129, 146, 162, 177, 191, 203, 214, 224, 233, 240, 246, 250, 253, 255, 256 };

        public const double StandShortest = 0.35, StandSkew = 2;

        /// <summary>The clump field (NE8): one clump centre per lattice square, its reach, and how many darts far from
        /// every clump are kept (of 256).</summary>
        public const int ClumpLattice256 = 14 * PoissonForest.Fixed, ClumpRadius256 = 8 * PoissonForest.Fixed, ClumpFloor = 96;

        /// <summary>A uniform 0–1 value for (seed, cell, index, salt).</summary>
        public static double Hash01(ulong seed, long cx, long cy, int k, int salt)
        {
            double v = CoverNoise.Lattice(seed ^ ((ulong)(uint)salt * 0x9E3779B97F4A7C15UL), cx * 131 + k, cy);
            return (v + 1) * 0.5;
        }
    }
}
