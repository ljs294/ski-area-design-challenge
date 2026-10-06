using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests).
    public sealed class ViewClockRunnerTests
    {
        static readonly ViewTime Morning = new ViewTime(2026, 15, 10 * 3600 + 30 * 60);

        [Test]
        public void StartsPausedAtSpeedOne()
        {
            var clock = new ViewClockRunner(Morning);
            Assert.That(clock.Paused, Is.True);
            Assert.That(clock.Speed, Is.EqualTo(1));
            Assert.That(clock.Advance(10), Is.False);
            Assert.That(clock.Now, Is.EqualTo(Morning));
        }

        [TestCase(1, 60)]
        [TestCase(2, 180)]
        [TestCase(3, 600)]
        [TestCase(4, 1800)]
        public void EachSpeedRunsAtTheMockupsRate(int speed, int gameSecondsPerSecond)
        {
            var clock = new ViewClockRunner(Morning);
            clock.SetSpeed(speed);
            Assert.That(clock.Paused, Is.False, "choosing a speed starts the clock");
            clock.Advance(2);
            Assert.That(clock.Now.SecondOfDay - Morning.SecondOfDay, Is.EqualTo(2 * gameSecondsPerSecond));
        }

        [Test]
        public void FractionsCarryOverFrames()
        {
            var clock = new ViewClockRunner(Morning);
            clock.SetSpeed(1);
            for (int i = 0; i < 60; i++) clock.Advance(1 / 60.0);   // a second at 60 fps
            Assert.That(clock.Now.SecondOfDay - Morning.SecondOfDay, Is.InRange(59, 60));
        }

        [Test]
        public void MidnightAndNewYearRollOver()
        {
            Assert.That(ViewClockRunner.Add(new ViewTime(2026, 15, 86_399), 2), Is.EqualTo(new ViewTime(2026, 16, 1)));
            Assert.That(ViewClockRunner.Add(new ViewTime(2026, 365, 86_000), 1000), Is.EqualTo(new ViewTime(2027, 1, 600)));
            Assert.That(ViewClockRunner.Add(new ViewTime(2024, 365, 0), 86_400), Is.EqualTo(new ViewTime(2024, 366, 0)), "leap year");
            Assert.That(ViewClockRunner.Add(new ViewTime(2026, 1, 10), -20), Is.EqualTo(new ViewTime(2025, 365, 86_390)));
        }

        [Test]
        public void TheSunCatchesUpEveryThirtyGameSeconds()
        {
            var clock = new ViewClockRunner(Morning);
            clock.SetSpeed(1);
            clock.Advance(0.25);   // 15 game seconds
            Assert.That(clock.SunBehind, Is.False);
            clock.Advance(0.25);   // 30
            Assert.That(clock.SunBehind, Is.True);
            clock.Pushed();
            Assert.That(clock.SunBehind, Is.False);
            clock.Advance(0.1);
            clock.SetPaused(true);
            Assert.That(clock.SunBehind, Is.True, "pausing shows the exact time");
        }

        [Test]
        public void ATimeSetElsewhereWins()
        {
            var clock = new ViewClockRunner(Morning);
            var dusk = new ViewTime(2026, 15, 17 * 3600);
            clock.Adopt(Morning);
            Assert.That(clock.Now, Is.EqualTo(Morning));
            clock.Adopt(dusk);
            Assert.That(clock.Now, Is.EqualTo(dusk));
            Assert.That(clock.SunBehind, Is.False);
        }

        [Test]
        public void ChangesAreAnnounced()
        {
            var clock = new ViewClockRunner(Morning);
            int changes = 0;
            clock.Changed += () => changes++;
            clock.TogglePause();
            clock.SetSpeed(3);
            clock.SetSpeed(3);
            clock.SetSpeed(9);
            Assert.That(changes, Is.EqualTo(3));
            Assert.That(clock.Speed, Is.EqualTo(ViewClockRunner.MaxSpeed));
        }
    }
}
