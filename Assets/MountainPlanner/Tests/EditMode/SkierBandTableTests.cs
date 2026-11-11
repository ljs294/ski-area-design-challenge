using System.Collections.Generic;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// GU-10 v3 (Guests Engine v0.1, owner 2026-11-09): a dot goes down its own band's table, so a beginner and an expert
    /// who set off together part on the pitch and still reach the bottom on their exit ticks; where a band holds back the
    /// dot traverses, and the traverse stays on the run.
    /// </summary>
    public sealed class SkierBandTableTests
    {
        const float Length = 200f, HalfWidth = 15f;

        static SkierPaths Paths()
        {
            // One straight 200 m run north; its top half a pitch the beginner fears (half speed, caution 0.5), its bottom
            // half a runout where everybody coasts.
            var input = new SkierPaths.EdgeInput
            {
                Line = new[] { new Vector3(0, float.NaN, 0), new Vector3(0, float.NaN, Length) },
                HalfWidth = HalfWidth,
                TableAlong = new[] { 0f, 100f, 200f },
                TableCount = 3,
                TableMs = new float[SkierPaths.Bands][],
                TableCaution = new float[SkierPaths.Bands][],
            };
            input.TableMs[0] = new[] { 0f, 1000f, 1500f };
            input.TableCaution[0] = new[] { 1f, 0.5f, 1f };
            for (int b = 1; b < SkierPaths.Bands; b++)
            {
                input.TableMs[b] = new[] { 0f, 300f, 800f };
                input.TableCaution[b] = new[] { 1f, 1f, 1f };
            }
            return SkierPaths.Build(1, new List<SkierPaths.EdgeInput> { input }, new List<Vector3[]>(), new List<Vector3> { Vector3.zero, Vector3.zero },
                                    (x, z) => 0f);
        }

        static float4[] Place(SkierPaths paths, double drawTick, params SkierRender[] records) =>
            Place(paths, drawTick, float4x4.identity, float3.zero, out _, records);

        static float4[] Place(SkierPaths paths, double drawTick, float4x4 viewProjection, float3 camera, out float3[] screens,
                              params SkierRender[] records)
        {
            var rec = new NativeArray<SkierRender>(records, Allocator.TempJob);
            var none3 = new NativeArray<float3>(1, Allocator.TempJob);
            var none2 = new NativeArray<int2>(1, Allocator.TempJob);
            var ropes = new NativeArray<uint>(1, Allocator.TempJob);
            var riders = new NativeArray<byte>(1, Allocator.TempJob);
            var lines = new NativeArray<int>(1, Allocator.TempJob);
            var snow = new NativeArray<float>(1, Allocator.TempJob);
            var prev = new NativeArray<float3>(16, Allocator.TempJob);
            var prevSerial = new NativeArray<uint>(16, Allocator.TempJob);
            var dots = new NativeArray<float4>(records.Length, Allocator.TempJob);
            var screen = new NativeArray<float3>(records.Length, Allocator.TempJob);
            try
            {
                new SkierPlaceJob
                {
                    Records = rec, DrawTick = drawTick, WalkStep = 1,
                    Edges = paths.Edges, EdgeSize = paths.EdgeSize, Samples = paths.Samples, Side = paths.Side,
                    LaneY = paths.LaneY, Table = paths.Table, LiftPaths = paths.Lifts, Nodes = paths.Nodes,
                    Slots = none3, LiftSlots = none2, RopeNow = ropes, DrawRiders = riders, LineCount = lines,
                    Snow = snow, SnowCell = 8,
                    ViewProjection = viewProjection, ScreenSize = new float2(1920, 1080), CameraPosition = camera,
                    Prev = prev, PrevSerial = prevSerial, Dots = dots, Screen = screen,
                }.Run(records.Length);
                screens = screen.ToArray();
                return dots.ToArray();
            }
            finally
            {
                rec.Dispose(); none3.Dispose(); none2.Dispose(); ropes.Dispose(); riders.Dispose(); lines.Dispose();
                snow.Dispose(); prev.Dispose(); prevSerial.Dispose(); dots.Dispose(); screen.Dispose();
            }
        }

        static SkierRender Skier(uint serial, byte band, ushort slot, byte lane = 4) =>
            new SkierRender(serial, 0, 0, slot, 0, 0, 100, lane, (byte)GuestState.Skiing, band);

        [Test]
        public void ABeginnerAndAnExpertPartOnThePitchAndMeetAtTheBottom()
        {
            using var paths = Paths();
            Assert.That(paths.Edges[0].w, Is.EqualTo(3), "three entries per band");
            Assert.That(paths.Table.Length, Is.EqualTo(3 * SkierPaths.Bands));
            // A third of the way through the time: the beginner has crept a third of the way down its slow pitch (share
            // 0.33 of 0.67), the expert is nearly off it (0.33 of 0.375).
            var mid = Place(paths, 33, Skier(1, 0, 1), Skier(2, 3, 2));
            Assert.That(mid[0].z, Is.EqualTo(49.5f).Within(1f), "the beginner holds back on the pitch");
            Assert.That(mid[1].z, Is.EqualTo(88f).Within(1f), "the expert lets it run");
            var end = Place(paths, 100, Skier(1, 0, 1), Skier(2, 3, 2));
            Assert.That(end[0].z, Is.EqualTo(Length).Within(0.01f));
            Assert.That(end[1].z, Is.EqualTo(Length).Within(0.01f), "both at the bottom on the worker's exit tick");
        }

        /// <summary>
        /// Finding 3 (owner, 2026-11-10): clicks missed the dots because the target was the skier's feet while the shader
        /// draws the dot 0.9 m up and pulled towards the camera. The job's screen point is now the drawn centre.
        /// </summary>
        [Test]
        public void AClickLandsWhereTheDotIsDrawnNotAtTheFeet()
        {
            using var paths = Paths();
            // A camera close by, 15 m off the run and a little above it, looking at the middle of the run.
            var camPos = new Vector3(12f, 6f, 92f);
            var look = Quaternion.LookRotation(new Vector3(0, 0, 100) - camPos);
            var view = Matrix4x4.TRS(camPos, look, new Vector3(1, 1, -1)).inverse;   // as Camera.worldToCameraMatrix
            var proj = Matrix4x4.Perspective(60f, 1920f / 1080f, 0.3f, 5000f);
            float4x4 vp = proj * view;
            var dots = Place(paths, 66, vp, camPos, out var screens, Skier(3, 3, 1));
            float3 feet = dots[0].xyz;
            float2 Project(float3 w)
            {
                float4 c = math.mul(vp, new float4(w, 1));
                return new float2((c.x / c.w * 0.5f + 0.5f) * 1920f, (c.y / c.w * 0.5f + 0.5f) * 1080f);
            }
            // The shader's vertex maths, written out: a standing skier's middle, pulled towards the camera.
            float3 world = feet + new float3(0, 0.9f, 0);
            float3 toCam = (float3)camPos - world;
            float dist = math.length(toCam);
            float3 drawn = world + toCam / dist * math.min(1.5f, dist * 0.01f);
            Assert.That(math.distance(screens[0].xy, Project(drawn)), Is.LessThan(0.5f), "the target is the drawn centre");
            Assert.That(math.distance(Project(drawn), Project(feet)), Is.GreaterThan(SkierLayer.HitPixels),
                        "up close the feet are more than the hit radius from the dot (the old bug)");
            Assert.That(math.distance(SkierPlaceJob.DrawnCentre(feet, camPos), drawn), Is.LessThan(1e-4f), "one formula for both");
        }

        [Test]
        public void AFrightenedSkierTraversesButStaysOnTheRun()
        {
            using var paths = Paths();
            float widest = 0f;
            for (int t = 0; t <= 66; t++)
            {
                // Outside lanes too: the swing is clamped to the run's edge.
                var dots = Place(paths, t, Skier(7, 0, 1, 0), Skier(8, 0, 2, 7), Skier(9, 3, 3, 4));
                for (int i = 0; i < 2; i++) Assert.That(math.abs(dots[i].x), Is.LessThanOrEqualTo(HalfWidth * 0.8f + 1e-3f), $"tick {t}");
                widest = math.max(widest, math.abs(dots[0].x - SkierPaths.LaneOffset(0, HalfWidth)));
                Assert.That(dots[2].x, Is.EqualTo(SkierPaths.LaneOffset(4, HalfWidth)).Within(1e-3f), "an expert at ease keeps its lane");
            }
            Assert.That(widest, Is.GreaterThan(2f), "the beginner swings across the pitch");
            Assert.That(SkierPlaceJob.Traverse(10f, 1f, HalfWidth, 5), Is.EqualTo(0f), "no swing at ease");
        }
    }
}
