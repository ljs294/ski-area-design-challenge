using System;
using System.Collections.Generic;
using MountainPlanner.UI.Hud;
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
        /// <summary>F4: whether it's running (Open, Closed, On hold, Scheduled; a placeholder until 4.11: Planned or Closed).</summary>
        public OperatingStatus Status;
        /// <summary>F6 (owner 2026-11-08): how popular it is with guests, 0–100; null shows "—" until guests ride (4.12/4.17).</summary>
        public int? Popularity;
        /// <summary>F5: its type under the card's name ("Detachable Six Pack", <see cref="LiftTypeMenu.TypeName"/>).</summary>
        public string Kind;
        /// <summary>F6: the towers' heads ("6 s6, 4 s4, 2 b8"), the lowest chairs over the ground and the rise against the
        /// type's limit (metres; the limit infinite for none), from the built lift's rope; null and NaN for a plan.</summary>
        public string Heads;
        public double LowestChairsMetres, RiseMetres, RiseLimitMetres;
        /// <summary>Task 4.11 PR B (shape 13): the plate beside its name ("3", "A").</summary>
        public string Plate;
        /// <summary>LO-11 v2: the gust it holds at, in mph, or -1 for a lift that never holds (tows and carpets).</summary>
        public int HoldMph;
        /// <summary>LO-09: peak days only (else daily); LO-07 v2: its own hours, or null for the resort's.</summary>
        public bool PeakDays;
        public Domain.Design.DayHours? OwnHours;
        /// <summary>The resort's day, which a lift's own hours stay inside.</summary>
        public Domain.Design.DayHours ResortHours;
    }

    /// <summary>
    /// Analysis › Lifts (task 09; the approved mockup's listTab and focus card, on P3-08's shared <see cref="AnalysisList"/>):
    /// the lifts with Lift, Model, Length, Vertical, An hour, Wait and Popularity (the simulation's, "—" until it runs) and Status,
    /// spreadsheet filters, rows that light their lift on the map, and a focus card with Edit and Remove.
    /// </summary>
    public sealed partial class MountainHud
    {
        AnalysisList _liftsList;
        VisualElement _liftCard;
        Label _liftCardName, _liftCardKind, _liftCardPlate;
        VisualElement _liftCardKv, _liftCardSched;
        Button _liftCardRide;
        ulong _liftCardId;
        readonly Dictionary<ulong, LiftRow> _liftRows = new Dictionary<ulong, LiftRow>();

        /// <summary>A lift row under the pointer (0 for none), or its card open: the app lights it on the map.</summary>
        public event Action<ulong> LiftFocused;
        /// <summary>The card's Edit or Remove: the app arms that tool on the lift.</summary>
        public event Action<ulong> LiftEditChosen, LiftRemoveChosen;
        /// <summary>F6: the card's name renamed (one undoable RenameLift); the app returns why it can't be, or null.</summary>
        public Func<ulong, string, string> LiftRenamed;
        /// <summary>Task 4.11 PR B: the card's plate changed (one undoable SetLiftPlate); the app returns why it can't be, or null.</summary>
        public Func<ulong, string, string> LiftPlateChanged;
        /// <summary>LO-09, LO-07 v2: the card's Runs and Hours (one undoable SetLiftSchedule): peak days, and its own hours or null.</summary>
        public Func<ulong, bool, Domain.Design.DayHours?, string> LiftScheduleChanged;
        /// <summary>Task 4.11 PR B: the card's Ride (or R on the card): ride that lift from the bottom.</summary>
        public event Action<ulong> LiftRideChosen;
        TextField _liftCardField, _liftCardPlateField;

        static readonly double[] LiftLengthBinsFt = { 3000, 6000 }, LiftVerticalBinsFt = { 1000, 2000 };

        void WireLifts()
        {
            LiftRow R(AnalysisList.Row r) => (LiftRow)r.Data;
            _liftsList = new AnalysisList("lifts", new[]
            {
                new AnalysisList.Column { Title = "Lift", Grow = 1.0f, Cell = r => R(r).Name, Lead = r => R(r).Plate },
                new AnalysisList.Column { Title = "Model", Grow = 1.5f, Cell = r => R(r).Model },
                new AnalysisList.Column { Title = "Length", Numeric = true, Grow = 0.95f, Cell = r => ToolFormat.Length(R(r).LengthMetres),
                    SortKey = r => R(r).LengthMetres, Bin = r => Bin(R(r).LengthMetres, LiftLengthBinsFt), Bins = BinLabels(LiftLengthBinsFt) },
                new AnalysisList.Column { Title = "Vertical", Numeric = true, Grow = 0.9f, Cell = r => ToolFormat.Length(R(r).VerticalMetres),
                    SortKey = r => R(r).VerticalMetres, Bin = r => Bin(R(r).VerticalMetres, LiftVerticalBinsFt), Bins = BinLabels(LiftVerticalBinsFt) },
                new AnalysisList.Column { Title = "An hour", Numeric = true, Grow = 0.75f, Cell = r => $"{R(r).Capacity:#,0}", SortKey = r => R(r).Capacity },
                new AnalysisList.Column { Title = "Wait", Numeric = true, Grow = 0.9f, Cell = r => LiftWaitText(R(r).Id),
                    SortKey = r => _liftLive.TryGetValue(R(r).Id, out var v) ? v.WaitSeconds : -1 },
                PopularityColumn(r => R(r).Popularity),
                new AnalysisList.Column { Title = "Status", Grow = 0.8f, Class = "an-cell--gap", Cell = r => OperatingStatusWords.Word(LiftStatus(R(r))),
                    Bin = r => LiftStatus(R(r)).ToString(), Bins = OperatingStatusWords.Bins() },
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
            head.AddToClassList("mp-head--kind");
            var (nameLine, kind) = CardTitles(head, "lift-card-kind");
            _liftCardKind = kind;
            // Task 4.11 PR B: the plate leads the name; a click (or Shift+F2) changes it, as the name renames.
            _liftCardPlate = new Label { name = "lift-card-plate", tooltip = "Change the plate (Shift+F2)" };
            _liftCardPlate.AddToClassList("mp-plate");
            _liftCardPlate.AddToClassList("lift-card__plate");
            _liftCardPlate.RegisterCallback<ClickEvent>(_ => EditLiftCardPlate());
            nameLine.Add(_liftCardPlate);
            _liftCardName = new Label { name = "lift-card-name", tooltip = "Rename (F2)" };
            _liftCardName.AddToClassList("mp-title");
            _liftCardName.AddToClassList("tw__name--edit");
            _liftCardName.RegisterCallback<ClickEvent>(_ => RenameLiftCard());
            nameLine.Add(_liftCardName);
            _liftCard.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == UnityEngine.KeyCode.F2 && _liftCardField?.parent == null && _liftCardPlateField?.parent == null)
                {
                    if (e.shiftKey) EditLiftCardPlate(); else RenameLiftCard();
                    e.StopPropagation();
                }
            });
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            // Task 4.11 PR B: Ride this lift, the video camera in the camera's style; greyed for a plan.
            _liftCardRide = new Button(RideLiftCard) { name = "lift-card-ride", tooltip = "Ride this lift (R)" };
            _liftCardRide.AddToClassList("mp-x");
            _liftCardRide.AddToClassList("lift-card__ride");
            _liftCardRide.Add(new HudIcon("s-video"));
            head.Add(_liftCardRide);
            var x = new Button(CloseLiftCard) { name = "lift-card-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            head.Add(x);
            _liftCard.Add(head);
            _liftCardKv = new VisualElement();
            _liftCardKv.AddToClassList("tw-kv");
            _liftCard.Add(_liftCardKv);
            _liftCardSched = new VisualElement { name = "lift-card-schedule" };
            _liftCardSched.AddToClassList("lift-sched");
            _liftCard.Add(_liftCardSched);
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

        /// <summary>A lift as the simulation runs it now (task 4.11): its Status, the line and the wait in real seconds.</summary>
        public readonly struct LiftLive
        {
            public readonly OperatingStatus Status;
            public readonly int Line;
            /// <summary>LO-04 v1: the wait at the current speed, real seconds.</summary>
            public readonly double WaitSeconds;
            /// <summary>The card's Today row: a hold is likely today (the forecast, 4.05; until then the F1 gust).</summary>
            public readonly bool HoldsLikely;
            public LiftLive(OperatingStatus status, int line, double waitSeconds, bool holdsLikely = false)
            {
                Status = status; Line = line; WaitSeconds = waitSeconds; HoldsLikely = holdsLikely;
            }
            public bool Same(LiftLive o) => Status == o.Status && Line == o.Line && (int)WaitSeconds == (int)o.WaitSeconds && HoldsLikely == o.HoldsLikely;
        }

        readonly Dictionary<ulong, LiftLive> _liftLive = new Dictionary<ulong, LiftLive>();
        bool _liveDirty;

        OperatingStatus LiftStatus(LiftRow r) => _liftLive.TryGetValue(r.Id, out var v) ? v.Status : r.Status;

        /// <summary>LO-04 v1's readout: "300 in line · 7:30" (the list shows "300 · 7:30"); "—" with no line or no simulation.</summary>
        string LiftWaitText(ulong id, bool words = false)
        {
            if (!_liftLive.TryGetValue(id, out var v) || v.Status == OperatingStatus.Planned) return "—";
            if (v.Line == 0) return words ? "No line" : "—";
            return words ? $"{v.Line:#,0} in line · {Hud.HudText.Clock(v.WaitSeconds)}" : $"{v.Line:#,0} · {Hud.HudText.Clock(v.WaitSeconds)}";
        }

        /// <summary>The simulation's word, line and wait for a lift (the app calls it at a bounded rate, never per tick).</summary>
        public void SetLiftLive(ulong id, LiftLive live)
        {
            if (_liftLive.TryGetValue(id, out var was) && was.Same(live)) return;
            _liftLive[id] = live;
            _liveDirty = true;
        }

        /// <summary>The lists and open cards redraw once after a batch of <see cref="SetLiftLive"/> and <see cref="SetTrailLive"/>.</summary>
        public void FlushLive()
        {
            if (!_liveDirty) return;
            _liveDirty = false;
            if (_liftsList != null && _analysisTab == "lifts") _liftsList.Refresh();
            if (_trailsList != null && _analysisTab == "trails") _trailsList.Refresh();
            if (_liftCardId != 0) FillLiftCard();
            if (_trailCardId != 0) FillTrailCard();
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
            CardsChanged(false);   // task P3-10, NT12
        }

        void FillLiftCard()
        {
            if (!_liftRows.TryGetValue(_liftCardId, out var l)) return;
            _liftCardName.text = l.Name;
            _liftCardPlate.text = l.Plate ?? "";
            _liftCardPlate.EnableInClassList("hidden", string.IsNullOrEmpty(l.Plate));
            _liftCardPlate.EnableInClassList("mp-plate--built", l.Built);
            _liftCardKind.text = l.Kind ?? "Lift";   // F5
            _liftCardRide.SetEnabled(l.Built);
            _liftCardRide.tooltip = l.Built ? "Ride this lift (R)" : "Build it to ride it";
            _liftCardKv.Clear();
            void Kv(string k, string v, bool warn = false)
            {
                var row = new VisualElement();
                row.AddToClassList("tw-kv__row");
                var kl = new Label(k);
                kl.AddToClassList("tw-kv__k");
                var vl = new Label(v);
                vl.AddToClassList("tw-kv__v");
                vl.AddToClassList("mono");
                if (warn) vl.AddToClassList("tw-kv__v--warn");
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
            Kv("Status", OperatingStatusWords.Word(LiftStatus(l)));   // F4, 4.11
            if (l.Built) Kv("Line", LiftWaitText(l.Id, words: true));   // LO-04 v1
            // LO-11 v2 (owner 2026-11-08): one figure for the hold, then whether one is expected today.
            Kv("Wind hold", l.HoldMph < 0 ? "Never" : $"{l.HoldMph} mph");
            if (l.HoldMph >= 0 && l.Built)
            {
                bool held = LiftStatus(l) == OperatingStatus.OnHold;
                bool likely = _liftLive.TryGetValue(l.Id, out var live) && live.HoldsLikely;
                Kv("Today", held ? "On hold now" : likely ? "Holds likely" : "No holds expected", held || likely);
            }
            FillLiftSchedule(l);
        }

        // ---------- LO-09 and LO-07 v2 on the card (task 4.11 PR B, the approved mockup's .sched) ----------

        const int QuarterHour = 15 * 60;

        void FillLiftSchedule(LiftRow l)
        {
            _liftCardSched.Clear();
            bool can = LiftScheduleChanged != null;
            _liftCardSched.EnableInClassList("hidden", !can);
            if (!can) return;
            var resort = l.ResortHours;
            SchedRow("Runs", Seg("lift-card-runs",
                ("Daily", !l.PeakDays, null, () => SetSchedule(l, false, l.OwnHours)),
                ("Peak days", l.PeakDays, "Weekends (and holidays, later): a lift for the busy days", () => SetSchedule(l, true, l.OwnHours))));
            SchedRow("Hours", Seg("lift-card-hours",
                ("The resort's", !l.OwnHours.HasValue, $"{HudText.ClockOfDay(resort.OpenSecond)} to {HudText.ClockOfDay(resort.StopSecond)}, last chair {HudText.ClockOfDay(resort.LastChairSecond)}",
                    () => SetSchedule(l, l.PeakDays, null)),
                ("Its own", l.OwnHours.HasValue, null, () => SetSchedule(l, l.PeakDays, l.OwnHours ?? resort))));
            if (!(l.OwnHours is Domain.Design.DayHours own)) return;
            var hrs = new VisualElement();
            hrs.AddToClassList("lift-sched__hrs");
            hrs.Add(HourStep("Opens", "lift-card-opens", own.OpenSecond, resort.OpenSecond, own.LastChairSecond - QuarterHour,
                v => new Domain.Design.DayHours(v, own.LastChairSecond, own.StopSecond), l));
            hrs.Add(HourStep("Last chair", "lift-card-last", own.LastChairSecond, own.OpenSecond + QuarterHour, own.StopSecond,
                v => new Domain.Design.DayHours(own.OpenSecond, v, own.StopSecond), l));
            hrs.Add(HourStep("Stops", "lift-card-stops", own.StopSecond, own.LastChairSecond, resort.StopSecond,
                v => new Domain.Design.DayHours(own.OpenSecond, own.LastChairSecond, v), l));
            _liftCardSched.Add(hrs);
            var hint = new Label($"Inside the resort's day, {HudText.ClockOfDay(resort.OpenSecond)} to {HudText.ClockOfDay(resort.StopSecond)}.");
            hint.AddToClassList("lift-sched__hint");
            _liftCardSched.Add(hint);
        }

        void SchedRow(string label, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("lift-sched__row");
            var k = new Label(label);
            k.AddToClassList("lift-sched__k");
            row.Add(k);
            row.Add(control);
            _liftCardSched.Add(row);
        }

        static VisualElement Seg(string name, params (string Label, bool On, string Tip, Action Go)[] opts)
        {
            var seg = new VisualElement { name = name };
            seg.AddToClassList("mp-seg");
            seg.AddToClassList("lift-sched__seg");
            for (int i = 0; i < opts.Length; i++)
            {
                var o = opts[i];
                var b = new Button(() => { if (!o.On) o.Go(); }) { text = o.Label, tooltip = o.Tip ?? "" };
                b.AddToClassList("mp-seg__opt");
                if (i == 0) b.AddToClassList("mp-seg__opt--first");
                b.EnableInClassList("mp-seg__opt--on", o.On);
                seg.Add(b);
            }
            return seg;
        }

        VisualElement HourStep(string label, string name, int value, int min, int max, Func<int, Domain.Design.DayHours> make, LiftRow l)
        {
            var box = new VisualElement();
            box.AddToClassList("lift-sched__hr");
            var k = new Label(label);
            k.AddToClassList("lift-sched__hk");
            box.Add(k);
            var step = new VisualElement { name = name };
            step.AddToClassList("mp-step");
            step.AddToClassList("lift-sched__step");
            var p = new Button(() => Go(-1)) { text = "‹", name = name + "-down" };
            var n = new Button(() => Go(1)) { text = "›", name = name + "-up" };
            p.AddToClassList("mp-step__b");
            n.AddToClassList("mp-step__b");
            var v = new Label(HudText.ClockOfDay(value));
            v.AddToClassList("mp-step__v");
            p.SetEnabled(value - QuarterHour >= min);
            n.SetEnabled(value + QuarterHour <= max);
            step.Add(p);
            step.Add(v);
            step.Add(n);
            box.Add(step);
            return box;
            void Go(int d)
            {
                int next = value + d * QuarterHour;
                if (next < min || next > max) return;
                SetSchedule(l, l.PeakDays, make(next));
            }
        }

        void SetSchedule(LiftRow l, bool peak, Domain.Design.DayHours? hours)
        {
            string problem = LiftScheduleChanged?.Invoke(l.Id, peak, hours);
            if (problem != null) Toast(problem);
        }

        void RideLiftCard()
        {
            if (_liftCardId == 0 || !_liftRows.TryGetValue(_liftCardId, out var l) || !l.Built) return;
            var id = _liftCardId;
            CloseLiftCard();
            LiftRideChosen?.Invoke(id);
        }

        /// <summary>R on an open card: ride its lift (true when it did).</summary>
        public bool RideOpenLiftCard()
        {
            if (_liftCardId == 0 || !_liftRows.TryGetValue(_liftCardId, out var l) || !l.Built) return false;
            if (_liftCardField?.parent != null || _liftCardPlateField?.parent != null) return false;
            RideLiftCard();
            return true;
        }

        // Task 4.11 PR B: the plate becomes a short text field, as the name does (Enter keeps it, Esc puts the old one back).
        void EditLiftCardPlate()
        {
            if (_liftCardId == 0 || LiftPlateChanged == null) return;
            if (_liftCardPlateField == null)
            {
                _liftCardPlateField = new TextField { name = "lift-card-plate-field", maxLength = Domain.Design.DesignRules.PlateMaxLength };
                _liftCardPlateField.AddToClassList("lift-card__plate-field");
                _liftCardPlateField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) { EndLiftCardPlate(true); e.StopPropagation(); }
                    else if (e.keyCode == UnityEngine.KeyCode.Escape) { EndLiftCardPlate(false); e.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                _liftCardPlateField.RegisterCallback<FocusOutEvent>(_ => EndLiftCardPlate(true));
            }
            if (_liftCardPlateField.parent != null) return;
            _liftCardPlateField.SetValueWithoutNotify(_liftCardPlate.text);
            InlineName.Open(_liftCardPlate, _liftCardPlateField);
            _liftCardPlateField.schedule.Execute(() => { _liftCardPlateField.Focus(); _liftCardPlateField.SelectAll(); });
        }

        void EndLiftCardPlate(bool keep)
        {
            if (_liftCardPlateField?.parent == null) return;
            string typed = _liftCardPlateField.value.Trim().ToUpperInvariant();
            _liftCardPlateField.RemoveFromHierarchy();
            _liftCardPlate.RemoveFromClassList("hidden");
            if (!keep || typed == _liftCardPlate.text || LiftPlateChanged == null) return;
            string problem = LiftPlateChanged(_liftCardId, typed);
            if (problem != null) Toast(problem);
            else _liftCardPlate.text = typed;
        }

        /// <summary>Types a plate into the card (tests).</summary>
        public void SetLiftCardPlateForTest(string plate)
        {
            EditLiftCardPlate();
            if (_liftCardPlateField == null) return;
            _liftCardPlateField.SetValueWithoutNotify(plate);
            EndLiftCardPlate(true);
        }

        // F6 (LT39): the card's name becomes a text field; Enter or a click elsewhere keeps it, Esc puts the old one back.
        void RenameLiftCard()
        {
            if (_liftCardId == 0 || LiftRenamed == null) return;
            if (_liftCardField == null)
            {
                _liftCardField = new TextField { name = "lift-card-name-field", maxLength = Domain.Design.DesignRules.NameMaxLength };
                _liftCardField.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.keyCode == UnityEngine.KeyCode.Return || e.keyCode == UnityEngine.KeyCode.KeypadEnter) { EndRenameLiftCard(true); e.StopPropagation(); }
                    else if (e.keyCode == UnityEngine.KeyCode.Escape) { EndRenameLiftCard(false); e.StopPropagation(); }
                }, TrickleDown.TrickleDown);
                _liftCardField.RegisterCallback<FocusOutEvent>(_ => EndRenameLiftCard(true));
            }
            if (_liftCardField.parent != null) return;
            _liftCardField.SetValueWithoutNotify(_liftCardName.text);
            InlineName.Open(_liftCardName, _liftCardField);
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
            EndLiftCardPlate(false);
            _liftCardId = 0;
            _liftsList?.SetFocused(0);
            _liftCard?.AddToClassList("hidden");
            LiftFocused?.Invoke(0);
            CardsChanged(true);   // task P3-10, NT11/NT12
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
        /// <summary>Opens the last listed lift's card (captures); false when the list is empty.</summary>
        public bool OpenLastLiftCardForCapture()
        {
            ulong last = 0;
            foreach (var id in _liftRows.Keys) last = id;
            if (last != 0) OpenLiftCard(last);
            return last != 0;
        }
        /// <summary>A column's menu open in Analysis › Lifts once its titles are laid out (-1 closes it): captures.</summary>
        public void OpenLiftsMenuForCapture(int menuColumn)
        {
            if (menuColumn < 0) { _liftsList.OpenMenuForCapture(-1); return; }
            _liftsList.Root.schedule.Execute(() => _liftsList.OpenMenuForCapture(menuColumn)).ExecuteLater(150);
        }
        /// <summary>Closes the lift card (captures).</summary>
        public void CloseLiftCardForCapture() => CloseLiftCard();
    }
}
