using System;
using System.Collections.Generic;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// S8 Settings (0.4 S8, decision U3; task P2-05), in the accepted mockup's window: categories down the left,
    /// label-and-control rows on the right, Restore defaults and Done at the foot. The pages are built here from
    /// code, as the mockup builds them from its SETTINGS table: Interface, Units and time, Graphics, Display,
    /// Controls and Data. Every row applies at once and is remembered; the only thing Unity needs a moment for is a
    /// display change, which asks "Keep these settings?" and goes back by itself after 15 seconds.
    ///
    /// Nothing runs per frame: rows redraw when something changes, and the 15-second countdown ticks four times a
    /// second only while it shows, from strings made once.
    /// </summary>
    public sealed class SettingsWindow
    {
        public const int KeepSeconds = 15;
        /// <summary>The pages' order, for opening one directly (the menu's Controls).</summary>
        public const int InterfacePage = 0, UnitsPage = 1, GraphicsPage = 2, DisplayPage = 3, ControlsPage = 4, DataPage = 5;

        /// <summary>Data › Library folder › Open: show the folder.</summary>
        public event Action LibraryOpenChosen;
        /// <summary>Data › Library folder › Change: the folder as typed (the app flow checks and applies it).</summary>
        public event Action<string> LibraryFolderChosen;
        /// <summary>The Data page opened: the app flow measures the disk use (off the main thread) and calls <see cref="ShowDisk"/>.</summary>
        public event Action DataShown;
        /// <summary>Data › Free space, confirmed.</summary>
        public event Action FreeSpaceConfirmed;

        /// <summary>False for captures and tests: changes apply for this run only, and the player's settings stay as they were.</summary>
        public bool Remember = true;

        readonly FlowScreens _screens;
        readonly VisualElement _window, _nav;
        readonly ScrollView _scroll;
        readonly List<Button> _tabs = new List<Button>();
        readonly List<VisualElement> _pages = new List<VisualElement>();
        readonly List<Action> _refresh = new List<Action>();
        readonly string[] _countdown = new string[KeepSeconds + 1];
        int _page;

        // Display: the keep-or-revert bar.
        VisualElement _keepBar;
        Label _keepText;
        IVisualElementScheduledItem _keepTick;
        float _keepUntil;
        int _keepShown = -1;
        FullScreenMode _keepMode;
        Vector2Int _keepSize;
        List<Vector2Int> _resolutions = new List<Vector2Int>();

        // Controls: the key being listened for.
        Button _listeningButton;
        int _listenEndFrame = -10;

        // Data.
        Label _libraryPath, _diskText;
        Button _libraryChange, _freeSpace;
        long _freeable;

        public SettingsWindow(VisualElement settings, FlowScreens screens)
        {
            _screens = screens;
            _window = settings;
            _nav = settings.Q("settings-nav");
            _scroll = settings.Q<ScrollView>("settings-page");
            for (int i = 0; i <= KeepSeconds; i++) _countdown[i] = $"Going back to the old display settings in {i} s.";
            BuildInterface();
            BuildUnits();
            BuildGraphics();
            BuildDisplay();
            BuildControls();
            BuildData();
            // While a key is being listened for, the window gives its own keys up: Space, Enter and the arrows
            // must not press or move between controls, and Esc only cancels.
            EventCallback<EventBase> swallow = e =>
            {
                if (!CapturingKeys) return;
                e.StopImmediatePropagation();
                _window.focusController?.IgnoreEvent(e);
            };
            _window.RegisterCallback<KeyDownEvent>(e => swallow(e), TrickleDown.TrickleDown);
            _window.RegisterCallback<NavigationMoveEvent>(e => swallow(e), TrickleDown.TrickleDown);
            _window.RegisterCallback<NavigationSubmitEvent>(e => swallow(e), TrickleDown.TrickleDown);
            _window.RegisterCallback<NavigationCancelEvent>(e => swallow(e), TrickleDown.TrickleDown);
            // The pages are longer than the window: the keyboard's focus scrolls into view.
            _scroll.RegisterCallback<FocusInEvent>(e =>
            {
                if (e.target is VisualElement target && _scroll.contentContainer.Contains(target)) _scroll.schedule.Execute(() => _scroll.ScrollTo(target));
            });
            UiPreferences.Changed += RefreshIfOpen;
            FlowUnits.Changed += RefreshIfOpen;
            KeyBindings.Changed += RefreshIfOpen;
        }

        /// <summary>Lets go of the game-wide events (the document is being rebuilt or closed).</summary>
        public void Dispose()
        {
            StopListening();
            UiPreferences.Changed -= RefreshIfOpen;
            FlowUnits.Changed -= RefreshIfOpen;
            KeyBindings.Changed -= RefreshIfOpen;
        }

        void RefreshIfOpen()
        {
            if (IsOpen) Refresh();
        }

        bool IsOpen => !_window.ClassListContains("hidden");

        /// <summary>The first page's tab, which takes focus when the window opens.</summary>
        public Button FirstTab => _tabs[0];

        /// <summary>True while a key is being listened for, and for the frame after: Esc then belongs to the window, not the app flow.</summary>
        public bool CapturingKeys => KeyBindings.Listening || Time.frameCount <= _listenEndFrame + 1;

        /// <summary>Redraws every row (on open and after any change).</summary>
        public void Refresh()
        {
            for (int i = 0; i < _refresh.Count; i++) _refresh[i]();
        }

        public void Opened()
        {
            Refresh();
            ShowPage(0);
        }

        public void Closed()
        {
            StopListening();
            if (_keepBar != null && !_keepBar.ClassListContains("hidden")) Keep();   // closing the window keeps the display as it is
        }

        /// <summary>Shows one category: 0 Interface, 1 Units and time, 2 Graphics, 3 Display, 4 Controls, 5 Data.</summary>
        public void ShowPage(int page)
        {
            StopListening();
            _page = Mathf.Clamp(page, 0, _pages.Count - 1);
            for (int i = 0; i < _pages.Count; i++)
            {
                FlowScreens.Show(_pages[i], i == _page);
                _tabs[i].EnableInClassList("mp-nav__item--on", i == _page);
            }
            _scroll.scrollOffset = Vector2.zero;
            if (_pages[_page].name == "settings-display") RefreshResolutions();
            if (_pages[_page].name == "settings-data") DataShown?.Invoke();
        }

        /// <summary>The page shown (0–5).</summary>
        public int Page => _page;

        // ---------- the pages ----------

        void BuildInterface()
        {
            var page = AddPage("interface", "Interface");
            Row(page, "Theme", "Auto follows the sun: light by day, dark at night.",
                Seg("theme", new[] { "dark", "light", "auto" }, new[] { "Dark", "Light", "Auto" },
                    () => (int)UiPreferences.Choice, i => UiPreferences.SetChoice((UiThemeChoice)i, Remember)));
            Row(page, "Interface scale", "The size of the bar, panels and text.",
                Step("scale-down", "scale-value", "scale-up", () => (UiPreferences.ScalePercent - UiPreferences.MinScalePercent) / UiPreferences.ScaleStepPercent,
                     () => (UiPreferences.MaxScalePercent - UiPreferences.MinScalePercent) / UiPreferences.ScaleStepPercent + 1,
                     i => (UiPreferences.MinScalePercent + i * UiPreferences.ScaleStepPercent) + "%",
                     i => UiPreferences.SetScale(UiPreferences.MinScalePercent + i * UiPreferences.ScaleStepPercent, Remember),
                     "Smaller (5% steps, down to 50%)", "Larger (5% steps, up to 150%)"));
        }

        void BuildUnits()
        {
            var page = AddPage("units", "Units and time");
            Row(page, "Units", "Lengths, heights, temperatures and snowfall. Steepness is always a grade in percent.",
                Seg("units", new[] { "metric", "imperial" }, new[] { "Metric", "Imperial" },
                    () => FlowUnits.Imperial ? 1 : 0, i => FlowUnits.Set(i == 1, Remember)));
            var note = Note(page, "");
            _refresh.Add(() => note.text = $"{KeyBindings.Caption(GameAction.Units)} switches units in the game too.");
        }

        void BuildGraphics()
        {
            var page = AddPage("graphics", "Graphics");
            Note(page, "Changes show at once, on the mountain behind this window. Choosing a preset sets every row; changing a row makes it Custom.", "mp-pnote");
            Row(page, "Quality preset", "Low suits older graphics cards; High is the reference look.",
                Step("quality-prev", "quality-value", "quality-next",
                     () => QualityPresets.Options.Preset.HasValue ? (int)QualityPresets.Options.Preset.Value : -1, () => 4,
                     i => i < 0 ? "Custom" : ((QualityPreset)i).ToString(),
                     i => Graphics(o => o.WithPreset((QualityPreset)i)),
                     customFrom: () => (int)QualityPresets.Options.Level));
            Row(page, "Render scale", "Below 100% draws fewer pixels and scales them up: faster, softer.",
                Slider("render-scale", GraphicsOptions.MinRenderScale, GraphicsOptions.MaxRenderScale, GraphicsOptions.RenderScaleStep,
                       () => QualityPresets.Options.RenderScalePercent, v => Graphics(o => { o.RenderScalePercent = v; return o; }), v => v + "%"));
            Row(page, "Anti-aliasing", "Smooths edges. MSAA is the sharpest; FXAA and SMAA cost less.",
                Step("aa-prev", "aa-value", "aa-next", () => (int)QualityPresets.Options.Antialiasing, () => 5,
                     i => AaNames[i], i => Graphics(o => { o.Antialiasing = (Antialiasing)i; return o; })));
            Row(page, "Shadows", "The sun's shadows near the camera: map size, cascades and soft edges.",
                Step("shadows-prev", "shadows-value", "shadows-next", () => (int)QualityPresets.Options.Shadows, () => 5,
                     i => ((ShadowLevel)i).ToString(), i => Graphics(o => { o.Shadows = (ShadowLevel)i; return o; })));
            Row(page, "Shadow distance", "How far from the camera trees and terrain cast sharp shadows.",
                Slider("shadow-distance", GraphicsOptions.MinShadowDistance, GraphicsOptions.MaxShadowDistance, GraphicsOptions.ShadowDistanceStep,
                       () => QualityPresets.Options.ShadowDistance, v => Graphics(o => { o.ShadowDistance = v; return o; }), Metres));
            Row(page, "Terrain detail", "How closely the ground's shape follows the lidar in the distance.",
                Step("terrain-prev", "terrain-value", "terrain-next", () => (int)QualityPresets.Options.Terrain, () => 4,
                     i => ((World.TerrainDetail)i).ToString(), i => Graphics(o => { o.Terrain = (World.TerrainDetail)i; return o; })));
            Row(page, "Terrain shading", "The mountain's own shade: sky occlusion in valleys and its distant shadows.",
                Seg("shading", new[] { "on", "off" }, new[] { "On", "Off" },
                    () => QualityPresets.Options.TerrainShading ? 0 : 1, i => Graphics(o => { o.TerrainShading = i == 0; return o; })));
            Row(page, "Tree detail", "How far trees keep their detailed models. Auto times this PC the first time an area opens.",
                Step("trees-prev", "trees-value", "trees-next", () => (int)QualityPresets.Options.Trees, () => 5,
                     i => i == 0 && QualityPresets.AutoLodBias > 0 ? "Auto · " + TreeDetailTiming.DetailOf(QualityPresets.AutoLodBias) : ((TreeDetail)i).ToString(),
                     i => Graphics(o => o.WithTrees((TreeDetail)i))));
            var measure = Ghost("trees-measure", "Measure again", MeasureTreeDetail);
            var measureRow = Row(page, "Auto tree detail", "", measure);
            var measureDesc = measureRow.Q<Label>(className: "mp-srow__desc");
            _refresh.Add(() =>
            {
                bool auto = QualityPresets.Options.Trees == TreeDetail.Auto;
                measure.SetEnabled(auto);
                measureDesc.text = !auto ? "Choose Auto above to let the game pick."
                                 : QualityPresets.AutoLodBias > 0 ? $"Chose {TreeDetailTiming.DetailOf(QualityPresets.AutoLodBias)} for this PC and screen size."
                                 : "Not timed yet: it runs a few seconds after an area opens.";
            });
            Row(page, "Textures", "Half and Quarter save graphics memory on older cards.",
                Seg("textures", new[] { "full", "half", "quarter" }, new[] { "Full", "Half", "Quarter" },
                    () => (int)QualityPresets.Options.Textures, i => Graphics(o => { o.Textures = (TextureQuality)i; return o; })));
        }

        static readonly string[] AaNames = { "Off", "FXAA", "SMAA", "MSAA 2×", "MSAA 4×" };

        void Graphics(Func<GraphicsOptions, GraphicsOptions> change)
        {
            QualityPresets.Choose(change(QualityPresets.Options), Remember);
            Refresh();
        }

        void MeasureTreeDetail()
        {
            TreeDetailTiming.Forget();
            QualityPresets.SetAutoLodBias(0);
            if (QualityPresets.Options.Trees == TreeDetail.Auto) QualityPresets.Apply(QualityPresets.Options);   // the preset's bias until the timing ends
            if (TreeDetailTiming.Requested != null)
            {
                TreeDetailTiming.Requested();
                _screens.Toast("Timing tree detail: a few seconds");
            }
            else _screens.Toast("Tree detail will be timed when an area opens");
            Refresh();
        }

        static string Metres(int metres) =>
            FlowUnits.Imperial ? $"{Mathf.RoundToInt(metres * 3.28084f / 10f) * 10} ft" : metres + " m";

        void BuildDisplay()
        {
            var page = AddPage("display", "Display");
            _keepBar = new VisualElement { name = "display-keep" };
            _keepBar.AddToClassList("settings-keep");
            _keepBar.AddToClassList("hidden");
            var keepText = new VisualElement();
            keepText.AddToClassList("settings-keep__text");
            keepText.Add(Text("Keep these display settings?", "settings-keep__title"));
            _keepText = Text("", "settings-keep__count");
            keepText.Add(_keepText);
            _keepBar.Add(keepText);
            var revert = new Button(Revert) { name = "display-revert", text = "Go back" };
            revert.AddToClassList("mp-ghost");
            var keep = new Button(Keep) { name = "display-keep-ok", text = "Keep" };
            keep.AddToClassList("mp-go");
            keep.AddToClassList("settings-keep__ok");
            _keepBar.Add(revert);
            _keepBar.Add(keep);
            page.Add(_keepBar);
            Row(page, "Display mode", "A window, or the whole screen without a border or with the screen to itself. Alt+Enter switches too.",
                Step("display-prev", "display-value", "display-next", () => UiDisplay.Current, () => UiDisplay.Modes.Length,
                     i => UiDisplay.Names[i], i => ChangeDisplay(() => UiDisplay.Set(i))));
            Row(page, "Resolution", "Wide screens (21:9 and 32:9) show more to the sides; the height of the view stays the same.",
                Step("resolution-prev", "resolution-value", "resolution-next", () => _resolutions.IndexOf(UiDisplay.Size), () => _resolutions.Count,
                     i => i >= 0 && i < _resolutions.Count ? UiDisplay.Describe(_resolutions[i]) : UiDisplay.Describe(UiDisplay.Size),
                     i => { var size = _resolutions[i]; ChangeDisplay(() => UiDisplay.SetResolution(size)); }));
            Row(page, "V-Sync", "Waits for the screen's refresh: no tearing, a little more latency.",
                Seg("vsync", new[] { "on", "off" }, new[] { "On", "Off" },
                    () => FramePacing.VSync ? 0 : 1, i => { FramePacing.SetVSync(i == 0, Remember); Refresh(); }));
            var capRow = Row(page, "Frame rate limit", "",
                Step("fps-prev", "fps-value", "fps-next", () => Array.IndexOf(FramePacing.Caps, FramePacing.Cap), () => FramePacing.Caps.Length,
                     i => FramePacing.CapName(FramePacing.Caps[i]), i => { FramePacing.SetCap(FramePacing.Caps[i], Remember); Refresh(); }));
            var capDesc = capRow.Q<Label>(className: "mp-srow__desc");
            _refresh.Add(() => capDesc.text = FramePacing.VSync ? "V-Sync sets the pace while it's on." : "Saves power and heat when the game runs faster than you need.");
            var fovRow = Row(page, "Field of view", "",
                Slider("fov", ViewFov.Min, ViewFov.Max, 1, () => ViewFov.Vertical, v => { ViewFov.Set(v, Remember); Refresh(); }, v => v + "°"));
            var fovDesc = fovRow.Q<Label>(className: "mp-srow__desc");
            _refresh.Add(() =>
            {
                float aspect = Screen.width / (float)Mathf.Max(1, Screen.height);
                fovDesc.text = $"The view's height. On this {UiDisplay.Shape(Screen.width, Screen.height)} screen that's {ViewFov.Horizontal(aspect):0}° across.";
            });
        }

        void RefreshResolutions()
        {
            _resolutions = UiDisplay.Resolutions();
            Refresh();
        }

        /// <summary>A display change: remember how it was, apply, and ask to keep it (the old settings come back after 15 s).</summary>
        void ChangeDisplay(Action apply)
        {
            if (_keepBar.ClassListContains("hidden"))
            {
                _keepMode = Screen.fullScreenMode;
                _keepSize = UiDisplay.Size;
            }
            apply();
            _keepUntil = Time.realtimeSinceStartup + KeepSeconds;
            _keepShown = -1;
            FlowScreens.Show(_keepBar, true);
            _keepTick ??= _keepBar.schedule.Execute(TickKeep).Every(250);
            _keepTick.Resume();
            TickKeep();
            // Unity changes the window on the next frame or two; redraw then.
            _window.schedule.Execute(RefreshResolutions).StartingIn(300);
        }

        void TickKeep()
        {
            int left = Mathf.Clamp(Mathf.CeilToInt(_keepUntil - Time.realtimeSinceStartup), 0, KeepSeconds);
            if (left != _keepShown)
            {
                _keepShown = left;
                _keepText.text = _countdown[left];
            }
            if (left == 0) Revert();
        }

        void Keep()
        {
            _keepTick?.Pause();
            FlowScreens.Show(_keepBar, false);
        }

        void Revert()
        {
            Keep();
            Screen.SetResolution(_keepSize.x, _keepSize.y, _keepMode);
            _window.schedule.Execute(RefreshResolutions).StartingIn(300);
        }

        void BuildControls()
        {
            var page = AddPage("controls", "Controls");
            Row(page, "Invert scroll zoom", "Scroll down to zoom in.",
                Seg("invert-zoom", new[] { "on", "off" }, new[] { "On", "Off" },
                    () => CameraOptions.InvertZoom ? 0 : 1, i => { CameraOptions.SetInvertZoom(i == 0, Remember); Refresh(); }));
            Row(page, "Zoom speed", "The wheel and the zoom keys.",
                Seg("zoom-speed", new[] { "slow", "normal", "fast" }, new[] { "Slow", "Normal", "Fast" },
                    () => (int)CameraOptions.Speed, i => { CameraOptions.SetSpeed((ZoomSpeed)i, Remember); Refresh(); }));
            var head = new VisualElement();
            head.AddToClassList("settings-keys-head");
            head.Add(Text("Keys", "mp-page-title"));
            head.Add(new VisualElement { style = { flexGrow = 1 } });
            var reset = new Button(() => { KeyBindings.ResetAll(Remember); Refresh(); }) { name = "keys-reset", text = "Reset keys" };
            reset.AddToClassList("mp-ghost");
            head.Add(reset);
            page.Add(head);
            Note(page, "Click a key, then press the new one, with Shift, Ctrl or Alt if you like. Esc cancels; Backspace clears a second key. " +
                       "A key another action had moves to that action's old key. Esc always backs out and can't be changed; Shift held makes the camera faster.", "mp-pnote");
            string group = null;
            for (int a = 0; a < KeyBindings.Count; a++)
            {
                var info = KeyBindings.Actions[a];
                if (info.Group != group)
                {
                    group = info.Group;
                    page.Add(Text(group, "settings-group"));
                }
                var row = new VisualElement();
                row.AddToClassList("settings-key");
                row.Add(Text(info.Label, "settings-key__label"));
                var action = (GameAction)a;
                for (int s = 0; s < KeyBindings.Slots; s++)
                {
                    int slot = s;
                    var key = new Button { name = $"key-{action}-{slot}" };
                    key.AddToClassList("settings-kbd");
                    key.clicked += () => StartListening(key, action, slot);
                    row.Add(key);
                    _refresh.Add(() =>
                    {
                        if (key == _listeningButton) return;
                        string caption = KeyBindings.Caption(action, slot);
                        key.text = caption.Length > 0 ? caption : "—";
                        key.EnableInClassList("settings-kbd--empty", caption.Length == 0);
                        key.tooltip = $"{info.Label}: click, then press a key";
                    });
                }
                page.Add(row);
            }
            var esc = new VisualElement();
            esc.AddToClassList("settings-key");
            esc.Add(Text("Back out · the menu", "settings-key__label"));
            var escKey = new Button { text = "Esc" };
            escKey.AddToClassList("settings-kbd");
            escKey.SetEnabled(false);
            esc.Add(escKey);
            var none = new VisualElement();
            none.AddToClassList("settings-kbd-gap");
            esc.Add(none);
            page.Add(esc);
        }

        void StartListening(Button key, GameAction action, int slot)
        {
            if (CapturingKeys) return;   // Space or Enter on the button that just took a key presses it again
            StopListening();
            _listeningButton = key;
            key.text = "Press a key…";
            key.AddToClassList("settings-kbd--listening");
            KeyBindings.Listen(action, slot, Remember, (outcome, swapped) =>
            {
                _listenEndFrame = Time.frameCount;
                key.RemoveFromClassList("settings-kbd--listening");
                _listeningButton = null;
                if (outcome == KeyBindings.Outcome.Swapped && swapped.HasValue)
                    _screens.Toast($"{KeyBindings.Caption(action, slot)} moved from {KeyBindings.Actions[(int)swapped.Value].Label}");
                else if (outcome == KeyBindings.Outcome.Reserved) _screens.Toast("That key is kept for backing out and finishing lines");
                Refresh();
            });
        }

        void StopListening()
        {
            if (_listeningButton == null) return;
            KeyBindings.StopListening();
            _listeningButton.RemoveFromClassList("settings-kbd--listening");
            _listeningButton = null;
            Refresh();
        }

        void BuildData()
        {
            var page = AddPage("data", "Data");
            var open = Ghost("library-open", "Open", () => LibraryOpenChosen?.Invoke());
            _libraryChange = Ghost("library-change", "Change…", ChangeLibrary);
            var pair = new VisualElement();
            pair.AddToClassList("settings-pair");
            open.AddToClassList("settings-pair__first");
            pair.Add(open);
            pair.Add(_libraryChange);
            var row = Row(page, "Library folder", "", pair);
            _libraryPath = row.Q<Label>(className: "mp-srow__desc");
            _libraryPath.AddToClassList("settings-path");
            _freeSpace = Ghost("data-free", "Free space", () =>
            {
                if (_freeable <= 0) return;
                _screens.Confirm($"Remove {LibraryViewModel.Disk(_freeable)} of terrain caches left by older versions of the game? " +
                                 "Your areas stay, and nothing has to be downloaded or rebuilt.", "Free " + LibraryViewModel.Disk(_freeable),
                                 () => FreeSpaceConfirmed?.Invoke(), danger: false);
            });
            var disk = Row(page, "Disk use", "Measuring…", _freeSpace);
            _diskText = disk.Q<Label>(className: "mp-srow__desc");
            Row(page, "Offline mode", "No downloads, map tiles or place search; the areas you have open as usual.",
                Seg("offline", new[] { "on", "off" }, new[] { "On", "Off" },
                    () => DataPreferences.OfflineNow ? 0 : 1, i => { DataPreferences.SetOffline(i == 0, Remember); Refresh(); }));
            _refresh.Add(() =>
            {
                _freeSpace.SetEnabled(_freeable > 0);
                _freeSpace.text = _freeable > 0 ? "Free " + LibraryViewModel.Disk(_freeable) : "Free space";
            });
        }

        void ChangeLibrary()
        {
            _screens.Prompt("Library folder", _libraryPath.text, "Areas download here and the game looks for them here. " +
                            "Your areas stay in the old folder; move them yourself if you want them in the new one.",
                            "Use this folder", 260, text => text.Trim().Length > 2 && System.IO.Path.IsPathRooted(text.Trim()),
                            text => LibraryFolderChosen?.Invoke(text.Trim()));
        }

        /// <summary>The library folder, and whether it can change now (not while a download runs or with -data).</summary>
        public void ShowLibrary(string folder, bool canChange, string why)
        {
            _libraryPath.text = folder;
            _libraryChange.SetEnabled(canChange);
            _libraryChange.tooltip = canChange ? "" : why;
        }

        /// <summary>Disk use, measured by the app flow; <paramref name="freeable"/> is what Free space would remove.</summary>
        public void ShowDisk(string text, long freeable)
        {
            _diskText.text = text;
            _freeable = freeable;
            Refresh();
        }

        // ---------- building blocks, in the mockup's components (mp-*) ----------

        VisualElement AddPage(string id, string title)
        {
            int index = _pages.Count;
            var tab = new Button(() => ShowPage(index)) { name = "settings-tab-" + id, text = title };
            tab.AddToClassList("mp-nav__item");
            _nav.Add(tab);
            _tabs.Add(tab);
            var page = new VisualElement { name = "settings-" + id };
            page.Add(Text(title, "mp-page-title"));
            if (index > 0) page.AddToClassList("hidden");
            _scroll.Add(page);
            _pages.Add(page);
            return page;
        }

        static VisualElement Row(VisualElement page, string label, string desc, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("mp-srow");
            var text = new VisualElement();
            text.AddToClassList("mp-srow__text");
            text.Add(Text(label, "mp-srow__label"));
            var d = Text(desc, "mp-srow__desc");
            text.Add(d);
            row.Add(text);
            row.Add(control);
            page.Add(row);
            return row;
        }

        static Label Note(VisualElement page, string text, string cls = "mp-note")
        {
            var note = Text(text, cls);
            page.Add(note);
            return note;
        }

        static Label Text(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        VisualElement Seg(string name, string[] values, string[] labels, Func<int> get, Action<int> set)
        {
            var seg = new VisualElement { name = name };
            seg.AddToClassList("mp-seg");
            var buttons = new Button[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                int index = i;
                var b = buttons[i] = new Button(() => { set(index); Refresh(); }) { name = name + "-" + values[i], text = labels[i] };
                b.AddToClassList("mp-seg__opt");
                if (i == 0) b.AddToClassList("mp-seg__opt--first");
                seg.Add(b);
            }
            _refresh.Add(() =>
            {
                int on = get();
                for (int i = 0; i < buttons.Length; i++) buttons[i].EnableInClassList("mp-seg__opt--on", i == on);
            });
            return seg;
        }

        /// <summary>
        /// ‹ value ›. <paramref name="get"/> −1 means a value between the steps (Custom): then ‹ and › both go to
        /// <paramref name="customFrom"/>'s step.
        /// </summary>
        VisualElement Step(string prev, string value, string next, Func<int> get, Func<int> count, Func<int, string> label, Action<int> set,
                           string prevTip = null, string nextTip = null, Func<int> customFrom = null)
        {
            var step = new VisualElement();
            step.AddToClassList("mp-step");
            var v = new Label { name = value };
            v.AddToClassList("mp-step__v");
            var p = new Button(() => Go(-1)) { name = prev, text = "‹", tooltip = prevTip ?? "" };
            var n = new Button(() => Go(1)) { name = next, text = "›", tooltip = nextTip ?? "" };
            p.AddToClassList("mp-step__b");
            n.AddToClassList("mp-step__b");
            step.Add(p);
            step.Add(v);
            step.Add(n);
            void Go(int d)
            {
                int i = get();
                int target = i < 0 && customFrom != null ? customFrom() : i + d;
                if (target < 0 || target >= count()) return;
                set(target);
                Refresh();
            }
            _refresh.Add(() =>
            {
                int i = get();
                v.text = label(i);
                bool custom = i < 0 && customFrom != null;
                p.SetEnabled(custom || i > 0);
                n.SetEnabled(custom || (i >= 0 && i < count() - 1));
            });
            return step;
        }

        /// <summary>The mockup's slider: a thin track, a square thumb, and the value on the right.</summary>
        VisualElement Slider(string name, int min, int max, int step, Func<int> get, Action<int> set, Func<int, string> format)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("mp-range");
            var slider = new SliderInt(min, max) { name = name, pageSize = step, fill = true };
            slider.AddToClassList("mp-range__slider");
            var value = new Label { name = name + "-value" };
            value.AddToClassList("mp-range__v");
            value.AddToClassList("mono");
            wrap.Add(slider);
            wrap.Add(value);
            slider.RegisterValueChangedCallback(e =>
            {
                int snapped = Mathf.Clamp(Mathf.RoundToInt(e.newValue / (float)step) * step, min, max);
                if (snapped != e.newValue) slider.SetValueWithoutNotify(snapped);
                value.text = format(snapped);
                if (snapped != get()) set(snapped);
            });
            _refresh.Add(() =>
            {
                int v = get();
                slider.SetValueWithoutNotify(v);
                value.text = format(v);
            });
            return wrap;
        }

        static Button Ghost(string name, string text, Action click)
        {
            var b = new Button(click) { name = name, text = text };
            b.AddToClassList("mp-ghost");
            b.AddToClassList("settings-action");
            return b;
        }

        // ---------- Restore defaults ----------

        /// <summary>Every setting back to the game's defaults, except where the library lives and offline mode.</summary>
        public void RestoreDefaults()
        {
            UiPreferences.SetChoice(UiThemeChoice.Dark, Remember);
            UiPreferences.SetScale(UiPreferences.DefaultScalePercent, Remember);
            FlowUnits.Set(true, Remember);
            QualityPresets.Choose(GraphicsOptions.Default, Remember);
            FramePacing.SetVSync(false, Remember);
            FramePacing.SetCap(0, Remember);
            ViewFov.Set(ViewFov.Default, Remember);
            CameraOptions.SetInvertZoom(false, Remember);
            CameraOptions.SetSpeed(ZoomSpeed.Normal, Remember);
            KeyBindings.ResetAll(Remember);
            if (UiDisplay.Current != 1 || UiDisplay.Size != new Vector2Int(Screen.currentResolution.width, Screen.currentResolution.height))
                ChangeDisplay(() => UiDisplay.Set(1));   // borderless full screen, asked to keep
            Refresh();
        }
    }
}
