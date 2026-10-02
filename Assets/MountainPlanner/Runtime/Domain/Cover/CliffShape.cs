using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>
    /// The shape of a cliff (style tile, owner request: "cliffs that jut out, with changing volumes").
    /// A heightmap can only store one height per point, so real cliffs flatten into smooth, steep ramps.
    /// A cliff shell adds the missing volume: where the lidar is steep, the surface is pushed outward along
    /// its normal by this displacement, which combines:
    /// - buttresses and bays: large 3D noise, so the face bulges out and recedes along its length;
    /// - strata: horizontal beds a few metres thick that jut out as ledges, with recessed bedding planes
    ///   between them; beds differ in hardness, so some ledges stand out more than others;
    /// - joints: vertical fractures that cut back into the face, making columns and gullies;
    /// - fine roughness.
    /// Every term is a keyed function of world position, so the shell is identical across tile edges.
    /// </summary>
    public static class CliffShape
    {
        /// <summary>Slopes (degrees) where the shell starts and reaches full strength: grades of 90% and 148%.</summary>
        public const double SlopeStart = 42, SlopeFull = 56;
        public const double ButtressMetres = 4.5, LedgeMetres = 1.8, JointMetres = 1.6, RoughMetres = 0.45;
        public const double BedMetres = 5.5;
        /// <summary>Where the shell fades out it tucks this far under the terrain, so its edge never shows.</summary>
        public const double TuckMetres = 0.6;
        /// <summary>At full strength the shell always stands at least this far off the lidar surface, so the
        /// two never share pixels (that flickered as fuzzy stipple).</summary>
        public const double LiftMetres = 0.35;

        /// <summary>Shell strength (0–1) from the local slope.</summary>
        public static double Weight(double slopeDegrees) => GroundCover.SmoothStep(SlopeStart, SlopeFull, slopeDegrees);

        /// <summary>
        /// Outward displacement in metres at a surface point (x east, y north, h up, all metres), for a shell
        /// weight 0–1. Negative only where the shell fades (it tucks under the terrain).
        /// </summary>
        public static double Displacement(ulong seed, double x, double y, double h, double weight)
        {
            if (weight <= 0) return -TuckMetres;
            double bulk = Fbm3(seed, x / 34, y / 34, h / 22, 3);
            double buttress = ButtressMetres * GroundCover.SmoothStep(0.35, 0.72, bulk);

            // Beds undulate gently across the face; each has its own hardness.
            double phase = h / BedMetres + 0.8 * CoverNoise.At(seed + 11, x, y, 70) + 0.2 * CoverNoise.At(seed + 12, x, y, 17);
            double bed = Math.Floor(phase), f = phase - bed;
            double hardness = 0.35 + 0.65 * (CoverNoise.Lattice(seed + 13, (long)bed, 0) + 1) * 0.5;
            double ledge = LedgeMetres * hardness * GroundCover.SmoothStep(0.04, 0.22, f) * (1 - GroundCover.SmoothStep(0.72, 0.96, f));

            double j = CoverNoise.At(seed + 21, x + 0.3 * h, y - 0.2 * h, 13);
            double joint = JointMetres * Math.Pow(Math.Max(0, 1 - Math.Abs(j) * 5), 3);

            double rough = RoughMetres * (Fbm3(seed + 31, x / 3.2, y / 3.2, h / 3.2, 2) - 0.5) * 2;

            double d = LiftMetres + Math.Max(0, buttress + ledge - joint + rough);
            return -TuckMetres + (d + TuckMetres) * weight;
        }

        /// <summary>Keyed 3D value noise in 0–1 (trilinear between lattice points).</summary>
        public static double Value3(ulong seed, double x, double y, double z)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            long ix = (long)fx, iy = (long)fy, iz = (long)fz;
            double tx = S(x - fx), ty = S(y - fy), tz = S(z - fz);
            double L(long a, long b, long c) => (CoverNoise.Lattice(seed ^ ((ulong)c * 0xD6E8FEB86659FD93UL), a, b) + 1) * 0.5;
            double Plane(long c)
            {
                double top = L(ix, iy, c) + (L(ix + 1, iy, c) - L(ix, iy, c)) * tx;
                double bottom = L(ix, iy + 1, c) + (L(ix + 1, iy + 1, c) - L(ix, iy + 1, c)) * tx;
                return top + (bottom - top) * ty;
            }
            double p0 = Plane(iz), p1 = Plane(iz + 1);
            return p0 + (p1 - p0) * tz;
        }

        public static double Fbm3(ulong seed, double x, double y, double z, int octaves)
        {
            double sum = 0, amp = 0.5, norm = 0;
            for (int o = 0; o < octaves; o++, x *= 2.03, y *= 2.03, z *= 2.03, amp *= 0.5)
            {
                sum += amp * Value3(seed + (ulong)o * 7919UL, x, y, z);
                norm += amp;
            }
            return sum / norm;
        }

        static double S(double t) => t * t * (3 - 2 * t);
    }
}
