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
        /// <summary>
        /// F1: the developer panel (status, data quality, every key, and the review switches).
        /// <c>-devpanel</c> starts with it open, for a capture or a hands-on look.
        /// </summary>
        bool _help;
        /// <summary>The overlay; off for benchmarks and lineups so captures show only the scene.</summary>
        bool _hud = true;
        /// <summary>H: hide all UI (0.4 S6).</summary>
        bool _ui = true;
        string _toast;
        float _toastUntil;
        bool _hudShown = true;
        float _nextReadout;
        /// <summary>The view opens paused at 10:30, unless -time or -light chose a time.</summary>
        const int OpeningSecond = 10 * 3600 + 30 * 60;
        FarTerrainShadow _farShadows;
        /// <summary>The IMGUI overlay, enabled only while it has something to draw (<see cref="NeedsOverlay"/>).</summary>
        ViewerOverlay _overlay;
        /// <summary>Landmark names in the HUD's UI Toolkit panel (no IMGUI, so nothing allocates per frame).</summary>
        MountainPlanner.UI.LandmarkLabelOverlay _landmarkLabels;
        // Lift, Area, run and landmark names placed together so none overlap (NT7); placed each LateUpdate.
        readonly MountainPlanner.UI.LabelPlacement _labelPlacement = new MountainPlanner.UI.LabelPlacement();
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
            PinTitleFov(false);
            StartTools();   // the title had none (P3-06)
        }

        /// <summary>
        /// The title always frames at the default 60° (owner, task P2-03), so it matches the loading picture whatever the
        /// field-of-view setting says; in the game the camera follows the setting again (ViewFov, task P2-05).
        /// </summary>
        void PinTitleFov(bool title)
        {
            if (Camera == null) return;
            Camera.FollowFovSetting = !title;
            var lens = Camera.GetComponent<UnityEngine.Camera>();
            if (lens != null) lens.fieldOfView = title ? ViewFov.Default : ViewFov.Vertical;
        }

        public static string DataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge");

        /// <summary>Where photo mode saves (0.4 S10): Pictures\Ski Area Design Challenge, or -photofolder's for test runs.</summary>
        public static string PhotoFolder
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-photofolder");
                return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1])
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Ski Area Design Challenge");
            }
        }

        async void Start()
        {
            // Task 4.26 BB23: an unattended run (batch mode, a GPU lane's job) whose start throws quits with an error
            // code; it used to wait forever (a bad -opensave held the render lane for 2 hours). Played by hand, the
            // exception goes to the log as before.
            try { await StartViewer(); }
            catch (Exception e) when (MountainPlanner.App.Diagnostics.UnattendedRun.Active && !(e is OperationCanceledException))
            {
                MountainPlanner.App.Diagnostics.UnattendedRun.FailSetup("the viewer's start threw", e);
            }
        }

        async System.Threading.Tasks.Task StartViewer()
        {
            Loading = new MountainPlanner.UI.Flow.LoadProgress();   // the loading box's stages (task P2-03.1)
            _overlay = gameObject.AddComponent<ViewerOverlay>();
            _overlay.Viewer = this;
            string[] startArgs = Environment.GetCommandLineArgs();
            // The graphics settings (task P2-05): the player's saved choices, or for this run only the preset that
            // -quality low|medium|high|ultra names (task 15; benchmarks), uncapped with V-Sync off. They must come
            // before -shadows (which edits the active URP asset) and before the mountain opens (terrain detail).
            ApplyGraphicsSettings(startArgs);
            Detail = QualityPresets.Options.Terrain;
            PinTitleFov(TitleMode);
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
                MountainPlanner.App.Diagnostics.UnattendedRun.FailSetup("no area to open");   // task 4.26 BB23
                return;
            }
            try
            {
                var progress = new Progress<OpenProgress>(p => { _status = p.Detail; _fraction = p.Fraction; });
                var assets = new ForestAssets { Trees = Trees, Cull = ForestCull, Shader = TreeShader, ImpostorShader = TreeImpostorShader, Cliff = CliffMaterial != null ? new Material(CliffMaterial) : null, Edge = EdgeMaterial };
                _resort = await ResortOpener.OpenAsync(folder, null, Detail, progress, destroyCancellationToken, TerrainMaterial, assets,
                                                       readOnly: BundledDemo.Contains(folder), load: Loading);   // the built-in demo is never written (task P2-03)
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
                if (TitleMode) foreach (var landmark in _landmarks) landmark.Line.enabled = false;   // a clean postcard behind the title
                _layers.Bind(_resort.Ground, _resort.EdgeMaterial, assets.Cliff, (float)_resort.Manifest.Crs.GridConvergenceDegrees);
                _layers.BindSnow(_resort.States);
                // -shelldebug (task 4.07 PR A3 repair): the snow shell painted flat and raised 2 m by plain material numbers,
                // which tells a shell that isn't drawn from one drawn with the wrong variant (MountainViewer.ShellCheck.cs).
                if (Array.IndexOf(args, "-shelldebug") >= 0 && _resort.Ground?.ShellMaterial != null)
                {
                    _resort.Ground.ShellMaterial.SetFloat("_ShellDebug", 1);
                    _resort.Ground.ShellMaterial.SetFloat("_ShellForce", 2);
                }
                // -shellfull (finding A3-7): the snow shell at the ground's own mesh detail, not half, to compare the facets.
                if (Array.IndexOf(args, "-shellfull") >= 0 && _resort.Ground?.ShellTiles != null)
                    foreach (var tile in _resort.Ground.ShellTiles)
                    {
                        var shell = TerrainTiles.ShellOf(tile);
                        if (shell != null) shell.heightmapPixelError = tile.heightmapPixelError;
                    }
                ReadShellPadArguments(args);   // -noshellpad, -flatpad (finding A3-6, MountainViewer.ShellPad.cs)
                StartCoroutine(WhenForestReady(() => _layers.BindForest(_resort.Root.GetComponent<ForestView>())));
                // -opensave <folder> (task 08 review): before the HUD starts the simulation, so a save's clock comes back as
                // it does from Load Area (finding B2c-5).
                OpenSaveForCapture(args);
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
                // Task 4.28: -plaingrass draws the grass as it was (one look whatever the land or the date), -moisturemap the
                // moisture map itself.
                // Task 4.29 PR B: -plaingrass also draws every trail mown; -trailgrass 10,50,100 sets the trails' look in turn.
                if (Array.IndexOf(args, "-plaingrass") >= 0) { GroundLayers.SetGrassVariation(false); _resort.States.PushTrailGrassParams(); }
                ReadTrailGrassArgs(args);
                _resort.States.ShowMoisture(Array.IndexOf(args, "-moisturemap") >= 0);
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
                if (Array.IndexOf(args, "-nosnow") >= 0 || Summer(args)) _layers.Set(MapLayers.Snow, false);
                if (Array.IndexOf(args, "-noforest") >= 0) _layers.Set(MapLayers.Trees, false);
                int view = Array.IndexOf(args, "-view");
                if (view >= 0 && view + 1 < args.Length && Camera != null)
                {
                    var v = args[view + 1].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    // -viewearth (task 4.07 PR A3 repair): the view stays on the earth whatever the snow, so pictures at
                    // different depths share one camera and only the snow moves.
                    Camera.OnEarth = Array.IndexOf(args, "-viewearth") >= 0;
                    Camera.Frame(new Vector3(v[0], _resort.Surface.HeightAt(v[0], v[1]), v[1]), v[2]);
                    Camera.SetAngles(v[3], v[4]);
                }
                // -fly x,z,height,yaw,pitch: a free-fly camera at a height above the ground (captures that look up at
                // something, such as a tower head, which -view's orbit round a ground point can't; task 07).
                int fly = Array.IndexOf(args, "-fly");
                if (fly >= 0 && fly + 1 < args.Length && Camera != null)
                {
                    var f = args[fly + 1].Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    Camera.FlyFrom(new Vector3(f[0], _resort.Surface.HeightAt(f[0], f[1]) + f[2], f[1]), f[3], f[4]);
                }
                int bench = Array.IndexOf(args, "-benchmark");
                if (bench >= 0 && bench + 1 < args.Length) StartCoroutine(RunBenchmark(args[bench + 1]));
                int hold = Array.IndexOf(args, "-holdview");
                if (hold >= 0 && hold + 1 < args.Length) StartCoroutine(RunHoldView(args[hold + 1]));
                int benchViews = Array.IndexOf(args, "-benchmark-views");
                if (benchViews >= 0 && benchViews + 1 < args.Length) StartCoroutine(RunViewBenchmark(args[benchViews + 1]));
                int movie = Array.IndexOf(args, "-pathmovie");
                if (movie >= 0 && movie + 1 < args.Length) StartCoroutine(RecordPathMovie(args[movie + 1]));
                if (Array.IndexOf(args, "-devpanel") >= 0) _help = true;   // task 4.12: open the panel for a capture
                int shot = Array.IndexOf(args, "-screenshot");
                if (shot >= 0 && shot + 1 < args.Length) StartCoroutine(CaptureAndQuit(args[shot + 1]));
                int lookShots = Array.IndexOf(args, "-lookshots");   // task 4.08 PR B: many snow-look pictures a run (MountainViewer.SnowTypes.cs)
                if (lookShots >= 0 && lookShots + 2 < args.Length) StartCoroutine(RunLookShots(args[lookShots + 1], args[lookShots + 2]));
                StartCoroutine(RunPhotoArguments(args));   // -photo, -photoshot (MountainViewer.Photo.cs)
                StartTerrainEdits(args);   // -testedit, -testbench, -editbench (MountainViewer.Edits.cs, task P3-05)
                StartTrailShots(args);     // -trailshots <folder> [-trailspot name] (MountainViewer.Trails.cs, task P3-08)
                StartTrailBench(args);     // -trailbench <out.json> (MountainViewer.Trails.cs, task P3-08)
                StartDesignBench(args);    // -designbench <out.json> with -drawnresort (MountainViewer.DrawnResort.cs, task P3-11)
                StartNetworkShots(args);   // -networkshots <folder> (MountainViewer.Network.cs, task P3-10)
                StartSectionShots(args);   // -sectionshots <folder> (MountainViewer.Sections.cs, task P3-10 B2)
                StartBrushRuns(args);      // -brushtour <folder> [-brushmovie], -brushbench <out.json> (MountainViewer.Brush.cs, task P3-15)
                StartGuestShots(args);     // -guestshot <folder> (MountainViewer.Guests.cs, task 4.12)
                StartWeatherShots(args);   // -weathershots <folder> (MountainViewer.WeatherReadout.cs, task 4.05)
                StartWeatherLook(args);    // -wxlook <look>, -wxshots <folder>, -wxdemo, bisect switches (MountainViewer.WeatherLook.cs, task 4.06)
                StartWaterShots(args);     // -watershots <folder> (MountainViewer.WaterShots.cs, task 4.14)
                StartSnowShots(args);      // -snowshots <folder> (MountainViewer.SnowShots.cs, task 4.15 PR B)
                StartLakeReport(args);     // -lakereport <folder> (MountainViewer.Lakes.cs, task 4.09)
                StartSnowLapse(args);      // -snowlapse <folder> [-snowdays N] (MountainViewer.SnowLapse.cs, task 4.07 PR A2)
                StartWhaleReport(args);    // -gunwhale: what became of it, once the snow is in (MountainViewer.GunWhale.cs)
                StartShellCheck(args);     // -shellcheck x,z[;x,z…] [-shellsteps] (MountainViewer.ShellCheck.cs, task 4.07 PR A3 repair)
                StartSimTo(args);          // -simto <MM-DD> (MountainViewer.SimTo.cs, task 4.07 PR A3)
                StartTrailSnowShots(args); // -trailsnowshots <folder> with -simto (MountainViewer.TrailSnowShots.cs, task 4.07 B1 part 2a)
                StartLineCheck(args);   // -linecheck <out.txt> (MountainViewer.LineCheck.cs, task 4.26 BB4)
                StartGroomShots(args);     // -groomshots <folder> with -simto [-groomers N] (MountainViewer.GroomShots.cs, task 4.25 PR A)
                StartPickBench(args);      // -pickbench <out.json> [-guests N] (MountainViewer.SkierBench.cs, task 4.13)
                StartSkierFigureShots(args);   // -skiershot <folder> [-guests N] (MountainViewer.SkierFigureShots.cs, task 4.22)
                StartCrowdShots(args);     // -crowdshot <folder> -crowdviews "x,z,d,yaw,pitch;..." [-guests N] (task 4.24)
                StartProofHold(args);      // -proofhold <out.json> [-guests N] [-skierproof lod|lod0] (task 4.13 PR C2)
                StartTypeCheck(args);      // -typecheck <out.txt> with -drawnresort (MountainViewer.TypeCheck.cs, task 4.26)
                StartTowerDragCheck(args); // -towerdragcheck <out.txt> with -drawnresort (MountainViewer.TowerDragCheck.cs, task 4.26 BB18)
                StartCoroutine(RunRideArguments(args));   // -ride <lift> [-rideshots <folder> | -ridebench <out.json>] (task 4.11 PR B)
                int clip = Array.IndexOf(args, "-clip");
                if (clip >= 0 && clip + 1 < args.Length) StartCoroutine(CaptureClipAndQuit(args[clip + 1]));
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                _error = "Couldn't open the area: " + e.Message;
                Loading.Fail();
                Debug.LogException(e);
                // Task 4.26 BB23: the opening or a capture switch's setup failed (-opensave, -view, -crowdviews...), so no
                // capture will run: an unattended run quits with an error code.
                MountainPlanner.App.Diagnostics.UnattendedRun.FailSetup(_error);
            }
        }

        /// <summary>-time HH:MM and -day &lt;day of year&gt;: the view time for captures (local standard time at the site).</summary>
        void ApplyTimeArguments(string[] args)
        {
            int time = Array.IndexOf(args, "-time"), day = Array.IndexOf(args, "-day");
            bool hasTime = time >= 0 && time + 1 < args.Length, hasDay = day >= 0 && day + 1 < args.Length;
            if (!hasTime && !hasDay && !Summer(args)) return;
            var now = Lighting.Clock.Now;
            int second = now.SecondOfDay, dayOfYear = Summer(args) ? MidJuly : now.DayOfYear;
            if (hasTime && TimeSpan.TryParse(args[time + 1], System.Globalization.CultureInfo.InvariantCulture, out var hm))
                second = (int)hm.TotalSeconds % ViewTime.SecondsPerDay;
            if (hasDay && int.TryParse(args[day + 1], out int d)) dayOfYear = Mathf.Clamp(d, 1, ViewTime.DaysInYear(now.Year));
            Lighting.SetTime(new ViewTime(now.Year, dayOfYear, second));
        }

        /// <summary>-season summer (task 07, SK4): the snow off and a mid-July sun, unless -day says otherwise.</summary>
        static bool Summer(string[] args)
        {
            int season = Array.IndexOf(args, "-season");
            return season >= 0 && season + 1 < args.Length && args[season + 1] == "summer";
        }

        const int MidJuly = 196;

        System.Collections.IEnumerator CaptureAndQuit(string path)
        {
            yield return WaitForCaptureReady();
            yield return ShellCheck("before the picture");   // -shellcheck: the drawn snow surface, measured (task 4.07 PR A3)
            var cam = Camera != null ? Camera.transform.position : Vector3.zero;
            Debug.Log($"[MountainViewer] camera {cam}, ground below {_resort.Surface.HeightAt(cam.x, cam.z):F0} m, target {(Camera != null ? Camera.Target : Vector3.zero)}, " +
                      $"active terrains {Terrain.activeTerrains.Length}, material {(TerrainMaterial != null ? TerrainMaterial.shader.name : "none")}, " +
                      $"time {(Lighting != null ? Lighting.Clock.Now.ToString() : "-")}, sun {(Lighting != null ? Lighting.CurrentLight.SunElevation : 0):F1}°");
            Debug.Log(BuildingLightsReport());
            LogSnowTypesOnScreen();   // -snowtypes: where each stripe is on this picture
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log($"[MountainViewer] Screenshot saved to {path}; {_fps:F0} fps");
            Application.Quit();
        }

        /// <summary>A capture's wait: the cover, any scripted edit, the live snow's first pass (seeded runs) and 90 frames to settle.</summary>
        System.Collections.IEnumerator WaitForCaptureReady()
        {
            while (!_resort.CoverReady.IsCompleted) yield return null;   // the cover, snowpack, contour labels and forest are in
            while (WorkBusy) yield return null;                           // and any -testbench or -styletile edit, drawn
            while (_simToRunning) yield return null;                      // and a -simto run, at its date
            for (float t = 0; !MoistureReady && t < 30; t += Time.unscaledDeltaTime) yield return null;   // and the ground's moisture (task 4.28)
            for (float t = 0; !TrailGrassReady && t < 30; t += Time.unscaledDeltaTime) yield return null;   // and the trails' grass (task 4.29 PR B)
            // Task 4.07 PR A3: a seeded capture waits (up to 180 s: a -styletile build re-runs the snow's inputs first) for the
            // live snow's first full pass, when the snow shell stands on it; it says so if it gave up.
            float waited = 0;
            bool LiveIn() => _resort.States == null || (_simSnowTerrainSent && _resort.States.FirstPassDone && _resort.States.LiftOn);
            bool wantLive = SnowSeedWanted || Array.IndexOf(Environment.GetCommandLineArgs(), "-simto") >= 0;
            for (; wantLive && !LiveIn() && waited < 180; waited += Time.unscaledDeltaTime)
                yield return null;
            if (wantLive && _resort.States != null)
            {
                var target = Camera != null ? Camera.Target : Vector3.zero;
                Debug.Log((LiveIn() ? $"[MountainViewer] live snow in after {waited:F0} s" : "[MountainViewer] live snow NOT in after 180 s")
                          + $" (inputs sent {_simSnowTerrainSent}, seed sent {_simSnowSent}, first pass {_resort.States.FirstPassDone}, lift {_resort.States.LiftScale});"
                          + $" depth at the target {SnowCursor.LiftAt(_resort.States, _resort.Ring, target.x, target.z):F2} m");
            }
            // Task 4.08 PR A: a -paintwhale capture waits (up to 120 s) for the paint to be sent and its tiles drawn.
            for (float t = 0; !PaintWhaleSettled && t < 120; t += Time.unscaledDeltaTime) yield return null;
            yield return ShowSnowTypes();   // task 4.08 PR B: -snowtypes, the types side by side (screen only)
            for (int i = 0; i < 90; i++) yield return null; // let LOD and shadows settle
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
            // An empty library still has the demo built into the game (task P2-03).
            return (demo ?? entries.FirstOrDefault() ?? BundledAreas.Scan(BundledDemo.Root).FirstOrDefault(e => e.Refusal.Length == 0))?.Folder;
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
                // While the Toolbox tray is open its tools take the letter keys (the key map; P3-06): the camera keeps the
                // arrows, + and −, Home and the mouse.
                if (Camera != null && Hud != null) Camera.LettersToTools = Hud.ToolboxOpen;
                HandleKeys(Keyboard.current);
                UpdateTerrainEdits(Keyboard.current);
                // A click on the mountain closes the HUD's dropdowns (the mockup).
                var click = Mouse.current;
                if (Hud != null && _resort != null && click != null && click.leftButton.wasPressedThisFrame && !Hud.IsPointerOverPanel(click.position.ReadValue())) Hud.ClickedMap();
                TickTools();   // the armed construction tool (MountainViewer.Tools.cs)
            }
            UpdateBuiltWork();   // built trails and lifts (MountainViewer.Built.cs)
            UpdateSkierHover();  // the dot under the pointer, and a click on one (task 4.13)
            UpdateLiftHover();   // the lift under the pointer glows (task 09, LT23)
            UpdateTrailHover();  // and a trail lights and opens its card (task P3-10, sections finding 4)
            UpdateSnowHover();   // then snowmaking: a gun, node, pump house or water line lights and opens its card (task 4.15)
            UpdateWaterHover();  // then water: a dam, pond, stream or lake lights and opens its card (task 4.14, LK-10)
            _resort?.States?.Sync();
            UpdateMoisture();   // task 4.28: the ground's moisture follows earthwork and ponds
            UpdateShellPad();
            StartSimulationIfReady();   // an opened save's clock, once its network is in (finding B2c-5)
            RunClock();
            UpdateWeather();   // task 4.06: the weather on screen (MountainViewer.Weather.cs)
            // The Auto theme follows this sun on every screen (task P2-01); nothing happens unless sunrise or sunset passed.
            if (Lighting != null) MountainPlanner.UI.UiPreferences.SetDaylight(Lighting.CurrentLight.SunElevation > 0);
            UpdateHud();
            UpdatePhoto();
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
        /// The view keys (docs/plans/controls-key-map.md; rebindable, <see cref="KeyBindings"/>). The HUD owns T, Tab, U, Space, 1–4, Ctrl+S and Enter;
        /// while its Toolbox tray is open (<see cref="ViewCamera.LettersToTools"/>) letters are tool keys, so P waits until
        /// it closes; the camera's keys (C, Home) work at all times (TL19).
        /// </summary>
        void HandleKeys(Keyboard keys)
        {
            // A click on the map lets go of a text field, as anywhere else would: the keys go back to the game.
            var mouse = Mouse.current;
            if (mouse != null && Hud != null && Hud.Typing
                && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame)
                && !PointerOverUi(mouse.position.ReadValue()))
                Hud.ReleaseKeyboard();
            if (keys == null || _scriptedRun) return;   // a scripted capture or benchmark ignores stray keys (task 4.13)
            bool letters = Camera == null || !Camera.LettersToTools;
            bool flowKeys = (FlowHasKeyboard?.Invoke() ?? false) || FlowTookEscapeFrame == Time.frameCount;
            if (keys.escapeKey.wasPressedThisFrame && !flowKeys)
            {
                // Esc backs out one step: photo mode, then the HUD's window, dropdown or panel, then a HUD control lets go
                // of the keyboard; with nothing left to close it opens the menu (Quit is in it, 0.4 S7).
                if (_riding) EndRide(false);   // a ride (task 4.11 PR B) leaves to the camera as it was
                else if (_photo && _photoBar != null && _photoBar.ReleaseKeyboard()) { }   // the photo bar lets go of the keys first
                else if (_photo) SetPhoto(false);   // photo mode (MountainViewer.Photo.cs) puts everything back
                else if (Hud != null && _resort != null && Hud.ModalOpen) Hud.BackOut();
                else if (Hud != null && _resort != null && Hud.ReleaseKeyboard()) { }
                else if (_resort != null && ClearNetworkPick()) { }   // the Network view's pick and its card (task P3-10, NT11)
                else if (Hud != null && _resort != null && Hud.CloseSkierCardIfOpen()) { }   // the skier card (task 4.13)
                else if (Hud != null && _resort != null && Hud.CloseTrailCardIfOpen()) { }   // a trail's focus card (task 08)
                else if (Hud != null && _resort != null && Hud.CloseLiftCardIfOpen()) { }    // a lift's focus card (task 09)
                else if (Hud != null && _resort != null && Hud.CloseWaterCardIfOpen()) { }   // a water card (task 4.14)
                else if (Hud != null && _resort != null && Hud.CloseGroomCardIfOpen()) { }   // a groomer card (task 4.25 PR B)
                else if (_resort != null && EscapeTool()) { }   // the armed tool backs out a step (CF-07, P3-06)
                else if (Hud != null && _resort != null && Hud.BackOut()) { }
                else if (Hud != null && _resort != null) Hud.ToggleMenu();
                else Application.Quit();
            }
            if (KeyBindings.Pressed(keys, GameAction.DeveloperPanel)) _help = !_help;
            if (_resort == null) return;
            if (HandleRideKeys(keys, letters)) return;   // riding a lift: only the ride's and the clock's keys
            if (_photo)
            {
                if (!UiHasKeyboard()) HandlePhotoKeys(keys, letters);
                return;
            }
            // Analysis (Tab by default) whenever no window has the keyboard (the HUD keeps Tab from moving between its controls).
            if (Hud != null && KeyBindings.Pressed(keys, GameAction.Analysis, letters) && !Hud.ModalOpen && !flowKeys && !(FlowHasKeyboard?.Invoke() ?? false)) Hud.ToggleAnalysis();
            if (UiHasKeyboard()) return;   // the menu, a dialog or a focused HUD control has the keys (task P2-01)
            // The player's keys (Settings › Controls; the defaults are the key map's). While the tray is open,
            // letter keys are tools, except the Toolbox's own key, which closes it.
            if (Hud != null && KeyBindings.Pressed(keys, GameAction.Toolbox)) Hud.ToggleToolbox();
            HandleToolKeys(keys);   // the tray's letters, the tool window's Tab, and undo (MountainViewer.Tools.cs)
            // The bar's clock (task 4.03): pause, and 1×, 2×, 4×, 8× (each also starts the clock, as the bar's buttons do).
            if (_sim != null && KeyBindings.Pressed(keys, GameAction.Pause, letters)) SendSim(SimCommandKind.TogglePause);
            if (_sim != null)
            {
                if (KeyBindings.Pressed(keys, GameAction.Speed1, letters)) SendSim(SimCommandKind.SetSpeed, 1);
                if (KeyBindings.Pressed(keys, GameAction.Speed2, letters)) SendSim(SimCommandKind.SetSpeed, 2);
                if (KeyBindings.Pressed(keys, GameAction.Speed3, letters)) SendSim(SimCommandKind.SetSpeed, 3);
                if (KeyBindings.Pressed(keys, GameAction.Speed4, letters)) SendSim(SimCommandKind.SetSpeed, 4);
            }
            if (KeyBindings.Pressed(keys, GameAction.HideUi, letters)) _ui = !_ui;
            if (KeyBindings.Pressed(keys, GameAction.PhotoMode, letters)) SetPhoto(true);
            if (KeyBindings.Pressed(keys, GameAction.FreeFly) && Camera != null) Camera.ToggleMode();
            if (KeyBindings.Pressed(keys, GameAction.Units, letters)) DisplayUnits.Toggle();   // feet or metres (task 12b.2)
            if (KeyBindings.Pressed(keys, GameAction.ResetView)) HomeView();
            // Map layers.
            if (KeyBindings.Pressed(keys, GameAction.LayerSnow, letters)) ToggleLayer(MapLayers.Snow);
            if (KeyBindings.Pressed(keys, GameAction.LayerTrees, letters)) ToggleLayer(MapLayers.Trees);
            if (KeyBindings.Pressed(keys, GameAction.LayerLifts, letters)) ToggleLayer(MapLayers.Lifts);
            if (KeyBindings.Pressed(keys, GameAction.LayerTrails, letters)) ToggleLayer(MapLayers.Trails);   // task 08
            // Info layers: Contours with anything; the other four take turns.
            if (KeyBindings.Pressed(keys, GameAction.InfoContours, letters)) ToggleLayer(MapLayers.Contours);
            if (KeyBindings.Pressed(keys, GameAction.InfoSlope, letters)) ToggleLayer(MapLayers.SlopeAngle);
            if (KeyBindings.Pressed(keys, GameAction.InfoExposure, letters)) ToggleLayer(MapLayers.Exposure);
            if (KeyBindings.Pressed(keys, GameAction.InfoDepth, letters)) ToggleLayer(MapLayers.SnowDepth);
            if (KeyBindings.Pressed(keys, GameAction.InfoConditions, letters)) ToggleLayer(MapLayers.SnowConditions);   // task 4.07 PR A2
            if (KeyBindings.Pressed(keys, GameAction.InfoTraffic, letters)) ToggleLayer(MapLayers.Traffic);   // task 4.13 PR C
        }

        /// <summary>
        /// The keys, the HUD's layer rows and the F1 panel all switch layers here (<see cref="MapLayers"/>): the change
        /// shows in this frame.
        /// </summary>
        void ToggleLayer(string layer)
        {
            if (_resort == null) return;
            _layers.Toggle(layer);
        }

        /// <summary>For UI captures (AppFlow.UiCapture): a layer on or off, and the pointer at a fraction of the screen (null: the mouse).</summary>
        internal void SetLayerForCapture(string layer, bool on) { if (_resort != null && _layers.IsOn(layer) != on) ToggleLayer(layer); }

        internal void SetPointerForCapture(Vector2? fraction) => _pointer = fraction;

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
                // The bar's clock (task 4.03): the simulation opens paused on October 1 at the resort's opening, and a
                // player's view shows that sun. Captures and benchmarks keep their own scenario's 10:30 (and -time or -light
                // their time) until the clock runs; the title stands still in a low afternoon sun at 15:30 (task P2-03).
                var args = Environment.GetCommandLineArgs();
                bool chosen = Array.IndexOf(args, "-time") >= 0 || Array.IndexOf(args, "-light") >= 0;
                if (!chosen)
                    Lighting.SetTime(Lighting.Clock.Now.WithSecondOfDay(TitleMode ? TitleSecondOfDay : OpeningSecond));
                if (!TitleMode)
                {
                    StartSimulationWhenReady(sunFromSimulation: !chosen && !Flow.AppFlow.CaptureRun);   // finding B2c-5
                    Hud.PauseChosen += () => SendSim(SimCommandKind.TogglePause);
                    Hud.SpeedChosen += speed => SendSim(SimCommandKind.SetSpeed, speed);
                }
            }
            Hud.UnitsChosen += DisplayUnits.Toggle;
            WirePhoto();
            ViewCamera.PointerBlocked = PointerOverUi;
            ViewCamera.KeysBlocked = UiHasKeyboard;
            _contourLabels = new MountainPlanner.UI.ContourLabelOverlay(Hud.ContourLabelLayer);
            _landmarkLabels = new MountainPlanner.UI.LandmarkLabelOverlay(Hud.ContourLabelLayer, _labelPlacement, MountainPlanner.UI.LabelPlacement.Landmarks);
            var named = new System.Collections.Generic.List<(string, Vector3)>(_landmarks.Count);
            foreach (var landmark in _landmarks) named.Add((landmark.Name, landmark.LabelAt));
            _landmarkLabels.SetLandmarks(named);
            DisplayUnits.Changed += OnUnitsChanged;
            StartTools();   // the Toolbox's tools (MountainViewer.Tools.cs)
        }

        /// <summary>New units: the contour interval and snow-depth stops in the shader, and the labels' numbers.</summary>
        void OnUnitsChanged()
        {
            _layers.Apply();
            _contourLabelSet = null;   // UpdateContourLabels picks the set for the new units
        }

        /// <summary>The package on screen, or null while none is open.</summary>
        public string OpenPackage => _resort?.PackageFolder;

        /// <summary>True once the open mountain is fully in: terrain, cover, snowpack and forest.</summary>
        public bool Ready => _resort != null && _resort.CoverReady.IsCompleted;
        /// <summary>How far opening the terrain has got, 0–1 (the app flow's cover shows it).</summary>
        public float OpenFraction => _resort != null ? 1 : _fraction;
        /// <summary>
        /// Opening this viewer's mountain, stage by stage (task P2-03.1): the loading box's bar and Details list. Null until
        /// the viewer starts; one per viewer, so an opener still running for a scene that has gone writes only into its own.
        /// </summary>
        public MountainPlanner.UI.Flow.LoadProgress Loading { get; private set; }
        /// <summary>Why the mountain couldn't open (or there's none), else null.</summary>
        public string OpenError => _error;

        /// <summary>Set by the app flow: its cover is up over the opening mountain, so the viewer's own progress text stays off (task P2-03).</summary>
        public static Func<bool> CoverUp;

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
            if (_resort == null || _resort.CacheLease != null || BundledDemo.Contains(_resort.PackageFolder)) return;   // the built-in demo takes no lease
            try { _resort.CacheLease = TerrainCache.Hold(_resort.PackageFolder); }
            catch (IOException e) { Debug.LogWarning($"[MountainViewer] Couldn't hold the cache again: {e.Message}"); }
        }

        void OnDestroy()
        {
            LogChairCost();
            LogSkierTiming();
            LogSkierProof();
            _weatherView.Dispose();   // task 4.06: the cloud field and the precipitation's material
            _resort?.CacheLease?.Dispose();   // the next scene's viewer holds its own
            DisplayUnits.Changed -= OnUnitsChanged;
            ReleasePhoto();
            StopTools();
            ReleaseTerrainEdits();
            if (ViewCamera.KeysBlocked == (Func<bool>)UiHasKeyboard) ViewCamera.KeysBlocked = null;
            ReleaseSettings();
            StopSimulation();
            _skiers?.Dispose();
            _groomerDots?.Dispose(); _groomerDots = null;
            DisposeBuildingLights();
            _skiers = null;
            _figures?.Dispose();
            _figures = null;
            _figuresTried = false;
            _riders?.Dispose();
            _riders = null;
        }

        /// <summary>
        /// Set by the app flow while one of its windows has the keyboard (Settings, Credits, a dialog, the picker) or
        /// it took this frame's Esc: the viewer's keys and Esc wait (task P2-01).
        /// </summary>
        public static Func<bool> FlowHasKeyboard;
        /// <summary>The frame in which the app flow used Esc (closing a dialog, minimising the download card).</summary>
        public static int FlowTookEscapeFrame = -1;

        bool UiHasKeyboard() => (Hud != null && Hud.HasKeyboard) || (_photoBar != null && _photoBar.HasKeyboard) || (FlowHasKeyboard?.Invoke() ?? false);

        /// <summary>The pointer is over the HUD or the photo bar: the camera leaves clicks and drags there alone.</summary>
        bool PointerOverUi(Vector2 screen) => (Hud != null && Hud.IsPointerOverPanel(screen)) || (_photoBar != null && _photoBar.IsPointerOver(screen));

        /// <summary>Moves the contour labels with the camera, after it has moved this frame.</summary>
        void LateUpdate()
        {
            TickRide();     // a ride's seat moves the camera, from this frame's rope time (task 4.11 PR B)
            TimedTickSkiers();   // every dot placed and drawn at the rope clock's tick (task 4.13)
            TickGroomers();      // the groomers' dots, from the night's plan (task 4.25, GO-13)
            TickBuildingLights();   // the buildings' lamps and windows at night, and their halos (task 4.50 slice 3)
            DrawChairs();   // after the camera has moved, for its LODs
            LogHeightReads();
            if (_contourLabels == null || _resort == null) return;
            _landmarkLabels?.Update(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null, _resort?.Surface, _hudShown && !_photo && (Hud == null || Hud.LabelsOn));
            TickTrailLayer(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null);   // task 08
            TickNetwork(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null);      // task P3-10
            TickSnowView(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null);     // task 4.15
            TickTraffic(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null);      // task 4.13 PR C (SN-31)
            TickCatchment(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null);    // task P3-10, NT16
            TickGroomMap(Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null);     // task 4.25 PR B
            _labelPlacement.Place();   // every set's names at once, none overlapping (NT7)
            var set = _resort.ContourLabels[(int)DisplayUnits.Current];
            if (!ReferenceEquals(set, _contourLabelSet))
            {
                _contourLabelSet = set;
                var labelWatch = System.Diagnostics.Stopwatch.StartNew();
                _contourLabels.SetLabels(set, DisplayUnits.Current);
                if (_brushBenching) Debug.Log($"[BrushBench] frame {Time.frameCount}: contour labels set again, {labelWatch.Elapsed.TotalMilliseconds:F1} ms");
            }
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            _contourLabels.Update(cam, _resort.Surface, _layers.ContoursOn && _hudShown);
        }

        /// <summary>The slope (a grade in percent) and the way it faces (degrees from true north) at a point, over 4 m as the shader measures.</summary>
        (float Percent, float Bearing) SlopeAt(float x, float z)
        {
            float east = (ReadoutHeight(x + 2, z) - ReadoutHeight(x - 2, z)) / 4, north = (ReadoutHeight(x, z + 2) - ReadoutHeight(x, z - 2)) / 4;
            float bearing = Mathf.Atan2(-east, -north) * Mathf.Rad2Deg - (float)_resort.Manifest.Crs.GridConvergenceDegrees;
            return ((float)SlopeBands.PercentFromRise(Mathf.Sqrt(east * east + north * north)), bearing);
        }

        /// <summary>The snow's depth at a point through the screen's curved filter (SN-29 v2: as the picture draws it).</summary>
        float SnowDepthAt(float x, float z) => SnowCursor.DepthAt(_resort.States, _resort.Ring, x, z);

        /// <summary>Shows or hides the HUD and refreshes it ten times a second (0.4 §8: bounded, no per-frame allocation).</summary>
        void UpdateHud()
        {
            if (Hud == null) return;
            bool show = _resort != null && _hud && _ui && !_photo && !_riding && !TitleMode;
            if (show != _hudShown) Hud.SetVisible(_hudShown = show);
            if (!show || Time.unscaledTime < _nextReadout || Camera == null) return;
            _nextReadout = Time.unscaledTime + 0.1f;
            foreach (string id in MapLayers.MapIds) Hud.SetLayer(id, _layers.IsOn(id));
            foreach (string id in MapLayers.InfoIds) Hud.SetLayer(id, _layers.IsOn(id));
            Hud.SetLegend(_layers.InfoLayerId, _layers.ContoursOn);
            SetHudClock();
            UpdateLiftLive();
            UpdateGroomingTab();   // task 4.25 PR B
            UpdateWaterLive();
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            var mouse = Mouse.current;
            float elevation = float.NaN, slope = float.NaN, bearing = 0, snow = float.NaN;
            int snowType = -1, quality = 0, age = 0, density = 0; float water = 0;
            string lakeLine = null;
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
                    elevation = ReadoutHeight(g.x, g.z);   // the edited float32 heights (SH-02, task P3-05)
                    // What the info layer that's on reads under the pointer (task 12b.2).
                    if (!float.IsNaN(elevation) && (info == MapLayers.SlopeAngle || info == MapLayers.Exposure)) (slope, bearing) = SlopeAt(g.x, g.z);
                    else if (!float.IsNaN(elevation) && (info == MapLayers.SnowDepth || info == MapLayers.SnowConditions)
                             && LakeUnder(g.x, g.z, out long lakeKey, out int lakeIndex))
                    {
                        // Task 4.09 PR B: over a lake or pond, its own ice and snow (the land field's snow isn't on the ice). Task 4.07
                        // PR A2: under Snow conditions too, where the land field reads bare over water (SN-25 sends what falls to the lake).
                        var units = DisplayUnits.Current;
                        lakeKey ^= (long)units << 62;
                        if (lakeKey != _lakeShownKey || _lakeShownLine == null)
                        {
                            _lakeShownKey = lakeKey;
                            _lakeShownLine = LakeLine(lakeIndex, _lakeMap[LakeCell(g.x, g.z)] - 1, units);
                        }
                        lakeLine = _lakeShownLine;
                    }
                    else if (!float.IsNaN(elevation) && info == MapLayers.SnowDepth) snow = SnowDepthAt(g.x, g.z);
                    else if (!float.IsNaN(elevation) && info == MapLayers.SnowConditions)
                    {
                        // Task 4.07 PR A2: the cell's surface, the depth as drawn.
                        snow = SnowDepthAt(g.x, g.z);
                        (snowType, quality, age, density) = SnowCursor.ConditionsAt(_resort.States, _resort.Ring, g.x, g.z);
                        if (snowType > 0 && snow < 0.0025f) snowType = 0;   // under a tenth of an inch reads bare
                        water = snow * density / 1000f;   // depth is water ÷ density (SN-08 v2), so the drawn depth × the cell's density
                    }
                }
            }
            Hud.SetElevation(elevation);
            Hud.SetInfoReadout(info, slope, bearing, snow, snowType, quality, age, water, lakeLine);
            if (info == MapLayers.Traffic) TrafficReadout(cam, pointer);   // task 4.13 PR C: the run or lift under the pointer
        }

        ForestRenderer Forest => _resort?.Root != null ? _resort.Root.GetComponent<ForestView>()?.Renderer : null;

        /// <summary>
        /// One look on every lake, through the task 10 API: an override that holds (task 4.09 PR B) until the panel's "From the
        /// simulation" clears it, and that bodies bound later take too.
        /// </summary>
        void SetLakes(WaterSurfaceState state) =>
            SetLakeOverride(state == WaterSurfaceState.SnowCoveredIce ? WaterSurface.IterationOne : new WaterSurface(state, 0.4f, 0));

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

        /// <summary>The title's time of day (owner, task P2-03): 15:30 local, a low warm sun with long shadows.</summary>
        public const int TitleSecondOfDay = 15 * 3600 + 30 * 60;

        /// <summary>The Home view's heading (degrees clockwise from grid north): looking north-northwest.</summary>
        public const float HomeYaw = 330f;

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
            if (!_hud || TitleMode) return false;
            if (_error == null && _resort == null && (CoverUp?.Invoke() ?? false)) return false;   // the flow's cover says it
            if (_error != null || _resort == null || _help) return true;
            if (_toast != null && Time.unscaledTime < _toastUntil) return true;
            if (WeatherReadoutAlways && _sim != null) return true;   // task 4.05: -weatherreadout (a dev switch; it allocates)
            return Hud == null;
        }

        /// <summary>The IMGUI overlay's drawing (<see cref="ViewerOverlay"/> calls it from OnGUI).</summary>
        internal void DrawOverlay()
        {
            if (!_hud || TitleMode) return;
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
            string text = _resort == null
                ? $"{_status}\n[{new string('#', (int)(_fraction * 30)).PadRight(30, '.')}] {_fraction * 100:F0}%"
                : $"{_resort.Manifest.Site.Name} · {UnitFormat.SiteSize(_resort.Manifest.Site.SizeMetres, DisplayUnits.Current)} · {_status} · {_fps:F0} fps\n" +
                  $"{_resort.Manifest.Quality.OneLiner}\n{_resort.Manifest.Flora.OneLiner}";
            DrawWeatherReadout();   // task 4.05: -weatherreadout, over the HUD or not
            if (_resort != null && !_help && Hud != null) return;   // the HUD carries the rest, landmark names included; F1 shows the developer panel
            if (_resort != null && _help)
            {
                text += "\nWASD or arrows pan · Q/E rotate · R/F tilt · Wheel, +/− or PgUp/PgDn zoom · Middle-drag rotate · Right-drag pan · Shift faster · " +
                        "Home reset view · C free-fly (W/S fly, Q/E turn, R/F pitch, PgUp/PgDn rise and sink, right-drag look) · " +
                        "Map layers: Shift+1 snow, Shift+3 trees · Info layers: Shift+6 contours, Shift+7 slope angle, Shift+8 slope exposure, Shift+9 snow depth, Shift+0 snow conditions · U feet or metres · H hide UI · P photo mode · F1 this panel · Esc menu · map data © OpenStreetMap contributors";
                // As wide as the screen allows (the owner's is 2,560 px, a laptop's 1,366): the panel's sentences wrap
                // inside it, so a wider screen simply reads in fewer lines.
                float wide = Mathf.Clamp(Screen.width - 40, 520, 1440);
                float height = style.CalcHeight(new GUIContent(text), wide);
                GUI.Box(new Rect(20, 20, wide, height), text, style);
                DrawDeveloperPanel(new Rect(20, 28 + height, wide, 0));
            }
            else GUI.Box(new Rect(20, 20, 820, style.CalcHeight(new GUIContent(text), 820)), text, style);   // sized to the wrapped text
            GUI.backgroundColor = Color.white;
            if (Hud == null) DrawLandmarkLabels();   // with a HUD they are in its UI Toolkit panel
        }

        /// <summary>
        /// The review switches that left the keyboard (owner, 2026-10-01): snow, trees, wind, light and time, haze,
        /// lakes, distant shadows and saved views. Plain IMGUI, developer-only, until the settings window (S8).
        ///
        /// The rows are laid out twice: once to measure them, which draws nothing, so the box is exactly as tall as
        /// they need, and once to draw them inside it. Each row's sentence wraps inside the box and pushes the rows
        /// below it down, so no line runs over its neighbour or off the edge, however long the sentence and however
        /// narrow the screen (owner, 2026-11-09: "the text still goes off the dashboard").
        /// </summary>
        void DrawDeveloperPanel(Rect at)
        {
            float height = PanelRows(at, draw: false);
            GUI.Box(new Rect(at.x, at.y, at.width, height), GUIContent.none);
            PanelRows(at, draw: true);
        }

        GUIStyle _panelCaption, _panelNote;

        /// <summary>The panel's rows; returns the height they need, the padding included. <paramref name="draw"/> off measures.</summary>
        float PanelRows(Rect at, bool draw)
        {
            const float row = 30, label = 130;
            float y = at.y + 8, x0 = at.x + 10 + label, w = 110;
            if (_panelCaption == null)
            {
                _panelCaption = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                _panelCaption.normal.textColor = Color.white;
                _panelNote = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
                _panelNote.normal.textColor = Color.white;
            }
            if (draw) GUI.backgroundColor = Color.white;
            bool Button(int column, string text) => draw && GUI.Button(new Rect(x0 + column * (w + 6), y, w, row - 4), text);
            void Caption(string text) { if (draw) GUI.Label(new Rect(at.x + 10, y, label, row), text, _panelCaption); }
            // A row's note: what it says, to the right of however many buttons it has, wrapped to the box. It returns
            // the height it needs past its own row, which the caller adds to y, so the rows below move down.
            float Note(float atY, int afterColumns, string text)
            {
                float nx = x0 + afterColumns * (w + 6);
                float nw = Mathf.Max(200f, at.xMax - 10 - nx);
                float h = _panelNote.CalcHeight(new GUIContent(text ?? ""), nw);
                if (draw) GUI.Label(new Rect(nx, atY + 4, nw, h), text, _panelNote);
                return Mathf.Max(0f, h - (row - 8));
            }

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
                if (draw)
                {
                    float hours = GUI.HorizontalSlider(new Rect(x0, y + 8, 4 * w + 18, row), now.SecondOfDay / 3600f, 0, 23.99f);
                    int second = Mathf.Clamp((int)(hours * 3600), 0, ViewTime.SecondsPerDay - 1);
                    if (second / 60 != now.SecondOfDay / 60) Lighting.SetTime(now.WithSecondOfDay(second));
                }
                y += row;

                Caption($"Day {now.DayOfYear} of {now.Year}");
                if (draw)
                {
                    float dayOfYear = GUI.HorizontalSlider(new Rect(x0, y + 8, 4 * w + 18, row), now.DayOfYear, 1, ViewTime.DaysInYear(now.Year));
                    if ((int)dayOfYear != now.DayOfYear) Lighting.SetTime(new ViewTime(now.Year, (int)dayOfYear, now.SecondOfDay));
                }
                y += row;
            }

            Caption("Lakes");
            // Task 4.09 PR B: the "●" marks the override, or "From the simulation" when each lake follows its own state.
            var lakeOverride = _resort.States?.Water.Override;
            bool overridden = lakeOverride.HasValue;
            var lakes = overridden ? lakeOverride.Value.State : (WaterSurfaceState)(-1);
            if (Button(0, (overridden && lakes == WaterSurfaceState.SnowCoveredIce ? "● " : "") + "Snow on ice")) SetLakes(WaterSurfaceState.SnowCoveredIce);
            if (Button(1, (overridden && lakes == WaterSurfaceState.Ice ? "● " : "") + "Bare ice")) SetLakes(WaterSurfaceState.Ice);
            if (Button(2, (overridden && lakes == WaterSurfaceState.OpenWater ? "● " : "") + "Open water")) SetLakes(WaterSurfaceState.OpenWater);
            y += row;
            if (Button(0, (overridden ? "" : "● ") + "From the sim")) SetLakeOverride(null);
            y += row;

            Caption("Shadows");
            if (_farShadows != null && Button(0, $"Distant: {(_farShadows.Enabled ? "on" : "off")}")) _farShadows.SetEnabled(!_farShadows.Enabled);
            if (Camera != null && Button(1, Camera.Current == ViewCamera.Mode.Orbit ? "Camera: orbit" : "Camera: free-fly")) Camera.ToggleMode();
            y += row;

            Caption("Saved views");
            if (Button(0, "Home")) HomeView();
            if (_landmarks.Count > 0 && Button(1, _landmarks[0].Name.Length > 14 ? _landmarks[0].Name.Substring(0, 13) + "…" : _landmarks[0].Name)) FlyToLandmark();

            if (_sim != null)
            {
                y += row;
                DrawSimulationRows(ref y, row, x0, w, Caption, Button, Note);
            }

            if (SampleDesignChosen != null)   // task P3-04: something to save before the drawing tools exist
            {
                y += row;
                Caption("Design");
                if (draw && GUI.Button(new Rect(x0, y, 2 * w + 6, row - 4), "Add sample design")) SampleDesignChosen();
            }
            return y - at.y + row + 8;
        }

        /// <summary>Set by the app flow (task P3-04): the developer panel's Add sample design, a lift and a planned trail.</summary>
        public static Action SampleDesignChosen;

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
