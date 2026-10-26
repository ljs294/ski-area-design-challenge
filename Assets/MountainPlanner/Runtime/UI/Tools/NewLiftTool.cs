using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Lifts › New (LF-01 to LF-03, LF-08; P3-06's part; task 09's plop, LT19–LT22). The bottom terminal's own
    /// model follows the pointer, see-through and green where it can stand, amber where the ground under it varies over 10 ft (its pad is levelled),
    /// red where it can't; Shift + wheel or , and . turn it (a preview of the space it takes, LT20) and Page Up / Page Down
    /// set its level. A click anchors it; the top terminal then follows the pointer facing back along the line, with its
    /// own level, and a second click fixes the line. Step 3, Towers,
    /// holds the Automatic or By hand switch (LF-01) and the tower in hand's height (Page Up / Page Down, LF-09); placing
    /// towers along the line is task 09's. Break ground submits <see cref="AddLift"/> (CF-05, CF-06).
    /// </summary>
    public sealed partial class NewLiftTool : IDesignTool
    {
        public const string ToolId = "new-lift";

        const float TerminalRadius = 8f;        // the ghost's footprint around the bullwheel, metres
        const float SitingReliefMetres = 3.048f;   // LF-08: the ground varies by no more than 10 ft under a terminal
        const float MinApartMetres = 91.44f;       // LF-08: the terminals stand at least 300 ft apart
        static readonly string[] Steps = { "First terminal", "Other terminal", "Towers", "Review" };
        // The switch remembers the last choice for the session (LF-01: Automatic the first time).
        static TowerMode _lastMode = TowerMode.Automatic;

        enum Step { First, Other, Towers, Review }

        ToolContext _ctx;
        Step _step;
        PointMm _bottom, _top;
        Vector2 _bottomXz, _topXz;
        float _bottomZ, _topZ;
        LiftTypeMenu _typeMenu;
        string _catalogId => _typeMenu.CatalogId;
        TowerMode _mode;
        TowerStance _stance = TowerStance.Plumb;
        string _name;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _line = new Vector2[2];
        ToolDropdown _modeDd, _stanceDd;
        int _hintKey = int.MinValue;
        string _problem;
        // Task 09: the lift as Break ground would build it (LiftBuild: towers, levels, earthwork, clearing), or null while it's
        // being worked out (off the main thread, CF-02) or the ground isn't ready.
        LiftBuild.Result _built;
        bool _building;
        // Task 09's plop (LT19–LT22): the bottom's preview turn (degrees from uphill), each terminal's level set by hand
        // (feet above the ground at its origin), and the type the ghosts show.
        float _turnDeg;
        int _bottomLevelFt, _topLevelFt;
        float _bottomGround, _topGround;
        Vector2 _bottomFacing = Vector2.up;
        LiftType _liftType;
        string _liftTypeOf;
        const int LevelMinFt = -10, LevelMaxFt = 30;
        const float MaxSitingFeet = 40;   // LC-09: amber past this, still placeable

        public string Id => ToolId;
        public string Name => "New lift";
        public bool HasWork => _step != Step.First;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _mode = _lastMode;
            _name = NextName(context.History.Document);
            var w = context.Window;
            w.SetName(_name);
            // F5: the name in the title renames the lift Break ground (or Save plan) adds.
            w.SetRenamable(n =>
            {
                if (DesignRules.NameProblem(n) is string bad) return bad;
                _name = n;
                return null;
            });
            _typeMenu = new LiftTypeMenu(w, LiftCatalogIds.SesselliftFgq4, Retype, "The kind of lift (owner, 2026-10-19)");
            (_modeDd, _stanceDd) = w.AddDropdownPair("towers", "Towers", "mode", PickMode, "stance", PickStance,
                "LF-01, TW-12: automatic towers, or yours; it can change until you break ground", "TW-14: plumb, or leaned square to the rope (15° at most)");
            _modeDd.Set(new[] { "Automatic", "By hand" }, _mode == TowerMode.Automatic ? 0 : 1);
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, _stance == TowerStance.Plumb ? 0 : 1);
            _typeMenu.AddNote();
            w.SetSteps(Steps);
            Go(Step.First);
        }

        void PickMode(int i)
        {
            _mode = _lastMode = i == 0 ? TowerMode.Automatic : TowerMode.ByHand;
            _modeDd.Set(new[] { "Automatic", "By hand" }, i);
            if (_step == Step.Towers) Go(Step.Towers);
        }

        void PickStance(int i)
        {
            _stance = i == 0 ? TowerStance.Plumb : TowerStance.Leaned;
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, i);
            Retype();
        }

        /// <summary>Picks a lift type by its catalog id (the menu, tests and captures).</summary>
        public void Choose(string catalogId) => _typeMenu.Choose(catalogId);

        // A new type or stance while the line is fixed: the review works it out again.
        void Retype()
        {
            if (_step == Step.Review || (_step == Step.Towers && _mode == TowerMode.Automatic)) Go(_step);
            else if (_step == Step.Towers) { PrepareHand(); _hintKey2 = long.MinValue; SyncEditor(); _editor?.TowersChanged(); ShowDraft(); }
        }

        static string NextName(DesignDocument doc) => "Chair " + (doc.Lifts.Count + 1);

        void Go(Step step)
        {
            var w = _ctx.Window;
            w.SetMessage(null);
            _hintKey = int.MinValue;
            // By hand, no towers yet: the review waits for them (LF-04).
            if (step == Step.Review && _mode == TowerMode.ByHand && _placed.Count == 0 && _ctx.EditedGround != null && _ctx.ToGround != null)
            {
                w.SetMessage("Place the towers first, or Fill the rest");
                if (_step == Step.Towers) return;
                step = Step.Towers;
            }
            _step = step;
            w.SetStep((int)step);
            _editorKey = long.MinValue;
            // F1–F4: the line's towers stay editable in step 3 and the review; before that there's no line.
            if (step != Step.Towers && step != Step.Review) { _editor?.Release(); _ctx.ShowDraftLift?.Invoke(null); }
            if (step == Step.Review) _editor?.StopAdding();   // the review's towers wait for a click
            if (step != Step.Towers) { w.SetButtons(); w.ShowGauge(null); }
            // LT19, LT21: a terminal in hand turns (the bottom only: the top faces the line) and takes its level.
            bool terminalInHand = step == Step.First || step == Step.Other;
            _ctx.Claim(GameAction.TerminalTurnLeft, step == Step.First);
            _ctx.Claim(GameAction.TerminalTurnRight, step == Step.First);
            _ctx.Claim(GameAction.TerminalLevelUp, terminalInHand);
            _ctx.Claim(GameAction.TerminalLevelDown, terminalInHand);
            if (step == Step.First) _ctx.TopGhost?.Hide();
            switch (step)
            {
                case Step.First:
                    w.SetHint("The bottom terminal follows the pointer, green where it can stand. Click to place it: the dot is its bullwheel.");
                    w.SetKeys((GameAction.TerminalTurnLeft, "turn"), (GameAction.TerminalTurnRight, "turn"),
                              (GameAction.TerminalLevelUp, "higher"), (GameAction.TerminalLevelDown, "lower"));
                    w.SetNext(false, false);
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    w.HideStrip();
                    break;
                case Step.Other:
                    w.SetHint("Click far up the mountain to place the top terminal. That fixes the line.");
                    w.SetKeys((GameAction.TerminalLevelUp, "higher"), (GameAction.TerminalLevelDown, "lower"));
                    w.SetNext(false, false);
                    w.HideStrip();   // the strip comes with the line: while aiming it would cover the mountain
                    break;
                case Step.Towers:
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    if (_mode == TowerMode.Automatic)
                    {
                        w.SetHint("The towers go in automatically along the line…");
                        w.SetKeys((GameAction.ToolNext, "review"));
                        w.SetButtons();
                        w.SetNext(true, true);
                        if (!BuiltIsCurrent()) StartBuild();
                        else TowersPlacedHint();
                        OpenTowers();
                    }
                    else
                    {
                        w.SetNext(false, false);
                        _hintKey2 = long.MinValue;
                        OpenTowers();
                        HandHint();
                    }
                    break;
                case Step.Review:
                    w.SetHint(null);
                    w.SetKeys();   // Break ground shows its own key
                    w.SetNext(false, false);
                    StartBuild();
                    BuildReview();
                    w.ShowReview(_review);
                    OpenTowers();
                    ReviewButtons();
                    break;
            }
            Draw(default, false);
        }

        public void Tick(in ToolInput input)
        {
            switch (_step)
            {
                case Step.First:
                {
                    // LT21: turn (5°, Shift 45°, a wheel notch 15°) and level (1 ft) the terminal in hand.
                    if (input.Pressed(ToolKeys.TurnLeft)) _turnDeg += input.Shift ? 45 : 5;
                    if (input.Pressed(ToolKeys.TurnRight)) _turnDeg -= input.Shift ? 45 : 5;
                    _turnDeg = Mathf.Repeat(_turnDeg + input.TurnNotches * 15, 360);
                    StepLevel(input, ref _bottomLevelFt);
                    if (!input.HasGround) { Draw(default, false); _ctx.BottomGhost?.Hide(); return; }
                    var at = new Vector2(input.Ground.x, input.Ground.z);
                    var facing = Turn(Uphill(at), _turnDeg);
                    var end = Type.Bottom;
                    var tint = Site(end, at, facing, out float ground, out float relief);
                    float level = ground + _bottomLevelFt * 0.3048f;
                    _problem = tint == GhostTint.Bad ? "A terminal must stand inside the area" : null;
                    _ctx.BottomGhost?.Show(end.Model, new Vector3(at.x, level, at.y), level, new Vector3(facing.x, 0, facing.y), tint);
                    Draw(at, true);
                    if (_problem != null) { _ctx.Window.SetMessage(_problem); _levelKey = int.MinValue; }
                    else Levelling("Bottom", level, _bottomLevelFt, relief);
                    if (input.Click && _problem == null)
                    {
                        _bottom = input.Point;
                        _bottomXz = at;
                        _bottomZ = level;
                        _bottomGround = ground;
                        _bottomFacing = facing;
                        Go(Step.Other);
                    }
                    break;
                }
                case Step.Other:
                {
                    StepLevel(input, ref _topLevelFt);
                    if (!input.HasGround) { Draw(default, false); _ctx.TopGhost?.Hide(); return; }
                    var at = new Vector2(input.Ground.x, input.Ground.z);
                    var up = at - _bottomXz;
                    var along = up.sqrMagnitude > 1e-4f ? up.normalized : _bottomFacing;
                    // LF-02: the anchored bottom swings to face the pointer; the top faces back along the line (LT20).
                    _ctx.BottomGhost?.Show(Type.Bottom.Model, new Vector3(_bottomXz.x, _bottomZ, _bottomXz.y), _bottomZ, new Vector3(along.x, 0, along.y), GhostTint.Ok);
                    var tint = Site(Type.Top, at, -along, out float ground, out float relief);
                    float z = ground + _topLevelFt * 0.3048f;
                    _problem = (tint == GhostTint.Bad ? "A terminal must stand inside the area" : null)
                        ?? (z <= _bottomZ ? "The top terminal must be higher" : null)
                        ?? (Vector2.Distance(at, _bottomXz) < MinApartMetres ? "The terminals stand at least 300 ft apart" : null);
                    _ctx.TopGhost?.Show(Type.Top.Model, new Vector3(at.x, z, at.y), z, new Vector3(-along.x, 0, -along.y), _problem != null ? GhostTint.Bad : tint);
                    Draw(at, true);
                    if (_problem != null) { _ctx.Window.SetMessage(_problem); _levelKey = int.MinValue; }
                    else Levelling("Top", z, _topLevelFt, relief, Vector2.Distance(at, _bottomXz), z - _bottomZ);
                    if (input.Click && _problem == null)
                    {
                        _top = input.Point;
                        _topXz = at;
                        _topZ = z;
                        _topGround = ground;
                        Go(Step.Towers);
                    }
                    break;
                }
                case Step.Towers:
                    TowersTick(input);
                    break;
                case Step.Review:
                    // Every tower stays editable (the mockup); a terminal clicked takes its level (LT34's suggestion).
                    EnsureEditor();
                    SyncEditor();
                    _editor.KeepAdding = false;
                    _editor.Tick(input);
                    EditorHint();
                    DrawTowers();
                    break;
            }
        }

        LiftType Type
        {
            get
            {
                if (_liftTypeOf != _catalogId) { _liftType = LiftType.Of(_catalogId); _liftTypeOf = _catalogId; }
                return _liftType;
            }
        }

        void StepLevel(in ToolInput input, ref int feet)
        {
            if (input.Pressed(ToolKeys.LevelUp)) feet = Mathf.Min(LevelMaxFt, feet + 1);
            if (input.Pressed(ToolKeys.LevelDown)) feet = Mathf.Max(LevelMinFt, feet - 1);
        }

        /// <summary>The way up the hill at <paramref name="at"/> (the mockup's default facing), or north on the flat.</summary>
        Vector2 Uphill(Vector2 at)
        {
            const float d = 10;
            float gx = _ctx.HeightAt(at + new Vector2(d, 0)) - _ctx.HeightAt(at - new Vector2(d, 0));
            float gz = _ctx.HeightAt(at + new Vector2(0, d)) - _ctx.HeightAt(at - new Vector2(0, d));
            var g = new Vector2(gx, gz);
            return float.IsNaN(g.x) || float.IsNaN(g.y) || g.sqrMagnitude < 1e-6f ? Vector2.up : g.normalized;
        }

        static Vector2 Turn(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        /// <summary>
        /// LF-08 over the terminal's real footprint as it faces (<see cref="TerminalEnd"/>, measured on its model): red
        /// outside the area; amber where the ground under it varies more than 10 ft, so its pad is cut and filled to level it (LF-12 v4)
        /// (LF-08 v2, LC-09); green otherwise. <paramref name="ground"/> is the ground at its origin (its 0.00 level).
        /// </summary>
        GhostTint Site(TerminalEnd end, Vector2 at, Vector2 facing, out float ground, out float relief)
        {
            var side = new Vector2(facing.y, -facing.x);
            ground = _ctx.HeightAt(at + facing * (float)end.OriginAlong);
            relief = 0;
            float lo = float.MaxValue, hi = float.MinValue;
            for (float a = -(float)end.Behind; a <= (float)end.Ahead + 1e-3f; a += 2)
                for (float w = -(float)end.HalfWidth; w <= (float)end.HalfWidth + 1e-3f; w += 2)
                {
                    var p = at + facing * a + side * w;
                    float h = _ctx.HeightAt(p);
                    if (!_ctx.InCore(p) || float.IsNaN(h)) return GhostTint.Bad;
                    lo = Mathf.Min(lo, h);
                    hi = Mathf.Max(hi, h);
                }
            if (float.IsNaN(ground)) return GhostTint.Bad;
            relief = hi - lo;
            return relief > SitingReliefMetres ? GhostTint.Raised : GhostTint.Ok;
        }

        // The window's line about the terminal in hand: its level (LT19) and the ground under it, rebuilt only when either
        // changes by a foot (no string a frame).
        void Levelling(string which, float level, int handFeet, float relief, float run = 0, float rise = 0)
        {
            int key = Mathf.RoundToInt(level / 0.3048f) * 4096 + handFeet * 64 + Mathf.Min(63, Mathf.RoundToInt(relief / 0.3048f / 5));
            key = key * 31 + ToolFormat.LengthKey(run) * 17 + ToolFormat.LengthKey(rise);
            if (key == _levelKey) return;
            _levelKey = key;
            string hand = handFeet == 0 ? "" : $" ({(handFeet > 0 ? "+" : "−")}{System.Math.Abs(handFeet)} ft)";
            string place = which == "Bottom"
                ? "The bottom terminal follows the pointer, green where it can stand. Click to place it: the dot is its bullwheel."
                : $"Click far up the mountain to place the top terminal. {ToolFormat.Length(run)} across · rise {ToolFormat.Length(rise)} · {ToolFormat.Grade(rise, run)}";
            _ctx.Window.SetHint(place + "\n" + which + " terminal level " + ToolFormat.Length(level) + hand);
            bool steep = relief > SitingReliefMetres;
            _ctx.Window.SetMessage(steep ? $"The ground under it varies {ToolFormat.Length(relief)}: its pad will be levelled with cut and fill{(relief > MaxSitingFeet * 0.3048f ? " (over 40 ft)" : "")}" : null, true);
        }

        int _levelKey = int.MinValue;

        // The ring round a terminal while there's no model to show it (LT19: the model itself, when the lift models are there).
        void Mark(int slot, Vector2 at, PreviewInk ink)
        {
            if (_ctx.BottomGhost != null) _ctx.Preview.Hide(slot);
            else _ctx.Preview.Ring(slot, at, TerminalRadius, ink);
        }

        void Draw(Vector2 pointer, bool hasPointer)
        {
            var p = _ctx.Preview;
            var ink = _problem == null ? PreviewInk.Ok : PreviewInk.Bad;
            switch (_step)
            {
                case Step.First:
                    if (hasPointer) { Mark(0, pointer, ink); p.Dot(1, pointer, ink); }
                    else p.HideFrom(0);
                    p.HideFrom(2);
                    break;
                case Step.Other:
                    Mark(0, _bottomXz, PreviewInk.Line);
                    p.Dot(1, _bottomXz, PreviewInk.Line);
                    if (hasPointer)
                    {
                        _line[0] = _bottomXz;
                        _line[1] = pointer;
                        p.Line(2, _line, 2, ink);
                        Mark(3, pointer, ink);
                        p.Dot(4, pointer, ink);
                    }
                    else p.HideFrom(2);
                    break;
                default:
                    _line[0] = _bottomXz;
                    _line[1] = _topXz;
                    Mark(0, _bottomXz, PreviewInk.Line);
                    p.Dot(1, _bottomXz, PreviewInk.Line);
                    p.Line(2, _line, 2, PreviewInk.Line);
                    Mark(3, _topXz, PreviewInk.Line);
                    p.Dot(4, _topXz, PreviewInk.Line);
                    break;
            }
        }

        /// <summary>
        /// Task 09: the lift built by the one build path (TW-12's towers, TW-16 v3's ends, the pads, trench and clearing, LF-12,
        /// GR-11 v2, CL-03/CL-09) on the edited ground, off the main thread; the review fills in when it's done. Towers by hand
        /// come with the lift menu's port (PR B); until then both modes place them automatically.
        /// </summary>
        // What the build was made from: a build for anything else is stale (a type, stance, mode or tower changed).
        string _builtFor;
        string BuildKey()
        {
            var k = new System.Text.StringBuilder().Append(_catalogId).Append('|').Append(_stance).Append('|').Append(_mode)
                .Append('|').Append(_bottomLevelFt).Append('|').Append(_topLevelFt);
            if (_mode == TowerMode.ByHand) foreach (var t in _placed) k.Append('|').Append(t.AlongMm).Append('/').Append(t.HeightMm);
            return k.ToString();
        }

        bool BuiltIsCurrent() => _built != null && _builtFor == BuildKey() && (_builtRebuild == null || _builtRevision == _ctx.History.Revision);

        // The fold's earthwork for Break ground when the area has one (accepted once the lift is in), else the build's own.
        DesignEarthwork.Result _builtRebuild;
        long _builtRevision = -1;
        Domain.Edits.TerrainEdits BuiltEdits => _builtRebuild != null ? _builtRebuild.Edits : _built?.Edits;

        // The profile strip (LF-10) from a solved rope over its ground, the terminals at their levels.
        LiftProfile ShowProfile(LiftRope rope, Domain.Edits.IHeightSource ground, LiftLevels levels, double openFrom = double.NaN)
        {
            if (rope == null || ground == null || _ctx.ToGround == null) return null;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            var profile = LiftProfile.From(rope, ground, b.X, b.Y, t.X, t.Y, levels, openFrom);
            _ctx.Window.ShowStrip(profile);
            return profile;
        }

        void TowersPlacedHint()
        {
            if (_built != null) _ctx.Window.SetHint($"{_built.Towers.Count} towers placed automatically (TW-12). Enter for the review.");
        }

        void StartBuild()
        {
            if (BuiltIsCurrent())
            {
                if (_step == Step.Review) { _ctx.PendingTerrain = BuiltEdits; BuildReview(); _ctx.Window.ShowReview(_review); }
                return;
            }
            _built = null;
            _ctx.PendingTerrain = null;
            var ground = _ctx.EditedGround?.Invoke();
            if (ground == null || _ctx.ToGround == null)
            {
                _building = false;
                _ctx.Window.SetMessage("Getting the ground ready: open Review again in a moment");
                return;
            }
            _building = true;
            var b = _ctx.ToGround(_bottom);
            var t = _ctx.ToGround(_top);
            var type = LiftType.Of(_catalogId);
            var stance = _stance;
            double bottomHand = _bottomLevelFt * 0.3048, topHand = _topLevelFt * 0.3048;   // LT19: the levels set by hand
            string key = BuildKey();
            // By hand, exactly the towers placed (LF-04); Automatic, TW-12's search.
            List<TowerSpec> hand = null;
            if (_mode == TowerMode.ByHand && _placed.Count > 0)
            {
                hand = new List<TowerSpec>(_placed.Count);
                foreach (var tw in _placed) hand.Add(tw.Spec);
            }
            var fold = _ctx.Earthwork != null && _ctx.Earthwork.Ready ? _ctx.Earthwork.Earthwork : null;
            var doc = _ctx.History.Document;
            var committed = _ctx.History.Terrain;
            long revision = _ctx.History.Revision;
            string name = _name, catalogId = _catalogId;
            var mode = _mode;
            var bottom = _bottom;
            var top = _top;
            _ctx.Analyse(() =>
            {
                // Finding D2: the lift as placed first, so the review shows at once; where it isn't within the rules, LT34's
                // search for the levels that would fit follows as a second step (SuggestLevels).
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = hand != null
                    ? LiftBuild.BuildWith(type, ground, b.X, b.Y, t.X, t.Y, hand, stance, bottomHand, topHand)
                    : LiftBuild.Build(type, ground, b.X, b.Y, t.X, t.Y, stance, null, bottomHand, topHand, suggest: false);
                long buildMs = watch.ElapsedMilliseconds;
                // Break ground's whole earthwork from the design's fold (trails, then lifts, TL3), as the trail tools make theirs.
                DesignEarthwork.Result rebuilt = null;
                if (fold != null)
                {
                    var after = new AddLift(revision, name, catalogId, bottom, top, mode, stance, r.Towers, WorkStage.Built, r.Levels).Apply(doc);
                    if (after.Accepted) rebuilt = fold.Rebuild(after.Document, committed);
                }
                Debug.Log($"[NewLift] {r.Towers.Count} towers placed in {buildMs} ms, the design's earthwork in {watch.ElapsedMilliseconds - buildMs} ms" +
                          (r.LevelsPending ? "; the levels to suggest follow" : ""));
                return (r, rebuilt);
            }, done =>
            {
                var result = done.r;
                _building = false;
                if (_step != Step.Review && _step != Step.Towers) return;
                if (result.LevelsPending) SuggestLevels(type, ground, b, t, stance, bottomHand, topHand, key);
                _built = result;
                _builtRebuild = done.rebuilt;
                _builtRevision = revision;
                _builtFor = key;
                ShowProfile(result.Rope, new Domain.Edits.EditedHeights(ground.Source, ground.Lattice, result.Edits), result.Levels);
                ShowDraft();   // F4: the line as Break ground would build it
                SyncEditor();
                _editorKey = long.MinValue;
                if (_step == Step.Towers) { if (_mode == TowerMode.Automatic) TowersPlacedHint(); return; }
                _ctx.PendingTerrain = BuiltEdits;
                BuildReview();
                _ctx.Window.ShowReview(_review);
                ReviewButtons();
            });
        }

        bool _suggesting;

        /// <summary>
        /// Finding D2: the build again with LT34's search, for a line that isn't within the rules: the same towers, levels and
        /// earthwork (the search starts from the lift as placed and comes back to it), with the hints to put it right.
        /// </summary>
        void SuggestLevels(LiftType type, Domain.Edits.EditedHeights ground, (double X, double Y) b, (double X, double Y) t, TowerStance stance,
                           double bottomHand, double topHand, string key)
        {
            _suggesting = true;
            _ctx.Analyse(() =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = LiftBuild.Build(type, ground, b.X, b.Y, t.X, t.Y, stance, null, bottomHand, topHand);
                Debug.Log($"[NewLift] the levels to suggest in {watch.ElapsedMilliseconds} ms ({r.Hints.Count} hints)");
                return r;
            }, result =>
            {
                _suggesting = false;
                if (_builtFor != key || _built == null || !_built.LevelsPending) return;
                _built = result;
                SyncEditor();
                if (_step == Step.Towers) { if (_mode == TowerMode.Automatic) TowersPlacedHint(); return; }
                if (_step != Step.Review) return;
                BuildReview();
                _ctx.Window.ShowReview(_review);
                ReviewButtons();
            });
        }

        void BuildReview()
        {
            float run = Vector2.Distance(_bottomXz, _topXz), rise = _topZ - _bottomZ;
            double slope = Mathf.Sqrt(run * run + rise * rise);
            _review.Clear()
                .Add("Length", $"{ToolFormat.Length(slope)} slope · {ToolFormat.Length(run)} across", "LF-06")
                .Add("Rise", $"{ToolFormat.Length(rise)} · {ToolFormat.Grade(rise, run)}")
                .Add("Ride", $"{ToolFormat.Duration(Type.RideSeconds(slope))} at {Type.SpeedMetresPerSecond * 196.850394:#,0} ft/min",
                     Type.Detachable ? "LC-04: slope length over the rope speed, plus the ramps out of and into the stations (LT41)" : "LC-04: slope length over the rope speed")
                .Add("Capacity", Type.Detachable ? $"{Type.Capacity:#,0} an hour · a chair every {Type.IntervalSeconds:0.#} s" : $"{Type.Capacity:#,0} an hour",
                     Type.Detachable ? "LC-03 v2: seats × 3,600 ÷ the loading interval (LT41)" : "LC-03")
                .Reserved("Network", "NW-12: No way down", ReviewModel.NetworkSlot);
            // NW-04: the lift as if built, in the network of everything else (task P3-10); a first lift alone has no way down.
            NetworkCheck.Fill(_review, NetworkCheck.Warnings(_ctx.History.Document,
                new AddLift(_ctx.History.Revision, _name, _catalogId, _bottom, _top, _mode, _stance, null, WorkStage.Built), 0, _ctx.Earthwork?.Earthwork, _ctx.Earthwork?.Heights?.Source));
            if (_built == null)
            {
                _review.Add("Towers", _building ? "placing…" : "—", "TW-12").Ready(_ctx.EditedGround == null, true);
                return;
            }
            var rope = _built.Rope;
            _review.Add("Towers", $"{_built.Towers.Count} towers", "TW-02, TW-12")
                .Add("Lowest chairs", $"{ToolFormat.Length(rope.LowestClearance)} over the ground", "TW-01: 15 ft at least")
                .Add("Rise allowed", $"{ToolFormat.Length(rope.RiseMetres)} of {(double.IsInfinity(rope.Type.RiseLimitMetres) ? "no limit" : ToolFormat.Length(rope.Type.RiseLimitMetres))}", "LC-07");
            foreach (var p in _built.Problems) _review.Warn(p.Message, p.Rule, p.Blocks);
            if (_built.LevelsPending) _review.Add("Levels", _suggesting ? "working out what would fit…" : "—", "LT34");
            _review.Ready(_built.Ok && !_built.LevelsPending, true);
        }

        public ToolBackOut Escape()
        {
            switch (_step)
            {
                case Step.Review:
                    if (_editor != null && _editor.Escape()) { _editorKey = long.MinValue; return ToolBackOut.Stepped; }
                    Go(Step.Towers);
                    return ToolBackOut.Stepped;
                case Step.Towers:
                    if (_editor != null && _editor.Escape()) { _hintKey2 = _editorKey = long.MinValue; return ToolBackOut.Stepped; }
                    ClearTowers();
                    Go(Step.Other);
                    return ToolBackOut.Stepped;
                case Step.Other: Go(Step.First); return ToolBackOut.Stepped;
                default: return ToolBackOut.Close;
            }
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (_step != Step.Review) return null;
            // Break ground carries the built lift's towers and levels (TW-15, SK14); a plan carries the line only (CF-06).
            // Without the game's ground (tests, labs: no EditedGround hook) it breaks ground on the line alone, as P3-06 did.
            if (stage == WorkStage.Built && _built == null && _ctx.EditedGround != null) return null;
            if (_built == null) return new AddLift(baseRevision, _name, _catalogId, _bottom, _top, _mode, _stance, null, stage);
            return stage == WorkStage.Built
                ? new AddLift(baseRevision, _name, _catalogId, _bottom, _top, _mode, _stance, _built.Towers, stage, _built.Levels)
                : new AddLift(baseRevision, _name, _catalogId, _bottom, _top, _mode, _stance, null, stage);
        }

        public void Committed(CommandResult result)
        {
            _built = null;
            _ctx.BottomGhost?.Hide();   // the built lift takes their place
            _ctx.TopGhost?.Hide();
            _turnDeg = 0;
            _bottomLevelFt = _topLevelFt = 0;
            ClearTowers();
            if (_builtRebuild != null && _ctx.History.Terrain == _builtRebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_builtRebuild);
            _builtRebuild = null;
            _builtFor = null;
            _ctx.Notice?.Invoke(_name + (result.Document.FindLift(new LiftId(result.Created[0]))?.Stage == WorkStage.Plan ? ": saved as a plan" : ": ground broken"));
            _name = NextName(result.Document);
            _ctx.Window.SetName(_name);
            Go(Step.First);
        }

        public void Disarm()
        {
            _editor?.Release();
            _ctx?.ShowDraftLift?.Invoke(null);
            _ctx?.BottomGhost?.Hide();
            _ctx?.TopGhost?.Hide();
            _ctx = null;
        }

        // For tests and captures.
        public void ChooseTowers(TowerMode mode) => PickMode(mode == TowerMode.Automatic ? 0 : 1);
        public ToolDropdown CarrierMenu => _typeMenu.Carrier;
        public int TowerFeet => Mathf.RoundToInt(_handMm / 304.8f);
        public int CurrentStep => (int)_step;
        public string Problem => _problem;
        public LiftBuild.Result Built => _built;
        public float TurnDegrees => _turnDeg;
        public int BottomLevelFeet => _bottomLevelFt;
        public int TopLevelFeet => _topLevelFt;
        public bool Building => _building;
        public string CatalogId => _catalogId;
        public PointMm Bottom => _bottom;
        public string LiftName => _name;
    }
}
