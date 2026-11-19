using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.15's Inline pump tool (I; HY-05 v2; the approved mockup's snowpump): click a water line to put a 250 hp booster
    /// on it, adding 300 psi. It pushes away from the nearest pump house by default (<see cref="SnowRoutes.AwayFromSource"/>),
    /// shown as an arrow; Enter flips it before you click. Each click is its own command (one undo step).
    /// </summary>
    public sealed class InlinePumpTool : IDesignTool
    {
        public const string ToolId = "inline-pump";
        const int RingSlot = 0, ArrowSlot = 1;

        ToolContext _ctx;
        bool _flip;
        SnowSnapHit _hover;
        bool _forward;
        readonly Vector2[] _arrow = new Vector2[5];

        public string Id => ToolId;
        public string Name => "Inline pump";
        public bool HasWork => false;   // each pump is its own command

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            w.SetName(null);
            w.SetSteps(new[] { "Place" });
            w.SetStep(0);
            w.SetPlanShown(false);
            w.SetCommit(false, false);
            w.SetNext(false, false);
            w.SetHint("Click a water line to put a booster on it: 250 hp, adding 300 psi. It pushes away from the pump house; the arrow shows which way.");
            w.SetKeys((GameAction.ToolNext, "flip"));
            w.SetMessage(null);
            Card();
        }

        public void Tick(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.Next)) { _flip = !_flip; Card(); }
            var p = _ctx.Preview;
            if (!input.HasGround || input.OverPanel) { p.Hide(RingSlot); p.Hide(ArrowSlot); return; }
            var net = _ctx.History.Document.Snow;
            _hover = SnowSnap.Pipe(net, input.Point, _ctx.SnapReachMm);
            if (!_hover.Any) { p.Ring(RingSlot, SnowSnap.Xz(input.Point), 4, PreviewInk.Bad); p.Hide(ArrowSlot); return; }
            var pipe = net.FindPipe(_hover.Id);
            var on = SnowGeometry.Project(pipe.Route, _hover.At);
            string problem = SnowSnap.TapProblem(net, pipe, on.ChainageMm);
            _forward = SnowRoutes.AwayFromSource(net, pipe.Id, on.ChainageMm) ^ _flip;
            p.Ring(RingSlot, SnowSnap.Xz(_hover.At), 4, problem == null ? PreviewInk.Ok : PreviewInk.Bad);
            DrawArrow(pipe, on);
            if (!input.Click) return;
            if (problem != null) { _ctx.Window.SetMessage(problem, true); return; }
            var r = _ctx.Submit?.Invoke(new AddSnowTap(_ctx.History.Revision, SnowNodeKind.Pump, pipe.Id, _hover.At, _forward, PumpSpec.Booster), null);
            if (r != null && !r.Accepted) { _ctx.Window.SetMessage(r.Reason, true); return; }
            _ctx.Window.SetMessage(null);
            _ctx.Notice?.Invoke("Booster placed on " + SnowSnap.Name(pipe));
            _flip = false;
            Card();
        }

        void DrawArrow(SnowPipe pipe, SnowGeometry.OnRoute on)
        {
            var a = pipe.Route[on.Segment];
            var b = pipe.Route[Math.Min(on.Segment + 1, pipe.Route.Count - 1)];
            var d = new Vector2(b.X - a.X, b.Y - a.Y).normalized * (_forward ? 1 : -1);
            var s = new Vector2(d.y, -d.x);
            var c = SnowSnap.Xz(on.Point);
            float len = 14f;   // metres: readable from the usual building height
            _arrow[0] = c - d * len;
            _arrow[1] = c + d * len;
            _arrow[2] = c + d * len * 0.45f + s * len * 0.4f;
            _arrow[3] = c + d * len;
            _arrow[4] = c + d * len * 0.45f - s * len * 0.4f;
            _ctx.Preview.Line(ArrowSlot, _arrow, 5, PreviewInk.Snap);
        }

        void Card()
        {
            var spec = PumpSpec.Booster;
            var r = new ReviewModel();
            r.Add("Pump", $"{spec.Hp} hp at {spec.EfficiencyPermille / 10}% · adds {spec.BoostPsi} psi", "HY-05 v2: it holds its boost up to its power; set it on its card");
            r.Add("Direction", _flip ? "towards the pump house (flipped)" : "away from the pump house", "Enter flips it");
            r.Add("Check valve", "water passes one way only, also when it's off", "plan Q2");
            r.Ready(false, false);
            _ctx.Window.ShowReview(r);
        }

        public ToolBackOut Escape() => ToolBackOut.Close;
        public DesignCommand Commit(WorkStage stage, long baseRevision) => null;   // each click submits
        public void Committed(CommandResult result) { }

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.Preview.Clear();
            _ctx = null;
        }

        public bool Forward => _forward;
    }
}
