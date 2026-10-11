using System;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The S6 mountain-view HUD in the Trailhead direction (task P2-02): the accepted mockup,
    /// docs/plans/prototypes/ui-layout.html, as built in Phase 2 (its #demo=p2). Its parts:
    /// <list type="bullet">
    /// <item>the status bar along the bottom, docked or floating (MountainHud.Bar.cs);</item>
    /// <item>the top-right buttons, the menu with its quick switches, the map layers dropdown and the legend card
    /// (this file and MountainHud.Layers.cs);</item>
    /// <item>the Toolbox tray, Analysis, the resort's stats, notifications and tooltips (MountainHud.Panels.cs).</item>
    /// </list>
    /// It only shows state and raises events; the app wires them. The app refreshes it ten times a second, and each
    /// part changes only when its shown value does, from text made once and kept: steady frames and refreshes
    /// allocate nothing (0.3 §8).
    /// </summary>
    public sealed partial class MountainHud : MonoBehaviour
    {
        public UIDocument Document;

        /// <summary>A layer row was clicked (a <see cref="MapLayers"/> id) and the state asked for. Snow conditions is reserved.</summary>
        public event Action<string, bool> LayerChanged;
        public event Action QuitChosen;
        /// <summary>The menu's Exit to title; the item shows only when the app flow is running (<see cref="ShowExitToTitle"/>).</summary>
        public event Action ExitChosen;
        /// <summary>The menu's units switch asked for the other units (U does the same): the app flips <see cref="DisplayUnits"/>.</summary>
        public event Action UnitsChosen;
        /// <summary>The menu's Settings; enabled only when the app flow offers its Settings window (<see cref="ShowSettings"/>).</summary>
        public event Action SettingsChosen;
        /// <summary>The menu's Controls: Settings on its Controls page (task P2-05).</summary>
        public event Action ControlsChosen;
        /// <summary>The menu's Photo mode (P does the same; task P2-07).</summary>
        public event Action PhotoChosen;
        /// <summary>The menu's Credits: the flow's Credits window (task P2-07); enabled with <see cref="ShowSettings"/>.</summary>
        public event Action CreditsChosen;
        /// <summary>The bar's pause button (Space does the same).</summary>
        public event Action PauseChosen;
        /// <summary>A speed arrow, 1 to 4 (the number keys do the same).</summary>
        public event Action<int> SpeedChosen;

        /// <summary>Where the contour labels go (behind the panels); <see cref="ContourLabelOverlay"/> fills it.</summary>
        public VisualElement ContourLabelLayer => _root.Q("contour-labels");

        public bool DarkThemeOn => UiPreferences.Dark;
        /// <summary>The open area's name, as the HUD shows it.</summary>
        public string SiteName => _siteName;
        public bool MenuOpen => _drop == Drop.Menu;
        public bool LayersOpen => _drop == Drop.Layers;
        public bool ToolboxOpen => _toolboxOpen;
        public bool AnalysisOpen => _analysisOpen;
        public bool StatsOpen => _statsOpen;
        /// <summary>The menu or the resort's stats: a window that keeps the keyboard until it closes.</summary>
        public bool ModalOpen => MenuOpen || _statsOpen;

        /// <summary>
        /// True while the HUD has the keyboard: the menu or the stats window is open, or a control was reached with the
        /// arrows. The camera then leaves the keys alone (a click never takes them: the HUD lets go of focus after one).
        /// </summary>
        public bool HasKeyboard
        {
            get
            {
                if (ModalOpen) return true;
                var focused = _root?.panel?.focusController?.focusedElement as VisualElement;
                return focused != null && _root.Contains(focused) && UiFocus.KeyboardActive(_root) && UiFocus.IsShown(focused);
            }
        }

        /// <summary>Esc's step when a HUD control has the keyboard: let go of it, so the camera keys work again.</summary>
        public bool ReleaseKeyboard()
        {
            if (ModalOpen || !HasKeyboard) return false;
            ((Focusable)_root.panel.focusController.focusedElement).Blur();
            return true;
        }

        enum Drop { None, Menu, Layers }

        VisualElement _root, _menu, _layers, _modal;
        Button _trSketch, _trLayers, _trMenu, _settingsRow, _controlsRow, _creditsRow;
        Drop _drop;
        string _siteName = "", _place = "";
        bool _settingsAvailable;

        void OnEnable()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            UiPanels.Adopt(Document);   // the shared theme, UI scale, focus ring and arrow keys (task P2-01)
            _root = Document.rootVisualElement;
            _menu = _root.Q("menu");
            _layers = _root.Q("layers");
            _modal = _root.Q("modal");
            _trSketch = _root.Q<Button>("tr-sketch");
            _trLayers = _root.Q<Button>("tr-layers");
            _trMenu = _root.Q<Button>("tr-menu");
            _trLayers.clicked += () => SetDrop(_drop == Drop.Layers ? Drop.None : Drop.Layers);
            _trMenu.clicked += ToggleMenu;

            WireMenu();
            WireBar();
            WireLayers();
            WirePanels();
            WireGlass();
            WireKeyCaptions();
            _menu.RegisterCallback<GeometryChangedEvent>(_ => FitDrop(_menu));
            _layers.RegisterCallback<GeometryChangedEvent>(_ => FitDrop(_root.Q("rcol")));

            UiPreferences.Changed += OnPreferencesChanged;
            HudPreferences.Changed += OnDockChanged;
            DisplayUnits.Changed += OnUnitsChanged;
            OnPreferencesChanged();
            OnDockChanged();
            OnUnitsChanged();
            // A click leaves no focus behind, so the camera keys keep working after one (in the menu and the stats window the
            // keyboard stays with the window).
            _root.RegisterCallback<PointerUpEvent>(_ => _root.schedule.Execute(LetGoAfterClick), TrickleDown.TrickleDown);
            // Icons follow hover and state: one pass after the pointer moves between parts (HudIcon.RetintAll).
            _root.RegisterCallback<PointerOverEvent>(_ => RetintSoon(), TrickleDown.TrickleDown);
            _root.RegisterCallback<PointerOutEvent>(_ => RetintSoon(), TrickleDown.TrickleDown);
            // and four times a second whatever else restyled them (a theme or state change): a check, no allocation.
            _root.schedule.Execute(HudIcon.RetintAll).Every(250);
            // The keys that move between controls belong to the map unless a window is open (NavigateOnlyInWindows).
            _root.schedule.Execute(() => _root.panel?.visualTree.RegisterCallback<NavigationMoveEvent>(NavigateOnlyInWindows, TrickleDown.TrickleDown));
        }

        void OnDisable()
        {
            UiPreferences.Changed -= OnPreferencesChanged;
            HudPreferences.Changed -= OnDockChanged;
            HudPreferences.Changed -= ApplyOpacity;
            DisplayUnits.Changed -= OnUnitsChanged;
            ReleaseKeyCaptions();
        }

        public void SetVisible(bool visible) => _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>The area's name, where it is (shown under the name in the menu and the stats), and the data quality score.</summary>
        public void SetSite(string name, int score) => SetSite(name, "", score);

        public void SetSite(string name, string place, int score)
        {
            _siteName = name ?? "";
            _place = place ?? "";
            _root.Q<Label>("bar-resort-name").text = _siteName;
            _root.Q<Label>("menu-name").text = _siteName;
            var placeLabel = _root.Q<Label>("menu-place");
            placeLabel.text = _place;
            placeLabel.EnableInClassList("hidden", _place.Length == 0);
            _root.Q("bar-resort").tooltip = _place.Length > 0 ? $"{_siteName}. {_place}. Click for the resort's stats." : $"{_siteName}. Click for the resort's stats.";
            SetStatsSite(score);
            _root.Q<Label>("analysis-name").text = _siteName;
        }

        // ---------- the menu (.drop.menu) ----------

        void WireMenu()
        {
            _root.Q<Button>("menu-resume").clicked += ToggleMenu;
            _root.Q<Button>("menu-quit").clicked += () => QuitChosen?.Invoke();
            _root.Q<Button>("menu-exit").clicked += () => ExitChosen?.Invoke();
            _settingsRow = _root.Q<Button>("menu-settings");
            _controlsRow = _root.Q<Button>("menu-controls");
            _settingsRow.clicked += OpenSettings;
            _controlsRow.clicked += () => { SetDrop(Drop.None); ControlsChosen?.Invoke(); };
            _creditsRow = _root.Q<Button>("menu-credits");
            _creditsRow.clicked += () => { SetDrop(Drop.None); CreditsChosen?.Invoke(); };
            _root.Q<Button>("menu-photo").clicked += () => { SetDrop(Drop.None); PhotoChosen?.Invoke(); };
            ShowSettings(false);
            Segment("menu-theme-dark", () => UiPreferences.SetChoice(UiThemeChoice.Dark));
            Segment("menu-theme-light", () => UiPreferences.SetChoice(UiThemeChoice.Light));
            Segment("menu-theme-auto", () => UiPreferences.SetChoice(UiThemeChoice.Auto));
            Segment("menu-dock-docked", () => HudPreferences.SetDocked(true));
            Segment("menu-dock-floating", () => HudPreferences.SetDocked(false));
            Segment("menu-units-metric", () => { if (DisplayUnits.Current != UnitSystem.Metric) UnitsChosen?.Invoke(); });
            Segment("menu-units-imperial", () => { if (DisplayUnits.Current != UnitSystem.Imperial) UnitsChosen?.Invoke(); });
        }

        void Segment(string name, Action chosen) => _root.Q<Button>(name).clicked += chosen;

        void OpenSettings()
        {
            SetDrop(Drop.None);
            SettingsChosen?.Invoke();
        }

        /// <summary>Shows the menu's Exit to title (off when the viewer runs without the title flow, as captures do).</summary>
        public void ShowExitToTitle(bool show) => _root.Q("menu-exit").EnableInClassList("hidden", !show);

        /// <summary>Enables the menu's Settings and Controls (the app flow's Settings window, when the flow runs).</summary>
        public void ShowSettings(bool available)
        {
            _settingsAvailable = available;
            _settingsRow.SetEnabled(available);
            _controlsRow.SetEnabled(available);
            _creditsRow.SetEnabled(available);
            _creditsRow.EnableInClassList("row--dis", !available);
            _settingsRow.EnableInClassList("row--dis", !available);
            _controlsRow.EnableInClassList("row--dis", !available);
        }

        /// <summary>
        /// The in-game menu (0.4 S7) is a modal: when it opens, focus goes to Resume and stays in the menu; when it
        /// closes, focus goes back where it was (task P2-01).
        /// </summary>
        public void ToggleMenu() => SetDrop(_drop == Drop.Menu ? Drop.None : Drop.Menu);

        /// <summary>Opens or toggles the map layers dropdown.</summary>
        public void ToggleLayers() => SetDrop(_drop == Drop.Layers ? Drop.None : Drop.Layers);

        void SetDrop(Drop drop)
        {
            if (drop == _drop) return;
            if (_drop == Drop.Menu) UiFocus.CloseModal(_menu);
            _drop = drop;
            _menu.EnableInClassList("hidden", drop != Drop.Menu);
            _layers.EnableInClassList("hidden", drop != Drop.Layers);
            _trMenu.EnableInClassList("sq--on", drop == Drop.Menu);
            _trLayers.EnableInClassList("sq--on", drop == Drop.Layers);
            if (drop == Drop.Menu) UiFocus.OpenModal(_menu, _root.Q<Button>("menu-resume"));
            ShowLegend();
            RetintSoon();
        }

        /// <summary>
        /// Esc backs out one step, as the mockup does: the stats window, then a dropdown, then the Toolbox or Analysis.
        /// False when there was nothing to close (the app then opens the menu).
        /// </summary>
        public bool BackOut()
        {
            if (_statsOpen) { SetStats(false); return true; }
            if (_drop != Drop.None) { SetDrop(Drop.None); return true; }
            if (HudWindow.Current != null) { HudWindow.Current.Close(); return true; }
            if (_toolboxOpen || _analysisOpen) { SetToolbox(false); SetAnalysis(false); return true; }
            return false;
        }

        /// <summary>A dropdown too tall for the room between the buttons and the bar moves up beside the buttons.</summary>
        void FitDrop(VisualElement drop)
        {
            if (_bar == null) return;
            var content = drop == _menu ? _menu : _layers;
            if (content.ClassListContains("hidden")) { drop.RemoveFromClassList("drop--up"); return; }
            bool up = drop.ClassListContains("drop--up");
            float height = content.layout.height, room = _bar.layout.y - 6 - 56;
            if (!up && height > room) drop.AddToClassList("drop--up");
            else if (up && height <= room) drop.RemoveFromClassList("drop--up");
        }

        /// <summary>A click on the mountain (not on the HUD) closes the dropdowns, as in the mockup.</summary>
        public void ClickedMap() => SetDrop(Drop.None);

        /// <summary>Opens a floating window (one at a time; Esc closes it). The tools of Phase 3 use it.</summary>
        public void OpenWindow(HudWindow window) => HudWindow.Open(_root.Q("hud"), window);

        /// <summary>The theme for the whole game (every screen follows <see cref="UiPreferences"/>).</summary>
        public void SetTheme(bool dark) => UiPreferences.SetTheme(dark ? UiTheme.Dark : UiTheme.Light);

        void OnPreferencesChanged()
        {
            var choice = UiPreferences.Choice;
            On("menu-theme-dark", choice == UiThemeChoice.Dark);
            On("menu-theme-light", choice == UiThemeChoice.Light);
            On("menu-theme-auto", choice == UiThemeChoice.Auto);
            if (_statsOpen) SetScrim();
            RetintSoon();
        }

        void OnDockChanged()
        {
            bool docked = HudPreferences.Docked;
            On("menu-dock-docked", docked);
            On("menu-dock-floating", !docked);
            _bar.EnableInClassList("bar--docked", docked);
            _bar.EnableInClassList("bar--floating", !docked);
            _root.Q("credit").EnableInClassList("credit--float", !docked);
            float above = (docked ? 0 : 10) + 52 + 10;   // the tray and Analysis sit 10 px above the bar (the mockup's trayBottom)
            _root.Q("toolbox").style.bottom = above;
            _root.Q("analysis").style.bottom = above;
        }

        void OnUnitsChanged()
        {
            bool imperial = DisplayUnits.Current == UnitSystem.Imperial;
            On("menu-units-metric", !imperial);
            On("menu-units-imperial", imperial);
            CheckUnits();
        }

        void On(string name, bool on) => _root.Q(name).EnableInClassList("mp-seg__opt--on", on);

        /// <summary>
        /// WASD, the arrows and Tab reach the HUD as navigation. In the game they're the map's (pan) and Analysis' (Tab,
        /// the key map), so they move between controls only inside the menu or the stats window, or once a control has
        /// been reached by keyboard there; with nothing focused they never pick a control (owner's report, 2026-10-06:
        /// WASD stopped panning). Registered on the HUD panel's own root, so it runs before UiFocus's arrow handling.
        /// </summary>
        void NavigateOnlyInWindows(NavigationMoveEvent e)
        {
            if (ModalOpen) return;
            bool tab = e.direction == NavigationMoveEvent.Direction.Next || e.direction == NavigationMoveEvent.Direction.Previous;
            var focused = _root.panel?.focusController?.focusedElement as VisualElement;
            if (!tab && focused != null && _root.Contains(focused) && UiFocus.KeyboardActive(_root)) return;
            e.StopPropagation();
            _root.focusController?.IgnoreEvent(e);
        }

        void LetGoAfterClick()
        {
            if (ModalOpen) return;
            var focused = _root.panel?.focusController?.focusedElement as VisualElement;
            if (focused != null && _root.Contains(focused)) ((Focusable)focused).Blur();
        }

        int _retintFrames;

        /// <summary>
        /// Icons check their colours on each of the next few frames: a theme or state change restyles the panel over a
        /// frame or two, and an icon left with the old tint would flash the wrong colour.
        /// </summary>
        void RetintSoon()
        {
            if (_root == null) return;
            bool running = _retintFrames > 0;
            _retintFrames = 4;
            if (running) return;
            _root.schedule.Execute(() => { HudIcon.RetintAll(); _retintFrames--; }).Every(16).Until(() => _retintFrames <= 0);
        }

        /// <summary>True when the pointer (screen pixels, origin bottom-left) is over a HUD panel, so the camera leaves the wheel alone.</summary>
        public bool IsPointerOverPanel(Vector2 screen) => PanelPointer.IsOver(_root, screen);
    }
}
