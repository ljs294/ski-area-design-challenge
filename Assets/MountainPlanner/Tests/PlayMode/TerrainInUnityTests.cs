using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.Persistence;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 06 acceptance (ring tiles now at their own 513², style tile), plus task 07's splat check (docs/plans/phase0-0.7-phase1-plan.md): neighbouring edge heights equal;
    /// TerrainData matches the source within one step; the demo opens in ≤10 s; opening runs with the
    /// network disabled. Uses the committed Jackson Hole test terrain (Git LFS).
    /// </summary>
    public sealed class TerrainInUnityTests
    {
        string _package;

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [OneTimeSetUp]
        public void CopyTestTerrain()
        {
            string source = Path.Combine(ProjectRoot, "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source)) Assert.Ignore("TestData/jackson-hole-2km is not available.");
            if (new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000) Assert.Ignore("Git LFS hasn't fetched the test terrain.");
            _package = Path.Combine(Path.GetTempPath(), "mp-unity-terrain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(_package, Path.GetFileName(f)));
        }

        [OneTimeTearDown]
        public void Clean()
        {
            Http.NetworkDisabled = false;
            if (_package != null && Directory.Exists(_package)) Directory.Delete(_package, true);
        }

        static IEnumerator Await<T>(Task<T> task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception!.InnerException!;
        }

        static IEnumerator AwaitTask(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception!.InnerException!;
        }

        [UnityTest]
        public IEnumerator TheTestTerrainOpensOfflineAsCracklessTerrainMatchingItsSource()
        {
            Http.NetworkDisabled = true; // opening must never touch the network
            var open = ResortOpener.OpenAsync(_package, null, TerrainDetail.High, null);
            yield return Await(open);
            var resort = open.Result;
            yield return AwaitTask(resort.CoverReady);
            try
            {
                Assert.That(resort.Tiles.Count, Is.EqualTo(64));
                foreach (var tile in resort.Cache.Tiles)
                    Assert.That(resort.Tiles[(tile.Column, tile.Row)].terrainData.heightmapResolution, Is.EqualTo(tile.Resolution),
                        "core tiles at 1,025² (1 m), ring tiles at their own 513² (2 m)");

                // Neighbouring edge heights are equal (east-west and north-south). Where a 1 m tile meets a 2 m
                // tile, every 2 m vertex of the edge is shared exactly (in-between 1 m samples are midpoints).
                int edges = 0;
                void CompareEdge(Terrain a, Terrain b, bool eastWest, string label)
                {
                    int na = a.terrainData.heightmapResolution, nb = b.terrainData.heightmapResolution, n = Math.Min(na, nb);
                    float[,] ea = eastWest ? a.terrainData.GetHeights(na - 1, 0, 1, na) : a.terrainData.GetHeights(0, 0, na, 1);
                    float[,] eb = eastWest ? b.terrainData.GetHeights(0, 0, 1, nb) : b.terrainData.GetHeights(0, nb - 1, nb, 1);
                    int sa = (na - 1) / (n - 1), sb = (nb - 1) / (n - 1);
                    for (int k = 0; k < n; k++)
                    {
                        float va = eastWest ? ea[k * sa, 0] : ea[0, k * sa], vb = eastWest ? eb[k * sb, 0] : eb[0, k * sb];
                        Assert.That(vb, Is.EqualTo(va), $"{label} at {k}");
                    }
                    edges++;
                }
                foreach (var ((c, r), t) in resort.Tiles)
                {
                    if (resort.Tiles.TryGetValue((c + 1, r), out var east)) CompareEdge(t, east, true, $"t{c}_{r} | t{c + 1}_{r}");
                    if (resort.Tiles.TryGetValue((c, r + 1), out var south)) CompareEdge(t, south, false, $"t{c}_{r} over t{c}_{r + 1}");   // Unity row n-1 is a tile's north edge
                }
                Assert.That(edges, Is.EqualTo(112), "every neighbour pair of the 8 × 8 tiles, including core-ring edges");

                // TerrainData matches the source within one step, and holds exactly what the cache stored
                // (this also proves Unity's heightmap maps 1.0 to 32,766: TerrainCache.MaxValue).
                double step = resort.Cache.HeightRange / TerrainCache.MaxValue;
                double worst = 0, worstStored = 0;
                foreach (var tile in resort.Cache.Tiles)
                {
                    var terrain = resort.Tiles[(tile.Column, tile.Row)];
                    ushort[] stored = TerrainCache.ReadTile(_package, tile);
                    int res = tile.Resolution;
                    float[,] unity = terrain.terrainData.GetHeights(0, 0, res, res);
                    for (int j = 0; j < res; j += 37)
                        for (int i = 0; i < res; i += 41)
                        {
                            int ux = i, uy = res - 1 - j;
                            double metres = terrain.transform.position.y + terrain.terrainData.GetHeight(ux, uy);
                            double source = TerrainCache.Dequantize(stored[j * res + i], resort.Cache.HeightMin, resort.Cache.HeightRange);
                            worst = Math.Max(worst, Math.Abs(metres - source));
                            worstStored = Math.Max(worstStored, Math.Abs(unity[uy, ux] - stored[j * res + i] / (double)TerrainCache.MaxValue));
                        }
                }
                Assert.That(worst, Is.LessThanOrEqualTo(step), $"one step is {step * 100:F2} cm");
                Assert.That(worstStored, Is.LessThan(1e-6), "Unity keeps the cache's 15-bit values exactly");

                // Task 07: the ground cover reached Unity's splat maps (six layers, texel for texel).
                foreach (var tile in resort.Cache.Tiles.Where(t => t.Core).Take(2).Concat(resort.Cache.Tiles.Where(t => !t.Core).Take(1)))
                {
                    var data = resort.Tiles[(tile.Column, tile.Row)].terrainData;
                    Assert.That(data.alphamapLayers, Is.EqualTo(6));
                    Assert.That(data.alphamapResolution, Is.EqualTo(tile.CoverResolution));
                    data.SyncTexture(TerrainData.AlphamapTextureName);
                    int n = tile.CoverResolution;
                    float[,,] maps = data.GetAlphamaps(0, 0, n, n);
                    var expected = SplatTexels.Load(_package, tile, snow: true);
                    double worstSplat = 0;
                    for (int y = 0; y < n; y += 29)
                        for (int x = 0; x < n; x += 31)
                            for (int k = 0; k < 6; k++)
                            {
                                byte e = expected.Textures[k / 4][(y * n + x) * 4 + k % 4];
                                worstSplat = Math.Max(worstSplat, Math.Abs(maps[y, x, k] - e / 255.0));
                            }
                    Assert.That(worstSplat, Is.LessThan(1.5 / 255), $"{tile.CoverFile} splat matches the cache");
                }
            }
            finally
            {
                Http.NetworkDisabled = false;
                Object.Destroy(resort.Root);
            }
        }

        [UnityTest]
        public IEnumerator TheForestGrowsOnTheTestTerrainAndDraws()
        {
#if UNITY_EDITOR
            var forest = new ForestAssets
            {
                Trees = UnityEditor.AssetDatabase.LoadAssetAtPath<TreePrototypeSet>("Assets/MountainPlanner/Art/Trees/TreePrototypes.asset"),
                Cull = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MountainPlanner/Art/Shaders/ForestCull.compute"),
                Shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/TreeInstanced.shader"),
                ImpostorShader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/TreeImpostor.shader"),
            };
            if (!forest.IsComplete) Assert.Ignore("The tree library isn't imported, or this GPU has no compute shaders.");
            var camera = new GameObject("Test camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.farClipPlane = 20000;
            camera.transform.SetPositionAndRotation(new Vector3(0, 3500, -2500), Quaternion.Euler(30, 0, 0));
            var open = ResortOpener.OpenAsync(_package, null, TerrainDetail.High, null, default, null, forest);
            yield return Await(open);
            var resort = open.Result;
            yield return AwaitTask(resort.CoverReady);
            try
            {
                var view = resort.Root.GetComponent<MountainPlanner.Presentation.ForestView>();
                Assert.That(view, Is.Not.Null, "the forest is attached to the resort");
                Assert.That(resort.TreesPlanted, Is.GreaterThan(100000), "the 2 km test site and its ring are forested");
                Assert.That(view.Renderer.DrawCount, Is.GreaterThan(100), "every prototype, LOD and visible submesh has a draw");
                for (int i = 0; i < 10; i++) yield return null;   // cull and draw a few frames without errors
                TestContext.WriteLine($"{resort.TreesPlanted:N0} trees, {view.Renderer.DrawCount} indirect draws, planted by {resort.ForestSeconds:F2} s");
            }
            finally
            {
                Object.Destroy(resort.Root);
                Object.Destroy(camera.gameObject);
            }
#else
            Assert.Ignore("Editor only.");
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator TheDemoMountainOpensWithinTenSeconds()
        {
            var demo = ResortLibrary.Scan(MountainViewer.DataRoot).Where(e => e.Name == "Jackson Hole")
                .OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
            if (demo == null || demo.SizeKm < 5) Assert.Ignore("Download the 5 km Jackson Hole demo first (demo.bat option 12).");
            if (!demo.CacheReady) Assert.Ignore("The demo's terrain isn't prepared yet; the 10 s budget is for a prepared mountain (0.3 §8).");
            Http.NetworkDisabled = true;
            var open = ResortOpener.OpenAsync(demo.Folder, null, TerrainDetail.High, null);
            yield return Await(open);
            yield return AwaitTask(open.Result.CoverReady);
            try
            {
                Assert.That(open.Result.Tiles.Count, Is.EqualTo(121));
                Assert.That(open.Result.Seconds, Is.LessThanOrEqualTo(10.0), $"opened in {open.Result.Seconds:F1} s");
                TestContext.WriteLine($"Jackson Hole 5 km opened in {open.Result.Seconds:F2} s; ground cover painted by {open.Result.CoverSeconds:F2} s");
            }
            finally
            {
                Http.NetworkDisabled = false;
                Object.Destroy(open.Result.Root);
            }
        }
    }
}
