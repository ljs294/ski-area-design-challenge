using MountainPlanner.Domain.Cover;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.28: the grass's colour through the year (<see cref="GrassSeason"/>). Engine-free.</summary>
    public sealed class GrassSeasonTests
    {
        [Test]
        public void SummerLeavesTheGrassAsItIs()
        {
            Assert.AreEqual((1f, 1f, 1f), GrassSeason.Tint(196, 44), "mid-July");
            Assert.AreEqual((1f, 1f, 1f), GrassSeason.Tint(210, 39), "late July in Colorado");
        }

        [Test]
        public void WinterIsStrawAndAutumnIsGold()
        {
            var winter = GrassSeason.Tint(20, 44);
            Assert.Greater(winter.R, winter.G); Assert.Greater(winter.G, winter.B);
            Assert.Less(winter.B, 0.75f, "dormant grass has lost its green");
            var gold = GrassSeason.Tint(288, 44);
            Assert.Greater(gold.R, 1.1f); Assert.Less(gold.B, 0.75f);
            Assert.AreEqual(GrassSeason.Tint(0, 44), GrassSeason.Tint(365, 44), "the year closes on itself");
        }

        [Test]
        public void SpringGreensFromBrownAndComesLaterTowardThePole()
        {
            var early = GrassSeason.Tint(125, 44);   // early May in New England: still mostly last year's straw
            var late = GrassSeason.Tint(152, 44);    // the start of June: fresh green
            Assert.Greater(early.R, early.G);
            Assert.Greater(late.G, late.R);
            // The same day is further along in the south than in the north.
            Assert.Greater(GrassSeason.Tint(140, 36).G, GrassSeason.Tint(140, 52).G);
        }

        [Test]
        public void TheColourNeverJumps()
        {
            var before = GrassSeason.Tint(0, 47);
            for (int d = 1; d <= 365; d++)
            {
                var now = GrassSeason.Tint(d, 47);
                Assert.Less(System.Math.Abs(now.R - before.R) + System.Math.Abs(now.G - before.G) + System.Math.Abs(now.B - before.B), 0.04f, $"day {d}");
                before = now;
            }
        }

        [Test]
        public void SouthOfTheEquatorTheSeasonsSwap()
        {
            Assert.AreEqual(196, GrassSeason.NorthernDay(196, 44));
            Assert.AreEqual(13, GrassSeason.NorthernDay(196, -44), "mid-July in New Zealand is midwinter");
            Assert.AreEqual(197, GrassSeason.NorthernDay(15, -44), "and mid-January is midsummer");
        }
    }
}
