using System;
using System.Collections.Generic;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// A construction tool's window (P3-06), the mockup's lift and trail menus: the title and the design's name, the
    /// tool's options, its numbered steps, the step's hint, messages and keys, the review figures, then Next, Break ground
    /// and Save plan. It lives in a <see cref="HudWindow"/>; the review is its last step, so the window never jumps.
    /// Every control is a button or slider the arrows can reach once the window has the keyboard (Tab, P3-06 Q7).
    /// </summary>
    public sealed class ToolWindow
    {
        public readonly HudWindow Window;
        readonly Label _name, _ask, _hint, _msg;
        readonly VisualElement _opts, _steps, _keys, _kv, _btns, _acts, _msgs, _extra;
        string _extraKey;
        readonly Button _next, _go, _plan;
        readonly Label _goKbd, _nextKbd, _goLabel;
        int _reviewVersion = -1;
        ReviewModel _review;

        public event Action NextClicked, BreakGroundClicked, SavePlanClicked;

        public ToolWindow(string id, string title, string glyph)
        {
            Window = new HudWindow(title) { name = "toolwin" };
            Window.AddToClassList("tw");
            Window.Head.name = "toolwin-head";
            Window.Q<Button>("window-x").name = "toolwin-x";
            if (glyph != null)
            {
                var icon = new HudIcon(glyph, null) { pickingMode = PickingMode.Ignore };
                icon.AddToClassList("tw__icon");
                Window.Head.Insert(0, icon);
            }
            _name = new Label { name = "toolwin-name", pickingMode = PickingMode.Ignore };
            _name.AddToClassList("tw__name");
            Window.Head.Insert(Window.Head.IndexOf(Window.Title) + 1, _name);
            _ask = new Label { name = "toolwin-ask", pickingMode = PickingMode.Ignore };
            _ask.AddToClassList("tw__ask");
            _ask.AddToClassList("hidden");
            Window.Head.Insert(Window.Head.IndexOf(_name) + 1, _ask);

            // Everything but the commit buttons scrolls when the window is taller than the room above the bar (150% on a
            // small screen); Break ground and Save plan stay pinned at the bottom.
            _scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                name = "toolwin-scroll",
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.Hidden,   // the wheel scrolls it; focus scrolls to itself
            };
            _scroll.RegisterCallback<FocusInEvent>(e => { if (e.target is VisualElement f) _scroll.ScrollTo(f); });
            _scroll.AddToClassList("tw__scroll");
            Window.Body.Add(_scroll);
            var body = _scroll.contentContainer;
            body.Add(_opts = Part("toolwin-opts", "tw__opts"));
            body.Add(Part(null, "tw__rule"));
            body.Add(_steps = Part("toolwin-steps", "tw-steps"));
            body.Add(Part(null, "tw__rule"));
            _hint = new Label { name = "toolwin-hint" };
            _hint.AddToClassList("tw-hint");
            body.Add(_hint);
            body.Add(_msgs = Part("toolwin-msgs", null));
            _msg = new Label { name = "toolwin-msg" };
            _msg.AddToClassList("tw-msg");
            _msg.AddToClassList("hidden");
            _msgs.Add(_msg);
            body.Add(_gauge = new LiftLoadGauge());
            _gauge.Show(null);
            body.Add(_kv = Part("toolwin-review", "tw-kv"));
            body.Add(_keys = Part("toolwin-keys", "tw-keys"));
            body.Add(_extra = Part("toolwin-extra", "tw-btns"));
            _extra.AddToClassList("hidden");
            body.Add(_btns = Part("toolwin-btns", "tw-btns"));
            _next = new Button(() => NextClicked?.Invoke()) { name = "toolwin-next" };
            _next.AddToClassList("mp-ghost");
            _next.Add(new Label("Next") { pickingMode = PickingMode.Ignore });
            _nextKbd = Kbd(KeyBindings.Caption(GameAction.ToolNext));
            _nextKbd.AddToClassList("tw-next__kbd");
            _next.Add(_nextKbd);
            _btns.Add(_next);

            Window.Body.Add(_acts = Part("toolwin-acts", "tw-acts"));
            _go = new Button(() => BreakGroundClicked?.Invoke()) { name = "review-go" };
            _go.AddToClassList("mp-go");
            var goLabel = _goLabel = new Label("Break ground") { pickingMode = PickingMode.Ignore };
            goLabel.AddToClassList("tw-go__label");
            _go.Add(goLabel);
            _goKbd = new Label(KeyBindings.Caption(GameAction.BreakGround)) { pickingMode = PickingMode.Ignore };
            _goKbd.AddToClassList("tw-go__kbd");
            _go.Add(_goKbd);
            _plan = new Button(() => SavePlanClicked?.Invoke()) { name = "review-plan", text = "Save plan" };
            _plan.AddToClassList("mp-ghost");
            _acts.Add(_go);
            _acts.Add(_plan);
            SetCommit(false, false);
            _kv.AddToClassList("hidden");
            _keys.AddToClassList("hidden");

            Window.RegisterCallback<GeometryChangedEvent>(_ => Fit());
            KeyBindings.Changed += OnKeysChanged;
            Window.RegisterCallback<DetachFromPanelEvent>(_ => KeyBindings.Changed -= OnKeysChanged);
            Window.RegisterCallback<AttachToPanelEvent>(_ => { KeyBindings.Changed -= OnKeysChanged; KeyBindings.Changed += OnKeysChanged; });
        }

        readonly ScrollView _scroll;

        /// <summary>
        /// Keeps the window clear of the HUD: the first time it lays out, it moves beside the Toolbox tray if it would
        /// cover it (and there's room); it never reaches into the status bar, scrolling its steps instead.
        /// </summary>
        void Fit()
        {
            var stage = Window.parent;
            if (stage == null) return;
            var tray = stage.Q("toolbox");
            var bar = stage.Q("bar");
            float barTop = bar != null ? bar.layout.yMin : stage.layout.height;
            var w = Window.layout;
            if (!Window.MovedByHand && w.height > 0)
            {
                bool trayShown = tray != null && tray.resolvedStyle.display == DisplayStyle.Flex;
                bool overTray = trayShown && w.xMin < tray.layout.xMax && w.yMax > tray.layout.yMin - 10;
                if (overTray && tray.layout.xMax + 10 + w.width + 10 <= stage.layout.width)
                {
                    Window.style.left = tray.layout.xMax + 10;
                    // Below the top-right buttons (sketch, layers, menu: 46 px tall at 10 px) when it reaches under them.
                    if (tray.layout.xMax + 10 + w.width > stage.layout.width - 170) Window.style.top = 66;
                }
            }
            // The profile strip (task 09) along the bottom is a floor too where the window is over it.
            if (_strip != null && _strip.parent == stage && _strip.resolvedStyle.display == DisplayStyle.Flex)
            {
                var sl = _strip.layout;
                if (sl.height > 0 && w.xMax > sl.xMin && w.xMin < sl.xMax) barTop = Mathf.Min(barTop, sl.yMin);
            }
            float room = Mathf.Max(120, barTop - 10 - w.yMin);
            if (Math.Abs(Window.resolvedStyle.maxHeight.value - room) > 0.5f) Window.style.maxHeight = room;
        }

        static VisualElement Part(string name, string cls)
        {
            var e = new VisualElement();
            if (name != null) e.name = name;
            if (cls != null) e.AddToClassList(cls);
            return e;
        }

        static Label Kbd(string caption)
        {
            var k = new Label(caption) { pickingMode = PickingMode.Ignore };
            k.AddToClassList("kbd");
            return k;
        }

        void OnKeysChanged()
        {
            _nextKbd.text = KeyBindings.Caption(GameAction.ToolNext);
            _goKbd.text = KeyBindings.Caption(GameAction.BreakGround);
            if (_keyItems != null) SetKeys(_keyItems);
        }

        // ---------- the head ----------

        /// <summary>The design's name after the title ("Summit Quad").</summary>
        public void SetName(string name) => _name.text = name ?? "";

        /// <summary>"Discard it? ✕ again" while a second Esc or ✕ would throw drawn work away (CF-07).</summary>
        public void SetAsking(string text)
        {
            _ask.text = text ?? "";
            _ask.EnableInClassList("hidden", string.IsNullOrEmpty(text));
        }

        // ---------- options ----------

        /// <summary>A segmented switch row (Type, Towers, Grading). Returns a setter that moves the chosen option.</summary>
        public Action<int> AddSwitch(string name, string label, string[] options, int chosen, Action<int> picked, string tooltip = null)
        {
            var row = Part("toolwin-opt-" + name, "tw-row");
            var l = new Label(label) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-row__label");
            row.Add(l);
            var seg = Part(null, "mp-seg");
            if (tooltip != null) seg.tooltip = tooltip;
            var buttons = new Button[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                int at = i;
                var b = new Button(() => picked(at)) { name = $"toolwin-opt-{name}-{i}", text = options[i] };
                b.AddToClassList("mp-seg__opt");
                if (i == 0) b.AddToClassList("mp-seg__opt--first");
                buttons[i] = b;
                seg.Add(b);
            }
            row.Add(seg);
            _opts.Add(row);
            void Set(int on)
            {
                for (int i = 0; i < buttons.Length; i++) buttons[i].EnableInClassList("mp-seg__opt--on", i == on);
            }
            Set(chosen);
            return Set;
        }

        readonly List<ToolDropdown> _dropdowns = new List<ToolDropdown>();
        readonly Dictionary<string, Label> _rowLabels = new Dictionary<string, Label>();

        /// <summary>A dropdown row (the lift's Type, Chair and Model; task 09). <paramref name="picked"/> gets the option's index.</summary>
        public ToolDropdown AddDropdown(string name, string label, Action<int> picked, string tooltip = null)
        {
            var row = Part("toolwin-opt-" + name, "tw-row");
            var l = new Label(label) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-row__label");
            row.Add(l);
            _rowLabels[name] = l;
            var dd = Dropdown(name, false, picked, tooltip);
            dd.Button.AddToClassList("tw-dd--grow");
            row.Add(dd.Button);
            _opts.Add(row);
            return dd;
        }

        /// <summary>A quieter row of two dropdowns with a dot between (the lift's Towers: Automatic · Plumb).</summary>
        public (ToolDropdown First, ToolDropdown Second) AddDropdownPair(string name, string label, string first, Action<int> pickedFirst,
                                                                          string second, Action<int> pickedSecond, string tipFirst = null, string tipSecond = null)
        {
            var row = Part("toolwin-opt-" + name, "tw-row");
            row.AddToClassList("tw-row--pair");
            var l = new Label(label) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-row__label");
            row.Add(l);
            var a = Dropdown(first, true, pickedFirst, tipFirst);
            var b = Dropdown(second, true, pickedSecond, tipSecond);
            var dot = new Label("·") { pickingMode = PickingMode.Ignore };
            dot.AddToClassList("tw-row__dot");
            row.Add(a.Button);
            row.Add(dot);
            row.Add(b.Button);
            _opts.Add(row);
            return (a, b);
        }

        ToolDropdown Dropdown(string name, bool quiet, Action<int> picked, string tooltip)
        {
            var dd = new ToolDropdown(name, Window, quiet, picked);
            if (tooltip != null) dd.Button.tooltip = tooltip;
            dd.Opened += opening => { foreach (var other in _dropdowns) if (other != opening) other.Close(); };
            _dropdowns.Add(dd);
            return dd;
        }

        /// <summary>Renames an option row's label (the lift's Chair row reads Cabin for a gondola or tram).</summary>
        public void SetRowLabel(string name, string label)
        {
            if (_rowLabels.TryGetValue(name, out var l)) l.text = label;
        }

        // ---------- the lift profile strip (task 09, LF-10) ----------

        LiftProfileStrip _strip;
        LiftLoadGauge _gauge;

        /// <summary>A tower's load gauge under the hint (TW-11, task 09), or none.</summary>
        public void ShowGauge(Domain.Design.TowerForces.Loads? loads, bool provisional = false) => _gauge.Show(loads, provisional);

        /// <summary>
        /// Shows the profile strip beside the Toolbox tray along the bottom (made on first use, gone with the window), with
        /// <paramref name="profile"/>, or its <paramref name="hint"/> while there's no line yet.
        /// </summary>
        public LiftProfileStrip ShowStrip(LiftProfile profile, string hint = null)
        {
            if (_strip == null)
            {
                _strip = new LiftProfileStrip();
                Window.RegisterCallback<DetachFromPanelEvent>(_ => _strip?.RemoveFromHierarchy());
            }
            if (_strip.parent == null && Window.parent != null)
            {
                Window.parent.Add(_strip);
                Window.parent.RegisterCallback<GeometryChangedEvent>(_ => PlaceStrip());
                _strip.schedule.Execute(PlaceStrip);
            }
            _strip.style.display = DisplayStyle.Flex;
            _strip.Show(profile, hint);
            PlaceStrip();
            Window.schedule.Execute(Fit);
            return _strip;
        }

        public void HideStrip()
        {
            if (_strip == null || _strip.style.display == DisplayStyle.None) return;
            _strip.style.display = DisplayStyle.None;
            Window.schedule.Execute(Fit);
        }

        public LiftProfileStrip Strip => _strip;

        // Beside the tray, its bottom on the tray's, to the stage's right edge (the mockup's liftStrip).
        void PlaceStrip()
        {
            var stage = _strip?.parent;
            if (stage == null) return;
            var tray = stage.Q("toolbox");
            var bar = stage.Q("bar");
            float stageW = stage.layout.width;
            if (float.IsNaN(stageW) || stageW <= 0) return;
            bool trayShown = tray != null && tray.resolvedStyle.display != DisplayStyle.None && !tray.ClassListContains("hidden") && tray.layout.width > 0;
            float left = trayShown ? tray.layout.xMax + 10 : 10;
            float bottom = trayShown ? tray.layout.yMax : (bar != null ? bar.layout.yMin - 10 : stage.layout.height - 10);
            const float height = 30 + 134 + 2;
            _strip.style.left = left;
            _strip.style.top = bottom - height;
            _strip.style.width = Mathf.Max(200, stageW - left - 10);
            _strip.style.height = height;
        }

        /// <summary>Esc closes an open dropdown first (true if one was open).</summary>
        public bool CloseMenus()
        {
            bool any = false;
            foreach (var d in _dropdowns) any |= d.Close();
            return any;
        }

        /// <summary>A slider row (the brush). Returns a setter for the value and its caption.</summary>
        public Action<int, string> AddSlider(string name, string label, int min, int max, int step, int value, Action<int> moved, string tooltip = null)
        {
            var row = Part("toolwin-opt-" + name, "tw-row");
            if (tooltip != null) row.tooltip = tooltip;
            var l = new Label(label) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-row__label");
            row.Add(l);
            var slider = new SliderInt(min, max) { name = "toolwin-opt-" + name + "-slider", pageSize = step, fill = true, value = value };
            slider.AddToClassList("tw-slider");
            var caption = new Label { name = "toolwin-opt-" + name + "-value", pickingMode = PickingMode.Ignore };
            caption.AddToClassList("tw-row__value");
            caption.AddToClassList("mono");
            bool quiet = false;
            slider.RegisterValueChangedCallback(e =>
            {
                if (quiet) return;
                int snapped = min + (int)Math.Round((e.newValue - min) / (double)step) * step;
                moved(Mathf.Clamp(snapped, min, max));
            });
            row.Add(slider);
            row.Add(caption);
            _opts.Add(row);
            return (v, text) =>
            {
                quiet = true;
                slider.SetValueWithoutNotify(v);
                quiet = false;
                caption.text = text;
            };
        }

        /// <summary>A text row (the trail's name, TF-01). <paramref name="changed"/> runs when the field loses focus or Enter.</summary>
        public TextField AddText(string name, string label, string value, int maxLength, Action<string> changed)
        {
            var row = Part("toolwin-opt-" + name, "tw-row");
            var l = new Label(label) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-row__label");
            row.Add(l);
            var field = new TextField { name = "toolwin-opt-" + name + "-field", value = value, maxLength = maxLength, isDelayed = true };
            field.AddToClassList("tw-field");
            field.RegisterValueChangedCallback(e => changed(e.newValue));
            row.Add(field);
            _opts.Add(row);
            return field;
        }

        /// <summary>A line of small print under the options ("2,400 an hour · 450 ft/min").</summary>
        public Label AddNote(string name, string text, string tooltip = null)
        {
            var n = new Label(text) { name = "toolwin-note-" + name, tooltip = tooltip };
            n.AddToClassList("tw-note");
            n.AddToClassList("mono");
            _opts.Add(n);
            return n;
        }

        // ---------- steps ----------

        readonly List<VisualElement> _stepItems = new List<VisualElement>();
        readonly List<Label> _stepNumbers = new List<Label>();

        public void SetSteps(string[] names)
        {
            _steps.Clear();
            _stepItems.Clear();
            _stepNumbers.Clear();
            _steps.EnableInClassList("hidden", names.Length == 0);   // Edit lift has no steps
            for (int i = 0; i < names.Length; i++)
            {
                var s = Part("toolwin-step-" + (i + 1), "tw-step");
                var n = new Label((i + 1).ToString()) { pickingMode = PickingMode.Ignore };
                n.AddToClassList("tw-step__n");
                n.AddToClassList("mono");
                var l = new Label(names[i]) { pickingMode = PickingMode.Ignore };
                l.AddToClassList("tw-step__label");
                s.Add(n);
                s.Add(l);
                _steps.Add(s);
                _stepItems.Add(s);
                _stepNumbers.Add(n);
            }
        }

        int _step = -1;

        /// <summary>Marks the current step (0-based): earlier ones done with a ✓.</summary>
        public void SetStep(int current)
        {
            if (current == _step) return;
            _step = current;
            for (int i = 0; i < _stepItems.Count; i++)
            {
                _stepItems[i].EnableInClassList("tw-step--now", i == current);
                _stepItems[i].EnableInClassList("tw-step--done", i < current);
                _stepNumbers[i].text = i < current ? "✓" : (i + 1).ToString();
            }
        }

        public int Step => _step;

        // ---------- the step's text ----------

        public void SetHint(string text)
        {
            if (_hint.text != text) _hint.text = text ?? "";
            _hint.EnableInClassList("hidden", string.IsNullOrEmpty(text));
        }

        /// <summary>One message under the hint: red (can't) or amber (a caution); null hides it.</summary>
        public void SetMessage(string text, bool warn = false)
        {
            bool show = !string.IsNullOrEmpty(text);
            if (show && _msg.text != text) _msg.text = text;
            _msg.EnableInClassList("hidden", !show);
            _msg.EnableInClassList("tw-msg--warn", warn);
        }

        public string Message => _msg.ClassListContains("hidden") ? null : _msg.text;

        (GameAction Action, string What)[] _keyItems;

        /// <summary>The step's keys, each caption read from <see cref="KeyBindings"/> (SH-07). Empty hides the line.</summary>
        public void SetKeys(params (GameAction Action, string What)[] items)
        {
            _keyItems = items;
            _keys.Clear();
            foreach (var (action, what) in items)
            {
                var item = Part(null, "tw-keys__item");
                item.Add(Kbd(KeyBindings.Caption(action)));
                var t = new Label(what) { pickingMode = PickingMode.Ignore };
                t.AddToClassList("tw-keys__text");
                item.Add(t);
                _keys.Add(item);
            }
            _keys.EnableInClassList("hidden", items.Length == 0);
        }

        // ---------- review and buttons ----------

        /// <summary>Shows the review figures and warnings (redrawn only when the model changed), or hides them (null).</summary>
        public void ShowReview(ReviewModel review)
        {
            _kv.EnableInClassList("hidden", review == null);
            if (review == null) { _review = null; _reviewVersion = -1; return; }
            if (review == _review && review.Version == _reviewVersion) return;
            _review = review;
            _reviewVersion = review.Version;
            _kv.Clear();
            foreach (var r in review.Rows)
            {
                var row = Part(r.Slot != null ? "review-" + r.Slot : "review-row", "tw-kv__row");
                if (r.Rule != null) row.tooltip = r.Rule;
                var k = new Label(r.Label) { pickingMode = PickingMode.Ignore };
                k.AddToClassList("tw-kv__k");
                var v = new Label(r.Value) { pickingMode = PickingMode.Ignore };
                v.AddToClassList("tw-kv__v");
                v.AddToClassList("mono");
                v.EnableInClassList("tw-kv__v--pending", r.Pending);
                row.Add(k);
                row.Add(v);
                _kv.Add(row);
            }
            // Warnings sit under the figures: ✕ blocks Break ground, ! is a caution.
            for (int i = _msgs.childCount - 1; i >= 1; i--) _msgs.RemoveAt(i);
            foreach (var w in review.Warnings)
            {
                var m = new Label((w.Blocks ? "✕ " : "! ") + w.Text) { tooltip = w.Rule };
                m.AddToClassList("tw-msg");
                m.EnableInClassList("tw-msg--warn", !w.Blocks);
                _msgs.Add(m);
            }
            SetCommit(review.CanBreakGround, review.CanSavePlan);
        }

        /// <summary>
        /// A row of the tool's own buttons over Next (the lift's Fill the rest, Re-place towers, Review; task 09), rebuilt only
        /// when their names or captions change; none hides the row. A null click greys one.
        /// </summary>
        public void SetButtons(params (string Name, string Text, Action Click)[] buttons)
        {
            var key = new System.Text.StringBuilder();
            foreach (var b in buttons) key.Append(b.Name).Append('|').Append(b.Text).Append('|').Append(b.Click == null ? '0' : '1').Append(';');
            string k = key.ToString();
            if (k == _extraKey) return;
            _extraKey = k;
            _extra.Clear();
            _extra.EnableInClassList("hidden", buttons.Length == 0);
            foreach (var b in buttons)
            {
                var click = b.Click;
                var button = new Button(() => click?.Invoke()) { name = "toolwin-btn-" + b.Name };
                button.AddToClassList("mp-ghost");
                button.Add(new Label(b.Text) { pickingMode = PickingMode.Ignore });
                button.SetEnabled(click != null);
                _extra.Add(button);
            }
        }

        /// <summary>The Next button (with its key): shown before the review step, live once the step is done.</summary>
        public void SetNext(bool shown, bool live)
        {
            _btns.EnableInClassList("hidden", !shown);
            _next.SetEnabled(live);
        }

        public void SetCommit(bool breakGround, bool savePlan)
        {
            _go.SetEnabled(breakGround);
            _plan.SetEnabled(savePlan || _planAlways);
        }

        /// <summary>Renames the commit buttons (Edit lift: Apply and Cancel; task 09, LT2).</summary>
        public void SetCommitText(string go, string plan)
        {
            _goLabel.text = go;
            _plan.text = plan;
        }

        /// <summary>Whether the second commit button takes clicks even with nothing to commit (Edit lift's Cancel).</summary>
        public void SetPlanAlwaysLive(bool on) => _planAlways = on;
        bool _planAlways;

        public bool CanBreakGround => _go.enabledSelf;
        public bool CanSavePlan => _plan.enabledSelf;

        /// <summary>For keyboard use (Tab): the first control the arrows start from.</summary>
        public Focusable FirstControl => _opts.Q<Button>() ?? (Focusable)_next;
    }
}
