using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.15's Check tool (K; HY-09; the approved mockup's snowdiag and snowcheck): the network's numbered problems
    /// (1–20), each with Show me (the camera flies there and lights it) and a one-click fix where one exists: Flip pump (7),
    /// Upsize pipe (12, 13 over, 14), Open valve (19). A switch reads the guns as they're set or every gun running at its
    /// wet bulb's stage (the "what if"). While a newer edit is solving, the old answer stays, greyed, with "Updating…".
    /// </summary>
    public sealed class CheckNetworkTool : IDesignTool
    {
        public const string ToolId = "check";
        const int MaxRows = 40;
        static readonly string[] Reads = { "As they're set", "Every gun running" };

        ToolContext _ctx;
        bool _everyGun;
        Action<int> _setReads;
        SnowSolution _shown;
        bool _shownPending;
        long _shownRevision = -1;

        public string Id => ToolId;
        public string Name => "Check";
        public bool HasWork => false;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            w.SetName(null);
            w.SetSteps(new[] { "Check" });
            w.SetStep(0);
            w.SetPlanShown(false);
            w.SetCommit(false, false);
            w.SetNext(false, false);
            w.SetKeys();
            w.RemoveOption("reads");
            _setReads = w.AddSwitch("reads", "Guns", Reads, _everyGun ? 1 : 0, i => { _everyGun = i == 1; _ctx.SetSnowCheck?.Invoke(_everyGun); Refresh(true); },
                "Every gun running: each gun on at its wet bulb's stage, to see whether the network could feed them all");
            _ctx.SetSnowCheck?.Invoke(_everyGun);
            w.SetHint("The network's problems, numbered as in the rules. Show me flies there; a fix button makes the change (one undo step).");
            _shown = null;
            Refresh(true);
        }

        SnowSolution Current() => _everyGun ? _ctx.LiveSnowCheck?.Invoke() : _ctx.LiveSnow?.Invoke();

        public void Tick(in ToolInput input) => Refresh(false);

        void Refresh(bool force)
        {
            var sol = Current();
            bool pending = _ctx.SnowPending?.Invoke() ?? false;
            long rev = _ctx.History.Revision;
            if (!force && ReferenceEquals(sol, _shown) && pending == _shownPending && rev == _shownRevision) return;
            _shown = sol;
            _shownPending = pending;
            _shownRevision = rev;
            Card(sol, pending);
        }

        void Card(SnowSolution sol, bool pending)
        {
            var w = _ctx.Window;
            var net = _ctx.History.Document.Snow;
            var r = new ReviewModel();
            if (net.Pipes.Count == 0 && net.Houses.Count == 0)
            {
                r.Add("Network", "nothing built yet: start with a pump house (P)");
                r.Ready(false, false);
                w.ShowReview(r);
                w.SetMessage(null);
                FillProblems(null);
                return;
            }
            if (sol == null)
            {
                r.Add("Network", "solving…");
                r.Ready(false, false);
                w.ShowReview(r);
                w.SetMessage(null);
                FillProblems(null);
                return;
            }
            int running = 0, warm = 0, shortP = 0, shortA = 0, dry = 0;
            foreach (var s in sol.GunStates)
                switch (s)
                {
                    case GunState.Running: running++; break;
                    case GunState.RunningWarm: warm++; break;
                    case GunState.ShortOfPressure: shortP++; break;
                    case GunState.ShortOfAir: shortA++; break;
                    case GunState.NoWater: dry++; break;
                }
            int guns = sol.GunStates.Length;
            if (pending) r.Add("Result", "updating…", "The game solves an edit a moment after it; the figures below are the last answer");
            r.Add("Running", guns == 0 ? "no guns yet" : $"{running + warm} of {guns}" + (shortP + shortA + dry > 0 ? $" · {shortP} short of pressure, {shortA} of air, {dry} dry" : ""),
                sol.WhatIf ? "every gun forced on at its stage" : "the guns as they're set");
            r.Add("Water", ToolFormat.Gpm(sol.DemandGpm), "HY-08: what the guns draw");
            int air = 0, cfm = 0;
            foreach (var a in sol.AirDrawn) air += a;
            foreach (var h in net.Houses) cfm += h.CompressorCfm;
            r.Add("Air", $"{air:N0} of {cfm:N0} cfm", "SG-06: 56 cfm a gun at stages 1–3, 16 at 4–5");
            if (!sol.Solved) r.Warn("The equations did not converge: the last good answer stands (10).", null, true);
            r.Ready(false, false);
            w.ShowReview(r);
            w.SetMessage(null);
            FillProblems(sol);
        }

        void FillProblems(SnowSolution sol)
        {
            var block = _ctx.Window.AddBlock("problems");
            block.Clear();
            block.EnableInClassList("tw-block--stale", _shownPending);
            if (sol == null) return;
            var net = _ctx.History.Document.Snow;
            var list = new List<SnowDiagnostic>(sol.Diagnostics);
            // Errors first, then by number, then by the entity: a stable order between solves.
            list.Sort((a, b) => a.IsWarning != b.IsWarning ? (a.IsWarning ? 1 : -1) : a.Code != b.Code ? a.Code.CompareTo(b.Code) : a.On.CompareTo(b.On));
            if (list.Count == 0)
            {
                block.Add(Note("No problems: every gun asked to run has water, air and pressure."));
                return;
            }
            // One row per number: 30 guns too warm read as one problem, and Show me lights all 30.
            int rows = 0;
            for (int i = 0; i < list.Count;)
            {
                int j = i;
                var ids = new List<SnowId>();
                bool same = true;
                while (j < list.Count && list[j].Code == list[i].Code)
                {
                    if (!list[j].On.IsNone) ids.Add(list[j].On);
                    same &= list[j].Text == list[i].Text;
                    j++;
                }
                var d = list[i];
                int count = j - i;
                if (rows++ >= MaxRows) { block.Add(Note($"…and {list.Count - i} more")); break; }
                var buttons = new List<(string, Action)>();
                if (ids.Count > 0) buttons.Add(("Show me", () => _ctx.ShowSnow?.Invoke(ids, null)));
                var fix = Fix(net, d);
                if (fix.Text != null) buttons.Add(fix);
                string title = count > 1 ? $"{Title(d.Code)} · {count} {Kinds(net, list, i, j)}" : Title(d.Code);
                string text = count == 1 ? Where(net, d) + d.Text : same ? d.Text : Where(net, d) + d.Text + $" (and {count - 1} more like it)";
                block.Add(ToolWindow.ProblemRow(d.Code, title, text, d.IsWarning, buttons.ToArray()));
                i = j;
            }
        }

        /// <summary>What a group of problems is about, for its title: "guns", "pipes", "pumps" or "places".</summary>
        static string Kinds(SnowNetwork net, List<SnowDiagnostic> list, int from, int to)
        {
            int guns = 0, pipes = 0, pumps = 0;
            for (int k = from; k < to; k++)
            {
                var on = list[k].On;
                if (net.FindGun(on) != null) guns++;
                else if (net.FindPipe(on) != null) pipes++;
                else if (net.FindNode(on) != null || net.FindHouse(on) != null) pumps++;
            }
            int n = to - from;
            return guns == n ? "guns" : pipes == n ? "pipes" : pumps == n ? "pumps" : "places";
        }

        static Label Note(string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("tw-prob__none");
            return l;
        }

        static string Title(int code)
        {
            switch (code)
            {
                case 1: return "No water source";
                case 2: return "Gun off its hydrant";
                case 3: return "Hydrant on no pipe";
                case 4: return "No route to the gun";
                case 7: return "Pump faces the wrong way";
                case 8: return "Outside the area";
                case 10: return "Did not converge";
                case 11: return "Pump can't push";
                case 12: return "Short of pressure";
                case 13: return "Pipe class";
                case 14: return "Fast water";
                case 15: return "Air lock";
                case 16: return "Source at minimum pool";
                case 17: return "Lake at drawdown limit";
                case 18: return "Short of air";
                case 19: return "Valve closed";
                case 20: return "Too warm";
                default: return "Problem";
            }
        }

        static string Where(SnowNetwork net, SnowDiagnostic d)
        {
            if (d.On.IsNone) return "";
            var g = net.FindGun(d.On);
            if (g != null) { var h = net.FindNode(g.Hydrant); return h != null ? "Gun at " + SnowSnap.Label(net, h) + ": " : "Gun: "; }
            var p = net.FindPipe(d.On);
            if (p != null) return SnowSnap.Name(p) + ": ";
            var node = net.FindNode(d.On);
            if (node != null) return SnowSnap.Label(net, node) + ": ";
            var house = net.FindHouse(d.On);
            return house != null ? house.Name + ": " : "";
        }

        (string Text, Action Click) Fix(SnowNetwork net, SnowDiagnostic d)
        {
            switch (d.Code)
            {
                case 7:
                {
                    var pump = net.FindNode(d.On);
                    if (pump == null || pump.Kind != SnowNodeKind.Pump) return default;
                    return ("Flip pump", () => Submit(new EditSnowPump(_ctx.History.Revision, pump.Id, pump.Pump, !pump.Forward), "Pump flipped"));
                }
                case 12:
                case 14:
                case 13 when !d.IsWarning:
                {
                    var pipe = PipeOf(net, d.On);
                    if (pipe == null) return default;
                    int i = Array.IndexOf(SnowRules.Diameters, pipe.DiameterIn);
                    if (i < 0 || i + 1 >= SnowRules.Diameters.Length) return default;
                    int bigger = SnowRules.Diameters[i + 1];
                    return ($"Upsize to {bigger} in", () => Submit(new EditSnowPipe(_ctx.History.Revision, pipe.Id, pipe.Name, bigger, pipe.FromClosed, pipe.ToClosed),
                        SnowSnap.Name(pipe) + $" upsized to {bigger} in"));
                }
                case 19:
                {
                    var pipe = PipeOf(net, d.On);
                    if (pipe == null || !pipe.FromClosed && !pipe.ToClosed) return default;
                    return ("Open valve", () => Submit(new EditSnowPipe(_ctx.History.Revision, pipe.Id, pipe.Name, pipe.DiameterIn, false, false), "Valves opened on " + SnowSnap.Name(pipe)));
                }
                default: return default;
            }
        }

        /// <summary>The pipe a problem is about: itself, or the line under the gun's hydrant.</summary>
        static SnowPipe PipeOf(SnowNetwork net, SnowId on)
        {
            var pipe = net.FindPipe(on);
            if (pipe != null) return pipe;
            var g = net.FindGun(on);
            var h = g != null ? net.FindNode(g.Hydrant) : net.FindNode(on);
            return h != null ? net.FindPipe(h.Host) : null;
        }

        void Submit(DesignCommand cmd, string done)
        {
            var r = _ctx.Submit?.Invoke(cmd, null);
            if (r != null && !r.Accepted) { _ctx.Window.SetMessage(r.Reason, true); return; }
            _ctx.Notice?.Invoke(done);
            Refresh(true);
        }

        public ToolBackOut Escape() => ToolBackOut.Close;
        public DesignCommand Commit(WorkStage stage, long baseRevision) => null;
        public void Committed(CommandResult result) { }

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.SetSnowCheck?.Invoke(false);
            _ctx.ShowSnow?.Invoke(Array.Empty<SnowId>(), null);
            _ctx.Window.RemoveOption("reads");
            _ctx.Window.AddBlock("problems").RemoveFromHierarchy();
            _ctx = null;
        }

        // For tests and scripted captures.
        public void Read(bool everyGun) { _everyGun = everyGun; _setReads?.Invoke(everyGun ? 1 : 0); _ctx.SetSnowCheck?.Invoke(everyGun); Refresh(true); }
    }
}
