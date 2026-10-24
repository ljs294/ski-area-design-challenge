using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    // Step 3, Towers, and the review (task 09; LF-04, TW-09, TW-13; the approved mockup; F1–F4, LT37, LT38). The line is
    // drawn in 3D as it stands: the terminals, the towers so far and the rope through them. By hand, the tower in hand (its
    // own see-through model, the head the game picks) follows the pointer up the line from the last placed tower (, and .
    // slide it 5 ft, Shift 50 ft), Page Up / Page Down set its height 5 ft at a time, and a click (or Enter) places it where
    // it's green; red says why. The green band shows where a tower that tall can stand. Every placed tower stays editable
    // (<see cref="LiftDraftEditor"/>): click it to change its height, slide, drag or remove it; a click on the line where no
    // tower is adds one. Delete takes the last tower back; Fill the rest finishes the line automatically; Revert to auto
    // places them all automatically (asking first). Automatic shows the towers the build placed; Edit towers (or any
    // change to one) opens them by hand. In the review a terminal can be clicked for its level (Page Up / Page Down, LT34's
    // "raise it 6 ft"). Enter with the line finished goes on to the review.
    public sealed partial class NewLiftTool
    {
        const double Slot = 1.524;   // 5 ft: TW-12's grid
        const int SlotBase = 8, HandSlot = 98, BandBase = 100, MaxBandRuns = 24;

        readonly List<Tower> _placed = new List<Tower>();
        TowerInHand _hand;
        EditedHeights _handGround;
        double _length;
        long _bandKey = long.MinValue, _hintKey2 = long.MinValue, _editorKey = long.MinValue;
        List<(double From, double To)> _band = new List<(double, double)>();
        readonly Vector2[][] _bandLines = new Vector2[MaxBandRuns][];
        bool _filling, _armReplace;
        int _drawnTowers;
        LiftProfile _handProfile;   // the strip's profile of the towers placed so far, strung to the top terminal
        LiftDraftEditor _editor;

        /// <summary>The towers placed by hand so far (tests).</summary>
        public IReadOnlyList<Tower> Placed => _placed;
        public double HandAlong => _editor != null && _editor.Adding ? _editor.HandS : -1;
        public TowerInHand.Status HandStatus => _editor?.HandStatus;
        public LiftDraftEditor Editor => _editor;
        int _handMm => _editor != null ? _editor.HandMm : DesignUnits.FeetToMm(35);

        Vector2 Dir => (_topXz - _bottomXz).normalized;
        Vector2 At(double s) => _bottomXz + Dir * (float)s;

        void EnsureEditor()
        {
            _editor ??= new LiftDraftEditor(_ctx)
            {
                Change = ChangeTowers,
                DragBegan = () => { },
                Dragged = change => { ByHandNow(); change(_placed); AfterChange(false); },
                DragEnded = () => AfterChange(true),
                HandFor = HandFor,
                KeepAdding = true,
                TakeBack = () => { if (_mode == TowerMode.ByHand && _placed.Count > 0) TakeBack(); },
                Level = TerminalLevel,
            };
            SyncEditor();
        }

        void SyncEditor()
        {
            if (_editor == null) return;
            _editor.BottomXz = _bottomXz;
            _editor.TopXz = _topXz;
            _editor.Type = Type;
            _editor.Stance = _stance;
            _editor.Towers = _mode == TowerMode.ByHand ? (IReadOnlyList<Tower>)_placed : AutoTowers();
            _editor.Rope = BuiltIsCurrent() && _built.Towers.Count == _editor.Towers.Count ? _built.Rope : null;
            // Terminals take their level in the review (LT34's suggestion); while placing towers they're the hand's.
            _editor.Terminals = _step == Step.Review;
        }

        // An automatic line's towers as the build placed them (no ids yet).
        List<Tower> _auto = new List<Tower>();
        IReadOnlyList<Tower> AutoTowers()
        {
            _auto.Clear();
            if (_built != null) for (int i = 0; i < _built.Towers.Count; i++) _auto.Add(new Tower(TowerId.Make(5, i), _built.Towers[i].AlongMm, _built.Towers[i].HeightMm));
            return _auto;
        }

        // F3: a change to an automatic line's tower opens it by hand, the build's towers its own.
        void ByHandNow()
        {
            if (_mode == TowerMode.ByHand) return;
            var towers = new List<Tower>(AutoTowers());
            _placed.Clear();
            _placed.AddRange(towers);
            _mode = TowerMode.ByHand;
            _modeDd.Set(new[] { "Automatic", "By hand" }, 1);
        }

        void ChangeTowers(System.Action<List<Tower>> change)
        {
            ByHandNow();
            change(_placed);
            for (int i = 0; i < _placed.Count; i++) _placed[i] = _placed[i].WithId(TowerId.Make(5, i));
            _armReplace = false;
            AfterChange(true);
        }

        void AfterChange(bool analyse)
        {
            SyncEditor();
            _editor?.TowersChanged();
            if (!analyse) { DrawTowers(); return; }
            PrepareHand();
            if (_step == Step.Review) StartBuild();
            // The line carried to the top terminal: the tower in hand is put down, the towers wait for edits.
            if (_step == Step.Towers && _editor != null && _editor.KeepAdding && _editor.Adding && _hand != null && TowerFill.IsComplete(Type, _hand, _placed.Count, _length))
                _editor.StopAdding();
            _hintKey2 = long.MinValue;
            ShowDraft();
        }

        // Step 3 opens: the editor over the line, and by hand the next tower in hand where the line isn't carried yet.
        void OpenTowers()
        {
            EnsureEditor();
            SyncEditor();
            _editor.Open();
            if (_mode != TowerMode.ByHand || _step != Step.Towers) { ShowDraft(); return; }
            PrepareHand();
            _editor.KeepAdding = true;
            if ((_hand == null || !TowerFill.IsComplete(Type, _hand, _placed.Count, _length)) && !_editor.Adding) _editor.StartAdding(_hand?.FirstAlong ?? double.NaN);
            ShowDraft();
        }

        // In the review: the selected terminal's level, 1 ft (LT19's range), and the lift worked out again.
        void TerminalLevel(bool bottom, int feet)
        {
            if (bottom) _bottomLevelFt = Mathf.Clamp(_bottomLevelFt + feet, LevelMinFt, LevelMaxFt);
            else _topLevelFt = Mathf.Clamp(_topLevelFt + feet, LevelMinFt, LevelMaxFt);
            _bottomZ = _bottomGround + _bottomLevelFt * 0.3048f;
            _topZ = _topGround + _topLevelFt * 0.3048f;
            PrepareHand();
            StartBuild();
            BuildReview();
            _ctx.Window.ShowReview(_review);
            _hintKey2 = long.MinValue;
        }

        TowerInHand HandFor(IReadOnlyList<Tower> below)
        {
            if (_handGround == null || _hand == null || _ctx.ToGround == null) return null;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            return new TowerInHand(Type, _handGround, b.X, b.Y, t.X, t.Y, _hand.Levels, below, _stance);
        }

        // The tower in hand checks against the edited ground, the terminals at their start levels (LT19), and the towers placed.
        void PrepareHand()
        {
            _hand = null;
            _bandKey = _hintKey2 = long.MinValue;
            var ground = _ctx.EditedGround?.Invoke();
            if (ground == null || _ctx.ToGround == null) return;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            double bottomHand = _bottomLevelFt * 0.3048, topHand = _topLevelFt * 0.3048;
            var levels = LiftBuild.StartLevels(Type, ground, b.X, b.Y, t.X, t.Y, bottomHand, topHand, out _, out _);
            _handGround = ground;
            _length = System.Math.Sqrt((t.X - b.X) * (t.X - b.X) + (t.Y - b.Y) * (t.Y - b.Y));
            _hand = new TowerInHand(Type, ground, b.X, b.Y, t.X, t.Y, levels, _placed, _stance);
            if (_step != Step.Towers || _mode != TowerMode.ByHand) return;
            double open = _placed.Count > 0 && !TowerFill.IsComplete(Type, _hand, _placed.Count, _length) ? _hand.Strung.Towers[_hand.Strung.Towers.Count - 1].RopeS
                        : _placed.Count == 0 ? 0 : double.NaN;
            _handProfile = ShowProfile(_hand.Strung, ground, levels, open);
        }

        // F4 (LT38): the line in 3D as it stands now: the towers placed (or the build's), the rope strung through them.
        void ShowDraft()
        {
            if (_ctx?.ShowDraftLift == null) return;
            bool line = _step == Step.Towers || _step == Step.Review;
            IReadOnlyList<Tower> towers = _mode == TowerMode.ByHand ? (IReadOnlyList<Tower>)_placed : AutoTowers();
            if (!line || towers.Count == 0) { _ctx.ShowDraftLift(null); return; }
            LiftLevels levels = BuiltIsCurrent() ? _built.Levels : _hand != null ? _hand.Levels
                : LiftLevels.FromMetres(_bottomZ, _topZ);
            var stored = new List<Tower>(towers.Count);
            for (int i = 0; i < towers.Count; i++) stored.Add(towers[i].WithId(TowerId.Make(5, i)));
            _ctx.ShowDraftLift(new Lift(LiftId.Make(5, 0), _name, WorkStage.Built, _catalogId, _bottom, _top, _mode, _stance, stored, levels));
            _ctx.BottomGhost?.Hide();   // the draft's own terminals stand there now
            _ctx.TopGhost?.Hide();
            _editor?.Relight();
        }

        void TowersTick(in ToolInput input)
        {
            EnsureEditor();
            SyncEditor();
            if (_mode == TowerMode.Automatic)
            {
                // The build's towers: Enter for the review; a click on one (or Add tower, Insert) opens them by hand.
                _editor.KeepAdding = false;
                if (BuiltIsCurrent()) _editor.Tick(input);
                if (input.Pressed(ToolKeys.Next) && !_editor.Adding) Go(Step.Review);
                EditorHint();
                DrawTowers();
                return;
            }
            if (_hand == null) PrepareHand();
            // The next tower is in hand from the step's start until the line is carried to the top terminal (AfterChange puts
            // it down then); Esc puts it down sooner, Insert or Add tower takes one up again.
            _editor.KeepAdding = true;
            bool wasAdding = _editor.Adding;
            _editor.Tick(input);
            if (_hand == null)
            {
                // No ground yet (or none at all: tests and labs): the height still steps, and Enter goes on to the review.
                if (input.Pressed(ToolKeys.Next)) Go(Step.Review);
                HandHint();
                return;
            }
            bool complete = _hand != null && TowerFill.IsComplete(Type, _hand, _placed.Count, _length);
            if (input.Pressed(ToolKeys.Next) && !wasAdding && complete) Go(Step.Review);
            if (_step != Step.Towers) return;
            BandAndProfile(complete);
            HandHint(complete);
            DrawTowers(complete);
        }

        // TW-09's green band for the height in hand, and the strip's tower in hand (rebuilt only when they change).
        void BandAndProfile(bool complete)
        {
            bool hand = _editor.Adding && _editor.HandStatus != null;
            long bandKey = _handMm * 1000L + _placed.Count;
            if (bandKey != _bandKey && _hand != null)
            {
                _bandKey = bandKey;
                _band = _hand.Band(_handMm);
                if (_handProfile != null) { _handProfile.Band = _band; _handProfile.Version++; }
                for (int i = 0; i < _band.Count && i < MaxBandRuns; i++)
                {
                    _bandLines[i] ??= new Vector2[2];
                    _bandLines[i][0] = At(_band[i].From);
                    _bandLines[i][1] = At(_band[i].To);
                }
            }
            if (_handProfile == null) return;
            double s = hand ? _editor.HandS : double.NaN;
            bool ok = hand && _editor.HandStatus.Ok;
            if (s != _handProfile.HandS && !(double.IsNaN(s) && double.IsNaN(_handProfile.HandS)) || _handProfile.HandMm != _handMm || _handProfile.HandOk != ok)
            {
                _handProfile.HandS = s;
                _handProfile.HandMm = _handMm;
                _handProfile.HandOk = ok;
                if (complete) _handProfile.Band = new List<(double, double)>();
                _ctx.Window.Strip?.Refresh();
            }
        }

        void TakeBack()
        {
            _placed.RemoveAt(_placed.Count - 1);
            AfterChange(true);
            if (_editor != null && !_editor.Adding && _hand != null) _editor.StartAdding(_hand.FirstAlong);
        }

        // Fill the rest (LF-04): the line finished from the last placed tower, off the main thread; then the review.
        void FillTheRest()
        {
            if (_filling || _handGround == null || _ctx.ToGround == null) return;
            _filling = true;
            _editor?.StopAdding();
            var type = Type;
            var ground = _handGround;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            double bottomHand = _bottomLevelFt * 0.3048, topHand = _topLevelFt * 0.3048;
            var placed = new List<Tower>(_placed);
            var stance = _stance;
            _hintKey2 = long.MinValue;
            _ctx.Analyse(() => TowerFill.Fill(type, ground, b.X, b.Y, t.X, t.Y, bottomHand, topHand, placed, stance), r =>
            {
                _filling = false;
                if (_step != Step.Towers) return;
                _placed.Clear();
                for (int i = 0; i < r.Towers.Count; i++) _placed.Add(new Tower(TowerId.Make(5, i), r.Towers[i].AlongMm, r.Towers[i].HeightMm, r.Towers[i].Kind));
                AfterChange(true);
                if (r.Problem is LiftProblem p) _ctx.Window.SetMessage(p.Message);
                else Go(Step.Review);
            });
        }

        // F3: Revert to auto: every tower placed automatically again and the lift Automatic, asking first where towers were
        // placed or changed by hand (LF-01).
        void RevertToAuto()
        {
            if (_mode == TowerMode.ByHand && _placed.Count > 0 && !_armReplace) { _armReplace = true; _hintKey2 = long.MinValue; ReviewButtons(); return; }
            _armReplace = false;
            _editor?.Release();
            _placed.Clear();
            _mode = _lastMode = TowerMode.Automatic;
            _modeDd.Set(new[] { "Automatic", "By hand" }, 0);
            Go(_step == Step.Review ? Step.Review : Step.Towers);
        }

        // F3: Edit towers: an automatic line's towers opened by hand, as placed.
        void EditTowers()
        {
            if (_built == null) return;
            ByHandNow();
            AfterChange(true);
            Go(_step);
        }

        // The window while a tower is in hand: its height and place, then its status (rebuilt only when either changes).
        void HandHint(bool complete = false)
        {
            var w = _ctx.Window;
            long key = (_editor?.StateKey ?? 0) * 4 + (complete ? 1 : 0) + (_filling ? 2 : 0) + (_armReplace ? 1_000_000_007L : 0) + _placed.Count * 7919L;
            if (key == _hintKey2) return;
            _hintKey2 = key;
            string height = ToolFormat.Feet(Mathf.RoundToInt(_handMm / 304.8f));
            if (_hand == null) w.SetHint($"Tower in hand: {height}. Getting the ground ready…");
            else if (_filling) w.SetHint("Filling the rest of the line…");
            else if (_editor != null && (_editor.Adding || _editor.Selected != LiftDraftEditor.None))
            {
                string line = _editor.Describe();
                if (_editor.Adding && _editor.HandStatus != null && _editor.HandStatus.Ok)
                    line += $" · the next can reach {ToolFormat.Length(_editor.HandStatus.NextReach)}";
                w.SetHint(line);
            }
            else if (complete) w.SetHint($"{_placed.Count} towers carry the line to the top terminal. Click one to change it, or Enter for the review.");
            else w.SetHint($"{_placed.Count} towers placed. Insert (or Add tower) puts the next in hand; Delete takes the last one back.");
            w.ShowGauge(_editor?.Loads, provisional: _editor != null && _editor.Adding);
            if (_editor?.HandProblem is LiftProblem p) w.SetMessage(p.Message, !p.Blocks);
            else w.SetMessage(null);
            w.SetKeys((GameAction.TowerSlideBack, "slide"), (GameAction.TowerSlideOn, "slide"), (GameAction.TowerUp, "taller"),
                      (GameAction.TowerDown, "shorter"), (GameAction.ToolNext, _editor != null && _editor.Adding ? "place" : "review"),
                      (GameAction.DeleteSelected, _editor != null && _editor.Selected >= 0 ? "remove" : "take back"), (GameAction.TowerAdd, "add"), (GameAction.TowerKind, "kind"));
            w.SetButtons(
                ("fill", "Fill the rest", _filling || _hand == null || complete ? null : (System.Action)FillTheRest),
                ("add", "Add tower", _filling || _hand == null || _editor == null || _editor.Adding ? null : (System.Action)(() => _editor.StartAdding())),
                ("replace", _armReplace ? "Again: revert yours" : "Revert to auto", _filling ? null : (System.Action)RevertToAuto),
                ("review", "Review", () => Go(Step.Review)));
            w.SetAsking(_armReplace ? "Your towers go: Revert to auto again" : null);
        }

        // The window over an automatic line's towers (the Towers step) or the review's: the selection, if any.
        void EditorHint()
        {
            if (_editor == null || _editor.StateKey == _editorKey) return;
            _editorKey = _editor.StateKey;
            var w = _ctx.Window;
            string line = _editor.Describe();
            if (_step == Step.Towers)
            {
                if (line != null) w.SetHint(line);
                else TowersPlacedHint();
                w.SetButtons(("edit", "Edit towers", _built != null ? (System.Action)EditTowers : null), ("review", "Review", () => Go(Step.Review)));
            }
            else
            {
                // LT34: a suggestion to raise or lower a terminal is acted on by clicking it here.
                bool levels = _built != null && _built.Hints.Count > 0;
                w.SetHint(line ?? (levels ? "Click a terminal, then Page Up / Page Down to set its level (the review says by how much)." : null));
                ReviewButtons();
            }
            w.ShowGauge(_editor.Loads, provisional: _editor.Adding);
            if (_editor.HandProblem is LiftProblem p) w.SetMessage(p.Message, !p.Blocks);
        }

        // The review's own buttons: every tower stays editable (the mockup), and a by-hand line can go back to automatic.
        void ReviewButtons()
        {
            var w = _ctx.Window;
            if (_step != Step.Review) return;
            w.SetButtons(
                ("add", "Add tower", _editor == null || _editor.Adding || _built == null ? null : (System.Action)(() => { EnsureEditor(); _editor.KeepAdding = false; _editor.StartAdding(); })),
                ("replace", _armReplace ? "Again: revert yours" : _mode == TowerMode.ByHand ? "Revert to auto" : "Edit towers",
                    _mode == TowerMode.ByHand ? (System.Action)RevertToAuto : _built != null ? (System.Action)EditTowers : null));
            w.SetAsking(_armReplace ? "Your towers go: Revert to auto again" : null);
        }

        // The towers on the map: the placed ones (or the build's, in Automatic) as dots on the line, the tower in hand green
        // or red, and the green band along the line where a tower that tall can stand.
        void DrawTowers(bool complete = false)
        {
            var p = _ctx.Preview;
            int n = 0;
            if (_mode == TowerMode.ByHand)
                foreach (var t in _placed) p.Dot(SlotBase + n++, At(t.AlongMm / 1000.0), _editor != null && _editor.Selected == n - 1 ? PreviewInk.Ok : PreviewInk.Lift);
            else if (_built != null)
                foreach (var t in _built.Towers) p.Dot(SlotBase + n++, At(t.AlongMm / 1000.0), PreviewInk.Lift);
            for (int i = n; i < _drawnTowers; i++) p.Hide(SlotBase + i);
            _drawnTowers = n;
            bool hand = _editor != null && _editor.Adding && _editor.HandStatus != null;
            if (hand) p.Dot(HandSlot, At(_editor.HandS), _editor.HandStatus.Ok ? PreviewInk.Ok : PreviewInk.Bad);
            else p.Hide(HandSlot);
            int runs = hand && _mode == TowerMode.ByHand ? Mathf.Min(_band.Count, MaxBandRuns) : 0;
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
            _armReplace = false;
            _editor?.Release();
            _ctx?.ShowDraftLift?.Invoke(null);
            _ctx?.Window.SetButtons();
            _ctx?.Preview.HideFrom(SlotBase);
            _drawnTowers = 0;
        }
    }
}
