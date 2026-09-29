using System;
using System.IO;
using System.Linq;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// The Mountain Viewer scene (task 06): opens a downloaded mountain from the library and shows it with
    /// the temporary fly camera. It's a stepping stone: the menu, library and HUD screens replace this
    /// overlay in tasks 13–14.
    ///
    /// Which mountain: the -package &lt;folder&gt; command-line argument, else the library's Jackson Hole
    /// (the demo), else the first mountain in the library. The library is
    /// %LOCALAPPDATA%\SkiAreaDesignChallenge, where tools/acquire (demo.bat 11–14) puts downloads.
    /// </summary>
    public sealed partial class MountainViewer : MonoBehaviour
    {
        public TerrainDetail Detail = TerrainDetail.High;
        /// <summary>The mountain terrain material (MountainTerrain.shader), referenced from the scene so builds keep it.</summary>
        public Material TerrainMaterial;
        public DebugFlyCamera Camera;
        /// <summary>Unlit colour for landmark lines, referenced from the scene so builds keep the shader.</summary>
        public Material HighlightMaterial;
        /// <summary>The tree library (Mountain Planner, Import Trees); without it the mountain is bare.</summary>
        public TreePrototypeSet Trees;
        /// <summary>Cliff shells (Cliff.shader), referenced from the scene so builds keep it.</summary>
        public Material CliffMaterial;
        /// <summary>The diorama base's walls and plinth (DioramaWall.shader), referenced from the scene so builds keep it.</summary>
        public Material EdgeMaterial;
        public ComputeShader ForestCull;
        public Shader TreeShader;
        public Shader TreeImpostorShader;

        string _status = "Starting";
        float _fraction;
        OpenedResort _resort;
        string _error;
        float _fps;
        bool _help = true;
        /// <summary>The overlay; off for benchmarks and lineups so captures show only the scene.</summary>
        bool _hud = true;
        System.Collections.Generic.List<Landmarks.Placed> _landmarks = new System.Collections.Generic.List<Landmarks.Placed>();

        public static string DataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge");

        async void Start()
        {
            Application.targetFrameRate = -1;
            if (StartReviewTools()) return;   // -lineup: a tree lineup instead of a mountain
            // Task 15 investigates instanced terrain; -instancing turns it on for that work.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-instancing") >= 0) TerrainTiles.DrawInstanced = true;
            string folder = PickPackage();
            if (folder == null)
            {
                _error = "No mountains downloaded yet.\nRun demo.bat and choose 12 (Jackson Hole, 5 km), then start the viewer again.";
                return;
            }
            try
            {
                var progress = new Progress<OpenProgress>(p => { _status = p.Detail; _fraction = p.Fraction; });
                _resort = await ResortOpener.OpenAsync(folder, null, Detail, progress, destroyCancellationToken, TerrainMaterial,
                    new ForestAssets { Trees = Trees, Cull = ForestCull, Shader = TreeShader, ImpostorShader = TreeImpostorShader, Cliff = CliffMaterial != null ? new Material(CliffMaterial) : null, Edge = EdgeMaterial });
                _status = $"Opened in {_resort.Seconds:F1} s";
                Debug.Log($"[MountainViewer] {_resort.Manifest.Site.Name}: {_resort.Tiles.Count} tiles opened in {_resort.Seconds:F2} s");
                if (Camera != null)
                {
                    Camera.Surface = _resort.Surface;
                    float centre = _resort.Surface.HeightAt(0, 0);
                    Camera.Frame(new Vector3(0, float.IsNaN(centre) ? 2500 : centre, 0), _resort.Manifest.Site.SizeMetres * 1.1f);
                }
                _landmarks = Landmarks.Place(_resort.Root.transform, _resort.Frame, _resort.Surface, HighlightMaterial);
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-landmark") >= 0) FlyToLandmark();
                // Unattended checks: -nosnow, -covermap, -view x,z,distance,yaw,pitch (metres from the centre, degrees),
                // -wind calm|breeze|strong, and -screenshot <file.png>, which captures the view once it has settled, then quits.
                string[] args = Environment.GetCommandLineArgs();
                int wind = Array.IndexOf(args, "-wind");
                if (wind >= 0 && wind + 1 < args.Length && Enum.TryParse(args[wind + 1], true, out ForestWind.Level level))
                    StartCoroutine(WhenForestReady(() => Forest.Wind.Set(level)));
                if (Array.IndexOf(args, "-covermap") >= 0) ToggleOverlay();
                if (Array.IndexOf(args, "-baretrees") >= 0) StartCoroutine(WhenForestReady(ToggleTreeSnow));
                else if (Array.IndexOf(args, "-nosnow") >= 0) await ResortOpener.SetSnowAsync(_resort, false, destroyCancellationToken);
                int view = Array.IndexOf(args, "-view");
                if (view >= 0 && view + 1 < args.Length && Camera != null)
                {
                    var v = args[view + 1].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    Camera.Frame(new Vector3(v[0], _resort.Surface.HeightAt(v[0], v[1]), v[1]), v[2]);
                    Camera.SetAngles(v[3], v[4]);
                }
                int bench = Array.IndexOf(args, "-benchmark");
                if (bench >= 0 && bench + 1 < args.Length) StartCoroutine(RunBenchmark(args[bench + 1]));
                int shot = Array.IndexOf(args, "-screenshot");
                if (shot >= 0 && shot + 1 < args.Length) StartCoroutine(CaptureAndQuit(args[shot + 1]));
                int clip = Array.IndexOf(args, "-clip");
                if (clip >= 0 && clip + 1 < args.Length) StartCoroutine(CaptureClipAndQuit(args[clip + 1]));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _error = "Couldn't open the mountain: " + e.Message;
                Debug.LogException(e);
            }
        }

        System.Collections.IEnumerator CaptureAndQuit(string path)
        {
            for (int i = 0; i < 90; i++) yield return null; // let LOD and shadows settle
            var cam = Camera != null ? Camera.transform.position : Vector3.zero;
            Debug.Log($"[MountainViewer] camera {cam}, ground below {_resort.Surface.HeightAt(cam.x, cam.z):F0} m, target {(Camera != null ? Camera.Target : Vector3.zero)}, " +
                      $"active terrains {Terrain.activeTerrains.Length}, material {(TerrainMaterial != null ? TerrainMaterial.shader.name : "none")}");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log($"[MountainViewer] Screenshot saved to {path}; {_fps:F0} fps");
            Application.Quit();
        }

        static string PickPackage()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-package");
            if (i >= 0 && i + 1 < args.Length && Directory.Exists(args[i + 1])) return args[i + 1];
            var entries = ResortLibrary.Scan(DataRoot);
            var demo = entries.Where(e => e.Name == "Jackson Hole").OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
            return (demo ?? entries.FirstOrDefault())?.Folder;
        }

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            var keys = Keyboard.current;
            if (keys != null && keys.hKey.wasPressedThisFrame) _help = !_help;
            if (keys != null && keys.escapeKey.wasPressedThisFrame) Application.Quit();
            if (keys != null && keys.cKey.wasPressedThisFrame) FlyToLandmark();
            if (_resort != null && keys != null && keys.nKey.wasPressedThisFrame) _ = ResortOpener.SetSnowAsync(_resort, !_resort.SnowOn, destroyCancellationToken);
            if (_resort != null && keys != null && keys.vKey.wasPressedThisFrame) ToggleOverlay();
            if (_resort != null && keys != null && keys.tKey.wasPressedThisFrame) ToggleTreeSnow();
            if (keys != null && keys.bKey.wasPressedThisFrame) Forest?.Wind.Cycle();

            // Keep lines a few pixels wide at any distance.
            if (Camera != null)
                foreach (var landmark in _landmarks)
                {
                    float width = Mathf.Max(3f, Vector3.Distance(Camera.transform.position, landmark.Centre) * 0.004f);
                    landmark.Line.widthMultiplier = width;
                }
        }

        /// <summary>The cover-map overlay (task 07): flat class colours with the snow off, to check the cover.</summary>
        async void ToggleOverlay()
        {
            bool on = !_resort.Ground.OverlayOn;
            _resort.Ground.SetOverlay(on);
            if (on == _resort.SnowOn) await ResortOpener.SetSnowAsync(_resort, !on, destroyCancellationToken);
        }

        ForestRenderer Forest => _resort?.Root != null ? _resort.Root.GetComponent<ForestView>()?.Renderer : null;

        /// <summary>T: fresh snow on the trees, or bare evergreens (the ground keeps its snow).</summary>
        void ToggleTreeSnow()
        {
            var forest = Forest;
            if (forest != null) forest.SetSnowLoad(forest.SnowLoad > 0.5f ? 0 : 1);
        }

        System.Collections.IEnumerator WhenForestReady(Action action)
        {
            while (Forest == null) yield return null;
            action();
        }

        void FlyToLandmark()
        {
            if (Camera == null || _landmarks.Count == 0) return;
            var landmark = _landmarks[0];
            Camera.Frame(landmark.Centre, 600f);
        }

        void OnGUI()
        {
            if (!_hud) return;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, wordWrap = true };
            style.normal.textColor = Color.white;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 2f); // the default box is too pale to read over snow
            if (_error != null)
            {
                GUI.Box(new Rect(20, 20, 520, 90), _error, style);
                return;
            }
            string text = _resort == null
                ? $"{_status}\n[{new string('#', (int)(_fraction * 30)).PadRight(30, '.')}] {_fraction * 100:F0}%"
                : $"{_resort.Manifest.Site.Name} · {_resort.Manifest.Site.SizeMetres / 1000.0:0.#} km · {_status} · {_fps:F0} fps\n" +
                  $"{_resort.Manifest.Quality.OneLiner}\n{_resort.Manifest.Flora.OneLiner}";
            if (_resort != null && _help && _landmarks.Count > 0)
                text += $"\nC: fly to {_landmarks[0].Name} · N: snow {(_resort.SnowOn ? "on" : "off")} · V: cover map {(_resort.Ground.OverlayOn ? "on" : "off")} · T: tree snow {(Forest == null || Forest.SnowLoad > 0.5f ? "on" : "off")} · B: wind {(Forest == null ? "breeze" : Forest.Wind.Target.ToString().ToLowerInvariant())} · map data © OpenStreetMap contributors";
            if (_resort != null && _help)
                text += "\nWASD move · Q/E rotate · R/F tilt · Wheel or PgUp/PgDn zoom · Middle-drag rotate · Right-drag move · Shift faster · H hide · Esc quit";
            GUI.Box(new Rect(20, 20, 820, style.CalcHeight(new GUIContent(text), 820)), text, style);   // sized to the wrapped text
            GUI.backgroundColor = Color.white;

            if (Camera == null) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            var label = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter };
            label.normal.textColor = Color.white;
            foreach (var landmark in _landmarks)
            {
                Vector3 screen = cam.WorldToScreenPoint(landmark.LabelAt);
                if (screen.z <= 0) continue;
                var rect = new Rect(screen.x - 120, Screen.height - screen.y - 28, 240, 26);
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), landmark.Name, label);
                GUI.color = Color.white;
                GUI.Label(rect, landmark.Name, label);
            }
        }
    }
}
