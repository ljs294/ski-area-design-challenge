using System;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// Task 4.15 (HY-08, HY-09): one solve's answer, copied out of the solver so the screen can hold it while the next
    /// solve runs. Immutable; made once a solve (never a frame), so the simulation's snapshot and the tools' live preview
    /// hand it over by reference.
    /// </summary>
    public sealed class SnowSolution
    {
        public readonly HydraulicGraph Graph;
        /// <summary>The simulation tick it was solved at (−1 for a tool's preview).</summary>
        public readonly long Tick;
        /// <summary>False when the equations did not converge (HY-09 10): the arrays hold zeros.</summary>
        public readonly bool Solved;
        /// <summary>Every gun forced on at its wet bulb's stage ("Check network"), not the guns as set.</summary>
        public readonly bool WhatIf;
        /// <summary>Pressure at each graph node: at grade (a hydrant's outlet, what a gun sees) and in the buried pipe.</summary>
        public readonly float[] GradePsi, PipePsi;
        /// <summary>Whether a node has a pressure (water nodes and nodes cut off from all water have none).</summary>
        public readonly bool[] Known;
        /// <summary>Flow on each graph link, gpm, positive from A to B.</summary>
        public readonly float[] LinkGpm;
        public readonly GunState[] GunStates;
        public readonly byte[] GunStage;
        public readonly float[] GunPsi;
        public readonly PumpState[] PumpStates;
        /// <summary>Air drawn per component, cfm (its compressors are <see cref="HydraulicGraph.ComponentCfm"/>).</summary>
        public readonly int[] AirDrawn;
        public readonly SnowDiagnostic[] Diagnostics;
        public readonly double DemandGpm;
        public readonly int Resolves, Iterations;

        public SnowSolution(SnowSolve s, long tick, bool whatIf)
        {
            var g = s.Graph;
            Graph = g;
            Tick = tick;
            WhatIf = whatIf;
            Solved = s.Solved;
            GradePsi = new float[g.NodeCount];
            PipePsi = new float[g.NodeCount];
            LinkGpm = new float[g.LinkCount];
            Known = new bool[g.NodeCount];
            if (Solved)
            {
                for (int i = 0; i < g.NodeCount; i++)
                    if (g.Unknown[i] >= 0 && (g.Reached == null || g.Reached[i])) { GradePsi[i] = (float)s.Solver.GradePsi(i); PipePsi[i] = (float)s.Solver.Psi(i); Known[i] = true; }
                for (int k = 0; k < g.LinkCount; k++) LinkGpm[k] = s.Solver.Shut[k] ? 0f : (float)s.Solver.Q[k];
            }
            GunStates = (GunState[])s.GunStates.Clone();
            GunStage = new byte[g.Guns.Length];
            GunPsi = new float[g.Guns.Length];
            for (int i = 0; i < GunStage.Length; i++)
            {
                GunStage[i] = (byte)s.GunStages[i].Number;
                int node = g.GunNode[i];
                bool reached = node >= 0 && (g.Reached == null || g.Reached[node]);
                GunPsi[i] = double.IsNaN(s.GunPsi[i]) || !reached ? float.NaN : (float)s.GunPsi[i];
            }
            PumpStates = (PumpState[])s.PumpStates.Clone();
            AirDrawn = (int[])s.AirDrawn.Clone();
            Diagnostics = s.Diagnostics.ToArray();
            DemandGpm = s.DemandGpm;
            Resolves = s.Resolves;
            Iterations = s.TotalIterations;
        }

        /// <summary>The gun's index in <see cref="HydraulicGraph.Guns"/>, or −1.</summary>
        public int GunIndex(SnowId gun)
        {
            var guns = Graph.Guns;
            int lo = 0, hi = guns.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = guns[mid].Id.CompareTo(gun);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>The worst running-gun margin over <see cref="GunCatalog.MinPsi"/> (negative: some gun is short), or NaN.</summary>
        public float WorstMarginPsi
        {
            get
            {
                float worst = float.NaN;
                for (int i = 0; i < GunPsi.Length; i++)
                {
                    if (GunStates[i] != GunState.Running && GunStates[i] != GunState.RunningWarm && GunStates[i] != GunState.ShortOfPressure) continue;
                    float m = GunPsi[i] - GunCatalog.MinPsi;
                    if (float.IsNaN(worst) || m < worst) worst = m;
                }
                return worst;
            }
        }
    }
}
