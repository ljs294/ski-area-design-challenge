using MountainPlanner.Domain.Cover;
using MountainPlanner.Persistence;

namespace MountainPlanner.World
{
    /// <summary>
    /// One tile's splat maps as raw RGBA32 bytes, ready for <see cref="TerrainTiles.ApplySplat"/>. Built
    /// on worker threads from the cached cover texels (task 07).
    /// Channel order follows the terrain layers: texture 0 = snow, forest floor, grass, rock;
    /// texture 1 = developed, water. Snow is layer 0 so an unpainted tile already reads as snow.
    /// </summary>
    public sealed class SplatTexels
    {
        /// <summary>
        /// Under a full forest canopy, this share of the snow gives way to the forest floor: shaded snow
        /// and needle litter, so a stand reads as a dark mass from a distance instead of trees on white.
        /// </summary>
        public const int ForestShade = 115; // of 255 (45%)
        /// <summary>Forest density is judged over this many metres, so lone trees and specks cast no stand shade.</summary>
        public const float ShadeRadiusMetres = 8;

        public int Resolution;
        public byte[][] Textures;

        /// <summary>
        /// Composes cover texels (north row first, six bytes each) into splat textures (south row first,
        /// as Unity's alphamaps are). With snow on, snow lies over the ground by its cover; with it off,
        /// the ground layers show alone (the "under the snow" view, T17).
        /// </summary>
        public static SplatTexels Compose(byte[] cover, int resolution, bool snow)
        {
            const int bands = TerrainCache.CoverBands;
            int n = resolution;
            var shade = StandShade(cover, n, snow);
            var t0 = new byte[n * n * 4];
            var t1 = new byte[n * n * 4];
            for (int j = 0; j < n; j++)
            {
                int src = j * n * bands, dst = (n - 1 - j) * n * 4;
                for (int i = 0; i < n; i++, src += bands, dst += 4)
                {
                    // Snow lies on the land; water keeps its weight under snow, so the terrain shader knows
                    // where the frozen lakes are and draws snow on ice there (MountainTerrain.shader).
                    int w = cover[src + 4], land = 255 - w;
                    int s = snow ? cover[src + GroundCover.Layers] * (255 - shade[j * n + i]) / 255 * land / 255 : 0;
                    int keep = land - s;
                    // Scale the land weights by what the snow leaves; rounding leftovers go to the largest.
                    int f = 0, g = 0, r = 0, d = 0;
                    if (land > 0)
                    {
                        f = cover[src] * keep / land; g = cover[src + 1] * keep / land;
                        r = cover[src + 2] * keep / land; d = cover[src + 3] * keep / land;
                    }
                    int rest = 255 - s - f - g - r - d - w;
                    if (rest > 0)
                    {
                        if (s >= 128) s += rest;
                        else if (g >= f && g >= r && g >= d && g >= w) g += rest;
                        else if (f >= r && f >= d && f >= w) f += rest;
                        else if (r >= d && r >= w) r += rest;
                        else if (d >= w) d += rest;
                        else w += rest;
                    }
                    t0[dst] = (byte)s;
                    t0[dst + 1] = (byte)f;
                    t0[dst + 2] = (byte)g;
                    t0[dst + 3] = (byte)r;
                    t1[dst] = (byte)d;
                    t1[dst + 1] = (byte)w;
                }
            }
            return new SplatTexels { Resolution = n, Textures = new[] { t0, t1 } };
        }

        /// <summary>
        /// Shade per texel (0–<see cref="ForestShade"/>): the forest-floor weight averaged over
        /// <see cref="ShadeRadiusMetres"/> (separable box), eased so only real stands get it.
        /// </summary>
        static byte[] StandShade(byte[] cover, int n, bool snow)
        {
            var shade = new byte[n * n];
            if (!snow) return shade;
            const int bands = TerrainCache.CoverBands;
            float texel = 1024f / (n - 1);
            int r = System.Math.Max(1, (int)System.Math.Round(ShadeRadiusMetres / texel));
            var rows = new int[n * n];
            for (int j = 0; j < n; j++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) if (i >= 0 && i < n) sum += cover[(j * n + i) * bands];
                for (int i = 0; i < n; i++)
                {
                    rows[j * n + i] = sum;
                    int add = i + r + 1, drop = i - r;
                    if (add < n) sum += cover[(j * n + add) * bands];
                    if (drop >= 0) sum -= cover[(j * n + drop) * bands];
                }
            }
            float count = (2 * r + 1) * (2 * r + 1) * 255f;
            for (int i = 0; i < n; i++)
            {
                int sum = 0;
                for (int j = -r; j <= r; j++) if (j >= 0 && j < n) sum += rows[j * n + i];
                for (int j = 0; j < n; j++)
                {
                    float density = sum / count;
                    float t = System.Math.Min(1f, System.Math.Max(0f, (density - 0.35f) / 0.45f));
                    shade[j * n + i] = (byte)(ForestShade * t * t * (3 - 2 * t));
                    int add = j + r + 1, drop = j - r;
                    if (add < n) sum += rows[add * n + i];
                    if (drop >= 0) sum -= rows[drop * n + i];
                }
            }
            return shade;
        }

        public static SplatTexels Load(string packageFolder, CacheTile tile, bool snow) =>
            Compose(TerrainCache.ReadCover(packageFolder, tile), tile.CoverResolution, snow);
    }
}
