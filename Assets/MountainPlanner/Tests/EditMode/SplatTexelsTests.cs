using MountainPlanner.Persistence;
using MountainPlanner.World;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Splat composition (task 07, frozen lakes in the style tile; map layers, task 12): channel 0 holds the snow's
    // weight on the land, the other five the bare ground cover, so the terrain shader can show or hide the snow
    // without a new splat. Water keeps its weight under snow so the shader can draw frozen lakes.
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

        static (int Snow, int Grass, int Water, int Ground) Texel(SplatTexels splat, int i)
        {
            byte[] t0 = splat.Textures[0], t1 = splat.Textures[1];
            int o = i * 4;
            return (t0[o], t0[o + 2], t1[o + 1], t0[o + 1] + t0[o + 2] + t0[o + 3] + t1[o] + t1[o + 1]);
        }

        [Test]
        public void SnowLiesOnTheLandAndWaterKeepsItsWeight()
        {
            var cover = Row((0, 0, 0, 0, 255, 255), (0, 255, 0, 0, 0, 255), (0, 127, 0, 0, 128, 255));
            var splat = SplatTexels.Compose(cover, 3);

            var lake = Texel(splat, 0);
            Assert.That(lake.Water, Is.EqualTo(255), "a lake stays water; the shader draws snow on the ice");
            Assert.That(lake.Snow, Is.EqualTo(0));

            var meadow = Texel(splat, 1);
            Assert.That(meadow.Snow, Is.EqualTo(255), "land is under snow as before");
            Assert.That(meadow.Water, Is.EqualTo(0));

            var shore = Texel(splat, 2);
            Assert.That(shore.Water, Is.EqualTo(128));
            Assert.That(shore.Snow, Is.EqualTo(127), "snow covers the land share of a shore texel");
        }

        [Test]
        public void TheGroundUnderTheSnowIsKeptWhole()
        {
            var cover = Row((0, 0, 0, 0, 255, 255), (0, 255, 0, 0, 0, 255), (0, 127, 0, 0, 128, 255));
            var splat = SplatTexels.Compose(cover, 3);
            Assert.That(Texel(splat, 0).Water, Is.EqualTo(255));
            Assert.That(Texel(splat, 1).Grass, Is.EqualTo(255), "the meadow under full snow is still a meadow");
            var shore = Texel(splat, 2);
            Assert.That((shore.Grass, shore.Water), Is.EqualTo((127, 128)));
            for (int i = 0; i < 3; i++) Assert.That(Texel(splat, i).Ground, Is.EqualTo(255), "the bare ground cover sums to 255 on its own");
        }

        [Test]
        public void TheShaderBlendMatchesTheOldCpuComposition()
        {
            // Half-snowed land: 200 grass and 55 rock under a 128 snow cover. Before task 12 SplatTexels scaled the
            // land layers by (land - snow) / land on the CPU: snow 128, grass 100 (99 plus the rounding leftover),
            // rock 27. The shader now does the same scaling (MountainTerrain.shader), within a step of rounding.
            var cover = Row((0, 200, 55, 0, 0, 128), (0, 200, 55, 0, 0, 128), (0, 200, 55, 0, 0, 128));
            var splat = SplatTexels.Compose(cover, 3);
            byte[] t0 = splat.Textures[0], t1 = splat.Textures[1];
            float snow = t0[0] / 255f, land = 1 - t1[1] / 255f, keep = 1 - snow / land;
            Assert.That(t0[0], Is.EqualTo(128));
            Assert.That(t0[2] * keep, Is.EqualTo(100).Within(1), "grass");
            Assert.That(t0[3] * keep, Is.EqualTo(27).Within(1), "rock");
            Assert.That(((int)t0[2], (int)t0[3]), Is.EqualTo((200, 55)), "with the Snow layer off the ground shows as it is");
        }
    }
}
