using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Trails › New (TF-01 to TF-03, TF-09; P3-06's part). Step 1 paints the trail's area: left-drag paints,
    /// Shift-drag erases, [ and ] change the brush, Ctrl+Z undoes a stroke; after each stroke the area and the patch
    /// count are worked out off the main thread (CF-02, CF-03), and a second separate patch blocks Next. Step 2 takes the
    /// head and the tail, each snapping within 30 ft to a lift's end or another trail (TR-05). Grading, the centreline,
    /// the rating and the profile are task 08's; the review shows their rows reserved. Break ground submits
    /// <see cref="AddTrail"/>.
    /// </summary>
    public sealed class NewTrailTool : IDesignTool
    {
        public const string ToolId = "new-trail";

        static readonly string[] Steps = { "Paint", "Head and tail", "Review" };
        const int MinFeet = 25, MaxFeet = 400, StepFeet = 5;

        // The brush width is remembered for the session (the mockup's S.trailWidth).
        static int _lastFeet = 100;

        enum Step { Paint, Ends, Review }

        ToolContext _ctx;
        Step _step;
        int _feet;
        bool _graded = true;
        string _name;
        readonly List<Stroke> _strokes = new List<Stroke>();
        readonly List<PointMm> _live = new List<PointMm>();
        bool _painting, _liveErase;
        Vector2 _lastXz;
        Vector2[] _left = new Vector2[64], _right = new Vector2[64];
        FootprintRaster _raster;
        bool _analysing;
        SnapHit _head, _tail;
        bool _hasHead, _hasTail;
        float _headZ, _tailZ;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _pair = new Vector2[2];
        System.Action<int, string> _setWidth;
        System.Action<int> _setGrading;
        UnityEngine.UIElements.TextField _nameField;

        public string Id => ToolId;
        public string Name => "New trail";
        public bool HasWork => _strokes.Count > 0 || _painting;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _feet = _lastFeet;
            _name = NextName(context.History.Document);
            var w = context.Window;
            w.SetName(_name);
            _nameField = w.AddText("name", "Name", _name, DesignRules.NameMaxLength, Rename);
            _setWidth = w.AddSlider("brush", "Brush", MinFeet, MaxFeet, StepFeet, _feet, SetFeet, "TR-01: 25–400 ft, in 5 ft steps; one trail can mix widths");
            _setGrading = w.AddSwitch("grading", "Grading", new[] { "Graded", "Natural" }, 0, i => { _graded = i == 0; _setGrading(i); },
                "TR-06, TF-06: Graded benches the trail; Natural keeps the ground");
            w.AddNote("kind", "Run · trees cleared", "TR-06: Glades and Zones, and Thin or no clearing, come later");
            w.SetSteps(Steps);
            SetFeet(_feet);
            Go(Step.Paint);
        }

        static string NextName(DesignDocument doc) => "Run " + (doc.Trails.Count + 1);

        void Rename(string name)
        {
            string trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || DesignRules.NameProblem(trimmed) != null) return;
            _name = trimmed;
            _ctx.Window.SetName(_name);
        }

        void SetFeet(int feet)
        {
            _feet = _lastFeet = Mathf.Clamp(feet, MinFeet, MaxFeet);
            _setWidth(_feet, ToolFormat.Feet(_feet));
        }

        void Go(Step step)
        {
            _step = step;
            var w = _ctx.Window;
            w.SetStep((int)step);
            bool painting = step == Step.Paint;
            _ctx.Claim(GameAction.BrushNarrower, painting);
            _ctx.Claim(GameAction.BrushWider, painting);
            _ctx.Claim(GameAction.UndoStroke, painting);
            _ctx.Claim(GameAction.Erase, painting);
            switch (step)
            {
                case Step.Paint:
                    _hasHead = _hasTail = false;
                    w.SetKeys((GameAction.BrushNarrower, ""), (GameAction.BrushWider, "width"), (GameAction.Erase, "erases"), (GameAction.UndoStroke, "undoes a stroke"));
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    PaintStatus();
                    break;
                case Step.Ends:
                    w.SetKeys((GameAction.ToolNext, "review"));
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    EndsStatus();
                    break;
                case Step.Review:
                    w.SetHint(null);
                    w.SetMessage(null);
                    w.SetKeys();   // Break ground shows its own key
                    w.SetNext(false, false);
                    BuildReview();
                    w.ShowReview(_review);
                    break;
            }
            DrawEnds();
        }

        void PaintStatus()
        {
            var w = _ctx.Window;
            if (_raster == null || _raster.Cells == 0)
            {
                w.SetHint(_strokes.Count == 0
                    ? "Drag on the mountain to paint the trail. Paint wider or narrower stretches as you go; leave holes for islands of trees."
                    : "Working out the area…");
                w.SetMessage(null);
                w.SetNext(true, false);
                return;
            }
            w.SetHint($"Area {ToolFormat.Area(_raster.AreaSquareMetres)}. Keep painting, or go on to the head and tail.");
            int extra = _raster.Patches - 1;
            w.SetMessage(extra > 0 ? $"{extra} separate patch{(extra > 1 ? "es" : "")}: join or erase {(extra > 1 ? "them" : "it")}" : null);
            w.SetNext(true, !_analysing && extra == 0);
        }

        void EndsStatus()
        {
            var w = _ctx.Window;
            w.SetHint(!_hasHead ? "Click the head, where the trail starts at the top. Ends within 30 ft of a lift's top or bottom, or of another trail, join it."
                : !_hasTail ? "Now click the tail, where it ends at the bottom."
                : "The centreline, grading and rating come with the trail task. Review it, or move an end: Esc takes it back.");
            w.SetNext(true, _hasHead && _hasTail);
        }

        public void Tick(in ToolInput input)
        {
            if (_step == Step.Paint) TickPaint(input);
            else if (_step == Step.Ends) TickEnds(input);
        }

        void TickPaint(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.BrushNarrower)) SetFeet(_feet - StepFeet);
            if (input.Pressed(ToolKeys.BrushWider)) SetFeet(_feet + StepFeet);
            if (input.Pressed(ToolKeys.UndoStroke) && !_painting && _strokes.Count > 0)
            {
                _strokes.RemoveAt(_strokes.Count - 1);
                Restroke();
            }
            if (input.Pressed(ToolKeys.Next) && !_painting && _raster != null && _raster.Patches == 1 && !_analysing)
            {
                Go(Step.Ends);
                return;
            }

            var preview = _ctx.Preview;
            float radius = DesignUnits.FeetToMm(_feet) / 2000f;
            if (input.HasGround)
            {
                var at = new Vector2(input.Ground.x, input.Ground.z);
                bool inside = _ctx.InCore(at);
                preview.Ring(0, at, radius, !inside ? PreviewInk.Bad : (_painting ? _liveErase : input.Erase) ? PreviewInk.Bad : PreviewInk.Brush);
                if (input.Click && inside && !_painting)
                {
                    // A footprint starts with paint (the design's rule), so the very first stroke can't erase.
                    _painting = true;
                    _liveErase = input.Erase && _strokes.Count > 0;
                    _live.Clear();
                    _live.Add(input.Point);
                    _lastXz = at;
                }
                else if (_painting && input.Drag && inside && Vector2.Distance(at, _lastXz) >= Mathf.Max(3f, radius / 2))
                {
                    _live.Add(input.Point);
                    _lastXz = at;
                    DrawStroke(1, _live, _feet, _liveErase);
                }
            }
            else preview.Hide(0);

            if (_painting && (input.Release || !input.Drag))
            {
                _painting = false;
                _strokes.Add(new Stroke(DesignUnits.FeetToMm(_feet), _liveErase, _live.ToArray()));
                _live.Clear();
                Restroke();
            }
        }

        // Redraws every stroke's edges and works out the area again (after a stroke, an undo, or a step back).
        void Restroke()
        {
            var preview = _ctx.Preview;
            preview.Hide(1);
            preview.Hide(2);
            int slot = 10;
            foreach (var s in _strokes)
            {
                DrawStroke(slot, s.Points, s.WidthMm / DesignUnits.FiveFeetMm * 5, s.Erase);
                slot += 2;
            }
            preview.HideFrom(slot);
            _raster = null;
            if (_strokes.Count == 0) { _analysing = false; PaintStatus(); return; }
            _analysing = true;
            PaintStatus();
            var strokes = _strokes.ToArray();
            _ctx.Analyse(() => FootprintRaster.Build(strokes), r =>
            {
                _raster = r;
                _analysing = false;
                if (_step == Step.Paint) PaintStatus();
            });
        }

        // A stroke drawn as its two edges, half the brush either side of its path (the mockup's dashed edge).
        void DrawStroke(int slot, IReadOnlyList<PointMm> points, int feet, bool erase)
        {
            int n = points.Count;
            if (_left.Length < n) { _left = new Vector2[n * 2]; _right = new Vector2[n * 2]; }
            float r = DesignUnits.FeetToMm(feet) / 2000f;
            DesignFrame.Edges(points, r, _left, _right);
            var ink = erase ? PreviewInk.Bad : PreviewInk.Paint;
            if (n == 1)
            {
                _ctx.Preview.Ring(slot, DesignFrame.ToLocal(points[0]), r, ink);
                _ctx.Preview.Hide(slot + 1);
                return;
            }
            _ctx.Preview.Line(slot, _left, n, ink);
            _ctx.Preview.Line(slot + 1, _right, n, ink);
        }

        void TickEnds(in ToolInput input)
        {
            var preview = _ctx.Preview;
            if (input.Pressed(ToolKeys.Next) && _hasHead && _hasTail) { Go(Step.Review); return; }
            if (!input.HasGround) { preview.Hide(4); return; }
            var snap = _ctx.Snap(input.Point, SnapTargets.All);
            var at = DesignFrame.ToLocal(snap.At);
            bool onTrail = snap.Snapped || (_raster != null && _raster.Contains(input.Point));
            if (snap.Snapped) preview.Ring(4, at, 9.144f, PreviewInk.Snap);
            else preview.Hide(4);
            if (!input.Click) return;
            if (!onTrail)
            {
                _ctx.Window.SetMessage("Click on the painted trail, or on a lift's end or another trail");
                return;
            }
            _ctx.Window.SetMessage(null);
            float z = _ctx.HeightAt(at);
            if (!_hasHead || _hasTail)
            {
                _head = snap; _headZ = z; _hasHead = true; _hasTail = false;
            }
            else
            {
                if (snap.At == _head.At) { _ctx.Window.SetMessage("The head and tail can't be the same point"); return; }
                _tail = snap; _tailZ = z; _hasTail = true;
            }
            EndsStatus();
            DrawEnds();
        }

        void DrawEnds()
        {
            var p = _ctx.Preview;
            if (_step == Step.Paint || !_hasHead) { p.Hide(5); p.Hide(6); p.Hide(7); return; }
            var h = DesignFrame.ToLocal(_head.At);
            p.Dot(5, h, _head.Snapped ? PreviewInk.Snap : PreviewInk.Line);
            if (!_hasTail) { p.Hide(6); p.Hide(7); return; }
            var t = DesignFrame.ToLocal(_tail.At);
            p.Dot(6, t, _tail.Snapped ? PreviewInk.Snap : PreviewInk.Line);
            _pair[0] = h;
            _pair[1] = t;
            p.Line(7, _pair, 2, PreviewInk.Line);
        }

        void BuildReview()
        {
            float run = Vector2.Distance(DesignFrame.ToLocal(_head.At), DesignFrame.ToLocal(_tail.At));
            float drop = _headZ - _tailZ;
            _review.Clear()
                .Add("Area", ToolFormat.Area(_raster.AreaSquareMetres), "TR-02")
                .Add("Head to tail", ToolFormat.Length(run), "TR-03: the centreline's length comes with the trail task")
                .Add("Vertical", ToolFormat.Length(Mathf.Abs(drop)))
                .Reserved("Rating", "TD-02, TD-03 (task 08)", "rating")
                .Reserved(_graded ? "Cut · fill" : "Grading", "GR-08 (tasks 05 and 08)", ReviewModel.TerrainSlot)
                .Reserved("Trees cleared", "CL-01, CL-02 (task 08)", "trees")
                .Reserved("Network", "NW-12: No lift serves this, Leads nowhere (task 10)", ReviewModel.NetworkSlot);
            if (drop < 0) _review.Warn("The head is lower than the tail: the trail runs uphill", "TR-03");
            _review.Ready(true, true);
        }

        public ToolBackOut Escape()
        {
            switch (_step)
            {
                case Step.Review: Go(Step.Ends); return ToolBackOut.Stepped;
                case Step.Ends:
                    if (_hasTail) _hasTail = false;
                    else if (_hasHead) _hasHead = false;
                    else { Go(Step.Paint); return ToolBackOut.Stepped; }
                    EndsStatus();
                    DrawEnds();
                    return ToolBackOut.Stepped;
                default:
                    if (_painting)
                    {
                        _painting = false;
                        _live.Clear();
                        Restroke();
                        return ToolBackOut.Stepped;
                    }
                    return ToolBackOut.Close;
            }
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (_step != Step.Review) return null;
            var part = new FootprintPart(_strokes.ToArray());
            var settings = _graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun;
            return new AddTrail(baseRevision, _name, settings, new[] { part }, _head.At, _tail.At, stage);
        }

        public void Committed(CommandResult result)
        {
            var trail = result.Document.FindTrail(new TrailId(result.Created[0]));
            _ctx.Notice?.Invoke(_name + (trail?.Stage == WorkStage.Plan ? ": saved as a plan" : ": ground broken"));
            _strokes.Clear();
            _raster = null;
            _ctx.Preview.HideFrom(0);
            _name = NextName(result.Document);
            _ctx.Window.SetName(_name);
            _nameField.SetValueWithoutNotify(_name);
            Go(Step.Paint);
        }

        public void Disarm()
        {
            _painting = false;
            _ctx = null;
        }

        // For tests and captures.
        public int CurrentStep => (int)_step;
        public int Strokes => _strokes.Count;
        public int BrushFeet => _feet;
        public FootprintRaster Raster => _raster;
        public bool Analysing => _analysing;
    }
}
