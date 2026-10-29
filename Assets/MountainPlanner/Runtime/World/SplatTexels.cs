using System.Buffers;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Persistence;
using UnityEngine;

namespace MountainPlanner.World
{
    /// <summary>
    /// One tile's splat maps as raw RGBA32 bytes, ready for <see cref="TerrainTiles.ApplySplat"/>. Built
    /// on worker threads from the cached cover texels (task 07).
    /// Channel order follows the terrain layers: texture 0 = snow, forest floor, grass, rock;
    /// texture 1 = developed, water, paved road, unpaved road (task 12d). Snow is layer 0 so an unpainted tile
    /// already reads as snow.
    ///
    /// The snow and the ground under it are both kept (map layers, task 12): channel 0 is the snow's weight,
    /// and the ground and water channels are the bare ground cover, summing to 255 on their own. The terrain
    /// shader lays the snow over the land share (MountainTerrain.shader), or leaves it off with the Snow layer
    /// off, so switching the snow needs no new splat.
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

        /// <summary>The tile's splat resolution; <see cref="Width"/> × <see cref="Height"/> are this part's texels (the whole tile unless composed by region).</summary>
        public int Resolution;
        public int Width, Height;
        public byte[][] Textures;

        /// <summary>
        /// Composes cover texels (north row first, eight bytes each) into splat textures (south row first,
        /// as Unity's alphamaps are): the snow's weight in channel 0 (snow lies on the land by its cover, thinned
        /// under forest stands), and the ground cover unscaled in the other seven (the "under the snow" view, T17).
        /// </summary>
        public static SplatTexels Compose(byte[] cover, int resolution) =>
            ComposeWindow(cover, resolution, 0, 0, resolution, resolution, resolution, new RectInt(0, 0, resolution, resolution));

        /// <summary>
        /// How far (texels) a texel's stand shade reaches: a region's cover window needs this margin round it, clipped at
        /// the tile's edge, for <see cref="ComposeWindow"/> to give the full tile's bytes.
        /// </summary>
        public static int ShadeReach(int resolution) => System.Math.Max(1, (int)System.Math.Round(ShadeRadiusMetres / (1024f / (resolution - 1))));

        /// <summary>
        /// Composes part of a tile (task P3-05: the texels a terrain edit changed). <paramref name="cover"/> is a window
        /// of the tile's cover texels, <paramref name="windowWidth"/> × <paramref name="windowHeight"/> from texel
        /// (<paramref name="windowX"/>, <paramref name="windowY"/>) of a <paramref name="resolution"/>² tile (north row
        /// first); <paramref name="inner"/> (cover coordinates, inside the window by <see cref="ShadeReach"/> or at the
        /// tile's edge) is composed. The result is the inner rectangle's splat, south row first, equal byte for byte to
        /// the same texels of <see cref="Compose"/> over the whole tile.
        /// </summary>
        public static SplatTexels ComposeWindow(byte[] cover, int resolution, int windowX, int windowY, int windowWidth, int windowHeight, int stride,
                                                RectInt inner)
        {
            const int bands = TerrainCache.CoverBands;
            // The shade and its row sums are scratch for this call only: rented, every entry written, and handed back (a
            // brush's catch-ups compose a few windows a second).
            var shade = ArrayPool<byte>.Shared.Rent(windowWidth * windowHeight);
            var rows = ArrayPool<int>.Shared.Rent(windowWidth * windowHeight);
            StandShade(cover, windowWidth, windowHeight, stride, resolution, shade, rows);
            int w = inner.width, h = inner.height;
            var t0 = new byte[w * h * 4];
            var t1 = new byte[w * h * 4];
            for (int j = inner.yMin; j < inner.yMax; j++)
            {
                int wj = j - windowY;
                int src = (wj * stride + (inner.xMin - windowX)) * bands, dst = (inner.yMax - 1 - j) * w * 4;
                for (int i = inner.xMin; i < inner.xMax; i++, src += bands, dst += 4)
                {
                    // Snow lies on the land; water keeps its weight under snow, so the terrain shader knows
                    // where the frozen lakes are and draws snow on ice there (MountainTerrain.shader).
                    int w4 = cover[src + 4], land = 255 - w4;
                    int s = cover[src + GroundCover.Layers] * (255 - shade[wj * windowWidth + (i - windowX)]) / 255 * land / 255;
                    int f = cover[src], g = cover[src + 1], r = cover[src + 2], d = cover[src + 3];
                    int paved = cover[src + 5], unpaved = cover[src + 6];
                    // Rounding leftovers go to the largest ground layer.
                    int rest = 255 - f - g - r - d - w4 - paved - unpaved;
                    if (rest > 0)
                    {
                        if (g >= f && g >= r && g >= d && g >= w4) g += rest;
                        else if (f >= r && f >= d && f >= w4) f += rest;
                        else if (r >= d && r >= w4) r += rest;
                        else if (d >= w4) d += rest;
                        else w4 += rest;
                    }
                    t0[dst] = (byte)s;
                    t0[dst + 1] = (byte)f;
                    t0[dst + 2] = (byte)g;
                    t0[dst + 3] = (byte)r;
                    t1[dst] = (byte)d;
                    t1[dst + 1] = (byte)w4;
                    t1[dst + 2] = (byte)paved;
                    t1[dst + 3] = (byte)unpaved;
                }
            }
            ArrayPool<byte>.Shared.Return(shade);
            ArrayPool<int>.Shared.Return(rows);
            return new SplatTexels { Resolution = resolution, Width = w, Height = h, Textures = new[] { t0, t1 } };
        }

        /// <summary>
        /// Shade per texel (0–<see cref="ForestShade"/>): the forest-floor weight averaged over
        /// <see cref="ShadeRadiusMetres"/> (separable box, clipped at the window), eased so only real stands get it.
        /// </summary>
        static void StandShade(byte[] cover, int ww, int wh, int stride, int resolution, byte[] shade, int[] rows)
        {
            const int bands = TerrainCache.CoverBands;
            int r = ShadeReach(resolution);
            for (int j = 0; j < wh; j++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) if (i >= 0 && i < ww) sum += cover[(j * stride + i) * bands];
                for (int i = 0; i < ww; i++)
                {
                    rows[j * ww + i] = sum;
                    int add = i + r + 1, drop = i - r;
                    if (add < ww) sum += cover[(j * stride + add) * bands];
                    if (drop >= 0) sum -= cover[(j * stride + drop) * bands];
                }
            }
            float count = (2 * r + 1) * (2 * r + 1) * 255f;
            for (int i = 0; i < ww; i++)
            {
                int sum = 0;
                for (int j = -r; j <= r; j++) if (j >= 0 && j < wh) sum += rows[j * ww + i];
                for (int j = 0; j < wh; j++)
                {
                    float density = sum / count;
                    float t = System.Math.Min(1f, System.Math.Max(0f, (density - 0.35f) / 0.45f));
                    shade[j * ww + i] = (byte)(ForestShade * t * t * (3 - 2 * t));
                    int add = j + r + 1, drop = j - r;
                    if (add < wh) sum += rows[add * ww + i];
                    if (drop >= 0) sum -= rows[drop * ww + i];
                }
            }
        }

        public static SplatTexels Load(string packageFolder, CacheTile tile) =>
            Compose(TerrainCache.ReadCover(packageFolder, tile), tile.CoverResolution);
    }
}
