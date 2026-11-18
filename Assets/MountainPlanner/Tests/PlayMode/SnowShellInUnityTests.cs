using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.App;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A3, round 3 repair (owner, 2026-11-17: "I see no difference in snow height from the terrain mesh"): a
    /// smoke test that the snow shell is DRAWN at the snow's depth, through the real opening path (<see cref="ResortOpener"/>
    /// on the committed Jackson Hole terrain, the real ground material and its shell copy). It measures the top drawn
    /// surface with <see cref="ShellProbe"/>'s slices, as <c>-shellcheck</c> does in the built game.
    /// <para>The editor compiles shader variants on demand, so this can't see a variant the build drops or fails to compile
    /// (<c>TheSnowShellVariantsCompile</c> and the build's shader-error gate cover that); it catches the material, the
    /// shell tiles, the lift's globals and the culling.</para>
    /// <para>Also (owner, 2026-11-17: "make sure that contours for the real ground beneath show through the snow"): with the
    /// ground buried, the Contours layer still changes the picture, so the shell draws the earth's lines.</para>
    /// </summary>
    public sealed class SnowShellInUnityTests
    {
        const float EvenMetres = 2f;
        string _package;

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [OneTimeSetUp]
        public void CopyTestTerrain()
        {
            string source = Path.Combine(ProjectRoot, "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source)) Assert.Ignore("TestData/jackson-hole-2km is not available.");
            if (new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000) Assert.Ignore("Git LFS hasn't fetched the test terrain.");
            _package = Path.Combine(Path.GetTempPath(), "mp-unity-shell-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>Even snow over the whole field, sent as the worker's tiles are (so the lift's first pass completes).</summary>
        static void LayEvenSnow(SurfaceStates states, float metres)
        {
            const int Tile = 64;
            int w = states.Snow.Width, d = states.Snow.Height;
            var mm = new ushort[Tile * Tile];
            for (int k = 0; k < mm.Length; k++) mm[k] = (ushort)Mathf.RoundToInt(metres * 1000);
            var surface = new byte[Tile * Tile * 4];
            for (int y = 0; y < d; y += Tile)
                for (int x = 0; x < w; x += Tile)
                    states.TakeTile(x, y, Mathf.Min(Tile, w - x), Mathf.Min(Tile, d - y), mm, 0, surface, 0);
        }

        [UnityTest]
        public IEnumerator TheSnowShellStandsAtTheSnowsDepthAndShowsTheEarthsContours()
        {
#if UNITY_EDITOR
            T Load<T>(string path) where T : Object => UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
            var terrainMaterial = Load<Material>("Assets/MountainPlanner/Art/Terrain/MountainTerrain.mat");
            var cliffSource = Load<Material>("Assets/MountainPlanner/Art/Terrain/Cliff.mat");
            if (terrainMaterial == null || cliffSource == null) Assert.Ignore("The terrain materials aren't imported.");
            var cliff = new Material(cliffSource);
            var sun = new GameObject("Test sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(40, 150, 0);

            var open = ResortOpener.OpenAsync(_package, null, TerrainDetail.High, null, default, terrainMaterial);
            yield return AwaitTask(open);
            var resort = open.Result;
            yield return AwaitTask(resort.CoverReady);
            ShellProbe probe = null;
            var layers = new MapLayers();
            try
            {
                Assert.That(resort.Ground?.ShellMaterial, Is.Not.Null, "the ground made its snow shell's material");
                Assert.That(resort.Ground.ShellTiles?.Count() ?? 0, Is.GreaterThan(0), "every tile has a snow shell");
                layers.Bind(resort.Ground, resort.EdgeMaterial, cliff, (float)resort.Manifest.Crs.GridConvergenceDegrees);
                layers.BindSnow(resort.States);
                LayEvenSnow(resort.States, EvenMetres);
                for (int f = 0; f < 600 && !(resort.States.FirstPassDone && resort.States.TilesWaiting == 0 && resort.States.LiftOn); f++)
                {
                    resort.States.Sync();
                    yield return null;
                }
                Assert.That(resort.States.LiftOn, Is.True, "the lift switched on once the snow's first pass was in");

                var core = resort.Core;
                var spots = new List<Vector2> { core.center, core.center + new Vector2(core.width * 0.25f, core.height * 0.2f), core.center - new Vector2(core.width * 0.2f, core.height * 0.25f) };
                probe = new ShellProbe(resort.Tiles.Values);
                var found = new int[2];
                float deepest = 0;
                foreach (var spot in spots)
                {
                    float earth = resort.Surface.HeightAt(spot.x, spot.y);
                    Assert.That(float.IsNaN(earth), Is.False, $"({spot.x:F0}, {spot.y:F0}) is on the terrain");
                    float says = SnowCursor.LiftAt(resort.States, resort.Ring, spot.x, spot.y);
                    deepest = Mathf.Max(deepest, says);
                    yield return probe.Slices(spot, earth, found);
                    Debug.Log($"[snow shell] at ({spot.x:F0}, {spot.y:F0}): game says {says:F2} m, top drawn slice {(found[0] == ShellProbe.Nothing ? "none" : (found[0] * ShellProbe.SliceMetres).ToString("F2") + " m")}");
                    Assert.That(ShellProbe.Agrees(found[0], says), $"({spot.x:F0}, {spot.y:F0}): the top drawn surface is where the game says the snow is ({says:F2} m)");
                }
                Assert.That(deepest, Is.GreaterThan(EvenMetres - 0.3f), "the spots stand under the even snow (the test isn't measuring bare ground)");

                // The Snow layer off: the shells hide and the top drawn surface is the earth.
                layers.Set(MapLayers.Snow, false);
                resort.States.Sync();
                for (int f = 0; f < 3; f++) yield return null;
                yield return probe.Slices(spots[0], resort.Surface.HeightAt(spots[0].x, spots[0].y), found);
                Assert.That(found[0] != ShellProbe.Nothing && Mathf.Abs(found[0] * ShellProbe.SliceMetres) <= 0.26f, $"Snow off: the earth is the top (read {found[0] * ShellProbe.SliceMetres:F2} m)");
                layers.Set(MapLayers.Snow, true);
                resort.States.Sync();
                for (int f = 0; f < 3; f++) yield return null;
                probe.Dispose();
                probe = null;

                // The earth's contours on the snow: a top-down picture over 2 m of snow changes when Contours turn on.
                var rt = new RenderTexture(256, 256, 24) { name = "Contours on the snow" };
                var read = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                var go = new GameObject("Contour camera");
                var cam = go.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 150;
                cam.targetTexture = rt;
                cam.farClipPlane = 5000;
                cam.enabled = false;   // drawn on demand (a batch-mode editor never reaches the end of a frame)
                float centreEarth = resort.Surface.HeightAt(core.center.x, core.center.y);
                go.transform.SetPositionAndRotation(new Vector3(core.center.x, centreEarth + 1500, core.center.y), Quaternion.Euler(90, 0, 0));
                Color32[] Picture()
                {
                    cam.Render();
                    var previous = RenderTexture.active;
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                    RenderTexture.active = previous;
                    return read.GetPixels32();
                }
                try
                {
                    layers.Set(MapLayers.Contours, false);
                    yield return null;
                    var without = Picture();
                    layers.Set(MapLayers.Contours, true);
                    yield return null;
                    var with = Picture();
                    int changed = 0;
                    for (int k = 0; k < with.Length; k++)
                        if (Mathf.Abs(with[k].r - without[k].r) + Mathf.Abs(with[k].g - without[k].g) + Mathf.Abs(with[k].b - without[k].b) > 30) changed++;
                    Debug.Log($"[snow shell] contours on {EvenMetres} m of snow change {100.0 * changed / with.Length:F1}% of a 300 m top-down picture");
                    Assert.That(changed, Is.GreaterThan(with.Length / 50), "the earth's contour lines show on the snow shell");
                }
                finally
                {
                    layers.Set(MapLayers.Contours, false);
                    cam.targetTexture = null;
                    Object.Destroy(go);
                    Object.Destroy(rt);
                    Object.Destroy(read);
                }
            }
            finally
            {
                probe?.Dispose();
                resort.CacheLease?.Dispose();   // the opener's cache lease (task P2-04): held, the folder can't be deleted
                Object.Destroy(resort.Root);
                Object.Destroy(sun.gameObject);
                Object.Destroy(cliff);
            }
#else
            Assert.Ignore("Loads the terrain material through the AssetDatabase (editor only).");
            yield break;
#endif
        }
    }
}
