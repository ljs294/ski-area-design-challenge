using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.Persistence;
using MountainPlanner.UI;
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
    /// Task P2-01 acceptance: every screen works by keyboard alone at 50%, 100% and 150% UI scale. The arrows, Tab
    /// and Enter arrive as UI Toolkit's navigation events at whatever has focus (what the event system sends for
    /// those keys); Esc and Delete come from a keyboard device, as the game reads them. Each step checks where focus
    /// is and that the focused control lies wholly on the screen.
    /// Uses a scratch library seeded with the committed Jackson Hole test terrain and never touches the player's
    /// saved settings.
    /// </summary>
    public sealed class UiKeyboardTests
    {
        const string ViewerScene = "Assets/MountainPlanner/Scenes/MountainViewer.unity";
        string _root, _package;
        Keyboard _keyboard;
        InputSettings.EditorInputBehaviorInPlayMode _routing;
        InputSettings.BackgroundBehavior _background;
        UiThemeChoice _theme;
        int _scale;

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
            _root = Path.Combine(Path.GetTempPath(), "mp-keys-" + Guid.NewGuid().ToString("N"));
            var m = ResortPackage.ReadManifest(source);
            _package = Path.Combine(ResortLibrary.ResortsFolder(_root), m.PackageId);
            Directory.CreateDirectory(_package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(_package, Path.GetFileName(f)));
            _theme = UiPreferences.Choice;
            _scale = UiPreferences.ScalePercent;
        }

        [OneTimeTearDown]
        public void Clean()
        {
            Http.NetworkDisabled = false;
            UiPreferences.SetChoice(_theme, remember: false);
            UiPreferences.SetScale(_scale, remember: false);
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [SetUp]
        public void AKeyboard()
        {
            // Batch-mode editors have no focused Game view; send the device's keys to the game anyway.
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
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            var viewer = SceneManager.GetSceneByPath(ViewerScene);
            if (!viewer.IsValid() || !viewer.isLoaded) yield break;
            SceneManager.SetActiveScene(SceneManager.CreateScene("UiKeyboardTests empty"));
            yield return SceneManager.UnloadSceneAsync(viewer);
            yield return Resources.UnloadUnusedAssets();
        }

        [UnityTest]
        public IEnumerator EveryScreenWorksByKeyboardAlone([Values(50, 100, 150)] int scale)
        {
            Http.NetworkDisabled = true;
            UiPreferences.SetChoice(UiThemeChoice.Dark, remember: false);
            UiPreferences.SetScale(scale, remember: false);
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120);
            yield return Frames(3);
            var ui = flow.Screens.Document.rootVisualElement;

            // S1 title: the first sign has focus; Down walks the signs; Enter on Load Area opens the library.
            AssertFocus(ui, "title-continue", "the title opens with its first sign focused");
            yield return Move(ui, NavigationMoveEvent.Direction.Down);
            AssertFocus(ui, "title-new");
            yield return Move(ui, NavigationMoveEvent.Direction.Down);
            AssertFocus(ui, "title-load");
            yield return Submit(ui);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Library));
            AssertFocusClass(ui, "lib-row", "Load Area opens with its selected row focused");
            yield return Press(Key.Escape);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title), "Esc goes back");
            yield return Frames(2);
            AssertOnScreen(ui);

            // S8 Settings: Tab reaches it; focus stays inside; the theme and the scale change by keyboard; Esc closes.
            yield return TabTo(ui, "title-settings");
            yield return Submit(ui);
            Assert.That(flow.Screens.OverlayOpen, Is.True);
            AssertFocus(ui, "settings-tab-interface");
            for (int i = 0; i < 12; i++)
            {
                yield return Move(ui, NavigationMoveEvent.Direction.Next);
                Assert.That(ui.Q("settings").Contains((VisualElement)Focused(ui)), $"Tab stays inside Settings ({Name(Focused(ui))})");
                AssertOnScreen(ui);
            }
            yield return TabTo(ui, "theme-light");
            yield return Submit(ui);
            Assert.That(UiPreferences.Theme, Is.EqualTo(UiTheme.Light), "Enter on Light");
            yield return TabTo(ui, scale < UiPreferences.MaxScalePercent ? "scale-up" : "scale-down");
            yield return Submit(ui);
            Assert.That(UiPreferences.ScalePercent, Is.EqualTo(scale < UiPreferences.MaxScalePercent ? scale + 5 : scale - 5), "Enter on the stepper");
            yield return Frames(3);
            AssertOnScreen(ui);
            UiPreferences.SetScale(scale, remember: false);
            UiPreferences.SetChoice(UiThemeChoice.Dark, remember: false);
            yield return Press(Key.Escape);
            Assert.That(flow.Screens.OverlayOpen, Is.False, "Esc closes Settings");
            AssertFocus(ui, "title-settings", "focus goes back where it was");

            // S9 Credits.
            yield return TabTo(ui, "title-credits");
            yield return Submit(ui);
            AssertFocus(ui, "credits-close");
            yield return Press(Key.Escape);
            Assert.That(flow.Screens.OverlayOpen, Is.False);

            // S2 Manage Areas and S11 confirm: Delete on a row asks, Cancel (focused first) keeps it.
            yield return TabTo(ui, "title-manage");
            yield return Submit(ui);
            AssertFocusClass(ui, "lib-row");
            yield return Press(Key.Delete);
            Assert.That(flow.Screens.ConfirmOpen, Is.True, "Delete asks first");
            AssertFocus(ui, "confirm-cancel", "the safe choice has focus");
            yield return Submit(ui);
            Assert.That(flow.Screens.ConfirmOpen, Is.False);
            Assert.That(Directory.Exists(_package), Is.True, "Cancel deleted nothing");
            AssertFocusClass(ui, "lib-row", "focus goes back to the row");
            yield return Press(Key.Escape);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));

            // S5 quality card: Open area is focused; Right reaches Load Area; Esc backs out.
            flow.Controller.DownloadFinished(_package);
            yield return Frames(3);
            AssertFocus(ui, "qc-open");
            yield return Move(ui, NavigationMoveEvent.Direction.Right);
            AssertFocus(ui, "qc-library");
            yield return Press(Key.Escape);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Library));
            yield return Press(Key.Escape);
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));

            // The game: Enter on the first sign opens the mountain; Esc opens the menu with Resume focused, the
            // arrows walk it, focus stays in it, Esc closes it.
            yield return TabTo(ui, "title-continue");
            yield return Submit(ui);
            yield return Frames(2);
            yield return WaitForMountain(120, inGame: true);
            yield return Frames(3);
            Assert.That(flow.Controller.InGame, Is.True);
            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            var hud = viewer.Hud.Document.rootVisualElement;
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.MenuOpen, Is.True, "Esc opens the in-game menu");
            AssertFocus(hud, "menu-resume");
            Assert.That(viewer.Hud.HasKeyboard, Is.True, "the camera leaves the keys alone");
            yield return Move(hud, NavigationMoveEvent.Direction.Down);
            AssertOnScreen(hud);
            Assert.That(hud.Q("menu").Contains((VisualElement)Focused(hud)), "Down stays in the menu");
            yield return TabTo(hud, "menu-settings");
            yield return Submit(hud);
            Assert.That(viewer.Hud.MenuOpen, Is.False);
            Assert.That(flow.Screens.OverlayOpen, Is.True, "the menu's Settings opens the Settings window");
            AssertFocus(ui, "settings-tab-interface");
            yield return Press(Key.Escape);
            Assert.That(flow.Screens.OverlayOpen, Is.False);
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.MenuOpen, Is.True);
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.MenuOpen, Is.False, "Esc closes the menu");

            // The HUD (task P2-02). T opens the Toolbox, and while it's open letters are its tools; Tab swaps it for
            // Analysis; Esc closes what's open before it opens the menu.
            yield return Press(Key.T);
            Assert.That(viewer.Hud.ToolboxOpen, Is.True, "T opens the Toolbox");
            yield return Frames(10);
            Assert.That(viewer.Camera.LettersToTools, Is.False, "the tools are placeholders until Phase 3: WASD still pans");
            AssertShownOnScreen(hud, "toolbox");
            yield return Press(Key.Tab);
            Assert.That(viewer.Hud.AnalysisOpen && !viewer.Hud.ToolboxOpen, Is.True, "Tab opens Analysis in the Toolbox's place");
            AssertShownOnScreen(hud, "analysis");
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.AnalysisOpen || viewer.Hud.MenuOpen, Is.False, "Esc closes Analysis first");
            yield return Frames(10);
            Assert.That(viewer.Camera.LettersToTools, Is.False);

            // The clock runs the sun: Space starts it, 3 picks the speed, Space stops it.
            Assert.That(viewer.Clock.Paused, Is.True, "the view opens paused");
            yield return Press(Key.Space);
            Assert.That(viewer.Clock.Paused, Is.False, "Space starts the clock");
            yield return Press(Key.Digit3);
            Assert.That(viewer.Clock.Speed, Is.EqualTo(3));
            var before = viewer.Clock.Now;
            yield return Frames(20);
            Assert.That(viewer.Clock.Now, Is.Not.EqualTo(before), "the clock runs");
            yield return Press(Key.Space);
            Assert.That(viewer.Clock.Paused, Is.True);

            // The menu's quick switches by keyboard alone: Tab reaches them inside the menu, Enter switches.
            bool docked = MountainPlanner.UI.Hud.HudPreferences.Docked;
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.MenuOpen, Is.True);
            yield return TabTo(hud, "menu-theme-light");
            yield return Submit(hud);
            Assert.That(UiPreferences.Theme, Is.EqualTo(UiTheme.Light), "Enter on the menu's Light");
            yield return TabTo(hud, docked ? "menu-dock-floating" : "menu-dock-docked");
            yield return Submit(hud);
            Assert.That(MountainPlanner.UI.Hud.HudPreferences.Docked, Is.EqualTo(!docked), "Enter on the menu's status bar switch");
            AssertShownOnScreen(hud, "bar");
            MountainPlanner.UI.Hud.HudPreferences.SetDocked(docked);   // as the player had it
            UiPreferences.SetChoice(UiThemeChoice.Dark, remember: false);
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.MenuOpen, Is.False);

            // The map layers dropdown: the top-right button opens it; the arrows reach its rows; Enter switches a layer.
            viewer.Hud.ToggleLayers();
            yield return Frames(3);
            UiFocus.UseKeyboard(hud);
            hud.Q<Button>("layer-snow").Focus();
            yield return Frames(2);
            yield return Move(hud, NavigationMoveEvent.Direction.Down);
            AssertFocus(hud, "layer-trees", "Down walks the layers");
            yield return Submit(hud);
            yield return Frames(10);
            Assert.That(hud.Q("layer-trees").ClassListContains("row--on"), Is.False, "Enter switched the trees off");
            yield return Submit(hud);
            yield return Frames(10);
            Assert.That(hud.Q("layer-trees").ClassListContains("row--on"), Is.True);
            yield return Press(Key.Escape);   // lets go of the keyboard
            yield return Press(Key.Escape);   // closes the dropdown
            Assert.That(viewer.Hud.LayersOpen, Is.False, "Esc closes the dropdown");

            // The resort's stats: a window over a scrim with ✕ focused; focus stays inside; Esc closes it.
            viewer.Hud.SetStats(true);
            yield return Frames(3);
            AssertFocus(hud, "rstats-x");
            yield return Move(hud, NavigationMoveEvent.Direction.Next);
            Assert.That(hud.Q("rstats").Contains((VisualElement)Focused(hud)), "focus stays in the stats window");
            yield return Press(Key.Escape);
            Assert.That(viewer.Hud.StatsOpen, Is.False, "Esc closes the stats");

            // 0 B: the HUD's refresh allocates nothing with the clock running at full speed and the pointer moving over
            // an info layer (each figure's text is made the first time it shows, so a second pass over the same
            // figures must allocate nothing).
            if (scale == 100) HudRefreshAllocatesNothing(viewer.Hud);
        }

        /// <summary>WASD and the arrows pan the map in the game, and keep panning: the HUD doesn't take them (owner's report, 2026-10-06).</summary>
        [UnityTest]
        public IEnumerator WasdPansTheMap()
        {
            Http.NetworkDisabled = true;
            UiPreferences.SetChoice(UiThemeChoice.Dark, remember: false);
            UiPreferences.SetScale(100, remember: false);
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120);
            yield return Frames(3);
            var ui = flow.Screens.Document.rootVisualElement;
            yield return TabTo(ui, "title-continue");
            yield return Submit(ui);
            yield return WaitForMountain(120, inGame: true);
            yield return Frames(10);
            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            var hud = viewer.Hud.Document.rootVisualElement;
            // The player's UI hears WASD and the arrows as navigation too: with nothing focused it must not pick a HUD
            // control (that took the keys from the camera), even after Tab put the UI in keyboard mode.
            yield return Press(Key.Tab);
            yield return Press(Key.Tab);
            foreach (var d in new[] { NavigationMoveEvent.Direction.Down, NavigationMoveEvent.Direction.Right, NavigationMoveEvent.Direction.Next })
            {
                yield return Move(hud, d);
                Assert.That(Focused(hud) is VisualElement f && hud.Contains(f), Is.False, $"navigation {d} picked {Name(Focused(hud))}");
            }
            // With the Toolbox open (its tools are placeholders until Phase 3) the letters still pan.
            viewer.Hud.SetToolbox(true);
            yield return Frames(10);
            foreach (var key in new[] { Key.W, Key.A, Key.S, Key.D, Key.UpArrow, Key.W })
            {
                var before = viewer.Camera.Target;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
                for (float until = Time.realtimeSinceStartup + 0.4f; Time.realtimeSinceStartup < until;) yield return null;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
                yield return Frames(3);
                Assert.That(viewer.Hud.HasKeyboard, Is.False, $"{key} didn't hand the keyboard to the HUD");
                Assert.That(Vector3.Distance(viewer.Camera.Target, before), Is.GreaterThan(1f), $"{key} pans the map");
            }
        }

        static void HudRefreshAllocatesNothing(MountainHud hud)
        {
            void Pass()
            {
                var t = new MountainPlanner.Simulation.ViewTime(2026, 15, 6 * 3600);
                for (int i = 0; i < 600; i++)
                {
                    t = MountainPlanner.Simulation.ViewClockRunner.Add(t, 180);   // speed 4 at 10 Hz
                    hud.SetClock(t, 4, false);
                    hud.SetLayer(MountainPlanner.Presentation.MapLayers.SlopeAngle, true);
                    hud.SetLegend(MountainPlanner.Presentation.MapLayers.SlopeAngle, true);
                    hud.SetElevation(2000 + i * 3.7f);
                    hud.SetInfoReadout(MountainPlanner.Presentation.MapLayers.SlopeAngle, i % 90, 0, float.NaN);
                }
                hud.SetInfoReadout(null, float.NaN, 0, float.NaN);
            }
            hud.PrepareElevations(1500, 4500);   // the readout's text for every elevation in the pass
            Pass();
            Pass();   // warm: the legend's two cards are built, every figure made
            long before = GC.GetAllocatedBytesForCurrentThread();
            Pass();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.EqualTo(0), "600 refreshes allocated nothing");
        }

        /// <summary>A HUD part is shown and wholly on the screen.</summary>
        static void AssertShownOnScreen(VisualElement root, string name)
        {
            var e = root.Q(name);
            Assert.That(e != null && UiFocus.IsShown(e), $"{name} is shown");
            var b = e.worldBound;
            var screen = root.panel.visualTree.worldBound;
            Assert.That(b.xMin >= screen.xMin - 1 && b.yMin >= screen.yMin - 1 && b.xMax <= screen.xMax + 1 && b.yMax <= screen.yMax + 1,
                $"{name} at {b} is on the {screen.width:F0}×{screen.height:F0} screen");
        }

        // ---------- helpers ----------

        /// <summary>Waits for a mountain on screen: behind the title, or opened in the game.</summary>
        static IEnumerator WaitForMountain(float seconds, bool inGame = false)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                var viewer = Object.FindAnyObjectByType<MountainViewer>();
                if (viewer != null && viewer.Camera != null && viewer.Camera.Surface != null && MountainViewer.TitleMode != inGame) yield break;
                yield return null;
            }
            Assert.Fail("The mountain didn't open in time.");
        }

        /// <summary>
        /// At least n frames and 0.1 s, as a player's pause between keys: UI Toolkit's scheduler moves focus on its
        /// next tick (16 ms or more), and batch-mode frames take about a millisecond.
        /// </summary>
        static IEnumerator Frames(int n)
        {
            float until = Time.realtimeSinceStartup + 0.1f;
            for (int i = 0; i < n || Time.realtimeSinceStartup < until; i++) yield return null;
        }

        static Focusable Focused(VisualElement root) => root.panel?.focusController?.focusedElement;

        static string Name(Focusable f) => f is VisualElement e ? (string.IsNullOrEmpty(e.name) ? e.GetType().Name : e.name) : "nothing";

        static void Send(VisualElement root, EventBase e)
        {
            var target = Focused(root) as VisualElement ?? root;
            e.target = target;
            target.SendEvent(e);
        }

        static IEnumerator Move(VisualElement root, NavigationMoveEvent.Direction direction)
        {
            using (var e = NavigationMoveEvent.GetPooled(direction)) Send(root, e);
            yield return Frames(2);
        }

        static IEnumerator Submit(VisualElement root)
        {
            using (var e = NavigationSubmitEvent.GetPooled()) Send(root, e);
            yield return Frames(3);
        }

        /// <summary>Tab (Next) until the named control has focus, as a player would; fails after 30 presses.</summary>
        static IEnumerator TabTo(VisualElement root, string name)
        {
            for (int i = 0; i < 30 && Name(Focused(root)) != name; i++) yield return Move(root, NavigationMoveEvent.Direction.Next);
            AssertFocus(root, name, "Tab reaches it");
        }

        /// <summary>A key on the keyboard device, as the game's Esc and Delete handling reads it.</summary>
        IEnumerator Press(Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            yield return Frames(2);
            // UI Toolkit hears Delete as a key event on whatever has focus.
            if (key == UnityEngine.InputSystem.Key.Delete)
                foreach (var doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                    if (Focused(doc.rootVisualElement) is VisualElement f && doc.rootVisualElement.Contains(f))
                        using (var e = KeyDownEvent.GetPooled('\0', KeyCode.Delete, EventModifiers.None)) { e.target = f; f.SendEvent(e); }
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return Frames(3);
        }

        static void AssertFocus(VisualElement root, string name, string why = null)
        {
            Assert.That(Name(Focused(root)), Is.EqualTo(name), why);
            AssertOnScreen(root);
        }

        static void AssertFocusClass(VisualElement root, string cls, string why = null)
        {
            var f = Focused(root) as VisualElement;
            Assert.That(f != null && f.ClassListContains(cls), Is.True, $"{why} (focus is on {Name(f)})");
            AssertOnScreen(root);
        }

        /// <summary>The focused control is shown and wholly on the screen.</summary>
        static void AssertOnScreen(VisualElement root)
        {
            if (!(Focused(root) is VisualElement f)) return;
            Assert.That(UiFocus.IsShown(f), $"{Name(f)} is shown");
            var b = f.worldBound;
            var screen = root.panel.visualTree.worldBound;
            Assert.That(b.xMin >= screen.xMin - 1 && b.yMin >= screen.yMin - 1 && b.xMax <= screen.xMax + 1 && b.yMax <= screen.yMax + 1,
                $"{Name(f)} at {b} is on the {screen.width:F0}×{screen.height:F0} screen");
        }
    }
}
