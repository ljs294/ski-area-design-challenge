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
        public const string TerrainMaterialPath = "Assets/MountainPlanner/Art/Terrain/MountainTerrain.mat";

        /// <summary>
        /// The mountain terrain material (MountainTerrain.shader) with the generated ground textures; the
        /// scene references it, so the shader and textures are always in builds.
        /// </summary>
        static Material TerrainMaterial()
        {
            if (AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.AlbedoPath) == null) GroundTextures.Generate();
            var material = AssetDatabase.LoadAssetAtPath<Material>(TerrainMaterialPath);
            if (material == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TerrainMaterialPath));
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/MountainTerrain.shader")
                             ?? throw new System.InvalidOperationException("MountainTerrain.shader not found.");
                material = new Material(shader) { name = "MountainTerrain" };
                AssetDatabase.CreateAsset(material, TerrainMaterialPath);
            }
            material.SetTexture("_Albedo", AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.AlbedoPath));
            material.SetTexture("_Normals", AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.NormalPath));
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>The cliff-shell material (Cliff.shader) with the generated ground textures.</summary>
        static Material CliffMaterial()
        {
            const string path = "Assets/MountainPlanner/Art/Terrain/Cliff.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/Cliff.shader")) { name = "Cliff" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_Albedo", AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.AlbedoPath));
            material.SetTexture("_Normals", AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.NormalPath));
            EditorUtility.SetDirty(material);
            return material;
        }

        const string HighlightMaterialPath = "Assets/MountainPlanner/Art/Highlight/Highlight.mat";

        /// <summary>A bright unlit red for landmark lines.</summary>
        static Material HighlightMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(HighlightMaterialPath);
            if (material != null) return material;
            Directory.CreateDirectory(Path.GetDirectoryName(HighlightMaterialPath));
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) throw new System.InvalidOperationException("URP Unlit shader not found.");
            material = new Material(shader) { name = "Highlight" };
            material.SetColor("_BaseColor", new Color(1f, 0.12f, 0.1f));
            AssetDatabase.CreateAsset(material, HighlightMaterialPath);
            return material;
        }

        [MenuItem("Mountain Planner/Create Viewer Scene")]
        public static void CreateViewerScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            // Update the scene in place when it exists: recreating it gave every object a new id on each build
            // (noisy diffs with no real change).
            var scene = File.Exists(ScenePath)
                ? EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single)
                : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = Find("Sun");
            var light = GetOrAdd<Light>(sun);
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(32f, 160f, 0f); // a winter morning sun from the south-east (task 11 computes the real one)

            var camGo = Find("Main Camera");
            camGo.tag = "MainCamera";
            var cam = GetOrAdd<Camera>(camGo);
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 30000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var fly = GetOrAdd<DebugFlyCamera>(camGo);

            var viewerGo = Find("Mountain Viewer");
            var viewer = GetOrAdd<MountainViewer>(viewerGo);
            viewer.Camera = fly;
            viewer.TerrainMaterial = TerrainMaterial();
            viewer.HighlightMaterial = HighlightMaterial();
            viewer.Trees = AssetDatabase.LoadAssetAtPath<MountainPlanner.World.TreePrototypeSet>(TreeImport.SetPath);
            viewer.CliffMaterial = CliffMaterial();
            viewer.ForestCull = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MountainPlanner/Art/Shaders/ForestCull.compute");
            viewer.TreeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/TreeInstanced.shader");
            if (viewer.Trees == null) Debug.LogWarning("[ViewerSetup] No tree library yet: run Mountain Planner > Import Trees.");

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

        static GameObject Find(string name) => GameObject.Find(name) ?? new GameObject(name);

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var existing = go.GetComponent<T>();
            return existing != null ? existing : go.AddComponent<T>();   // not ??: Unity's missing components are "fake null"
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
