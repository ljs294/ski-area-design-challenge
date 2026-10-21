using System;
using System.Collections.Generic;
using MountainPlanner.UI.Tools;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>One lift as Analysis › Lifts and its card show it (task 09): the app fills these from the design.</summary>
    public struct LiftRow
    {
        public ulong Id;
        public string Name, Model;
        public double LengthMetres, VerticalMetres, Capacity, RideSeconds;
        public int Towers;
        public bool Built;
        /// <summary>F6: the towers' heads ("6 s6, 4 s4, 2 b8"), the lowest chairs over the ground and the rise against the
        /// type's limit (metres; the limit infinite for none), from the built lift's rope; null and NaN for a plan.</summary>
        public string Heads;
        public double LowestChairsMetres, RiseMetres, RiseLimitMetres;
    }

    /// <summary>
    /// Analysis › Lifts (task 09; the approved mockup's listTab and focus card, on P3-08's shared <see cref="AnalysisList"/>):
    /// the lifts with Lift, Model, Length, Vertical, An hour, Wait (the simulation's, "—" until it runs) and Status,
    /// spreadsheet filters, rows that light their lift on the map, and a focus card with Edit and Remove.
    /// </summary>
    public sealed partial class MountainHud
    {
        AnalysisList _liftsList;
        VisualElement _liftCard;
        Label _liftCardName;
        VisualElement _liftCardKv;
        ulong _liftCardId;
        readonly Dictionary<ulong, LiftRow> _liftRows = new Dictionary<ulong, LiftRow>();

        /// <summary>A lift row under the pointer (0 for none), or its card open: the app lights it on the map.</summary>
        public event Action<ulong> LiftFocused;
        /// <summary>The card's Edit or Remove: the app arms that tool on the lift.</summary>
        public event Action<ulong> LiftEditChosen, LiftRemoveChosen;
        /// <summary>F6: the card's name renamed (one undoable RenameLift); the app returns why it can't be, or null.</summary>
        public Func<ulong, string, string> LiftRenamed;
        TextField _liftCardField;

        static readonly double[] LiftLengthBinsFt = { 3000, 6000 }, LiftVerticalBinsFt = { 1000, 2000 };

        void WireLifts()
        {
            LiftRow R(AnalysisList.Row r) => (LiftRow)r.Data;
            _liftsList = new AnalysisList("lifts", new[]
            {
                new AnalysisList.Column { Title = "Lift", Grow = 1.6f, Cell = r => R(r).Name },
                new AnalysisList.Column { Title = "Model", Grow = 1.1f, Cell = r => R(r).Model },
                new AnalysisList.Column { Title = "Length", Numeric = true, Grow = 0.8f, Cell = r => ToolFormat.Length(R(r).LengthMetres),
                    SortKey = r => R(r).LengthMetres, Bin = r => Bin(R(r).LengthMetres, LiftLengthBinsFt), Bins = BinLabels(LiftLengthBinsFt) },
                new AnalysisList.Column { Title = "Vertical", Numeric = true, Grow = 0.8f, Cell = r => ToolFormat.Length(R(r).VerticalMetres),
                    SortKey = r => R(r).VerticalMetres, Bin = r => Bin(R(r).VerticalMetres, LiftVerticalBinsFt), Bins = BinLabels(LiftVerticalBinsFt) },
                new AnalysisList.Column { Title = "An hour", Numeric = true, Grow = 0.7f, Cell = r => $"{R(r).Capacity:#,0}", SortKey = r => R(r).Capacity },
                new AnalysisList.Column { Title = "Wait", Numeric = true, Grow = 0.55f, Cell = r => "—" },
                new AnalysisList.Column { Title = "Status", Grow = 0.8f, Cell = r => R(r).Built ? "Built" : "Planned",
                    Bin = r => R(r).Built ? "built" : "planned", Bins = new[] { ("built", "Built"), ("planned", "Planned") } },
            });
            _liftsList.Root.AddToClassList("hidden");
            _root.Q("analysis-body").Add(_liftsList.Root);
            _liftsList.Hovered += id => LiftFocused?.Invoke(id != 0 ? id : _liftCardId);
            _liftsList.Chosen += OpenLiftCard;

            _liftCard = new VisualElement { name = "lift-card" };
            _liftCard.AddToClassList("mp-win");
            _liftCard.AddToClassList("an-card");
            _liftCard.AddToClassList("hidden");
            var head = new VisualElement();
            head.AddToClassList("mp-head");
            _liftCardName = new Label { name = "lift-card-name", tooltip = "Rename (F2)" };
            _liftCardName.AddToClassList("mp-title");
            _liftCardName.AddToClassList("tw__name--edit");
            _liftCardName.RegisterCallback<ClickEvent>(_ => RenameLiftCard());
            head.Add(_liftCardName);
            _liftCard.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == UnityEngine.KeyCode.F2 && _liftCardField?.parent == null) { RenameLiftCard(); e.StopPropagation(); } });
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            var x = new Button(CloseLiftCard) { name = "lift-card-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            _liftCard.Add(head);
            _liftCardKv = new VisualElement();
            _liftCardKv.AddToClassList("tw-kv");
            _liftCard.Add(_liftCardKv);
            var acts = new VisualElement();
            acts.AddToClassList("tw-btns");
            acts.AddToClassList("an-card__acts");
            var edit = new Button(() => { var id = _liftCardId; CloseLiftCard(); LiftEditChosen?.Invoke(id); }) { name = "lift-card-edit", text = "Edit" };
            edit.AddToClassList("mp-ghost");
            var remove = new Button(() => { var id = _liftCardId; CloseLiftCard(); LiftRemoveChosen?.Invoke(id); }) { name = "lift-card-remove", text = "Remove" };
            remove.AddToClassList("mp-ghost");
            acts.Add(edit);
            acts.Add(remove);
            _liftCard.Add(acts);
            _root.Q("hud").Add(_liftCard);
        }

        /// <summary>The lifts Analysis › Lifts lists (rebuilt when the design changes, never per tick).</summary>
        public void SetLifts(IReadOnlyList<LiftRow> lifts)
        {
            _liftRows.Clear();
            var rows = new List<AnalysisList.Row>(lifts.Count);
            foreach (var l in lifts)
            {
                _liftRows[l.Id] = l;
                rows.Add(new AnalysisList.Row { Id = l.Id, Data = l });
            }
            _liftsList.SetRows(rows, "");
            if (_liftCardId != 0)
            {
                if (_liftRows.ContainsKey(_liftCardId)) FillLiftCard();
                else CloseLiftCard();
            }
            ShowLiftsTab();
        }

        /// <summary>The list shows on the Lifts tab, in place of its "comes in Phase 3" line.</summary>
        void ShowLiftsTab()
        {
            if (_liftsList == null) return;
            bool on = _analysisTab == "lifts";
            _liftsList.Root.EnableInClassList("hidden", !on);
            if (!on) return;
            _analysisEmpty.text = _liftRows.Count == 0 ? "No lifts yet: Toolbox › Lifts › New places one." : "";
            _analysisEmpty.EnableInClassList("hidden", _liftRows.Count > 0);
        }

        void OpenLiftCard(ulong id)
        {
            _liftCardId = id;
            _liftsList.SetFocused(id);
            FillLiftCard();
            _liftCard.RemoveFromClassList("hidden");
            LiftFocused?.Invoke(id);
        }

        void FillLiftCard()
        {
            if (!_liftRows.TryGetValue(_liftCardId, out var l)) return;
            _liftCardName.text = l.Name;
            _liftCardKv.Clear();
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
                _liftCardKv.Add(row);
            }
            Kv("Model", l.Model);
            Kv("Length", ToolFormat.Length(l.LengthMetres));
            Kv("Vertical", ToolFormat.Length(l.VerticalMetres));
            Kv("Capacity", $"{l.Capacity:#,0} an hour");
            if (l.RideSeconds > 0) Kv("Ride", ToolFormat.Duration(l.RideSeconds));
            if (l.Built) Kv("Towers", string.IsNullOrEmpty(l.Heads) ? l.Towers.ToString() : $"{l.Towers} · {l.Heads}");
            if (l.Built && !double.IsNaN(l.LowestChairsMetres)) Kv("Lowest chairs", $"{ToolFormat.Length(l.LowestChairsMetres)} over the ground");
            if (l.Built && !double.IsNaN(l.RiseMetres))
                Kv("Rise", $"{ToolFormat.Length(l.RiseMetres)} of {(double.IsInfinity(l.RiseLimitMetres) ? "no limit" : ToolFormat.Length(l.RiseLimitMetres))}");
            Kv("Status", l.Built ? "Built" : "Planned");
        }

        // F6 (LT39): the card's name becomes a text field; Enter or a click elsewhere keeps it, Esc puts the old one back.
        void RenameLiftCard()
        {
            if (_liftCardId == 0 || LiftRenamed == null) return;
            if (_liftCardField == null)
            {
                _liftCardField = new TextField { name = "lift-card-name-field", maxLength = Domain.Design.DesignRules.NameMaxLength };
                _liftCardField.AddToClassList("tw__namefield");
                _liftCardField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) { EndRenameLiftCard(true); e.StopPropagation(); }
                    else if (e.keyCode == UnityEngine.KeyCode.Escape) { EndRenameLiftCard(false); e.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                _liftCardField.RegisterCallback<FocusOutEvent>(_ => EndRenameLiftCard(true));
            }
            if (_liftCardField.parent != null) return;
            _liftCardField.SetValueWithoutNotify(_liftCardName.text);
            _liftCardName.parent.Insert(_liftCardName.parent.IndexOf(_liftCardName) + 1, _liftCardField);
            _liftCardName.AddToClassList("hidden");
            _liftCardField.schedule.Execute(() => { _liftCardField.Focus(); _liftCardField.SelectAll(); });
        }

        void EndRenameLiftCard(bool keep)
        {
            if (_liftCardField?.parent == null) return;
            string typed = _liftCardField.value;
            _liftCardField.RemoveFromHierarchy();
            _liftCardName.RemoveFromClassList("hidden");
            if (!keep || typed == _liftCardName.text || LiftRenamed == null) return;
            string problem = LiftRenamed(_liftCardId, typed);
            if (problem != null) Toast(problem);
            else _liftCardName.text = typed;
        }

        /// <summary>Opens the card's name for typing, or keeps what was typed (tests).</summary>
        public void RenameLiftCardForTest(string name)
        {
            RenameLiftCard();
            if (_liftCardField == null) return;
            _liftCardField.SetValueWithoutNotify(name);
            EndRenameLiftCard(true);
        }

        void CloseLiftCard()
        {
            EndRenameLiftCard(false);
            _liftCardId = 0;
            _liftsList?.SetFocused(0);
            _liftCard?.AddToClassList("hidden");
            LiftFocused?.Invoke(0);
        }

        /// <summary>Esc: closes the card (true when it did).</summary>
        public bool CloseLiftCardIfOpen()
        {
            if (_liftCardId == 0) return false;
            CloseLiftCard();
            return true;
        }

        /// <summary>The list (tests and captures).</summary>
        internal AnalysisList LiftsList => _liftsList;

        /// <summary>Opens a lift's card (tests and captures).</summary>
        public void OpenLiftCardFor(ulong id) => OpenLiftCard(id);
    }
}
