using System.Collections.Generic;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The map layers dropdown and the legend card (task P2-02, the mockup's .rcol): map layers in any combination,
    /// info layers one at a time plus contours, keys shown only here; one legend card for the info layer and the contours
    /// together, whose ✕ closes it until a layer is next turned on. Hidden while the menu is open (it opens over it).
    /// </summary>
    public sealed partial class MountainHud
    {
        VisualElement _legend, _legendBody;
        Label _legendTitle;
        readonly Dictionary<string, Button> _layerRows = new Dictionary<string, Button>();
        string _shownLegend, _legendInfo;
        bool _shownContours, _legendContours, _legendClosed;

        /// <summary>The Labels row: the names of the peaks on the map (no key).</summary>
        public bool LabelsOn { get; private set; } = true;

        void WireLayers()
        {
            _legend = _root.Q("legend");
            _legendBody = _root.Q("legend-body");
            _legendTitle = _root.Q<Label>("legend-title");
            _legend.RegisterCallback<GeometryChangedEvent>(_ => FitLegend());
            _root.Q<Button>("legend-x").clicked += () =>
            {
                _legendClosed = true;
                ShowLegend();
            };
            // One row per layer, named layer-<id> (MapLayers ids). Snow conditions waits for the snow simulation.
            foreach (var ids in new[] { MapLayers.MapIds, MapLayers.InfoIds })
                foreach (string id in ids)
                {
                    var row = _root.Q<Button>("layer-" + id);
                    if (row == null) continue;
                    _layerRows[id] = row;
                    row.clicked += () => LayerChanged?.Invoke(id, !row.ClassListContains("row--on"));
                    row.SetEnabled(MapLayers.IsSwitchable(id));
                }
            // Lifts, trails, snowmaking and infrastructure come with the building tools (Phase 3): inert (WirePanels).
            var labels = _root.Q<Button>("layer-labels");
            labels.EnableInClassList("row--on", LabelsOn);
            labels.clicked += () =>
            {
                LabelsOn = !LabelsOn;
                labels.EnableInClassList("row--on", LabelsOn);
            };
        }

        /// <summary>Shows a layer's state (the app owns it; a click asks for the change through <see cref="LayerChanged"/>).</summary>
        public void SetLayer(string id, bool on)
        {
            if (_layerRows.TryGetValue(id, out var row)) row.EnableInClassList("row--on", on);
        }

        /// <summary>
        /// The legend card for the info layer that's on (a <see cref="MapLayers"/> id, or null for none), with the
        /// contours' line when they're on. Rebuilt only when either (or the units) changes; turning a layer on brings a
        /// closed card back.
        /// </summary>
        public void SetLegend(string infoId, bool contours)
        {
            CheckUnits();
            if ((infoId != null && infoId != _legendInfo) || (contours && !_legendContours)) _legendClosed = false;
            _legendInfo = infoId;
            _legendContours = contours;
            if (infoId != _shownLegend || contours != _shownContours)
            {
                _shownLegend = infoId;
                _shownContours = contours;
                BuildLegend(infoId, contours, DisplayUnits.Current);
            }
            ShowLegend();
        }

        void ShowLegend()
        {
            bool show = (_legendInfo != null || _legendContours) && !_legendClosed && _drop != Drop.Menu;
            _legend.EnableInClassList("hidden", !show);
            _legend.EnableInClassList("legend--below", _drop == Drop.Layers);
            if (_drop != Drop.Layers) _root.Q("rcol").RemoveFromClassList("rcol--side");
        }

        /// <summary>Under the dropdown when it fits above the bar; beside it when it doesn't.</summary>
        void FitLegend()
        {
            var rcol = _root.Q("rcol");
            bool below = _drop == Drop.Layers && !_legend.ClassListContains("hidden");
            if (!below) { rcol.RemoveFromClassList("rcol--side"); return; }
            float stacked = _layers.layout.height + 6 + _legend.layout.height;   // the same either way
            rcol.EnableInClassList("rcol--side", rcol.layout.y + stacked > _bar.layout.y - 6);
        }

        void BuildLegend(string infoId, bool contours, UnitSystem units)
        {
            _legendBody.Clear();
            bool imperial = units == UnitSystem.Imperial;
            switch (infoId)
            {
                case MapLayers.SlopeAngle:
                    _legendTitle.text = InfoLegend.SlopeTitle;
                    foreach (var e in InfoLegend.SlopeAngle) _legendBody.Add(LegendRow(e.Colour, e.Hatched, e.Label, e.Figure));
                    _legendBody.Add(Note(InfoLegend.SlopeNote));
                    break;
                case MapLayers.Exposure:
                    _legendTitle.text = InfoLegend.ExposureTitle;
                    _legendBody.Add(ExposureCompass());
                    _legendBody.Add(Note(InfoLegend.ExposureNote));
                    break;
                case MapLayers.SnowDepth:
                    _legendTitle.text = InfoLegend.SnowDepthTitle;
                    var rows = InfoLegend.SnowDepth(units);
                    for (int k = 0; k < rows.Length; k++)
                    {
                        // The mockup's figures: "10 ft+" and a bare metric "0".
                        string figure = rows[k].Figure.Replace(" +", "+");
                        if (k == 0 && !imperial) figure = "0";
                        _legendBody.Add(LegendRow(rows[k].Colour, false, rows[k].Label, figure));
                    }
                    _legendBody.Add(Note(InfoLegend.SnowDepthNote));
                    break;
                default:
                    _legendTitle.text = "Contours";
                    break;
            }
            if (!contours) return;
            if (infoId != null)
            {
                var rule = new VisualElement { pickingMode = PickingMode.Ignore };
                rule.AddToClassList("lrule");
                _legendBody.Add(rule);
            }
            var line = new VisualElement { pickingMode = PickingMode.Ignore };
            line.AddToClassList("cl");
            line.EnableInClassList("cl--alone", infoId == null);
            var icon = new VisualElement { pickingMode = PickingMode.Ignore };
            icon.AddToClassList("cl__icon");
            icon.generateVisualContent += DrawContourIcon;
            line.Add(icon);
            line.Add(Text(imperial ? "Contours every 40 ft,\nlabelled every 200 ft" : "Contours every 10 m,\nlabelled every 50 m", "cl__text"));
            _legendBody.Add(line);
        }

        static Label Text(string text, string cls)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(cls);
            return label;
        }

        static Label Note(string text) => Text(text, "ln");

        static VisualElement LegendRow(Color colour, bool hatched, string word, string figure)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("lr");
            var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
            swatch.AddToClassList("lr__sw");
            swatch.style.backgroundColor = colour;
            if (hatched) swatch.generateVisualContent += DrawHatch;
            row.Add(swatch);
            row.Add(Text(word, "lr__word"));
            var n = Text(figure, "lr__n");
            n.AddToClassList("mono");
            row.Add(n);
            return row;
        }

        /// <summary>Experts only: white stripes at 135° over the black band (the mockup's repeating gradient).</summary>
        static void DrawHatch(MeshGenerationContext ctx)
        {
            var r = ctx.visualElement.contentRect;
            var p = ctx.painter2D;
            p.strokeColor = Color.white;
            p.lineWidth = 1.4f;
            for (float x = -r.height; x < r.width; x += 3.6f)
            {
                // each stripe from (x, 0) to (x + h, h), cut to the swatch
                float x0 = Mathf.Max(x, 0), x1 = Mathf.Min(x + r.height, r.width);
                if (x1 <= x0) continue;
                p.BeginPath();
                p.MoveTo(new Vector2(x0, x0 - x));
                p.LineTo(new Vector2(x1, x1 - x));
                p.Stroke();
            }
        }

        /// <summary>The contours' symbol: a heavy and a light wavy line, in the text colour.</summary>
        static void DrawContourIcon(MeshGenerationContext ctx)
        {
            var e = ctx.visualElement;
            var p = ctx.painter2D;
            p.strokeColor = e.resolvedStyle.color;
            p.lineWidth = 1.3f;
            p.BeginPath();
            p.MoveTo(new Vector2(0, 3.5f));
            p.BezierCurveTo(new Vector2(6, 1.5f), new Vector2(12, 5.5f), new Vector2(22, 3.5f));
            p.Stroke();
            p.lineWidth = 0.7f;
            p.strokeColor = e.resolvedStyle.color * new Color(1, 1, 1, 0.7f);
            p.BeginPath();
            p.MoveTo(new Vector2(0, 9));
            p.BezierCurveTo(new Vector2(7, 7), new Vector2(13, 10.5f), new Vector2(22, 8.5f));
            p.Stroke();
        }

        /// <summary>The exposure colours laid out as a compass: NW N NE / W flat E / SW S SE, each with its bearing.</summary>
        static VisualElement ExposureCompass()
        {
            var grid = new VisualElement { pickingMode = PickingMode.Ignore };
            grid.AddToClassList("compass");
            int[] order = { 7, 0, 1, 6, -1, 2, 5, 4, 3 };
            for (int i = 0; i < order.Length; i++)
            {
                int k = order[i];
                var colour = k < 0 ? InfoLegend.ExposureFlat : InfoLegend.ExposureColours[k];
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.AddToClassList("compass__cell");
                if (i % 3 == 1) cell.AddToClassList("compass__cell--mid");
                cell.style.backgroundColor = colour;
                // Dark words on light colours, as the mockup picks them (0.3 R + 0.59 G + 0.11 B over 150 of 255).
                var c32 = (Color32)colour;
                var ink = 0.3f * c32.r + 0.59f * c32.g + 0.11f * c32.b > 150 ? new Color32(0x1b, 0x18, 0x15, 255) : new Color32(255, 255, 255, 255);
                var dir = Text(k < 0 ? "Flat" : InfoLegend.ExposurePoints[k], "compass__dir");
                var deg = Text(k < 0 ? "<10%" : k * 45 + "°", "compass__deg");
                deg.AddToClassList("mono");
                foreach (var label in new[] { dir, deg }) label.style.color = label.style.unityTextOutlineColor = (Color)ink;
                cell.Add(dir);
                cell.Add(deg);
                grid.Add(cell);
            }
            return grid;
        }
    }
}
