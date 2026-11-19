using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// Task 4.15: the network as pipes between their nodes (ends and taps, by chainage), for the tools' questions about
    /// where water comes from. The inline pump's default direction (plan: "away from the source", so MapLibre's most common
    /// error, a booster pushing back at its pump house, never arises) is the way along its pipe that leads away from the
    /// nearest pump house or intake by pipe length.
    /// </summary>
    public static class SnowRoutes
    {
        /// <summary>
        /// Whether a pump at <paramref name="chainageMm"/> on <paramref name="pipe"/> should push along the route (from its
        /// first point to its last): true when the water reaches it from the route's start side, or when no water reaches it.
        /// </summary>
        public static bool AwayFromSource(SnowNetwork net, SnowId pipe, double chainageMm)
        {
            var host = net.FindPipe(pipe);
            if (host == null) return true;
            var dist = FromSources(net);
            // The nearest node on each side of the point along the pipe, plus the pipe length to it.
            double back = double.PositiveInfinity, ahead = double.PositiveInfinity;
            foreach (var (node, c) in Stations(net, host))
            {
                if (!dist.TryGetValue(node, out double d)) continue;
                if (c <= chainageMm) back = Math.Min(back, d + (chainageMm - c));
                else ahead = Math.Min(ahead, d + (c - chainageMm));
            }
            if (double.IsInfinity(back) && double.IsInfinity(ahead)) return true;
            return back <= ahead;
        }

        /// <summary>Each node's pipe distance (mm) from the nearest pump house or intake; nodes no water reaches are absent.</summary>
        public static Dictionary<SnowId, double> FromSources(SnowNetwork net)
        {
            var adj = new Dictionary<SnowId, List<(SnowId To, double Mm)>>();
            void Edge(SnowId a, SnowId b, double mm)
            {
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<(SnowId, double)>();
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<(SnowId, double)>();
                la.Add((b, mm));
                lb.Add((a, mm));
            }
            foreach (var p in net.Pipes)
            {
                var st = Stations(net, p);
                for (int i = 0; i + 1 < st.Count; i++) Edge(st[i].Node, st[i + 1].Node, st[i + 1].C - st[i].C);
            }
            var dist = new Dictionary<SnowId, double>();
            // Dijkstra on a sorted set: (distance, id) keeps the order deterministic.
            var open = new SortedSet<(double D, ulong Id)>();
            foreach (var n in net.Nodes)
                if (n.Kind == SnowNodeKind.House || n.Kind == SnowNodeKind.Intake) { dist[n.Id] = 0; open.Add((0, n.Id.Value)); }
            while (open.Count > 0)
            {
                var (d, idv) = open.Min;
                open.Remove(open.Min);
                var id = new SnowId(idv);
                if (d > dist[id] || !adj.TryGetValue(id, out var edges)) continue;
                foreach (var (to, mm) in edges)
                {
                    double nd = d + mm;
                    if (dist.TryGetValue(to, out double old) && old <= nd) continue;
                    if (dist.ContainsKey(to)) open.Remove((old, to.Value));
                    dist[to] = nd;
                    open.Add((nd, to.Value));
                }
            }
            return dist;
        }

        /// <summary>A pipe's nodes in route order with their chainages (mm): its two ends and every tap on it.</summary>
        public static List<(SnowId Node, double C)> Stations(SnowNetwork net, SnowPipe p)
        {
            var list = new List<(SnowId, double)> { (p.From, 0), (p.To, SnowGeometry.Length(p.Route)) };
            foreach (var t in net.TapsOn(p.Id)) list.Add((t.Id, SnowGeometry.Project(p.Route, t.At).ChainageMm));
            list.Sort((a, b) => a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : a.Item1.Value.CompareTo(b.Item1.Value));
            return list;
        }
    }
}
