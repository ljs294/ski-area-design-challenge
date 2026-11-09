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
        /// <summary>TD-07: the sign the player posted, which the list and card show (R3); null until posted.</summary>
        public MountainPlanner.Domain.Measure.SlopeBands.Band? Posted;
        /// <summary>
        /// The game's own rating (TD-02 v2 over the trail's own segments, R4; a plan's TD-06 estimate), percent: shown only
        /// with the dev switch (R6). <see cref="SteepestPercent"/> is the same steepest 150 ft.
        /// </summary>
        public double RatingPercent, MeanPercent, SteepestPercent;
        public double LengthMetres, VerticalMetres;
        public bool Graded, Built;
        /// <summary>F1/F4: whether it's open (a placeholder until 4.07/4.17 open trails: Planned or Closed).</summary>
        public OperatingStatus Status;
        /// <summary>F1: its snow conditions (SN-20 v2, 4.07); null shows "—" until then.</summary>
        public string Conditions;
        /// <summary>F6 (owner 2026-11-08): how popular it is with guests, 0–100; null shows "—" until guests ski (4.12/4.17).</summary>
        public int? Popularity;
        /// <summary>F5: what kind of object it is, under the card's name; null reads "Trail" (the only kind so far).</summary>
        public string Kind;
    }

    /// <summary>
    /// Analysis › Trails (task 08, owner TL8, TL16; the mockup's listTab and focus card): the trails in a list with
    /// Trail, Length, Vertical, Conditions, Popularity and Status (4.21 findings F1, F4, F6: no Grading), spreadsheet filters, rows that
    /// light their trail on the map, and a focus card (TF-08: name with its kind under it, F5; posted sign, length,
    /// vertical, average grade, status) with Edit and Remove. The game's own rating shows only in Edit trail, with the
    /// dev switch (TD-07, R6, F3).
    /// </summary>
    public sealed partial class MountainHud
    {
        AnalysisList _trailsList;
        VisualElement _trailCard;
        Label _trailCardName, _trailCardSymbol, _trailCardKind;
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

        static readonly string[] PostedBins = { "green", "blue", "black", "double" };

        static string BandOf(MountainPlanner.Domain.Measure.SlopeBands.Band? posted) => posted.HasValue ? PostedBins[(int)posted.Value] : "none";

        static string SymbolOf(MountainPlanner.Domain.Measure.SlopeBands.Band? posted) => posted.HasValue ? TrailTool.Symbol(posted.Value) : "";

        void WireTrails()
        {
            TrailRow R(AnalysisList.Row r) => (TrailRow)r.Data;
            _trailsList = new AnalysisList("trails", new[]
            {
                new AnalysisList.Column { Title = "Trail", Grow = 1.6f, Cell = r => $"{SymbolOf(R(r).Posted)}  {R(r).Name}",
                    Bin = r => BandOf(R(r).Posted),
                    Bins = new[] { ("green", "● Easiest"), ("blue", "■ More difficult"), ("black", "◆ Most difficult"), ("double", "◆◆ Experts only"), ("none", "Not posted") } },
                new AnalysisList.Column { Title = "Length", Numeric = true, Grow = 0.85f, Cell = r => ToolFormat.Length(R(r).LengthMetres),
                    SortKey = r => R(r).LengthMetres, Bin = r => Bin(R(r).LengthMetres, LengthBinsFt), Bins = BinLabels(LengthBinsFt) },
                new AnalysisList.Column { Title = "Vertical", Numeric = true, Grow = 0.85f, Cell = r => ToolFormat.Length(R(r).VerticalMetres),
                    SortKey = r => R(r).VerticalMetres, Bin = r => Bin(R(r).VerticalMetres, VerticalBinsFt), Bins = BinLabels(VerticalBinsFt) },
                new AnalysisList.Column { Title = "Conditions", Grow = 0.95f, Class = "an-cell--gap", Cell = r => R(r).Conditions ?? "—" },
                PopularityColumn(r => R(r).Popularity),
                new AnalysisList.Column { Title = "Status", Grow = 0.8f, Class = "an-cell--gap", Cell = r => OperatingStatusWords.Word(TrailStatus(R(r))),
                    Bin = r => TrailStatus(R(r)).ToString(), Bins = OperatingStatusWords.Bins() },
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
            head.AddToClassList("mp-head--kind");
            var (nameLine, kind) = CardTitles(head, "trail-card-kind");
            _trailCardKind = kind;
            // The rating's symbol stands apart from the name, so the name renames in place beside it (sections finding 8).
            _trailCardSymbol = new Label { name = "trail-card-symbol" };
            _trailCardSymbol.AddToClassList("mp-title");
            _trailCardSymbol.style.marginRight = 6;
            nameLine.Add(_trailCardSymbol);
            _trailCardName = new Label { name = "trail-card-name", tooltip = "Rename (F2)" };
            _trailCardName.AddToClassList("mp-title");
            _trailCardName.AddToClassList("tw__name--edit");
            _trailCardName.RegisterCallback<ClickEvent>(_ => RenameTrailCard());   // sections finding 3
            nameLine.Add(_trailCardName);
            _trailCard.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == UnityEngine.KeyCode.F2 && _trailCardField?.parent == null) { RenameTrailCard(); e.StopPropagation(); } });
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

        readonly Dictionary<ulong, OperatingStatus> _trailLive = new Dictionary<ulong, OperatingStatus>();

        OperatingStatus TrailStatus(TrailRow r) => r.Built && _trailLive.TryGetValue(r.Id, out var v) ? v : r.Status;

        /// <summary>
        /// A built trail's word from the simulation (task 4.11's stand-in for SN-20 until 4.07: Open while an open lift's
        /// descent runs along it in the resort's hours). The app calls it at a bounded rate; <see cref="FlushLive"/> redraws.
        /// </summary>
        public void SetTrailLive(ulong id, OperatingStatus status)
        {
            if (_trailLive.TryGetValue(id, out var was) && was == status) return;
            _trailLive[id] = status;
            _liveDirty = true;
        }

        /// <summary>Forgets the simulation's words (a new mountain, or the network not yet known): the placeholders show.</summary>
        public void ClearLive()
        {
            if (_trailLive.Count == 0 && _liftLive.Count == 0) return;
            _trailLive.Clear();
            _liftLive.Clear();
            _liveDirty = true;
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
            CardsChanged(false);   // task P3-10, NT12
        }

        /// <summary>The card's name renamed (one undoable step); the app returns why it can't be, or null.</summary>
        public Func<ulong, string, string> TrailRenamed;
        TextField _trailCardField;

        // Sections finding 3: the card's name becomes a text field in its place (finding 8), its symbol kept beside it; Enter
        // or a click elsewhere keeps it, Esc puts the old one back.
        void RenameTrailCard()
        {
            if (_trailCardId == 0 || TrailRenamed == null || !_trailRows.TryGetValue(_trailCardId, out var row)) return;
            if (_trailCardField == null)
            {
                _trailCardField = new TextField { name = "trail-card-name-field", maxLength = Domain.Design.DesignRules.NameMaxLength };
                _trailCardField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) { EndRenameTrailCard(true); e.StopPropagation(); }
                    else if (e.keyCode == UnityEngine.KeyCode.Escape) { EndRenameTrailCard(false); e.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                _trailCardField.RegisterCallback<FocusOutEvent>(_ => EndRenameTrailCard(true));
            }
            if (_trailCardField.parent != null) return;
            _trailCardField.SetValueWithoutNotify(row.Name);
            InlineName.Open(_trailCardName, _trailCardField);
            _trailCardField.schedule.Execute(() => { _trailCardField.Focus(); _trailCardField.SelectAll(); });
        }

        void EndRenameTrailCard(bool keep)
        {
            if (_trailCardField?.parent == null) return;
            string typed = _trailCardField.value;
            _trailCardField.RemoveFromHierarchy();
            _trailCardName.RemoveFromClassList("hidden");
            if (!keep || TrailRenamed == null || !_trailRows.TryGetValue(_trailCardId, out var row) || typed.Trim() == row.Name) return;
            string problem = TrailRenamed(_trailCardId, typed.Trim());
            if (problem != null) Toast(problem);
        }

        /// <summary>Opens the card's name for typing, or puts it back (captures).</summary>
        public void RenameTrailCardForCapture(bool open) { if (open) RenameTrailCard(); else EndRenameTrailCard(false); }
        /// <summary>Closes the trail card (captures).</summary>
        public void CloseTrailCardForCapture() => CloseTrailCard();

        void FillTrailCard()
        {
            if (!_trailRows.TryGetValue(_trailCardId, out var t)) return;
            _trailCardSymbol.text = SymbolOf(t.Posted);
            _trailCardSymbol.EnableInClassList("hidden", _trailCardSymbol.text.Length == 0);
            _trailCardName.text = t.Name;
            _trailCardKind.text = t.Kind ?? "Trail";   // F5
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
            Kv("Posted", TrailTool.Posted(t.Posted));   // TD-07: read only; Edit trail changes it (R2) and shows the game's (F3)
            Kv("Length", ToolFormat.Length(t.LengthMetres));
            Kv("Vertical", ToolFormat.Length(t.VerticalMetres));
            if (!double.IsNaN(t.MeanPercent)) Kv("Average grade", $"{Math.Round(t.MeanPercent):0}%");
            Kv("Status", OperatingStatusWords.Word(TrailStatus(t)));   // F4: no Grading row; 4.11's stand-in until 4.07
        }

        void CloseTrailCard()
        {
            _trailCardId = 0;
            _trailsList?.SetFocused(0);
            _trailCard?.AddToClassList("hidden");
            TrailFocused?.Invoke(0);
            CardsChanged(true);   // task P3-10, NT11/NT12
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

        /// <summary>
        /// F6 (owner 2026-11-08): a list's Popularity column for trails and lifts, a whole number 0–100 from the guests,
        /// sortable and filterable by thirds; "—" (sorted last) until the simulation gives one.
        /// </summary>
        static AnalysisList.Column PopularityColumn(Func<AnalysisList.Row, int?> of) => new AnalysisList.Column
        {
            Title = "Popularity", Numeric = true, Grow = 0.85f,
            Cell = r => of(r) is int p ? p.ToString() : "—",
            SortKey = r => of(r) ?? -1,
            Bin = r => of(r) is int p ? (p >= 67 ? "high" : p >= 34 ? "mid" : "low") : "none",
            Bins = new[] { ("high", "67–100"), ("mid", "34–66"), ("low", "0–33"), ("none", "Not yet") },
        };

        /// <summary>
        /// F5: a card head's titles, the name line over a small line saying what kind of object it is ("Trail", "Detachable
        /// Six Pack"). Returns the name line (the card adds its symbol and name) and the kind label.
        /// </summary>
        static (VisualElement nameLine, Label kind) CardTitles(VisualElement head, string kindName)
        {
            var titles = new VisualElement();
            titles.AddToClassList("mp-head__titles");
            var line = new VisualElement();
            line.AddToClassList("mp-head__line");
            titles.Add(line);
            var kind = new Label { name = kindName };
            kind.AddToClassList("mp-head__kind");
            titles.Add(kind);
            head.Add(titles);
            return (line, kind);
        }

        /// <summary>The list (tests and captures).</summary>
        internal AnalysisList TrailsList => _trailsList;

        /// <summary>Opens a trail's card (tests and captures).</summary>
        public void OpenTrailCardFor(ulong id) => OpenTrailCard(id);

        /// <summary>Opens the last listed trail's card (captures); false when the list is empty.</summary>
        public bool OpenLastTrailCardForCapture()
        {
            ulong last = 0;
            foreach (var id in _trailRows.Keys) last = id;
            if (last != 0) OpenTrailCard(last);
            return last != 0;
        }
    }
}
