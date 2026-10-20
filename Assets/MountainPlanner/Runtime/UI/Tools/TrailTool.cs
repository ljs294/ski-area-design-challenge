using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    public enum TrailToolMode { New, Edit, Remove }

    /// <summary>
    /// The Trails tab's tools (TF-01 to TF-09; P3-06's tool contract, task 08's analysis), one class in three modes.
    ///
    /// <b>New:</b> step 1 paints the trail's area: left-drag paints, Shift-drag erases, [ and ] change the brush, Ctrl+Z
    /// undoes a stroke; after each stroke the footprint, its area and the estimated rating (TD-06) are worked out off the
    /// main thread (CF-02, CF-03), and a second separate patch blocks Next. Step 2 takes the head and the tail, each
    /// snapping within 30 ft (TR-05); then the whole trail is analysed against the design's earthwork (TL3): the automatic
    /// centreline (TR-03), the grade when the player chose Graded (GR-14: a new trail starts Natural), clearing (CL-01,
    /// CL-02, CL-08), the rating (TD-01 v2, TD-02), the yellow pending contours (CF-08) and red stretches too steep to bench
    /// (GR-07). Break ground submits <see cref="AddTrail"/> with the edits.
    ///
    /// <b>Edit</b> (TF-07, owner TL16): click a trail; Repaint and Move ends reopen its own steps, Natural or Graded and
    /// Rename go straight to review; Apply submits one <see cref="EditTrail"/>, and grading runs again only where something
    /// changed (TL3). The trail keeps its setting (GR-14). <b>Remove</b>: click a trail; its review restores the ground and
    /// trees (CL-07) with <see cref="DeleteTrail"/>.
    /// </summary>
    public class TrailTool : IDesignTool
    {
        static readonly string[] NewSteps = { "Paint", "Head and tail", "Review" };
        static readonly string[] EditSteps = { "Pick", "Paint", "Head and tail", "Review" };
        static readonly string[] RemoveSteps = { "Pick", "Remove" };
        const int MinFeet = 25, MaxFeet = 400, StepFeet = 5;

        // The brush width is remembered for the session (the mockup's S.trailWidth).
        static int _lastFeet = 100;

        enum Step { Pick, Paint, Ends, Review }

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

        readonly TrailToolMode _mode;
        ToolContext _ctx;
        Step _step;
        int _feet;
        bool _graded;
        string _name;
        Trail _target;
        bool _repaintable;
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
        UnityEngine.UIElements.Button[] _editButtons;
        TrailProfileStrip _strip;
        // Footprints of the design's trails, for picking (Edit, Remove); kept while each trail is unchanged.
        readonly Dictionary<TrailId, (Trail trail, TrailFootprint fp)> _picks = new Dictionary<TrailId, (Trail, TrailFootprint)>();
        Trail _hover;

        // Preview slots: 0 brush, 1 live stroke, 4 snap, 5–7 ends, 8 centreline, 10–199 strokes, 200+ red stretches.
        const int CentrelineSlot = 8, StrokeSlots = 10, RedSlots = 200;

        protected TrailTool(TrailToolMode mode) { _mode = mode; }

        public string Id => _mode == TrailToolMode.New ? NewTrailTool.ToolId : _mode == TrailToolMode.Edit ? EditTrailTool.ToolId : RemoveTrailTool.ToolId;
        public string Name => _mode == TrailToolMode.New ? "New trail" : _mode == TrailToolMode.Edit ? "Edit trail" : "Remove trail";
        public bool HasWork => _mode == TrailToolMode.New ? _strokes.Count > 0 || _painting : _target != null && _step != Step.Pick;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();   // the ground's session, if this area hasn't one yet (task 08)
            _feet = _lastFeet;
            _graded = false;           // GR-14: grading only when the player chooses it
            var w = context.Window;
            if (_mode == TrailToolMode.New)
            {
                _name = NextName(context.History.Document);
                w.SetName(_name);
                _nameField = w.AddText("name", "Name", _name, DesignRules.NameMaxLength, Rename);
                AddBrushAndGrading(w);
                w.AddNote("kind", "Run · trees cleared", "TR-06: Glades and Zones, and Thin or no clearing, come later");
                w.AddStrip(_strip = new TrailProfileStrip());
                w.SetSteps(NewSteps);
                SetFeet(_feet);
                Go(Step.Paint);
                return;
            }
            w.SetName(null);
            if (_mode == TrailToolMode.Edit)
            {
                _nameField = w.AddText("name", "Name", "", DesignRules.NameMaxLength, Rename);
                AddBrushAndGrading(w);
                var change = w.AddButtons("edit", new[] { "Repaint", "Move ends", "Rename" }, EditAction,
                    new[] { "TF-07: the trail's strokes open in Paint", "TF-07: click a new head, then a new tail", "TF-07: keep the name typed above" });
                var network = w.AddButtons("network", new[] { "Split", "Join" }, _ => { },
                    new[] { "NW-10: Split and Join come with the network (task 10)", "NW-10: Split and Join come with the network (task 10)" });
                _editButtons = new[] { change[0], change[1], change[2], network[0], network[1] };
                w.AddStrip(_strip = new TrailProfileStrip());
                w.SetSteps(EditSteps);
                SetFeet(_feet);
            }
            else w.SetSteps(RemoveSteps);
            Go(Step.Pick);
        }

        void AddBrushAndGrading(ToolWindow w)
        {
            _setWidth = w.AddSlider("brush", "Brush", MinFeet, MaxFeet, StepFeet, _feet, SetFeet, "TR-01: 25–400 ft, in 5 ft steps; one trail can mix widths");
            _setGrading = w.AddSwitch("grading", "Grading", new[] { "Natural", "Graded" }, 0, i => { _setGrading(i); SetGraded(i == 1); },
                "TR-06, TF-06, GR-14: Natural keeps the ground and clears the trees; Graded benches the trail level across");
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
            _setWidth?.Invoke(_feet, ToolFormat.Feet(_feet));
        }

        void SetGraded(bool graded)
        {
            if (_graded == graded) return;
            _graded = graded;
            if (_mode == TrailToolMode.Edit && _target != null && _step == Step.Pick) { Go(Step.Review); return; }
            if (_step != Step.Paint && _hasHead && _hasTail) Analyse();
        }

        // Edit's buttons (TF-07): Repaint, Move ends, Rename; Split and Join wait for task 10.
        void EditAction(int i)
        {
            if (_target == null) return;
            switch (i)
            {
                case 0: if (_repaintable) Go(Step.Paint); break;
                case 1: _hasHead = _hasTail = false; Go(Step.Ends); break;
                case 2: Go(Step.Review); break;
            }
        }

        int WindowStep => _mode == TrailToolMode.New ? (int)_step - 1 : _mode == TrailToolMode.Remove ? (_step == Step.Pick ? 0 : 1) : (int)_step;

        void Go(Step step)
        {
            _step = step;
            var w = _ctx.Window;
            w.SetStep(WindowStep);
            bool painting = step == Step.Paint;
            _ctx.Claim(GameAction.BrushNarrower, painting);
            _ctx.Claim(GameAction.BrushWider, painting);
            _ctx.Claim(GameAction.UndoStroke, painting);
            _ctx.Claim(GameAction.Erase, painting);
            w.SetGoLabel(_mode == TrailToolMode.Remove ? (_target != null ? "Remove " + _target.Name : "Remove")
                : _mode == TrailToolMode.Edit && _target?.Stage == WorkStage.Built ? "Apply" : null);
            switch (step)
            {
                case Step.Pick:
                    DropAnalysis();
                    w.SetKeys();
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    w.SetNext(false, false);
                    PickStatus();
                    break;
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
                    if (_mode != TrailToolMode.New && (_analysis == null || _wantAnalysis || _analysing)) Analyse();
                    BuildReview();
                    w.ShowReview(_review);
                    break;
            }
            DrawEnds();
            if (step != Step.Pick && _mode != TrailToolMode.New) DrawTarget();
        }

        void PickStatus()
        {
            var w = _ctx.Window;
            bool edit = _mode == TrailToolMode.Edit;
            if (_target == null)
            {
                w.SetHint(edit ? "Click a trail to edit it." : "Click the trail to remove.");
                w.SetMessage(null);
                SetEditButtons(false);
                return;
            }
            string facts = Facts(_target);
            w.SetHint(edit ? $"{facts} The rating comes from the ground: regrade or repaint to change it. Grading runs again only where something changes."
                           : facts);
            w.SetMessage(edit && !_repaintable ? "This trail was split or joined: Repaint waits for the network tools (task 10)" : null);
            SetEditButtons(true);
        }

        void SetEditButtons(bool on)
        {
            if (_editButtons == null) return;
            _editButtons[0].SetEnabled(on && _repaintable);
            _editButtons[1].SetEnabled(on);
            _editButtons[2].SetEnabled(on);
            _editButtons[3].SetEnabled(false);
            _editButtons[4].SetEnabled(false);
        }

        // A built trail's card line (TF-08): length, vertical and rating, from its last grade; its profile in the strip.
        string Facts(Trail t)
        {
            var build = _ctx.Earthwork?.Earthwork?.DetailOf(t.Id.Value) as TrailBuild;
            var heights = _ctx.Earthwork?.Heights;
            if (build?.Grade == null || heights == null) return $"{t.Name}.";
            var r = TrailRating.Rate(heights, build.Footprint, build.Grade);
            _strip?.Show(build.Grade, r);
            return $"{t.Name}: {Rating(r.ValuePercent)}, {ToolFormat.Length(build.Grade.LengthMetres)}, {ToolFormat.Length(System.Math.Abs(r.DropMetres))} vertical.";
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
            if (_step == Step.Pick) TickPick(input);
            else if (_step == Step.Paint) TickPaint(input);
            else if (_step == Step.Ends) TickEnds(input);
        }

        // ---------- Pick (Edit, Remove) ----------

        /// <summary>The trail under a design point: the first, in ID order, whose painted footprint holds it.</summary>
        Trail TrailAt(PointMm p)
        {
            var site = Site;
            foreach (var t in _ctx.History.Document.Trails)
            {
                if (!_picks.TryGetValue(t.Id, out var e) || !ReferenceEquals(e.trail, t))
                    _picks[t.Id] = e = (t, TrailFootprint.Build(t.Parts, site.Lattice, site.OriginX, site.OriginY));
                if (e.fp.Contains(p)) return t;
            }
            return null;
        }

        void TickPick(in ToolInput input)
        {
            if (!input.HasGround) return;
            var t = TrailAt(input.Point);
            if (t != _hover)
            {
                _hover = t;
                DrawOutline(t ?? _target);
            }
            if (input.Click && t != null) Select(t);
            if (input.Pressed(ToolKeys.Delete) && _mode == TrailToolMode.Remove && _target != null) Go(Step.Review);
        }

        void DrawOutline(Trail t)
        {
            var preview = _ctx.Preview;
            int slot = StrokeSlots;
            if (t != null)
                foreach (var part in t.Parts)
                    foreach (var s in part.Strokes)
                    {
                        if (slot + 1 >= RedSlots) break;
                        DrawStroke(slot, s.Points, s.WidthMm / DesignUnits.FiveFeetMm * 5, s.Erase);
                        slot += 2;
                    }
            for (int k = slot; k < RedSlots; k++) preview.Hide(k);
        }

        void Select(Trail t)
        {
            _target = t;
            _name = t.Name;
            _ctx.Window.SetName(t.Name);
            _nameField?.SetValueWithoutNotify(t.Name);
            _graded = t.Settings.Grading == TrailGrading.Graded;
            _setGrading?.Invoke(_graded ? 1 : 0);
            _repaintable = t.Parts.Count == 1 && t.Parts[0].Cuts.Count == 0;
            _strokes.Clear();
            foreach (var part in t.Parts) _strokes.AddRange(part.Strokes);
            if (t.HasEnds)
            {
                _head = SnapHit.None(t.Head.Value);
                _tail = SnapHit.None(t.Tail.Value);
                _hasHead = _hasTail = true;
                _headZ = _ctx.HeightAt(DesignFrame.ToLocal(_head.At));
                _tailZ = _ctx.HeightAt(DesignFrame.ToLocal(_tail.At));
            }
            _analysis = null;
            Restroke();
            DrawOutline(t);
            if (_mode == TrailToolMode.Remove) Go(Step.Review);
            else PickStatus();
        }

        // ---------- Paint ----------

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
                    _ctx.Paint?.Stamp(at, radius, _liveErase);   // the brush paints where it lands (owner, 2026-10-19)
                }
                else if (_painting && input.Drag && inside && Vector2.Distance(at, _lastXz) >= Mathf.Max(3f, radius / 2))
                {
                    // Stamps every quarter brush along the move, so a quick drag paints a solid band, not beads.
                    if (_ctx.Paint != null)
                    {
                        float step = Mathf.Max(1f, radius / 2), d = Vector2.Distance(_lastXz, at);
                        int n = Mathf.Max(1, Mathf.CeilToInt(d / step));
                        for (int k = 1; k <= n; k++) _ctx.Paint.Stamp(Vector2.Lerp(_lastXz, at, k / (float)n), radius, _liveErase);
                    }
                    _live.Add(input.Point);
                    _lastXz = at;
                    if (_ctx.Paint == null) DrawStroke(1, _live, _feet, _liveErase);
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
            DrawStrokes();
            _raster = null;
            _estimate = double.NaN;
            if (_strokes.Count == 0) { _analysing = false; _ctx.Paint?.Clear(); PaintStatus(); return; }
            _analysing = true;
            if (_step == Step.Paint) PaintStatus();
            var parts = Parts();
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
                // The painted ground, exactly as the trail will store it: the trail orange, a patch apart red (TF-02).
                if (_mode != TrailToolMode.Remove) _ctx.Paint?.Show(p.Footprint);
                if (_step == Step.Paint) PaintStatus();
            });
        }

        void DrawStrokes()
        {
            int slot = StrokeSlots;
            if (_ctx.Paint != null && _mode != TrailToolMode.Remove)
            {
                // The paint on the ground shows the strokes (TrailPaintMap): no edge lines.
                for (int k = slot; k < RedSlots; k++) _ctx.Preview.Hide(k);
                return;
            }
            foreach (var s in _strokes)
            {
                if (slot + 1 >= RedSlots) break;
                DrawStroke(slot, s.Points, s.WidthMm / DesignUnits.FiveFeetMm * 5, s.Erase);
                slot += 2;
            }
            for (int k = slot; k < RedSlots; k++) _ctx.Preview.Hide(k);
        }

        void DrawTarget() => DrawStrokes();

        // The footprint as the trail will store it: its original parts while unrepainted (a split trail keeps its cuts),
        // or one part of the strokes as painted.
        IReadOnlyList<FootprintPart> Parts() =>
            _target != null && !_repaintable ? _target.Parts : new[] { new FootprintPart(_strokes.ToArray()) };

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

        // ---------- Head and tail ----------

        void TickEnds(in ToolInput input)
        {
            var preview = _ctx.Preview;
            if (input.Pressed(ToolKeys.Next) && NextReady) { Go(Step.Review); return; }
            if (_hasTail && !_analysing && !_wantAnalysis) EndsStatus();   // the ground may have become ready
            if (!input.HasGround) { preview.Hide(4); return; }
            var snap = _ctx.Snap(input.Point, SnapTargets.All, _target?.Id.Value ?? 0);
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

        DesignCommand Command(long revision, WorkStage stage)
        {
            var settings = _graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun;
            switch (_mode)
            {
                case TrailToolMode.New: return new AddTrail(revision, _name, settings, Parts(), _head.At, _tail.At, stage);
                case TrailToolMode.Edit:
                    return new EditTrail(revision, _target.Id, _name, Parts(), _head.At, _tail.At, settings,
                                         stage == WorkStage.Built || _target.Stage == WorkStage.Built ? WorkStage.Built : WorkStage.Plan);
                default: return new DeleteTrail(revision, _target.Id);
            }
        }

        /// <summary>
        /// The whole trail against the design's earthwork, off the main thread (CF-02): what Break ground (or Apply, or
        /// Remove) would build, with every built trail and lift it touches folded in order (TL3).
        /// </summary>
        void Analyse()
        {
            DropAnalysis();
            var host = _ctx.Earthwork;
            if (host == null) { EndsStatus(); return; }   // no ground session (unit tests): ends only
            if (!host.Ready) { _wantAnalysis = true; if (_step == Step.Ends) EndsStatus(); return; }
            _wantAnalysis = false;
            _analysing = true;
            if (_step == Step.Ends) EndsStatus();
            var history = _ctx.History;
            long revision = history.Revision;
            var cmd = Command(revision, WorkStage.Built);
            ulong key = _target?.Id.Value ?? 0;
            bool remove = _mode == TrailToolMode.Remove;
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
                var pending = new EditedHeights(heights.Source, heights.Lattice, a.Rebuild.Edits);
                // The contours on other threads while the rating runs here (CF-09's budget on a 1 km run, TL18).
                var contours = System.Threading.Tasks.Task.Run(() => Contours(heights, pending, a.Rebuild.Changed, interval));
                if (remove) { a.Contours = contours.Result; return a; }
                ulong id = key != 0 ? key : after.Created[0];
                // A change that moves no ground (a rename) regrades nothing: the trail's last grade stands.
                a.Build = (a.Rebuild.DetailOf(id) ?? earthwork.DetailOf(id)) as TrailBuild;
                if (a.Build?.Grade == null) { a.Problem = "No way through the painted area joins the head to the tail."; return a; }
                a.Rating = TrailRating.Rate(pending, a.Build.Footprint, a.Build.Grade);
                a.Contours = contours.Result;
                return a;
            }, a =>
            {
                _analysing = false;
                _analysis = a;
                _ctx.PendingTerrain = a.Problem == null ? a.Rebuild.Edits : null;
                if (a.Contours != null) host.ShowPending(a.Contours);
                DrawAnalysis();
                if (a.Build?.Grade != null && a.Rating != null) _strip?.Show(a.Build.Grade, a.Rating);
                if (_step == Step.Review) { BuildReview(); _ctx.Window.ShowReview(_review); }
                else if (_step == Step.Ends) EndsStatus();
            });
        }

        /// <summary>
        /// The pending contours (CF-08), a tile at a time across threads, joined in tile order: the same segments as one
        /// <see cref="PendingContours.Build"/> over all of them.
        /// </summary>
        public static List<double> Contours(EditedHeights current, EditedHeights pending, IReadOnlyList<EditTileKey> tiles, double interval)
        {
            var parts = new List<double>[tiles.Count];
            System.Threading.Tasks.Parallel.For(0, tiles.Count, k =>
            {
                var part = new List<double>();
                PendingContours.Build(current, pending, new[] { tiles[k] }, interval, part);
                parts[k] = part;
            });
            var all = new List<double>();
            foreach (var part in parts) all.AddRange(part);
            return all;
        }

        void DropAnalysis()
        {
            _analysis = null;
            _strip?.ShowWaiting(_step == Step.Paint || _step == Step.Pick
                ? "Paint the trail, then set its head and tail: the trail shows here from the side."
                : "Click the head, then the tail: the trail shows here from the side.");
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
            if (_step == Step.Paint || _step == Step.Pick || !_hasHead) { p.Hide(5); p.Hide(6); p.Hide(7); return; }
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

        // ---------- Review ----------

        void BuildReview()
        {
            _review.Clear();
            var a = _analysis;
            if (_mode == TrailToolMode.Remove)
            {
                _review.Add("Trail", _target?.Name ?? "—")
                    .Add("Ground and trees", _target?.Stage == WorkStage.Built ? "back as they were" : "unchanged: it's only a plan", "CL-07, TF-07")
                    .Add("Undo", "Ctrl+Z brings it back", "CF-05");
                if (a?.Problem != null) _review.Warn(a.Problem, null, blocks: true);
                _review.Ready(a != null && a.Problem == null && !_analysing || _ctx.Earthwork == null, false);
                return;
            }
            var g = a?.Build?.Grade;
            if (g == null)
            {
                // No ground session (unit tests), or the analysis isn't in: the straight head to tail.
                float run = Vector2.Distance(DesignFrame.ToLocal(_head.At), DesignFrame.ToLocal(_tail.At));
                float drop = _headZ - _tailZ;
                _review.Add("Area", _raster != null ? ToolFormat.Area(_raster.AreaSquareMetres) : "—", "TR-02")
                    .Add("Head to tail", ToolFormat.Length(run), "TR-03")
                    .Add("Vertical", ToolFormat.Length(Mathf.Abs(drop)))
                    .Reserved("Network", "NW-12: No lift serves this, Leads nowhere (task 10)", ReviewModel.NetworkSlot);
                if (a?.Problem != null) _review.Warn(a.Problem, "TR-03", blocks: true);
                if (drop < 0) _review.Warn("The head is lower than the tail: the trail runs uphill", "TR-03");
                bool ready = _ctx.Earthwork == null || (a != null && a.Problem == null);
                _review.Ready(ready, ready && PlanAllowed);
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
            else _review.Add("Grading", "natural, none", "TF-06, GR-14");
            _review.Add("Trees cleared", ToolFormat.Area(g.ClearedCells), "CL-01, CL-02: the ragged edge keeps some at the treeline")
                .Reserved("Network", "NW-12: No lift serves this, Leads nowhere (task 10)", ReviewModel.NetworkSlot);
            if (a.Graded && g.UngradedStations > 0)
                _review.Warn($"{ToolFormat.Length(g.UngradedMetres)} is too steep to bench and stays natural (red)", "GR-07");
            if (r.DropMetres < 0) _review.Warn("The head is lower than the tail: the trail runs uphill", "TR-03");
            if (g.LengthMetres < 30.48) _review.Warn("A trail is at least 100 ft long", "TR-04", blocks: true);
            _review.Ready(true, PlanAllowed);
        }

        // Save plan: a new trail or a planned one; a built trail stays built (EditTrail).
        bool PlanAllowed => _mode == TrailToolMode.New || (_mode == TrailToolMode.Edit && _target?.Stage == WorkStage.Plan);

        public ToolBackOut Escape()
        {
            switch (_step)
            {
                case Step.Review:
                    if (_mode == TrailToolMode.New) Go(Step.Ends);
                    else if (_mode == TrailToolMode.Remove) { _target = null; Go(Step.Pick); }
                    else Go(Step.Pick);
                    return ToolBackOut.Stepped;
                case Step.Ends:
                    if (_hasTail) { _hasTail = false; DropAnalysis(); }
                    else if (_hasHead) _hasHead = false;
                    else if (_mode == TrailToolMode.New || _repaintable) { Go(Step.Paint); return ToolBackOut.Stepped; }
                    else { Go(Step.Pick); return ToolBackOut.Stepped; }
                    EndsStatus();
                    DrawEnds();
                    return ToolBackOut.Stepped;
                case Step.Paint:
                    if (_painting)
                    {
                        _painting = false;
                        _live.Clear();
                        Restroke();
                        return ToolBackOut.Stepped;
                    }
                    if (_mode == TrailToolMode.Edit) { Select(_target); Go(Step.Pick); return ToolBackOut.Stepped; }
                    return ToolBackOut.Close;
                default:
                    if (_target != null) { _target = null; _hover = null; DrawOutline(null); PickStatus(); return ToolBackOut.Stepped; }
                    return ToolBackOut.Close;
            }
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (_step != Step.Review) return null;
            if (_mode != TrailToolMode.New && _target == null) return null;
            return Command(baseRevision, stage);
        }

        public void Committed(CommandResult result)
        {
            if (_analysis?.Rebuild != null && _ctx.History.Terrain == _analysis.Rebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_analysis.Rebuild);
            _ctx.Earthwork?.ClearPending();
            _ctx.Preview.HideFrom(0);
            _analysis = null;
            _strokes.Clear();
            _raster = null;
            if (_mode == TrailToolMode.New)
            {
                var trail = result.Document.FindTrail(new TrailId(result.Created[0]));
                _ctx.Notice?.Invoke(_name + (trail?.Stage == WorkStage.Built ? ": ground broken" : ": saved as a plan"));
                _name = NextName(result.Document);
                _ctx.Window.SetName(_name);
                _nameField.SetValueWithoutNotify(_name);
                Go(Step.Paint);
                return;
            }
            _ctx.Notice?.Invoke(_mode == TrailToolMode.Remove ? _target.Name + " removed" : _name + ": changed");
            _target = null;
            _hover = null;
            _ctx.Window.SetName(null);
            Go(Step.Pick);
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
            _setGrading?.Invoke(graded ? 1 : 0);
            SetGraded(graded);
        }

        /// <summary>Selects a trail as a click on it does (Edit, Remove; tests and captures).</summary>
        public void ChooseTrail(Trail t) { if (_step == Step.Pick && t != null) Select(t); }

        /// <summary>Edit's buttons by index (0 Repaint, 1 Move ends, 2 Rename; tests and captures).</summary>
        public void PressEdit(int i) => EditAction(i);

        // For tests and captures.
        public int CurrentStep => WindowStep;
        public int Strokes => _strokes.Count;
        public int BrushFeet => _feet;
        public TrailFootprint Raster => _raster;
        public bool Analysing => _analysing || _wantAnalysis;
        public bool Graded => _graded;
        public Analysis Result => _analysis;
        public Trail Target => _target;
    }

    /// <summary>Toolbox › Trails › New (TF-01 to TF-05).</summary>
    public sealed class NewTrailTool : TrailTool
    {
        public const string ToolId = "new-trail";
        public NewTrailTool() : base(TrailToolMode.New) { }
    }

    /// <summary>Toolbox › Trails › Edit (TF-07).</summary>
    public sealed class EditTrailTool : TrailTool
    {
        public const string ToolId = "edit-trail";
        public EditTrailTool() : base(TrailToolMode.Edit) { }
    }

    /// <summary>Toolbox › Trails › Remove (TF-07, CL-07).</summary>
    public sealed class RemoveTrailTool : TrailTool
    {
        public const string ToolId = "remove-trail";
        public RemoveTrailTool() : base(TrailToolMode.Remove) { }
    }
}
