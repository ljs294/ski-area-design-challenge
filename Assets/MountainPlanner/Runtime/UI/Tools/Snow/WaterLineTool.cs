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
    /// Task 4.15's Water line tool (W; HY-01, HY-02; the approved mockup's snowline and snowlinereview):
    /// - **Route.** Click point by point; the first click starts on a pump house, a hydrant or junction, a pipe (a new
    ///   junction) or in a source's water (an intake). Landing on any of those ends the line (in a pond's water: an outlet
    ///   that fills it, WS-11); Enter ends it anywhere. Backspace takes the last point back; [ and ] change the size.
    /// - **Live, never wiped.** While you draw, the line solves as the game would with a gun on every hydrant it would get:
    ///   its length and climb, the pressure at its end, how many of those guns would be short and by how much, and the
    ///   water's speed (the "snappy calculations": a draft solve is a few milliseconds, run off the main thread).
    /// - **Review.** Name, size and the hydrant run: every N ft (100 by default) or by count, or none; spots within 10 ft of
    ///   a tap or the line's ends are skipped. Break ground lays it.
    /// </summary>
    public sealed class WaterLineTool : IDesignTool, IKeepsWork
    {
        public const string ToolId = "pipe";
        const int LineSlot = 0, FirstSlot = 1, EndSlot = 2, HydrantSlots = 10, MaxHydrantDots = 500;
        const int FootMm = 305;
        static readonly string[] HydrantModes = { "Every", "By count", "None" };

        enum Step { Route, Review }

        ToolContext _ctx;
        Step _step;
        readonly List<PointMm> _route = new List<PointMm>();
        SnowSnapHit _from, _to;
        int _diameterIndex = Array.IndexOf(SnowRules.Diameters, SnowRules.DefaultDiameterIn);
        int _spacingFt = 100, _count = 10, _hydrantMode;
        string _name;
        Action<int, string> _setDiameter, _setSpacing, _setCount;
        Action<int> _setMode;
        readonly ReviewModel _review = new ReviewModel();
        Vector2[] _xz = new Vector2[64];

        // The live solve.
        Readout _readout;
        bool _solving;
        string _solvedKey, _wantKey;
        PointMm _cursor;
        bool _hasCursor;

        /// <summary>One draft solve's answer.</summary>
        public sealed class Readout
        {
            public string Problem;
            public double LengthM, ClimbM;
            public double EndPsi = double.NaN;
            /// <summary>The hydrant farthest along the line ("H30"), whose pressure <see cref="EndPsi"/> is; null: the line's end.</summary>
            public string LastHydrant;
            public int Guns, Short;
            public double WorstShortPsi;
            public double MaxVelocityFtS, PeakGpm;
            public int Hydrants, Skipped;
            public bool NoGround;
        }

        public string Id => ToolId;
        public string Name => "Water line";
        public bool HasWork => _route.Count > 0;
        int Diameter => SnowRules.Diameters[_diameterIndex];

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            var w = context.Window;
            w.SetSteps(new[] { "Route", "Hydrants and review" });
            w.SetPlanShown(false);
            w.SetGoLabel("Break ground");
            foreach (var o in new[] { "dia", "hmode", "spacing", "count" }) w.RemoveOption(o);
            _setDiameter = w.AddSlider("dia", "Diameter", 0, SnowRules.Diameters.Length - 1, 1, _diameterIndex, i => SetDiameter(i),
                "HY-01: 4 to 24 in. A bigger pipe loses less to friction; [ and ] change it");
            _setMode = w.AddSwitch("hmode", "Hydrants", HydrantModes, _hydrantMode, i => { _hydrantMode = i; ShowHydrantRows(); Changed(); },
                "Hydrants along the line; a free (dead) end gets one too. A spot within 10 ft of another tap or a connected end is skipped. Add more anywhere with Snow gun › Hydrant");
            _setSpacing = w.AddSlider("spacing", "Every", 50, 500, 25, _spacingFt, v => { _spacingFt = v; _setSpacing(v, ToolFormat.Feet(v)); Changed(); });
            _setCount = w.AddSlider("count", "Count", 2, 100, 1, _count, v => { _count = v; _setCount(v, v.ToString()); Changed(); });
            _setSpacing(_spacingFt, ToolFormat.Feet(_spacingFt));
            _setCount(_count, _count.ToString());
            SetDiameter(_diameterIndex);
            Restart();
        }

        void Restart()
        {
            _route.Clear();
            _from = default;
            _to = default;
            _step = Step.Route;
            _readout = null;
            _solvedKey = null;
            _name = NextName();
            var w = _ctx.Window;
            w.SetName(_name);
            w.SetRenamable(Rename);
            w.SetStep(0);
            w.SetHint("Start on a pump house, a hydrant, a line or in a source's water, then click along the route. Landing on a line, a hydrant or a pond ends it; Enter ends it anywhere.");
            w.SetMessage(null);
            w.SetKeys((GameAction.ToolNext, "finish"), (GameAction.UndoStroke, "last point"), (GameAction.BrushNarrower, "smaller"), (GameAction.BrushWider, "bigger"));
            w.ShowOption("hmode", false);
            w.ShowOption("spacing", false);
            w.ShowOption("count", false);
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.ShowReview(null);
            Draw();
        }

        string NextName()
        {
            var net = _ctx.History.Document.Snow;
            for (int n = net.Pipes.Count + 1; ; n++)
            {
                string name = "Line " + n;
                bool taken = false;
                foreach (var p in net.Pipes) taken |= p.Name == name;
                if (!taken) return name;
            }
        }

        string Rename(string name)
        {
            if (DesignRules.NameProblem(name) is string p) return p;
            _name = name;
            _ctx.Window.SetName(name);
            return null;
        }

        void SetDiameter(int index)
        {
            _diameterIndex = Mathf.Clamp(index, 0, SnowRules.Diameters.Length - 1);
            _setDiameter?.Invoke(_diameterIndex, SnowSnap.Inches(Diameter));
            Changed();
        }

        void ShowHydrantRows()
        {
            var w = _ctx.Window;
            bool review = _step == Step.Review;
            _setMode?.Invoke(_hydrantMode);
            w.ShowOption("hmode", review);
            w.ShowOption("spacing", review && _hydrantMode == 0);
            w.ShowOption("count", review && _hydrantMode == 1);
        }

        void Changed()
        {
            _solvedKey = null;
            if (_ctx != null && _step == Step.Review) Review();
        }

        public void Tick(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.BrushWider)) SetDiameter(_diameterIndex + 1);
            if (input.Pressed(ToolKeys.BrushNarrower)) SetDiameter(_diameterIndex - 1);
            if (_step == Step.Route) RouteTick(input);
            else Draw();
            Solve();
        }

        void RouteTick(in ToolInput input)
        {
            if (input.Pressed(ToolKeys.UndoStroke) && _route.Count > 0)
            {
                _route.RemoveAt(_route.Count - 1);
                if (_route.Count == 0) _from = default;
                _solvedKey = null;
            }
            if (input.Pressed(ToolKeys.Next)) { Finish(SnowSnapHit.FreeAt(_route.Count > 0 ? _route[_route.Count - 1] : default)); return; }
            _hasCursor = input.HasGround && !input.OverPanel;
            var net = _ctx.History.Document.Snow;
            SnowSnapHit snap = _hasCursor ? SnowSnap.End(_ctx, net, input.Point, _ctx.SnapReachMm) : default;
            if (_hasCursor) _cursor = snap.Any ? snap.At : input.Point;
            Draw(snap);
            if (!input.Click || input.OverPanel || !input.HasGround) return;
            if (!input.InCore) { _ctx.Window.SetMessage("Keep the line inside the core.", true); return; }
            var at = snap.Any ? snap.At : input.Point;
            if (_route.Count == 0)
            {
                _from = snap.Any ? snap : SnowSnapHit.FreeAt(at);
                _route.Add(at);
                _ctx.Window.SetMessage(null);
                if (snap.Any) _ctx.Window.SetHint("From " + snap.Label + (snap.Kind == SnowSnapKind.Water ? " (an intake draws from it)" : "") + ". Click along the route; landing on a line, a hydrant or a pond ends it; Enter ends it anywhere.");
                _solvedKey = null;
                return;
            }
            if (_route[_route.Count - 1].Within(at, 1000)) return;   // a metre at least
            if (_route.Count >= SnowRules.MaxRoutePoints - 1) { Finish(SnowSnapHit.FreeAt(at)); return; }
            bool onStart = snap.Id != SnowId.None && snap.Id == _from.Id;
            if (snap.Any && !onStart) { _route.Add(at); Finish(snap); return; }
            _route.Add(at);
            _ctx.Window.SetMessage(null);
            _solvedKey = null;
        }

        void Finish(SnowSnapHit end)
        {
            if (_route.Count < 2) { _ctx.Window.SetMessage("Click at least two points.", true); return; }
            if (SnowGeometry.Length(_route) < 3000) { _ctx.Window.SetMessage("A line needs 10 ft at least.", true); return; }
            _to = end.Any ? end : SnowSnapHit.FreeAt(_route[_route.Count - 1]);
            _route[_route.Count - 1] = _to.At;
            _step = Step.Review;
            var w = _ctx.Window;
            w.SetStep(1);
            w.SetHint("Choose the hydrants along it. The figures assume a gun on every hydrant (Check network shows them all running).");
            w.SetKeys((GameAction.BrushNarrower, "smaller"), (GameAction.BrushWider, "bigger"));
            w.SetMessage(null);
            ShowHydrantRows();
            _solvedKey = null;
            Review();
        }

        static SnowEnd EndOf(SnowSnapHit h, bool first)
        {
            switch (h.Kind)
            {
                case SnowSnapKind.Node:
                case SnowSnapKind.House: return new SnowEnd(SnowEndKind.Node, h.At, h.Id);
                case SnowSnapKind.Pipe: return SnowEnd.OnPipe(h.Id, h.At);
                case SnowSnapKind.Water: return !first && h.Fillable ? SnowEnd.Outlet(h.At, h.Source) : SnowEnd.Intake(h.At, h.Source);
                default: return SnowEnd.Free(h.At);
            }
        }

        int SpacingMm => _step == Step.Route ? SnowRules.DefaultSpacingMm
                       : _hydrantMode == 0 ? _spacingFt * FootMm
                       : _hydrantMode == 1 ? (int)Math.Max(SnowRules.TapGapMm, Math.Floor(SnowGeometry.Length(_route) / Math.Max(1, _count - 1)))
                       : 0;

        /// <summary>The line as a command: the route (with the cursor while drawing), its ends and its hydrants.</summary>
        AddSnowPipe Command(long revision, bool withCursor)
        {
            var route = new List<PointMm>(_route);
            var to = _to;
            if (withCursor && _step == Step.Route)
            {
                if (_hasCursor && (route.Count == 0 || !route[route.Count - 1].Within(_cursor, 1000))) route.Add(_cursor);
                to = SnowSnapHit.FreeAt(route[route.Count - 1]);
            }
            return new AddSnowPipe(revision, _name, Diameter, route, EndOf(_from, true), EndOf(to, false), SpacingMm);
        }

        // ---------- the live solve ----------

        void Solve()
        {
            // A result dropped as stale (an edit landed meanwhile) never calls back: free the next solve.
            if (_solving && _ctx.Running == 0) _solving = false;
            if (_route.Count == 0) return;
            int n = _route.Count + (_step == Step.Route && _hasCursor ? 1 : 0);
            if (n < 2) return;
            string key = $"{_step}|{_route.Count}|{(_hasCursor ? _cursor.X / 1000 + "," + _cursor.Y / 1000 : "-")}|{Diameter}|{SpacingMm}|{_ctx.History.Revision}";
            _wantKey = key;
            if (_solving || key == _solvedKey) return;
            var service = _ctx.Snow?.Invoke();
            var doc = _ctx.History.Document;
            var cmd = Command(_ctx.History.Revision, true);
            _solving = true;
            _ctx.Analyse(() => Analyse(doc, cmd, service), r =>
            {
                _solving = false;
                _solvedKey = key;
                _readout = r;
                if (_step == Step.Review) Review(); else Live();
            });
        }

        /// <summary>The draft on the design, a gun on each of its hydrants, solved as the game would with every gun on (any thread).</summary>
        public static Readout Analyse(DesignDocument doc, AddSnowPipe cmd, SnowService service)
        {
            var r = new Readout();
            var after = cmd.Apply(doc);
            r.LengthM = SnowGeometry.Length(cmd.Route) / 1000.0;
            if (!after.Accepted) { r.Problem = after.Reason; return r; }
            var net = after.Document.Snow;
            SnowPipe pipe = null;
            foreach (var p in net.Pipes) if (doc.Snow.FindPipe(p.Id) == null) pipe = p;
            if (pipe == null) { r.Problem = "Nothing to lay."; return r; }
            var hydrants = net.HydrantsOn(pipe.Id);
            r.Hydrants = hydrants.Count;
            if (cmd.SpacingMm > 0)
            {
                var run = SnowGeometry.RunBySpacing(0, SnowGeometry.Length(pipe.Route), cmd.SpacingMm);
                int taps = hydrants.FindAll(h => h.Host == pipe.Id).Count;
                r.Skipped = Math.Max(0, run.Count - 2 - taps);   // the run's two ends sit on the line's own ends
            }
            // Owner 2026-11-19: pipe past the last hydrant is a dead end: the readout is the last hydrant's, not the end's.
            double lastC = SnowGeometry.Length(pipe.Route);
            if (hydrants.Count > 0)
            {
                lastC = -1;
                foreach (var h in hydrants)
                {
                    double c = SnowGeometry.Project(pipe.Route, h.At).ChainageMm;
                    if (c > lastC) { lastC = c; r.LastHydrant = SnowSnap.Label(net, h); }
                }
            }
            if (service == null) { r.NoGround = true; return r; }
            if (service.Ground.TryHeightMm(pipe.Route[0], out int z0) && service.Ground.TryHeightMm(pipe.Route[pipe.Route.Count - 1], out int z1))
                r.ClimbM = (z1 - z0) / 1000.0;
            // A gun beside every free hydrant on the new line: what it can feed.
            var points = new List<PointMm>();
            var aims = new List<int>();
            AddSnowGuns.FillPoints(net, pipe.Id, 3_048, points, aims);
            var withGuns = after.Document;
            if (points.Count > 0)
            {
                var g2 = new AddSnowGuns(0, GunModel.Stick20, points, aims).Apply(after.Document);
                if (g2.Accepted) withGuns = g2.Document;
            }
            var sol = service.Solve(withGuns.Snow, everyGun: true);
            var psi = new float[1];
            SnowProfile.PressureAlong(sol, pipe, service.Ground, new[] { lastC }, psi);
            r.EndPsi = psi[0];
            foreach (var s in SnowProfile.Stretches(sol, pipe))
            {
                r.MaxVelocityFtS = Math.Max(r.MaxVelocityFtS, s.VelocityFtS);
                r.PeakGpm = Math.Max(r.PeakGpm, Math.Abs(s.Gpm));
            }
            var mine = new HashSet<SnowId>();
            foreach (var t in withGuns.Snow.HydrantsOn(pipe.Id)) mine.Add(t.Id);
            r.WorstShortPsi = 0;
            foreach (var gun in withGuns.Snow.Guns)
            {
                if (!mine.Contains(gun.Hydrant)) continue;
                r.Guns++;
                int i = sol.GunIndex(gun.Id);
                if (i < 0) continue;
                var st = sol.GunStates[i];
                if (st == GunState.ShortOfPressure || st == GunState.NoWater)
                {
                    r.Short++;
                    float p = sol.GunPsi[i];
                    double gap = float.IsNaN(p) ? GunCatalog.MinPsi : GunCatalog.MinPsi - Math.Max(0, (double)p);
                    r.WorstShortPsi = Math.Max(r.WorstShortPsi, gap);
                }
            }
            if (!sol.Solved) r.Problem = "The network doesn't solve: check the problems (Check, K).";
            return r;
        }

        // While drawing: the readouts at the cursor in the window (the mockup's live panel).
        void Live()
        {
            var r = _readout;
            _review.Clear();
            if (r == null) { _ctx.Window.ShowReview(null); return; }
            Figures(r);
            _review.Ready(false, false);
            _ctx.Window.ShowReview(_review);
        }

        void Figures(Readout r)
        {
            _review.Add("Length", ToolFormat.Length(r.LengthM));
            if (r.NoGround) { _review.Add("Pressure", "reading the ground…"); return; }
            _review.Add("Vertical", (r.ClimbM >= 0 ? "+" : "−") + ToolFormat.Length(Math.Abs(r.ClimbM)), "HY-03: the pipe follows the ground");
            if (r.LastHydrant != null) _review.Add("Last hydrant", $"{r.LastHydrant} · {SnowSnap.Psi(r.EndPsi)}", "Psi at grade at the hydrant farthest along, with every gun on the line running; pipe past it is a dead end");
            else _review.Add("At the end", SnowSnap.Psi(r.EndPsi), "Psi at grade where the line ends, with every gun on it running");
            if (r.Guns > 0)
                _review.Add("Guns on it", r.Short == 0 ? $"all {r.Guns} have 200 psi" : $"{r.Short} of {r.Guns} short, up to {Math.Round(r.WorstShortPsi):0} psi", "SG-02: a gun needs 200 psi at its hydrant");
            if (r.PeakGpm > 0)
                _review.Add("Velocity", $"{SnowSnap.Speed(r.MaxVelocityFtS)} at {ToolFormat.Gpm(r.PeakGpm)}", "HY-09 14: over 15 ft/s wears the pipe (water hammer)");
            if (r.MaxVelocityFtS > SnowSolve.VelocityWarnFtS) _review.Warn($"The water runs {SnowSnap.Speed(r.MaxVelocityFtS)}: a bigger pipe ( ] ) slows it.", "HY-09 14");
            if (r.Short > 0) _review.Warn($"{r.Short} guns here would be short of pressure: a bigger pipe, a booster (Inline pump, I) or a stronger pump house.", "HY-09 12");
        }

        void Review()
        {
            var r = _readout;
            _review.Clear();
            _review.Add("Diameter", SnowSnap.Inches(Diameter));
            _review.Add("From", Describe(_from, true));
            _review.Add("To", Describe(_to, false));
            if (_hydrantMode != 2 && r != null) _review.Add("Hydrants", r.Skipped > 0 ? $"{r.Hydrants} · {r.Skipped} spots taken, skipped" : r.Hydrants.ToString());
            if (r != null) Figures(r);
            else _review.Add("Pressure", "solving…");
            if (r?.Problem != null) _review.Warn(r.Problem, null, true);
            bool ok = r != null && r.Problem == null;
            _review.Ready(ok, false);
            _ctx.Window.ShowReview(_review);
            _ctx.Window.SetCommit(ok, false);
        }

        static string Describe(SnowSnapHit h, bool first)
        {
            switch (h.Kind)
            {
                case SnowSnapKind.House: return h.Label;
                case SnowSnapKind.Node: return h.Label;
                case SnowSnapKind.Pipe: return "a new junction on " + h.Label;
                case SnowSnapKind.Water: return (!first && h.Fillable ? "fills " : "an intake in ") + h.Label;
                default: return "a dead end";
            }
        }

        // ---------- drawing ----------

        void Draw(SnowSnapHit snap = default)
        {
            var p = _ctx.Preview;
            int n = _route.Count + (_step == Step.Route && _hasCursor && _route.Count > 0 ? 1 : 0);
            if (_xz.Length < n + 1) _xz = new Vector2[n * 2];
            for (int i = 0; i < _route.Count; i++) _xz[i] = SnowSnap.Xz(_route[i]);
            if (n > _route.Count) _xz[_route.Count] = SnowSnap.Xz(_cursor);
            bool fast = _readout != null && _readout.MaxVelocityFtS > SnowSolve.VelocityWarnFtS;
            if (n >= 2) p.Line(LineSlot, _xz, n, _readout?.Problem != null || fast ? PreviewInk.Bad : PreviewInk.Line); else p.Hide(LineSlot);
            if (_route.Count > 0) p.Dot(FirstSlot, _xz[0], PreviewInk.Snap); else p.Hide(FirstSlot);
            if (snap.Any) p.Ring(EndSlot, SnowSnap.Xz(snap.At), 4f, PreviewInk.Snap);
            else if (_step == Step.Review && _to.Any) p.Dot(EndSlot, SnowSnap.Xz(_to.At), PreviewInk.Snap);
            else p.Hide(EndSlot);
            // The hydrant spots along the line in review.
            int dots = 0;
            if (_step == Step.Review && SpacingMm > 0)
            {
                var run = SnowGeometry.RunBySpacing(0, SnowGeometry.Length(_route), SpacingMm);
                for (int i = 1; i + 1 < run.Count && dots < MaxHydrantDots; i++)
                    p.Dot(HydrantSlots + dots++, SnowSnap.Xz(SnowGeometry.At(_route, run[i])), PreviewInk.Ok);
            }
            for (int i = HydrantSlots + dots; i < HydrantSlots + MaxHydrantDots; i++) p.Hide(i);
        }

        // ---------- the tool contract ----------

        public ToolBackOut Escape()
        {
            if (_step == Step.Review)
            {
                _step = Step.Route;
                _to = default;
                _route.RemoveAt(_route.Count - 1);
                var w = _ctx.Window;
                w.SetStep(0);
                w.ShowReview(null);
                w.SetCommit(false, false);
                ShowHydrantRows();
                _solvedKey = null;
                return ToolBackOut.Stepped;
            }
            if (_route.Count > 0) { Restart(); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision)
        {
            if (stage != WorkStage.Built || _step != Step.Review || _readout?.Problem != null) return null;
            return Command(baseRevision, false);
        }

        public void Committed(CommandResult result)
        {
            _ctx.Notice?.Invoke(_name + " laid");
            Restart();
        }

        public void AskBeforeLeaving(Action then) => _ctx.Window.SetAskRow("Leave without laying this line?", GameAction.ToolNext,
            ("leave", "Leave", () => { _route.Clear(); then(); }), ("stay", "Stay", () => _ctx.Window.SetAskRow(null, GameAction.ToolNext)));

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.Preview.Clear();
            _ctx = null;
        }

        // For tests and scripted captures.
        public int Points => _route.Count;
        public bool Reviewing => _step == Step.Review;
        public Readout Last => _readout;
        public bool Solving => _solving;
        public int DiameterIn => Diameter;
        public void SetHydrants(int mode, int spacingFt = 100, int count = 10)
        {
            _hydrantMode = mode; _spacingFt = spacingFt; _count = count;
            _setSpacing?.Invoke(_spacingFt, ToolFormat.Feet(_spacingFt));
            _setCount?.Invoke(_count, _count.ToString());
            ShowHydrantRows();
            Changed();
        }
    }
}
