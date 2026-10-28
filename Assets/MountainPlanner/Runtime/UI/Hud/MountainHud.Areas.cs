using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>An Area (NW-15, owner NT19) as Analysis › Network lists it and its card shows it; its ID is its hub's.</summary>
    public struct NetworkAreaRow
    {
        public ulong Id;
        public string Name;
        /// <summary>False while it goes by its default name (the card asks for one).</summary>
        public bool Named;
        /// <summary>Each lift with a terminal here and what it does here ("Chair 1 loads"), by lift.</summary>
        public string[] Lifts;
        /// <summary>Trails with an end or a cut in the hub.</summary>
        public int TrailsHere;
        /// <summary>What a skier reaches from it (NetworkReport.ReachFromArea).</summary>
        public int TrailsReached, LiftsReached;
    }

    /// <summary>
    /// Analysis › Network's Areas (task P3-10, NW-15; owner NT19; the approved mockup's Areas table and areaCard): the
    /// hubs with two or more lifts, a row lighting what one reaches and opening its card. The card's name renames it
    /// (a click or F2, as the lift card's does, F6), one undoable RenameArea.
    /// </summary>
    public sealed partial class MountainHud
    {
        AnalysisList _networkAreas;
        VisualElement _areaCard, _areaCardKv;
        Label _areaCardName, _areaCardHint;
        TextField _areaCardField;
        ulong _areaCardId;
        readonly Dictionary<ulong, NetworkAreaRow> _areaRows = new Dictionary<ulong, NetworkAreaRow>();

        /// <summary>An Area's row clicked: the app picks the Area (it lights what the Area reaches and opens its card).</summary>
        public event Action<ulong> NetworkAreaChosen;
        /// <summary>The Area card's name renamed; the app returns why it can't be, or null.</summary>
        public Func<ulong, string, string> AreaRenamed;

        void WireAreas()
        {
            NetworkAreaRow A(AnalysisList.Row r) => (NetworkAreaRow)r.Data;
            _networkAreas = new AnalysisList("areas", new[]
            {
                new AnalysisList.Column { Title = "Area", Grow = 1.9f, Cell = r => A(r).Name },
                new AnalysisList.Column { Title = "Lifts", Numeric = true, Grow = 0.55f, Cell = r => A(r).Lifts.Length.ToString(), SortKey = r => A(r).Lifts.Length },
                new AnalysisList.Column { Title = "Trails here", Numeric = true, Grow = 0.85f, Cell = r => A(r).TrailsHere.ToString(), SortKey = r => A(r).TrailsHere },
                new AnalysisList.Column { Title = "Trails reached", Numeric = true, Grow = 0.9f, Cell = r => A(r).TrailsReached.ToString(), SortKey = r => A(r).TrailsReached },
            }, "network-areas");
            _networkAreas.Root.AddToClassList("an-network__areas");
            _networkAreas.Root.AddToClassList("hidden");
            _network.Add(_networkAreas.Root);
            _networkAreas.Chosen += id => NetworkAreaChosen?.Invoke(id);

            _areaCard = new VisualElement { name = "network-area-card" };
            _areaCard.AddToClassList("mp-win");
            _areaCard.AddToClassList("an-card");
            _areaCard.AddToClassList("an-card--stacked");
            _areaCard.AddToClassList("hidden");
            var head = new VisualElement();
            head.AddToClassList("mp-head");
            _areaCardName = new Label { name = "network-area-card-name", tooltip = "Rename (F2)" };
            _areaCardName.AddToClassList("mp-title");
            _areaCardName.AddToClassList("tw__name--edit");
            _areaCardName.RegisterCallback<ClickEvent>(_ => RenameAreaCard());
            head.Add(_areaCardName);
            _areaCard.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == UnityEngine.KeyCode.F2 && _areaCardField?.parent == null) { RenameAreaCard(); e.StopPropagation(); } });
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            var kind = new Label("Area");
            kind.AddToClassList("an-card__kind");
            head.Add(kind);
            var x = new Button(CloseAreaCard) { name = "network-area-card-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            _areaCard.Add(head);
            _areaCardKv = new VisualElement();
            _areaCardKv.AddToClassList("tw-kv");
            _areaCard.Add(_areaCardKv);
            _areaCardHint = new Label();
            _areaCardHint.AddToClassList("tw-hint");
            _areaCard.Add(_areaCardHint);
            _root.Q("rcol").Add(_areaCard);
        }

        /// <summary>The Areas (rebuilt with the network, never per tick); hidden while there are none.</summary>
        void SetNetworkAreas(IReadOnlyList<NetworkAreaRow> areas)
        {
            if (_networkAreas == null) return;
            _areaRows.Clear();
            var rows = new List<AnalysisList.Row>(areas.Count);
            foreach (var a in areas)
            {
                _areaRows[a.Id] = a;
                rows.Add(new AnalysisList.Row { Id = a.Id, Data = a });
            }
            _networkAreas.SetRows(rows, "");
            _networkAreas.Root.EnableInClassList("hidden", areas.Count == 0);
            if (_areaCardId == 0) return;
            if (_areaRows.ContainsKey(_areaCardId)) FillAreaCard();
            else CloseAreaCard();
        }

        /// <summary>Opens an Area's card (the app's pick, from its row or its shape on the map).</summary>
        public void OpenAreaCardFor(ulong id)
        {
            if (!_areaRows.ContainsKey(id)) return;
            _areaCardId = id;
            _networkAreas.SetFocused(id);
            FillAreaCard();
            _areaCard.RemoveFromClassList("hidden");
            CardsChanged(false);
        }

        void FillAreaCard()
        {
            if (!_areaRows.TryGetValue(_areaCardId, out var a)) return;
            _areaCardName.text = a.Name;
            _areaCardKv.Clear();
            void Kv(string k, string v)
            {
                var row = new VisualElement();
                row.AddToClassList("tw-kv__row");
                var kl = new Label(k);
                kl.AddToClassList("tw-kv__k");
                var vl = new Label(v);
                vl.AddToClassList("tw-kv__v");
                row.Add(kl);
                row.Add(vl);
                _areaCardKv.Add(row);
            }
            Kv("Lifts", string.Join("\n", a.Lifts));
            Kv("Trails here", a.TrailsHere.ToString());
            Kv("Reaches", $"{a.TrailsReached} trail{(a.TrailsReached == 1 ? "" : "s")} · {a.LiftsReached} lift{(a.LiftsReached == 1 ? "" : "s")}");
            _areaCardHint.text = "One stop on the network: every lift and trail here connects." + (a.Named ? "" : " Click the name to name it.");
        }

        // As the lift card's (F6): the name becomes a text field; Enter or a click elsewhere keeps it, Esc puts the old one back.
        void RenameAreaCard()
        {
            if (_areaCardId == 0 || AreaRenamed == null) return;
            if (_areaCardField == null)
            {
                _areaCardField = new TextField { name = "network-area-card-name-field", maxLength = Domain.Design.DesignRules.NameMaxLength };
                _areaCardField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) { EndRenameAreaCard(true); e.StopPropagation(); }
                    else if (e.keyCode == UnityEngine.KeyCode.Escape) { EndRenameAreaCard(false); e.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                _areaCardField.RegisterCallback<FocusOutEvent>(_ => EndRenameAreaCard(true));
            }
            if (_areaCardField.parent != null) return;
            _areaCardField.SetValueWithoutNotify(_areaCardName.text);
            InlineName.Open(_areaCardName, _areaCardField);
            _areaCardField.schedule.Execute(() => { _areaCardField.Focus(); _areaCardField.SelectAll(); });
        }

        void EndRenameAreaCard(bool keep)
        {
            if (_areaCardField?.parent == null) return;
            string typed = _areaCardField.value;
            _areaCardField.RemoveFromHierarchy();
            _areaCardName.RemoveFromClassList("hidden");
            if (!keep || typed == _areaCardName.text || AreaRenamed == null) return;
            string problem = AreaRenamed(_areaCardId, typed);
            if (problem != null) Toast(problem);
            else _areaCardName.text = typed;
        }

        /// <summary>Opens the card's name for typing, or keeps what was typed (tests).</summary>
        public void RenameAreaCardForTest(string name)
        {
            RenameAreaCard();
            if (_areaCardField == null) return;
            _areaCardField.SetValueWithoutNotify(name);
            EndRenameAreaCard(true);
        }

        void CloseAreaCard()
        {
            EndRenameAreaCard(false);
            _areaCardId = 0;
            _networkAreas?.SetFocused(0);
            _areaCard?.AddToClassList("hidden");
            CardsChanged(true);
        }

        /// <summary>Esc or another pick: closes the card (true when it did).</summary>
        public bool CloseAreaCardIfOpen()
        {
            if (_areaCardId == 0) return false;
            CloseAreaCard();
            return true;
        }

        /// <summary>The Area whose card is open (0 for none).</summary>
        public ulong AreaCardId => _areaCardId;
    }
}
