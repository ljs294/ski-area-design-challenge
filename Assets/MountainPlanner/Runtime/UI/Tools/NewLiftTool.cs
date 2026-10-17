using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Lifts › New (LF-01 to LF-03, LF-08; P3-06's part). The bottom terminal follows the pointer, green
    /// where it can sit; a click anchors it; a second click places the top terminal and fixes the line. Step 3, Towers,
    /// holds the Automatic or By hand switch (LF-01) and the tower in hand's height (Page Up / Page Down, LF-09); placing
    /// towers along the line is task 09's. Break ground submits <see cref="AddLift"/> (CF-05, CF-06).
    /// </summary>
    public sealed class NewLiftTool : IDesignTool
    {
        public const string ToolId = "new-lift";

        // Task 09 moves these into the lift catalog (LC-02, LC-03): both fixed-grip quads share them for now.
        const double RopeFpm = 450, CapacityPerHour = 2400;
        const float TerminalRadius = 8f;        // the ghost's footprint around the bullwheel, metres
        const float SitingReliefMetres = 3.048f;   // LF-08: the ground varies by no more than 10 ft under a terminal
        const float MinApartMetres = 91.44f;       // LF-08: the terminals stand at least 300 ft apart
        static readonly string[] Steps = { "First terminal", "Other terminal", "Towers", "Review" };
        static readonly string[] Types = { "Sessellift FGQ-4", "Monta FG4" };
        static readonly string[] TypeIds = { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.MontaFg4 };

        // The switch remembers the last choice for the session (LF-01: Automatic the first time).
        static TowerMode _lastMode = TowerMode.Automatic;

        enum Step { First, Other, Towers, Review }

        ToolContext _ctx;
        Step _step;
        PointMm _bottom, _top;
        Vector2 _bottomXz, _topXz;
        float _bottomZ, _topZ;
        int _type;
        TowerMode _mode;
        TowerStance _stance = TowerStance.Plumb;
        int _towerFeet = 35;
        string _name;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _line = new Vector2[2];
        System.Action<int> _setType, _setMode, _setStance;
        int _hintKey = int.MinValue;
        string _problem;

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
            _setType = w.AddSwitch("type", "Type", Types, _type, i => { _type = i; _setType(i); }, "LF-01, LC-02: both fixed-grip quads share these values for now");
            _setMode = w.AddSwitch("towers", "Towers", new[] { "Automatic", "By hand" }, _mode == TowerMode.Automatic ? 0 : 1, PickMode,
                "LF-01, TW-12: automatic towers, or yours; it can change until you break ground");
            _setStance = w.AddSwitch("stand", "Stand", new[] { "Plumb", "Leaned" }, 0, i => { _stance = i == 0 ? TowerStance.Plumb : TowerStance.Leaned; _setStance(i); },
                "TW-14: plumb, or leaned square to the rope");
            w.AddNote("capacity", $"{CapacityPerHour:#,0} an hour · {RopeFpm:0} ft/min", "LC-02, LC-03: capacity is shown, not chosen");
            w.SetSteps(Steps);
            Go(Step.First);
        }

        void PickMode(int i)
        {
            _mode = _lastMode = i == 0 ? TowerMode.Automatic : TowerMode.ByHand;
            _setMode(i);
            if (_step == Step.Towers) Go(Step.Towers);
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
            switch (step)
            {
                case Step.First:
                    w.SetHint("The bottom terminal follows the pointer, green where it can sit. Click to anchor it: the dot is its bullwheel.");
                    w.SetKeys();
                    w.SetNext(false, false);
                    w.ShowReview(null);
                    w.SetCommit(false, false);
                    break;
                case Step.Other:
                    w.SetHint("Click far up the mountain to place the top terminal. That fixes the line.");
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
                    w.SetKeys((GameAction.BreakGround, "break ground"));
                    w.SetNext(false, false);
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
                    if (!input.HasGround) { Draw(default, false); return; }
                    var at = new Vector2(input.Ground.x, input.Ground.z);
                    _problem = Siting(at);
                    Draw(at, true);
                    _ctx.Window.SetMessage(_problem);
                    if (input.Click && _problem == null)
                    {
                        _bottom = input.Point;
                        _bottomXz = at;
                        _bottomZ = input.Ground.y;
                        Go(Step.Other);
                    }
                    break;
                }
                case Step.Other:
                {
                    if (!input.HasGround) { Draw(default, false); return; }
                    var at = new Vector2(input.Ground.x, input.Ground.z);
                    float z = input.Ground.y;
                    _problem = Siting(at)
                        ?? (z <= _bottomZ ? "The top terminal must be higher" : null)
                        ?? (Vector2.Distance(at, _bottomXz) < MinApartMetres ? "The terminals stand at least 300 ft apart" : null);
                    Draw(at, true);
                    _ctx.Window.SetMessage(_problem);
                    Aiming(at, z);
                    if (input.Click && _problem == null)
                    {
                        _top = input.Point;
                        _topXz = at;
                        _topZ = z;
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

        // The hint's figures while aiming, rebuilt only when the rounded length changes (no string a frame).
        void Aiming(Vector2 at, float z)
        {
            float run = Vector2.Distance(at, _bottomXz);
            int key = ToolFormat.LengthKey(run) * 31 + ToolFormat.LengthKey(z - _bottomZ);
            if (key == _hintKey) return;
            _hintKey = key;
            _ctx.Window.SetHint($"Click far up the mountain to place the top terminal. {ToolFormat.Length(run)} across · rise {ToolFormat.Length(z - _bottomZ)} · {ToolFormat.Grade(z - _bottomZ, run)}");
        }

        /// <summary>LF-08 (P3-06's part): inside the core box, on the ground, the ground under it within 10 ft. Lakes and roads are task 09's.</summary>
        string Siting(Vector2 at)
        {
            if (!_ctx.InCore(at + new Vector2(TerminalRadius, TerminalRadius)) || !_ctx.InCore(at - new Vector2(TerminalRadius, TerminalRadius)))
                return "A terminal must stand inside the area";
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i <= 8; i++)
            {
                var p = i == 8 ? at : at + TerminalRadius * new Vector2(Mathf.Cos(i * Mathf.PI / 4), Mathf.Sin(i * Mathf.PI / 4));
                float h = _ctx.HeightAt(p);
                if (float.IsNaN(h)) return "A terminal must stand inside the area";
                lo = Mathf.Min(lo, h);
                hi = Mathf.Max(hi, h);
            }
            return hi - lo > SitingReliefMetres ? "Too steep here: the ground under a terminal varies by 10 ft at most" : null;
        }

        void Draw(Vector2 pointer, bool hasPointer)
        {
            var p = _ctx.Preview;
            var ink = _problem == null ? PreviewInk.Ok : PreviewInk.Bad;
            switch (_step)
            {
                case Step.First:
                    if (hasPointer) { p.Ring(0, pointer, TerminalRadius, ink); p.Dot(1, pointer, ink); }
                    else p.HideFrom(0);
                    p.HideFrom(2);
                    break;
                case Step.Other:
                    p.Ring(0, _bottomXz, TerminalRadius, PreviewInk.Line);
                    p.Dot(1, _bottomXz, PreviewInk.Line);
                    if (hasPointer)
                    {
                        _line[0] = _bottomXz;
                        _line[1] = pointer;
                        p.Line(2, _line, 2, ink);
                        p.Ring(3, pointer, TerminalRadius, ink);
                        p.Dot(4, pointer, ink);
                    }
                    else p.HideFrom(2);
                    break;
                default:
                    _line[0] = _bottomXz;
                    _line[1] = _topXz;
                    p.Ring(0, _bottomXz, TerminalRadius, PreviewInk.Line);
                    p.Dot(1, _bottomXz, PreviewInk.Line);
                    p.Line(2, _line, 2, PreviewInk.Line);
                    p.Ring(3, _topXz, TerminalRadius, PreviewInk.Line);
                    p.Dot(4, _topXz, PreviewInk.Line);
                    break;
            }
        }

        void BuildReview()
        {
            float run = Vector2.Distance(_bottomXz, _topXz), rise = _topZ - _bottomZ;
            double slope = Mathf.Sqrt(run * run + rise * rise);
            _review.Clear()
                .Add("Length", $"{ToolFormat.Length(slope)} slope · {ToolFormat.Length(run)} across", "LF-06")
                .Add("Rise", $"{ToolFormat.Length(rise)} · {ToolFormat.Grade(rise, run)}")
                .Add("Ride", $"{ToolFormat.Duration(slope / FpmToMetresPerSecond(RopeFpm))} at {RopeFpm:0} ft/min", "LC-04: slope length over 450 ft/min")
                .Add("Capacity", $"{CapacityPerHour:#,0} an hour", "LC-03")
                .Reserved("Towers", "TW-02, TW-12: the lift task places them", "towers")
                .Reserved("Lowest chairs", "TW-01: 15 ft at least", "clearance")
                .Reserved("Tension", "RP-06", "tension")
                .Reserved("Network", "NW-12: No way down (task 10)", ReviewModel.NetworkSlot)
                .Ready(true, true);
        }

        // ft/min to metres a second.
        static double FpmToMetresPerSecond(double fpm) => fpm * 0.3048 / 60;

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
            return new AddLift(baseRevision, _name, TypeIds[_type], _bottom, _top, _mode, _stance, null, stage);
        }

        public void Committed(CommandResult result)
        {
            _ctx.Notice?.Invoke(_name + (result.Document.FindLift(new LiftId(result.Created[0]))?.Stage == WorkStage.Plan ? ": saved as a plan" : ": ground broken"));
            _name = NextName(result.Document);
            _ctx.Window.SetName(_name);
            Go(Step.First);
        }

        public void Disarm()
        {
            _ctx = null;
        }

        // For tests and captures.
        internal void ChooseTowers(TowerMode mode) => PickMode(mode == TowerMode.Automatic ? 0 : 1);
        public int TowerFeet => _towerFeet;
        public int CurrentStep => (int)_step;
        public string Problem => _problem;
    }
}
