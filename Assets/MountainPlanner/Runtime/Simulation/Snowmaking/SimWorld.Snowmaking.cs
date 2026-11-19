using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Domain.Water;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.15 (HY-07, HY-08, SG-06, SG-07, WS-11): the snowmaking network on the worker.
    /// - **The state's solve.** Each face hour, after the sources' own water, a network that ends in a pond or dam solves
    ///   cold and moves that hour's outlet flow from the sources feeding it (in proportion to their intakes' flow) with
    ///   <see cref="TransferWater"/>. Cold, so the water moved is a function of the network, the levels and the weather
    ///   alone: a reloaded game moves the same water (SI-22), and nothing about the solve is saved.
    /// - **The screen's solve.** A new network, a gun's override (it comes in the design) or <see cref="SimCommandKind.CheckNetwork"/>
    ///   asks for a solve that lands <see cref="Pacing.SolveLatencyTicks"/> ticks later (HY-08's fixed latency), so a
    ///   burst of edits costs one solve. The answers go out in the snapshot by reference (<see cref="SnowSolution"/>).
    /// - **Which guns.** Until 4.16's schedules a gun runs only when set On (forced, SG-01's stage at its wet bulb, or the
    ///   forced stage when too warm); Auto waits. "Check network" solves again as if every gun ran (plan Q4).
    /// </summary>
    public sealed partial class SimWorld
    {
        SimSnowNetwork _snowNet;
        SnowSolve _snowSolve, _snowCheckSolve;
        GunAsk[] _snowAsks = Array.Empty<GunAsk>(), _snowCheckAsks = Array.Empty<GunAsk>();
        long _snowDueTick = -1;
        bool _snowCheck;
        SnowSolution _snowSolution, _snowCheckSolution;
        Func<ulong, SourceLevel> _snowLevel;

        /// <summary>The network playing now, or null.</summary>
        public SimSnowNetwork SnowNet => _snowNet;
        /// <summary>The last solve's answer for the guns as set, or null.</summary>
        public SnowSolution SnowSolution => _snowSolution;
        /// <summary>The last "Check network" answer (every gun on), or null while the check is off.</summary>
        public SnowSolution SnowCheckSolution => _snowCheck ? _snowCheckSolution : null;

        /// <summary>The <see cref="SimCommandKind.SetSnowNetwork"/> command: null clears the network.</summary>
        bool ApplySnowNetwork(object payload)
        {
            if (payload == null)
            {
                _snowNet = null; _snowSolve = null; _snowCheckSolve = null;
                _snowSolution = null; _snowCheckSolution = null; _snowDueTick = -1;
                return true;
            }
            if (!(payload is SimSnowNetwork n)) return false;
            if (_snowNet != null && _snowNet.Hash == n.Hash) return true;
            _snowNet = n;
            _snowSolve = new SnowSolve(n.Graph, Pacing.MaxResolves, Pacing.PipeClass);
            _snowCheckSolve = new SnowSolve(n.Graph, Pacing.MaxResolves, Pacing.PipeClass);
            _snowAsks = new GunAsk[n.Graph.Guns.Length];
            _snowCheckAsks = new GunAsk[n.Graph.Guns.Length];
            RequestSnowSolve();
            return true;
        }

        /// <summary>The <see cref="SimCommandKind.CheckNetwork"/> command: A = 1 turns the what-if solve on, 0 off.</summary>
        bool ApplyCheckNetwork(in SimCommand c)
        {
            bool on = c.A != 0;
            if (on == _snowCheck) { Reject(c, SimRefusal.NoChange); return false; }
            _snowCheck = on;
            if (!on) _snowCheckSolution = null;
            else RequestSnowSolve();
            return true;
        }

        Func<ulong, SourceLevel> Level => _snowLevel ??= SnowLevel;

        void RequestSnowSolve()
        {
            if (_snowNet != null && _snowDueTick < 0) _snowDueTick = Tick + Pacing.SolveLatencyTicks;
        }

        /// <summary>
        /// The systems table's gun-output row: a requested solve lands in the step that reaches its tick (a step covers
        /// [Tick, Tick + its ticks), so an idle step of many ticks lands it too), stamped with the tick it shows from.
        /// </summary>
        internal void StepSnowmaking()
        {
            long end = Tick + _stepTicks;
            if (_snowDueTick < 0 || end < _snowDueTick) return;
            _snowDueTick = -1;
            SolveForScreen(end);
        }

        /// <summary>
        /// While paused or held no tick passes, so a requested screen solve lands at once (the host calls this then): building
        /// while paused still shows the pressure. The screen's solve moves no state, so the clock's answers are unchanged.
        /// </summary>
        public bool SolveSnowNow()
        {
            if (_snowDueTick < 0) return false;
            _snowDueTick = -1;
            SolveForScreen(Tick);
            return true;
        }

        void SolveForScreen(long tick)
        {
            if (_snowNet == null) return;
            FillAsks(_snowAsks, false);
            _snowSolve.Run(_snowAsks, Level, cold: true);
            _snowSolution = new SnowSolution(_snowSolve, tick, false);
            if (_snowCheck)
            {
                FillAsks(_snowCheckAsks, true);
                _snowCheckSolve.Run(_snowCheckAsks, Level, cold: true);
                _snowCheckSolution = new SnowSolution(_snowCheckSolve, tick, true);
            }
        }

        /// <summary>
        /// The hour step's snowmaking, after the sources' own water: a network with outlets solves and moves the hour's water
        /// (WS-11); any other asks for a fresh answer for the screen (the weather moved the stages).
        /// </summary>
        internal void HourStepSnowmaking()
        {
            if (_snowNet == null) return;
            if (!_snowNet.HasOutlets || _srcCount == 0) { RequestSnowSolve(); return; }
            FillAsks(_snowAsks, false);
            bool ok = _snowSolve.Run(_snowAsks, Level, cold: true);
            _snowSolution = new SnowSolution(_snowSolve, Tick, false);
            if (_snowCheck) RequestSnowSolve();
            if (ok) MoveOutletWater();
        }

        /// <summary>WS-11: each outlet's flow for the hour, taken from the sources feeding its network by their intakes' share.</summary>
        void MoveOutletWater()
        {
            var g = _snowNet.Graph;
            var s = _snowSolve.Solver;
            int seconds = WaterSeconds;
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] != HLinkKind.Outlet || s.Shut[k] || s.Q[k] <= 0) continue;
                int to = _sources.IndexOf(g.Source[g.B[k]]);
                if (to < 0) continue;
                int component = g.Component[g.A[k]];
                // Whole gallons for the hour, rounded to a tenth of a gpm first so a last-bit difference never moves a gallon.
                long gallons = (long)Math.Round(Math.Round(s.Q[k], 1) * seconds / 60.0);
                double feed = 0;
                for (int j = 0; j < g.LinkCount; j++)
                    if (IsFeed(g, s, j, component)) feed += Math.Round(s.Q[j], 1);
                if (feed <= 0 || gallons <= 0) continue;
                long left = gallons;
                int last = -1;
                for (int j = 0; j < g.LinkCount; j++) if (IsFeed(g, s, j, component)) last = j;
                for (int j = 0; j < g.LinkCount && left > 0; j++)
                {
                    if (!IsFeed(g, s, j, component)) continue;
                    long share = j == last ? left : (long)Math.Floor(gallons * Math.Round(s.Q[j], 1) / feed);
                    int from = _sources.IndexOf(g.Source[g.A[j]]);
                    if (from >= 0 && from != to && share > 0) TransferWater(from, to, share);
                    left -= share;
                }
            }
        }

        static bool IsFeed(HydraulicGraph g, HydraulicSolver s, int link, int component) =>
            g.LinkKind[link] == HLinkKind.Intake && !s.Shut[link] && s.Q[link] > 0 && g.Component[g.B[link]] == component;

        /// <summary>SG-01 per gun, by <see cref="SnowAsks"/> (the tools' preview reads the same rule).</summary>
        void FillAsks(GunAsk[] asks, bool everyGun) =>
            SnowAsks.Fill(_snowNet.Graph, asks, everyGun, _weather != null && _wxHourSet ? _wxHour : (Domain.Weather.WeatherHour?)null,
                          _weather != null ? _weather.Rows.ZrefFeet : 0, _wxTerrain, Pacing);

        /// <summary>A source's water for the solve: its surface now, whether it can still supply and whether it's full.</summary>
        SourceLevel SnowLevel(ulong id)
        {
            int i = _sources != null ? _sources.IndexOf(id) : -1;
            if (i < 0) return new SourceLevel(0, false, true, false);
            var src = _sources[i];
            long gallons = _srcHeld[i] / MilliPerGallon;
            int level = (int)(src.FullPoolMm - src.Curve.BelowFullMicrons(gallons) / 1000);
            long cap = src.Curve.CapacityGallons * MilliPerGallon;
            return new SourceLevel(level, _srcHeld[i] > FloorOf(i), _srcHeld[i] >= cap, src.Kind == WaterBodyKind.Lake);
        }

        void WriteSnowmaking(SimSnapshot s)
        {
            s.Snowmaking = _snowSolution;
            s.SnowCheck = _snowCheck ? _snowCheckSolution : null;
            s.SnowSolvePending = _snowDueTick >= 0;
        }
    }
}
