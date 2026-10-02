using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Water;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// The Mountain Viewer scene (task 06): opens a downloaded mountain from the library and shows it with
    /// the game camera. It's a stepping stone: the menu, library and HUD screens replace this overlay in
    /// tasks 13–14.
    ///
    /// Which mountain: the -package &lt;folder&gt; command-line argument, else the library's Jackson Hole
    /// (the demo), else the first mountain in the library. The library is
    /// %LOCALAPPDATA%\SkiAreaDesignChallenge, where tools/acquire (demo.bat 11–14) puts downloads.
    ///
    /// Keys (docs/plans/controls-key-map.md): the camera's own (<see cref="ViewCamera"/>), C free-fly, Home
    /// resets the view, H hides the UI, P photo mode, Shift+1 and Shift+3 map layers, Shift+6–0 info layers, F1 the developer panel, Esc the
    /// menu. The review switches (snow, wind, light, haze, saved views) live in the F1 panel, not on keys.
    /// </summary>
    public sealed partial class MountainViewer : MonoBehaviour
    {
        public TerrainDetail Detail = TerrainDetail.High;
        /// <summary>The mountain terrain material (MountainTerrain.shader), referenced from the scene so builds keep it.</summary>
        public Material TerrainMaterial;
        public ViewCamera Camera;
        /// <summary>The sun from the clock, and the four lighting presets (the F1 panel and -light pick one; -time sets the clock).</summary>
        public SceneLighting Lighting;
        /// <summary>Distant terrain shadows (FarShadow.compute), referenced from the scene so builds keep it.</summary>
        public ComputeShader FarShadowCompute;
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
        /// <summary>F1: the developer panel (status, data quality, every key, and the review switches).</summary>
        bool _help;
        /// <summary>The overlay; off for benchmarks and lineups so captures show only the scene.</summary>
        bool _hud = true;
        /// <summary>H: hide all UI (0.4 S6).</summary>
        bool _ui = true;
        /// <summary>P: photo mode (0.4 S10): the HUD hides and F12 saves a picture.</summary>
        bool _photo;
        bool _capturing;
        string _toast;
        float _toastUntil;
        bool _hudShown = true;
        float _nextReadout;
        string _clockText = "12:00";
        int _clockMinute = -1;
        FarTerrainShadow _farShadows;
        /// <summary>Map layers (snow, trees) and info layers (slope, exposure, snow depth, contours): keys, the HUD's rows and the F1 panel.</summary>
        readonly MapLayers _layers = new MapLayers();
        System.Collections.Generic.List<Landmarks.Placed> _landmarks = new System.Collections.Generic.List<Landmarks.Placed>();

        /// <summary>
        /// App scene flow (task 14): the package folder to open next. AppFlow sets it, then reloads this scene;
        /// it wins over -package, -site and the demo mountain.
        /// </summary>
        public static string RequestedPackage;

        /// <summary>
        /// App scene flow (task 14): the demo mountain behind the title screen. While true the HUD, the overlay and
        /// the viewer's keys are off and the camera ignores input (AppFlow drives the orbit); <see cref="EnterGame"/> ends it.
        /// </summary>
        public static bool TitleMode;

        /// <summary>Leaves the title screen: the keys, camera input and HUD come back as after a normal open.</summary>
        public void EnterGame()
        {
            TitleMode = false;
            if (Camera != null) Camera.InputEnabled = true;
        }

        public static string DataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge");

        /// <summary>Where photo mode saves (0.4 S10).</summary>
        public static string PhotoFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Ski Area Design Challenge");

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
            if (Lighting != null) ApplyTimeArguments(startArgs);
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
                var assets = new ForestAssets { Trees = Trees, Cull = ForestCull, Shader = TreeShader, ImpostorShader = TreeImpostorShader, Cliff = CliffMaterial != null ? new Material(CliffMaterial) : null, Edge = EdgeMaterial };
                _resort = await ResortOpener.OpenAsync(folder, null, Detail, progress, destroyCancellationToken, TerrainMaterial, assets);
                _status = $"Opened in {_resort.Seconds:F1} s";
                Debug.Log($"[MountainViewer] {_resort.Manifest.Site.Name}: {_resort.Tiles.Count} tiles opened in {_resort.Seconds:F2} s");
                string[] args = Environment.GetCommandLineArgs();
                if (Lighting != null)
                {
                    // The real sun for this place (task 11); a chosen preset keeps its time of day here.
                    var site = _resort.Manifest.Site;
                    Lighting.SetSite(site.Latitude, site.Longitude, _resort.Manifest.Crs.GridConvergenceDegrees);
                    ApplyTimeArguments(args);
                    if (FarShadowCompute != null && SystemInfo.supportsComputeShaders)
                    {
                        _farShadows = new FarTerrainShadow(FarShadowCompute, _resort.Tiles.Values, _resort.Ring);
                        if (Array.IndexOf(args, "-nofarshadows") >= 0) _farShadows.SetEnabled(false);   // cost measurements
                        Lighting.FarShadows = _farShadows;
                    }
                }
                if (Camera != null)
                {
                    Camera.Surface = _resort.Surface;
                    Camera.Ring = _resort.Ring;
                    Camera.HasRing = true;
                    Camera.PlinthTop = _resort.PlinthTop;
                    HomeView();
                }
                _landmarks = Landmarks.Place(_resort.Root.transform, _resort.Frame, _resort.Surface, HighlightMaterial);
                _layers.Bind(_resort.Ground, _resort.EdgeMaterial, assets.Cliff, (float)_resort.Manifest.Crs.GridConvergenceDegrees);
                StartCoroutine(WhenForestReady(() => _layers.BindForest(_resort.Root.GetComponent<ForestView>())));
                WireHud();
                if (Array.IndexOf(args, "-landmark") >= 0) FlyToLandmark();
                // Unattended checks: -nosnow, -noforest, -covermap, -info slope|exposure|depth, -contours,
                // -view x,z,distance,yaw,pitch (metres from the centre, degrees),
                // -wind calm|breeze|strong, -lake open|ice|snow, and -screenshot <file.png>, which captures the view once
                // it has settled, then quits.
                int wind = Array.IndexOf(args, "-wind");
                if (wind >= 0 && wind + 1 < args.Length && Enum.TryParse(args[wind + 1], true, out ForestWind.Level level))
                    StartCoroutine(WhenForestReady(() => Forest.Wind.Set(level)));
                int lake = Array.IndexOf(args, "-lake");
                if (lake >= 0 && lake + 1 < args.Length) SetLakes(args[lake + 1] == "open" ? WaterSurfaceState.OpenWater
                                                                 : args[lake + 1] == "ice" ? WaterSurfaceState.Ice : WaterSurfaceState.SnowCoveredIce);
                if (Array.IndexOf(args, "-covermap") >= 0) _layers.Set(MapLayers.CoverMap, true);
                int info = Array.IndexOf(args, "-info");
                if (info >= 0 && info + 1 < args.Length) _layers.Set(args[info + 1], true);
                if (Array.IndexOf(args, "-contours") >= 0) _layers.Set(MapLayers.Contours, true);
                int lt = Array.IndexOf(args, "-lodtransitions");   // review runs: LOD0→1, 1→2, 2→impostor, impostor→culled screen heights
                if (lt >= 0 && lt + 1 < args.Length)
                {
                    var t = args[lt + 1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    StartCoroutine(WhenForestReady(() => Forest.LodTransitions = new Vector4(t[0], t[1], t[2], t[3])));
                }
                if (Array.IndexOf(args, "-baretrees") >= 0) _layers.SetTreeSnow(false);
                if (Array.IndexOf(args, "-nosnow") >= 0) _layers.Set(MapLayers.Snow, false);
                if (Array.IndexOf(args, "-noforest") >= 0) _layers.Set(MapLayers.Trees, false);
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

        /// <summary>-time HH:MM and -day &lt;day of year&gt;: the view time for captures (local standard time at the site).</summary>
        void ApplyTimeArguments(string[] args)
        {
            int time = Array.IndexOf(args, "-time"), day = Array.IndexOf(args, "-day");
            bool hasTime = time >= 0 && time + 1 < args.Length, hasDay = day >= 0 && day + 1 < args.Length;
            if (!hasTime && !hasDay) return;
            var now = Lighting.Clock.Now;
            int second = now.SecondOfDay, dayOfYear = now.DayOfYear;
            if (hasTime && TimeSpan.TryParse(args[time + 1], System.Globalization.CultureInfo.InvariantCulture, out var hm))
                second = (int)hm.TotalSeconds % ViewTime.SecondsPerDay;
            if (hasDay && int.TryParse(args[day + 1], out int d)) dayOfYear = Mathf.Clamp(d, 1, ViewTime.DaysInYear(now.Year));
            Lighting.SetTime(new ViewTime(now.Year, dayOfYear, second));
        }

        System.Collections.IEnumerator CaptureAndQuit(string path)
        {
            for (int i = 0; i < 90; i++) yield return null; // let LOD and shadows settle
            var cam = Camera != null ? Camera.transform.position : Vector3.zero;
            Debug.Log($"[MountainViewer] camera {cam}, ground below {_resort.Surface.HeightAt(cam.x, cam.z):F0} m, target {(Camera != null ? Camera.Target : Vector3.zero)}, " +
                      $"active terrains {Terrain.activeTerrains.Length}, material {(TerrainMaterial != null ? TerrainMaterial.shader.name : "none")}, " +
                      $"time {(Lighting != null ? Lighting.Clock.Now.ToString() : "-")}, sun {(Lighting != null ? Lighting.CurrentLight.SunElevation : 0):F1}°");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log($"[MountainViewer] Screenshot saved to {path}; {_fps:F0} fps");
            Application.Quit();
        }

        static string PickPackage()
        {
            if (!string.IsNullOrEmpty(RequestedPackage) && Directory.Exists(RequestedPackage)) return RequestedPackage;
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
            if (TitleMode)
            {
                if (Camera != null) Camera.InputEnabled = false;
            }
            else HandleKeys(Keyboard.current);
            _resort?.States?.Sync();
            UpdateHud();

            // Keep lines a few pixels wide at any distance.
            if (Camera != null)
                foreach (var landmark in _landmarks)
                {
                    float width = Mathf.Max(3f, Vector3.Distance(Camera.transform.position, landmark.Centre) * 0.004f);
                    landmark.Line.widthMultiplier = width;
                }
        }

        /// <summary>
        /// The view keys (docs/plans/controls-key-map.md). The HUD owns T, Tab, U, Space, 1–4, Ctrl+S and Enter;
        /// while its Toolbox tray is open (<see cref="ViewCamera.LettersToTools"/>) letters are tool keys, so P
        /// and C wait until it closes.
        /// </summary>
        void HandleKeys(Keyboard keys)
        {
            if (keys == null) return;
            bool letters = Camera == null || !Camera.LettersToTools;
            bool shift = keys.shiftKey.isPressed, plain = !shift && !keys.ctrlKey.isPressed && !keys.altKey.isPressed;
            if (keys.escapeKey.wasPressedThisFrame)
            {
                if (_photo) _photo = false;   // Esc backs out one step
                else if (Hud != null && _resort != null) Hud.ToggleMenu();   // Quit is in the menu (0.4 S7)
                else Application.Quit();
            }
            if (keys.f1Key.wasPressedThisFrame) _help = !_help;
            if (_resort == null) return;
            if (plain && keys.hKey.wasPressedThisFrame) _ui = !_ui;
            if (plain && letters && keys.pKey.wasPressedThisFrame) _photo = !_photo;
            if (_photo && (keys.f12Key.wasPressedThisFrame || keys.spaceKey.wasPressedThisFrame) && !_capturing) StartCoroutine(CapturePhoto());
            if (plain && letters && keys.cKey.wasPressedThisFrame && Camera != null) Camera.ToggleMode();
            if (keys.homeKey.wasPressedThisFrame) HomeView();
            if (shift)
            {
                // Map layers (Shift+2, 4 and 5 are free for lifts and the rest as they're built).
                if (keys.digit1Key.wasPressedThisFrame) ToggleLayer(MapLayers.Snow);
                if (keys.digit3Key.wasPressedThisFrame) ToggleLayer(MapLayers.Trees);
                // Info layers: Contours with anything; the other four take turns.
                if (keys.digit6Key.wasPressedThisFrame) ToggleLayer(MapLayers.Contours);
                if (keys.digit7Key.wasPressedThisFrame) ToggleLayer(MapLayers.SlopeAngle);
                if (keys.digit8Key.wasPressedThisFrame) ToggleLayer(MapLayers.Exposure);
                if (keys.digit9Key.wasPressedThisFrame) ToggleLayer(MapLayers.SnowDepth);
                if (keys.digit0Key.wasPressedThisFrame) ToggleLayer(MapLayers.SnowConditions);   // reserved
            }
        }

        /// <summary>
        /// The keys, the HUD's layer rows and the F1 panel all switch layers here (<see cref="MapLayers"/>): the change
        /// shows in this frame. Snow conditions waits for the snow simulation.
        /// </summary>
        void ToggleLayer(string layer)
        {
            if (_resort == null) return;
            if (_layers.Toggle(layer)) return;
            if (layer == MapLayers.SnowConditions) Toast("Snow conditions come with the snow simulation");
        }

        void WireHud()
        {
            if (Hud == null) return;
            Hud.SetSite(_resort.Manifest.Site.Name, _resort.Manifest.Quality.Score);
            Hud.LayerChanged += (layer, on) => { if (on != _layers.IsOn(layer)) ToggleLayer(layer); };
            Hud.PresetChosen += i => Lighting?.Set(i);
            Hud.NorthUpChosen += () => Camera?.SetAngles(0, Camera.Pitch);
            Hud.QuitChosen += Application.Quit;
            ViewCamera.PointerBlocked = Hud.IsPointerOverPanel;
        }

        /// <summary>Shows or hides the HUD and refreshes it ten times a second (0.4 §8: bounded, no per-frame allocation).</summary>
        void UpdateHud()
        {
            if (Hud == null) return;
            bool show = _resort != null && _hud && _ui && !_photo && !TitleMode;
            if (show != _hudShown) Hud.SetVisible(_hudShown = show);
            if (!show || Time.unscaledTime < _nextReadout || Camera == null) return;
            _nextReadout = Time.unscaledTime + 0.1f;
            foreach (string id in MapLayers.MapIds) Hud.SetLayer(id, _layers.IsOn(id));
            foreach (string id in MapLayers.InfoIds) Hud.SetLayer(id, _layers.IsOn(id));
            Hud.SetLegend(_layers.InfoLayerId, _layers.ContoursOn);
            int preset = Lighting != null ? Lighting.Current : LightingPreset.Noon;
            int second = Lighting != null ? Lighting.Clock.Now.SecondOfDay : 12 * 3600;
            if (second / 60 != _clockMinute)   // the clock's text changes once a minute, not on every refresh
            {
                _clockMinute = second / 60;
                _clockText = $"{second / 3600:D2}:{second / 60 % 60:D2}";
            }
            Hud.SetPreset(preset, _clockText, second / (float)ViewTime.SecondsPerDay);
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            float distance = Vector3.Distance(cam.transform.position, Camera.Target);
            float metresPerPixel = 2 * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
            var mouse = Mouse.current;
            float elevation = float.NaN;
            if (mouse != null)
            {
                var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                float hit = Camera.GroundAlong(ray, 30000);
                if (!float.IsNaN(hit))
                {
                    var g = ray.GetPoint(hit);
                    elevation = _resort.Surface.HeightAt(g.x, g.z);
                }
            }
            Hud.SetReadouts(cam.transform.eulerAngles.y, metresPerPixel, elevation);
        }

        ForestRenderer Forest => _resort?.Root != null ? _resort.Root.GetComponent<ForestView>()?.Renderer : null;

        /// <summary>Every lake's surface state, through the task 10 API (iteration 1: one shared state).</summary>
        void SetLakes(WaterSurfaceState state)
        {
            if (_resort?.States == null) return;
            var surface = state == WaterSurfaceState.SnowCoveredIce ? WaterSurface.IterationOne : new WaterSurface(state, 0.4f, 0);
            _resort.States.Water.Set(WaterBodies.AllWater, surface);
        }

        System.Collections.IEnumerator WhenForestReady(Action action)
        {
            while (Forest == null) yield return null;
            action();
        }

        /// <summary>The saved view of the first landmark (Corbet's Couloir at Jackson Hole): the developer panel and -landmark.</summary>
        void FlyToLandmark()
        {
            if (Camera == null || _landmarks.Count == 0) return;
            var landmark = _landmarks[0];
            Camera.Frame(landmark.Centre, 600f);
        }

        /// <summary>
        /// Home: the whole resort as it opens, seen from the south-southeast looking north-northwest, so the
        /// winter sun (in the south) falls across the slopes and shows their relief (owner, 2026-10-01).
        /// </summary>
        void HomeView()
        {
            if (Camera == null || _resort == null) return;
            float centre = _resort.Surface.HeightAt(0, 0);
            Camera.Frame(new Vector3(0, float.IsNaN(centre) ? 2500 : centre, 0), _resort.Manifest.Site.SizeMetres * 1.1f);
            Camera.SetAngles(HomeYaw, 24f);
        }

        /// <summary>The Home view's heading (degrees clockwise from grid north): looking north-northwest.</summary>
        public const float HomeYaw = 330f;

        /// <summary>Photo mode's capture: a PNG of the scene alone (the hint hides for that frame), then a toast.</summary>
        System.Collections.IEnumerator CapturePhoto()
        {
            _capturing = true;
            yield return null;   // one frame without the photo-mode hint
            Directory.CreateDirectory(PhotoFolder);
            string name = $"{_resort.Manifest.Site.Name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
            string path = Path.Combine(PhotoFolder, string.Concat(name.Split(Path.GetInvalidFileNameChars())));
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            _capturing = false;
            Toast("Saved to Pictures\\Ski Area Design Challenge");
            Debug.Log($"[MountainViewer] Photo saved to {path}");
        }

        void Toast(string text)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        void OnGUI()
        {
            if (!_hud || _capturing || TitleMode) return;
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, wordWrap = true };
            style.normal.textColor = Color.white;
            GUI.backgroundColor = new Color(0f, 0f, 0f, 2f); // the default box is too pale to read over snow
            if (_error != null)
            {
                GUI.Box(new Rect(20, 20, 520, 90), _error, style);
                return;
            }
            if (_toast != null && Time.unscaledTime < _toastUntil)
            {
                var toast = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect(Screen.width / 2 - 220, Screen.height - 90, 440, 34), _toast, toast);
            }
            if (_photo)
            {
                var bar = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontSize = 14 };
                GUI.Box(new Rect(Screen.width / 2 - 260, 14, 520, 30), "Photo mode · F12 or Space: capture · Esc or P: exit", bar);
                GUI.backgroundColor = Color.white;
                return;
            }
            string text = _resort == null
                ? $"{_status}\n[{new string('#', (int)(_fraction * 30)).PadRight(30, '.')}] {_fraction * 100:F0}%"
                : $"{_resort.Manifest.Site.Name} · {_resort.Manifest.Site.SizeMetres / 1000.0:0.#} km · {_status} · {_fps:F0} fps\n" +
                  $"{_resort.Manifest.Quality.OneLiner}\n{_resort.Manifest.Flora.OneLiner}";
            if (_resort != null && !_help && Hud != null)
            {
                DrawLandmarkLabels();   // the HUD carries the rest; F1 shows the developer panel
                return;
            }
            if (_resort != null && _help)
            {
                text += "\nWASD or arrows pan · Q/E rotate · R/F tilt · Wheel, +/− or PgUp/PgDn zoom · Middle-drag rotate · Right-drag pan · Shift faster · " +
                        "Home reset view · C free-fly (W/S fly, Q/E turn, R/F pitch, PgUp/PgDn rise and sink, right-drag look) · " +
                        "Map layers: Shift+1 snow, Shift+3 trees · Info layers: Shift+6 contours, Shift+7 slope angle, Shift+8 slope exposure, Shift+9 snow depth, Shift+0 snow conditions (later) · H hide UI · P photo mode · F1 this panel · Esc menu · map data © OpenStreetMap contributors";
                float height = style.CalcHeight(new GUIContent(text), 820);
                GUI.Box(new Rect(20, 20, 820, height), text, style);
                DrawDeveloperPanel(new Rect(20, 28 + height, 820, 0));
            }
            else GUI.Box(new Rect(20, 20, 820, style.CalcHeight(new GUIContent(text), 820)), text, style);   // sized to the wrapped text
            GUI.backgroundColor = Color.white;
            DrawLandmarkLabels();
        }

        /// <summary>
        /// The review switches that left the keyboard (owner, 2026-10-01): snow, trees, wind, light and time, haze,
        /// lakes, distant shadows and saved views. Plain IMGUI, developer-only, until the settings window (S8).
        /// </summary>
        void DrawDeveloperPanel(Rect at)
        {
            const float row = 30, label = 130;
            var box = new Rect(at.x, at.y, at.width, row * 7 + 16);
            GUI.Box(box, GUIContent.none);
            float y = box.y + 8, x0 = box.x + 10 + label, w = 110;
            var caption = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            caption.normal.textColor = Color.white;
            GUI.backgroundColor = Color.white;
            bool Button(int column, string text) => GUI.Button(new Rect(x0 + column * (w + 6), y, w, row - 4), text);
            void Caption(string text) => GUI.Label(new Rect(box.x + 10, y, label, row), text, caption);

            Caption("Snow");
            if (Button(0, $"Layer: {(_layers.SnowOn ? "on" : "off")}")) ToggleLayer(MapLayers.Snow);
            var forest = Forest;
            if (Button(1, $"Trees: {(_layers.TreeSnowOn ? "on" : "off")}")) _layers.SetTreeSnow(!_layers.TreeSnowOn);
            if (Button(2, $"Wind: {(forest == null ? "breeze" : forest.Wind.Target.ToString().ToLowerInvariant())}")) forest?.Wind.Cycle();
            if (Button(3, $"Cover map: {(_layers.CoverMapOn ? "on" : "off")}")) ToggleLayer(MapLayers.CoverMap);
            y += row;

            if (Lighting != null)
            {
                Caption($"Light ({Lighting.CurrentName.ToLowerInvariant()})");
                for (int i = 0; i < LightingPreset.All.Length; i++)
                    if (Button(i, LightingPreset.All[i].Name)) Lighting.Set(i);
                if (Button(4, $"Haze: {(Lighting.HazeOn ? "on" : "off")}")) Lighting.SetHaze(!Lighting.HazeOn);
                y += row;

                var now = Lighting.Clock.Now;
                Caption($"Time {now.SecondOfDay / 3600:D2}:{now.SecondOfDay / 60 % 60:D2}");
                float hours = GUI.HorizontalSlider(new Rect(x0, y + 8, 4 * w + 18, row), now.SecondOfDay / 3600f, 0, 23.99f);
                int second = Mathf.Clamp((int)(hours * 3600), 0, ViewTime.SecondsPerDay - 1);
                if (second / 60 != now.SecondOfDay / 60) Lighting.SetTime(now.WithSecondOfDay(second));
                y += row;

                Caption($"Day {now.DayOfYear} of {now.Year}");
                float dayOfYear = GUI.HorizontalSlider(new Rect(x0, y + 8, 4 * w + 18, row), now.DayOfYear, 1, ViewTime.DaysInYear(now.Year));
                if ((int)dayOfYear != now.DayOfYear) Lighting.SetTime(new ViewTime(now.Year, (int)dayOfYear, now.SecondOfDay));
                y += row;
            }

            Caption("Lakes");
            var lakes = _resort.States != null ? _resort.States.Water[WaterBodies.AllWater].State : WaterSurfaceState.SnowCoveredIce;
            if (Button(0, (lakes == WaterSurfaceState.SnowCoveredIce ? "● " : "") + "Snow on ice")) SetLakes(WaterSurfaceState.SnowCoveredIce);
            if (Button(1, (lakes == WaterSurfaceState.Ice ? "● " : "") + "Bare ice")) SetLakes(WaterSurfaceState.Ice);
            if (Button(2, (lakes == WaterSurfaceState.OpenWater ? "● " : "") + "Open water")) SetLakes(WaterSurfaceState.OpenWater);
            y += row;

            Caption("Shadows");
            if (_farShadows != null && Button(0, $"Distant: {(_farShadows.Enabled ? "on" : "off")}")) _farShadows.SetEnabled(!_farShadows.Enabled);
            if (Camera != null && Button(1, Camera.Current == ViewCamera.Mode.Orbit ? "Camera: orbit" : "Camera: free-fly")) Camera.ToggleMode();
            y += row;

            Caption("Saved views");
            if (Button(0, "Home")) HomeView();
            if (_landmarks.Count > 0 && Button(1, _landmarks[0].Name.Length > 14 ? _landmarks[0].Name.Substring(0, 13) + "…" : _landmarks[0].Name)) FlyToLandmark();
        }

        void DrawLandmarkLabels()
        {
            if (Camera == null || !_ui || _photo) return;
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
