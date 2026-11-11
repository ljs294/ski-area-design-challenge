using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// GU-16 v2 (task 4.13): every path a skier can be on, sampled once per design (and per terrain edit), so a frame
    /// places 3,000 dots by table lookups and never asks the terrain anything ("agent elevation from precomputed samples").
    ///
    /// For each network edge (the simulation's edge order, <c>SimNetwork</c>): its centreline resampled every
    /// <see cref="StepMetres"/>, the ground under each of the <see cref="Lanes"/> lateral lanes at every sample (edits
    /// included), and GU-10's time table normalised to 0–1, so a skier's progress maps to a distance the way the worker's
    /// exit tick was worked out. For each lift: its line on the ground, bottom to top, for riders on tows and carpets and
    /// for the follow camera on a chair. Everything is in NativeArrays the placement job reads.
    /// </summary>
    public sealed class SkierPaths : IDisposable
    {
        public const float StepMetres = 2f;
        /// <summary>SN-24's lateral bands (<c>Pacing.WearBands</c>): a skier's lane is one of these across the run.</summary>
        public const int Lanes = 8;
        /// <summary>GU-10 v3: one time table per skier band (beginner, intermediate, advanced, expert).</summary>
        public const int Bands = 4;

        /// <summary>One edge's centreline (local metres, on the ground), its half width, and GU-10's table for it.</summary>
        public struct EdgeInput
        {
            public Vector3[] Line;
            public float HalfWidth;
            /// <summary>Metres from the edge's start at each station (from <c>SkiTimeTable.Fill</c>); null: even speed.</summary>
            public float[] TableAlong;
            /// <summary>
            /// GU-10 v3 (Guests Engine v0.1): per skier band (<see cref="Bands"/>), the ms to reach each station and how much
            /// that band holds back there (0.5–1). The same stations for every band. A missing band takes band 3's.
            /// </summary>
            public float[][] TableMs, TableCaution;
            public int TableCount;
        }

        /// <summary>Per edge: x first sample, y sample count, z first table entry, w table entries per band (the bands follow each other).</summary>
        public NativeArray<int4> Edges;
        /// <summary>Per edge: its length in metres and its half width.</summary>
        public NativeArray<float2> EdgeSize;
        /// <summary>Per sample: the centreline point (x, ground y, z) and the metres walked from the edge's start.</summary>
        public NativeArray<float4> Samples;
        /// <summary>Per sample: the unit vector to the run's right, on the level.</summary>
        public NativeArray<float2> Side;
        /// <summary>Per sample and lane: the ground under that lane (index sample × Lanes + lane).</summary>
        public NativeArray<float> LaneY;
        /// <summary>
        /// Per table entry (band b of edge e at Edges[e].z + b × Edges[e].w): x the metres from the edge's start, y the share of
        /// that band's time (0 to 1), z the band's caution over the station ending there (1: at ease; under 1: traversing).
        /// </summary>
        public NativeArray<float3> Table;
        /// <summary>Per lift: x first sample, y sample count, into <see cref="Samples"/> (the lift's line, bottom first).</summary>
        public NativeArray<int2> Lifts;
        public NativeArray<float> LiftLength;
        /// <summary>Per network node: where it is, on the ground (where a choosing skier waits).</summary>
        public NativeArray<float3> Nodes;

        public int EdgeCount => Edges.IsCreated ? Edges.Length : 0;
        public int LiftCount => Lifts.IsCreated ? Lifts.Length : 0;
        /// <summary>The simulation's design revision these paths belong to (the render frame names the one it was written for).</summary>
        public long Revision { get; private set; } = -1;

        public static SkierPaths Build(long revision, IReadOnlyList<EdgeInput> edges, IReadOnlyList<Vector3[]> lifts, IReadOnlyList<Vector3> nodes,
                                       Func<float, float, float> ground)
        {
            var samples = new List<float4>();
            var side = new List<float2>();
            var laneY = new List<float>();
            var table = new List<float3>();
            var p = new SkierPaths { Revision = revision };
            p.Edges = new NativeArray<int4>(edges.Count, Allocator.Persistent);
            p.EdgeSize = new NativeArray<float2>(edges.Count, Allocator.Persistent);
            for (int e = 0; e < edges.Count; e++)
            {
                var input = edges[e];
                int first = samples.Count;
                float length = Resample(input.Line, input.HalfWidth, ground, samples, side, laneY);
                int count = samples.Count - first;
                int t0 = table.Count;
                int per;
                if (input.TableAlong != null && input.TableCount >= 2 && input.TableMs != null && TimesOf(input, Bands - 1) != null)
                {
                    // The table's metres are along the trail's own centreline; the drawn line can differ by a few
                    // percent (it is resampled), so the table is scaled to end exactly at the drawn length. Each band's
                    // share is of its own total: every band reaches the bottom on its own exit tick.
                    float along = Mathf.Max(1e-3f, input.TableAlong[input.TableCount - 1]);
                    for (int b = 0; b < Bands; b++)
                    {
                        float[] ms = TimesOf(input, b);
                        float[] hold = input.TableCaution != null && b < input.TableCaution.Length ? input.TableCaution[b] : null;
                        float total = ms[input.TableCount - 1];
                        for (int k = 0; k < input.TableCount; k++)
                            table.Add(new float3(input.TableAlong[k] / along * length, ms[k] / total, hold != null ? hold[k] : 1f));
                    }
                    per = input.TableCount;
                }
                else
                {
                    for (int b = 0; b < Bands; b++)
                    {
                        table.Add(new float3(0, 0, 1));
                        table.Add(new float3(length, 1, 1));
                    }
                    per = 2;
                }
                p.Edges[e] = new int4(first, count, t0, per);
                p.EdgeSize[e] = new float2(length, Mathf.Max(0, input.HalfWidth));
            }
            p.Lifts = new NativeArray<int2>(lifts.Count, Allocator.Persistent);
            p.LiftLength = new NativeArray<float>(lifts.Count, Allocator.Persistent);
            for (int i = 0; i < lifts.Count; i++)
            {
                int first = samples.Count;
                float length = Resample(lifts[i], 0, ground, samples, side, laneY);
                p.Lifts[i] = new int2(first, samples.Count - first);
                p.LiftLength[i] = length;
            }
            p.Nodes = new NativeArray<float3>(Math.Max(1, nodes?.Count ?? 0), Allocator.Persistent);
            for (int n = 0; n < (nodes?.Count ?? 0); n++)
            {
                var at = nodes[n];
                p.Nodes[n] = new float3(at.x, Ground(ground, at.x, at.z, at.y), at.z);
            }
            p.Samples = ToNative(samples);
            p.Side = ToNative(side);
            p.LaneY = ToNative(laneY);
            p.Table = ToNative(table);
            return p;
        }

        /// <summary>A band's times, or the expert's where that band has none; null when the table is unusable.</summary>
        static float[] TimesOf(in EdgeInput input, int band)
        {
            float[] ms = band < input.TableMs.Length ? input.TableMs[band] : null;
            if ((ms == null || ms.Length < input.TableCount || ms[input.TableCount - 1] <= 0) && band != Bands - 1) return TimesOf(input, Bands - 1);
            return ms != null && ms.Length >= input.TableCount && ms[input.TableCount - 1] > 0 ? ms : null;
        }

        /// <summary>A lane's offset from the centreline, metres, for a run of this half width (lanes keep off the very edge).</summary>
        public static float LaneOffset(int lane, float halfWidth) =>
            ((lane + 0.5f) / Lanes - 0.5f) * 2f * halfWidth * 0.8f;

        static float Resample(Vector3[] line, float halfWidth, Func<float, float, float> ground,
                              List<float4> samples, List<float2> side, List<float> laneY)
        {
            if (line == null || line.Length == 0) { AddSample(Vector3.zero, 0, Vector2.right, halfWidth, ground, samples, side, laneY); return 0; }
            if (line.Length == 1) { AddSample(line[0], 0, Vector2.right, halfWidth, ground, samples, side, laneY); return 0; }
            float walked = 0;
            float next = 0;
            int seg = 0;
            float segStart = 0;
            float total = 0;
            for (int k = 1; k < line.Length; k++) total += Flat(line[k] - line[k - 1]);
            while (true)
            {
                while (seg < line.Length - 2 && segStart + Flat(line[seg + 1] - line[seg]) < next)
                {
                    segStart += Flat(line[seg + 1] - line[seg]);
                    seg++;
                }
                var a = line[seg];
                var b = line[seg + 1];
                float len = Flat(b - a);
                float t = len > 0 ? Mathf.Clamp01((next - segStart) / len) : 0;
                var at = Vector3.Lerp(a, b, t);
                var dir = new Vector2(b.x - a.x, b.z - a.z);
                dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector2.up;
                var right = new Vector2(dir.y, -dir.x);
                walked = next;
                AddSample(at, walked, right, halfWidth, ground, samples, side, laneY);
                if (next >= total) break;
                next = Mathf.Min(total, next + StepMetres);
            }
            return total;
        }

        static void AddSample(Vector3 at, float walked, Vector2 right, float halfWidth, Func<float, float, float> ground,
                              List<float4> samples, List<float2> side, List<float> laneY)
        {
            float y = Ground(ground, at.x, at.z, at.y);
            samples.Add(new float4(at.x, y, at.z, walked));
            side.Add(new float2(right.x, right.y));
            for (int l = 0; l < Lanes; l++)
            {
                float off = LaneOffset(l, halfWidth);
                laneY.Add(Ground(ground, at.x + right.x * off, at.z + right.y * off, y));
            }
        }

        static float Ground(Func<float, float, float> ground, float x, float z, float fallback)
        {
            float y = ground != null ? ground(x, z) : float.NaN;
            return !float.IsNaN(y) ? y : float.IsNaN(fallback) ? 0f : fallback;
        }

        static float Flat(Vector3 d) => Mathf.Sqrt(d.x * d.x + d.z * d.z);

        static NativeArray<T> ToNative<T>(List<T> list) where T : struct
        {
            var a = new NativeArray<T>(Math.Max(1, list.Count), Allocator.Persistent);
            for (int i = 0; i < list.Count; i++) a[i] = list[i];
            return a;
        }

        public void Dispose()
        {
            if (Edges.IsCreated) Edges.Dispose();
            if (EdgeSize.IsCreated) EdgeSize.Dispose();
            if (Samples.IsCreated) Samples.Dispose();
            if (Side.IsCreated) Side.Dispose();
            if (LaneY.IsCreated) LaneY.Dispose();
            if (Table.IsCreated) Table.Dispose();
            if (Lifts.IsCreated) Lifts.Dispose();
            if (LiftLength.IsCreated) LiftLength.Dispose();
            if (Nodes.IsCreated) Nodes.Dispose();
        }
    }
}
