using System;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Snow;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.28: the ground's moisture map (<see cref="GroundMoisture"/>), which the grass draws from. Engine-free: made-up
    /// land shows each signal on its own, and the same heights always give the same bytes.
    /// </summary>
    public sealed class GroundMoistureTests
    {
        const int W = 64, D = 64;

        /// <summary>The sun's side at 44° N on an unturned grid: grid south.</summary>
        static readonly (int X, int Y) South = GroundMoisture.SunSide(44, 0);

        static int[] Land(Func<int, int, int> mm)
        {
            var h = new int[W * D];
            for (int y = 0; y < D; y++)
                for (int x = 0; x < W; x++) h[y * W + x] = mm(x, y);
            return h;
        }

        /// <summary>0 at 16 and 48, 16 at 0, 32 and 64: two valleys with a ridge between.</summary>
        static int Fold(int v) => 16 - Math.Abs((v + 16) % 32 - 16);

        [Test]
        public void TheValleyFloorIsWetterThanTheRidge()
        {
            // Valleys run north–south at x = 16 and 48 (25 % sides), the ridge at x = 32; everything tilts gently south.
            int[] mm = Land((x, y) => 2000 * Fold(x) + 100 * y);
            byte[] m = GroundMoisture.Build(W, D, mm, null, South.X, South.Y);
            Assert.Greater(m[32 * W + 16], m[32 * W + 32] + 60, "valley floor against ridge top");
            Assert.Greater(m[32 * W + 16], GroundMoisture.Neutral, "the floor is wetter than ordinary ground");
            Assert.Less(m[32 * W + 32], GroundMoisture.Neutral, "the ridge is drier than ordinary ground");
        }

        [Test]
        public void TheSunnySideIsDrierThanTheShadedSide()
        {
            // A ridge running east–west at y = 32: its south face falls toward y = 0, its north face toward y = 64.
            int[] mm = Land((x, y) => 2000 * Fold(y) + 50 * x);
            byte[] m = GroundMoisture.Build(W, D, mm, null, South.X, South.Y);
            Assert.Greater(m[38 * W + 32], m[26 * W + 32] + 15, "north face against south face, the same way down each");
            // South of the equator the sun is in the north: the faces swap.
            var north = GroundMoisture.SunSide(-44, 0);
            byte[] s = GroundMoisture.Build(W, D, mm, null, north.X, north.Y);
            Assert.Greater(s[26 * W + 32], s[38 * W + 32] + 15);
        }

        [Test]
        public void TheGroundBesideAPondIsWet()
        {
            int[] mm = Land((x, y) => 300 * x + 300 * y);
            var water = new byte[W * D];
            for (int y = 30; y < 34; y++)
                for (int x = 30; x < 34; x++) water[y * W + x] = 2;
            byte[] dry = GroundMoisture.Build(W, D, mm, null, South.X, South.Y);
            byte[] wet = GroundMoisture.Build(W, D, mm, water, South.X, South.Y);
            Assert.Greater(wet[32 * W + 35], dry[32 * W + 35] + 40, "two cells from the shore");
            Assert.AreEqual(dry[32 * W + 50], wet[32 * W + 50], 6, "far from the pond nothing much changes");
        }

        [Test]
        public void AHollowDugIntoASlopeTurnsWet()
        {
            int[] before = Land((x, y) => 800 * x + 100 * y);
            int[] after = (int[])before.Clone();
            for (int y = 28; y < 37; y++)
                for (int x = 28; x < 37; x++) after[y * W + x] -= 4000 - 800 * Math.Max(Math.Abs(x - 32), Math.Abs(y - 32));
            byte[] a = GroundMoisture.Build(W, D, before, null, South.X, South.Y);
            byte[] b = GroundMoisture.Build(W, D, after, null, South.X, South.Y);
            Assert.Greater(b[32 * W + 32], a[32 * W + 32] + 15, "the bottom of the dug hollow");
        }

        [Test]
        public void TheSameLandGivesTheSameBytes()
        {
            int[] mm = Land((x, y) => 2000 * Fold(x) + 100 * y + (x * 7919 + y * 104729) % 300);
            // Holes in the data are outlets, never a crash.
            for (int y = 10; y < 14; y++)
                for (int x = 40; x < 44; x++) mm[y * W + x] = SurfaceSampler.NoData;
            byte[] a = GroundMoisture.Build(W, D, mm, null, South.X, South.Y);
            byte[] b = GroundMoisture.Build(W, D, (int[])mm.Clone(), null, South.X, South.Y);
            CollectionAssert.AreEqual(a, b);
            Assert.AreEqual(GroundMoisture.Neutral, a[12 * W + 42], "no data reads as ordinary ground");
        }

        [Test]
        public void SunSideAndLog()
        {
            Assert.AreEqual((0, -1024), GroundMoisture.SunSide(44, 0));
            Assert.AreEqual((0, 1024), GroundMoisture.SunSide(-44, 0));
            Assert.AreEqual(0, GroundMoisture.Log2Q8(1));
            Assert.AreEqual(256, GroundMoisture.Log2Q8(2));
            Assert.AreEqual(384, GroundMoisture.Log2Q8(3));
            Assert.AreEqual(2560, GroundMoisture.Log2Q8(1024));
        }
    }
}
