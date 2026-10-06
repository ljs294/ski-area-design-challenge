using System;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The HUD's panels (task P2-02): the Toolbox tray and Analysis (placeholders until Phases 3 and 4: every tab and tile
    /// as the mockup draws them, greyed, with a line saying what fills them), the resort's stats, notifications,
    /// tooltips and the floating window the tools will use.
    /// </summary>
    public sealed partial class MountainHud
    {
        // The mockup's tools (its TOOLS table): id, tile label, name, key, symbol, badge.
        static readonly (string Tab, string Id, string Label, string Name, string Key, string Glyph, string Badge)[] Tools =
        {
            ("lifts", "new-lift", "New", "New lift", "N", "s-lift", "new"),
            ("lifts", "edit-lift", "Edit", "Edit lift", "E", "s-lift", "edit"),
            ("lifts", "remove-lift", "Remove", "Remove lift", "X", "s-lift", "remove"),
            ("trails", "new-trail", "New", "New trail", "N", "s-trail", "new"),
            ("trails", "edit-trail", "Edit", "Edit trail", "E", "s-trail", "edit"),
            ("trails", "remove-trail", "Remove", "Remove trail", "X", "s-trail", "remove"),
            ("snow", "gun", "Snow gun", "Snow gun", "G", "s-gun", null),
            ("snow", "pipe", "Water line", "Water line", "W", "s-pipe", null),
            ("snow", "pump", "Pump house", "Pump house", "P", "s-pump", null),
            ("infra", "lodge", "Lodge", "Lodge", "L", "s-lodge", null),
            ("infra", "road", "Road", "Road", "R", "s-road", null),
            ("infra", "parking", "Parking", "Parking", "K", "s-parking", null),
        };
        static readonly string[] ToolboxTabs = { "lifts", "trails", "snow", "infra" };
        static readonly string[] AnalysisTabs = { "overview", "lifts", "trails", "guests", "snow", "weather", "amenities", "finances" };
        /// <summary>Each Analysis tab's line until its phase fills it (the mockup's demo=p2 wording).</summary>
        static readonly string[] AnalysisEmpty =
        {
            "The resort's day (lifts, trails, guests and weather) comes with the simulation in Phase 4.",
            "Lifts come with the building tools in Phase 3.",
            "Trails come with the building tools in Phase 3.",
            "Guests come with the simulation in Phase 4.",
            "Snowmaking comes with the building tools in Phase 3.",
            "Weather comes with the snow simulation in Phase 4.",
            "Lodges and parking come with the building tools in Phase 3.",
            "Money comes with the simulation in Phase 4.",
        };

        VisualElement _toolbox, _analysis, _tools, _rstats, _figs, _gauge, _tip, _toasts;
        Label _toolName, _toolKey, _toolHint, _analysisEmpty, _statsFoot;
        Button _barToolbox, _barAnalysis;
        bool _toolboxOpen, _analysisOpen, _statsOpen;
        string _toolboxTab = "lifts", _analysisTab = "overview";
        int _qualityScore;
        double _summitMetres = double.NaN, _baseMetres = double.NaN, _areaSquareMetres = double.NaN;

        void WirePanels()
        {
            _toolbox = _root.Q("toolbox");
            _analysis = _root.Q("analysis");
            _tools = _root.Q("toolbox-tools");
            _toolName = _root.Q<Label>("toolbox-label-name");
            _toolKey = _root.Q<Label>("toolbox-label-key");
            _toolHint = _root.Q<Label>("toolbox-label-hint");
            _analysisEmpty = _root.Q<Label>("analysis-empty");
            _barToolbox = _root.Q<Button>("bar-toolbox");
            _barAnalysis = _root.Q<Button>("bar-analysis");
            _rstats = _root.Q("rstats");
            _figs = _root.Q("rstats-figs");
            _gauge = _root.Q("rstats-gauge");
            _statsFoot = _root.Q<Label>("rstats-foot");
            _tip = _root.Q("tooltip");
            _toasts = _root.Q("toasts");

            foreach (string tab in ToolboxTabs)
            {
                string t = tab;
                _root.Q<Button>("toolbox-tab-" + tab).clicked += () => SetToolboxTab(t);
            }
            foreach (string tab in AnalysisTabs)
            {
                string t = tab;
                var b = _root.Q<Button>("analysis-tab-" + tab);
                if (tab == "guests") Inert(b, "tab--dis", AnalysisEmpty[3]);   // greyed until the simulation (the spec)
                else b.clicked += () => SetAnalysisTab(t);
            }
            _root.Q<Button>("analysis-x").clicked += () => SetAnalysis(false);
            SetToolboxTab(_toolboxTab);
            SetAnalysisTab(_analysisTab);

            _root.Q<Button>("rstats-x").clicked += () => SetStats(false);
            Inert(_root.Q("rstats-rename"), "ghost--dis", null);
            _modal.RegisterCallback<ClickEvent>(e => { if (e.target == _modal) SetStats(false); });   // a click on the scrim
            _gauge.generateVisualContent += DrawGauge;

            // Inert parts keep their tooltips (as the mockup's titles do) but take no clicks or keyboard stops.
            Inert(_trSketch, "sq--dis", null);
            foreach (string name in new[] { "bar-money", "bar-lifts", "bar-trails", "bar-guests", "bar-save" }) Inert(_root.Q(name), null, null);
            foreach (string name in new[] { "menu-save", "menu-rename", "layer-lifts", "layer-trails", "layer-snowmaking", "layer-infrastructure", "layer-conditions" })
                Inert(_root.Q(name), "row--dis", null);

            WireTooltips();
            WireToasts();
        }

        /// <summary>A part that comes later: greyed by its class, no clicks, no keyboard stop, its tooltip kept.</summary>
        static void Inert(VisualElement e, string cls, string tooltip)
        {
            if (e == null) return;
            e.SetEnabled(true);
            e.focusable = false;
            if (cls != null) e.AddToClassList(cls);
            if (tooltip != null) e.tooltip = tooltip;
            if (e is Button b) b.clickable = null;
        }

        // ---------- the Toolbox tray ----------

        public void ToggleToolbox() => SetToolbox(!_toolboxOpen);

        public void SetToolbox(bool open)
        {
            _toolboxOpen = open;
            if (open && _analysisOpen) SetAnalysis(false);
            _toolbox.EnableInClassList("hidden", !open);
            _barToolbox.EnableInClassList("tbtn--on", open);
            RetintSoon();
        }

        public void SetToolboxTab(string tab)
        {
            _toolboxTab = tab;
            foreach (string t in ToolboxTabs) _root.Q("toolbox-tab-" + t).EnableInClassList("tab--on", t == tab);
            _tools.Clear();
            foreach (var tool in Tools)
            {
                if (tool.Tab != tab) continue;
                var tile = new Button { name = "tool-" + tool.Id };   // the tool label says what it is (the mockup's hover)
                tile.AddToClassList("tool");
                tile.AddToClassList("tool--dis");
                tile.focusable = false;
                tile.clickable = null;
                var glyph = new HudIcon(tool.Glyph, tool.Badge);
                glyph.name = "tool-" + tool.Id + "-glyph";
                glyph.AddToClassList("tool__glyph");
                tile.Add(glyph);
                var label = new Label(tool.Label) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("tool__label");
                tile.Add(label);
                var t = tool;
                tile.RegisterCallback<PointerEnterEvent>(_ => ShowTool(t.Name, t.Key, "comes in Phase 3"));
                tile.RegisterCallback<PointerLeaveEvent>(_ => ShowTool("Select tool", "", ""));
                _tools.Add(tile);
            }
            ShowTool("Select tool", "", "");
            RetintSoon();
        }

        void ShowTool(string name, string key, string hint)
        {
            _toolName.text = name;
            _toolKey.text = key;
            _toolHint.text = hint;
            _toolKey.EnableInClassList("hidden", key.Length == 0);
            _toolHint.EnableInClassList("hidden", hint.Length == 0);
        }

        // ---------- Analysis ----------

        public void ToggleAnalysis() => SetAnalysis(!_analysisOpen);

        /// <summary>A bar figure's tab: opens Analysis on it, or closes Analysis if it's already showing it (the mockup's aopen).</summary>
        public void OpenAnalysisTab(string tab)
        {
            if (_analysisOpen && _analysisTab == tab) { SetAnalysis(false); return; }
            SetAnalysisTab(tab);
            SetAnalysis(true);
        }

        public void SetAnalysis(bool open)
        {
            _analysisOpen = open;
            if (open && _toolboxOpen) SetToolbox(false);
            _analysis.EnableInClassList("hidden", !open);
            _barAnalysis.EnableInClassList("tbtn--on", open);
            _when.EnableInClassList("stat--on", open && _analysisTab == "weather");
            RetintSoon();
        }

        public void SetAnalysisTab(string tab)
        {
            _analysisTab = tab;
            int i = Array.IndexOf(AnalysisTabs, tab);
            foreach (string t in AnalysisTabs) _root.Q("analysis-tab-" + t).EnableInClassList("tab--on", t == tab);
            _analysisEmpty.text = AnalysisEmpty[Mathf.Max(0, i)];
            _when?.EnableInClassList("stat--on", _analysisOpen && tab == "weather");
        }

        public string AnalysisTab => _analysisTab;

        // ---------- the resort's stats ----------

        /// <summary>The terrain's highest and lowest points and the area's size: the stats until lifts are built.</summary>
        public void SetTerrain(double highestMetres, double lowestMetres, double areaSquareMetres)
        {
            _summitMetres = highestMetres;
            _baseMetres = lowestMetres;
            _areaSquareMetres = areaSquareMetres;
            if (_statsOpen) FillStats();
        }

        void SetStatsSite(int score)
        {
            _qualityScore = score;
            _root.Q<Label>("rstats-name").text = _siteName;
            var place = _root.Q<Label>("rstats-place");
            place.text = _place;
            place.EnableInClassList("hidden", _place.Length == 0);
        }

        public void SetStats(bool open)
        {
            if (open == _statsOpen) return;
            _statsOpen = open;
            if (open) SetDrop(Drop.None);
            _modal.EnableInClassList("hidden", !open);
            if (open)
            {
                SetScrim();
                FillStats();
                UiFocus.OpenModal(_rstats, _root.Q<Button>("rstats-x"));
            }
            else UiFocus.CloseModal(_rstats);
        }

        /// <summary>
        /// UI Toolkit blends in linear light, where the mockup's --scrim (0.45 dark, 0.30 light over sRGB) reads much
        /// paler; these alphas give the mockup's dimming over the map.
        /// </summary>
        void SetScrim() => _modal.style.backgroundColor = (Color)(UiPreferences.Dark ? new Color32(10, 16, 26, 174) : new Color32(20, 30, 45, 125));

        void FillStats()
        {
            var units = DisplayUnits.Current;
            _figs.Clear();
            bool known = !double.IsNaN(_summitMetres) && !double.IsNaN(_baseMetres);
            AddSection("Mountain", true);
            AddFigure("Summit elevation", known ? Length(_summitMetres, units) : null);
            AddFigure("Base elevation", known ? Length(_baseMetres, units) : null);
            AddFigure("Vertical drop", known ? Length(_summitMetres - _baseMetres, units) : null);
            AddFigure("Area", double.IsNaN(_areaSquareMetres) ? null : Area(_areaSquareMetres, units));
            AddSection("Lifts", false);
            AddFigure("Lifts", null);
            AddFigure("Uphill capacity", null);
            AddSection("Trails", false);
            AddFigure("Trails", null);
            AddFigure("Trail area", null);
            AddSection("Snow", false);
            AddFigure("Average annual snowfall", null);
            AddFigure("Snowmaking capacity", null);
            AddFigure("Snowmaking water, this season", null);
            string band = _qualityScore >= 90 ? "Excellent" : _qualityScore >= 75 ? "Good" : _qualityScore >= 50 ? "Fair" : "Limited";
            _statsFoot.text = $"Summit and base are the terrain's highest and lowest points until lifts are built. Data quality {_qualityScore} · {band}.";
            _gauge.MarkDirtyRepaint();
            _gauge.Clear();
            if (known) GaugeLabels(units);
        }

        static (string Figure, string Unit) Length(double metres, UnitSystem units)
        {
            string text = UnitFormat.Elevation(metres, units);   // "10,449 ft"
            int space = text.LastIndexOf(' ');
            return (text.Substring(0, space), text.Substring(space + 1));
        }

        static (string, string) Area(double squareMetres, UnitSystem units) => units == UnitSystem.Imperial
            ? (Math.Round(squareMetres / 4046.86).ToString("N0", System.Globalization.CultureInfo.InvariantCulture), "acres")
            : (Math.Round(squareMetres / 10000).ToString("N0", System.Globalization.CultureInfo.InvariantCulture), "ha");

        void AddSection(string title, bool first)
        {
            if (!first)
            {
                var rule = new VisualElement { pickingMode = PickingMode.Ignore };
                rule.AddToClassList("rule");
                _figs.Add(rule);
            }
            var sub = Text(title, "sub");
            if (first) sub.AddToClassList("sub--first");
            _figs.Add(sub);
            var kv = new VisualElement { pickingMode = PickingMode.Ignore };
            kv.AddToClassList("kv");
            _figs.Add(kv);
        }

        void AddFigure(string name, (string Figure, string Unit)? value)
        {
            var kv = _figs[_figs.childCount - 1];
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("kv__row");
            row.Add(Text(name, "kv__dt"));
            var dd = new VisualElement { pickingMode = PickingMode.Ignore };
            dd.AddToClassList("kv__dd");
            if (value == null)
            {
                var dash = Text("—", "kv__u");
                dash.AddToClassList("nodata");
                dd.Add(dash);
            }
            else
            {
                var b = Text(value.Value.Figure, "kv__b");
                b.AddToClassList("mono");
                dd.Add(b);
                var u = Text(value.Value.Unit, "kv__u");
                u.AddToClassList("mono");
                u.AddToClassList("kv__u--gap");
                dd.Add(u);
            }
            row.Add(dd);
            kv.Add(row);
        }

        // The gauge (the mockup's elevGauge): the mountain's height from base to summit as a ruled scale with the vertical
        // drop dimensioned. 150 × 236 inside its padding.
        const float GaugeW = 150, GaugeH = 236, GaugeTop = 22, GaugeBottom = GaugeH - 26;

        float GaugeY(double e) => (float)(GaugeBottom - (e - _baseMetres) / Math.Max(1, _summitMetres - _baseMetres) * (GaugeBottom - GaugeTop));

        void GaugeLabels(UnitSystem units)
        {
            string summit = "Summit " + UnitFormat.Elevation(_summitMetres, units), @base = "Base " + UnitFormat.Elevation(_baseMetres, units);
            AddGaugeLabel(summit, "gauge__lab", GaugeY(_summitMetres) - 6, right: true);
            AddGaugeLabel(@base, "gauge__lab", GaugeY(_baseMetres) + 14, right: true);
            var plate = AddGaugeLabel(UnitFormat.Elevation(_summitMetres - _baseMetres, units), "gauge__plate", (GaugeTop + GaugeBottom) / 2 + 4, right: false);
            plate.AddToClassList("mono");
        }

        Label AddGaugeLabel(string text, string cls, float baseline, bool right)
        {
            var label = Text(text, cls);
            label.style.position = Position.Absolute;
            label.style.top = 10 + baseline - (right ? 10 : 13);   // the gauge's top padding; a label's baseline sits about 10 px down, the plate's 13
            if (right) label.style.right = 10;
            _gauge.Add(label);
            return label;
        }

        void DrawGauge(MeshGenerationContext ctx)
        {
            if (double.IsNaN(_summitMetres) || double.IsNaN(_baseMetres)) return;
            var p = ctx.painter2D;
            var ink = GaugeColours();
            var o = new Vector2(0, 10);   // the top padding
            Vector2 P(float x, float y) => o + new Vector2(x, y);
            float F(float t) => GaugeY(_baseMetres + (_summitMetres - _baseMetres) * t);
            // the silhouette
            p.BeginPath();
            p.MoveTo(P(24, GaugeBottom));
            p.BezierCurveTo(P(34, F(0.12f)), P(44, F(0.3f)), P(54, F(0.38f)));
            p.BezierCurveTo(P(64, 2 * F(0.38f) - F(0.3f)), P(70, F(0.52f)), P(78, F(0.74f)));
            p.LineTo(P(92, GaugeTop));
            p.LineTo(P(104, F(0.82f)));
            p.BezierCurveTo(P(112, F(0.66f)), P(118, F(0.6f)), P(126, F(0.42f)));
            p.BezierCurveTo(P(134, 2 * F(0.42f) - F(0.6f)), P(138, F(0.08f)), P(144, GaugeBottom));
            p.ClosePath();
            p.fillColor = ink.Fill;
            p.Fill();
            p.strokeColor = ink.Line;
            p.lineWidth = 1.2f;
            p.Stroke();
            // the ruler: a tick every 500 ft (200 m), longer every other one, and its spine
            double step = DisplayUnits.Current == UnitSystem.Imperial ? 500 / 3.28084 : 200;
            p.strokeColor = ink.Label;
            p.lineWidth = 1;
            for (double e = Math.Ceiling(_baseMetres / step) * step; e <= _summitMetres; e += step)
            {
                float y = GaugeY(e);
                p.BeginPath();
                p.MoveTo(P(18, y));
                p.LineTo(P(Math.Round(e / step) % 2 != 0 ? 22 : 26, y));
                p.Stroke();
            }
            p.BeginPath();
            p.MoveTo(P(18, GaugeTop));
            p.LineTo(P(18, GaugeBottom));
            p.Stroke();
            // summit and base, dashed 3-2
            p.strokeColor = ink.Soft;
            foreach (float y in new[] { GaugeY(_summitMetres), GaugeY(_baseMetres) })
                for (float x = 18; x < GaugeW - 10; x += 5)
                {
                    p.BeginPath();
                    p.MoveTo(P(x, y));
                    p.LineTo(P(Mathf.Min(x + 3, GaugeW - 10), y));
                    p.Stroke();
                }
            // the vertical drop: an arrow both ways, and its figure on a plate
            p.strokeColor = ink.Strong;
            p.lineWidth = 1.2f;
            p.BeginPath(); p.MoveTo(P(92, GaugeTop + 4)); p.LineTo(P(92, GaugeBottom - 4)); p.Stroke();
            p.BeginPath(); p.MoveTo(P(89, GaugeTop + 9)); p.LineTo(P(92, GaugeTop + 3)); p.LineTo(P(95, GaugeTop + 9)); p.Stroke();
            p.BeginPath(); p.MoveTo(P(89, GaugeBottom - 9)); p.LineTo(P(92, GaugeBottom - 3)); p.LineTo(P(95, GaugeBottom - 9)); p.Stroke();
            float my = (GaugeTop + GaugeBottom) / 2;
        }

        /// <summary>The theme's chart colours, read from small unseen swatches the stylesheet colours (.gauge__ink--*).</summary>
        (Color Fill, Color Line, Color Label, Color Soft, Color Strong, Color Plate) GaugeColours()
        {
            if (_gaugeInks == null)
            {
                _gaugeInks = new VisualElement[6];
                string[] names = { "fill", "line", "label", "soft", "strong", "plate" };
                for (int i = 0; i < names.Length; i++)
                {
                    var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
                    swatch.AddToClassList("gauge__ink");
                    swatch.AddToClassList("gauge__ink--" + names[i]);
                    _rstats.Add(swatch);
                    _gaugeInks[i] = swatch;
                }
            }
            Color C(int i) => _gaugeInks[i].resolvedStyle.backgroundColor;
            return (C(0), C(1), C(2), C(3), C(4), C(5));
        }

        VisualElement[] _gaugeInks;

        // ---------- notifications (.log) ----------

        const int ToastCount = 3;
        const float ToastSeconds = 3.6f;
        readonly VisualElement[] _toastItems = new VisualElement[ToastCount];
        readonly float[] _toastUntil = new float[ToastCount];

        void WireToasts()
        {
            for (int i = 0; i < ToastCount; i++)
            {
                var item = new VisualElement { pickingMode = PickingMode.Ignore };
                item.AddToClassList("log__item");
                item.AddToClassList("hidden");
                var stripe = new VisualElement { pickingMode = PickingMode.Ignore };
                stripe.AddToClassList("log__stripe");
                item.Add(stripe);
                item.Add(Text("", "log__text"));
                _toasts.Add(item);
                _toastItems[i] = item;
            }
            _root.schedule.Execute(ExpireToasts).Every(200);
        }

        /// <summary>
        /// A short message at the top centre for 3.6 seconds, with a coloured stripe (grey for a routine note); at most
        /// three, the oldest goes first.
        /// </summary>
        public void Toast(string text, Color? stripe = null)
        {
            // The oldest shown, or a free one, moves to the bottom with the new text.
            VisualElement item = null;
            for (int i = 0; i < ToastCount && item == null; i++)
                if (_toastItems[i].ClassListContains("hidden")) item = _toastItems[i];
            if (item == null) item = _toasts[0];
            item.BringToFront();
            item.RemoveFromClassList("hidden");
            item[0].style.backgroundColor = stripe ?? new Color32(0x6f, 0x67, 0x5e, 255);
            ((Label)item[1]).text = text;
            _toastUntil[Array.IndexOf(_toastItems, item)] = Time.unscaledTime + ToastSeconds;
        }

        void ExpireToasts()
        {
            for (int i = 0; i < ToastCount; i++)
                if (!_toastItems[i].ClassListContains("hidden") && Time.unscaledTime > _toastUntil[i]) _toastItems[i].AddToClassList("hidden");
        }

        // ---------- tooltips ----------
        // UI Toolkit shows tooltips only in the editor; the HUD shows its own: the hovered part's tooltip after half a
        // second, under the pointer, kept on the screen.

        VisualElement _tipOwner;
        Vector2 _tipAt;
        IVisualElementScheduledItem _tipShow;

        void WireTooltips()
        {
            _tipShow = _root.schedule.Execute(ShowTip);
            _tipShow.Pause();
            _root.RegisterCallback<PointerMoveEvent>(e =>
            {
                var owner = TipOwner(e.target as VisualElement);
                _tipAt = e.position;
                if (owner == _tipOwner) return;
                _tipOwner = owner;
                _tip.AddToClassList("hidden");
                if (owner != null) _tipShow.ExecuteLater(500);
                else _tipShow.Pause();
            }, TrickleDown.TrickleDown);
            _root.RegisterCallback<PointerLeaveEvent>(_ => HideTip());
            _root.RegisterCallback<PointerDownEvent>(_ => HideTip(), TrickleDown.TrickleDown);
        }

        static VisualElement TipOwner(VisualElement e)
        {
            for (var x = e; x != null; x = x.hierarchy.parent)
                if (!string.IsNullOrEmpty(x.tooltip)) return x;
            return null;
        }

        void ShowTip()
        {
            if (_tipOwner == null || _tipOwner.panel == null || !UiFocus.IsShown(_tipOwner)) return;
            ((Label)_tip).text = _tipOwner.tooltip;
            _tip.RemoveFromClassList("hidden");
            _tip.schedule.Execute(PlaceTip);
        }

        void PlaceTip()
        {
            var screen = _root.worldBound;
            var size = new Vector2(_tip.resolvedStyle.width, _tip.resolvedStyle.height);
            var at = _tipAt - screen.position + new Vector2(12, 18);
            if (at.x + size.x > screen.width - 4) at.x = screen.width - 4 - size.x;
            if (at.y + size.y > screen.height - 4) at.y = _tipAt.y - screen.y - size.y - 8;
            _tip.style.left = Mathf.Max(4, at.x);
            _tip.style.top = Mathf.Max(4, at.y);
        }

        void HideTip()
        {
            _tipOwner = null;
            _tipShow.Pause();
            _tip.AddToClassList("hidden");
        }
    }
}
