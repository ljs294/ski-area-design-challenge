using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
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
    /// Task P2-03.1: the loading screen. A fresh install opens behind it (the stages all finish, the live view draws a few
    /// frames under the picture before the fade); the Details list is click only and remembered; no key reaches the game
    /// under it; a first open shows the terrain being prepared.
    /// </summary>
    public sealed class LoadingScreenTests
    {
        const string ViewerScene = "Assets/MountainPlanner/Scenes/MountainViewer.unity";
        const string TestPrefix = "MountainPlannerTest.LoadingScreen.";

        sealed class NoDownloads : ISiteDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> p, CancellationToken ct) =>
                throw new AssertionException("Nothing here downloads.");
        }

        string _empty;
        BundledDemoFixture _demo;

        [SetUp]
        public void Setup()
        {
            SettingsStore.Prefix = TestPrefix;   // the player's own Details choice stays untouched
            SettingsStore.Delete("LoadingDetails");
            LoadingPreferences.Reload();
            Http.NetworkDisabled = true;
            _empty = Path.Combine(Path.GetTempPath(), "mp-loading-" + Guid.NewGuid().ToString("N"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (AppFlow.Instance != null) Object.Destroy(AppFlow.Instance.gameObject);
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            BundledDemo.Override = null;
            Http.NetworkDisabled = false;
            SettingsStore.Delete("LoadingDetails");
            SettingsStore.Prefix = SettingsStore.DefaultPrefix;
            LoadingPreferences.Reload();
            var viewer = SceneManager.GetSceneByPath(ViewerScene);
            if (viewer.IsValid() && viewer.isLoaded)
            {
                SceneManager.SetActiveScene(SceneManager.CreateScene("LoadingScreenTests empty"));
                yield return SceneManager.UnloadSceneAsync(viewer);
                yield return Resources.UnloadUnusedAssets();
            }
            _demo?.Dispose();
            _demo = null;
            if (Directory.Exists(_empty)) Directory.Delete(_empty, true);
        }

        static VisualElement Ui(AppFlow flow) => flow.Screens.Document.rootVisualElement;

        static bool Shown(VisualElement e) => e != null && !e.ClassListContains("hidden");

        /// <summary>A left click on an element, as the pointer would make it.</summary>
        static void Click(VisualElement e)
        {
            Vector2 at = e.worldBound.center;
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 })) e.SendEvent(down);
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 })) e.SendEvent(up);
        }

        [UnityTest]
        public IEnumerator AFreshInstallOpensBehindTheLoadingBox()
        {
            _demo = new BundledDemoFixture();
            BundledDemo.Override = _demo.Root;
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_empty, new NoDownloads());
            var ui = Ui(flow);
            Assert.That(flow.Screens.CoverUp, Is.True);
            Assert.That(Shown(ui.Q("cover-box")), Is.True);
            Assert.That(Shown(ui.Q("cover-details")), Is.False, "Details are off until the player asks");
            Assert.That(ui.Q<Label>("cover-text").text, Is.EqualTo("Opening Jackson Hole"));

            yield return SceneManager.LoadSceneAsync(ViewerScene);
            MountainViewer viewer = null;
            UnityEngine.Camera lens = null;
            int warm = 0;
            float until = Time.realtimeSinceStartup + 120;
            while (flow.Screens.CoverUp && Time.realtimeSinceStartup < until)
            {
                viewer ??= Object.FindAnyObjectByType<MountainViewer>();
                if (viewer != null && lens == null && viewer.Camera != null) lens = viewer.Camera.GetComponent<UnityEngine.Camera>();
                if (lens != null && lens.enabled) warm++;
                yield return null;
            }
            Assert.That(flow.Screens.CoverUp, Is.False, "the box lifted");
            Assert.That(viewer, Is.Not.Null);
            Assert.That(warm, Is.GreaterThanOrEqualTo(3), "the live view drew frames under the picture before the fade");
            var load = viewer.Loading;
            Assert.That(load.Complete, Is.True);
            Assert.That(load.Fraction, Is.EqualTo(1f));
            Assert.That(load.State(LoadStage.Preparing), Is.EqualTo(LoadState.Skipped), "the bundled demo's cache is built in");
            foreach (LoadStage s in Enum.GetValues(typeof(LoadStage)))
                Assert.That(load.State(s), Is.EqualTo(LoadState.Done).Or.EqualTo(LoadState.Skipped), s.ToString());
            Assert.That(load.Done(LoadStage.Terrain), Is.EqualTo(load.Total(LoadStage.Terrain)));
        }

        [UnityTest]
        public IEnumerator DetailsAreClickOnlyRememberedAndNoKeyReachesTheGame()
        {
            _demo = new BundledDemoFixture();
            BundledDemo.Override = _demo.Root;
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_empty, new NoDownloads());
            flow.HoldCover = true;   // the box stays up whatever the mountain does
            var ui = Ui(flow);
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            for (int i = 0; i < 10; i++) yield return null;   // laid out

            Assert.That(MountainViewer.FlowHasKeyboard?.Invoke(), Is.True, "under the box the viewer's keys wait (Esc can't quit)");
            var toggle = ui.Q<Button>("cover-details-toggle");
            Assert.That(toggle.focusable, Is.False, "click only");
            Click(toggle);
            yield return null;
            Assert.That(LoadingPreferences.Details, Is.True);
            Assert.That(Shown(ui.Q("cover-details")), Is.True);
            Assert.That(toggle.text, Is.EqualTo("Hide details"));
            LoadingPreferences.Reload();
            Assert.That(LoadingPreferences.Details, Is.True, "remembered: a fresh read of the store finds it on");

            // The rows follow the stages once the mountain is in.
            float until = Time.realtimeSinceStartup + 120;
            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            while ((viewer.Loading == null || !viewer.Loading.Complete) && Time.realtimeSinceStartup < until) yield return null;
            yield return null;
            var terrain = ui.Q("ld-terrain");
            Assert.That(terrain.ClassListContains("ld-row--done"), Is.True);
            Assert.That(terrain.Q<Label>(className: "ld-count").text, Does.EndWith(" tiles"));
            Assert.That(terrain.Q<Label>(className: "ld-time").text, Does.EndWith(" s"));
            Assert.That(Shown(ui.Q("ld-preparing")), Is.False, "nothing to prepare for the bundled demo");
            Assert.That(flow.Screens.CoverUp, Is.True, "held");

            Click(toggle);
            yield return null;
            Assert.That(LoadingPreferences.Details, Is.False);
            Assert.That(Shown(ui.Q("cover-details")), Is.False);

            flow.HoldCover = false;
            until = Time.realtimeSinceStartup + 10;
            while (flow.Screens.CoverUp && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(flow.Screens.CoverUp, Is.False, "released, it lifts");
            Assert.That(MountainViewer.FlowHasKeyboard?.Invoke(), Is.False, "the title has the keys back");
        }

        [UnityTest]
        public IEnumerator AFirstOpenShowsTheTerrainBeingPrepared()
        {
            // A library area with no cache yet: opening it builds one first.
            string source = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source) || new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000)
                Assert.Ignore("Git LFS hasn't fetched TestData/jackson-hole-2km.");
            var m = ResortPackage.ReadManifest(source);
            string package = Path.Combine(ResortLibrary.ResortsFolder(_empty), m.PackageId);
            Directory.CreateDirectory(package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(package, Path.GetFileName(f)));
            BundledDemo.Override = "";   // no built-in demo: the title shows this area
            LoadingPreferences.SetDetails(true, remember: false);

            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_empty, new NoDownloads());
            var ui = Ui(flow);
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            bool sawPreparing = false;
            float until = Time.realtimeSinceStartup + 180;
            while (flow.Screens.CoverUp && Time.realtimeSinceStartup < until)
            {
                sawPreparing |= Shown(ui.Q("ld-preparing")) && ui.Q("ld-preparing").ClassListContains("ld-row--current");
                yield return null;
            }
            Assert.That(sawPreparing, Is.True, "the Details list showed the cache being built");
            var load = Object.FindAnyObjectByType<MountainViewer>().Loading;
            Assert.That(load.State(LoadStage.Preparing), Is.EqualTo(LoadState.Done));
            Assert.That(load.Complete, Is.True);
        }
    }
}
