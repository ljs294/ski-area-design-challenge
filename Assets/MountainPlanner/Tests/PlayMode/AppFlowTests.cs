using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 14 acceptance, the offline half: with the network disabled, the game starts on the title over the
    /// library's mountain, then Load Area → Open puts that mountain on screen in the game. Uses a scratch
    /// library seeded with the committed Jackson Hole test terrain, never the shared one.
    /// </summary>
    public sealed class AppFlowTests
    {
        const string ViewerScene = "Assets/MountainPlanner/Scenes/MountainViewer.unity";
        string _root, _package;

        /// <summary>Fails the test if the flow ever tries to download.</summary>
        sealed class NoDownloads : ISiteDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> p, CancellationToken ct) =>
                throw new AssertionException("Opening a downloaded mountain must not download anything.");
        }

        [OneTimeSetUp]
        public void SeedScratchLibrary()
        {
            string source = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source) || new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000)
                Assert.Ignore("Git LFS hasn't fetched TestData/jackson-hole-2km.");
            _root = Path.Combine(Path.GetTempPath(), "mp-flow-" + Guid.NewGuid().ToString("N"));
            var m = ResortPackage.ReadManifest(source);
            _package = Path.Combine(ResortLibrary.ResortsFolder(_root), m.PackageId);
            Directory.CreateDirectory(_package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(_package, Path.GetFileName(f)));
        }

        [OneTimeTearDown]
        public void Clean()
        {
            Http.NetworkDisabled = false;
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            if (AppFlow.Instance != null) Object.Destroy(AppFlow.Instance.gameObject);
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary>
        /// Leaves no viewer scene behind: its sun, sky and lighting would change what later PlayMode tests render
        /// (the map-layer test's brightness checks failed after this one until it cleaned up).
        /// </summary>
        [UnityTearDown]
        public IEnumerator UnloadTheViewer()
        {
            if (AppFlow.Instance != null) Object.Destroy(AppFlow.Instance.gameObject);
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            var viewer = SceneManager.GetSceneByPath(ViewerScene);
            if (!viewer.IsValid() || !viewer.isLoaded) yield break;
            SceneManager.SetActiveScene(SceneManager.CreateScene("AppFlowTests empty"));
            yield return SceneManager.UnloadSceneAsync(viewer);
            yield return Resources.UnloadUnusedAssets();
        }

        static bool CoverUp => AppFlow.Instance != null && AppFlow.Instance.Screens.CoverUp;

        static IEnumerator WaitForMountain(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                var viewer = Object.FindAnyObjectByType<MountainViewer>();
                if (viewer != null && viewer.Camera != null && viewer.Camera.Surface != null && !CoverUp) yield break;   // and the flow's cover has lifted (task P2-03)
                yield return null;
            }
            Assert.Fail("The mountain didn't open in time.");
        }

        [UnityTest]
        public IEnumerator TheLibraryOpensAMountainWithTheNetworkOff()
        {
            Http.NetworkDisabled = true;
            // In an interactive editor run the flow has already booted on the real library; start over on the scratch one.
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            Assert.That(MountainViewer.TitleMode, Is.True);
            Assert.That(MountainViewer.RequestedPackage, Is.EqualTo(_package), "the title shows this library's demo mountain");
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));
            Assert.That(flow.Controller.InGame, Is.False);

            flow.Controller.MyResorts();
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Library));
            var row = flow.Screens.SelectedRow;
            Assert.That(row?.Entry, Is.Not.Null, "the library lists the seeded mountain and selects it");
            Assert.That(row.Entry.Folder, Is.EqualTo(_package));

            flow.Controller.Open(row.Entry.Folder);   // what Enter or Open does
            yield return null;
            yield return WaitForMountain(120);
            Assert.That(MountainViewer.TitleMode, Is.False);
            Assert.That(flow.Controller.InGame, Is.True);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Game));
            Assert.That(RecentResorts.Load(_root).Opened.ContainsKey(row.Entry.PackageId), Is.True, "Continue will reopen it");
            for (int i = 0; i < 5; i++) yield return null;   // a few frames in the game without errors
        }

        /// <summary>
        /// Task P2-03 acceptance: a fresh install (no library) with the network off opens the title over the demo built
        /// into the game, in the low afternoon sun, drifting; "Open the demo" opens it; nothing is written into the game's
        /// own folder (no cache lease, no view.json), and only the player's recent list records the open.
        /// </summary>
        [UnityTest]
        public IEnumerator AFreshInstallOpensTheBuiltInDemoOffline()
        {
            string empty = Path.Combine(Path.GetTempPath(), "mp-fresh-" + Guid.NewGuid().ToString("N"));   // not even created yet
            var demo = new BundledDemoFixture();
            try
            {
                string before = demo.Snapshot();
                BundledDemo.Override = demo.Root;
                Http.NetworkDisabled = true;
                if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
                var flow = AppFlow.Create(empty, new NoDownloads());
                Assert.That(MountainViewer.RequestedPackage, Is.EqualTo(demo.Package), "the built-in demo is behind the title");
                var ui = flow.Screens.Document.rootVisualElement;
                Assert.That(flow.Screens.CoverUp, Is.True, "the cover is up from the first frame");
                Assert.That(ui.Q<Label>("cover-text").text, Is.EqualTo("Opening Jackson Hole"));
                yield return SceneManager.LoadSceneAsync(ViewerScene);
                yield return WaitForMountain(120);
                Assert.That(Object.FindAnyObjectByType<MountainViewer>().Ready, Is.True, "the cover lifts only once the mountain is whole");
                for (int i = 0; i < 3; i++) yield return null;

                Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));
                Assert.That(ui.Q<Label>("title-continue-label").text, Is.EqualTo("Open the demo"));
                var viewer = Object.FindAnyObjectByType<MountainViewer>();
                Assert.That(viewer.OpenPackage, Is.EqualTo(demo.Package));
                Assert.That(viewer.Lighting.Clock.Now.SecondOfDay, Is.EqualTo(MountainViewer.TitleSecondOfDay), "the title's 15:30 sun");
                var cam = viewer.Camera;
                Vector3 from = cam.transform.position;
                for (int i = 0; i < 30; i++) yield return null;
                Assert.That(Vector3.Distance(cam.transform.position, from), Is.GreaterThan(0.01f), "the camera drifts");

                flow.Controller.Continue(demo.Package);   // what "Open the demo" does
                yield return null;
                yield return WaitForMountain(120);
                Assert.That(flow.Controller.InGame, Is.True);
                Assert.That(Object.FindAnyObjectByType<MountainViewer>().OpenPackage, Is.EqualTo(demo.Package));
                Assert.That(RecentResorts.Load(empty).Opened.ContainsKey(Path.GetFileName(demo.Package)), Is.True, "Continue will reopen it");
                for (int i = 0; i < 5; i++) yield return null;
                Assert.That(demo.Snapshot(), Is.EqualTo(before), "nothing was written into the game's own folder");
            }
            finally
            {
                BundledDemo.Override = null;   // the viewer scene goes in UnloadTheViewer
                demo.Dispose();
                if (Directory.Exists(empty)) Directory.Delete(empty, true);
            }
        }

        /// <summary>The screen point (pixels, origin bottom-left) at the centre of a flow element.</summary>
        static Vector2 ScreenCentre(VisualElement e)
        {
            var tree = e.panel.visualTree.worldBound;
            var c = e.worldBound.center;
            return new Vector2(c.x * Screen.width / tree.width, Screen.height - c.y * Screen.height / tree.height);
        }

        /// <summary>
        /// Polish: in the game, the camera ignores the pointer over the flow's download card (and only there),
        /// and the HUD menu's Exit to title goes back to the signpost over the demo mountain.
        /// </summary>
        [UnityTest]
        public IEnumerator TheCameraIgnoresTheDownloadCardAndExitGoesToTheTitle()
        {
            Http.NetworkDisabled = true;
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120);
            flow.Controller.Open(_package);
            yield return null;
            yield return WaitForMountain(120);
            Assert.That(flow.Controller.InGame, Is.True);

            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            var exit = viewer.Hud.Document.rootVisualElement.Q("menu-exit");
            Assert.That(exit.ClassListContains("hidden"), Is.False, "the in-game menu offers Exit to title");

            flow.Screens.ShowDownloadCard(open: true, active: true, inGame: true);   // as a running download shows it
            for (int i = 0; i < 3; i++) yield return null;                         // let the panel lay out
            var card = flow.Screens.Document.rootVisualElement.Q("download");
            Assert.That(card.worldBound.width, Is.GreaterThan(0), "the card is laid out");
            Vector2 onCard = ScreenCentre(card);
            Assert.That(Presentation.ViewCamera.IsPointerBlocked(onCard), Is.True, "over the download card the camera leaves the pointer alone");
            Assert.That(Presentation.ViewCamera.IsPointerBlocked(new Vector2(Screen.width * 0.6f, Screen.height * 0.5f)), Is.False,
                "over the open map the camera still takes the pointer");
            flow.Screens.ShowDownloadCard(open: false, active: false, inGame: true);
            yield return null;
            Assert.That(Presentation.ViewCamera.IsPointerBlocked(onCard), Is.False, "a closed card no longer blocks");

            flow.Controller.ExitToTitle();   // what the menu's Exit to title does
            yield return null;
            yield return WaitForMountain(120);
            Assert.That(MountainViewer.TitleMode, Is.True);
            Assert.That(flow.Controller.InGame, Is.False);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));
        }

        /// <summary>Holds the download at its second stage until released (task P2-06).</summary>
        sealed class HeldDownload : ISiteDownloader
        {
            public volatile bool Release;
            public int Runs;

            public async Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> progress, CancellationToken ct)
            {
                Interlocked.Increment(ref Runs);
                var stages = new[] { "Terrain", "Forest", "Building" };
                progress.Report(new DownloadStatus { Name = d.Name, Stages = stages, StageIndex = 1, StageCount = 3, Stage = "Terrain", Overall = 0.05 });
                while (!Release) { ct.ThrowIfCancellationRequested(); await Task.Delay(10, ct); }
                throw new OperationCanceledException();   // the test ends by cancelling; nothing reaches the library
            }
        }

        /// <summary>
        /// Task P2-06 acceptance, the in-game half: a download paused by a quit resumes by itself once the title is up,
        /// as the pill (at the share it reached), keeps going while another area opens, and shows in that area's HUD bar.
        /// </summary>
        [UnityTest]
        public IEnumerator APausedDownloadResumesByItselfAndKeepsGoingInTheGame()
        {
            Http.NetworkDisabled = false;
            var held = new HeldDownload();
            var paused = new PendingDownload
            {
                Id = PendingDownloads.IdFor("Crystal Mountain", 46.935, -121.474, 2), Name = "Crystal Mountain", Latitude = 46.935, Longitude = -121.474,
                SizeKm = 2, StartedUtc = "2026-10-08T10:00:00Z", LastOverall = 0.4, LastStage = "Forest",
            };
            Assert.That(PendingDownloads.Save(_root, paused), Is.True);
            try
            {
                if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
                var flow = AppFlow.Create(_root, held);
                flow.ResumeAtLaunch = true;   // off in batch runs; this is what a player's launch does
                yield return SceneManager.LoadSceneAsync(ViewerScene);
                yield return WaitForMountain(120);
                for (int i = 0; i < 120 && (!flow.Downloads.Running || held.Runs == 0); i++) yield return null;
                Assert.That(flow.Downloads.Running, Is.True, "resumed by itself once the title was up");
                Assert.That(held.Runs, Is.EqualTo(1));
                Assert.That(flow.Controller.DownloadActive && !flow.Controller.DownloadCardOpen, Is.True, "as the pill, not the card");
                Assert.That(flow.Screens.PillShown, Is.True);
                Assert.That(flow.Downloads.View.Percent, Is.EqualTo("40%"), "at the share it had reached, not 0%");

                flow.Controller.Open(_package);   // explore another area meanwhile
                yield return null;
                yield return WaitForMountain(120);
                Assert.That(flow.Controller.InGame, Is.True);
                Assert.That(flow.Downloads.Running, Is.True, "it keeps going across the scene load");
                Assert.That(flow.Screens.PillShown, Is.False, "in the game the pill is the HUD bar's");
                yield return null;
                var hud = Object.FindAnyObjectByType<MountainViewer>().Hud;
                var cell = hud.Document.rootVisualElement.Q("bar-download-cell");
                Assert.That(cell, Is.Not.Null);
                Assert.That(cell.ClassListContains("hidden"), Is.False, "the HUD bar shows the download");
                StringAssert.Contains("Crystal Mountain", hud.Document.rootVisualElement.Q<Label>("bar-download-text").text);
            }
            finally
            {
                held.Release = true;
                AppFlow.Instance?.Downloads.Cancel(keep: false);
            }
            for (int i = 0; i < 30 && AppFlow.Instance != null && AppFlow.Instance.Downloads.Active; i++) yield return null;
        }
    }
}
