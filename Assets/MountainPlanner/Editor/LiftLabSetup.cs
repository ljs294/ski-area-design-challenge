using System.IO;
using MountainPlanner.App;
using MountainPlanner.Presentation;
using MountainPlanner.World.Lifts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Creates the Lift Lab scene (decision LP1) and builds it as its own small Windows player, for reviewing
    /// the lift assets hands-on (demo.bat 23). The game's scene list and player settings are never touched:
    /// the build passes its one scene directly.
    ///   Unity -batchmode -executeMethod MountainPlanner.Editor.LiftLabSetup.BuildPlayer -quit
    /// </summary>
    public static class LiftLabSetup
    {
        public const string ScenePath = "Assets/MountainPlanner/Scenes/LiftLab.unity";
        public const string PlayerPath = "Builds/LiftLab/LiftLab.exe";
        public const string DevPlayerPath = "Builds/LiftLabDev/LiftLab.exe";
        const string Materials = LiftImport.Root + "/Materials";

        static Material Asset(string name, string shaderName, Color color, float smoothness)
        {
            string path = $"{Materials}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find(shaderName) ?? throw new System.InvalidOperationException(shaderName + " not found.");
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(m);
            return m;
        }

        [MenuItem("Mountain Planner/Create Lift Lab Scene")]
        public static void CreateScene()
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(LiftImport.SetPath)
                ?? throw new System.InvalidOperationException("No lift models yet: run Mountain Planner > Import Lifts.");
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun");
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(35f, 150f, 0f);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 8000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var fly = camGo.AddComponent<DebugFlyCamera>();
            fly.MinDistance = 1.5f;
            fly.Distance = 30f;

            var labGo = new GameObject("Lift Lab");
            var lab = labGo.AddComponent<LiftLab>();
            var overlay = labGo.AddComponent<LiftLabOverlay>();
            overlay.Lab = lab;
            lab.Overlay = overlay;
            lab.Camera = fly;
            lab.Models = models;
            lab.Structure = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftStructure.mat");
            lab.Glass = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftGlass.mat");
            lab.Chair = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftChair.mat");
            lab.RopeMaterial = Asset("LiftRope", "Universal Render Pipeline/Unlit", new Color(0.12f, 0.12f, 0.13f), 0f);
            lab.GroundMaterial = Asset("LiftLabSnow", "Universal Render Pipeline/Lit", new Color(0.93f, 0.95f, 0.98f), 0.25f);

            var sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/MountainPlanner/Art/Sky/ProceduralSky.mat");
            if (sky != null) RenderSettings.skybox = sky;
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.fog = false;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[LiftLabSetup] Saved {ScenePath}");
        }

        [MenuItem("Mountain Planner/Build Lift Lab Player")]
        public static void BuildPlayer() => Build(PlayerPath, BuildOptions.None);

        /// <summary>A Development build: the benchmark's render counters and GPU timings need it.</summary>
        public static void BuildPlayerDev() => Build(DevPlayerPath, BuildOptions.Development);

        static void Build(string path, BuildOptions options)
        {
            CreateScene();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = options,
            });
            Debug.Log($"[LiftLabSetup] Build {report.summary.result}: {report.summary.totalSize / 1e6:F0} MB in {report.summary.totalTime.TotalSeconds:F0} s → {path}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
