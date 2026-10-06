using System;

namespace MountainPlanner.App
{
    /// <summary>
    /// One benchmark run's results (task 15), written as JSON by -benchmark and committed as the baseline under
    /// docs/perf/. tools/perf/compare.mjs reads it, so keep field names stable and bump <see cref="schema"/> on change.
    /// </summary>
    [Serializable]
    public sealed class BenchmarkReport
    {
        public string schema = "mountain-planner-benchmark/1";
        public string commit, builtUtc, measuredUtc;
        public bool commitDirty;
        public string gpu, cpu, unity;
        public int gpuMemoryMB, systemMemoryMB;
        public bool development;
        public string quality;
        public int width, height;
        public float renderScale, shadowDistance, lodBias;
        public int msaa;
        public string terrainDetail;
        public bool instancedTerrain, terrainShading, hud;
        public string site;
        public float siteSizeKm;
        public int trees;
        public int laps;
        public float lapSeconds;
        public FrameStats.Summary total;
        public Leg[] legs;
        public Budget budget;
        public Result result;

        [Serializable]
        public sealed class Leg
        {
            public string name;
            public FrameStats.Summary stats;
        }

        [Serializable]
        public sealed class Budget
        {
            public bool isChecked;
            public float p95Ms, p99Ms, percentOver50Ms, gfxMemoryMB;
            public long gcBytes;
        }

        /// <summary>
        /// Pass or fail against the budget; a check whose counter the player doesn't record counts as not measured.
        /// Garbage passes with under a byte per frame on average, no garbage collection and no recurring allocation
        /// (at most one frame in 10,000 allocating), the "0 bytes per frame in steady state" of 0.3 §8.
        /// </summary>
        [Serializable]
        public sealed class Result
        {
            public bool frameTime, garbage, memory, all;
            public bool garbageMeasured, memoryMeasured;
            public string summary;
        }
    }
}
