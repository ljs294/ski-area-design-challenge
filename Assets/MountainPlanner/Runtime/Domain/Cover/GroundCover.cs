using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>The terrain's ground layers, in splat order (0.5 §2). Snow lies over all of them.</summary>
    public enum GroundLayer
    {
        ForestFloor = 0,
        Grass = 1,
        Rock = 2,
        Developed = 3,
        Water = 4,
    }

    /// <summary>
    /// Everything the ground-cover rules need at one point, already sampled smoothly from the
    /// package's layers: soft weights (0–1), not hard classes, so 10 m cells never show as squares.
    /// </summary>
    public struct CoverSample
    {
        /// <summary>Share of nearby 1 m canopy cells with trees (core only).</summary>
        public double Canopy;
        /// <summary>How much to trust <see cref="Canopy"/> over WorldCover trees: 1 inside the core, easing to 0 at its edge.</summary>
        public double CanopyWeight;
        /// <summary>WorldCover soft weights by group, bilinear between 10 m cells (they sum to about 1).</summary>
        public double WcTrees, WcGrass, WcRock, WcDeveloped, WcWater;
        /// <summary>OpenStreetMap coverage from anti-aliased 1–2 m rasters.</summary>
        public double OsmWater, OsmDeveloped;
        /// <summary>Terrain slope in degrees, from the lidar.</summary>
        public double SlopeDegrees;
        /// <summary>Keyed noise in −1…1 at this point, for irregular edges.</summary>
        public double Noise;
    }

    /// <summary>
    /// The ground-cover rules (0.3 §4.4, T6): turn a <see cref="CoverSample"/> into five ground-layer
    /// weights and a snow cover, all deterministic.
    /// - Water and developed land come first: OpenStreetMap shapes where mapped, WorldCover otherwise.
    /// - Forest floor follows the canopy map in the core, WorldCover trees in the ring. In the core,
    ///   WorldCover "trees" where the canopy is open become meadow, so real ski runs stay open.
    /// - Rock shows on steep faces (from the lidar) and where WorldCover says bare or ice.
    /// - Snow covers everything in iteration 1 (T8) except rock faces steeper than about 55° (A2).
    /// </summary>
    public static class GroundCover
    {
        public const int Layers = 5;
        /// <summary>Slopes (degrees) where the ground turns to rock (under the snow): grades of 78% to 119%.</summary>
        public const double RockSlopeStart = 38, RockSlopeFull = 50;
        /// <summary>Slopes (degrees) where snow can't hold and bare rock shows (A2): grades of 128% to 173%.</summary>
        public const double BareStart = 52, BareFull = 60;
        /// <summary>How far noise shifts the slope thresholds, in degrees.</summary>
        public const double SlopeJitter = 3;

        /// <summary>WorldCover classes grouped into the game's layers (10 trees … 100 moss and lichen).</summary>
        public static int Group(byte worldCoverClass)
        {
            switch (worldCoverClass)
            {
                case 10: return 0;                                              // trees
                case 20: case 30: case 40: case 90: case 95: case 100: return 1; // shrubs, grass, crops, wetland, mangrove, moss
                case 60: case 70: return 2;                                     // bare, snow and ice
                case 50: return 3;                                              // built-up
                case 80: return 4;                                              // water
                default: return -1;                                             // no data
            }
        }

        /// <summary>
        /// Weights for the five ground layers (summing to 1) and the snow cover (0–1) at one point.
        /// </summary>
        public static void Classify(in CoverSample s, Span<double> weights, out double snow)
        {
            double water = Math.Max(s.OsmWater, Sharpen(s.WcWater));
            double developed = Math.Max(s.OsmDeveloped, Sharpen(s.WcDeveloped)) * (1 - water);
            double land = Math.Max(0, 1 - water - developed);

            double slope = s.SlopeDegrees + s.Noise * SlopeJitter;
            double steep = SmoothStep(RockSlopeStart, RockSlopeFull, slope);
            double bare = SmoothStep(BareStart, BareFull, slope);

            // Any trees nearby darken the floor; half cover is full forest.
            double canopyForest = SmoothStep(0.05, 0.45, s.Canopy);
            double forest = Sharpen(s.WcTrees) + (canopyForest - Sharpen(s.WcTrees)) * s.CanopyWeight;
            forest *= 1 - bare;                                // cliffs are rock even among trees

            double wcLand = s.WcTrees + s.WcGrass + s.WcRock;
            double wcRock = wcLand > 1e-9 ? s.WcRock / wcLand : 0;
            double rock = Math.Max(steep, Sharpen(wcRock)) * (1 - forest);
            double grass = Math.Max(0, 1 - forest - rock);

            weights[(int)GroundLayer.ForestFloor] = forest * land;
            weights[(int)GroundLayer.Grass] = grass * land;
            weights[(int)GroundLayer.Rock] = rock * land;
            weights[(int)GroundLayer.Developed] = developed;
            weights[(int)GroundLayer.Water] = water;
            snow = 1 - bare * (1 - water);
        }

        /// <summary>Quantizes weights to bytes that sum to exactly 255 (largest remainders get the leftovers).</summary>
        public static void Quantize(ReadOnlySpan<double> weights, Span<byte> output)
        {
            double total = 0;
            for (int i = 0; i < weights.Length; i++) total += Math.Max(0, weights[i]);
            if (total <= 0)
            {
                for (int i = 0; i < output.Length; i++) output[i] = 0;
                output[(int)GroundLayer.Grass] = 255;
                return;
            }
            int sum = 0;
            Span<double> remainder = stackalloc double[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                double v = Math.Max(0, weights[i]) / total * 255;
                int q = (int)Math.Floor(v);
                output[i] = (byte)q;
                remainder[i] = v - q;
                sum += q;
            }
            for (; sum < 255; sum++)
            {
                int best = 0;
                for (int i = 1; i < weights.Length; i++) if (remainder[i] > remainder[best]) best = i;
                output[best]++;
                remainder[best] = -1;
            }
        }

        public static byte ToByte(double v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255, MidpointRounding.AwayFromZero);

        public static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = (x - edge0) / (edge1 - edge0);
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return t * t * (3 - 2 * t);
        }

        /// <summary>Tightens a bilinear class weight into a narrow soft edge around the 50% line.</summary>
        static double Sharpen(double w) => SmoothStep(0.3, 0.7, w);
    }

    /// <summary>
    /// Keyed value noise (deterministic, no Random): smooth in −1…1, two octaves. Used to warp class
    /// boundaries and jitter slope thresholds so no straight 10 m edges survive.
    /// </summary>
    public static class CoverNoise
    {
        public static double At(ulong seed, double x, double y, double wavelength)
        {
            double a = Octave(seed, x / wavelength, y / wavelength);
            double b = Octave(seed ^ 0x9E3779B97F4A7C15UL, x * 2.03 / wavelength, y * 2.03 / wavelength);
            return (a * 2 + b) / 3;
        }

        static double Octave(ulong seed, double x, double y)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y);
            long ix = (long)fx, iy = (long)fy;
            double tx = x - fx, ty = y - fy;
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);
            double v00 = Lattice(seed, ix, iy), v10 = Lattice(seed, ix + 1, iy), v01 = Lattice(seed, ix, iy + 1), v11 = Lattice(seed, ix + 1, iy + 1);
            double top = v00 + (v10 - v00) * tx, bottom = v01 + (v11 - v01) * tx;
            return top + (bottom - top) * ty;
        }

        /// <summary>A hash of (seed, x, y) mapped to −1…1 (SplitMix64 finalizer).</summary>
        public static double Lattice(ulong seed, long x, long y)
        {
            ulong h = seed ^ (ulong)x * 0xBF58476D1CE4E5B9UL ^ (ulong)y * 0x94D049BB133111EBUL;
            h ^= h >> 30;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 27;
            h *= 0x94D049BB133111EBUL;
            h ^= h >> 31;
            return (h >> 11) * (2.0 / (1UL << 53)) - 1;
        }

        /// <summary>A resort's noise seed from its site centre (whole metres), so re-downloads look the same.</summary>
        public static ulong SeedFor(double centreX, double centreY)
        {
            ulong h = 0x5EED0C0FEEUL;
            h = Mix(h ^ (ulong)(long)Math.Round(centreX));
            h = Mix(h ^ (ulong)(long)Math.Round(centreY));
            return h;
        }

        static ulong Mix(ulong h)
        {
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            h *= 0xC4CEB9FE1A85EC53UL;
            h ^= h >> 33;
            return h;
        }
    }
}
