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
            var t0 = new byte[n * n * 4];
            var t1 = new byte[n * n * 4];
            for (int j = 0; j < n; j++)
            {
                int src = j * n * bands, dst = (n - 1 - j) * n * 4;
                for (int i = 0; i < n; i++, src += bands, dst += 4)
                {
                    int s = snow ? cover[src + GroundCover.Layers] : 0;
                    int keep = 255 - s;
                    // Scale the ground weights by what the snow leaves; rounding leftovers go to the largest.
                    int f = cover[src] * keep / 255, g = cover[src + 1] * keep / 255, r = cover[src + 2] * keep / 255;
                    int d = cover[src + 3] * keep / 255, w = cover[src + 4] * keep / 255;
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

        public static SplatTexels Load(string packageFolder, CacheTile tile, bool snow) =>
            Compose(TerrainCache.ReadCover(packageFolder, tile), tile.CoverResolution, snow);
    }
}
