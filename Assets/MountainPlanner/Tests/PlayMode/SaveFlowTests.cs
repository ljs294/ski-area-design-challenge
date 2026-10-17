using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.Domain.Design;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.UI.Flow;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-04 acceptance in the game: Ctrl+S asks for a name the first time and saves; the bar's save symbol shows
    /// unsaved changes; Exit to title with changes asks "Save before leaving?"; Continue reopens the newest save with an
    /// identical document. A scratch library seeded with the committed Jackson Hole test terrain.
    /// </summary>
    public sealed class SaveFlowTests
    {
        const string ViewerScene = "Assets/MountainPlanner/Scenes/MountainViewer.unity";
        string _root, _package, _packageId;
        Keyboard _keyboard;
        InputSettings.EditorInputBehaviorInPlayMode _routing;
        InputSettings.BackgroundBehavior _background;

        sealed class NoDownloads : ISiteDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> p, CancellationToken ct) =>
                throw new AssertionException("Nothing here downloads.");
        }

        [OneTimeSetUp]
        public void SeedScratchLibrary()
        {
            string source = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source) || new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000)
                Assert.Ignore("Git LFS hasn't fetched TestData/jackson-hole-2km.");
            _root = Path.Combine(Path.GetTempPath(), "mp-saveflow-" + Guid.NewGuid().ToString("N"));
            var m = ResortPackage.ReadManifest(source);
            _packageId = m.PackageId;
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
            OpenDesign.Current = null;
            if (AppFlow.Instance != null) Object.Destroy(AppFlow.Instance.gameObject);
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [SetUp]
        public void AKeyboard()
        {
            _routing = InputSystem.settings.editorInputBehaviorInPlayMode;
            _background = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _keyboard.MakeCurrent();
        }

        [UnityTearDown]
        public IEnumerator UnloadTheViewer()
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            InputSystem.RemoveDevice(_keyboard);
            InputSystem.settings.editorInputBehaviorInPlayMode = _routing;
            InputSystem.settings.backgroundBehavior = _background;
            if (AppFlow.Instance != null) Object.Destroy(AppFlow.Instance.gameObject);
            OpenDesign.Current = null;
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            var viewer = SceneManager.GetSceneByPath(ViewerScene);
            if (!viewer.IsValid() || !viewer.isLoaded) yield break;
            SceneManager.SetActiveScene(SceneManager.CreateScene("SaveFlowTests empty"));
            yield return SceneManager.UnloadSceneAsync(viewer);
            yield return Resources.UnloadUnusedAssets();
        }

        static bool CoverUp => AppFlow.Instance != null && AppFlow.Instance.Screens.CoverUp;

        static IEnumerator WaitForMountain(float seconds, bool inGame)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                var viewer = Object.FindAnyObjectByType<MountainViewer>();
                if (viewer != null && viewer.Camera != null && viewer.Camera.Surface != null && MountainViewer.TitleMode != inGame && !CoverUp) yield break;
                yield return null;
            }
            Assert.Fail("The mountain didn't open in time.");
        }

        static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        static void Click(Button b)
        {
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }

        static void AddLift(DesignHistory h, string name, double dx)
        {
            var r = h.Submit(new AddLift(h.Revision, name, LiftCatalogIds.SesselliftFgq4, PointMm.FromMetres(dx, 0), PointMm.FromMetres(dx + 200, 600),
                TowerMode.Automatic, TowerStance.Plumb, Array.Empty<TowerSpec>(), WorkStage.Built));
            Assert.That(r.Accepted, Is.True, r.ToString());
        }

        [UnityTest]
        public IEnumerator SaveExitAndContinueGiveAnIdenticalDesign()
        {
            Http.NetworkDisabled = true;
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120, inGame: false);
            Assert.That(OpenDesign.Current, Is.Null, "nothing is open on the title");

            flow.Controller.Open(_package);
            yield return null;
            yield return WaitForMountain(120, inGame: true);
            var open = OpenDesign.Current;
            Assert.That(open, Is.Not.Null, "an area with no saves opens a new design");
            Assert.That(open.PackageId, Is.EqualTo(_packageId));
            Assert.That(open.Dirty, Is.False);
            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            var hudUi = viewer.Hud.Document.rootVisualElement;
            var barSave = hudUi.Q<Button>("bar-save");
            yield return Frames(2);
            Assert.That(barSave.ClassListContains("save--due"), Is.False, "nothing to save yet: the symbol is grey");

            AddLift(open.History, "Chair 1", 0);
            yield return Frames(2);
            Assert.That(barSave.ClassListContains("save--due"), Is.True, "unsaved changes: the symbol is white");

            // Ctrl+S, the first time: the name dialog, with the area's name filled in.
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.LeftCtrl, Key.S));
            yield return Frames(2);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return Frames(2);
            Assert.That(flow.Screens.PromptOpen, Is.True, "the first save asks for a name");
            var ui = flow.Screens.Document.rootVisualElement;
            var field = ui.Q<TextField>("prompt-field");
            Assert.That(field.value, Is.EqualTo(viewer.Hud.SiteName));
            field.value = "Test plan";
            Click(ui.Q<Button>("prompt-ok"));
            yield return Frames(2);
            Assert.That(flow.Screens.PromptOpen, Is.False);
            Assert.That(SaveStore.List(_root, _packageId).Select(s => (s.Name, s.Lifts)), Is.EqualTo(new[] { ("Test plan", 1) }));
            Assert.That(open.Dirty, Is.False);
            Assert.That(barSave.ClassListContains("save--due"), Is.False);

            // Another change, then Exit to title: "Save before leaving?", and Save and leave writes to the same save.
            AddLift(open.History, "Chair 2", 400);
            byte[] expected = DesignBytes.Encode(open.History.Document);
            Click(hudUi.Q<Button>("menu-exit"));
            yield return Frames(2);
            Assert.That(flow.Screens.DialogTitle, Is.EqualTo("Save before leaving?"));
            Click(ui.Q<Button>("confirm-ok"));   // Save and leave
            yield return null;
            yield return WaitForMountain(120, inGame: false);
            Assert.That(OpenDesign.Current, Is.Null);
            var saves = SaveStore.List(_root, _packageId);
            Assert.That(saves.Select(s => (s.Name, s.Lifts)), Is.EqualTo(new[] { ("Test plan", 2) }), "Save and leave wrote the same named save");

            // Continue: the area's newest save, identical to what was saved (a fresh read from disk).
            flow.Controller.Continue(_package);
            yield return null;
            yield return WaitForMountain(120, inGame: true);
            Assert.That(OpenDesign.Current, Is.Not.Null);
            Assert.That(OpenDesign.Current.SaveName, Is.EqualTo("Test plan"));
            Assert.That(DesignBytes.Encode(OpenDesign.Current.History.Document), Is.EqualTo(expected), "save, quit and reopen give an identical document");
            Assert.That(OpenDesign.Current.Dirty, Is.False);
        }
    }
}
