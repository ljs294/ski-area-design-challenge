using System.Collections.Generic;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.13 PR C2's measurements: <c>-dotstress</c> pads the dots with copies of the skiers on runs (render only), and
    /// <c>-skierproof</c> turns the dots near the camera into stand-in figures by distance band, within the study's
    /// triangle budgets.
    /// </summary>
    public sealed class SkierBenchTests
    {
        const byte Skiing = (byte)GuestState.Skiing, Queued = (byte)GuestState.Queued;

        static SkierRender Ski(uint serial, ushort edge, uint entry, uint exit, byte band = 1) =>
            new SkierRender(serial, edge, 0, 0, SkierRender.NoNode, entry, exit, 2, Skiing, band);

        [Test]
        public void StressPadsWithCopiesOfTheSkiersOnRunsOnly()
        {
            var records = new NativeArray<SkierRender>(100, Allocator.Temp);
            records[0] = Ski(11, 3, 1000, 1600);
            records[1] = new SkierRender(12, 3, 4, 7, SkierRender.NoNode, 900, 0, 0, Queued, 2);
            records[2] = Ski(13, 5, 1200, 1500, band: 3);
            int n = DotStress.Pad(records, 3, 60, 1300);
            Assert.That(n, Is.EqualTo(60));
            Assert.That(records[1].State, Is.EqualTo(Queued), "the real records stay as they were");
            var serials = new HashSet<uint> { 11, 12, 13 };
            for (int i = 3; i < n; i++)
            {
                var r = records[i];
                Assert.That(serials.Add(r.Serial), Is.True, "every copy has a serial of its own");
                Assert.That(r.Serial & DotStress.CopyMark, Is.Not.Zero);
                Assert.That(r.State, Is.EqualTo(Skiing), "nobody is copied into a line");
                Assert.That(r.Segment == 3 || r.Segment == 5, Is.True, "a copy skis its skier's run");
                Assert.That(r.ExitTick - r.EntryTick, Is.EqualTo(r.Segment == 3 ? 600u : 300u), "at its skier's pace");
                Assert.That(r.EntryTick, Is.LessThanOrEqualTo(1300u + (r.ExitTick - r.EntryTick)));
                Assert.That(r.ExitTick, Is.GreaterThanOrEqualTo(1300u), "somewhere on its run now, not past its end");
            }
        }

        [Test]
        public void StressIsTheSameEveryTimeAndKeepsACopyWithItsSkier()
        {
            var a = new NativeArray<SkierRender>(40, Allocator.Temp);
            var b = new NativeArray<SkierRender>(40, Allocator.Temp);
            var c = new NativeArray<SkierRender>(40, Allocator.Temp);
            a[0] = Ski(21, 1, 500, 900); a[1] = Ski(22, 2, 600, 800);
            c[0] = Ski(21, 1, 500, 900); c[1] = Ski(22, 2, 600, 800);
            b[0] = Ski(22, 2, 600, 800); b[1] = Ski(21, 1, 500, 900);   // the same skiers in another order
            DotStress.Pad(a, 2, 40, 700);
            DotStress.Pad(b, 2, 40, 700);
            DotStress.Pad(c, 2, 40, 700);
            var placesA = new List<(ushort, uint, byte)>();
            var placesB = new List<(ushort, uint, byte)>();
            for (int i = 2; i < 40; i++)
            {
                Assert.That(c[i].Serial == a[i].Serial && c[i].EntryTick == a[i].EntryTick && c[i].Lane == a[i].Lane, Is.True, "the same column pads the same");
                Assert.That(a[i].Slot, Is.EqualTo(DotStress.NoSlot), "a copy never shares a real skier's easing slot");
                placesA.Add((a[i].Segment, a[i].EntryTick, a[i].Lane));
                placesB.Add((b[i].Segment, b[i].EntryTick, b[i].Lane));
            }
            Assert.That(placesB, Is.EquivalentTo(placesA), "each copy keeps its place on its skier's run whatever the order");
        }

        [Test]
        public void StressFillsUpEvenWithFewOnARun()
        {
            var records = new NativeArray<SkierRender>(11_000, Allocator.Temp);
            records[0] = Ski(1, 0, 100, 400);
            Assert.That(DotStress.Pad(records, 1, 11_000, 200), Is.EqualTo(11_000), "one skier on a run is enough to fill any count");
        }

        [Test]
        public void StressWithNobodyOnARunAddsNothing()
        {
            var records = new NativeArray<SkierRender>(10, Allocator.Temp);
            records[0] = new SkierRender(1, 0, 0, 0, SkierRender.NoNode, 0, 0, 0, Queued, 0);
            Assert.That(DotStress.Pad(records, 1, 10, 50), Is.EqualTo(1));
        }

        [Test]
        public void TheStandInsFitTheStudysTriangleBudgets()
        {
            for (int band = 0; band < SkierProof.Bands; band++)
            {
                var mesh = SkierProof.BuildStandIn(band);
                int triangles = mesh.triangles.Length / 3;
                Assert.That(triangles, Is.LessThanOrEqualTo(SkierProof.TriangleBudget[band]), $"band {band}");
                Assert.That(triangles, Is.GreaterThanOrEqualTo(SkierProof.TriangleBudget[band] * 8 / 10), $"band {band} measures near its budget, not a cheap shape");
                Assert.That(mesh.bounds.size.y, Is.InRange(1.6f, 2f), "a standing skier's height");
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void FiguresAreTheDotsInViewWithinReachByDistanceBand()
        {
            var go = new GameObject("proof camera");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 60; cam.nearClipPlane = 0.3f; cam.farClipPlane = 5000; cam.aspect = 16f / 9f;
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // looking along +z
            var proof = new SkierProof(null, 16);
            var dots = new NativeArray<float4>(16, Allocator.TempJob);   // a job reads it
            var records = new NativeArray<SkierRender>(16, Allocator.TempJob);
            float[] ahead = { 10, 60, 200, 300 };
            for (int i = 0; i < ahead.Length; i++) { dots[i] = new float4(0, 0, ahead[i], 5); records[i] = Ski((uint)(i + 1), 0, 0, 10); }
            dots[4] = new float4(0, 0, -20, 5); records[4] = Ski(5, 0, 0, 10);            // behind the camera
            dots[5] = new float4(0, 0, 20, SkierPlaceJob.Hidden); records[5] = Ski(6, 0, 0, 10);   // a hidden rider
            try
            {
                proof.Select(dots, records, 6, cam);
                Assert.That((proof.CountIn(0), proof.CountIn(1), proof.CountIn(2)), Is.EqualTo((1, 1, 1)), "10 m, 60 m and 200 m: one in each band");
                Assert.That(proof.Figures, Is.EqualTo(3));
                for (int i = 0; i < 3; i++) Assert.That(dots[i].w, Is.EqualTo(SkierPlaceJob.Hidden), "a figure's dot isn't drawn too");
                Assert.That(dots[3].w, Is.EqualTo(5f), "past 250 m it stays a dot");
                Assert.That(dots[4].w, Is.EqualTo(5f), "behind the camera it stays a dot");

                for (int i = 0; i < 3; i++) dots[i] = new float4(0, 0, ahead[i], 5);
                proof.AllLod0 = true;
                proof.Select(dots, records, 6, cam);
                Assert.That((proof.CountIn(0), proof.CountIn(1), proof.CountIn(2)), Is.EqualTo((3, 0, 0)), "lod0: every figure at the closest band's mesh");
            }
            finally
            {
                proof.Dispose();
                dots.Dispose();
                records.Dispose();
                Object.DestroyImmediate(go);
            }
        }
    }
}
