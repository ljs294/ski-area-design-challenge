using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    // Step 3, Towers (task 09; LF-04, TW-09, TW-13; the approved mockup). By hand, the tower in hand follows the pointer up the
    // line from the last placed tower (← / → slide it 5 ft, Shift 50 ft), Page Up / Page Down set its height 5 ft at a time,
    // and a click (or Enter) places it where it's green; red says why. The green band shows where a tower that tall can
    // stand. Delete takes the last tower back; Fill the rest finishes the line automatically; Re-place towers places them
    // all (asking first). Enter with the line finished goes on to the review. Automatic shows the towers the build placed.
    public sealed partial class NewLiftTool
    {
        const double Slot = 1.524;   // 5 ft: TW-12's grid
        const int SlotBase = 8, HandSlot = 98, BandBase = 100, MaxBandRuns = 24;

        readonly List<Tower> _placed = new List<Tower>();
        TowerInHand _hand;
        EditedHeights _handGround;
        double _length, _handS = -1;
        int _handMm = DesignUnits.FeetToMm(35);
        TowerInHand.Status _status;
        long _statusKey = long.MinValue, _bandKey = long.MinValue, _hintKey2 = long.MinValue;
        List<(double From, double To)> _band = new List<(double, double)>();
        readonly Vector2[][] _bandLines = new Vector2[MaxBandRuns][];
        Vector2 _lastPointer = new Vector2(float.NaN, float.NaN);
        bool _filling, _armReplace;
        int _drawnTowers;

        /// <summary>The towers placed by hand so far (tests).</summary>
        public IReadOnlyList<Tower> Placed => _placed;
        public double HandAlong => _handS;
        public TowerInHand.Status HandStatus => _status;

        Vector2 Dir => (_topXz - _bottomXz).normalized;
        Vector2 At(double s) => _bottomXz + Dir * (float)s;

        // The tower in hand checks against the edited ground, the terminals at their start levels (LT19), and the towers placed.
        void PrepareHand()
        {
            _hand = null;
            _statusKey = _bandKey = _hintKey2 = long.MinValue;
            var ground = _ctx.EditedGround?.Invoke();
            if (ground == null || _ctx.ToGround == null) return;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            double bottomHand = _bottomLevelFt * 0.3048, topHand = _topLevelFt * 0.3048;
            var levels = LiftBuild.StartLevels(Type, ground, b.X, b.Y, t.X, t.Y, bottomHand, topHand, out _, out _);
            _handGround = ground;
            _length = System.Math.Sqrt((t.X - b.X) * (t.X - b.X) + (t.Y - b.Y) * (t.Y - b.Y));
            _hand = new TowerInHand(Type, ground, b.X, b.Y, t.X, t.Y, levels, _placed, _stance);
            if (_handS < _hand.FirstAlong) _handS = _hand.FirstAlong;
        }

        void TowersTick(in ToolInput input)
        {
            if (_mode == TowerMode.Automatic)
            {
                if (input.Pressed(ToolKeys.Next)) Go(Step.Review);
                DrawTowers();
                return;
            }
            int maxMm = LiftCatalogIds.TowerMaxMm(_catalogId);
            if (input.Pressed(ToolKeys.TowerUp)) _handMm = Mathf.Min(maxMm, _handMm + DesignUnits.FiveFeetMm);
            if (input.Pressed(ToolKeys.TowerDown)) _handMm = Mathf.Max(DesignUnits.StationTowerMinMm, _handMm - DesignUnits.FiveFeetMm);
            if (_hand == null) PrepareHand();
            if (_hand == null)
            {
                // No ground yet (or none at all: tests and labs): the height still steps, and Enter goes on to the review.
                if (input.Pressed(ToolKeys.Next)) Go(Step.Review);
                HandHint();
                return;
            }
            double step = input.Shift ? 10 * Slot : Slot;
            if (input.Pressed(ToolKeys.SlideOn)) _handS += step;
            if (input.Pressed(ToolKeys.SlideBack)) _handS -= step;
            if (input.HasGround && !input.OverPanel)
            {
                var p = new Vector2(input.Ground.x, input.Ground.z);
                if (float.IsNaN(_lastPointer.x) || (p - _lastPointer).sqrMagnitude > 0.25f)
                {
                    _lastPointer = p;
                    _handS = Vector2.Dot(p - _bottomXz, Dir);
                }
            }
            double first = _hand.FirstAlong, last = _length - DesignUnits.TowerGapMm / 1000.0;
            _handS = System.Math.Round(_handS / Slot) * Slot;
            _handS = System.Math.Max(first, System.Math.Min(last, _handS));
            long key = (long)System.Math.Round(_handS * 1000) * 1000 + _handMm / DesignUnits.FiveFeetMm * 7 + _placed.Count;
            if (key != _statusKey)
            {
                _statusKey = key;
                _status = last >= first ? _hand.Evaluate(_handS, _handMm) : null;
            }
            long bandKey = _handMm * 1000L + _placed.Count;
            if (bandKey != _bandKey)
            {
                _bandKey = bandKey;
                _band = _hand.Band(_handMm);
                for (int i = 0; i < _band.Count && i < MaxBandRuns; i++)
                {
                    _bandLines[i] ??= new Vector2[2];
                    _bandLines[i][0] = At(_band[i].From);
                    _bandLines[i][1] = At(_band[i].To);
                }
            }
            bool complete = TowerFill.IsComplete(Type, _hand, _placed.Count, _length);
            if (input.Click && !input.OverPanel && _status != null && _status.Ok && !complete) PlaceInHand();
            else if (input.Pressed(ToolKeys.Next))
            {
                if (complete) Go(Step.Review);
                else if (_status != null && _status.Ok) PlaceInHand();
            }
            if (input.Pressed(ToolKeys.Delete) && _placed.Count > 0) TakeBack();
            HandHint(complete);
            DrawTowers(complete);
        }

        void PlaceInHand()
        {
            int along = (int)System.Math.Round(_handS * 1000 / DesignUnits.FiveFeetMm) * DesignUnits.FiveFeetMm;
            _placed.Add(new Tower(TowerId.Make(5, _placed.Count), along, _handMm));
            _armReplace = false;
            PrepareHand();
            _lastPointer = new Vector2(float.NaN, float.NaN);   // the next one starts past it, wherever the pointer is
            _handS = _hand != null ? _hand.FirstAlong : _handS;
        }

        void TakeBack()
        {
            _placed.RemoveAt(_placed.Count - 1);
            PrepareHand();
        }

        // Fill the rest (LF-04): the line finished from the last placed tower, off the main thread; then the review.
        void FillTheRest(bool all)
        {
            if (_filling || _handGround == null || _ctx.ToGround == null) return;
            if (all && _placed.Count > 0 && !_armReplace) { _armReplace = true; _hintKey2 = long.MinValue; return; }
            _armReplace = false;
            _filling = true;
            var type = Type;
            var ground = _handGround;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            double bottomHand = _bottomLevelFt * 0.3048, topHand = _topLevelFt * 0.3048;
            var placed = all ? new List<Tower>() : new List<Tower>(_placed);
            var stance = _stance;
            _hintKey2 = long.MinValue;
            _ctx.Analyse(() => TowerFill.Fill(type, ground, b.X, b.Y, t.X, t.Y, bottomHand, topHand, placed, stance), r =>
            {
                _filling = false;
                if (_step != Step.Towers) return;
                _placed.Clear();
                for (int i = 0; i < r.Towers.Count; i++) _placed.Add(new Tower(TowerId.Make(5, i), r.Towers[i].AlongMm, r.Towers[i].HeightMm));
                PrepareHand();
                if (r.Problem is LiftProblem p) _ctx.Window.SetMessage(p.Message);
                else Go(Step.Review);
            });
        }

        // The window while a tower is in hand: its height and place, then its status (rebuilt only when either changes).
        void HandHint(bool complete = false)
        {
            var w = _ctx.Window;
            long key = _statusKey * 4 + (complete ? 1 : 0) + (_filling ? 2 : 0) + (_armReplace ? 1_000_000_007L : 0) + _handMm;
            if (key == _hintKey2) return;
            _hintKey2 = key;
            int n = _placed.Count + 1;
            string height = ToolFormat.Feet(Mathf.RoundToInt(_handMm / 304.8f));
            if (_hand == null) w.SetHint($"Tower in hand: {height}. Getting the ground ready…");
            else if (_filling) w.SetHint("Filling the rest of the line…");
            else if (complete) w.SetHint($"{_placed.Count} towers carry the line to the top terminal. Enter for the review, or Delete to take the last one back.");
            else
            {
                string where = $"Tower {n} in hand: {height} at {ToolFormat.Length(_handS)} up the line. Click to place it where it's green.";
                if (_status != null && _status.Ok)
                    where += $"\n{HeadName(_status.Head)} · lowest chairs {ToolFormat.Length(_status.LowestClearance)} · the next can reach {ToolFormat.Length(_status.NextReach)}";
                w.SetHint(where);
            }
            if (_status != null && !complete && _status.Problem is LiftProblem bad) w.SetMessage(bad.Message);
            else if (_status != null && !complete && _status.Warning is LiftProblem amber) w.SetMessage(amber.Message, true);
            else w.SetMessage(null);
            w.SetButtons(
                ("fill", "Fill the rest", _filling || _hand == null || complete ? null : (System.Action)(() => FillTheRest(false))),
                ("replace", _armReplace ? "Again: re-place yours" : "Re-place towers", _filling || _hand == null ? null : (System.Action)(() => FillTheRest(true))),
                ("review", "Review", () => Go(Step.Review)));
        }

        static string HeadName(LiftRope.Head head) => head switch
        {
            LiftRope.Head.S4 => "Support, 4 sheaves",
            LiftRope.Head.S6 => "Support, 6 sheaves",
            LiftRope.Head.B8 => "Breakover, 8 sheaves",
            LiftRope.Head.D8 => "Hold-down, 8 sheaves",
            LiftRope.Head.C8 => "Combination, 4 + 4 sheaves",
            _ => "Too sharp for one head",
        };

        // The towers on the map: the placed ones (or the build's, in Automatic) as dots on the line, the tower in hand green
        // or red, and the green band along the line where a tower that tall can stand.
        void DrawTowers(bool complete = false)
        {
            var p = _ctx.Preview;
            int n = 0;
            if (_mode == TowerMode.ByHand)
                foreach (var t in _placed) p.Dot(SlotBase + n++, At(t.AlongMm / 1000.0), PreviewInk.Line);
            else if (_built != null)
                foreach (var t in _built.Towers) p.Dot(SlotBase + n++, At(t.AlongMm / 1000.0), PreviewInk.Line);
            for (int i = n; i < _drawnTowers; i++) p.Hide(SlotBase + i);
            _drawnTowers = n;
            bool hand = _mode == TowerMode.ByHand && _hand != null && !complete && _status != null && _step == Step.Towers;
            if (hand) p.Dot(HandSlot, At(_handS), _status.Ok ? PreviewInk.Ok : PreviewInk.Bad);
            else p.Hide(HandSlot);
            int runs = hand ? Mathf.Min(_band.Count, MaxBandRuns) : 0;
            for (int i = 0; i < MaxBandRuns; i++)
            {
                if (i < runs) p.Line(BandBase + i, _bandLines[i], 2, PreviewInk.Ok);
                else p.Hide(BandBase + i);
            }
        }

        void ClearTowers()
        {
            _placed.Clear();
            _hand = null;
            _handGround = null;
            _handS = -1;
            _armReplace = false;
            _lastPointer = new Vector2(float.NaN, float.NaN);
            _ctx?.Window.SetButtons();
            _ctx?.Preview.HideFrom(SlotBase);
            _drawnTowers = 0;
        }
    }
}
