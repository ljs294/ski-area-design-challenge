using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>The Terrain tab's tool ids (TB-01).</summary>
    public static class TerrainIds
    {
        public const string Raise = "terrain-raise", Flatten = "terrain-flatten", Smooth = "terrain-smooth", Slope = "terrain-slope";
    }

    public enum TerrainToolMode { RaiseLower, Flatten, Smooth, Slope }

    /// <summary>
    /// The Terrain tab's brushes (task 15; TB-01 to TB-14, owner BR1–BR13), one class in four modes, Cities: Skylines 2's set.
    ///
    /// <b>Paint:</b> the left button paints (Raise/Lower: the right lowers; Flatten and Slope: a right click takes the target
    /// elevation under the pointer). A stroke lays dabs 30 times a second at the pointer, filled in along a fast move
    /// (TB-03), through <see cref="BrushRun"/>, the replay's own code, so the ground moves live under the brush (BR5) and
    /// what's confirmed is what was seen. Strokes stay pending; Ctrl+Z takes one back. <b>Review</b> (Next or Enter): the
    /// session folded through the design's earthwork after trails and lifts, with its cut and fill, trees cleared, towers
    /// re-heighted under their fixed heads and where the brush was held (TB-09); <b>Confirm</b> puts every pending stroke in
    /// as one <see cref="AddTerrainStrokes"/>, one undo step (BR7). Esc or ✕ with strokes pending asks, then puts the ground
    /// back. The Contours layer is on while a brush is armed (BR13).
    /// </summary>
    public class TerrainBrushTool : IDesignTool, IOwnsRightButton
    {
        static readonly string[] Steps = { "Paint", "Review" };
        const int MinFeet = 10, MaxFeet = 400, StepFeet = 5;
        const float TickSeconds = 1f / TerrainBrush.TicksPerSecond;

        // TB-07: the ratings that pick a slope's band (TD-03), with the grade each starts at.
        static readonly (string Symbol, string Name, int Low, int High, int Start)[] Bands =
        {
            ("●", "Easiest", 1, 24, 12), ("■", "More difficult", 25, 39, 32), ("◆", "Most difficult", 40, 59, 48), ("◆◆", "Experts only", 60, 100, 70),
        };

        // The pane's settings carry between brushes for the session (as the trail brush's width does).
        static int _lastFeet = 60, _lastStrength = 50;

        enum Step { Paint, Review }

        readonly TerrainToolMode _mode;
        ToolContext _ctx;
        Step _step;
        int _feet, _strength, _band, _grade;
        int? _targetMm;

        // The session: strokes painted and not yet confirmed, and the ground with them on it.
        readonly List<StrokePainting> _session = new List<StrokePainting>();
        TerrainEdits _base, _pending;
        FixedGround _fixed;
        double _cut, _fill;
        int _cleared;
        readonly HashSet<Hold> _held = new HashSet<Hold>();

        // The stroke in hand.
        BrushRun _run;
        List<BrushDab> _dabs;
        BrushSettings _settings;
        bool _rightStroke;
        PointMm _lastDab;
        float _clock;
        long _pathMm;
        PointRect _liveDirty = PointRect.Empty;

        // The review.
        DesignEarthwork.Result _rebuild;
        AddTerrainStrokes _command;
        bool _analysing;
        readonly ReviewModel _review = new ReviewModel();
        Action<int, string> _setFeet, _setStrength, _setGrade;
        Action<int> _setBand;
        UnityEngine.UIElements.Label _targetNote;

        protected TerrainBrushTool(TerrainToolMode mode) { _mode = mode; }

        public string Id => _mode switch
        {
            TerrainToolMode.RaiseLower => TerrainIds.Raise, TerrainToolMode.Flatten => TerrainIds.Flatten,
            TerrainToolMode.Smooth => TerrainIds.Smooth, _ => TerrainIds.Slope,
        };

        public string Name => _mode switch
        {
            TerrainToolMode.RaiseLower => "Raise or lower ground", TerrainToolMode.Flatten => "Flatten ground",
            TerrainToolMode.Smooth => "Smooth ground", _ => "Slope ground",
        };

        public bool HasWork => _session.Count > 0 || _run != null;

        /// <summary>The strokes pending (tests).</summary>
        public int Pending => _session.Count;

        /// <summary>The ground with the pending strokes on it (tests).</summary>
        public TerrainEdits PendingGround => _pending;

        /// <summary>The pane's running bill, or the review once Break ground opens it (tests, captures).</summary>
        public ReviewModel Bill => _review;

        /// <summary>The review is being worked out (tests, captures).</summary>
        public bool Analysing => _analysing;

        /// <summary>The review is ready to confirm (tests, captures).</summary>
        public bool ReviewReady => _step == Step.Review && !_analysing && _rebuild != null;

        /// <summary>The pane's diameter, feet (tests, captures).</summary>
        public int BrushFeet => _feet;

        /// <summary>Sets the pane's diameter and strength (captures; the player uses the sliders and keys).</summary>
        public void SetBrush(int feet, int strengthPercent) { SetFeet(feet); SetStrength(strengthPercent); }

        /// <summary>Picks a slope's rating band (0 Easiest … 3 Experts only) and grade (captures).</summary>
        public void SetSlope(int band, int grade) { SetBand(band); SetGrade(grade); }

        bool UsesTarget => _mode == TerrainToolMode.Flatten || _mode == TerrainToolMode.Slope;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            _ctx.LiveGround?.ContoursWhileArmed(true);   // BR13
            _feet = _lastFeet;
            _strength = _lastStrength;
            _band = 0;
            _grade = Bands[0].Start;
            var w = context.Window;
            w.SetName(null);
            _setFeet = w.AddSlider("diameter", "Diameter", MinFeet, MaxFeet, StepFeet, _feet, SetFeet, "TB-02: 10–400 ft, in 5 ft steps");
            _setStrength = w.AddSlider("strength", "Strength", 1, 100, 1, _strength, SetStrength, "TB-02: 1–100%");
            if (UsesTarget) _targetNote = w.AddNote("target", "", "TB-05, TB-07: right-click the ground; Page Up / Page Down 1 ft (Shift 10 ft)");
            if (_mode == TerrainToolMode.Slope)
            {
                var names = new string[Bands.Length];
                for (int i = 0; i < Bands.Length; i++) names[i] = Bands[i].Symbol;
                _setBand = w.AddSwitch("rating", "Slope", names, 0, SetBand, "TB-07, TD-03: the rating picks the band");
                _setGrade = w.AddSlider("grade", "Grade", 1, 100, 1, _grade, SetGrade, "TB-07: a grade inside the band (Shift , and Shift .)");
            }
            w.SetSteps(Steps);
            SetFeet(_feet);
            SetStrength(_strength);
            if (_mode == TerrainToolMode.Slope) SetBand(0);
            ShowTarget();
            Go(Step.Paint);
        }

        // ---------- the pane ----------

        void SetFeet(int feet)
        {
            _feet = _lastFeet = Mathf.Clamp((feet + StepFeet / 2) / StepFeet * StepFeet, MinFeet, MaxFeet);
            _setFeet?.Invoke(_feet, ToolFormat.Feet(_feet));
        }

        void SetStrength(int percent)
        {
            _strength = _lastStrength = Mathf.Clamp(percent, 1, 100);
            _setStrength?.Invoke(_strength, _strength + "%");
        }

        void SetBand(int band)
        {
            _band = Mathf.Clamp(band, 0, Bands.Length - 1);
            _setBand?.Invoke(_band);
            SetGrade(Bands[_band].Start);
        }

        void SetGrade(int grade)
        {
            var b = Bands[_band];
            _grade = Mathf.Clamp(grade, b.Low, b.High);
            _setGrade?.Invoke(_grade, $"{b.Symbol} {_grade}%");
        }

        void ShowTarget()
        {
            if (_targetNote == null) return;
            _targetNote.text = _targetMm.HasValue
                ? $"Target {UnitFormat.Elevation(_targetMm.Value / 1000.0, DisplayUnits.Current)} · right-click to set"
                : "Target: right-click the ground to set it";
        }

        int RadiusMm => _feet * DesignUnits.FiveFeetMm / 10;   // half the diameter, in 2½ ft steps

        BrushSettings SettingsFor(bool lower, int startMm) => _mode switch
        {
            TerrainToolMode.RaiseLower => BrushSettings.RaiseOrLower(lower, RadiusMm, TerrainBrush.StepFor(_strength)),
            TerrainToolMode.Flatten => BrushSettings.Flatten(RadiusMm, TerrainBrush.StrengthFor(_strength), _targetMm ?? startMm),
            TerrainToolMode.Smooth => BrushSettings.Smooth(RadiusMm, TerrainBrush.StrengthFor(_strength)),
            _ => BrushSettings.Slope(RadiusMm, TerrainBrush.StrengthFor(_strength), startMm, _targetMm ?? startMm, _grade * 10),
        };

        // ---------- steps ----------

        void Go(Step step)
        {
            _step = step;
            var w = _ctx.Window;
            w.SetStep((int)step);
            bool painting = step == Step.Paint;
            _ctx.Claim(GameAction.BrushNarrower, painting);
            _ctx.Claim(GameAction.BrushWider, painting);
            _ctx.Claim(GameAction.BrushWeaker, painting);
            _ctx.Claim(GameAction.BrushStronger, painting);
            _ctx.Claim(GameAction.UndoStroke, painting);
            _ctx.Claim(GameAction.TargetUp, painting && UsesTarget);
            _ctx.Claim(GameAction.TargetDown, painting && UsesTarget);
            if (painting)
            {
                var keys = new List<(GameAction, string)> { (GameAction.BrushNarrower, ""), (GameAction.BrushWider, "diameter"), (GameAction.BrushWeaker, ""), (GameAction.BrushStronger, "strength") };
                if (UsesTarget) { keys.Add((GameAction.TargetUp, "")); keys.Add((GameAction.TargetDown, "target")); }
                keys.Add((GameAction.UndoStroke, "undoes a stroke"));
                w.SetKeys(keys.ToArray());
                w.SetGoLabel(null);
                w.SetCommitText("Break ground", "Back");
                w.SetPlanShown(false);
                w.SetNext(false, false);
                PaintStatus();
            }
            else
            {
                w.SetHint(null);
                w.SetKeys();
                w.SetCommitText("Confirm", "Back");
                w.SetPlanShown(true);
                w.SetPlanAlwaysLive(true);
                Review();
            }
        }

        void PaintStatus()
        {
            var w = _ctx.Window;
            w.SetHint(_mode switch
            {
                TerrainToolMode.RaiseLower => "Left raises, right lowers; hold to keep going.",
                TerrainToolMode.Flatten => "Right-click takes the target; left flattens to it.",
                TerrainToolMode.Smooth => "Left smooths the ground under the brush.",
                _ => "Right-click sets the target; drag from the start to slope to it.",
            });
            _billKey = null;
            ShowBill();
            w.SetPlanAlwaysLive(false);
            if (_ctx.Earthwork != null && !_ctx.Earthwork.Ready) w.SetMessage("Getting the ground ready…", true);
            else w.SetMessage(null);
        }

        /// <summary>How often the bill counts up while you drag (BR14): bounded, never per frame.</summary>
        const float BillSeconds = 0.2f;
        float _billClock;
        string _billKey;
        readonly HashSet<Hold> _billHeld = new HashSet<Hold>();

        /// <summary>
        /// The running bill (TB-09): the pending strokes plus the one being painted, so cut and fill count up as you drag
        /// (BR14). Redrawn only when a figure it shows changes.
        /// </summary>
        void ShowBill()
        {
            double cut = _cut, fill = _fill;
            int cleared = _cleared;
            _billHeld.Clear();
            _billHeld.UnionWith(_held);
            if (_run != null)
            {
                _run.Volumes(out double c, out double f);
                cut += c;
                fill += f;
                cleared += _run.ClearedCount;
                foreach (var h in _run.Held()) _billHeld.Add(h);
            }
            bool any = _session.Count > 0 || _run != null;
            string volumes = $"{ToolFormat.Volume(cut)} · {ToolFormat.Volume(fill)}", area = ToolFormat.Area(cleared);
            string held = _billHeld.Count > 0 ? HeldWords(_billHeld) : null;
            string key = any ? volumes + "|" + area + "|" + held + "|" + _session.Count : "";
            if (key == _billKey) return;
            _billKey = key;
            _review.Clear();
            if (any)
            {
                _review.Add("Cut · fill", volumes, "TB-09: so far, pending")
                       .Add("Cleared of trees", area, "TB-10: ground moved 1 ft or more");
                if (held != null) _review.Warn($"Held {held}", "TB-10 to TB-13");
            }
            _review.Ready(_session.Count > 0, false);
            _ctx.Window.ShowReview(_review);
        }

        // ---------- painting ----------

        public void Tick(in ToolInput input)
        {
            if (_step == Step.Review)
            {
                if (input.Pressed(ToolKeys.Next)) ConfirmNow();   // Enter confirms, as Break ground's button does
                return;
            }
            Keys(input);
            bool ready = _ctx.Earthwork == null || _ctx.Earthwork.Ready;
            if (!ready) { if (!_waiting) { _waiting = true; PaintStatus(); } Cursor(input, false); return; }
            if (_waiting) { _waiting = false; PaintStatus(); }
            if (_base == null) StartSession();
            Cursor(input, input.HasGround);

            if (_run == null)
            {
                bool left = input.Click, right = input.RightClick;
                if (right && UsesTarget && input.HasGround)
                {
                    _targetMm = GroundMm(input.Point);   // TB-05, TB-07: the target from the ground under the pointer
                    ShowTarget();
                }
                else if ((left || (right && _mode == TerrainToolMode.RaiseLower)) && input.HasGround && input.InCore)
                    Begin(input, lower: right);
                return;
            }
            bool held = _rightStroke ? input.RightDrag : input.Drag;
            if (input.HasGround)
            {
                _clock += Mathf.Min(input.DeltaSeconds, 0.25f);
                int ticks = 0;
                while (_clock >= TickSeconds && ticks++ < 8) { _clock -= TickSeconds; Lay(input.Point); }
                if (_dabs != null && _dabs.Count >= DesignRules.MaxDabsPerStroke)
                {
                    bool lower = _rightStroke;
                    Finish();
                    if (held) Begin(input, lower, _carryStart);   // a long drag carries on as a new stroke (TB-14)
                }
                Live();
                _billClock += input.DeltaSeconds;
                if (_run != null && _billClock >= BillSeconds) { _billClock = 0; ShowBill(); }
            }
            if (!held || (_rightStroke ? input.RightRelease : input.Release)) Finish();
        }

        void Keys(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.BrushNarrower)) SetFeet(_feet - StepFeet);
            if (input.Pressed(ToolKeys.BrushWider)) SetFeet(_feet + StepFeet);
            bool grade = input.Shift && _mode == TerrainToolMode.Slope;
            if (input.Pressed(ToolKeys.BrushWeaker)) { if (grade) SetGrade(_grade - 1); else SetStrength(_strength - 5); }
            if (input.Pressed(ToolKeys.BrushStronger)) { if (grade) SetGrade(_grade + 1); else SetStrength(_strength + 5); }
            if (UsesTarget && _targetMm.HasValue)
            {
                int step = input.Shift ? 3048 : 305;
                if (input.Pressed(ToolKeys.TargetUp)) { _targetMm += step; ShowTarget(); }
                if (input.Pressed(ToolKeys.TargetDown)) { _targetMm -= step; ShowTarget(); }
            }
            if (input.Pressed(ToolKeys.UndoStroke) && _run == null && _session.Count > 0) UndoStroke();
            if (input.Pressed(ToolKeys.Next) && _session.Count > 0 && _run == null) Go(Step.Review);
        }

        void Cursor(in ToolInput input, bool shown)
        {
            var at = new Vector2(input.Ground.x, input.Ground.z);
            float r = RadiusMm / 1000f;
            var paint = _ctx.Paint;
            if (paint != null)
            {
                var site = _ctx.Earthwork?.Earthwork?.Site;
                if (site.HasValue && !_aligned) { paint.Align(site.Value.Lattice, site.Value.OriginX, site.Value.OriginY); _aligned = true; }
                paint.Cursor(at, r, !input.InCore, shown);
            }
            else if (shown) _ctx.Preview.Ring(0, at, r, input.InCore ? PreviewInk.Line : PreviewInk.Bad);
            else _ctx.Preview.Hide(0);
        }

        bool _aligned, _waiting;

        void StartSession()
        {
            _base = _pending = _ctx.History.Terrain;
            var earthwork = _ctx.Earthwork.Earthwork;
            _fixed = FixedGround.For(_ctx.History.Document, earthwork.Site, id => earthwork.DetailOf(id.Value) as LiftBuild.Works);
            _session.Clear();
            _cut = _fill = 0;
            _cleared = 0;
            _held.Clear();
        }

        int GroundMm(PointMm p)
        {
            var g = _ctx.ToGround?.Invoke(p);
            var heights = _ctx.Earthwork?.Heights;
            if (g == null || heights == null) return 0;
            // The pending ground, as the player sees it.
            var shown = new EditedHeights(heights.Source, heights.Lattice, _pending ?? heights.Edits);
            return (int)Math.Round(shown.At(g.Value.X, g.Value.Y) * 1000);
        }

        void Begin(in ToolInput input, bool lower, int? carriedStart = null)
        {
            var heights = _ctx.Earthwork.Heights;
            int startMm = carriedStart ?? GroundMm(input.Point);
            if (UsesTarget && !_targetMm.HasValue) { _targetMm = startMm; ShowTarget(); }   // no target yet: the press's ground
            _settings = SettingsFor(lower, startMm);
            _rightStroke = lower && _mode == TerrainToolMode.RaiseLower;
            _run = new BrushRun(_settings, new EditedHeights(heights.Source, heights.Lattice, _pending), _ctx.Earthwork.Earthwork.Site, _fixed);
            _dabs = new List<BrushDab>(256);
            _clock = 0;
            _billClock = 0;
            _pathMm = 0;
            _lastDab = input.Point;
            Dab(input.Point, 1000);
        }

        // One tick at the pointer (TB-03): a fast move fills dabs in a quarter radius apart, sharing the tick's weight.
        void Lay(PointMm at)
        {
            long d2 = at.DistanceSquared(_lastDab);
            double d = Math.Sqrt(d2);
            int n = Math.Max(1, (int)Math.Ceiling(d / Math.Max(1, RadiusMm / 4)));
            int each = Math.Max(1, 1000 / n);
            for (int k = 1; k <= n; k++)
            {
                var p = new PointMm((int)Math.Round(_lastDab.X + (at.X - _lastDab.X) * (double)k / n), (int)Math.Round(_lastDab.Y + (at.Y - _lastDab.Y) * (double)k / n));
                Dab(p, each);
            }
            _pathMm += (long)Math.Round(d);
            _lastDab = at;
        }

        void Dab(PointMm at, int weight)
        {
            var dab = new BrushDab(at, weight);
            _dabs.Add(dab);
            _run.Dab(dab);
        }

        void Live()
        {
            var dirty = _run.TakeDirty();
            if (dirty.IsEmpty || _ctx.LiveGround == null) return;
            _liveDirty = _liveDirty.Union(dirty);
            _ctx.LiveGround.Live(_run.Preview(_pending), dirty);
        }

        void Finish()
        {
            if (_run == null) return;
            if (_dabs.Count > 0)
            {
                _session.Add(new StrokePainting(_settings, _dabs));
                _pending = _run.Preview(_pending);
                _run.Volumes(out double cut, out double fill);
                _cut += cut;
                _fill += fill;
                _cleared += _run.ClearedCount;
                foreach (var h in _run.Held()) _held.Add(h);
                _ctx.LiveGround?.Show(_pending);   // trees, cover and snow catch up (TB-08)
                _liveDirty = PointRect.Empty;
            }
            // A slope carried on past 4,096 dabs starts where its ramp has got to.
            if (_mode == TerrainToolMode.Slope && _targetMm.HasValue)
            {
                long gap = _targetMm.Value - _settings.StartMm, rise = Math.Min(_pathMm * _settings.GradePermille / 1000, Math.Abs(gap));
                _carryStart = (int)(_settings.StartMm + Math.Sign(gap) * rise);
            }
            _run = null;
            _dabs = null;
            PaintStatus();
        }

        int? _carryStart;

        void UndoStroke()
        {
            _session.RemoveAt(_session.Count - 1);
            // Replay what's left over the committed ground: the same runs, so the same bytes as before the stroke.
            var heights = _ctx.Earthwork.Heights;
            var ground = _base;
            _cut = _fill = 0;
            _cleared = 0;
            _held.Clear();
            foreach (var s in _session)
            {
                var run = new BrushRun(s.Settings, new EditedHeights(heights.Source, heights.Lattice, ground), _ctx.Earthwork.Earthwork.Site, _fixed);
                run.Dabs(s.Dabs);
                ground = run.Preview(ground);
                run.Volumes(out double cut, out double fill);
                _cut += cut;
                _fill += fill;
                _cleared += run.ClearedCount;
                foreach (var h in run.Held()) _held.Add(h);
            }
            _pending = ground;
            _ctx.LiveGround?.Show(_pending);
            PaintStatus();
        }

        // ---------- the review ----------

        void Review()
        {
            var w = _ctx.Window;
            _review.Clear().Add("Working it out…", "", "TB-09");
            _review.Ready(false, true);
            w.ShowReview(_review);
            var doc = _ctx.History.Document;
            long revision = _ctx.History.Revision;
            var committed = _ctx.History.Terrain;
            var command = new AddTerrainStrokes(revision, new List<StrokePainting>(_session));
            var earthwork = _ctx.Earthwork.Earthwork;
            _analysing = true;
            _ctx.Analyse(() =>
            {
                var after = command.Apply(doc);
                return after.Accepted ? (after, earthwork.Rebuild(after.Document, committed)) : (after, null);
            }, r =>
            {
                _analysing = false;
                if (!r.after.Accepted) { w.SetMessage(r.after.Reason); return; }
                _command = command;
                _rebuild = r.Item2;
                _ctx.PendingTerrain = _rebuild.Edits;
                BuildReview(r.after.Document, committed);
            });
        }

        void BuildReview(DesignDocument after, TerrainEdits committed)
        {
            double cut = 0, fill = 0;
            int cleared = 0;
            var held = new HashSet<Hold>();
            foreach (var k in after.Strokes)
                if (_rebuild.DetailOf(k.Id.Value) is StrokeBuild b)
                {
                    cut += b.CutCubicMetres;
                    fill += b.FillCubicMetres;
                    cleared += b.ClearedCells;
                    foreach (var h in b.Held) held.Add(h);
                }
            _review.Clear()
                .Add("Cut", ToolFormat.Volume(cut), "TB-09")
                .Add("Fill", ToolFormat.Volume(fill), "TB-09")
                .Add(fill >= cut ? "Import" : "Export", ToolFormat.Volume(Math.Abs(fill - cut)), "TB-09: fill − cut, brought in or hauled away")
                .Add("Cleared of trees", ToolFormat.Area(cleared), "TB-10: ground moved 1 ft or more")
                .Add("Strokes", _session.Count.ToString(), "TB-08: one undo step");
            foreach (var line in TowersReheighted(after, committed, _rebuild.Edits)) _review.Add(line.Label, line.Value, "TB-13, TW-02 v4: the heads stay put; the steel follows the ground");
            if (held.Count > 0) _review.Warn($"Held {HeldWords(held)}", "TB-10 to TB-13");
            _review.Ready(true, true);
            _ctx.Window.ShowReview(_review);
        }

        // TB-09: each tower whose steel the strokes change, under its fixed head (TW-02 v4).
        List<(string Label, string Value)> TowersReheighted(DesignDocument doc, TerrainEdits before, TerrainEdits after)
        {
            var lines = new List<(string, string)>();
            var heights = _ctx.Earthwork.Heights;
            var site = _ctx.Earthwork.Earthwork.Site;
            var was = new EditedHeights(heights.Source, heights.Lattice, before);
            var now = new EditedHeights(heights.Source, heights.Lattice, after);
            foreach (var lift in doc.Lifts)
            {
                if (!(_ctx.Earthwork.Earthwork.DetailOf(lift.Id.Value) is LiftBuild.Works works) || works.Rope == null) continue;
                site.PointOf(lift.Bottom, out double bx, out double by);
                site.PointOf(lift.Top, out double tx, out double ty);
                double len = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
                if (len <= 0) continue;
                double ux = (tx - bx) / len, uy = (ty - by) / len;
                for (int i = 0; i < works.Rope.Towers.Count; i++)
                {
                    var t = works.Rope.Towers[i];
                    double x = bx + ux * t.BaseS, y = by + uy * t.BaseS;
                    double g0 = was.At(x, y), g1 = now.At(x, y);
                    if (Math.Abs(g1 - g0) < 0.01) continue;
                    LiftRope.AsBuilt(t, g0, out double s0, out _);
                    LiftRope.AsBuilt(t, g1, out double s1, out _);
                    if (Math.Abs(s1 - s0) < 0.01) continue;
                    int f0 = (int)Math.Round(s0 / UnitFormat.MetresPerFoot), f1 = (int)Math.Round(s1 / UnitFormat.MetresPerFoot);
                    lines.Add(($"{lift.Name}, tower {i + 1}", $"{ToolFormat.Feet(f0)} → {ToolFormat.Feet(f1)}"));
                }
            }
            return lines;
        }

        string HeldWords(HashSet<Hold> held)
        {
            var parts = new List<string>();
            var doc = _ctx.History.Document;
            string Lift(LiftId id) => doc.FindLift(id)?.Name ?? "a lift";
            foreach (var h in held)
            {
                string s = h.Kind switch
                {
                    HoldKind.Area => "at the area's edge",
                    HoldKind.Depth => "at 40 ft from natural ground",
                    HoldKind.Terminal => $"by {Lift(h.Lift)}'s terminal",
                    HoldKind.LiftLine => $"by {Lift(h.Lift)}'s line (15 ft under the chairs)",
                    HoldKind.Tower => $"by {Lift(h.Lift)}'s tower {h.Tower} (its height limit)",
                    _ => null,
                };
                if (s != null && !parts.Contains(s)) parts.Add(s);
            }
            parts.Sort(StringComparer.Ordinal);
            return parts.Count <= 2 ? string.Join(" and ", parts) : $"{parts[0]}, {parts[1]} and {parts.Count - 2} more";
        }

        // ---------- the contract ----------

        public ToolBackOut Escape()
        {
            if (_run != null) { CancelStroke(); return ToolBackOut.Stepped; }
            if (_step == Step.Review) { BackToPaint(); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;
        }

        void BackToPaint()
        {
            _ctx.PendingTerrain = null;
            _rebuild = null;
            _command = null;
            Go(Step.Paint);
        }

        void CancelStroke()
        {
            _run = null;
            _dabs = null;
            if (!_liveDirty.IsEmpty) _ctx.LiveGround?.Live(_pending, _liveDirty);   // the ground as it was before the stroke
            _liveDirty = PointRect.Empty;
            PaintStatus();
        }

        // Enter in the review: the same as Confirm.
        void ConfirmNow()
        {
            if (_analysing || _rebuild == null || _command == null) return;
            var result = _ctx.Submit?.Invoke(new AddTerrainStrokes(_ctx.History.Revision, _command.Strokes), _rebuild.Edits);
            if (result == null || !result.Accepted) return;
            _ctx.PendingTerrain = null;
            Committed(result);
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (stage == WorkStage.Plan) { if (_step == Step.Review) BackToPaint(); return null; }   // "Back"
            if (_step == Step.Paint) { if (_session.Count > 0 && _run == null) Go(Step.Review); return null; }   // Break ground opens the review
            if (_analysing || _rebuild == null || _command == null) return null;
            return new AddTerrainStrokes(baseRevision, _command.Strokes);
        }

        public void Committed(CommandResult result)
        {
            _ctx.Earthwork?.Earthwork?.Accept(_rebuild);
            _ctx.Notice?.Invoke($"{_session.Count} stroke{(_session.Count == 1 ? "" : "s")} confirmed");
            _rebuild = null;
            _command = null;
            _base = null;
            _session.Clear();
            _liveDirty = PointRect.Empty;
            Go(Step.Paint);   // the tool stays armed (P3-06 Q4)
        }

        public void Disarm()
        {
            if (_run != null) CancelStroke();
            // Strokes not confirmed: the committed ground again (CF-06).
            if (_base != null && !ReferenceEquals(_pending, _base)) _ctx.LiveGround?.Show(_ctx.History.Terrain);
            _ctx.Paint?.Cursor(Vector2.zero, 0, false, false);
            _ctx.LiveGround?.ContoursWhileArmed(false);
            _session.Clear();
        }
    }

    public sealed class RaiseLowerTool : TerrainBrushTool { public RaiseLowerTool() : base(TerrainToolMode.RaiseLower) { } }
    public sealed class FlattenTool : TerrainBrushTool { public FlattenTool() : base(TerrainToolMode.Flatten) { } }
    public sealed class SmoothTool : TerrainBrushTool { public SmoothTool() : base(TerrainToolMode.Smooth) { } }
    public sealed class SlopeTool : TerrainBrushTool { public SlopeTool() : base(TerrainToolMode.Slope) { } }
}
