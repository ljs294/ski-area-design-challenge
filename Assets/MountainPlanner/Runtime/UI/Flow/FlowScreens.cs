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
    /// Areas, the S4 download card and pill, the S5 quality card, S8 Settings (<see cref="SettingsWindow"/>), S9 Credits and a
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
        /// <summary>Rename confirmed in its dialog (Manage Areas, task P2-04): the row and the name as typed.</summary>
        public event Action<LibraryRow, string> RenameConfirmed;
        /// <summary>Free space confirmed (Manage Areas, task P2-04).</summary>
        public event Action FreeSpaceConfirmed;
        public event Action<LibrarySort> SortChosen;
        public event Action MinimiseChosen, RestoreChosen, RetryChosen, CloseChosen;
        /// <summary>Try now, while the download waits for the connection (task P2-06).</summary>
        public event Action TryNowChosen;
        /// <summary>Cancel confirmed: true keeps the partial download for resuming.</summary>
        public event Action<bool> CancelConfirmed;
        public event Action QualityOpenChosen, QualityLibraryChosen;
        /// <summary>The Settings window closed (Done, ✕ or Esc).</summary>
        public event Action SettingsClosed;
        /// <summary>
        /// Settings › Data (task P2-05), forwarded here so the app flow can subscribe before the window is built: the
        /// page opened (measure the disk), a new library folder was typed, Free space was confirmed. Open uses
        /// <see cref="DataFolderChosen"/>.
        /// </summary>
        public event Action SettingsDataShown, SettingsFreeSpaceConfirmed, SettingsClearCacheConfirmed;
        public event Action<string> SettingsLibraryFolderChosen;

        VisualElement _root, _title, _library, _download, _quality, _confirm, _prompt, _settings, _credits, _stages, _barFill, _qcLines;
        TextField _promptField;
        Label _promptTitle, _promptHint;
        Button _promptOk, _free;
        Func<string, bool> _promptValid;
        Action<string> _promptAction;
        LibraryViewModel _libraryVm;
        readonly List<Label> _rowDisks = new List<Label>();
        VisualElement _dlActions, _dlConfirm, _dlFailed, _dlWaiting, _confirmPanel, _pillFill;
        Label _confirmTitle, _pillText;
        Button _confirmAlt, _confirmCancel, _confirmOk;
        Action _altAction;
        /// <summary>The view model's version last drawn: the card redraws only when it changes (task P2-06).</summary>
        int _dlVersion = -1;
        bool _pillWanted, _pillInGame;
        ScrollView _rows;
        Label _continueLabel, _continueSub, _libraryTitle, _libraryKeys, _summary, _empty, _dlTitle, _dlPercent, _dlLeft, _dlDetail, _dlTransfer, _qcTitle, _qcPlace, _confirmText, _toast;
        Button _continue, _pill, _sortOpened, _sortName, _sortQuality;
        Label _settingsResort;
        ScrollView _creditsBody;
        readonly List<VisualElement> _rowElements = new List<VisualElement>();
        readonly List<LibraryRow> _rowData = new List<LibraryRow>();
        Action _confirmAction;
        int _stagesVersion = -1, _statesVersion = -1, _selected = -1;
        float _toastUntil;

        public bool ConfirmOpen => _confirm != null && !_confirm.ClassListContains("hidden");
        /// <summary>The text dialog (Rename) is open: Enter confirms, Esc cancels.</summary>
        public bool PromptOpen => _prompt != null && !_prompt.ClassListContains("hidden");
        /// <summary>Settings or Credits is open over the title (Esc closes it first).</summary>
        public bool OverlayOpen => IsShown(_settings) || IsShown(_credits);
        public LibraryMode Mode { get; private set; } = LibraryMode.Load;
        /// <summary>The S8 Settings window's pages and rows (task P2-05).</summary>
        public SettingsWindow Settings { get; private set; }
        /// <summary>Settings is listening for a key to bind: Esc cancels that, and doesn't close the window.</summary>
        public bool CapturingKeys => Settings != null && Settings.CapturingKeys;
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
            _prompt = _root.Q("prompt");
            _promptField = _root.Q<TextField>("prompt-field");
            _promptTitle = _root.Q<Label>("prompt-title");
            _promptHint = _root.Q<Label>("prompt-hint");
            _promptOk = _root.Q<Button>("prompt-ok");
            _free = _root.Q<Button>("library-free");
            _settings = _root.Q("settings");
            _credits = _root.Q("credits");
            _creditsBody = _root.Q<ScrollView>("credits-body");
            _libraryTitle = _root.Q<Label>("library-title");
            _libraryKeys = _root.Q<Label>("library-keys");
            _stages = _root.Q("dl-stages");
            _barFill = _root.Q("dl-bar-fill");
            _qcLines = _root.Q("qc-lines");
            _dlActions = _root.Q("dl-actions");
            _dlConfirm = _root.Q("dl-confirm");
            _dlFailed = _root.Q("dl-failed");
            _dlWaiting = _root.Q("dl-waiting");
            _confirmPanel = _root.Q("confirm-panel");
            _confirmTitle = _root.Q<Label>("confirm-title");
            _confirmAlt = _root.Q<Button>("confirm-alt");
            _confirmCancel = _root.Q<Button>("confirm-cancel");
            _confirmOk = _root.Q<Button>("confirm-ok");
            _pillText = _root.Q<Label>("dl-pill-text");
            _pillFill = _root.Q("dl-pill-fill");
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
            _cover = _root.Q("cover");
            _coverFill = _root.Q("cover-fill");
            _coverText = _root.Q<Label>("cover-text");
            // While the cover is up nothing underneath takes a key: Enter can't open an area that isn't in yet (task P2-03).
            _root.RegisterCallback<KeyDownEvent>(e => { if (CoverUp) e.StopImmediatePropagation(); }, TrickleDown.TrickleDown);
            _root.RegisterCallback<NavigationSubmitEvent>(e => { if (CoverUp) e.StopImmediatePropagation(); }, TrickleDown.TrickleDown);
            _root.RegisterCallback<NavigationMoveEvent>(e => { if (CoverUp) e.StopImmediatePropagation(); }, TrickleDown.TrickleDown);

            _continue.clicked += () => ContinueChosen?.Invoke();
            _root.Q<Button>("title-new").clicked += () => NewResortChosen?.Invoke();
            _root.Q<Button>("title-load").clicked += () => LoadChosen?.Invoke();
            _root.Q<Button>("title-manage").clicked += () => ManageChosen?.Invoke();
            _root.Q<Button>("title-credits").clicked += () => CreditsChosen?.Invoke();
            _root.Q<Button>("title-settings").clicked += ShowSettings;
            _root.Q<Button>("title-quit").clicked += () => QuitChosen?.Invoke();
            _root.Q<Button>("settings-close").clicked += CloseOverlay;
            _root.Q<Button>("credits-close").clicked += CloseOverlay;
            // The mockup's Settings window: categories down the left, Restore defaults and Done at the foot (task P2-05).
            _settingsResort = _root.Q<Label>("settings-resort");
            Settings = new SettingsWindow(_settings, this);
            Settings.LibraryOpenChosen += () => DataFolderChosen?.Invoke();
            Settings.LibraryFolderChosen += folder => SettingsLibraryFolderChosen?.Invoke(folder);
            Settings.DataShown += () => SettingsDataShown?.Invoke();
            Settings.FreeSpaceConfirmed += () => SettingsFreeSpaceConfirmed?.Invoke();
            Settings.ClearCacheConfirmed += () => SettingsClearCacheConfirmed?.Invoke();
            _root.Q<Button>("settings-done").clicked += CloseOverlay;
            _root.Q<Button>("settings-defaults").clicked += () => Settings.RestoreDefaults();
            _root.Q<Button>("library-new").clicked += () => NewResortChosen?.Invoke();
            _root.Q<Button>("library-close").clicked += () => LibraryClosed?.Invoke();
            _root.Q<Button>("library-folder").clicked += () => DataFolderChosen?.Invoke();
            _free.clicked += ConfirmFreeSpace;
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
            _root.Q<Button>("dl-trynow").clicked += () => TryNowChosen?.Invoke();
            _root.Q<Button>("dl-wait-cancel").clicked += () => ShowCancelConfirm(true);
            _pill.clicked += () => RestoreChosen?.Invoke();
            _root.Q<Button>("qc-open").clicked += () => QualityOpenChosen?.Invoke();
            _root.Q<Button>("qc-library").clicked += () => QualityLibraryChosen?.Invoke();
            _confirmCancel.clicked += CloseConfirm;
            _confirmOk.clicked += () =>
            {
                var action = _confirmAction;
                CloseConfirm();
                action?.Invoke();
            };
            _confirmAlt.clicked += () =>
            {
                var action = _altAction;
                CloseConfirm();
                action?.Invoke();
            };
            _root.Q<Button>("prompt-cancel").clicked += ClosePrompt;
            _root.Q<Button>("prompt-close").clicked += ClosePrompt;
            _promptOk.clicked += SubmitPrompt;
            _promptField.RegisterValueChangedCallback(_ => _promptOk.SetEnabled(_promptValid == null || _promptValid(_promptField.value)));
            // Enter confirms from the field itself, as in the mockup; the field keeps the arrows for its caret.
            _promptField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                SubmitPrompt();
            }, TrickleDown.TrickleDown);
        }

        void OnDisable() => Settings?.Dispose();

        void Update()
        {
            if (_toast != null && _toastUntil > 0 && Time.unscaledTime > _toastUntil)
            {
                _toastUntil = 0;
                Show(_toast, false);
            }
            if (_coverGoneAt > 0 && Time.unscaledTime > _coverGoneAt)
            {
                _coverGoneAt = 0;
                Show(_cover, false);   // faded out: out of the layout and the pointer's way
            }
        }

        // ---------- the cover (task P2-03) ----------

        VisualElement _cover, _coverFill;
        Label _coverText;
        float _coverGoneAt, _coverShown = -1;

        /// <summary>The cover is up: a mountain is opening under it, and the flow's screens take no keys.</summary>
        public bool CoverUp { get; private set; }

        /// <summary>
        /// Alpine Labs' cover: the game's name plate on warm near-black, a thin progress rule and "Opening …", over the
        /// whole screen while a mountain opens, so its tiles are never seen filling in (owner, task P2-03).
        /// </summary>
        public void ShowCover(string text)
        {
            if (_cover == null) return;
            CoverUp = true;
            _coverGoneAt = 0;
            _cover.RemoveFromClassList("cover--fading");
            _cover.pickingMode = PickingMode.Position;   // nothing underneath takes a click
            Show(_cover, true);
            _coverText.text = text ?? "";
            _coverShown = -1;
            SetCoverProgress(0);
        }

        /// <summary>The rule's fill, 0–1 (only restyled when it moves a visible step).</summary>
        public void SetCoverProgress(float fraction)
        {
            float f = Mathf.Round(Mathf.Clamp01(fraction) * 200) / 200;
            if (_coverFill == null || f == _coverShown) return;
            _coverShown = f;
            _coverFill.style.width = Length.Percent(f * 100);
        }

        /// <summary>Fades the cover out over the finished mountain; the title's first sign takes the keyboard.</summary>
        public void HideCover()
        {
            if (!CoverUp) return;
            CoverUp = false;
            SetCoverProgress(1);
            _cover.AddToClassList("cover--fading");
            _cover.pickingMode = PickingMode.Ignore;   // the mountain takes the pointer while the cover fades
            _coverGoneAt = Time.unscaledTime + CoverFadeSeconds;
            if (IsShown(_title)) UiFocus.FocusSoon(_title);
        }

        public const float CoverFadeSeconds = 0.7f;

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
            // A redraw of the list on screen (a rename, a delete, a sort) keeps the same row selected, and keeps the
            // keyboard in the list; opening the screen, or the other mode, starts from the top as before.
            bool redraw = LibraryVisible && mode == Mode;
            var focused = _root.panel?.focusController?.focusedElement as VisualElement;
            bool keepFocus = redraw && (focused == null || focused.panel == null || _rows.Contains(focused) || _prompt.Contains(focused) || _confirm.Contains(focused));
            string keep = redraw ? Key(SelectedRow) : null;
            Mode = mode;
            bool manage = mode == LibraryMode.Manage;
            _libraryTitle.text = manage ? "Manage Areas" : "Load Area";
            _libraryKeys.text = manage ? "F2 renames · Delete removes · Esc goes back" : "Enter opens · Esc goes back";
            _libraryVm = vm;
            ShowSizes(vm);
            Mark(_sortOpened, vm.Sort == LibrarySort.LastOpened);
            Mark(_sortName, vm.Sort == LibrarySort.Name);
            Mark(_sortQuality, vm.Sort == LibrarySort.Quality);
            _rows.Clear();
            _rowElements.Clear();
            _rowData.Clear();
            _rowDisks.Clear();
            foreach (var row in vm.Rows)
            {
                if (row.IsPaused && !manage) continue;   // paused downloads are managed, not loaded
                var r = row;
                var el = new VisualElement();
                el.AddToClassList("lib-row");
                var name = Text(r.Name, "lib-name");
                name.tooltip = r.Name;   // a long one ends in an ellipsis
                el.Add(name);
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
                    el.Add(DiskCell(r));
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
                    el.Add(DiskCell(r));
                    el.Add(Text(r.Opened, "lib-cell", "lib-cell--when"));
                    var spacer = new VisualElement();
                    spacer.AddToClassList("spacer");
                    el.Add(spacer);
                    var actions = new VisualElement();
                    actions.AddToClassList("lib-actions");
                    if (manage && r.IsBuiltIn)
                    {
                        // The demo is part of the game (task P2-03): nothing to rename or delete.
                        var note = Text("Built into the game", "lib-builtin");
                        note.tooltip = BundledAreas.Refusal;
                        actions.Add(note);
                    }
                    else if (manage)
                    {
                        var rename = Btn("Rename", "btn--ghost", () => OpenRename(r));
                        rename.SetEnabled(r.CanRename);
                        if (!r.CanRename) rename.tooltip = r.Entry.RenameRefusal;
                        actions.Add(rename);
                        actions.Add(Btn("Delete", "btn--ghost", () => ConfirmDelete(r)));
                    }
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
                    el.RegisterCallback<KeyDownEvent>(e =>
                    {
                        if (e.target != el || !manage) return;
                        if (e.keyCode == KeyCode.Delete && r.CanDelete) ConfirmDelete(r);
                        else if (e.keyCode == KeyCode.F2) OpenRename(r);
                    });
                }
                el.RegisterCallback<FocusInEvent>(_ => Select(_rowData.IndexOf(r)));
                _rows.Add(el);
                _rowElements.Add(el);
                _rowData.Add(r);
            }
            _empty.text = vm.EmptyText;
            Show(_empty, _rowData.Count == 0);
            int kept = keep == null ? -1 : _rowData.FindIndex(x => Key(x) == keep);
            int first = _rowData.FindIndex(x => x.CanOpen);
            Select(kept >= 0 ? kept : first >= 0 ? first : _rowData.FindIndex(x => !x.IsPaused));
            if (keepFocus) FocusSelectedRow();   // also when that row was just deleted or renamed: the keyboard stays in the list
        }

        /// <summary>A row's identity across redraws: its folder, or the paused download's id.</summary>
        static string Key(LibraryRow r) => r == null ? null : r.Pending != null ? "p:" + r.Pending.Id : "a:" + r.Entry?.Folder;

        Label DiskCell(LibraryRow r)
        {
            var l = Text(r.Disk, "lib-cell", "lib-cell--narrow", "mono");
            l.tooltip = r.DiskDetail;
            _rowDisks.Add(l);
            l.userData = r;
            return l;
        }

        /// <summary>The summary and the Free space button (shown in Manage Areas when there's something to free).</summary>
        void ShowSizes(LibraryViewModel vm)
        {
            _summary.text = vm.Summary;
            _free.text = vm.FreeText;
            Show(_free, Mode == LibraryMode.Manage && vm.FreeableBytes > 0);
        }

        /// <summary>
        /// Disk use measured off the main thread arrived (task P2-04): the sizes change in place, so the list,
        /// the selection and the keyboard focus stay as they are. <paramref name="vm"/> has the same rows.
        /// </summary>
        public void UpdateLibrarySizes(LibraryViewModel vm)
        {
            if (_libraryVm == null || vm.Rows.Count != _libraryVm.Rows.Count) { RenderLibrary(vm, Mode); return; }
            _libraryVm = vm;
            ShowSizes(vm);
            foreach (var label in _rowDisks)
            {
                var old = (LibraryRow)label.userData;
                var row = vm.Rows.Find(x => Key(x) == Key(old));
                if (row == null) continue;
                SetText(label, row.Disk);
                label.tooltip = row.DiskDetail;
                old.Disk = row.Disk;
                old.DiskDetail = row.DiskDetail;
            }
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
            if (r?.Entry == null || !r.CanDelete) return;
            string frees = r.Entry.Measured ? $" This frees {LibraryViewModel.Disk(r.Entry.BytesOnDisk)}." : "";
            Confirm($"Delete {r.Name}?{frees} You can download it again later.", "Delete", () => DeleteConfirmed?.Invoke(r));
        }

        /// <summary>Rename (Manage Areas, F2): the mockup's Rename dialog, with the current name selected.</summary>
        public void OpenRename(LibraryRow r)
        {
            if (r == null || !r.CanRename) return;
            Prompt("Rename area", r.Name, "Shown in the library, the title's Continue and the game's status bar. The download keeps its own name.",
                   "Rename", ResortLibrary.MaxNameLength, text => ResortLibrary.NormalizeName(text).Length > 0, name => RenameConfirmed?.Invoke(r, name));
        }

        /// <summary>Free space: says what goes, and that the areas themselves stay.</summary>
        public void ConfirmFreeSpace()
        {
            if (_libraryVm == null || _libraryVm.FreeableBytes <= 0) return;
            Confirm($"Remove {LibraryViewModel.Disk(_libraryVm.FreeableBytes)} of terrain caches left by older versions of the game? " +
                    "Your areas stay, and nothing has to be downloaded or rebuilt.",
                    _libraryVm.FreeText, () => FreeSpaceConfirmed?.Invoke(), danger: false);
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

        /// <summary>
        /// The card when open; otherwise the pill while a download is active. In the game the pill is the HUD bar's
        /// (task P2-06, owner D3), so this one stays hidden there.
        /// </summary>
        public void ShowDownloadCard(bool open, bool active, bool inGame)
        {
            Show(_download, open && active);
            _pillWanted = !open && active;
            _pillInGame = inGame;
            Show(_pill, _pillWanted && !inGame);
            if (!open) ShowCancelConfirm(false);
        }

        /// <summary>The pill is showing on the flow's screens (S1, S2, the picker).</summary>
        public bool PillShown => IsShown(_pill);

        /// <summary>The pill's class for a phase: its hairline's colour.</summary>
        public static string PillState(DownloadPhase phase) =>
            phase == DownloadPhase.Waiting ? "pill--waiting" : phase == DownloadPhase.Paused ? "pill--paused" : phase == DownloadPhase.Failed ? "pill--failed" : "";

        /// <summary>Draws the card and pill; only when the view model has changed (at most 4 times a second while it runs).</summary>
        public void RenderDownload(DownloadViewModel vm)
        {
            if (vm.Version == _dlVersion) return;
            _dlVersion = vm.Version;
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
            SetText(_pillText, vm.Pill);
            _barFill.style.width = Length.Percent(vm.Fraction * 100f);
            _pillFill.style.width = Length.Percent(vm.Fraction * 100f);
            foreach (string c in new[] { "pill--waiting", "pill--paused", "pill--failed" }) _pill.EnableInClassList(c, c == PillState(vm.Phase));
            bool failed = vm.Phase == DownloadPhase.Failed, waiting = vm.Phase == DownloadPhase.Waiting;
            bool confirming = !_dlConfirm.ClassListContains("hidden");
            Show(_dlFailed, failed);
            Show(_dlWaiting, waiting && !confirming);
            if (failed) Show(_dlConfirm, false);
            Show(_dlActions, !failed && !waiting && !confirming);
            _download.EnableInClassList("download--waiting", waiting);
        }

        /// <summary>Forces the next <see cref="RenderDownload"/> to draw (a new card, a theme change).</summary>
        public void InvalidateDownload() => _dlVersion = -1;

        void ShowCancelConfirm(bool show)
        {
            if (_dlConfirm == null) return;
            bool waiting = _download.ClassListContains("download--waiting");
            Show(_dlConfirm, show);
            Show(_dlActions, !show && !waiting);
            Show(_dlWaiting, !show && waiting);
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
            SetText(_settingsResort, resort ?? "");
            Settings.Opened();
            Show(_settings, true);
            UiFocus.OpenModal(_settings, Settings.FirstTab);
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
            bool settings = IsShown(_settings);
            if (settings) Settings.Closed();
            Show(_settings, false);
            Show(_credits, false);
            UiFocus.CloseModal(_settings);
            UiFocus.CloseModal(_credits);
            if (settings) SettingsClosed?.Invoke();
        }

        static bool IsShown(VisualElement e) => e != null && !e.ClassListContains("hidden");

        // ---------- S11 ----------

        /// <summary>The confirm dialog; <paramref name="danger"/> paints its button red (deleting) rather than green.</summary>
        public void Confirm(string text, string ok, Action action, bool danger = true)
        {
            SetDialog("", text, ok, action, danger, "Cancel", null, null, error: false);
            UiFocus.OpenModal(_confirm, _confirmCancel);   // the safe choice first
        }

        /// <summary>
        /// An error or offline dialog (S11, task P2-06): what happened as the heading, what to do below, then up to
        /// three buttons. <paramref name="ok"/> (green) is the way forward and has the focus; <paramref name="alt"/> sits
        /// on the left (e.g. Settings › Data); <paramref name="close"/> just closes. Esc closes too.
        /// </summary>
        public void Alert(string title, string text, string ok, Action okAction, string close = "Close", string alt = null, Action altAction = null, bool error = true)
        {
            SetDialog(title, text, ok, okAction, false, close, alt, altAction, error);
            UiFocus.OpenModal(_confirm, ok != null ? _confirmOk : _confirmCancel);
        }

        /// <summary>The open dialog's heading ("" for a plain confirm): the captures and tests read it.</summary>
        public string DialogTitle => ConfirmOpen ? _confirmTitle.text : "";

        void SetDialog(string title, string text, string ok, Action okAction, bool danger, string close, string alt, Action altAction, bool error)
        {
            _confirmTitle.text = title ?? "";
            Show(_confirmTitle, !string.IsNullOrEmpty(title));
            _confirmText.text = text;
            _confirmOk.text = ok ?? "";
            Show(_confirmOk, ok != null);
            _confirmOk.EnableInClassList("mp-go--danger", danger);
            _confirmCancel.text = close;
            _confirmAlt.text = alt ?? "";
            Show(_confirmAlt, alt != null);
            _confirmPanel.EnableInClassList("confirm-panel--error", error && !string.IsNullOrEmpty(title));
            _confirmPanel.EnableInClassList("confirm-panel--wide", !string.IsNullOrEmpty(title));
            _confirmAction = okAction;
            _altAction = altAction;
            Show(_confirm, true);
        }

        public void CloseConfirm()
        {
            _confirmAction = null;
            _altAction = null;
            Show(_confirm, false);
            UiFocus.CloseModal(_confirm);
        }

        /// <summary>
        /// A one-line text dialog (the mockup's Rename): a title, the field with <paramref name="value"/> selected, a
        /// hint, then the go button and Cancel. Enter confirms while <paramref name="valid"/> accepts the text; Esc and
        /// Cancel close it. Reusable by any screen on this document (task 06's dialogs).
        /// </summary>
        public void Prompt(string title, string value, string hint, string ok, int maxLength, Func<string, bool> valid, Action<string> action)
        {
            _promptTitle.text = title;
            _promptHint.text = hint ?? "";
            Show(_promptHint, !string.IsNullOrEmpty(hint));
            _promptOk.text = ok;
            _promptValid = valid;
            _promptAction = action;
            _promptField.maxLength = maxLength;
            _promptField.SetValueWithoutNotify(value ?? "");
            _promptOk.SetEnabled(valid == null || valid(_promptField.value));
            Show(_prompt, true);
            UiFocus.OpenModal(_prompt, _promptField);
            _promptField.schedule.Execute(() => _promptField.SelectAll()).ExecuteLater(50);   // after it takes focus
        }

        public void ClosePrompt()
        {
            _promptAction = null;
            _promptValid = null;
            Show(_prompt, false);
            UiFocus.CloseModal(_prompt);
        }

        void SubmitPrompt()
        {
            string text = _promptField.value;
            if (_promptValid != null && !_promptValid(text)) return;
            var action = _promptAction;
            ClosePrompt();
            action?.Invoke(text);
        }

        // ---------- helpers ----------

        internal static void Show(VisualElement e, bool show) => e?.EnableInClassList("hidden", !show);

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
