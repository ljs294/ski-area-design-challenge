using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The S6 mountain-view HUD (0.4 §4), style-tile mock: a top bar with the name and quality badge, the
    /// layers panel (map layers and info layers, with the info layer's legend card), a compass, scale bar and
    /// elevation readout, and the time bar with the lighting presets.
    /// It only shows state and raises events; the app wires them. Readouts change text only when their
    /// rounded value changes, so steady frames allocate nothing (0.4 §8).
    /// </summary>
    public sealed class MountainHud : MonoBehaviour
    {
        public UIDocument Document;
        public ThemeStyleSheet LightTheme, DarkTheme;

        /// <summary>A layer row was clicked (a <see cref="MapLayers"/> id) and the state asked for. Snow conditions is reserved.</summary>
        public event Action<string, bool> LayerChanged;
        public event Action<int> PresetChosen;
        public event Action NorthUpChosen;
        public event Action QuitChosen;
        /// <summary>The menu's Exit to title; the item shows only when the app flow is running (<see cref="ShowExitToTitle"/>).</summary>
        public event Action ExitChosen;
        /// <summary>The menu's units switch (U does the same): the app flips <see cref="DisplayUnits"/>.</summary>
        public event Action UnitsChosen;

        /// <summary>Where the contour labels go (behind the panels); <see cref="ContourLabelOverlay"/> fills it.</summary>
        public VisualElement ContourLabelLayer => _root.Q("contour-labels");

        public bool DarkThemeOn { get; private set; }
        public bool MenuOpen => _menu != null && !_menu.ClassListContains("hidden");

        VisualElement _root, _menu, _layersBody, _compass, _scaleBar, _thumb, _sun, _legend, _legendBody;
        Label _legendTitle, _legendNote;
        readonly Dictionary<string, Button> _layerRows = new Dictionary<string, Button>();
        string _shownLegend;
        bool _shownContours;
        Label _name, _quality, _scaleLabel, _elevation, _time, _caret;
        Button _themeButton;
        readonly Button[] _presets = new Button[4];
        float _heading;
        int _shownScale = -1, _shownElevation = int.MinValue, _shownPreset = -1;
        long _shownInfo = long.MinValue;
        Label _infoReadout;
        Button _unitsButton;
        UnitSystem _shownUnits = (UnitSystem)(-1);
        (double Metres, string Label)[] _scaleLengths;
        const float ScaleMaxWidth = 120;   // panel units

        void OnEnable()
        {
            if (Document == null) Document = GetComponent<UIDocument>();
            _root = Document.rootVisualElement;
            _name = _root.Q<Label>("site-name");
            _quality = _root.Q<Label>("quality");
            _menu = _root.Q("menu");
            _layersBody = _root.Q("layers-body");
            _caret = _root.Q<Label>("layers-caret");
            _compass = _root.Q("compass");
            _scaleBar = _root.Q("scale-bar");
            _scaleLabel = _root.Q<Label>("scale-label");
            _elevation = _root.Q<Label>("elevation");
            _time = _root.Q<Label>("time");
            _thumb = _root.Q("thumb");
            _sun = _root.Q("sun-icon");
            _themeButton = _root.Q<Button>("menu-theme");
            _legend = _root.Q("legend");
            _legendBody = _root.Q("legend-body");
            _legendTitle = _root.Q<Label>("legend-title");
            _legendNote = _root.Q<Label>("legend-note");

            _root.Q<Button>("menu-button").clicked += ToggleMenu;
            _root.Q<Button>("menu-resume").clicked += ToggleMenu;
            _root.Q<Button>("menu-quit").clicked += () => QuitChosen?.Invoke();
            _root.Q<Button>("menu-exit").clicked += () => ExitChosen?.Invoke();
            _root.Q<Button>("menu-settings").SetEnabled(false);
            _themeButton.clicked += () => SetTheme(!DarkThemeOn);
            _unitsButton = _root.Q<Button>("menu-units");
            _unitsButton.clicked += () => UnitsChosen?.Invoke();
            _infoReadout = _root.Q<Label>("info-readout");
            _root.Q<Button>("layers-header").clicked += () =>
            {
                bool show = _layersBody.style.display == DisplayStyle.None;
                _layersBody.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                _caret.text = show ? "–" : "+";
            };
            // One row per layer, named layer-<id> (MapLayers ids). Snow conditions waits for the snow simulation.
            foreach (var ids in new[] { MapLayers.MapIds, MapLayers.InfoIds })
                foreach (string id in ids)
                {
                    var row = _root.Q<Button>("layer-" + id);
                    if (row == null) continue;
                    _layerRows[id] = row;
                    row.clicked += () => LayerChanged?.Invoke(id, !IsOn(row));
                    row.SetEnabled(MapLayers.IsSwitchable(id));
                }
            for (int i = 0; i < _presets.Length; i++)
            {
                int index = i;
                _presets[i] = _root.Q<Button>($"preset-{i}");
                _presets[i].clicked += () => PresetChosen?.Invoke(index);
            }
            _root.Q<Button>("reset").clicked += () => PresetChosen?.Invoke(1);
            _compass.RegisterCallback<ClickEvent>(_ => NorthUpChosen?.Invoke());
            _compass.generateVisualContent += DrawCompass;
            _sun.generateVisualContent += DrawSun;
        }

        public void SetVisible(bool visible) => _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        /// <summary>Shows the menu's Exit to title (off when the viewer runs without the title flow, as captures do).</summary>
        public void ShowExitToTitle(bool show) => _root.Q("menu-exit").EnableInClassList("hidden", !show);

        public void ToggleMenu() => _menu.EnableInClassList("hidden", MenuOpen);

        public void SetTheme(bool dark)
        {
            DarkThemeOn = dark;
            var theme = dark ? DarkTheme : LightTheme;
            if (theme != null) Document.panelSettings.themeStyleSheet = theme;
            _themeButton.text = dark ? "Light theme" : "Dark theme";
            _compass.MarkDirtyRepaint();
            _sun.MarkDirtyRepaint();
        }

        public void SetSite(string name, int score)
        {
            _name.text = name;
            string band = score >= 90 ? "excellent" : score >= 75 ? "good" : score >= 50 ? "fair" : "limited";
            _quality.text = $"{score} · {char.ToUpperInvariant(band[0])}{band.Substring(1)}";
            foreach (var b in new[] { "excellent", "good", "fair", "limited" }) _quality.EnableInClassList("badge--" + b, b == band);
            _quality.tooltip = "Terrain quality (0.4 §5)";
        }

        static bool IsOn(VisualElement row) => row.ClassListContains("layer--on");

        /// <summary>Shows a layer's state (the app owns it; a click asks for the change through <see cref="LayerChanged"/>).</summary>
        public void SetLayer(string id, bool on)
        {
            if (_layerRows.TryGetValue(id, out var row)) row.EnableInClassList("layer--on", on);
        }

        /// <summary>
        /// The legend card for the info layer that's on (a <see cref="MapLayers"/> id, or null for none), with a line
        /// about the contours when they're on. Rebuilt only when either changes.
        /// </summary>
        public void SetLegend(string infoId, bool contours)
        {
            CheckUnits();
            if (infoId == _shownLegend && contours == _shownContours) return;
            _shownLegend = infoId;
            _shownContours = contours;
            var units = DisplayUnits.Current;
            _legendBody.Clear();
            string note = null;
            switch (infoId)
            {
                case MapLayers.SlopeAngle:
                    _legendTitle.text = InfoLegend.SlopeTitle;
                    foreach (var e in InfoLegend.SlopeAngle) _legendBody.Add(LegendRow(e));
                    note = InfoLegend.SlopeNote;
                    break;
                case MapLayers.Exposure:
                    _legendTitle.text = InfoLegend.ExposureTitle;
                    _legendBody.Add(ExposureCompass());
                    note = InfoLegend.ExposureNote;
                    break;
                case MapLayers.SnowDepth:
                    _legendTitle.text = InfoLegend.SnowDepthTitle;
                    foreach (var e in InfoLegend.SnowDepth(units)) _legendBody.Add(LegendRow(e));
                    note = InfoLegend.SnowDepthNote;
                    break;
                default:
                    _legendTitle.text = "Contours";
                    break;
            }
            if (contours) note = note == null ? InfoLegend.ContoursNote(units) : note + " " + InfoLegend.ContoursNote(units);
            _legendNote.text = note ?? "";
            _legendNote.style.display = note == null ? DisplayStyle.None : DisplayStyle.Flex;
            _legend.EnableInClassList("hidden", infoId == null && !contours);
        }

        static VisualElement LegendRow(in InfoLegend.Entry e)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("legend-row");
            row.Add(Swatch(e.Colour, e.Hatched));
            var label = new Label(e.Label) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("legend-label");
            row.Add(label);
            var figure = new Label(e.Figure) { pickingMode = PickingMode.Ignore };
            figure.AddToClassList("legend-figure");
            row.Add(figure);
            return row;
        }

        static VisualElement Swatch(Color colour, bool hatched)
        {
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.AddToClassList("legend-swatch");
            swatch.style.backgroundColor = colour;
            if (hatched)
                swatch.generateVisualContent += ctx =>
                {
                    var r = ctx.visualElement.contentRect;
                    var p = ctx.painter2D;
                    p.strokeColor = new Color(0.92f, 0.92f, 0.92f);
                    p.lineWidth = 2;
                    for (float x = -r.height; x < r.width; x += 6)
                    {
                        p.BeginPath();
                        p.MoveTo(new Vector2(x, r.height));
                        p.LineTo(new Vector2(x + r.height, 0));
                        p.Stroke();
                    }
                };
            return swatch;
        }

        /// <summary>The exposure colours laid out as a compass: NW N NE / W flat E / SW S SE.</summary>
        static VisualElement ExposureCompass()
        {
            var grid = new VisualElement { pickingMode = PickingMode.Ignore };
            grid.AddToClassList("legend-compass");
            int[] order = { 7, 0, 1, 6, -1, 2, 5, 4, 3 };
            foreach (int k in order)
            {
                var colour = k < 0 ? InfoLegend.ExposureFlat : InfoLegend.ExposureColours[k];
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList("legend-cell");
                cell.style.backgroundColor = colour;
                var label = new Label(k < 0 ? "flat" : InfoLegend.ExposurePoints[k]) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("legend-cell-label");
                float luminance = 0.2126f * colour.r + 0.7152f * colour.g + 0.0722f * colour.b;
                label.style.color = luminance > 0.5f ? new Color(0.1f, 0.1f, 0.1f) : Color.white;
                cell.Add(label);
                grid.Add(cell);
            }
            return grid;
        }

        /// <summary>The time bar: which preset is on, its clock time and where that falls in the day.</summary>
        public void SetPreset(int index, string clock, float dayFraction)
        {
            if (index == _shownPreset) return;
            _shownPreset = index;
            for (int i = 0; i < _presets.Length; i++) _presets[i].EnableInClassList("preset--on", i == index);
            _time.text = clock;
            _thumb.style.left = Length.Percent(dayFraction * 100);
            _sun.MarkDirtyRepaint();
        }

        /// <summary>Compass heading (degrees clockwise from north), metres per screen pixel, elevation (NaN: off the terrain).</summary>
        /// <summary>When the units change, every figure redraws: the menu item, the scale bar, the readouts and the legend.</summary>
        void CheckUnits()
        {
            var units = DisplayUnits.Current;
            if (units == _shownUnits) return;
            _shownUnits = units;
            _scaleLengths = UnitFormat.ScaleLengths(units);
            _unitsButton.text = units == UnitSystem.Imperial ? "Units: imperial" : "Units: metric";
            _shownScale = -1;
            _shownElevation = int.MinValue;
            _shownInfo = long.MinValue;
            _shownLegend = "\0";   // forces the legend to rebuild
        }

        /// <summary>
        /// What the info layer that's on reads under the pointer, below the elevation (task 12b.2): the slope as a grade
        /// and its trail band, the way it faces, or the snow depth. Null (or a NaN) hides the line. The text is rebuilt
        /// only when the shown figure changes.
        /// </summary>
        public void SetInfoReadout(string infoId, float slopePercent, float bearingDegrees, float snowMetres)
        {
            CheckUnits();
            var units = _shownUnits;
            long key;
            switch (infoId)
            {
                case MapLayers.SlopeAngle when !float.IsNaN(slopePercent): key = 1L << 40 | (uint)Mathf.RoundToInt(slopePercent); break;
                case MapLayers.Exposure when !float.IsNaN(slopePercent):
                    key = slopePercent < SlopeBands.FlatPercent ? 2L << 40 : 3L << 40 | (uint)Mathf.RoundToInt(Mathf.Repeat(bearingDegrees, 360)) % 360; break;
                case MapLayers.SnowDepth when !float.IsNaN(snowMetres): key = 4L << 40 | (uint)UnitFormat.SnowDepthKey(snowMetres, units); break;
                default: key = 0; break;
            }
            if (key == _shownInfo) return;
            _shownInfo = key;
            _infoReadout.EnableInClassList("hidden", key == 0);
            switch (key >> 40)
            {
                case 1:
                    int percent = (int)(key & 0xFFFFFFFF);
                    _infoReadout.text = $"Slope {percent}% · {SlopeBands.Names[(int)SlopeBands.Of(percent)]}";
                    break;
                case 2: _infoReadout.text = "Flat"; break;
                case 3:
                    int bearing = (int)(key & 0xFFFFFFFF);
                    _infoReadout.text = $"Faces {SlopeBands.CompassPoint(bearing)} · {bearing}°";
                    break;
                case 4: _infoReadout.text = "Snow " + UnitFormat.SnowDepth(snowMetres, units); break;
            }
        }

        public void SetReadouts(float heading, float metresPerPixel, float elevation)
        {
            CheckUnits();
            if (Mathf.Abs(Mathf.DeltaAngle(heading, _heading)) > 0.5f)
            {
                _heading = heading;
                _compass.MarkDirtyRepaint();
            }
            // Panel units per screen pixel: the panel scales with the screen height.
            float panelPerPixel = _root.resolvedStyle.width > 0 ? _root.resolvedStyle.width / Screen.width : 1;
            float metresPerUnit = metresPerPixel / Mathf.Max(panelPerPixel, 1e-4f);
            int length = 0;
            for (int k = 0; k < _scaleLengths.Length; k++) if (_scaleLengths[k].Metres / metresPerUnit <= ScaleMaxWidth) length = k;
            _scaleBar.style.width = (float)(_scaleLengths[length].Metres / metresPerUnit);
            if (length != _shownScale)
            {
                _shownScale = length;
                _scaleLabel.text = _scaleLengths[length].Label;
            }
            int shown = float.IsNaN(elevation) ? int.MinValue : UnitFormat.ElevationKey(elevation, _shownUnits);
            if (shown != _shownElevation)
            {
                _shownElevation = shown;
                _elevation.text = shown == int.MinValue ? "Elev –" : "Elev " + UnitFormat.Elevation(elevation, _shownUnits);
            }
        }

        /// <summary>True when the pointer (screen pixels, origin bottom-left) is over a HUD panel, so the camera leaves the wheel alone.</summary>
        public bool IsPointerOverPanel(Vector2 screen) => PanelPointer.IsOver(_root, screen);

        Color Themed(string light, string dark) => ColorUtility.TryParseHtmlString(DarkThemeOn ? dark : light, out var c) ? c : Color.magenta;

        void DrawCompass(MeshGenerationContext ctx)
        {
            var r = _compass.contentRect;
            var c = r.center;
            float radius = Mathf.Min(r.width, r.height) * 0.36f;
            float a = -_heading * Mathf.Deg2Rad;   // north turns against the camera's heading
            var up = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
            var side = new Vector2(-up.y, up.x) * radius * 0.32f;
            var p = ctx.painter2D;
            p.fillColor = Themed("#b33338", "#ff9a9f");   // north half in the danger red, as on a real compass
            p.BeginPath(); p.MoveTo(c + up * radius); p.LineTo(c + side); p.LineTo(c - side); p.ClosePath(); p.Fill();
            p.fillColor = Themed("#7c8e98", "#78929f");
            p.BeginPath(); p.MoveTo(c - up * radius); p.LineTo(c + side); p.LineTo(c - side); p.ClosePath(); p.Fill();
        }

        void DrawSun(MeshGenerationContext ctx)
        {
            var r = _sun.contentRect;
            var c = r.center;
            var p = ctx.painter2D;
            bool night = _shownPreset == 3;
            var colour = night ? Themed("#526570", "#b0c1ca") : Themed("#b7791f", "#f0c36d");
            p.fillColor = colour;
            p.BeginPath(); p.Arc(c, r.width * 0.22f, 0, 360); p.Fill();
            if (night) return;
            p.strokeColor = colour;
            p.lineWidth = 1.5f;
            for (int i = 0; i < 8; i++)
            {
                float t = i * Mathf.PI / 4;
                var d = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
                p.BeginPath(); p.MoveTo(c + d * r.width * 0.32f); p.LineTo(c + d * r.width * 0.46f); p.Stroke();
            }
        }
    }
}
