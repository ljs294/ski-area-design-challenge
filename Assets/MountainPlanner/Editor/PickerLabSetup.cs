using System.IO;
using MountainPlanner.App.Picker;
using MountainPlanner.UI.Picker;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Creates the Picker Lab scene (task 13) and builds it as its own small Windows player for the demo
    /// (demo.bat). The game's scene list and player settings are never touched: the build passes its one
    /// scene directly.
    ///   Unity -batchmode -executeMethod MountainPlanner.Editor.PickerLabSetup.BuildPlayer -quit
    /// </summary>
    public static class PickerLabSetup
    {
        public const string ScenePath = "Assets/MountainPlanner/Scenes/PickerLab.unity";
        public const string PlayerPath = "Builds/PickerLab/PickerLab.exe";
        const string UiFolder = "Assets/MountainPlanner/Art/UI/";

        /// <summary>The picker's panel settings: scaled with the screen height from a 1080p reference, like the HUD.</summary>
        public static PanelSettings PanelSettingsAsset()
        {
            const string path = UiFolder + "PickerPanel.asset";
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, path);
            }
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(UiFolder + "Theme-Dark.tss");
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 1;   // by height
            panel.sortingOrder = 10;
            EditorUtility.SetDirty(panel);
            return panel;
        }

        [MenuItem("Mountain Planner/Create Picker Lab Scene")]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.07f, 0.08f);

            var go = new GameObject("Picker Lab");
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = PanelSettingsAsset();
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiFolder + "SitePicker.uxml")
                ?? throw new System.InvalidOperationException("SitePicker.uxml is missing.");
            var picker = go.AddComponent<SitePicker>();
            picker.Document = document;
            go.AddComponent<PickerLab>().Picker = picker;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[PickerLabSetup] Saved {ScenePath}");
        }

        [MenuItem("Mountain Planner/Build Picker Lab Player")]
        public static void BuildPlayer()
        {
            CreateScene();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = PlayerPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[PickerLabSetup] Build {report.summary.result}: {report.summary.totalSize / 1e6:F0} MB in {report.summary.totalTime.TotalSeconds:F0} s → {PlayerPath}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }
    }
}
