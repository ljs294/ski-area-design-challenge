using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Trails › New (TF-01 to TF-05, TF-09; P3-06's tool, task 08's analysis). Step 1 paints the trail's area:
    /// left-drag paints, Shift-drag erases, [ and ] change the brush, Ctrl+Z undoes a stroke; after each stroke the
    /// footprint, its area and the estimated rating (TD-06) are worked out off the main thread (CF-02, CF-03), and a
    /// second separate patch blocks Next. Step 2 takes the head and the tail, each snapping within 30 ft (TR-05); then
    /// the whole trail is analysed against the design's earthwork (TL3): the automatic centreline (TR-03), the grade
    /// (GR-01 to GR-13) when the player chose Graded (TL13: a new trail starts Natural), clearing (CL-01, CL-02, CL-08),
    /// the rating (TD-01 v2, TD-02), the yellow pending contours (CF-08) and red stretches too steep to bench (GR-07).
    /// Break ground submits <see cref="AddTrail"/> with the edits.
    /// </summary>
    public sealed class NewTrailTool : IDesignTool
    {
        public const string ToolId = "new-trail";

        static readonly string[] Steps = { "Paint", "Head and tail", "Review" };
        const int MinFeet = 25, MaxFeet = 400, StepFeet = 5;

        // The brush width is remembered for the session (the mockup's S.trailWidth).
        static int _lastFeet = 100;

        enum Step { Paint, Ends, Review }

        /// <summary>A painted footprint and what step 1 shows.</summary>
        sealed class Paint
        {
            public TrailFootprint Footprint;
            public double Estimate = double.NaN;
        }

        /// <summary>The whole trail worked out (step 2 onward).</summary>
        public sealed class Analysis
        {
            public DesignEarthwork.Result Rebuild;
            public TrailBuild Build;
            public TrailRating.Result Rating;
            public List<double> Contours;
            public long BaseRevision;
            public bool Graded;
            public string Problem;
        }

        ToolContext _ctx;
        Step _step;
        int _feet;
        bool _graded;
        string _name;
        readonly List<Stroke> _strokes = new List<Stroke>();
        readonly List<PointMm> _live = new List<PointMm>();
        bool _painting, _liveErase;
        Vector2 _lastXz;
        Vector2[] _left = new Vector2[64], _right = new Vector2[64];
        TrailFootprint _raster;
        double _estimate = double.NaN;
        bool _analysing, _wantAnalysis;
        Analysis _analysis;
        SnapHit _head, _tail;
        bool _hasHead, _hasTail;
        float _headZ, _tailZ;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _pair = new Vector2[2];
        Vector2[] _line = new Vector2[64];
        System.Action<int, string> _setWidth;
        System.Action<int> _setGrading;
        UnityEngine.UIElements.TextField _nameField;

        // Preview slots: 0 brush, 1 live stroke, 4 snap, 5–7 ends, 8 centreline, 9 unused, 10–199 strokes, 200+ red stretches.
        const int CentrelineSlot = 8, StrokeSlots = 10, RedSlots = 200;

        public string Id => ToolId;
        public string Name => "New trail";
        public bool HasWork => _strokes.Count > 0 || _painting;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();   // the ground's session, if this area hasn't one yet (task 08)
            _feet = _lastFeet;
            _graded = false;           // TL13: grading only when the player chooses it
            _name = NextName(context.History.Document);
            var w = context.Window;
            w.SetName(_name);
            _nameField = w.AddText("name", "Name", _name, DesignRules.NameMaxLength, Rename);
            _setWidth = w.AddSlider("brush", "Brush", MinFeet, MaxFeet, StepFeet, _feet, SetFeet, "TR-01: 25–400 ft, in 5 ft steps; one trail can mix widths");
            _setGrading = w.AddSwitch("grading", "Grading", new[] { "Natural", "Graded" }, 0, i => { SetGraded(i == 1); _setGrading(i); },
                "TR-06, TF-06, TL13: Natural keeps the ground and clears the trees; Graded benches the trail level across");
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

        void SetGraded(bool graded)
        {
            if (_graded == graded) return;
            _graded = graded;
            if (_step != Step.Paint && _hasHead && _hasTail) Analyse();
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
                    DropAnalysis();
                    w.SetKeys((GameAction.BrushNarrower, ""), (GameAction.BrushWider, "width"), (GameAction.Erase, "erases"), (GameAction.UndoStroke, "undoes a stroke"));
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    PaintStatus();
                    break;
                case Step.Ends:
                    _ctx.Preview.Hide(0);   // the brush ring belongs to painting
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
            string rating = double.IsNaN(_estimate) ? "" : $" Estimated {Rating(_estimate)} (TD-06).";
            w.SetHint($"Area {ToolFormat.Area(_raster.AreaSquareMetres)}.{rating} Keep painting, or go on to the head and tail.");
            int extra = _raster.Patches - 1;
            w.SetMessage(extra > 0 ? $"{extra} separate patch{(extra > 1 ? "es" : "")}: join or erase {(extra > 1 ? "them" : "it")}" : null);
            w.SetNext(true, !_analysing && extra == 0);
        }

        /// <summary>A rating as the player reads it: symbol, name and grade ("■ More difficult, 32%").</summary>
        public static string Rating(double percent)
        {
            var band = SlopeBands.Of(percent);
            return $"{Symbol(band)} {SlopeBands.Names[(int)band]}, {System.Math.Round(percent):0}%";
        }

        public static string Symbol(SlopeBands.Band band) =>
            band == SlopeBands.Band.Green ? "●" : band == SlopeBands.Band.Blue ? "■" : band == SlopeBands.Band.Black ? "◆" : "◆◆";

        void EndsStatus()
        {
            var w = _ctx.Window;
            bool ready = _ctx.Earthwork == null || _ctx.Earthwork.Ready;
            w.SetHint(!_hasHead ? "Click the head, where the trail starts at the top. Ends within 30 ft of a lift's top or bottom, or of another trail, join it."
                : !_hasTail ? "Now click the tail, where it ends at the bottom."
                : !ready ? "Preparing the ground…"
                : _analysing || _wantAnalysis ? "Working out the centreline, grading and rating…"
                : _analysis?.Problem ?? "Review it, or move an end: Esc takes it back.");
            w.SetNext(true, NextReady);
        }

        bool NextReady => _hasHead && _hasTail && !_analysing && !_wantAnalysis && (_ctx.Earthwork == null || _analysis != null);

        public void Tick(in ToolInput input)
        {
            if (_wantAnalysis && !_analysing) Analyse();
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

        EarthworkSite Site => _ctx.Earthwork?.Earthwork?.Site ?? new EarthworkSite(new EditLattice(0, 0), 0, 0);

        // Redraws every stroke's edges and works out the footprint again (after a stroke, an undo, or a step back).
        void Restroke()
        {
            var preview = _ctx.Preview;
            preview.Hide(1);
            preview.Hide(2);
            int slot = StrokeSlots;
            foreach (var s in _strokes)
            {
                if (slot + 1 >= RedSlots) break;
                DrawStroke(slot, s.Points, s.WidthMm / DesignUnits.FiveFeetMm * 5, s.Erase);
                slot += 2;
            }
            for (int k = slot; k < RedSlots; k++) preview.Hide(k);
            _raster = null;
            _estimate = double.NaN;
            if (_strokes.Count == 0) { _analysing = false; PaintStatus(); return; }
            _analysing = true;
            PaintStatus();
            var parts = new[] { new FootprintPart(_strokes.ToArray()) };
            var site = Site;
            var heights = _ctx.Earthwork != null && _ctx.Earthwork.Ready ? _ctx.Earthwork.Heights : null;
            _ctx.Analyse(() =>
            {
                var p = new Paint { Footprint = TrailFootprint.Build(parts, site.Lattice, site.OriginX, site.OriginY) };
                if (heights != null) p.Estimate = TrailRating.Estimate(heights, p.Footprint);
                return p;
            }, p =>
            {
                _raster = p.Footprint;
                _estimate = p.Estimate;
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
            if (input.Pressed(ToolKeys.Next) && NextReady) { Go(Step.Review); return; }
            if (_hasTail && !_analysing && !_wantAnalysis) EndsStatus();   // the ground may have become ready
            if (!input.HasGround) { preview.Hide(4); return; }
            var snap = _ctx.Snap(input.Point, SnapTargets.All);
            var at = DesignFrame.ToLocal(snap.At);
            bool onTrail = snap.Snapped || (_raster != null && _raster.Contains(input.Point));
            if (snap.Snapped) preview.Ring(4, at, _ctx.SnapReachMm / 1000f, PreviewInk.Snap);
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
                DropAnalysis();
            }
            else
            {
                if (snap.At == _head.At) { _ctx.Window.SetMessage("The head and tail can't be the same point"); return; }
                _tail = snap; _tailZ = z; _hasTail = true;
                Analyse();
            }
            EndsStatus();
            DrawEnds();
        }

        /// <summary>
        /// The whole trail against the design's earthwork, off the main thread (CF-02): what Break ground would build,
        /// with every built trail and lift it touches folded in order (TL3).
        /// </summary>
        void Analyse()
        {
            DropAnalysis();
            var host = _ctx.Earthwork;
            if (host == null) { EndsStatus(); return; }   // no ground session (unit tests): ends only
            if (!host.Ready) { _wantAnalysis = true; EndsStatus(); return; }
            _wantAnalysis = false;
            _analysing = true;
            EndsStatus();
            var history = _ctx.History;
            long revision = history.Revision;
            var cmd = new AddTrail(revision, _name, _graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                   new[] { new FootprintPart(_strokes.ToArray()) }, _head.At, _tail.At, WorkStage.Built);
            var doc = history.Document;
            var committed = history.Terrain;
            var earthwork = host.Earthwork;
            var heights = host.Heights;
            double interval = host.ContourInterval;
            bool graded = _graded;
            _ctx.Analyse(() =>
            {
                var a = new Analysis { BaseRevision = revision, Graded = graded };
                var after = cmd.Apply(doc);
                if (!after.Accepted) { a.Problem = after.Reason; return a; }
                a.Rebuild = earthwork.Rebuild(after.Document, committed);
                a.Build = a.Rebuild.DetailOf(after.Created[0]) as TrailBuild;
                if (a.Build?.Grade == null) { a.Problem = "No way through the painted area joins the head to the tail."; return a; }
                var pending = new EditedHeights(heights.Source, heights.Lattice, a.Rebuild.Edits);
                a.Rating = TrailRating.Rate(pending, a.Build.Footprint, a.Build.Grade);
                a.Contours = new List<double>();
                PendingContours.Build(heights, pending, a.Rebuild.Tiles, interval, a.Contours);
                return a;
            }, a =>
            {
                _analysing = false;
                _analysis = a;
                _ctx.PendingTerrain = a.Problem == null ? a.Rebuild.Edits : null;
                if (a.Contours != null) host.ShowPending(a.Contours);
                DrawAnalysis();
                if (_step == Step.Review) { BuildReview(); _ctx.Window.ShowReview(_review); }
                else EndsStatus();
            });
        }

        void DropAnalysis()
        {
            _analysis = null;
            _ctx.PendingTerrain = null;
            _ctx.Earthwork?.ClearPending();
            _ctx.Preview.Hide(CentrelineSlot);
            _ctx.Preview.HideFrom(RedSlots);
        }

        // The centreline dashed white (TF-03) and each stretch too steep to bench in red (GR-07).
        void DrawAnalysis()
        {
            var p = _ctx.Preview;
            p.HideFrom(RedSlots);
            var g = _analysis?.Build?.Grade;
            if (g == null) { p.Hide(CentrelineSlot); return; }
            var site = Site;
            int n = g.Stations.Length / 2;
            if (_line.Length < n) _line = new Vector2[n * 2];
            for (int k = 0; k < n; k++)
                _line[k] = new Vector2((float)(g.Stations[2 * k] - site.OriginX), (float)(g.Stations[2 * k + 1] - site.OriginY));
            p.Line(CentrelineSlot, _line, n, PreviewInk.Line);
            if (!g.IsGraded) return;
            int slot = RedSlots;
            for (int k = 0; k < n;)
            {
                if (g.Benched[k]) { k++; continue; }
                int end = k;
                while (end + 1 < n && !g.Benched[end + 1]) end++;
                int a = System.Math.Max(0, k - 1), b = System.Math.Min(n - 1, end + 1);
                var red = new Vector2[b - a + 1];
                System.Array.Copy(_line, a, red, 0, red.Length);
                p.Line(slot++, red, red.Length, PreviewInk.Bad);
                k = end + 1;
            }
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
            if (_analysis?.Build?.Grade != null) { p.Hide(7); return; }   // the centreline replaces the straight line
            _pair[0] = h;
            _pair[1] = t;
            p.Line(7, _pair, 2, PreviewInk.Line);
        }

        const double CubicYardsPerCubicMetre = 1.307950619;

        void BuildReview()
        {
            _review.Clear();
            var a = _analysis;
            var g = a?.Build?.Grade;
            if (g == null)
            {
                // No ground session (unit tests) or no centreline yet: the straight head to tail.
                float run = Vector2.Distance(DesignFrame.ToLocal(_head.At), DesignFrame.ToLocal(_tail.At));
                float drop = _headZ - _tailZ;
                _review.Add("Area", ToolFormat.Area(_raster.AreaSquareMetres), "TR-02")
                    .Add("Head to tail", ToolFormat.Length(run), "TR-03")
                    .Add("Vertical", ToolFormat.Length(Mathf.Abs(drop)))
                    .Reserved("Network", "NW-12: No lift serves this, Leads nowhere (task 10)", ReviewModel.NetworkSlot);
                if (a?.Problem != null) _review.Warn(a.Problem, "TR-03", blocks: true);
                if (drop < 0) _review.Warn("The head is lower than the tail: the trail runs uphill", "TR-03");
                _review.Ready(a?.Problem == null, a?.Problem == null);
                return;
            }
            var r = a.Rating;
            _review.Add("Rating", Rating(r.ValuePercent), "TD-01 v2, TD-02 v1, TD-03")
                .Add("Length", ToolFormat.Length(g.LengthMetres), "TR-03: along the centreline")
                .Add("Vertical", ToolFormat.Length(System.Math.Abs(r.DropMetres)))
                .Add("Mean · steepest", $"{System.Math.Round(r.MeanPercent):0}% · {System.Math.Round(r.MaxPercent):0}%", "TD-02: the steepest 75 ft")
                .Add("Area", ToolFormat.Area(a.Build.Footprint.AreaSquareMetres), "TR-02");
            if (a.Graded)
            {
                double cut = g.CutCubicMetres, fill = g.FillCubicMetres;
                _review.Add("Cut · fill", $"{ToolFormat.Volume(cut)} · {ToolFormat.Volume(fill)}", "GR-08")
                    .Add("Balance", ToolFormat.Volume(cut - fill, signed: true), "GR-08: cut − fill");
                if (g.UngradedStations > 0) _review.Add("Ungraded", ToolFormat.Length(g.UngradedMetres), "GR-07: too steep to bench, kept natural");
            }
            else _review.Add("Grading", "Natural: the ground stays as it is", "TF-06, TL13");
            _review.Add("Trees cleared", ToolFormat.Area(g.ClearedCells), "CL-01, CL-02: the ragged edge keeps some at the treeline")
                .Reserved("Network", "NW-12: No lift serves this, Leads nowhere (task 10)", ReviewModel.NetworkSlot);
            if (a.Graded && g.UngradedStations > 0)
                _review.Warn($"{ToolFormat.Length(g.UngradedMetres)} is too steep to bench and stays natural (red)", "GR-07");
            if (r.DropMetres < 0) _review.Warn("The head is lower than the tail: the trail runs uphill", "TR-03");
            if (g.LengthMetres < 30.48) _review.Warn("A trail is at least 100 ft long", "TR-04", blocks: true);
            _review.Ready(true, true);
        }

        public ToolBackOut Escape()
        {
            switch (_step)
            {
                case Step.Review: Go(Step.Ends); return ToolBackOut.Stepped;
                case Step.Ends:
                    if (_hasTail) { _hasTail = false; DropAnalysis(); }
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
            bool built = trail?.Stage == WorkStage.Built;
            if (built && _analysis?.Rebuild != null) _ctx.Earthwork?.Earthwork?.Accept(_analysis.Rebuild);
            _ctx.Notice?.Invoke(_name + (built ? ": ground broken" : ": saved as a plan"));
            _strokes.Clear();
            _raster = null;
            _analysis = null;
            _ctx.Earthwork?.ClearPending();
            _ctx.Preview.HideFrom(0);
            _name = NextName(result.Document);
            _ctx.Window.SetName(_name);
            _nameField.SetValueWithoutNotify(_name);
            Go(Step.Paint);
        }

        public void Disarm()
        {
            _painting = false;
            _ctx?.Earthwork?.ClearPending();
            _ctx = null;
        }

        /// <summary>Picks Natural or Graded as the window's switch does (tests and captures).</summary>
        public void ChooseGraded(bool graded)
        {
            _setGrading(graded ? 1 : 0);
            SetGraded(graded);
        }

        // For tests and captures.
        public int CurrentStep => (int)_step;
        public int Strokes => _strokes.Count;
        public int BrushFeet => _feet;
        public TrailFootprint Raster => _raster;
        public bool Analysing => _analysing || _wantAnalysis;
        public bool Graded => _graded;
        public Analysis Result => _analysis;
    }
}
