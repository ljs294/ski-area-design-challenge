using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
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
            // A rename that failed half way doesn't carry into the next scale's run (task P2-04).
            string view = Path.Combine(_package, ViewState.FileName);
            if (File.Exists(view)) File.Delete(view);
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
            yield return SettingsByKeyboard(flow, ui);
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
            yield return LibraryActionsByKeyboard(flow, ui);
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
            // The menu's Controls opens Settings on its Controls page (task P2-05).
            yield return TabTo(hud, "menu-controls");
            yield return Submit(hud);
            Assert.That(flow.Screens.OverlayOpen, Is.True, "the menu's Controls opens the Settings window");
            Assert.That(flow.Screens.Settings.Page, Is.EqualTo(SettingsWindow.ControlsPage), "on its Controls page");
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

        /// <summary>
        /// Task 08's library states by keyboard alone: an area from a newer game is reached by the arrows but never
        /// opens (Enter does nothing), and Delete still asks to remove it; a library folder from a newer game lists
        /// nothing, says why, and puts focus on New Area.
        /// </summary>
        [UnityTest]
        public IEnumerator NewerAreasWorkByKeyboardAlone([Values(50, 100, 150)] int scale)
        {
            string fixtures = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TestData", "formats", "demo");
            string newer = Path.Combine(ResortLibrary.ResortsFolder(_root), "newer-area");
            string layout = Path.Combine(_root, LibraryIndex.FileName);
            Directory.CreateDirectory(newer);
            File.Copy(Path.Combine(fixtures, "newer-area", ResortPackage.ManifestFile), Path.Combine(newer, ResortPackage.ManifestFile), true);
            try
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

                // Load Area: the mountain that opens is selected first; Down reaches the greyed row; Enter leaves it shut.
                yield return TabTo(ui, "title-load");
                yield return Submit(ui);
                AssertFocusClass(ui, "lib-row");
                Assert.That(flow.Screens.SelectedRow.CanOpen, Is.True, "an area that opens is selected first");
                yield return Move(ui, NavigationMoveEvent.Direction.Down);
                AssertFocusClass(ui, "lib-row--newer", "Down reaches the newer game's area");
                Assert.That(flow.Screens.SelectedRow.IsNewer, Is.True);
                yield return Submit(ui);
                yield return Press(Key.Enter);
                Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Library), "Enter doesn't open a newer game's area");
                Assert.That(flow.Controller.InGame, Is.False);
                AssertFocusClass(ui, "lib-row--newer", "focus stays on it");
                yield return Press(Key.Escape);
                Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));

                // Manage Areas: Delete on the greyed row asks; Cancel (focused first) keeps it.
                yield return TabTo(ui, "title-manage");
                yield return Submit(ui);
                yield return Move(ui, NavigationMoveEvent.Direction.Down);
                AssertFocusClass(ui, "lib-row--newer");
                yield return Press(Key.Delete);
                Assert.That(flow.Screens.ConfirmOpen, Is.True, "Delete asks first");
                AssertFocus(ui, "confirm-cancel");
                yield return Submit(ui);
                Assert.That(flow.Screens.ConfirmOpen, Is.False);
                Assert.That(Directory.Exists(newer), Is.True, "Cancel deleted nothing");
                AssertFocusClass(ui, "lib-row--newer", "focus goes back to the row");
                yield return Press(Key.Escape);
                Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));

                // A library folder from a newer game: Load Area lists nothing, says why, and focuses New Area.
                File.WriteAllText(layout, File.ReadAllText(Path.Combine(fixtures, LibraryIndex.FileName)));
                yield return TabTo(ui, "title-load");
                yield return Submit(ui);
                yield return Frames(3);
                Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Library));
                Assert.That(flow.Screens.SelectedRow, Is.Null, "nothing is listed");
                Assert.That(ui.Q<Label>("library-empty")?.text ?? "", Does.Contain("newer version of Mountain Planner"), "it says why");
                AssertFocus(ui, "library-new", "focus goes to New Area");
                yield return Press(Key.Escape);
                Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Title));
            }
            finally
            {
                if (File.Exists(layout)) File.Delete(layout);
                if (Directory.Exists(newer)) Directory.Delete(newer, true);
            }
        }

        /// <summary>
        /// Task P2-04 in Manage Areas, by keyboard: F2 renames (Enter confirms, Esc cancels, the name survives a fresh
        /// scan), the sort is remembered, and Free space removes an older version's cache after asking.
        /// </summary>
        IEnumerator LibraryActionsByKeyboard(AppFlow flow, VisualElement ui)
        {
            var prompt = ui.Q("prompt");
            var field = ui.Q<TextField>("prompt-field");
            Assert.That(ResortLibrary.Scan(_root).Single().Name, Is.EqualTo("Jackson Hole"), "starts from the downloaded name");
            yield return Press(Key.F2);
            Assert.That(flow.Screens.PromptOpen, Is.True, "F2 opens Rename");
            Assert.That(field.Contains((VisualElement)Focused(ui)) || Focused(ui) == field, Is.True, $"the name field has focus (it's on {Name(Focused(ui))})");
            AssertOnScreen(ui);
            Assert.That(field.value, Is.EqualTo("Jackson Hole"), "it starts from the current name");
            AssertShownOnScreen(ui, "prompt-ok");
            AssertShownOnScreen(ui, "prompt-cancel");
            field.value = "Teton Village";   // as typed
            yield return Press(Key.Enter);
            Assert.That(flow.Screens.PromptOpen, Is.False, "Enter renames");
            Assert.That(ResortLibrary.Scan(_root).Single().Name, Is.EqualTo("Teton Village"), "saved: a fresh scan (a restart) reads it");
            Assert.That(((Label)ui.Q(className: "lib-name")).text, Is.EqualTo("Teton Village"), "the row shows the new name");
            AssertFocusClass(ui, "lib-row", "focus goes back to the row");

            yield return Press(Key.F2);
            field.value = "Something else";
            yield return Press(Key.Escape);
            Assert.That(flow.Screens.PromptOpen, Is.False, "Esc cancels");
            Assert.That(flow.Controller.Screen, Is.EqualTo(FlowScreen.Library), "and only the dialog closes");
            Assert.That(ResortLibrary.Scan(_root).Single().Name, Is.EqualTo("Teton Village"));
            AssertFocusClass(ui, "lib-row");

            yield return Press(Key.F2);
            field.value = "Jackson Hole";   // back to the downloaded name, for the rest of the tour
            yield return Press(Key.Enter);
            Assert.That(ResortLibrary.Scan(_root).Single().Name, Is.EqualTo("Jackson Hole"));
            Assert.That(prompt.ClassListContains("hidden"), Is.True);

            // The sort is remembered across a restart (a setting).
            bool hadSort = PlayerPrefs.HasKey("MountainPlanner.LibrarySort");
            int savedSort = PlayerPrefs.GetInt("MountainPlanner.LibrarySort", 0);
            try
            {
                yield return TabTo(ui, "sort-name");
                yield return Submit(ui);
                Assert.That(PlayerPrefs.GetInt("MountainPlanner.LibrarySort", -1), Is.EqualTo((int)LibrarySort.Name));
            }
            finally
            {
                if (hadSort) PlayerPrefs.SetInt("MountainPlanner.LibrarySort", savedSort);
                else PlayerPrefs.DeleteKey("MountainPlanner.LibrarySort");
            }

            // Free space: an older version's cache is measured off the main thread, then offered.
            string old = TerrainCache.FolderFor(_package, TerrainCache.Version - 1);
            Directory.CreateDirectory(old);
            File.WriteAllBytes(Path.Combine(old, "t0_0.h16"), new byte[3_000_000]);
            yield return TabTo(ui, "sort-opened");
            yield return Submit(ui);   // a redraw rescans and measures
            var free = ui.Q<Button>("library-free");
            float until = Time.realtimeSinceStartup + 10;
            while (free.ClassListContains("hidden") && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(free.ClassListContains("hidden"), Is.False, "Free space shows once there's something to free");
            Assert.That(free.text, Is.EqualTo("Free 3 MB"));
            yield return TabTo(ui, "library-free");
            yield return Submit(ui);
            Assert.That(flow.Screens.ConfirmOpen, Is.True, "Free space asks first");
            AssertFocus(ui, "confirm-cancel");
            yield return Move(ui, NavigationMoveEvent.Direction.Right);
            AssertFocus(ui, "confirm-ok");
            yield return Submit(ui);
            until = Time.realtimeSinceStartup + 10;
            while (Directory.Exists(old) && Time.realtimeSinceStartup < until) yield return null;
            Assert.That(Directory.Exists(old), Is.False, "the older cache is gone");
            Assert.That(TerrainCache.IsCurrent(_package, ResortPackage.ReadManifest(_package)), Is.True, "the current one stays");
            yield return Frames(3);
            Assert.That(free.ClassListContains("hidden"), Is.True, "nothing left to free");
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

        /// <summary>
        /// Task P2-05: every Settings page opens from its tab by keyboard, Tab walks its rows with each focused
        /// control on screen (the page scrolls to it), a preset changes Unity's quality level live, and a key is
        /// rebound by pressing it (Esc cancels listening without closing the window). Nothing is remembered.
        /// </summary>
        IEnumerator SettingsByKeyboard(AppFlow flow, VisualElement ui)
        {
            var settings = flow.Screens.Settings;
            settings.Remember = false;
            var graphics = QualityPresets.Options;
            int level = QualitySettings.GetQualityLevel();
            try
            {
                var nav = ui.Q("settings-nav");
                foreach (string id in new[] { "interface", "units", "graphics", "display", "controls", "data" })
                {
                    // From the first control in a row, Left goes back to the categories (the Controls page is long for Tab alone).
                    yield return BackToCategories(ui);
                    Assert.That(nav.Contains((VisualElement)Focused(ui)), $"Left reaches the categories ({Name(Focused(ui))})");
                    yield return TabTo(ui, "settings-tab-" + id);
                    yield return Submit(ui);
                    Assert.That(UiFocus.IsShown(ui.Q("settings-" + id)), $"the {id} page shows");
                    for (int i = 0; i < 24; i++)
                    {
                        yield return Move(ui, NavigationMoveEvent.Direction.Next);
                        Assert.That(ui.Q("settings").Contains((VisualElement)Focused(ui)), $"Tab stays inside Settings ({Name(Focused(ui))})");
                        AssertOnScreen(ui);
                    }
                }

                // Graphics: ‹ on the preset steps High to Medium, live.
                yield return BackToCategories(ui);
                yield return TabTo(ui, "settings-tab-graphics");
                yield return Submit(ui);
                QualityPresets.Apply(GraphicsOptions.For(QualityPreset.High));
                settings.Refresh();
                yield return TabTo(ui, "quality-prev");
                yield return Submit(ui);
                Assert.That(QualityPresets.Options.Preset, Is.EqualTo(QualityPreset.Medium));
                Assert.That(QualitySettings.names[QualitySettings.GetQualityLevel()], Is.EqualTo("Medium"), "Unity's quality level changed at once");
                Assert.That(ui.Q<Label>("quality-value").text, Is.EqualTo("Medium"));

                // Controls: Enter on Move forward's key listens; B binds it; Esc while listening only cancels.
                yield return BackToCategories(ui);
                yield return TabTo(ui, "settings-tab-controls");
                yield return Submit(ui);
                yield return TabTo(ui, "key-MoveForward-0");
                yield return Submit(ui);
                Assert.That(KeyBindings.Listening, Is.True, "Enter on a key listens for the next one");
                yield return Press(Key.B);
                Assert.That(KeyBindings.Listening, Is.False);
                Assert.That(KeyBindings.Caption(GameAction.MoveForward, 0), Is.EqualTo("B"), "B moves forward now");
                Assert.That(ui.Q<Button>("key-MoveForward-0").text, Is.EqualTo("B"));
                yield return Frames(3);
                yield return TabTo(ui, "key-MoveBack-0");
                yield return Submit(ui);
                Assert.That(KeyBindings.Listening, Is.True);
                yield return Press(Key.Escape);
                Assert.That(KeyBindings.Listening, Is.False, "Esc cancels listening");
                Assert.That(flow.Screens.OverlayOpen, Is.True, "and leaves Settings open");
                Assert.That(KeyBindings.Caption(GameAction.MoveBack, 0), Is.EqualTo("S"), "the key is unchanged");
                yield return Frames(3);
            }
            finally
            {
                KeyBindings.ResetAll(remember: false);
                QualityPresets.Apply(graphics);
                if (QualitySettings.GetQualityLevel() != level) QualitySettings.SetQualityLevel(level, true);
                settings.Remember = true;
            }
            yield return BackToCategories(ui);
            for (int i = 0; i < 8 && Name(Focused(ui)) != "settings-tab-interface"; i++) yield return Move(ui, NavigationMoveEvent.Direction.Up);
            AssertFocus(ui, "settings-tab-interface", "Up walks the categories");
            yield return Submit(ui);
        }

        /// <summary>Left from the first control in a row reaches the categories; a slider keeps Left, so Up off it first.</summary>
        static IEnumerator BackToCategories(VisualElement ui)
        {
            var nav = ui.Q("settings-nav");
            for (int i = 0; i < 6 && !nav.Contains((VisualElement)Focused(ui)); i++)
                yield return Move(ui, Focused(ui) is BaseSlider<int> ? NavigationMoveEvent.Direction.Up : NavigationMoveEvent.Direction.Left);
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
            // UI Toolkit hears Delete, F2 and Enter as key events on whatever has focus.
            KeyCode code = key == Key.Delete ? KeyCode.Delete : key == Key.F2 ? KeyCode.F2 : key == Key.Enter ? KeyCode.Return : KeyCode.None;
            if (code != KeyCode.None)
                foreach (var doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                    if (Focused(doc.rootVisualElement) is VisualElement f && doc.rootVisualElement.Contains(f))
                        using (var e = KeyDownEvent.GetPooled('\0', code, EventModifiers.None)) { e.target = f; f.SendEvent(e); }
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
