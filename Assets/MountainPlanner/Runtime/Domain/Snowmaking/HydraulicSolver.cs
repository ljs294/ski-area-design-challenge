using System;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// HY-06's solve: the global gradient method (Todini's Newton scheme on heads and flows) over a
    /// <see cref="HydraulicGraph"/>, re-specified from the rules and checked by hand (plan v2, M4). Loops and many sources
    /// solve directly (HY-02). Each Newton step assembles the weighted Laplacian of the unknown heads, factors it with the
    /// graph's sparse Cholesky and updates heads, then flows. Pumps and the intake and outlet valves are check valves whose
    /// open/closed status is settled after each converged solve (EPANET's way). A cold solve runs the five pump-power stages
    /// (0.1 → 1.0); a warm one starts from the last heads and flows at full power. One instance per graph; no allocation per
    /// solve.
    /// </summary>
    public sealed class HydraulicSolver
    {
        public const double ContinuityTolGpm = 0.01;
        public const double EnergyTolFt = 0.01;
        public const int MaxIterations = 100;
        /// <summary>HY-06's derivative floor.</summary>
        public const double FlowFloorGpm = 0.01;
        /// <summary>A shut link (a closed valve or check valve) as a stiff linear resistance, ft per gpm.</summary>
        public const double ShutResistance = 1e6;
        /// <summary>An off pump's open check valve (and an intake's or outlet's open valve), nearly free: ft per gpm².</summary>
        public const double OpenValveR = 1e-9;
        /// <summary>HY-05 v2: the pump's droop below its power limit, so its head always falls a little with flow.</summary>
        public const double PumpDroop = 0.02;
        static readonly double[] ColdStages = { 0.1, 0.25, 0.5, 0.75, 1.0 };
        static readonly double[] WarmStages = { 1.0 };

        public readonly HydraulicGraph Graph;
        /// <summary>Heads, ft, per node; flows, gpm, per link (from A to B).</summary>
        public readonly double[] H, Q;
        /// <summary>Per link: shut by its check valve (pumps, intakes, outlets).</summary>
        public readonly bool[] Shut;
        /// <summary>Per link: a pump's boost after the class throttle (HY-07), psi; 0 leaves the pump's own.</summary>
        public readonly double[] BoostPsi;
        public int Iterations { get; private set; }
        public double ContinuityResidual { get; private set; }
        public double EnergyResidual { get; private set; }
        public bool HasSolution { get; private set; }

        readonly double[] _demand, _w, _e, _c, _rhs, _dh, _hTry, _qTry, _cTry, _eTry;
        double _scale = 1;

        public HydraulicSolver(HydraulicGraph g)
        {
            Graph = g;
            int n = g.NodeCount, m = g.LinkCount, u = g.UnknownCount;
            H = new double[n];
            Q = new double[m];
            Shut = new bool[m];
            BoostPsi = new double[m];
            _demand = new double[n];
            _w = new double[m];
            _e = new double[m];
            _eTry = new double[m];
            _qTry = new double[m];
            _c = new double[u];
            _cTry = new double[u];
            _rhs = new double[u];
            _dh = new double[u];
            _hTry = new double[n];
        }

        /// <summary>Sets a node's demand, gpm (a gun's water at its hydrant).</summary>
        public void ClearDemands() => Array.Clear(_demand, 0, _demand.Length);
        public void AddDemand(int node, double gpm) => _demand[node] += gpm;
        public double Demand(int node) => _demand[node];

        /// <summary>Sets a water node's head: a source's surface, ft.</summary>
        public void SetWater(int node, double headFt) => H[node] = headFt;

        /// <summary>Shuts or opens a source's intake or an outlet (a source at its floor, a pond full: WS-05, WS-11).</summary>
        public void ShutValve(int link, bool shut) => Shut[link] = shut;

        /// <summary>
        /// Solves for the current demands and water heads. Returns false if Newton fails to converge (HY-09 10); heads and
        /// flows are then the last iterate and the caller keeps its last good solution.
        /// </summary>
        public bool Solve(bool warm)
        {
            var g = Graph;
            Iterations = 0;
            if (!warm || !HasSolution) ColdStart();
            var stages = warm && HasSolution ? WarmStages : ColdStages;
            HasSolution = false;
            for (int pass = 0; pass < 12; pass++)
            {
                foreach (double s in stages)
                {
                    _scale = s;
                    if (!Newton()) return false;
                }
                stages = WarmStages;
                if (!SettleValves()) { HasSolution = true; return true; }
            }
            HasSolution = true;   // status still flipping after 12 passes: the last converged state stands
            return true;
        }

        void ColdStart()
        {
            var g = Graph;
            // Heads: every unknown at the highest water head plus the strongest pump's boost (a warm guess for pumped
            // systems); flows: one multi-source shortest-path tree from the water, each demand carried up it (plan v2, M7).
            double top = double.NegativeInfinity, boost = 0;
            for (int i = 0; i < g.NodeCount; i++) if (g.Kind[i] == HNodeKind.Water) top = Math.Max(top, H[i]);
            for (int k = 0; k < g.LinkCount; k++) if (g.LinkKind[k] == HLinkKind.Pump && g.Pump[k].On) boost = Math.Max(boost, g.Pump[k].BoostPsi * HydraulicGraph.FtPerPsi);
            if (double.IsNegativeInfinity(top)) top = 0;
            for (int i = 0; i < g.NodeCount; i++) if (g.Unknown[i] >= 0) H[i] = top + 0.5 * boost;
            Array.Clear(Q, 0, Q.Length);
            for (int k = 0; k < Shut.Length; k++) Shut[k] = _forcedShut != null && _forcedShut[k];

            int n = g.NodeCount;
            var dist = new double[n];
            var parentLink = new int[n];
            var order = new int[n];
            int settled = 0;
            for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; parentLink[i] = -1; }
            var heap = new System.Collections.Generic.SortedSet<(double, int)>();
            for (int i = 0; i < n; i++) if (g.Kind[i] == HNodeKind.Water) { dist[i] = 0; heap.Add((0, i)); }
            var adj = g.Adjacency();
            while (heap.Count > 0)
            {
                var (d, x) = heap.Min;
                heap.Remove(heap.Min);
                order[settled++] = x;
                foreach (int k in adj[x])
                {
                    bool forward = g.A[k] == x;
                    if (g.LinkKind[k] != HLinkKind.Pipe && !forward) continue;
                    int y = forward ? g.B[k] : g.A[k];
                    double wgt = g.LinkKind[k] == HLinkKind.Pipe ? Math.Max(g.R[k], 1e-12) * (g.ValveClosed[k] ? 1e12 : 1) : 1e-12;
                    if (d + wgt < dist[y])
                    {
                        if (!double.IsPositiveInfinity(dist[y])) heap.Remove((dist[y], y));
                        dist[y] = d + wgt;
                        parentLink[y] = k;
                        heap.Add((dist[y], y));
                    }
                }
            }
            var carry = new double[n];
            for (int i = 0; i < n; i++) carry[i] = _demand[i];
            for (int s = settled - 1; s >= 0; s--)
            {
                int x = order[s], k = parentLink[x];
                if (k < 0 || carry[x] == 0) continue;
                int from = g.A[k] == x ? g.B[k] : g.A[k];
                Q[k] += g.B[k] == x ? carry[x] : -carry[x];
                carry[from] += carry[x];
            }
        }

        // One Newton solve at the current pump scale. False on failure.
        bool Newton()
        {
            var g = Graph;
            var ch = g.Cholesky;
            double flowScale = 1, headScale = 100;
            for (int i = 0; i < g.NodeCount; i++) flowScale += _demand[i];
            double hiE = double.NegativeInfinity, loE = double.PositiveInfinity;
            for (int i = 0; i < g.NodeCount; i++) { hiE = Math.Max(hiE, g.ElevFt[i]); loE = Math.Min(loE, g.ElevFt[i]); }
            if (hiE > loE) headScale = Math.Max(100, hiE - loE);

            double norm = Residuals(H, Q, _c, _e, flowScale, headScale);
            for (int it = 0; it < MaxIterations; it++)
            {
                if (ContinuityResidual <= ContinuityTolGpm && EnergyResidual <= EnergyTolFt) return true;
                Iterations++;
                ch.Clear();
                for (int i = 0; i < _rhs.Length; i++) _rhs[i] = -_c[i];
                for (int k = 0; k < g.LinkCount; k++)
                {
                    double d = Derivative(k, Q[k]);
                    if (!(d > 0) || double.IsInfinity(d)) return false;   // HY-06: a non-positive derivative stops the stage (M5)
                    double w = 1 / d;
                    _w[k] = w;
                    int a = g.Unknown[g.A[k]], b = g.Unknown[g.B[k]];
                    if (a >= 0) { ch.Add(g.SlotAA[k], w); _rhs[a] -= w * _e[k]; }
                    if (b >= 0) { ch.Add(g.SlotBB[k], w); _rhs[b] += w * _e[k]; }
                    if (g.SlotAB[k] >= 0) ch.Add(g.SlotAB[k], -w);
                }
                if (_rhs.Length > 0)
                {
                    if (!ch.Factor()) return false;
                    ch.Solve(_rhs, _dh);
                }
                // Halving line search on the scaled residual (MapLibre's, kept: at least 1/1024 of the step).
                double step = 1;
                bool accepted = false;
                while (step >= 1.0 / 1024)
                {
                    for (int i = 0; i < g.NodeCount; i++) { int u = g.Unknown[i]; _hTry[i] = u >= 0 ? H[i] + step * _dh[u] : H[i]; }
                    for (int k = 0; k < g.LinkCount; k++)
                    {
                        int a = g.Unknown[g.A[k]], b = g.Unknown[g.B[k]];
                        double dha = a >= 0 ? _dh[a] : 0, dhb = b >= 0 ? _dh[b] : 0;
                        _qTry[k] = Q[k] + step * _w[k] * (_e[k] + dha - dhb);
                    }
                    double next = Residuals(_hTry, _qTry, _cTry, _eTry, flowScale, headScale);
                    if (next <= norm * (1 - 1e-4 * step) || next < 1e-12)
                    {
                        Array.Copy(_hTry, H, H.Length);
                        Array.Copy(_qTry, Q, Q.Length);
                        Array.Copy(_cTry, _c, _c.Length);
                        Array.Copy(_eTry, _e, _e.Length);
                        norm = next;
                        accepted = true;
                        break;
                    }
                    step *= 0.5;
                }
                if (!accepted)
                {
                    Residuals(H, Q, _c, _e, flowScale, headScale);
                    return ContinuityResidual <= ContinuityTolGpm && EnergyResidual <= EnergyTolFt;
                }
            }
            return ContinuityResidual <= ContinuityTolGpm && EnergyResidual <= EnergyTolFt;
        }

        double Residuals(double[] h, double[] q, double[] c, double[] e, double flowScale, double headScale)
        {
            var g = Graph;
            Array.Clear(c, 0, c.Length);
            for (int i = 0; i < g.NodeCount; i++) { int u = g.Unknown[i]; if (u >= 0) c[u] += _demand[i]; }
            double maxE = 0;
            for (int k = 0; k < g.LinkCount; k++)
            {
                int a = g.Unknown[g.A[k]], b = g.Unknown[g.B[k]];
                if (a >= 0) c[a] += q[k];
                if (b >= 0) c[b] -= q[k];
                e[k] = h[g.A[k]] - h[g.B[k]] - Loss(k, q[k]);
                if (g.Unknown[g.A[k]] >= 0 || g.Unknown[g.B[k]] >= 0) maxE = Math.Max(maxE, Math.Abs(e[k]));
            }
            double maxC = 0;
            for (int i = 0; i < c.Length; i++) maxC = Math.Max(maxC, Math.Abs(c[i]));
            ContinuityResidual = maxC;
            EnergyResidual = maxE;
            return Math.Max(maxC / flowScale, maxE / headScale);
        }

        /// <summary>The head lost along a link from A to B at flow q, ft (negative where a pump adds head).</summary>
        public double Loss(int k, double q)
        {
            var g = Graph;
            if (g.ValveClosed[k] || Shut[k]) return ShutResistance * q;
            switch (g.LinkKind[k])
            {
                case HLinkKind.Pipe: return g.R[k] * q * Math.Pow(Math.Abs(q), 0.852);
                case HLinkKind.Pump:
                    if (!g.Pump[k].On) return OpenValveR * q * Math.Abs(q) + 1e-9 * q;
                    return -Gain(k, q);
                default: return OpenValveR * q * Math.Abs(q) + 1e-9 * q;
            }
        }

        double Derivative(int k, double q)
        {
            var g = Graph;
            if (g.ValveClosed[k] || Shut[k]) return ShutResistance;
            switch (g.LinkKind[k])
            {
                case HLinkKind.Pipe: return 1.852 * g.R[k] * Math.Pow(Math.Max(Math.Abs(q), FlowFloorGpm), 0.852);
                case HLinkKind.Pump:
                    if (!g.Pump[k].On) return 2 * OpenValveR * Math.Max(Math.Abs(q), FlowFloorGpm) + 1e-9;
                    return -GainSlope(k, q);
                default: return 2 * OpenValveR * Math.Max(Math.Abs(q), FlowFloorGpm) + 1e-9;
            }
        }

        // HY-05 v2: up to the boost while power lasts, then constant power, with a small droop: continuous, always falling.
        void PumpCurve(int k, out double hb, out double p, out double qStar)
        {
            var spec = Graph.Pump[k];
            double boost = BoostPsi[k] > 0 ? Math.Min(BoostPsi[k], spec.BoostPsi) : spec.BoostPsi;
            hb = boost * HydraulicGraph.FtPerPsi * _scale;
            p = 3960.0 * spec.Hp * (spec.EfficiencyPermille / 1000.0) * _scale;
            qStar = p / hb;
        }

        /// <summary>The head a pump adds at flow q, ft.</summary>
        public double Gain(int k, double q)
        {
            PumpCurve(k, out double hb, out double p, out double qStar);
            return q <= qStar ? hb * (1 - PumpDroop * q / qStar) : (1 - PumpDroop) * p / q;
        }

        double GainSlope(int k, double q)
        {
            PumpCurve(k, out double hb, out double p, out double qStar);
            return q <= qStar ? -PumpDroop * hb / qStar : -(1 - PumpDroop) * p / (q * q);
        }

        /// <summary>The power limit's flow: past it the pump can no longer hold its boost, gpm.</summary>
        public double PowerLimitGpm(int k)
        {
            PumpCurve(k, out _, out _, out double qStar);
            return qStar;
        }

        // Check valves (pumps, intakes, outlets): an open one running backwards shuts; a shut one whose heads would push
        // water forward opens. Returns true when any status changed (solve again).
        bool SettleValves()
        {
            var g = Graph;
            bool changed = false;
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] == HLinkKind.Pipe || g.ValveClosed[k]) continue;
                if (_forcedShut != null && _forcedShut[k]) continue;
                if (!Shut[k])
                {
                    if (Q[k] < -ContinuityTolGpm) { Shut[k] = true; changed = true; }
                }
                else
                {
                    double gain = g.LinkKind[k] == HLinkKind.Pump && g.Pump[k].On ? Gain(k, 0) : 0;
                    if (H[g.A[k]] + gain > H[g.B[k]] + EnergyTolFt) { Shut[k] = false; changed = true; }
                }
            }
            return changed;
        }

        bool[] _forcedShut;

        /// <summary>Holds a valve shut whatever the heads (a source at its floor, a full pond), or lets it go.</summary>
        public void ForceShut(int link, bool shut)
        {
            _forcedShut ??= new bool[Graph.LinkCount];
            _forcedShut[link] = shut;
            Shut[link] = shut;
        }

        /// <summary>A node's gauge pressure on the pipe, psi.</summary>
        public double Psi(int node) => (H[node] - Graph.ElevFt[node]) / HydraulicGraph.FtPerPsi;
        /// <summary>A node's pressure at grade (a hydrant's outlet), psi.</summary>
        public double GradePsi(int node) => (H[node] - Graph.GradeFt[node]) / HydraulicGraph.FtPerPsi;
    }
}
