using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.App;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Round 4 of the brush (a drag like Cities Skylines 2's): the live ground wants to skip Unity's whole-tile
    /// <c>SyncHeightmap</c> on every dab frame, which only works if <c>SetHeightsDelayLOD</c> alone puts the new heights
    /// into the GPU heightmap the terrain shader reads. This probe settles that on a real tile, and whether a sync ever
    /// hands the tile a new heightmap texture (which would need <see cref="TerrainTiles.BindSplat"/> again). It logs its
    /// findings as [Probe] lines; it fails only if the heightmap is wrong once synced.
    /// </summary>
    public sealed class LiveHeightmapProbeTests
    {
        string _package;

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [OneTimeSetUp]
        public void CopyTestTerrain()
        {
            string source = Path.Combine(ProjectRoot, "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source)) Assert.Ignore("TestData/jackson-hole-2km is not available.");
            if (new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000) Assert.Ignore("Git LFS hasn't fetched the test terrain.");
            _package = Path.Combine(Path.GetTempPath(), "mp-unity-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(_package, Path.GetFileName(f)));
        }

        [OneTimeTearDown]
        public void Clean()
        {
            if (_package != null && Directory.Exists(_package)) Directory.Delete(_package, true);
        }

        static IEnumerator AwaitTask(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception!.InnerException!;
        }

        /// <summary>The GPU heightmap over a region, in metres, read back through an RFloat render texture.</summary>
        static float[,] GpuHeights(TerrainData data, int x0, int y0, int w, int h)
        {
            int res = data.heightmapResolution;
            var p = TerrainTiles.HeightmapParams(data);
            var rt = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(data.heightmapTexture, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var read = new Texture2D(res, res, TextureFormat.RFloat, false, true);
            read.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var metres = new float[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    metres[y, x] = read.GetPixel(x0 + x, y0 + y).r * p.y;
            Object.Destroy(read);
            return metres;
        }

        static (int Differ, float Worst) Compare(TerrainData data, float[,] gpu, int x0, int y0)
        {
            int differ = 0;
            float worst = 0;
            for (int y = 0; y < gpu.GetLength(0); y++)
                for (int x = 0; x < gpu.GetLength(1); x++)
                {
                    float err = Mathf.Abs(gpu[y, x] - data.GetHeight(x0 + x, y0 + y));
                    if (err > 0.02f) differ++;
                    worst = Mathf.Max(worst, err);
                }
            return (differ, worst);
        }

        [UnityTest]
        public IEnumerator SetHeightsDelayLODAloneReachesTheGpuHeightmap()
        {
            var open = ResortOpener.OpenAsync(_package, null, TerrainDetail.High, null);
            yield return AwaitTask(open);
            var resort = open.Result;
            yield return AwaitTask(resort.CoverReady);
            try
            {
                var terrain = resort.Tiles.Values.First(t => t.terrainData.heightmapResolution == TerrainTiles.Resolution);
                var data = terrain.terrainData;
                const int x0 = 300, y0 = 300, size = 64;
                int textureBefore = data.heightmapTexture.GetInstanceID();
                var before = Compare(data, GpuHeights(data, x0, y0, size, size), x0, y0);
                Assert.That(before.Differ, Is.EqualTo(0), $"the GPU heightmap matched the CPU heights before the probe (worst {before.Worst:F3} m)");

                // Raise a 64-post block by 3 m with SetHeightsDelayLOD only, and look at the GPU heightmap a frame later.
                var block = data.GetHeights(x0, y0, size, size);
                float bump = 3f / data.size.y;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        block[y, x] = Mathf.Min(block[y, x] + bump, 0.5f);
                data.SetHeightsDelayLOD(x0, y0, block);
                yield return null;
                var delayed = Compare(data, GpuHeights(data, x0, y0, size, size), x0, y0);
                int textureAfterSet = data.heightmapTexture.GetInstanceID();
                Debug.Log($"[Probe] SetHeightsDelayLOD alone: the GPU heightmap differs from the CPU heights at {delayed.Differ} of {size * size} posts (worst {delayed.Worst:F3} m); texture {(textureAfterSet == textureBefore ? "unchanged" : "REPLACED")}");

                data.SyncHeightmap();
                yield return null;
                var synced = Compare(data, GpuHeights(data, x0, y0, size, size), x0, y0);
                int textureAfterSync = data.heightmapTexture.GetInstanceID();
                Debug.Log($"[Probe] after SyncHeightmap: differs at {synced.Differ} posts (worst {synced.Worst:F3} m); texture {(textureAfterSync == textureBefore ? "unchanged" : "REPLACED")}");
                Assert.That(synced.Differ, Is.EqualTo(0), "once synced, the GPU heightmap holds the new heights");
            }
            finally
            {
                resort.CacheLease?.Dispose();
                Object.Destroy(resort.Root);
            }
        }
    }
}
