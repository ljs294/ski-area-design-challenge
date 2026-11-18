using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>What a water card shows (task 4.14, LK-10, owner finding F1): filled by the app from the design and the area's water.</summary>
    public struct WaterCardModel
    {
        /// <summary>What the card is for ("lake:…", "stream:…", "pond:…", "dam:…"): the app's key, given back with every action.</summary>
        public string Key;
        /// <summary>"Lake", "Stream", "Pond" or "Reservoir".</summary>
        public string Kind;
        public string Name;
        /// <summary>Whether a click or F2 on the name renames it.</summary>
        public bool Renamable;
        public (string K, string V)[] Rows;
        /// <summary>The card's buttons: an id given back on a click, its text, and whether it shows as on (a switch).</summary>
        public (string Id, string Label, bool On)[] Actions;
        public string Hint;
    }

    /// <summary>
    /// The water card (task 4.14, LK-10; MapLibre's lake and stream panels, re-specified): a lake, stream, pond or dam picked
    /// on the map with no tool armed. Laid out as the Area card: the name (a click or F2 renames it, one undoable command),
    /// the kind, ✕; the facts; its buttons; a hint. The app owns what it says and what each button does.
    /// </summary>
    public sealed partial class MountainHud
    {
        VisualElement _waterCard, _waterCardKv, _waterCardActs;
        Label _waterCardName, _waterCardKind, _waterCardHint;
        TextField _waterCardField;
        string _waterCardKey;
        bool _waterCardRenamable;

        /// <summary>The name renamed: the app returns why it can't be, or null (it refills the card).</summary>
        public Func<string, string, string> WaterRenamed;
        /// <summary>A card button: the card's key and the button's id.</summary>
        public event Action<string, string> WaterCardAction;
        /// <summary>The card closed (✕, Esc or another card).</summary>
        public event Action WaterCardClosed;

        void BuildWaterCard()
        {
            if (_waterCard != null) return;
            _waterCard = new VisualElement { name = "water-card" };
            _waterCard.AddToClassList("mp-win");
            _waterCard.AddToClassList("an-card");
            _waterCard.AddToClassList("an-card--stacked");
            _waterCard.AddToClassList("hidden");
            var head = new VisualElement();
            head.AddToClassList("mp-head");
            _waterCardName = new Label { name = "water-card-name" };
            _waterCardName.AddToClassList("mp-title");
            _waterCardName.RegisterCallback<ClickEvent>(_ => RenameWaterCard());
            head.Add(_waterCardName);
            _waterCard.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == UnityEngine.KeyCode.F2 && _waterCardField?.parent == null) { RenameWaterCard(); e.StopPropagation(); } });
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            _waterCardKind = new Label();
            _waterCardKind.AddToClassList("an-card__kind");
            head.Add(_waterCardKind);
            var x = new Button(CloseWaterCard) { name = "water-card-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            _waterCard.Add(head);
            _waterCardKv = new VisualElement();
            _waterCardKv.AddToClassList("tw-kv");
            _waterCard.Add(_waterCardKv);
            _waterCardActs = new VisualElement { name = "water-card-actions" };
            _waterCardActs.AddToClassList("tw-btns");
            _waterCardActs.AddToClassList("water-card__btns");
            _waterCard.Add(_waterCardActs);
            _waterCardHint = new Label();
            _waterCardHint.AddToClassList("tw-hint");
            _waterCard.Add(_waterCardHint);
            _root.Q("rcol").Add(_waterCard);
        }

        /// <summary>Opens a water card, or refills the open one; any other card closes first.</summary>
        public void ShowWaterCard(in WaterCardModel m)
        {
            BuildWaterCard();
            if (_waterCardKey == null)
            {
                CloseLiftCardIfOpen();
                CloseTrailCardIfOpen();
                CloseAreaCardIfOpen();
                CloseSkierCardIfOpen();
            }
            if (_waterCardKey != m.Key) EndRenameWaterCard(false);
            _waterCardKey = m.Key;
            _waterCardRenamable = m.Renamable;
            if (_waterCardField?.parent == null) _waterCardName.text = m.Name ?? "";
            _waterCardName.tooltip = m.Renamable ? "Rename (F2)" : "";
            _waterCardName.EnableInClassList("tw__name--edit", m.Renamable);
            _waterCardKind.text = m.Kind ?? "";
            _waterCardKv.Clear();
            foreach (var (k, v) in m.Rows ?? Array.Empty<(string, string)>())
            {
                var row = new VisualElement();
                row.AddToClassList("tw-kv__row");
                var kl = new Label(k);
                kl.AddToClassList("tw-kv__k");
                var vl = new Label(v);
                vl.AddToClassList("tw-kv__v");
                row.Add(kl);
                row.Add(vl);
                _waterCardKv.Add(row);
            }
            _waterCardActs.Clear();
            string key = m.Key;
            foreach (var (id, label, on) in m.Actions ?? Array.Empty<(string, string, bool)>())
            {
                string act = id;
                var b = new Button(() => WaterCardAction?.Invoke(key, act)) { name = "water-card-" + id, text = label };
                b.AddToClassList("mp-ghost");
                b.EnableInClassList("mp-ghost--on", on);
                _waterCardActs.Add(b);
            }
            _waterCardActs.EnableInClassList("hidden", _waterCardActs.childCount == 0);
            _waterCardHint.text = m.Hint ?? "";
            _waterCardHint.EnableInClassList("hidden", string.IsNullOrEmpty(m.Hint));
            _waterCard.RemoveFromClassList("hidden");
            CardsChanged(false);
        }

        // As the Area card's: the name becomes a text field; Enter or a click elsewhere keeps it, Esc puts the old one back.
        void RenameWaterCard()
        {
            if (_waterCardKey == null || !_waterCardRenamable || WaterRenamed == null) return;
            if (_waterCardField == null)
            {
                _waterCardField = new TextField { name = "water-card-name-field", maxLength = Domain.Design.DesignRules.NameMaxLength };
                _waterCardField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) { EndRenameWaterCard(true); e.StopPropagation(); }
                    else if (e.keyCode == UnityEngine.KeyCode.Escape) { EndRenameWaterCard(false); e.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                _waterCardField.RegisterCallback<FocusOutEvent>(_ => EndRenameWaterCard(true));
            }
            if (_waterCardField.parent != null) return;
            _waterCardField.SetValueWithoutNotify(_waterCardName.text);
            InlineName.Open(_waterCardName, _waterCardField);
            _waterCardField.schedule.Execute(() => { _waterCardField.Focus(); _waterCardField.SelectAll(); });
        }

        void EndRenameWaterCard(bool keep)
        {
            if (_waterCardField?.parent == null) return;
            string typed = _waterCardField.value;
            _waterCardField.RemoveFromHierarchy();
            _waterCardName.RemoveFromClassList("hidden");
            if (!keep || typed == _waterCardName.text || WaterRenamed == null || _waterCardKey == null) return;
            string problem = WaterRenamed(_waterCardKey, typed);
            if (problem != null) Toast(problem);
        }

        /// <summary>Opens the card's name for typing and keeps what was typed (tests).</summary>
        public void RenameWaterCardForTest(string name)
        {
            RenameWaterCard();
            if (_waterCardField == null) return;
            _waterCardField.SetValueWithoutNotify(name);
            EndRenameWaterCard(true);
        }

        /// <summary>Presses a card button by its id (tests).</summary>
        public void PressWaterCardForTest(string id)
        {
            if (_waterCardKey != null) WaterCardAction?.Invoke(_waterCardKey, id);
        }

        void CloseWaterCard()
        {
            if (_waterCardKey == null) return;
            EndRenameWaterCard(false);
            _waterCardKey = null;
            _waterCard?.AddToClassList("hidden");
            CardsChanged(true);
            WaterCardClosed?.Invoke();
        }

        /// <summary>Esc or another pick: closes the card (true when it did).</summary>
        public bool CloseWaterCardIfOpen()
        {
            if (_waterCardKey == null) return false;
            CloseWaterCard();
            return true;
        }

        /// <summary>The open water card's key, or null.</summary>
        public string WaterCardKey => _waterCardKey;

        /// <summary>The open card's shown name, facts and buttons (tests).</summary>
        public string WaterCardTextForTest()
        {
            if (_waterCard == null || _waterCardKey == null) return null;
            var parts = new List<string> { _waterCardName.text, _waterCardKind.text };
            foreach (var row in _waterCardKv.Children())
                foreach (var l in row.Children()) if (l is Label t) parts.Add(t.text);
            foreach (var b in _waterCardActs.Children()) if (b is Button bt) parts.Add("[" + bt.text + "]");
            parts.Add(_waterCardHint.text);
            return string.Join(" | ", parts);
        }
    }
}
