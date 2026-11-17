using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.07 PR A2, SN-01 v2: the one 8 m sampler and the snow's static inputs.</summary>
    public sealed class SnowTerrainTests
    {
        static readonly Pacing P = Pacing.Default;

        /// <summary>A tile of posts from a height function of (east, north) in metres from the ring's corner.</summary>
        readonly struct Posts : ITileSamples
        {
            readonly Func<double, double, double> _f;
            readonly double _west, _south, _step;
            public int Resolution { get; }
            public Posts(int res, double west, double south, double tile, Func<double, double, double> f)
            { Resolution = res; _west = west; _south = south; _step = tile / (res - 1); _f = f; }
            public int Value(int c, int r) => (int)Math.Round(_f(_west + c * _step, _south + r * _step) * 1000);
        }

        static double Plane(double x, double y) => 2000 + 0.20 * x - 0.05 * y;

        static int[] Sample(int width, int depth, int res, double tile, double offset, Func<double, double, double> f)
        {
            var s = new SurfaceSampler(width, depth);
            int tiles = (int)Math.Ceiling((width * 8 - offset) / tile) + 1;
            for (int tj = 0; tj < tiles; tj++)
                for (int ti = 0; ti < tiles; ti++)
                {
                    double west = offset + ti * tile, south = offset + tj * tile;
                    s.AddPosts(new Posts(res, west, south, tile, f), (long)Math.Round(west * 1000), (long)Math.Round(south * 1000), (long)(tile * 1000),
                               ti == tiles - 1, tj == tiles - 1, 0, 0, width, depth);
                }
            var mm = new int[width * depth];
            s.Heights(mm);
            return mm;
        }

        [TestCase(129, 128.0, 0.0, TestName = "SN01_ACellIsTheMeanOfItsPostsAtItsCentre(1 m posts)")]
        [TestCase(65, 128.0, 0.0, TestName = "SN01_ACellIsTheMeanOfItsPostsAtItsCentre(2 m posts)")]
        [TestCase(129, 128.0, -3.5, TestName = "SN01_ACellIsTheMeanOfItsPostsAtItsCentre(tiles off the cells)")]
        public void SN01_ACellIsTheMeanOfItsPostsAtItsCentre(int res, double tile, double offset)
        {
            // A plane's mean over a cell is the plane at its centre, if every post is counted once and edges split fairly,
            // across tile seams too.
            const int w = 40, d = 40;
            var mm = Sample(w, d, res, tile, offset, Plane);
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                    Assert.That(mm[j * w + i], Is.EqualTo((int)Math.Round(Plane(i * 8 + 4, j * 8 + 4) * 1000)).Within(1), $"cell {i}, {j}");
        }

        static WeatherTerrain Weather(int w, int d, int[] mm)
        {
            var feet = new int[mm.Length];
            for (int k = 0; k < mm.Length; k++) feet[k] = mm[k] == SurfaceSampler.NoData ? int.MinValue : (int)Math.Round(mm[k] / 304.8);
            return WeatherTerrain.Build(w, d, feet, 0, 0, (0, 0, w, d), d / 2 * w + w / 2, SimWeather.TerrainTuning(P));
        }

        static int[] Factors() => (int[])P.WindExposure.Clone();

        static SnowTerrain Terrain(int w, int d, Func<double, double, double> f, out int[] mm, byte canopy = 0)
        {
            mm = new int[w * d];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++) mm[j * w + i] = (int)Math.Round(f(i * 8 + 4, j * 8 + 4) * 1000);
            var c = new byte[w * d];
            for (int k = 0; k < c.Length; k++) c[k] = canopy;
            return SnowTerrain.Build(w, d, mm, c, Weather(w, d, mm), Factors());
        }

        [Test]
        public void SN01_ASouthFacingSlopesNormalPointsSouth()
        {
            // 20 % grade falling to the south: the normal leans south by atan(0.2) ≈ 11.3°.
            var t = Terrain(32, 32, (x, y) => 3000 + 0.2 * y, out _);
            int c = 16 * 32 + 16;
            double nx = SnowTerrain.Micro(t.NormalX[c]) / 1e6, ny = SnowTerrain.Micro(t.NormalY[c]) / 1e6, nz = t.NormalZMicro(c) / 1e6;
            TestContext.Progress.WriteLine($"normal ({nx:F3}, {ny:F3}, {nz:F3})");
            Assert.That(nx, Is.EqualTo(0).Within(0.01));
            Assert.That(ny, Is.EqualTo(-0.196).Within(0.01));
            Assert.That(nz, Is.EqualTo(0.981).Within(0.01));
            Assert.That(nx * nx + ny * ny + nz * nz, Is.EqualTo(1).Within(0.02), "a unit vector");
            Assert.That(t.Rise[c], Is.EqualTo(0), "a plane is no ridge");
        }

        [Test]
        public void SN01_ARidgeRisesAndAHollowSinks()
        {
            // An east–west ridge with a hollow either side: the crest stands above its 7 × 7 mean, the hollow below.
            double Ridge(double x, double y) => 3000 + 20 * Math.Exp(-Math.Pow((y - 256) / 20, 2)) - 10 * Math.Exp(-Math.Pow((y - 200) / 15, 2));
            var t = Terrain(64, 64, Ridge, out _);
            int crest = 32 * 64 + 32, hollow = 25 * 64 + 32;
            TestContext.Progress.WriteLine($"crest rise {t.Rise[crest] / 100.0} m, hollow {t.Rise[hollow] / 100.0} m");
            Assert.That(t.Rise[crest], Is.GreaterThan(300));
            Assert.That(t.Rise[hollow], Is.LessThan(-100));
        }

        [Test]
        public void WX59_AValleyFloorSeesItsWallsSkyline()
        {
            // A cell on a flat floor with a 30° wall rising 400 m to its east: the skyline east is about 30° (sin 0.5) once the
            // wall's top is in reach; west it's open; the open sky is less than whole.
            double Valley(double x, double y) => 2000 + Math.Max(0, x - 1200) * Math.Tan(Math.PI / 6) - Math.Max(0, x - 1200 - 693) * Math.Tan(Math.PI / 6);
            var t = Terrain(320, 64, Valley, out _);
            int a = 400 / 8 / SnowTerrain.LatticeCells, b = 32 / SnowTerrain.LatticeCells;   // 400 m east: the wall starts 800 m away
            int l = b * t.LatticeWidth + a;
            double east = t.Horizon[l * SnowTerrain.Directions + 4] / 255.0, west = t.Horizon[l * SnowTerrain.Directions + 12] / 255.0;
            double open = t.OpenSky[l] * 4 / 1000.0;
            TestContext.Progress.WriteLine($"skyline east sin {east:F3} (wall top {Math.Sin(Math.Atan(400.0 / 1493)):F3}), west {west:F3}, open sky {open:F3}");
            Assert.That(east, Is.EqualTo(Math.Sin(Math.Atan(400.0 / 1493))).Within(0.02), "the wall's top, 1,493 m off and 400 m up");
            Assert.That(west, Is.EqualTo(0));
            Assert.That(open, Is.LessThan(1).And.GreaterThan(0.9));
        }

        [Test]
        public void SN01_AnEditsPatchIsTheSameAsBuildingAgain()
        {
            // Dig a pit: the patch over the changed rectangle, applied to the worker's copy, gives the shapes and canopy a fresh
            // build of the edited ground gives (the bins and skyline stay as downloaded).
            const int w = 96, d = 96;
            var t = Terrain(w, d, (x, y) => 2500 + 0.3 * x + 10 * Math.Sin(y / 40), out var mm, canopy: 40);
            var worker = t.Clone();
            var canopy = new byte[w * d];
            for (int k = 0; k < canopy.Length; k++) canopy[k] = 40;
            for (int j = 40; j < 50; j++)
                for (int i = 30; i < 44; i++) { mm[j * w + i] -= 6000; canopy[j * w + i] = 0; }
            var patch = t.Patch(mm, canopy, 30, 40, 44, 50);
            worker.Apply(patch);
            var fresh = SnowTerrain.Build(w, d, mm, canopy, Weather(w, d, mm), Factors(), horizon: false);
            Assert.That(worker.NormalX, Is.EqualTo(fresh.NormalX));
            Assert.That(worker.NormalY, Is.EqualTo(fresh.NormalY));
            Assert.That(worker.Rise, Is.EqualTo(fresh.Rise));
            Assert.That(worker.Canopy, Is.EqualTo(fresh.Canopy));
            Assert.That(worker.Horizon, Is.EqualTo(t.Horizon), "the skyline is the downloaded ground's");
            Assert.That(worker.Hash, Is.Not.EqualTo(t.Hash));
        }
    }
}
