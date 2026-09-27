using System.IO;
using MountainPlanner.App;
using MountainPlanner.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Creates the Mountain Viewer scene and builds the Windows player (task 06). Both run from the menu or
    /// unattended: Unity -batchmode -executeMethod MountainPlanner.Editor.ViewerSetup.BuildWindows -quit
    /// </summary>
    public static class ViewerSetup
    {
        public const string ScenePath = "Assets/MountainPlanner/Scenes/MountainViewer.unity";
        public const string PlayerPath = "Builds/Windows/SkiAreaDesignChallenge.exe";
        public const string TerrainMaterialPath = "Assets/MountainPlanner/Art/Terrain/TerrainLit.mat";

        /// <summary>A URP Terrain/Lit material asset; the scene references it so the shader is always in builds.</summary>
        static Material TerrainMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterialPath);
            if (existing != null) return ConfigureTerrainMaterial(existing);
            Directory.CreateDirectory(Path.GetDirectoryName(TerrainMaterialPath));
            var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            if (shader == null) throw new System.InvalidOperationException("URP Terrain/Lit shader not found.");
            var material = new Material(shader) { name = "TerrainLit", enableInstancing = true };
            AssetDatabase.CreateAsset(material, TerrainMaterialPath);
            return ConfigureTerrainMaterial(material);
        }

        /// <summary>
        /// Instanced terrain is lit from per-pixel normals, a material keyword the Material Inspector normally
        /// turns on; without it, instanced terrain renders flat-lit.
        /// </summary>
        static Material ConfigureTerrainMaterial(Material material)
        {
            material.enableInstancing = true;
            material.SetFloat("_EnableInstancedPerPixelNormal", 1f);
            material.EnableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
            EditorUtility.SetDirty(material);
            return material;
        }

        [MenuItem("Mountain Planner/Create Viewer Scene")]
        public static void CreateViewerScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(32f, 160f, 0f); // a winter morning sun from the south-east (task 11 computes the real one)

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 30000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var fly = camGo.AddComponent<DebugFlyCamera>();

            var viewerGo = new GameObject("Mountain Viewer");
            var viewer = viewerGo.AddComponent<MountainViewer>();
            viewer.Camera = fly;
            viewer.TerrainMaterial = TerrainMaterial();

            // A procedural sky until task 11's sky and lighting presets.
            var sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/MountainPlanner/Art/Sky/ProceduralSky.mat");
            if (sky == null)
            {
                Directory.CreateDirectory("Assets/MountainPlanner/Art/Sky");
                sky = new Material(Shader.Find("Skybox/Procedural")) { name = "ProceduralSky" };
                sky.SetFloat("_AtmosphereThickness", 0.8f);
                sky.SetFloat("_Exposure", 1.2f);
                AssetDatabase.CreateAsset(sky, "Assets/MountainPlanner/Art/Sky/ProceduralSky.mat");
            }
            RenderSettings.skybox = sky;
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.fog = false;

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log($"[ViewerSetup] Saved {ScenePath} and set it as the only scene in the build.");
        }

        [MenuItem("Mountain Planner/Build Windows Player")]
        public static void BuildWindows()
        {
            CreateViewerScene();
            PlayerSettings.companyName = "Ski Area Design Challenge";
            PlayerSettings.productName = "Ski Area Design Challenge";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = PlayerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[ViewerSetup] Build {report.summary.result}: {report.summary.totalSize / 1e6:F0} MB in {report.summary.totalTime.TotalSeconds:F0} s → {PlayerPath}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
