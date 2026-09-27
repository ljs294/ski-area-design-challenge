using System;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests).
    public sealed class ViewTimeTests
    {
        [Test]
        public void LeapYearsFollowTheGregorianRule()
        {
            Assert.That(ViewTime.DaysInYear(2024), Is.EqualTo(366));
            Assert.That(ViewTime.DaysInYear(2026), Is.EqualTo(365));
            Assert.That(ViewTime.DaysInYear(1900), Is.EqualTo(365));
            Assert.That(ViewTime.DaysInYear(2000), Is.EqualTo(366));
        }

        [TestCase(2026, 0, 0)]
        [TestCase(2026, 366, 0)]
        [TestCase(2026, 1, -1)]
        [TestCase(2026, 1, 86_400)]
        [TestCase(0, 1, 0)]
        public void OutOfRangeValuesAreRejected(int year, int day, int second)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ViewTime(year, day, second));
        }

        [Test]
        public void LastSecondOfALeapYearIsValid()
        {
            var t = new ViewTime(2024, 366, 86_399);
            Assert.That(t.IsValid, Is.True);
            Assert.That(t.ToString(), Is.EqualTo("2024-366 23:59:59"));
        }

        [Test]
        public void DefaultIsInvalidAndEqualityIsByValue()
        {
            Assert.That(default(ViewTime).IsValid, Is.False);
            Assert.That(new ViewTime(2026, 32, 3600), Is.EqualTo(new ViewTime(2026, 32, 3600)));
            Assert.That(new ViewTime(2026, 32, 3600) != new ViewTime(2026, 32, 3601), Is.True);
        }
    }

    public sealed class ManualViewClockTests
    {
        static readonly ViewTime Noon = new ViewTime(2026, 32, 12 * 3600);

        [Test]
        public void SetRaisesChangedWithTheNewTime()
        {
            var clock = new ManualViewClock(Noon);
            ViewTime? seen = null;
            clock.Changed += t => seen = t;

            var evening = Noon.WithSecondOfDay(18 * 3600);
            clock.Set(evening);

            Assert.That(clock.Now, Is.EqualTo(evening));
            Assert.That(seen, Is.EqualTo(evening));
        }

        [Test]
        public void SettingTheSameTimeRaisesNothing()
        {
            var clock = new ManualViewClock(Noon);
            var raised = 0;
            clock.Changed += _ => raised++;

            clock.Set(Noon);

            Assert.That(raised, Is.Zero);
        }

        [Test]
        public void TheClockNeverAdvancesOnItsOwn()
        {
            IGameClock clock = new ManualViewClock(Noon);
            Assert.That(clock.Now, Is.EqualTo(Noon));
            Assert.That(clock.Now, Is.EqualTo(Noon));
        }

        [Test]
        public void DefaultTimesAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new ManualViewClock(default));
            Assert.Throws<ArgumentException>(() => new ManualViewClock(Noon).Set(default));
        }
    }
}
