using MountainPlanner.Persistence;
using MountainPlanner.World;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Splat composition (task 07, frozen lakes in the style tile): snow lies on the land, and water keeps
    // its weight under snow so the terrain shader can draw frozen lakes.
    public sealed class SplatTexelsTests
    {
        const int Bands = TerrainCache.CoverBands;

        // One row of cover texels: forest floor, grass, rock, developed, water, snow cover.
        static byte[] Row(params (byte F, byte G, byte R, byte D, byte W, byte Snow)[] texels)
        {
            var cover = new byte[texels.Length * texels.Length * Bands];
            for (int j = 0; j < texels.Length; j++)
                for (int i = 0; i < texels.Length; i++)
                {
                    var t = texels[i];
                    int o = (j * texels.Length + i) * Bands;
                    cover[o] = t.F; cover[o + 1] = t.G; cover[o + 2] = t.R; cover[o + 3] = t.D; cover[o + 4] = t.W; cover[o + 5] = t.Snow;
                }
            return cover;
        }

        static (int Snow, int Grass, int Water, int Sum) Texel(SplatTexels splat, int i)
        {
            byte[] t0 = splat.Textures[0], t1 = splat.Textures[1];
            int o = i * 4;
            return (t0[o], t0[o + 2], t1[o + 1], t0[o] + t0[o + 1] + t0[o + 2] + t0[o + 3] + t1[o] + t1[o + 1]);
        }

        [Test]
        public void WaterKeepsItsWeightUnderSnow()
        {
            var cover = Row((0, 0, 0, 0, 255, 255), (0, 255, 0, 0, 0, 255), (0, 127, 0, 0, 128, 255));
            var splat = SplatTexels.Compose(cover, 3, snow: true);

            var lake = Texel(splat, 0);
            Assert.That(lake.Water, Is.EqualTo(255), "a lake stays water; the shader draws snow on the ice");
            Assert.That(lake.Snow, Is.EqualTo(0));

            var meadow = Texel(splat, 1);
            Assert.That(meadow.Snow, Is.EqualTo(255), "land is under snow as before");
            Assert.That(meadow.Water, Is.EqualTo(0));

            var shore = Texel(splat, 2);
            Assert.That(shore.Water, Is.EqualTo(128));
            Assert.That(shore.Snow, Is.EqualTo(127), "snow covers the land share of a shore texel");
            foreach (var t in new[] { lake, meadow, shore }) Assert.That(t.Sum, Is.EqualTo(255));
        }

        [Test]
        public void WithoutSnowTheGroundShowsAlone()
        {
            var cover = Row((0, 0, 0, 0, 255, 255), (0, 255, 0, 0, 0, 255), (0, 127, 0, 0, 128, 255));
            var splat = SplatTexels.Compose(cover, 3, snow: false);
            Assert.That(Texel(splat, 0).Water, Is.EqualTo(255));
            Assert.That(Texel(splat, 1).Grass, Is.EqualTo(255));
            var shore = Texel(splat, 2);
            Assert.That((shore.Grass, shore.Water, shore.Snow), Is.EqualTo((127, 128, 0)));
        }
    }
}
