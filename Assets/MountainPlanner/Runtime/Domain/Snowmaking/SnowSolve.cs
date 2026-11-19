using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>What the caller asks of one gun for a solve (by <see cref="HydraulicGraph.Guns"/> index).</summary>
    public struct GunAsk
    {
        /// <summary>The gun should run: forced On, or Auto with its trail making snow (4.16), or every gun in a network check.</summary>
        public bool Wanted;
        /// <summary>Forced On by its override (SM-02): it runs even when too warm, drawing stage 1 and making no snow.</summary>
        public bool Forced;
        /// <summary>The wet bulb at the gun's cell, tenths of a degree F (WX-02 plus the hydrant's keyed jitter).</summary>
        public int WetBulbTenthsF;
        /// <summary>SG-07: its trail's priority, 1 (high) – 5 (low); 3 until 4.16 sets one.</summary>
        public int Priority;
    }

    /// <summary>A source's state for a solve (WS-05).</summary>
    public readonly struct SourceLevel
    {
        public readonly int LevelMm;
        /// <summary>Above its floor (MinPool, or a lake's drawdown limit): it supplies.</summary>
        public readonly bool Supplying;
        /// <summary>At full pool: an outlet into it stops.</summary>
        public readonly bool Full;
        public readonly bool IsLake;
        public SourceLevel(int levelMm, bool supplying, bool full, bool isLake)
        {
            LevelMm = levelMm;
            Supplying = supplying;
            Full = full;
            IsLake = isLake;
        }
    }

    public enum GunState : byte
    {
        /// <summary>Not asked to run (Off, or Auto with nothing calling for snow).</summary>
        Idle,
        /// <summary>Asked to run but too warm (Auto above 28 °F wet bulb).</summary>
        TooWarm,
        Running,
        /// <summary>Forced on above 28 °F: draws water, makes no snow (HY-09 20).</summary>
        RunningWarm,
        /// <summary>Shut off for pressure (HY-09 12).</summary>
        ShortOfPressure,
        /// <summary>Dropped for air (HY-09 18).</summary>
        ShortOfAir,
        /// <summary>No water reaches it (no hydrant, no route, a closed valve, its sources at their floors).</summary>
        NoWater,
    }

    public enum PumpState : byte { Off, Boosting, PowerLimited, Throttled, NoForwardFlow }

    /// <summary>
    /// HY-07 and HY-08's job around the Newton solve, and SG-06/SG-07's limits: one instance per compiled network, kept
    /// between solves so each starts warm from the last (plan v2: snappy). <see cref="Run"/> sets the water, sheds guns for
    /// air, solves, throttles pumps over the pipe class, sheds guns short of pressure in SG-07's order and re-solves (at
    /// most <see cref="MaxResolves"/> times), then reads every pressure, flow and diagnostic into its result arrays. The tools'
    /// live preview and the simulation's job both run it; it touches no other state.
    /// </summary>
    public sealed class SnowSolve
    {
        /// <summary>p4-02's defaults (HY-07, HY-08); the simulation passes its Pacing's (MaxResolves, PipeClass).</summary>
        public const int DefaultMaxResolves = 10;
        public const double DefaultPipeClassPsi = 1000;
        /// <summary>The re-solves allowed and the pipe class (psi) this solve uses: warn above 80 % of the class.</summary>
        public readonly int MaxResolves;
        public readonly double ClassPsi, ClassWarnPsi;
        /// <summary>HY-07: the pipe class, its warning band and the velocity warning.</summary>
        public const double VelocityWarnFtS = 15;

        public readonly HydraulicGraph Graph;
        public readonly HydraulicSolver Solver;

        // Results, reused.
        public readonly GunState[] GunStates;
        public readonly GunStage[] GunStages;
        /// <summary>Each gun's pressure at its hydrant (grade), psi; NaN without a solution.</summary>
        public readonly double[] GunPsi;
        public readonly PumpState[] PumpStates;   // per link (pumps only)
        public readonly List<SnowDiagnostic> Diagnostics = new List<SnowDiagnostic>();
        public bool Solved { get; private set; }
        public int Resolves { get; private set; }
        public int TotalIterations { get; private set; }
        public double DemandGpm { get; private set; }
        /// <summary>Air drawn and available per component, cfm.</summary>
        public readonly int[] AirDrawn;

        readonly bool[] _active;
        readonly List<int> _short = new List<int>();
        readonly int[] _order;

        public SnowSolve(HydraulicGraph g, int maxResolves = DefaultMaxResolves, double pipeClassPsi = DefaultPipeClassPsi)
        {
            if (maxResolves < 0) throw new ArgumentOutOfRangeException(nameof(maxResolves));
            if (!(pipeClassPsi > 0)) throw new ArgumentOutOfRangeException(nameof(pipeClassPsi));
            MaxResolves = maxResolves;
            ClassPsi = pipeClassPsi;
            ClassWarnPsi = pipeClassPsi * 0.8;
            Graph = g;
            Solver = new HydraulicSolver(g);
            int guns = g.Guns.Length;
            GunStates = new GunState[guns];
            GunStages = new GunStage[guns];
            GunPsi = new double[guns];
            PumpStates = new PumpState[g.LinkCount];
            AirDrawn = new int[g.ComponentCfm.Length];
            _active = new bool[guns];
            _order = new int[guns];
        }

        /// <summary>Runs the solve. <paramref name="asks"/> is indexed like <see cref="HydraulicGraph.Guns"/>.</summary>
        /// <param name="cold">Start from nothing, not the last answer: the simulation's state solves are cold, so what they move
        /// is a function of the inputs alone and a reloaded game moves the same water (SI-22).</param>
        public bool Run(GunAsk[] asks, Func<ulong, SourceLevel> source, bool cold = false)
        {
            var g = Graph;
            var s = Solver;
            Diagnostics.Clear();
            Diagnostics.AddRange(g.Diagnostics);
            Resolves = 0;
            TotalIterations = 0;
            var blockedGun = new HashSet<SnowId>();
            foreach (var d in g.Diagnostics) if (d.Code == 2 || d.Code == 4 || d.Code == 19) blockedGun.Add(d.On);

            // The water: heads at each source's surface; intakes on sources at their floor shut, outlets into full ponds shut.
            var wetComponent = new bool[g.ComponentHasWater.Length];
            var noted = new HashSet<ulong>();
            for (int k = 0; k < g.LinkCount; k++)
            {
                var kind = g.LinkKind[k];
                if (kind != HLinkKind.Intake && kind != HLinkKind.Outlet) continue;
                int water = kind == HLinkKind.Intake ? g.A[k] : g.B[k];
                ulong src = g.Source[water];
                var lv = src == 0 ? new SourceLevel(0, false, true, false) : source(src);
                s.SetWater(water, lv.LevelMm * HydraulicGraph.FtPerMm);
                bool shut = kind == HLinkKind.Intake ? !lv.Supplying : lv.Full;
                s.ForceShut(k, shut);
                if (kind == HLinkKind.Intake && !shut) wetComponent[g.Component[water]] = true;
                if (kind == HLinkKind.Intake && shut && src != 0 && noted.Add(src))
                    Diagnostics.Add(lv.IsLake
                        ? new SnowDiagnostic(17, g.LinkDesign[k], "The lake is at its drawdown limit: its intake is shut.")
                        : new SnowDiagnostic(16, g.LinkDesign[k], "The source is at its minimum pool: its intake is shut."));
            }

            // Stages and who wants to run.
            for (int i = 0; i < g.Guns.Length; i++)
            {
                var a = asks[i];
                _active[i] = false;
                GunPsi[i] = double.NaN;
                GunStages[i] = default;
                if (!a.Wanted) { GunStates[i] = GunState.Idle; continue; }
                var stage = GunCatalog.StageAt(a.WetBulbTenthsF);
                bool warm = stage.IsNone;
                if (warm && !a.Forced) { GunStates[i] = GunState.TooWarm; continue; }
                GunStages[i] = warm ? GunCatalog.Forced : stage;
                int node = g.GunNode[i];
                if (node < 0 || blockedGun.Contains(g.Guns[i].Id) || !wetComponent[g.Component[node]])
                {
                    GunStates[i] = GunState.NoWater;
                    continue;
                }
                GunStates[i] = warm ? GunState.RunningWarm : GunState.Running;
                _active[i] = true;
                if (warm) Diagnostics.Add(new SnowDiagnostic(20, g.Guns[i].Id, "Forced on while too warm: it draws water and makes no snow."));
            }

            // SG-06: each component's compressors feed its guns; over the supply, guns drop in SG-07's order.
            Array.Clear(AirDrawn, 0, AirDrawn.Length);
            for (int i = 0; i < g.Guns.Length; i++) if (_active[i]) AirDrawn[g.Component[g.GunNode[i]]] += GunStages[i].AirCfm;
            for (int c = 0; c < AirDrawn.Length; c++)
            {
                if (AirDrawn[c] <= g.ComponentCfm[c]) continue;
                int count = DropOrder(asks, i => _active[i] && g.Component[g.GunNode[i]] == c);
                for (int j = 0; j < count && AirDrawn[c] > g.ComponentCfm[c]; j++)
                {
                    int i = _order[j];
                    _active[i] = false;
                    AirDrawn[c] -= GunStages[i].AirCfm;
                    GunStates[i] = GunState.ShortOfAir;
                    Diagnostics.Add(new SnowDiagnostic(18, g.Guns[i].Id, g.ComponentCfm[c] == 0
                        ? "No compressor feeds this gun: a pump house on its network supplies air."
                        : "Short of air: the compressors on this network are full."));
                }
            }

            for (int k = 0; k < g.LinkCount; k++) s.BoostPsi[k] = 0;
            bool warmStart = !cold && s.HasSolution;
            Solved = false;
            for (int round = 0; ; round++)
            {
                s.ClearDemands();
                DemandGpm = 0;
                for (int i = 0; i < g.Guns.Length; i++)
                    if (_active[i]) { s.AddDemand(g.GunNode[i], GunStages[i].WaterGpm); DemandGpm += GunStages[i].WaterGpm; }
                bool ok = s.Solve(warmStart);
                TotalIterations += s.Iterations;
                if (!ok)
                {
                    Diagnostics.Add(new SnowDiagnostic(10, SnowId.None, "The hydraulic equations did not converge: the last good solution stands."));
                    return false;
                }
                warmStart = true;
                if (round >= MaxResolves)
                {
                    // Out of re-solves: any gun still short shuts off without another solve (HY-08).
                    for (int i = 0; i < g.Guns.Length; i++)
                        if (_active[i] && s.GradePsi(g.GunNode[i]) < GunCatalog.MinPsi - 1e-6) { _active[i] = false; GunStates[i] = GunState.ShortOfPressure; }
                    break;
                }
                // HY-07: a pump that pushes its network over the pipe class throttles to it.
                if (Throttle()) { Resolves++; continue; }
                _short.Clear();
                for (int i = 0; i < g.Guns.Length; i++)
                {
                    if (!_active[i]) continue;
                    double psi = s.GradePsi(g.GunNode[i]);
                    GunPsi[i] = psi;
                    if (psi < GunCatalog.MinPsi - 1e-6) _short.Add(i);
                }
                if (_short.Count == 0) break;
                // SG-07: shed the short guns in order, half of them a round (all of them on the last), then solve again.
                int n = DropOrder(asks, i => _short.Contains(i));
                int drop = round == MaxResolves - 1 ? n : (n + 1) / 2;
                for (int j = 0; j < drop; j++)
                {
                    int i = _order[j];
                    _active[i] = false;
                    GunStates[i] = GunState.ShortOfPressure;
                }
                Resolves++;
            }
            Solved = true;
            Read();
            return true;
        }

        // SG-07: low priority first (5 before 1), then the lowest pressure, then the furthest from the water, then by ID.
        int DropOrder(GunAsk[] asks, Func<int, bool> pick)
        {
            var g = Graph;
            int n = 0;
            for (int i = 0; i < g.Guns.Length; i++) if (pick(i)) _order[n++] = i;
            Array.Sort(_order, 0, n, Comparer<int>.Create((x, y) =>
            {
                int c = asks[y].Priority.CompareTo(asks[x].Priority);
                if (c != 0) return c;
                double px = double.IsNaN(GunPsi[x]) ? 0 : GunPsi[x], py = double.IsNaN(GunPsi[y]) ? 0 : GunPsi[y];
                c = px.CompareTo(py);
                if (c != 0) return c;
                c = g.FromWaterFt[g.GunNode[y]].CompareTo(g.FromWaterFt[g.GunNode[x]]);
                return c != 0 ? c : g.Guns[x].Id.CompareTo(g.Guns[y].Id);
            }));
            return n;
        }

        bool Throttle()
        {
            var g = Graph;
            var s = Solver;
            bool any = false;
            var worst = new double[g.ComponentHasWater.Length];
            for (int i = 0; i < g.NodeCount; i++)
                if (g.Unknown[i] >= 0) worst[g.Component[i]] = Math.Max(worst[g.Component[i]], s.Psi(i));
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] != HLinkKind.Pump || !g.Pump[k].On || s.Shut[k]) continue;
                double over = worst[g.Component[g.A[k]]] - ClassPsi;
                if (over <= 0.5) continue;
                double boost = (s.BoostPsi[k] > 0 ? s.BoostPsi[k] : g.Pump[k].BoostPsi) - over;
                s.BoostPsi[k] = Math.Max(1, boost);
                any = true;
            }
            return any;
        }

        void Read()
        {
            var g = Graph;
            var s = Solver;
            for (int i = 0; i < g.Guns.Length; i++)
            {
                if (g.GunNode[i] >= 0) GunPsi[i] = s.GradePsi(g.GunNode[i]);
                if (GunStates[i] == GunState.ShortOfPressure)
                    Diagnostics.Add(new SnowDiagnostic(12, g.Guns[i].Id, $"Short of pressure: needs {GunCatalog.MinPsi} psi, has {Math.Max(0, GunPsi[i]):0}."));
            }
            for (int k = 0; k < g.LinkCount; k++)
            {
                PumpStates[k] = PumpState.Off;
                if (g.LinkKind[k] != HLinkKind.Pump || !g.Pump[k].On) continue;
                if (s.Shut[k])
                {
                    PumpStates[k] = PumpState.NoForwardFlow;
                    Diagnostics.Add(new SnowDiagnostic(11, g.LinkDesign[k], "This pump has no forward flow: the water downstream stands higher than it can push."));
                }
                else if (s.BoostPsi[k] > 0) PumpStates[k] = PumpState.Throttled;
                else PumpStates[k] = s.Q[k] > s.PowerLimitGpm(k) ? PumpState.PowerLimited : PumpState.Boosting;
            }
            var over = new HashSet<SnowId>();
            var fast = new HashSet<SnowId>();
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] != HLinkKind.Pipe) continue;
                if (VelocityFt(k) > VelocityWarnFtS && fast.Add(g.LinkDesign[k]))
                    Diagnostics.Add(new SnowDiagnostic(14, g.LinkDesign[k], $"Water runs at {VelocityFt(k):0.0} ft/s here (over 15): a bigger pipe loses less."));
                double psi = Math.Max(s.Unknown(g.A[k]) ? s.Psi(g.A[k]) : 0, s.Unknown(g.B[k]) ? s.Psi(g.B[k]) : 0);
                if (psi > ClassWarnPsi && over.Add(g.LinkDesign[k]))
                    Diagnostics.Add(new SnowDiagnostic(13, g.LinkDesign[k], psi > ClassPsi + 0.5
                        ? $"Over its {ClassPsi:#,0} psi class at {psi:0} psi: the pumps throttle."
                        : $"Near its {ClassPsi:#,0} psi class at {psi:0} psi."));
            }
            for (int i = 0; i < g.NodeCount; i++)
            {
                if (g.Kind[i] != HNodeKind.HighPoint || g.Unknown[i] < 0) continue;
                if (s.Psi(i) < 0)
                {
                    var pipe = SnowId.None;
                    for (int k = 0; k < g.LinkCount; k++) if (g.A[k] == i || g.B[k] == i) { pipe = g.LinkDesign[k]; break; }
                    Diagnostics.Add(new SnowDiagnostic(15, pipe, "Air lock: the pipe's high point is below atmospheric pressure."));
                }
            }
        }

        public double VelocityFt(int link) => HydraulicGraph.VelocityFtS(Solver.Q[link], Graph.DiameterIn[link]);
    }

    static class SolverExt
    {
        public static bool Unknown(this HydraulicSolver s, int node) => s.Graph.Unknown[node] >= 0;
    }
}
