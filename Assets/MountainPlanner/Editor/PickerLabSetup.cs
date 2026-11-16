using System;
using System.Globalization;
using System.IO;
using System.Linq;
using MountainPlanner.App.Picker;
using UnityEditor.Media;
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
            panel.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(UiFolder + "Resources/MountainPlannerUI/Theme-Dark.tss");   // UiPanels swaps in the player's theme at run time
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = MountainPlanner.UI.UiPanels.Reference(100);   // the accepted mockup's 1280×720 design base (task P2-01)
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

        /// <summary>
        /// Turns Picker Lab tour recordings into MP4s (H.264) with Unity's own encoder, no other tools:
        /// every folder of f_&lt;ms&gt;.jpg frames under <c>-movies root</c> becomes root/&lt;folder&gt;.mp4 at 15 fps,
        /// each frame held until the next one's time, so the movie runs at the recorded speed.
        ///   Unity -batchmode -executeMethod MountainPlanner.Editor.PickerLabSetup.EncodeMovies -movies folder -quit
        /// </summary>
        public static void EncodeMovies()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-movies");
            string root = i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("-movies folder");
            foreach (string dir in Directory.GetDirectories(root))
            {
                var frames = Directory.GetFiles(dir, "f_*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToArray();
                if (frames.Length == 0) continue;
                var times = frames.Select(f => int.Parse(Path.GetFileNameWithoutExtension(f).Substring(2), CultureInfo.InvariantCulture)).ToArray();
                Encode(frames, times, Path.Combine(root, Path.GetFileName(dir) + ".mp4"));
            }
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Turns a folder of stills into an MP4, each held for the same time, in name order (task 4.07 PR A2: the snow's
        /// time-lapse, one still a face day, from demo 111):
        ///   Unity -batchmode -executeMethod MountainPlanner.Editor.PickerLabSetup.EncodeStills -stills folder -pattern
        ///   snow-*.png -hold 400 -out file.mp4 -quit
        /// </summary>
        public static void EncodeStills()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name, string fallback)
            {
                int k = Array.IndexOf(args, name);
                return k >= 0 && k + 1 < args.Length ? args[k + 1] : fallback ?? throw new ArgumentException(name);
            }
            string folder = Arg("-stills", null);
            int hold = int.Parse(Arg("-hold", "400"), CultureInfo.InvariantCulture);
            var frames = Directory.GetFiles(folder, Arg("-pattern", "*.png")).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (frames.Length > 0) Encode(frames, frames.Select((_, k) => k * hold).ToArray(), Arg("-out", Path.Combine(folder, "movie.mp4")));
            else Debug.LogWarning($"[PickerLabSetup] no stills in {folder}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>Encodes frames (JPG or PNG) shown from their times (ms) at 15 fps, with a second's hold on the last.</summary>
        static void Encode(string[] frames, int[] times, string output)
        {
            const int fps = 15;
            var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            decoded.LoadImage(File.ReadAllBytes(frames[0]));
            int w = decoded.width & ~1, h = decoded.height & ~1;
            var frame = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var video = new VideoTrackAttributes
            {
                frameRate = new MediaRational(fps),
                width = (uint)w,
                height = (uint)h,
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.High,
            };
            int count = times[times.Length - 1] * fps / 1000 + fps;   // a second's hold on the last frame
            int shown = -1;
            using (var encoder = new MediaEncoder(output, video))
            {
                for (int k = 0, j = 0; k < count; k++)
                {
                    int at = k * 1000 / fps;
                    while (j + 1 < times.Length && times[j + 1] <= at) j++;
                    if (j != shown)
                    {
                        decoded.LoadImage(File.ReadAllBytes(frames[j]));
                        frame.SetPixels32(Crop(decoded.GetPixels32(), decoded.width, w, h));
                        frame.Apply();
                        shown = j;
                    }
                    encoder.AddFrame(frame);
                }
            }
            Debug.Log($"[PickerLabSetup] {output}: {count} frames ({count / (double)fps:F1} s) from {frames.Length} captures");
        }

        static Color32[] Crop(Color32[] pixels, int width, int w, int h)
        {
            if (width == w && pixels.Length == w * h) return pixels;
            var cropped = new Color32[w * h];
            for (int y = 0; y < h; y++) Array.Copy(pixels, y * width, cropped, y * w, w);
            return cropped;
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
