using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.15 (HY-01, HY-08): <see cref="SimCommandKind.SetSnowNetwork"/>'s payload: the design's snowmaking network
    /// compiled against the ground, off the worker (the worker never computes geometry, SI-21). Immutable: the worker swaps
    /// it in. A view-sequence input like the water sources: the design's and the terrain's, sent again after every load.
    /// </summary>
    public sealed class SimSnowNetwork
    {
        public readonly HydraulicGraph Graph;
        /// <summary>A hash of everything the solve reads of the graph, so an unchanged resend changes nothing.</summary>
        public readonly ulong Hash;
        /// <summary>Whether a pipe ends in a pond or dam (WS-11): then each face hour moves real water.</summary>
        public readonly bool HasOutlets;

        public SimSnowNetwork(HydraulicGraph graph)
        {
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            Hash = HashOf(graph);
            for (int k = 0; k < graph.LinkCount; k++) if (graph.LinkKind[k] == HLinkKind.Outlet) HasOutlets = true;
        }

        /// <summary>Compiles a design's network (on any thread): buried <see cref="Domain.Design.SnowRules.BuryMm"/>, sampled every <see cref="Domain.Design.SnowRules.SampleMm"/>.</summary>
        /// <param name="levelMm">A source's water surface now (mm above the datum), or null for a source the area doesn't have.</param>
        public static SimSnowNetwork From(SnowNetwork net, IGroundHeights ground, Func<ulong, int?> levelMm) =>
            new SimSnowNetwork(HydraulicGraph.Compile(net, ground, Domain.Design.SnowRules.BuryMm, Domain.Design.SnowRules.SampleMm, levelMm));

        static ulong HashOf(HydraulicGraph g)
        {
            ulong h = 14695981039346656037UL;
            void Add(ulong v)
            {
                for (int b = 0; b < 8; b++) { h ^= (v >> (8 * b)) & 0xff; h *= 1099511628211UL; }
            }
            void AddD(double d) => Add((ulong)BitConverter.DoubleToInt64Bits(d));
            Add((ulong)g.NodeCount);
            for (int i = 0; i < g.NodeCount; i++) { Add((ulong)g.Kind[i]); AddD(g.ElevFt[i]); AddD(g.GradeFt[i]); Add(g.Source[i]); }
            Add((ulong)g.LinkCount);
            for (int k = 0; k < g.LinkCount; k++)
            {
                Add((ulong)g.LinkKind[k]); Add((ulong)g.A[k]); Add((ulong)g.B[k]); AddD(g.R[k]); Add(g.ValveClosed[k] ? 1UL : 0UL);
                var p = g.Pump[k];
                Add((ulong)p.Hp); Add((ulong)p.EfficiencyPermille); Add((ulong)p.BoostPsi); Add(p.On ? 1UL : 0UL);
            }
            Add((ulong)g.Guns.Length);
            for (int i = 0; i < g.Guns.Length; i++) { Add(g.Guns[i].Id.Value); Add((ulong)g.Guns[i].Override); Add((ulong)g.GunNode[i]); }
            foreach (int cfm in g.ComponentCfm) Add((ulong)cfm);
            foreach (var d in g.Diagnostics) { Add((ulong)d.Code); Add(d.On.Value); }
            return h;
        }
    }
}
