using System.Threading;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// BG-01 under Unity's Mono (task 4.13): the .NET run counts the worker's garbage per thread, but Mono boxes or
    /// allocates in places .NET doesn't, and the Development player saw ~128 B a tick with a crowd on the hill. Each
    /// piece of the worker's loop is checked on its own, so a failure names it.
    /// </summary>
    public sealed class WorkerGarbageTests
    {
        static readonly Pacing P = Pacing.Default;

        sealed class FakeTime : IRealTime
        {
            public long Microseconds { get; set; }
        }

        static SimWorld Busy()
        {
            var w = new SimWorld(P, GuestFixture.Resort(), new SimSite(43.59, -110.83), 2026, 7_777UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            const int fw = 160, fh = 120;
            var metres = new float[fw * fh];
            for (int i = 0; i < metres.Length; i++) metres[i] = 1.2f + (i % 7) * 0.05f;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, SimSnowpack.FromMetres(metres, fw, fh, 0, 0, fw, fh)));
            long until = SimWorld.DayMs + (DayHours.Default.OpenSecond + 3600) * 1000L;
            for (int guard = 0; w.FaceMs < until && guard < 20_000_000; guard++) w.Step(1);
            return w;
        }

        [Test]
        public void ATickAllocatesNothing()
        {
            var w = Busy();
            for (int k = 0; k < 300; k++) w.Step(1);
            Assert.That(w.SkierCount, NUnit.Framework.Is.GreaterThan(0), "a crowd on the hill");
            Assert.That(() => { for (int k = 0; k < 200; k++) w.Step(1); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void ATickWithItsSnapshotAllocatesNothing()
        {
            var w = Busy();
            var s = new SimSnapshot(P);
            for (int k = 0; k < 300; k++) w.Step(1, s);
            Assert.That(() => { for (int k = 0; k < 200; k++) w.Step(1, s); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void PublishingTheColumnAllocatesNothing()
        {
            var w = Busy();
            for (int k = 0; k < 5; k++) { w.Step(1); w.PublishSkierRender(); }
            Assert.That(() => { for (int k = 0; k < 50; k++) { w.PublishSkierRender(); } }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void TheHostsLoopAllocatesNothing()
        {
            var w = Busy();
            var time = new FakeTime();
            var host = new SimulationHost(w, time, threaded: false);
            host.Send(SimCommandKind.Resume, 0);
            host.Send(SimCommandKind.SetSpeed, 1);
            void Run(int services)
            {
                for (int k = 0; k < services; k++)
                {
                    time.Microseconds += 100_000;
                    host.Service();
                    var s = host.Latest;
                    var ring = host.Staging;
                    for (long n = ring.Released; n < s.StagedTiles; n++) ring.Read(n, out _, out _, out _, out _, out _, out _);
                    ring.Release(s.StagedTiles);
                }
            }
            Run(300);
            Assert.That(() => Run(200), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void TheTickTimingsP99AllocatesNothing()
        {
            // Array.Sort allocated here under Mono (the Development player's ~128 B a publish); the host selects instead.
            var a = new int[256];
            for (int i = 0; i < a.Length; i++) a[i] = (i * 7919) % 1000;
            SimulationHost.Select(a, a.Length, 250);
            Assert.That(() => { for (int k = 0; k < 20; k++) { a[k] = 1000 - k; SimulationHost.Select(a, a.Length, 250); } }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void StagingTheSnowAllocatesNothing()
        {
            var w = Busy();
            var ring = new TileStagingRing();
            void Run(int n)
            {
                for (int k = 0; k < n; k++)
                {
                    w.Step(1);
                    w.StageSnow(ring, false);
                    for (long t = ring.Released; t < ring.Written; t++) ring.Read(t, out _, out _, out _, out _, out _, out _);
                    ring.Release(ring.Written);
                }
            }
            Run(300);
            Assert.That(() => Run(200), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void TheWorkersWaitAllocatesNothing()
        {
            var e = new ManualResetEventSlim(false);
            e.Wait(1); e.Reset();
            Assert.That(() => { for (int k = 0; k < 20; k++) { e.Wait(1); e.Reset(); } }, Is.Not.AllocatingGCMemory());
            e.Dispose();
        }
    }
}
