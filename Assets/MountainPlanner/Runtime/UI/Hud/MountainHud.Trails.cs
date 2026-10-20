using System;
using System.Collections.Generic;
using MountainPlanner.UI.Tools;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>One trail as Analysis › Trails and its card show it (task 08): the app fills these from the design.</summary>
    public struct TrailRow
    {
        public ulong Id;
        public string Name;
        /// <summary>The rating (TD-02), percent; its band gives the symbol (TD-03).</summary>
        public double RatingPercent, MeanPercent, SteepestPercent;
        public double LengthMetres, VerticalMetres;
        public bool Graded, Built;
    }

    /// <summary>
    /// Analysis › Trails (task 08, owner TL8, TL16; the mockup's listTab and focus card): the trails in a list with
    /// Trail, Length, Vertical, Grading and Status, spreadsheet filters, rows that light their trail on the map, and a
    /// focus card (TF-08: name, rating, length, vertical, average and steepest grade) with Edit and Remove.
    /// </summary>
    public sealed partial class MountainHud
    {
        AnalysisList _trailsList;
        VisualElement _trailCard;
        Label _trailCardName;
        VisualElement _trailCardKv;
        ulong _trailCardId;
        readonly Dictionary<ulong, TrailRow> _trailRows = new Dictionary<ulong, TrailRow>();

        /// <summary>A trail row under the pointer (0 for none), or its card open: the app lights it on the map.</summary>
        public event Action<ulong> TrailFocused;
        /// <summary>The card's Edit or Remove: the app arms that tool on the trail.</summary>
        public event Action<ulong> TrailEditChosen, TrailRemoveChosen;

        static readonly double[] LengthBinsFt = { 2000, 5000 }, VerticalBinsFt = { 500, 1500 };
        const double Foot = 0.3048;

        static string Bin(double metres, double[] edgesFt) => metres / Foot < edgesFt[0] ? "s" : metres / Foot < edgesFt[1] ? "m" : "l";

        static (string, string)[] BinLabels(double[] edgesFt) => new[]
        {
            ("s", "Under " + ToolFormat.Length(edgesFt[0] * Foot)),
            ("m", ToolFormat.Length(edgesFt[0] * Foot) + "–" + ToolFormat.Length(edgesFt[1] * Foot)),
            ("l", "Over " + ToolFormat.Length(edgesFt[1] * Foot)),
        };

        static string BandOf(double percent) => double.IsNaN(percent) ? "green" : percent < 25 ? "green" : percent < 40 ? "blue" : percent < 60 ? "black" : "double";

        static string SymbolOf(double percent) => double.IsNaN(percent) ? "" : TrailTool.Symbol(MountainPlanner.Domain.Measure.SlopeBands.Of(percent));

        void WireTrails()
        {
            TrailRow R(AnalysisList.Row r) => (TrailRow)r.Data;
            _trailsList = new AnalysisList("trails", new[]
            {
                new AnalysisList.Column { Title = "Trail", Grow = 1.6f, Cell = r => $"{SymbolOf(R(r).RatingPercent)}  {R(r).Name}",
                    Bin = r => BandOf(R(r).RatingPercent),
                    Bins = new[] { ("green", "● Easiest"), ("blue", "■ More difficult"), ("black", "◆ Most difficult"), ("double", "◆◆ Experts only") } },
                new AnalysisList.Column { Title = "Length", Numeric = true, Grow = 0.85f, Cell = r => ToolFormat.Length(R(r).LengthMetres),
                    SortKey = r => R(r).LengthMetres, Bin = r => Bin(R(r).LengthMetres, LengthBinsFt), Bins = BinLabels(LengthBinsFt) },
                new AnalysisList.Column { Title = "Vertical", Numeric = true, Grow = 0.85f, Cell = r => ToolFormat.Length(R(r).VerticalMetres),
                    SortKey = r => R(r).VerticalMetres, Bin = r => Bin(R(r).VerticalMetres, VerticalBinsFt), Bins = BinLabels(VerticalBinsFt) },
                new AnalysisList.Column { Title = "Grading", Grow = 0.95f, Cell = r => R(r).Graded ? "Graded" : "Natural" },
                new AnalysisList.Column { Title = "Status", Grow = 0.8f, Cell = r => R(r).Built ? "Built" : "Planned",
                    Bin = r => R(r).Built ? "built" : "planned", Bins = new[] { ("built", "Built"), ("planned", "Planned") } },
            });
            _trailsList.Root.AddToClassList("hidden");
            _root.Q("analysis-body").Add(_trailsList.Root);
            _trailsList.Hovered += id => TrailFocused?.Invoke(id != 0 ? id : _trailCardId);
            _trailsList.Chosen += OpenTrailCard;

            // The focus card (the mockup's .pn.fcard), right of the map.
            _trailCard = new VisualElement { name = "trail-card" };
            _trailCard.AddToClassList("mp-win");
            _trailCard.AddToClassList("an-card");
            _trailCard.AddToClassList("hidden");
            var head = new VisualElement();
            head.AddToClassList("mp-head");
            _trailCardName = new Label { name = "trail-card-name" };
            _trailCardName.AddToClassList("mp-title");
            head.Add(_trailCardName);
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            var x = new Button(CloseTrailCard) { name = "trail-card-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            _trailCard.Add(head);
            _trailCardKv = new VisualElement();
            _trailCardKv.AddToClassList("tw-kv");
            _trailCard.Add(_trailCardKv);
            var acts = new VisualElement();
            acts.AddToClassList("tw-btns");
            acts.AddToClassList("an-card__acts");
            var edit = new Button(() => { var id = _trailCardId; CloseTrailCard(); TrailEditChosen?.Invoke(id); }) { name = "trail-card-edit", text = "Edit" };
            edit.AddToClassList("mp-ghost");
            var remove = new Button(() => { var id = _trailCardId; CloseTrailCard(); TrailRemoveChosen?.Invoke(id); }) { name = "trail-card-remove", text = "Remove" };
            remove.AddToClassList("mp-ghost");
            acts.Add(edit);
            acts.Add(remove);
            _trailCard.Add(acts);
            _root.Q("hud").Add(_trailCard);   // in the HUD's layer, as its windows are
        }

        /// <summary>The trails Analysis › Trails lists (rebuilt when the design changes, never per tick).</summary>
        public void SetTrails(IReadOnlyList<TrailRow> trails)
        {
            _trailRows.Clear();
            var rows = new List<AnalysisList.Row>(trails.Count);
            foreach (var t in trails)
            {
                _trailRows[t.Id] = t;
                rows.Add(new AnalysisList.Row { Id = t.Id, Data = t });
            }
            _trailsList.SetRows(rows, "");
            if (_trailCardId != 0)
            {
                if (_trailRows.ContainsKey(_trailCardId)) FillTrailCard();
                else CloseTrailCard();
            }
            ShowTrailsTab();
        }

        /// <summary>The list shows on the Trails tab, in place of its "comes in Phase 3" line.</summary>
        void ShowTrailsTab()
        {
            if (_trailsList == null) return;
            bool on = _analysisTab == "trails";
            _trailsList.Root.EnableInClassList("hidden", !on);
            if (on) _analysisEmpty.text = _trailRows.Count == 0 ? "No trails yet: Toolbox › Trails › New paints one." : "";
            _analysisEmpty.EnableInClassList("hidden", on && _trailRows.Count > 0);
        }

        void OpenTrailCard(ulong id)
        {
            _trailCardId = id;
            _trailsList.SetFocused(id);
            FillTrailCard();
            _trailCard.RemoveFromClassList("hidden");
            TrailFocused?.Invoke(id);
        }

        void FillTrailCard()
        {
            if (!_trailRows.TryGetValue(_trailCardId, out var t)) return;
            _trailCardName.text = $"{SymbolOf(t.RatingPercent)}  {t.Name}";
            _trailCardKv.Clear();
            void Kv(string k, string v)
            {
                var row = new VisualElement();
                row.AddToClassList("tw-kv__row");
                var kl = new Label(k);
                kl.AddToClassList("tw-kv__k");
                var vl = new Label(v);
                vl.AddToClassList("tw-kv__v");
                vl.AddToClassList("mono");
                row.Add(kl);
                row.Add(vl);
                _trailCardKv.Add(row);
            }
            Kv("Rating", double.IsNaN(t.RatingPercent) ? "—" : TrailTool.Rating(t.RatingPercent));
            Kv("Length", ToolFormat.Length(t.LengthMetres));
            Kv("Vertical", ToolFormat.Length(t.VerticalMetres));
            if (!double.IsNaN(t.MeanPercent)) Kv("Average grade", $"{Math.Round(t.MeanPercent):0}%");
            if (!double.IsNaN(t.SteepestPercent)) Kv("Steepest", $"{Math.Round(t.SteepestPercent):0}%");
            Kv("Grading", t.Graded ? "Graded" : "Natural");
            Kv("Status", t.Built ? "Built" : "Planned");
        }

        void CloseTrailCard()
        {
            _trailCardId = 0;
            _trailsList?.SetFocused(0);
            _trailCard?.AddToClassList("hidden");
            TrailFocused?.Invoke(0);
        }

        /// <summary>Whether a trail's card is open (Esc closes it first).</summary>
        public bool TrailCardOpen => _trailCardId != 0;

        /// <summary>Esc: closes the card (true when it did).</summary>
        public bool CloseTrailCardIfOpen()
        {
            if (_trailCardId == 0) return false;
            CloseTrailCard();
            return true;
        }

        /// <summary>The list (tests and captures).</summary>
        internal AnalysisList TrailsList => _trailsList;

        /// <summary>Opens a trail's card (tests and captures).</summary>
        public void OpenTrailCardFor(ulong id) => OpenTrailCard(id);
    }
}
