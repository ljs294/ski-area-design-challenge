using System;
using Unity.Profiling;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Frame-time recorder for benchmarks (0.3 §8.1; the Lift Lab and the mountain benchmark, task 15): frame and
    /// GPU times go into preallocated buffers, so recording allocates nothing per frame; percentiles are computed
    /// once at the end. Render and memory counters come from ProfilerRecorder where the player exposes them (all of
    /// them in a Development build); a counter the player doesn't record reads -1 in the summary.
    /// </summary>
    public sealed class FrameStats : IDisposable
    {
        readonly float[] _ms, _gpuMs;
        int _count, _gpuCount, _over50;
        double _msSum;
        ProfilerRecorder _batches, _setPass, _triangles, _drawCalls, _gc, _gfxMemory, _totalMemory;
        long _batchSum, _setPassSum, _triangleSum, _drawCallSum, _gcSum, _gcMax, _gcFrames, _gfxMax, _totalMax;
        int _counterFrames;
        long _heapStart, _heapEnd = -1;
        int _collectionsStart, _collectionsEnd;
        /// <summary>The first frames that allocated: when (seconds into the measurement) and how much, to find the source.</summary>
        const int MaxGcEvents = 16;
        readonly float[] _gcEventSeconds = new float[MaxGcEvents];
        readonly long[] _gcEventBytes = new long[MaxGcEvents];
        int _gcEvents;
        /// <summary>
        /// ProfilerRecorder.LastValue lags the frame being recorded (by up to two frames for the GC counter), so the
        /// first frames after Reset report work from before the measurement started; their counters are skipped.
        /// </summary>
        const int SkipCounterFrames = 3;
        int _skipCounters;
        static readonly FrameTiming[] Timing = new FrameTiming[1];

        public FrameStats(int capacity = 1 << 17)   // 30 s at over 4,000 fps; 1 MB allocated once
        {
            _ms = new float[capacity];
            _gpuMs = new float[capacity];
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            _gfxMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
            _totalMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
            MarkHeap();
        }

        /// <summary>
        /// The managed heap where measuring starts. Release players don't record "GC Allocated In Frame", so the heap's growth
        /// and the number of collections between here and <see cref="Stop"/> show whether anything allocated: with no
        /// collection, any allocation grows the heap.
        /// </summary>
        void MarkHeap()
        {
            _heapStart = GC.GetTotalMemory(false);
            _collectionsStart = GC.CollectionCount(0);
            _heapEnd = -1;
        }

        /// <summary>Ends a measurement for the heap check; call straight after the last <see cref="Record"/>, before anything allocates.</summary>
        public void Stop()
        {
            _heapEnd = GC.GetTotalMemory(false);
            _collectionsEnd = GC.CollectionCount(0);
        }

        public int Count => _count;

        /// <summary>
        /// The GPU time of the latest frame Unity has timings for (ms), or -1 where the player has no frame timing
        /// (PlayerSettings.enableFrameTimingStats off). Call once per frame and pass the value to every recorder.
        /// </summary>
        public static float SampleGpuMs()
        {
            FrameTimingManager.CaptureFrameTimings();
            return FrameTimingManager.GetLatestTimings(1, Timing) > 0 && Timing[0].gpuFrameTime > 0 ? (float)Timing[0].gpuFrameTime : -1f;
        }

        public void Reset()
        {
            _count = _gpuCount = _over50 = 0;
            _msSum = 0;
            _batchSum = _setPassSum = _triangleSum = _drawCallSum = _gcSum = _gcMax = _gcFrames = _gfxMax = _totalMax = 0;
            _counterFrames = 0;
            _gcEvents = 0;
            _skipCounters = SkipCounterFrames;
            MarkHeap();
        }

        /// <summary>Call once per frame; <paramref name="gpuMs"/> from <see cref="SampleGpuMs"/>, or -1 to skip GPU time.</summary>
        public void Record(float unscaledDeltaTime, float gpuMs = -1f)
        {
            float ms = unscaledDeltaTime * 1000f;
            if (_count < _ms.Length) _ms[_count++] = ms;
            _msSum += ms;
            if (ms > 50f) _over50++;
            if (gpuMs > 0 && _gpuCount < _gpuMs.Length) _gpuMs[_gpuCount++] = gpuMs;
            if (_skipCounters > 0)
            {
                _skipCounters--;
                return;
            }
            _counterFrames++;
            if (_batches.Valid) _batchSum += _batches.LastValue;
            if (_setPass.Valid) _setPassSum += _setPass.LastValue;
            if (_triangles.Valid) _triangleSum += _triangles.LastValue;
            if (_drawCalls.Valid) _drawCallSum += _drawCalls.LastValue;
            if (_gc.Valid)
            {
                long gc = _gc.LastValue;
                _gcSum += gc;
                if (gc > 0)
                {
                    _gcFrames++;
                    if (_gcEvents < MaxGcEvents)
                    {
                        _gcEventSeconds[_gcEvents] = (float)(_msSum / 1000.0);
                        _gcEventBytes[_gcEvents++] = gc;
                    }
                }
                if (gc > _gcMax) _gcMax = gc;
            }
            if (_gfxMemory.Valid && _gfxMemory.LastValue > _gfxMax) _gfxMax = _gfxMemory.LastValue;
            if (_totalMemory.Valid && _totalMemory.LastValue > _totalMax) _totalMax = _totalMemory.LastValue;
        }

        public Summary Summarise()
        {
            var sorted = Sorted(_ms, _count);
            var gpu = Sorted(_gpuMs, _gpuCount);
            int frames = Mathf.Max(1, _counterFrames);
            return new Summary
            {
                frames = _count,
                meanMs = _count == 0 ? 0 : (float)(_msSum / _count),
                p50Ms = Pct(sorted, 0.50f),
                p95Ms = Pct(sorted, 0.95f),
                p99Ms = Pct(sorted, 0.99f),
                maxMs = _count == 0 ? 0 : sorted[_count - 1],
                framesOver50Ms = _over50,
                percentOver50Ms = _count == 0 ? 0 : 100f * _over50 / _count,
                gpuFrames = _gpuCount,
                gpuP50Ms = Pct(gpu, 0.50f),
                gpuP95Ms = Pct(gpu, 0.95f),
                gpuP99Ms = Pct(gpu, 0.99f),
                batches = _batches.Valid ? _batchSum / frames : -1,
                setPassCalls = _setPass.Valid ? _setPassSum / frames : -1,
                triangles = _triangles.Valid ? _triangleSum / frames : -1,
                drawCalls = _drawCalls.Valid ? _drawCallSum / frames : -1,
                gcBytesPerFrame = _gc.Valid ? _gcSum / frames : -1,
                gcBytesTotal = _gc.Valid ? _gcSum : -1,
                gcFramesWithAllocations = _gc.Valid ? _gcFrames : -1,
                gcBytesMaxFrame = _gc.Valid ? _gcMax : -1,
                gfxMemoryMBMax = _gfxMemory.Valid ? _gfxMax / (1024f * 1024f) : -1,
                totalMemoryMBMax = _totalMemory.Valid ? _totalMax / (1024f * 1024f) : -1,
                gcEventSeconds = Copy(_gcEventSeconds, _gcEvents),
                gcEventBytes = Copy(_gcEventBytes, _gcEvents),
                heapGrowthBytes = _heapEnd < 0 ? -1 : _heapEnd - _heapStart,
                gcCollections = _heapEnd < 0 ? -1 : _collectionsEnd - _collectionsStart,
            };
        }

        static T[] Copy<T>(T[] values, int count)
        {
            var copy = new T[count];
            Array.Copy(values, copy, count);
            return copy;
        }

        static float[] Sorted(float[] values, int count)
        {
            var sorted = new float[count];
            Array.Copy(values, sorted, count);
            Array.Sort(sorted);
            return sorted;
        }

        static float Pct(float[] sorted, float p) =>
            sorted.Length == 0 ? 0 : sorted[Mathf.Clamp(Mathf.CeilToInt(p * sorted.Length) - 1, 0, sorted.Length - 1)];

        public void Dispose()
        {
            _batches.Dispose();
            _setPass.Dispose();
            _triangles.Dispose();
            _drawCalls.Dispose();
            _gc.Dispose();
            _gfxMemory.Dispose();
            _totalMemory.Dispose();
        }

        [Serializable]
        public struct Summary
        {
            public int frames;
            public float meanMs, p50Ms, p95Ms, p99Ms, maxMs;
            public int framesOver50Ms;
            public float percentOver50Ms;
            public int gpuFrames;
            public float gpuP50Ms, gpuP95Ms, gpuP99Ms;
            public long batches, setPassCalls, triangles, drawCalls;
            /// <summary>Garbage: the average per frame, the total, how many frames allocated, and the worst frame (bytes; -1 where not recorded).</summary>
            public long gcBytesPerFrame, gcBytesTotal, gcFramesWithAllocations, gcBytesMaxFrame;
            /// <summary>Peak graphics memory and peak memory Unity tracks for the process (MB; -1 where not recorded).</summary>
            public float gfxMemoryMBMax, totalMemoryMBMax;
            /// <summary>The managed heap's growth and the garbage collections between Reset and Stop (-1 without Stop): the release player's garbage check.</summary>
            /// <summary>The first frames that allocated (up to 16): seconds into the measurement, and bytes.</summary>
            public float[] gcEventSeconds;
            public long[] gcEventBytes;
            public long heapGrowthBytes;
            public int gcCollections;

        }
    }
}
