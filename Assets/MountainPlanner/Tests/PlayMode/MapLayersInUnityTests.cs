using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using MountainPlanner.App;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 12 acceptance (docs/plans/phase0-0.7-phase1-plan.md §3, row 12): a layer switch takes effect within
    /// one frame, with no rebuild. On the committed Jackson Hole test terrain, each switch made in a frame shows
    /// in that frame's picture, the frame holds exactly one <see cref="MapLayers.ApplyMarkerName"/> sample, and
    /// no splat is uploaded (<see cref="TerrainTiles.ApplySplatMarkerName"/> stays at zero).
    /// </summary>
    public sealed class MapLayersInUnityTests
    {
        string _package;

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [OneTimeSetUp]
        public void CopyTestTerrain()
        {
            string source = Path.Combine(ProjectRoot, "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source)) Assert.Ignore("TestData/jackson-hole-2km is not available.");
            if (new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000) Assert.Ignore("Git LFS hasn't fetched the test terrain.");
            _package = Path.Combine(Path.GetTempPath(), "mp-unity-layers-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>Mean brightness of the camera's last picture.</summary>
        static float Brightness(RenderTexture rt, Texture2D readback)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            RenderTexture.active = previous;
            var pixels = readback.GetPixels32();
            double sum = 0;
            foreach (var p in pixels) sum += 0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b;
            return (float)(sum / pixels.Length / 255);
        }

        [UnityTest]
        public IEnumerator EachLayerShowsInTheFrameItIsSwitchedWithNoSplatUpload()
        {
#if UNITY_EDITOR
            T Load<T>(string path) where T : Object => UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
            var terrainMaterial = Load<Material>("Assets/MountainPlanner/Art/Terrain/MountainTerrain.mat");
            var forest = new ForestAssets
            {
                Trees = Load<TreePrototypeSet>("Assets/MountainPlanner/Art/Trees/TreePrototypes.asset"),
                Cull = Load<ComputeShader>("Assets/MountainPlanner/Art/Shaders/ForestCull.compute"),
                Shader = Load<Shader>("Assets/MountainPlanner/Art/Shaders/TreeInstanced.shader"),
                ImpostorShader = Load<Shader>("Assets/MountainPlanner/Art/Shaders/TreeImpostor.shader"),
                Cliff = new Material(Load<Material>("Assets/MountainPlanner/Art/Terrain/Cliff.mat")),
                Edge = Load<Material>("Assets/MountainPlanner/Art/Terrain/DioramaWall.mat"),
            };
            if (terrainMaterial == null || !forest.IsComplete) Assert.Ignore("The terrain material or tree library isn't imported, or this GPU has no compute shaders.");

            var rt = new RenderTexture(320, 180, 24) { name = "Layer test" };
            var readback = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var camera = new GameObject("Test camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.farClipPlane = 20000;
            camera.targetTexture = rt;
            camera.transform.SetPositionAndRotation(new Vector3(0, 4200, -1800), Quaternion.Euler(55, 0, 0));
            var sun = new GameObject("Test sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(40, 150, 0);

            var open = ResortOpener.OpenAsync(_package, null, TerrainDetail.High, null, default, terrainMaterial, forest);
            yield return AwaitTask(open);
            var resort = open.Result;
            yield return AwaitTask(resort.CoverReady);
            var apply = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, MapLayers.ApplyMarkerName);
            var uploads = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, TerrainTiles.ApplySplatMarkerName);
            try
            {
                var view = resort.Root.GetComponent<ForestView>();
                Assert.That(view, Is.Not.Null, "the forest is attached to the resort");
                var layers = new MapLayers();
                layers.Bind(resort.Ground, resort.EdgeMaterial, forest.Cliff);
                layers.BindForest(view);
                for (int i = 0; i < 30; i++) yield return null;   // LOD, culling and shadows settle

                // A frame with no switch has no marker sample.
                yield return null;
                Assert.That(apply.Valid && uploads.Valid, "the recorders are running");
                Assert.That(apply.GetSample(0).Count, Is.EqualTo(0), "no switch, no apply");
                float before = Brightness(rt, readback);

                IEnumerator Switch(string layer, bool on, string what)
                {
                    Assert.That(layers.Set(layer, on), Is.True, what);
                    // The coroutine resumes in the next frame's update, before it renders: the picture read now
                    // is the frame the switch was made in, and so are the recorders' last samples.
                    yield return null;
                    float after = Brightness(rt, readback);
                    TestContext.WriteLine($"{what}: brightness {before:F3} → {after:F3}, apply {apply.GetSample(0).Count}× in {apply.LastValue / 1000.0:F1} µs, splat uploads {uploads.GetSample(0).Count}");
                    Assert.That(apply.GetSample(0).Count, Is.EqualTo(1), what + ": one apply in the frame of the switch");
                    Assert.That(uploads.GetSample(0).Count, Is.EqualTo(0), what + ": no splat upload (no rebuild)");
                    Assert.That(Mathf.Abs(after - before), Is.GreaterThan(0.004f), what + ": the picture changed in that same frame");
                    before = after;
                }

                yield return Switch(MapLayers.Snow, false, "snow off");
                Assert.That(resort.Ground.Material.GetFloat("_SnowOn"), Is.EqualTo(0));
                yield return Switch(MapLayers.Snow, true, "snow on");
                yield return Switch(MapLayers.Forest, false, "forest off");
                Assert.That(view.enabled, Is.False);
                yield return Switch(MapLayers.Forest, true, "forest on");
                yield return Switch(MapLayers.Cover, true, "cover map on");
                yield return Switch(MapLayers.Cover, false, "cover map off");
                Assert.That(layers.SnowOn && layers.ForestOn && !layers.CoverMapOn, "back where it started");
            }
            finally
            {
                apply.Dispose();
                uploads.Dispose();
                Object.Destroy(resort.Root);
                Object.Destroy(camera.gameObject);
                Object.Destroy(sun.gameObject);
                Object.Destroy(forest.Cliff);
                rt.Release();
                Object.Destroy(rt);
                Object.Destroy(readback);
            }
#else
            Assert.Ignore("Editor only.");
            yield break;
#endif
        }
    }
}
