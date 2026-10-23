using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The lift profile strip (LF-10, task 09; the approved mockup's .lstrip), along the bottom beside the Toolbox tray while
    /// a lift is placed or edited. Its header has the line's figures, Fit / True scale and the rise bar (how much of the
    /// type's allowed rise the line uses, LT13). Under it, the side view: the ground, the loaded rope span by span (red where
    /// its chairs dip under 15 ft), the towers with their heads and heights, the terminals, the green band where the tower in
    /// hand could stand and the tower in hand; and the chairs' height over the ground against the dashed 15 ft line (TW-01),
    /// the load and unload zones hatched, the lowest point marked. Drawn with the vector API from a <see cref="LiftProfile"/>;
    /// labels are pooled and placed only when the profile, the width or the scale changes.
    /// </summary>
    public sealed class LiftProfileStrip : VisualElement
    {
        const float PadL = 58, PadR = 14, Top = 8, Base = 72, T0 = 82, T1 = 118, ChartHeight = 134;
        const float TrackMax = 18.288f;   // 60 ft
        const float Fifteen = 4.572f;     // TW-01: 15 ft

        readonly VisualElement _chart, _labels, _riseBar;
        readonly Label _info, _riseValue, _riseOf, _empty;
        readonly Button _fit, _true;
        readonly List<Label> _pool = new List<Label>();
        readonly Dictionary<string, VisualElement> _inks = new Dictionary<string, VisualElement>();
        LiftProfile _profile;
        int _drawnVersion = -1;
        float _drawnWidth = -1;
        bool _drawnTrue;
        static bool _trueScale;

        public LiftProfile Profile => _profile;

        public LiftProfileStrip()
        {
            name = "liftstrip";
            AddToClassList("tw-strip");
            tooltip = "LF-10: the side view and the chairs' height over the ground. The band is where the tower in hand could stand at its height (TW-09); the dashed line is the chairs' 15 ft over the ground (TW-01); hatched ends are the load and unload zones.";
            var head = new VisualElement { name = "liftstrip-head" };
            head.AddToClassList("tw-strip__head");
            var title = new Label("Profile") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("tw-strip__title");
            head.Add(title);
            _info = new Label { name = "liftstrip-info", pickingMode = PickingMode.Ignore };
            _info.AddToClassList("tw-strip__info");
            _info.AddToClassList("mono");
            head.Add(_info);
            var seg = new VisualElement();
            seg.AddToClassList("mp-seg");
            seg.AddToClassList("tw-strip__seg");
            seg.AddToClassList("mp-seg--mini");
            seg.tooltip = "LF-10: Fit stretches the profile to the strip; True scale draws it 1:1";
            _fit = new Button(() => SetTrue(false)) { name = "liftstrip-fit", text = "Fit" };
            _true = new Button(() => SetTrue(true)) { name = "liftstrip-true", text = "True scale" };
            foreach (var b in new[] { _fit, _true }) { b.AddToClassList("mp-seg__opt"); seg.Add(b); }
            _fit.AddToClassList("mp-seg__opt--first");
            head.Add(seg);
            var rise = new VisualElement { name = "liftstrip-rise" };
            rise.AddToClassList("tw-strip__rise");
            var riseLabel = new Label("Rise") { pickingMode = PickingMode.Ignore };
            riseLabel.AddToClassList("tw-strip__rise-label");
            rise.Add(riseLabel);
            _riseBar = new VisualElement { pickingMode = PickingMode.Ignore };
            _riseBar.AddToClassList("tw-strip__bar");
            _riseBar.generateVisualContent += DrawRise;
            rise.Add(_riseBar);
            _riseValue = new Label { pickingMode = PickingMode.Ignore };
            _riseValue.AddToClassList("tw-strip__rise-value");
            _riseValue.AddToClassList("mono");
            rise.Add(_riseValue);
            _riseOf = new Label { pickingMode = PickingMode.Ignore };
            _riseOf.AddToClassList("tw-strip__info");
            _riseOf.AddToClassList("mono");
            rise.Add(_riseOf);
            head.Add(rise);
            Add(head);
            _chart = new VisualElement { name = "liftstrip-chart", pickingMode = PickingMode.Ignore };
            _chart.AddToClassList("tw-strip__chart");
            _chart.style.height = ChartHeight;
            _chart.generateVisualContent += Draw;
            _labels = new VisualElement { pickingMode = PickingMode.Ignore };
            _labels.AddToClassList("tw-strip__labels");
            _chart.Add(_labels);
            _empty = new Label { name = "liftstrip-empty", pickingMode = PickingMode.Ignore };
            _empty.AddToClassList("tw-strip__empty");
            _chart.Add(_empty);
            Add(_chart);
            foreach (string ink in new[] { "chart-fill", "chart-a", "chart-b", "chart-label", "chart-bg", "row", "neg", "strong", "soft", "text", "hair2", "chip", "pos" })
            {
                var swatch = new VisualElement { pickingMode = PickingMode.Ignore };
                swatch.AddToClassList("tw-strip__ink");
                swatch.AddToClassList("tw-strip__ink--" + ink);
                Add(swatch);
                _inks[ink] = swatch;
            }
            _chart.RegisterCallback<GeometryChangedEvent>(_ => { _chart.MarkDirtyRepaint(); Relabel(); });
            SetTrue(_trueScale);
            Show(null, "Anchor a terminal, then aim at the other end: the line shows here from the side.");
        }

        Color Ink(string name) => _inks.TryGetValue(name, out var e) ? e.resolvedStyle.backgroundColor : Color.magenta;

        static bool Imperial => DisplayUnits.Current == UnitSystem.Imperial;

        void SetTrue(bool on)
        {
            _trueScale = on;
            _fit.EnableInClassList("mp-seg__opt--on", !on);
            _true.EnableInClassList("mp-seg__opt--on", on);
            _chart.MarkDirtyRepaint();
            Relabel();
        }

        /// <summary>Shows a profile, or (null) the <paramref name="hint"/> in its place.</summary>
        public void Show(LiftProfile profile, string hint = null)
        {
            _profile = profile;
            _empty.text = hint ?? "";
            _empty.EnableInClassList("hidden", profile != null);
            if (profile == null)
            {
                _info.text = "";
                _riseValue.text = _riseOf.text = "";
            }
            else
            {
                double rise = profile.Rise;
                _info.text = $"{ToolFormat.Length(profile.Length)} · rise {ToolFormat.Length(rise)} · {ToolFormat.Grade(rise, profile.Length)} · {profile.Towers} tower{(profile.Towers == 1 ? "" : "s")}";
                bool over = !double.IsInfinity(profile.RiseLimit) && rise > profile.RiseLimit;
                _riseValue.text = ToolFormat.Length(rise);
                _riseValue.EnableInClassList("tw-strip__rise-value--over", over);
                _riseOf.text = double.IsInfinity(profile.RiseLimit) ? "no limit" : "of " + ToolFormat.Length(profile.RiseLimit);
            }
            _drawnVersion = -1;
            _chart.MarkDirtyRepaint();
            _riseBar.MarkDirtyRepaint();
            Relabel();
        }

        /// <summary>The tower in hand moved or changed (no new profile): only the drawing changes.</summary>
        public void Refresh() => _chart.MarkDirtyRepaint();

        // ---------- the scales ----------

        float _fx, _fy, _zl;
        float W => _chart.contentRect.width;

        bool Scale()
        {
            var p = _profile;
            if (p == null || float.IsNaN(W) || W < PadL + PadR + 10) return false;
            float zl = float.MaxValue, zh = float.MinValue;
            for (int i = 0; i < p.Ground.Length; i++) { zl = Mathf.Min(zl, p.Ground[i]); zh = Mathf.Max(zh, p.Ground[i]); }
            foreach (var t in p.Rope.Towers) zh = Mathf.Max(zh, (float)t.RopeY);
            zh = Mathf.Max(zh, (float)Math.Max(p.BottomZ, p.TopZ));
            _zl = zl;
            _fx = (W - PadL - PadR) / (float)p.Length;
            _fy = (Base - Top) / Mathf.Max(1, zh - zl);
            if (_trueScale) _fx = _fy = Mathf.Min(_fx, _fy);
            return true;
        }

        float X(double s) => PadL + (float)s * _fx;
        float Y(double z) => Base - ((float)z - _zl) * _fy;
        static float CY(double c) => T1 - Mathf.Clamp((float)c, 0, TrackMax) / TrackMax * (T1 - T0);

        // ---------- drawing ----------

        void Draw(MeshGenerationContext ctx)
        {
            var p = _profile;
            if (!Scale()) return;
            var g = ctx.painter2D;
            float right = W - PadR;
            Color row = Ink("row"), fill = Ink("chart-fill"), lineA = Ink("chart-a"), lineB = Ink("chart-b"), strong = Ink("strong"),
                  neg = Ink("neg"), soft = Ink("soft"), text = Ink("text"), hair = Ink("hair2"), bg = Ink("chart-bg");

            // Elevation grid.
            float gstep = Imperial ? ((Base - Top) / _fy > 183 ? 60.96f : 30.48f) : 50f;
            g.lineWidth = 1;
            g.strokeColor = row;
            for (float z = Mathf.Ceil(_zl / gstep) * gstep; Y(z) >= Top - 0.5f; z += gstep) Seg(g, PadL, Y(z), right, Y(z));

            // The green band (TW-09) where the tower in hand could stand.
            var bandFill = text;
            bandFill.a *= 0.07f;
            foreach (var (a, b) in p.Band)
            {
                Rect(g, X(a), 2, Mathf.Max(2, X(b) - X(a)), T1 - 2, bandFill);
                Rect(g, X(a), 2, Mathf.Max(2, X(b) - X(a)), 1.5f, soft);
            }

            // The ground.
            g.BeginPath();
            g.MoveTo(new Vector2(X(p.S[0]), Y(p.Ground[0])));
            for (int i = 1; i < p.S.Length; i++) g.LineTo(new Vector2(X(p.S[i]), Y(p.Ground[i])));
            g.LineTo(new Vector2(X(p.Length), Base));
            g.LineTo(new Vector2(X(0), Base));
            g.ClosePath();
            g.fillColor = fill;
            g.Fill();
            g.BeginPath();
            g.MoveTo(new Vector2(X(p.S[0]), Y(p.Ground[0])));
            for (int i = 1; i < p.S.Length; i++) g.LineTo(new Vector2(X(p.S[i]), Y(p.Ground[i])));
            g.strokeColor = lineA;
            g.lineWidth = 1.3f;
            g.Stroke();

            // The loaded rope, span by span: red where its chairs dip under 15 ft outside the zones.
            g.lineWidth = 1.6f;
            foreach (var span in p.Rope.Loaded)
            {
                g.BeginPath();
                const int n = 14;
                for (int i = 0; i <= n; i++)
                {
                    double s = span.From + (span.To - span.From) * i / n;
                    var pt = new Vector2(X(s), Y(span.Y(s)));
                    if (i == 0) g.MoveTo(pt); else g.LineTo(pt);
                }
                bool unstrung = span.From >= p.OpenFrom - 0.5;
                g.strokeColor = unstrung ? soft : Dips(p, span.From, span.To) ? neg : strong;
                if (unstrung) Dashes(g, span);
                else g.Stroke();
            }

            // Towers: the mast from its base to the rope, and its head's sheaves (over, under or both).
            foreach (var t in p.Rope.Towers)
            {
                float xb = X(t.BaseS), x = X(t.RopeS), yt = Y(t.RopeY), yg = Y(t.BaseY);
                Color c = t.Head == LiftRope.Head.TooSharp ? neg : text;
                g.strokeColor = c;
                g.lineWidth = 1.6f;
                Seg(g, xb, yg, x, yt);
                void Dots(float dy) { Dot(g, x - 2.2f, yt + dy, 1.25f, c); Dot(g, x + 2.2f, yt + dy, 1.25f, c); }
                if (TowerForces.IsHoldDown(t.Head)) Dots(2.6f);
                else if (t.Head == LiftRope.Head.C8) { Dots(-2.6f); Dots(2.6f); }
                else { Dots(-2.6f); if (t.Head == LiftRope.Head.B8 || t.Head == LiftRope.Head.B10) Dot(g, x, yt - 4.4f, 1.25f, c); }
            }

            // The terminals, where the rope leaves them.
            Rect(g, X(p.BottomS) - 4, Y(p.BottomZ) - 3, 8, 6, strong);
            Rect(g, X(p.TopS) - 4, Y(p.TopZ) - 3, 8, 6, strong);

            // The tower in hand: a dashed line across the strip and its mast.
            if (!double.IsNaN(p.HandS))
            {
                float x = X(p.HandS);
                float yg = Y(GroundAt(p, p.HandS));
                g.lineWidth = 1.4f;
                g.strokeColor = p.HandOk ? soft : neg;
                for (float y = 2; y < T1; y += 5) Seg(g, x, y, x, Mathf.Min(T1, y + 3));
                g.lineWidth = 2.6f;
                g.strokeColor = p.HandOk ? strong : neg;
                Seg(g, x, yg, x, Y(GroundAt(p, p.HandS) + p.HandMm / 1000f));
            }

            // The clearance track (TW-01): the chairs over the ground, 0 to 60 ft, against the dashed 15 ft line.
            g.lineWidth = 1;
            g.strokeColor = lineB;
            Seg(g, PadL, T1, right, T1);
            g.strokeColor = soft;
            for (float x = PadL; x < right; x += 7) Seg(g, x, CY(Fifteen), Mathf.Min(right, x + 4), CY(Fifteen));
            Hatch(g, PadL, X(Math.Max(0, p.ZoneB)), hair, bg);
            Hatch(g, X(Math.Min(p.Length, p.ZoneT)), X(p.Length), hair, bg);
            var dip = neg;
            dip.a *= 0.45f;
            if (!double.IsNaN(p.OpenFrom)) Hatch(g, X(p.OpenFrom), X(Math.Min(p.Length, p.ZoneT)), hair, bg);   // not strung yet
            for (int i = 0; i + 1 < p.S.Length; i++)
            {
                if (p.S[i] > p.OpenFrom) break;
                if (float.IsNaN(p.Clear[i]) || p.Clear[i] >= Fifteen - 0.03f || p.S[i] <= p.ZoneB || p.S[i] >= p.ZoneT) continue;
                Rect(g, X(p.S[i]), CY(Fifteen), Mathf.Max(1, X(p.S[i + 1]) - X(p.S[i])), T1 - CY(Fifteen), dip);
            }
            g.lineWidth = 1.4f;
            g.strokeColor = lineA;
            g.BeginPath();
            bool open = false;
            for (int i = 0; i < p.S.Length; i++)
            {
                if (p.S[i] > p.OpenFrom) break;
                if (float.IsNaN(p.Clear[i])) { open = false; continue; }
                var pt = new Vector2(X(p.S[i]), CY(p.Clear[i]));
                if (!open) { g.MoveTo(pt); open = true; } else g.LineTo(pt);
            }
            g.Stroke();
            if (!double.IsNaN(p.Lowest)) Dot(g, X(p.LowestS), CY(p.Lowest), 2.6f, p.Lowest < Fifteen - 0.03 ? neg : strong);

            // The distance axis.
            g.lineWidth = 1;
            g.strokeColor = Ink("chart-label");
            double stepAx = AxisStep(p.Length);
            for (double s = 0; s <= p.Length + 1e-3; s += stepAx) Seg(g, X(s), T1, X(s), T1 + 3);
            // Labels move outside drawing (the hierarchy can't change while the mesh is made): next frame if they're stale.
            if (_drawnVersion != p.Version || !Mathf.Approximately(_drawnWidth, W) || _drawnTrue != _trueScale) schedule.Execute(Relabel);
        }

        static double AxisStep(double length) => Imperial ? (length > 1524 ? 304.8 : 152.4) : (length > 1600 ? 300 : 100);

        static float GroundAt(LiftProfile p, double s)
        {
            int i = Mathf.Clamp((int)Math.Round(s / p.Length * (p.S.Length - 1)), 0, p.S.Length - 1);
            return p.Ground[i];
        }

        // An open span ("needs towers"): the rope as dashes.
        void Dashes(Painter2D g, LiftRope.Span span)
        {
            const int n = 40;
            for (int i = 0; i < n; i += 2)
            {
                double s0 = span.From + (span.To - span.From) * i / n, s1 = span.From + (span.To - span.From) * (i + 1) / n;
                Seg(g, X(s0), Y(span.Y(s0)), X(s1), Y(span.Y(s1)));
            }
        }

        static bool Dips(LiftProfile p, double from, double to)
        {
            for (int i = 0; i < p.S.Length; i++)
                if (p.S[i] >= from && p.S[i] <= to && p.S[i] > p.ZoneB && p.S[i] < p.ZoneT && !float.IsNaN(p.Clear[i]) && p.Clear[i] < Fifteen - 0.03f) return true;
            return false;
        }

        static void Seg(Painter2D g, float x0, float y0, float x1, float y1)
        {
            g.BeginPath();
            g.MoveTo(new Vector2(x0, y0));
            g.LineTo(new Vector2(x1, y1));
            g.Stroke();
        }

        static void Rect(Painter2D g, float x, float y, float w, float h, Color c)
        {
            g.BeginPath();
            g.MoveTo(new Vector2(x, y));
            g.LineTo(new Vector2(x + w, y));
            g.LineTo(new Vector2(x + w, y + h));
            g.LineTo(new Vector2(x, y + h));
            g.ClosePath();
            g.fillColor = c;
            g.Fill();
        }

        static void Dot(Painter2D g, float x, float y, float r, Color c)
        {
            g.BeginPath();
            g.Arc(new Vector2(x, y), r, 0, 360);
            g.fillColor = c;
            g.Fill();
        }

        // A load or unload zone on the track: hatched at 45°.
        static void Hatch(Painter2D g, float x0, float x1, Color line, Color bg)
        {
            if (x1 - x0 < 1) return;
            Rect(g, x0, T0, x1 - x0, T1 - T0, bg);
            g.lineWidth = 1.2f;
            g.strokeColor = line;
            for (float x = x0 - (T1 - T0); x < x1; x += 5)
            {
                float a = Mathf.Max(x, x0), b = Mathf.Min(x + (T1 - T0), x1);
                if (b <= a) continue;
                Seg(g, a, T1 - (a - x), b, T1 - (b - x));
            }
        }

        // ---------- the rise bar ----------

        void DrawRise(MeshGenerationContext ctx)
        {
            var p = _profile;
            if (p == null || double.IsInfinity(p.RiseLimit) || p.RiseLimit <= 0) return;
            var g = ctx.painter2D;
            float bw = _riseBar.contentRect.width;
            if (float.IsNaN(bw) || bw < 4) return;
            float BX(double f) => 1 + Mathf.Min(1.12f, (float)f) / 1.12f * (bw - 2);
            double frac = p.Rise / p.RiseLimit;
            Rect(g, 0.5f, 2.5f, bw - 1, 7, Ink("chart-bg"));
            Rect(g, 1, 3, BX(frac) - 1, 6, frac > 1 ? Ink("neg") : Ink("chart-a"));
            g.lineWidth = 1.6f;
            g.strokeColor = Ink("strong");
            Seg(g, BX(1), 0, BX(1), 12);
        }

        // ---------- labels (pooled; placed only when the profile, the width or the scale changes) ----------

        int _used;

        Label Next(string text, float x, float y, string cls, float anchor)
        {
            if (_used == _pool.Count)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("tw-strip__label");
                _labels.Add(l);
                _pool.Add(l);
            }
            var label = _pool[_used++];
            label.text = text;
            label.ClearClassList();
            label.AddToClassList("tw-strip__label");
            if (cls != null) label.AddToClassList(cls);
            label.style.display = DisplayStyle.Flex;
            label.style.left = x;
            label.style.top = y;
            label.style.translate = new Translate(new Length(-anchor * 100, LengthUnit.Percent), new Length(-50, LengthUnit.Percent));
            return label;
        }

        void Relabel()
        {
            _used = 0;
            var p = _profile;
            if (p != null && Scale())
            {
                _drawnVersion = p.Version;
                _drawnWidth = W;
                _drawnTrue = _trueScale;
                float gstep = Imperial ? ((Base - Top) / _fy > 183 ? 60.96f : 30.48f) : 50f;
                for (float z = Mathf.Ceil(_zl / gstep) * gstep; Y(z) >= Top - 0.5f; z += gstep)
                    Next(Imperial ? $"{Math.Round(z / UnitFormat.MetresPerFoot):#,0}" : $"{Math.Round(z):#,0}", PadL - 4, Y(z), "tw-strip__label--axis", 1);
                foreach (var t in p.Rope.Towers)
                {
                    double h = t.RopeY - t.BaseY;   // rope over the base: the label the mockup shows is the authored height; near enough here
                    Next(Imperial ? $"{Math.Round(h / UnitFormat.MetresPerFoot):0}" : $"{h:0.0}", X(t.RopeS), Mathf.Max(6, Y(t.RopeY) - 9),
                         t.Head == LiftRope.Head.TooSharp ? "tw-strip__label--bad" : "tw-strip__label--tower", 0.5f);
                }
                if (!double.IsNaN(p.OpenFrom)) Next("needs towers", X((p.OpenFrom + p.TopS) / 2), Y((GroundAt(p, p.OpenFrom) + p.TopZ) / 2) - 14, "tw-strip__label--side", 0.5f);
                Next("Chairs", 6, T0 + 5, "tw-strip__label--side", 0);
                Next("over ground", 6, T0 + 16, "tw-strip__label--side2", 0);
                Next(ToolFormat.Length(Fifteen), PadL - 4, CY(Fifteen), "tw-strip__label--axis", 1);
                double stepAx = AxisStep(p.Length);
                for (double s = 0; s <= p.Length + 1e-3; s += stepAx)
                    Next(s == 0 ? "0" : Imperial ? $"{Math.Round(s / UnitFormat.MetresPerFoot):#,0}" : $"{Math.Round(s):#,0}", X(s), T1 + 10, "tw-strip__label--axis", 0.5f);
                if (!double.IsNaN(p.Lowest))
                    Next("lowest " + ToolFormat.Length(Math.Max(0, Math.Floor(p.Lowest / 0.3048) * 0.3048)), Mathf.Min(X(p.LowestS) + 6, W - 70), Mathf.Max(T0 + 6, CY(p.Lowest) - 6),
                         p.Lowest < Fifteen - 0.03 ? "tw-strip__label--bad" : "tw-strip__label--tower", 0);
            }
            for (int i = _used; i < _pool.Count; i++) _pool[i].style.display = DisplayStyle.None;
        }
    }
}
