using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Terrain;
using MountainPlanner.Persistence;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>The library screen's two modes (the title's Load Area and Manage Areas signs).</summary>
    public enum LibraryMode { Load, Manage }

    /// <summary>
    /// Task 14's screens on one UI Toolkit document (Flow.uxml): S1 title, S2 Load Area and Manage
    /// Areas, the S4 download card and pill, the S5 quality card, S8 Settings (units), S9 Credits and a
    /// confirm dialog. Like the HUD, it only
    /// shows state and raises events; AppFlow decides what happens. The download card updates text in
    /// place each snapshot and rebuilds its stage rows only when the stage list changes.
    /// </summary>
    public sealed class FlowScreens : MonoBehaviour
    {
        public UIDocument Document;

        public event Action ContinueChosen, NewResortChosen, LoadChosen, ManageChosen, CreditsChosen, QuitChosen;
        public event Action LibraryClosed, DataFolderChosen;
        public event Action<LibraryRow> OpenChosen, ResumeChosen, DiscardChosen, DeleteConfirmed;
        public event Action<LibrarySort> SortChosen;
        public event Action MinimiseChosen, RestoreChosen, RetryChosen, CloseChosen;
        /// <summary>Cancel confirmed: true keeps the partial download for resuming.</summary>
        public event Action<bool> CancelConfirmed;
        public event Action QualityOpenChosen, QualityLibraryChosen;

        VisualElement _root, _title, _library, _download, _quality, _confirm, _settings, _credits, _stages, _barFill, _qcLines;
        VisualElement _dlActions, _dlConfirm, _dlFailed;
        ScrollView _rows;
        Label _continueLabel, _continueSub, _libraryTitle, _libraryKeys, _summary, _empty, _dlTitle, _dlPercent, _dlLeft, _dlDetail, _dlTransfer, _qcTitle, _qcPlace, _confirmText, _toast, _scaleValue;
        Button _continue, _pill, _sortOpened, _sortName, _sortQuality, _imperial, _metric, _themeDark, _themeLight, _themeAuto, _scaleDown, _scaleUp, _tabInterface, _displayPrev, _displayNext;
        Button[] _settingsTabs;
        VisualElement[] _settingsPages;
        Label _displayValue;
        int _display = 1;
        Label _settingsResort;
        ScrollView _creditsBody;
        readonly List<VisualElement> _rowElements = new List<VisualElement>();
        readonly List<LibraryRow> _rowData = new List<LibraryRow>();
        Action _confirmAction;
        int _stagesVersion = -1, _statesVersion = -1, _selected = -1;
        float _toastUntil;

        public bool ConfirmOpen => _confirm != null && !_confirm.ClassListContains("hidden");
        /// <summary>Settings or Credits is open over the title (Esc closes it first).</summary>
        public bool OverlayOpen => IsShown(_settings) || IsShown(_credits);
        public LibraryMode Mode { get; private set; } = LibraryMode.Load;
        /// <summary>True when the pointer (screen pixels, origin bottom-left) is over a shown screen, card, pill or dialog.</summary>
        public bool IsPointerOverPanel(Vector2 screen) => PanelPointer.IsOver(_root, screen);
        public bool LibraryVisible => _library != null && !_library.ClassListContains("hidden");
        public LibraryRow SelectedRow => _selected >= 0 && _selected < _rowData.Count ? _rowData[_selected] : null;

        void OnEnable()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            if (Document == null || Document.rootVisualElement == null) return;
            UiPanels.Adopt(Document);   // the shared theme, UI scale, focus ring and arrow keys (task P2-01)
            _root = Document.rootVisualElement;
            _root.pickingMode = PickingMode.Ignore;   // only the visible screens take clicks; the game gets the rest
            _title = _root.Q("title");
            _library = _root.Q("library");
            _download = _root.Q("download");
            _quality = _root.Q("quality");
            _confirm = _root.Q("confirm");
            _settings = _root.Q("settings");
            _credits = _root.Q("credits");
            _creditsBody = _root.Q<ScrollView>("credits-body");
            _imperial = _root.Q<Button>("units-imperial");
            _metric = _root.Q<Button>("units-metric");
            _libraryTitle = _root.Q<Label>("library-title");
            _libraryKeys = _root.Q<Label>("library-keys");
            _stages = _root.Q("dl-stages");
            _barFill = _root.Q("dl-bar-fill");
            _qcLines = _root.Q("qc-lines");
            _dlActions = _root.Q("dl-actions");
            _dlConfirm = _root.Q("dl-confirm");
            _dlFailed = _root.Q("dl-failed");
            _rows = _root.Q<ScrollView>("library-rows");
            _continue = _root.Q<Button>("title-continue");
            _continueLabel = _root.Q<Label>("title-continue-label");
            _continueSub = _root.Q<Label>("title-continue-sub");
            _summary = _root.Q<Label>("library-summary");
            _empty = _root.Q<Label>("library-empty");
            _dlTitle = _root.Q<Label>("dl-title");
            _dlPercent = _root.Q<Label>("dl-percent");
            _dlLeft = _root.Q<Label>("dl-left");
            _dlDetail = _root.Q<Label>("dl-detail");
            _dlTransfer = _root.Q<Label>("dl-transfer");
            _qcTitle = _root.Q<Label>("qc-title");
            _qcPlace = _root.Q<Label>("qc-place");
            _confirmText = _root.Q<Label>("confirm-text");
            _toast = _root.Q<Label>("toast");
            _pill = _root.Q<Button>("dl-pill");
            _sortOpened = _root.Q<Button>("sort-opened");
            _sortName = _root.Q<Button>("sort-name");
            _sortQuality = _root.Q<Button>("sort-quality");

            _continue.clicked += () => ContinueChosen?.Invoke();
            _root.Q<Button>("title-new").clicked += () => NewResortChosen?.Invoke();
            _root.Q<Button>("title-load").clicked += () => LoadChosen?.Invoke();
            _root.Q<Button>("title-manage").clicked += () => ManageChosen?.Invoke();
            _root.Q<Button>("title-credits").clicked += () => CreditsChosen?.Invoke();
            _root.Q<Button>("title-settings").clicked += ShowSettings;
            _root.Q<Button>("title-quit").clicked += () => QuitChosen?.Invoke();
            _root.Q<Button>("settings-close").clicked += CloseOverlay;
            _root.Q<Button>("credits-close").clicked += CloseOverlay;
            _imperial.clicked += () => { FlowUnits.Set(true); MarkUnits(); };
            _metric.clicked += () => { FlowUnits.Set(false); MarkUnits(); };
            _themeDark = _root.Q<Button>("theme-dark");
            _themeLight = _root.Q<Button>("theme-light");
            _themeAuto = _root.Q<Button>("theme-auto");
            _scaleDown = _root.Q<Button>("scale-down");
            _scaleUp = _root.Q<Button>("scale-up");
            _scaleValue = _root.Q<Label>("scale-value");
            _themeDark.clicked += () => UiPreferences.SetTheme(UiTheme.Dark);
            _themeLight.clicked += () => UiPreferences.SetTheme(UiTheme.Light);
            _themeAuto.clicked += () => UiPreferences.SetChoice(UiThemeChoice.Auto);
            _scaleDown.clicked += () => UiPreferences.StepScale(-1);
            _scaleUp.clicked += () => UiPreferences.StepScale(1);
            UiPreferences.Changed += MarkInterface;
            // The mockup's Settings window: categories down the left, Restore defaults and Done at the foot.
            _tabInterface = _root.Q<Button>("settings-tab-interface");
            _settingsTabs = new[] { _tabInterface, _root.Q<Button>("settings-tab-units"), _root.Q<Button>("settings-tab-graphics") };
            _settingsPages = new[] { _root.Q("settings-interface"), _root.Q("settings-units"), _root.Q("settings-graphics") };
            _settingsResort = _root.Q<Label>("settings-resort");
            for (int i = 0; i < _settingsTabs.Length; i++)
            {
                int page = i;
                _settingsTabs[i].clicked += () => SettingsPage(page);
            }
            _displayPrev = _root.Q<Button>("display-prev");
            _displayNext = _root.Q<Button>("display-next");
            _displayValue = _root.Q<Label>("display-value");
            _displayPrev.clicked += () => SetDisplay(_display - 1);
            _displayNext.clicked += () => SetDisplay(_display + 1);
            _root.Q<Button>("settings-done").clicked += CloseOverlay;
            _root.Q<Button>("settings-defaults").clicked += () =>
            {
                UiPreferences.SetChoice(UiThemeChoice.Dark);
                UiPreferences.SetScale(UiPreferences.DefaultScalePercent);
                FlowUnits.Set(true);
                MarkUnits();
                SetDisplay(1);   // borderless full screen
            };
            _root.Q<Button>("library-new").clicked += () => NewResortChosen?.Invoke();
            _root.Q<Button>("library-close").clicked += () => LibraryClosed?.Invoke();
            _root.Q<Button>("library-folder").clicked += () => DataFolderChosen?.Invoke();
            _sortOpened.clicked += () => SortChosen?.Invoke(LibrarySort.LastOpened);
            _sortName.clicked += () => SortChosen?.Invoke(LibrarySort.Name);
            _sortQuality.clicked += () => SortChosen?.Invoke(LibrarySort.Quality);
            _root.Q<Button>("dl-minimise").clicked += () => MinimiseChosen?.Invoke();
            _root.Q<Button>("dl-cancel").clicked += () => ShowCancelConfirm(true);
            _root.Q<Button>("dl-back").clicked += () => ShowCancelConfirm(false);
            _root.Q<Button>("dl-keep").clicked += () => { ShowCancelConfirm(false); CancelConfirmed?.Invoke(true); };
            _root.Q<Button>("dl-discard").clicked += () => { ShowCancelConfirm(false); CancelConfirmed?.Invoke(false); };
            _root.Q<Button>("dl-retry").clicked += () => RetryChosen?.Invoke();
            _root.Q<Button>("dl-close").clicked += () => CloseChosen?.Invoke();
            _pill.clicked += () => RestoreChosen?.Invoke();
            _root.Q<Button>("qc-open").clicked += () => QualityOpenChosen?.Invoke();
            _root.Q<Button>("qc-library").clicked += () => QualityLibraryChosen?.Invoke();
            _root.Q<Button>("confirm-cancel").clicked += CloseConfirm;
            _root.Q<Button>("confirm-ok").clicked += () =>
            {
                var action = _confirmAction;
                CloseConfirm();
                action?.Invoke();
            };
        }

        void OnDisable() => UiPreferences.Changed -= MarkInterface;

        void Update()
        {
            if (_toast != null && _toastUntil > 0 && Time.unscaledTime > _toastUntil)
            {
                _toastUntil = 0;
                Show(_toast, false);
            }
        }

        // ---------- screens ----------

        /// <summary>Which full screen shows: "title", "library", "quality", or null for none (the game, or the site picker).</summary>
        public void ShowScreen(string name)
        {
            bool qualityWas = IsShown(_quality);
            Show(_title, name == "title");
            Show(_library, name == "library");
            Show(_quality, name == "quality");
            // Each screen opens with its first control focused, so the keyboard works at once (task P2-01).
            if (name == "title") UiFocus.FocusSoon(_title);
            if (name == "library") FocusSelectedRow();
            if (name == "quality") UiFocus.OpenModal(_quality, _root.Q<Button>("qc-open"));
            else if (qualityWas) UiFocus.CloseModal(_quality);
        }

        /// <summary>The Continue sign: hidden when there's nothing to continue.</summary>
        public void SetContinue(string label, string sub)
        {
            Show(_continue, !string.IsNullOrEmpty(label));
            _continueLabel.text = label ?? "";
            _continueSub.text = sub ?? "";
            Show(_continueSub, !string.IsNullOrEmpty(sub));
        }

        public void Toast(string message, float seconds = 3)
        {
            _toast.text = message;
            Show(_toast, true);
            _toastUntil = Time.unscaledTime + seconds;
        }

        // ---------- S2 ----------

        /// <summary>
        /// S2 in one of its two modes: Load Area lists the downloaded areas to open; Manage Areas lists them to
        /// delete, with paused downloads to resume or discard.
        /// </summary>
        public void RenderLibrary(LibraryViewModel vm, LibraryMode mode)
        {
            Mode = mode;
            bool manage = mode == LibraryMode.Manage;
            _libraryTitle.text = manage ? "Manage Areas" : "Load Area";
            _libraryKeys.text = manage ? "Delete removes · Esc goes back" : "Enter opens · Esc goes back";
            _summary.text = vm.Summary;
            Mark(_sortOpened, vm.Sort == LibrarySort.LastOpened);
            Mark(_sortName, vm.Sort == LibrarySort.Name);
            Mark(_sortQuality, vm.Sort == LibrarySort.Quality);
            _rows.Clear();
            _rowElements.Clear();
            _rowData.Clear();
            foreach (var row in vm.Rows)
            {
                if (row.IsPaused && !manage) continue;   // paused downloads are managed, not loaded
                var r = row;
                var el = new VisualElement();
                el.AddToClassList("lib-row");
                el.Add(Text(r.Name, "lib-name"));
                el.Add(Text(r.Place, "lib-cell", "mono"));
                el.Add(Text(r.Size, "lib-cell", "lib-cell--narrow", "mono"));
                if (r.IsPaused)
                {
                    el.Add(Text(r.PausedText, "lib-paused"));
                    var actions = new VisualElement();
                    actions.AddToClassList("lib-actions");
                    actions.Add(Btn("Resume", "btn--go", () => ResumeChosen?.Invoke(r)));
                    actions.Add(Btn("Discard", "btn--ghost", () => Confirm($"Discard the paused download of {r.Name}? Its partial files are deleted.", "Discard", () => DiscardChosen?.Invoke(r))));
                    el.Add(actions);
                }
                else if (r.IsNewer)
                {
                    // A newer game's area (task 08): named and sized, never read further; Delete works, Open doesn't.
                    el.AddToClassList("lib-row--newer");
                    el.Add(Text(r.NewerText, "lib-paused"));
                    el.Add(Text(r.Disk, "lib-cell", "lib-cell--narrow", "mono"));
                    var actions = new VisualElement();
                    actions.AddToClassList("lib-actions");
                    if (manage) actions.Add(Btn("Delete", "btn--ghost", () => ConfirmDelete(r)));
                    else
                    {
                        var open = Btn("Open", "btn--go", () => { });
                        open.SetEnabled(false);
                        actions.Add(open);
                    }
                    el.Add(actions);
                    el.RegisterCallback<ClickEvent>(e => Select(_rowData.IndexOf(r)));
                    el.focusable = true;   // reached by the arrows like any row; Delete removes it in Manage Areas (task P2-01)
                    el.RegisterCallback<KeyDownEvent>(e => { if (e.target == el && manage && e.keyCode == KeyCode.Delete) ConfirmDelete(r); });
                }
                else
                {
                    el.Add(Score("Terrain", r.TerrainScore));
                    el.Add(Score("Flora", r.FloraScore));
                    el.Add(Text(r.Disk, "lib-cell", "lib-cell--narrow", "mono"));
                    el.Add(Text(r.Opened, "lib-cell"));
                    var spacer = new VisualElement();
                    spacer.AddToClassList("spacer");
                    el.Add(spacer);
                    var actions = new VisualElement();
                    actions.AddToClassList("lib-actions");
                    if (manage) actions.Add(Btn("Delete", "btn--ghost", () => ConfirmDelete(r)));
                    else actions.Add(Btn("Open", "btn--go", () => OpenChosen?.Invoke(r)));
                    el.Add(actions);
                    el.RegisterCallback<ClickEvent>(e =>
                    {
                        Select(_rowData.IndexOf(r));
                        if (e.clickCount == 2 && !manage) OpenChosen?.Invoke(r);
                    });
                    // The row itself takes keyboard focus (task P2-01): the arrows move between rows, Enter opens
                    // (Load Area), Delete removes (Manage Areas); Right reaches the row's own button.
                    el.focusable = true;
                    el.RegisterCallback<NavigationSubmitEvent>(e => { if (e.target == el && !manage) OpenChosen?.Invoke(r); });
                    el.RegisterCallback<KeyDownEvent>(e => { if (e.target == el && manage && e.keyCode == KeyCode.Delete) ConfirmDelete(r); });
                }
                el.RegisterCallback<FocusInEvent>(_ => Select(_rowData.IndexOf(r)));
                _rows.Add(el);
                _rowElements.Add(el);
                _rowData.Add(r);
            }
            _empty.text = vm.EmptyText;
            Show(_empty, _rowData.Count == 0);
            int first = _rowData.FindIndex(x => x.CanOpen);
            Select(first >= 0 ? first : _rowData.FindIndex(x => !x.IsPaused));
        }

        /// <summary>Moves the library selection, and keyboard focus with it.</summary>
        public void MoveSelection(int delta)
        {
            if (_rowData.Count == 0) return;
            Select(Mathf.Clamp((_selected < 0 ? 0 : _selected) + delta, 0, _rowData.Count - 1));
            FocusSelectedRow();
        }

        /// <summary>Keyboard focus on the selected row (or New Area when the library is empty), once it's shown.</summary>
        void FocusSelectedRow()
        {
            Focusable target = _selected >= 0 && _selected < _rowElements.Count && _rowElements[_selected].focusable
                ? _rowElements[_selected]
                : _root.Q<Button>("library-new");
            UiFocus.FocusSoon(_library, target);
        }

        public void ConfirmDelete(LibraryRow r)
        {
            if (r?.Entry == null) return;
            Confirm($"Delete {r.Name}? This frees {LibraryViewModel.Disk(r.Entry.BytesOnDisk)}. You can download it again later.", "Delete",
                    () => DeleteConfirmed?.Invoke(r));
        }

        void Select(int index)
        {
            if (_selected >= 0 && _selected < _rowElements.Count) _rowElements[_selected].RemoveFromClassList("lib-row--selected");
            _selected = index;
            if (_selected >= 0 && _selected < _rowElements.Count)
            {
                var row = _rowElements[_selected];
                row.AddToClassList("lib-row--selected");
                // A freshly built row has no layout yet; scroll once it has one.
                _rows.schedule.Execute(() => { if (row.panel != null) _rows.ScrollTo(row); });
            }
        }

        static VisualElement Score(string label, int score)
        {
            var band = QualityBands.Of(score);
            var l = Text($"{label} {score.ToString(CultureInfo.InvariantCulture)} · {QualityBands.Word(band)}", "lib-score");
            l.AddToClassList("lib-score--" + QualityBands.Word(band).ToLowerInvariant());
            return l;
        }

        // ---------- S4 ----------

        public void ShowDownloadCard(bool open, bool active, bool inGame)
        {
            Show(_download, open && active);
            Show(_pill, !open && active);
            _pill.EnableInClassList("pill--game", inGame);
            if (!open) ShowCancelConfirm(false);
        }

        public void RenderDownload(DownloadViewModel vm)
        {
            if (vm.StagesVersion != _stagesVersion)
            {
                _stagesVersion = vm.StagesVersion;
                _statesVersion = -1;
                _stages.Clear();
                foreach (string name in vm.StageNames)
                {
                    var row = new VisualElement();
                    row.AddToClassList("dl-stage");
                    row.Add(Text("", "dl-tick"));
                    row.Add(Text(name, "dl-stage-name"));
                    _stages.Add(row);
                }
            }
            if (vm.StatesVersion != _statesVersion)
            {
                _statesVersion = vm.StatesVersion;
                for (int i = 0; i < _stages.childCount && i < vm.StageStates.Count; i++)
                {
                    var row = _stages[i];
                    var state = vm.StageStates[i];
                    row.EnableInClassList("dl-stage--done", state == StageState.Done);
                    row.EnableInClassList("dl-stage--current", state == StageState.Current);
                    ((Label)row[0]).text = state == StageState.Done ? "✓" : state == StageState.Current ? "▸" : "";
                }
            }
            SetText(_dlTitle, vm.Title);
            SetText(_dlPercent, vm.Percent);
            SetText(_dlLeft, vm.TimeLeft);
            SetText(_dlDetail, vm.Detail);
            SetText(_dlTransfer, vm.Transfer);
            SetText(_pill, vm.Pill);
            _barFill.style.width = Length.Percent(vm.Fraction * 100f);
            bool failed = vm.Phase == DownloadPhase.Failed;
            Show(_dlFailed, failed);
            if (failed)
            {
                Show(_dlActions, false);
                Show(_dlConfirm, false);
            }
            else if (_dlConfirm.ClassListContains("hidden")) Show(_dlActions, true);
        }

        void ShowCancelConfirm(bool show)
        {
            if (_dlConfirm == null) return;
            Show(_dlConfirm, show);
            Show(_dlActions, !show);
        }

        // ---------- S5 ----------

        public void RenderQuality(QualityCardViewModel vm)
        {
            _qcTitle.text = vm.Title;
            _qcPlace.text = vm.Place;
            _qcLines.Clear();
            foreach (var line in vm.Lines)
            {
                var block = new VisualElement();
                block.AddToClassList("qc-line");
                var head = new VisualElement();
                head.AddToClassList("qc-head");
                head.Add(Text(line.Label, "qc-label"));
                head.Add(Text($"{line.Score} / 100", "qc-score", "mono"));
                head.Add(Text(line.Word, "qc-word", "qc-word--" + line.Word.ToLowerInvariant()));
                block.Add(head);
                if (line.Caveat.Length > 0) block.Add(Text(line.Caveat, "qc-caveat"));
                if (line.Detail.Length > 0) block.Add(Text(line.Detail, "qc-detail"));
                _qcLines.Add(block);
            }
        }

        // ---------- S8 and S9 ----------

        public void ShowSettings() => ShowSettings(null);

        /// <summary>Opens Settings on its first page; in the game the head names the resort, as the mockup's does.</summary>
        public void ShowSettings(string resort)
        {
            MarkUnits();
            MarkInterface();
            SetText(_settingsResort, resort ?? "");
            _display = UiDisplay.Current;   // Alt+Enter may have changed it since
            MarkDisplay();
            SettingsPage(0);
            Show(_settings, true);
            UiFocus.OpenModal(_settings, _tabInterface);
        }

        /// <summary>Shows one category: 0 Interface, 1 Units and time, 2 Graphics.</summary>
        void SettingsPage(int page)
        {
            for (int i = 0; i < _settingsPages.Length; i++)
            {
                Show(_settingsPages[i], i == page);
                _settingsTabs[i].EnableInClassList("mp-nav__item--on", i == page);
            }
        }

        /// <summary>Graphics › Display mode: a window, borderless full screen or exclusive full screen (UiDisplay).</summary>
        void SetDisplay(int index)
        {
            index = Mathf.Clamp(index, 0, UiDisplay.Modes.Length - 1);
            if (index == _display) return;
            _display = index;
            UiDisplay.Set(index);
            MarkDisplay();
        }

        void MarkDisplay()
        {
            SetText(_displayValue, UiDisplay.Names[_display]);
            _displayPrev.SetEnabled(_display > 0);
            _displayNext.SetEnabled(_display < UiDisplay.Modes.Length - 1);
        }

        void MarkUnits()
        {
            Mark(_imperial, FlowUnits.Imperial);
            Mark(_metric, !FlowUnits.Imperial);
        }

        /// <summary>The Interface rows (task P2-01): the theme that's on and the UI scale, 50–150%.</summary>
        void MarkInterface()
        {
            if (_scaleValue == null) return;
            Mark(_themeDark, UiPreferences.Choice == UiThemeChoice.Dark);
            Mark(_themeLight, UiPreferences.Choice == UiThemeChoice.Light);
            Mark(_themeAuto, UiPreferences.Choice == UiThemeChoice.Auto);
            _scaleValue.text = UiPreferences.ScalePercent + "%";
            _scaleDown.SetEnabled(UiPreferences.ScalePercent > UiPreferences.MinScalePercent);
            _scaleUp.SetEnabled(UiPreferences.ScalePercent < UiPreferences.MaxScalePercent);
        }

        /// <summary>Credits: sections of (heading, lines), e.g. the data each downloaded area credits, and the fonts.</summary>
        public void ShowCredits(IEnumerable<(string Heading, IEnumerable<string> Lines)> sections)
        {
            _creditsBody.Clear();
            foreach (var (heading, lines) in sections)
            {
                _creditsBody.Add(Text(heading, "credits-head"));
                foreach (string line in lines) _creditsBody.Add(Text(line, "credits-line"));
            }
            Show(_credits, true);
            UiFocus.OpenModal(_credits, _root.Q<Button>("credits-close"));
        }

        public void CloseOverlay()
        {
            Show(_settings, false);
            Show(_credits, false);
            UiFocus.CloseModal(_settings);
            UiFocus.CloseModal(_credits);
        }

        static bool IsShown(VisualElement e) => e != null && !e.ClassListContains("hidden");

        // ---------- S11 ----------

        public void Confirm(string text, string ok, Action action)
        {
            _confirmText.text = text;
            _root.Q<Button>("confirm-ok").text = ok;
            _confirmAction = action;
            Show(_confirm, true);
            UiFocus.OpenModal(_confirm, _root.Q<Button>("confirm-cancel"));   // the safe choice first
        }

        public void CloseConfirm()
        {
            _confirmAction = null;
            Show(_confirm, false);
            UiFocus.CloseModal(_confirm);
        }

        // ---------- helpers ----------

        static void Show(VisualElement e, bool show) => e?.EnableInClassList("hidden", !show);

        /// <summary>The chosen option of a segmented switch (the mockup's filled segment).</summary>
        static void Mark(VisualElement e, bool on) => e?.EnableInClassList("mp-seg__opt--on", on);

        static void SetText(TextElement e, string text)
        {
            if (e != null && e.text != text) e.text = text;
        }

        static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (string c in classes) l.AddToClassList(c);
            return l;
        }

        static Button Btn(string text, string kind, Action click)
        {
            var b = new Button(click) { text = text };
            b.AddToClassList(kind == "btn--go" ? "mp-go" : "mp-ghost");   // the mockup's filled and outlined buttons
            return b;
        }
    }
}
