using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.06: the sun's clock moves smoothly between the two newest snapshots and never runs ahead of the newest.</summary>
    public sealed class FaceClockTests
    {
        static readonly Pacing P = Pacing.Default;

        static SimSnapshot At(long seq, long tick, long faceMs) => new SimSnapshot(P) { Seq = seq, Tick = tick, FaceMs = faceMs, TicksPerRealTick = 1 };

        [Test]
        public void TheSunMovesBetweenSnapshotsAndNeverAheadOfTheNewest()
        {
            var clock = new FaceClock();
            clock.Advance(At(1, 100, 400_000), 0, P.TickMs);
            Assert.That(clock.SecondsPastSnapshot, Is.EqualTo(0).Within(1e-9), "the first snapshot is where it starts");
            clock.Advance(At(2, 101, 404_000), 0.05, P.TickMs);   // half a tick of real time at 1×
            Assert.That(clock.BehindMs, Is.EqualTo(2000).Within(1e-6));
            Assert.That(clock.SecondsPastSnapshot, Is.EqualTo(-2).Within(1e-9), "halfway from 400 s to 404 s");
            double last = clock.SecondsPastSnapshot;
            for (int frame = 0; frame < 10; frame++)
            {
                clock.Advance(null, 0.01, P.TickMs);
                Assert.That(clock.SecondsPastSnapshot, Is.GreaterThanOrEqualTo(last), "only forward");
                Assert.That(clock.SecondsPastSnapshot, Is.LessThanOrEqualTo(0), "never ahead of the newest");
                last = clock.SecondsPastSnapshot;
            }
            Assert.That(last, Is.EqualTo(0).Within(1e-9), "caught up");
        }

        [Test]
        public void ItCarriesTheSnapshotsOwnMilliseconds()
        {
            var clock = new FaceClock();
            clock.Advance(At(1, 10, 61_250), 0, P.TickMs);
            Assert.That(clock.SecondsPastSnapshot, Is.EqualTo(0.25).Within(1e-9));
        }

        [Test]
        public void AClockThatGoesBackStartsAgain()
        {
            var clock = new FaceClock();
            clock.Advance(At(1, 500, 2_000_000), 0, P.TickMs);
            clock.Advance(At(2, 501, 2_004_000), 0, P.TickMs);
            clock.Advance(At(3, 20, 80_000), 0, P.TickMs);   // a load
            Assert.That(clock.BehindMs, Is.EqualTo(0));
            Assert.That(clock.DrawTick, Is.EqualTo(20));
        }

        [Test]
        public void PausedItHoldsStill()
        {
            var clock = new FaceClock();
            clock.Advance(At(1, 100, 400_000), 0, P.TickMs);
            var paused = At(2, 101, 404_000);
            paused.Paused = true;
            clock.Advance(paused, 1.0, P.TickMs);
            Assert.That(clock.BehindMs, Is.EqualTo(4000).Within(1e-6));
        }
    }
}
