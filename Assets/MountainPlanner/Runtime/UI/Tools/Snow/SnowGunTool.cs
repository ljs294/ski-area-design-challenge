using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.15's Snow gun tool (G; SG-01…04, SM-02; the approved mockup's snowgun and snowlasso), three ways:
    /// - **Place.** Each click puts a gun of the chosen model (SLE stick 10, 20 or 30 ft, or the ground fan gun) and hooks
    ///   its 50 ft hose to the nearest free hydrant: a dashed line to it as you aim, or a red ring when none is in reach (it
    ///   is built unhooked, and the Check tool lists it).
    /// - **Fill hydrants.** Click a water line: a gun beside every free hydrant on it, aimed across it.
    /// - **Select.** Drag a lasso round guns, then set them Auto, On or Off together (one undo step).
    /// New guns are set On by default: until 4.16's schedules an Auto gun waits, and an On gun shows its pressure now.
    /// </summary>
    public sealed class SnowGunTool : IDesignTool
    {
        public const string ToolId = "gun";
        const int RingSlot = 0, HoseSlot = 1, LassoSlot = 2, PickSlots = 10, MaxPicks = 400;
        const int FillOffsetMm = 3_048;   // 10 ft off the line
        static readonly string[] Models = { "10 ft", "20 ft", "30 ft", "Fan" };
        static readonly string[] Modes = { "Place", "Fill hydrants", "Select", "Hydrant" };
        static readonly string[] NewModes = { "On", "Auto" };

        ToolContext _ctx;
        GunModel _model = GunModel.Stick20;
        int _mode;
        GunOverride _newOverride = GunOverride.On;
        Action<int> _setModel, _setMode, _setNew;
        readonly List<Vector2> _lasso = new List<Vector2>();
        readonly List<SnowId> _picked = new List<SnowId>();
        Vector2[] _xz = new Vector2[256];
        readonly Vector2[] _two = new Vector2[2];

        public string Id => ToolId;
        public string Name => "Snow gun";
        public bool HasWork => false;   // each click is its own command

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            w.SetName(null);
            w.SetSteps(new[] { "Place guns" });
            w.SetStep(0);
            w.SetPlanShown(false);
            w.SetCommit(false, false);
            w.SetNext(false, false);
            foreach (var o in new[] { "model", "mode", "new" }) w.RemoveOption(o);
            _setModel = w.AddSwitch("model", "Model", Models, (int)_model, i => { _model = (GunModel)i; Card(); },
                "SLE stick guns throw farther the taller they stand (10, 20, 30 ft); the fan gun sits on the ground");
            _setMode = w.AddSwitch("mode", "Do", Modes, _mode, SetMode);
            _setNew = w.AddSwitch("new", "New guns", NewModes, 0, i => { _newOverride = i == 0 ? GunOverride.On : GunOverride.Auto; Card(); },
                "SM-02: On runs it whenever it's cold enough; Auto waits for the snowmaking schedule (task 4.16)");
            SetMode(_mode);
        }

        void SetMode(int mode)
        {
            _mode = mode;
            _setMode?.Invoke(mode);
            _lasso.Clear();
            _picked.Clear();
            var w = _ctx.Window;
            w.ShowOption("model", mode < 2);
            w.ShowOption("new", mode < 2);
            w.SetSteps(new[] { mode == 0 ? "Place guns" : mode == 1 ? "Fill a line" : mode == 2 ? "Select guns" : "Add hydrants" });
            w.SetStep(0);
            w.SetHint(mode == 0 ? "Click beside a hydrant: its 50 ft hose hooks to the nearest free one (a red ring: none in reach; Do: Hydrant adds one)."
                    : mode == 1 ? "Click a water line: a gun beside every free hydrant on it."
                    : mode == 2 ? "Drag round guns to select them, then set them Auto, On or Off."
                    : "Click a water line to put a hydrant there: anywhere along it, 10 ft from its ends and other hydrants.");
            w.SetMessage(null);
            w.SetKeys();
            w.SetButtons();
            _ctx.Preview.Clear();
            Card();
        }

        public void Tick(in ToolInput input)
        {
            var p = _ctx.Preview;
            if (_mode == 2) { LassoTick(input); return; }
            if (!input.HasGround || input.OverPanel) { p.Hide(RingSlot); p.Hide(HoseSlot); return; }
            var net = _ctx.History.Document.Snow;
            if (_mode == 3) { HydrantTick(input, net); return; }
            if (_mode == 0)
            {
                var h = NearestFreeHydrant(net, input.Point);
                if (h != null)
                {
                    _two[0] = SnowSnap.Xz(input.Point); _two[1] = SnowSnap.Xz(h.At);
                    p.Line(HoseSlot, _two, 2, PreviewInk.Snap);
                    p.Ring(RingSlot, SnowSnap.Xz(h.At), 3, PreviewInk.Ok);
                }
                else { p.Hide(HoseSlot); p.Ring(RingSlot, SnowSnap.Xz(input.Point), 4.5f, PreviewInk.Bad); }
                if (!input.Click) return;
                if (!input.InCore) { _ctx.Window.SetMessage("Keep guns inside the core.", true); return; }
                int aim = h != null ? AimAcross(net, h, input.Point) : 0;
                Submit(new AddSnowGuns(_ctx.History.Revision, _model, new[] { input.Point }, new[] { aim }, _newOverride),
                       h != null ? "Gun hooked to " + SnowSnap.Label(net, h) : "Gun placed with no hydrant in reach");
                return;
            }
            // Fill hydrants.
            var pipe = SnowSnap.Pipe(net, input.Point, _ctx.SnapReachMm);
            if (!pipe.Any) { p.Ring(RingSlot, SnowSnap.Xz(input.Point), 4, PreviewInk.Bad); return; }
            p.Ring(RingSlot, SnowSnap.Xz(pipe.At), 4, PreviewInk.Snap);
            if (!input.Click) return;
            var points = new List<PointMm>();
            var aims = new List<int>();
            AddSnowGuns.FillPoints(net, pipe.Id, FillOffsetMm, points, aims);
            if (points.Count == 0) { _ctx.Window.SetMessage("Every hydrant on " + pipe.Label + " has a gun (or it has none).", true); return; }
            Submit(new AddSnowGuns(_ctx.History.Revision, _model, points, aims, _newOverride), SnowSnap.Count(points.Count, "gun") + " on " + pipe.Label);
        }

        // Do: Hydrant (owner 2026-11-19: hydrants wherever the player likes): one on the line under the pointer, one undo step.
        void HydrantTick(in ToolInput input, SnowNetwork net)
        {
            var p = _ctx.Preview;
            p.Hide(HoseSlot);
            var hit = SnowSnap.Pipe(net, input.Point, _ctx.SnapReachMm);
            if (!hit.Any) { p.Ring(RingSlot, SnowSnap.Xz(input.Point), 4, PreviewInk.Bad); return; }
            var pipe = net.FindPipe(hit.Id);
            string problem = SnowSnap.TapProblem(net, pipe, SnowGeometry.Project(pipe.Route, hit.At).ChainageMm);
            p.Ring(RingSlot, SnowSnap.Xz(hit.At), 3, problem == null ? PreviewInk.Ok : PreviewInk.Bad);
            if (!input.Click) return;
            if (problem != null) { _ctx.Window.SetMessage(problem, true); return; }
            Submit(new AddSnowTap(_ctx.History.Revision, SnowNodeKind.Hydrant, pipe.Id, hit.At), "Hydrant added to " + SnowSnap.Name(pipe));
        }

        void Submit(DesignCommand cmd, string done)
        {
            var r = _ctx.Submit?.Invoke(cmd, null);
            if (r != null && !r.Accepted) { _ctx.Window.SetMessage(r.Reason, true); return; }
            _ctx.Window.SetMessage(null);
            _ctx.Notice?.Invoke(done);
            Card();
        }

        static SnowNode NearestFreeHydrant(SnowNetwork net, PointMm at)
        {
            SnowNode best = null;
            long bestD = (long)SnowRules.HoseMm * SnowRules.HoseMm + 1;
            foreach (var n in net.Nodes)
            {
                if (n.Kind != SnowNodeKind.Hydrant || net.GunOn(n.Id) != null) continue;
                long d = n.At.DistanceSquared(at);
                if (d < bestD || d == bestD && best != null && n.Id.Value < best.Id.Value) { bestD = d; best = n; }
            }
            return best;
        }

        /// <summary>Aimed across the hydrant's line, away from it on the click's side (SG-03's aim).</summary>
        static int AimAcross(SnowNetwork net, SnowNode hydrant, PointMm at)
        {
            var host = net.FindPipe(hydrant.Host);
            double dx = at.X - hydrant.At.X, dy = at.Y - hydrant.At.Y;
            if (host != null && host.Route.Count >= 2)
            {
                var on = SnowGeometry.Project(host.Route, hydrant.At);
                var a = host.Route[on.Segment];
                var b = host.Route[Math.Min(on.Segment + 1, host.Route.Count - 1)];
                double ux = b.X - a.X, uy = b.Y - a.Y, len = Math.Sqrt(ux * ux + uy * uy);
                if (len > 0)
                {
                    double nx = -uy / len, ny = ux / len;
                    if (nx * dx + ny * dy < 0) { nx = -nx; ny = -ny; }
                    dx = nx; dy = ny;
                }
            }
            if (dx == 0 && dy == 0) return 0;
            double deg = Math.Atan2(dx, dy) * 180 / Math.PI;
            return ((int)Math.Round(deg * 100) % 36000 + 36000) % 36000;
        }

        // ---------- the lasso ----------

        void LassoTick(in ToolInput input)
        {
            var p = _ctx.Preview;
            if (input.Click && input.HasGround && !input.OverPanel) { _lasso.Clear(); _picked.Clear(); _ctx.Window.SetButtons(); DrawPicks(); }
            if (input.Drag && input.HasGround)
            {
                var at = SnowSnap.Xz(input.Point);
                if (_lasso.Count == 0 || (_lasso[_lasso.Count - 1] - at).sqrMagnitude > 4f) _lasso.Add(at);
                if (_xz.Length < _lasso.Count + 1) _xz = new Vector2[_lasso.Count * 2];
                for (int i = 0; i < _lasso.Count; i++) _xz[i] = _lasso[i];
                if (_lasso.Count >= 2) p.Line(LassoSlot, _xz, _lasso.Count, PreviewInk.Snap, true);
            }
            if (input.Release && _lasso.Count >= 3)
            {
                _picked.Clear();
                foreach (var g in _ctx.History.Document.Snow.Guns)
                    if (Inside(_lasso, SnowSnap.Xz(g.At))) _picked.Add(g.Id);
                _lasso.Clear();
                p.Hide(LassoSlot);
                DrawPicks();
                var w = _ctx.Window;
                if (_picked.Count == 0) { w.SetMessage("No guns inside.", true); w.SetButtons(); Card(); return; }
                w.SetMessage(null);
                Card();
                w.SetButtons(("auto", "Set Auto", () => SetPicked(GunOverride.Auto)), ("on", "On", () => SetPicked(GunOverride.On)),
                             ("off", "Off", () => SetPicked(GunOverride.Off)), ("clear", "Clear", () => SetMode(2)));
                _ctx.ShowSnow?.Invoke(_picked, null);
            }
        }

        void SetPicked(GunOverride over)
        {
            var still = new List<SnowId>();
            foreach (var id in _picked) if (_ctx.History.Document.Snow.FindGun(id) != null) still.Add(id);
            if (still.Count == 0) return;
            Submit(new SetGunOverride(_ctx.History.Revision, still, over), SnowSnap.Count(still.Count, "gun") + " set " + over);
        }

        void DrawPicks()
        {
            var p = _ctx.Preview;
            var net = _ctx.History.Document.Snow;
            int n = 0;
            foreach (var id in _picked)
            {
                if (n >= MaxPicks) break;
                var g = net.FindGun(id);
                if (g != null) p.Ring(PickSlots + n++, SnowSnap.Xz(g.At), 3.5f, PreviewInk.Snap);
            }
            for (int i = PickSlots + n; i < PickSlots + MaxPicks; i++) p.Hide(i);
            if (_picked.Count == 0) _ctx.ShowSnow?.Invoke(Array.Empty<SnowId>(), null);
        }

        static bool Inside(List<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) inside = !inside;
            return inside;
        }

        // The panel: the model's throw and water, and the guns so far.
        void Card()
        {
            var spec = GunCatalog.Spec(_model);
            var net = _ctx.History.Document.Snow;
            int hooked = 0, on = 0;
            foreach (var g in net.Guns) { if (!g.Hydrant.IsNone) hooked++; if (g.Override == GunOverride.On) on++; }
            var r = new ReviewModel();
            if (_mode < 2) r.Add("Model", $"{spec.Name} · throws {ToolFormat.Feet((int)spec.ThrowFt)}", "SG-03");
            else if (_mode == 3)
            {
                int hydrants = 0, free = 0;
                foreach (var n in net.Nodes) if (n.Kind == SnowNodeKind.Hydrant) { hydrants++; if (net.GunOn(n.Id) == null) free++; }
                r.Add("Hydrants", hydrants == 0 ? "none yet" : $"{hydrants} · {free} free");
            }
            if (_mode == 2) r.Add("Selected", _picked.Count == 0 ? "none: drag round guns" : SnowSnap.Count(_picked.Count, "gun"));
            r.Add("Needs", $"{GunCatalog.MinPsi} psi at its hydrant · up to {GunCatalog.Stages[GunCatalog.Stages.Length - 1].WaterGpm} gpm", "SG-01, SG-02");
            r.Add("Guns", net.Guns.Count == 0 ? "none yet" : $"{net.Guns.Count} · {hooked} on hydrants · {on} on");
            r.Ready(false, false);
            _ctx.Window.ShowReview(r);
        }

        public ToolBackOut Escape()
        {
            if (_mode == 2 && _picked.Count > 0) { SetMode(2); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision) => null;   // each click submits
        public void Committed(CommandResult result) { }

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.ShowSnow?.Invoke(Array.Empty<SnowId>(), null);
            _ctx.Preview.Clear();
            _ctx = null;
        }

        // For tests and scripted captures.
        public void Choose(GunModel model, int mode) { _model = model; _setModel?.Invoke((int)model); SetMode(mode); }
        public IReadOnlyList<SnowId> Picked => _picked;
        public void Pick(IEnumerable<SnowId> guns)
        {
            _picked.Clear();
            _picked.AddRange(guns);
            DrawPicks();
            _ctx.ShowSnow?.Invoke(_picked, null);
            _ctx.Window.SetButtons(("auto", "Set Auto", () => SetPicked(GunOverride.Auto)), ("on", "On", () => SetPicked(GunOverride.On)),
                                   ("off", "Off", () => SetPicked(GunOverride.Off)), ("clear", "Clear", () => SetMode(2)));
            _ctx.Window.SetMessage(null);
            Card();
        }
    }
}
