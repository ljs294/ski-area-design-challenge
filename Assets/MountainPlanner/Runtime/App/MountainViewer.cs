using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Measure;
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
        /// <summary>Terrain detail; set from the quality preset (<see cref="QualityPresets"/>) when the scene starts.</summary>
        public TerrainDetail Detail = TerrainDetail.High;
        /// <summary>The mountain terrain material (MountainTerrain.shader), referenced from the scene so builds keep it.</summary>
        public Material TerrainMaterial;
        public ViewCamera Camera;
        /// <summary>The sun from the clock, and the four lighting presets (the F1 panel and -light pick one; -time sets the clock).</summary>
        public SceneLighting Lighting;
        /// <summary>Distant terrain shadows (FarShadow.compute), referenced from the scene so builds keep it.</summary>
        public ComputeShader FarShadowCompute;
        /// <summary>The S6 HUD in the Trailhead direction (UI Toolkit, task P2-02).</summary>
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
        /// <summary>The bar's clock: pause and speeds 1–4 run the sun (task P2-02). Made when a mountain opens.</summary>
        ViewClockRunner _clock;
        /// <summary>The view opens paused at 10:30, unless -time or -light chose a time.</summary>
        const int OpeningSecond = 10 * 3600 + 30 * 60;
        FarTerrainShadow _farShadows;
        /// <summary>The IMGUI overlay, enabled only while it has something to draw (<see cref="NeedsOverlay"/>).</summary>
        ViewerOverlay _overlay;
        /// <summary>Landmark names in the HUD's UI Toolkit panel (no IMGUI, so nothing allocates per frame).</summary>
        MountainPlanner.UI.LandmarkLabelOverlay _landmarkLabels;
        /// <summary>Map layers (snow, trees) and info layers (slope, exposure, snow depth, contours): keys, the HUD's rows and the F1 panel.</summary>
        readonly MapLayers _layers = new MapLayers();
        /// <summary>Contour elevation labels (task 12b.2), and the label set they show (it changes with the units).</summary>
        MountainPlanner.UI.ContourLabelOverlay _contourLabels;
        ContourLabel[] _contourLabelSet;
        /// <summary>-pointer x,y (fractions of the screen): a fixed pointer for captures of the readouts.</summary>
        Vector2? _pointer;
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
            _overlay = gameObject.AddComponent<ViewerOverlay>();
            _overlay.Viewer = this;
            string[] startArgs = Environment.GetCommandLineArgs();
            // The graphics settings (task P2-05): the player's saved choices, or for this run only the preset that
            // -quality low|medium|high|ultra names (task 15; benchmarks), uncapped with V-Sync off. They must come
            // before -shadows (which edits the active URP asset) and before the mountain opens (terrain detail).
            ApplyGraphicsSettings(startArgs);
            Detail = QualityPresets.Options.Terrain;
            if (Array.IndexOf(startArgs, "-nohud") >= 0) _ui = false;   // clean captures: as if H was pressed
            // -theme dark|light and -uiscale 50..150 are read by UiPreferences, for every screen.
            if (Hud != null) Hud.SetVisible(_hudShown = false);   // shown once a mountain is open
            int light = Array.IndexOf(startArgs, "-light");
            if (Lighting != null && light >= 0 && light + 1 < startArgs.Length) Lighting.Set(LightingPreset.IndexOf(startArgs[light + 1]), instant: true);
            if (Lighting != null && Array.IndexOf(startArgs, "-nohaze") >= 0) Lighting.SetHaze(false);
            int look = Array.IndexOf(startArgs, "-look");   // -look current|bluebird|soft|postcard: the grade (beauty pass)
            if (Lighting != null && look >= 0 && look + 1 < startArgs.Length) Lighting.SetStyle(LookStyle.IndexOf(startArgs[look + 1]));
            if (Lighting != null) ApplyTimeArguments(startArgs);
            // -nopost: no grading or tonemapping, to measure what post-processing costs.
            if (Array.IndexOf(startArgs, "-nopost") >= 0 && Camera != null)
                UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(Camera.GetComponent<UnityEngine.Camera>()).renderPostProcessing = false;
            if (StartReviewTools()) return;   // -lineup: a tree lineup instead of a mountain
            // Instanced terrain is the default (task 15); -noinstancing draws it the old way, for comparisons.
            TerrainTiles.DrawInstanced = Array.IndexOf(startArgs, "-noinstancing") < 0;
            string folder = PickPackage();
            if (folder == null)
            {
                _error = "No areas downloaded yet.\nRun demo.bat and choose 12 (Jackson Hole, 5 km), then start the viewer again.";
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
                        _noFarShadows = Array.IndexOf(args, "-nofarshadows") >= 0;   // cost measurements
                        _noSkyOcclusion = Array.IndexOf(args, "-noao") >= 0;         // comparisons (beauty pass, item 2)
                        ApplyTerrainShading();   // off on Low, or as Settings › Graphics says
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
                StartTreeDetailTiming(args);   // Auto tree detail, the first time on this PC and screen (task P2-05)
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
                int pointer = Array.IndexOf(args, "-pointer");
                if (pointer >= 0 && pointer + 1 < args.Length)
                {
                    var f = args[pointer + 1].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    _pointer = new Vector2(f[0], f[1]);
                }
                int units = Array.IndexOf(args, "-units");   // -units metric|imperial: captures, without changing the saved choice
                if (units >= 0 && units + 1 < args.Length) DisplayUnits.Set(args[units + 1] == "metric" ? UnitSystem.Metric : UnitSystem.Imperial, remember: false);
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
                int benchViews = Array.IndexOf(args, "-benchmark-views");
                if (benchViews >= 0 && benchViews + 1 < args.Length) StartCoroutine(RunViewBenchmark(args[benchViews + 1]));
                int movie = Array.IndexOf(args, "-pathmovie");
                if (movie >= 0 && movie + 1 < args.Length) StartCoroutine(RecordPathMovie(args[movie + 1]));
                int shot = Array.IndexOf(args, "-screenshot");
                if (shot >= 0 && shot + 1 < args.Length) StartCoroutine(CaptureAndQuit(args[shot + 1]));
                int clip = Array.IndexOf(args, "-clip");
                if (clip >= 0 && clip + 1 < args.Length) StartCoroutine(CaptureClipAndQuit(args[clip + 1]));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _error = "Couldn't open the area: " + e.Message;
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
            while (!_resort.CoverReady.IsCompleted) yield return null;   // the cover, snowpack, contour labels and forest are in
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
                var named = entries.Where(e => string.Equals(e.Name, args[site + 1], StringComparison.OrdinalIgnoreCase) || string.Equals(e.OriginalName, args[site + 1], StringComparison.OrdinalIgnoreCase))
                                   .OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
                if (named != null) return named.Folder;
            }
            var demo = entries.Where(e => e.OriginalName == "Jackson Hole").OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
            return (demo ?? entries.FirstOrDefault())?.Folder;
        }

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(1e-4f, Time.unscaledDeltaTime), 0.05f);
            if (TitleMode)
            {
                if (Camera != null) Camera.InputEnabled = false;
            }
            else
            {
                HandleKeys(Keyboard.current);
                // A click on the mountain closes the HUD's dropdowns (the mockup).
                var click = Mouse.current;
                if (Hud != null && _resort != null && click != null && click.leftButton.wasPressedThisFrame && !Hud.IsPointerOverPanel(click.position.ReadValue())) Hud.ClickedMap();
            }
            _resort?.States?.Sync();
            RunClock();
            // The Auto theme follows this sun on every screen (task P2-01); nothing happens unless sunrise or sunset passed.
            if (Lighting != null) MountainPlanner.UI.UiPreferences.SetDaylight(Lighting.CurrentLight.SunElevation > 0);
            UpdateHud();
            bool overlay = NeedsOverlay();
            if (_overlay != null && _overlay.enabled != overlay) _overlay.enabled = overlay;

            // Keep lines a few pixels wide at any distance.
            if (Camera != null)
                foreach (var landmark in _landmarks)
                {
                    float width = Mathf.Max(3f, Vector3.Distance(Camera.transform.position, landmark.Centre) * 0.004f);
                    landmark.Line.widthMultiplier = width;
                }
        }

        /// <summary>
        /// The bar's clock moves the sun (task P2-02): a time set elsewhere (the F1 slider, -time) is taken first, and the
        /// sun catches up every 30 game seconds or when the clock stops (ViewClockRunner).
        /// </summary>
        void RunClock()
        {
            if (_clock == null || Lighting == null || TitleMode) return;
            _clock.Adopt(Lighting.Clock.Now);
            int day = _clock.Now.DayOfYear;
            _clock.Advance(Mathf.Min(0.25f, Time.unscaledDeltaTime));
            // A new day while the clock runs gets a routine note, as in the mockup (a few words once a game day).
            if (_clock.Now.DayOfYear != day && Hud != null && _hudShown)
                Hud.Toast(MountainPlanner.UI.Hud.HudText.Day(MountainPlanner.UI.Hud.HudText.SeasonDay(_clock.Now.Year, _clock.Now.DayOfYear)) + " begins");
            if (!_clock.SunBehind) return;
            Lighting.SetTime(_clock.Now);
            _clock.Pushed();
        }

        /// <summary>
        /// The view keys (docs/plans/controls-key-map.md; rebindable, <see cref="KeyBindings"/>). The HUD owns T, Tab, U, Space, 1–4, Ctrl+S and Enter;
        /// while its Toolbox tray is open (<see cref="ViewCamera.LettersToTools"/>) letters are tool keys, so P
        /// and C wait until it closes.
        /// </summary>
        void HandleKeys(Keyboard keys)
        {
            if (keys == null) return;
            bool letters = Camera == null || !Camera.LettersToTools;
            bool flowKeys = (FlowHasKeyboard?.Invoke() ?? false) || FlowTookEscapeFrame == Time.frameCount;
            if (keys.escapeKey.wasPressedThisFrame && !flowKeys)
            {
                // Esc backs out one step: photo mode, then the HUD's window, dropdown or panel, then a HUD control lets go
                // of the keyboard; with nothing left to close it opens the menu (Quit is in it, 0.4 S7).
                if (_photo) _photo = false;
                else if (Hud != null && _resort != null && Hud.ModalOpen) Hud.BackOut();
                else if (Hud != null && _resort != null && Hud.ReleaseKeyboard()) { }
                else if (Hud != null && _resort != null && Hud.BackOut()) { }
                else if (Hud != null && _resort != null) Hud.ToggleMenu();
                else Application.Quit();
            }
            if (KeyBindings.Pressed(keys, GameAction.DeveloperPanel)) _help = !_help;
            if (_resort == null) return;
            // Analysis (Tab by default) whenever no window has the keyboard (the HUD keeps Tab from moving between its controls).
            if (Hud != null && KeyBindings.Pressed(keys, GameAction.Analysis, letters) && !Hud.ModalOpen && !flowKeys && !(FlowHasKeyboard?.Invoke() ?? false)) Hud.ToggleAnalysis();
            if (UiHasKeyboard()) return;   // the menu, a dialog or a focused HUD control has the keys (task P2-01)
            // The player's keys (Settings › Controls; the defaults are the key map's). While the tray is open,
            // letter keys are tools, except the Toolbox's own key, which closes it.
            if (Hud != null && KeyBindings.Pressed(keys, GameAction.Toolbox)) Hud.ToggleToolbox();
            if (_photo && KeyBindings.Pressed(keys, GameAction.PhotoCapture, letters) && !_capturing) StartCoroutine(CapturePhoto());
            else if (_clock != null && !_photo && KeyBindings.Pressed(keys, GameAction.Pause, letters)) _clock.TogglePause();
            if (_clock != null)
            {
                if (KeyBindings.Pressed(keys, GameAction.Speed1, letters)) _clock.SetSpeed(1);
                if (KeyBindings.Pressed(keys, GameAction.Speed2, letters)) _clock.SetSpeed(2);
                if (KeyBindings.Pressed(keys, GameAction.Speed3, letters)) _clock.SetSpeed(3);
                if (KeyBindings.Pressed(keys, GameAction.Speed4, letters)) _clock.SetSpeed(4);
            }
            if (KeyBindings.Pressed(keys, GameAction.HideUi, letters)) _ui = !_ui;
            if (KeyBindings.Pressed(keys, GameAction.PhotoMode, letters)) _photo = !_photo;
            if (KeyBindings.Pressed(keys, GameAction.FreeFly, letters) && Camera != null) Camera.ToggleMode();
            if (KeyBindings.Pressed(keys, GameAction.Units, letters)) DisplayUnits.Toggle();   // feet or metres (task 12b.2)
            if (KeyBindings.Pressed(keys, GameAction.ResetView, letters)) HomeView();
            // Map layers (Shift+2, 4 and 5 are free for lifts and the rest as they're built).
            if (KeyBindings.Pressed(keys, GameAction.LayerSnow, letters)) ToggleLayer(MapLayers.Snow);
            if (KeyBindings.Pressed(keys, GameAction.LayerTrees, letters)) ToggleLayer(MapLayers.Trees);
            // Info layers: Contours with anything; the other four take turns.
            if (KeyBindings.Pressed(keys, GameAction.InfoContours, letters)) ToggleLayer(MapLayers.Contours);
            if (KeyBindings.Pressed(keys, GameAction.InfoSlope, letters)) ToggleLayer(MapLayers.SlopeAngle);
            if (KeyBindings.Pressed(keys, GameAction.InfoExposure, letters)) ToggleLayer(MapLayers.Exposure);
            if (KeyBindings.Pressed(keys, GameAction.InfoDepth, letters)) ToggleLayer(MapLayers.SnowDepth);
            if (KeyBindings.Pressed(keys, GameAction.InfoConditions, letters)) ToggleLayer(MapLayers.SnowConditions);   // reserved
        }

        /// <summary>
        /// The keys, the HUD's layer rows and the F1 panel all switch layers here (<see cref="MapLayers"/>): the change
        /// shows in this frame. Snow conditions waits for the snow simulation.
        /// </summary>
        void ToggleLayer(string layer)
        {
            if (_resort == null) return;
            if (_layers.Toggle(layer)) return;
            if (layer != MapLayers.SnowConditions) return;
            if (Hud != null && _hudShown) Hud.Toast("Snow conditions come with the snow simulation");
            else Toast("Snow conditions come with the snow simulation");
        }

        /// <summary>For UI captures (AppFlow.UiCapture): a layer on or off, and the pointer at a fraction of the screen (null: the mouse).</summary>
        internal void SetLayerForCapture(string layer, bool on) { if (_resort != null && _layers.IsOn(layer) != on) ToggleLayer(layer); }

        internal void SetPointerForCapture(Vector2? fraction) => _pointer = fraction;

        /// <summary>The bar's clock (tests).</summary>
        internal ViewClockRunner Clock => _clock;

        static string Coordinates(double lat, double lon) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00}° {1}, {2:0.00}° {3}", Math.Abs(lat), lat >= 0 ? "N" : "S", Math.Abs(lon), lon >= 0 ? "E" : "W");

        void WireHud()
        {
            if (Hud == null) return;
            var site = _resort.Manifest.Site;
            // Packages keep no place name yet, so the line under the name (the mockup's "Jackson Hole, Wyoming") says where it is.
            // The name is the player's own when they renamed the area (task P2-04).
            Hud.SetSite(ResortLibrary.DisplayName(_resort.PackageFolder, _resort.Manifest), Coordinates(site.Latitude, site.Longitude), _resort.Manifest.Quality.Score);
            Hud.PrepareElevations(_resort.Cache.HeightMin - 100, _resort.Cache.HeightMin + _resort.Cache.HeightRange + 100);
            Hud.SetTerrain(_resort.Cache.HeightMin + _resort.Cache.HeightRange, _resort.Cache.HeightMin, (double)site.SizeMetres * site.SizeMetres);
            Hud.LayerChanged += (layer, on) => { if (on != _layers.IsOn(layer)) ToggleLayer(layer); };
            Hud.QuitChosen += Application.Quit;
            if (Lighting != null)
            {
                // The bar's clock (task P2-02): the view opens paused at 10:30 unless -time or -light chose a time.
                var args = Environment.GetCommandLineArgs();
                if (Array.IndexOf(args, "-time") < 0 && Array.IndexOf(args, "-light") < 0) Lighting.SetTime(Lighting.Clock.Now.WithSecondOfDay(OpeningSecond));
                _clock = new ViewClockRunner(Lighting.Clock.Now);
                Hud.PauseChosen += _clock.TogglePause;
                Hud.SpeedChosen += _clock.SetSpeed;
            }
            Hud.UnitsChosen += DisplayUnits.Toggle;
            ViewCamera.PointerBlocked = Hud.IsPointerOverPanel;
            ViewCamera.KeysBlocked = UiHasKeyboard;
            _contourLabels = new MountainPlanner.UI.ContourLabelOverlay(Hud.ContourLabelLayer);
            _landmarkLabels = new MountainPlanner.UI.LandmarkLabelOverlay(Hud.ContourLabelLayer);
            var named = new System.Collections.Generic.List<(string, Vector3)>(_landmarks.Count);
            foreach (var landmark in _landmarks) named.Add((landmark.Name, landmark.LabelAt));
            _landmarkLabels.SetLandmarks(named);
            DisplayUnits.Changed += OnUnitsChanged;
        }

        /// <summary>New units: the contour interval and snow-depth stops in the shader, and the labels' numbers.</summary>
        void OnUnitsChanged()
        {
            _layers.Apply();
            _contourLabelSet = null;   // UpdateContourLabels picks the set for the new units
        }

        /// <summary>The package on screen, or null while none is open.</summary>
        public string OpenPackage => _resort?.PackageFolder;

        /// <summary>
        /// Lets go of the open cache (task P2-04), so Manage Areas can delete the mountain behind the title. False while
        /// it's still opening: its cover and trees are being read from that cache.
        /// </summary>
        public bool ReleaseCache()
        {
            if (_resort == null) return true;
            if (!_resort.CoverReady.IsCompleted) return false;
            _resort.CacheLease?.Dispose();
            _resort.CacheLease = null;
            return true;
        }

        /// <summary>Holds the open cache again after <see cref="ReleaseCache"/>, when the delete it allowed didn't happen.</summary>
        public void HoldCache()
        {
            if (_resort == null || _resort.CacheLease != null) return;
            try { _resort.CacheLease = TerrainCache.Hold(_resort.PackageFolder); }
            catch (IOException e) { Debug.LogWarning($"[MountainViewer] Couldn't hold the cache again: {e.Message}"); }
        }

        void OnDestroy()
        {
            _resort?.CacheLease?.Dispose();   // the next scene's viewer holds its own
            DisplayUnits.Changed -= OnUnitsChanged;
            if (ViewCamera.KeysBlocked == (Func<bool>)UiHasKeyboard) ViewCamera.KeysBlocked = null;
            ReleaseSettings();
        }

        /// <summary>
        /// Set by the app flow while one of its windows has the keyboard (Settings, Credits, a dialog, the picker) or
        /// it took this frame's Esc: the viewer's keys and Esc wait (task P2-01).
        /// </summary>
        public static Func<bool> FlowHasKeyboard;
        /// <summary>The frame in which the app flow used Esc (closing a dialog, minimising the download card).</summary>
        public static int FlowTookEscapeFrame = -1;

        bool UiHasKeyboard() => (Hud != null && Hud.HasKeyboard) || (FlowHasKeyboard?.Invoke() ?? false);

        /// <summary>Moves the contour labels with the camera, after it has moved this frame.</summary>
        void LateUpdate()
        {
            if (_contourLabels == null || _resort == null) return;
            _landmarkLabels?.Update(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null, _hudShown && !_photo && (Hud == null || Hud.LabelsOn));
            var set = _resort.ContourLabels[(int)DisplayUnits.Current];
            if (!ReferenceEquals(set, _contourLabelSet))
            {
                _contourLabelSet = set;
                _contourLabels.SetLabels(set, DisplayUnits.Current);
            }
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            _contourLabels.Update(cam, _resort.Surface, _layers.ContoursOn && _hudShown);
        }

        /// <summary>The slope (a grade in percent) and the way it faces (degrees from true north) at a point, over 4 m as the shader measures.</summary>
        (float Percent, float Bearing) SlopeAt(float x, float z)
        {
            var s = _resort.Surface;
            float east = (s.HeightAt(x + 2, z) - s.HeightAt(x - 2, z)) / 4, north = (s.HeightAt(x, z + 2) - s.HeightAt(x, z - 2)) / 4;
            float bearing = Mathf.Atan2(-east, -north) * Mathf.Rad2Deg - (float)_resort.Manifest.Crs.GridConvergenceDegrees;
            return ((float)SlopeBands.PercentFromRise(Mathf.Sqrt(east * east + north * north)), bearing);
        }

        /// <summary>The snow-depth field at a point, between its 8 m cells.</summary>
        float SnowDepthAt(float x, float z)
        {
            var field = _resort.States?.Snow;
            if (field == null) return float.NaN;
            float u = Mathf.Clamp((x - _resort.Ring.xMin) / field.CellMetres - 0.5f, 0, field.Width - 1);
            float v = Mathf.Clamp((z - _resort.Ring.yMin) / field.CellMetres - 0.5f, 0, field.Height - 1);
            int i = Mathf.Min((int)u, field.Width - 2 < 0 ? 0 : field.Width - 2), j = Mathf.Min((int)v, field.Height - 2 < 0 ? 0 : field.Height - 2);
            int i1 = Mathf.Min(i + 1, field.Width - 1), j1 = Mathf.Min(j + 1, field.Height - 1);
            float fu = u - i, fv = v - j;
            return Mathf.Lerp(Mathf.Lerp(field[i, j], field[i1, j], fu), Mathf.Lerp(field[i, j1], field[i1, j1], fu), fv);
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
            if (_clock != null) Hud.SetClock(_clock.Now, _clock.Speed, _clock.Paused);
            // While the tray is open its tools take their letters (the key map), from Phase 3 when the tools work; until
            // then the letters, WASD among them, stay with the camera.
            Camera.LettersToTools = false;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            var mouse = Mouse.current;
            float elevation = float.NaN, slope = float.NaN, bearing = 0, snow = float.NaN;
            string info = _layers.InfoLayerId;
            Vector2? pointer = _pointer.HasValue ? new Vector2(_pointer.Value.x * Screen.width, _pointer.Value.y * Screen.height)
                : mouse != null ? mouse.position.ReadValue() : (Vector2?)null;
            // Over the HUD the bar shows no elevation, as the mockup does ("– – –").
            if (pointer.HasValue && (_pointer.HasValue || !Hud.IsPointerOverPanel(pointer.Value)))
            {
                var ray = cam.ScreenPointToRay(pointer.Value);
                float hit = Camera.GroundAlong(ray, 30000);
                if (!float.IsNaN(hit))
                {
                    var g = ray.GetPoint(hit);
                    elevation = _resort.Surface.HeightAt(g.x, g.z);
                    // What the info layer that's on reads under the pointer (task 12b.2).
                    if (!float.IsNaN(elevation) && (info == MapLayers.SlopeAngle || info == MapLayers.Exposure)) (slope, bearing) = SlopeAt(g.x, g.z);
                    else if (!float.IsNaN(elevation) && info == MapLayers.SnowDepth) snow = SnowDepthAt(g.x, g.z);
                }
            }
            Hud.SetElevation(elevation);
            Hud.SetInfoReadout(info, slope, bearing, snow);
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

        /// <summary>
        /// Whether the IMGUI overlay has anything to draw: loading, an error, a toast, photo mode, the F1 panel, or
        /// landmark names when there's no HUD to carry them. Otherwise it's off, so steady play allocates nothing.
        /// </summary>
        bool NeedsOverlay()
        {
            if (!_hud || _capturing || TitleMode) return false;
            if (_error != null || _resort == null || _photo || _help) return true;
            if (_toast != null && Time.unscaledTime < _toastUntil) return true;
            return Hud == null;
        }

        /// <summary>The IMGUI overlay's drawing (<see cref="ViewerOverlay"/> calls it from OnGUI).</summary>
        internal void DrawOverlay()
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
                : $"{_resort.Manifest.Site.Name} · {UnitFormat.SiteSize(_resort.Manifest.Site.SizeMetres, DisplayUnits.Current)} · {_status} · {_fps:F0} fps\n" +
                  $"{_resort.Manifest.Quality.OneLiner}\n{_resort.Manifest.Flora.OneLiner}";
            if (_resort != null && !_help && Hud != null) return;   // the HUD carries the rest, landmark names included; F1 shows the developer panel
            if (_resort != null && _help)
            {
                text += "\nWASD or arrows pan · Q/E rotate · R/F tilt · Wheel, +/− or PgUp/PgDn zoom · Middle-drag rotate · Right-drag pan · Shift faster · " +
                        "Home reset view · C free-fly (W/S fly, Q/E turn, R/F pitch, PgUp/PgDn rise and sink, right-drag look) · " +
                        "Map layers: Shift+1 snow, Shift+3 trees · Info layers: Shift+6 contours, Shift+7 slope angle, Shift+8 slope exposure, Shift+9 snow depth, Shift+0 snow conditions (later) · U feet or metres · H hide UI · P photo mode · F1 this panel · Esc menu · map data © OpenStreetMap contributors";
                float height = style.CalcHeight(new GUIContent(text), 820);
                GUI.Box(new Rect(20, 20, 820, height), text, style);
                DrawDeveloperPanel(new Rect(20, 28 + height, 820, 0));
            }
            else GUI.Box(new Rect(20, 20, 820, style.CalcHeight(new GUIContent(text), 820)), text, style);   // sized to the wrapped text
            GUI.backgroundColor = Color.white;
            if (Hud == null) DrawLandmarkLabels();   // with a HUD they are in its UI Toolkit panel
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
