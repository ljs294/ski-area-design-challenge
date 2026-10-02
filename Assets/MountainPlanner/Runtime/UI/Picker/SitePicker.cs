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
    /// overlay, a 2–5 km square centred by a click, the name (suggested, editable) and the estimate line.
    /// It raises <see cref="SiteChosen"/> with the exact EPSG:6350 square, or <see cref="Cancelled"/>; the
    /// app owns what happens next. Set <see cref="Services"/> before showing it.
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

        const long EstimateDelayMs = 350;
        const int EstimateSamples = 64;

        VisualElement _root, _results, _offline, _legend, _mapHint;
        TextField _search, _name;
        Label _searchMessage, _sizeValue, _sizeMin, _sizeMax, _nameHint, _estimate, _attribution;
        Slider _size;
        Button _download, _topo, _imagery, _coverage;
        CancellationTokenSource _searchCts, _placeCts;
        IVisualElementScheduledItem _estimateLater;
        bool _rendering;

        void OnEnable()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            _root = Document.rootVisualElement.Q("picker");
            _results = _root.Q("results");
            _results.BringToFront();   // floats over the map instead of pushing the window taller
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
            _estimate = _root.Q<Label>("estimate");
            _attribution = _root.Q<Label>("attribution");
            _size = _root.Q<Slider>("size");
            _download = _root.Q<Button>("download");
            _topo = _root.Q<Button>("base-topo");
            _imagery = _root.Q<Button>("base-imagery");
            _coverage = _root.Q<Button>("coverage-toggle");

            Map = new TileMapView { Services = Services };
            _root.Q("map-host").Add(Map);
            Map.Clicked += OnMapClicked;
            Map.NudgeRequested += (east, north) => { if (Model.Square.HasValue) { Model.Nudge(east, north); RequestDetails(suggestName: true); } };
            Map.OfflineChanged += Model.SetOffline;

            _search.RegisterCallback<KeyDownEvent>(OnSearchKey, TrickleDown.TrickleDown);
            _name.RegisterValueChangedCallback(e => { if (!_rendering) Model.TypeName(e.newValue); });
            _size.lowValue = (float)SiteSquare.MinSizeKm;
            _size.highValue = (float)SiteSquare.MaxSizeKm;
            _size.RegisterValueChangedCallback(e => { if (!_rendering) { Model.SetSize(e.newValue); RequestDetails(suggestName: false); } });
            _size.RegisterCallback<KeyDownEvent>(OnSizeKey, TrickleDown.TrickleDown);
            _root.RegisterCallback<KeyDownEvent>(OnRootKey, TrickleDown.TrickleDown);

            _root.Q<Button>("close").clicked += Cancel;
            _root.Q<Button>("cancel").clicked += Cancel;
            _root.Q<Button>("retry").clicked += Retry;
            _root.Q<Button>("zoom-in").clicked += () => Map.ZoomBy(1);
            _root.Q<Button>("zoom-out").clicked += () => Map.ZoomBy(-1);
            _topo.clicked += () => SetImagery(false);
            _imagery.clicked += () => SetImagery(true);
            _coverage.clicked += ToggleCoverage;
            _download.clicked += Download;

            _estimateLater = _root.schedule.Execute(RefreshEstimate);
            _estimateLater.Pause();
            Model.Changed += Render;
            PickerUnits.Changed += Render;
            Render();
        }

        void OnDisable()
        {
            Model.Changed -= Render;
            PickerUnits.Changed -= Render;
            _searchCts?.Cancel();
            _placeCts?.Cancel();
        }

        /// <summary>Opens the picker over whatever is behind it.</summary>
        public void Show()
        {
            Map.Services = Services;
            _attribution.text = Services?.Attribution ?? "";
            _root.style.display = DisplayStyle.Flex;
            Map.Retry();
            _search.Focus();
        }

        public void Hide() => _root.style.display = DisplayStyle.None;

        // ---- search ----

        void OnSearchKey(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            evt.StopImmediatePropagation();
            RunSearch();
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
            catch (IOException) { Model.SetOffline(true); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Picker] Search: {ex.Message}");
                Model.ShowResults(Array.Empty<PlaceResult>());
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
        }

        // ---- the square ----

        void OnMapClicked(GeoPoint point) => PlaceAt(point);

        /// <summary>Centres the square on a point, as a click on the map does, and looks up a name for it.</summary>
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
            _estimateLater.ExecuteLater(EstimateDelayMs);
            if (suggestName && Model.NameIsSuggestion && Model.Square.HasValue) SuggestName(Model.Square.Value, _placeCts.Token);
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
            catch (IOException) { Model.SetOffline(true); }
            catch (Exception ex) { Debug.LogWarning($"[Picker] Name: {ex.Message}"); }
        }

        async void RefreshEstimate()
        {
            _estimateLater.Pause();
            if (!Model.Square.HasValue || Services == null) return;
            var square = Model.Square.Value;
            var ct = _placeCts?.Token ?? CancellationToken.None;
            _estimate.text = "Checking the terrain data here…";
            double s1m = 0, ringS1m = 0, one = 0, three = 0;
            bool known = false;
            try
            {
                s1m = Share(square.Core, await Services.S1mTilesAsync(square.Core, ct));
                ringS1m = Share(square.Ring, await Services.S1mTilesAsync(square.Ring, ct));
                one = await CoverageShare(CoverageLayer.OneMetre, square.Core, ct);
                three = await CoverageShare(CoverageLayer.ThreeMetre, square.Core, ct);
                known = true;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { Model.SetOffline(true); }
            catch (Exception ex) { Debug.LogWarning($"[Picker] Estimate: {ex.Message}"); }
            if (ct.IsCancellationRequested || !Model.Square.HasValue || !Model.Square.Value.Core.Equals(square.Core)) return;
            var estimate = Services.Estimate(square, s1m, Math.Max(one, s1m), Math.Max(three, one), ringS1m, known);
            Model.SetEstimate(estimate, Services.EstimateLine(estimate));
        }

        /// <summary>The share of a box (0–1) inside the given tiles.</summary>
        public static double Share(AlbersBox box, IReadOnlyList<AlbersBox> tiles)
        {
            double covered = 0;
            foreach (var t in tiles)
            {
                double w = Math.Min(box.East, t.East) - Math.Max(box.West, t.West);
                double h = Math.Min(box.North, t.North) - Math.Max(box.South, t.South);
                if (w > 0 && h > 0) covered += w * h;
            }
            return Math.Min(1, covered / (box.Width * box.Height));
        }

        /// <summary>The share of the core inside one DEM layer's footprints, sampled from a small coverage image.</summary>
        async Task<double> CoverageShare(CoverageLayer layer, AlbersBox core, CancellationToken ct)
        {
            var corners = SlippyMap.Corners(core);
            double west = double.MaxValue, east = double.MinValue, south = double.MaxValue, north = double.MinValue;
            foreach (var c in corners)
            {
                var (x, y) = WebMercator.Forward(c);
                west = Math.Min(west, x); east = Math.Max(east, x);
                south = Math.Min(south, y); north = Math.Max(north, y);
            }
            byte[] png = await Services.CoverageImageAsync(layer, west, south, east, north, EstimateSamples, EstimateSamples, ct);
            if (png == null) return 0;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(png)) return 0;
                var pixels = tex.GetPixels32();
                int filled = 0;
                foreach (var p in pixels) if (p.a > 8) filled++;
                return pixels.Length == 0 ? 0 : filled / (double)pixels.Length;
            }
            finally { Destroy(tex); }
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

        void OnRootKey(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            evt.StopImmediatePropagation();
            if (Model.Results.Count > 0 || Model.SearchMessage.Length > 0) Model.ClearResults();
            else Cancel();
        }

        void SetImagery(bool on)
        {
            Map.Imagery = on;
            _topo.EnableInClassList("segment--on", !on);
            _imagery.EnableInClassList("segment--on", on);
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
            if (Model.Square.HasValue) RequestDetails(suggestName: Model.NameIsSuggestion);
        }

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
                _sizeMin.text = PickerUnits.SizeEnd(SiteSquare.MinSizeKm);
                _sizeMax.text = PickerUnits.SizeEnd(SiteSquare.MaxSizeKm);
                _mapHint.visible = !Model.Square.HasValue;
                _sizeValue.text = Model.SizeLabel;
                if (Math.Abs(_size.value - Model.SizeKm) > 1e-4) _size.SetValueWithoutNotify((float)Model.SizeKm);
                if (_name.value != Model.Name) _name.SetValueWithoutNotify(Model.Name);
                _nameHint.visible = Model.NameIsSuggestion && Model.Name.Length > 0;
                _offline.EnableInClassList("hidden", !Model.Offline);
                _search.SetEnabled(!Model.Offline);
                _download.SetEnabled(Model.CanDownload);

                if (!Model.Square.HasValue) _estimate.text = "Place the square to see the expected quality and download size.";
                else if (Model.Estimate.HasValue) _estimate.text = Model.EstimateLine;
                _estimate.EnableInClassList("picker-estimate--rough", Model.Estimate.HasValue && Model.Estimate.Value.IsRough);

                RenderResults();
            }
            finally { _rendering = false; }
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
                var name = new Label(p.Name);
                name.AddToClassList("result__name");
                var where = new Label(p.DisplayName);
                where.AddToClassList("result__where");
                row.Add(name);
                row.Add(where);
                _results.Add(row);
            }
        }
    }
}
