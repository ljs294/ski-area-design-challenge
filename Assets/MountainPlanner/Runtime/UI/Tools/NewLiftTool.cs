using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Lifts › New (LF-01 to LF-03, LF-08; P3-06's part; task 09's plop, LT19–LT22). The bottom terminal's own
    /// model follows the pointer, see-through and green where it can stand, amber where it will stand higher on its pier,
    /// red where it can't; Shift + wheel or , and . turn it (a preview of the space it takes, LT20) and Page Up / Page Down
    /// set its level. A click anchors it; the top terminal then follows the pointer facing back along the line, with its
    /// own level, and a second click fixes the line. Step 3, Towers,
    /// holds the Automatic or By hand switch (LF-01) and the tower in hand's height (Page Up / Page Down, LF-09); placing
    /// towers along the line is task 09's. Break ground submits <see cref="AddLift"/> (CF-05, CF-06).
    /// </summary>
    public sealed class NewLiftTool : IDesignTool
    {
        public const string ToolId = "new-lift";

        const float TerminalRadius = 8f;        // the ghost's footprint around the bullwheel, metres
        const float SitingReliefMetres = 3.048f;   // LF-08: the ground varies by no more than 10 ft under a terminal
        const float MinApartMetres = 91.44f;       // LF-08: the terminals stand at least 300 ft apart
        static readonly string[] Steps = { "First terminal", "Other terminal", "Towers", "Review" };
        // The type menu (owner 2026-10-19, LT13; the approved mockup): the kind, then its carrier, then its maker. Only the
        // fixed-grip quad has its own models (Sessellift FGQ-4, Monta FG4); the others are drawn with the quad's until theirs
        // exist. Carrier ids are LiftCatalogIds.Carriers'; "fixed-quad" stands for the two quad models.
        static readonly string[] Kinds = { "Fixed-grip chair", "Detachable chair", "Detachable gondola", "Aerial tram" };
        static readonly string[][] KindCarriers =
        {
            new[] { "fixed-double", "fixed-triple", "fixed-quad" },
            new[] { "detach-quad", "detach-six", "detach-eight" },
            new[] { "gondola-6", "gondola-8", "gondola-10" },
            new[] { "tram-60", "tram-100" },
        };
        static readonly string[][] CarrierNames =
        {
            new[] { "Double", "Triple", "Quad" },
            new[] { "Quad", "Six", "Eight" },
            new[] { "6 a cabin", "8 a cabin", "10 a cabin" },
            new[] { "60 a cabin", "100 a cabin" },
        };
        static readonly int[] KindDefault = { 2, 0, 1, 1 };   // the quad, a detachable quad, 8 a cabin, 100 a cabin
        static readonly string[] QuadModels = { "Sessellift FGQ-4", "Monta FG4", "Chairworks (comes later)" };
        static readonly string[] QuadIds = { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.MontaFg4 };
        static readonly bool[] QuadOff = { false, false, true };
        static readonly string[] MakerNames = { "Sessellift", "Monta", "Chairworks" };

        // The switch remembers the last choice for the session (LF-01: Automatic the first time).
        static TowerMode _lastMode = TowerMode.Automatic;

        enum Step { First, Other, Towers, Review }

        ToolContext _ctx;
        Step _step;
        PointMm _bottom, _top;
        Vector2 _bottomXz, _topXz;
        float _bottomZ, _topZ;
        string _catalogId = LiftCatalogIds.SesselliftFgq4;
        TowerMode _mode;
        TowerStance _stance = TowerStance.Plumb;
        int _towerFeet = 35;
        string _name;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _line = new Vector2[2];
        ToolDropdown _kindDd, _carrierDd, _modelDd, _modeDd, _stanceDd;
        UnityEngine.UIElements.Label _note;
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
            _kindDd = w.AddDropdown("kind", "Type", PickKind, "The kind of lift (owner, 2026-10-19)");
            _carrierDd = w.AddDropdown("carrier", "Chair", PickCarrier, "The carrier size: smaller carriers climb further (LT14)");
            _modelDd = w.AddDropdown("model", "Model", PickModel, "LC-02: its maker; a type without its own models is drawn with the quad's");
            (_modeDd, _stanceDd) = w.AddDropdownPair("towers", "Towers", "mode", PickMode, "stance", PickStance,
                "LF-01, TW-12: automatic towers, or yours; it can change until you break ground", "TW-14: plumb, or leaned square to the rope (15° at most)");
            _modeDd.Set(new[] { "Automatic", "By hand" }, _mode == TowerMode.Automatic ? 0 : 1);
            _stanceDd.Set(new[] { "Plumb", "Leaned" }, _stance == TowerStance.Plumb ? 0 : 1);
            _note = w.AddNote("capacity", "", "LC-02, LC-03: capacity is shown, not chosen");
            ShowType();
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

        // ---------- the type menu ----------

        static int KindOf(string id)
        {
            string carrier = LiftCatalogIds.Carrier(id);
            for (int k = 0; k < KindCarriers.Length; k++)
                if (System.Array.IndexOf(KindCarriers[k], carrier) >= 0) return k;
            return 0;
        }

        static string IdFor(string carrier, string maker) =>
            carrier == "fixed-quad" ? (maker == "monta" ? LiftCatalogIds.MontaFg4 : LiftCatalogIds.SesselliftFgq4) : LiftCatalogIds.Of(carrier, maker);

        void PickKind(int k)
        {
            if (k == KindOf(_catalogId)) return;
            Choose(IdFor(KindCarriers[k][KindDefault[k]], "sessellift"));
        }

        void PickCarrier(int c)
        {
            int k = KindOf(_catalogId);
            Choose(IdFor(KindCarriers[k][c], LiftCatalogIds.Maker(_catalogId)));
        }

        void PickModel(int m)
        {
            if (LiftCatalogIds.Carrier(_catalogId) == "fixed-quad") { if (m < QuadIds.Length) Choose(QuadIds[m]); }
            else Choose(LiftCatalogIds.Of(LiftCatalogIds.Carrier(_catalogId), LiftCatalogIds.Makers[m]));
        }

        /// <summary>Picks a lift type by its catalog id (the menu, tests and captures).</summary>
        public void Choose(string catalogId)
        {
            if (!LiftCatalogIds.Known(catalogId) || catalogId == _catalogId) return;
            _catalogId = catalogId;
            ShowType();
            Retype();
        }

        // The menu shows the type, and the note its capacity, speed and spacing.
        void ShowType()
        {
            int k = KindOf(_catalogId);
            string carrier = LiftCatalogIds.Carrier(_catalogId);
            _kindDd.Set(Kinds, k);
            _carrierDd.Set(CarrierNames[k], System.Array.IndexOf(KindCarriers[k], carrier));
            _ctx.Window.SetRowLabel("carrier", k >= 2 ? "Cabin" : "Chair");
            bool quad = carrier == "fixed-quad";
            if (quad) _modelDd.Set(QuadModels, System.Array.IndexOf(QuadIds, _catalogId), QuadOff);
            else
            {
                int m = System.Array.IndexOf(LiftCatalogIds.Makers, LiftCatalogIds.Maker(_catalogId));
                _modelDd.Set(MakerNames, m, null, MakerNames[m] + " " + Type.Name.ToLowerInvariant());
            }
            var t = Type;
            string spacing = k == 3 ? "two cabins" : $"{(k == 2 ? "cabins" : "chairs")} {ToolFormat.Length(t.ChairSpacingMetres)} apart";
            _note.text = $"{t.Capacity:#,0} an hour · {t.SpeedMetresPerSecond * 196.850394:#,0} ft/min · {spacing}"
                         + (quad ? "" : "\ndrawn with the quad's model until its own exists");
        }

        // A new type or stance while the line is fixed: the review works it out again.
        void Retype()
        {
            if (_step == Step.Review) Go(Step.Review);
        }

        static string NextName(DesignDocument doc) => "Chair " + (doc.Lifts.Count + 1);

        void Go(Step step)
        {
            _step = step;
            var w = _ctx.Window;
            w.SetStep((int)step);
            w.SetMessage(null);
            _hintKey = int.MinValue;
            bool towersInHand = step == Step.Towers && _mode == TowerMode.ByHand;
            _ctx.Claim(GameAction.TowerUp, towersInHand);
            _ctx.Claim(GameAction.TowerDown, towersInHand);
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
                    break;
                case Step.Other:
                    w.SetHint("Click far up the mountain to place the top terminal. That fixes the line.");
                    w.SetKeys((GameAction.TerminalLevelUp, "higher"), (GameAction.TerminalLevelDown, "lower"));
                    w.SetNext(false, false);
                    break;
                case Step.Towers:
                    w.SetHint(_mode == TowerMode.Automatic
                        ? "The towers go in automatically along the line (they come with the lift task)."
                        : $"Tower in hand: {ToolFormat.Feet(_towerFeet)}. Placing it along the line comes with the lift task.");
                    if (towersInHand) w.SetKeys((GameAction.TowerUp, "taller"), (GameAction.TowerDown, "shorter"), (GameAction.ToolNext, "review"));
                    else w.SetKeys((GameAction.ToolNext, "review"));
                    w.SetNext(true, true);
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    break;
                case Step.Review:
                    w.SetHint(null);
                    w.SetKeys();   // Break ground shows its own key
                    w.SetNext(false, false);
                    StartBuild();
                    BuildReview();
                    w.ShowReview(_review);
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
                    if (_mode == TowerMode.ByHand && (input.Pressed(ToolKeys.TowerUp) || input.Pressed(ToolKeys.TowerDown)))
                    {
                        _towerFeet = Mathf.Clamp(_towerFeet + (input.Pressed(ToolKeys.TowerUp) ? 5 : -5), 25, 100);
                        Go(Step.Towers);
                    }
                    if (input.Pressed(ToolKeys.Next)) Go(Step.Review);
                    break;
                case Step.Review:
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
        /// outside the area; amber where the ground under it varies more than 10 ft, so it stands higher on its pier
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
            _ctx.Window.SetMessage(steep ? $"The ground under it varies {ToolFormat.Length(relief)}: it stands higher on its pier{(relief > MaxSitingFeet * 0.3048f ? " (over 40 ft)" : "")}" : null, true);
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
        void StartBuild()
        {
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
            _ctx.Analyse(() => LiftBuild.Build(type, ground, b.X, b.Y, t.X, t.Y, stance, null, bottomHand, topHand), result =>
            {
                _building = false;
                if (_step != Step.Review) return;
                _built = result;
                _ctx.PendingTerrain = result.Edits;
                BuildReview();
                _ctx.Window.ShowReview(_review);
            });
        }

        void BuildReview()
        {
            float run = Vector2.Distance(_bottomXz, _topXz), rise = _topZ - _bottomZ;
            double slope = Mathf.Sqrt(run * run + rise * rise);
            _review.Clear()
                .Add("Length", $"{ToolFormat.Length(slope)} slope · {ToolFormat.Length(run)} across", "LF-06")
                .Add("Rise", $"{ToolFormat.Length(rise)} · {ToolFormat.Grade(rise, run)}")
                .Add("Ride", $"{ToolFormat.Duration(slope / Type.SpeedMetresPerSecond)} at {Type.SpeedMetresPerSecond * 196.850394:#,0} ft/min", "LC-04: slope length over the rope speed")
                .Add("Capacity", $"{Type.Capacity:#,0} an hour", "LC-03")
                .Reserved("Network", "NW-12: No way down (task 10)", ReviewModel.NetworkSlot);
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
            _review.Ready(_built.Ok, true);
        }

        public ToolBackOut Escape()
        {
            switch (_step)
            {
                case Step.Review: Go(Step.Towers); return ToolBackOut.Stepped;
                case Step.Towers: Go(Step.Other); return ToolBackOut.Stepped;
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
            _ctx.Notice?.Invoke(_name + (result.Document.FindLift(new LiftId(result.Created[0]))?.Stage == WorkStage.Plan ? ": saved as a plan" : ": ground broken"));
            _name = NextName(result.Document);
            _ctx.Window.SetName(_name);
            Go(Step.First);
        }

        public void Disarm()
        {
            _ctx?.BottomGhost?.Hide();
            _ctx?.TopGhost?.Hide();
            _ctx = null;
        }

        // For tests and captures.
        internal void ChooseTowers(TowerMode mode) => PickMode(mode == TowerMode.Automatic ? 0 : 1);
        public int TowerFeet => _towerFeet;
        public int CurrentStep => (int)_step;
        public string Problem => _problem;
        public LiftBuild.Result Built => _built;
        public float TurnDegrees => _turnDeg;
        public int BottomLevelFeet => _bottomLevelFt;
        public int TopLevelFeet => _topLevelFt;
        public bool Building => _building;
        public string CatalogId => _catalogId;
    }
}
