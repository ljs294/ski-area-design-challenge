using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Picker
{
    /// <summary>
    /// The S3 site picker (0.4 §4, 0.3 §6.1, T19): search (Enter only), the mini-map with the data-quality
    /// overlay, a 2–5 km square centred by a click, the name (suggested, editable) and the estimate.
    /// It raises <see cref="SiteChosen"/> with the exact EPSG:6350 square, or <see cref="Cancelled"/>; the
    /// app owns what happens next. Set <see cref="Services"/> before showing it.
    /// A modal window (0.4 §5): focus stays inside it while it is open and goes back where it was when it
    /// closes. Every control works from the keyboard: Enter searches, Down moves into the results, Enter on
    /// the map places the square there, Esc backs out one step.
    /// </summary>
    public sealed class SitePicker : MonoBehaviour
    {
        public UIDocument Document;

        public ISitePickerServices Services;

        public event Action<PickedSite> SiteChosen;
        public event Action Cancelled;

        public SitePickerModel Model { get; } = new SitePickerModel();
        public TileMapView Map { get; private set; }
        public bool IsOpen => _root != null && _root.style.display != DisplayStyle.None;
        public bool DarkTheme { get; private set; } = true;

        const long EstimateDelayMs = 350;
        const string SearchProblem = "Search isn't answering right now. Try again in a moment, or find the place on the map.";

        VisualElement _root, _window, _results, _offline, _legend, _mapHint, _figs, _sliderFill;
        TextField _search, _name;
        Label _searchMessage, _sizeValue, _sizeMin, _sizeMax, _nameHint, _empty, _score, _word, _mix, _downloadText, _warning, _note, _downloadSize, _attribution;
        Label _legendS1m, _legendLidar, _legendThree, _legendTen;
        Slider _size;
        Button _download, _topo, _imagery, _coverage;
        Focusable _focusBefore;
        CancellationTokenSource _searchCts, _placeCts;
        IVisualElementScheduledItem _estimateLater;
        bool _rendering, _checking;

        void OnEnable()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            _root = Document.rootVisualElement.Q("picker");
            _window = _root.Q("window");
            _results = _root.Q("results");
            _searchMessage = _root.Q<Label>("search-message");
            _offline = _root.Q("offline");
            _legend = _root.Q("legend");
            _mapHint = _root.Q("map-hint");
            _search = _root.Q<TextField>("search");
            _name = _root.Q<TextField>("name");
            _sizeValue = _root.Q<Label>("size-value");
            _sizeMin = _root.Q<Label>("size-min");
            _sizeMax = _root.Q<Label>("size-max");
            _nameHint = _root.Q<Label>("name-hint");
            _empty = _root.Q<Label>("estimate-empty");
            _figs = _root.Q("estimate-figs");
            _score = _root.Q<Label>("est-score");
            _word = _root.Q<Label>("est-word");
            _mix = _root.Q<Label>("est-mix");
            _downloadText = _root.Q<Label>("est-size");
            _warning = _root.Q<Label>("est-warning");
            _note = _root.Q<Label>("note");
            _downloadSize = _root.Q<Label>("download-size");
            _attribution = _root.Q<Label>("attribution");
            _legendS1m = _root.Q<Label>("legend-s1m");
            _legendLidar = _root.Q<Label>("legend-lidar");
            _legendThree = _root.Q<Label>("legend-three");
            _legendTen = _root.Q<Label>("legend-ten");
            _size = _root.Q<Slider>("size");
            _download = _root.Q<Button>("download");
            _topo = _root.Q<Button>("base-topo");
            _imagery = _root.Q<Button>("base-imagery");
            _coverage = _root.Q<Button>("coverage-toggle");

            Map = new TileMapView { Services = Services };
            _root.Q("map-host").Add(Map);
            Map.Clicked += PlaceAt;
            // A nudge doesn't change the place: keep the name (a search result's is better than a reverse lookup's).
            Map.NudgeRequested += (east, north) => { Model.Nudge(east, north); RequestDetails(suggestName: NameMissing); };
            Map.OfflineChanged += Model.SetOffline;

            _search.RegisterCallback<KeyDownEvent>(OnSearchKey, TrickleDown.TrickleDown);
            _name.RegisterValueChangedCallback(e => { if (!_rendering) Model.TypeName(e.newValue); });
            _name.textEdition.placeholder = "Name your mountain";
            _size.lowValue = (float)SiteSquare.MinSizeKm;
            _size.highValue = (float)SiteSquare.MaxSizeKm;
            _size.RegisterValueChangedCallback(e => { if (!_rendering) { Model.SetSize(e.newValue); RequestDetails(suggestName: false); } });
            _size.RegisterCallback<KeyDownEvent>(OnSizeKey, TrickleDown.TrickleDown);
            _sliderFill = new VisualElement { pickingMode = PickingMode.Ignore };
            _sliderFill.AddToClassList("picker-slider__fill");
            _size.Q(className: "unity-base-slider__tracker")?.Add(_sliderFill);
            _root.RegisterCallback<KeyDownEvent>(OnRootKey, TrickleDown.TrickleDown);
            // The focus trap: focus leaving the window (Tab past the last control) comes back to the search.
            _window.RegisterCallback<FocusOutEvent>(OnFocusOut, TrickleDown.TrickleDown);

            _root.Q<Button>("close").clicked += Cancel;
            _root.Q<Button>("cancel").clicked += Cancel;
            _root.Q<Button>("retry").clicked += Retry;
            _topo.clicked += () => SetImagery(false);
            _imagery.clicked += () => SetImagery(true);
            _coverage.clicked += ToggleCoverage;
            _download.clicked += Download;

            _estimateLater = _root.schedule.Execute(RefreshEstimate);
            _estimateLater.Pause();
            Model.Changed += Render;
            PickerUnits.Changed += OnUnitsChanged;
            Render();
        }

        void OnDisable()
        {
            Model.Changed -= Render;
            PickerUnits.Changed -= OnUnitsChanged;
            _searchCts?.Cancel();
            _placeCts?.Cancel();
        }

        /// <summary>Opens the picker over whatever is behind it (a 150 ms fade, 0.4 §7), with the search focused.</summary>
        public void Show()
        {
            Map.Services = Services;
            _attribution.text = Services?.Attribution ?? "";
            _focusBefore = _root.panel?.focusController?.focusedElement;
            _root.style.display = DisplayStyle.Flex;
            _root.style.opacity = 0;
            _root.schedule.Execute(() => _root.style.opacity = 1);
            Map.Retry();
            _search.Focus();
        }

        /// <summary>Closes the picker and gives focus back to whatever had it before.</summary>
        public void Hide()
        {
            _root.style.display = DisplayStyle.None;
            _focusBefore?.Focus();
            _focusBefore = null;
        }

        /// <summary>Dark (warm graphite) or light (sign white), as the game's theme is set.</summary>
        public void SetTheme(bool dark)
        {
            DarkTheme = dark;
            _root.EnableInClassList("picker--light", !dark);
        }

        void OnFocusOut(FocusOutEvent evt)
        {
            if (!IsOpen || evt.relatedTarget == null) return;
            if (evt.relatedTarget is VisualElement next && _window.Contains(next)) return;
            _window.schedule.Execute(() => { if (IsOpen) _search.Focus(); });
        }

        void OnUnitsChanged()
        {
            Map.RefreshDimensionText();
            Render();
        }

        // ---- search ----

        void OnSearchKey(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                evt.StopImmediatePropagation();
                RunSearch();
            }
            else if (evt.keyCode == KeyCode.DownArrow && ResultRows.Count > 0)
            {
                evt.StopImmediatePropagation();
                ResultRows[0].Focus();
            }
        }

        /// <summary>Runs the search in the field. Only Enter calls this: no search-as-you-type (Nominatim policy).</summary>
        public async void RunSearch()
        {
            string query = _search.value?.Trim() ?? "";
            if (query.Length == 0 || Model.Searching || Model.Offline || Services == null) return;
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;
            Model.BeginSearch();
            try
            {
                var results = await Services.SearchAsync(query, ct);
                if (ct.IsCancellationRequested) return;
                Model.ShowResults(results);
                if (results.Count == 1) Choose(results[0]);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Only the map decides "offline": a search hiccup says so under the field and leaves the map alone.
                if (!(ex is IOException)) Debug.LogWarning($"[Picker] Search: {ex.Message}");
                Model.ShowSearchProblem(SearchProblem);
            }
        }

        /// <summary>Flies the map to a result, centres the square on it and suggests its name.</summary>
        void Choose(PlaceResult place)
        {
            Model.ClearResults();
            if (place.North - place.South > 1e-6) Map.FitBounds(place.South, place.North, place.West, place.East);
            else Map.SetCentre(place.Location, 12);
            Model.PlaceAt(Albers6350.Forward(place.Location));
            Model.Suggest(place.Name);
            RequestDetails(suggestName: false);
            Map.Focus();
        }

        List<Button> ResultRows
        {
            get
            {
                var rows = new List<Button>();
                foreach (var child in _results.Children()) if (child is Button b) rows.Add(b);
                return rows;
            }
        }

        void OnResultKey(KeyDownEvent evt, Button row)
        {
            var rows = ResultRows;
            int i = rows.IndexOf(row);
            if (evt.keyCode == KeyCode.DownArrow && i + 1 < rows.Count) rows[i + 1].Focus();
            else if (evt.keyCode == KeyCode.UpArrow) { if (i > 0) rows[i - 1].Focus(); else _search.Focus(); }
            else return;
            evt.StopImmediatePropagation();
        }

        // ---- the square ----

        /// <summary>Centres the square on a point, as a click (or Enter) on the map does, and looks up a name for it.</summary>
        public void PlaceAt(GeoPoint point)
        {
            Model.ClearResults();
            Model.PlaceAt(Albers6350.Forward(point));
            RequestDetails(suggestName: true);
        }

        /// <summary>After the square moves or resizes: the estimate (debounced) and, for a click, one name lookup.</summary>
        void RequestDetails(bool suggestName)
        {
            _placeCts?.Cancel();
            _placeCts = new CancellationTokenSource();
            _checking = Model.Square.HasValue;
            _estimateLater.ExecuteLater(EstimateDelayMs);
            if (suggestName && Model.NameIsSuggestion && Model.Square.HasValue) SuggestName(Model.Square.Value, _placeCts.Token);
            Render();
        }

        async void SuggestName(SiteSquare square, CancellationToken ct)
        {
            if (Services == null || Model.Offline) return;
            try
            {
                string name = await Services.SuggestNameAsync(Albers6350.Inverse(square.Centre), ct);
                if (!ct.IsCancellationRequested && name != null) Model.Suggest(name);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!(ex is IOException)) Debug.LogWarning($"[Picker] Name: {ex.Message}"); }   // no suggestion; the player names it
        }

        /// <summary>The estimate, the way the downloader will score the site (one request; rough if it fails).</summary>
        async void RefreshEstimate()
        {
            _estimateLater.Pause();
            if (!Model.Square.HasValue || Services == null) return;
            var square = Model.Square.Value;
            var ct = _placeCts?.Token ?? CancellationToken.None;
            SiteEstimate estimate;
            try { estimate = await Services.EstimateAsync(square, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Picker] Estimate: {ex.Message}");
                return;
            }
            if (ct.IsCancellationRequested || !Model.Square.HasValue || !Model.Square.Value.Core.Equals(square.Core)) return;
            _checking = false;
            Model.SetEstimate(estimate);
        }

        // ---- keys and buttons ----

        void OnSizeKey(KeyDownEvent evt)
        {
            int dir = evt.keyCode == KeyCode.RightArrow || evt.keyCode == KeyCode.UpArrow ? 1
                    : evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.DownArrow ? -1 : 0;
            if (dir == 0) return;
            evt.StopImmediatePropagation();
            Model.StepSize(dir * (evt.shiftKey ? 10 : 1));
            RequestDetails(suggestName: false);
        }

        /// <summary>Esc backs out one step (controls-key-map.md): the results, then the picker.</summary>
        void OnRootKey(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            evt.StopImmediatePropagation();
            if (Model.Results.Count > 0 || Model.SearchMessage.Length > 0)
            {
                Model.ClearResults();
                _search.Focus();
            }
            else Cancel();
        }

        /// <summary>The basemap: USGS imagery, or the topo map (the default).</summary>
        public void SetImagery(bool on)
        {
            Map.Imagery = on;
            _topo.EnableInClassList("seg__item--on", !on);
            _imagery.EnableInClassList("seg__item--on", on);
        }

        void ToggleCoverage()
        {
            Map.ShowCoverage = !Map.ShowCoverage;
            _coverage.EnableInClassList("legend-toggle--on", Map.ShowCoverage);
            _legend.EnableInClassList("legend--off", !Map.ShowCoverage);
        }

        void Retry()
        {
            Model.SetOffline(false);
            Map.Retry();
            if (Model.Square.HasValue) RequestDetails(suggestName: NameMissing);
        }

        /// <summary>No name yet: only then do a nudge or a reconnect ask for one.</summary>
        bool NameMissing => Model.Name.Trim().Length == 0;

        void Cancel()
        {
            _searchCts?.Cancel();
            _placeCts?.Cancel();
            Cancelled?.Invoke();
        }

        void Download()
        {
            if (!Model.CanDownload) return;
            SiteChosen?.Invoke(Model.Choose());
        }

        // ---- render ----

        void Render()
        {
            _rendering = true;
            try
            {
                Map.Square = Model.Square;
                _legendS1m.text = PickerText.SourceLabel(Domain.Terrain.TerrainSource.S1m);
                _legendLidar.text = PickerText.SourceLabel(Domain.Terrain.TerrainSource.Lidar1m);
                _legendThree.text = PickerText.SourceLabel(Domain.Terrain.TerrainSource.ThreeMetre);
                _legendTen.text = PickerText.SourceLabel(Domain.Terrain.TerrainSource.TenMetre);
                _sizeMin.text = PickerUnits.SizeEnd(SiteSquare.MinSizeKm);
                _sizeMax.text = PickerUnits.SizeEnd(SiteSquare.MaxSizeKm);
                _mapHint.parent.EnableInClassList("hidden", Model.Square.HasValue);
                _sizeValue.text = Model.SizeLabel;
                if (Math.Abs(_size.value - Model.SizeKm) > 1e-4) _size.SetValueWithoutNotify((float)Model.SizeKm);
                _sliderFill.style.width = Length.Percent((float)((Model.SizeKm - SiteSquare.MinSizeKm) / (SiteSquare.MaxSizeKm - SiteSquare.MinSizeKm) * 100));
                // Through value (the _rendering guard stops the echo): SetValueWithoutNotify leaves a suggested
                // name styled as the placeholder.
                if (_name.value != Model.Name) _name.value = Model.Name;
                _nameHint.visible = Model.NameIsSuggestion && Model.Name.Length > 0;
                _offline.EnableInClassList("hidden", !Model.Offline);
                _search.SetEnabled(!Model.Offline);
                _download.SetEnabled(Model.CanDownload);
                RenderEstimate();
                RenderResults();
            }
            finally { _rendering = false; }
        }

        void RenderEstimate()
        {
            bool placed = Model.Square.HasValue;
            var e = Model.Estimate;
            _empty.EnableInClassList("hidden", placed);
            _figs.EnableInClassList("hidden", !placed);
            string warning = null;
            if (placed && e.HasValue && !_checking)
            {
                var est = e.Value;
                _score.text = est.TerrainScore + (est.IsRough ? "+" : "");   // rough: at least this
                _word.text = PickerText.QualityWord(est.TerrainScore);
                _mix.text = PickerText.Sources(est);
                _downloadText.text = PickerText.Download(est);
                _downloadSize.text = PickerText.Megabytes(est.Bytes);
                _score.parent.parent.EnableInClassList("fig--rough", est.IsRough);
                warning = PickerText.Warning(est);
            }
            else if (placed)
            {
                _score.text = "…";
                _word.text = "";
                _mix.text = "Checking the terrain data here…";
                _downloadText.text = "…";
                _downloadSize.text = "";
                _score.parent.parent.EnableInClassList("fig--rough", false);
            }
            else _downloadSize.text = "";
            // The line keeps its space when empty, so the centred window doesn't jump when a warning appears.
            _warning.text = warning ?? "";
            _warning.visible = warning != null;

            // What Download is waiting for, in words; nothing once it's ready.
            _note.text = !placed ? "" : Model.Name.Trim().Length == 0 ? "Name your mountain to download it." : "";
        }

        IReadOnlyList<PlaceResult> _shownResults;

        void RenderResults()
        {
            bool open = Model.Results.Count > 0 || Model.SearchMessage.Length > 0;
            _results.EnableInClassList("hidden", !open);
            _searchMessage.text = Model.SearchMessage;
            _searchMessage.EnableInClassList("hidden", Model.SearchMessage.Length == 0);
            if (ReferenceEquals(_shownResults, Model.Results)) return;
            _shownResults = Model.Results;
            for (int i = _results.childCount - 1; i >= 0; i--)
                if (_results[i] != _searchMessage) _results.RemoveAt(i);
            foreach (var place in Model.Results)
            {
                var p = place;
                var row = new Button(() => Choose(p));
                row.AddToClassList("result");
                row.RegisterCallback<KeyDownEvent>(evt => OnResultKey(evt, row), TrickleDown.TrickleDown);
                var name = new Label(p.Name) { pickingMode = PickingMode.Ignore };
                name.AddToClassList("result__name");
                var where = new Label(p.DisplayName) { pickingMode = PickingMode.Ignore };
                where.AddToClassList("result__where");
                row.Add(name);
                row.Add(where);
                _results.Add(row);
            }
        }
    }
}
