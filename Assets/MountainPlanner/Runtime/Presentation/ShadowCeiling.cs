using System;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The mountain's shadow ceiling in plain C# (task 4.06): per texel, the height under which the air is in the terrain's
    /// shadow. FarShadow.compute's CeilingStart and CeilingStep kernels build the map the weather's screen pass reads
    /// (<see cref="FarTerrainShadow"/>); this is the same sum, step for step, so tests can pin what the kernels must give
    /// without a GPU. A point is shadowed when a ray from it toward the light meets terrain, so the ceiling at x is the
    /// highest of height(x + light * t) - t * rise over every distance t: found here by doubling (steps of 1, 2, 4, ...
    /// texels, each taking the higher of a texel and the texel a step toward the light less the ray's rise over that step).
    /// </summary>
    public static class ShadowCeiling
    {
        /// <summary>Under every height the terrain can have: a texel off the map.</summary>
        public const float Nothing = -10000;

        /// <summary>The terrain's highest over each scale x scale block (CeilingStart).</summary>
        public static float[] Start(float[] heights, int width, int depth, int scale, out int outWidth, out int outDepth)
        {
            if (heights == null) throw new ArgumentNullException(nameof(heights));
            if (scale < 1 || width < 1 || depth < 1 || heights.Length < width * depth) throw new ArgumentException("the height map's size");
            outWidth = Math.Max(1, (width + scale - 1) / scale);
            outDepth = Math.Max(1, (depth + scale - 1) / scale);
            var result = new float[outWidth * outDepth];
            for (int y = 0; y < outDepth; y++)
            for (int x = 0; x < outWidth; x++)
            {
                float top = Nothing;
                for (int j = 0; j < scale; j++)
                for (int i = 0; i < scale; i++)
                    top = Math.Max(top, heights[Math.Min(y * scale + j, depth - 1) * width + Math.Min(x * scale + i, width - 1)]);
                result[y * outWidth + x] = top;
            }
            return result;
        }

        /// <summary>One doubling pass (CeilingStep): the higher of each texel and the texel <paramref name="step"/> texels
        /// toward the light (bilinear, as the kernel samples) less <paramref name="drop"/> metres.</summary>
        public static void Step(float[] from, float[] to, int width, int depth, float lightX, float lightZ, float step, float drop)
        {
            for (int y = 0; y < depth; y++)
            for (int x = 0; x < width; x++)
            {
                float qx = x + 0.5f + lightX * step, qy = y + 0.5f + lightZ * step;
                float there = Nothing;
                if (qx >= 0 && qy >= 0 && qx <= width && qy <= depth) there = Bilinear(from, width, depth, qx, qy) - drop;
                to[y * width + x] = Math.Max(from[y * width + x], there);
            }
        }

        /// <summary>The ceiling over a coarse height map (texels <paramref name="metres"/> wide) for a light toward
        /// (lightX, lightZ), a horizontal unit vector, at tan(elevation) <paramref name="tanElevation"/>.</summary>
        public static float[] Build(float[] coarse, int width, int depth, float metres, float lightX, float lightZ, float tanElevation)
        {
            var from = (float[])coarse.Clone();
            var to = new float[from.Length];
            for (int step = 1; step < Math.Max(width, depth); step *= 2)
            {
                Step(from, to, width, depth, lightX, lightZ, step, step * metres * tanElevation);
                (from, to) = (to, from);
            }
            return from;
        }

        /// <summary>The same ceiling the slow way: every whole distance toward the light, one at a time. Equal to
        /// <see cref="Build"/> when the light lies along an axis (every sample lands on a texel).</summary>
        public static float[] BruteForce(float[] coarse, int width, int depth, float metres, float lightX, float lightZ, float tanElevation)
        {
            int reach = 1;
            while (reach < Math.Max(width, depth)) reach *= 2;
            var result = new float[coarse.Length];
            for (int y = 0; y < depth; y++)
            for (int x = 0; x < width; x++)
            {
                float top = coarse[y * width + x];
                for (int t = 1; t < reach; t++)
                {
                    float qx = x + 0.5f + lightX * t, qy = y + 0.5f + lightZ * t;
                    if (qx < 0 || qy < 0 || qx > width || qy > depth) break;
                    top = Math.Max(top, Bilinear(coarse, width, depth, qx, qy) - t * metres * tanElevation);
                }
                result[y * width + x] = top;
            }
            return result;
        }

        /// <summary>How much of the sun reaches a point at <paramref name="height"/> over a texel whose ceiling is
        /// <paramref name="ceiling"/>: 0 well under it, 1 well over, easing across <paramref name="softness"/> metres
        /// (Weather.hlsl's WxSunLit).</summary>
        public static float Lit(float height, float ceiling, float softness) =>
            Math.Min(1, Math.Max(0, (height - ceiling) / softness + 0.5f));

        // A bilinear tap at (x, y) in texels (texel centres at +0.5), clamped at the edges, like sampler_linear_clamp.
        static float Bilinear(float[] map, int width, int depth, float x, float y)
        {
            float fx = Math.Min(Math.Max(x - 0.5f, 0), width - 1), fy = Math.Min(Math.Max(y - 0.5f, 0), depth - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, depth - 1);
            float tx = fx - x0, ty = fy - y0;
            float a = map[y0 * width + x0] + (map[y0 * width + x1] - map[y0 * width + x0]) * tx;
            float b = map[y1 * width + x0] + (map[y1 * width + x1] - map[y1 * width + x0]) * tx;
            return a + (b - a) * ty;
        }
    }
}
