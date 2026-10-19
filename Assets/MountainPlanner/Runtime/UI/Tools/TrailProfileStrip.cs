using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The trail profile (TF-04, task 08, owner TL7), ported from the mockup's strip above the status bar: distance along
    /// the centreline against elevation; the natural ground dashed over a light fill, the graded surface solid in each
    /// stretch's rating colour (TD-03), stretches too steep to bench red (GR-07), and the steepest 75 ft window marked
    /// (TD-02). Shown while a trail is built or edited; redrawn when its analysis changes, never per frame.
    /// </summary>
    public sealed class TrailProfileStrip : VisualElement
    {
        static readonly CustomStyleProperty<Color> ChartA = new CustomStyleProperty<Color>("--tps-a");
        static readonly CustomStyleProperty<Color> ChartFill = new CustomStyleProperty<Color>("--tps-fill");
        static readonly CustomStyleProperty<Color> Row = new CustomStyleProperty<Color>("--tps-row");
        static readonly CustomStyleProperty<Color> Strong = new CustomStyleProperty<Color>("--tps-strong");
        static readonly CustomStyleProperty<Color> Neg = new CustomStyleProperty<Color>("--tps-neg");
        static readonly CustomStyleProperty<Color> Text = new CustomStyleProperty<Color>("--tps-text");
        static readonly Color Green = new Color(0.247f, 0.682f, 0.416f), Blue = new Color(0.302f, 0.541f, 0.839f);

        readonly Label _summary, _rating, _range;
        readonly VisualElement _chart;
        Color _a = Color.gray, _fill = new Color(1, 1, 1, 0.1f), _row = new Color(1, 1, 1, 0.08f), _strong = Color.white, _neg = Color.red, _text = Color.white;

        double[] _along, _ground, _surface, _slope;
        bool[] _benched;
        bool _graded;
        int _steepest;

        public TrailProfileStrip()
        {
            name = "trail-profile";
            AddToClassList("mp-win");
            AddToClassList("tps");
            pickingMode = PickingMode.Ignore;
            var head = new VisualElement();
            head.AddToClassList("tps__head");
            var title = new Label("Profile");
            title.AddToClassList("tps__title");
            head.Add(title);
            _summary = new Label { name = "trail-profile-summary" };
            _summary.AddToClassList("tps__mono");
            _summary.AddToClassList("mono");
            head.Add(_summary);
            var gap = new VisualElement();
            gap.style.flexGrow = 1;
            head.Add(gap);
            _range = new Label();
            _range.AddToClassList("tps__mono");
            _range.AddToClassList("mono");
            head.Add(_range);
            _rating = new Label { name = "trail-profile-rating" };
            _rating.AddToClassList("tps__rating");
            head.Add(_rating);
            Add(head);
            _chart = new VisualElement { name = "trail-profile-chart" };
            _chart.AddToClassList("tps__chart");
            _chart.generateVisualContent += Draw;
            Add(_chart);
            RegisterCallback<CustomStyleResolvedEvent>(e =>
            {
                e.customStyle.TryGetValue(ChartA, out _a);
                e.customStyle.TryGetValue(ChartFill, out _fill);
                e.customStyle.TryGetValue(Row, out _row);
                e.customStyle.TryGetValue(Strong, out _strong);
                e.customStyle.TryGetValue(Neg, out _neg);
                e.customStyle.TryGetValue(Text, out _text);
                _chart.MarkDirtyRepaint();
            });
        }

        /// <summary>Before a trail has a centreline: the strip says what comes.</summary>
        public void ShowWaiting(string text)
        {
            _along = null;
            _summary.text = text;
            _rating.text = _range.text = "";
            _chart.MarkDirtyRepaint();
        }

        /// <summary>A trail's analysis: its grade (stations, natural ground) and rating (the surface, each station's slope).</summary>
        public void Show(TrailGrader.Graded grade, TrailRating.Result rating)
        {
            _along = grade.Along;
            _ground = grade.Ground;
            _surface = rating.Surface;
            _slope = rating.StationSlope;
            _benched = grade.Benched;
            _graded = grade.IsGraded;
            _steepest = rating.SteepestStation;
            _summary.text = $"{ToolFormat.Length(grade.LengthMetres)} · vertical {ToolFormat.Length(System.Math.Abs(rating.DropMetres))} · " +
                            $"mean {System.Math.Round(rating.MeanPercent):0}% · steepest {System.Math.Round(rating.MaxPercent):0}%";
            _rating.text = TrailTool.Rating(rating.ValuePercent);
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < _ground.Length; i++)
            {
                lo = System.Math.Min(lo, System.Math.Min(_ground[i], _surface[i]));
                hi = System.Math.Max(hi, System.Math.Max(_ground[i], _surface[i]));
            }
            _range.text = $"{ToolFormat.Length(hi)} → {ToolFormat.Length(lo)}  ";
            _chart.MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext mgc)
        {
            var r = _chart.contentRect;
            if (_along == null || _along.Length < 2 || r.width < 20 || r.height < 20) return;
            int n = _along.Length;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                lo = System.Math.Min(lo, System.Math.Min(_ground[i], _surface[i]));
                hi = System.Math.Max(hi, System.Math.Max(_ground[i], _surface[i]));
            }
            float padL = 12, padR = 12, top = 8, bottom = r.height - 8;
            double L = System.Math.Max(1, _along[n - 1]), span = System.Math.Max(1, hi - lo);
            float X(double s) => padL + (float)(s / L) * (r.width - padL - padR);
            float Y(double z) => bottom - (float)((z - lo) / span) * (bottom - top);
            var p = mgc.painter2D;

            // Grid: four level lines.
            p.strokeColor = _row;
            p.lineWidth = 1;
            for (int k = 0; k <= 4; k++)
            {
                float y = top + (bottom - top) * k / 4;
                p.BeginPath(); p.MoveTo(new Vector2(padL, y)); p.LineTo(new Vector2(r.width - padR, y)); p.Stroke();
            }
            // The steepest 75 ft (TD-02).
            int a = Mathf.Clamp(_steepest, 0, n - 1), b = Mathf.Clamp(_steepest + TrailRating.WindowStations - 1, 0, n - 1);
            p.fillColor = new Color(_text.r, _text.g, _text.b, 0.08f);
            p.BeginPath();
            p.MoveTo(new Vector2(X(_along[a]), top - 4)); p.LineTo(new Vector2(Mathf.Max(X(_along[b]), X(_along[a]) + 3), top - 4));
            p.LineTo(new Vector2(Mathf.Max(X(_along[b]), X(_along[a]) + 3), bottom)); p.LineTo(new Vector2(X(_along[a]), bottom)); p.ClosePath();
            p.Fill();
            // The natural ground: a light fill and a dashed line.
            p.fillColor = _fill;
            p.BeginPath();
            p.MoveTo(new Vector2(X(_along[0]), bottom));
            for (int i = 0; i < n; i++) p.LineTo(new Vector2(X(_along[i]), Y(_ground[i])));
            p.LineTo(new Vector2(X(_along[n - 1]), bottom));
            p.ClosePath();
            p.Fill();
            p.strokeColor = _a;
            p.lineWidth = 1.2f;
            float dash = 4, gapLen = 3, carried = 0;
            bool on = true;
            for (int i = 1; i < n; i++)
            {
                var from = new Vector2(X(_along[i - 1]), Y(_ground[i - 1]));
                var to = new Vector2(X(_along[i]), Y(_ground[i]));
                float len = Vector2.Distance(from, to), t = 0;
                while (t < len)
                {
                    float step = Mathf.Min((on ? dash : gapLen) - carried, len - t);
                    if (on)
                    {
                        p.BeginPath();
                        p.MoveTo(Vector2.Lerp(from, to, t / len));
                        p.LineTo(Vector2.Lerp(from, to, (t + step) / len));
                        p.Stroke();
                    }
                    t += step;
                    carried += step;
                    if (carried >= (on ? dash : gapLen) - 1e-3f) { carried = 0; on = !on; }
                }
            }
            // The surface: solid, each stretch in its rating's colour; red where too steep to bench.
            p.lineWidth = 2.2f;
            for (int i = 1; i < n; i++)
            {
                bool ok = !_graded || (_benched[i - 1] && _benched[i]);
                var band = SlopeBands.Of(_slope[i]);
                p.strokeColor = !ok ? _neg : band == SlopeBands.Band.Green ? Green : band == SlopeBands.Band.Blue ? Blue : _strong;
                p.BeginPath();
                p.MoveTo(new Vector2(X(_along[i - 1]), Y(_surface[i - 1])));
                p.LineTo(new Vector2(X(_along[i]), Y(_surface[i])));
                p.Stroke();
            }
        }
    }
}
