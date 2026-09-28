using System;
using Unity.Profiling;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Frame-time recorder for the Lift Lab benchmark (0.3 §8.1): unscaled frame times go into a preallocated
    /// buffer, so recording allocates nothing per frame; percentiles are computed once at the end. Render
    /// counters (batches, SetPass calls, triangles, GC bytes) come from ProfilerRecorder where the player
    /// exposes them (all of them in a Development build).
    /// </summary>
    public sealed class FrameStats : IDisposable
    {
        readonly float[] _ms;
        int _count;
        ProfilerRecorder _batches, _setPass, _triangles, _gc;
        long _batchSum, _setPassSum, _triangleSum, _gcSum, _gcMax;
        int _counterFrames;

        public FrameStats(int capacity = 8192)
        {
            _ms = new float[capacity];
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        }

        public int Count => _count;

        public void Reset()
        {
            _count = 0;
            _batchSum = _setPassSum = _triangleSum = _gcSum = _gcMax = 0;
            _counterFrames = 0;
        }

        /// <summary>Call once per frame.</summary>
        public void Record(float unscaledDeltaTime)
        {
            if (_count < _ms.Length) _ms[_count++] = unscaledDeltaTime * 1000f;
            _counterFrames++;
            if (_batches.Valid) _batchSum += _batches.LastValue;
            if (_setPass.Valid) _setPassSum += _setPass.LastValue;
            if (_triangles.Valid) _triangleSum += _triangles.LastValue;
            if (_gc.Valid)
            {
                long gc = _gc.LastValue;
                _gcSum += gc;
                if (gc > _gcMax) _gcMax = gc;
            }
        }

        public Summary Summarise()
        {
            var sorted = new float[_count];
            Array.Copy(_ms, sorted, _count);
            Array.Sort(sorted);
            float Pct(float p) => _count == 0 ? 0 : sorted[Mathf.Clamp(Mathf.CeilToInt(p * _count) - 1, 0, _count - 1)];
            int frames = Mathf.Max(1, _counterFrames);
            return new Summary
            {
                frames = _count,
                p50Ms = Pct(0.50f),
                p95Ms = Pct(0.95f),
                p99Ms = Pct(0.99f),
                maxMs = _count == 0 ? 0 : sorted[_count - 1],
                batches = _batches.Valid ? _batchSum / frames : -1,
                setPassCalls = _setPass.Valid ? _setPassSum / frames : -1,
                triangles = _triangles.Valid ? _triangleSum / frames : -1,
                gcBytesPerFrame = _gc.Valid ? _gcSum / frames : -1,
                gcBytesMaxFrame = _gc.Valid ? _gcMax : -1,
            };
        }

        public void Dispose()
        {
            _batches.Dispose();
            _setPass.Dispose();
            _triangles.Dispose();
            _gc.Dispose();
        }

        [Serializable]
        public struct Summary
        {
            public int frames;
            public float p50Ms, p95Ms, p99Ms, maxMs;
            public long batches, setPassCalls, triangles, gcBytesPerFrame, gcBytesMaxFrame;
        }
    }
}
