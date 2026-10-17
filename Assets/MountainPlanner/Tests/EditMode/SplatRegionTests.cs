using MountainPlanner.Persistence;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // A terrain edit repaints only the splat it changed (task P3-05, GR-09): composing a window of cover texels gives
    // exactly the bytes the whole tile's compose gives there, stand shade included, at the tile's edges too.
    public sealed class SplatRegionTests
    {
        const int Bands = TerrainCache.CoverBands;

        /// <summary>A tile of cover with forest stands, grass, rock, water and snow in patterns (no randomness).</summary>
        static byte[] Cover(int n)
        {
            var cover = new byte[n * n * Bands];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int o = (j * n + i) * Bands;
                    int f = ((i / 9 + j / 7) % 3 == 0) ? 255 : ((i * 13 + j * 7) % 11) * 20;
                    if (f > 255) f = 255;
                    int water = (i > n - 20 && j < 15) ? 255 - f : 0;
                    if (water < 0) water = 0;
                    int rock = (i + j) % 17 == 0 ? (255 - f - water) / 2 : 0;
                    cover[o] = (byte)f;
                    cover[o + 4] = (byte)water;
                    cover[o + 2] = (byte)rock;
                    cover[o + 1] = (byte)(255 - f - water - rock);
                    cover[o + MountainPlanner.Domain.Cover.GroundCover.Layers] = (byte)(200 + (i * j) % 56);
                }
            return cover;
        }

        static void AssertRegion(int n, RectInt inner)
        {
            var cover = Cover(n);
            var full = SplatTexels.Compose(cover, n);
            int reach = SplatTexels.ShadeReach(n);
            int wx = Mathf.Max(0, inner.xMin - reach), wy = Mathf.Max(0, inner.yMin - reach);
            int wx1 = Mathf.Min(n, inner.xMax + reach), wy1 = Mathf.Min(n, inner.yMax + reach);
            int ww = wx1 - wx, wh = wy1 - wy;
            var window = new byte[ww * wh * Bands];
            for (int j = 0; j < wh; j++)
                System.Array.Copy(cover, ((wy + j) * n + wx) * Bands, window, j * ww * Bands, ww * Bands);
            var part = SplatTexels.ComposeWindow(window, n, wx, wy, ww, wh, ww, inner);
            Assert.That(part.Width, Is.EqualTo(inner.width));
            Assert.That(part.Height, Is.EqualTo(inner.height));
            for (int t = 0; t < 2; t++)
                for (int j = inner.yMin; j < inner.yMax; j++)
                    for (int i = inner.xMin; i < inner.xMax; i++)
                    {
                        int fullRow = n - 1 - j, partRow = inner.yMax - 1 - j;   // both south row first
                        for (int c = 0; c < 4; c++)
                            Assert.That(part.Textures[t][(partRow * inner.width + (i - inner.xMin)) * 4 + c],
                                        Is.EqualTo(full.Textures[t][(fullRow * n + i) * 4 + c]), $"texture {t} texel ({i}, {j}) channel {c}");
                    }
        }

        [Test]
        public void GR09_AWindowInsideTheTileGivesTheWholeTilesBytes() => AssertRegion(257, new RectInt(60, 90, 70, 40));

        [Test]
        public void GR09_AWindowAtTheTilesCornersGivesTheWholeTilesBytes()
        {
            AssertRegion(257, new RectInt(0, 0, 30, 25));
            AssertRegion(257, new RectInt(230, 240, 27, 17));
        }

        [Test]
        public void GR09_TheWholeTileAsAWindowIsTheCompose() => AssertRegion(129, new RectInt(0, 0, 129, 129));
    }
}
