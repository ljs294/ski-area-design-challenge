using System;
using MountainPlanner.App.Flow;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-03: the title's postcard drift. It passes through each chosen view, loops without a jump, keeps moving
    /// (slowly near the views, never still), never jerks between frames, and allocates nothing per frame.
    /// </summary>
    public sealed class TitleDriftTests
    {
        const double DemoLat = 43.593002, DemoLon = -110.848007;

        static TitleDrift Demo() => TitleDrift.For(DemoLat, DemoLon, 5000, 330, (x, z) => 2000 + 0.1f * x);

        static float Gap(TitleDrift.Shot a, TitleDrift.Shot b) =>
            Mathf.Max(Mathf.Max(Mathf.Abs(a.X - b.X), Mathf.Abs(a.Z - b.Z)), Mathf.Max(Mathf.Abs(a.Distance - b.Distance), Mathf.Abs(a.Y - b.Y)));

        static float Turn(TitleDrift.Shot a, TitleDrift.Shot b) =>
            Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(a.Yaw, b.Yaw)), Mathf.Abs(a.Pitch - b.Pitch));

        [Test]
        public void ItPassesThroughEachViewAndLoopsInTwoMinutes()
        {
            var drift = Demo();
            Assert.That(drift.Shots, Is.EqualTo(4));
            Assert.That(drift.LoopSeconds, Is.EqualTo(120f));
            for (int i = 0; i < drift.Shots; i++)
            {
                var at = drift.Evaluate(i * TitleDrift.SecondsPerShot);
                Assert.That(Gap(at, drift[i]), Is.LessThan(0.01f), $"view {i}");
                Assert.That(Turn(at, drift[i]), Is.LessThan(0.01f), $"view {i}");
                Assert.That(at.Y, Is.EqualTo(2000 + 0.1f * drift[i].X).Within(0.01f), "each view's target sits on the ground there");
            }
            Assert.That(Gap(drift.Evaluate(drift.LoopSeconds), drift.Evaluate(0)), Is.LessThan(0.01f));
            Assert.That(Turn(drift.Evaluate(drift.LoopSeconds - 0.001f), drift.Evaluate(0)), Is.LessThan(0.05f), "no jump where it loops");
        }

        [Test]
        public void ItGlidesFrameToFrameAndNeverStops()
        {
            var drift = Demo();
            const float frame = 1f / 60f;
            var last = drift.Evaluate(0);
            float slowest = float.MaxValue;
            for (float t = frame; t <= drift.LoopSeconds * 2; t += frame)
            {
                var now = drift.Evaluate(t);
                float moved = Gap(now, last), turned = Turn(now, last);
                Assert.That(moved, Is.LessThan(4f), $"no jump at {t:F2} s (metres in one frame)");
                Assert.That(turned, Is.LessThan(0.25f), $"no jerk at {t:F2} s (degrees in one frame)");
                slowest = Mathf.Min(slowest, Mathf.Max(moved, turned * 10));
                last = now;
            }
            Assert.That(slowest, Is.GreaterThan(0.001f), "it slows at each view but never holds still");
        }

        [Test]
        public void EvaluatingAllocatesNothing()
        {
            var drift = Demo();
            drift.Evaluate(1);   // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            float sum = 0;
            for (int i = 0; i < 10000; i++) sum += drift.Evaluate(i * 0.016f).Yaw;
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.That(after - before, Is.Zero, $"bytes allocated ({sum})");
        }

        [Test]
        public void AnotherMountainGetsAGentleSweepRoundItsCentre()
        {
            var drift = TitleDrift.For(46.935, -121.474, 2000, 330, (x, z) => float.NaN);   // Crystal Mountain, off the terrain everywhere
            for (int i = 0; i < drift.Shots; i++)
            {
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(drift[i].Yaw, 330)), Is.LessThanOrEqualTo(30f), "from the Home view's side");
                Assert.That(drift[i].Distance, Is.InRange(1800f, 2300f), "sized to the area");
                Assert.That(drift[i].Y, Is.Zero, "no ground there: the target stays level");
            }
        }
    }
}
