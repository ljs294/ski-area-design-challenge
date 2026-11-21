using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Hud
{
    /// <summary>
    /// Task 4.25 PR B: Analysis › Grooming's night strip (the approved mockup's grStrip), drawn with Painter2D as the trail
    /// profile is: one lane per groomer from the swing's start to the opening, each shift's window (dashed "No crew" when
    /// nobody takes it out), tonight's blocks along it, and what doesn't fit hung past the night's end in amber (red: not
    /// reached). Each block is an unseen child with its tooltip, so hovering it names the trail and raises
    /// <see cref="BlockHovered"/>; a groomer's name opens its card. Rebuilt only by <see cref="Set"/>.
    /// </summary>
    public sealed class GroomStrip : VisualElement
    {
        static readonly CustomStyleProperty<Color> ChartA = new CustomStyleProperty<Color>("--gs-a");
        static readonly CustomStyleProperty<Color> ChartBg = new CustomStyleProperty<Color>("--gs-bg");
        static readonly CustomStyleProperty<Color> Hair = new CustomStyleProperty<Color>("--gs-hair");
        static readonly CustomStyleProperty<Color> Warn = new CustomStyleProperty<Color>("--gs-warn");
        static readonly CustomStyleProperty<Color> Neg = new CustomStyleProperty<Color>("--gs-neg");
        static readonly CustomStyleProperty<Color> LabelInk = new CustomStyleProperty<Color>("--gs-label");

        public const float X0 = 76, RightPad = 84, Top = 19, LaneH = 19;   // room for "14.5 of 14.5 h" (round 3)
        Color _a = Color.gray, _bg = new Color(1, 1, 1, 0.02f), _hair = Color.gray, _warn = new Color(0.94f, 0.7f, 0.24f), _neg = Color.red, _label = Color.gray;
        GroomingTabModel _m;
        readonly List<(VisualElement Hit, GroomingTabModel.Block Block, int Lane)> _hits = new List<(VisualElement, GroomingTabModel.Block, int)>();
        readonly List<(Label Label, int Minute, int Lane, float Dy, bool End)> _placed = new List<(Label, int, int, float, bool)>();
        readonly List<(Label Label, float Minute)> _top = new List<(Label, float)>();

        /// <summary>A block under the pointer: its trail's row id (0 when the pointer leaves it).</summary>
        public event Action<ulong> BlockHovered;
        /// <summary>A groomer's name clicked: its number.</summary>
        public event Action<int> LaneClicked;
        /// <summary>The groomer whose card is open (its name in bold).</summary>
        public int Picked;

        public GroomStrip()
        {
            name = "grooming-strip-chart";
            AddToClassList("gr-strip");
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => Place());
            RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                e.customStyle.TryGetValue(ChartA, out _a);
                e.customStyle.TryGetValue(ChartBg, out _bg);
                e.customStyle.TryGetValue(Hair, out _hair);
                e.customStyle.TryGetValue(Warn, out _warn);
                e.customStyle.TryGetValue(Neg, out _neg);
                e.customStyle.TryGetValue(LabelInk, out _label);
                MarkDirtyRepaint();
            });
        }

        float Width => float.IsNaN(contentRect.width) || contentRect.width < 200 ? 520 : contentRect.width;
        float X(float minute) => X0 + minute / Mathf.Max(1, _m?.StripEnd ?? 900) * (Width - X0 - RightPad);
        int LaneCount => Math.Max(1, _m?.Lanes.Count ?? 0);
        float Bottom => Top + LaneCount * LaneH;

        /// <summary>The night to draw (labels and hit areas are made here, never per frame).</summary>
        public void Set(GroomingTabModel m)
        {
            _m = m;
            Clear();
            _hits.Clear();
            _placed.Clear();
            _top.Clear();
            style.height = Bottom + 15;
            if (m == null) { MarkDirtyRepaint(); return; }
            if (m.SwingLabel.Length > 0) _top.Add((Text(m.SwingLabel, "gr-strip__shift"), m.SwingEnd / 2f));
            _top.Add((Text(m.NightLabel, "gr-strip__shift"), (m.SwingEnd + m.NightEnd) / 2f));
            for (int i = 0; i < m.Lanes.Count; i++)
            {
                var lane = m.Lanes[i];
                int number = lane.Number;
                var name = new Label(lane.Label) { name = "grooming-lane-" + number, tooltip = "Its card" };
                name.AddToClassList("gr-strip__lane");
                name.EnableInClassList("gr-strip__lane--on", Picked == number);
                name.RegisterCallback<ClickEvent>(_ => LaneClicked?.Invoke(number));
                name.style.position = Position.Absolute;
                name.style.left = 0;
                name.style.top = Top + i * LaneH + 1;
                Add(name);
                var hours = Text(lane.Hours, "gr-strip__hours");
                _placed.Add((hours, -1, i, 1, true));
                if (!m.LastNight && !m.DayShift)
                {
                    if (!lane.Swing && m.SwingEnd > 0) _placed.Add((Text("No crew", "gr-strip__nocrew"), m.SwingEnd / 2, i, 2, false));
                    if (!lane.Night && m.NightEnd > m.SwingEnd) _placed.Add((Text("No crew", "gr-strip__nocrew"), (m.SwingEnd + m.NightEnd) / 2, i, 2, false));
                }
                else if (m.DayShift && !lane.Day) _placed.Add((Text("No crew", "gr-strip__nocrew"), m.NightEnd / 2, i, 2, false));
                foreach (var b in lane.Blocks)
                {
                    if (b.Kind == GroomBlockKind.Transit)
                    {
                        var drive = new VisualElement { tooltip = b.Tip, pickingMode = PickingMode.Position };
                        drive.AddToClassList("gr-strip__hit");
                        Add(drive);
                        _hits.Add((drive, b, i));
                        continue;
                    }
                    var hit = new VisualElement { tooltip = b.Tip, pickingMode = PickingMode.Position };
                    hit.AddToClassList("gr-strip__hit");
                    var block = b;
                    hit.RegisterCallback<PointerEnterEvent>(_ => BlockHovered?.Invoke(block.Row));
                    hit.RegisterCallback<PointerLeaveEvent>(_ => BlockHovered?.Invoke(0));
                    var word = new Label(b.Name) { pickingMode = PickingMode.Ignore };
                    word.AddToClassList("gr-strip__word");
                    word.EnableInClassList("gr-strip__word--bad", b.Kind == GroomBlockKind.Bad);
                    hit.Add(word);
                    Add(hit);
                    _hits.Add((hit, b, i));
                }
            }
            if (m.Lanes.Count == 0 && m.StripNote.Length > 0) _placed.Add((Text(m.StripNote, "gr-strip__note"), m.StripEnd / 2, 0, 1f, false));
            foreach (var t in m.Ticks) _placed.Add((Text(t.Label, "gr-strip__tick"), t.Minute, -1, 0, false));
            if (m.OpensLabel.Length > 0) _placed.Add((Text(m.OpensLabel, "gr-strip__tick"), m.Opens, -2, 0, true));
            Place();
            MarkDirtyRepaint();
        }

        Label Text(string text, string cls)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            l.style.position = Position.Absolute;
            Add(l);
            return l;
        }

        /// <summary>Places the labels and hit areas for the strip's width.</summary>
        void Place()
        {
            if (_m == null) return;
            float w = Width;
            foreach (var (label, minute) in _top)
            {
                label.style.left = X(minute) - 80;
                label.style.width = 160;
                label.style.top = 0;
            }
            foreach (var (label, minute, lane, dy, end) in _placed)
            {
                float y = lane >= 0 ? Top + lane * LaneH + dy : Bottom + 1;
                if (minute < 0) { label.style.left = StyleKeyword.Auto; label.style.right = 0; label.style.top = y; continue; }
                if (end) { label.style.left = StyleKeyword.Auto; label.style.right = Mathf.Max(0, w - X(minute)); label.style.top = y; continue; }
                label.style.left = X(minute) - 100;
                label.style.width = 200;
                label.style.top = y;
            }
            foreach (var (hit, b, lane) in _hits)
            {
                float xa = X(b.Start) + 0.6f, xb = X(Mathf.Min(b.End, _m.StripEnd));
                hit.style.position = Position.Absolute;
                hit.style.left = xa;
                hit.style.top = Top + lane * LaneH + 3.5f;
                hit.style.width = Mathf.Max(1.5f, xb - xa - 1.2f);
                hit.style.height = LaneH - 7;
            }
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext mgc)
        {
            if (_m == null) return;
            var p = mgc.painter2D;
            float w = Width, bottom = Bottom;
            void Rect(float x, float y, float rw, float rh, Color fill)
            {
                p.fillColor = fill;
                p.BeginPath();
                p.MoveTo(new Vector2(x, y)); p.LineTo(new Vector2(x + rw, y)); p.LineTo(new Vector2(x + rw, y + rh)); p.LineTo(new Vector2(x, y + rh));
                p.ClosePath();
                p.Fill();
            }
            void Line(float x0, float y0, float x1, float y1, Color c, float width, float dash = 0)
            {
                p.strokeColor = c;
                p.lineWidth = width;
                if (dash <= 0) { p.BeginPath(); p.MoveTo(new Vector2(x0, y0)); p.LineTo(new Vector2(x1, y1)); p.Stroke(); return; }
                float len = Vector2.Distance(new Vector2(x0, y0), new Vector2(x1, y1));
                for (float t = 0; t < len; t += dash * 2)
                {
                    float a = t / len, b = Mathf.Min(len, t + dash) / len;
                    p.BeginPath();
                    p.MoveTo(Vector2.Lerp(new Vector2(x0, y0), new Vector2(x1, y1), a));
                    p.LineTo(Vector2.Lerp(new Vector2(x0, y0), new Vector2(x1, y1), b));
                    p.Stroke();
                }
            }
            void Box(float x, float y, float rw, float rh, Color stroke, bool dashed)
            {
                float d = dashed ? 2 : 0;
                Line(x, y, x + rw, y, stroke, 1, d); Line(x + rw, y, x + rw, y + rh, stroke, 1, d);
                Line(x + rw, y + rh, x, y + rh, stroke, 1, d); Line(x, y + rh, x, y, stroke, 1, d);
            }
            // Past the night's end: hatched, where what doesn't fit hangs.
            float xe = X(_m.NightEnd), xh = X(_m.StripEnd);
            for (float x = xe - (bottom - Top); x < xh; x += 4)
            {
                float a = Mathf.Max(x, xe), b = Mathf.Min(x + (bottom - Top), xh);
                if (b <= a) continue;
                Line(a, Top + (a - x), b, Top + (b - x), _hair, 1);
            }
            for (int i = 0; i < _m.Lanes.Count; i++)
            {
                var lane = _m.Lanes[i];
                float y = Top + i * LaneH;
                if (_m.DayShift) Window(0, _m.NightEnd, lane.Day || _m.LastNight);
                else
                {
                    if (_m.SwingEnd > 0) Window(0, _m.SwingEnd, lane.Swing);
                    if (_m.NightEnd > _m.SwingEnd) Window(_m.SwingEnd, _m.NightEnd, lane.Night);
                }
                void Window(int from, int to, bool on)
                {
                    float xa = X(from) + 0.5f, rw = X(to) - X(from) - 1;
                    if (on) Rect(xa, y + 2.5f, rw, LaneH - 5, _bg);
                    Box(xa, y + 2.5f, rw, LaneH - 5, _hair, !on);
                }
                foreach (var b in lane.Blocks)
                {
                    float xa = X(b.Start) + 0.6f, rw = Mathf.Max(1.5f, X(Mathf.Min(b.End, _m.StripEnd)) - X(b.Start) - 1.2f);
                    if (b.Kind == GroomBlockKind.Transit)
                    {
                        // GO-15: a drive is a thin bar along the lane's middle, in the labels' ink
                        Color t = _label; t.a *= 0.75f;
                        Rect(xa, y + LaneH / 2f - 2f, rw, 4f, t);
                        continue;
                    }
                    Color c = b.Kind == GroomBlockKind.Bad ? _neg : b.Kind == GroomBlockKind.Warn ? _warn : _a;
                    c.a *= b.Kind == GroomBlockKind.Till ? 0.62f : 0.92f;
                    Rect(xa, y + 3.5f, rw, LaneH - 7, c);
                }
            }
            if (!_m.DayShift && _m.SwingEnd > 0) Line(X(_m.SwingEnd), Top - 3, X(_m.SwingEnd), bottom + 2, _label, 0.8f);
            Line(xe, Top - 3, xe, bottom + 2, _label, 0.8f);
            if (!_m.DayShift) Line(X(_m.Opens), Top - 3, X(_m.Opens), bottom + 2, _label, 0.8f, 2);
            _ = w;
        }
    }
}
