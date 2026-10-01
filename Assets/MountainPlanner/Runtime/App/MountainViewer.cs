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
        /// <summary>The lighting presets (dawn, noon, golden hour, night): L cycles them, -light picks one.</summary>
        public SceneLighting Lighting;
        /// <summary>The S6 HUD (UI Toolkit, style-tile mock).</summary>
        public MountainPlanner.UI.MountainHud Hud;
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
        /// <summary>F1: the developer card (status, data quality, every key).</summary>
        bool _help;
        /// <summary>The overlay; off for benchmarks and lineups so captures show only the scene.</summary>
        bool _hud = true;
        /// <summary>H: hide all UI (0.4 S6).</summary>
        bool _ui = true;
        bool _hudShown = true;
        float _nextReadout;
        /// <summary>Clock times shown for the presets until task 11's scrubber (mid-January, Jackson Hole).</summary>
        static readonly string[] PresetClock = { "07:45", "12:20", "16:15", "22:00" };
        static readonly float[] PresetDay = { 7.75f / 24, 12.33f / 24, 16.25f / 24, 22f / 24 };
        System.Collections.Generic.List<Landmarks.Placed> _landmarks = new System.Collections.Generic.List<Landmarks.Placed>();

        public static string DataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge");

        async void Start()
        {
            Application.targetFrameRate = -1;
            string[] startArgs = Environment.GetCommandLineArgs();
            if (Array.IndexOf(startArgs, "-nohud") >= 0) _ui = false;   // clean captures: as if H was pressed
            if (Hud != null)
            {
                Hud.SetVisible(_hudShown = false);   // shown once a mountain is open
                int theme = Array.IndexOf(startArgs, "-theme");
                if (theme >= 0 && theme + 1 < startArgs.Length) Hud.SetTheme(startArgs[theme + 1] == "dark");
            }
            int light = Array.IndexOf(startArgs, "-light");
            if (Lighting != null && light >= 0 && light + 1 < startArgs.Length) Lighting.Set(LightingPreset.IndexOf(startArgs[light + 1]), instant: true);
            if (Lighting != null && Array.IndexOf(startArgs, "-nohaze") >= 0) Lighting.SetHaze(false);
            // -nopost: no grading or tonemapping, to measure what post-processing costs.
            if (Array.IndexOf(startArgs, "-nopost") >= 0 && Camera != null)
                UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(Camera.GetComponent<UnityEngine.Camera>()).renderPostProcessing = false;
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
                WireHud();
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-landmark") >= 0) FlyToLandmark();
                // Unattended checks: -nosnow, -covermap, -view x,z,distance,yaw,pitch (metres from the centre, degrees),
                // -wind calm|breeze|strong, and -screenshot <file.png>, which captures the view once it has settled, then quits.
                string[] args = Environment.GetCommandLineArgs();
                int wind = Array.IndexOf(args, "-wind");
                if (wind >= 0 && wind + 1 < args.Length && Enum.TryParse(args[wind + 1], true, out ForestWind.Level level))
                    StartCoroutine(WhenForestReady(() => Forest.Wind.Set(level)));
                if (Array.IndexOf(args, "-covermap") >= 0) ToggleOverlay();
                int lt = Array.IndexOf(args, "-lodtransitions");   // review runs: LOD0→1, 1→2, 2→impostor, impostor→culled screen heights
                if (lt >= 0 && lt + 1 < args.Length)
                {
                    var t = args[lt + 1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    StartCoroutine(WhenForestReady(() => Forest.LodTransitions = new Vector4(t[0], t[1], t[2], t[3])));
                }
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
            int site = Array.IndexOf(args, "-site");   // -site "Crystal Mountain": the largest, newest download with that name
            if (site >= 0 && site + 1 < args.Length)
            {
                var named = entries.Where(e => string.Equals(e.Name, args[site + 1], StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
                if (named != null) return named.Folder;
            }
            var demo = entries.Where(e => e.Name == "Jackson Hole").OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
            return (demo ?? entries.FirstOrDefault())?.Folder;
        }

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            var keys = Keyboard.current;
            if (keys != null && keys.hKey.wasPressedThisFrame) _ui = !_ui;
            if (keys != null && keys.f1Key.wasPressedThisFrame) _help = !_help;
            if (keys != null && keys.escapeKey.wasPressedThisFrame)
            {
                if (Hud != null && _resort != null) Hud.ToggleMenu();   // Quit is in the menu (0.4 S7)
                else Application.Quit();
            }
            if (keys != null && keys.cKey.wasPressedThisFrame) FlyToLandmark();
            if (_resort != null && keys != null && keys.nKey.wasPressedThisFrame) _ = ResortOpener.SetSnowAsync(_resort, !_resort.SnowOn, destroyCancellationToken);
            if (_resort != null && keys != null && keys.vKey.wasPressedThisFrame) ToggleOverlay();
            if (_resort != null && keys != null && keys.tKey.wasPressedThisFrame) ToggleTreeSnow();
            if (keys != null && keys.bKey.wasPressedThisFrame) Forest?.Wind.Cycle();
            if (keys != null && keys.lKey.wasPressedThisFrame && Lighting != null) Lighting.Cycle();
            if (keys != null && keys.mKey.wasPressedThisFrame && Lighting != null) Lighting.SetHaze(!Lighting.HazeOn);

            UpdateHud();

            // Keep lines a few pixels wide at any distance.
            if (Camera != null)
                foreach (var landmark in _landmarks)
                {
                    float width = Mathf.Max(3f, Vector3.Distance(Camera.transform.position, landmark.Centre) * 0.004f);
                    landmark.Line.widthMultiplier = width;
                }
        }

        void WireHud()
        {
            if (Hud == null) return;
            Hud.SetSite(_resort.Manifest.Site.Name, _resort.Manifest.Quality.Score);
            Hud.LayerChanged += (layer, on) =>
            {
                if (layer == "snow" && on != _resort.SnowOn) _ = ResortOpener.SetSnowAsync(_resort, on, destroyCancellationToken);
                else if (layer == "cover" && on != _resort.Ground.OverlayOn) ToggleOverlay();
                else if (layer == "forest") { var view = _resort.Root.GetComponent<ForestView>(); if (view != null) view.enabled = on; }
            };
            Hud.PresetChosen += i => Lighting?.Set(i);
            Hud.NorthUpChosen += () => Camera?.SetAngles(0, Camera.Pitch);
            Hud.QuitChosen += Application.Quit;
            DebugFlyCamera.PointerBlocked = Hud.IsPointerOverPanel;
        }

        /// <summary>Shows or hides the HUD and refreshes it ten times a second (0.4 §8: bounded, no per-frame allocation).</summary>
        void UpdateHud()
        {
            if (Hud == null) return;
            bool show = _resort != null && _hud && _ui;
            if (show != _hudShown) Hud.SetVisible(_hudShown = show);
            if (!show || Time.unscaledTime < _nextReadout || Camera == null) return;
            _nextReadout = Time.unscaledTime + 0.1f;
            var view = _resort.Root.GetComponent<ForestView>();
            Hud.SetLayer("snow", _resort.SnowOn);
            Hud.SetLayer("cover", _resort.Ground.OverlayOn);
            Hud.SetLayer("forest", view == null || view.enabled);
            int preset = Lighting != null ? Lighting.Current : 1;
            Hud.SetPreset(preset, PresetClock[preset], PresetDay[preset]);
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            float distance = Vector3.Distance(cam.transform.position, Camera.Target);
            float metresPerPixel = 2 * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
            var mouse = Mouse.current;
            float elevation = mouse != null ? GroundUnder(cam.ScreenPointToRay(mouse.position.ReadValue())) : float.NaN;
            Hud.SetReadouts(cam.transform.eulerAngles.y, metresPerPixel, elevation);
        }

        /// <summary>The terrain height where a ray meets the ground (NaN if it doesn't within 30 km): march, then bisect.</summary>
        float GroundUnder(Ray ray)
        {
            float t = 0, step = 10, previous = 0;
            while (t < 30000)
            {
                var p = ray.GetPoint(t);
                float h = _resort.Surface.HeightAt(p.x, p.z);
                if (!float.IsNaN(h) && p.y <= h)
                {
                    float lo = previous, hi = t;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        var m = ray.GetPoint(mid);
                        float hm = _resort.Surface.HeightAt(m.x, m.z);
                        if (!float.IsNaN(hm) && m.y <= hm) hi = mid; else lo = mid;
                    }
                    var g = ray.GetPoint(hi);
                    return _resort.Surface.HeightAt(g.x, g.z);
                }
                previous = t;
                step = Mathf.Max(10, t * 0.01f);
                t += step;
            }
            return float.NaN;
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
            if (_resort != null && !_help && Hud != null)
            {
                DrawLandmarkLabels();   // the HUD carries the rest; F1 shows the developer card
                return;
            }
            if (_resort != null && _help && _landmarks.Count > 0)
                text += $"\nC: fly to {_landmarks[0].Name} · N: snow {(_resort.SnowOn ? "on" : "off")} · V: cover map {(_resort.Ground.OverlayOn ? "on" : "off")} · T: tree snow {(Forest == null || Forest.SnowLoad > 0.5f ? "on" : "off")} · B: wind {(Forest == null ? "breeze" : Forest.Wind.Target.ToString().ToLowerInvariant())} · L: {(Lighting == null ? "noon" : Lighting.CurrentName.ToLowerInvariant())} · M: haze {(Lighting == null || Lighting.HazeOn ? "on" : "off")} · map data © OpenStreetMap contributors";
            if (_resort != null && _help)
                text += "\nWASD move · Q/E rotate · R/F tilt · Wheel or PgUp/PgDn zoom · Middle-drag rotate · Right-drag move · Shift faster · H hide UI · F1 this card · Esc menu";
            GUI.Box(new Rect(20, 20, 820, style.CalcHeight(new GUIContent(text), 820)), text, style);   // sized to the wrapped text
            GUI.backgroundColor = Color.white;
            DrawLandmarkLabels();
        }

        void DrawLandmarkLabels()
        {
            if (Camera == null || !_ui) return;
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
