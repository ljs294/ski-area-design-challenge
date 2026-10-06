using System;
using System.Collections;
using System.Linq;
using MountainPlanner.App;
using MountainPlanner.Persistence;
using NUnit.Framework;
using Unity.PerformanceTesting;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 15 (0.3 §8): the benchmark's fixed camera path through Unity's Performance Testing package. Opens the
    /// 5 km Jackson Hole demo from the library, flies one lap of the path and records frame time, GPU time and
    /// garbage per frame as Performance Testing sample groups (PerformanceTestResults.json beside the test results).
    /// It runs in the editor and in a player (-testPlatform StandaloneWindows64). Its numbers are informational: the
    /// budgets, garbage included, are checked by the game's own -benchmark (demo.bat 41), because the test framework
    /// allocates for itself every frame (about 11.6 MB a lap in a player, measured in task 15), which would swamp the
    /// game's zero. Skipped when the demo isn't downloaded (demo.bat 12).
    /// </summary>
    public sealed class BenchmarkPathTests
    {
        const string ViewerScene = "Assets/MountainPlanner/Scenes/MountainViewer.unity";

        [UnityTearDown]
        public IEnumerator UnloadTheViewer()
        {
            MountainViewer.RequestedPackage = null;
            var viewer = SceneManager.GetSceneByPath(ViewerScene);
            if (!viewer.IsValid() || !viewer.isLoaded) yield break;
            SceneManager.SetActiveScene(SceneManager.CreateScene("BenchmarkPathTests empty"));
            yield return SceneManager.UnloadSceneAsync(viewer);
            yield return Resources.UnloadUnusedAssets();
        }

        [UnityTest, Performance, Timeout(600000)]
        public IEnumerator TheBenchmarkPathRecordsPerformanceSamples()
        {
            var demo = ResortLibrary.Scan(MountainViewer.DataRoot).Where(e => e.OriginalName == "Jackson Hole")
                .OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
            if (demo == null || demo.SizeKm < 5) Assert.Ignore("Download the 5 km Jackson Hole demo first (demo.bat option 12).");

            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = demo.Folder;
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            MountainViewer viewer = null;
            float until = Time.realtimeSinceStartup + 300;
            while (Time.realtimeSinceStartup < until)
            {
                viewer = Object.FindAnyObjectByType<MountainViewer>();
                if (viewer != null && viewer.BenchmarkReady) break;
                yield return null;
            }
            Assert.That(viewer != null && viewer.BenchmarkReady, "the demo mountain didn't open in time");
            if (viewer.Camera != null) viewer.Camera.InputEnabled = false;
            if (viewer.Hud != null) viewer.Hud.SetVisible(false);

            var path = viewer.CreateBenchmarkPath();
            // Warm up on the first leg, then measure one lap.
            for (float start = Time.unscaledTime; Time.unscaledTime - start < 3f;)
            {
                viewer.PlaceOnPath(path, 0);
                yield return null;
            }
            var frame = new SampleGroup("Frame time", SampleUnit.Millisecond);
            var gpu = new SampleGroup("GPU time", SampleUnit.Millisecond);
            var garbage = new SampleGroup("Garbage per frame", SampleUnit.Byte);
            using var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            long total = 0;
            bool first = true;
            for (float start = Time.unscaledTime, t = 0; t < path.LapSeconds; t = Time.unscaledTime - start)
            {
                viewer.PlaceOnPath(path, t);
                yield return null;
                Measure.Custom(frame, Time.unscaledDeltaTime * 1000.0);
                float gpuMs = FrameStats.SampleGpuMs();
                if (gpuMs > 0) Measure.Custom(gpu, gpuMs);
                if (gc.Valid && !first)   // LastValue is the previous frame's: the first one is from before measuring
                {
                    Measure.Custom(garbage, gc.LastValue);
                    total += gc.LastValue;
                }
                first = false;
            }

            // Reported, not asserted: the test framework's own per-frame allocations are in this figure (see above).
            Debug.Log($"[BenchmarkPathTests] garbage over one lap, test framework included: {total} B ({(gc.Valid ? "counter recorded" : "counter not recorded")}, editor {Application.isEditor})");
            Assert.That(path.LapSeconds, Is.GreaterThan(0), "the path has legs");
        }
    }
}
