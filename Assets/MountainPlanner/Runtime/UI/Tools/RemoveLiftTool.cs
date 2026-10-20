using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Toolbox › Lifts › Remove (task 09; the approved mockup, #92): the lift under the pointer lights up; a click selects it
    /// ("Click again to remove"), a second click (or Enter) takes it out with its pads, trench and clearing in one undoable
    /// command, and Esc keeps it. No money in Phase 3. A built lift's ground comes back by replaying the other built lifts
    /// from what they store (<see cref="LiftReplay"/>, LT24), worked out off the main thread while you decide. A planned
    /// lift has no ground: it's picked by its dashed line and goes on the second click at once.
    /// </summary>
    public sealed class RemoveLiftTool : IDesignTool, IPicksLifts
    {
        public const string ToolId = "remove-lift";
        const float PickPixels = 8;

        ToolContext _ctx;
        LiftId _armed;
        string _armedName;
        bool _armedBuilt;
        TerrainEdits _without;          // the ground without the armed lift, once worked out
        DesignEarthwork.Result _rebuild; // the same, as the design's fold made it (accepted once the removal is in)
        bool _working, _removeWhenReady;
        readonly Vector2[] _line = new Vector2[2];

        public string Id => ToolId;
        public string Name => "Remove lift";
        public bool HasWork => false;   // nothing drawn: Esc and ✕ never ask

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            w.SetName(null);
            Idle();
        }

        void Idle()
        {
            _armed = default;
            _armedName = null;
            _without = null;
            _rebuild = null;
            _working = _removeWhenReady = false;
            _ctx.HoldLift?.Invoke(0);
            _ctx.Preview.HideFrom(0);
            var w = _ctx.Window;
            w.SetHint("Point at a lift to light it, then click it. Its pads, trench and clearing go with it; Ctrl+Z puts it back.");
            w.SetMessage(null);
            w.SetKeys();
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.ShowReview(null);
        }

        public void Tick(in ToolInput input)
        {
            var doc = _ctx.History.Document;
            if (_armed.Value != 0 && doc.FindLift(_armed) == null) Idle();   // undone or removed meanwhile
            ulong under = _ctx.LiftUnderPointer?.Invoke() ?? 0;
            if (under == 0 && input.HasGround) under = PlanUnder(doc, input);
            DrawPlan(doc, under);
            if (_armed.Value != 0 && input.Pressed(ToolKeys.Next)) { Remove(); return; }
            if (!input.Click || input.OverPanel) return;
            if (under == 0) { if (_armed.Value != 0) Idle(); return; }
            if (under == _armed.Value) { Remove(); return; }
            Select(doc.FindLift(new LiftId(under)));
        }

        void Select(Lift lift)
        {
            if (lift == null) return;
            Idle();
            _armed = lift.Id;
            _armedName = lift.Name;
            _armedBuilt = lift.Stage == WorkStage.Built;
            if (_armedBuilt) _ctx.HoldLift?.Invoke(lift.Id.Value);
            var w = _ctx.Window;
            w.SetName(lift.Name);
            w.SetHint(_armedBuilt
                ? $"Click {lift.Name} again to remove it, with its pads, trench and clearing. Esc keeps it."
                : $"Click the plan {lift.Name} again to remove it. Esc keeps it.");
            w.SetKeys((GameAction.ToolNext, "remove"));
            if (_armedBuilt) WorkOutGround(lift.Id);
        }

        // The ground without the lift: every other built lift replayed from what it stores (LT24), off the main thread.
        void WorkOutGround(LiftId id)
        {
            var ground = _ctx.EditedGround?.Invoke();
            if (ground == null || _ctx.ToGround == null) { _without = null; return; }   // no game ground (tests): the ground stays
            _working = true;
            var doc = _ctx.History.Document;
            var committed = _ctx.History.Terrain;
            long revision = _ctx.History.Revision;
            var toGround = _ctx.ToGround;
            var fold = _ctx.Earthwork != null && _ctx.Earthwork.Ready ? _ctx.Earthwork.Earthwork : null;
            // The design's earthwork folded without it (trails and the other lifts kept exactly, TL3), or with no fold every
            // other built lift replayed (LT24).
            _ctx.Analyse(() =>
            {
                if (fold == null) return (LiftReplay.Rebuild(ground.Source, ground.Lattice, doc, p => toGround(p), id), (DesignEarthwork.Result)null);
                var r = fold.Rebuild(new DeleteLift(revision, id).Apply(doc).Document, committed);
                return (r.Edits, r);
            }, done =>
            {
                _working = false;
                if (_armed.Value != id.Value) return;
                _without = done.Item1;
                _rebuild = done.Item2;
                if (_removeWhenReady) Remove();
            });
        }

        void Remove()
        {
            if (_armed.Value == 0) return;
            if (_armedBuilt && _working)
            {
                _removeWhenReady = true;
                _ctx.Window.SetMessage("Working out the ground without it…", true);
                return;
            }
            var name = _armedName;
            var result = _ctx.Submit?.Invoke(new DeleteLift(_ctx.History.Revision, _armed), _armedBuilt ? _without : null);
            if (result == null || !result.Accepted) return;
            if (_rebuild != null && _ctx.History.Terrain == _rebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_rebuild);
            _rebuild = null;
            _ctx.Notice?.Invoke(name + " removed");
            _ctx.Window.SetName(null);
            Idle();
        }

        // A planned lift's dashed line within a few pixels of the pointer (built lifts pick through their models).
        ulong PlanUnder(DesignDocument doc, in ToolInput input)
        {
            float reach = Mathf.Max(5f, PickPixels * input.MetresPerPixel);
            var p = new Vector2(input.Ground.x, input.Ground.z);
            ulong best = 0;
            float bestD = reach;
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Plan) continue;
                float d = SegmentDistance(p, Xz(l.Bottom), Xz(l.Top));
                if (d < bestD) { bestD = d; best = l.Id.Value; }
            }
            return best;
        }

        // A plan under the pointer, or the armed plan, is drawn over its dashed line in white (the lit lift's colour, UI-15).
        void DrawPlan(DesignDocument doc, ulong under)
        {
            ulong show = !_armedBuilt && _armed.Value != 0 ? _armed.Value : under;
            var l = show != 0 ? doc.FindLift(new LiftId(show)) : null;
            if (l == null || l.Stage != WorkStage.Plan) { _ctx.Preview.HideFrom(0); return; }
            _line[0] = Xz(l.Bottom);
            _line[1] = Xz(l.Top);
            _ctx.Preview.Line(0, _line, 2, PreviewInk.Line);
            _ctx.Preview.Dot(1, _line[0], PreviewInk.Line);
            _ctx.Preview.Dot(2, _line[1], PreviewInk.Line);
        }

        static Vector2 Xz(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);

        static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0;
            return Vector2.Distance(p, a + ab * t);
        }

        public ToolBackOut Escape()
        {
            if (_armed.Value == 0) return ToolBackOut.Close;
            _ctx.Window.SetName(null);
            Idle();
            return ToolBackOut.Stepped;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision) => null;   // no Break ground: the second click removes
        public void Committed(CommandResult result) { }

        public void Disarm()
        {
            _ctx?.HoldLift?.Invoke(0);
            _ctx = null;
        }

        // For tests.
        public ulong Armed => _armed.Value;
        public bool Working => _working;
    }
}
