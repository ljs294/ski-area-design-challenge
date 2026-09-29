using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // The forest's wind (TR4, ForestWind): levels cycle, strength eases instead of snapping, and the
    // wind clock the tree shader reads never jumps except at its wrap, which the shader's frequencies hide.
    public sealed class ForestWindTests
    {
        [Test]
        public void OpensInABreezeAndCyclesThroughTheLevels()
        {
            var wind = new ForestWind();
            Assert.That(wind.Target, Is.EqualTo(ForestWind.Level.Breeze));
            Assert.That(wind.Strength, Is.EqualTo(ForestWind.StrengthOf(ForestWind.Level.Breeze)));
            Assert.That(wind.Cycle(), Is.EqualTo(ForestWind.Level.Strong));
            Assert.That(wind.Cycle(), Is.EqualTo(ForestWind.Level.Calm));
            Assert.That(wind.Cycle(), Is.EqualTo(ForestWind.Level.Breeze));
            Assert.That(ForestWind.StrengthOf(ForestWind.Level.Calm), Is.EqualTo(0f));
        }

        [Test]
        public void StrengthEasesToTheNewLevelInAboutTwoSeconds()
        {
            var wind = new ForestWind(ForestWind.Level.Calm);
            wind.Cycle();   // breeze
            float previous = wind.Strength, goal = ForestWind.StrengthOf(ForestWind.Level.Breeze);
            for (int frame = 1; frame <= 120; frame++)   // 2 s at 60 fps
            {
                wind.Advance(1f / 60);
                Assert.That(wind.Strength, Is.GreaterThanOrEqualTo(previous), "rises steadily");
                Assert.That(wind.Strength - previous, Is.LessThan(0.05f * goal), "never snaps");
                previous = wind.Strength;
            }
            Assert.That(wind.Strength, Is.EqualTo(goal).Within(0.05f * goal));
            for (int frame = 0; frame < 120; frame++) wind.Advance(1f / 60);
            Assert.That(wind.Strength, Is.EqualTo(goal), "settles exactly");
        }

        [Test]
        public void SetJumpsStraightToALevel()
        {
            var wind = new ForestWind();
            wind.Set(ForestWind.Level.Strong);
            Assert.That(wind.Strength, Is.EqualTo(1f));
            Assert.That(wind.ShaderValue.z, Is.EqualTo(1f));
        }

        [Test]
        public void ClockRunsFasterInStrongerWindAndWraps()
        {
            var calm = new ForestWind(ForestWind.Level.Calm);
            var strong = new ForestWind(ForestWind.Level.Strong);
            calm.Advance(1);
            strong.Advance(1);
            Assert.That(strong.Clock, Is.GreaterThan(calm.Clock));
            for (int i = 0; i < 1000; i++) strong.Advance(1);
            Assert.That(strong.Clock, Is.InRange(0f, ForestWind.ClockPeriod));
        }

        [Test]
        public void DirectionIsAUnitVectorNearTheBaseDirection()
        {
            var wind = new ForestWind();
            for (int i = 0; i < 400; i++)
            {
                wind.Advance(0.5f);
                Vector2 d = wind.Direction;
                Assert.That(d.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(Vector2.Angle(d, ForestWind.BaseDirection), Is.LessThanOrEqualTo(ForestWind.VeerDegrees + 0.01f));
            }
        }
    }
}
