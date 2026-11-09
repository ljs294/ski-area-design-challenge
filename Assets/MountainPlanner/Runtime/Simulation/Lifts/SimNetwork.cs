using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// LO-10 v1's input (SI-21): the built network's descent graph (NW-02) as plain arrays, made by the submitter off the
    /// worker from the app's <see cref="NetworkGraph"/>. Ski edges only (trail segments and paths, never a lift), in the
    /// graph's order; each lift's top and bottom as node indices (−1 for a plan). Immutable. The worker searches it with
    /// the trails' readiness bits (4.07 writes them; every built trail is ready until then), so it never builds geometry.
    /// </summary>
    public sealed class SimNetwork
    {
        public readonly int NodeCount;
        /// <summary>Per ski edge: from and to node, and the trail it runs along as an index into <see cref="TrailIds"/>
        /// (−1 for a path, which is always open).</summary>
        public readonly int[] EdgeFrom, EdgeTo, EdgeTrail;
        /// <summary>Each node's first outgoing edge and how many (edges are grouped by their from node).</summary>
        public readonly int[] FirstEdge, EdgeCount;
        /// <summary>The built trails, in ID order.</summary>
        public readonly ulong[] TrailIds;

        SimNetwork(int nodes, int[] from, int[] to, int[] trail, ulong[] trails)
        {
            NodeCount = nodes;
            EdgeFrom = from; EdgeTo = to; EdgeTrail = trail; TrailIds = trails;
            FirstEdge = new int[nodes];
            EdgeCount = new int[nodes];
            for (int n = 0; n < nodes; n++) FirstEdge[n] = -1;
            for (int e = 0; e < from.Length; e++)
            {
                if (e > 0 && from[e] < from[e - 1]) throw new ArgumentException("Edges must be grouped by their from node.");
                if (FirstEdge[from[e]] < 0) FirstEdge[from[e]] = e;
                EdgeCount[from[e]]++;
            }
        }

        /// <summary>
        /// A network from raw arrays (tests): edges in any order (sorted here by from node, stably), trails in ID order.
        /// </summary>
        public static SimNetwork Of(int nodes, IReadOnlyList<(int from, int to, int trail)> edges, ulong[] trailIds)
        {
            var list = new List<(int from, int to, int trail, int k)>(edges.Count);
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                if (e.from < 0 || e.from >= nodes || e.to < 0 || e.to >= nodes) throw new ArgumentException("An edge's node is out of range.");
                if (e.trail < -1 || e.trail >= trailIds.Length) throw new ArgumentException("An edge's trail is out of range.");
                list.Add((e.from, e.to, e.trail, i));
            }
            list.Sort((a, b) => a.from != b.from ? a.from.CompareTo(b.from) : a.k.CompareTo(b.k));
            var from = new int[list.Count]; var to = new int[list.Count]; var trail = new int[list.Count];
            for (int i = 0; i < list.Count; i++) { from[i] = list[i].from; to[i] = list[i].to; trail[i] = list[i].trail; }
            for (int i = 1; i < trailIds.Length; i++)
                if (trailIds[i - 1] >= trailIds[i]) throw new ArgumentException("Trails must be in ID order, each once.");
            return new SimNetwork(nodes, from, to, trail, trailIds);
        }

        /// <summary>
        /// From the app's built graph: the ski edges, and through <paramref name="liftTop"/>/<paramref name="liftBottom"/> each
        /// lift's station nodes (−1 where the lift isn't in the graph), for the lifts in <paramref name="liftIds"/>' order.
        /// </summary>
        public static SimNetwork From(NetworkGraph graph, IReadOnlyList<ulong> liftIds, out int[] liftTop, out int[] liftBottom)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var index = new Dictionary<ulong, int>(graph.Nodes.Count);
            for (int i = 0; i < graph.Nodes.Count; i++) index[graph.Nodes[i].Id] = i;
            var trails = new SortedSet<ulong>();
            foreach (var e in graph.Edges) if (e.Kind == EdgeKind.TrailSegment) trails.Add(e.Owner);
            var trailIds = new ulong[trails.Count];
            trails.CopyTo(trailIds);
            var edges = new List<(int, int, int)>();
            foreach (var e in graph.Edges)
            {
                if (e.Kind == EdgeKind.Lift) continue;
                if (!index.TryGetValue(e.From, out int a) || !index.TryGetValue(e.To, out int b)) continue;
                int t = e.Kind == EdgeKind.TrailSegment ? Array.BinarySearch(trailIds, e.Owner) : -1;
                edges.Add((a, b, t));
            }
            liftTop = new int[liftIds.Count];
            liftBottom = new int[liftIds.Count];
            for (int i = 0; i < liftIds.Count; i++)
            {
                var id = new LiftId(liftIds[i]);
                liftTop[i] = index.TryGetValue(graph.StationOf(id, top: true), out int t) ? t : -1;
                liftBottom[i] = index.TryGetValue(graph.StationOf(id, top: false), out int b) ? b : -1;
            }
            return Of(graph.Nodes.Count, edges, trailIds);
        }
    }
}
