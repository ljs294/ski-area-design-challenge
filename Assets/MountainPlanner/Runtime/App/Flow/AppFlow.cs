using System;
using System.IO;
using System.Linq;
using MountainPlanner.App.Picker;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Flow;
using MountainPlanner.UI.Picker;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// The game's screen flow (task 14; 0.4 §3): title → New Area → download → quality card → the mountain,
    /// and title → Load Area → open. It lives for the whole session (downloads keep running across scene
    /// reloads) and drives the Mountain Viewer scene through its hooks: <see cref="MountainViewer.TitleMode"/>
    /// shows the demo mountain behind the signpost, and <see cref="MountainViewer.RequestedPackage"/> plus a
    /// scene reload opens a chosen mountain from disk, with no network.
    ///
    /// It starts itself before the first scene loads, unless a capture, benchmark or explicit mountain was
    /// asked for on the command line (those keep the old straight-to-the-mountain start), or the run is in
    /// batch mode without -title. -data &lt;folder&gt; points the whole flow at another library (demos, tests);
    /// -offline turns the network off for the whole session.
    /// </summary>
    public sealed partial class AppFlow : MonoBehaviour, IFlowHost
    {
        public const string ResourceFolder = "MountainPlannerFlow/";

        /// <summary>Command-line switches that mean "no title screen": captures, benchmarks and explicit mountains.</summary>
        static readonly string[] SkipTitle =
        {
            "-notitle", "-package", "-site", "-screenshot", "-benchmark", "-benchmark-views", "-pathmovie", "-lineup", "-lodtransitions", "-view", "-landmark", "-clip",
        };

        public static AppFlow Instance { get; private set; }

        public string DataRoot { get; private set; }
        public FlowController Controller { get; private set; }
        public DownloadService Downloads { get; private set; }
        public FlowScreens Screens { get; private set; }
        /// <summary>Task 13's site picker (S3), on its own document above the flow screens.</summary>
        public SitePicker Picker { get; private set; }

        MountainViewer _viewer;
        FlowScreen _afterTitle = FlowScreen.Title;
        LibrarySort _sort = LibrarySort.LastOpened;
        LibraryMode _mode = LibraryMode.Load;
        LibraryViewModel _library;
        /// <summary>The last scan's areas; the background measure fills in their sizes (task P2-04).</summary>
        System.Collections.Generic.List<LibraryEntry> _entries = new System.Collections.Generic.List<LibraryEntry>(), _newer = new System.Collections.Generic.List<LibraryEntry>();
        System.Collections.Generic.List<PendingDownload> _pending = new System.Collections.Generic.List<PendingDownload>();
        /// <summary>Sizes measured so far, by folder: a redraw shows them at once while a fresh measure runs.</summary>
        readonly System.Collections.Generic.Dictionary<string, DiskUse> _sizes = new System.Collections.Generic.Dictionary<string, DiskUse>(StringComparer.OrdinalIgnoreCase);
        int _measureRun;
        /// <summary>Bytes in the library's ".trash-" folders (deletes cut short): Free space offers them too.</summary>
        long _leftovers;
        bool _freeing;
        const string SortPref = "MountainPlanner.LibrarySort";
        /// <summary>Frames since a viewer scene loaded; RequestedPackage is cleared at 3, once the viewer has read it.</summary>
        int _framesSinceLoad = 3;
        string _viewerScene;
        /// <summary>The title's camera path over the mountain behind it, and how far along it is (task P2-03).</summary>
        TitleDrift _drift;
        float _driftSeconds;
        /// <summary>Set by -titlemovie: the capture moves the drift's clock itself, a frame at a time.</summary>
        bool _driftHeld;
        /// <summary>Logged once: seconds from launch until the title's mountain is fully in (the ≤10 s acceptance check).</summary>
        static bool _readyLogged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (Application.isBatchMode && Array.IndexOf(args, "-title") < 0) return;
            if (SkipTitle.Any(a => Array.IndexOf(args, a) >= 0)) return;
            // -offline: every network call fails, as with the cable out (the acceptance check for opening offline).
            if (Array.IndexOf(args, "-offline") >= 0) MountainPlanner.Acquisition.IO.Http.NetworkDisabled = true;
            Create(DataRootFrom(args), new PipelineDownloader(DataRootFrom(args)));
        }

        /// <summary>Builds the flow object (tests call this with a scratch library and a fake downloader).</summary>
        public static AppFlow Create(string dataRoot, ISiteDownloader downloader)
        {
            if (Instance != null) return Instance;
            var go = new GameObject("App flow");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = UI.UiPanels.Runtime(Resources.Load<PanelSettings>(ResourceFolder + "FlowPanel"));
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>(ResourceFolder + "Flow");
            document.sortingOrder = 10;
            var screens = go.AddComponent<FlowScreens>();
            screens.Document = document;
            var flow = go.AddComponent<AppFlow>();
            flow.DataRoot = dataRoot;
            flow.Screens = screens;
            flow.Downloads = new DownloadService(dataRoot, downloader, () => DownloadService.UtcStamp(DateTime.UtcNow));
            flow.Controller = new FlowController(flow);
            flow.Picker = CreatePicker();
            Instance = flow;
            // The camera ignores the pointer over the flow's panels (download card and pill, quality card, dialogs) and the picker.
            ViewCamera.OverlayBlocked = flow.PointerOverFlow;
            MountainViewer.FlowHasKeyboard = flow.FlowHasKeyboard;
            MountainViewer.TitleMode = true;
            MountainViewer.RequestedPackage = flow.TitleBackground();
            go.SetActive(true);
            return flow;
        }

        /// <summary>Its own root object, active from the start: a child of the inactive flow object would never wake up.</summary>
        static SitePicker CreatePicker()
        {
            var assets = Resources.Load<FlowAssets>(ResourceFolder + "FlowAssets");
            if (assets == null || assets.PickerTree == null || assets.PickerPanel == null)
            {
                Debug.LogError("[AppFlow] The site picker's assets are missing (Resources/MountainPlannerFlow/FlowAssets)");
                return null;
            }
            var go = new GameObject("Site picker");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var document = go.AddComponent<UIDocument>();
            document.panelSettings = UI.UiPanels.Runtime(assets.PickerPanel);
            document.visualTreeAsset = assets.PickerTree;
            document.sortingOrder = 20;
            var picker = go.AddComponent<SitePicker>();
            picker.Document = document;
            go.SetActive(true);
            picker.Hide();
            return picker;
        }

        public static string DataRootFrom(string[] args)
        {
            int i = Array.IndexOf(args, "-data");
            return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : MountainViewer.DataRoot;
        }

        void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;

        /// <summary>Wires the screens to the controller once (Create assigns the fields before the object wakes).</summary>
        void Awake()
        {
            Screens.ContinueChosen += () => Controller.Continue(ContinueTarget()?.Folder);
            Screens.NewResortChosen += Controller.NewResort;
            Screens.LoadChosen += () => { _mode = LibraryMode.Load; Controller.MyResorts(); };
            Screens.ManageChosen += () => { _mode = LibraryMode.Manage; Controller.MyResorts(); };
            Screens.CreditsChosen += () => Screens.ShowCredits(Credits());
            Screens.QuitChosen += Controller.Quit;
            Screens.LibraryClosed += () => Controller.Escape();
            Screens.DataFolderChosen += () =>
            {
                Directory.CreateDirectory(DataRoot);
                Application.OpenURL(new Uri(DataRoot).AbsoluteUri);
            };
            // The sort is remembered (a setting, not a frozen format: task P2-04).
            int savedSort = PlayerPrefs.GetInt(SortPref, (int)LibrarySort.LastOpened);
            _sort = Enum.IsDefined(typeof(LibrarySort), savedSort) ? (LibrarySort)savedSort : LibrarySort.LastOpened;
            Screens.SortChosen += s =>
            {
                _sort = s;
                PlayerPrefs.SetInt(SortPref, (int)s);
                PlayerPrefs.Save();
                RefreshLibrary();
            };
            Screens.RenameConfirmed += (r, name) =>
            {
                if (ResortLibrary.Rename(r.Entry, name)) Screens.Toast($"Renamed to {r.Entry.Name}.");
                else Screens.Toast(r.Entry.RenameRefusal.Length > 0 ? r.Entry.RenameRefusal : "That name can't be used.", 5);
                RefreshLibrary();
            };
            Screens.FreeSpaceConfirmed += FreeSpace;
            Screens.OpenChosen += r => { if (r.CanOpen) Controller.Open(r.Entry.Folder); };
            Screens.ResumeChosen += r => StartDownload(r.Pending);
            Screens.DiscardChosen += r =>
            {
                if (Downloads.Running && Downloads.Current?.Id == r.Pending.Id) { Screens.Toast("That download is running; cancel it first."); return; }
                PendingDownloads.Remove(DataRoot, r.Pending);
                RefreshLibrary();
            };
            Screens.DeleteConfirmed += Delete;
            if (Picker != null)
            {
                Picker.SiteChosen += OnSiteChosen;
                Picker.Cancelled += Controller.PickerCancelled;   // the picker handles its own Esc
            }
            Screens.MinimiseChosen += Controller.MinimiseDownload;
            Screens.RestoreChosen += Controller.RestoreDownload;
            Screens.CancelConfirmed += keep => Downloads.Cancel(keep);
            Screens.RetryChosen += () =>
            {
                var d = Downloads.Current;
                if (d != null && Downloads.Start(d)) Controller.DownloadStarted();
            };
            Screens.CloseChosen += () =>
            {
                Downloads.Dismiss();
                Controller.DownloadStopped();
            };
            Screens.QualityOpenChosen += Controller.QualityOpen;
            Screens.QualityLibraryChosen += Controller.QualityBackToLibrary;
            Downloads.Finished += folder => Controller.DownloadFinished(folder);
            Downloads.Stopped += kept =>
            {
                Controller.DownloadStopped();
                Screens.Toast(kept ? "Download paused. Resume it from Manage Areas." : "Download discarded.");
                if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
            };
            Downloads.Failed += message => Controller.RestoreDownload();
            FlowUnits.Changed += OnUnitsChanged;
        }

        /// <summary>Imperial or metric changed: redraw the open screen's lengths.</summary>
        void OnUnitsChanged()
        {
            if (Controller.Screen == FlowScreen.Library || Controller.Screen == FlowScreen.Quality) ShowScreen(Controller.Screen);
        }

        void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        void OnDestroy()
        {
            FlowUnits.Changed -= OnUnitsChanged;
            if (Picker != null) Destroy(Picker.gameObject);
            if (Instance == this)
            {
                Instance = null;
                ViewCamera.OverlayBlocked = null;
                MountainViewer.FlowHasKeyboard = null;
            }
        }

        void Start()
        {
            // The first scene may already be up (tests create the flow after loading it).
            if (_viewer == null && FindAnyObjectByType<MountainViewer>() != null) OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
            StartCaptureIfAsked();
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _viewer = FindAnyObjectByType<MountainViewer>();
            if (_viewer == null)
            {
                // Another scene (the Lift Lab, a test's own scene): the flow stays out of the way until a viewer scene loads.
                Screens.ShowScreen(null);
                return;
            }
            _viewerScene = scene.path;
            _framesSinceLoad = 0;
            _drift = null;   // each title starts its drift from the first view
            _driftSeconds = 0;
            if (_viewer.Hud != null)
            {
                _viewer.Hud.ShowExitToTitle(true);
                _viewer.Hud.ExitChosen += Controller.ExitToTitle;
                _viewer.Hud.ShowSettings(true);   // the menu's Settings opens the flow's window: units, theme, UI scale
                var hud = _viewer.Hud;
                hud.SettingsChosen += () => Screens.ShowSettings(hud.SiteName);   // the head names the resort, as in the mockup
            }
            var then = _afterTitle;
            _afterTitle = FlowScreen.Title;
            Controller.SceneReady(!MountainViewer.TitleMode, then);
        }

        void Update()
        {
            // The viewer reads RequestedPackage in its first Start; clear it after so a later reload doesn't reuse it.
            if (_framesSinceLoad < 3 && ++_framesSinceLoad == 3) MountainViewer.RequestedPackage = null;

            Downloads.Pump(Time.unscaledDeltaTime);
            if (Downloads.Running || Downloads.View.Phase == DownloadPhase.Failed) Screens.RenderDownload(Downloads.View);
            HandleKeys(Keyboard.current);

            // The title's slow drift over the demo mountain (the viewer's own camera input is off in title mode).
            var cam = _viewer != null ? _viewer.Camera : null;
            if (MountainViewer.TitleMode && cam != null && cam.Surface != null)
            {
                _drift ??= DriftFor(cam);
                _drift.Apply(cam, _driftSeconds);
                if (!_driftHeld && _viewer.Ready) _driftSeconds += Time.unscaledDeltaTime;   // starts at the first view once all is in
                if (!_readyLogged && _viewer.Ready)
                {
                    _readyLogged = true;
                    Debug.Log($"[AppFlow] The title's mountain is in at {Time.realtimeSinceStartup:F1} s after launch ({(BundledDemo.Contains(_viewer.OpenPackage) ? "built-in demo" : "library")})");
                }
            }
        }

        /// <summary>The postcard path for the mountain on screen (read once, as the title's mountain comes in).</summary>
        TitleDrift DriftFor(Presentation.ViewCamera cam)
        {
            double lat = 0, lon = 0;
            float size = 5000;
            try
            {
                var site = ResortPackage.ReadManifest(_viewer.OpenPackage).Site;
                lat = site.Latitude;
                lon = site.Longitude;
                size = (float)site.SizeMetres;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                Debug.LogWarning($"[AppFlow] The title's drift uses the plain sweep: {e.Message}");
            }
            var surface = cam.Surface;
            return TitleDrift.For(lat, lon, size, MountainViewer.HomeYaw, surface.HeightAt);
        }

        /// <summary>True when the pointer (screen pixels, origin bottom-left) is over a flow panel or the open picker.</summary>
        public bool PointerOverFlow(Vector2 screen) =>
            Screens.IsPointerOverPanel(screen) || (Picker != null && Picker.IsPointerOver(screen));

        /// <summary>
        /// Esc backs out one step: a dialog, then Settings or Credits, then the screen (the picker handles its own).
        /// Everything else on the flow's screens is UI Toolkit focus: the arrows, Tab, Enter, and Delete on a
        /// Manage Areas row (FlowScreens, UiFocus; task P2-01).
        /// </summary>
        void HandleKeys(Keyboard keys)
        {
            if (keys == null) return;
            if (keys.escapeKey.wasPressedThisFrame && Controller.Screen != FlowScreen.Picker)
            {
                bool used = true;
                if (Screens.PromptOpen) Screens.ClosePrompt();
                else if (Screens.ConfirmOpen) Screens.CloseConfirm();
                else if (Screens.OverlayOpen) Screens.CloseOverlay();
                else used = Controller.Escape();
                if (used) MountainViewer.FlowTookEscapeFrame = Time.frameCount;   // the in-game menu doesn't open as well
            }
        }

        /// <summary>True while one of the flow's windows has the keyboard, so the viewer leaves the keys alone.</summary>
        bool FlowHasKeyboard() =>
            Screens.ConfirmOpen || Screens.PromptOpen || Screens.OverlayOpen || (Picker != null && Picker.IsOpen) || Controller.Screen == FlowScreen.Library || Controller.Screen == FlowScreen.Quality;

        // ---------- IFlowHost ----------

        public void ShowScreen(FlowScreen screen)
        {
            if (screen != FlowScreen.Picker && Picker != null && Picker.IsOpen) Picker.Hide();
            switch (screen)
            {
                case FlowScreen.Title:
                    var target = ContinueTarget();
                    bool opened = target != null && RecentResorts.Load(DataRoot).Opened.ContainsKey(target.PackageId);
                    if (target == null) Screens.SetContinue(null, null);
                    else if (opened) Screens.SetContinue("Continue", $"{target.Name} · last opened {LibraryViewModel.When(RecentResorts.Load(DataRoot).Opened[target.PackageId], DateTime.UtcNow)}");
                    else Screens.SetContinue(target.OriginalName == "Jackson Hole" && target.Name == target.OriginalName ? "Open the demo" : "Continue", target.Name);
                    Screens.ShowScreen("title");
                    // A data folder from a newer game (task 08) lists nothing; say why rather than look empty.
                    string refusal = LibraryIndex.Refusal(DataRoot);
                    if (refusal != null) Screens.Toast(refusal);
                    break;
                case FlowScreen.Library:
                    RefreshLibrary();
                    Screens.ShowScreen("library");
                    break;
                case FlowScreen.Picker:
                    Screens.ShowScreen(null);   // the picker opens over the live mountain
                    if (Picker == null) { Screens.Toast("The site picker isn't available in this build."); Controller.PickerCancelled(); break; }
                    Picker.Services ??= new SitePickerServices(Path.Combine(PipelineDownloader.CacheFolder(DataRoot), "picker"));
                    Picker.Show();
                    break;
                case FlowScreen.Quality:
                    try
                    {
                        Screens.RenderQuality(QualityCardViewModel.From(ResortPackage.ReadManifest(Controller.FinishedPackage), justDownloaded: true));
                        Screens.ShowScreen("quality");
                    }
                    catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
                    {
                        Debug.LogWarning($"[AppFlow] The finished package can't be read: {e.Message}");
                        Screens.Toast("The download finished but its package can't be read; try opening it from Load Area.");
                        Screens.ShowScreen(MountainViewer.TitleMode ? "title" : null);
                    }
                    break;
                default:
                    Screens.ShowScreen(null);
                    break;
            }
        }

        public void SetDownloadCardOpen(bool open) => Screens.ShowDownloadCard(open, Downloads.Running || Downloads.View.Phase == DownloadPhase.Failed && Downloads.Current != null, !MountainViewer.TitleMode);

        public void OpenMountain(string packageFolder)
        {
            // Keyed by package id, as the library is (the folder name needn't match it).
            try
            {
                if (!RecentResorts.Touch(DataRoot, ResortPackage.ReadManifest(packageFolder).PackageId, DownloadService.UtcStamp(DateTime.UtcNow)))
                    Debug.LogWarning($"[AppFlow] recent.json left as it is: {RecentResorts.Load(DataRoot).Refusal}");
            }
            catch (Exception e) { Debug.LogWarning($"[AppFlow] Couldn't record the open in recent.json: {e.Message}"); }
            Debug.Log($"[AppFlow] Opening {packageFolder}");
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = packageFolder;
            Reload();
        }

        public void ReturnToTitle(FlowScreen then)
        {
            _afterTitle = then;
            MountainViewer.TitleMode = true;
            MountainViewer.RequestedPackage = TitleBackground();
            Reload();
        }

        public void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ---------- helpers ----------

        void Reload()
        {
            Screens.ShowScreen(null);
            if (!string.IsNullOrEmpty(_viewerScene)) SceneManager.LoadScene(_viewerScene);
            else SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        /// <summary>The picker's hand-off (task 13's SitePicker.SiteChosen).</summary>
        public void OnSiteChosen(PickedSite site)
        {
            // The pipeline rebuilds the square from the centre; it must be the exact square the player saw.
            var rebuilt = SiteSquare.Create(site.Centre, site.SizeKm);
            if (rebuilt.Centre.X != site.Square.Centre.X || rebuilt.Centre.Y != site.Square.Centre.Y || rebuilt.SizeMetres != site.Square.SizeMetres)
                Debug.LogError($"[AppFlow] {site.Name}: the picked square doesn't survive the trip to the pipeline");
            StartDownload(DownloadService.Request(site, DownloadService.UtcStamp(DateTime.UtcNow)));
        }

        void StartDownload(PendingDownload d)
        {
            if (Downloads.Running)
            {
                Screens.Toast("One download at a time: wait for this one or cancel it.");
                return;
            }
            if (Downloads.Start(d)) Controller.DownloadStarted();
            if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        /// <summary>
        /// Rescans the library and redraws S2. Sizes already measured show at once; a fresh measure then runs on a
        /// worker thread (it reads every file's size) and updates them in place (task P2-04).
        /// </summary>
        void RefreshLibrary()
        {
            var running = Downloads.Running ? Downloads.Current?.Id : null;
            _pending = PendingDownloads.List(DataRoot).Where(p => p.Id != running).ToList();
            _newer = new System.Collections.Generic.List<LibraryEntry>();
            _entries = ResortLibrary.ScanWithBundled(DataRoot, BundledDemo.Root, _newer);
            foreach (var e in _entries.Concat(_newer))
                if (_sizes.TryGetValue(e.Folder, out var known)) ResortLibrary.SetDisk(e, known);
            Screens.RenderLibrary(_library = BuildLibrary(), _mode);
            MeasureLibrary();
        }

        LibraryViewModel BuildLibrary()
        {
            string refusal = LibraryIndex.Refusal(DataRoot);
            return refusal != null ? LibraryViewModel.Refused(refusal, _sort)
                                   : LibraryViewModel.Build(_entries, _pending, RecentResorts.Load(DataRoot), _sort, DateTime.UtcNow, _newer, _leftovers);
        }

        async void MeasureLibrary()
        {
            int run = ++_measureRun;
            var folders = _entries.Concat(_newer).Where(e => !e.Bundled).Select(e => e.Folder).ToList();   // the built-in demo isn't the player's disk
            string root = DataRoot;
            System.Collections.Generic.List<(string Folder, DiskUse Use)> measured;
            long leftovers;
            try
            {
                (measured, leftovers) = await System.Threading.Tasks.Task.Run(() =>
                    (folders.Select(f => (f, ResortLibrary.MeasureFolder(f))).ToList(), ResortLibrary.LeftoverBytes(root)));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[AppFlow] Measuring the library: {e.Message}");
                return;
            }
            if (this == null || run != _measureRun) return;   // closed, or a newer scan is measuring
            _leftovers = leftovers;
            foreach (var (folder, use) in measured) _sizes[folder] = use;
            foreach (var e in _entries.Concat(_newer))
                if (_sizes.TryGetValue(e.Folder, out var use)) ResortLibrary.SetDisk(e, use);
            if (Controller.Screen != FlowScreen.Library) return;
            _library = BuildLibrary();
            Screens.UpdateLibrarySizes(_library);
        }

        /// <summary>
        /// Deletes an area (confirmed). The mountain behind the title holds its cache open, so deleting that one lets
        /// it go first and then brings up another behind the menu.
        /// </summary>
        void Delete(LibraryRow r)
        {
            bool backdrop = _viewer != null && MountainViewer.TitleMode && SameFolder(_viewer.OpenPackage ?? MountainViewer.RequestedPackage, r.Entry.Folder);
            if (backdrop && (_viewer.OpenPackage == null || !_viewer.ReleaseCache()))
            {
                Screens.Toast($"{r.Name} is still opening behind the menu; try again in a moment.");
                return;
            }
            // Only the quick, all-or-nothing rename happens here; the files go on a worker thread.
            if (!ResortLibrary.TryRemove(r.Entry, out long freed, sweep: false))
            {
                if (backdrop) _viewer.HoldCache();   // still on screen: protected again
                Screens.Toast($"{r.Name} is open in another window of the game, or one of its files is in use. Close it there first.", 5);
                RefreshLibrary();
                return;
            }
            _sizes.Remove(r.Entry.Folder);
            string resorts = ResortLibrary.ResortsFolder(DataRoot);
            System.Threading.Tasks.Task.Run(() => ResortLibrary.SweepTrash(resorts));
            Screens.Toast($"Deleted {r.Name}. {LibraryViewModel.Disk(freed)} freed.");
            if (backdrop) ReturnToTitle(FlowScreen.Library);   // a new mountain behind the menu, back in Manage Areas
            else RefreshLibrary();
        }

        /// <summary>Free space (confirmed): older versions' caches go on a worker thread; the toast says what was freed.</summary>
        async void FreeSpace()
        {
            if (_freeing) return;
            _freeing = true;
            long freed;
            try { freed = await System.Threading.Tasks.Task.Run(() => ResortLibrary.FreeSpace(DataRoot)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[AppFlow] Free space: {e.Message}");
                freed = -1;
            }
            finally { _freeing = false; }
            if (this == null) return;
            Screens.Toast(freed > 0 ? $"{LibraryViewModel.Disk(freed)} freed."
                          : freed == 0 ? "Nothing freed: those caches are open in another window of the game, or already gone."
                          : "Some caches couldn't be removed.");
            _sizes.Clear();
            if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        static bool SameFolder(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The credits (S9): the data every downloaded area credits, read from the packages so it works offline
        /// (0.3 §5), then the fonts.
        /// </summary>
        System.Collections.Generic.IEnumerable<(string, System.Collections.Generic.IEnumerable<string>)> Credits()
        {
            var data = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (var e in Areas())
            {
                try { foreach (string line in ResortPackage.ReadManifest(e.Folder).Attribution) data.Add(line); }
                catch (Exception ex) { Debug.LogWarning($"[AppFlow] Credits: {e.Name}: {ex.Message}"); }
            }
            yield return ("Ski Area Design Challenge", new[] { "A ski resort designer on real mountains." });
            yield return ("Map and terrain data", data.Count > 0 ? (System.Collections.Generic.IEnumerable<string>)data
                                                                 : new[] { "Download an area to see the data it uses." });
            yield return ("Type", new[] { "Overpass and Overpass Mono, SIL Open Font License 1.1." });
        }

        /// <summary>The mountain Continue opens: the last opened, else the demo, else the first in the library.</summary>
        LibraryEntry ContinueTarget()
        {
            var entries = Areas().Where(e => e.Refusal.Length == 0).ToList();
            if (entries.Count == 0) return null;
            string latest = RecentResorts.Load(DataRoot).Latest(entries.Select(e => e.PackageId));
            return entries.FirstOrDefault(e => e.PackageId == latest) ?? Demo(entries) ?? entries[0];
        }

        /// <summary>
        /// Behind the title: the Jackson Hole demo (the library's copy, else the one built into the game: task P2-03),
        /// else the first area in the library, else whatever the viewer picks itself.
        /// </summary>
        string TitleBackground()
        {
            var entries = Areas().Where(e => e.Refusal.Length == 0).ToList();
            return (Demo(entries) ?? entries.FirstOrDefault(e => !e.Bundled) ?? entries.FirstOrDefault())?.Folder;
        }

        /// <summary>The library's areas plus the ones built into the game (a library copy of the same package wins).</summary>
        System.Collections.Generic.List<LibraryEntry> Areas() => ResortLibrary.ScanWithBundled(DataRoot, BundledDemo.Root);

        static LibraryEntry Demo(System.Collections.Generic.List<LibraryEntry> entries) =>
            entries.Where(e => e.OriginalName == "Jackson Hole").OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
    }
}
