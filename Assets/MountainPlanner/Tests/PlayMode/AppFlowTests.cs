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

        static IEnumerator WaitForMountain(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                var viewer = Object.FindAnyObjectByType<MountainViewer>();
                if (viewer != null && viewer.Camera != null && viewer.Camera.Surface != null) yield break;
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
    }
}
