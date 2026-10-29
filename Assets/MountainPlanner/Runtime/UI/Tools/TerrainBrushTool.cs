using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
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
    /// The Terrain tab's brushes (task 15; TB-01 to TB-14, owner BR1–BR19), one class in four modes, Cities: Skylines 2's set.
    ///
    /// <b>Paint:</b> the left button paints (Raise/Lower: the right lowers; Flatten and Slope: a right click takes the target
    /// elevation under the pointer). A stroke lays dabs 30 times a second at the pointer, filled in along a fast move
    /// (TB-03), through <see cref="BrushRun"/>, the replay's own code, so the ground moves live under the brush (BR5) and
    /// what's confirmed is what was seen. <b>The session</b> (BR17) runs from opening a brush to closing it, across all four
    /// (BR15): Ctrl+Z takes back the last press-to-release, never past the session's start; the pane counts its cut and
    /// fill as you paint (BR14, BR19). <b>Confirm</b> (or Enter) folds the session through the design's earthwork after
    /// trails and lifts and puts it in as one <see cref="AddTerrainStrokes"/>, one undo step, with no review page; that's when
    /// the player is charged (<see cref="IEarthworkCharges"/>). <b>Revert</b> puts the session's ground back. Esc, ✕ or
    /// another kind of tool with ground not yet confirmed asks Confirm / Revert / Keep painting (BR18). The Contours layer is
    /// on while a brush is armed (BR13). <b>On screen</b> (BR21, BR22): the ground, the trees and the lift towers follow the
    /// brush every frame (trees out where the stroke clears, with Clear trees on; towers re-heighted under their fixed heads),
    /// and the cover and the snow catch up a few times a second, so letting go and Confirm change nothing on screen.
    /// </summary>
    public class TerrainBrushTool : IDesignTool, IOwnsRightButton, ICarriesWork, IKeepsWork
    {
        const int MinFeet = 10, MaxFeet = 400, StepFeet = 5;
        const float TickSeconds = 1f / TerrainBrush.TicksPerSecond;

        // TB-07: each rating's symbol (TD-03), to name the grade a slope will make.
        static readonly string[] BandSymbols = { "●", "■", "◆", "◆◆" };

        // The pane's settings carry between brushes for the session (as the trail brush's width does).
        static int _lastFeet = 60, _lastStrength = 50;
        // BR22: the pane's Clear trees, remembered like the diameter; on, a stroke clears the cells it moves 1 ft or more
        // the moment the ground moves (TB-10 v2); off, the trees ride the new ground.
        static bool _lastClear = true;
        bool _clear = true;
        Action<bool> _setClear;
        /// <summary>Patches pooled before the first stroke (round 4): a 400 ft drag across a few tiles, 100 KB each.</summary>
        const int WarmPatches = 48;

        readonly TerrainToolMode _mode;
        ToolContext _ctx;
        int _feet, _strength;
        int? _targetMm;
        // Slope (BR16): the target point the right click took, and the stroke's start (the press), kept when a long drag carries on.
        PointMm? _targetAt;
        PointMm _startAt;
        int _startMm;

        // The session (BR17): strokes painted and not yet confirmed, and the ground with them on it.
        readonly List<StrokePainting> _session = new List<StrokePainting>();
        TerrainEdits _base, _pending;
        FixedGround _fixed;
        double _cut, _fill;
        int _cleared;
        readonly HashSet<Hold> _held = new HashSet<Hold>();

        // One press-to-release (Ctrl+Z's step): where its strokes start in the session, and the ground and bill before it,
        // so taking it back is the same bytes as before it, with nothing replayed.
        readonly struct Press
        {
            public readonly int Start, Cleared;
            public readonly TerrainEdits Before;
            public readonly double Cut, Fill;
            public readonly Hold[] Held;
            public Press(int start, TerrainEdits before, double cut, double fill, int cleared, Hold[] held)
            {
                Start = start; Before = before; Cut = cut; Fill = fill; Cleared = cleared; Held = held;
            }
        }

        readonly List<Press> _presses = new List<Press>();
        bool _pressOpen;

        // The stroke in hand.
        BrushRun _run;
        List<BrushDab> _dabs;
        BrushSettings _settings;
        bool _rightStroke;
        PointMm _lastDab;
        float _clock;
        PointRect _liveDirty = PointRect.Empty;

        // BR21: the last catch-up asked for (cover, trees' footing and snow following the stroke), and the last ground change
        // with heights (Ctrl+Z, Revert) still landing, which a new press waits for; the press waiting (1 left, 2 right).
        Task _catchUp = Task.CompletedTask, _settling = Task.CompletedTask;
        float _catchUpClock;
        int _heldPress;

        /// <summary>How often the cover, the trees' footing and the snow catch up while you drag (BR21): bounded, never per frame.</summary>
        const float CatchUpSeconds = 0.2f;

        // Confirm (BR17): the session folded off the main thread, then submitted; and the close ask (BR18).
        bool _confirming, _landed, _asking;
        Action _afterConfirm, _afterAsk, _thenNext;

        readonly ReviewModel _review = new ReviewModel();
        Action<int, string> _setFeet, _setStrength;
        UnityEngine.UIElements.Label _targetNote, _rampNote;
        (int Length, int Percent) _rampShown = (-1, -1);

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

        /// <summary>The strokes of the session not yet confirmed (tests).</summary>
        public int Pending => _session.Count;

        /// <summary>The ground with the session's strokes on it (tests).</summary>
        public TerrainEdits PendingGround => _pending;

        /// <summary>The pane's running bill (tests, captures).</summary>
        public ReviewModel Bill => _review;

        /// <summary>A Confirm is being folded and submitted (tests, captures).</summary>
        public bool Confirming => _confirming;

        /// <summary>The close ask is showing (BR18; tests, captures).</summary>
        public bool Asking => _asking;

        /// <summary>The pane's diameter, feet (tests, captures).</summary>
        public int BrushFeet => _feet;

        /// <summary>Sets the pane's diameter and strength (captures; the player uses the sliders and keys).</summary>
        public void SetBrush(int feet, int strengthPercent) { SetFeet(feet); SetStrength(strengthPercent); }

        /// <summary>Sets the target as a right click on the ground there would (captures, tests).</summary>
        public void SetTarget(PointMm at)
        {
            _targetMm = GroundMm(at);
            _targetAt = at;
            ShowTarget();
        }

        bool UsesTarget => _mode == TerrainToolMode.Flatten || _mode == TerrainToolMode.Slope;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            _ctx.LiveGround?.ContoursWhileArmed(true);   // BR13
            BrushRun.Warm(WarmPatches);                  // round 4: the first stroke's patches, allocated here and not a frame
            _feet = _lastFeet;
            _strength = _lastStrength;
            var w = context.Window;
            w.SetName(null);
            _setFeet = w.AddSlider("diameter", "Diameter", MinFeet, MaxFeet, StepFeet, _feet, SetFeet, "TB-02: 10–400 ft, in 5 ft steps", StepFeet);
            _setStrength = w.AddSlider("strength", "Strength", 1, 100, 1, _strength, SetStrength, "TB-02: 1–100%", 5);   // the wheel, as , and . (BR20)
            _clear = _lastClear;
            _setClear = w.AddCheck("clear", "Trees", "Clear trees", _clear, SetClear,
                "TB-10 v2 (BR22): on, the trees come out where the ground moves 1 ft or more, as it moves; off, they stand on the new ground").Set;
            if (UsesTarget) _targetNote = w.AddNote("target", "", "TB-05, TB-07: right-click the ground; Page Up / Page Down 1 ft (Shift 10 ft)");
            if (_mode == TerrainToolMode.Slope) _rampNote = w.AddNote("ramp", "", "TB-07, BR16: the grade comes from the distance and the drop between the two points");
            w.SetSteps(Array.Empty<string>());   // one step: paint, then Confirm or Revert (BR17)
            SetFeet(_feet);
            SetStrength(_strength);
            ShowTarget();
            foreach (var a in new[] { GameAction.BrushNarrower, GameAction.BrushWider, GameAction.BrushWeaker, GameAction.BrushStronger, GameAction.UndoStroke })
                _ctx.Claim(a, true);
            _ctx.Claim(GameAction.TargetUp, UsesTarget);
            _ctx.Claim(GameAction.TargetDown, UsesTarget);
            var keys = new List<(GameAction, string)> { (GameAction.BrushNarrower, ""), (GameAction.BrushWider, "diameter"), (GameAction.BrushWeaker, ""), (GameAction.BrushStronger, "strength") };
            if (UsesTarget) { keys.Add((GameAction.TargetUp, "")); keys.Add((GameAction.TargetDown, "target")); }
            keys.Add((GameAction.UndoStroke, "undoes a stroke"));
            w.SetKeys(keys.ToArray());
            w.SetCommitText("Confirm", "Revert");
            w.SetPlanShown(true);
            w.SetPlanAlwaysLive(false);
            w.SetNext(false, false);
            PaintStatus();
        }

        // ---------- the pane ----------

        void SetFeet(int feet)
        {
            _feet = _lastFeet = Mathf.Clamp((feet + StepFeet / 2) / StepFeet * StepFeet, MinFeet, MaxFeet);
            _setFeet?.Invoke(_feet, ToolFormat.Feet(_feet));
        }

        /// <summary>BR22: whether the next stroke clears the trees where it moves the ground (the pane's Clear trees).</summary>
        public void SetClear(bool clear)
        {
            _clear = _lastClear = clear;
            _setClear?.Invoke(clear);
        }

        public bool ClearsTrees => _clear;

        void SetStrength(int percent)
        {
            _strength = _lastStrength = Mathf.Clamp(percent, 1, 100);
            _setStrength?.Invoke(_strength, _strength + "%");
        }

        void ShowTarget()
        {
            if (_targetNote == null) return;
            _targetNote.text = _targetMm.HasValue
                ? $"Target {UnitFormat.Elevation(_targetMm.Value / 1000.0, DisplayUnits.Current)} · right-click to set"
                : "Target: right-click the ground to set it";
        }

        // BR16: the ramp from the pointer (or the stroke's start) to the target: its length and the grade it makes, with the
        // rating that grade falls in. Redrawn only when a rounded figure changes.
        void ShowRamp(PointMm from, int fromMm)
        {
            if (_rampNote == null) return;
            if (!_targetAt.HasValue) { SetRamp(-2, -2, "Ramp: set the target first"); return; }
            double run = Math.Sqrt(from.DistanceSquared(_targetAt.Value)) / 1000.0, rise = (_targetMm.Value - fromMm) / 1000.0;
            int length = ToolFormat.LengthKey(run), percent = run < 0.5 ? 0 : (int)Math.Round(Math.Abs(rise) / run * 100);
            if (length == _rampShown.Length && percent == _rampShown.Percent) return;
            string band = BandSymbols[(int)SlopeBands.Of(percent)];
            SetRamp(length, percent, run < 0.5 ? "Ramp: the target is here" : $"Ramp {ToolFormat.Length(run)} at {band} {percent}%");
        }

        void SetRamp(int length, int percent, string text)
        {
            if (length == _rampShown.Length && percent == _rampShown.Percent) return;
            _rampShown = (length, percent);
            _rampNote.text = text;
        }

        int RadiusMm => _feet * DesignUnits.FiveFeetMm / 10;   // half the diameter, in 2½ ft steps

        BrushSettings SettingsFor(bool lower, int startMm) => (_mode switch
        {
            TerrainToolMode.RaiseLower => BrushSettings.RaiseOrLower(lower, RadiusMm, TerrainBrush.StepFor(_strength)),
            TerrainToolMode.Flatten => BrushSettings.Flatten(RadiusMm, TerrainBrush.StrengthFor(_strength), _targetMm ?? startMm),
            TerrainToolMode.Smooth => BrushSettings.Smooth(RadiusMm, TerrainBrush.StrengthFor(_strength)),
            _ => BrushSettings.Slope(RadiusMm, TerrainBrush.StrengthFor(_strength), _startAt, _startMm, _targetAt.Value, _targetMm.Value),
        }).WithClearing(_clear);   // TB-10 v2: stored with the stroke, so a replay clears exactly what the player saw

        void PaintStatus()
        {
            var w = _ctx.Window;
            w.SetHint(_mode switch
            {
                TerrainToolMode.RaiseLower => "Left raises, right lowers; hold to keep going.",
                TerrainToolMode.Flatten => "Right-click takes the target; left flattens to it.",
                TerrainToolMode.Smooth => "Left smooths the ground under the brush.",
                _ => _targetAt.HasValue ? "Drag from the start: the ground ramps evenly to the target." : "Right-click the ground to set the target, then drag from the start.",
            });
            _billKey = null;
            ShowBill();
            if (_confirming) w.SetMessage("Confirming…", true);
            else if (_ctx.Earthwork != null && !_ctx.Earthwork.Ready) w.SetMessage("Getting the ground ready…", true);
            else w.SetMessage(null);
        }

        /// <summary>How often the bill counts up while you drag (BR14): bounded, never per frame.</summary>
        const float BillSeconds = 0.2f;
        float _billClock;
        string _billKey;
        bool _syncDue;   // round 4: a catch-up just started; the next tick syncs one tile
        readonly HashSet<Hold> _billHeld = new HashSet<Hold>(), _billShown = new HashSet<Hold>();
        (bool, bool, double, double, int, int) _billFigures = (false, false, -1, -1, -1, -1);

        /// <summary>
        /// The session's bill (TB-09 v2, BR19): every stroke of the session plus the one being painted, so cut and fill
        /// count up as you drag (BR14). Confirm and Revert go live once a stroke is down. Redrawn only when a figure it
        /// shows changes.
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
                foreach (var i in _run.HeldIndices) _billHeld.Add(_run.Holds[i]);
            }
            bool any = _session.Count > 0 || _run != null, can = _session.Count > 0 && !_confirming;
            // The figures first (round 4: five times a second while painting, so no strings unless one changed): the volumes
            // and the area to the metre, which is finer than any unit the pane shows, so an unchanged figure is a repeat.
            var figures = (any, can, any ? Math.Round(cut) : 0, any ? Math.Round(fill) : 0, any ? cleared : 0, any ? _billHeld.Count : 0);
            if (figures == _billFigures && (!any || _billHeld.SetEquals(_billShown))) return;
            _billFigures = figures;
            _billShown.Clear();
            _billShown.UnionWith(_billHeld);
            string volumes = $"{ToolFormat.Volume(cut)} · {ToolFormat.Volume(fill)}", net = ToolFormat.Volume(Math.Abs(fill - cut)), area = ToolFormat.Area(cleared);
            string held = _billHeld.Count > 0 ? HeldWords(_billHeld) : null;
            string key = any ? volumes + "|" + net + "|" + area + "|" + held + "|" + can : "|" + can;
            if (key == _billKey) return;
            _billKey = key;
            _review.Clear();
            if (any)
            {
                _review.Add("Cut · fill", volumes, "TB-09 v2: this session, counted as you paint; Confirm is when it's paid for (BR19)")
                       .Add(fill >= cut ? "Import" : "Export", net, "TB-09: fill − cut, brought in or hauled away")
                       .Add("Cleared of trees", area, "TB-10: ground moved 1 ft or more");
                if (held != null) _review.Warn($"Held {held}", "TB-10 to TB-13");
            }
            _review.Ready(can, can);
            _ctx.Window.ShowReview(_review);
        }

        // ---------- painting ----------

        public void Tick(in ToolInput input)
        {
            // The fold ahead waits for the let-go's catch-up to land, so the two don't share the CPU.
            if (_foldDue && _run == null && !_confirming && _catchUp.IsCompleted) { _foldDue = false; FoldAhead(); }
            // Round 4: between presses, the tiles the live upload wrote are brought up to date in Unity one a frame.
            if (_run == null || _syncDue) { _syncDue = false; _ctx.LiveGround?.Settle(); }
            if (_thenNext != null)
            {
                // What the close ask's Confirm was for (close, or the next tool), run here rather than inside the fold's hand-over.
                var then = _thenNext;
                _thenNext = null;
                then();
                return;
            }
            if (_confirming)
            {
                // The confirm's fold is running; a design change meanwhile drops it (CF-03), and the session stays to confirm again.
                if (!_landed && _ctx.Running == 0)
                {
                    _confirming = false;
                    _afterConfirm = null;
                    PaintStatus();
                    _ctx.Window.SetMessage("The design changed; Confirm again.", true);
                }
                Cursor(input, false);
                return;
            }
            Keys(input);
            if (_confirming) { Cursor(input, false); return; }
            bool ready = _ctx.Earthwork == null || _ctx.Earthwork.Ready;
            if (!ready) { if (!_waiting) { _waiting = true; PaintStatus(); } Cursor(input, false); return; }
            if (_waiting) { _waiting = false; PaintStatus(); }
            if (_base == null) StartSession();
            Cursor(input, input.HasGround);

            if (_run == null)
            {
                if (!_settling.IsCompleted)
                {
                    // BR21: a Ctrl+Z or Revert is still landing its heights; a press waits for it (a frame or two).
                    if (input.Click) _heldPress = 1;
                    else if (input.RightClick && _mode == TerrainToolMode.RaiseLower) _heldPress = 2;
                    return;
                }
                if (_heldPress != 0)
                {
                    bool lower = _heldPress == 2;
                    _heldPress = 0;
                    if ((lower ? input.RightDrag : input.Drag) && input.HasGround && input.InCore) Begin(input, lower);
                    return;
                }
                bool left = input.Click, right = input.RightClick;
                if ((left || right) && _asking) KeepPainting();   // painting again answers the close ask (BR18)
                if (right && UsesTarget && input.HasGround)
                {
                    SetTarget(input.Point);   // TB-05, TB-07: the target from the ground under the pointer (for Slope, where it is too)
                    PaintStatus();
                }
                else if (left && _mode == TerrainToolMode.Slope && !_targetAt.HasValue)
                    _ctx.Window.SetMessage("Right-click the ground to set the slope's target first.", false);   // BR16: no hidden target
                else if ((left || (right && _mode == TerrainToolMode.RaiseLower)) && input.HasGround && input.InCore)
                    Begin(input, lower: right);
                if (_mode == TerrainToolMode.Slope && input.HasGround && _run == null) ShowRamp(input.Point, GroundMm(input.Point));
                return;
            }
            bool held = _rightStroke ? input.RightDrag : input.Drag;
            _lastTick = default;
            if (input.HasGround)
            {
                long t0 = Stopwatch.GetTimestamp();
                _clock += Mathf.Min(input.DeltaSeconds, 0.25f);
                int ticks = 0;
                while (_clock >= TickSeconds && ticks++ < 8) { _clock -= TickSeconds; Lay(input.Point); }
                _lastTick.Dabs = Ms(t0);
                if (_dabs != null && _dabs.Count >= DesignRules.MaxDabsPerStroke)
                {
                    bool lower = _rightStroke;
                    Finish(released: !held);
                    if (held) Begin(input, lower, carryOn: true);   // a long drag carries on as a new stroke, on the same ramp (TB-14)
                }
                Live();
                _catchUpClock += input.DeltaSeconds;
                if (_run != null && _catchUpClock >= CatchUpSeconds && _catchUp.IsCompleted)
                {
                    long tc = Stopwatch.GetTimestamp();
                    _catchUpClock = 0;
                    CatchUp(_run.Preview(_pending));
                    _lastTick.CatchUp = Ms(tc);
                    _syncDue = true;   // one tile's Unity sync on the next tick, not this one (the catch-up's start is here)
                }
                _billClock += input.DeltaSeconds;
                long t1 = Stopwatch.GetTimestamp();
                if (_run != null && _billClock >= BillSeconds) { _billClock = 0; ShowBill(); if (_mode == TerrainToolMode.Slope) ShowRamp(_startAt, _startMm); }
                _lastTick.Bill = Ms(t1);
            }
            if (!held || (_rightStroke ? input.RightRelease : input.Release)) Finish();
        }

        void Keys(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.BrushNarrower)) SetFeet(_feet - StepFeet);
            if (input.Pressed(ToolKeys.BrushWider)) SetFeet(_feet + StepFeet);
            if (input.Pressed(ToolKeys.BrushWeaker)) SetStrength(_strength - 5);
            if (input.Pressed(ToolKeys.BrushStronger)) SetStrength(_strength + 5);
            if (UsesTarget && _targetMm.HasValue)
            {
                int step = input.Shift ? 3048 : 305;
                if (input.Pressed(ToolKeys.TargetUp)) { _targetMm += step; ShowTarget(); }
                if (input.Pressed(ToolKeys.TargetDown)) { _targetMm -= step; ShowTarget(); }
            }
            if (input.Pressed(ToolKeys.UndoStroke) && _run == null && _presses.Count > 0) UndoStroke();
            if (input.Pressed(ToolKeys.Next) && _run == null) Confirm(_asking ? TakeAsk() : null);   // Enter confirms (BR17)
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
            _presses.Clear();
            _cut = _fill = 0;
            _cleared = 0;
            _held.Clear();
        }

        int GroundMm(PointMm p)
        {
            var g = _ctx.ToGround?.Invoke(p);
            var heights = _ctx.Earthwork?.Heights;
            if (g == null || heights == null) return 0;
            // The session's ground, as the player sees it.
            var shown = new EditedHeights(heights.Source, heights.Lattice, _pending ?? heights.Edits);
            return (int)Math.Round(shown.At(g.Value.X, g.Value.Y) * 1000);
        }

        void Begin(in ToolInput input, bool lower, bool carryOn = false)
        {
            var heights = _ctx.Earthwork.Heights;
            int startMm = GroundMm(input.Point);
            if (!carryOn) { _startAt = input.Point; _startMm = startMm; _pressOpen = false; }   // BR16: the ramp starts at the press
            if (_mode == TerrainToolMode.Flatten && !_targetMm.HasValue) { _targetMm = startMm; ShowTarget(); }   // Flatten with no target: the press's ground
            _settings = SettingsFor(lower, startMm);
            _rightStroke = lower && _mode == TerrainToolMode.RaiseLower;
            _run = new BrushRun(_settings, new EditedHeights(heights.Source, heights.Lattice, _pending), _ctx.Earthwork.Earthwork.Site, _fixed);
            if (!carryOn) _ctx.LiveGround?.Painting(true);
            _dabs = new List<BrushDab>(256);
            _clock = 0;
            _billClock = 0;
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
            _ctx.LiveGround.Live(_run.Live, dirty);   // the stroke as it stands, read point by point: no tiles built a frame
        }

        /// <summary>The last painting tick's time in its parts, ms (the brush bench, demo 75).</summary>
        public struct TickTimings { public double Dabs, Bill, CatchUp; }

        TickTimings _lastTick;

        /// <summary>The last painting tick's time in its parts (the brush bench).</summary>
        public TickTimings LastTick => _lastTick;

        /// <summary>How long the last stroke's end took on the main thread, ms: its tiles built and the ground handed over (the brush bench).</summary>
        public double LastFinishMs { get; private set; }

        /// <summary>The part of <see cref="LastFinishMs"/> that built the stroke's tiles, ms.</summary>
        public double LastFinishPreviewMs { get; private set; }

        static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

        void Finish(bool released = true)
        {
            if (_run == null) return;
            long t0 = Stopwatch.GetTimestamp();
            if (_dabs.Count > 0)
            {
                if (!_pressOpen)
                {
                    _presses.Add(new Press(_session.Count, _pending, _cut, _fill, _cleared, new List<Hold>(_held).ToArray()));
                    _pressOpen = true;
                }
                _session.Add(new StrokePainting(_settings, _dabs));
                long tp = Stopwatch.GetTimestamp();
                _pending = _run.Preview(_pending);
                LastFinishPreviewMs = Ms(tp);
                _run.Volumes(out double cut, out double fill);
                _cut += cut;
                _fill += fill;
                _cleared += _run.ClearedCount;
                foreach (var i in _run.HeldIndices) _held.Add(_run.Holds[i]);
                CatchUp(_pending);   // BR21: only what moved since the last catch-up; nothing on screen changes
                _liveDirty = PointRect.Empty;
                if (released) { _fold = null; _foldDue = true; }   // Confirm's fold, ahead of the click
            }
            _run.Release();   // its tiles are built and handed over; the patches serve the next stroke (round 4)
            _run = null;
            _dabs = null;
            if (released) _ctx.LiveGround?.Painting(false);
            LastFinishMs = Ms(t0);
            PaintStatus();
        }

        // BR21: the cover, the trees' footing and the snow follow the ground whose heights the live upload put up; the session's
        // clearing stays the committed one until Confirm.
        // BR22: the ground's own clearing comes out with it (the session's cleared cells), as the trees did every frame.
        void CatchUp(TerrainEdits ground)
        {
            if (_ctx.LiveGround == null || _base == null) return;
            _catchUp = _ctx.LiveGround.CatchUp(ground, ground);
        }

        // Ctrl+Z (TB-08 v2): the last press-to-release comes back off, to the very ground and bill before it.
        void UndoStroke()
        {
            var press = _presses[_presses.Count - 1];
            _presses.RemoveAt(_presses.Count - 1);
            _session.RemoveRange(press.Start, _session.Count - press.Start);
            _pending = press.Before;
            _cut = press.Cut;
            _fill = press.Fill;
            _cleared = press.Cleared;
            _held.Clear();
            _held.UnionWith(press.Held);
            if (_ctx.LiveGround != null) _settling = _ctx.LiveGround.ShowGround(_pending, _pending);   // BR22: its trees back too
            _fold = null;
            _foldDue = true;
            PaintStatus();
        }

        // ---------- Confirm and Revert (BR17) ----------

        /// <summary>
        /// Confirm: the session folded through the design's earthwork off the main thread (after trails and lifts, BR6), then
        /// put in as one command and one undo step. The ground the player painted is the ground confirmed (TB14), so the
        /// same set goes in and nothing on the map moves. <paramref name="then"/> runs once it's in (the close ask's Confirm).
        /// </summary>
        void Confirm(Action then)
        {
            if (_confirming || _run != null || _session.Count == 0) { if (!_confirming && _session.Count == 0) then?.Invoke(); return; }
            _confirming = true;
            _landed = false;
            _afterConfirm = then;
            PaintStatus();
            _confirmClicked = Stopwatch.GetTimestamp();
            // Usually the fold started when the last press ended is done or nearly done; if the session or the design moved
            // since, it folds now, as before.
            string why = _fold == null ? "none" : $"revision {_fold.Revision} vs {_ctx.History.Revision}, strokes {_fold.Command.Strokes.Count} vs {_session.Count}";
            var fold = _fold != null && _fold.Matches(_ctx.History.Revision, _session) ? _fold : (_fold = StartFold());
            _foldDue = false;
            FoldWasAhead = fold.Started < _confirmClicked;
            UnityEngine.Debug.Log($"[TerrainBrush] frame {Time.frameCount}: Confirm {(FoldWasAhead ? fold.Work.IsCompleted ? "found its fold done" : "waits for its fold" : "folds now")} ({why})");
            _ctx.Await(fold.Work, r =>
            {
                LastFold = (r.ApplyMs, r.RebuildMs);
                Landed(fold.Command, fold.Committed, r.After, r.Rebuild);
            });
        }

        long _confirmClicked;

        // ---------- the fold ahead (Confirm's work, started when a press ends) ----------

        /// <summary>
        /// The session folded through the design's earthwork (the command's Apply, then the earthwork's Rebuild), off the main
        /// thread: the very work Confirm needs, on the very inputs (the design, the committed ground and the strokes as they stand),
        /// so its result is the same whenever it runs. It's used only while the session's strokes and the design's revision are
        /// still those it folded.
        /// </summary>
        sealed class Fold
        {
            public long Revision, Started;
            public TerrainEdits Committed;
            public AddTerrainStrokes Command;
            public Task<FoldResult> Work;

            public bool Matches(long revision, List<StrokePainting> session)
            {
                if (revision != Revision || Command.Strokes.Count != session.Count) return false;
                // A stroke is a struct: the same stroke is the same dab list (each press makes its own) with the same settings.
                for (int i = 0; i < session.Count; i++)
                    if (!ReferenceEquals(Command.Strokes[i].Dabs, session[i].Dabs) || !Command.Strokes[i].Settings.Equals(session[i].Settings)) return false;
                return true;
            }
        }

        struct FoldResult
        {
            public CommandResult After;
            public DesignEarthwork.Result Rebuild;
            public double ApplyMs, RebuildMs;
        }

        Fold _fold;
        bool _foldDue;

        Fold StartFold()
        {
            var doc = _ctx.History.Document;
            long revision = _ctx.History.Revision;
            var committed = _ctx.History.Terrain;
            var command = new AddTerrainStrokes(revision, new List<StrokePainting>(_session));
            var earthwork = _ctx.Earthwork.Earthwork;
            return new Fold
            {
                Revision = revision, Started = Stopwatch.GetTimestamp(), Committed = committed, Command = command,
                Work = Task.Run(() =>
                {
                    long f0 = Stopwatch.GetTimestamp();
                    var after = command.Apply(doc);
                    double apply = Ms(f0);
                    long f1 = Stopwatch.GetTimestamp();
                    var rebuild = after.Accepted ? earthwork.Rebuild(after.Document, committed) : null;
                    return new FoldResult { After = after, Rebuild = rebuild, ApplyMs = apply, RebuildMs = Ms(f1) };
                }),
            };
        }

        // A press ended (or Ctrl+Z took one back): fold the session as it now stands, so Confirm finds it done. One at a time on
        // one worker; a newer one replaces it (the older one's result goes unused).
        void FoldAhead()
        {
            if (_session.Count == 0 || _ctx.Earthwork?.Earthwork == null || _ctx.History == null) { _fold = null; return; }
            long t0 = Stopwatch.GetTimestamp();
            _fold = StartFold();
            UnityEngine.Debug.Log($"[TerrainBrush] frame {Time.frameCount}: folding the session's {_session.Count} strokes ahead of Confirm ({Ms(t0):F1} ms here)");
        }

        /// <summary>Whether the session's fold, started when the last press ended, has finished (tests).</summary>
        public bool FoldDone => _fold != null && _fold.Work.IsCompleted;

        /// <summary>Whether the last Confirm found its fold already started (the bench).</summary>
        public bool FoldWasAhead { get; private set; }

        /// <summary>The last Confirm's fold off the main thread: the command's Apply and the earthwork's Rebuild, ms (the bench).</summary>
        public (double Apply, double Rebuild) LastFold { get; private set; }

        /// <summary>From the last Confirm's click to its fold landing on the main thread, ms (the bench).</summary>
        public double LastConfirmToLandedMs { get; private set; }

        void Landed(AddTerrainStrokes command, TerrainEdits committed, CommandResult after, DesignEarthwork.Result rebuild)
        {
            _landed = true;
            _confirming = false;
            LastConfirmToLandedMs = Ms(_confirmClicked);
            if (!after.Accepted) { _afterConfirm = null; PaintStatus(); _ctx.Window.SetMessage(after.Reason); return; }
            long t0 = Stopwatch.GetTimestamp();
            var edits = rebuild.Edits;
            if (TerrainEdits.Changed(_pending, edits).Count == 0) edits = _pending;   // the same set the map shows: nothing applies again
            else UnityEngine.Debug.LogError("[TerrainBrush] the confirmed ground differs from the ground painted (TB14); confirming the fold's");
            double compare = Ms(t0);
            long t1 = Stopwatch.GetTimestamp();
            var towers = TowersReheighted(after.Document, committed, edits);
            double towersMs = Ms(t1);
            long t2 = Stopwatch.GetTimestamp();
            var result = _ctx.Submit?.Invoke(new AddTerrainStrokes(_ctx.History.Revision, command.Strokes), edits);
            double submit = Ms(t2);
            if (result == null || !result.Accepted) { _afterConfirm = null; PaintStatus(); return; }
            long t3 = Stopwatch.GetTimestamp();
            _ctx.Earthwork?.Earthwork?.Accept(rebuild);
            double accept = Ms(t3);
            // BR19: Confirm is when the player is charged; Phase 4's economy prices the volumes.
            _ctx.Charges?.Charge(new EarthworkBill(_cut, _fill, _cleared, _session.Count));
            string what = $"{_session.Count} stroke{(_session.Count == 1 ? "" : "s")} confirmed";
            if (towers.Count > 0) what += "; " + (towers.Count == 1 ? $"{towers[0].Label} {towers[0].Value}" : $"{towers.Count} towers re-heighted");
            long t4 = Stopwatch.GetTimestamp();
            _ctx.Notice?.Invoke(what);
            EndSession();
            PaintStatus();
            UnityEngine.Debug.Log($"[TerrainBrush] confirm landed: compare {compare:F1} ms, towers {towersMs:F1}, submit {submit:F1}, accept {accept:F1}, notice and pane {Ms(t4):F1}");
            _thenNext = _afterConfirm;
            _afterConfirm = null;
        }

        /// <summary>Revert (BR17): the session's ground goes back; nothing reaches the design or the bill.</summary>
        void Revert()
        {
            if (_run != null) CancelStroke();
            if (_session.Count == 0 || _confirming) return;
            long t0 = Stopwatch.GetTimestamp();
            if (!ReferenceEquals(_pending, _base) && _ctx.LiveGround != null) _settling = _ctx.LiveGround.ShowGround(_base, _base);
            double show = Ms(t0);
            long t1 = Stopwatch.GetTimestamp();
            _ctx.Notice?.Invoke("Ground reverted");
            double notice = Ms(t1);
            long t2 = Stopwatch.GetTimestamp();
            EndSession();
            PaintStatus();
            UnityEngine.Debug.Log($"[TerrainBrush] revert: ground {show:F1} ms, notice {notice:F1}, pane {Ms(t2):F1}");
        }

        // The tool stays open on a fresh session (P3-06 Q4); the next press starts it from the committed ground.
        void EndSession()
        {
            _base = null;
            _session.Clear();
            _presses.Clear();
            _cut = _fill = 0;
            _cleared = 0;
            _held.Clear();
            _liveDirty = PointRect.Empty;
            _fold = null;
            _foldDue = false;
            _ctx.LiveGround?.Painting(false);
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

        // ---------- the close ask (BR18) ----------

        /// <summary>
        /// BR18: Esc, ✕ or another kind of tool with ground not yet confirmed: Confirm / Revert / Keep painting.
        /// <paramref name="then"/> (close, or arm the next tool) runs once the ground is confirmed or reverted.
        /// </summary>
        public void AskBeforeLeaving(Action then)
        {
            if (_confirming) { _afterConfirm = then; return; }   // Confirm is already on its way: go once it's in
            if (_run != null) CancelStroke();
            if (_session.Count == 0) { then?.Invoke(); return; }
            _asking = true;
            _afterAsk = then;
            // The ask's row takes the pane's foot, as the mockup's terrainask: no second Confirm and Revert under it.
            _ctx.Window.SetAskRow("This ground isn't confirmed yet", GameAction.ToolNext,
                ("ask-confirm", "Confirm", () => Confirm(TakeAsk())),
                ("ask-revert", "Revert", () => { var next = TakeAsk(); Revert(); next?.Invoke(); }),
                ("ask-keep", "Keep painting", KeepPainting));
        }

        void KeepPainting() => TakeAsk();

        // Ends the ask and hands back what was to happen after it.
        Action TakeAsk()
        {
            var then = _afterAsk;
            _asking = false;
            _afterAsk = null;
            _ctx.Window.SetAskRow(null, GameAction.ToolNext);
            return then;
        }

        // ---------- the contract ----------

        public ToolBackOut Escape()
        {
            if (_asking) { KeepPainting(); return ToolBackOut.Stepped; }   // Esc on the ask: keep painting
            if (_run != null) { CancelStroke(); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;   // the host asks first when there's ground to confirm (BR18)
        }

        void CancelStroke()
        {
            _run?.Release();
            _run = null;
            _dabs = null;
            _ctx.LiveGround?.Painting(false);
            if (!_liveDirty.IsEmpty)
            {
                _ctx.LiveGround?.Live(_pending, _liveDirty);   // the ground as it was before the stroke
                CatchUp(_pending);                             // and its cover, trees' footing and snow (BR21)
            }
            _liveDirty = PointRect.Empty;
            PaintStatus();
        }

        /// <summary>The pane's buttons: Confirm (Built, as Break ground's place) and Revert (the second button). Both work here and return no command.</summary>
        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            var then = _asking ? TakeAsk() : null;   // while the close ask shows, they answer it (BR18)
            if (stage == WorkStage.Plan) { Revert(); then?.Invoke(); }
            else Confirm(then);
            return null;
        }

        /// <summary>Not used: Confirm submits its own command once the fold lands (<see cref="ToolContext.Submit"/>).</summary>
        public void Committed(CommandResult result) { }

        /// <summary>
        /// BR15: switching to another Terrain brush carries the session on (its strokes, the ground they made, the bill, Ctrl+Z
        /// and the target), so one Confirm takes them all. A stroke in hand is dropped.
        /// </summary>
        public bool CarryTo(IDesignTool next)
        {
            if (!(next is TerrainBrushTool t) || _base == null || _confirming) return false;
            if (_run != null) CancelStroke();
            if (_asking) TakeAsk();
            t._base = _base;
            t._pending = _pending;
            t._fixed = _fixed;
            t._session.AddRange(_session);
            t._presses.AddRange(_presses);
            t._cut = _cut;
            t._fill = _fill;
            t._cleared = _cleared;
            t._held.UnionWith(_held);
            t._targetMm = _targetMm;
            t._targetAt = _targetAt;
            t._fold = _fold;
            t._foldDue = _foldDue;
            _carried = true;
            return true;
        }

        bool _carried;

        public void Disarm()
        {
            if (_run != null) CancelStroke();
            // Closed without the ask (photo mode, the title, the Toolbox closing; BR18): the committed ground again, with a
            // notice, unless the next brush carries the session on (BR15).
            if (!_carried && _base != null && !ReferenceEquals(_pending, _base))
            {
                _ctx.LiveGround?.Show(_ctx.History.Terrain);
                if (_session.Count > 0) _ctx.Notice?.Invoke("Ground not confirmed: reverted");
            }
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
