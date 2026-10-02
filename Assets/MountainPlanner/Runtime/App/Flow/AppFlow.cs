using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// The game's screen flow (task 14; 0.4 §3): title → New Resort → download → quality card → the mountain,
    /// and title → My Resorts → open. It lives for the whole session (downloads keep running across scene
    /// reloads) and drives the Mountain Viewer scene through its hooks: <see cref="MountainViewer.TitleMode"/>
    /// shows the demo mountain behind the signpost, and <see cref="MountainViewer.RequestedPackage"/> plus a
    /// scene reload opens a chosen mountain from disk, with no network.
    ///
    /// It starts itself before the first scene loads, unless a capture, benchmark or explicit mountain was
    /// asked for on the command line (those keep the old straight-to-the-mountain start), or the run is in
    /// batch mode without -title. -data &lt;folder&gt; points the whole flow at another library (demos, tests);
    /// -offline turns the network off for the whole session.
    /// </summary>
    public sealed class AppFlow : MonoBehaviour, IFlowHost
    {
        public const string ResourceFolder = "MountainPlannerFlow/";

        /// <summary>Command-line switches that mean "no title screen": captures, benchmarks and explicit mountains.</summary>
        static readonly string[] SkipTitle =
        {
            "-notitle", "-package", "-site", "-screenshot", "-benchmark", "-lineup", "-lodtransitions", "-view", "-landmark", "-clip",
        };

        public static AppFlow Instance { get; private set; }

        public string DataRoot { get; private set; }
        public FlowController Controller { get; private set; }
        public DownloadService Downloads { get; private set; }
        public FlowScreens Screens { get; private set; }

        MountainViewer _viewer;
        FlowScreen _afterTitle = FlowScreen.Title;
        LibrarySort _sort = LibrarySort.LastOpened;
        LibraryViewModel _library;
        int _framesSinceLoad;
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
            document.panelSettings = Resources.Load<PanelSettings>(ResourceFolder + "FlowPanel");
            document.visualTreeAsset = Resources.Load<VisualTreeAsset>(ResourceFolder + "Flow");
            document.sortingOrder = 10;
            var screens = go.AddComponent<FlowScreens>();
            screens.Document = document;
            var flow = go.AddComponent<AppFlow>();
            flow.DataRoot = dataRoot;
            flow.Screens = screens;
            flow.Downloads = new DownloadService(dataRoot, downloader, () => DownloadService.UtcStamp(DateTime.UtcNow));
            flow.Controller = new FlowController(flow);
            Instance = flow;
            MountainViewer.TitleMode = true;
            MountainViewer.RequestedPackage = flow.TitleBackground();
            go.SetActive(true);
            return flow;
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
            Screens.LibraryChosen += Controller.MyResorts;
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
            Screens.PickerSubmitted += OnPickerSubmitted;
            Screens.PickerCancelled += Controller.PickerCancelled;
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
                Screens.Toast(kept ? "Download paused. Resume it from My Resorts." : "Download discarded.");
                if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
            };
            Downloads.Failed += message => Controller.RestoreDownload();
        }

        void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Start()
        {
            // The first scene may already be up (tests create the flow after loading it).
            if (_viewer == null) OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _viewer = FindAnyObjectByType<MountainViewer>();
            if (_viewer == null)
            {
                // Another scene (the Lift Lab, say): the flow isn't for it.
                MountainViewer.TitleMode = false;
                MountainViewer.RequestedPackage = null;
                Destroy(gameObject);
                return;
            }
            _viewerScene = scene.path;
            _framesSinceLoad = 0;
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

        void HandleKeys(Keyboard keys)
        {
            if (keys == null) return;
            if (keys.escapeKey.wasPressedThisFrame)
            {
                if (Screens.ConfirmOpen) Screens.CloseConfirm();
                else Controller.Escape();
                return;
            }
            if (Controller.Screen != FlowScreen.Library || Screens.ConfirmOpen) return;
            if (keys.downArrowKey.wasPressedThisFrame) Screens.MoveSelection(1);
            if (keys.upArrowKey.wasPressedThisFrame) Screens.MoveSelection(-1);
            var row = Screens.SelectedRow;
            if (row?.Entry == null) return;
            if (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame) Controller.Open(row.Entry.Folder);
            if (keys.deleteKey.wasPressedThisFrame) Screens.ConfirmDelete(row);
        }

        // ---------- IFlowHost ----------

        public void ShowScreen(FlowScreen screen)
        {
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
                    Screens.SetPickerError("");
                    Screens.ShowScreen("picker");
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
                        Screens.Toast("The download finished but its package can't be read; try opening it from My Resorts.");
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

        void OnPickerSubmitted(string name, double lat, double lon, double km)
        {
            PickedSite site;
            try { site = PickedSite.Create(name, Albers6350.Forward(new GeoPoint(lat, lon)), km, false, default); }
            catch (ArgumentException e)
            {
                Screens.SetPickerError(e is ArgumentOutOfRangeException ? "Sites are 2–5 km, in 0.1 km steps." : e.Message);
                return;
            }
            OnSiteChosen(site);
        }

        /// <summary>The picker's hand-off (task 13's SitePicker raises the same PickedSite).</summary>
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
            Screens.RenderLibrary(_library);
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
