using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// A tower's load gauge (TW-04, TW-05, TW-11, RP-04; the approved mockup's loadGauge): each rope side's load on one sheave
    /// in the two load cases (line empty, uphill side full), from "pulls up" on the left to "presses down" on the right, with
    /// the ends at a sheave's 1,500 lbf. The side the head carries is shaded; the grey middle is a group load under 750 lbf
    /// (LC-02), where the rope needs hold-down sheaves too. Under it, the figures in words.
    /// </summary>
    public sealed class LiftLoadGauge : VisualElement
    {
        readonly VisualElement _bar;
        readonly Label _empty, _full, _left, _right, _detail;
        readonly VisualElement[] _inks = new VisualElement[6];
        TowerForces.Loads _loads;
        bool _has;

        public LiftLoadGauge()
        {
            name = "toolwin-gauge";
            AddToClassList("tw-gauge");
            tooltip = "TW-04, TW-05, TW-11, RP-04: the load on each sheave with the line empty and the uphill side full, against a sheave's 1,500 lbf. The shaded side is what this head carries; the grey middle is a group load under 750 lbf, where the rope needs hold-down sheaves too.";
            _bar = new VisualElement { pickingMode = PickingMode.Ignore };
            _bar.AddToClassList("tw-gauge__bar");
            _bar.generateVisualContent += Draw;
            _bar.RegisterCallback<GeometryChangedEvent>(_ => Place());
            Add(_bar);
            _empty = Mark("empty");
            _full = Mark("full");
            var ends = new VisualElement { pickingMode = PickingMode.Ignore };
            ends.AddToClassList("tw-gauge__ends");
            _left = new Label("◂ pulls up") { pickingMode = PickingMode.Ignore };
            _right = new Label("presses down ▸") { pickingMode = PickingMode.Ignore };
            foreach (var l in new[] { _left, _right }) { l.AddToClassList("tw-gauge__end"); ends.Add(l); }
            Add(ends);
            _detail = new Label { name = "toolwin-gauge-detail", pickingMode = PickingMode.Ignore };
            _detail.AddToClassList("tw-gauge__detail");
            Add(_detail);
            string[] names = { "chart-bg", "chart-fill", "hair2", "strong", "neg", "soft" };
            for (int i = 0; i < names.Length; i++)
            {
                _inks[i] = new VisualElement { pickingMode = PickingMode.Ignore };
                _inks[i].AddToClassList("tw-strip__ink");
                _inks[i].AddToClassList("tw-strip__ink--" + names[i]);
                Add(_inks[i]);
            }
        }

        Label Mark(string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-gauge__mark");
            _bar.Add(l);
            return l;
        }

        Color Ink(int i) => _inks[i].resolvedStyle.backgroundColor;

        static bool Imperial => DisplayUnits.Current == UnitSystem.Imperial;
        static string Force(double newtons) => Imperial ? $"{Math.Round(newtons / 4.448222):#,0} lbf" : $"{newtons / 1000:0.0} kN";

        /// <summary>Shows a tower's loads (and whether its head is still provisional), or nothing.</summary>
        public void Show(TowerForces.Loads? loads, bool provisional = false)
        {
            _has = loads.HasValue;
            style.display = _has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_has) return;
            _loads = loads.Value;
            int n = Sheaves(_loads.GroupLoaded);
            double pctFull = Math.Abs(_loads.GroupLoaded) / Math.Max(1, n) / TowerForces.SheaveLimitNewtons * 100;
            double pctEmpty = Math.Abs(_loads.GroupEmpty) / Math.Max(1, Sheaves(_loads.GroupEmpty)) / TowerForces.SheaveLimitNewtons * 100;
            string word(double g) => g > 0 ? "pulls" : "presses";
            string bend = $"bend {Math.Abs(_loads.BendLoaded) * 180 / Math.PI:0.0}°";
            _detail.text = provisional
                ? $"For now, until the span past it is strung. Full {pctFull:0}% · empty {pctEmpty:0}% · {bend}"
                : $"Full: {word(_loads.GroupLoaded)} {Force(Math.Abs(_loads.GroupLoaded) / Math.Max(1, n))} a sheave ({pctFull:0}%) · empty: {Force(Math.Abs(_loads.GroupEmpty) / Math.Max(1, Sheaves(_loads.GroupEmpty)))} ({pctEmpty:0}%) · {bend}";
            _bar.MarkDirtyRepaint();
            Place();
        }

        // The sheaves that take a group load: support sheaves for a load pressing down, hold-down ones for one pulling up.
        int Sheaves(double group)
        {
            int support = _loads.SupportSheaves, hold = _loads.HoldDownSheaves;
            int n = group > 0 ? hold : support;
            return n > 0 ? n : Math.Max(support, hold);
        }

        // Presses down is to the right (+): the group load's sign is + pulling up, so it flips.
        double F(double group) => -group / Math.Max(1, Sheaves(group)) / TowerForces.SheaveLimitNewtons;

        float W => _bar.contentRect.width;
        float X(double f) => W / 2 + (float)Math.Max(-1.1, Math.Min(1.1, f)) * (W / 2 - 12);

        void Place()
        {
            if (!_has || float.IsNaN(W) || W < 40) return;
            _empty.style.left = Mathf.Clamp(X(F(_loads.GroupEmpty)), 16, W - 16);
            _full.style.left = Mathf.Clamp(X(F(_loads.GroupLoaded)), 16, W - 16);
        }

        void Draw(MeshGenerationContext ctx)
        {
            if (!_has || float.IsNaN(W) || W < 40) return;
            var g = ctx.painter2D;
            float y0 = 15, h = 9;
            Rect(g, X(-1), y0, X(1) - X(-1), h, Ink(0));
            double seat = TowerForces.GroupMinimumNewtons / Math.Max(1, _loads.SupportSheaves + _loads.HoldDownSheaves) / TowerForces.SheaveLimitNewtons;
            var head = _loads.Head;
            bool down = TowerForces.IsHoldDown(head);
            if (!down) Rect(g, X(seat), y0, X(1) - X(seat), h, Ink(1));
            if (down || head == LiftRope.Head.C8) Rect(g, X(-1), y0, X(-seat) - X(-1), h, Ink(1));
            var mid = Ink(2);
            mid.a *= 0.7f;
            Rect(g, X(-seat), y0, X(seat) - X(-seat), h, mid);
            g.lineWidth = 1.6f;
            g.strokeColor = Ink(3);
            foreach (float f in new[] { -1f, 1f }) Seg(g, X(f), 11, X(f), 28);
            g.lineWidth = 1;
            g.strokeColor = Ink(5);
            Seg(g, W / 2, 12, W / 2, 27);
            foreach (double group in new[] { _loads.GroupEmpty, _loads.GroupLoaded })
            {
                double f = F(group);
                Rect(g, X(f) - 1.5f, 11, 3, 17, Math.Abs(f) > 1 ? Ink(4) : Ink(3));
            }
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
            if (w <= 0) return;
            g.BeginPath();
            g.MoveTo(new Vector2(x, y));
            g.LineTo(new Vector2(x + w, y));
            g.LineTo(new Vector2(x + w, y + h));
            g.LineTo(new Vector2(x, y + h));
            g.ClosePath();
            g.fillColor = c;
            g.Fill();
        }
    }
}
