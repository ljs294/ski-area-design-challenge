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
        /// <summary>Frames since a viewer scene loaded; RequestedPackage is cleared at 3, once the viewer has read it.</summary>
        int _framesSinceLoad = 3;
        string _viewerScene;

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
            Screens.SortChosen += s =>
            {
                _sort = s;
                RefreshLibrary();
            };
            Screens.OpenChosen += r => Controller.Open(r.Entry?.Folder);
            Screens.ResumeChosen += r => StartDownload(r.Pending);
            Screens.DiscardChosen += r =>
            {
                if (Downloads.Running && Downloads.Current?.Id == r.Pending.Id) { Screens.Toast("That download is running; cancel it first."); return; }
                PendingDownloads.Remove(DataRoot, r.Pending);
                RefreshLibrary();
            };
            Screens.DeleteConfirmed += r =>
            {
                long freed = ResortLibrary.Remove(r.Entry);
                Screens.Toast($"Deleted {r.Name}. {LibraryViewModel.Disk(freed)} freed.");
                RefreshLibrary();
            };
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

            // The title's slow orbit over the demo mountain (the viewer's own camera input is off in title mode).
            var cam = _viewer != null ? _viewer.Camera : null;
            if (MountainViewer.TitleMode && cam != null && cam.Surface != null) cam.SetAngles(cam.Yaw + 2.5f * Time.unscaledDeltaTime, cam.Pitch);
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
                if (Screens.ConfirmOpen) Screens.CloseConfirm();
                else if (Screens.OverlayOpen) Screens.CloseOverlay();
                else used = Controller.Escape();
                if (used) MountainViewer.FlowTookEscapeFrame = Time.frameCount;   // the in-game menu doesn't open as well
            }
        }

        /// <summary>True while one of the flow's windows has the keyboard, so the viewer leaves the keys alone.</summary>
        bool FlowHasKeyboard() =>
            Screens.ConfirmOpen || Screens.OverlayOpen || (Picker != null && Picker.IsOpen) || Controller.Screen == FlowScreen.Library || Controller.Screen == FlowScreen.Quality;

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
                    else Screens.SetContinue(target.Name == "Jackson Hole" ? "Open the demo" : "Continue", target.Name);
                    Screens.ShowScreen("title");
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
            try { RecentResorts.Touch(DataRoot, ResortPackage.ReadManifest(packageFolder).PackageId, DownloadService.UtcStamp(DateTime.UtcNow)); }
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

        void RefreshLibrary()
        {
            var running = Downloads.Running ? Downloads.Current?.Id : null;
            var pending = PendingDownloads.List(DataRoot).Where(p => p.Id != running).ToList();
            _library = LibraryViewModel.Build(ResortLibrary.Scan(DataRoot), pending, RecentResorts.Load(DataRoot), _sort, DateTime.UtcNow);
            Screens.RenderLibrary(_library, _mode);
        }

        /// <summary>
        /// The credits (S9): the data every downloaded area credits, read from the packages so it works offline
        /// (0.3 §5), then the fonts.
        /// </summary>
        System.Collections.Generic.IEnumerable<(string, System.Collections.Generic.IEnumerable<string>)> Credits()
        {
            var data = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (var e in ResortLibrary.Scan(DataRoot))
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
            var entries = ResortLibrary.Scan(DataRoot);
            if (entries.Count == 0) return null;
            string latest = RecentResorts.Load(DataRoot).Latest(entries.Select(e => e.PackageId));
            return entries.FirstOrDefault(e => e.PackageId == latest) ?? Demo(entries) ?? entries[0];
        }

        /// <summary>Behind the title: the demo mountain from this library, else whatever the viewer picks itself.</summary>
        string TitleBackground() => Demo(ResortLibrary.Scan(DataRoot))?.Folder ?? ResortLibrary.Scan(DataRoot).FirstOrDefault()?.Folder;

        static LibraryEntry Demo(System.Collections.Generic.List<LibraryEntry> entries) =>
            entries.Where(e => e.Name == "Jackson Hole").OrderByDescending(e => e.SizeKm).ThenByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
    }
}
