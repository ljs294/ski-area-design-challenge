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
            // Keeps the shader's instancing variants in builds (Graphics settings strip unused ones), so
            // TerrainTiles.DrawInstanced can draw instanced terrain (task 15).
            material.enableInstancing = true;
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

        /// <summary>The diorama base material (DioramaWall.shader) with the generated ground textures.</summary>
        static Material EdgeMaterial()
        {
            const string path = "Assets/MountainPlanner/Art/Terrain/DioramaWall.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/DioramaWall.shader")) { name = "DioramaWall" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_Albedo", AssetDatabase.LoadAssetAtPath<Texture2DArray>(GroundTextures.AlbedoPath));
            EditorUtility.SetDirty(material);
            return material;
        }

        const string UiFolder = "Assets/MountainPlanner/Art/UI/";

        /// <summary>The HUD's panel settings: scaled with the screen height from a 1080p reference, light theme first.</summary>
        static UnityEngine.UIElements.PanelSettings HudPanelSettings()
        {
            const string path = UiFolder + "HudPanel.asset";
            var panel = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.PanelSettings>(path);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
                AssetDatabase.CreateAsset(panel, path);
            }
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(UiFolder + "Resources/MountainPlannerUI/Theme-Dark.tss");   // UiPanels swaps in the player's theme at run time
            panel.scaleMode = UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = MountainPlanner.UI.UiPanels.Reference(100);   // the accepted mockup's 1280×720 design base (task P2-01)
            panel.screenMatchMode = UnityEngine.UIElements.PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 1;   // by height
            EditorUtility.SetDirty(panel);
            return panel;
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
            // Direction from the clock (the NOAA sun, or the moon at night); colour and strength from the lighting look (SceneLighting).
            var lighting = GetOrAdd<SceneLighting>(sun);
            lighting.Sun = light;

            var camGo = Find("Main Camera");
            camGo.tag = "MainCamera";
            var cam = GetOrAdd<Camera>(camGo);
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 30000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            var camData = GetOrAdd<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>(camGo);
            camData.renderPostProcessing = true;   // the lighting preset's grading
            // Nothing samples the depth or opaque textures (the project template's defaults ask for both). With
            // post-processing and MSAA on, making them drew the whole scene a second time: 2x the GPU time.
            camData.requiresDepthOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
            camData.requiresColorOption = UnityEngine.Rendering.Universal.CameraOverrideOption.Off;
            var fly = GetOrAdd<ViewCamera>(camGo);
            fly.MinDistance = 2f;   // close-ups (the style tile flagged 20 m as too far); the near plane closes in near the ground

            var viewerGo = Find("Mountain Viewer");
            var viewer = GetOrAdd<MountainViewer>(viewerGo);
            viewer.Camera = fly;
            viewer.Lighting = lighting;

            // The S6 HUD (UI Toolkit).
            var hudGo = Find("HUD");
            var document = GetOrAdd<UnityEngine.UIElements.UIDocument>(hudGo);
            document.panelSettings = HudPanelSettings();
            document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.VisualTreeAsset>(UiFolder + "Hud.uxml");
            var hud = GetOrAdd<MountainPlanner.UI.MountainHud>(hudGo);
            hud.Document = document;
            viewer.Hud = hud;
            viewer.TerrainMaterial = TerrainMaterial();
            viewer.HighlightMaterial = HighlightMaterial();
            viewer.Trees = AssetDatabase.LoadAssetAtPath<MountainPlanner.World.TreePrototypeSet>(TreeImport.SetPath);
            viewer.CliffMaterial = CliffMaterial();
            viewer.EdgeMaterial = EdgeMaterial();
            viewer.FarShadowCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MountainPlanner/Art/Shaders/FarShadow.compute");
            viewer.ForestCull = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MountainPlanner/Art/Shaders/ForestCull.compute");
            viewer.TreeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/TreeInstanced.shader");
            viewer.TreeImpostorShader = AssetDatabase.LoadAssetAtPath<Shader>(TreeImport.ImpostorShaderPath);
            if (viewer.Trees == null) Debug.LogWarning("[ViewerSetup] No tree library yet: run Mountain Planner > Import Trees.");

            // The gradient sky (Sky.shader); the lighting preset sets its colours.
            const string skyPath = "Assets/MountainPlanner/Art/Sky/Sky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (sky == null)
            {
                Directory.CreateDirectory("Assets/MountainPlanner/Art/Sky");
                sky = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/MountainPlanner/Art/Shaders/Sky.shader")) { name = "Sky" };
                AssetDatabase.CreateAsset(sky, skyPath);
            }
            lighting.Sky = sky;
            RenderSettings.skybox = sky;
            RenderSettings.sun = light;
            RenderSettings.ambientMode = AmbientMode.Trilight;   // sky light from the preset
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

        /// <summary>Where the Development player goes (task 15): exact garbage per frame and graphics memory for -benchmark.</summary>
        public const string DevPlayerPath = "Builds/WindowsDev/SkiAreaDesignChallenge.exe";

        [MenuItem("Mountain Planner/Build Windows Player")]
        public static void BuildWindows() => Build(PlayerPath, BuildOptions.None);

        /// <summary>
        /// A Development player beside the release one: the profiler counters a release player doesn't record
        /// ("GC Allocated In Frame", "Gfx Used Memory") work here, so the benchmark's garbage and memory checks are exact.
        /// Frame times of record still come from the release player.
        /// </summary>
        [MenuItem("Mountain Planner/Build Windows Development Player")]
        public static void BuildWindowsDev() => Build(DevPlayerPath, BuildOptions.Development);

        static void Build(string path, BuildOptions options)
        {
            CreateViewerScene();
            PlayerSettings.companyName = "Ski Area Design Challenge";
            PlayerSettings.productName = "Ski Area Design Challenge";
            PlayerSettings.enableFrameTimingStats = true;   // GPU frame times for -benchmark
            // Keep running when the window loses focus: loading a mountain shouldn't stall on alt-tab, and
            // unattended captures and benchmarks froze whenever another window took focus.
            PlayerSettings.runInBackground = true;
            // Borderless full screen at the display's own resolution by default (21:9 and 32:9 included, E6); a
            // window can be resized and maximised; Alt+Enter switches between the two (task P2-01).
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.defaultIsNativeResolution = true;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.allowFullscreenSwitch = true;
            WriteBuildInfo();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = options,
            });
            Debug.Log($"[ViewerSetup] Build {report.summary.result}: {report.summary.totalSize / 1e6:F0} MB in {report.summary.totalTime.TotalSeconds:F0} s → {path}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? 0 : 1);
        }

        /// <summary>Where the build stamp goes: a git-ignored Resources folder, so builds never dirty the working tree.</summary>
        const string BuildInfoPath = "Assets/MountainPlanner/Generated/Resources/" + MountainPlanner.App.BuildInfo.ResourceName + ".json";

        /// <summary>
        /// The commit this player is built from (git SHA, uncommitted changes, build time), so benchmark results
        /// record what they measured (0.3 §8). Without git it records "unknown".
        /// </summary>
        static void WriteBuildInfo()
        {
            string Git(string arguments)
            {
                try
                {
                    using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments)
                    {
                        RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                    });
                    string output = git.StandardOutput.ReadToEnd();
                    git.WaitForExit();
                    return git.ExitCode == 0 ? output.Trim() : null;
                }
                catch (System.Exception) { return null; }
            }
            string commit = Git("rev-parse HEAD");
            // Uncommitted changes outside the generated stamp itself.
            string status = Git("status --porcelain --untracked-files=no");
            var info = new MountainPlanner.App.BuildInfo
            {
                commit = string.IsNullOrEmpty(commit) ? "unknown" : commit,
                dirty = !string.IsNullOrEmpty(status),
                builtUtc = System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(BuildInfoPath));
            File.WriteAllText(BuildInfoPath, JsonUtility.ToJson(info, true));
            AssetDatabase.ImportAsset(BuildInfoPath);
            Debug.Log($"[ViewerSetup] Build info: {info.commit}{(info.dirty ? " (uncommitted changes)" : "")}");
        }
    }
}
