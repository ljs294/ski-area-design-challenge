using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Hud;
using MountainPlanner.UI.Tools;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Task 4.25 PR B: Analysis › Grooming (the approved mockup's groomingTab, round 5 Trails | Equipment and round 6's
    /// Skogkatt 400 / 400 W / 800 / 800 W). Trails: the night strip with Tonight | Last night, the needs-you rows first
    /// (Show me, a fix, ✕), then the trails with their setting and priority as inline menus. Equipment: the groomers with a
    /// crew check per shift (a click pins that shift's count; "pinned ✕" hands it back to Autopilot), the crews and cost
    /// per shift, and the catalogue; a groomer or a model opens its card on the right, one card at a time. The app gives a
    /// <see cref="GroomingTabModel"/> at the live rate; the tab redraws only when it moves.
    /// </summary>
    public sealed partial class MountainHud
    {
        VisualElement _groom, _groomTrailsPage, _groomEquipPage, _groomSubLine, _groomStripHead, _groomKey, _groomFirstBuy, _groomTools, _groomTable;
        VisualElement _groomFleetPlate, _groomBuyPlate, _groomTabDot;
        Label _groomLead, _groomHeader, _groomHeaderShort, _groomToolsHint;
        // GO-14 v3: the budget line over both sub-tabs
        VisualElement _groomBudget, _groomBudgetFill;
        Label _groomBudgetText, _groomBudgetRest, _groomBudgetCost;
        GroomStrip _groomStrip;
        VisualElement _groomCard;
        GroomingTabModel _groomModel;
        bool _groomDirty, _groomEquip, _groomLast, _groomView, _groomSellAsk, _groomBuyOpen;
        GroomFilter _groomFilter = GroomFilter.Tonight;
        readonly HashSet<string> _groomDismissed = new HashSet<string>();
        readonly List<ToolDropdown> _groomDropdowns = new List<ToolDropdown>();
        int _groomCardNumber;
        GroomerModel _groomBuyModel;
        GroomerDrive _groomBuyDrive = GroomerDrive.Diesel;

        /// <summary>A grooming row or strip block under the pointer (its trail's row id, 0 none): the app lights it.</summary>
        public event Action<ulong> GroomingHovered;
        /// <summary>A needs-you row's Show me: the app frames that trail.</summary>
        public event Action<ulong> GroomingShowMe;
        /// <summary>The tab showing on an open Analysis, or not: the map takes tonight's colours while it does.</summary>
        public event Action<bool> GroomingViewChanged;
        /// <summary>Tonight | Last night switched or a row dismissed: the app builds the model again at once.</summary>
        public event Action GroomingRefreshWanted;
        /// <summary>A trail's setting or priority (one undoable SetTrailGrooming); null or why not.</summary>
        public Func<ulong, GroomSetting?, GroomPriority?, string> GroomTrailChosen;
        /// <summary>Tonight's order for a trail (Till, Push, Skip): straight to the worker; null or why not.</summary>
        public Func<ulong, GroomOrder, string> GroomOrderChosen;
        /// <summary>Buy, sell, fit the tiller, pin a shift's crews (a count, or −1 for Autopilot): undoable; null or why not.</summary>
        public Func<GroomerModel, GroomerDrive, string> GroomerBuyChosen;
        public Func<int, string> GroomerSellChosen;
        public Func<int, bool, string> GroomerTillerChosen;
        public Func<int, int, string> GroomCrewsChosen;
        /// <summary>A trail's grooming as the design has it now (the trail card's row), or null to hide the row.</summary>
        public Func<ulong, (GroomSetting Setting, GroomPriority Priority)?> TrailGroomingOf;

        /// <summary>Whether the strip shows last night (the app builds the model for it).</summary>
        public bool GroomingLastNight => _groomLast;
        /// <summary>The needs-you rows dismissed this session (their keys).</summary>
        public ICollection<string> GroomingDismissed => _groomDismissed;
        /// <summary>Round 3 (owner 2026-11-21): the groomer whose card is open (0 none): the map shows its path tonight.</summary>
        public int GroomerPicked => _groomCardNumber;

        /// <summary>The model last given (tests and captures).</summary>
        public GroomingTabModel GroomingModel => _groomModel;

        // The map's colours while the tab is open (the key's swatches; the app draws the trails in them).
        public static readonly Color GroomMapPlanned = new Color(0.874f, 0.910f, 0.933f), GroomMapNeeds = new Color(0.941f, 0.706f, 0.235f),
                                     GroomMapBad = new Color(0.831f, 0.318f, 0.247f), GroomMapIdle = new Color(0.427f, 0.451f, 0.471f),
                                     GroomMapNever = new Color(0.29f, 0.267f, 0.243f),
                                     // a picked groomer's path: the runs it grooms tonight, and its drives between them
                                     GroomMapPath = new Color(0.373f, 0.749f, 0.416f), GroomMapDrive = new Color(0.62f, 0.80f, 0.96f);

        /// <summary>A row's colour on the map: what needs you, not reached, groomed (or planned), nothing, Never.</summary>
        public static Color GroomMapColour(GroomingTabModel.Row r)
        {
            if (r == null) return GroomMapIdle;
            var sev = r.Need > 0 ? r.NeedSeverity : r.WhatSeverity;
            if (sev == GroomSeverity.Bad) return GroomMapBad;
            if (sev == GroomSeverity.Warn) return GroomMapNeeds;
            if (r.Never) return GroomMapNever;
            return sev == GroomSeverity.None && (r.Tonight || r.What.StartsWith("Groomed", StringComparison.Ordinal)) ? GroomMapPlanned : GroomMapIdle;
        }

        void WireGrooming()
        {
            _groom = new VisualElement { name = "grooming" };
            _groom.AddToClassList("gr");
            _groom.AddToClassList("hidden");
            _root.Q("analysis-body").Add(_groom);

            _groomSubLine = new VisualElement { name = "grooming-subtabs" };
            _groomSubLine.AddToClassList("gr-sw");
            _groom.Add(_groomSubLine);
            _groomLead = new Label { name = "grooming-lead" };
            _groomLead.AddToClassList("gr-lead");
            _groomBudget = new VisualElement { name = "grooming-budget" };
            _groomBudget.AddToClassList("gr-bud");
            var budLine = new VisualElement();
            budLine.AddToClassList("gr-bud__line");
            _groomBudgetText = new Label { name = "grooming-budget-text" };
            _groomBudgetText.AddToClassList("gr-bud__t");
            _groomBudgetRest = new Label { name = "grooming-budget-rest" };
            _groomBudgetRest.AddToClassList("gr-bud__t");
            _groomBudgetCost = new Label { name = "grooming-budget-cost" };
            _groomBudgetCost.AddToClassList("gr-bud__c");
            budLine.Add(_groomBudgetText); budLine.Add(_groomBudgetRest); budLine.Add(_groomBudgetCost);
            _groomBudget.Add(budLine);
            var track = new VisualElement();
            track.AddToClassList("gr-bud__track");
            _groomBudgetFill = new VisualElement { name = "grooming-budget-fill" };
            _groomBudgetFill.AddToClassList("gr-bud__fill");
            track.Add(_groomBudgetFill);
            _groomBudget.Add(track);
            _groom.Add(_groomBudget);

            // ---------- Trails ----------
            _groomTrailsPage = new VisualElement { name = "grooming-trails-page" };
            _groom.Add(_groomTrailsPage);
            var plate = new VisualElement { name = "grooming-strip" };
            plate.AddToClassList("gr-plate");
            _groomTrailsPage.Add(plate);
            _groomStripHead = new VisualElement();
            _groomStripHead.AddToClassList("gr-hd");
            plate.Add(_groomStripHead);
            _groomHeader = new Label { name = "grooming-header" };
            _groomHeader.AddToClassList("gr-hd__text");
            _groomHeaderShort = new Label { name = "grooming-header-short" };
            _groomHeaderShort.AddToClassList("gr-hd__text");
            _groomHeaderShort.AddToClassList("gr-warn");
            _groomStrip = new GroomStrip();
            _groomStrip.BlockHovered += id => GroomingHovered?.Invoke(id);
            _groomStrip.LaneClicked += n => { SetGroomEquip(true); OpenGroomerCard(n); };
            plate.Add(_groomStrip);
            _groomKey = new VisualElement { name = "grooming-key" };
            _groomKey.AddToClassList("gr-key");
            KeyItem("Push", "gr-key__sw");
            KeyItem("Till", "gr-key__sw--till");
            KeyItem("Needs you", "gr-key__sw--warn");
            KeyItem("Not reached", "gr-key__sw--bad");
            KeyItem("Transit", "gr-key__sw--transit");
            var keyHint = new Label("Hover a block for its trail; click a groomer for its card");
            keyHint.AddToClassList("gr-key__r");
            _groomKey.Add(keyHint);
            plate.Add(_groomKey);
            _groomFirstBuy = new VisualElement { name = "grooming-first-buy" };
            _groomFirstBuy.AddToClassList("tw-acts");
            var go = new Button(() => OpenBuyCard(GroomerModel.S400)) { name = "grooming-first-buy-go" };   // to Equipment, buying begun
            go.AddToClassList("mp-go");
            go.Add(GoLabel("Buy your first groomer"));
            go.Add(GoLabel($"from {GroomingTabModel.Usd(GroomingTabModel.PriceUsd(GroomerModel.S400, GroomerDrive.Diesel))}"));
            _groomFirstBuy.Add(go);
            plate.Add(_groomFirstBuy);

            _groomTools = new VisualElement { name = "grooming-tools" };
            _groomTools.AddToClassList("an-tools");
            _groomTrailsPage.Add(_groomTools);
            _groomToolsHint = new Label();
            _groomToolsHint.AddToClassList("gr-tools__hint");
            _groomTable = new VisualElement { name = "grooming-trails" };
            _groomTable.AddToClassList("gr-tbl");
            _groomTrailsPage.Add(_groomTable);

            // ---------- Equipment ----------
            _groomEquipPage = new VisualElement { name = "grooming-equipment" };
            _groomEquipPage.AddToClassList("hidden");
            _groom.Add(_groomEquipPage);
            _groomFleetPlate = new VisualElement { name = "grooming-fleet" };
            _groomFleetPlate.AddToClassList("gr-plate");
            _groomEquipPage.Add(_groomFleetPlate);
            _groomBuyPlate = new VisualElement { name = "grooming-buy" };
            _groomBuyPlate.AddToClassList("gr-plate");
            _groomEquipPage.Add(_groomBuyPlate);

            // ---------- the cards, right of the map, as the trail card ----------
            _groomCard = new VisualElement { name = "groomer-card" };
            _groomCard.AddToClassList("mp-win");
            _groomCard.AddToClassList("an-card");
            _groomCard.AddToClassList("hidden");
            _root.Q("hud").Add(_groomCard);

            // The tab's amber dot while the fleet is empty.
            var tab = _root.Q<Button>("analysis-tab-grooming");
            if (tab != null)
            {
                _groomTabDot = new VisualElement { name = "analysis-tab-grooming-dot", pickingMode = PickingMode.Ignore };
                _groomTabDot.AddToClassList("gr-alert");
                _groomTabDot.AddToClassList("hidden");
                // beside the tab, not in it: a Button with a child is no longer measured by its words
                tab.parent.Add(_groomTabDot);
                tab.RegisterCallback<GeometryChangedEvent>(_ =>
                {
                    _groomTabDot.style.left = tab.layout.xMax - 8;
                    _groomTabDot.style.top = tab.layout.y + 3;
                });
            }
            FillGrooming();
        }

        void KeyItem(string word, string cls)
        {
            var item = new VisualElement();
            item.AddToClassList("gr-key__item");
            var sw = new VisualElement();
            sw.AddToClassList("gr-key__sw");
            if (cls != "gr-key__sw") sw.AddToClassList(cls);
            item.Add(sw);
            var l = new Label(word);
            l.AddToClassList("gr-key__word");
            item.Add(l);
            _groomKey.Add(item);
        }

        static Label GoLabel(string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-go__label");
            return l;
        }

        /// <summary>The newest grooming (the app calls it at the live rate; the tab redraws only when it differs).</summary>
        public void SetGrooming(GroomingTabModel m)
        {
            if (ReferenceEquals(m, _groomModel)) return;
            _groomModel = m;
            _groomDirty = true;
            _groomTabDot?.EnableInClassList("hidden", m == null || !m.Empty);
            FlushGrooming();
        }

        void FlushGrooming()
        {
            if (!_groomDirty || _groom == null) return;
            foreach (var d in _groomDropdowns) if (d.Open) return;   // a menu open: after it closes
            if (_trailCardGroomMenus.Exists(d => d.Open)) return;
            _groomDirty = false;
            if (_analysisTab == "grooming") FillGrooming();
            if (_groomCardNumber != 0) FillGroomerCard();
            if (_trailCardId != 0) FillTrailCard();
        }

        /// <summary>The tab shows on Analysis › Grooming; the map's colours follow it.</summary>
        void ShowGroomingTab()
        {
            if (_groom == null) return;
            bool on = _analysisTab == "grooming";
            _groom.EnableInClassList("hidden", !on);
            if (on)
            {
                _analysisEmpty.text = "";
                _analysisEmpty.EnableInClassList("hidden", true);
                FillGrooming();
            }
            bool view = on && _analysisOpen;
            if (view == _groomView) return;
            _groomView = view;
            if (view) _networkKeyClosed = false;
            ShowNetworkKey();
            GroomingViewChanged?.Invoke(view);
        }

        void SetGroomEquip(bool equip)
        {
            if (equip == _groomEquip) return;
            _groomEquip = equip;
            FillGrooming();
        }

        // ---------- filling ----------

        void FillGrooming()
        {
            if (_groom == null) return;
            var m = _groomModel ?? GroomingTabModel.Build(null, 0, null, null, GroomCrews.AllAuto, null, _groomLast, 0);
            foreach (var d in _groomDropdowns) d.Close();
            _groomDropdowns.Clear();
            _groomSubLine.Clear();
            var sub = Seg("grooming-tabs", ("Trails", !_groomEquip, null, () => SetGroomEquip(false)), ("Equipment", _groomEquip, null, () => SetGroomEquip(true)));
            sub.RemoveFromClassList("lift-sched__seg");
            sub.AddToClassList("gr-seg");
            _groomSubLine.Add(sub);
            _groomLead.text = _groomEquip ? m.EquipLead : m.Lead;
            _groomLead.EnableInClassList("gr-warn", !_groomEquip && m.LeadWarn);
            _groomSubLine.Add(_groomLead);
            // GO-14 v3: the plan's hours against the crews', updated the moment a trail is picked
            _groomBudget.EnableInClassList("hidden", !m.BudgetShown);
            _groomBudgetText.text = m.Budget;
            _groomBudgetRest.text = m.BudgetRest.Length > 0 ? " · " + m.BudgetRest : "";
            _groomBudgetRest.EnableInClassList("gr-warn", m.BudgetWarn);
            _groomBudgetCost.text = m.BudgetCost;
            _groomBudgetCost.tooltip = m.CostTip;
            _groomBudget.tooltip = m.BudgetTip;
            _groomBudgetFill.style.width = UnityEngine.UIElements.Length.Percent(100f * m.BudgetFill);
            _groomBudgetFill.EnableInClassList("gr-bud__fill--warn", m.BudgetWarn);
            _groomTrailsPage.EnableInClassList("hidden", _groomEquip);
            _groomEquipPage.EnableInClassList("hidden", !_groomEquip);
            if (_groomEquip) FillGroomEquip(m);
            else FillGroomTrails(m);
        }

        void FillGroomTrails(GroomingTabModel m)
        {
            _groomStripHead.Clear();
            var view = Seg("grooming-view", ("Tonight", !_groomLast, null, () => SetGroomLast(false)), ("Last night", _groomLast, null, () => SetGroomLast(true)));
            view.RemoveFromClassList("lift-sched__seg");
            view.AddToClassList("gr-seg");
            view.AddToClassList("gr-seg--mini");
            _groomStripHead.Add(view);
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            _groomStripHead.Add(gap);
            _groomHeader.text = m.Header;
            _groomHeader.tooltip = m.CostTip;
            _groomStripHead.Add(_groomHeader);
            _groomHeaderShort.text = m.HeaderShort.Length > 0 ? " · " + m.HeaderShort : "";
            _groomHeaderShort.EnableInClassList("hidden", m.HeaderShort.Length == 0);
            _groomStripHead.Add(_groomHeaderShort);
            _groomStrip.Picked = _groomCardNumber;
            _groomStrip.Set(m);
            _groomKey.EnableInClassList("hidden", m.Empty);
            _groomFirstBuy.EnableInClassList("hidden", !m.Empty);

            // The filters and the table.
            _groomTools.Clear();
            if (m.Empty && m.Rows.Count == 0) { _groomTable.Clear(); return; }
            (string, bool, string, Action) F(GroomFilter f, string word) => ($"{word}  {m.CountFor(f)}", _groomFilter == f, null, () => { _groomFilter = f; FillGrooming(); });
            var filt = Seg("grooming-filter", F(GroomFilter.Tonight, _groomLast ? "Last night" : "Tonight"), F(GroomFilter.Needs, "Needs you"), F(GroomFilter.All, "All"), F(GroomFilter.Never, "Never"));
            filt.RemoveFromClassList("lift-sched__seg");
            filt.AddToClassList("gr-seg");
            filt.AddToClassList("gr-seg--mini");
            _groomTools.Add(filt);
            _groomToolsHint.text = _groomFilter == GroomFilter.Needs ? "Worst first" : _groomFilter == GroomFilter.Never ? "Glades and zones" : "Needs you first, then in order of work";
            _groomTools.Add(_groomToolsHint);

            _groomTable.Clear();
            if (_groomFilter == GroomFilter.Needs)
            {
                if (m.Needs.Count == 0) { _groomTable.Add(Cell("Nothing needs you " + (_groomLast ? "from last night." : "tonight."), "gr-cell--lead")); return; }
                var list = new ScrollView(ScrollViewMode.Vertical);
                list.AddToClassList("gr-rows");
                foreach (var need in m.Needs) list.Add(NeedRow(need));
                _groomTable.Add(list);
                return;
            }
            var head = GrRow(true);
            head.Add(Col(Cell("", "gr-head"), 0, 18));
            head.Add(Col(Cell("Trail", "gr-head"), 1.3f));
            head.Add(Col(Cell("Grooming", "gr-head"), 0.95f));
            head.Add(Col(Cell("Priority", "gr-head"), 0.62f));
            head.Add(Col(Cell(_groomLast ? "Last night" : "Tonight", "gr-head"), 1.2f));
            head.Add(Col(Cell("h", "gr-head", true), 0, 34));
            _groomTable.Add(head);
            var rows = new ScrollView(ScrollViewMode.Vertical);
            rows.AddToClassList("gr-rows");
            var shown = m.RowsFor(_groomFilter);
            if (shown.Count == 0) rows.Add(Cell(_groomFilter == GroomFilter.Never ? "Every trail is groomed by its setting." : _groomLast ? "Nothing was groomed last night." : "Nothing on the plan.", "gr-cell--lead"));
            foreach (var r in shown) rows.Add(TrailGroomRow(r));
            _groomTable.Add(rows);
        }

        VisualElement NeedRow(GroomingTabModel.Need need)
        {
            var buttons = new List<(string, Action)> { ("Show me", () => GroomingShowMe?.Invoke(need.Row)) };
            if (need.Fix != GroomFix.None && need.FixLabel != null) buttons.Add((need.FixLabel, () => RunGroomFix(need)));
            if (need.Dismissable) buttons.Add(("✕", () => { _groomDismissed.Add(need.Key); GroomingRefreshWanted?.Invoke(); }));
            string title = need.Symbol.Length > 0 ? $"{need.Symbol}  {need.Name}" : need.Name;
            var row = ToolWindow.ProblemRow(need.Number, title, need.Why, need.Severity != GroomSeverity.Bad, buttons.ToArray());
            row.name = "grooming-need-" + need.Number;
            row.RegisterCallback<PointerEnterEvent>(_ => GroomingHovered?.Invoke(need.Row));
            row.RegisterCallback<PointerLeaveEvent>(_ => GroomingHovered?.Invoke(0));
            return row;
        }

        void RunGroomFix(GroomingTabModel.Need need)
        {
            if (need.Fix == GroomFix.Buy) { SetGroomEquip(true); OpenBuyCard(need.BuyModel); return; }
            if (need.Fix != GroomFix.Order) return;
            string problem = GroomOrderChosen?.Invoke(need.Row, need.Order);
            if (problem != null) { Toast(problem); return; }
            _groomDismissed.Add(need.Key);
            Toast($"{need.Name}: {(need.Order == GroomOrder.Push ? "a push" : "a till")} ordered for tonight; its setting stays yours");
            GroomingRefreshWanted?.Invoke();
        }

        VisualElement TrailGroomRow(GroomingTabModel.Row r)
        {
            var row = GrRow(false);
            row.name = "grooming-row-" + r.Id;
            row.AddToClassList("gr-row--item");
            var n = Cell(r.Need > 0 ? r.Need.ToString() : "", "gr-n");
            n.AddToClassList("mono");
            n.EnableInClassList("gr-n--bad", r.NeedSeverity == GroomSeverity.Bad);
            n.EnableInClassList("gr-n--warn", r.NeedSeverity == GroomSeverity.Warn);
            row.Add(Col(n, 0, 18));
            row.Add(Col(Cell(r.Symbol.Length > 0 ? $"{r.Symbol}  {r.Name}" : r.Name, "gr-cell"), 1.3f));
            ulong id = r.Id;
            var set = new ToolDropdown("grooming-set-" + id, _root.Q("hud"), true, i => ChooseGroomTrail(id, GroomingTabModel.SettingAt(i), null));
            set.Set(GroomingTabModel.SettingNames, GroomingTabModel.SettingIndex(r.Setting));
            set.Button.tooltip = GroomingTabModel.SettingTips[GroomingTabModel.SettingIndex(r.Setting)];
            set.Button.EnableInClassList("gr-dd--set", r.Setting != GroomSetting.Autopilot && r.Setting != GroomSetting.Never);
            _groomDropdowns.Add(set);
            row.Add(Col(set.Button, 0.95f));
            var pri = new ToolDropdown("grooming-prio-" + id, _root.Q("hud"), true, i => ChooseGroomTrail(id, null, GroomingTabModel.PriorityAt(i)));
            pri.Set(GroomingTabModel.PriorityNames, GroomingTabModel.PriorityIndex(r.Priority));
            pri.Button.tooltip = GroomingTabModel.PriorityTips[GroomingTabModel.PriorityIndex(r.Priority)];
            pri.Button.EnableInClassList("gr-dd--set", r.Priority != GroomPriority.Normal);
            _groomDropdowns.Add(pri);
            row.Add(Col(pri.Button, 0.62f));
            var what = Cell(r.What, "gr-cell");
            what.EnableInClassList("gr-cell--lead", r.WhatSeverity == GroomSeverity.Quiet);
            what.EnableInClassList("gr-warn", r.WhatSeverity == GroomSeverity.Warn);
            what.EnableInClassList("gr-bad", r.WhatSeverity == GroomSeverity.Bad);
            row.Add(Col(what, 1.2f));
            var h = Cell(r.Hours, "gr-cell", true);
            h.AddToClassList("mono");
            row.Add(Col(h, 0, 34));
            row.RegisterCallback<PointerEnterEvent>(_ => GroomingHovered?.Invoke(id));
            row.RegisterCallback<PointerLeaveEvent>(_ => GroomingHovered?.Invoke(0));
            row.RegisterCallback<ClickEvent>(e =>
            {
                for (var t = e.target as VisualElement; t != null && t != row; t = t.hierarchy.parent) if (t is Button) return;
                if (_trailRows.ContainsKey(id)) OpenTrailCard(id);
            });
            return row;
        }

        void ChooseGroomTrail(ulong id, GroomSetting? setting, GroomPriority? priority)
        {
            string problem = GroomTrailChosen?.Invoke(id, setting, priority);
            if (problem != null) Toast(problem);
            GroomingRefreshWanted?.Invoke();
        }

        void SetGroomLast(bool last)
        {
            if (last == _groomLast) return;
            _groomLast = last;
            ShowNetworkKey();
            GroomingRefreshWanted?.Invoke();
            FillGrooming();
        }

        void FillGroomEquip(GroomingTabModel m)
        {
            // Round 3 (owner 2026-11-21: simpler, still the information): one row a groomer, a crew tick and its hours per
            // shift and the night's; one All groomers row of used of available; one crews line. The hours count driving.
            _groomFleetPlate.Clear();
            _groomFleetPlate.Add(Hd2("Groomers", m.FleetHeader == "none yet" ? m.FleetHeader : ""));   // the maker and count are on the sub-tab line
            int shifts = m.DayColumn ? 3 : 2;
            var head = GrRow(true);
            head.Add(Col(Cell("", "gr-head"), 0.9f));
            head.Add(Col(Cell("", "gr-head"), 0.6f));
            for (int s = 0; s < shifts; s++)
            {
                var h = Cell(m.Shifts[s].Name, "gr-head gr-cc");   // owner 2026-11-21: just the shift's name (its times are on the strip)
                h.name = "grooming-shift-" + s;
                head.Add(Col(h, 0, ShiftW));
            }
            head.Add(Col(Cell(m.DayColumn ? "Day total" : "Tonight", "gr-head", true), 0, TotalW));
            _groomFleetPlate.Add(head);
            if (m.Fleet.Count == 0) _groomFleetPlate.Add(Cell("No groomers yet: buy one below.", "gr-cell--lead"));
            for (int i = 0; i < m.Fleet.Count; i++)
            {
                var f = m.Fleet[i];
                int number = f.Number;
                var row = GrRow(false);
                row.name = "grooming-groomer-" + number;
                row.AddToClassList("gr-row--item");
                row.EnableInClassList("gr-row--on", _groomCardNumber == number);
                row.tooltip = f.Model;
                row.Add(Col(Cell(f.Label, "gr-cell"), 0.9f));
                row.Add(Col(Cell(f.Short, "gr-cell gr-cell--nm"), 0.6f));
                for (int s = 0; s < shifts; s++)
                {
                    int shift = s;
                    bool on = f.Crewed[s];
                    // the crews go out by rank (a winch groomer first when a pitch needs it, then the 800s): one crew fewer or more
                    int crews = m.Shifts[s].Crews;
                    var cell = new Button(() => ChooseCrews(shift, on ? Math.Max(0, crews - 1) : crews + 1))
                    {
                        name = $"grooming-crew-{number}-{s}",
                        tooltip = $"{(on ? "One crew fewer" : "One crew more")} on the {m.Shifts[s].Name.ToLowerInvariant()} shift (the crews take the winch groomers and the 800s first)",
                    };
                    cell.AddToClassList("gr-chk");
                    cell.EnableInClassList("row--on", on);
                    var box = new VisualElement { pickingMode = PickingMode.Ignore };
                    box.AddToClassList("chk");
                    var tick = new VisualElement { pickingMode = PickingMode.Ignore };
                    tick.AddToClassList("chk__tick");
                    box.Add(tick);
                    cell.Add(box);
                    var pair = new VisualElement();
                    pair.AddToClassList("gr-hpair");
                    pair.Add(cell);
                    int used = f.UsedOn(s);
                    var hrs = Cell(on || used > 0 ? $"{GroomingTabModel.H(used)} h" : "", "gr-cell gr-hrs");
                    hrs.name = $"grooming-hours-{number}-{s}";
                    hrs.AddToClassList("mono");
                    if (on || used > 0)
                        hrs.tooltip = $"{GroomingTabModel.H(f.PlanMin[s])} h grooming and {GroomingTabModel.H(f.TransitMin[s])} h driving of {GroomingTabModel.H(f.OpenMin[s])} h on the {m.Shifts[s].Name.ToLowerInvariant()} shift";
                    pair.Add(hrs);
                    row.Add(Col(pair, 0, ShiftW));
                }
                var day = Cell(f.TonightText, "gr-cell", true);   // its work of its own hours (owner 2026-11-21)
                day.name = "grooming-hours-" + number;
                day.AddToClassList("mono");
                day.tooltip = $"{GroomingTabModel.H(f.PlanTotal)} h grooming and {GroomingTabModel.H(f.TransitTotal)} h driving of {GroomingTabModel.H(f.OpenTotal)} h crewed";
                row.Add(Col(day, 0, TotalW));
                row.RegisterCallback<ClickEvent>(e =>
                {
                    for (var t = e.target as VisualElement; t != null && t != row; t = t.hierarchy.parent) if (t is Button) return;
                    OpenGroomerCard(number);
                });
                _groomFleetPlate.Add(row);
            }
            if (m.Fleet.Count > 0)
            {
                // All groomers: used (grooming and driving) of what the crews out can give, per shift and in all
                var all = GrRow(false);
                all.name = "grooming-hours-all";
                all.AddToClassList("gr-row--tot");
                all.Add(Col(Cell("All groomers", "gr-cell"), 1.5f));
                int usedAll = 0, openAll = 0, groomAll = 0, driveAll = 0;
                for (int s = 0; s < shifts; s++)
                {
                    int used = 0, open = 0, groom = 0, drive = 0;
                    foreach (var f in m.Fleet) { used += f.UsedOn(s); open += f.OpenMin[s]; groom += f.PlanMin[s]; drive += f.TransitMin[s]; }
                    usedAll += used; openAll += open; groomAll += groom; driveAll += drive;
                    var c = Cell(open > 0 || used > 0 ? $"{GroomingTabModel.H(used)} h" : "–", "gr-cell gr-cc");   // owner 2026-11-21: the hours used only
                    c.name = "grooming-hours-all-" + s;
                    c.EnableInClassList("gr-warn", used > open);
                    c.tooltip = $"{GroomingTabModel.H(groom)} h grooming and {GroomingTabModel.H(drive)} h driving of {GroomingTabModel.H(open)} h the {m.Shifts[s].Name.ToLowerInvariant()} crews are out";
                    all.Add(Col(c, 0, ShiftW));
                }
                var t = Cell($"{GroomingTabModel.H(usedAll)} h", "gr-cell gr-cell--b", true);
                t.name = "grooming-hours-all-day";
                t.tooltip = $"{GroomingTabModel.H(groomAll)} h grooming and {GroomingTabModel.H(driveAll)} h driving of {GroomingTabModel.H(openAll)} h crewed";
                all.Add(Col(t, 0, TotalW));
                _groomFleetPlate.Add(all);
                // one crews line: Auto, or set by you with the way back
                var crews = GrRow(false);
                crews.name = "grooming-crews";
                crews.AddToClassList("gr-row--tot");
                crews.Add(Cell(m.CrewsLine, "gr-cell gr-cell--lead"));
                if (m.CrewsPinned)
                {
                    var back = new Button(() => ChooseCrews(-1, GroomCrews.Auto)) { text = "Back to Auto", name = "grooming-crews-auto",
                                                                                     tooltip = "Auto crews just enough for the plan, every shift" };
                    back.AddToClassList("gr-pin");
                    crews.Add(back);
                }
                else crews.tooltip = "Tick or untick a groomer on a shift to set that shift's crews yourself";
                _groomFleetPlate.Add(crews);
            }

            // A Buy button; the models show once buying has begun (owner 2026-11-21: one buy form, in the window).
            _groomBuyPlate.Clear();
            if (_groomBuyOpen) { FillBuyForm(m); return; }
            var acts = new VisualElement { name = "grooming-buy-start" };
            acts.AddToClassList("tw-acts");
            acts.AddToClassList("gr-buy__acts");
            var start = new Button(() => OpenBuyCard(GroomerModel.S400)) { name = "grooming-buy-go" };
            start.AddToClassList("mp-go");
            start.Add(GoLabel(m.Empty ? "Buy your first groomer" : "Buy a groomer"));
            start.Add(GoLabel($"from {GroomingTabModel.Usd(GroomingTabModel.PriceUsd(GroomerModel.S400, GroomerDrive.Diesel))}"));
            acts.Add(start);
            _groomBuyPlate.Add(acts);
        }

        const int ShiftW = 104, TotalW = 112;

        void ChooseCrews(int shift, int count)
        {
            string problem = GroomCrewsChosen?.Invoke(shift, count);
            if (problem != null) Toast(problem);
            GroomingRefreshWanted?.Invoke();
        }

        // ---------- the cards ----------

        void OpenGroomerCard(int number)
        {
            CloseOtherCards();
            _groomCardNumber = number;
            _groomSellAsk = false;
            FillGroomerCard();
            if (_groomCardNumber == 0) return;
            _groomCard.RemoveFromClassList("hidden");
            CardsChanged(false);
            if (_analysisTab == "grooming") FillGrooming();
        }

        void CloseOtherCards()
        {
            CloseTrailCardIfOpen();
            CloseLiftCardIfOpen();
            CloseSkierCardIfOpen();
            CloseWaterCardIfOpen();
            CloseAreaCardIfOpen();
        }

        GroomingTabModel.FleetRow FleetRowOf(int number)
        {
            if (_groomModel == null) return null;
            foreach (var f in _groomModel.Fleet) if (f.Number == number) return f;
            return null;
        }

        void FillGroomerCard()
        {
            var f = FleetRowOf(_groomCardNumber);
            if (f == null) { CloseGroomCards(); return; }
            _groomCard.Clear();
            _groomCard.Add(CardHead($"Groomer {f.Number}", GroomingTabModel.ShiftsText(f), "groomer-card"));
            var kv = new VisualElement();
            kv.AddToClassList("tw-kv");
            foreach (var (k, v) in GroomingTabModel.GroomerFigures(f)) kv.Add(KvRow(k, v));
            _groomCard.Add(kv);
            var sched = new VisualElement();
            sched.AddToClassList("lift-sched");
            var (winch, winchHint) = GroomingTabModel.WinchText(f.Kind);
            sched.Add(SchedLine("Winch", Cell(winch, "gr-card__v")));
            sched.Add(Hint(winchHint));
            VisualElement tiller;
            if (f.Tiller) tiller = Cell("Fitted", "gr-card__v");
            else
            {
                var fit = new Button(() =>
                {
                    string problem = GroomerTillerChosen?.Invoke(f.Number, true);
                    if (problem != null) Toast(problem);
                    else Toast($"Groomer {f.Number}: wider tiller fitted");
                    GroomingRefreshWanted?.Invoke();
                }) { name = "groomer-card-tiller", text = $"Fit · {GroomingTabModel.Usd(GroomingTabModel.TillerPriceUsd)}" };
                fit.AddToClassList("mp-ghost");
                fit.AddToClassList("gr-card__btn");
                fit.SetEnabled(!f.Dev);
                if (f.Dev) fit.tooltip = "A dev groomer isn't in the design";
                tiller = fit;
            }
            sched.Add(SchedLine("Tiller", tiller));
            sched.Add(Hint(GroomingTabModel.TillerHint()));
            _groomCard.Add(sched);
            var acts = new VisualElement();
            acts.AddToClassList("tw-btns");
            acts.AddToClassList("an-card__acts");
            if (f.Dev) acts.Add(Hint("A dev groomer (-groomers): free, not saved, not for sale."));
            else
            {
                int price = GroomingTabModel.SellUsd(f);
                var sell = new Button(() =>
                {
                    if (!_groomSellAsk) { _groomSellAsk = true; FillGroomerCard(); return; }
                    _groomSellAsk = false;
                    string problem = GroomerSellChosen?.Invoke(f.Number);
                    if (problem != null) { Toast(problem); return; }
                    Toast($"Groomer {f.Number} sold");
                    CloseGroomCards();
                    GroomingRefreshWanted?.Invoke();
                }) { name = "groomer-card-sell", text = _groomSellAsk ? $"Sell it? Click again · {GroomingTabModel.Usd(price)}" : $"Sell · {GroomingTabModel.Usd(price)}",
                     tooltip = "Asks first; a guess at half its price" };
                sell.AddToClassList("mp-ghost");
                acts.Add(sell);
            }
            _groomCard.Add(acts);
        }

        /// <summary>The buy form opens in the Equipment page, in the catalogue's place.</summary>
        void OpenBuyCard(GroomerModel model)
        {
            _groomBuyModel = model;
            _groomBuyDrive = GroomerDrive.Diesel;
            _groomBuyOpen = true;
            _groomEquip = true;
            if (_analysisTab == "grooming") FillGrooming();
        }

        void CloseBuyForm()
        {
            if (!_groomBuyOpen) return;
            _groomBuyOpen = false;
            if (_analysisTab == "grooming") FillGrooming();
        }

        void FillBuyForm(GroomingTabModel m)
        {
            var p = _groomBuyPlate;
            var hd = new VisualElement { name = "groomer-buy-card" };
            hd.AddToClassList("gr-hd2");
            hd.Add(Cell(m.Empty ? "Buy your first groomer" : "Buy a Skogkatt", "gr-hd2__l"));
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            hd.Add(gap);
            hd.Add(Cell("Arrives at once · diesel price", "gr-hd2__r"));
            var x = new Button(CloseBuyForm) { name = "groomer-buy-card-x", text = "✕", tooltip = "Stop buying (Esc)" };
            x.AddToClassList("mp-x");
            hd.Add(x);
            p.Add(hd);

            // The models: click one to choose it.
            var ch = GrRow(true);
            ch.Add(Col(Cell("Model", "gr-head"), 0, 52));
            ch.Add(Col(Cell("Price", "gr-head", true), 0, 80));
            ch.Add(Col(Cell("PS", "gr-head", true), 0, 38));
            ch.Add(Col(Cell("Till", "gr-head", true), 0, 80));
            ch.Add(Col(Cell("Push", "gr-head", true), 0, 98));
            ch.Add(Col(Cell("Slopes", "gr-head gr-gap"), 1));
            p.Add(ch);
            foreach (var c in GroomingTabModel.Catalogue())
            {
                var model = c.Model;
                var row = GrRow(false);
                row.name = "grooming-model-" + c.Name.Replace(" ", "").ToLowerInvariant();
                row.AddToClassList("gr-row--item");
                row.EnableInClassList("gr-row--on", model == _groomBuyModel);
                row.Add(Col(Cell(c.Name, "gr-cell"), 0, 52));
                row.Add(Col(Mono(c.Price, true), 0, 80));
                row.Add(Col(Mono(c.Ps, true), 0, 38));
                row.Add(Col(MonoUnit(c.Till, "ac/h"), 0, 80));
                row.Add(Col(MonoUnit(c.Push, "yd³/h"), 0, 98));
                row.Add(Col(Cell(c.Slopes, "gr-cell gr-cell--nm gr-gap"), 1));
                row.RegisterCallback<ClickEvent>(_ => { _groomBuyModel = model; FillGrooming(); });
                p.Add(row);
            }
            var park = GrRow(false);
            park.AddToClassList("gr-row--dis");
            park.tooltip = "Parks come later";
            park.Add(Col(Cell("Park", "gr-cell"), 0, 52));
            park.Add(Col(Cell("later", "gr-cell", true), 0, 80));
            park.Add(Col(Cell("", "gr-cell"), 0, 38));
            park.Add(Col(Cell("", "gr-cell"), 0, 80));
            park.Add(Col(Cell("", "gr-cell"), 0, 98));
            park.Add(Col(Cell("Park features", "gr-cell gr-gap"), 1));
            p.Add(park);

            // Its drive, then Buy.
            var sched = new VisualElement();
            sched.AddToClassList("lift-sched");
            sched.AddToClassList("gr-buy");
            var drives = new (string, bool, string, Action)[3];
            for (int i = 0; i < 3; i++)
            {
                var d = (GroomerDrive)(i + 1);
                drives[i] = (GroomingTabModel.DriveName(d), d == _groomBuyDrive, null, () => { _groomBuyDrive = d; FillGrooming(); });
            }
            sched.Add(BuyRow("Drive", Seg("groomer-buy-drive", drives)));
            p.Add(sched);
            int list0 = GroomingTabModel.PriceUsd(_groomBuyModel, GroomerDrive.Diesel), price = GroomingTabModel.PriceUsd(_groomBuyModel, _groomBuyDrive);
            string extra = price != list0 ? $"{GroomingTabModel.DriveName(_groomBuyDrive)} +{GroomingTabModel.Usd(price - list0)}. " : "";
            var hint = Hint(extra + "A wider tiller is fitted later, from the groomer's card. A winch comes only on a W.");
            hint.AddToClassList("gr-card__hint");
            p.Add(hint);
            var acts = new VisualElement();
            acts.AddToClassList("tw-acts");
            acts.AddToClassList("gr-buy__acts");
            var buy = new Button(() =>
            {
                string problem = GroomerBuyChosen?.Invoke(_groomBuyModel, _groomBuyDrive);
                if (problem != null) { Toast(problem); return; }
                Toast($"Skogkatt {GroomingTabModel.ModelName(_groomBuyModel)} bought: ready tonight");
                _groomBuyOpen = false;
                GroomingRefreshWanted?.Invoke();
                FillGrooming();
            }) { name = "groomer-buy-card-buy" };
            buy.AddToClassList("mp-go");
            buy.Add(GoLabel($"Buy Skogkatt {GroomingTabModel.ModelName(_groomBuyModel)} · {GroomingTabModel.Usd(price)}"));
            acts.Add(buy);
            var cancel = new Button(CloseBuyForm) { name = "groomer-buy-card-cancel", text = "Cancel" };
            cancel.AddToClassList("mp-ghost");
            acts.Add(cancel);
            p.Add(acts);
        }

        static VisualElement BuyRow(string key, VisualElement value)
        {
            var row = new VisualElement();
            row.AddToClassList("lift-sched__row");
            var k = new Label(key);
            k.AddToClassList("lift-sched__k");
            row.Add(k);
            row.Add(value);
            return row;
        }

        VisualElement CardHead(string title, string kind, string name)
        {
            var head = new VisualElement();
            head.AddToClassList("mp-head");
            head.AddToClassList("mp-head--kind");
            var (line, kindLabel) = CardTitles(head, name + "-kind");
            var t = new Label(title) { name = name + "-name" };
            t.AddToClassList("mp-title");
            line.Add(t);
            kindLabel.text = kind;
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            var x = new Button(CloseGroomCards) { name = name + "-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            return head;
        }

        void CloseGroomCards()
        {
            bool was = _groomCardNumber != 0;
            _groomCardNumber = 0;
            _groomSellAsk = false;
            _groomCard?.AddToClassList("hidden");
            if (!was) return;
            CardsChanged(true);
            if (_analysisTab == "grooming") FillGrooming();
        }

        bool GroomCardOpen => _groomCard != null && !_groomCard.ClassListContains("hidden");

        /// <summary>Esc: closes a groomer or buy card (true when it did).</summary>
        public bool CloseGroomCardIfOpen()
        {
            if (GroomCardOpen) { CloseGroomCards(); return true; }
            if (_groomBuyOpen) { CloseBuyForm(); return true; }
            return false;
        }

        // ---------- the map's key while the tab is open ----------

        void ShowGroomKey()
        {
            _legendBody.Clear();
            _legendTitle.text = _groomLast ? "Grooming last night" : "Grooming tonight";
            _legendBody.Add(LegendRow(GroomMapPlanned, false, _groomLast ? "Groomed" : "Groomed tonight", ""));
            _legendBody.Add(LegendRow(GroomMapNeeds, false, "Needs you", ""));
            _legendBody.Add(LegendRow(GroomMapBad, false, "Not reached", ""));
            _legendBody.Add(LegendRow(GroomMapIdle, false, _groomLast ? "Not groomed" : "Nothing tonight", ""));
            _legendBody.Add(LegendRow(GroomMapNever, false, "Never", ""));
            _legendBody.Add(Note("Hover a block on the night strip to find its trail."));
            ShowLegend();
        }

        // ---------- the morning toast ----------

        /// <summary>The morning's line ("4 trails not groomed · 9 groomer-hours short"); a click opens Last night.</summary>
        public void GroomMorningToast(string text)
        {
            Toast(text, new Color32(0xf0, 0xb4, 0x3c, 255), () =>
            {
                _groomLast = true;
                _groomEquip = false;
                SetAnalysisTab("grooming");
                SetAnalysis(true);
                GroomingRefreshWanted?.Invoke();
            });
        }

        // ---------- the trail card's grooming row ----------

        VisualElement _trailCardGroom;
        readonly List<ToolDropdown> _trailCardGroomMenus = new List<ToolDropdown>();

        void FillTrailGrooming(TrailRow t)
        {
            if (_trailCardGroom == null)
            {
                _trailCardGroom = new VisualElement { name = "trail-card-grooming" };
                _trailCardGroom.AddToClassList("lift-sched");
                int at = _trailCard.IndexOf(_trailCardSwitch);
                _trailCard.Insert(at + 1, _trailCardGroom);
            }
            foreach (var d in _trailCardGroomMenus) d.Close();
            _trailCardGroomMenus.Clear();
            _trailCardGroom.Clear();
            var g = t.Built ? TrailGroomingOf?.Invoke(t.Id) : null;
            _trailCardGroom.EnableInClassList("hidden", !g.HasValue);
            if (!g.HasValue) return;
            ulong id = t.Id;
            var row = _groomModel?.RowOf(id);
            var row2 = new VisualElement();
            row2.AddToClassList("lift-sched__row");
            var k = new Label("Grooming");
            k.AddToClassList("lift-sched__k");
            k.AddToClassList("gr-card__k");
            row2.Add(k);
            var names = new string[GroomingTabModel.SettingNames.Length + 2];
            Array.Copy(GroomingTabModel.SettingNames, names, GroomingTabModel.SettingNames.Length);
            names[names.Length - 2] = "Skip tonight";
            names[names.Length - 1] = "Push tonight";
            var off = new bool[names.Length];
            off[names.Length - 1] = row == null || !row.CanPush;
            var set = new ToolDropdown("trail-card-grooming-set", _root.Q("hud"), false, i =>
            {
                if (i < GroomingTabModel.SettingNames.Length) { ChooseGroomTrail(id, GroomingTabModel.SettingAt(i), null); FillTrailCard(); return; }
                var order = i == names.Length - 2 ? GroomOrder.Skip : GroomOrder.Push;
                string problem = GroomOrderChosen?.Invoke(id, order);
                Toast(problem ?? (order == GroomOrder.Skip ? "Skipped tonight; its setting stays" : "A push ordered for tonight; its setting stays"));
                GroomingRefreshWanted?.Invoke();
            });
            set.Set(names, GroomingTabModel.SettingIndex(g.Value.Setting), off);
            set.Button.AddToClassList("gr-card__dd");
            _trailCardGroomMenus.Add(set);
            row2.Add(set.Button);
            var pri = new ToolDropdown("trail-card-grooming-prio", _root.Q("hud"), false, i => { ChooseGroomTrail(id, null, GroomingTabModel.PriorityAt(i)); FillTrailCard(); });
            pri.Set(GroomingTabModel.PriorityNames, GroomingTabModel.PriorityIndex(g.Value.Priority));
            pri.Button.AddToClassList("gr-card__dd");
            _trailCardGroomMenus.Add(pri);
            row2.Add(pri.Button);
            _trailCardGroom.Add(row2);
            var line = new Label(row?.CardLine ?? "Not groomed yet") { name = "trail-card-grooming-line" };
            line.AddToClassList("lift-sched__hint");
            _trailCardGroom.Add(line);
        }

        // ---------- small parts ----------

        static VisualElement GrRow(bool head)
        {
            var r = new VisualElement();
            r.AddToClassList("gr-row");
            if (head) r.AddToClassList("gr-row--head");
            return r;
        }

        static VisualElement Col(VisualElement e, float grow, float width = 0)
        {
            e.AddToClassList("gr-col");
            e.style.flexGrow = grow;
            e.style.flexShrink = grow > 0 ? 1 : 0;
            e.style.flexBasis = width > 0 ? width : 0;
            if (width > 0) e.style.width = width;
            return e;
        }

        static Label Cell(string text, string classes, bool numeric = false)
        {
            var l = new Label(text ?? "") { pickingMode = PickingMode.Ignore };
            foreach (var c in classes.Split(' ')) if (c.Length > 0) l.AddToClassList(c);
            if (numeric) l.AddToClassList("gr-cell--n");
            return l;
        }

        static Label Mono(string text, bool numeric)
        {
            var l = Cell(text, "gr-cell", numeric);
            l.AddToClassList("mono");
            return l;
        }

        static VisualElement MonoUnit(string figure, string unit)
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.style.flexDirection = FlexDirection.Row;
            box.style.justifyContent = Justify.FlexEnd;
            box.Add(Mono(figure, true));
            var u = Cell(" " + unit, "gr-cell gr-cell--lead");
            box.Add(u);
            return box;
        }

        static VisualElement Hd2(string left, string right)
        {
            var hd = new VisualElement();
            hd.AddToClassList("gr-hd2");
            hd.Add(Cell(left, "gr-hd2__l"));
            hd.Add(Cell(right, "gr-hd2__r"));
            return hd;
        }

        static VisualElement KvRow(string k, string v)
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
            return row;
        }

        static VisualElement SchedLine(string label, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("lift-sched__row");
            var k = new Label(label);
            k.AddToClassList("lift-sched__k");
            row.Add(k);
            control.style.flexGrow = 1;
            row.Add(control);
            return row;
        }

        static Label Hint(string text)
        {
            var l = new Label(text);
            l.AddToClassList("lift-sched__hint");
            return l;
        }

        // ---------- tests and captures ----------

        /// <summary>Opens a sub-tab, the strip's night and a filter (tests and captures).</summary>
        public void ShowGroomingForCapture(bool equipment, bool lastNight, GroomFilter filter)
        {
            _groomEquip = equipment;
            _groomBuyOpen = false;
            _groomFilter = filter;
            if (_groomLast != lastNight) { _groomLast = lastNight; GroomingRefreshWanted?.Invoke(); }
            FillGrooming();
        }

        /// <summary>Opens a groomer's card or a model's buy card (tests and captures).</summary>
        public void OpenGroomerCardFor(int number) => OpenGroomerCard(number);
        public void OpenGroomerBuyCardFor(GroomerModel model) => OpenBuyCard(model);
    }
}
